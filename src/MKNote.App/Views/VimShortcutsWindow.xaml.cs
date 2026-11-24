using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MKNote.Core.Models;
using Windows.Graphics;

namespace MKNote.App.Views;

public sealed class VimShortcutRow
{
    public VimKeyMapping Model { get; }

    public VimShortcutRow(VimKeyMapping model) => Model = model;

    public string Category => Model.Category;
    public string ModeBadge => Model.Mode.ToUpperInvariant();
    public string Description => Model.Description;
    public string DefaultKeys => Model.DefaultKeys;
    public bool Editable => Model.Editable;
    public string Placeholder => Model.Editable ? "留空用默认" : "固定保护不可改";

    public string CustomKeys
    {
        get => Model.CustomKeys;
        set => Model.CustomKeys = value;
    }
}

/// <summary>
/// Vim 快捷键指南与自定义键位映射弹窗：支持启用/关闭 Vim 模式、修改按键映射、添加自定义映射，并严格保护固定全局热键不被覆盖。
/// </summary>
public sealed partial class VimShortcutsWindow : Window
{
    private readonly Func<bool, List<VimKeyMapping>, Task> _onApply;
    private List<VimShortcutRow> _allRows = [];

    public VimShortcutsWindow(AppSettings settings, ElementTheme theme, Func<bool, List<VimKeyMapping>, Task> onApply)
    {
        InitializeComponent();
        _onApply = onApply;

        AppWindow.Resize(new SizeInt32(840, 660));
        SystemBackdrop = new MicaBackdrop();
        RootGrid.RequestedTheme = theme;

        VimEnableToggle.IsOn = settings.EnableVimMode;
        LoadRows(VimShortcutCatalog.MergeWithSaved(settings.CustomVimMappings));
    }

    private void LoadRows(List<VimKeyMapping> mappings)
    {
        _allRows = mappings.Select(m => new VimShortcutRow(new VimKeyMapping
        {
            Id = m.Id,
            Category = m.Category,
            Description = m.Description,
            Mode = m.Mode,
            DefaultKeys = m.DefaultKeys,
            CustomKeys = m.CustomKeys,
            ActionTarget = m.ActionTarget,
            Editable = m.Editable
        })).ToList();

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var q = FilterBox?.Text?.Trim() ?? string.Empty;
        var selectedCat = (CategoryFilterBox?.SelectedItem as ComboBoxItem)?.Content as string ?? "全部分类";

        var filtered = _allRows.Where(r =>
        {
            if (selectedCat != "全部分类" && !string.Equals(r.Category, selectedCat, StringComparison.OrdinalIgnoreCase))
                return false;
            if (string.IsNullOrEmpty(q)) return true;
            return r.Description.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.DefaultKeys.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.CustomKeys.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.Category.Contains(q, StringComparison.OrdinalIgnoreCase)
                || r.ModeBadge.Contains(q, StringComparison.OrdinalIgnoreCase);
        }).ToList();

        ShortcutsList.ItemsSource = filtered;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void CategoryFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => ApplyFilter();

    private async void AddCustomMapping_Click(object sender, RoutedEventArgs e)
    {
        var lhs = AddLhsBox.Text.Trim();
        var rhs = AddRhsBox.Text.Trim();
        if (string.IsNullOrEmpty(lhs) || string.IsNullOrEmpty(rhs)) return;

        var err = ShortcutGuard.ValidateCustomShortcut(lhs, "vim");
        if (err is not null)
        {
            await ShowValidationErrorAsync(err);
            return;
        }

        var mode = (AddModeBox.SelectedItem as ComboBoxItem)?.Content as string ?? "normal";
        var desc = string.IsNullOrWhiteSpace(AddDescBox.Text) ? $"自定义映射：{lhs} → {rhs}" : AddDescBox.Text.Trim();

        _allRows.Add(new VimShortcutRow(new VimKeyMapping
        {
            Id = "user_" + Guid.NewGuid().ToString("N")[..8],
            Category = "自定义映射",
            Mode = mode,
            DefaultKeys = rhs,
            CustomKeys = lhs,
            ActionTarget = rhs,
            Description = desc,
            Editable = true
        }));

        AddLhsBox.Text = "";
        AddRhsBox.Text = "";
        AddDescBox.Text = "";
        ApplyFilter();
    }

    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        LoadRows(VimShortcutCatalog.GetDefaults());
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in _allRows.Where(r => r.Editable && !string.IsNullOrWhiteSpace(r.CustomKeys)))
        {
            var err = ShortcutGuard.ValidateCustomShortcut(row.CustomKeys, "vim");
            if (err is not null)
            {
                await ShowValidationErrorAsync($"{row.Description}：{err}");
                return;
            }
        }

        var toSave = _allRows
            .Select(r => r.Model)
            .Where(m => m.Editable && (!string.IsNullOrWhiteSpace(m.CustomKeys) || m.Id.StartsWith("user_", StringComparison.Ordinal)))
            .ToList();

        await _onApply(VimEnableToggle.IsOn, toSave);
        Close();
    }

    private async Task ShowValidationErrorAsync(string message)
    {
        var dlg = new ContentDialog
        {
            Title = "受保护的固定快捷键",
            Content = message,
            CloseButtonText = "知道了",
            XamlRoot = RootGrid.XamlRoot
        };
        await dlg.ShowAsync();
    }
}
