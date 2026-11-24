using Microsoft.UI.Xaml;
using MKNote.App.Services;
using MKNote.App.Views;
using MKNote.Core.Interfaces;
using MKNote.Migration;
using MKNote.Storage;

namespace MKNote.App;

/// <summary>
/// MKNote Next 桌面应用入口：负责严格单实例检测、全局异常防崩溃兜底、本地加密库与迁移服务初始化。
/// </summary>
public partial class App : Application
{
    public static IVaultRepository Vault { get; private set; } = null!;
    public static VaultMigrationService Migration { get; private set; } = null!;
    public static MainWindow? MainAppWindow { get; private set; }
    public static bool LaunchedViaAutoStart { get; private set; }

    /// <summary>获取多分辨率应用图标（AppIcon.ico）的运行时绝对路径。</summary>
    public static string? AppIconPath
    {
        get
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            return File.Exists(path) ? path : null;
        }
    }

    private static SingleInstanceGuard? _instanceGuard;

    public App()
    {
        InitializeComponent();

        // Global crash resilience handlers — log errors and keep recovery journal intact
        UnhandledException += (_, e) =>
        {
            LogCrash("WinUI.UnhandledException", e.Exception);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                LogCrash("AppDomain.UnhandledException", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        LaunchedViaAutoStart = Environment.GetCommandLineArgs()
            .Any(a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));

        // Strict Single-Instance enforcement (Win32 Mutex + NamedPipe wakeup even from system tray)
        _instanceGuard = new SingleInstanceGuard();
        if (!_instanceGuard.TryAcquirePrimaryInstance())
        {
            SingleInstanceGuard.SignalExistingInstanceToWake();
            _instanceGuard.Dispose();
            Environment.Exit(0);
            return;
        }

        _instanceGuard.StartListeningForWakeups(() =>
        {
            MainAppWindow?.DispatcherQueue.TryEnqueue(() => MainAppWindow.BringToFront());
        });

        Vault = new SqliteVaultRepository();
        await Vault.InitializeAsync();
        Migration = new VaultMigrationService(Vault);

        MainAppWindow = new MainWindow();
        MainAppWindow.Closed += (_, _) => _instanceGuard?.Dispose();
        MainAppWindow.Activate();
    }

    private static void LogCrash(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MKNoteNext", "Logs");
            Directory.CreateDirectory(dir);
            var logPath = Path.Combine(dir, $"crash_{DateTime.Now:yyyyMMdd}.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {source}: {ex}\r\n\r\n");
        }
        catch { }
    }
}
