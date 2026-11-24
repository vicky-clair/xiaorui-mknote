namespace MKNote.Core.Models;

public sealed class CategoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? ParentId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string? Color { get; set; }
    public int SortOrder { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public int NoteCount { get; set; }
}

public sealed class NoteItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string? CategoryId { get; set; }
    public string Title { get; set; } = "无标题笔记";
    public string TitlePinyin { get; set; } = string.Empty;
    public DocumentFormat DocFormat { get; set; } = DocumentFormat.RichHtml;
    public string FileRelPath { get; set; } = string.Empty;
    public string ContentSummary { get; set; } = string.Empty;
    public long ContentSize { get; set; }
    public int WordCount { get; set; }
    public string ContentSha256 { get; set; } = string.Empty;
    public string? BackgroundColor { get; set; }
    public BackgroundStyle BackgroundStyle { get; set; } = BackgroundStyle.Solid;
    public string? FrontmatterJson { get; set; }
    public string? SourceUrl { get; set; }
    public bool IsPinned { get; set; }
    public bool IsStarred { get; set; }
    public bool IsEncrypted { get; set; }
    public string? PasswordHint { get; set; }
    public string? CryptoMetaJson { get; set; }
    public bool IsSticky { get; set; }
    public bool IsDeleted { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Tags { get; set; } = [];
}

public sealed class TagItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string? Color { get; set; }
    public int NoteCount { get; set; }
}

public sealed class AttachmentItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NoteId { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string MimeType { get; set; } = "application/octet-stream";
    public long FileSize { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public string StorageRelPath { get; set; } = string.Empty;
    public bool IsInline { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NoteLinkItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string SourceNoteId { get; set; } = string.Empty;
    public string? TargetNoteId { get; set; }
    public string TargetTitle { get; set; } = string.Empty;
    public string? TargetHeading { get; set; }
    public string? Alias { get; set; }
    public string LinkType { get; set; } = "wikilink"; // wikilink | embed | markdown
    public string ContextSnippet { get; set; } = string.Empty;
    public string? SourceNoteTitle { get; set; }
}

public sealed class NoteRevisionItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string NoteId { get; set; } = string.Empty;
    public DocumentFormat DocFormat { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ContentSha256 { get; set; } = string.Empty;
    public string Reason { get; set; } = "auto_save";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NoteTemplateItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public DocumentFormat DocFormat { get; set; } = DocumentFormat.RichHtml;
    public string DefaultTitlePattern { get; set; } = string.Empty;
    public List<string> DefaultTags { get; set; } = [];
    public string? DefaultBackgroundColor { get; set; }
    public string Content { get; set; } = string.Empty;
    public bool IsBuiltin { get; set; }
    public int SortOrder { get; set; }
}

public sealed record HeadingItem(int Level, string Text, int Line);

public sealed record SearchHit(
    string NoteId,
    string Title,
    DocumentFormat DocFormat,
    string HighlightSnippet,
    DateTimeOffset UpdatedAt,
    double Rank);

public sealed record GraphNode(
    string Id,
    string Label,
    string Kind, // "note" | "unresolved" | "tag"
    string? Group,
    int Degree,
    string Format);

public sealed record GraphEdge(string Source, string Target, string Kind);

public sealed record GraphData(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Microsoft Word 风格的文档字数与段落统计数据。
/// </summary>
public sealed record WordCountStats(
    int WordStyleTotal,
    int CjkChars,
    int LatinWords,
    int CharsNoSpaces,
    int CharsWithSpaces,
    int Paragraphs,
    int Lines,
    int SelectionWords = 0,
    int SelectionChars = 0);

/// <summary>
/// 加密防崩溃预写恢复记录（未正式落盘前以 AES-256-GCM 暂存，崩溃重启后自动恢复）。
/// </summary>
public sealed record CrashRecoveryRecord(
    string NoteId,
    string Title,
    DocumentFormat DocFormat,
    string Content,
    DateTimeOffset SavedAtUtc);

/// <summary>
/// 端到端加密 (E2EE) 同步配置（支持群晖 NAS WebDAV / SMB、通用 WebDAV、本地同步目录）。
/// </summary>
public sealed class SyncSettings
{
    /// <summary>同步提供程序类型：None | SynologyWebDav | SynologySmb | GenericWebDav | LocalFolder</summary>
    public string Provider { get; set; } = "None";
    public string EndpointUrl { get; set; } = string.Empty;
    public string RemotePath { get; set; } = "/MKNoteSync";
    public string Username { get; set; } = string.Empty;
    /// <summary>经 VaultKeyManager（DPAPI 保护主密钥）加密后的认证密码密文，绝不明文落盘。</summary>
    public string ProtectedPasswordBase64 { get; set; } = string.Empty;
    public bool AllowSelfSignedLanCert { get; set; } = true;
    public bool EnableE2EE { get; set; } = true;
    /// <summary>经本地加密保护的 E2EE 同步主密码。</summary>
    public string ProtectedE2eePassphraseBase64 { get; set; } = string.Empty;
    public int AutoSyncMinutes { get; set; } = 0; // 0 表示仅手动同步
    public DateTimeOffset? LastSyncTimeUtc { get; set; }
    public string LastSyncSummary { get; set; } = string.Empty;
}

public sealed class AppSettings
{
    public string VaultPath { get; set; } = string.Empty;
    /// <summary>zh-CN | en-US | ja-JP</summary>
    public string Language { get; set; } = "zh-CN";
    public DocumentFormat DefaultNewNoteFormat { get; set; } = DocumentFormat.Markdown;
    public MarkdownViewMode DefaultMarkdownViewMode { get; set; } = MarkdownViewMode.LivePreview;
    public string ThemeMode { get; set; } = "System"; // System | Light | Dark
    public string SkinPresetId { get; set; } = "blue";
    public string DefaultBackgroundColor { get; set; } = "default";
    public BackgroundStyle DefaultBackgroundStyle { get; set; } = BackgroundStyle.Solid;
    public string? GlobalBgImageSha256 { get; set; }
    /// <summary>cover | stretch | contain | center | repeat</summary>
    public string GlobalBgImageFit { get; set; } = "cover";
    public double GlobalBgImageOpacity { get; set; } = 0.25;
    public string UiFontFamily { get; set; } = "Microsoft YaHei UI";
    public string EditorFontFamily { get; set; } = "Microsoft YaHei UI";
    public int EditorFontSize { get; set; } = 15;
    public bool EnableVimMode { get; set; } = false;
    public bool VimJkEscape { get; set; } = true;
    public bool VimRelativeLineNumbers { get; set; } = false;
    public List<VimKeyMapping> CustomVimMappings { get; set; } = [];
    public List<MarkdownShortcutItem> CustomMarkdownShortcuts { get; set; } = [];
    public SyncSettings Sync { get; set; } = new();
    public bool ShowChildCategoryNotes { get; set; } = true;
    public bool ConfirmDeleteNote { get; set; } = true;
    public bool ConfirmDeleteCategory { get; set; } = true;
    public bool AutoStartWithWindows { get; set; } = false;
    public bool CloseToTray { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowFloatingToolbar { get; set; } = false;
    public bool EnableAppLock { get; set; } = false;
    public bool UseWindowsHello { get; set; } = false;
    public bool LockOnMinimize { get; set; } = false;
    public int AutoLockIdleMinutes { get; set; } = 0; // 0 = disabled
    public string? AppLockPasswordHash { get; set; }
    public string? AppLockPasswordSalt { get; set; }
    public string? AppLockPasswordHint { get; set; }
    public bool EnableAutoBackup { get; set; } = true;
    public int AutoBackupIntervalHours { get; set; } = 24;
    public int MaxBackupCount { get; set; } = 15;
    public string? CustomBackupDir { get; set; }
    public double SidebarWidth { get; set; } = 230;
    public double NoteListWidth { get; set; } = 290;
    public bool RightPanelVisible { get; set; } = true;
    public string HotKeyShowHide { get; set; } = "Ctrl+Alt+M";
    public string HotKeyNewNote { get; set; } = "Ctrl+Alt+N";
    public string HotKeySearch { get; set; } = "Ctrl+Alt+F";
}
