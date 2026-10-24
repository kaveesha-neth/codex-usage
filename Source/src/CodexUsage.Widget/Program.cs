using System;
using System.Threading;
using System.Windows;

namespace CodexUsage.Widget;
internal static class Program
{
    public static bool SessionEnding { get; private set; }
    [STAThread]
    private static void Main(string[] args)
    {
        using var singleton = new Mutex(true, "Local\\CodexUsageWidget", out var first);
        if (!first) { MessageBox.Show("Codex Usage is already running. Open its tray icon for Settings or Quit."); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.SessionEnding += (_, _) => SessionEnding = true;
        app.DispatcherUnhandledException += (_, e) => { MessageBox.Show("The widget encountered an unexpected error (" + e.Exception.GetType().Name + "). Restart it from its installed folder.", "Codex Usage"); e.Handled = true; app.Shutdown(1); };
        app.Run(new WidgetWindow(Array.Exists(args, a => a == "--mock")));
    }
}
