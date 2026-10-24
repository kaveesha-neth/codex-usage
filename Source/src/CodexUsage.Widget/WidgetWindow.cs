using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CodexUsage.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CodexUsage.Widget;
internal sealed class WidgetWindow : Window
{
    private readonly Preferences prefs = Preferences.Load();
    private readonly UsageMonitor monitor;
    private readonly DesktopHost host;
    private readonly Forms.NotifyIcon tray;
    private readonly bool mock;
    private readonly DispatcherTimer timer;
    private readonly StackPanel rows = new();
    private readonly Border widgetPanel;
    private readonly TextBlock status = Text("Connecting", 11, "#76C9DC"), footer = Text("No successful refresh", 10, "#93A3B5");
    private readonly List<(TextBlock Label, QuotaWindow Window)> countdowns = new();
    private MonitorView view = new(null, "Connecting", false, "", null);
    private Window? settings;
    private TaskbarWindow? taskbar;
    private TextBlock? diagnostics;
    private string cliVersion = "Not inspected yet";
    private bool through, dragging, exiting;
    private (int X, int Y) dragCursor, dragStart;
    private int ticks;
    public WidgetWindow(bool mock)
    {
        this.mock = mock;
        monitor = new UsageMonitor(prefs.StartInfo, Path.Combine(Preferences.Folder, "usage-cache.json"));
        host = new DesktopHost(this, prefs);
        Title = mock ? "Codex Usage — DEMO" : "Codex Usage";
        Width = 346; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowInTaskbar = false; Topmost = false; FontFamily = new FontFamily("Segoe UI");
        UseLayoutRounding = true; SnapsToDevicePixels = true;
        var body = new StackPanel { Margin = new Thickness(18, 15, 18, 14) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 15) };
        DockPanel.SetDock(status, Dock.Right); header.Children.Add(status);
        header.Children.Add(Text(mock ? "DEMO · SYNTHETIC DATA" : "CODEX USAGE", mock ? 11 : 12, "#DFEAF3", FontWeights.SemiBold));
        body.Children.Add(header);
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = 650 };
        body.Children.Add(scroll); footer.Margin = new Thickness(0, 5, 0, 0); body.Children.Add(footer);
        widgetPanel = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Child = body, Margin = new Thickness(3) };
        Content = widgetPanel;
        ApplyPanelAppearance();
        SizeChanged += (_, _) => { var area = Forms.Screen.FromPoint(new System.Drawing.Point(host.Position.X, host.Position.Y)).WorkingArea; var scale = VisualTreeHelper.GetDpi(this).DpiScaleY; scroll.MaxHeight = Math.Max(120, area.Height / scale - 130); host.Place(); };
        MouseLeftButtonDown += (_, e) => { if (prefs.Locked || through) return; dragging = true; host.Dragging = true; dragCursor = DesktopHost.Cursor; dragStart = host.Position; CaptureMouse(); e.Handled = true; };
        MouseMove += (_, _) => { if (dragging) { var p = DesktopHost.Cursor; host.MoveScreen(dragStart.X + p.X - dragCursor.X, dragStart.Y + p.Y - dragCursor.Y); } };
        MouseLeftButtonUp += (_, _) => { if (!dragging) return; dragging = false; host.Dragging = false; ReleaseMouseCapture(); host.SavePosition(); };
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = mock ? "Codex Usage — DEMO" : "Codex Usage", Visible = true };
        tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(new Action(OpenSettings));
        RebuildMenus();
        monitor.Changed += value => Dispatcher.BeginInvoke(new Action(() => Render(value)));
        timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; timer.Tick += (_, _) => Tick();
        SourceInitialized += (_, _) => host.Initialize();
        Loaded += async (_, _) =>
        {
            host.Place(); timer.Start();
            if (mock) Render(Demo()); else { monitor.Start(); await InspectVersion(); }
            ApplyTaskbarMode();
        };
        SystemEvents.PowerModeChanged += PowerChanged;
        SystemEvents.DisplaySettingsChanged += DisplaysChanged;
        Closing += (_, _) => exiting = true;
        Closed += async (_, _) =>
        {
            timer.Stop(); SystemEvents.PowerModeChanged -= PowerChanged; SystemEvents.DisplaySettingsChanged -= DisplaysChanged;
            tray.Visible = false; tray.Dispose(); settings?.Close(); taskbar?.CloseAll(); host.Dispose();
            await monitor.DisposeAsync();
            if (!exiting && !Program.SessionEnding)
            {
                // Explorer can destroy a cross-process child HWND. Recreate a fresh WPF window.
                var replacement = new WidgetWindow(mock); Application.Current.MainWindow = replacement; replacement.Show();
            }
            else Application.Current.Shutdown();
        };
    }
    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var row in countdowns) { row.Label.Text = row.Window.Countdown(now); row.Label.ToolTip = row.Window.ResetLocal(TimeZoneInfo.Local); }
        if (!mock) monitor.ClockTick(now);
        taskbar?.Tick();
        var stale = view.Snapshot is { } s && (view.Stale || now - s.FetchedAt > TimeSpan.FromMinutes(6));
        var expired = view.Snapshot?.Buckets.SelectMany(b => b.Windows).Any(w => w.ResetUtc is { } r && r <= now) == true;
        status.Text = mock ? "DEMO" : stale ? "Stale · " + view.Connection : expired ? "Refreshing…" : view.Connection;
        footer.Text = (view.Snapshot is { } data ? (mock ? "SYNTHETIC · " : "Updated ") + data.FetchedAt.ToLocalTime().ToString("h:mm:ss tt") : "No successful refresh") + (host.Mode.StartsWith("Fallback", StringComparison.Ordinal) ? " · Fallback mode" : "");
        if (++ticks % 5 == 0) { host.CheckHost(); UpdateDiagnostics(); }
    }
    private void Render(MonitorView value)
    {
        view = value; rows.Children.Clear(); countdowns.Clear();
        if (value.Snapshot is not { Buckets.Count: > 0 } snap)
        {
            var empty = Text(value.Snapshot is null ? value.Connection == "Sign in required" ? "Sign in with ChatGPT from the tray menu." : "Usage unavailable. Open Settings for connection controls." : "No quota buckets returned.", 12, "#AAB9C9");
            empty.TextWrapping = TextWrapping.Wrap; empty.Margin = new Thickness(0, 0, 0, 12); rows.Children.Add(empty);
        }
        else foreach (var bucket in snap.Buckets)
        {
            var heading = Text(bucket.Name, 13, "#DCE8F3", FontWeights.SemiBold); heading.ToolTip = "Limit ID: " + bucket.Id; rows.Children.Add(heading);
            if (bucket.Windows.Count == 0) { var unavailable = Text("Windows unavailable", 11, "#94A8BA"); unavailable.Margin = new Thickness(0, 4, 0, 12); rows.Children.Add(unavailable); }
            foreach (var w in bucket.Windows)
            {
                var group = new StackPanel { Margin = new Thickness(0, 6, 0, 15), ToolTip = "Limit ID: " + bucket.Id + " · " + w.Kind };
                group.Children.Add(Text(w.DurationLabel, 11, "#A4B8C9"));
                if (w.RemainingPercent is { } remaining)
                    group.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = Math.Clamp(remaining, 0, 100), FlowDirection = FlowDirection.LeftToRight, Height = 3, BorderThickness = new Thickness(0), Foreground = ColorBrush("#68B9D0"), Background = ColorBrush("#294052"), Margin = new Thickness(0, 8, 0, 6), ToolTip = $"{QuotaWindow.Percent(remaining)} quota remaining" });
                group.Children.Add(Text(w.UsedPercent.HasValue ? $"{QuotaWindow.Percent(w.UsedPercent)} used · {QuotaWindow.Percent(w.RemainingPercent)} quota remaining" : "Usage unavailable · quota remaining unavailable", 11, "#D1E0EC"));
                var reset = Text("Resets " + w.ResetLocal(TimeZoneInfo.Local), 10, "#94A8BA"); reset.Margin = new Thickness(0, 7, 0, 1); reset.TextWrapping = TextWrapping.Wrap; group.Children.Add(reset);
                var countdown = Text(w.Countdown(DateTimeOffset.UtcNow), 11, "#76C9DC"); countdowns.Add((countdown, w)); group.Children.Add(countdown);
                rows.Children.Add(group);
            }
        }
        footer.Text = value.Snapshot is { } snapshot ? (mock ? "SYNTHETIC · " : "Updated ") + snapshot.FetchedAt.ToLocalTime().ToString("h:mm:ss tt") : "No successful refresh";
        footer.ToolTip = value.Snapshot?.FetchedAt.ToLocalTime().ToString("F zzz");
        taskbar?.Update(value);
        Tick(); UpdateDiagnostics();
    }
    private void RebuildMenus()
    {
        var menu = new ContextMenu(); var trayMenu = new Forms.ContextMenuStrip();
        void Add(string title, Action action, bool enabled = true)
        {
            var item = new MenuItem { Header = title, IsEnabled = enabled }; item.Click += (_, _) => action(); menu.Items.Add(item);
            var trayItem = new Forms.ToolStripMenuItem(title) { Enabled = enabled }; trayItem.Click += (_, _) => Dispatcher.BeginInvoke(action); trayMenu.Items.Add(trayItem);
        }
        Add("Refresh", () => { if (mock) Render(Demo()); else monitor.Refresh(); });
        Add(prefs.Locked ? "Unlock position" : "Lock position", () => { prefs.Locked = !prefs.Locked; prefs.Save(); RebuildMenus(); });
        Add(through ? "Disable click-through (restore selection)" : "Enable click-through", () => { through = !through; host.ApplyClickThrough(through); RebuildMenus(); });
        Add("Settings", OpenSettings);
        Add(prefs.TaskbarEnabled ? "Disable taskbar widget" : "Enable taskbar widget", () => { prefs.TaskbarEnabled = !prefs.TaskbarEnabled; prefs.Save(); ApplyTaskbarMode(); RebuildMenus(); });
        Add("Open official usage dashboard", () => OpenUrl("https://chatgpt.com/codex/settings/usage"));
        Add("Sign in with ChatGPT", () => _ = Guard(SignIn), !mock);
        Add("Sign out (shared Codex sign-in)", () => _ = Guard(monitor.SignOutAsync), !mock);
        Add(Preferences.StartsWithWindows ? "Disable Start with Windows" : "Start with Windows", () => _ = Guard(() => { Preferences.StartsWithWindows = !Preferences.StartsWithWindows; RebuildMenus(); return Task.CompletedTask; }), !mock);
        Add("Quit", () => { if (!exiting) { exiting = true; Close(); } });
        ContextMenu = menu; var old = tray.ContextMenuStrip; tray.ContextMenuStrip = trayMenu; old?.Dispose();
    }
    private void ApplyTaskbarMode()
    {
        if (prefs.TaskbarEnabled)
        {
            taskbar ??= new TaskbarWindow(prefs, OpenSettings);
            taskbar.Show(); taskbar.Update(view); taskbar.Place();
        }
        else
        {
            taskbar?.CloseAll(); taskbar = null;
        }
        host.Place();
    }
    private void ApplyPanelAppearance()
    {
        // A transparent brush keeps the panel draggable without painting a backdrop.
        widgetPanel.Background = prefs.TransparentBackground ? Brushes.Transparent : ColorBrush("#E815202C");
        widgetPanel.BorderBrush = prefs.TransparentBackground ? Brushes.Transparent : ColorBrush("#384E697B");
    }
    private void OpenSettings()
    {
        if (settings is not null) { settings.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(20) };
        var transparent = new CheckBox { Content = "Transparent background (applies immediately)", IsChecked = prefs.TransparentBackground, Margin = new Thickness(0, 0, 0, 12) };
        transparent.Checked += (_, _) => { prefs.TransparentBackground = true; prefs.Save(); ApplyPanelAppearance(); };
        transparent.Unchecked += (_, _) => { prefs.TransparentBackground = false; prefs.Save(); ApplyPanelAppearance(); };
        panel.Children.Add(transparent);
        var taskbarToggle = new CheckBox { Content = "Taskbar widget (ring and 5-hour quota remaining)", IsChecked = prefs.TaskbarEnabled, Margin = new Thickness(0, 0, 0, 8) };
        taskbarToggle.Checked += (_, _) => { prefs.TaskbarEnabled = true; prefs.Save(); ApplyTaskbarMode(); RebuildMenus(); };
        taskbarToggle.Unchecked += (_, _) => { prefs.TaskbarEnabled = false; prefs.Save(); ApplyTaskbarMode(); RebuildMenus(); };
        panel.Children.Add(taskbarToggle);
        panel.Children.Add(new TextBlock { Text = "Taskbar offset left of the notification area (pixels)", Margin = new Thickness(0, 0, 0, 4) });
        var taskbarOffset = new Slider { Minimum = 0, Maximum = 800, Value = prefs.TaskbarOffsetPixels, TickFrequency = 5, IsSnapToTickEnabled = true };
        taskbarOffset.ValueChanged += (_, _) => { prefs.TaskbarOffsetPixels = (int)taskbarOffset.Value; prefs.Save(); taskbar?.Place(); };
        panel.Children.Add(taskbarOffset);
        var zoomLabel = new TextBlock { Text = $"Taskbar zoom: {prefs.TaskbarZoom:P0}", Margin = new Thickness(0, 12, 0, 4) };
        panel.Children.Add(zoomLabel);
        var taskbarZoom = new Slider { Minimum = 50, Maximum = 200, Value = prefs.TaskbarZoom * 100, TickFrequency = 10, IsSnapToTickEnabled = true };
        taskbarZoom.ValueChanged += (_, _) => { prefs.TaskbarZoom = taskbarZoom.Value / 100; zoomLabel.Text = $"Taskbar zoom: {prefs.TaskbarZoom:P0}"; prefs.Save(); taskbar?.ApplyZoom(); };
        panel.Children.Add(taskbarZoom);
        diagnostics = Text("", 12, "#24394C"); diagnostics.TextWrapping = TextWrapping.Wrap; panel.Children.Add(diagnostics);
        panel.Children.Add(new TextBlock { Text = "Native Codex executable (leave blank for detection)", Margin = new Thickness(0, 18, 0, 5) });
        var path = new TextBox { Text = prefs.CodexPath ?? "", MinWidth = 360 }; panel.Children.Add(path);
        var browse = new Button { Content = "Browse…", Margin = new Thickness(0, 5, 0, 5) }; browse.Click += (_, _) => { var dialog = new OpenFileDialog { Filter = "Codex native executable (*.exe)|*.exe" }; if (dialog.ShowDialog() == true) path.Text = dialog.FileName; }; panel.Children.Add(browse);
        var fallback = new CheckBox { Content = "Use fallback bottom window (not Explorer attachment)", IsChecked = prefs.Fallback, Margin = new Thickness(0, 10, 0, 10) }; panel.Children.Add(fallback);
        var save = new Button { Content = "Save settings and restart widget", Padding = new Thickness(10, 5, 10, 5) };
        save.Click += (_, _) => { prefs.CodexPath = string.IsNullOrWhiteSpace(path.Text) ? null : path.Text.Trim(); prefs.Fallback = fallback.IsChecked == true; prefs.Save(); MessageBox.Show("Settings saved. Quit from the tray, then relaunch the widget.", "Codex Usage"); }; panel.Children.Add(save);
        void Button(string label, Func<Task> action) { var b = new Button { Content = label, Margin = new Thickness(0, 8, 0, 0), IsEnabled = !mock }; b.Click += async (_, _) => await Guard(action); panel.Children.Add(b); }
        Button("Sign in with ChatGPT", SignIn); Button("Cancel pending sign-in", monitor.CancelLoginAsync); Button("Sign out of shared Codex account", monitor.SignOutAsync);
        var reset = new Button { Content = "Recover widget: disable click-through, unlock, move upper-right", Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) => { through = false; prefs.Locked = false; prefs.X = .98; prefs.Y = .025; prefs.Monitor = null; prefs.Save(); host.ApplyClickThrough(false); host.Attach(); RebuildMenus(); }; panel.Children.Add(reset);
        var notes = new TextBlock { Text = "Sign-in and sign-out use the CLI's managed account and can affect other Codex clients. No API keys, tokens, threads, model requests, purchases or reset-credit actions.\n\nExplorer attachment is undocumented. If invisible or unreliable, select fallback and restart. Fallback stays behind normal windows but Show Desktop may hide it; restore it with Recover widget. Click-through is never restored at startup.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 15, 0, 0), FontSize = 11 }; panel.Children.Add(notes);
        settings = new Window { Title = mock ? "Codex Usage Settings — DEMO" : "Codex Usage Settings", Width = 490, Height = 650, MinWidth = 440, MinHeight = 420, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        settings.Closed += (_, _) => { settings = null; diagnostics = null; }; UpdateDiagnostics(); settings.Show();
    }
    private void UpdateDiagnostics()
    {
        if (diagnostics is null) return;
        string detected; try { detected = prefs.FindCodex(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { detected = "Not found. Select native codex.exe below."; }
        diagnostics.Text = $"{(mock ? "DEMO: NO ACCOUNT CONNECTION" : view.Connection)}\n{view.Detail}\nPlan: {view.Plan ?? "Unavailable"}\nPlacement: {host.Mode}\nCLI: {cliVersion}\n{detected}\nTimezone: {TimeZoneInfo.Local.DisplayName}";
    }
    private async Task InspectVersion()
    {
        try
        {
            using var process = new Process { StartInfo = new ProcessStartInfo(prefs.FindCodex()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("--version"); process.Start();
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (TimeoutException) { process.Kill(true); throw; }
            var text = (await stdout).Trim(); await stderr;
            cliVersion = System.Text.RegularExpressions.Regex.IsMatch(text, @"^codex-cli \d+\.\d+\.\d+[-\w.]*$") ? text : "Version unavailable";
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { cliVersion = "Not available"; }
        UpdateDiagnostics();
    }
    private async Task SignIn()
    {
        if (mock) return;
        var url = await monitor.SignInAsync();
        if (url is null) return;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !(uri.Host == "chatgpt.com" || uri.Host.EndsWith(".chatgpt.com", StringComparison.OrdinalIgnoreCase) || uri.Host == "openai.com" || uri.Host.EndsWith(".openai.com", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Unexpected managed authentication URL");
        OpenUrl(uri.AbsoluteUri);
    }
    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private async Task Guard(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or InvalidOperationException or TimeoutException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { MessageBox.Show("The action could not complete (" + e.GetType().Name + "). Check the native Codex path and managed account sign-in.", "Codex Usage"); }
    }
    private void PowerChanged(object sender, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) Dispatcher.BeginInvoke(new Action(() => { if (!mock) monitor.Refresh(); host.Attach(); taskbar?.Place(); })); }
    private void DisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() => { host.Place(); taskbar?.Place(); }));
    private static MonitorView Demo() => new(new UsageSnapshot(DateTimeOffset.UtcNow, [new("demo_general", "DEMO · General", [new("primary", 47.25, 300, DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds()), new("secondary", 16, 10080, DateTimeOffset.UtcNow.AddDays(3).ToUnixTimeSeconds())]), new("demo_extra", "DEMO · Additional bucket", [new("primary", null, null, null)])]), "DEMO", false, "Synthetic values only. App-server is not started; no live cache is read or written.", null);
    private static SolidColorBrush ColorBrush(string value) => (SolidColorBrush)new BrushConverter().ConvertFromString(value)!;
    private static TextBlock Text(string text, double size, string color, FontWeight? weight = null) => new() { Text = text, FontSize = size, Foreground = ColorBrush(color), FontWeight = weight ?? FontWeights.Normal };
}
