$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet).Source }
& $dotnetExe run --project (Join-Path $projectRoot 'tools/ProxyDesk.IconBuilder/ProxyDesk.IconBuilder.csproj') -c Release -- (Join-Path $projectRoot 'src/ProxyDesk.Desktop/Assets')
if ($LASTEXITCODE -ne 0) { throw 'Application icon generation failed.' }
