$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$vsWhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsRoot) { throw 'Visual Studio C++ build tools are required.' }
$vcVars = Join-Path $vsRoot 'VC/Auxiliary/Build/vcvarsall.bat'
$outputRoot = Join-Path $projectRoot 'artifacts/native'
New-Item -ItemType Directory -Force -Path (Join-Path $outputRoot 'x64') | Out-Null
& (Join-Path $PSScriptRoot 'patch-native.ps1')
function Run-Native([string]$architecture, [string]$workingDirectory, [string[]]$commands) {
    $batchFile = Join-Path $outputRoot ('build-' + $architecture + '.cmd')
    $batch = @('@echo off', ('call "' + $vcVars + '" ' + $architecture + ' >nul'), 'if errorlevel 1 exit /b 1')
    foreach ($command in $commands) { $batch += $command; $batch += 'if errorlevel 1 exit /b 1' }
    [IO.File]::WriteAllLines($batchFile, $batch, [Text.Encoding]::ASCII)
    Push-Location $workingDirectory
    try { & cmd.exe /c $batchFile; if ($LASTEXITCODE -ne 0) { throw "Native $architecture build failed." } }
    finally { Pop-Location }
}
$sourceRoot = Join-Path $projectRoot 'native/vendor/proxybridge/Windows/src'
$driverRoot = Join-Path $projectRoot 'native/vendor/windivert'
$coreSources = Get-ChildItem $sourceRoot -Filter '*.c' | ForEach-Object { '"' + $_.FullName + '"' }
$coreSources += '"' + (Join-Path $projectRoot 'native/bridge/proxy_desk.c') + '"'
$symbols = Get-ChildItem $sourceRoot -Filter '*.c' | Get-Content | Select-String -AllMatches '\bWinDivert[A-Za-z0-9_]+(?=\s*\()' | ForEach-Object { $_.Matches.Value } | Sort-Object -Unique
$importDef = Join-Path $outputRoot 'windivert.def'
[IO.File]::WriteAllLines($importDef, @('LIBRARY WinDivert.dll', 'EXPORTS') + $symbols, [Text.Encoding]::ASCII)
Run-Native 'x64' (Join-Path $outputRoot 'x64') @(
    ('lib /nologo /def:"' + $importDef + '" /machine:x64 /out:"' + (Join-Path $outputRoot 'WinDivert.lib') + '"'),
    ('cl /nologo /O2 /MT /W3 /DPROXYBRIDGE_EXPORTS /D_CRT_SECURE_NO_WARNINGS /I"' + (Join-Path $driverRoot 'include') + '" /I"' + (Join-Path $projectRoot 'native/bridge') + '" /I"' + $sourceRoot + '" /LD ' + ($coreSources -join ' ') + ' /link "' + (Join-Path $outputRoot 'WinDivert.lib') + '" ws2_32.lib iphlpapi.lib advapi32.lib /OUT:"' + (Join-Path $outputRoot 'ProxyDeskCore.dll') + '"')
)
foreach ($name in @('WinDivert.dll','WinDivert64.sys')) {
    $driverFile = Join-Path $driverRoot "x64/$name"
    if (Test-Path -LiteralPath $driverFile) { Copy-Item -LiteralPath $driverFile -Destination $outputRoot -Force }
}
Write-Output 'NATIVE_BUILD_PASS'
