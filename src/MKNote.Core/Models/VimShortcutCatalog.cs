namespace MKNote.Core.Models;

/// <summary>
/// 单条 Vim 快捷键映射配置模型。
/// </summary>
public sealed class VimKeyMapping
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Category { get; set; } = "自定义映射";
    public string Description { get; set; } = string.Empty;
    /// <summary>所属模式：normal | insert | visual | ex | global。</summary>
    public string Mode { get; set; } = "normal";
    public string DefaultKeys { get; set; } = string.Empty;
    public string CustomKeys { get; set; } = string.Empty;
    /// <summary>按下 CustomKeys 时执行的目标 Vim 命令（例如 "&lt;Esc&gt;"、"dd"、"^"、"$"）。</summary>
    public string ActionTarget { get; set; } = string.Empty;
    public bool Editable { get; set; } = true;

    public string EffectiveKeys => string.IsNullOrWhiteSpace(CustomKeys) ? DefaultKeys : CustomKeys.Trim();
}

/// <summary>
/// 内置 Vim 快捷键默认目录与用户自定义映射合并器。
/// </summary>
public static class VimShortcutCatalog
{
    public static List<VimKeyMapping> GetDefaults() =>
    [
        // 模式切换
        new() { Id = "vim_esc_jk", Category = "模式切换", Description = "插入模式快速返回普通模式", Mode = "insert", DefaultKeys = "jk", ActionTarget = "<Esc>", Editable = true },
        new() { Id = "vim_ins_i", Category = "模式切换", Description = "在光标前进入输入模式", Mode = "normal", DefaultKeys = "i", ActionTarget = "i", Editable = true },
        new() { Id = "vim_ins_a", Category = "模式切换", Description = "在光标后进入输入模式", Mode = "normal", DefaultKeys = "a", ActionTarget = "a", Editable = true },
        new() { Id = "vim_ins_I", Category = "模式切换", Description = "跳至行首进入输入模式", Mode = "normal", DefaultKeys = "I", ActionTarget = "I", Editable = true },
        new() { Id = "vim_ins_A", Category = "模式切换", Description = "跳至行尾进入输入模式", Mode = "normal", DefaultKeys = "A", ActionTarget = "A", Editable = true },
        new() { Id = "vim_ins_o", Category = "模式切换", Description = "在下方新开一行并输入", Mode = "normal", DefaultKeys = "o", ActionTarget = "o", Editable = true },
        new() { Id = "vim_ins_O", Category = "模式切换", Description = "在上方新开一行并输入", Mode = "normal", DefaultKeys = "O", ActionTarget = "O", Editable = true },
        new() { Id = "vim_vis_v", Category = "模式切换", Description = "字符可视选择模式 (Visual)", Mode = "normal", DefaultKeys = "v", ActionTarget = "v", Editable = true },
        new() { Id = "vim_vis_V", Category = "模式切换", Description = "整行可视选择模式 (Visual Line)", Mode = "normal", DefaultKeys = "V", ActionTarget = "V", Editable = true },
        new() { Id = "vim_esc", Category = "模式切换", Description = "退出输入/可视模式返回普通模式（固定保护）", Mode = "insert", DefaultKeys = "Esc", ActionTarget = "<Esc>", Editable = false },

        // 光标移动
        new() { Id = "vim_mv_hjkl", Category = "光标移动", Description = "左 / 下 / 上 / 右移动光标（支持自动换行 gj/gk）", Mode = "normal", DefaultKeys = "h / j / k / l", ActionTarget = "", Editable = false },
        new() { Id = "vim_mv_w", Category = "光标移动", Description = "向后跳至下一个词首", Mode = "normal", DefaultKeys = "w", ActionTarget = "w", Editable = true },
        new() { Id = "vim_mv_b", Category = "光标移动", Description = "向前跳至上一个词首", Mode = "normal", DefaultKeys = "b", ActionTarget = "b", Editable = true },
        new() { Id = "vim_mv_e", Category = "光标移动", Description = "跳至当前/下一个词尾", Mode = "normal", DefaultKeys = "e", ActionTarget = "e", Editable = true },
        new() { Id = "vim_mv_0", Category = "光标移动", Description = "跳至当前行首非空字符（如可改为 H）", Mode = "normal", DefaultKeys = "0", ActionTarget = "^", Editable = true },
        new() { Id = "vim_mv_end", Category = "光标移动", Description = "跳至当前行尾（如可改为 L）", Mode = "normal", DefaultKeys = "$", ActionTarget = "$", Editable = true },
        new() { Id = "vim_mv_gg", Category = "光标移动", Description = "跳至文档顶部第一行", Mode = "normal", DefaultKeys = "gg", ActionTarget = "gg", Editable = true },
        new() { Id = "vim_mv_G", Category = "光标移动", Description = "跳至文档底部最后一行", Mode = "normal", DefaultKeys = "G", ActionTarget = "G", Editable = true },
        new() { Id = "vim_mv_f", Category = "光标移动", Description = "行内向前查找字符（如 fa 跳至下一个 a）", Mode = "normal", DefaultKeys = "f{char}", ActionTarget = "", Editable = false },

        // 编辑与删除
        new() { Id = "vim_ed_dd", Category = "编辑与删除", Description = "删除当前整行（或光标所在图片行）", Mode = "normal", DefaultKeys = "dd", ActionTarget = "dd", Editable = true },
        new() { Id = "vim_ed_D", Category = "编辑与删除", Description = "从光标处删除至行尾", Mode = "normal", DefaultKeys = "D", ActionTarget = "D", Editable = true },
        new() { Id = "vim_ed_C", Category = "编辑与删除", Description = "从光标处修改至行尾并进入输入模式", Mode = "normal", DefaultKeys = "C", ActionTarget = "C", Editable = true },
        new() { Id = "vim_ed_dw", Category = "编辑与删除", Description = "删除当前光标后一个词", Mode = "normal", DefaultKeys = "dw", ActionTarget = "dw", Editable = true },
        new() { Id = "vim_ed_cc", Category = "编辑与删除", Description = "清空当前行并进入输入模式", Mode = "normal", DefaultKeys = "cc", ActionTarget = "cc", Editable = true },
        new() { Id = "vim_ed_ciw", Category = "编辑与删除", Description = "修改光标所在的整个词 (Markdown/Rich)", Mode = "normal", DefaultKeys = "ciw", ActionTarget = "ciw", Editable = true },
        new() { Id = "vim_ed_x", Category = "编辑与删除", Description = "删除当前光标处字符或选中图片", Mode = "normal", DefaultKeys = "x", ActionTarget = "x", Editable = true },

        // 复制粘贴与撤销
        new() { Id = "vim_cp_yy", Category = "复制与撤销", Description = "复制当前整行 (Yank)", Mode = "normal", DefaultKeys = "yy", ActionTarget = "yy", Editable = true },
        new() { Id = "vim_cp_p", Category = "复制与撤销", Description = "在光标后/下一行粘贴内容", Mode = "normal", DefaultKeys = "p", ActionTarget = "p", Editable = true },
        new() { Id = "vim_cp_P", Category = "复制与撤销", Description = "在光标前/上一行粘贴内容", Mode = "normal", DefaultKeys = "P", ActionTarget = "P", Editable = true },
        new() { Id = "vim_cp_u", Category = "复制与撤销", Description = "撤销上一步修改 (Undo)", Mode = "normal", DefaultKeys = "u", ActionTarget = "u", Editable = true },
        new() { Id = "vim_cp_redo", Category = "复制与撤销", Description = "重做已撤销的修改 (Redo，固定保护)", Mode = "normal", DefaultKeys = "Ctrl+R", ActionTarget = "<C-r>", Editable = false },

        // Ex 命令与搜索
        new() { Id = "vim_ex_w", Category = "命令与搜索", Description = "立即保存并创建本地版本快照", Mode = "ex", DefaultKeys = ":w", ActionTarget = ":w<CR>", Editable = false },
        new() { Id = "vim_ex_noh", Category = "命令与搜索", Description = "清除搜索高亮标记", Mode = "ex", DefaultKeys = ":noh", ActionTarget = ":noh<CR>", Editable = false },
        new() { Id = "vim_ex_sub", Category = "命令与搜索", Description = "全文查找并替换所有匹配文本", Mode = "ex", DefaultKeys = ":%s/旧/新/g", ActionTarget = "", Editable = false },
        new() { Id = "vim_ex_find", Category = "命令与搜索", Description = "向下搜索文档关键词（n/N 切换匹配）", Mode = "ex", DefaultKeys = "/关键词", ActionTarget = "", Editable = false },
        new() { Id = "vim_ex_rfind", Category = "命令与搜索", Description = "向上反向搜索关键词", Mode = "ex", DefaultKeys = "?关键词", ActionTarget = "", Editable = false },

        // 应用全局快捷操作（固定保护，不可修改）
        new() { Id = "app_search", Category = "固定全局快捷键", Description = "聚焦顶部全库精确搜索框（固定保护）", Mode = "global", DefaultKeys = "Ctrl+K", ActionTarget = "", Editable = false },
        new() { Id = "app_capture", Category = "固定全局快捷键", Description = "区域屏幕截图并加密插入当前笔记（固定保护）", Mode = "global", DefaultKeys = "Ctrl+Shift+A", ActionTarget = "", Editable = false },
        new() { Id = "app_md_read", Category = "固定全局快捷键", Description = "切换 Markdown 行内预览 / 阅读模式（固定保护）", Mode = "global", DefaultKeys = "Ctrl+E", ActionTarget = "", Editable = false },
        new() { Id = "app_list_nav", Category = "固定全局快捷键", Description = "Vim 模式下切换笔记列表下/上一篇（固定保护）", Mode = "global", DefaultKeys = "Ctrl+J / Ctrl+P", ActionTarget = "", Editable = false }
    ];

    public static List<VimKeyMapping> MergeWithSaved(IEnumerable<VimKeyMapping>? saved)
    {
        var defaults = GetDefaults();
        if (saved is null) return defaults;

        var savedList = saved.ToList();
        var byId = savedList.ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var d in defaults)
        {
            if (byId.TryGetValue(d.Id, out var custom) && d.Editable &&
                !ShortcutGuard.IsReservedAppShortcut(custom.CustomKeys))
            {
                d.CustomKeys = custom.CustomKeys;
            }
        }

        // Also include user-added custom bindings not in default catalog (excluding reserved app hotkeys)
        var defaultIds = defaults.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var extra in savedList.Where(s => !defaultIds.Contains(s.Id) && !ShortcutGuard.IsReservedAppShortcut(s.EffectiveKeys)))
        {
            defaults.Add(extra);
        }

        return defaults;
    }
}
