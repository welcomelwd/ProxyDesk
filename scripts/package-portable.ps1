$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$publishDirectory = Join-Path $projectRoot 'artifacts/windows-x64'
$packageName = 'ProxyDesk-Portable-0.1.2-x64'
$stagingDirectory = Join-Path $projectRoot ('artifacts/portable-package/' + [Guid]::NewGuid().ToString('N'))
$packageDirectory = Join-Path $stagingDirectory $packageName
foreach ($name in @('ProxyDesk.Desktop.exe','ProxyDesk.Service.exe','ProxyDeskCore.dll','ProxyDeskDns32.dll','ProxyDeskDns64.dll','ProxyDesk.Injector32.exe','ProxyDesk.Injector64.exe','licenses/WinDivert-source-v2.2.2.zip')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishDirectory $name))) { throw ('Missing portable input: ' + $name) }
}
$driverHashes = @{
    'WinDivert.dll' = 'C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2'
    'WinDivert64.sys' = '8DA085332782708D8767BCACE5327A6EC7283C17CFB85E40B03CD2323A90DDC2'
}
foreach ($name in $driverHashes.Keys) {
    if ((Get-FileHash -LiteralPath (Join-Path $publishDirectory $name)).Hash -ne $driverHashes[$name]) { throw ('Unverified bundled driver: ' + $name) }
}
if ((Get-AuthenticodeSignature (Join-Path $publishDirectory 'WinDivert64.sys')).Status -ne 'Valid') { throw 'The bundled driver signature is not valid.' }
New-Item -ItemType Directory -Path $packageDirectory -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -File -Recurse) {
    if ($file.Extension -eq '.pdb' -or $file.Name -in @('run-preview.cmd','ProxyDesk.Launcher.exe','ProxyDesk.Desktop.exe','README.md')) { continue }
    $relative = [IO.Path]::GetRelativePath($publishDirectory, $file.FullName)
    $destination = [IO.Path]::GetFullPath((Join-Path $packageDirectory $relative))
    if (-not $destination.StartsWith($packageDirectory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'A package input escaped the staging directory.' }
    New-Item -ItemType Directory -Path (Split-Path $destination -Parent) -Force | Out-Null
    Copy-Item -LiteralPath $file.FullName -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $publishDirectory 'ProxyDesk.Desktop.exe') -Destination (Join-Path $packageDirectory 'ProxyDesk.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/portable.md') -Destination (Join-Path $packageDirectory '使用说明.txt')
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/ProxyDesk.Desktop/Assets/ProxyDesk.ico') -Destination $packageDirectory
[IO.File]::WriteAllText((Join-Path $packageDirectory 'portable.mode'), 'ProxyDesk portable mode')
New-Item -ItemType Directory -Path (Join-Path $packageDirectory 'Data') | Out-Null
$archivePath = Join-Path $projectRoot ('artifacts/' + $packageName + '.zip')
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $packageDirectory -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($stagingDirectory, $file.FullName).Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
    $null = $archive.CreateEntry($packageName + '/Data/')
} finally { $archive.Dispose() }
[ordered]@{ archive=$archivePath; directory=$packageDirectory } | ConvertTo-Json |
    Set-Content -LiteralPath (Join-Path $projectRoot 'artifacts/portable-package-result.json') -Encoding utf8
Write-Output ('PORTABLE_PACKAGE_PASS: ' + $archivePath)
