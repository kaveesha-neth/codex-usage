using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace CodexUsage.Widget;

/// <summary>All undocumented Explorer behavior is isolated here; never sets HWND_TOPMOST.</summary>
internal sealed class DesktopHost : IDisposable
{
    private readonly Window window;
    private readonly Preferences prefs;
    private IntPtr hwnd, parent;
    private bool attached;
    private HwndSource? shellWatcher;
    private int hostChecks;
    public bool Dragging { get; set; }
    public string Mode { get; private set; } = "Desktop attachment pending";
    public DesktopHost(Window window, Preferences prefs) { this.window = window; this.prefs = prefs; }
    public void Initialize()
    {
        hwnd = new WindowInteropHelper(window).Handle;
        var source = HwndSource.FromHwnd(hwnd);
        source?.AddHook(Hook);
        // Broadcast shell messages target top-level windows, not our reparented child.
        shellWatcher = new HwndSource(new HwndSourceParameters("CodexUsageShellWatcher") { Width = 0, Height = 0, WindowStyle = unchecked((int)0x80000000) });
        shellWatcher.AddHook(Hook);
        Attach();
        ApplyClickThrough(false); // Always recover input on launch; click-through is session only.
    }
    public void Attach()
    {
        var oldRect = new RectNative(); GetWindowRect(hwnd, out oldRect);
        if (attached && IsWindow(parent)) SetParent(hwnd, IntPtr.Zero);
        SetWindowLongPtr(hwnd, -16, new IntPtr((GetWindowLongPtr(hwnd, -16).ToInt64() & ~0x40000000L) | unchecked((long)0x80000000)));
        attached = false; parent = IntPtr.Zero;
        if (!prefs.Fallback)
        {
            var progman = FindWindow("Progman", null);
            if (progman != IntPtr.Zero)
            {
                SendMessageTimeout(progman, 0x052C, IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out _);
                EnumWindows((top, _) =>
                {
                    if (FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                        parent = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                    return true;
                }, IntPtr.Zero);
                if (parent != IntPtr.Zero && IsWindowVisible(parent))
                {
                    var style = GetWindowLongPtr(hwnd, -16).ToInt64();
                    SetWindowLongPtr(hwnd, -16, new IntPtr((style & ~0x80000000L) | 0x40000000L));
                    SetLastError(0); SetParent(hwnd, parent);
                    attached = GetParent(hwnd) == parent;
                }
            }
        }
        if (!attached)
        {
            parent = IntPtr.Zero; SetParent(hwnd, IntPtr.Zero);
            var style = GetWindowLongPtr(hwnd, -16).ToInt64();
            SetWindowLongPtr(hwnd, -16, new IntPtr((style & ~0x40000000L) | 0x80000000L));
            Mode = "Fallback · non-topmost bottom window";
        }
        else Mode = "Explorer desktop layer (experimental)";
        SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0013 | 0x0020); // HWND_BOTTOM, NOACTIVATE, NOSIZE, NOMOVE, FRAMECHANGED
        Place();
    }
    public void CheckHost()
    {
        if (attached && (!IsWindow(parent) || !IsWindowVisible(parent) || GetParent(hwnd) != parent)) Attach();
        else if (!attached && !prefs.Fallback && ++hostChecks % 6 == 0) Attach();
        else if (!attached) SetWindowPos(hwnd, new IntPtr(1), 0, 0, 0, 0, 0x0013);
    }
    public void Place()
    {
        if (hwnd == IntPtr.Zero || Dragging) return;
        var screens = Forms.Screen.AllScreens;
        var screen = Array.Find(screens, s => s.DeviceName == prefs.Monitor) ?? Forms.Screen.PrimaryScreen ?? screens[0];
        var area = screen.WorkingArea;
        var dpi = GetDpiForWindow(hwnd); var scale = dpi == 0 ? 1 : dpi / 96.0;
        var width = (int)Math.Ceiling(window.ActualWidth * scale);
        var height = (int)Math.Ceiling(window.ActualHeight * scale);
        var x = area.Left + (int)(Math.Max(0, area.Width - width) * Math.Clamp(prefs.X, 0, 1));
        var y = area.Top + (int)(Math.Max(0, area.Height - height) * Math.Clamp(prefs.Y, 0, 1));
        MoveScreen(x, y, width, height);
    }
    public void MoveScreen(int x, int y, int width = 0, int height = 0)
    {
        var p = new PointNative { X = x, Y = y };
        if (attached) ScreenToClient(parent, ref p);
        SetWindowPos(hwnd, new IntPtr(1), p.X, p.Y, width, height, 0x0010 | (width == 0 ? 0x0001u : 0));
    }
    public (int X, int Y) Position { get { GetWindowRect(hwnd, out var r); return (r.Left, r.Top); } }
    public static (int X, int Y) Cursor { get { GetCursorPos(out var p); return (p.X, p.Y); } }
    public void SavePosition()
    {
        GetWindowRect(hwnd, out var r);
        var screen = Forms.Screen.FromRectangle(new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        var area = screen.WorkingArea; prefs.Monitor = screen.DeviceName;
        prefs.X = Math.Clamp((double)(r.Left - area.Left) / Math.Max(1, area.Width - r.Right + r.Left), 0, 1);
        prefs.Y = Math.Clamp((double)(r.Top - area.Top) / Math.Max(1, area.Height - r.Bottom + r.Top), 0, 1);
        prefs.Save(); Place();
    }
    public void ApplyClickThrough(bool enabled)
    {
        var style = GetWindowLongPtr(hwnd, -20).ToInt64();
        SetWindowLongPtr(hwnd, -20, new IntPtr(enabled ? style | 0x20L | 0x08000000L : style & ~(0x20L | 0x08000000L)));
    }
    private IntPtr Hook(IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == 0x007E || msg == 0x02E0 || msg == 0x001A) window.Dispatcher.BeginInvoke(new Action(Place));
        if (msg == (int)RegisterWindowMessage("TaskbarCreated")) window.Dispatcher.BeginInvoke(new Action(Attach));
        return IntPtr.Zero;
    }
    public void Dispose() { shellWatcher?.Dispose(); shellWatcher = null; }
    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lparam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetParent(IntPtr child, IntPtr parent);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out RectNative rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out PointNative point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref PointNative point);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
}
