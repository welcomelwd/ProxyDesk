using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class InstalledWinDivert
{
    public const string CompatibleLibrarySha256 = "C1E060EE19444A259B2162F8AF0F3FE8C4428A1C6F694DCE20DE194AC8D7D9A2";

    public static string? FindDirectory()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\WinDivert");
        if (key?.GetValue("ImagePath") is not string configuredPath) return null;
        var driver = Environment.ExpandEnvironmentVariables(configuredPath).Trim('"');
        if (driver.StartsWith(@"\??\", StringComparison.Ordinal)) driver = driver[4..];
        if (driver.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
            driver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), driver[12..]);
        driver = Path.GetFullPath(driver);
        if (!Path.GetFileName(driver).Equals("WinDivert64.sys", StringComparison.OrdinalIgnoreCase) || !File.Exists(driver)) return null;
        var directory = Path.GetDirectoryName(driver)!;
        var library = Path.Combine(directory, "WinDivert.dll");
        if (!File.Exists(library)) return null;
        VerifyLibrary(File.ReadAllBytes(library));
        if (FileVersionInfo.GetVersionInfo(driver).FileVersion?.StartsWith("2.2", StringComparison.Ordinal) != true)
            throw new InvalidDataException("The installed WinDivert driver is not the supported 2.2 version.");
        WinDivertPackage.VerifySignature(driver);
        return directory;
    }

    public static void VerifyLibrary(byte[] bytes)
    {
        if (Convert.ToHexString(SHA256.HashData(bytes)) != CompatibleLibrarySha256)
            throw new InvalidDataException("The installed WinDivert.dll does not match the verified compatible x64 library.");
        WinDivertPackage.ValidateX64Image(bytes);
    }
}
