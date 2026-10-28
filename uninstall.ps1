param(
    [switch]$PurgeData
)

$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:LOCALAPPDATA 'VibeNotch'
$appDir = Join-Path $dataDir 'app'
$appExe = Join-Path $appDir 'VibeNotch.exe'
$legacyDataDir = Join-Path $env:LOCALAPPDATA 'QoderVibeNotch'

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

if ($PurgeData) {
    foreach ($dir in $dataDir, $legacyDataDir) {
        if (Test-Path $dir) {
            Remove-Item $dir -Recurse -Force
            Write-Host "Deleted $dir"
        }
    }
} else {
    Write-Host "Kept data in $dataDir (run with -PurgeData to delete)."
    if (Test-Path $legacyDataDir) { Write-Host "Old QoderVibeNotch folder left at $legacyDataDir." }
}

Write-Host ''
Write-Host 'Uninstalled.'
