using System.Diagnostics;
using System.Text.Json;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using MKNote.App.Services;
using MKNote.Core.Interfaces;
using MKNote.Core.Localization;
using MKNote.Core.Markdown;
using MKNote.Core.Models;
using MKNote.Core.Templates;
using MKNote.Migration;
using MKNote.Security;
using MKNote.Sync;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MKNote.App.Views;

/// <summary>左侧笔记本树形行视图模型（含缩进层级与笔记计数）。</summary>
public sealed record CategoryRow(CategoryItem Model, int Depth)
{
    public string Id => Model.Id;
    public string Name => Model.Name;
    public string DisplayName => Depth <= 0 ? Model.Name : new string(' ', Depth * 2) + "└ " + Model.Name;
    public int NoteCount => Model.NoteCount;
}

/// <summary>中间笔记列表行视图模型（展示置顶/加密标识、格式徽章、摘要与标签）。</summary>
public sealed record NoteListRow(NoteItem Note)
{
    public string DisplayTitle => (Note.IsPinned ? "📌 " : "") + (Note.IsEncrypted ? "🔒 " : "") + Note.Title;
    public string FormatBadge => Note.DocFormat == DocumentFormat.Markdown ? "MD" : Strings.T("nav_rich");
    public string SummaryPreview => Note.IsDeleted
        ? "🗑 " + Strings.T("trash_blocked_title")
        : Note.IsEncrypted
            ? "🔒 AES-256-GCM"
            : (string.IsNullOrWhiteSpace(Note.ContentSummary) ? "…" : Note.ContentSummary);
    public string UpdatedDisplay => Note.UpdatedAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
    public string TagsDisplay => Note.Tags.Count == 0 ? "" : string.Join(" ", Note.Tags.Select(t => "#" + t));
}

/// <summary>背景色预设面板色块行模型（按当前深/浅色主题自动呈现专属配色与名称）。</summary>
public sealed record BackgroundSwatchRow(string Id, string DisplayName, string SwatchHex, string Description);

/// <summary>右侧大纲面板标题行视图模型。</summary>
public sealed record OutlineRow(HeadingItem Heading)
{
    public string Display => new string(' ', Math.Max(0, Heading.Level - 1) * 3) + "• " + Heading.Text;
}

/// <summary>
/// MKNote Next 主工作区窗口：
/// 负责可拖动三栏布局、笔记本与标签筛选、批量多选/删除、WebView2 双内核编辑器桥接、
/// 加密附件虚拟域拦截、区域截图、系统托盘、本地锁屏、三语切换及自动加密同步调度。
/// </summary>
public sealed partial class MainWindow : Window
{
    private AppSettings _settings = new();
    private NoteItem? _currentNote;
    private string? _currentUnlockPassword;
    private bool _editorReady;
    private bool _windowLoaded;
    private string? _selectedCategoryId;
    private string? _selectedTagName;
    private string _quickView = "all";
    private DocumentFormat? _formatFilter;
    private bool _sidebarVisible = true;
    private DispatcherTimer? _idleLockTimer;
    private DispatcherTimer? _autoSyncTimer;
    private SystemTrayService? _trayService;
    private bool _forceExit;
    private bool _isHiddenToTray;
    private bool _syncRunning;
    private bool _isGraphOpen;
    private bool _isMultiSelectMode;
    private bool _suppressSelectionChanged;
    private readonly Dictionary<string, string> _unlockedSessionPasswords = [];
    private WordCountStats _lastWordStats = new(0, 0, 0, 0, 0, 0, 0);

    public MainWindow()
    {
        InitializeComponent();
        AppWindow.Resize(new SizeInt32(1380, 860));
        if (App.AppIconPath is not null) AppWindow.SetIcon(App.AppIconPath);
        SystemBackdrop = new MicaBackdrop();

        RefreshBackgroundPresetSwatches();
        BuildTemplatesMenu();

        InitSystemTray();
        AppWindow.Closing += AppWindow_Closing;

        Closed += (_, _) =>
        {
            _idleLockTimer?.Stop();
            _autoSyncTimer?.Stop();
            _trayService?.Dispose();
        };
        ShellRoot.Loaded += MainWindow_Loaded;
    }

    public void BringToFront()
    {
        _isHiddenToTray = false;
        AppWindow.Show();
        var hwnd = WindowNative.GetWindowHandle(this);
        Win32Helper.SetForegroundWindow(hwnd);
        Activate();
    }

    private void HideToTray()
    {
        _isHiddenToTray = true;
        if (_settings.EnableAppLock && _settings.LockOnMinimize && !string.IsNullOrEmpty(_settings.AppLockPasswordHash))
        {
            ShowAppLockOverlay();
        }
        AppWindow.Hide();
    }

    private void InitSystemTray()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        _trayService = new SystemTrayService(hwnd, "MKNote Next — 本地加密知识库");
        _trayService.ToggleShowHideRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isHiddenToTray || !AppWindow.IsVisible)
                BringToFront();
            else
                HideToTray();
        });
        _trayService.NewMarkdownRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            BringToFront();
            await CreateNoteInteractiveAsync(DocumentFormat.Markdown);
        });
        _trayService.NewRichRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            BringToFront();
            await CreateNoteInteractiveAsync(DocumentFormat.RichHtml);
        });
        _trayService.CaptureScreenRequested += () => DispatcherQueue.TryEnqueue(async () =>
        {
            await CaptureScreenCoreAsync();
        });
        _trayService.LockRequested += () => DispatcherQueue.TryEnqueue(() =>
        {
            BringToFront();
            if (!string.IsNullOrEmpty(_settings.AppLockPasswordHash))
                ShowAppLockOverlay();
        });
        _trayService.ExitRequested += () => DispatcherQueue.TryEnqueue(ForceExit);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_forceExit && _settings.CloseToTray)
        {
            args.Cancel = true;
            HideToTray();
        }
    }

    private void ForceExitApp_Click(object sender, RoutedEventArgs e) => ForceExit();

    private void ForceExit()
    {
        _forceExit = true;
        _trayService?.Dispose();
        Close();
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await App.Vault.LoadSettingsAsync();
        _settings.AutoStartWithWindows = AutoStartService.IsEnabled();
        Strings.CurrentLanguage = string.IsNullOrWhiteSpace(_settings.Language) ? "zh-CN" : _settings.Language;

        ApplyAppTheme(_settings.ThemeMode);
        ApplyUiFont(_settings.UiFontFamily);
        ApplyLocalization();
        UpdateVimQuickButtonLabel();
        InitializePaneSplitters();

        ShellRoot.KeyDown += ShellRoot_KeyDown;

        if (App.LaunchedViaAutoStart && _settings.CloseToTray)
        {
            HideToTray();
        }

        // 启动时执行加密防崩溃恢复检查：自动找回异常退出前尚未落盘的编辑草稿
        var recovered = await App.Vault.RecoverPendingDirtyNotesAsync();
        StatusLeftText.Text = recovered.Count > 0
            ? $"🛡️ {Strings.T("status_recovered")} ({recovered.Count})"
            : $"{Strings.T("status_encrypted")} · {App.Vault.VaultRootPath}";

        if (_settings.EnableAppLock && !string.IsNullOrEmpty(_settings.AppLockPasswordHash))
        {
            ShowAppLockOverlay();
        }

        StartIdleLockMonitor();
        ConfigureAutoSyncTimer();
        await InitWebViewAsync();
        _windowLoaded = true;
        await RefreshAllListsAsync();
    }

    // ==================== Trilingual Localization (zh-CN / en-US / ja-JP) ====================
    private void ApplyLocalization()
    {
        Strings.CurrentLanguage = _settings.Language;
        NewNoteBtnText.Text = Strings.T("new_note");
        MenuNewMd.Text = Strings.T("new_md");
        MenuNewRich.Text = Strings.T("new_rich");
        TemplatesMenu.Text = Strings.T("new_template");
        MenuNewSticky.Text = Strings.T("new_sticky");
        SearchBox.PlaceholderText = Strings.T("search_placeholder");

        ToolTipService.SetToolTip(CaptureBtn, Strings.T("tip_screenshot"));
        ToolTipService.SetToolTip(InsertImgBtn, Strings.T("tip_insert_image"));
        ToolTipService.SetToolTip(BgColorBtn, Strings.T("tip_bg"));
        ToolTipService.SetToolTip(MoreMenuBtn, Strings.T("tip_more"));
        ToolTipService.SetToolTip(ExportNoteBtn, Strings.T("tip_export_note"));
        ToolTipService.SetToolTip(CopyNoteToBtn, Strings.T("tip_copy_to"));
        ToolTipService.SetToolTip(MoveNoteToBtn, Strings.T("tip_move_to"));
        ToolTipService.SetToolTip(PinNoteToggle, Strings.T("tip_pin"));
        ToolTipService.SetToolTip(StarNoteToggle, Strings.T("tip_star"));
        ToolTipService.SetToolTip(DeleteNoteBtn, Strings.T("tip_delete"));
        ToolTipService.SetToolTip(AddCatBtn, Strings.T("tip_new_notebook"));

        MenuSyncItem.Text = Strings.T("menu_sync");
        MenuVimGuideItem.Text = Strings.T("menu_vim_guide");
        MenuMdGuideItem.Text = Strings.T("menu_md_guide");
        EncryptMenuItem.Text = Strings.T("menu_encrypt_note");
        MenuHistoryItem.Text = Strings.T("menu_history");
        MenuGraphItem.Text = Strings.T("nav_graph");
        MenuBackupItem.Text = Strings.T("menu_backup");
        MenuImportObsItem.Text = Strings.T("menu_import_obs");
        MenuImportLegacyItem.Text = Strings.T("menu_import_legacy");
        MenuExportCurrentItem.Text = Strings.T("menu_export_current");
        MenuExportObsItem.Text = Strings.T("menu_export_obs");
        MenuLockItem.Text = Strings.T("menu_lock");
        MenuSettingsItem.Text = Strings.T("menu_settings");
        MenuExitItem.Text = Strings.T("tray_exit");

        NavAllText.Text = Strings.T("nav_all");
        NavStarredText.Text = Strings.T("nav_starred");
        NavTrashText.Text = Strings.T("nav_trash");
        SectionNotebooksText.Text = Strings.T("section_notebooks");
        SectionTagsText.Text = Strings.T("section_tags");
        FooterGraphText.Text = Strings.T("nav_graph");
        FooterSettingsText.Text = Strings.T("menu_settings").TrimEnd('.');

        FilterAllBtn.Content = _settings.Language == "en-US" ? "All" : _settings.Language == "ja-JP" ? "すべて" : "全部";
        FilterRichBtn.Content = Strings.T("nav_rich");
        EmptyTrashBtn.Content = Strings.T("btn_empty_trash");
        ToolTipService.SetToolTip(MultiSelectToggleBtn, Strings.T("tip_multi_select"));
        BatchSelectAllBtn.Content = Strings.T("batch_select_all");
        BatchInvertBtn.Content = Strings.T("batch_invert");
        BatchRestoreBtn.Content = Strings.T("batch_restore");
        BatchExportBtn.Content = Strings.T("batch_export");
        BatchCancelBtn.Content = Strings.T("batch_done");
        UpdateBatchSelectionUi();
        NoteTitleBox.PlaceholderText = Strings.T("note_title_placeholder");
        AddNoteTagBtn.Content = "+ " + Strings.T("section_tags");

        EmptyStateHintText.Text = Strings.T("empty_editor_hint");
        TrashBlockedTitleText.Text = Strings.T("trash_blocked_title");
        TrashBlockedDescText.Text = Strings.T("trash_blocked_desc");
        TrashRestoreGateBtn.Content = Strings.T("btn_restore_note");
        TrashPermDeleteGateBtn.Content = Strings.T("btn_perm_delete");
        RestoreNoteBtn.Content = Strings.T("btn_restore_note");

        PivotOutlineItem.Header = Strings.T("panel_outline");
        PivotBacklinksItem.Header = Strings.T("panel_backlinks");
        PivotAttachItem.Header = Strings.T("panel_attachments");
        AddAttachBtn.Content = Strings.T("btn_add_attachment");

        WcTitleText.Text = Strings.T("wc_title");
        WcLblTotal.Text = Strings.T("wc_total");
        WcLblCjk.Text = Strings.T("wc_cjk");
        WcLblLatin.Text = Strings.T("wc_latin");
        WcLblNoSp.Text = Strings.T("wc_chars_no_sp");
        WcLblSp.Text = Strings.T("wc_chars_sp");
        WcLblPara.Text = Strings.T("wc_paragraphs");
        WcLblLines.Text = Strings.T("wc_lines");
        WcLblSel.Text = Strings.T("wc_selection");

        UpdateWordCountDisplay(_lastWordStats);
        UpdateVimQuickButtonLabel();
        SendToEditor("editor.setLocale", new { dict = Strings.GetEditorLocaleDictionary(_settings.Language) });
    }

    /// <summary>
    /// 安全应用界面字体：通过多语言回退链避免缺失字符乱码，并严格跳过所有 WinUI 图标控件与内部符号字形，防止图标变方框。
    /// </summary>
    private void ApplyUiFont(string? fontFamilyName)
    {
        var safeChain = SystemFontService.BuildSafeUiFontChain(fontFamilyName);
        var ff = new FontFamily(safeChain);

        // 同步更新应用主题默认文字字体（不影响 SymbolThemeFontFamily 图标字体），让弹窗与虚拟化列表项自动继承
        if (Application.Current?.Resources is not null)
        {
            Application.Current.Resources["ContentControlThemeFontFamily"] = ff;
        }

        ApplyFontRecursive(ShellRoot, ff);
    }

    private static void ApplyFontRecursive(DependencyObject root, FontFamily ff)
    {
        // 严禁进入 FontIcon / SymbolIcon / PathIcon 等图标控件及其子树，防止覆盖 Segoe Fluent Icons / MDL2 Assets
        if (root is IconElement)
            return;

        if (root is Control ctrl)
        {
            bool isGlyphContent = ctrl is ContentControl { Content: string cs } && ContainsPrivateUseChar(cs);
            if (!isGlyphContent && !IsIconFontSource(ctrl.FontFamily?.Source))
                ctrl.FontFamily = ff;
        }
        else if (root is TextBlock tb)
        {
            if (!IsProtectedIconTextBlock(tb))
                tb.FontFamily = ff;
        }

        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            ApplyFontRecursive(VisualTreeHelper.GetChild(root, i), ff);
        }
    }

    private static bool IsIconFontSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        return source.Contains("Icons", StringComparison.OrdinalIgnoreCase)
            || source.Contains("MDL2", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Symbol", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Assets", StringComparison.OrdinalIgnoreCase)
            || source.Contains("Marlett", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsProtectedIconTextBlock(TextBlock tb)
    {
        if (IsIconFontSource(tb.FontFamily?.Source))
            return true;

        var name = tb.Name;
        if (!string.IsNullOrEmpty(name) &&
            (name.Contains("Glyph", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Chevron", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Icon", StringComparison.OrdinalIgnoreCase)
             || name.Contains("Check", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // WinUI 内置图标字符位于 Unicode 私用区 (U+E000 - U+F8FF)
        return ContainsPrivateUseChar(tb.Text);
    }

    private static bool ContainsPrivateUseChar(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var ch in text)
        {
            if (ch is >= '\uE000' and <= '\uF8FF')
                return true;
        }
        return false;
    }

    private void BuildTemplatesMenu()
    {
        TemplatesMenu.Items.Clear();
        foreach (var tpl in BuiltinTemplates.GetAll())
        {
            var mi = new MenuFlyoutItem { Text = tpl.Name, Tag = tpl };
            mi.Click += async (s, _) =>
            {
                if (s is MenuFlyoutItem { Tag: NoteTemplateItem t })
                {
                    var title = BuiltinTemplates.ExpandVariables(t.DefaultTitlePattern);
                    var body = BuiltinTemplates.ExpandVariables(t.Content);
                    var created = await App.Vault.CreateNoteAsync(
                        title,
                        t.DocFormat,
                        _selectedCategoryId,
                        body,
                        backgroundColor: t.DefaultBackgroundColor,
                        tags: t.DefaultTags);
                    await RefreshAllListsAsync(selectNoteId: created.Id);
                }
            };
            TemplatesMenu.Items.Add(mi);
        }
    }

    private async Task InitWebViewAsync()
    {
        var userDataDir = Path.Combine(App.Vault.VaultRootPath, ".webview2_cache");
        var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, userDataDir, null);
        await EditorWebView.EnsureCoreWebView2Async(env);

        var core = EditorWebView.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = true;

        var editorAssetsDir = Path.Combine(AppContext.BaseDirectory, "Assets", "EditorWeb");
        if (Directory.Exists(editorAssetsDir))
        {
            core.SetVirtualHostNameToFolderMapping(
                "appassets.mknote.local",
                editorAssetsDir,
                CoreWebView2HostResourceAccessKind.Allow);
        }

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;
        core.NewWindowRequested += (s, args) =>
        {
            args.Handled = true;
            if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) && (uri.Scheme == "http" || uri.Scheme == "https"))
                Process.Start(new ProcessStartInfo(uri.ToString()) { UseShellExecute = true });
        };

        core.Navigate("https://appassets.mknote.local/index.html");
    }

    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (!Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri)) return;

        if (uri.Host.Equals("vault.mknote.local", StringComparison.OrdinalIgnoreCase) &&
            uri.AbsolutePath.StartsWith("/attachments/", StringComparison.OrdinalIgnoreCase))
        {
            var key = Uri.UnescapeDataString(uri.AbsolutePath["/attachments/".Length..]);
            var media = App.Vault.ReadAttachmentBytesAsync(key).GetAwaiter().GetResult();
            if (media is not null)
            {
                var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(mem))
                {
                    writer.WriteBytes(media.Value.Data);
                    writer.StoreAsync().AsTask().GetAwaiter().GetResult();
                    writer.DetachStream();
                }
                mem.Seek(0);
                args.Response = sender.Environment.CreateWebResourceResponse(mem, 200, "OK", $"Content-Type: {media.Value.MimeType}");
                return;
            }
            args.Response = sender.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        if (!uri.Host.EndsWith(".mknote.local", StringComparison.OrdinalIgnoreCase) &&
            uri.Scheme is "http" or "https")
        {
            args.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Offline Blocked", "");
        }
    }

    private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        try
        {
            using var doc = JsonDocument.Parse(args.WebMessageAsJson);
            var root = doc.RootElement;
            var method = root.GetProperty("method").GetString();
            var hasPayload = root.TryGetProperty("payload", out var payload);

            switch (method)
            {
                case "editor.ready":
                    _editorReady = true;
                    SendToEditor("editor.setLocale", new { dict = Strings.GetEditorLocaleDictionary(_settings.Language) });
                    PushSystemFontsToEditor();
                    PushGlobalBackgroundImageToEditor();
                    PushMarkdownShortcutsToEditor();
                    if (_currentNote is not null)
                        await LoadNoteIntoEditorAsync(_currentNote);
                    else
                        ShowEmptyEditorState();
                    break;

                case "editor.fontSettingsChanged":
                    if (hasPayload)
                    {
                        if (payload.TryGetProperty("fontFamily", out var ffProp) && !string.IsNullOrWhiteSpace(ffProp.GetString()))
                            _settings.EditorFontFamily = SystemFontService.BuildSafeEditorCssFontChain(ffProp.GetString());
                        if (payload.TryGetProperty("fontSize", out var fsProp) && fsProp.TryGetInt32(out var fs))
                            _settings.EditorFontSize = Math.Clamp(fs, 11, 36);
                        await App.Vault.SaveSettingsAsync(_settings);
                        StatusLeftText.Text = $"✓ 正文字体已更新：{ExtractPrimaryFontName(_settings.EditorFontFamily)} · {_settings.EditorFontSize}px";
                    }
                    break;

                case "doc.contentChanged":
                    if (_currentNote is not null && !_currentNote.IsDeleted && hasPayload)
                    {
                        var noteId = payload.GetProperty("noteId").GetString();
                        if (noteId != _currentNote.Id) break;
                        var content = payload.GetProperty("content").GetString() ?? "";

                        // 1. 先立即写入加密防崩溃预写日志，防止异常闪退丢失内容
                        if (!_currentNote.IsEncrypted)
                        {
                            await App.Vault.SaveCrashRecoveryEntryAsync(_currentNote.Id, _currentNote.Title, _currentNote.DocFormat, content);
                        }
                        // 2. 经单笔记异步锁串行提交至 SQLCipher 加密数据库，落盘后自动清理恢复日志
                        await App.Vault.SaveNoteContentAsync(_currentNote, content, _currentUnlockPassword, createRevision: false);
                        StatusLeftText.Text = $"{Strings.T("status_saved")} · {DateTime.Now:HH:mm:ss}";
                        RenderNoteTagChips(_currentNote);
                        await RefreshInspectorAsync(_currentNote, content);
                    }
                    break;

                case "doc.statsChanged":
                    if (hasPayload)
                    {
                        var stats = new WordCountStats(
                            WordStyleTotal: payload.TryGetProperty("wordStyleTotal", out var v1) ? v1.GetInt32() : 0,
                            CjkChars: payload.TryGetProperty("cjkChars", out var v2) ? v2.GetInt32() : 0,
                            LatinWords: payload.TryGetProperty("latinWords", out var v3) ? v3.GetInt32() : 0,
                            CharsNoSpaces: payload.TryGetProperty("charsNoSpaces", out var v4) ? v4.GetInt32() : 0,
                            CharsWithSpaces: payload.TryGetProperty("charsWithSpaces", out var v5) ? v5.GetInt32() : 0,
                            Paragraphs: payload.TryGetProperty("paragraphs", out var v6) ? v6.GetInt32() : 0,
                            Lines: payload.TryGetProperty("lines", out var v7) ? v7.GetInt32() : 0,
                            SelectionWords: payload.TryGetProperty("selectionWords", out var v8) ? v8.GetInt32() : 0,
                            SelectionChars: payload.TryGetProperty("selectionChars", out var v9) ? v9.GetInt32() : 0);
                        UpdateWordCountDisplay(stats);
                    }
                    break;

                case "doc.forceSaveRequested":
                    if (_currentNote is not null && !_currentNote.IsDeleted)
                    {
                        var content = await App.Vault.ReadNoteContentAsync(_currentNote, _currentUnlockPassword);
                        await App.Vault.SaveNoteContentAsync(_currentNote, content, _currentUnlockPassword, createRevision: true, revisionReason: "manual_save");
                        StatusLeftText.Text = $"✓ {Strings.T("status_saved")} (Snapshot) · {DateTime.Now:HH:mm:ss}";
                    }
                    break;

                case "doc.openWikiLink":
                    if (hasPayload)
                    {
                        var targetTitle = payload.GetProperty("targetTitle").GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(targetTitle))
                            await OpenOrCreateWikiLinkAsync(targetTitle);
                    }
                    break;

                case "graph.openNote":
                    _isGraphOpen = false;
                    if (hasPayload)
                    {
                        var targetId = payload.GetProperty("noteId").GetString();
                        if (!string.IsNullOrWhiteSpace(targetId))
                            await RefreshAllListsAsync(selectNoteId: targetId);
                    }
                    break;

                case "graph.close":
                    await CloseGraphViewAsync();
                    break;

                case "vim.modeChanged":
                    if (hasPayload)
                    {
                        var mode = (payload.GetProperty("mode").GetString() ?? "normal").ToUpperInvariant();
                        VimModeQuickBtn.Content = $"VIM [{mode}]";
                    }
                    break;

                case "doc.pickImageRequested":
                    await InsertLocalImagesCoreAsync();
                    break;

                case "doc.captureScreenRequested":
                    await CaptureScreenCoreAsync();
                    break;

                case "doc.savePastedAsset":
                    if (_currentNote is not null && !_currentNote.IsDeleted && hasPayload)
                    {
                        var fileName = payload.GetProperty("fileName").GetString() ?? "image.png";
                        var mimeType = payload.GetProperty("mimeType").GetString() ?? "image/png";
                        var b64 = payload.GetProperty("base64Data").GetString() ?? "";
                        var bytes = Convert.FromBase64String(b64);
                        var att = await App.Vault.SaveAttachmentAsync(_currentNote.Id, fileName, mimeType, bytes, isInline: true);
                        var localUrl = $"https://vault.mknote.local/attachments/{att.Sha256}";
                        SendToEditor("editor.insertMedia", new { url = localUrl, fileName = att.FileName, isImage = mimeType.StartsWith("image/") });
                        await RefreshAttachmentsAsync(_currentNote.Id);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"Editor Event Error: {ex.Message}";
        }
    }

    private void UpdateWordCountDisplay(WordCountStats stats)
    {
        _lastWordStats = stats;
        var suffix = Strings.T("word_count_suffix");
        WordCountBtn.Content = stats.SelectionWords > 0
            ? $"{stats.SelectionWords} / {stats.WordStyleTotal} {suffix}"
            : $"{stats.WordStyleTotal} {suffix}";

        WcValTotal.Text = stats.WordStyleTotal.ToString("N0");
        WcValCjk.Text = stats.CjkChars.ToString("N0");
        WcValLatin.Text = stats.LatinWords.ToString("N0");
        WcValNoSp.Text = stats.CharsNoSpaces.ToString("N0");
        WcValSp.Text = stats.CharsWithSpaces.ToString("N0");
        WcValPara.Text = stats.Paragraphs.ToString("N0");
        WcValLines.Text = stats.Lines.ToString("N0");
        WcValSel.Text = $"{stats.SelectionWords:N0} / {stats.SelectionChars:N0}";
    }

    private void SendToEditor(string method, object payload)
    {
        if (!_editorReady || EditorWebView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { type = "request", method, payload });
        EditorWebView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void ShowEmptyEditorState()
    {
        _isGraphOpen = false;
        _currentNote = null;
        NoteHeaderBar.Visibility = Visibility.Collapsed;
        NoteLockGate.Visibility = Visibility.Collapsed;
        TrashBlockedGate.Visibility = Visibility.Collapsed;
        EditorWebView.Visibility = Visibility.Collapsed;
        EmptyStateGate.Visibility = Visibility.Visible;
        SendToEditor("editor.clear", new { });
        UpdateWordCountDisplay(new WordCountStats(0, 0, 0, 0, 0, 0, 0));
    }

    private static List<CategoryRow> BuildIndentedCategoryRows(IReadOnlyList<CategoryItem> cats)
    {
        var byParent = cats.GroupBy(c => c.ParentId ?? "").ToDictionary(g => g.Key, g => g.ToList());
        var rows = new List<CategoryRow>();
        var visited = new HashSet<string>();

        void Walk(string parentKey, int depth)
        {
            if (!byParent.TryGetValue(parentKey, out var children)) return;
            foreach (var c in children)
            {
                if (!visited.Add(c.Id)) continue;
                rows.Add(new CategoryRow(c, depth));
                Walk(c.Id, depth + 1);
            }
        }
        Walk("", 0);
        // Any orphaned sub-categories whose parent was deleted
        foreach (var c in cats.Where(c => !visited.Contains(c.Id)))
            rows.Add(new CategoryRow(c, 0));

        return rows;
    }

    private async Task RefreshAllListsAsync(string? selectNoteId = null)
    {
        var cats = await App.Vault.GetCategoriesAsync();
        CategoryList.ItemsSource = BuildIndentedCategoryRows(cats);

        var tags = await App.Vault.GetTagsAsync();
        TagList.ItemsSource = tags;

        await RefreshNoteListAsync(selectNoteId ?? _currentNote?.Id);
    }

    private async Task RefreshNoteListAsync(string? selectNoteId = null)
    {
        var filter = new NoteQueryFilter(
            CategoryId: _selectedCategoryId,
            IncludeChildCategories: _settings.ShowChildCategoryNotes,
            TagName: _selectedTagName,
            OnlyStarred: _quickView == "starred",
            OnlyTrash: _quickView == "trash",
            FormatFilter: _formatFilter,
            SearchQuery: SearchBox.Text);

        var notes = await App.Vault.QueryNotesAsync(filter);
        var rows = notes.Select(n => new NoteListRow(n)).ToList();
        NoteListView.ItemsSource = rows;

        EmptyTrashBtn.Visibility = _quickView == "trash" ? Visibility.Visible : Visibility.Collapsed;
        ListHeaderText.Text = _quickView switch
        {
            "starred" => $"{Strings.T("nav_starred")} ({rows.Count})",
            "trash" => $"{Strings.T("nav_trash")} ({rows.Count})",
            _ => $"{Strings.T("nav_all")} ({rows.Count})"
        };

        SendToEditor("editor.updateKnownTitles", new { titles = notes.Select(n => n.Title).Distinct().ToList() });

        if (_isMultiSelectMode)
        {
            _suppressSelectionChanged = true;
            NoteListView.SelectedItems.Clear();
            _suppressSelectionChanged = false;
            UpdateBatchSelectionUi();
            if (rows.Count == 0)
                ShowEmptyEditorState();
            return;
        }

        if (rows.Count > 0)
        {
            var match = selectNoteId is not null ? rows.FirstOrDefault(r => r.Note.Id == selectNoteId) : null;
            NoteListView.SelectedItem = match ?? rows[0];
        }
        else
        {
            NoteListView.SelectedItem = null;
            ShowEmptyEditorState();
        }
    }

    private async void NoteListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionChanged) return;
        if (_isMultiSelectMode)
        {
            UpdateBatchSelectionUi();
            return;
        }

        if (NoteListView.SelectedItem is not NoteListRow row)
        {
            ShowEmptyEditorState();
            return;
        }
        _currentNote = row.Note;
        _currentUnlockPassword = _unlockedSessionPasswords.GetValueOrDefault(_currentNote.Id);
        await LoadNoteIntoEditorAsync(_currentNote);
    }

    // ==================== 笔记批量多选、全选、反选与批量删除 ====================
    private void MultiSelectToggle_Click(object sender, RoutedEventArgs e)
    {
        SetMultiSelectMode(MultiSelectToggleBtn.IsChecked == true);
    }

    private void ExitMultiSelect_Click(object sender, RoutedEventArgs e)
    {
        SetMultiSelectMode(false);
    }

    private void SetMultiSelectMode(bool enable)
    {
        _isMultiSelectMode = enable;
        MultiSelectToggleBtn.IsChecked = enable;
        BatchActionBar.Visibility = enable ? Visibility.Visible : Visibility.Collapsed;

        _suppressSelectionChanged = true;
        if (enable)
        {
            NoteListView.SelectionMode = ListViewSelectionMode.Multiple;
            NoteListView.SelectedItems.Clear();
        }
        else
        {
            NoteListView.SelectionMode = ListViewSelectionMode.Single;
            if (_currentNote is not null && NoteListView.ItemsSource is IEnumerable<NoteListRow> rows)
            {
                NoteListView.SelectedItem = rows.FirstOrDefault(r => r.Note.Id == _currentNote.Id);
            }
        }
        _suppressSelectionChanged = false;
        UpdateBatchSelectionUi();
    }

    private void UpdateBatchSelectionUi()
    {
        if (BatchSelectionCountText is null) return;
        var count = _isMultiSelectMode ? NoteListView.SelectedItems.Count : 0;
        BatchSelectionCountText.Text = string.Format(Strings.T("batch_selected_fmt"), count);

        bool isTrash = _quickView == "trash";
        BatchDeleteBtn.Content = isTrash ? Strings.T("batch_perm_delete") : Strings.T("batch_delete");
        BatchDeleteBtn.IsEnabled = count > 0;
        BatchRestoreBtn.Visibility = isTrash ? Visibility.Visible : Visibility.Collapsed;
        BatchRestoreBtn.IsEnabled = isTrash && count > 0;
        BatchExportBtn.Visibility = isTrash ? Visibility.Collapsed : Visibility.Visible;
        BatchExportBtn.IsEnabled = !isTrash && count > 0;
    }

    private void SelectAllNotes_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMultiSelectMode) return;
        _suppressSelectionChanged = true;
        NoteListView.SelectAll();
        _suppressSelectionChanged = false;
        UpdateBatchSelectionUi();
    }

    private void InvertNoteSelection_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMultiSelectMode) return;
        var selectedIds = NoteListView.SelectedItems
            .OfType<NoteListRow>()
            .Select(r => r.Note.Id)
            .ToHashSet(StringComparer.Ordinal);

        var toSelect = NoteListView.Items
            .OfType<NoteListRow>()
            .Where(r => !selectedIds.Contains(r.Note.Id))
            .ToList();

        _suppressSelectionChanged = true;
        NoteListView.SelectedItems.Clear();
        foreach (var row in toSelect)
            NoteListView.SelectedItems.Add(row);
        _suppressSelectionChanged = false;
        UpdateBatchSelectionUi();
    }

    private async void BatchDeleteNotes_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMultiSelectMode) return;
        var selectedRows = NoteListView.SelectedItems.OfType<NoteListRow>().ToList();
        if (selectedRows.Count == 0) return;

        bool isTrash = _quickView == "trash";
        var confirmMsg = isTrash
            ? $"确定永久彻底删除已选中的 {selectedRows.Count} 篇笔记吗？此操作不可恢复，关联的孤立图片也会同步清理。"
            : $"确定将已选中的 {selectedRows.Count} 篇笔记移入回收站吗？";

        var dlg = new ContentDialog
        {
            Title = isTrash ? Strings.T("batch_perm_delete") : Strings.T("batch_delete"),
            Content = confirmMsg,
            PrimaryButtonText = Strings.T("dlg_ok"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var ids = selectedRows.Select(r => r.Note.Id).ToList();
        if (_currentNote is not null && ids.Contains(_currentNote.Id, StringComparer.Ordinal))
        {
            // 立即清空画布，避免已删除文档残留展示
            ShowEmptyEditorState();
        }

        int count = isTrash
            ? await App.Vault.PermanentlyDeleteNotesBatchAsync(ids)
            : await App.Vault.SoftDeleteNotesBatchAsync(ids);

        await RefreshAllListsAsync(selectNoteId: null);
        StatusLeftText.Text = isTrash
            ? $"✓ 已永久删除 {count} 篇笔记"
            : $"✓ 已将 {count} 篇笔记移入回收站";
    }

    private async void BatchRestoreNotes_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMultiSelectMode || _quickView != "trash") return;
        var selectedIds = NoteListView.SelectedItems.OfType<NoteListRow>().Select(r => r.Note.Id).ToList();
        if (selectedIds.Count == 0) return;

        int restored = await App.Vault.RestoreNotesBatchAsync(selectedIds);
        await RefreshAllListsAsync(selectNoteId: null);
        StatusLeftText.Text = $"✓ 已从回收站恢复 {restored} 篇笔记";
    }

    private async Task LoadNoteIntoEditorAsync(NoteItem note)
    {
        _isGraphOpen = false;
        EmptyStateGate.Visibility = Visibility.Collapsed;
        NoteHeaderBar.Visibility = Visibility.Visible;
        NoteTitleBox.Text = note.Title;
        NoteTitleBox.IsEnabled = !note.IsDeleted;
        PinNoteToggle.IsChecked = note.IsPinned;
        StarNoteToggle.IsChecked = note.IsStarred;
        CopyNoteToBtn.Visibility = note.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        MoveNoteToBtn.Visibility = note.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        PinNoteToggle.Visibility = note.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        StarNoteToggle.Visibility = note.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        RestoreNoteBtn.Visibility = note.IsDeleted ? Visibility.Visible : Visibility.Collapsed;
        AddNoteTagBtn.Visibility = note.IsDeleted ? Visibility.Collapsed : Visibility.Visible;
        RenderNoteTagChips(note);

        EncryptMenuItem.Text = note.IsEncrypted ? "🔓 " + Strings.T("menu_encrypt_note") : "🔒 " + Strings.T("menu_encrypt_note");

        // Trash privacy rule: deleted notes CANNOT be viewed until restored!
        if (note.IsDeleted)
        {
            NoteLockGate.Visibility = Visibility.Collapsed;
            EditorWebView.Visibility = Visibility.Collapsed;
            TrashBlockedGate.Visibility = Visibility.Visible;
            SendToEditor("editor.clear", new { });
            UpdateWordCountDisplay(new WordCountStats(0, 0, 0, 0, 0, 0, 0));
            return;
        }

        TrashBlockedGate.Visibility = Visibility.Collapsed;

        var isDark = IsAppDarkTheme();
        var resolvedHex = BackgroundPreset.ResolveHex(note.BackgroundColor ?? _settings.DefaultBackgroundColor, isDark);
        BgColorSwatch.Background = new SolidColorBrush(StickyNoteWindow.ParseHexColor(resolvedHex));

        if (note.IsEncrypted && string.IsNullOrEmpty(_currentUnlockPassword))
        {
            EditorWebView.Visibility = Visibility.Collapsed;
            NoteLockGate.Visibility = Visibility.Visible;
            NoteLockHintText.Text = $"密码提示：{(string.IsNullOrWhiteSpace(note.PasswordHint) ? "无" : note.PasswordHint)}";
            NoteUnlockPwdBox.Password = "";
            NoteUnlockErrorText.Text = "";
            return;
        }

        NoteLockGate.Visibility = Visibility.Collapsed;
        EditorWebView.Visibility = Visibility.Visible;

        var content = await App.Vault.ReadNoteContentAsync(note, _currentUnlockPassword);
        var allNotes = await App.Vault.QueryNotesAsync(new NoteQueryFilter());

        SendToEditor("editor.loadDocument", new
        {
            noteId = note.Id,
            format = note.DocFormat == DocumentFormat.Markdown ? "markdown" : "rich_html",
            content,
            readOnly = false,
            bgHex = resolvedHex,
            bgStyle = note.BackgroundStyle.ToString().ToLowerInvariant(),
            isLightBg = BackgroundPreset.IsLightBackground(resolvedHex),
            viewMode = _settings.DefaultMarkdownViewMode switch
            {
                MarkdownViewMode.SplitPreview => "split",
                MarkdownViewMode.Reading => "reading",
                _ => "live"
            },
            fontFamily = SystemFontService.BuildSafeEditorCssFontChain(_settings.EditorFontFamily),
            fontSize = _settings.EditorFontSize,
            vimConfig = BuildVimConfigPayload(),
            mdShortcuts = BuildMarkdownShortcutsPayload(),
            globalBgImage = BuildGlobalBgImagePayload(),
            knownTitles = allNotes.Select(n => n.Title).Distinct().ToList()
        });

        UpdateWordCountDisplay(MarkdownAnalyzer.ComputeWordCountStats(content, note.DocFormat));
        await RefreshInspectorAsync(note, content);
        await RefreshAttachmentsAsync(note.Id);
    }

    // ==================== Interactive Tag Chips (Both Rich Text & Markdown) ====================
    private void RenderNoteTagChips(NoteItem note)
    {
        NoteTagsPanel.Children.Clear();
        foreach (var tag in note.Tags)
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            chip.Children.Add(new TextBlock
            {
                Text = "#" + tag,
                FontSize = 11.5,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            });

            if (!note.IsDeleted)
            {
                var delBtn = new Button
                {
                    Content = "×",
                    Tag = tag,
                    Padding = new Thickness(3, 0, 3, 0),
                    MinWidth = 0,
                    MinHeight = 0,
                    FontSize = 11,
                    Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                    BorderThickness = new Thickness(0)
                };
                delBtn.Click += async (s, _) =>
                {
                    if (_currentNote is null || s is not Button { Tag: string t }) return;
                    _currentNote.Tags.RemoveAll(x => x.Equals(t, StringComparison.OrdinalIgnoreCase));
                    await App.Vault.UpdateNoteTagsAsync(_currentNote.Id, _currentNote.Tags);
                    RenderNoteTagChips(_currentNote);
                    TagList.ItemsSource = await App.Vault.GetTagsAsync();
                    await RefreshNoteListAsync(_currentNote.Id);
                };
                chip.Children.Add(delBtn);
            }

            var border = new Border
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 5, 1),
                Background = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"],
                Child = chip
            };
            NoteTagsPanel.Children.Add(border);
        }
    }

    private async void AddTagToNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        var box = new TextBox { PlaceholderText = "输入标签名（多个可用逗号或空格分隔，支持多级如 工作/项目）" };
        var dlg = new ContentDialog
        {
            Title = Strings.T("tip_add_tag"),
            Content = box,
            PrimaryButtonText = Strings.T("dlg_ok"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            var newTags = box.Text.Split([',', '，', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var t in newTags)
            {
                var clean = t.TrimStart('#');
                if (!string.IsNullOrEmpty(clean) && !_currentNote.Tags.Contains(clean, StringComparer.OrdinalIgnoreCase))
                    _currentNote.Tags.Add(clean);
            }
            await App.Vault.UpdateNoteTagsAsync(_currentNote.Id, _currentNote.Tags);
            RenderNoteTagChips(_currentNote);
            TagList.ItemsSource = await App.Vault.GetTagsAsync();
            await RefreshNoteListAsync(_currentNote.Id);
        }
    }

    private async Task RefreshInspectorAsync(NoteItem note, string content)
    {
        if (note.DocFormat == DocumentFormat.Markdown && !note.IsEncrypted)
        {
            var analysis = MarkdownAnalyzer.Analyze(note.Id, content);
            OutlineList.ItemsSource = analysis.Headings.Select(h => new OutlineRow(h)).ToList();
        }
        else
        {
            OutlineList.ItemsSource = Array.Empty<OutlineRow>();
        }

        var backlinks = await App.Vault.GetBacklinksAsync(note.Id, note.Title);
        BacklinksList.ItemsSource = backlinks;
    }

    private async Task RefreshAttachmentsAsync(string noteId)
    {
        AttachmentsList.ItemsSource = await App.Vault.GetAttachmentsAsync(noteId);
    }

    private async Task OpenOrCreateWikiLinkAsync(string targetTitle)
    {
        var existing = await App.Vault.GetNoteByTitleAsync(targetTitle);
        if (existing is null)
        {
            existing = await App.Vault.CreateNoteAsync(
                targetTitle,
                DocumentFormat.Markdown,
                _currentNote?.CategoryId,
                "", // Clean empty body — no duplicated title
                backgroundColor: _settings.DefaultBackgroundColor,
                backgroundStyle: _settings.DefaultBackgroundStyle);
        }
        _quickView = "all";
        _selectedCategoryId = null;
        _selectedTagName = null;
        await RefreshAllListsAsync(selectNoteId: existing.Id);
    }

    // ==================== Creation Handlers (Clean Empty Body — No Duplicated Title!) ====================
    private async void NewDefaultNote_Click(SplitButton sender, SplitButtonClickEventArgs args) =>
        await CreateNoteInteractiveAsync(_settings.DefaultNewNoteFormat);

    private async void NewMarkdownNote_Click(object sender, RoutedEventArgs e) =>
        await CreateNoteInteractiveAsync(DocumentFormat.Markdown);

    private async void NewRichNote_Click(object sender, RoutedEventArgs e) =>
        await CreateNoteInteractiveAsync(DocumentFormat.RichHtml);

    private async Task CreateNoteInteractiveAsync(DocumentFormat format)
    {
        var title = format == DocumentFormat.Markdown
            ? $"新 Markdown 笔记 {DateTime.Now:MM-dd HH:mm}"
            : $"新富文本笔记 {DateTime.Now:MM-dd HH:mm}";

        // Clean blank body — does NOT duplicate title inside document body
        var note = await App.Vault.CreateNoteAsync(
            title,
            format,
            _selectedCategoryId,
            initialContent: "",
            backgroundColor: _settings.DefaultBackgroundColor,
            backgroundStyle: _settings.DefaultBackgroundStyle);

        _quickView = "all";
        await RefreshAllListsAsync(selectNoteId: note.Id);
    }

    private async void NewStickyNote_Click(object sender, RoutedEventArgs e)
    {
        var note = await App.Vault.CreateNoteAsync(
            $"便签 {DateTime.Now:MM-dd HH:mm}",
            DocumentFormat.Markdown,
            _selectedCategoryId,
            "",
            backgroundColor: "sticky_yellow",
            isSticky: true);
        var win = new StickyNoteWindow(note, "", () => DispatcherQueue.TryEnqueue(async () => await RefreshNoteListAsync()));
        win.Activate();
        await RefreshAllListsAsync(selectNoteId: note.Id);
    }

    private async void OpenCurrentAsSticky_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsEncrypted || _currentNote.IsDeleted) return;
        var content = await App.Vault.ReadNoteContentAsync(_currentNote, _currentUnlockPassword);
        var win = new StickyNoteWindow(_currentNote, content, () => DispatcherQueue.TryEnqueue(async () => await RefreshNoteListAsync()));
        win.Activate();
    }

    // ==================== Category / Folder Full Management (Add, Subfolder, Rename, Delete ANY folder) ====================
    private async void AddCategory_Click(object sender, RoutedEventArgs e) => await PromptCreateCategoryAsync(null);

    private async void AddSubCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: CategoryRow row })
            await PromptCreateCategoryAsync(row.Id);
    }

    private async Task PromptCreateCategoryAsync(string? parentId)
    {
        var box = new TextBox { PlaceholderText = "输入新笔记本名称..." };
        var dlg = new ContentDialog
        {
            Title = parentId is null ? Strings.T("tip_new_notebook") : Strings.T("ctx_new_subcat"),
            Content = box,
            PrimaryButtonText = Strings.T("dlg_ok"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            await App.Vault.CreateCategoryAsync(box.Text, parentId);
            await RefreshAllListsAsync();
        }
    }

    private async void RenameCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CategoryRow row }) return;
        var box = new TextBox { Text = row.Name, PlaceholderText = "输入新名称..." };
        var dlg = new ContentDialog
        {
            Title = Strings.T("ctx_rename_cat"),
            Content = box,
            PrimaryButtonText = Strings.T("dlg_save"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            await App.Vault.RenameCategoryAsync(row.Id, box.Text);
            await RefreshAllListsAsync();
            StatusLeftText.Text = $"已重命名笔记本：{box.Text.Trim()}";
        }
    }

    private async void DeleteCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CategoryRow row }) return;
        var dlg = new ContentDialog
        {
            Title = Strings.T("ctx_delete_cat"),
            Content = $"确定要删除笔记本「{row.Name}」吗？\n删除后，该笔记本内的所有笔记将安全保留在「全部笔记」中，不会丢失数据。",
            PrimaryButtonText = Strings.T("dlg_ok"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            await App.Vault.DeleteCategoryAsync(row.Id);
            if (_selectedCategoryId == row.Id)
            {
                _selectedCategoryId = null;
                _quickView = "all";
            }
            await RefreshAllListsAsync();
            StatusLeftText.Text = $"已删除笔记本「{row.Name}」（笔记已保留在全部笔记中）";
        }
    }

    // ==================== Note Copy-To & Move-To Category ====================
    private async void CopyNoteTo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        var target = await PickTargetCategoryAsync(Strings.T("dlg_copy_title"), _currentNote.CategoryId);
        if (!target.Confirmed) return;

        try
        {
            var copied = await App.Vault.CopyNoteToCategoryAsync(_currentNote.Id, target.CategoryId);
            await RefreshAllListsAsync(selectNoteId: _currentNote.Id);
            StatusLeftText.Text = $"✓ 已复制「{copied.Title}」到「{target.DisplayName}」";
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void MoveNoteTo_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        var target = await PickTargetCategoryAsync(Strings.T("dlg_move_title"), _currentNote.CategoryId);
        if (!target.Confirmed) return;

        await App.Vault.MoveNoteToCategoryAsync(_currentNote.Id, target.CategoryId);
        _currentNote.CategoryId = target.CategoryId;
        await RefreshAllListsAsync(selectNoteId: _currentNote.Id);
        StatusLeftText.Text = $"✓ 已将「{_currentNote.Title}」移动到「{target.DisplayName}」";
    }

    private async Task<(bool Confirmed, string? CategoryId, string DisplayName)> PickTargetCategoryAsync(string dialogTitle, string? currentCategoryId)
    {
        var cats = await App.Vault.GetCategoriesAsync();
        var options = new List<(string? Id, string Name)> { (null, Strings.T("nav_all") + " (未分类)") };
        foreach (var row in BuildIndentedCategoryRows(cats))
            options.Add((row.Id, row.DisplayName));

        var list = new ListView
        {
            Height = 260,
            SelectionMode = ListViewSelectionMode.Single
        };
        foreach (var opt in options)
            list.Items.Add(new ListViewItem { Content = opt.Name, Tag = opt });

        var initialIdx = options.FindIndex(o => o.Id == currentCategoryId);
        list.SelectedIndex = initialIdx >= 0 ? initialIdx : 0;

        var dlg = new ContentDialog
        {
            Title = dialogTitle,
            Content = list,
            PrimaryButtonText = Strings.T("dlg_ok"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary &&
            list.SelectedItem is ListViewItem { Tag: ValueTuple<string?, string> chosen })
        {
            return (true, chosen.Item1, chosen.Item2);
        }
        return (false, null, "");
    }

    // ==================== 文档背景色（双主题特色预设）、纸张纹理与全局背景图 ====================
    private void BgColorFlyout_Opening(object? sender, object e)
    {
        RefreshBackgroundPresetSwatches();
    }

    /// <summary>按当前应用浅色/深色主题刷新文档背景色块与纸张纹理高亮状态。</summary>
    private void RefreshBackgroundPresetSwatches()
    {
        bool isDark = IsAppDarkTheme();
        BgPresetGrid.ItemsSource = BackgroundPreset.All
            .Select(p => new BackgroundSwatchRow(p.Id, p.GetDisplayName(isDark), p.GetHex(isDark), p.Description))
            .ToList();

        if (BgPresetHeaderText is not null)
        {
            BgPresetHeaderText.Text = isDark ? "文档背景色（深色特色预设）" : "文档背景色（浅色特色预设）";
        }

        var activeStyle = (_currentNote?.BackgroundStyle ?? _settings.DefaultBackgroundStyle).ToString();
        if (BgStyleButtonsPanel is not null)
        {
            var accentBrush = Application.Current?.Resources.TryGetValue("AccentFillColorDefaultBrush", out var b) == true && b is Brush ab
                ? ab
                : new SolidColorBrush(Microsoft.UI.Colors.DodgerBlue);

            foreach (var child in BgStyleButtonsPanel.Children)
            {
                if (child is Button btn && btn.Tag is string tag)
                {
                    bool selected = tag.Equals(activeStyle, StringComparison.OrdinalIgnoreCase);
                    btn.BorderBrush = selected ? accentBrush : (Brush)Application.Current!.Resources["ControlStrokeColorDefaultBrush"];
                    btn.BorderThickness = new Thickness(selected ? 1.5 : 1);
                    btn.FontWeight = selected ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
                }
            }
        }
    }

    private async void BgPresetGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is BackgroundSwatchRow swatch)
        {
            await ApplyNoteBackgroundAsync(swatch.Id, _currentNote?.BackgroundStyle ?? BackgroundStyle.Solid);
            RefreshBackgroundPresetSwatches();
        }
    }

    private async void BgStyle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string styleName } && Enum.TryParse<BackgroundStyle>(styleName, true, out var style))
        {
            await ApplyNoteBackgroundAsync(_currentNote?.BackgroundColor ?? _settings.DefaultBackgroundColor, style);
            RefreshBackgroundPresetSwatches();
        }
    }

    private async void ApplyCustomBgColor_Click(object sender, RoutedEventArgs e)
    {
        var c = CustomBgColorPicker.Color;
        var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        await ApplyNoteBackgroundAsync(hex, _currentNote?.BackgroundStyle ?? BackgroundStyle.Solid);
        BgColorFlyout.Hide();
    }

    private async Task ApplyNoteBackgroundAsync(string? colorIdOrHex, BackgroundStyle style)
    {
        if (_currentNote is null) return;
        _currentNote.BackgroundColor = colorIdOrHex;
        _currentNote.BackgroundStyle = style;
        await App.Vault.UpdateNoteMetadataAsync(_currentNote);

        var resolvedHex = BackgroundPreset.ResolveHex(colorIdOrHex, IsAppDarkTheme());
        BgColorSwatch.Background = new SolidColorBrush(StickyNoteWindow.ParseHexColor(resolvedHex));
        SendToEditor("editor.setBackground", new
        {
            bgHex = resolvedHex,
            bgStyle = style.ToString().ToLowerInvariant(),
            isLightBg = BackgroundPreset.IsLightBackground(resolvedHex)
        });
        StatusLeftText.Text = $"已更新当前笔记背景：{resolvedHex} ({style})";
    }

    private object BuildGlobalBgImagePayload() => new
    {
        url = string.IsNullOrWhiteSpace(_settings.GlobalBgImageSha256)
            ? null
            : $"https://vault.mknote.local/attachments/{_settings.GlobalBgImageSha256}",
        fit = _settings.GlobalBgImageFit ?? "cover",
        opacity = _settings.GlobalBgImageOpacity
    };

    private void PushGlobalBackgroundImageToEditor() =>
        SendToEditor("editor.setBackgroundImage", BuildGlobalBgImagePayload());

    private static string ExtractPrimaryFontName(string? cssFontChain)
    {
        var primary = cssFontChain?.Split(',')[0].Trim().Trim('"', '\'');
        return string.IsNullOrWhiteSpace(primary) || SystemFontService.IsSymbolOrIconFont(primary)
            ? "Microsoft YaHei UI"
            : primary;
    }

    private void PushSystemFontsToEditor() =>
        SendToEditor("editor.setSystemFonts", new
        {
            fonts = SystemFontService.GetInstalledFontFamilies(),
            currentFont = ExtractPrimaryFontName(_settings.EditorFontFamily),
            currentSize = _settings.EditorFontSize
        });

    // ==================== 知识图谱视图（支持按钮返回 / Esc 退出 / 再次点击切回） ====================
    private async void OpenGraphView_Click(object sender, RoutedEventArgs e)
    {
        if (_isGraphOpen)
        {
            await CloseGraphViewAsync();
            return;
        }

        _isGraphOpen = true;
        var graph = await App.Vault.BuildGraphAsync();
        var isDark = IsAppDarkTheme();
        var bgHex = isDark ? "#1E1E1E" : "#FFFFFF";
        NoteHeaderBar.Visibility = Visibility.Collapsed;
        NoteLockGate.Visibility = Visibility.Collapsed;
        TrashBlockedGate.Visibility = Visibility.Collapsed;
        EmptyStateGate.Visibility = Visibility.Collapsed;
        EditorWebView.Visibility = Visibility.Visible;
        SendToEditor("graph.loadData", new { graph, bgHex, isLightBg = !isDark });
        StatusLeftText.Text = $"{Strings.T("nav_graph")}：{graph.Nodes.Count} nodes · {graph.Edges.Count} edges";
    }

    private async Task CloseGraphViewAsync()
    {
        if (!_isGraphOpen) return;
        _isGraphOpen = false;
        if (_currentNote is not null)
        {
            await LoadNoteIntoEditorAsync(_currentNote);
            StatusLeftText.Text = Strings.T("status_encrypted");
        }
        else
        {
            ShowEmptyEditorState();
        }
    }

    private async void QuickNavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        if (QuickNavList.SelectedItem is not ListViewItem { Tag: string tag }) return;
        _quickView = tag;
        _selectedCategoryId = null;
        _selectedTagName = null;
        if (CategoryList is not null) CategoryList.SelectedItem = null;
        if (TagList is not null) TagList.SelectedItem = null;
        await RefreshNoteListAsync();
    }

    private async void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        if (CategoryList.SelectedItem is not CategoryRow row) return;
        _selectedCategoryId = row.Id;
        _selectedTagName = null;
        _quickView = "all";
        await RefreshNoteListAsync();
    }

    private async void TagList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_windowLoaded) return;
        if (TagList.SelectedItem is not TagItem tag) return;
        _selectedTagName = tag.Name;
        _selectedCategoryId = null;
        _quickView = "all";
        await RefreshNoteListAsync();
    }

    private async void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
            await RefreshNoteListAsync();
    }

    // ==================== Note Actions: Rename, Pin, Star, Delete, Revisions ====================
    private async void NoteTitleBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        var newTitle = NoteTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newTitle) || newTitle == _currentNote.Title) return;

        var updatedRefs = await App.Vault.RenameNoteWithLinkUpdateAsync(_currentNote.Id, newTitle);
        _currentNote.Title = newTitle;
        if (updatedRefs > 0)
            StatusLeftText.Text = $"已重命名并自动更新 {updatedRefs} 篇笔记中的 [[双向链接]]";
        await RefreshNoteListAsync(_currentNote.Id);
    }

    private async void PinNoteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        _currentNote.IsPinned = PinNoteToggle.IsChecked == true;
        await App.Vault.UpdateNoteMetadataAsync(_currentNote);
        await RefreshNoteListAsync(_currentNote.Id);
    }

    private async void StarNoteToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        _currentNote.IsStarred = StarNoteToggle.IsChecked == true;
        await App.Vault.UpdateNoteMetadataAsync(_currentNote);
        await RefreshNoteListAsync(_currentNote.Id);
    }

    private async void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null) return;
        var idToDelete = _currentNote.Id;
        var wasInTrash = _currentNote.IsDeleted;

        // Immediately wipe canvas so deleted content never lingers on screen
        ShowEmptyEditorState();

        if (wasInTrash)
            await App.Vault.PermanentlyDeleteNoteAsync(idToDelete);
        else
            await App.Vault.SoftDeleteNoteAsync(idToDelete);

        await RefreshAllListsAsync(selectNoteId: null);
    }

    private async void RestoreNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null) return;
        var id = _currentNote.Id;
        await App.Vault.RestoreNoteAsync(id);
        _quickView = "all";
        await RefreshAllListsAsync(selectNoteId: id);
    }

    private async void EmptyTrash_Click(object sender, RoutedEventArgs e)
    {
        ShowEmptyEditorState();
        await App.Vault.EmptyTrashAsync();
        await RefreshAllListsAsync(selectNoteId: null);
    }

    private async void ShowRevisions_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsEncrypted || _currentNote.IsDeleted) return;
        var revs = await App.Vault.GetRevisionsAsync(_currentNote.Id);
        var list = new ListView
        {
            ItemsSource = revs,
            Height = 280,
            DisplayMemberPath = nameof(NoteRevisionItem.CreatedAt)
        };
        var dlg = new ContentDialog
        {
            Title = $"{Strings.T("menu_history")} ({revs.Count})",
            Content = list,
            PrimaryButtonText = Strings.T("btn_restore_note"),
            CloseButtonText = Strings.T("dlg_close"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary && list.SelectedItem is NoteRevisionItem selected)
        {
            var oldContent = await App.Vault.ReadRevisionContentAsync(selected.Id);
            await App.Vault.SaveNoteContentAsync(_currentNote, oldContent, createRevision: true, revisionReason: "restored");
            await LoadNoteIntoEditorAsync(_currentNote);
        }
    }

    // ==================== Sync, Vim Guide, Markdown Guide & Preferences ====================
    private void OpenSyncWindow_Click(object sender, RoutedEventArgs e)
    {
        var win = new SyncWindow(
            _settings,
            ShellRoot.ActualTheme,
            async syncCfg =>
            {
                _settings.Sync = syncCfg;
                await App.Vault.SaveSettingsAsync(_settings);
                ConfigureAutoSyncTimer();
            },
            async () =>
            {
                await RefreshAllListsAsync();
            });
        win.Activate();
    }

    private void ConfigureAutoSyncTimer()
    {
        _autoSyncTimer?.Stop();
        _autoSyncTimer = null;

        if (_settings.Sync is null || _settings.Sync.AutoSyncMinutes <= 0 || _settings.Sync.Provider == "None")
            return;

        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(_settings.Sync.AutoSyncMinutes) };
        _autoSyncTimer.Tick += async (_, _) =>
        {
            if (_syncRunning || string.IsNullOrWhiteSpace(_settings.Sync.EndpointUrl)) return;
            _syncRunning = true;
            try
            {
                var svc = new EncryptedSyncService();
                var pwd = App.Vault.UnprotectSecret(_settings.Sync.ProtectedPasswordBase64);
                var e2ee = App.Vault.UnprotectSecret(_settings.Sync.ProtectedE2eePassphraseBase64);
                var syncSnapshot = _settings.Sync;
                var report = await Task.Run(() => svc.SynchronizeAsync(App.Vault, syncSnapshot, pwd, e2ee));
                _settings.Sync.LastSyncTimeUtc = report.CompletedAtUtc;
                _settings.Sync.LastSyncSummary = $"+{report.UploadedNotes}/↓{report.DownloadedNotes}"
                    + (report.DeletedRemoteNotes + report.DeletedLocalNotes > 0
                        ? $"/🗑{report.DeletedRemoteNotes + report.DeletedLocalNotes}"
                        : "");
                await App.Vault.SaveSettingsAsync(_settings);
                if (report.DownloadedNotes > 0 || report.SyncedCategories > 0 || report.DeletedLocalNotes > 0)
                    await RefreshAllListsAsync();
                StatusLeftText.Text = $"🔄 自动加密同步完成 ({DateTime.Now:HH:mm}) · {_settings.Sync.LastSyncSummary}";
            }
            catch { }
            finally
            {
                _syncRunning = false;
            }
        };
        _autoSyncTimer.Start();
    }

    private void OpenVimShortcuts_Click(object sender, RoutedEventArgs e)
    {
        var win = new VimShortcutsWindow(_settings, ShellRoot.ActualTheme, async (enableVim, customMappings) =>
        {
            _settings.EnableVimMode = enableVim;
            _settings.CustomVimMappings = customMappings;
            await App.Vault.SaveSettingsAsync(_settings);
            PushVimConfigToEditor();
            StatusLeftText.Text = enableVim
                ? $"已保存 {customMappings.Count} 条自定义 Vim 快捷键并立即生效"
                : "已保存 Vim 快捷键配置";
        });
        win.Activate();
    }

    private void OpenMarkdownGuide_Click(object sender, RoutedEventArgs e)
    {
        var win = new MarkdownGuideWindow(_settings, ShellRoot.ActualTheme, async customMdShortcuts =>
        {
            _settings.CustomMarkdownShortcuts = customMdShortcuts;
            await App.Vault.SaveSettingsAsync(_settings);
            PushMarkdownShortcutsToEditor();
            StatusLeftText.Text = $"已保存 Markdown 快捷键设置（{customMdShortcuts.Count} 条自定义）";
        });
        win.Activate();
    }

    private async void OpenPreferences_Click(object sender, RoutedEventArgs e)
    {
        var systemFonts = SystemFontService.GetInstalledFontFamilies();

        // 1. Language Switcher (zh-CN / en-US / ja-JP)
        var langCombo = new ComboBox { Header = Strings.T("set_language"), HorizontalAlignment = HorizontalAlignment.Stretch };
        langCombo.Items.Add(new ComboBoxItem { Tag = "zh-CN", Content = "简体中文 (Chinese)" });
        langCombo.Items.Add(new ComboBoxItem { Tag = "en-US", Content = "English" });
        langCombo.Items.Add(new ComboBoxItem { Tag = "ja-JP", Content = "日本語 (Japanese)" });
        langCombo.SelectedIndex = _settings.Language switch { "en-US" => 1, "ja-JP" => 2, _ => 0 };

        // 2. Separate System Fonts for UI & Document Editor
        var uiFontCombo = new ComboBox { Header = Strings.T("set_ui_font"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = systemFonts };
        uiFontCombo.SelectedItem = systemFonts.FirstOrDefault(f => f.Equals(_settings.UiFontFamily, StringComparison.OrdinalIgnoreCase)) ?? systemFonts[0];

        var currentEditorFont = _settings.EditorFontFamily.Split(',')[0].Trim().Trim('"', '\'');
        var editorFontCombo = new ComboBox { Header = Strings.T("set_editor_font"), HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = systemFonts };
        editorFontCombo.SelectedItem = systemFonts.FirstOrDefault(f => f.Equals(currentEditorFont, StringComparison.OrdinalIgnoreCase)) ?? systemFonts[0];

        var fontSizeBox = new NumberBox
        {
            Header = Strings.T("set_font_size"),
            Value = _settings.EditorFontSize,
            Minimum = 11,
            Maximum = 28
        };

        // 3. Global Encrypted Background Image + Fit Mode + Opacity
        string? pendingBgSha = _settings.GlobalBgImageSha256;
        var bgImgStatus = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(pendingBgSha) ? "当前未设置全局背景图" : $"已设置加密背景图 ({pendingBgSha[..8]}…)",
            FontSize = 11.5,
            Opacity = 0.72
        };
        var chooseBgBtn = new Button { Content = Strings.T("set_bg_choose") };
        var clearBgBtn = new Button { Content = Strings.T("set_bg_clear") };
        chooseBgBtn.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker();
            InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add(".png");
            picker.FileTypeFilter.Add(".jpg");
            picker.FileTypeFilter.Add(".jpeg");
            picker.FileTypeFilter.Add(".webp");
            picker.FileTypeFilter.Add(".bmp");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var bytes = await File.ReadAllBytesAsync(file.Path);
            pendingBgSha = await App.Vault.SaveGlobalMediaBlobAsync(file.Name, GuessMimeType(file.Path), bytes);
            bgImgStatus.Text = $"已加密载入：{file.Name}";
        };
        clearBgBtn.Click += (_, _) =>
        {
            pendingBgSha = null;
            bgImgStatus.Text = "已清除全局背景图";
        };
        var bgBtnRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        bgBtnRow.Children.Add(chooseBgBtn);
        bgBtnRow.Children.Add(clearBgBtn);

        var bgFitCombo = new ComboBox { Header = Strings.T("set_bg_fit"), HorizontalAlignment = HorizontalAlignment.Stretch };
        bgFitCombo.Items.Add(new ComboBoxItem { Tag = "cover", Content = Strings.T("fit_cover") });
        bgFitCombo.Items.Add(new ComboBoxItem { Tag = "stretch", Content = Strings.T("fit_stretch") });
        bgFitCombo.Items.Add(new ComboBoxItem { Tag = "contain", Content = Strings.T("fit_contain") });
        bgFitCombo.Items.Add(new ComboBoxItem { Tag = "center", Content = Strings.T("fit_center") });
        bgFitCombo.Items.Add(new ComboBoxItem { Tag = "repeat", Content = Strings.T("fit_repeat") });
        bgFitCombo.SelectedIndex = _settings.GlobalBgImageFit switch
        {
            "stretch" => 1,
            "contain" => 2,
            "center" => 3,
            "repeat" => 4,
            _ => 0
        };

        var bgOpacitySlider = new Slider
        {
            Header = Strings.T("set_bg_opacity"),
            Minimum = 5,
            Maximum = 100,
            Value = Math.Clamp((int)Math.Round(_settings.GlobalBgImageOpacity * 100), 5, 100)
        };

        // 4. Startup, Tray & Vim
        var autoStartToggle = new ToggleSwitch { Header = Strings.T("set_autostart"), IsOn = AutoStartService.IsEnabled() };
        var closeToTrayToggle = new ToggleSwitch { Header = Strings.T("set_close_tray"), IsOn = _settings.CloseToTray };
        var lockOnTrayToggle = new ToggleSwitch { Header = "隐藏到托盘时自动锁定保险库", IsOn = _settings.LockOnMinimize };
        var vimToggle = new ToggleSwitch { Header = Strings.T("set_vim"), IsOn = _settings.EnableVimMode };
        var jkToggle = new ToggleSwitch { Header = Strings.T("set_vim_jk"), IsOn = _settings.VimJkEscape };
        var relLineToggle = new ToggleSwitch { Header = Strings.T("set_vim_rel"), IsOn = _settings.VimRelativeLineNumbers };

        var defaultFmtCombo = new ComboBox { Header = Strings.T("set_default_fmt"), HorizontalAlignment = HorizontalAlignment.Stretch };
        defaultFmtCombo.Items.Add("Markdown");
        defaultFmtCombo.Items.Add(Strings.T("nav_rich"));
        defaultFmtCombo.SelectedIndex = _settings.DefaultNewNoteFormat == DocumentFormat.Markdown ? 0 : 1;

        var defaultMdModeCombo = new ComboBox { Header = "Markdown 默认视图", HorizontalAlignment = HorizontalAlignment.Stretch };
        defaultMdModeCombo.Items.Add(Strings.T("ed_live_preview"));
        defaultMdModeCombo.Items.Add(Strings.T("ed_split_preview"));
        defaultMdModeCombo.Items.Add(Strings.T("ed_reading"));
        defaultMdModeCombo.SelectedIndex = _settings.DefaultMarkdownViewMode switch
        {
            MarkdownViewMode.SplitPreview => 1,
            MarkdownViewMode.Reading => 2,
            _ => 0
        };

        var sp = new StackPanel { Spacing = 10, Width = 430 };
        sp.Children.Add(langCombo);
        sp.Children.Add(uiFontCombo);
        sp.Children.Add(editorFontCombo);
        sp.Children.Add(fontSizeBox);
        sp.Children.Add(new TextBlock { Text = Strings.T("set_bg_image"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 0) });
        sp.Children.Add(bgBtnRow);
        sp.Children.Add(bgImgStatus);
        sp.Children.Add(bgFitCombo);
        sp.Children.Add(bgOpacitySlider);
        sp.Children.Add(defaultFmtCombo);
        sp.Children.Add(defaultMdModeCombo);
        sp.Children.Add(vimToggle);
        sp.Children.Add(jkToggle);
        sp.Children.Add(relLineToggle);
        sp.Children.Add(autoStartToggle);
        sp.Children.Add(closeToTrayToggle);
        sp.Children.Add(lockOnTrayToggle);

        var dlg = new ContentDialog
        {
            Title = "⚙️ " + Strings.T("dlg_settings_title"),
            Content = new ScrollViewer { Content = sp, MaxHeight = 520 },
            PrimaryButtonText = Strings.T("dlg_save"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                AutoStartService.SetEnabled(autoStartToggle.IsOn);
                _settings.AutoStartWithWindows = autoStartToggle.IsOn;
            }
            catch (Exception ex)
            {
                StatusLeftText.Text = $"AutoStart Error: {ex.Message}";
            }

            _settings.Language = (langCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "zh-CN";
            var selUiFont = uiFontCombo.SelectedItem as string ?? "Microsoft YaHei UI";
            _settings.UiFontFamily = SystemFontService.IsSymbolOrIconFont(selUiFont) ? "Microsoft YaHei UI" : selUiFont;
            var selEditorFont = editorFontCombo.SelectedItem as string ?? "Microsoft YaHei UI";
            _settings.EditorFontFamily = SystemFontService.BuildSafeEditorCssFontChain(selEditorFont);
            _settings.EditorFontSize = double.IsNaN(fontSizeBox.Value) ? 15 : (int)fontSizeBox.Value;

            _settings.GlobalBgImageSha256 = pendingBgSha;
            _settings.GlobalBgImageFit = (bgFitCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "cover";
            _settings.GlobalBgImageOpacity = Math.Clamp(bgOpacitySlider.Value / 100.0, 0.05, 1.0);

            _settings.CloseToTray = closeToTrayToggle.IsOn;
            _settings.LockOnMinimize = lockOnTrayToggle.IsOn;
            _settings.EnableVimMode = vimToggle.IsOn;
            _settings.VimJkEscape = jkToggle.IsOn;
            _settings.VimRelativeLineNumbers = relLineToggle.IsOn;
            _settings.DefaultNewNoteFormat = defaultFmtCombo.SelectedIndex == 0 ? DocumentFormat.Markdown : DocumentFormat.RichHtml;
            _settings.DefaultMarkdownViewMode = defaultMdModeCombo.SelectedIndex switch
            {
                1 => MarkdownViewMode.SplitPreview,
                2 => MarkdownViewMode.Reading,
                _ => MarkdownViewMode.LivePreview
            };

            await App.Vault.SaveSettingsAsync(_settings);
            ApplyUiFont(_settings.UiFontFamily);
            ApplyLocalization();
            PushSystemFontsToEditor();
            PushGlobalBackgroundImageToEditor();
            PushVimConfigToEditor();
            if (_currentNote is not null && !_currentNote.IsDeleted)
                await LoadNoteIntoEditorAsync(_currentNote);
            await RefreshNoteListAsync(_currentNote?.Id);
            StatusLeftText.Text = $"✓ {Strings.T("dlg_save")}";
        }
    }

    private async void QuickToggleVim_Click(object sender, RoutedEventArgs e)
    {
        _settings.EnableVimMode = !_settings.EnableVimMode;
        await App.Vault.SaveSettingsAsync(_settings);
        PushVimConfigToEditor();
    }

    private object BuildVimConfigPayload()
    {
        var merged = VimShortcutCatalog.MergeWithSaved(_settings.CustomVimMappings);
        var activeCustom = merged
            .Where(m => m.Editable && !string.IsNullOrWhiteSpace(m.CustomKeys) && !string.IsNullOrWhiteSpace(m.ActionTarget))
            .Select(m => new { id = m.Id, mode = m.Mode, lhs = m.CustomKeys.Trim(), rhs = m.ActionTarget.Trim() })
            .ToList();

        return new
        {
            enabled = _settings.EnableVimMode,
            jkEscape = _settings.VimJkEscape,
            relativeLineNumbers = _settings.VimRelativeLineNumbers,
            customMappings = activeCustom
        };
    }

    private object BuildMarkdownShortcutsPayload()
    {
        var merged = MarkdownShortcutCatalog.MergeWithSaved(_settings.CustomMarkdownShortcuts);
        return merged
            .Where(m => !string.IsNullOrWhiteSpace(m.CommandAction) && !string.IsNullOrWhiteSpace(m.EffectiveShortcut))
            .Select(m => new { id = m.Id, action = m.CommandAction, shortcut = m.EffectiveShortcut })
            .ToList();
    }

    private void PushVimConfigToEditor()
    {
        UpdateVimQuickButtonLabel();
        SendToEditor("editor.setVimConfig", BuildVimConfigPayload());
    }

    private void PushMarkdownShortcutsToEditor() =>
        SendToEditor("editor.setMarkdownShortcuts", new { shortcuts = BuildMarkdownShortcutsPayload() });

    private void UpdateVimQuickButtonLabel()
    {
        VimModeQuickBtn.Content = _settings.EnableVimMode
            ? "VIM [NORMAL]"
            : (_settings.Language == "en-US" ? "Normal Mode" : _settings.Language == "ja-JP" ? "通常モード" : "普通模式");
    }

    // ==================== 三栏可拖动调宽与持久化记忆 ====================
    private void InitializePaneSplitters()
    {
        // 从本地加密配置恢复上次拖动保存的侧边栏宽度
        var navW = Math.Clamp(_settings.SidebarWidth, 150, 460);
        var listW = Math.Clamp(_settings.NoteListWidth, 210, 560);
        _settings.SidebarWidth = navW;
        _settings.NoteListWidth = listW;
        _settings.InspectorWidth = Math.Clamp(_settings.InspectorWidth, 200, 500);

        NavCol.Width = new GridLength(navW);
        ListCol.Width = new GridLength(listW);

        // 1. 左侧导航栏分隔条（向右拉宽、向左收窄，双击恢复 224px）
        NavSplitter.DragDelta += delta =>
        {
            if (!_sidebarVisible) return;
            var maxAllowed = Math.Max(180, WorkspaceGrid.ActualWidth - ListCol.ActualWidth - InspectorCol.ActualWidth - 340);
            var next = Math.Clamp(NavCol.Width.Value + delta, 150, Math.Min(460, maxAllowed));
            NavCol.Width = new GridLength(next);
            _settings.SidebarWidth = next;
        };
        NavSplitter.DragCompleted += async () => await App.Vault.SaveSettingsAsync(_settings);
        NavSplitter.ResetRequested += async () =>
        {
            _settings.SidebarWidth = 224;
            NavCol.Width = new GridLength(224);
            await App.Vault.SaveSettingsAsync(_settings);
        };

        // 2. 中间笔记列表分隔条（向右拉宽、向左收窄，双击恢复 290px）
        ListSplitter.DragDelta += delta =>
        {
            var maxAllowed = Math.Max(220, WorkspaceGrid.ActualWidth - NavCol.ActualWidth - InspectorCol.ActualWidth - 340);
            var next = Math.Clamp(ListCol.Width.Value + delta, 210, Math.Min(560, maxAllowed));
            ListCol.Width = new GridLength(next);
            _settings.NoteListWidth = next;
        };
        ListSplitter.DragCompleted += async () => await App.Vault.SaveSettingsAsync(_settings);
        ListSplitter.ResetRequested += async () =>
        {
            _settings.NoteListWidth = 290;
            ListCol.Width = new GridLength(290);
            await App.Vault.SaveSettingsAsync(_settings);
        };

        // 3. 右侧大纲/反链/附件抽屉分隔条（向左拉宽、向右收窄，双击恢复 260px）
        InspectorSplitter.DragDelta += delta =>
        {
            if (InspectorPane.Visibility != Visibility.Visible) return;
            var maxAllowed = Math.Max(200, WorkspaceGrid.ActualWidth - NavCol.ActualWidth - ListCol.ActualWidth - 340);
            var next = Math.Clamp(InspectorCol.Width.Value - delta, 190, Math.Min(500, maxAllowed));
            InspectorCol.Width = new GridLength(next);
            _settings.InspectorWidth = next;
        };
        InspectorSplitter.DragCompleted += async () => await App.Vault.SaveSettingsAsync(_settings);
        InspectorSplitter.ResetRequested += async () =>
        {
            _settings.InspectorWidth = 260;
            if (InspectorPane.Visibility == Visibility.Visible)
                InspectorCol.Width = new GridLength(260);
            await App.Vault.SaveSettingsAsync(_settings);
        };
    }

    private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
    {
        _sidebarVisible = !_sidebarVisible;
        NavPane.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        NavSplitter.Visibility = _sidebarVisible ? Visibility.Visible : Visibility.Collapsed;
        NavCol.Width = _sidebarVisible ? new GridLength(Math.Clamp(_settings.SidebarWidth, 150, 460)) : new GridLength(0);
        NavSplitterCol.Width = _sidebarVisible ? new GridLength(6) : new GridLength(0);
    }

    private async void FormatFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag }) return;
        _formatFilter = tag switch
        {
            "md" => DocumentFormat.Markdown,
            "rich" => DocumentFormat.RichHtml,
            _ => null
        };
        UpdateFormatFilterStyles();
        await RefreshNoteListAsync();
    }

    private void UpdateFormatFilterStyles()
    {
        var activeBg = (Brush)Application.Current.Resources["ControlFillColorSecondaryBrush"];
        var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        void ApplyStyle(Button btn, bool active)
        {
            btn.Background = active ? activeBg : transparent;
            btn.FontWeight = active ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
            btn.Opacity = active ? 1.0 : 0.72;
        }

        ApplyStyle(FilterAllBtn, _formatFilter is null);
        ApplyStyle(FilterMdBtn, _formatFilter == DocumentFormat.Markdown);
        ApplyStyle(FilterRichBtn, _formatFilter == DocumentFormat.RichHtml);
    }

    private void ShellRoot_KeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape && _isGraphOpen)
        {
            e.Handled = true;
            _ = CloseGraphViewAsync();
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape && _isMultiSelectMode)
        {
            e.Handled = true;
            SetMultiSelectMode(false);
            return;
        }

        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl) return;

        if (shift && e.Key == Windows.System.VirtualKey.A)
        {
            e.Handled = true;
            _ = CaptureScreenCoreAsync();
            return;
        }

        if (e.Key == Windows.System.VirtualKey.K)
        {
            SearchBox.Focus(FocusState.Programmatic);
            e.Handled = true;
            return;
        }

        if (_settings.EnableVimMode && NoteListView.Items.Count > 0)
        {
            if (e.Key == Windows.System.VirtualKey.J)
            {
                NoteListView.SelectedIndex = Math.Min(NoteListView.SelectedIndex + 1, NoteListView.Items.Count - 1);
                e.Handled = true;
            }
            else if (e.Key == Windows.System.VirtualKey.P)
            {
                NoteListView.SelectedIndex = Math.Max(NoteListView.SelectedIndex - 1, 0);
                e.Handled = true;
            }
        }
    }

    // ==================== Per-Note AES-256-GCM Encryption & Local App Lock ====================
    private async void ToggleNoteEncryption_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;

        if (!_currentNote.IsEncrypted)
        {
            var pwdBox = new PasswordBox { PlaceholderText = "设置该笔记的独立加密密码" };
            var hintBox = new TextBox { PlaceholderText = "密码提示（可选）" };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(pwdBox);
            panel.Children.Add(hintBox);

            var dlg = new ContentDialog
            {
                Title = "🔒 AES-256-GCM",
                Content = panel,
                PrimaryButtonText = Strings.T("dlg_ok"),
                CloseButtonText = Strings.T("dlg_cancel"),
                XamlRoot = ShellRoot.XamlRoot
            };

            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(pwdBox.Password))
            {
                var plain = await App.Vault.ReadNoteContentAsync(_currentNote);
                await App.Vault.SetNoteEncryptionAsync(_currentNote.Id, plain, pwdBox.Password, hintBox.Text);
                _unlockedSessionPasswords.Remove(_currentNote.Id);
                _currentUnlockPassword = null;
                await RefreshAllListsAsync(_currentNote.Id);
            }
        }
        else
        {
            var pwdBox = new PasswordBox { PlaceholderText = "输入当前密码以解除加密" };
            var dlg = new ContentDialog
            {
                Title = "🔓 Decrypt",
                Content = pwdBox,
                PrimaryButtonText = Strings.T("dlg_ok"),
                CloseButtonText = Strings.T("dlg_cancel"),
                XamlRoot = ShellRoot.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(pwdBox.Password))
            {
                try
                {
                    await App.Vault.RemoveNoteEncryptionAsync(_currentNote.Id, pwdBox.Password);
                    _unlockedSessionPasswords.Remove(_currentNote.Id);
                    _currentUnlockPassword = null;
                    await RefreshAllListsAsync(_currentNote.Id);
                }
                catch (Exception ex)
                {
                    StatusLeftText.Text = ex.Message;
                }
            }
        }
    }

    private async void UnlockEncryptedNote_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null) return;
        try
        {
            _ = await App.Vault.ReadNoteContentAsync(_currentNote, NoteUnlockPwdBox.Password);
            _currentUnlockPassword = NoteUnlockPwdBox.Password;
            _unlockedSessionPasswords[_currentNote.Id] = _currentUnlockPassword;
            await LoadNoteIntoEditorAsync(_currentNote);
        }
        catch (Exception ex)
        {
            NoteUnlockErrorText.Text = ex.Message;
        }
    }

    private async void ConfigureAppLock_Click(object sender, RoutedEventArgs e)
    {
        var enableToggle = new ToggleSwitch { Header = Strings.T("set_app_lock"), IsOn = _settings.EnableAppLock };
        var helloToggle = new ToggleSwitch { Header = Strings.T("set_win_hello"), IsOn = _settings.UseWindowsHello };
        var pwdBox = new PasswordBox { PlaceholderText = Strings.T("set_lock_pwd") };
        var hintBox = new TextBox { PlaceholderText = "Hint", Text = _settings.AppLockPasswordHint ?? "" };
        var idleBox = new NumberBox { Header = "Idle Auto-Lock (min, 0=Off)", Value = _settings.AutoLockIdleMinutes, Minimum = 0, Maximum = 120 };

        var sp = new StackPanel { Spacing = 8, Width = 340 };
        sp.Children.Add(enableToggle);
        sp.Children.Add(helloToggle);
        sp.Children.Add(pwdBox);
        sp.Children.Add(hintBox);
        sp.Children.Add(idleBox);

        var dlg = new ContentDialog
        {
            Title = "🛡️ App Lock",
            Content = sp,
            PrimaryButtonText = Strings.T("dlg_save"),
            CloseButtonText = Strings.T("dlg_cancel"),
            XamlRoot = ShellRoot.XamlRoot
        };

        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            _settings.EnableAppLock = enableToggle.IsOn;
            _settings.UseWindowsHello = helloToggle.IsOn;
            _settings.AppLockPasswordHint = hintBox.Text;
            _settings.AutoLockIdleMinutes = double.IsNaN(idleBox.Value) ? 0 : (int)idleBox.Value;
            if (!string.IsNullOrWhiteSpace(pwdBox.Password))
            {
                var (hash, salt) = NoteCryptoService.HashAppLockPassword(pwdBox.Password);
                _settings.AppLockPasswordHash = hash;
                _settings.AppLockPasswordSalt = salt;
            }
            await App.Vault.SaveSettingsAsync(_settings);
            StatusLeftText.Text = "✓ App Lock Saved";
        }
    }

    private void LockAppNow_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_settings.AppLockPasswordHash))
        {
            ConfigureAppLock_Click(sender, e);
            return;
        }
        ShowAppLockOverlay();
    }

    private async void ShowAppLockOverlay()
    {
        _unlockedSessionPasswords.Clear();
        _currentUnlockPassword = null;
        AppLockPwdBox.Password = "";
        AppLockErrText.Text = "";
        AppLockHintText.Text = string.IsNullOrWhiteSpace(_settings.AppLockPasswordHint)
            ? Strings.T("status_encrypted")
            : _settings.AppLockPasswordHint;
        HelloUnlockBtn.Visibility = _settings.UseWindowsHello && await WindowsHelloHelper.IsAvailableAsync()
            ? Visibility.Visible
            : Visibility.Collapsed;
        AppLockOverlay.Visibility = Visibility.Visible;
    }

    private void UnlockApp_Click(object sender, RoutedEventArgs e)
    {
        if (NoteCryptoService.VerifyAppLockPassword(AppLockPwdBox.Password, _settings.AppLockPasswordHash, _settings.AppLockPasswordSalt))
        {
            AppLockOverlay.Visibility = Visibility.Collapsed;
            AppLockPwdBox.Password = "";
        }
        else
        {
            AppLockErrText.Text = "Invalid password";
        }
    }

    private async void UnlockAppWithHello_Click(object sender, RoutedEventArgs e)
    {
        if (await WindowsHelloHelper.RequestUnlockAsync())
            AppLockOverlay.Visibility = Visibility.Collapsed;
        else
            AppLockErrText.Text = "Windows Hello failed";
    }

    private void StartIdleLockMonitor()
    {
        _idleLockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _idleLockTimer.Tick += (_, _) =>
        {
            if (_settings.EnableAppLock &&
                _settings.AutoLockIdleMinutes > 0 &&
                !string.IsNullOrEmpty(_settings.AppLockPasswordHash) &&
                AppLockOverlay.Visibility == Visibility.Collapsed &&
                Win32Helper.GetSystemIdleTime().TotalMinutes >= _settings.AutoLockIdleMinutes)
            {
                ShowAppLockOverlay();
            }
        };
        _idleLockTimer.Start();
    }

    // ==================== Screen Capture & Encrypted Image Insertion ====================
    private async void CaptureScreen_Click(object sender, RoutedEventArgs e) => await CaptureScreenCoreAsync();
    private async void InsertLocalImage_Click(object sender, RoutedEventArgs e) => await InsertLocalImagesCoreAsync();

    private async Task CaptureScreenCoreAsync()
    {
        if (_currentNote is null || _currentNote.IsDeleted)
        {
            await CreateNoteInteractiveAsync(_settings.DefaultNewNoteFormat);
            if (_currentNote is null) return;
        }
        if (_currentNote.IsEncrypted && string.IsNullOrEmpty(_currentUnlockPassword)) return;

        try
        {
            AppWindow.Hide();
            await Task.Delay(220);

            var screenBuf = ScreenCaptureService.CaptureVirtualScreen();
            var overlay = new ScreenCaptureWindow(screenBuf);
            var pngBytes = await overlay.CaptureAsync();

            AppWindow.Show();
            BringToFront();

            if (pngBytes is null || pngBytes.Length == 0) return;

            var fileName = $"screenshot_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var att = await App.Vault.SaveAttachmentAsync(_currentNote.Id, fileName, "image/png", pngBytes, isInline: true);
            var secureUrl = $"https://vault.mknote.local/attachments/{att.Sha256}";

            SendToEditor("editor.insertMedia", new { url = secureUrl, fileName = att.FileName, isImage = true });
            await RefreshAttachmentsAsync(_currentNote.Id);
            StatusLeftText.Text = $"📷 Encrypted Screenshot Inserted ({pngBytes.Length / 1024} KB)";
        }
        catch (Exception ex)
        {
            AppWindow.Show();
            BringToFront();
            StatusLeftText.Text = $"Capture Error: {ex.Message}";
        }
    }

    private async Task InsertLocalImagesCoreAsync()
    {
        if (_currentNote is null || _currentNote.IsDeleted)
        {
            await CreateNoteInteractiveAsync(_settings.DefaultNewNoteFormat);
            if (_currentNote is null) return;
        }
        if (_currentNote.IsEncrypted && string.IsNullOrEmpty(_currentUnlockPassword)) return;

        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp" })
            picker.FileTypeFilter.Add(ext);

        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        foreach (var file in files)
        {
            var bytes = await File.ReadAllBytesAsync(file.Path);
            var mime = GuessMimeType(file.Path);
            var att = await App.Vault.SaveAttachmentAsync(_currentNote.Id, file.Name, mime, bytes, isInline: true);
            var secureUrl = $"https://vault.mknote.local/attachments/{att.Sha256}";
            SendToEditor("editor.insertMedia", new { url = secureUrl, fileName = att.FileName, isImage = true });
        }

        await RefreshAttachmentsAsync(_currentNote.Id);
        StatusLeftText.Text = $"🖼️ Inserted {files.Count} encrypted image(s)";
    }

    // ==================== Inspector & Attachments ====================
    private void OutlineList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is OutlineRow row)
            SendToEditor("editor.scrollToHeading", new { line = row.Heading.Line });
    }

    private async void BacklinksList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is NoteLinkItem link)
            await RefreshAllListsAsync(selectNoteId: link.SourceNoteId);
    }

    private async void AddAttachment_Click(object sender, RoutedEventArgs e)
    {
        if (_currentNote is null || _currentNote.IsDeleted) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var bytes = await File.ReadAllBytesAsync(file.Path);
        var mime = GuessMimeType(file.Path);
        var att = await App.Vault.SaveAttachmentAsync(_currentNote.Id, file.Name, mime, bytes, isInline: mime.StartsWith("image/"));
        await RefreshAttachmentsAsync(_currentNote.Id);
        if (mime.StartsWith("image/"))
            SendToEditor("editor.insertMedia", new { url = $"https://vault.mknote.local/attachments/{att.Sha256}", fileName = att.FileName, isImage = true });
        StatusLeftText.Text = $"📎 {att.FileName}";
    }

    private async void AttachmentsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AttachmentItem att) return;
        var media = await App.Vault.ReadAttachmentBytesAsync(att.Sha256);
        if (media is null) return;

        if (media.Value.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var writer = new Windows.Storage.Streams.DataWriter(mem))
            {
                writer.WriteBytes(media.Value.Data);
                await writer.StoreAsync();
                writer.DetachStream();
            }
            mem.Seek(0);
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(mem);

            var img = new Image { Source = bmp, MaxHeight = 520, MaxWidth = 760, Stretch = Stretch.Uniform };
            var dlg = new ContentDialog
            {
                Title = $"🔐 {att.FileName}",
                Content = img,
                PrimaryButtonText = Strings.T("dlg_ok"),
                CloseButtonText = Strings.T("dlg_close"),
                XamlRoot = ShellRoot.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                SendToEditor("editor.insertMedia", new
                {
                    url = $"https://vault.mknote.local/attachments/{att.Sha256}",
                    fileName = att.FileName,
                    isImage = true
                });
            }
        }
        else
        {
            var dlg = new ContentDialog
            {
                Title = $"🔐 {att.FileName}",
                Content = $"{att.FileSize / 1024.0:F1} KB · SQLCipher AES-256 Encrypted",
                PrimaryButtonText = "Export Copy...",
                CloseButtonText = Strings.T("dlg_close"),
                XamlRoot = ShellRoot.XamlRoot
            };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary)
            {
                var saver = new FileSavePicker();
                InitializeWithWindow.Initialize(saver, WindowNative.GetWindowHandle(this));
                saver.SuggestedFileName = att.FileName;
                var ext = Path.GetExtension(att.FileName);
                saver.FileTypeChoices.Add("File", [string.IsNullOrEmpty(ext) ? ".bin" : ext]);
                var target = await saver.PickSaveFileAsync();
                if (target is not null)
                    await File.WriteAllBytesAsync(target.Path, media.Value.Data);
            }
        }
    }

    // ==================== Backup & Migration ====================
    private async void CreateBackup_Click(object sender, RoutedEventArgs e)
    {
        var path = await App.Vault.CreateLocalBackupAsync(_settings.CustomBackupDir);
        StatusLeftText.Text = $"✓ Backup: {path}";
    }

    private void OpenVaultFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo(App.Vault.VaultRootPath) { UseShellExecute = true });

    private async void ImportMarkdownFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var res = await App.Migration.ImportMarkdownFolderAsync(folder.Path);
        StatusLeftText.Text = $"✓ 已导入 Markdown 文件夹：{res.CategoriesImported} 个分类，{res.NotesImported} 篇笔记";
        await RefreshAllListsAsync();
    }

    private async void ExportMarkdownFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        StatusLeftText.Text = "正在导出全部笔记（按格式分别生成 .md 与 .html）...";
        var unlocked = SnapshotUnlockedPasswords();
        var summary = await Task.Run(() => App.Migration.ExportAllNotesAsync(folder.Path, unlocked));
        await ShowExportSummaryDialogAsync("全库导出完成", summary, isSingleFile: false);
    }

    private async void ExportCurrentNote_Click(object sender, RoutedEventArgs e) =>
        await ExportSingleNoteFlowAsync(_currentNote);

    private async void ExportNoteFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: NoteListRow row })
            await ExportSingleNoteFlowAsync(row.Note);
    }

    private async void CopyNoteFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: NoteListRow row } || row.Note.IsDeleted) return;
        var target = await PickTargetCategoryAsync(Strings.T("dlg_copy_title"), row.Note.CategoryId);
        if (!target.Confirmed) return;

        try
        {
            var copied = await App.Vault.CopyNoteToCategoryAsync(row.Note.Id, target.CategoryId);
            await RefreshAllListsAsync(selectNoteId: _currentNote?.Id);
            StatusLeftText.Text = $"✓ 已复制「{copied.Title}」到「{target.DisplayName}」";
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"复制失败：{ex.Message}";
        }
    }

    private async void MoveNoteFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: NoteListRow row } || row.Note.IsDeleted) return;
        var target = await PickTargetCategoryAsync(Strings.T("dlg_move_title"), row.Note.CategoryId);
        if (!target.Confirmed) return;

        try
        {
            await App.Vault.MoveNoteToCategoryAsync(row.Note.Id, target.CategoryId);
            await RefreshAllListsAsync(selectNoteId: _currentNote?.Id);
            StatusLeftText.Text = $"✓ 已移动「{row.Note.Title}」到「{target.DisplayName}」";
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"移动失败：{ex.Message}";
        }
    }

    private async void DeleteNoteFromList_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: NoteListRow row }) return;
        if (row.Note.IsDeleted)
        {
            await App.Vault.PermanentlyDeleteNoteAsync(row.Note.Id);
            StatusLeftText.Text = $"已彻底删除「{row.Note.Title}」";
        }
        else
        {
            await App.Vault.SoftDeleteNoteAsync(row.Note.Id);
            StatusLeftText.Text = $"已移入回收站「{row.Note.Title}」";
        }
        if (_currentNote?.Id == row.Note.Id) ShowEmptyEditorState();
        await RefreshAllListsAsync(selectNoteId: _currentNote?.Id == row.Note.Id ? null : _currentNote?.Id);
    }

    /// <summary>
    /// 单篇笔记导出流程：Markdown 仅提供 .md 导出，富文本仅提供独立单文件 .html 导出。
    /// </summary>
    private async Task ExportSingleNoteFlowAsync(NoteItem? note)
    {
        if (note is null)
        {
            StatusLeftText.Text = "请先选择一篇要导出的笔记。";
            return;
        }
        if (note.IsDeleted)
        {
            var warn = new ContentDialog
            {
                Title = "无法直接导出",
                Content = "该笔记目前位于回收站中，请先恢复到笔记本后再执行导出。",
                CloseButtonText = Strings.T("dlg_close"),
                XamlRoot = ShellRoot.XamlRoot
            };
            await warn.ShowAsync();
            return;
        }

        string? unlockPwd = null;
        if (note.IsEncrypted)
        {
            if (_currentNote?.Id == note.Id && !string.IsNullOrEmpty(_currentUnlockPassword))
                unlockPwd = _currentUnlockPassword;
            else if (_unlockedSessionPasswords.TryGetValue(note.Id, out var cached))
                unlockPwd = cached;
            else
            {
                var pwdBox = new PasswordBox { PlaceholderText = "请输入该笔记的独立加密密码..." };
                var pwdDlg = new ContentDialog
                {
                    Title = $"解锁并导出「{note.Title}」",
                    Content = pwdBox,
                    PrimaryButtonText = Strings.T("dlg_ok"),
                    CloseButtonText = Strings.T("dlg_cancel"),
                    XamlRoot = ShellRoot.XamlRoot
                };
                if (await pwdDlg.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrEmpty(pwdBox.Password))
                    return;
                unlockPwd = pwdBox.Password;
            }
        }

        bool isMd = note.DocFormat == DocumentFormat.Markdown;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = SanitizeExportFileName(note.Title)
        };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        // 严格按文档格式限定导出类型：Markdown -> 仅 .md；富文本 -> 仅 .html
        if (isMd)
            picker.FileTypeChoices.Add("Markdown 文档 (.md)", [".md"]);
        else
            picker.FileTypeChoices.Add("HTML 网页文档 (.html)", [".html"]);

        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            StatusLeftText.Text = $"正在导出「{note.Title}」...";
            var summary = await Task.Run(() => App.Migration.ExportSingleNoteAsync(note, file.Path, unlockPwd));
            if (note.IsEncrypted && !string.IsNullOrEmpty(unlockPwd))
                _unlockedSessionPasswords[note.Id] = unlockPwd;

            await ShowExportSummaryDialogAsync("单篇笔记导出成功", summary, isSingleFile: true);
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"❌ 导出失败：{ex.Message}";
            var errDlg = new ContentDialog
            {
                Title = "导出失败",
                Content = ex.Message,
                CloseButtonText = Strings.T("dlg_close"),
                XamlRoot = ShellRoot.XamlRoot
            };
            await errDlg.ShowAsync();
        }
    }

    /// <summary>
    /// 单独导出指定分类笔记本内的全部文档（自动按格式分别导出 .md 与 .html，有子分类时可选择是否递归导出）。
    /// </summary>
    private async void ExportCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: CategoryRow row }) return;

        var allCats = await App.Vault.GetCategoriesAsync();
        bool hasChildren = allCats.Any(c => string.Equals(c.ParentId, row.Id, StringComparison.Ordinal));
        bool includeSubcategories = true;

        if (hasChildren)
        {
            var scopeDlg = new ContentDialog
            {
                Title = $"导出笔记本「{row.Name}」",
                Content = "该笔记本下包含子笔记本，是否一并导出子笔记本并保留多级目录结构？\n（导出时将自动按格式分别生成 .md 与 .html 文件）",
                PrimaryButtonText = "包含子笔记本（推荐）",
                SecondaryButtonText = "仅导出当前层级",
                CloseButtonText = Strings.T("dlg_cancel"),
                XamlRoot = ShellRoot.XamlRoot
            };
            var choice = await scopeDlg.ShowAsync();
            if (choice == ContentDialogResult.None) return;
            includeSubcategories = choice == ContentDialogResult.Primary;
        }

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        try
        {
            StatusLeftText.Text = $"正在导出笔记本「{row.Name}」...";
            var unlocked = SnapshotUnlockedPasswords();
            var summary = await Task.Run(() => App.Migration.ExportCategoryAsync(row.Id, folder.Path, includeSubcategories, unlocked));
            await ShowExportSummaryDialogAsync($"笔记本「{row.Name}」导出完成", summary, isSingleFile: false);
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"❌ 分类导出失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 批量导出多选模式中勾选的笔记（按各自格式分别导出 .md 与 .html）。
    /// </summary>
    private async void BatchExportNotes_Click(object sender, RoutedEventArgs e)
    {
        if (!_isMultiSelectMode || _quickView == "trash") return;
        var selectedRows = NoteListView.SelectedItems.OfType<NoteListRow>().ToList();
        if (selectedRows.Count == 0) return;

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        try
        {
            StatusLeftText.Text = $"正在批量导出 {selectedRows.Count} 篇笔记...";
            var ids = selectedRows.Select(r => r.Note.Id).ToList();
            var unlocked = SnapshotUnlockedPasswords();
            var summary = await Task.Run(() => App.Migration.ExportNotesBatchAsync(ids, folder.Path, unlocked));
            await ShowExportSummaryDialogAsync("批量导出完成", summary, isSingleFile: false);
        }
        catch (Exception ex)
        {
            StatusLeftText.Text = $"❌ 批量导出失败：{ex.Message}";
        }
    }

    private Dictionary<string, string> SnapshotUnlockedPasswords()
    {
        var copy = new Dictionary<string, string>(_unlockedSessionPasswords, StringComparer.Ordinal);
        if (_currentNote is not null && !string.IsNullOrEmpty(_currentUnlockPassword))
            copy[_currentNote.Id] = _currentUnlockPassword;
        return copy;
    }

    private async Task ShowExportSummaryDialogAsync(string title, ExportSummary summary, bool isSingleFile)
    {
        var detailLines = new List<string>();
        if (isSingleFile)
        {
            var fmt = summary.MarkdownCount > 0 ? "Markdown (.md)" : "独立单文件 HTML (.html)";
            detailLines.Add($"导出格式：{fmt}");
            if (summary.ExportedAssets > 0)
            {
                detailLines.Add(summary.MarkdownCount > 0
                    ? $"导出关联图片/附件：{summary.ExportedAssets} 个（已保存至同级 assets 文件夹）"
                    : $"内嵌图片/附件：{summary.ExportedAssets} 个（图片已自包含内嵌，单文件可直接打开）");
            }
            detailLines.Add($"保存位置：{summary.OutputPath}");
            StatusLeftText.Text = $"✓ 已导出至 {summary.OutputPath}";
        }
        else
        {
            detailLines.Add($"共导出 {summary.ExportedNotes} 篇笔记（Markdown：{summary.MarkdownCount} 篇 · 富文本 HTML：{summary.HtmlCount} 篇）");
            if (summary.ExportedAssets > 0)
                detailLines.Add($"同步还原加密图片/附件：{summary.ExportedAssets} 个（已保存在各目录 assets 文件夹）");
            if (summary.SkippedEncryptedCount > 0)
                detailLines.Add($"已安全跳过 {summary.SkippedEncryptedCount} 篇未解锁的独立加密笔记（解锁后即可导出）");
            detailLines.Add($"导出目录：{summary.OutputPath}");
            StatusLeftText.Text = $"✓ 已导出 {summary.ExportedNotes} 篇笔记（MD {summary.MarkdownCount} / HTML {summary.HtmlCount}）至 {summary.OutputPath}";
        }

        var dlg = new ContentDialog
        {
            Title = title,
            Content = string.Join("\n", detailLines),
            PrimaryButtonText = "打开所在位置",
            CloseButtonText = Strings.T("dlg_close"),
            XamlRoot = ShellRoot.XamlRoot
        };
        if (await dlg.ShowAsync() == ContentDialogResult.Primary)
        {
            try
            {
                if (isSingleFile && File.Exists(summary.OutputPath))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{summary.OutputPath}\"") { UseShellExecute = true });
                else if (Directory.Exists(summary.OutputPath))
                    Process.Start(new ProcessStartInfo(summary.OutputPath) { UseShellExecute = true });
            }
            catch { }
        }
    }

    private static string SanitizeExportFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Untitled" : clean;
    }

    private async void ImportLegacyDb_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".db");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        var res = await App.Migration.ImportLegacyDbAsync(file.Path);
        StatusLeftText.Text = $"✓ 已导入 SQLite 数据库：{res.NotesImported} 篇笔记";
        await RefreshAllListsAsync();
    }

    private async void ImportMknoteFile_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".mknote");
        var files = await picker.PickMultipleFilesAsync();
        if (files is null || files.Count == 0) return;

        int totalNotes = 0, totalAtts = 0;
        var errors = new List<string>();
        foreach (var file in files)
        {
            try
            {
                var res = await App.Migration.ImportMknoteFileAsync(file.Path);
                totalNotes += res.NotesImported;
                totalAtts += res.AttachmentsImported;
            }
            catch (Exception ex)
            {
                errors.Add($"{file.Name}: {ex.Message}");
            }
        }

        StatusLeftText.Text = $"✓ 已导入 {totalNotes} 篇 .mknote 归档笔记（含 {totalAtts} 个加密图片/附件）";
        await RefreshAllListsAsync();

        if (errors.Count > 0)
        {
            var dlg = new ContentDialog
            {
                Title = "部分 .mknote 文件导入异常",
                Content = string.Join("\n", errors),
                CloseButtonText = Strings.T("dlg_close"),
                XamlRoot = ShellRoot.XamlRoot
            };
            await dlg.ShowAsync();
        }
    }

    private async void ImportHtmlFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        StatusLeftText.Text = "正在导入 HTML 文件夹与关联图片...";
        var res = await Task.Run(() => App.Migration.ImportHtmlFolderAsync(folder.Path));
        StatusLeftText.Text = $"✓ 已导入 HTML 笔记：{res.NotesImported} 篇，加密图片 {res.AttachmentsImported} 张";
        await RefreshAllListsAsync();
    }

    // ==================== Theme & Layout ====================
    private async void AppTheme_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { Tag: string mode })
        {
            _settings.ThemeMode = mode;
            ApplyAppTheme(mode);
            await App.Vault.SaveSettingsAsync(_settings);
            if (_currentNote is not null && !_currentNote.IsDeleted)
                await ApplyNoteBackgroundAsync(_currentNote.BackgroundColor, _currentNote.BackgroundStyle);
        }
    }

    private void ApplyAppTheme(string mode)
    {
        ShellRoot.RequestedTheme = mode switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
        RefreshBackgroundPresetSwatches();
    }

    private bool IsAppDarkTheme() => _settings.ThemeMode switch
    {
        "Dark" => true,
        "Light" => false,
        _ => ShellRoot.ActualTheme == ElementTheme.Dark
    };

    private void RightPanelToggle_Click(object sender, RoutedEventArgs e)
    {
        var visible = RightPanelToggle.IsChecked == true;
        InspectorPane.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        InspectorSplitterCol.Width = visible ? new GridLength(6) : new GridLength(0);
        InspectorCol.Width = visible ? new GridLength(Math.Clamp(_settings.InspectorWidth, 190, 500)) : new GridLength(0);
    }

    private static string GuessMimeType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".pdf" => "application/pdf",
        ".txt" or ".md" => "text/plain; charset=utf-8",
        _ => "application/octet-stream"
    };
}
