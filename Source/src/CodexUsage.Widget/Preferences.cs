using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace CodexUsage.Widget;
internal sealed class Preferences
{
    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexUsageWidget");
    public string? Monitor { get; set; }
    public double X { get; set; } = .98;
    public double Y { get; set; } = .025;
    public bool Locked { get; set; }
    public bool Fallback { get; set; }
    public bool TransparentBackground { get; set; }
    public bool TaskbarEnabled { get; set; }
    public int TaskbarOffsetPixels { get; set; } = 215;
    public double TaskbarZoom { get; set; } = 1;
    public string? CodexPath { get; set; }
    public static Preferences Load()
    {
        try { var p = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(Path.Combine(Folder, "preferences.json"))) ?? new(); if (!double.IsFinite(p.X)) p.X = .98; if (!double.IsFinite(p.Y)) p.Y = .025; p.TaskbarOffsetPixels = Math.Clamp(p.TaskbarOffsetPixels, 0, 800); p.TaskbarZoom = double.IsFinite(p.TaskbarZoom) ? Math.Clamp(p.TaskbarZoom, .5, 2) : 1; return p; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { return new(); }
    }
    public void Save()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(Path.Combine(Folder, "preferences.json.tmp"), JsonSerializer.Serialize(this)); File.Move(Path.Combine(Folder, "preferences.json.tmp"), Path.Combine(Folder, "preferences.json"), true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
    public ProcessStartInfo StartInfo()
    {
        var p = new ProcessStartInfo(FindCodex()); p.ArgumentList.Add("app-server");
        p.WorkingDirectory = Folder; Directory.CreateDirectory(Folder);
        return p;
    }
    public string FindCodex()
    {
        if (!string.IsNullOrWhiteSpace(CodexPath))
        {
            if (File.Exists(CodexPath) && Path.GetExtension(CodexPath).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(CodexPath);
            throw new IOException("Select a native Codex executable");
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var path = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(path)) return path;
        }
        // npm uses codex.cmd: resolve its native package instead of running a shell command.
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "node_modules", "@openai");
        if (Directory.Exists(root))
        {
            var candidates = Directory.EnumerateFiles(root, "codex.exe", SearchOption.AllDirectories).ToArray();
            var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "aarch64" : "x86_64";
            var match = candidates.FirstOrDefault(p => p.Contains(arch, StringComparison.OrdinalIgnoreCase)) ?? candidates.FirstOrDefault();
            if (match is not null) return match;
        }
        throw new IOException("Codex executable not found; select it in Settings");
    }
    public static bool StartsWithWindows
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("CodexUsageWidget") is not null; }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (value) key.SetValue("CodexUsageWidget", "\"" + Environment.ProcessPath + "\""); else key.DeleteValue("CodexUsageWidget", false);
        }
    }
}
