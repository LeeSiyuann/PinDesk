using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace PinDesk.Windows;

internal sealed class GlobalHotkey : IDisposable
{
    private readonly HwndSource _source = new(new HwndSourceParameters("PinDesk.Hotkey") { ParentWindow = new nint(-3), WindowStyle = 0 });
    private int _currentId;
    private uint _modifiers, _key;
    public event EventHandler? Pressed;
    public GlobalHotkey() => _source.AddHook(HandleMessage);

    public void Bind(uint modifiers, uint key)
    {
        if (_currentId != 0 && _modifiers == modifiers && _key == key) return;
        var candidateId = _currentId == 1 ? 2 : 1;
        // Register before releasing the old binding, so a conflict never loses a working shortcut.
        if (!RegisterHotKey(_source.Handle, candidateId, modifiers | 0x4000, key))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "快捷键不可用，请在设置中更换组合。菜单入口仍可使用。");
        if (_currentId != 0) UnregisterHotKey(_source.Handle, _currentId);
        _currentId = candidateId; _modifiers = modifiers; _key = key;
    }

    private nint HandleMessage(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && (int)wParam == _currentId) { handled = true; Pressed?.Invoke(this, EventArgs.Empty); }
        return 0;
    }
    public void Dispose()
    {
        if (_currentId != 0) UnregisterHotKey(_source.Handle, _currentId);
        _source.Dispose();
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
}
