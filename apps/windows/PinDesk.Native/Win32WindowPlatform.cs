using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using PinDesk.Core;

namespace PinDesk.Native;

/// <summary>
/// Uses only documented User32 APIs. Session properties disappear when a window is destroyed;
/// PID and process creation time add protection against both HWND and process ID reuse.
/// </summary>
public sealed class Win32WindowPlatform : IWindowPlatform
{
    private const long WsExTopmost = 0x00000008;
    private const long WsExToolwindow = 0x00000080;
    private const long WsExAppwindow = 0x00040000;
    private readonly bool _excludeCurrentProcess;
    private readonly NativeMethods.WinEventDelegate _eventCallback;
    private readonly List<nint> _hooks = [];
    private int _eventPending;
    private volatile bool _disposed;

    /// <param name="enableEvents">Install on a thread with a message loop, such as the WPF dispatcher.</param>
    public Win32WindowPlatform(bool excludeCurrentProcess = true, bool enableEvents = true)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("窗口置顶仅支持 Windows。");
        _excludeCurrentProcess = excludeCurrentProcess;
        _eventCallback = OnWinEvent;
        if (enableEvents)
        {
            // Out-of-context hooks never inject DLLs into another application.
            TryHook(0x0003, 0x0003); // EVENT_SYSTEM_FOREGROUND
            TryHook(0x0016, 0x0017); // EVENT_SYSTEM_MINIMIZESTART / END
            TryHook(0x8000, 0x800C); // Window destruction, show/hide, bounds and title changes.
        }
    }

    public event EventHandler? WindowChanged;

    public WindowInfo? GetForeground()
    {
        ThrowIfDisposed();
        var handle = NativeMethods.GetForegroundWindow();
        handle = NativeMethods.GetAncestor(handle, 2); // GA_ROOT; do not switch to an unrelated owner.
        return IsEligible(handle) ? ReadWindow(handle) : null;
    }

    public IReadOnlyList<WindowInfo> GetWindows()
    {
        ThrowIfDisposed();
        var windows = new List<WindowInfo>();
        NativeMethods.EnumWindows((handle, _) =>
        {
            if (IsEligible(handle) && ReadWindow(handle) is { } window) windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    public WindowInfo? ReadWindow(nint handle)
    {
        ThrowIfDisposed();
        if (handle == 0 || !NativeMethods.IsWindow(handle)) return null;
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (processId == 0) return null;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            var processStart = process.StartTime.ToUniversalTime().Ticks;
            var title = new StringBuilder(2048);
            NativeMethods.GetWindowText(handle, title, title.Capacity);
            // Reading the process may race destruction. Recheck PID before exposing the snapshot.
            NativeMethods.GetWindowThreadProcessId(handle, out var currentProcessId);
            if (!NativeMethods.IsWindow(handle) || currentProcessId != processId) return null;
            return new(handle, (int)processId, processStart, title.ToString(), process.ProcessName,
                (GetExtendedStyle(handle) & WsExTopmost) != 0, NativeMethods.IsIconic(handle));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        { return null; }
    }

    public void SetSessionMarker(WindowInfo expectedWindow, Guid marker)
    {
        ThrowIfDisposed();
        if (marker == Guid.Empty) throw new ArgumentException("会话标记不能为空。", nameof(marker));
        if (!SameProcess(ReadWindow(expectedWindow.Handle), expectedWindow) || !IsEligible(expectedWindow.Handle))
            throw new WindowOperationException("目标窗口已关闭、发生变化或不支持置顶，请重新选择。");
        if (!NativeMethods.SetProp(expectedWindow.Handle, PropertyName(marker), new nint(1)))
            throw NativeFailure("无法标记目标窗口。若该应用以管理员身份运行，请使用相同权限启动 PinDesk");
        if (!Matches(WindowIdentity.For(expectedWindow, marker)))
            throw new WindowOperationException("标记期间目标窗口已变化，请重新选择。");
    }

    public bool Matches(WindowIdentity identity)
    {
        ThrowIfDisposed();
        if (identity.Marker == Guid.Empty || identity.ProcessId <= 0) return false;
        var current = ReadWindow(identity.NativeHandle);
        return current is not null && current.ProcessId == identity.ProcessId &&
            current.ProcessStartTimeUtcTicks == identity.ProcessStartTimeUtcTicks &&
            NativeMethods.GetProp(identity.NativeHandle, PropertyName(identity.Marker)) == new nint(1);
    }

    public void RemoveSessionMarker(WindowIdentity identity)
    {
        ThrowIfDisposed();
        if (!Matches(identity)) return;
        NativeMethods.RemoveProp(identity.NativeHandle, PropertyName(identity.Marker));
        if (NativeMethods.GetProp(identity.NativeHandle, PropertyName(identity.Marker)) != 0)
            throw NativeFailure("无法清除窗口会话标记");
    }

    public void SetTopmost(WindowIdentity identity, bool topmost)
    {
        ThrowIfDisposed();
        if (!Matches(identity)) throw new WindowOperationException("窗口身份已经变化，已取消操作以避免修改其他窗口。");
        // Avoid synchronously sending window-position messages to an already hung application.
        if (NativeMethods.SendMessageTimeout(identity.NativeHandle, 0, 0, 0, 0x0003, 300, out _) == 0)
            throw NativeFailure("目标应用未响应，请稍后重试");
        if (!Matches(identity)) throw new WindowOperationException("目标窗口已在操作前关闭。");
        const uint flags = 0x0001 | 0x0002 | 0x0010 | 0x0200; // NOSIZE | NOMOVE | NOACTIVATE | NOOWNERZORDER
        if (!NativeMethods.SetWindowPos(identity.NativeHandle, topmost ? new nint(-1) : new nint(-2), 0, 0, 0, 0, flags))
            throw NativeFailure("无法改变窗口置顶状态。目标应用可能以更高权限运行");
        var current = ReadWindow(identity.NativeHandle);
        if (!Matches(identity) || current?.IsTopmost != topmost)
            throw new WindowOperationException("系统未保留请求的窗口层级，目标应用可能自行管理置顶状态。");
    }

    private bool IsEligible(nint handle)
    {
        if (handle == 0 || !NativeMethods.IsWindow(handle) || !NativeMethods.IsWindowVisible(handle) ||
            handle == NativeMethods.GetDesktopWindow() || handle == NativeMethods.GetShellWindow()) return false;
        NativeMethods.GetWindowThreadProcessId(handle, out var processId);
        if (_excludeCurrentProcess && processId == (uint)Environment.ProcessId) return false;
        var style = GetExtendedStyle(handle);
        if ((style & WsExToolwindow) != 0 && (style & WsExAppwindow) == 0) return false;
        if (NativeMethods.GetWindow(handle, 4) != 0 && (style & WsExAppwindow) == 0) return false; // GW_OWNER
        if (NativeMethods.GetWindowTextLength(handle) == 0) return false;
        try
        {
            if (NativeMethods.DwmGetWindowAttribute(handle, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                return false;
        }
        catch (DllNotFoundException) { }
        return ReadWindow(handle) is not null;
    }

    private static bool SameProcess(WindowInfo? actual, WindowInfo expected) => actual is not null &&
        actual.ProcessId == expected.ProcessId && actual.ProcessStartTimeUtcTicks == expected.ProcessStartTimeUtcTicks;
    private static string PropertyName(Guid marker) => $"PinDesk.Session.{marker:N}";
    private static long GetExtendedStyle(nint handle) => nint.Size == 8
        ? (long)NativeMethods.GetWindowLongPtr(handle, -20) : NativeMethods.GetWindowLong(handle, -20);
    private static WindowOperationException NativeFailure(string message)
    {
        var error = Marshal.GetLastWin32Error();
        return new($"{message}（Windows 错误 {error}：{new Win32Exception(error).Message}）。");
    }
    private void TryHook(uint minimum, uint maximum)
    {
        var hook = NativeMethods.SetWinEventHook(minimum, maximum, 0, _eventCallback, 0, 0, 0);
        if (hook != 0) _hooks.Add(hook); // The service's periodic reconcile covers unavailable hooks.
    }
    private void OnWinEvent(nint hook, uint eventType, nint handle, int objectId, int childId, uint thread, uint time)
    {
        if (_disposed || handle == 0 || (eventType >= 0x8000 && (objectId != 0 || childId != 0))) return;
        if (Interlocked.Exchange(ref _eventPending, 1) != 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { if (!_disposed) WindowChanged?.Invoke(this, EventArgs.Empty); }
            finally { Interlocked.Exchange(ref _eventPending, 0); }
        });
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var hook in _hooks) NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_eventCallback);
        GC.SuppressFinalize(this);
    }
}

internal static class NativeMethods
{
    internal delegate bool EnumWindowsDelegate(nint handle, nint parameter);
    internal delegate void WinEventDelegate(nint hook, uint eventType, nint handle, int objectId, int childId, uint eventThread, uint eventTime);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowsDelegate callback, nint parameter);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern nint GetAncestor(nint handle, uint flags);
    [DllImport("user32.dll")] internal static extern nint GetDesktopWindow();
    [DllImport("user32.dll")] internal static extern nint GetShellWindow();
    [DllImport("user32.dll")] internal static extern nint GetWindow(nint handle, uint command);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsIconic(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW", CharSet = CharSet.Unicode)] internal static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] internal static extern nint GetWindowLongPtr(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] internal static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll", EntryPoint = "SetPropW", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProp(nint handle, string name, nint data);
    [DllImport("user32.dll", EntryPoint = "GetPropW", CharSet = CharSet.Unicode)] internal static extern nint GetProp(nint handle, string name);
    [DllImport("user32.dll", EntryPoint = "RemovePropW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint RemoveProp(nint handle, string name);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)] internal static extern nint SendMessageTimeout(nint handle, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);
    [DllImport("dwmapi.dll")] internal static extern int DwmGetWindowAttribute(nint handle, uint attribute, out int value, int size);
    [DllImport("user32.dll")] internal static extern nint SetWinEventHook(uint minimum, uint maximum, nint module, WinEventDelegate callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(nint hook);
}
