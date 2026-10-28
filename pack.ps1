param(
    [Parameter(Mandatory = $true)]
    [string]$Version,
    [switch]$Upload,
    [switch]$Prerelease,
    [string]$Channel = 'win'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'artifacts\publish'
$releases = Join-Path $root 'Releases'
$icon = Join-Path $root 'src\VibeNotch.App\Assets\app.ico'
$repoUrl = 'https://github.com/changshiyu12138/vibe-notch'

function Resolve-Dotnet {
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    $candidates += (Join-Path $env:USERPROFILE '.dotnet\dotnet.exe')
    $candidates += (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe')
    foreach ($candidate in $candidates) {
        if (Test-Path $candidate) {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and ($sdks | Where-Object { $_ -match '^8\.' })) { return $candidate }
        }
    }
    throw 'A .NET 8 SDK was not found.'
}

function Resolve-Vpk {
    $cmd = Get-Command vpk -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
    if (Test-Path $candidate) { return $candidate }
    throw 'vpk was not found. Install it with: dotnet tool install -g vpk'
}

function Resolve-Gh {
    $cmd = Get-Command gh -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $candidate = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe'
    if (Test-Path $candidate) { return $candidate }
    throw 'GitHub CLI was not found.'
}

$dotnet = Resolve-Dotnet
$vpk = Resolve-Vpk

Write-Host "Publishing Vibe Notch $Version..."
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $publish | Out-Null

& $dotnet publish (Join-Path $root 'src\VibeNotch.Hook\VibeNotch.Hook.csproj') `
    -c Release -r win-x64 --self-contained false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'Hook publish failed.' }

& $dotnet publish (Join-Path $root 'src\VibeNotch.App\VibeNotch.App.csproj') `
    -c Release -r win-x64 --self-contained false -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw 'App publish failed.' }

Write-Host 'Packing with Velopack...'
& $vpk pack `
    --packId VibeNotch `
    --packVersion $Version `
    --packDir $publish `
    --mainExe VibeNotch.exe `
    --runtime win-x64 `
    --packTitle 'Vibe Notch' `
    --packAuthors 'changshiyu12138' `
    --framework net8-x64-desktop `
    --icon $icon `
    --channel $Channel `
    --outputDir $releases
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed.' }

if ($Upload) {
    $gh = Resolve-Gh
    $token = & $gh auth token
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($token)) {
        throw 'GitHub CLI is not logged in. Run: gh auth login'
    }

    $uploadArgs = @(
        'upload', 'github',
        '--outputDir', $releases,
        '--channel', $Channel,
        '--repoUrl', $repoUrl,
        '--token', $token,
        '--publish',
        '--merge',
        '--tag', "v$Version",
        '--releaseName', "Vibe Notch v$Version"
    )
    if ($Prerelease) { $uploadArgs += '--pre' }

    Write-Host "Uploading release v$Version to GitHub..."
    & $vpk @uploadArgs
    if ($LASTEXITCODE -ne 0) { throw 'vpk upload github failed.' }
}

Write-Host ''
Write-Host "Done. Packages are in $releases"
