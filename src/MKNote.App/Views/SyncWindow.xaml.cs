using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MKNote.Core.Models;
using MKNote.Sync;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MKNote.App.Views;

/// <summary>
/// 端到端加密云端 / 群晖 NAS 同步配置窗口：支持群晖 WebDAV、群晖 SMB、通用 WebDAV 与本地同步盘配置、连接测试、后台并发增量同步、实时百分比进度与随时取消。
/// </summary>
public sealed partial class SyncWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<SyncSettings, Task> _onSettingsSaved;
    private readonly Func<Task> _onSyncCompleted;
    private CancellationTokenSource? _activeCts;

    public SyncWindow(
        AppSettings settings,
        ElementTheme theme,
        Func<SyncSettings, Task> onSettingsSaved,
        Func<Task> onSyncCompleted)
    {
        InitializeComponent();
        _settings = settings;
        _onSettingsSaved = onSettingsSaved;
        _onSyncCompleted = onSyncCompleted;

        AppWindow.Resize(new SizeInt32(960, 700));
        if (App.AppIconPath is not null) AppWindow.SetIcon(App.AppIconPath);
        SystemBackdrop = new MicaBackdrop();
        RootGrid.RequestedTheme = theme;

        Closed += (_, _) =>
        {
            // 关闭窗口时立即取消进行中的同步/测试请求，避免后台残留传输
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        };

        LoadFromSettings(settings.Sync ?? new SyncSettings());
    }

    private void LoadFromSettings(SyncSettings sync)
    {
        var provider = string.IsNullOrWhiteSpace(sync.Provider) || sync.Provider == "None" ? "SynologyWebDav" : sync.Provider;
        for (int i = 0; i < ProviderCombo.Items.Count; i++)
        {
            if (ProviderCombo.Items[i] is ComboBoxItem item && (item.Tag as string) == provider)
            {
                ProviderCombo.SelectedIndex = i;
                break;
            }
        }
        if (ProviderCombo.SelectedIndex < 0) ProviderCombo.SelectedIndex = 0;

        EndpointBox.Text = sync.EndpointUrl ?? "";
        RemotePathBox.Text = string.IsNullOrWhiteSpace(sync.RemotePath) ? "/MKNoteSync" : sync.RemotePath;
        UsernameBox.Text = sync.Username ?? "";
        PasswordBox.Password = App.Vault.UnprotectSecret(sync.ProtectedPasswordBase64);
        SelfSignedCertCheck.IsChecked = sync.AllowSelfSignedLanCert;
        E2eeToggle.IsOn = sync.EnableE2EE;
        E2eePassBox.Password = App.Vault.UnprotectSecret(sync.ProtectedE2eePassphraseBase64);

        AutoSyncCombo.SelectedIndex = sync.AutoSyncMinutes switch
        {
            5 => 1,
            15 => 2,
            30 => 3,
            60 => 4,
            _ => 0
        };

        if (sync.LastSyncTimeUtc.HasValue)
        {
            SyncStatusText.Text = $"上次同步：{sync.LastSyncTimeUtc.Value.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {sync.LastSyncSummary}";
        }

        UpdateProviderUi();
    }

    private string SelectedProvider =>
        (ProviderCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "SynologyWebDav";

    private void ProviderCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateProviderUi();

    private void UpdateProviderUi()
    {
        if (RemotePathBox is null || GuideText is null || EndpointBox is null) return;
        var p = SelectedProvider;
        bool isWebDav = p is "SynologyWebDav" or "GenericWebDav";

        RemotePathBox.Visibility = isWebDav ? Visibility.Visible : Visibility.Collapsed;
        WebDavAuthPanel.Visibility = isWebDav ? Visibility.Visible : Visibility.Collapsed;
        SelfSignedCertCheck.Visibility = isWebDav ? Visibility.Visible : Visibility.Collapsed;
        BrowseFolderBtn.Visibility = isWebDav ? Visibility.Collapsed : Visibility.Visible;

        EndpointBox.PlaceholderText = p switch
        {
            "SynologyWebDav" => "例如：https://192.168.1.100:5006",
            "SynologySmb" => @"例如：\\192.168.1.100\homes\MKNoteSync 或 Z:\MKNoteSync",
            "GenericWebDav" => "例如：https://your-webdav-server.example/dav",
            _ => @"例如：D:\MKNoteSync"
        };

        GuideText.Text = SyncGuideProvider.GetGuide(p, _settings.Language);
    }

    private SyncSettings CollectSettings()
    {
        var autoMin = (AutoSyncCombo.SelectedItem as ComboBoxItem)?.Tag is string s && int.TryParse(s, out var m) ? m : 0;
        return new SyncSettings
        {
            Provider = SelectedProvider,
            EndpointUrl = EndpointBox.Text.Trim(),
            RemotePath = RemotePathBox.Text.Trim(),
            Username = UsernameBox.Text.Trim(),
            ProtectedPasswordBase64 = App.Vault.ProtectSecret(PasswordBox.Password),
            AllowSelfSignedLanCert = SelfSignedCertCheck.IsChecked == true,
            EnableE2EE = E2eeToggle.IsOn,
            ProtectedE2eePassphraseBase64 = App.Vault.ProtectSecret(E2eePassBox.Password),
            AutoSyncMinutes = autoMin,
            LastSyncTimeUtc = _settings.Sync.LastSyncTimeUtc,
            LastSyncSummary = _settings.Sync.LastSyncSummary
        };
    }

    private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
            EndpointBox.Text = folder.Path;
    }

    private void CancelSync_Click(object sender, RoutedEventArgs e)
    {
        if (_activeCts is null || _activeCts.IsCancellationRequested) return;
        CancelSyncBtn.IsEnabled = false;
        SyncStatusText.Text = "正在取消当前操作...";
        _activeCts.Cancel();
    }

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectSettings();
        var plainPwd = PasswordBox.Password;
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;

        SetBusy(true, "正在测试连接与读写权限...");
        try
        {
            await Task.Run(async () =>
            {
                using var transport = EncryptedSyncService.CreateTransport(cfg, plainPwd);
                await transport.TestConnectionAsync(ct).ConfigureAwait(false);
            }, ct);
            SyncStatusText.Text = "✅ 连接成功！已验证远端目录可正常读取与写入。";
        }
        catch (OperationCanceledException)
        {
            SyncStatusText.Text = "⚠️ 已取消连接测试。";
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"❌ 连接失败：{FormatExceptionMessage(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectSettings();
        var plainPwd = PasswordBox.Password;
        var e2eePass = E2eePassBox.Password;
        _activeCts?.Dispose();
        _activeCts = new CancellationTokenSource();
        var ct = _activeCts.Token;

        SetBusy(true, "正在初始化端到端加密同步...");
        try
        {
            var svc = new EncryptedSyncService();
            var progress = new Progress<SyncProgressInfo>(info =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_activeCts is null) return;
                    SyncStatusText.Text = info.Message;
                    SyncProgress.IsIndeterminate = info.IsIndeterminate;
                    if (!info.IsIndeterminate && info.Total > 0)
                    {
                        SyncProgress.Value = info.Percent;
                        SyncPercentText.Text = $"{info.Percent:0}%";
                        SyncPercentText.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        SyncPercentText.Visibility = Visibility.Collapsed;
                    }
                });
            });

            // 全部加解密与网络传输均在后台线程池执行，绝不阻塞 WinUI 界面线程
            var report = await Task.Run(
                () => svc.SynchronizeWithProgressAsync(App.Vault, cfg, plainPwd, e2eePass, progress, ct),
                ct);

            cfg.LastSyncTimeUtc = report.CompletedAtUtc;
            bool noChanges = report.UploadedNotes == 0 && report.DownloadedNotes == 0 &&
                             report.UploadedBlobs == 0 && report.DownloadedBlobs == 0 &&
                             report.SyncedCategories == 0 && report.ConflictCopiesCreated == 0;

            cfg.LastSyncSummary = noChanges
                ? $"已是最新（跳过未变更 {report.SkippedUnchangedNotes} 篇）" + (report.UsedE2EE ? " (E2EE)" : "")
                : $"上传 {report.UploadedNotes} 篇 / 下载 {report.DownloadedNotes} 篇 / 媒体 +{report.UploadedBlobs}/↓{report.DownloadedBlobs}"
                    + (report.SkippedUnchangedNotes > 0 ? $" / 跳过 {report.SkippedUnchangedNotes} 篇" : "")
                    + (report.ConflictCopiesCreated > 0 ? $" / 冲突副本 {report.ConflictCopiesCreated} 篇" : "")
                    + (report.UsedE2EE ? " (E2EE)" : "");

            await _onSettingsSaved(cfg);
            await _onSyncCompleted();

            SyncStatusText.Text = $"✅ 同步完成（{report.CompletedAtUtc.LocalDateTime:HH:mm:ss}，耗时 {report.ElapsedSeconds:0.0}s）：{cfg.LastSyncSummary}";
        }
        catch (OperationCanceledException)
        {
            SyncStatusText.Text = "⚠️ 已取消同步（已上传的文件不会丢失，下次同步将自动增量续传）。";
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"❌ 同步失败：{FormatExceptionMessage(ex)}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SaveAndClose_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectSettings();
        await _onSettingsSaved(cfg);
        Close();
    }

    private void SetBusy(bool busy, string? status = null)
    {
        TestConnBtn.IsEnabled = !busy;
        SyncNowBtn.IsEnabled = !busy;
        SaveCloseBtn.IsEnabled = !busy;
        CancelSyncBtn.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelSyncBtn.IsEnabled = busy;
        SyncProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy)
        {
            SyncProgress.IsIndeterminate = true;
            SyncProgress.Value = 0;
        }
        else
        {
            SyncPercentText.Visibility = Visibility.Collapsed;
            _activeCts?.Dispose();
            _activeCts = null;
        }
        if (status is not null) SyncStatusText.Text = status;
    }

    private static string FormatExceptionMessage(Exception ex)
    {
        if (ex is AggregateException agg && agg.InnerExceptions.Count > 0)
            ex = agg.InnerExceptions[0];
        if (ex.InnerException is not null && !ex.Message.Contains(ex.InnerException.Message, StringComparison.Ordinal))
            return $"{ex.Message}（{ex.InnerException.Message}）";
        return ex.Message;
    }
}
