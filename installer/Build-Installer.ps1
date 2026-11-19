[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '0.2.3',
    [ValidatePattern('^SimpleVideoSnipper(?:\.SetupCheck[A-Za-z0-9._-]*)?$')]
    [string] $Profile = 'SimpleVideoSnipper',
    [string] $CompilerPath,
    [string] $DotnetPath,
    [string] $FFmpegArchive
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$toolRoot = Join-Path $repo '.tools\installer'
$output = Join-Path $toolRoot 'dist'
$work = Join-Path $toolRoot ('build-' + [guid]::NewGuid().ToString('N'))
$publish = Join-Path $work 'app'
$media = Join-Path $work 'media'
New-Item -ItemType Directory -Force -Path $output, $publish, $media | Out-Null

if (-not $CompilerPath) {
    $CompilerPath = @(
        (Join-Path $toolRoot 'Inno Setup 6\ISCC.exe')
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
        (Join-Path $env:ProgramFiles 'Inno Setup 7\ISCC.exe')
    ) | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if (-not $CompilerPath) { throw 'Install Inno Setup 6.7 or newer or pass -CompilerPath to ISCC.exe.' }
if (-not $DotnetPath) {
    $workspaceSdk = Join-Path $repo '.tools\dotnet\dotnet.exe'
    $DotnetPath = if (Test-Path -LiteralPath $workspaceSdk) { $workspaceSdk } else { 'dotnet' }
}

$asset = 'ffmpeg-n9.0.2-39-g7adfbe40dc-win64-lgpl-shared-9.0.zip'
$release = 'https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/tag/autobuild-2026-10-31-12-59'
$download = "https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/autobuild-2026-10-31-12-59/$asset"
$expectedHash = 'E907B01FA4783913151237E37DF2A10FA97D99A1598A021BF59D4AE62FFED4C0'
if (-not $FFmpegArchive) {
    $FFmpegArchive = Join-Path $toolRoot $asset
    if (-not (Test-Path -LiteralPath $FFmpegArchive)) {
        $partial = $FFmpegArchive + '.' + [guid]::NewGuid().ToString('N') + '.download'
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest -UseBasicParsing -Uri $download -OutFile $partial
        if ((Get-FileHash -LiteralPath $partial -Algorithm SHA256).Hash -ne $expectedHash) {
            throw 'FFmpeg download did not match the expected SHA-256.'
        }
        Move-Item -LiteralPath $partial -Destination $FFmpegArchive
    }
}
if ((Get-FileHash -LiteralPath $FFmpegArchive -Algorithm SHA256).Hash -ne $expectedHash) {
    throw 'The FFmpeg archive must match the pinned Windows x64 asset and SHA-256.'
}
tar.exe -xf $FFmpegArchive -C $work
if ($LASTEXITCODE -ne 0) { throw 'Could not extract the FFmpeg archive.' }
$ffmpeg = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($asset))
Copy-Item -LiteralPath (Join-Path $ffmpeg 'bin\ffmpeg.exe'), (Join-Path $ffmpeg 'bin\ffprobe.exe'),
    (Join-Path $ffmpeg 'LICENSE.txt') -Destination $media
Get-ChildItem -LiteralPath (Join-Path $ffmpeg 'bin') -Filter '*.dll' -File | Copy-Item -Destination $media
@"
This build includes the FFmpeg LGPL shared Windows x64 executables and libraries.
The FFmpeg license is included in LICENSE.txt.

Binary asset: $download
SHA-256: $expectedHash
Build release: $release
FFmpeg source at the build revision: https://github.com/FFmpeg/FFmpeg/tree/7adfbe40dc
Build recipes: https://github.com/FFmpeg-Builds/FFmpeg-Builds
"@ | Set-Content -LiteralPath (Join-Path $media 'BUILD-SOURCE.txt') -Encoding UTF8

$publishArgs = @('publish', (Join-Path $repo 'SimpleVideoSnipper.csproj'), '-c', 'Release',
    '-r', 'win-x64', '--self-contained', 'true', '-o', $publish, "-p:Version=$Version",
    "-p:SnipperProfile=$Profile", '-warnaserror')
& $DotnetPath @publishArgs
if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

$name = if ($Profile -eq 'SimpleVideoSnipper') { 'Simple Video Snipper' } else { "Simple Video Snipper ($Profile)" }
$setupArgs = @('/Qp', "/O$output", "/DAppVersion=$Version", "/DProfile=$Profile", "/DDisplayName=$name",
    "/DPublishDir=$publish", "/DMediaDir=$media", (Join-Path $PSScriptRoot 'SimpleVideoSnipper.iss'))
& $CompilerPath @setupArgs
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
Get-Item -LiteralPath (Join-Path $output "$Profile-$Version-win-x64-setup.exe")
