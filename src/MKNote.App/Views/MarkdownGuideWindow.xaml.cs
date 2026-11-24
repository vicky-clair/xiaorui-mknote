using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MKNote.Core.Models;
using Windows.Graphics;

namespace MKNote.App.Views;

public sealed class MarkdownShortcutRow
{
    public MarkdownShortcutItem Model { get; }
    public MarkdownShortcutRow(MarkdownShortcutItem model) => Model = model;

    public string Category => Model.Category;
    public string Title => Model.Title;
    public string Syntax => Model.Syntax;
    public string Description => Model.Description;
    public string DefaultShortcut => Model.DefaultShortcut;
    public bool Editable => Model.Editable;
    public string Placeholder => Model.Editable ? "留空用默认" : "固定保护不可改";

    public string CustomShortcut
    {
        get => Model.CustomShortcut;
        set => Model.CustomShortcut = value;
    }
}

/// <summary>
/// Markdown 语法速查与快捷键自定义弹窗：支持按分类搜索、自定义快捷键校验（阻止覆盖受保护全局热键）与一键恢复默认。
/// </summary>
public sealed partial class MarkdownGuideWindow : Window
{
    private readonly Func<List<MarkdownShortcutItem>, Task> _onSave;
    private List<MarkdownShortcutRow> _allRows = [];

    public MarkdownGuideWindow(AppSettings settings, ElementTheme theme, Func<List<MarkdownShortcutItem>, Task> onSave)
    {
        InitializeComponent();
        _onSave = onSave;
        AppWindow.Resize(new SizeInt32(900, 660));
        if (App.AppIconPath is not null) AppWindow.SetIcon(App.AppIconPath);
        SystemBackdrop = new MicaBackdrop();
        RootGrid.RequestedTheme = theme;

        LoadRows(MarkdownShortcutCatalog.MergeWithSaved(settings.CustomMarkdownShortcuts));
    }

    private void LoadRows(List<MarkdownShortcutItem> items)
    {
        _allRows = items.Select(m => new MarkdownShortcutRow(new MarkdownShortcutItem
        {
            Id = m.Id,
            Category = m.Category,
            Title = m.Title,
            Syntax = m.Syntax,
            Description = m.Description,
            DefaultShortcut = m.DefaultShortcut,
            CustomShortcut = m.CustomShortcut,
            CommandAction = m.CommandAction,
            Editable = m.Editable
        })).ToList();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (ShortcutsList is null) return;
        var q = FilterBox?.Text?.Trim() ?? string.Empty;
        var selectedCat = (CategoryFilterBox?.SelectedItem as ComboBoxItem)?.Content as string ?? "全部分类";

        var filtered = _allRows.Where(r =>
        {
            if (selectedCat != "全部分类" && !string.Equals(r.Category, selectedCat, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(q)) return true;
            return r.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Syntax.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.DefaultShortcut.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CustomShortcut.Contains(q, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        ShortcutsList.ItemsSource = filtered;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void CategoryFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        ValidationMsg.Text = "";
        LoadRows(MarkdownShortcutCatalog.GetDefaults());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationMsg.Text = "";
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var r in _allRows.Where(x => x.Editable))
        {
            var err = ShortcutGuard.ValidateCustomShortcut(r.CustomShortcut, "markdown");
            if (err is not null)
            {
                ValidationMsg.Text = $"{r.Title}：{err}";
                return;
            }
            var norm = ShortcutGuard.Normalize(r.Model.EffectiveShortcut);
            if (!string.IsNullOrEmpty(norm) && !seen.Add(norm))
            {
                ValidationMsg.Text = $"快捷键冲突：「{r.Model.EffectiveShortcut}」被多个 Markdown 命令重复占用。";
                return;
            }
        }

        var toSave = _allRows
            .Select(r => r.Model)
            .Where(m => m.Editable && !string.IsNullOrWhiteSpace(m.CustomShortcut))
            .ToList();

        await _onSave(toSave);
        Close();
    }
}
