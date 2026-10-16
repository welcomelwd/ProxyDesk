using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ProxyDesk.Core;

namespace ProxyDesk.Platform.Windows;

[SupportedOSPlatform("windows")]
public static class WindowsRuleTargets
{
    public static readonly string[] ScriptExtensions = [".ps1", ".bat", ".cmd", ".py", ".pyw", ".js", ".mjs", ".cjs", ".vbs", ".wsf", ".sh", ".rb", ".pl", ".php", ".lua"];
    public sealed record Target(string Name, string Path, SelectorKind Kind);

    public static Target Resolve(string path)
        => Resolve(path, new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static Target Resolve(string path, HashSet<string> visited)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!visited.Add(path)) throw new InvalidDataException("快捷方式存在循环引用。");
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(path);
                try
                {
                    string target = Environment.ExpandEnvironmentVariables((string)shortcut.TargetPath);
                    if (target.Length == 0) throw new InvalidDataException("此快捷方式没有可用于规则的程序路径。");
                    string arguments = shortcut.Arguments; string directory = shortcut.WorkingDirectory;
                    var script = FindProcessScript(target, "\"" + target + "\" " + arguments, directory);
                    if (script is not null) target = script;
                    var resolved = Resolve(target, visited);
                    return resolved with { Name = name };
                }
                finally { Marshal.FinalReleaseComObject(shortcut); }
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        if (Directory.Exists(path)) return new(name, path, SelectorKind.DirectoryGlob);
        if (!File.Exists(path)) throw new FileNotFoundException("找不到选择的程序或脚本。", path);
        var extension = System.IO.Path.GetExtension(path);
        if (ScriptExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) return new(name, path, SelectorKind.ScriptPath);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || extension.Equals(".com", StringComparison.OrdinalIgnoreCase)) return new(name, path, SelectorKind.FullPath);
        throw new InvalidDataException("请选择程序、脚本、文件夹或 Windows 快捷方式（.lnk）。");
    }

    public static string? FindProcessScript(string executable, string command, string? directory = null)
    {
        if (System.IO.Path.GetFileName(executable).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            var invocation = System.Text.RegularExpressions.Regex.Match(command, @"(?:^|\s)/[ck]\s+", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (invocation.Success)
            {
                var tail = command[(invocation.Index + invocation.Length)..].TrimStart();
                if (tail.StartsWith("\"\"", StringComparison.Ordinal)) tail = tail[1..];
                return FindScript(executable, SplitArguments(tail), directory);
            }
        }
        return FindScript(executable, SplitArguments(command).Skip(1).ToArray(), directory);
    }

    public static string? FindScript(string executable, IReadOnlyList<string> arguments, string? directory = null)
    {
        var host = System.IO.Path.GetFileNameWithoutExtension(executable).ToLowerInvariant();
        string[] extensions = host switch {
            "powershell" or "pwsh" => [".ps1"], "cmd" => [".cmd", ".bat"],
            "python" or "python3" or "pythonw" or "py" => [".py", ".pyw"],
            "node" or "nodejs" => [".js", ".mjs", ".cjs"], "wscript" or "cscript" => [".vbs", ".js", ".wsf"],
            "bash" or "sh" => [".sh"], "ruby" => [".rb"], "perl" => [".pl"], "php" => [".php"], "lua" => [".lua"], _ => [] };
        if (extensions.Length == 0) return null;
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index].Trim('"');
            if (host is "python" or "python3" or "pythonw" or "py" && argument is "-c" or "-m") return null;
            if (host is "powershell" or "pwsh" && new[] { "-command", "-c", "-encodedcommand", "-enc" }.Contains(argument, StringComparer.OrdinalIgnoreCase)) return null;
            if (host is "node" or "nodejs" && argument is "-e" or "--eval" or "-p" or "--print") return null;
            if (host is "node" or "nodejs" && argument is "-r" or "--require" or "--loader" or "--import") { index++; continue; }
            if (!extensions.Contains(System.IO.Path.GetExtension(argument), StringComparer.OrdinalIgnoreCase)) continue;
            if (System.IO.Path.IsPathFullyQualified(argument)) return System.IO.Path.GetFullPath(argument);
            return string.IsNullOrWhiteSpace(directory) ? null : System.IO.Path.GetFullPath(argument, directory);
        }
        return null;
    }

    public static string[] SplitArguments(string command)
    {
        if (string.IsNullOrWhiteSpace(command)) return [];
        var buffer = CommandLineToArgv("placeholder.exe " + command, out var count);
        if (buffer == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try { return Enumerable.Range(1, count - 1).Select(index => Marshal.PtrToStringUni(Marshal.ReadIntPtr(buffer, index * IntPtr.Size))!).ToArray(); }
        finally { LocalFree(buffer); }
    }

    [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint CommandLineToArgv(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint buffer);
}
