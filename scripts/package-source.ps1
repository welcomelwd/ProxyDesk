$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$archivePath = Join-Path $projectRoot 'artifacts/ProxyDesk-0.1.1-source.zip'
New-Item -ItemType Directory -Force -Path (Split-Path $archivePath -Parent) | Out-Null
$stream = [IO.File]::Open($archivePath, [IO.FileMode]::Create)
$archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in (Get-ChildItem -LiteralPath $projectRoot -File -Recurse -Force)) {
        $relative = [IO.Path]::GetRelativePath($projectRoot, $file.FullName).Replace('\', '/')
        if ($relative -match '^(\.tools|artifacts|\.git|\.vs)/|(^|/)(bin|obj)/|^native/vendor/detours/(lib\.[^/]+|bin\.[^/]+|include|src/obj\.[^/]+)/|^docs/verification\.md$|\.(user|suo|log|dll|exe|sys|pdb|zip)$') { continue }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, 'ProxyDesk/' + $relative, [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally { $archive.Dispose() }
Write-Output ('SOURCE_PACKAGE_PASS: ' + $archivePath)
