param([switch]$SkipNative, [switch]$Publish, [switch]$CheckLinux)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$dotnetExe = Join-Path $projectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetExe)) { $dotnetExe = (Get-Command dotnet).Source }
function Dotnet([string[]]$arguments) {
    & $dotnetExe @arguments
    if ($LASTEXITCODE -ne 0) { throw ('dotnet failed: ' + ($arguments -join ' ')) }
}
Push-Location $projectRoot
try {
    if (-not $SkipNative) { & "$PSScriptRoot/build-native.ps1" }
    Dotnet @('build','src/ProxyDesk.Desktop/ProxyDesk.Desktop.csproj','-c','Release','-v','minimal')
    Dotnet @('build','src/ProxyDesk.Service/ProxyDesk.Service.csproj','-c','Release','-v','minimal')
    if ($CheckLinux) { Dotnet @('publish','src/ProxyDesk.Desktop/ProxyDesk.Desktop.csproj','-c','Release','-r','linux-x64','--self-contained','true','-o','artifacts/linux-desktop-build','-v','minimal') }
    if ($Publish) {
        $publishDirectory = Join-Path $projectRoot 'artifacts/windows-x64'
        Dotnet @('publish','src/ProxyDesk.Desktop/ProxyDesk.Desktop.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',$publishDirectory,'-v','minimal')
        Dotnet @('publish','src/ProxyDesk.Service/ProxyDesk.Service.csproj','-c','Release','-r','win-x64','--self-contained','true','-o',$publishDirectory,'-v','minimal')
        foreach ($name in @('ProxyDeskCore.dll','WinDivert.dll','WinDivert64.sys')) {
            $nativeFile = Join-Path $projectRoot "artifacts/native/$name"
            if (Test-Path -LiteralPath $nativeFile) { Copy-Item -LiteralPath $nativeFile -Destination $publishDirectory -Force }
        }
        foreach ($name in @('ProxyDeskDns32.dll','ProxyDeskDns64.dll','ProxyDesk.Injector32.exe','ProxyDesk.Injector64.exe','ProxyDesk.Launcher.exe')) {
            $legacyFile = Join-Path $publishDirectory $name
            if (Test-Path -LiteralPath $legacyFile) { Remove-Item -LiteralPath $legacyFile }
        }
        Copy-Item -LiteralPath (Join-Path $projectRoot 'README.md') -Destination $publishDirectory -Force
        Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $publishDirectory -Force
        New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'licenses') | Out-Null
        Copy-Item 'licenses/*' (Join-Path $publishDirectory 'licenses') -Force
        Copy-Item 'native/vendor/proxybridge/LICENSE' (Join-Path $publishDirectory 'licenses/ProxyBridge-MIT.txt') -Force
        $legacyLicense = Join-Path $publishDirectory 'licenses/Detours-MIT.txt'
        if (Test-Path -LiteralPath $legacyLicense) { Remove-Item -LiteralPath $legacyLicense }
        Copy-Item 'native/vendor/windivert/LICENSE' (Join-Path $publishDirectory 'licenses/WinDivert-license.txt') -Force
        Copy-Item 'native/vendor/windivert/source-v2.2.2.zip' (Join-Path $publishDirectory 'licenses/WinDivert-source-v2.2.2.zip') -Force
        Copy-Item 'scripts/run-preview.cmd' $publishDirectory -Force
    }
    Write-Output 'BUILD_PASS'
} finally { Pop-Location }
