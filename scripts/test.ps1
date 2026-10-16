param([switch]$Native)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet).Source }
Push-Location $projectRoot
try {
    $arguments = @('run','--project','tests/ProxyDesk.Tests/ProxyDesk.Tests.csproj','-c','Release')
    if ($Native) { & "$PSScriptRoot/build-native.ps1" -DnsTests; $arguments += @('--','--native') }
    & $dotnetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Core or native DNS tests failed.' }
    & $dotnetExe run --project tests/ProxyDesk.ServiceTests/ProxyDesk.ServiceTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Service tests failed.' }
} finally { Pop-Location }
