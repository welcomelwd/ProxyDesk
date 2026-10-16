using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WinDivertPackage
{
    public const string ArchiveSha256 = "63CB41763BB4B20F600B6DE04E991A9C2BE73279E317D4D82F237B150C5F3F15";
    public static readonly string[] RequiredFiles = ["WinDivert.dll", "WinDivert64.sys"];

    public static Dictionary<string, byte[]> ReadVerifiedArchive(string archivePath, string validationDirectory)
    {
        var fileInfo = new FileInfo(archivePath);
        if (fileInfo.Length > 8 * 1024 * 1024) throw new InvalidDataException("The driver package exceeds the expected size limit.");
        var archiveBytes = File.ReadAllBytes(archivePath);
        if (Convert.ToHexString(SHA256.HashData(archiveBytes)) != ArchiveSha256)
            throw new InvalidDataException("The WinDivert 2.2.2-A package SHA-256 does not match the pinned release. No files were installed.");
        using var archive = new ZipArchive(new MemoryStream(archiveBytes), ZipArchiveMode.Read);
        var files = new Dictionary<string, byte[]>();
        foreach (var name in RequiredFiles)
        {
            var entry = archive.Entries.SingleOrDefault(e => e.FullName == "WinDivert-2.2.2-A/x64/" + name)
                ?? throw new InvalidDataException("The official x64 package is missing " + name);
            if (entry.Length is < 64 or > 4 * 1024 * 1024) throw new InvalidDataException("Invalid driver binary size.");
            using var input = entry.Open(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
            var binary = bytes.ToArray(); ValidateX64Image(binary); files.Add(name, binary);
        }
        Directory.CreateDirectory(validationDirectory);
        var signaturePath = Path.Combine(validationDirectory, "WinDivert64.sys");
        File.WriteAllBytes(signaturePath, files["WinDivert64.sys"]);
        VerifySignature(signaturePath);
        return files;
    }

    public static void ValidateX64Image(byte[] binary)
    {
        if (binary.Length < 64 || binary[0] != 'M' || binary[1] != 'Z') throw new InvalidDataException("The driver package contains an invalid PE image.");
        var header = BitConverter.ToInt32(binary, 60);
        if (header < 64 || header > binary.Length - 6 || BitConverter.ToUInt32(binary, header) != 0x4550 || BitConverter.ToUInt16(binary, header + 4) != 0x8664)
            throw new InvalidDataException("The driver package must contain Windows x64 binaries.");
    }

    public static void VerifySignature(string path)
    {
        var file = new TrustFile { Size = (uint)Marshal.SizeOf<TrustFile>(), Path = Path.GetFullPath(path) };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        Marshal.StructureToPtr(file, pointer, false);
        var data = new TrustData { Size = (uint)Marshal.SizeOf<TrustData>(), UiChoice = 2, UnionChoice = 1, File = pointer, StateAction = 1 };
        var policy = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        try
        {
            var status = WinVerifyTrust(new nint(-1), ref policy, ref data);
            if (status != 0) throw new InvalidDataException($"Windows did not trust the driver signature (0x{status:X8}). No driver was installed.");
        }
        finally
        {
            data.StateAction = 2; WinVerifyTrust(new nint(-1), ref policy, ref data);
            Marshal.DestroyStructure<TrustFile>(pointer); Marshal.FreeHGlobal(pointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TrustFile
    {
        public uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] public string Path;
        public nint FileHandle;
        public nint KnownSubject;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public nint PolicyCallback;
        public nint SipClient;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
    }
    [DllImport("wintrust.dll", ExactSpelling = true)] private static extern int WinVerifyTrust(nint window, ref Guid policy, ref TrustData data);
}
