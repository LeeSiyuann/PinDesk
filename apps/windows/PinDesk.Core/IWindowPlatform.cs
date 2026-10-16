namespace PinDesk.Core;

/// <summary>OS boundary. Tests use a fake; the shipping app uses Win32WindowPlatform.</summary>
public interface IWindowPlatform : IDisposable
{
    event EventHandler? WindowChanged;
    WindowInfo? GetForeground();
    IReadOnlyList<WindowInfo> GetWindows();
    WindowInfo? ReadWindow(nint handle);
    void SetSessionMarker(WindowInfo expectedWindow, Guid marker);
    bool Matches(WindowIdentity identity);
    void RemoveSessionMarker(WindowIdentity identity);
    void SetTopmost(WindowIdentity identity, bool topmost);
}

public interface IRecoveryJournal
{
    RecoveryDocument Read();
    void Write(RecoveryDocument document);
}
