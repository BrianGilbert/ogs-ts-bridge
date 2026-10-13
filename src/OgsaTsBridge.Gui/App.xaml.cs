using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace OgsaTsBridge.Gui
{
public partial class App : Application
{
    const string MutexName = "OgsaTsBridge.Gui.SingleInstance";
    Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, MutexName, out bool first);
        if (!first)
        {
            // a window already exists (possibly hidden in the tray) —
            // surface it instead of spawning a duplicate that would
            // fight over the game's telemetry port
            Win32.ActivateMainWindow();
            Shutdown();
            return;
        }
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}

static class Win32
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    static extern bool SetForegroundWindow(nint hWnd);

    const int SwRestore = 9;

    public static void ActivateMainWindow()
    {
        var hwnd = FindWindow(null, "OGSA-TS Bridge");
        if (hwnd != nint.Zero)
        {
            ShowWindow(hwnd, SwRestore);
            SetForegroundWindow(hwnd);
        }
    }
}
}
