using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace PinDesk.Windows;

internal sealed class PinBorder : Window
{
    private readonly nint _target;
    public PinBorder(nint target)
    {
        _target = target;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        IsHitTestVisible = false; Width = 100; Height = 100;
        Content = new Border { BorderBrush = new SolidColorBrush(Color.FromRgb(50, 117, 230)), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(5) };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtrW(hwnd, -20);
            SetWindowLongPtrW(hwnd, -20, new nint(style.ToInt64() | 0x08000000 | 0x20 | 0x80));
        };
    }

    public void Synchronize()
    {
        if (!IsWindowVisible(_target) || IsIconic(_target) || !GetWindowRect(_target, out var bounds)) { Hide(); return; }
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        // User32 uses physical pixels under PerMonitorV2; avoid mixing monitor coordinates with WPF DIPs.
        SetWindowPos(hwnd, new nint(-1), bounds.Left - 2, bounds.Top - 2, bounds.Right - bounds.Left + 4, bounds.Bottom - bounds.Top + 4, 0x10 | 0x40);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
}
