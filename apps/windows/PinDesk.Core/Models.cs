using System.Text.Json.Serialization;

namespace PinDesk.Core;

/// <summary>A fresh snapshot of a top-level window. No HWND is trusted without its process identity.</summary>
public sealed record WindowInfo(
    nint Handle,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string Title,
    string ProcessName,
    bool IsTopmost,
    bool IsMinimized = false);

/// <summary>
/// The random marker is a property attached to the window, not to its process. Recreated windows in
/// the same process therefore cannot inherit a previous session even when Windows reuses an HWND.
/// </summary>
public sealed record WindowIdentity(long Handle, int ProcessId, long ProcessStartTimeUtcTicks, Guid Marker)
{
    [JsonIgnore]
    public nint NativeHandle => (nint)Handle;
    public static WindowIdentity For(WindowInfo window, Guid marker) =>
        new((long)window.Handle, window.ProcessId, window.ProcessStartTimeUtcTicks, marker);
}

public sealed record PinSession(Guid Id, WindowInfo Window, bool OriginallyTopmost, DateTimeOffset CreatedAtUtc)
{
    public WindowIdentity Identity => WindowIdentity.For(Window, Id);
}

public enum PinResult { Pinned, Unpinned, AlreadyPinned }

public sealed record RecoveryEntry(
    WindowIdentity Identity,
    bool OriginallyTopmost,
    int OwnerProcessId,
    long OwnerProcessStartTimeUtcTicks,
    DateTimeOffset CreatedAtUtc);

public sealed record RecoveryDocument(int Version, IReadOnlyList<RecoveryEntry> Entries)
{
    public static RecoveryDocument Empty { get; } = new(1, []);
}

public sealed record RecoveryResult(WindowIdentity Identity, bool Restored, string Message);

public sealed class WindowOperationException : Exception
{
    public WindowOperationException(string message) : base(message) { }
    public WindowOperationException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class WindowServiceErrorEventArgs(Exception exception) : EventArgs
{
    public Exception Exception { get; } = exception;
}
