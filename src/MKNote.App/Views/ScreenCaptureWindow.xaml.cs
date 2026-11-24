using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using MKNote.App.Services;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Imaging;
using Windows.System;

namespace MKNote.App.Views;

/// <summary>
/// 全虚拟屏幕交互式区域截图遮罩窗口。
/// 支持多显示器高 DPI 框选，选区结束后直接在内存中编码为 PNG 字节流并加密存入本地知识库（无磁盘明文临时文件）。
/// </summary>
public sealed partial class ScreenCaptureWindow : Window
{
    private readonly CapturedScreenBuffer _buffer;
    private readonly TaskCompletionSource<byte[]?> _tcs = new();
    private bool _dragging;
    private Point _startPt;
    private Point _endPt;

    public ScreenCaptureWindow(CapturedScreenBuffer buffer)
    {
        InitializeComponent();
        _buffer = buffer;

        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.SetBorderAndTitleBar(false, false);
            p.IsAlwaysOnTop = true;
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
        }

        AppWindow.MoveAndResize(new RectInt32(buffer.Left, buffer.Top, buffer.Width, buffer.Height));

        Closed += (_, _) => _tcs.TrySetResult(null);
        OverlayRoot.Loaded += OverlayRoot_Loaded;
    }

    public Task<byte[]?> CaptureAsync()
    {
        Activate();
        return _tcs.Task;
    }

    private async void OverlayRoot_Loaded(object sender, RoutedEventArgs e)
    {
        var sb = new SoftwareBitmap(BitmapPixelFormat.Bgra8, _buffer.Width, _buffer.Height, BitmapAlphaMode.Premultiplied);
        sb.CopyFromBuffer(_buffer.BgraPixels.AsBuffer());
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(sb);
        SnapshotImage.Source = source;
        OverlayRoot.Focus(FocusState.Programmatic);
    }

    private void OverlayRoot_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            _tcs.TrySetResult(null);
            Close();
        }
    }

    private void OverlayRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        var props = e.GetCurrentPoint(OverlayRoot).Properties;
        if (props.IsRightButtonPressed)
        {
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        _dragging = true;
        _startPt = e.GetCurrentPoint(OverlayRoot).Position;
        _endPt = _startPt;
        OverlayRoot.CapturePointer(e.Pointer);
        UpdateSelectionVisual();
    }

    private void OverlayRoot_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _endPt = e.GetCurrentPoint(OverlayRoot).Position;
        UpdateSelectionVisual();
    }

    private async void OverlayRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        OverlayRoot.ReleasePointerCapture(e.Pointer);
        _endPt = e.GetCurrentPoint(OverlayRoot).Position;

        var scale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1.0;
        var x = (int)Math.Round(Math.Min(_startPt.X, _endPt.X) * scale);
        var y = (int)Math.Round(Math.Min(_startPt.Y, _endPt.Y) * scale);
        var w = (int)Math.Round(Math.Abs(_endPt.X - _startPt.X) * scale);
        var h = (int)Math.Round(Math.Abs(_endPt.Y - _startPt.Y) * scale);

        if (w < 6 || h < 6)
        {
            _tcs.TrySetResult(null);
            Close();
            return;
        }

        var pngBytes = await ScreenCaptureService.EncodeRegionToPngAsync(_buffer, x, y, w, h);
        _tcs.TrySetResult(pngBytes);
        Close();
    }

    private void UpdateSelectionVisual()
    {
        var left = Math.Min(_startPt.X, _endPt.X);
        var top = Math.Min(_startPt.Y, _endPt.Y);
        var w = Math.Abs(_endPt.X - _startPt.X);
        var h = Math.Abs(_endPt.Y - _startPt.Y);

        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, left);
        Canvas.SetTop(SelectionRect, top);
        SelectionRect.Width = w;
        SelectionRect.Height = h;

        var scale = OverlayRoot.XamlRoot?.RasterizationScale ?? 1.0;
        InfoText.Text = $"{(int)Math.Round(w * scale)} × {(int)Math.Round(h * scale)} px · 松开鼠标完成截图 · Esc 取消";
        Canvas.SetLeft(InfoBadge, Math.Max(12, left));
        Canvas.SetTop(InfoBadge, top > 44 ? top - 38 : top + h + 10);
    }
}
