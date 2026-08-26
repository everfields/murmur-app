<#
.SYNOPSIS
    Publishes Murmur and creates Start Menu / Desktop shortcuts for it.

.DESCRIPTION
    Per-user install, no administrator rights: the app lands in
    %LOCALAPPDATA%\Murmur\app, beside the models, settings and log the app already
    keeps in %LOCALAPPDATA%\Murmur. Nothing is written outside the user profile.

    The whole publish directory is installed, not just the exe, and the shortcut sets
    WorkingDirectory to it: Murmur.Platform.Windows.dll and the NAudio DLLs are
    resolved at run time rather than referenced (see Murmur.App.csproj), so anything
    the publish step puts beside the exe has to travel with it. Verify an install with
    `Murmur.App.exe --selftest`, which is the check that catches a platform layer that
    did not come along.

    ASCII only, deliberately: Windows PowerShell 5.1 reads .ps1 files as ANSI, and a
    stray em dash lands in the shortcut's tooltip as mojibake.

.PARAMETER InstallDir
    Where to install. Default %LOCALAPPDATA%\Murmur\app.

.PARAMETER NoDesktop
    Skip the Desktop shortcut.

.PARAMETER NoStartMenu
    Skip the Start Menu shortcut.

.PARAMETER SkipPublish
    Reuse whatever is already in InstallDir; only (re)create the shortcuts.

.PARAMETER Uninstall
    Remove the shortcuts and the install directory. Leaves models, settings,
    transcripts and the log alone.

.EXAMPLE
    pwsh -File tools/install-shortcut.ps1
.EXAMPLE
    pwsh -File tools/install-shortcut.ps1 -NoDesktop
.EXAMPLE
    pwsh -File tools/install-shortcut.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Murmur\app'),
    [switch] $NoDesktop,
    [switch] $NoStartMenu,
    [switch] $SkipPublish,
    [switch] $Uninstall
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$windowsRoot = Split-Path -Parent $PSScriptRoot
$project     = Join-Path $windowsRoot 'src\Murmur.App\Murmur.App.csproj'
$iconSource  = Join-Path $windowsRoot 'src\Murmur.App\Assets\tray.ico'

$shortcutName = 'Murmur.lnk'
$startMenuDir = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
$desktopDir   = [Environment]::GetFolderPath('Desktop')
$startMenuLnk = Join-Path $startMenuDir $shortcutName
$desktopLnk   = Join-Path $desktopDir   $shortcutName

$exePath  = Join-Path $InstallDir 'Murmur.App.exe'
$iconPath = Join-Path $InstallDir 'Murmur.ico'

# The exe holds a lock on itself while running, and single-file self-extraction means a
# stale process can also pin the extracted natives. Close it before touching the files.
function Stop-Murmur {
    $running = Get-Process -Name 'Murmur.App' -ErrorAction SilentlyContinue
    if ($running) {
        Write-Host 'Closing the running Murmur...'
        $running | Stop-Process -Force
        $running | Wait-Process -Timeout 10 -ErrorAction SilentlyContinue
    }
}

function New-Shortcut {
    param([string] $LinkPath, [string] $Target, [string] $WorkDir, [string] $Icon)

    $parent = Split-Path -Parent $LinkPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    # WScript.Shell is the only shortcut writer present on a stock Windows with no extra
    # modules or compilation. COM object, so release it explicitly rather than waiting for
    # the GC to drop the RCW.
    $shell = New-Object -ComObject WScript.Shell
    try {
        $lnk = $shell.CreateShortcut($LinkPath)
        $lnk.TargetPath       = $Target
        $lnk.WorkingDirectory = $WorkDir
        $lnk.Description      = 'Murmur - push-to-talk dictation'
        if (Test-Path -LiteralPath $Icon) { $lnk.IconLocation = "$Icon,0" }
        $lnk.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    }
    Write-Host "  $LinkPath"
}

if ($Uninstall) {
    Stop-Murmur
    foreach ($lnk in @($startMenuLnk, $desktopLnk)) {
        if (Test-Path -LiteralPath $lnk) {
            Remove-Item -LiteralPath $lnk -Force
            Write-Host "removed $lnk"
        }
    }
    if (Test-Path -LiteralPath $InstallDir) {
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
        Write-Host "removed $InstallDir"
    }
    Write-Host 'Models, settings, transcripts and the log were left in place.'
    return
}

if (-not $SkipPublish) {
    if (-not (Test-Path -LiteralPath $project)) { throw "Project not found: $project" }

    Stop-Murmur

    # Publish to a staging directory first: publishing straight into the install
    # directory would leave it half-written if the build fails, and the shortcut would
    # then point at a broken exe.
    $staging = Join-Path $windowsRoot 'artifacts\install-staging'
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

    Write-Host 'Publishing Murmur (self-contained, single file)...'
    # Same switches CI uses, so what lands on the Desktop is the artifact that gets
    # smoke-tested on every push.
    dotnet publish $project `
        --configuration Release --runtime win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        --output $staging
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $staged = Join-Path $staging 'Murmur.App.exe'
    if (-not (Test-Path -LiteralPath $staged)) { throw "Published exe not found at $staged" }

    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    Copy-Item -Path (Join-Path $staging '*') -Destination $InstallDir -Recurse -Force
    Remove-Item -LiteralPath $staging -Recurse -Force

    # The exe carries no embedded icon, so ship the tray icon beside it and point the
    # shortcut at that - otherwise Windows draws the generic console-app icon.
    if (Test-Path -LiteralPath $iconSource) { Copy-Item -LiteralPath $iconSource -Destination $iconPath -Force }

    $mb = [math]::Round((Get-Item -LiteralPath $exePath).Length / 1MB, 1)
    Write-Host "Installed to $InstallDir ($mb MB exe)"
}

if (-not (Test-Path -LiteralPath $exePath)) {
    throw "No Murmur.App.exe in $InstallDir. Run without -SkipPublish to build one."
}

Write-Host 'Shortcuts:'
if (-not $NoStartMenu) { New-Shortcut -LinkPath $startMenuLnk -Target $exePath -WorkDir $InstallDir -Icon $iconPath }
if (-not $NoDesktop)   { New-Shortcut -LinkPath $desktopLnk   -Target $exePath -WorkDir $InstallDir -Icon $iconPath }

Write-Host ''
Write-Host 'Done. Press Start and type "Murmur", or use the Desktop shortcut.'
Write-Host 'Dictation needs the Parakeet model - see docs/PARAKEET-WINDOWS.md.'
