<#
.SYNOPSIS
    Publish, package and tag Simplified Studio and Simplified ERP.

.DESCRIPTION
    One command for what was a six-step manual routine. The steps are not the
    point - the guards are. Each one below exists because the manual version
    got it wrong at least once:

      * a dirty tree is refused, so the tag cannot claim a commit the binary
        does not contain
      * the release folder's runtime\ is mirrored, not copied over, so a file
        that stops being part of the build (libzstd, when ERP moved to the
        managed zstd port) does not linger there forever
      * staging copies only from the publish output, never from the release
        folder, so the config.json the app writes on first run cannot end up
        inside a package
      * the packaged exe's stamp is checked against HEAD, so "is this build
        that commit?" is answered rather than assumed

    Both apps live in one repository on two branches, checked out as two
    worktrees. This finds them through git rather than hardcoded paths, so it
    works from either one.

.PARAMETER App
    Studio, ERP, or All (the default).

.PARAMETER DryRun
    Say what would happen and change nothing. Publishes nothing, writes
    nothing, moves no tags.

.PARAMETER NoTag
    Package without moving the version tag.

.PARAMETER AllowDirty
    Package a tree with uncommitted changes. The resulting build will not
    match its tag; only useful for trying a package out.

.PARAMETER KeepBuildOutput
    Leave bin/obj in place. They are removed by default - roughly 230 MB per
    app.

.EXAMPLE
    .\tools\release.ps1 -DryRun
    .\tools\release.ps1 -App ERP
#>
[CmdletBinding()]
param(
    [ValidateSet('Studio', 'ERP', 'All')]
    [string] $App = 'All',
    [switch] $DryRun,
    [switch] $NoTag,
    [switch] $AllowDirty,
    [switch] $KeepBuildOutput
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# Tag prefixes follow the convention already in the repo: Studio's releases are
# v1.3, v2.0; ERP's are erp-v1.3, erp-v2.0. One repo, so they cannot collide.
$AppInfo = @{
    'Simplified Studio' = @{ Short = 'Studio'; TagPrefix = 'v' }
    'Simplified ERP'    = @{ Short = 'ERP';    TagPrefix = 'erp-v' }
}

# What both apps report when a person closed the window — App.ClosedByUserExitCode.
# Neither app shuts itself down, so this separates "somebody shut it" from "it
# quit on its own", which would otherwise both arrive as the 0 that success uses.
$ClosedByUser = 1000

function Write-Step { param([string] $Message) Write-Host "  $Message" }
function Write-Head { param([string] $Message) Write-Host ""; Write-Host $Message -ForegroundColor Cyan }

function Get-Worktrees {
    <# Every checkout of this repository, with the app each one builds. #>
    $found = @()
    $current = $null
    foreach ($line in (git worktree list --porcelain)) {
        if ($line -like 'worktree *') { $current = $line.Substring(9) }
        elseif ($line -like 'branch *' -and $current) {
            $csproj = Join-Path $current 'src\SimplifiedStudio\SimplifiedStudio.csproj'
            if (Test-Path $csproj) {
                # The assembly name is what the app is actually called, so the
                # script never has to be told which worktree is which.
                $name = ([xml](Get-Content $csproj)).Project.PropertyGroup.AssemblyName |
                        Where-Object { $_ } | Select-Object -First 1
                if ($AppInfo.ContainsKey($name)) {
                    $found += [pscustomobject]@{
                        Name   = $name
                        Short  = $AppInfo[$name].Short
                        Prefix = $AppInfo[$name].TagPrefix
                        Path   = $current
                        Branch = $line.Substring(7) -replace '^refs/heads/', ''
                    }
                }
            }
            $current = $null
        }
    }
    return $found
}

function Get-DeclaredVersion {
    param([string] $Tree)
    $props = Join-Path $Tree 'Directory.Build.props'
    $version = ([xml](Get-Content $props)).Project.PropertyGroup.Version |
               Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { throw "No <Version> in $props" }
    return $version.Trim()
}

function Sync-Directory {
    <#
        Make $Destination match $Source: copy what is new or changed, and remove
        what the source no longer has. Copy-Item alone only ever adds, which is
        how a dropped native library can sit in a release folder indefinitely.
    #>
    param([string] $Source, [string] $Destination, [switch] $Pretend)

    if (-not (Test-Path $Destination)) {
        if (-not $Pretend) { New-Item -ItemType Directory $Destination -Force | Out-Null }
    }

    $wanted = @{}
    foreach ($file in Get-ChildItem $Source -File) {
        $wanted[$file.Name] = $true
        $target = Join-Path $Destination $file.Name
        if (-not $Pretend) { Copy-Item $file.FullName $target -Force }
    }

    $removed = @()
    if (Test-Path $Destination) {
        foreach ($file in Get-ChildItem $Destination -File) {
            if (-not $wanted.ContainsKey($file.Name)) {
                $removed += $file.Name
                if (-not $Pretend) { [System.IO.File]::Delete($file.FullName) }
            }
        }
    }
    return $removed
}

function Invoke-Release {
    param([object] $Target)

    $name    = $Target.Name
    $tree    = $Target.Path
    $version = Get-DeclaredVersion $tree
    $tag     = "$($Target.Prefix)$version"
    $release = Join-Path (Split-Path $tree -Parent) "$name - Release"
    $publish = Join-Path $tree 'src\SimplifiedStudio\bin\Release\net10.0-windows\win-x64\publish'
    $head    = (git -C $tree rev-parse HEAD).Trim()

    Write-Head "$name  ($($Target.Branch) @ $($head.Substring(0,7)))"

    # ── Guard: the binary must be able to claim its tag. ───────────────
    $dirty = git -C $tree status --porcelain
    if ($dirty) {
        if (-not $AllowDirty) {
            Write-Step "REFUSED: uncommitted changes. Commit them, or pass -AllowDirty to package anyway."
            $dirty | Select-Object -First 5 | ForEach-Object { Write-Step "    $_" }
            return $null
        }
        Write-Step "WARNING: packaging a dirty tree - the build will not match $tag."
    }

    if ($DryRun) {
        Write-Step "would publish   $publish"
        Write-Step "would sync      $release  (exe, icon.ico, runtime\)"
        Write-Step "would write     $release\$name.zip"
        if (-not $NoTag) { Write-Step "would move tag  $tag -> $($head.Substring(0,7))" }
        return $null
    }

    # ── Tag first, so the publish stamps the commit the tag points at. ─
    if (-not $NoTag) {
        git -C $tree tag -f -a $tag $head -m "$name v$version" | Out-Null
        Write-Step "tag $tag -> $($head.Substring(0,7))"
    }

    # ── Publish from scratch: a stale publish folder is a stale package. ─
    if (Test-Path $publish) { [System.IO.Directory]::Delete($publish, $true) }
    & dotnet publish (Join-Path $tree 'src\SimplifiedStudio\SimplifiedStudio.csproj') `
        -c Release -r win-x64 -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $name" }

    $exe = Join-Path $publish "$name.exe"
    if (-not (Test-Path $exe)) { throw "publish produced no $name.exe" }

    # ── Guard: the exe has to carry the commit being released. ─────────
    $stamp = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $AllowDirty -and $stamp -notlike "*$($head.Substring(0,7))*") {
        throw "$name.exe is stamped '$stamp', which does not carry HEAD $($head.Substring(0,7))"
    }

    # ── Sync the release folder from the publish output, and only from it.
    #    Files the app writes at the release root - config.json, the team
    #    marker - are the user's and are left alone; runtime\ is the build's
    #    and is mirrored exactly.
    # Every file the publish produced at the root, not a named list. Naming them
    # meant project.ico reached the zip - which is built from the publish output -
    # while never reaching the release folder, so the association pointed at an
    # icon that was not there.
    $shipped = @{}
    foreach ($file in Get-ChildItem $publish -File) {
        $shipped[$file.Name] = $true
        Copy-Item $file.FullName (Join-Path $release $file.Name) -Force
    }

    # Files at the root are not mirrored wholesale, because config.json and
    # team.simplified belong to the user and the build knows nothing about them.
    # But anything with a build-owned extension that the publish no longer
    # produces is leftover - the icons, when they moved into runtime\ - and
    # would otherwise sit there forever looking current.
    foreach ($file in Get-ChildItem $release -File) {
        if ($shipped.ContainsKey($file.Name)) { continue }
        if ($file.Extension -notin '.ico', '.dll', '.exe') { continue }
        if ($file.Name -eq "$name.zip") { continue }
        Write-Step "removed stale $($file.Name)"
        [System.IO.File]::Delete($file.FullName)
    }
    $dropped = Sync-Directory (Join-Path $publish 'runtime') (Join-Path $release 'runtime')
    foreach ($gone in $dropped) { Write-Step "removed stale runtime\$gone" }

    # ── Zip from the publish output too, so nothing local can creep in. ─
    $zip = Join-Path $release "$name.zip"
    if (Test-Path $zip) { [System.IO.File]::Delete($zip) }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # ── Guard: say what shipped, so a surprise is visible rather than not.
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    $archive.Dispose()
    # The exe, any icon it ships (the app's own, and Studio's document icon for
    # the .simplified association), and the runtime folder. Anything else is a
    # surprise and stops the release.
    $unexpected = @($entries | Where-Object {
        $_ -notlike 'runtime\*' -and $_ -notlike '*.ico' -and $_ -ne "$name.exe"
    })
    if ($unexpected.Count -gt 0) {
        throw "unexpected files in the package: $($unexpected -join ', ')"
    }

    # ── Guard: the folder people run from must hold everything the zip does.
    #    The two are filled by different routes, so they can drift apart.
    $missing = @($entries | Where-Object { -not (Test-Path (Join-Path $release $_)) })
    if ($missing.Count -gt 0) {
        throw "in the zip but missing from $release : $($missing -join ', ')"
    }

    # ── Does it start? ─────────────────────────────────────────────────
    #
    # Still up after twelve seconds is the pass. Gone needs its code read:
    # closing the window yourself is a pass too — it ran, and someone shut it -
    # while a bare 0 is the app having quit on its own, which nothing in either
    # one does on purpose.
    Write-Step 'checking it starts (closing the window is fine)'
    $proc = Start-Process (Join-Path $release "$name.exe") -PassThru
    Start-Sleep -Seconds 12
    if (-not $proc.HasExited) {
        $run = 'ok'
        $proc | Stop-Process -Force
    } elseif ($proc.ExitCode -eq $ClosedByUser) {
        $run = 'ok, you closed it'
    } else {
        $run = "EXITED $($proc.ExitCode)"
    }

    if (-not $KeepBuildOutput) {
        Start-Sleep -Seconds 2
        $freed = 0
        Get-ChildItem (Join-Path $tree 'src') -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\.*\\(bin|obj)$' } |
            Sort-Object { $_.FullName.Length } -Descending |
            ForEach-Object {
                $size = (Get-ChildItem $_.FullName -Recurse -File -ErrorAction SilentlyContinue |
                         Measure-Object Length -Sum).Sum
                try { [System.IO.Directory]::Delete($_.FullName, $true); $freed += $size } catch { }
            }
        Write-Step ("cleaned {0:N0} MB of build output" -f ($freed / 1MB))
    }

    return [pscustomobject]@{
        App     = $name
        Tag     = $tag
        Commit  = $head.Substring(0, 7)
        ExeKB   = [int]((Get-Item (Join-Path $release "$name.exe")).Length / 1KB)
        ZipKB   = [int]((Get-Item $zip).Length / 1KB)
        Files   = $entries.Count
        Run     = $run
    }
}

# ── Run ────────────────────────────────────────────────────────────────
$worktrees = Get-Worktrees
if ($worktrees.Count -eq 0) { throw "No app worktrees found. Run this from inside the repository." }

$targets = $worktrees
if ($App -ne 'All') { $targets = $worktrees | Where-Object { $_.Short -eq $App } }
if (-not $targets) {
    throw "$App has no worktree. Found: $(($worktrees | ForEach-Object { $_.Short }) -join ', ')"
}

if ($DryRun) { Write-Host "DRY RUN - nothing will be published, written or tagged" -ForegroundColor Yellow }

$results = @()
foreach ($target in $targets) {
    $result = Invoke-Release $target
    if ($result) { $results += $result }
}

if ($results.Count -gt 0) {
    Write-Head 'Released'
    $results | Format-Table App, Tag, Commit, ExeKB, ZipKB, Files, Run -AutoSize
}
