using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using MKNote.Core.Models;
using Windows.Graphics;
using Windows.UI;

namespace MKNote.App.Views;

/// <summary>
/// 独立桌面便签窗口：支持置顶显示、多彩便签底色切换与自动防抖加密保存。
/// </summary>
public sealed partial class StickyNoteWindow : Window
{
    private readonly NoteItem _note;
    private readonly Action? _onSaved;
    private CancellationTokenSource? _saveCts;
    private bool _suppressSave;

    public StickyNoteWindow(NoteItem note, string initialContent, Action? onSaved = null)
    {
        InitializeComponent();
        _note = note;
        _onSaved = onSaved;

        AppWindow.Resize(new SizeInt32(380, 340));
        if (App.AppIconPath is not null) AppWindow.SetIcon(App.AppIconPath);
        Title = $"便签 - {note.Title}";

        _suppressSave = true;
        TitleBox.Text = note.Title;
        ContentBox.Text = initialContent;
        _suppressSave = false;

        ApplyColor(BackgroundPreset.ResolveHex(note.BackgroundColor ?? "sticky_yellow", false));

        TitleBox.LostFocus += async (_, _) =>
        {
            if (TitleBox.Text.Trim() != _note.Title && !string.IsNullOrWhiteSpace(TitleBox.Text))
            {
                _note.Title = TitleBox.Text.Trim();
                await App.Vault.UpdateNoteMetadataAsync(_note);
                _onSaved?.Invoke();
            }
        };
    }

    private void PinToggle_Click(object sender, RoutedEventArgs e)
    {
        if (AppWindow.Presenter is OverlappedPresenter p)
            p.IsAlwaysOnTop = PinToggle.IsChecked == true;
    }

    private async void StickyColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex })
        {
            ApplyColor(hex);
            _note.BackgroundColor = hex;
            await App.Vault.UpdateNoteMetadataAsync(_note);
            _onSaved?.Invoke();
        }
    }

    private void ApplyColor(string hex)
    {
        var color = ParseHexColor(hex);
        RootGrid.Background = new SolidColorBrush(color);
        var isLight = BackgroundPreset.IsLightBackground(hex);
        var fg = new SolidColorBrush(isLight ? Color.FromArgb(255, 31, 35, 40) : Color.FromArgb(255, 230, 237, 243));
        TitleBox.Foreground = fg;
        ContentBox.Foreground = fg;
        StatusText.Foreground = fg;
    }

    private async void ContentBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressSave) return;
        StatusText.Text = "正在保存...";
        _saveCts?.Cancel();
        _saveCts = new CancellationTokenSource();
        var token = _saveCts.Token;

        try
        {
            await Task.Delay(350, token);
            await App.Vault.SaveNoteContentAsync(_note, ContentBox.Text, createRevision: false, ct: token);
            StatusText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
            _onSaved?.Invoke();
        }
        catch (TaskCanceledException) { }
    }

    internal static Color ParseHexColor(string hex)
    {
        var clean = hex.TrimStart('#');
        if (clean.Length == 6)
        {
            return Color.FromArgb(
                255,
                Convert.ToByte(clean[0..2], 16),
                Convert.ToByte(clean[2..4], 16),
                Convert.ToByte(clean[4..6], 16));
        }
        return Color.FromArgb(255, 255, 251, 230);
    }
}
