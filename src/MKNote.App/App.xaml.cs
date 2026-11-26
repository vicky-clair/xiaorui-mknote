using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MKNote.App.Services;
using MKNote.App.Views;
using MKNote.Core.Interfaces;
using MKNote.Migration;
using MKNote.Security;
using MKNote.Storage;
using Windows.Graphics;

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
        if (VaultKeyManager.RequiresPortablePassphrase(Vault.VaultRootPath))
        {
            var unlocked = await PromptAndUnlockPortableVaultAsync(Vault);
            if (!unlocked)
            {
                _instanceGuard?.Dispose();
                Environment.Exit(0);
                return;
            }
        }
        else
        {
            await Vault.InitializeAsync();
        }

        Migration = new VaultMigrationService(Vault);

        MainAppWindow = new MainWindow();
        MainAppWindow.Closed += (_, _) => _instanceGuard?.Dispose();
        MainAppWindow.Activate();
    }

    /// <summary>
    /// 当便携库已启用 MKP2 强口令保护时，在打开主工作区前弹出原生 WinUI 3 解锁窗口校验口令并初始化加密库。
    /// </summary>
    private static Task<bool> PromptAndUnlockPortableVaultAsync(IVaultRepository vault)
    {
        var tcs = new TaskCompletionSource<bool>();
        var unlockWin = new Window
        {
            Title = "解锁便携加密库 (MKP2) — MKNote Next",
            SystemBackdrop = new MicaBackdrop()
        };
        unlockWin.AppWindow.Resize(new SizeInt32(440, 290));
        if (AppIconPath is not null) unlockWin.AppWindow.SetIcon(AppIconPath);

        var titleTxt = new TextBlock { Text = "🔐 解锁便携加密知识库", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var descTxt = new TextBlock
        {
            Text = "当前便携库已启用 MKP2 强口令保护（PBKDF2-SHA512 + AES-256-GCM），请输入主口令解锁。",
            FontSize = 12.5,
            Opacity = 0.72,
            TextWrapping = TextWrapping.Wrap
        };
        var pwdBox = new PasswordBox { PlaceholderText = "请输入便携库主口令..." };
        var errTxt = new TextBlock { Foreground = new SolidColorBrush(Microsoft.UI.Colors.IndianRed), FontSize = 12, TextWrapping = TextWrapping.Wrap };
        var unlockBtn = new Button { Content = "解锁并打开", Style = (Style)Application.Current.Resources["AccentButtonStyle"], HorizontalAlignment = HorizontalAlignment.Right };

        async Task TryUnlockAsync()
        {
            if (string.IsNullOrEmpty(pwdBox.Password))
            {
                errTxt.Text = "请输入便携库主口令。";
                return;
            }
            unlockBtn.IsEnabled = false;
            errTxt.Text = "正在派生密钥并解锁...";
            try
            {
                var pass = pwdBox.Password;
                await Task.Run(() => vault.InitializeAsync(pass));
                tcs.TrySetResult(true);
                unlockWin.Close();
            }
            catch (Exception ex)
            {
                errTxt.Text = ex.Message;
                unlockBtn.IsEnabled = true;
                pwdBox.Focus(FocusState.Programmatic);
            }
        }

        unlockBtn.Click += async (_, _) => await TryUnlockAsync();
        pwdBox.KeyDown += async (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
                await TryUnlockAsync();
        };
        unlockWin.Closed += (_, _) => tcs.TrySetResult(false);

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(titleTxt);
        panel.Children.Add(descTxt);
        panel.Children.Add(pwdBox);
        panel.Children.Add(errTxt);
        panel.Children.Add(unlockBtn);
        unlockWin.Content = panel;
        unlockWin.Activate();
        return tcs.Task;
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
