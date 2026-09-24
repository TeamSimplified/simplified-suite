<#
.SYNOPSIS
    Record the SHA-256 of the game files packaging reads, for Shared\game-files.json.

.DESCRIPTION
    Packaging merges a mod's resources into the game's own shader archives, which
    have to come from the user's install. That makes a mod already sitting in one
    a real hazard: it goes out inside somebody's package, under their name,
    without anyone seeing it happen. GameFileGuard weighs those archives against
    the hashes this writes.

    Run it against a CLEAN install after every game update - a Steam "verify
    integrity of game files" first is the cheap way to be sure it is clean, and
    the whole point of the file is that what goes in here is the game's own.

    Builds accumulate. A user a patch behind is not doing anything wrong, so old
    entries stay and a file matching any recorded build passes.

.PARAMETER GameDir
    The install. Detected from the usual Steam and EA locations when omitted.

.PARAMETER Note
    A sentence about the build, kept beside it.

.EXAMPLE
    .\tools\game-hashes.ps1
    .\tools\game-hashes.ps1 -GameDir "D:\Games\Steam\steamapps\common\F1 25"
#>
[CmdletBinding()]
param(
    [string] $GameDir,
    [string] $Note = ''
)

$ErrorActionPreference = 'Stop'
$Suite = Split-Path $PSScriptRoot -Parent
$Manifest = Join-Path $Suite 'Shared\game-files.json'

# ── Find the install ───────────────────────────────────────────────────
if (-not $GameDir) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Steam\steamapps\common\F1 25"
        "$env:ProgramFiles\Steam\steamapps\common\F1 25"
        "D:\Games\Steam\steamapps\common\F1 25"
        "$env:ProgramFiles\EA Games\F1 25"
    )
    $GameDir = $candidates | Where-Object { Test-Path (Join-Path $_ 'shader_package_2025') } | Select-Object -First 1
}
if (-not $GameDir -or -not (Test-Path (Join-Path $GameDir 'shader_package_2025'))) {
    throw "No F1 25 install found. Pass -GameDir with the folder that holds shader_package_2025."
}

$exe = Get-ChildItem $GameDir -Filter 'F1_*.exe' | Select-Object -First 1
if (-not $exe) { throw "No F1_*.exe at the top of $GameDir - is that the install root?" }
$vi = $exe.VersionInfo
# Built from the numeric parts, as Simplified.Shared.GameVersion does: this
# title's ProductVersion carries commas, and the two spellings have to compare
# equal or recording the build is pointless.
$version = "$($vi.FileMajorPart).$($vi.FileMinorPart).$($vi.FileBuildPart).$($vi.FilePrivatePart)"
# The title's own name for itself, as Simplified.Shared.GameVersion.ReadName
# takes it. Recorded beside the build so a future game cannot be mistaken for
# this one on a version number alone.
$game = $vi.ProductName
if (-not $game) { $game = [IO.Path]::GetFileNameWithoutExtension($exe.Name).Replace('_', ' ') }

Write-Host "  install  $GameDir"
Write-Host "  game     $game"
Write-Host "  build    $version"

# ── What gets recorded ─────────────────────────────────────────────────
# Every shader archive, because which pair a package merges into is the
# project's business - Studio takes the one its .serps names, and that could be
# any of them. Plus the database, which the editors now read from the install.
$wanted = @()
$wanted += Get-ChildItem (Join-Path $GameDir 'shader_package_2025') -Filter *.erp |
           ForEach-Object { "shader_package_2025/$($_.Name)" }
$database = 'game_package/gamedatabases/gamedatabases.2025.bin'
if (Test-Path (Join-Path $GameDir $database.Replace('/', '\'))) { $wanted += $database }

Write-Host "  hashing  $($wanted.Count) file(s)..."
$files = [ordered]@{}
foreach ($rel in $wanted) {
    $full = Join-Path $GameDir $rel.Replace('/', '\')
    $files[$rel] = (Get-FileHash $full -Algorithm SHA256).Hash
}

# ── Merge into the manifest ────────────────────────────────────────────
$doc = if (Test-Path $Manifest) { Get-Content $Manifest -Raw | ConvertFrom-Json } else { $null }

$builds = @()
if ($doc -and $doc.builds) {
    # Same build recorded twice means this one replaces it; a re-run after a
    # repair should correct what is on file rather than sit beside it.
    $builds = @($doc.builds | Where-Object { $_.version -ne $version })
}

$entry = [ordered]@{
    game    = $game
    version = $version
    taken   = (Get-Date -Format 'yyyy-MM-dd')
    note    = $Note
    files   = $files
}

# Newest first, so a reader sees the current build at the top.
$builds = @($entry) + $builds

$out = [ordered]@{
    _comment = 'SHA-256 of the game files packaging reads from the install, taken from clean installs. Written by tools/game-hashes.ps1; read by Simplified.Shared.GameFileGuard. Several builds are kept on purpose - a user a patch behind is not doing anything wrong.'
    builds   = $builds
}

$json = $out | ConvertTo-Json -Depth 6
Set-Content -Path $Manifest -Value $json -Encoding utf8

Write-Host ""
Write-Host "  wrote    $Manifest" -ForegroundColor Green
Write-Host "  builds   $(($builds | ForEach-Object { "$($_.game) $($_.version)" }) -join ', ')"
