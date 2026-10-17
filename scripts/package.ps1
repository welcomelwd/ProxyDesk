param([switch]$PreviewWithoutDriver, [string]$MakeNsis)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishDirectory = Join-Path $projectRoot 'artifacts/windows-x64'
foreach ($name in @('ProxyDeskDns32.dll','ProxyDeskDns64.dll','ProxyDesk.Injector32.exe','ProxyDesk.Injector64.exe','ProxyDesk.Launcher.exe')) {
    if (Test-Path -LiteralPath (Join-Path $publishDirectory $name)) { throw 'Publish the network-only build before packaging; legacy DNS components remain.' }
}
if (-not (Test-Path (Join-Path $publishDirectory 'ProxyDesk.Desktop.exe'))) { throw 'Run scripts/build.ps1 -Publish first.' }
if (-not $PreviewWithoutDriver) {
    foreach ($name in @('ProxyDeskCore.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name))) { throw ('The full installer requires the network component: ' + $name) }
    }
    $driverHashes = @{
        'WinDivert.dll' = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
        'WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
    }
    foreach ($name in $driverHashes.Keys) {
        $driverFile = Join-Path $publishDirectory $name
        if (-not (Test-Path -LiteralPath $driverFile)) { throw 'The full installer requires the bundled WinDivert binaries. A driverless preview must be requested explicitly.' }
        if ((Get-FileHash -LiteralPath $driverFile -Algorithm SHA256).Hash -ne $driverHashes[$name]) { throw ('The bundled WinDivert checksum does not match: ' + $name) }
    }
    if ((Get-AuthenticodeSignature (Join-Path $publishDirectory 'WinDivert64.sys')).Status -ne 'Valid') { throw 'The WinDivert driver signature is not valid.' }
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory 'licenses/WinDivert-source-v2.2.2.zip'))) { throw 'The WinDivert source archive must accompany the full installer.' }
}
if (-not $MakeNsis) {
    $localCompiler = Join-Path $projectRoot '.tools/nsis/makensis.exe'
    $MakeNsis = if (Test-Path $localCompiler) { $localCompiler } else { (Get-Command makensis -ErrorAction Stop).Source }
}
$manifest = @()
foreach ($installedFile in (Get-ChildItem -LiteralPath $publishDirectory -File -Recurse)) {
    $relative = [IO.Path]::GetRelativePath($publishDirectory, $installedFile.FullName)
    if ($relative.StartsWith('..')) { throw 'An installer input escaped the publish directory.' }
    $manifest += 'Delete /REBOOTOK "$INSTDIR\' + $relative.Replace('$', '$$') + '"'
}
foreach ($installedDirectory in (Get-ChildItem -LiteralPath $publishDirectory -Directory -Recurse | Sort-Object { $_.FullName.Length } -Descending)) {
    $relative = [IO.Path]::GetRelativePath($publishDirectory, $installedDirectory.FullName)
    $manifest += 'RMDir "$INSTDIR\' + $relative.Replace('$', '$$') + '"'
}
[IO.File]::WriteAllLines((Join-Path $projectRoot 'artifacts/uninstall-files.nsh'), $manifest, [Text.UTF8Encoding]::new($true))
$arguments = @('/INPUTCHARSET','UTF8','/V2',('/DPUBLISH_DIR=' + $publishDirectory),('/DOUTPUT_DIR=' + (Join-Path $projectRoot 'artifacts')))
if ($PreviewWithoutDriver) { $arguments += '/DDRIVERLESS_PREVIEW' }
$arguments += Join-Path $projectRoot 'installer/ProxyDesk.nsi'
& $MakeNsis @arguments
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed.' }
Write-Output 'PACKAGE_PASS'
