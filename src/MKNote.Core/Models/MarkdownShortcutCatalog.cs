namespace MKNote.Core.Models;

/// <summary>
/// Strictly protects fixed OS & application-level shortcuts from being overwritten or shadowed
/// by user-customized Vim or Markdown shortcuts.
/// </summary>
public static class ShortcutGuard
{
    private static readonly HashSet<string> ReservedCanonicalShortcuts = new(StringComparer.OrdinalIgnoreCase)
    {
        "CTRL+C",
        "CTRL+V",
        "CTRL+X",
        "CTRL+Z",
        "CTRL+Y",
        "CTRL+A",
        "CTRL+S",
        "CTRL+F",
        "CTRL+K",
        "CTRL+E",
        "CTRL+J",
        "CTRL+P",
        "CTRL+R",
        "CTRL+SHIFT+A",
        "CTRL+SHIFT+V",
        "CTRL+ALT+M",
        "CTRL+ALT+N",
        "CTRL+ALT+F",
        "ALT+F4",
        "ESC",
        "ESCAPE",
        "<ESC>",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12"
    };

    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var s = raw.Trim();

        // Normalize Vim notation like <C-s> / <C-S-a> / <A-f4>
        if (s.StartsWith('<') && s.EndsWith('>') && s.Length > 2)
        {
            var inner = s[1..^1].Trim();
            if (inner.Equals("Esc", StringComparison.OrdinalIgnoreCase) || inner.Equals("Escape", StringComparison.OrdinalIgnoreCase))
                return "ESC";
            if (inner.StartsWith("C-S-", StringComparison.OrdinalIgnoreCase))
                return "CTRL+SHIFT+" + inner[4..].ToUpperInvariant();
            if (inner.StartsWith("C-A-", StringComparison.OrdinalIgnoreCase))
                return "CTRL+ALT+" + inner[4..].ToUpperInvariant();
            if (inner.StartsWith("C-", StringComparison.OrdinalIgnoreCase))
                return "CTRL+" + inner[2..].ToUpperInvariant();
            if (inner.StartsWith("A-", StringComparison.OrdinalIgnoreCase) || inner.StartsWith("M-", StringComparison.OrdinalIgnoreCase))
                return "ALT+" + inner[2..].ToUpperInvariant();
        }

        var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.ToUpperInvariant() switch
            {
                "CONTROL" or "CMD" or "MOD" => "CTRL",
                "ESCAPE" => "ESC",
                var other => other
            })
            .ToList();

        if (parts.Count == 1) return parts[0];

        var hasCtrl = parts.Contains("CTRL");
        var hasAlt = parts.Contains("ALT");
        var hasShift = parts.Contains("SHIFT");
        var main = parts.LastOrDefault(p => p is not ("CTRL" or "ALT" or "SHIFT")) ?? "";

        var ordered = new List<string>(4);
        if (hasCtrl) ordered.Add("CTRL");
        if (hasAlt) ordered.Add("ALT");
        if (hasShift) ordered.Add("SHIFT");
        if (!string.IsNullOrEmpty(main)) ordered.Add(main);
        return string.Join("+", ordered);
    }

    public static bool IsReservedAppShortcut(string? candidate)
    {
        var norm = Normalize(candidate);
        if (string.IsNullOrEmpty(norm)) return false;
        return ReservedCanonicalShortcuts.Contains(norm);
    }

    public static string? ValidateCustomShortcut(string? candidate, string mode = "shortcut")
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null; // empty resets to default
        if (IsReservedAppShortcut(candidate))
            return $"「{candidate.Trim()}」是应用或系统保留的固定快捷键，禁止修改或占用。";

        if (mode == "markdown")
        {
            var norm = Normalize(candidate);
            if (!norm.Contains("CTRL+") && !norm.Contains("ALT+"))
                return "Markdown 快捷键必须包含 Ctrl 或 Alt 修饰键（例如 Ctrl+B、Ctrl+Alt+1）。";
        }
        return null;
    }
}

public sealed class MarkdownShortcutItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "基础排版";
    public string Title { get; set; } = string.Empty;
    public string Syntax { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string DefaultShortcut { get; set; } = string.Empty;
    public string CustomShortcut { get; set; } = string.Empty;
    /// <summary>在 markdownEditor.ts 中分发的编辑器命令标识（例如 "bold"、"h1"、"codeblock"）。</summary>
    public string CommandAction { get; set; } = string.Empty;
    public bool Editable { get; set; } = true;

    public string EffectiveShortcut => string.IsNullOrWhiteSpace(CustomShortcut) ? DefaultShortcut : CustomShortcut.Trim();
}

public static class MarkdownShortcutCatalog
{
    public static List<MarkdownShortcutItem> GetDefaults() =>
    [
        // 标题与结构
        new() { Id = "md_h1", Category = "标题与结构", Title = "一级标题 (H1)", Syntax = "# 标题文本", Description = "将当前行设为一级大标题", DefaultShortcut = "Ctrl+1", CommandAction = "h1", Editable = true },
        new() { Id = "md_h2", Category = "标题与结构", Title = "二级标题 (H2)", Syntax = "## 标题文本", Description = "将当前行设为二级标题", DefaultShortcut = "Ctrl+2", CommandAction = "h2", Editable = true },
        new() { Id = "md_h3", Category = "标题与结构", Title = "三级标题 (H3)", Syntax = "### 标题文本", Description = "将当前行设为三级标题", DefaultShortcut = "Ctrl+3", CommandAction = "h3", Editable = true },
        new() { Id = "md_quote", Category = "标题与结构", Title = "引用块", Syntax = "> 引用内容", Description = "将当前行切换为引用格式", DefaultShortcut = "Ctrl+Shift+Q", CommandAction = "quote", Editable = true },
        new() { Id = "md_hr", Category = "标题与结构", Title = "分隔线", Syntax = "---", Description = "插入水平分隔线", DefaultShortcut = "Ctrl+Alt+H", CommandAction = "hr", Editable = true },

        // 行内样式
        new() { Id = "md_bold", Category = "行内样式", Title = "加粗 (Bold)", Syntax = "**文本**", Description = "对选中文本或光标处加粗", DefaultShortcut = "Ctrl+B", CommandAction = "bold", Editable = true },
        new() { Id = "md_italic", Category = "行内样式", Title = "斜体 (Italic)", Syntax = "*文本*", Description = "对选中文本或光标处设为斜体", DefaultShortcut = "Ctrl+I", CommandAction = "italic", Editable = true },
        new() { Id = "md_strike", Category = "行内样式", Title = "删除线", Syntax = "~~文本~~", Description = "添加删除线标记", DefaultShortcut = "Ctrl+Shift+X", CommandAction = "strike", Editable = true },
        new() { Id = "md_highlight", Category = "行内样式", Title = "荧光高亮", Syntax = "==重点内容==", Description = "行内荧光高亮标记", DefaultShortcut = "Ctrl+Shift+H", CommandAction = "highlight", Editable = true },
        new() { Id = "md_code", Category = "行内样式", Title = "行内代码", Syntax = "`code`", Description = "将选中文本包裹为行内代码", DefaultShortcut = "Ctrl+`", CommandAction = "code", Editable = true },

        // 代码块与列表
        new() { Id = "md_codeblock", Category = "代码块与列表", Title = "围栏代码块", Syntax = "```lang\\n代码\\n```", Description = "插入带语法高亮的多行代码块", DefaultShortcut = "Ctrl+Shift+C", CommandAction = "codeblock", Editable = true },
        new() { Id = "md_bullet", Category = "代码块与列表", Title = "无序列表", Syntax = "- 列表项", Description = "插入或切换无序项目符号列表", DefaultShortcut = "Ctrl+Shift+U", CommandAction = "bullet", Editable = true },
        new() { Id = "md_ordered", Category = "代码块与列表", Title = "有序列表", Syntax = "1. 列表项", Description = "插入编号有序列表", DefaultShortcut = "Ctrl+Shift+O", CommandAction = "ordered", Editable = true },
        new() { Id = "md_todo", Category = "代码块与列表", Title = "待办任务复选框", Syntax = "- [ ] 待办事项", Description = "插入可勾选的任务列表项", DefaultShortcut = "Ctrl+L", CommandAction = "todo", Editable = true },

        // 进阶知识库语法
        new() { Id = "md_wikilink", Category = "双链与进阶语法", Title = "双向链接 (Wikilink)", Syntax = "[[笔记标题]]", Description = "插入双向链接并自动加入知识图谱", DefaultShortcut = "Ctrl+Shift+L", CommandAction = "wikilink", Editable = true },
        new() { Id = "md_link", Category = "双链与进阶语法", Title = "超链接", Syntax = "[名称](https://...)", Description = "插入标准 Markdown 网页链接", DefaultShortcut = "Ctrl+Alt+K", CommandAction = "link", Editable = true },
        new() { Id = "md_table", Category = "双链与进阶语法", Title = "Markdown 表格", Syntax = "| 列1 | 列2 |\\n| --- | --- |", Description = "插入标准 2x2 Markdown 表格", DefaultShortcut = "Ctrl+Alt+T", CommandAction = "table", Editable = true },
        new() { Id = "md_callout", Category = "双链与进阶语法", Title = "提示卡片 (Callout)", Syntax = "> [!tip] 标题\\n> 内容", Description = "插入可视化提示卡片（支持 note/tip/warning/danger）", DefaultShortcut = "Ctrl+Alt+C", CommandAction = "callout", Editable = true },
        new() { Id = "md_math", Category = "双链与进阶语法", Title = "LaTeX 数学公式块", Syntax = "$$\\nE = mc^2\\n$$", Description = "插入 KaTeX 渲染的块级数学公式（行内可用 $x^2$）", DefaultShortcut = "Ctrl+Alt+M", CommandAction = "math", Editable = false },
        new() { Id = "md_mermaid", Category = "双链与进阶语法", Title = "Mermaid 流程图", Syntax = "```mermaid\\nflowchart LR\\n  A-->B\\n```", Description = "插入可实时渲染的 Mermaid 流程图", DefaultShortcut = "Ctrl+Alt+G", CommandAction = "mermaid", Editable = true },

        // 应用固定快捷键（受保护，不可修改）
        new() { Id = "md_fixed_read", Category = "固定快捷键（不可修改）", Title = "切换行内预览 / 阅读模式", Syntax = "内置视图切换", Description = "固定应用快捷键，受保护禁止修改", DefaultShortcut = "Ctrl+E", CommandAction = "", Editable = false },
        new() { Id = "md_fixed_search", Category = "固定快捷键（不可修改）", Title = "全库精确搜索", Syntax = "全局搜索栏", Description = "固定应用快捷键，受保护禁止修改", DefaultShortcut = "Ctrl+K", CommandAction = "", Editable = false },
        new() { Id = "md_fixed_capture", Category = "固定快捷键（不可修改）", Title = "应用内截图并加密插入", Syntax = "区域截屏", Description = "固定应用快捷键，受保护禁止修改", DefaultShortcut = "Ctrl+Shift+A", CommandAction = "", Editable = false },
        new() { Id = "md_fixed_save", Category = "固定快捷键（不可修改）", Title = "手动创建版本快照", Syntax = "立即落盘并存档", Description = "固定应用快捷键，受保护禁止修改", DefaultShortcut = "Ctrl+S", CommandAction = "", Editable = false }
    ];

    public static List<MarkdownShortcutItem> MergeWithSaved(IEnumerable<MarkdownShortcutItem>? saved)
    {
        var defaults = GetDefaults();
        if (saved is null) return defaults;

        var byId = saved.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            if (d.Editable && byId.TryGetValue(d.Id, out var custom) &&
                !ShortcutGuard.IsReservedAppShortcut(custom.CustomShortcut))
            {
                d.CustomShortcut = custom.CustomShortcut;
            }
        }
        return defaults;
    }
}
