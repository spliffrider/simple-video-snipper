[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $CleanSetup,
    [Parameter(Mandatory)] [string] $UpgradeSetup,
    [Parameter(Mandatory)] [ValidatePattern('^SimpleVideoSnipper\.SetupCheck[A-Za-z0-9._-]*$')] [string] $Profile,
    [Parameter(Mandatory)] [version] $UpgradeVersion
)

$ErrorActionPreference = 'Stop'
$name = "Simple Video Snipper ($Profile)"
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ('..\.tools\installer\test-' + [guid]::NewGuid().ToString('N'))))
New-Item -ItemType Directory -Path $root | Out-Null
$appDir = Join-Path $root 'installed'
$recordings = Join-Path $root ('Clips ' + [char]0x00E9)
$profileKey = "HKCU:\Software\$Profile"
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\${Profile}_is1"
$userState = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) $Profile

foreach ($path in $CleanSetup, $UpgradeSetup) {
    if ((Get-Item -LiteralPath $path).VersionInfo.ProductName.Trim() -ne $name) {
        throw 'Use test installers built with the specified SetupCheck profile.'
    }
}
foreach ($path in $profileKey, $uninstallKey, $userState) {
    if (Test-Path -LiteralPath $path) { throw "Choose a new test profile. This location is already in use: $path" }
}

function Invoke-Setup([string]$setup, [string]$label, [string]$folder) {
    $log = Join-Path $root "$label.log"
    $args = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/SP-', '/NORESTART', ('/DIR="' + $appDir + '"'),
        ('/LOG="' + $log + '"'))
    if ($folder) { $args += '/RECORDINGSFOLDER="' + $folder + '"' }
    $setupProcess = Start-Process -FilePath $setup -ArgumentList $args -PassThru -WindowStyle Hidden
    if (-not $setupProcess.WaitForExit(45000)) {
        Stop-Process -InputObject $setupProcess
        throw "Setup timed out. See $log"
    }
    return $setupProcess.ExitCode
}

function Invoke-Uninstall {
    $uninstall = Join-Path $appDir 'unins000.exe'
    if (Test-Path -LiteralPath $uninstall) {
        $removal = Start-Process -FilePath $uninstall -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -PassThru -Wait -WindowStyle Hidden
        if ($removal.ExitCode -ne 0) { throw "The test uninstall failed with code $($removal.ExitCode)." }
    }
}

function Assert-Test([bool]$condition, [string]$description) {
    if (-not $condition) { throw "$description failed. See logs in $root" }
    Write-Host "$description passed."
}

function Test-InstalledApp {
    $exe = Join-Path $appDir 'SimpleVideoSnipper.exe'
    $savedPath = $env:PATH
    $savedDotnetRoot = $env:DOTNET_ROOT
    try {
        $env:PATH = "$env:SystemRoot\System32;$env:SystemRoot"
        $env:DOTNET_ROOT = $null
        $installed = Start-Process -FilePath $exe -PassThru -WindowStyle Hidden
        try {
            $runtime = Join-Path $appDir 'coreclr.dll'
            for ($attempt = 0; $attempt -lt 25; $attempt++) {
                $installed.Refresh()
                if ($installed.HasExited) { break }
                if ($installed.Modules.FileName -contains $runtime -and $installed.WaitForInputIdle(1000)) {
                    Assert-Test $true 'App launch with the installed .NET runtime'
                    return
                }
                Start-Sleep -Milliseconds 200
            }
            Assert-Test $false 'App launch with the installed .NET runtime'
        }
        finally {
            if (-not $installed.HasExited) {
                Stop-Process -InputObject $installed
                $installed.WaitForExit()
            }
            $installed.Dispose()
        }
    }
    finally {
        $env:PATH = $savedPath
        $env:DOTNET_ROOT = $savedDotnetRoot
    }
}

$blocked = Join-Path $root 'file-instead-of-folder'
Set-Content -LiteralPath $blocked -Value 'Installer test fixture'
try {
    $failed = Invoke-Setup $CleanSetup 'invalid' $blocked
    $failedLog = Get-Content -LiteralPath (Join-Path $root 'invalid.log') -Raw
    Assert-Test ($failed -ne 0 -and $failedLog.Contains('Could not create the temporary recordings folder') -and
        -not (Test-Path -LiteralPath $profileKey) -and
        -not (Test-Path -LiteralPath (Join-Path $appDir 'SimpleVideoSnipper.exe'))) 'Invalid storage is rejected before install'

    Assert-Test ((Invoke-Setup $CleanSetup 'default' '') -eq 0) 'Clean install with the default folder'
    $defaultPending = Join-Path $userState 'Recordings\Pending'
    $currentSeed = (Get-ItemProperty -LiteralPath $profileKey).TemporaryRecordingsFolder
    Assert-Test ($currentSeed -eq (Join-Path $userState 'Recordings') -and
        (Test-Path -LiteralPath $defaultPending -PathType Container)) 'Default storage is written and probed'
    Invoke-Uninstall
    Remove-ItemProperty -LiteralPath $profileKey -Name TemporaryRecordingsFolder

    Assert-Test ((Invoke-Setup $CleanSetup 'custom' $recordings) -eq 0) 'Clean install with a custom Unicode path'
    $currentSeed = (Get-ItemProperty -LiteralPath $profileKey).TemporaryRecordingsFolder
    $pending = Join-Path $recordings 'Simple Video Snipper Pending'
    Assert-Test ($currentSeed -eq $recordings -and (Test-Path -LiteralPath $pending -PathType Container) -and
        @(Get-ChildItem -LiteralPath $pending -Force).Count -eq 0) 'Custom storage is written and the probe is removed'
    Test-InstalledApp

    $fixture = Join-Path $pending 'Area test 11111111111111111111111111111111.mp4'
    [IO.File]::WriteAllBytes($fixture, [byte[]]@(17, 23, 41))
    $fixtureHash = (Get-FileHash -LiteralPath $fixture).Hash
    $settings = Join-Path $userState 'settings.json'
    $changed = Join-Path $root 'Different storage'
    @{
        Mode = 'QuickSave'
        Format = 'WebM'
        Folder = Join-Path $root 'Exports'
        AutoCopy = $true
        TemporaryFolder = $changed
        PreviousTemporaryFolders = @($recordings)
    } | ConvertTo-Json | Set-Content -LiteralPath $settings -Encoding UTF8
    $settingsHash = (Get-FileHash -LiteralPath $settings).Hash

    Assert-Test ((Invoke-Setup $UpgradeSetup 'upgrade' (Join-Path $root 'ignored-on-upgrade')) -eq 0) 'In-place upgrade'
    $displayVersion = (Get-ItemProperty -LiteralPath $uninstallKey).DisplayVersion
    Assert-Test ([version]$displayVersion -eq $UpgradeVersion) 'Installed version advances'
    Assert-Test ((Get-ItemProperty -LiteralPath $profileKey).TemporaryRecordingsFolder -eq $recordings -and
        (Get-FileHash -LiteralPath $settings).Hash -eq $settingsHash -and
        (Get-FileHash -LiteralPath $fixture).Hash -eq $fixtureHash) 'Upgrade preserves storage, settings, and pending data'
    Test-InstalledApp
    $encoder = Join-Path $appDir 'ffmpeg\ffmpeg.exe'
    $encoders = & $encoder -v error -encoders | Out-String
    Assert-Test ($LASTEXITCODE -eq 0 -and $encoders -match 'h264_mf' -and $encoders -match 'libvpx-vp9' -and
        $encoders -match 'libopus') 'Bundled FFmpeg starts with the expected encoders'

    $mp4 = Join-Path $root 'export.mp4'
    $webm = Join-Path $root 'export.webm'
    & $encoder -hide_banner -v error -f lavfi -i 'color=s=320x240:r=30' -f lavfi -i 'anullsrc=r=48000:cl=stereo' `
        -t 1 -c:v h264_mf -pix_fmt nv12 -rate_control quality -quality 80 -c:a aac $mp4
    Assert-Test ($LASTEXITCODE -eq 0) 'Bundled FFmpeg creates MP4 with H.264 and AAC'
    & $encoder -hide_banner -v error -i $mp4 -c:v libvpx-vp9 -c:a libopus $webm
    Assert-Test ($LASTEXITCODE -eq 0) 'Bundled FFmpeg creates WebM with VP9 and Opus'
    foreach ($clip in $mp4, $webm) {
        & $encoder -hide_banner -v error -xerror -i $clip -map 0:v:0 -map 0:a:0 -f null -
        Assert-Test ($LASTEXITCODE -eq 0) "Video and audio decode from $([IO.Path]::GetFileName($clip))"
    }

    Invoke-Uninstall
    Assert-Test ((Test-Path -LiteralPath $fixture) -and (Test-Path -LiteralPath $settings) -and
        -not (Test-Path -LiteralPath (Join-Path $appDir 'SimpleVideoSnipper.exe'))) 'Uninstall keeps settings and recordings'
}
finally {
    Invoke-Uninstall
    if (Test-Path -LiteralPath $profileKey) {
        Remove-ItemProperty -LiteralPath $profileKey -Name TemporaryRecordingsFolder -ErrorAction SilentlyContinue
    }
}
Write-Host "Installer test logs are in $root"
