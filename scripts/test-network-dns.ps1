$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
& "$PSScriptRoot/build-native.ps1"
$vsWhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsRoot = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
$vcVars = Join-Path $vsRoot 'VC/Auxiliary/Build/vcvarsall.bat'
$output = Join-Path $projectRoot 'artifacts/native'
$batch = @('@echo off', ('call "' + $vcVars + '" x64 >nul'), 'if errorlevel 1 exit /b 1')
foreach ($test in @('process_lookup_tests', 'network_dns_tests')) {
    $batch += 'cl /nologo /O2 /MT /W3 /D_CRT_SECURE_NO_WARNINGS /I"' + $projectRoot + '\native\vendor\windivert\include" /I"' + $projectRoot + '\native\vendor\proxybridge\Windows\src" /I"' + $projectRoot + '\native\bridge" "' + $projectRoot + '\tests\native\' + $test + '.c" /Fe:"' + $output + '\' + $test + '.exe" /link "' + $output + '\WinDivert.lib"'
    $batch += 'if errorlevel 1 exit /b 1'
}
[IO.File]::WriteAllLines((Join-Path $output 'test-network-dns.cmd'), $batch, [Text.Encoding]::ASCII)
Push-Location $output
try {
    & cmd.exe /c (Join-Path $output 'test-network-dns.cmd')
    if ($LASTEXITCODE -ne 0) { throw 'Network DNS test compilation failed.' }
    foreach ($test in @('process_lookup_tests', 'network_dns_tests')) {
        & (Join-Path $output ($test + '.exe'))
        if ($LASTEXITCODE -ne 0) { throw "$test failed." }
    }
} finally { Pop-Location }
