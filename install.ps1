param(
    [switch]$AutoStart,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dataDir = Join-Path $env:APPDATA 'VibeNotch'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\VibeNotch'
$appExe = Join-Path $appDir 'VibeNotch.exe'
$legacyDataDirs = @(
    (Join-Path $env:LOCALAPPDATA 'VibeNotch'),
    (Join-Path $env:LOCALAPPDATA 'QoderVibeNotch')
)
$legacyDataDir = Join-Path $env:LOCALAPPDATA 'QoderVibeNotch'
$startMenu = [Environment]::GetFolderPath('Programs')

function Resolve-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')

    foreach ($candidate in $candidates) {
        if (-not (Test-Path $candidate)) { continue }
        $sdks = & $candidate --list-sdks 2>$null
        if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^8\.' })) {
            return $candidate
        }
    }
    throw 'A .NET 8 SDK was not found. Install it first: https://dotnet.microsoft.com/download/dotnet/8.0'
}

$dotnet = Resolve-Dotnet

Write-Host 'Stopping running Vibe Notch...'
Get-Process VibeNotch -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process VibeNotch.Hook -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process QoderVibeNotch -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process QoderVibeNotch.Hook -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

New-Item -ItemType Directory -Force -Path $appDir | Out-Null
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

# Data now lives in %APPDATA%\VibeNotch; carry over whatever earlier builds
# left in %LOCALAPPDATA% and drop the old QoderVibeNotch shell integration.
foreach ($legacyDir in $legacyDataDirs) {
    if (-not (Test-Path $legacyDir)) { continue }
    foreach ($name in 'settings.json', 'stats.json') {
        $from = Join-Path $legacyDir $name
        $to = Join-Path $dataDir $name
        if ((Test-Path $from) -and -not (Test-Path $to)) {
            Copy-Item $from $to
            Write-Host "  copied $name from $legacyDir"
        }
    }
}

if (Test-Path $legacyDataDir) {
    Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'QoderVibeNotch' -ErrorAction SilentlyContinue
    Remove-Item 'HKCU:\Software\Classes\qoder-vibe-notch' -Recurse -Force -ErrorAction SilentlyContinue
    $legacyShortcut = Join-Path $startMenu 'Qoder Vibe Notch.lnk'
    if (Test-Path $legacyShortcut) { Remove-Item $legacyShortcut -Force; Write-Host '  removed old start menu shortcut' }
}

Write-Host 'Publishing hook client...'
& $dotnet publish (Join-Path $root 'src\VibeNotch.Hook\VibeNotch.Hook.csproj') -c Release -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed.' }

Write-Host 'Publishing app...'
& $dotnet publish (Join-Path $root 'src\VibeNotch.App\VibeNotch.App.csproj') -c Release -o $appDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

if ($AutoStart) {
    $settingsPath = Join-Path $dataDir 'settings.json'
    $settings = $null
    if (Test-Path $settingsPath) {
        try { $settings = Get-Content $settingsPath -Raw | ConvertFrom-Json } catch { $settings = $null }
    }
    if ($null -eq $settings) { $settings = New-Object psobject }
    $settings | Add-Member -MemberType NoteProperty -Name AutoStart -Value $true -Force
    $settings | ConvertTo-Json -Depth 6 | Set-Content -Path $settingsPath -Encoding UTF8
    Write-Host 'Auto start enabled.'
}

$shortcut = Join-Path $startMenu 'Vibe Notch.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $appExe
$link.WorkingDirectory = $appDir
$link.IconLocation = "$appExe,0"
$link.Description = 'Vibe Notch - floating island for AI coding sessions'
$link.Save()
Write-Host "Start menu shortcut: $shortcut"

# WScript keeps the .lnk mapped until COM is released; the app rewrites it with
# its AppUserModelID right after launch, so give the shell a moment here.
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($link)
[void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
Start-Sleep -Milliseconds 600

Write-Host 'Installing Qoder hooks...'
$proc = Start-Process -FilePath $appExe -ArgumentList '--install-hooks' -Wait -PassThru -WindowStyle Hidden
if ($proc.ExitCode -ne 0) { throw 'Hook installation failed. Check %APPDATA%\VibeNotch\logs\app.log' }
Write-Host 'Hooks installed into ~/.qoder/settings.json'

if (-not $NoLaunch) {
    Start-Process -FilePath $appExe -WorkingDirectory $appDir
    Write-Host 'Vibe Notch is running. Look for the thin dark strip at the top center of the screen.'
}

Write-Host ''
Write-Host 'Done. Uninstall with: powershell -ExecutionPolicy Bypass -File uninstall.ps1'
