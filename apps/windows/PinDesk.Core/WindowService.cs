using System.Diagnostics;

namespace PinDesk.Core;

/// <summary>
/// Owns reversible pin sessions, not an application's topmost policy. If an application removes
/// topmost itself we release our session instead of repeatedly fighting its desired state.
/// Changed and Faulted can be raised on a background thread; GUI callers must dispatch them.
/// </summary>
public sealed class WindowService : IDisposable
{
    private readonly object _gate = new();
    private readonly IWindowPlatform _platform;
    private readonly IRecoveryJournal _journal;
    private readonly Dictionary<Guid, PinSession> _sessions = [];
    private readonly int _ownerProcessId = Environment.ProcessId;
    private readonly long _ownerStartTime;
    private readonly Timer _reconcileTimer;
    private bool _disposed;

    public WindowService(IWindowPlatform platform, IRecoveryJournal journal, string ownerName = "PinDesk")
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(journal);
        _platform = platform;
        _journal = journal;
        using var process = Process.GetCurrentProcess();
        _ownerStartTime = process.StartTime.ToUniversalTime().Ticks;
        // The host must run RecoveryService before creating a fresh service. Refuse to overwrite
        // unrecovered intent, including sessions that belong to a separate running instance.
        if (_journal.Read().Entries.Count > 0)
            throw new WindowOperationException($"{ownerName} 存在未处理的恢复记录，请先运行恢复流程。");
        _platform.WindowChanged += PlatformWindowChanged;
        _reconcileTimer = new Timer(_ => ReconcileSafely(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public event EventHandler? Changed;
    public event EventHandler<WindowServiceErrorEventArgs>? Faulted;
    public IReadOnlyList<PinSession> Sessions { get { lock (_gate) return _sessions.Values.ToArray(); } }
    public WindowInfo? GetForeground() => _platform.GetForeground();
    public IReadOnlyList<WindowInfo> GetWindows() => _platform.GetWindows();

    public PinResult Toggle(nint handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = _sessions.Values.FirstOrDefault(session => session.Window.Handle == handle);
            if (existing is not null && _platform.Matches(existing.Identity))
            { Unpin(existing.Id); return PinResult.Unpinned; }
            if (existing is not null) Release(existing, restore: false);
            return Pin(handle);
        }
    }

    public PinResult Pin(nint handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            var existing = _sessions.Values.FirstOrDefault(session => session.Window.Handle == handle);
            if (existing is not null && _platform.Matches(existing.Identity)) return PinResult.AlreadyPinned;
            if (existing is not null) Release(existing, restore: false);
            var window = _platform.ReadWindow(handle)
                ?? throw new WindowOperationException("目标窗口已关闭或不可访问，请重新选择窗口。");
            var marker = Guid.NewGuid();
            _platform.SetSessionMarker(window, marker);
            var identity = WindowIdentity.For(window, marker);
            var markedWindow = _platform.ReadWindow(handle);
            if (!_platform.Matches(identity) || markedWindow is null)
                throw new WindowOperationException("目标窗口在创建会话期间已关闭，请重新选择。");
            // Sample the original policy as late as possible, after attaching our identity marker.
            var session = new PinSession(marker, markedWindow, markedWindow.IsTopmost, DateTimeOffset.UtcNow);
            _sessions.Add(session.Id, session);
            try
            {
                // Durable recovery intent precedes SetWindowPos, including its crash window.
                Persist();
                if (!session.OriginallyTopmost) _platform.SetTopmost(session.Identity, true);
                var current = _platform.ReadWindow(handle);
                if (!_platform.Matches(session.Identity) || current?.IsTopmost != true)
                    throw new WindowOperationException("窗口未能保持置顶；目标应用可能限制窗口层级。");
                _sessions[session.Id] = session with { Window = current };
            }
            catch
            {
                // A failed verification can follow a successful native mutation. Restore using the
                // same guarded release path; if restoration fails retain intent for the watchdog.
                try { Release(session, restore: true); }
                catch { /* Keep the original error; any retained entry remains recoverable. */ }
                throw;
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return PinResult.Pinned;
    }

    public void Unpin(Guid sessionId)
    {
        bool changed;
        lock (_gate)
        {
            ThrowIfDisposed();
            changed = _sessions.TryGetValue(sessionId, out var session);
            if (session is not null) Release(session, restore: true);
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Unpin(nint handle)
    {
        var session = Sessions.FirstOrDefault(item => item.Window.Handle == handle);
        if (session is not null) Unpin(session.Id);
    }

    public void UnpinAll()
    {
        List<Exception> failures = [];
        lock (_gate)
        {
            ThrowIfDisposed();
            foreach (var session in _sessions.Values.ToArray())
                try { Release(session, restore: true); }
                catch (Exception exception) { failures.Add(exception); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
        if (failures.Count > 0)
            throw new AggregateException("部分窗口无法恢复，已保留恢复记录供下次启动重试。", failures);
    }

    public void Reconcile()
    {
        var changed = false;
        lock (_gate)
        {
            if (_disposed) return;
            foreach (var session in _sessions.Values.ToArray())
            {
                if (!_platform.Matches(session.Identity)) { Release(session, restore: false); changed = true; continue; }
                var current = _platform.ReadWindow(session.Window.Handle);
                if (current is null || !current.IsTopmost)
                { Release(session, restore: false); changed = true; continue; }
                if (current != session.Window)
                { _sessions[session.Id] = session with { Window = current }; changed = true; }
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Release(PinSession session, bool restore)
    {
        if (_platform.Matches(session.Identity))
        {
            if (restore && !session.OriginallyTopmost && _platform.ReadWindow(session.Window.Handle)?.IsTopmost == true)
                _platform.SetTopmost(session.Identity, false);
            _platform.RemoveSessionMarker(session.Identity);
        }
        _sessions.Remove(session.Id);
        Persist();
    }

    private void Persist() => _journal.Write(new(1, _sessions.Values.Select(session =>
        new RecoveryEntry(session.Identity, session.OriginallyTopmost, _ownerProcessId, _ownerStartTime, session.CreatedAtUtc)).ToArray()));

    private void PlatformWindowChanged(object? sender, EventArgs args) => ReconcileSafely();
    private void ReconcileSafely()
    {
        try { Reconcile(); }
        catch (Exception exception) { Faulted?.Invoke(this, new(exception)); }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _reconcileTimer.Dispose();
            _platform.WindowChanged -= PlatformWindowChanged;
            try { UnpinAll(); }
            finally { _disposed = true; }
        }
        // Platform lifetime belongs to its host, which also uses it for recovery.
        GC.SuppressFinalize(this);
    }
}
