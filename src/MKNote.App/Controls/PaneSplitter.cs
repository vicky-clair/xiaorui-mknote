using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace MKNote.App.Controls;

/// <summary>
/// 支持原生水平双箭头（↔）光标的 WinUI 3 可拖动面板分隔条控件。
/// 悬停与拖拽时自动高亮竖线，拖拽过程以父级容器为坐标系计算偏移以避免抖动，双击可触发恢复默认宽度。
/// </summary>
public sealed class PaneSplitter : Grid
{
    private readonly Border _indicator;
    private bool _isDragging;
    private double _lastX;

    /// <summary>拖动过程中持续触发，参数为本次水平位移增量（像素，向右为正）。</summary>
    public event Action<double>? DragDelta;

    /// <summary>拖动结束（松开鼠标）时触发，可用于持久化保存栏宽。</summary>
    public event Action? DragCompleted;

    /// <summary>双击分隔条时触发，用于快速恢复该侧边栏默认宽度。</summary>
    public event Action? ResetRequested;

    public PaneSplitter()
    {
        Width = 6;
        VerticalAlignment = VerticalAlignment.Stretch;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Background = new SolidColorBrush(Colors.Transparent);

        // 原生 Windows 水平调整大小光标 (↔)
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);

        _indicator = new Border
        {
            Width = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Stretch,
            CornerRadius = new CornerRadius(1),
            Opacity = 0,
            Background = GetAccentBrush()
        };
        Children.Add(_indicator);

        PointerEntered += (_, _) =>
        {
            if (!_isDragging)
            {
                _indicator.Background = GetAccentBrush();
                _indicator.Opacity = 0.55;
            }
        };

        PointerExited += (_, _) =>
        {
            if (!_isDragging)
                _indicator.Opacity = 0;
        };

        PointerPressed += OnSplitterPointerPressed;
        PointerMoved += OnSplitterPointerMoved;
        PointerReleased += OnSplitterPointerReleased;
        PointerCanceled += OnSplitterPointerCanceled;
        PointerCaptureLost += OnSplitterPointerCanceled;

        DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ResetRequested?.Invoke();
        };
    }

    private UIElement? GetCoordinateHost() =>
        VisualTreeHelper.GetParent(this) as UIElement;

    private void OnSplitterPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed) return;

        _isDragging = true;
        _lastX = e.GetCurrentPoint(GetCoordinateHost()).Position.X;
        CapturePointer(e.Pointer);
        _indicator.Background = GetAccentBrush();
        _indicator.Opacity = 0.95;
        e.Handled = true;
    }

    private void OnSplitterPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        var currentX = e.GetCurrentPoint(GetCoordinateHost()).Position.X;
        var delta = currentX - _lastX;
        if (Math.Abs(delta) >= 0.5)
        {
            _lastX = currentX;
            DragDelta?.Invoke(delta);
        }
        e.Handled = true;
    }

    private void OnSplitterPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        ReleasePointerCapture(e.Pointer);
        _indicator.Opacity = 0;
        DragCompleted?.Invoke();
        e.Handled = true;
    }

    private void OnSplitterPointerCanceled(object sender, PointerRoutedEventArgs e)
    {
        if (!_isDragging) return;
        _isDragging = false;
        _indicator.Opacity = 0;
        DragCompleted?.Invoke();
    }

    private static Brush GetAccentBrush()
    {
        if (Application.Current?.Resources.TryGetValue("AccentFillColorDefaultBrush", out var res) == true && res is Brush b)
            return b;
        return new SolidColorBrush(ColorHelper.FromArgb(255, 0, 120, 212));
    }
}
