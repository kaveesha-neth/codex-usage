using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using CodexUsage.Core;
using Forms = System.Windows.Forms;

namespace CodexUsage.Widget;

// A transparent overlay at the taskbar coordinates. Windows does not provide a supported
// extension slot here, so the offset is user-adjustable and overlap is possible.
internal sealed class TaskbarWindow : Window
{
    private readonly Preferences prefs;
    private readonly TextBlock percentage = new() { Foreground = Brushes.White, FontSize = 15, VerticalAlignment = VerticalAlignment.Center };
    private readonly Path arc = new() { Stroke = new SolidColorBrush(Color.FromRgb(79, 159, 133)), StrokeThickness = 4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Window detail;
    private readonly StackPanel entries = new();
    private MonitorView view = new(null, "Connecting", false, "", null);
    private DateTimeOffset lastPaint = DateTimeOffset.MinValue;
    private bool hovering;
    private System.Windows.Rect taskbarRect;
    private readonly System.Windows.Threading.DispatcherTimer hideTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    public TaskbarWindow(Preferences prefs, Action openSettings)
    {
        this.prefs = prefs;
        Width = 100; Height = 36; WindowStyle = WindowStyle.None; AllowsTransparency = true;
        Background = Brushes.Transparent; ShowInTaskbar = false; ShowActivated = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        var ring = new Grid { Width = 30, Height = 30, Margin = new Thickness(0, 0, 8, 0) };
        ring.Children.Add(new Ellipse { Stroke = new SolidColorBrush(Color.FromRgb(53, 59, 61)), StrokeThickness = 4 });
        ring.Children.Add(arc);
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        row.Children.Add(ring); row.Children.Add(percentage);
        // Fully transparent pixels in a layered window pass pointer input to Explorer.
        // A nearly transparent surface gives the whole overlay a dependable hover target.
        var hitSurface = new Grid { Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) };
        hitSurface.Children.Add(row); Content = hitSurface;
        detail = new Window { Width = 312, SizeToContent = SizeToContent.Height, MaxHeight = 460, WindowStyle = WindowStyle.None,
            AllowsTransparency = true, Background = Brushes.Transparent, ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Content = new Border { Background = new SolidColorBrush(Color.FromRgb(8, 10, 12)), BorderBrush = new SolidColorBrush(Color.FromRgb(36, 42, 43)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10), Padding = new Thickness(16), Child = new ScrollViewer { Content = entries, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 } } };
        hideTimer.Tick += (_, _) => { hideTimer.Stop(); HideIfAway(); };
        hitSurface.MouseEnter += (_, _) => { hideTimer.Stop(); hovering = true; ShowDetail(); };
        hitSurface.MouseLeave += (_, _) => { hovering = false; hideTimer.Start(); };
        detail.MouseEnter += (_, _) => { hideTimer.Stop(); hovering = true; };
        detail.MouseLeave += (_, _) => { hovering = false; hideTimer.Start(); };
        MouseRightButtonUp += (_, _) => openSettings();
        detail.MouseRightButtonUp += (_, _) => openSettings();
        Loaded += (_, _) => Place();
        ApplyZoom();
    }
    private void HideIfAway()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            var point = Forms.Control.MousePosition;
            bool over(Window w)
            {
                var handle = new WindowInteropHelper(w).Handle;
                return w.IsVisible && handle != IntPtr.Zero && GetWindowRect(handle, out var bounds)
                    && point.X >= bounds.Left && point.X < bounds.Right && point.Y >= bounds.Top && point.Y < bounds.Bottom;
            }
            if (!hovering && !over(this) && !over(detail)) detail.Hide();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    public void Place()
    {
        if (!IsLoaded) return;
        var scale = GetScale();
        var taskbar = FindWindow("Shell_TrayWnd", null);
        // Explorer can temporarily remove its taskbar while rebuilding it. Keep the
        // last valid position until the next timer tick instead of vanishing.
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var bar)) return;
        var notify = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var slotRight = bar.Right - 80;
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var tray)) slotRight = tray.Left;
        // Horizontal bars are supported; for vertical or auto-hidden taskbars the control is hidden.
        var barWidth = bar.Right - bar.Left;
        var barHeight = bar.Bottom - bar.Top;
        if (barWidth < barHeight * 3 || barHeight < 20) { Hide(); detail.Hide(); return; }
        taskbarRect = new System.Windows.Rect(bar.Left, bar.Top, barWidth, barHeight);
        var x = Math.Clamp(slotRight - prefs.TaskbarOffsetPixels - Width * scale, bar.Left + 4.0, bar.Right - Width * scale - 4.0);
        Left = x / scale; Top = (bar.Top + (bar.Bottom - bar.Top - Height * scale) / 2) / scale;
        if (!IsVisible) Show();
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0013); // TOPMOST, NOACTIVATE, NOMOVE, NOSIZE
        if (detail.IsVisible) PositionDetail();
    }
    private void PositionDetail()
    {
        var scale = GetScale();
        var work = Forms.Screen.FromPoint(new System.Drawing.Point((int)(Left * scale), (int)(Top * scale))).WorkingArea;
        detail.Left = Math.Clamp(Left + (Width - detail.Width) / 2, work.Left / scale + 4, work.Right / scale - detail.Width - 4);
        var height = detail.ActualHeight > 0 ? detail.ActualHeight : 460;
        // Use the taskbar edge, not the overlay's centre, to choose a side.
        var bottomTaskbar = taskbarRect.Top >= work.Bottom - 2;
        detail.Top = bottomTaskbar ? taskbarRect.Top / scale - height - 9 : taskbarRect.Bottom / scale + 9;
    }
    private double GetScale() => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
    private void ShowDetail() { DrawDetails(); detail.Show(); detail.UpdateLayout(); PositionDetail(); }
    public void ApplyZoom()
    {
        var zoom = prefs.TaskbarZoom;
        Width = 100 * zoom; Height = 36 * zoom;
        if (Content is Grid surface && surface.Children[0] is StackPanel row)
            row.LayoutTransform = new ScaleTransform(zoom, zoom);
        Place();
    }
    public void Update(MonitorView next) { view = next; DrawRing(); if (detail.IsVisible) DrawDetails(); }
    public void Tick()
    {
        if (detail.IsVisible) DrawDetails();
        if (DateTimeOffset.UtcNow - lastPaint > TimeSpan.FromSeconds(5)) { DrawRing(); Place(); }
    }
    private void DrawRing()
    {
        lastPaint = DateTimeOffset.UtcNow;
        var five = FiveHour();
        var stale = view.Stale || view.Snapshot is { } s && DateTimeOffset.UtcNow - s.FetchedAt > TimeSpan.FromMinutes(6);
        var remaining = five?.RemainingPercent;
        percentage.Text = remaining is null ? "—" : QuotaWindow.Percent(remaining);
        percentage.Foreground = stale ? new SolidColorBrush(Color.FromRgb(145, 147, 147)) : Brushes.White;
        arc.Stroke = stale ? new SolidColorBrush(Color.FromRgb(95, 104, 100)) : new SolidColorBrush(Color.FromRgb(79, 159, 133));
        var portion = Math.Clamp(remaining ?? 0, 0, 100);
        if (portion >= 99.999) { arc.Data = new EllipseGeometry(new Point(15, 15), 13, 13); return; }
        if (portion <= 0) { arc.Data = Geometry.Empty; return; }
        double end = -90 + 360 * portion / 100, radians = end * Math.PI / 180;
        var figure = new PathFigure { StartPoint = new Point(15, 2), IsClosed = false };
        figure.Segments.Add(new ArcSegment(new Point(15 + 13 * Math.Cos(radians), 15 + 13 * Math.Sin(radians)), new Size(13, 13), 0, portion > 50, SweepDirection.Clockwise, true));
        arc.Data = new PathGeometry(new[] { figure });
    }
    private QuotaWindow? FiveHour() => view.Snapshot?.Buckets.SelectMany(b => b.Windows).FirstOrDefault(w => w.WindowDurationMins == 300);
    private void DrawDetails()
    {
        entries.Children.Clear();
        entries.Children.Add(Label("CODEX QUOTA", 12, "#C4CECB", new Thickness(0, 0, 0, 9)));
        if (view.Snapshot is not { Buckets.Count: > 0 } s) entries.Children.Add(Label(view.Connection + " · Open Settings from the tray", 11, "#9EA8A5", new Thickness(0, 0, 0, 10)));
        else foreach (var bucket in s.Buckets)
            foreach (var w in bucket.Windows)
            {
                entries.Children.Add(Label(bucket.Name + " · " + w.DurationLabel, 12, "#DAE3DF", new Thickness(0, 8, 0, 4)));
                entries.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(w.RemainingPercent ?? 0, 0, 100), Height = 4, Foreground = new SolidColorBrush(Color.FromRgb(79, 159, 133)), Background = new SolidColorBrush(Color.FromRgb(43, 48, 49)), BorderThickness = new Thickness(0) });
                entries.Children.Add(Label(w.RemainingPercent is null ? "Quota remaining unavailable" : QuotaWindow.Percent(w.RemainingPercent) + " quota remaining", 12, "#97C5AE", new Thickness(0, 5, 0, 3)));
                entries.Children.Add(Label("Resets " + w.ResetLocal(TimeZoneInfo.Local), 10, "#9CA7A3", new Thickness(0)));
                entries.Children.Add(Label(w.Countdown(DateTimeOffset.UtcNow), 11, "#9CA7A3", new Thickness(0, 1, 0, 9)));
            }
        entries.Children.Add(Label((view.Stale ? "Stale · " : "") + view.Connection + " · Updated " + (view.Snapshot?.FetchedAt.ToLocalTime().ToString("h:mm:ss tt") ?? "unavailable"), 10, "#82908A", new Thickness(0, 5, 0, 0)));
    }
    private static TextBlock Label(string value, double size, string color, Thickness margin) => new() { Text = value, FontSize = size, Margin = margin, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)new BrushConverter().ConvertFromString(color)! };
    public void CloseAll() { hideTimer.Stop(); detail.Close(); Close(); }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string cls, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string? title);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
