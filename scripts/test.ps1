param([switch]$Native)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet).Source }
Push-Location $projectRoot
try {
    $arguments = @('run','--project','tests/ProxyDesk.Tests/ProxyDesk.Tests.csproj','-c','Release')
    & $dotnetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Core tests failed.' }
    $serviceArguments = @('run','--project','tests/ProxyDesk.ServiceTests/ProxyDesk.ServiceTests.csproj','-c','Release')
    & $dotnetExe @serviceArguments
    if ($LASTEXITCODE -ne 0) { throw 'Service tests failed.' }
    if ($Native) { & "$PSScriptRoot/test-network-dns.ps1" }
} finally { Pop-Location }
