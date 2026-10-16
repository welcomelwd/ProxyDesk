param([switch]$SkipSdk, [switch]$SkipDriver)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$toolsRoot = Join-Path $projectRoot '.tools'
New-Item -ItemType Directory -Force -Path $toolsRoot | Out-Null
if (-not $SkipSdk -and -not (Test-Path (Join-Path $toolsRoot 'dotnet/dotnet.exe'))) {
    $releases = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $sdk = $releases.releases.sdk | Where-Object version -eq '10.0.402' | Select-Object -First 1
    $archive = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.url.EndsWith('.zip') } | Select-Object -First 1
    if (-not $archive) { throw 'The pinned .NET SDK archive was not found.' }
    $sdkZip = Join-Path $toolsRoot 'dotnet-sdk.zip'
    Invoke-WebRequest $archive.url -OutFile $sdkZip
    if ((Get-FileHash $sdkZip -Algorithm SHA512).Hash -ne $archive.hash) { throw '.NET SDK checksum mismatch.' }
    Expand-Archive $sdkZip -DestinationPath (Join-Path $toolsRoot 'dotnet') -Force
}
$coreRoot = Join-Path $projectRoot 'native/vendor/proxybridge'
if (-not (Test-Path (Join-Path $coreRoot 'Windows/src/ProxyBridge.c'))) {
    $vendorZip = Join-Path $toolsRoot 'proxybridge.zip'
    Invoke-WebRequest 'https://codeload.github.com/InterceptSuite/ProxyBridge/zip/d697164b836f0d3f897128774d3cb72e54770e74' -OutFile $vendorZip
    Expand-Archive $vendorZip -DestinationPath (Join-Path $toolsRoot 'proxybridge-extract') -Force
    $sourceRoot = Join-Path $toolsRoot 'proxybridge-extract/ProxyBridge-d697164b836f0d3f897128774d3cb72e54770e74'
    New-Item -ItemType Directory -Force -Path $coreRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'Windows') -Destination $coreRoot -Recurse
    Copy-Item -LiteralPath (Join-Path $sourceRoot 'LICENSE') -Destination $coreRoot
}
$driverRoot = Join-Path $projectRoot 'native/vendor/windivert'
if (-not $SkipDriver -and -not (Test-Path (Join-Path $driverRoot 'x64/WinDivert64.sys'))) {
    $driverZip = Join-Path $toolsRoot 'windivert.zip'
    Invoke-WebRequest 'https://github.com/basil00/WinDivert/releases/download/v2.2.2/WinDivert-2.2.2-A.zip' -OutFile $driverZip
    Expand-Archive $driverZip -DestinationPath (Join-Path $toolsRoot 'windivert-extract') -Force
    $driverSource = Join-Path $toolsRoot 'windivert-extract/WinDivert-2.2.2-A'
    $signature = Get-AuthenticodeSignature (Join-Path $driverSource 'x64/WinDivert64.sys')
    if ($signature.Status -ne 'Valid') { throw "WinDivert signature validation failed: $($signature.Status)" }
    New-Item -ItemType Directory -Force -Path $driverRoot | Out-Null
    Copy-Item "$driverSource/*" $driverRoot -Recurse -Force
}
if (-not (Test-Path (Join-Path $driverRoot 'include/windivert.h'))) {
    New-Item -ItemType Directory -Force -Path (Join-Path $driverRoot 'include') | Out-Null
    Invoke-WebRequest 'https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/include/windivert.h' -OutFile (Join-Path $driverRoot 'include/windivert.h')
    Invoke-WebRequest 'https://raw.githubusercontent.com/basil00/WinDivert/v2.2.2/LICENSE' -OutFile (Join-Path $driverRoot 'LICENSE')
}
if (-not $SkipDriver -and -not (Test-Path (Join-Path $driverRoot 'source-v2.2.2.zip'))) {
    Invoke-WebRequest 'https://codeload.github.com/basil00/WinDivert/zip/refs/tags/v2.2.2' -OutFile (Join-Path $driverRoot 'source-v2.2.2.zip')
}
$detoursRoot = Join-Path $projectRoot 'native/vendor/detours'
if (-not (Test-Path (Join-Path $detoursRoot 'src/detours.h'))) {
    $detoursZip = Join-Path $toolsRoot 'detours.zip'
    Invoke-WebRequest 'https://codeload.github.com/microsoft/Detours/zip/refs/tags/v4.0.1' -OutFile $detoursZip
    Expand-Archive $detoursZip -DestinationPath (Join-Path $toolsRoot 'detours-extract') -Force
    New-Item -ItemType Directory -Force -Path $detoursRoot | Out-Null
    Copy-Item (Join-Path $toolsRoot 'detours-extract/Detours-4.0.1/*') $detoursRoot -Recurse -Force
}
Write-Output 'BOOTSTRAP_PASS'
