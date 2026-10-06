using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using DesktopUtility.Host.Core;
using Microsoft.Win32;
using Application = System.Windows.Application;

namespace DesktopUtility.Host;

public partial class App : Application
{
    private const string MutexName = @"Local\DesktopUtility.SingleInstance";
    private const string ShowEventName = @"Local\DesktopUtility.ShowWindow";
    private const string QuitEventName = @"Local\DesktopUtility.Quit";

    private Mutex? _singleInstance;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _quitEvent;
    private NotifyIcon? _trayIcon;
    private HostService? _host;
    private bool _trayHintShown;

    /// <summary>True once the user chose Exit; lets the main window actually close.</summary>
    public static bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Install();
        RegisterCrashHandlers();

        _singleInstance = new Mutex(true, MutexName, out bool isFirstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);

        var options = LaunchOptions.Parse(e.Args);

        if (!isFirstInstance)
        {
            // Another copy is already serving: bring its window forward, or ask it to exit (used by the installer).
            (options.Quit ? _quitEvent : _showEvent).Set();
            Shutdown();
            return;
        }

        if (options.Quit)
        {
            Shutdown(); // nothing is running
            return;
        }

        _host = new HostService(options);
        var window = new MainWindow(_host);
        MainWindow = window;

        SetUpTrayIcon();
        ListenForSecondInstance();

        if (!options.StartMinimized) ShowMainWindow();

        // The backend starts in the background so the window appears instantly.
        _ = Task.Run(_host.StartAsync);

        SystemEvents.SessionEnding += (_, _) => Dispatcher.Invoke(ExitApplication);
    }

    private void SetUpTrayIcon()
    {
        _trayIcon = new NotifyIcon
        {
            Text = "DesktopUtility",
            Icon = LoadTrayIcon(),
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip()
        };
        _trayIcon.ContextMenuStrip.Items.Add(Loc.Get("tray.open"), null, (_, _) => ShowMainWindow());
        _trayIcon.ContextMenuStrip.Items.Add(new ToolStripSeparator());
        _trayIcon.ContextMenuStrip.Items.Add(Loc.Get("tray.exit"), null, (_, _) => ExitApplication());
        _trayIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            var stream = GetResourceStream(new Uri("pack://application:,,,/AppIcon.ico"))?.Stream;
            if (stream != null) return new Icon(stream);
        }
        catch { /* fall through */ }

        return SystemIcons.Application;
    }

    private void ListenForSecondInstance()
    {
        var handles = new WaitHandle[] { _showEvent!, _quitEvent! };
        var thread = new Thread(() =>
        {
            while (!IsExiting)
            {
                try
                {
                    switch (WaitHandle.WaitAny(handles, 500))
                    {
                        case 0: Dispatcher.InvokeAsync(ShowMainWindow); break;
                        case 1: Dispatcher.InvokeAsync(ExitApplication); return;
                    }
                }
                catch (ObjectDisposedException) { return; }
            }
        })
        { IsBackground = true, Name = "SingleInstanceListener" };
        thread.Start();
    }

    public void ShowMainWindow()
    {
        if (MainWindow == null) return;

        MainWindow.Show();
        if (MainWindow.WindowState == WindowState.Minimized) MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    /// <summary>One-time balloon explaining where the window went when it is closed.</summary>
    public void NotifyHiddenToTray()
    {
        if (_trayHintShown || _trayIcon == null) return;

        _trayHintShown = true;
        _trayIcon.ShowBalloonTip(2500, "DesktopUtility", Loc.Get("tray.hint"), ToolTipIcon.Info);
    }

    public void ExitApplication()
    {
        if (IsExiting) return;
        IsExiting = true;

        _trayIcon?.Dispose();
        _trayIcon = null;

        // Disposing the host may show a UAC prompt (removing the virtual monitor), so keep the UI thread free.
        var host = _host;
        Task.Run(() => host?.Dispose()).ContinueWith(_ => Dispatcher.Invoke(() =>
        {
            Shutdown();
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private void RegisterCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Console.WriteLine($"[Crash] Unhandled exception: {args.ExceptionObject}");

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Console.WriteLine($"[Error] Unobserved task exception: {args.Exception.Flatten().InnerException?.Message}");
            args.SetObserved();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            // A UI glitch should not take the whole host down; it is in the log for diagnosis.
            Console.WriteLine($"[Error] UI exception: {args.Exception}");
            args.Handled = true;
        };
    }
}
