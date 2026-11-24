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
/// 端到端加密云端 / 群晖 NAS 同步配置窗口：支持群晖 WebDAV、群晖 SMB、通用 WebDAV 与本地同步盘配置、连接测试、即时同步及三语使用指南展示。
/// </summary>
public sealed partial class SyncWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<SyncSettings, Task> _onSettingsSaved;
    private readonly Func<Task> _onSyncCompleted;

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

        AppWindow.Resize(new SizeInt32(940, 660));
        SystemBackdrop = new MicaBackdrop();
        RootGrid.RequestedTheme = theme;

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
            "GenericWebDav" => "例如：https://dav.jianguoyun.com/dav",
            _ => @"例如：D:\SynologyDrive\MKNoteSync"
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

    private async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectSettings();
        SetBusy(true, "正在测试连接...");
        try
        {
            using var transport = EncryptedSyncService.CreateTransport(cfg, PasswordBox.Password);
            await transport.TestConnectionAsync();
            SyncStatusText.Text = "✅ 连接成功！可以正常读写该同步目标。";
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"❌ 连接失败：{ex.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        var cfg = CollectSettings();
        SetBusy(true, "正在执行端到端加密同步...");
        try
        {
            var svc = new EncryptedSyncService();
            var progress = new Progress<string>(msg => DispatcherQueue.TryEnqueue(() => SyncStatusText.Text = msg));
            var report = await svc.SynchronizeAsync(App.Vault, cfg, PasswordBox.Password, E2eePassBox.Password, progress);

            cfg.LastSyncTimeUtc = report.CompletedAtUtc;
            cfg.LastSyncSummary = $"上传 {report.UploadedNotes} 篇 / 下载 {report.DownloadedNotes} 篇 / 媒体 +{report.UploadedBlobs}/↓{report.DownloadedBlobs}"
                + (report.ConflictCopiesCreated > 0 ? $" / 保留冲突副本 {report.ConflictCopiesCreated} 篇" : "")
                + (report.UsedE2EE ? " (E2EE)" : "");

            await _onSettingsSaved(cfg);
            await _onSyncCompleted();
            SyncStatusText.Text = $"✅ 同步完成（{report.CompletedAtUtc.LocalDateTime:HH:mm:ss}）：{cfg.LastSyncSummary}";
        }
        catch (Exception ex)
        {
            SyncStatusText.Text = $"❌ 同步失败：{ex.Message}";
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
        SyncProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (status is not null) SyncStatusText.Text = status;
    }
}
