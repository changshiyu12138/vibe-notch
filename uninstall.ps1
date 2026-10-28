param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:APPDATA 'VibeNotch'
$appDir = Join-Path $env:LOCALAPPDATA 'Programs\VibeNotch'
$appExe = Join-Path $appDir 'VibeNotch.exe'
$velopackRoot = Join-Path $env:LOCALAPPDATA 'VibeNotch'
$legacyDataDir = Join-Path $env:LOCALAPPDATA 'QoderVibeNotch'

# Hook removal needs a runnable app; a Velopack install keeps its binary under
# current\, a source install under Programs\VibeNotch.
if (-not (Test-Path $appExe)) {
    $velopackExe = Join-Path $velopackRoot 'current\VibeNotch.exe'
    if (Test-Path $velopackExe) { $appExe = $velopackExe }
}

Write-Host 'Removing Qoder hooks...'
if (Test-Path $appExe) {
    $proc = Start-Process -FilePath $appExe -ArgumentList '--uninstall-hooks' -Wait -PassThru -WindowStyle Hidden
    if ($proc.ExitCode -ne 0) {
        Write-Warning 'Hook removal reported an error. A backup was kept next to ~/.qoder/settings.json.'
    } else {
        Write-Host 'Hooks removed from ~/.qoder/settings.json'
    }
} else {
    Write-Warning "App not found at $appExe - remove the VibeNotch.Hook.exe entries from ~/.qoder/settings.json manually."
}

Write-Host 'Stopping Vibe Notch...'
Get-Process VibeNotch -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process VibeNotch.Hook -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process QoderVibeNotch -ErrorAction SilentlyContinue | Stop-Process -Force
Get-Process QoderVibeNotch.Hook -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300

$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
foreach ($valueName in 'VibeNotch', 'QoderVibeNotch') {
    try {
        Remove-ItemProperty -Path $runKey -Name $valueName -ErrorAction Stop
        Write-Host "Removed auto start entry ($valueName)."
    } catch {
        # value did not exist
    }
}

foreach ($name in 'Vibe Notch', 'Qoder Vibe Notch') {
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
    if (Test-Path $shortcut) {
        Remove-Item $shortcut -Force
        Write-Host "Removed start menu shortcut ($name)."
    }
}

foreach ($protocol in 'HKCU:\Software\Classes\vibe-notch', 'HKCU:\Software\Classes\qoder-vibe-notch') {
    if (Test-Path $protocol) {
        Remove-Item $protocol -Recurse -Force
        Write-Host "Removed toast protocol registration ($protocol)."
    }
}

# Source install only; a Velopack install is removed from Windows Settings >
# Installed apps (or %LOCALAPPDATA%\VibeNotch\Update.exe --uninstall).
if (Test-Path $appDir) {
    Remove-Item $appDir -Recurse -Force
    Write-Host "Removed program files at $appDir."
}

if ($PurgeData) {
    foreach ($dir in $dataDir, $legacyDataDir) {
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force
            Write-Host "Deleted $dir"
        }
    }
} else {
    Write-Host "Kept settings and logs in $dataDir (run with -PurgeData to delete)."
}

Write-Host ''
Write-Host 'Uninstalled.'
if (Test-Path (Join-Path $velopackRoot 'Update.exe')) {
    Write-Host 'The Velopack install is still present; remove it from Windows Settings > Apps > Installed apps.'
}
