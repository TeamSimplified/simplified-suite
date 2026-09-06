<#
.SYNOPSIS
    Publish, package and tag the apps in the Simplified Suite.

.DESCRIPTION
    One command for what was a six-step manual routine per app. The steps are
    not the point - the guards are. Each one below exists because the manual
    version got it wrong at least once:

      * a dirty tree is refused, so a tag cannot claim a commit the binary
        does not contain
      * the release folder's subfolders are mirrored, not copied over, so a
        file that stops being part of the build does not linger there forever
      * staging copies only from the publish output, never from the release
        folder, so the config.json an app writes on first run cannot end up
        inside a package
      * the packaged exe's stamp is checked against HEAD, so "is this build
        that commit?" is answered rather than assumed
      * what lands in the zip is checked against what that app is supposed to
        ship, so a stray file stops the release instead of shipping

    The suite is one repository, so every app releases from the same commit.
    Apps are listed rather than discovered: the source tree also holds dev
    harnesses and libraries, and guessing which .csproj is an app is how a
    smoke test ends up packaged.

.PARAMETER App
    Studio, ERP, Toolkit, Chassis, Driverwear, Helmet, TeamEditor, Swapper, Driver, Grid, or All (the default).

.PARAMETER DryRun
    Say what would happen and change nothing.

.PARAMETER NoTag
    Package without moving version tags.

.PARAMETER AllowDirty
    Package a tree with uncommitted changes. The build will not match its tag;
    only useful for trying a package out.

.PARAMETER KeepBuildOutput
    Leave bin/obj in place. They are removed by default.

.EXAMPLE
    .\tools\release.ps1 -DryRun
    .\tools\release.ps1 -App Toolkit
#>
[CmdletBinding()]
param(
    [ValidateSet('Studio', 'ERP', 'Toolkit', 'Chassis', 'Driverwear', 'Helmet', 'TeamEditor', 'Swapper', 'Driver', 'Grid', 'All')]
    [string] $App = 'All',
    [switch] $DryRun,
    [switch] $NoTag,
    [switch] $AllowDirty,
    [switch] $KeepBuildOutput
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

# The suite root: this script lives in tools\ directly beneath it.
$Suite = Split-Path $PSScriptRoot -Parent

# What both windowed apps report when a person closed the window - see
# App.ClosedByUserExitCode. None of them shuts itself down, so this separates
# "somebody shut it" from "it quit on its own", which would otherwise both
# arrive as the 0 that success uses.
$ClosedByUser = 1000

# Every app, and only the apps.
#
#   Folder   top level folder in the suite, which is also the source tree
#   Project  the app's .csproj, relative to Folder
#   Exe      what the publish produces
#   Package  what the release folder and zip are called
#   Tag      prefix; Studio keeps the bare "v" it has always had
#   Kind     window apps are smoke-launched, the CLI is asked its version
#   Ships    what may appear in the package. Anything else stops the release.
$Apps = @(
    [pscustomobject]@{
        Short = 'Studio'; Folder = 'Simplified Studio'
        Project = 'src\SimplifiedStudio\SimplifiedStudio.csproj'
        Exe = 'Simplified Studio'; Package = 'Simplified Studio'
        Tag = 'v'; Kind = 'window'; Ships = @('*.exe', '*.ico', 'runtime\*')
    }
    [pscustomobject]@{
        Short = 'ERP'; Folder = 'Simplified ERP'
        Project = 'src\SimplifiedStudio\SimplifiedStudio.csproj'
        Exe = 'Simplified ERP'; Package = 'Simplified ERP'
        Tag = 'erp-v'; Kind = 'window'; Ships = @('*.exe', '*.ico', 'runtime\*')
    }
    [pscustomobject]@{
        Short = 'Toolkit'; Folder = 'Simplified Toolkit'
        Project = 'src\SimplifiedToolkit\SimplifiedToolkit.csproj'
        Exe = 'stk'; Package = 'Simplified Toolkit'
        Tag = 'stk-v'; Kind = 'cli'; Ships = @('stk.exe', 'tools\*')
    }
    [pscustomobject]@{
        Short = 'Chassis'; Folder = 'Simplified Chassis Swapper'
        Project = 'src\SimplifiedChassisSwapper\SimplifiedChassisSwapper.csproj'
        Exe = 'Simplified Chassis Swapper'; Package = 'Simplified Chassis Swapper'
        Tag = 'chassis-v'; Kind = 'window'; Ships = @('*.exe', 'runtime\*')
        # Superseded by Simplified Swapper, which does both swaps in one
        # window. Kept building and kept consistent with the suite, so it
        # is ready if it is ever wanted; just not shipped alongside the
        # app that replaced it. -App Chassis still packages it.
        InAll = $false
    }
    [pscustomobject]@{
        Short = 'Driverwear'; Folder = 'Simplified Driverwear Swapper'
        Project = 'src\SimplifiedDriverwearSwapper\SimplifiedDriverwearSwapper.csproj'
        Exe = 'Simplified Driverwear Swapper'; Package = 'Simplified Driverwear Swapper'
        Tag = 'driverwear-v'; Kind = 'window'; Ships = @('*.exe', 'runtime\*')
        # Superseded by Simplified Swapper, which does both swaps in one
        # window. Kept building and kept consistent with the suite, so it
        # is ready if it is ever wanted; just not shipped alongside the
        # app that replaced it. -App Driverwear still packages it.
        InAll = $false
    }
    [pscustomobject]@{
        Short = 'Helmet'; Folder = 'Simplified Helmet Swapper'
        Project = 'src\SimplifiedHelmetSwapper\SimplifiedHelmetSwapper.csproj'
        Exe = 'Simplified Helmet Swapper'; Package = 'Simplified Helmet Swapper'
        Tag = 'helmet-v'; Kind = 'window'; Ships = @('*.exe', 'runtime\*')
        # Held back from a suite release: the shell swap has not been
        # confirmed in game yet. Still releasable on its own with
        # -App Helmet, which is how it goes out once it has been.
        InAll = $false
    }
    [pscustomobject]@{
        Short = 'TeamEditor'; Folder = 'Simplified Team Editor'
        Project = 'src\SimplifiedTeamEditor\SimplifiedTeamEditor.csproj'
        Exe = 'Simplified Team Editor'; Package = 'Simplified Team Editor'
        Tag = 'teameditor-v'; Kind = 'window'
        Ships = @('*.exe', '*.ico', 'runtime\*')
        # Superseded by the Grid Editor, which is this and the Driver Swapper in
        # one window over one project. Kept building and kept consistent with
        # the suite, so it is ready if it is ever wanted; just not shipped
        # alongside the app that replaced it. -App TeamEditor still packages it.
        InAll = $false
    }
    [pscustomobject]@{
        Short = 'Swapper'; Folder = 'Simplified Swapper'
        Project = 'src\SimplifiedSwapper\SimplifiedSwapper.csproj'
        Exe = 'Simplified Swapper'; Package = 'Simplified Swapper'
        Tag = 'swapper-v'; Kind = 'window'; Ships = @('*.exe', 'runtime\*')
    }
    [pscustomobject]@{
        Short = 'Grid'; Folder = 'Simplified Grid Editor'
        Project = 'src\SimplifiedGridEditor\SimplifiedGridEditor.csproj'
        Exe = 'Simplified Grid Editor'; Package = 'Simplified Grid Editor'
        Tag = 'grid-v'; Kind = 'window'
        Ships = @('*.exe', '*.ico', 'runtime\*')
    }
    [pscustomobject]@{
        Short = 'Driver'; Folder = 'Simplified Driver Swapper'
        Project = 'src\SimplifiedDriverSwapper\SimplifiedDriverSwapper.csproj'
        Exe = 'Simplified Driver Swapper'; Package = 'Simplified Driver Swapper'
        Tag = 'driver-v'; Kind = 'window'; Ships = @('*.exe', '*.ico', 'runtime\*')
        # Superseded by the Grid Editor, which is this and the Team Editor in one
        # window over one project. Kept building and kept consistent with the
        # suite, so it is ready if it is ever wanted; just not shipped alongside
        # the app that replaced it. -App Driver still packages it.
        InAll = $false
    }
)

function Write-Step { param([string] $Message) Write-Host "  $Message" }
function Write-Head { param([string] $Message) Write-Host ""; Write-Host $Message -ForegroundColor Cyan }

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
    param([string] $Source, [string] $Destination, [string] $Prefix = '')

    if (-not (Test-Path $Source)) { return @() }
    if (-not (Test-Path $Destination)) { New-Item -ItemType Directory $Destination -Force | Out-Null }

    $wanted = @{}
    foreach ($file in Get-ChildItem $Source -File) {
        $wanted[$file.Name] = $true
        Copy-Item $file.FullName (Join-Path $Destination $file.Name) -Force
    }

    $removed = @()
    foreach ($file in Get-ChildItem $Destination -File) {
        if (-not $wanted.ContainsKey($file.Name)) {
            $removed += "$Prefix$($file.Name)"
            [System.IO.File]::Delete($file.FullName)
        }
    }

    # Subfolders as well. This used to stop at one level, which was invisible
    # while every app's only subfolder was a flat runtime\ or tools\ - and then
    # the Team Editor arrived carrying assets\original\localisation\, and the
    # zip had it while the folder people run from did not.
    $wantedDirs = @{}
    foreach ($dir in Get-ChildItem $Source -Directory) {
        $wantedDirs[$dir.Name] = $true
        $removed += Sync-Directory $dir.FullName (Join-Path $Destination $dir.Name) "$Prefix$($dir.Name)\"
    }
    foreach ($dir in Get-ChildItem $Destination -Directory) {
        if ($wantedDirs.ContainsKey($dir.Name)) { continue }
        $removed += "$Prefix$($dir.Name)\"
        [System.IO.Directory]::Delete($dir.FullName, $true)
    }

    return $removed
}

function Test-Ships {
    <# Is this package entry one this app is supposed to ship? #>
    param([string] $Entry, [string[]] $Patterns)
    foreach ($pattern in $Patterns) { if ($Entry -like $pattern) { return $true } }
    return $false
}

function Invoke-Release {
    param([object] $Target, [string] $Head)

    $tree    = Join-Path $Suite $Target.Folder
    $version = Get-DeclaredVersion $tree
    $tag     = "$($Target.Tag)$version"
    # Every package under one folder, beside the sources rather than inside
    # them: what someone downloads is not part of what builds it.
    $release = Join-Path (Join-Path $Suite 'Release') $Target.Package
    $project = Join-Path $tree $Target.Project
    $publish = Join-Path (Split-Path $project -Parent) 'bin\Release\net10.0-windows\win-x64\publish'
    $short   = $Head.Substring(0, 7)

    Write-Head "$($Target.Package)  (v$version @ $short)"

    if ($DryRun) {
        Write-Step "would publish   $publish"
        Write-Step "would sync      $release"
        Write-Step "would write     $release\$($Target.Package).zip"
        if (-not $NoTag) { Write-Step "would move tag  $tag -> $short" }
        return $null
    }

    # ── Tag first, so the publish stamps the commit the tag points at. ─
    if (-not $NoTag) {
        git -C $Suite tag -f -a $tag $Head -m "$($Target.Package) v$version" | Out-Null
        Write-Step "tag $tag -> $short"
    }

    # ── Publish from scratch: a stale publish folder is a stale package. ─
    if (Test-Path $publish) { [System.IO.Directory]::Delete($publish, $true) }
    & dotnet publish $project -c Release -r win-x64 -v q --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $($Target.Package)" }

    $exe = Join-Path $publish "$($Target.Exe).exe"
    if (-not (Test-Path $exe)) { throw "publish produced no $($Target.Exe).exe" }

    # ── Guard: the exe has to carry the commit being released. ─────────
    $stamp = (Get-Item $exe).VersionInfo.ProductVersion
    if (-not $AllowDirty -and $stamp -notlike "*$short*") {
        throw "$($Target.Exe).exe is stamped '$stamp', which does not carry HEAD $short"
    }

    # ── Sync the release folder from the publish output, and only from it.
    #    Files an app writes at the release root - config.json, the team
    #    marker - are the user's and are left alone; the build's own
    #    subfolders are mirrored exactly.
    if (-not (Test-Path $release)) { New-Item -ItemType Directory $release -Force | Out-Null }

    $shipped = @{}
    foreach ($file in Get-ChildItem $publish -File) {
        $shipped[$file.Name] = $true
        Copy-Item $file.FullName (Join-Path $release $file.Name) -Force
    }

    # The root is not mirrored wholesale, because config.json and the like
    # belong to the user and the build knows nothing about them. But anything
    # with a build-owned extension that the publish no longer produces is
    # leftover - the icons, when they moved into runtime\ - and would otherwise
    # sit there forever looking current.
    foreach ($file in Get-ChildItem $release -File) {
        if ($shipped.ContainsKey($file.Name)) { continue }
        if ($file.Extension -notin '.ico', '.dll', '.exe', '.pdb') { continue }
        if ($file.Name -eq "$($Target.Package).zip") { continue }
        Write-Step "removed stale $($file.Name)"
        [System.IO.File]::Delete($file.FullName)
    }

    foreach ($dir in Get-ChildItem $publish -Directory) {
        foreach ($gone in (Sync-Directory $dir.FullName (Join-Path $release $dir.Name))) {
            Write-Step "removed stale $($dir.Name)\$gone"
        }
    }
    # A subfolder the publish stopped producing goes too.
    foreach ($dir in Get-ChildItem $release -Directory) {
        if (Test-Path (Join-Path $publish $dir.Name)) { continue }
        Write-Step "removed stale $($dir.Name)\"
        [System.IO.Directory]::Delete($dir.FullName, $true)
    }

    # ── Zip from the publish output too, so nothing local can creep in. ─
    $zip = Join-Path $release "$($Target.Package).zip"
    if (Test-Path $zip) { [System.IO.File]::Delete($zip) }
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $publish, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

    # ── Guard: only what this app ships may be in the package. ─────────
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    $entries = @($archive.Entries | ForEach-Object { $_.FullName })
    $archive.Dispose()

    $unexpected = @($entries | Where-Object { -not (Test-Ships $_ $Target.Ships) })
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
    $run = Test-Starts $Target (Join-Path $release "$($Target.Exe).exe")

    if (-not $KeepBuildOutput) {
        Start-Sleep -Seconds 2
        $freed = 0
        Get-ChildItem $tree -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
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
        App    = $Target.Package
        Tag    = $tag
        Commit = $short
        ExeKB  = [int]((Get-Item (Join-Path $release "$($Target.Exe).exe")).Length / 1KB)
        ZipKB  = [int]((Get-Item $zip).Length / 1KB)
        Files  = $entries.Count
        Run    = $run
    }
}

function Test-Starts {
    <#
        A windowed app still up after twelve seconds is the pass. Gone needs its
        code read: closing the window yourself is a pass too - it ran, and
        someone shut it - while a bare 0 is the app having quit on its own,
        which nothing here does on purpose.

        The CLI is the other way round: it is supposed to exit. Asking its
        version proves it starts, loads its libraries and answers.
    #>
    param([object] $Target, [string] $Exe)

    if ($Target.Kind -eq 'cli') {
        # Collected whole, not piped into Select-Object: taking the first line
        # from a live pipeline closes it, which kills the process and reports
        # -1 for an exe that answered perfectly well.
        $output = & $Exe --version
        $code = $LASTEXITCODE
        if ($code -ne 0) { return "EXITED $code" }
        if (-not @($output)[0]) { return 'no version' }
        return 'ok'
    }

    Write-Step 'checking it starts (closing the window is fine)'
    $proc = Start-Process $Exe -PassThru
    Start-Sleep -Seconds 12
    if (-not $proc.HasExited) {
        $proc | Stop-Process -Force
        return 'ok'
    }
    if ($proc.ExitCode -eq $ClosedByUser) { return 'ok, you closed it' }
    return "EXITED $($proc.ExitCode)"
}

# ── Run ────────────────────────────────────────────────────────────────

if (-not (Test-Path (Join-Path $Suite '.git'))) {
    throw "No repository at $Suite. Run this from inside the suite."
}

$targets = if ($App -eq 'All') { $Apps | Where-Object { $_.InAll -ne $false } }
           else { $Apps | Where-Object { $_.Short -eq $App } }
if (-not $targets) { throw "No app called '$App'." }

if ($App -eq 'All') {
    $held = @($Apps | Where-Object { $_.InAll -eq $false } | ForEach-Object { $_.Package })
    if ($held) { Write-Host "  holding back: $($held -join ', ')" -ForegroundColor DarkYellow }
}

$head = (git -C $Suite rev-parse HEAD).Trim()

# ── Guard: one repository, so one dirty check for all of them. ─────────
$dirty = git -C $Suite status --porcelain
if ($dirty) {
    if (-not $AllowDirty) {
        Write-Host "REFUSED: uncommitted changes. Commit them, or pass -AllowDirty to package anyway." -ForegroundColor Red
        $dirty | Select-Object -First 5 | ForEach-Object { Write-Host "    $_" }
        exit 1
    }
    Write-Host "WARNING: packaging a dirty tree - the builds will not match their tags." -ForegroundColor Yellow
}

if ($DryRun) { Write-Host "DRY RUN - nothing will be published, written or tagged" -ForegroundColor Yellow }

$results = @()
foreach ($target in $targets) {
    $result = Invoke-Release $target $head
    if ($result) { $results += $result }
}

if ($results.Count -gt 0) {
    Write-Head 'Released'
    $results | Format-Table App, Tag, Commit, ExeKB, ZipKB, Files, Run -AutoSize
}

# Explicit, so the script's own status is not whatever the last native command
# happened to leave in $LASTEXITCODE.
exit 0
