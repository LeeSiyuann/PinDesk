using PinDesk.Core;

var tests = new (string Name, Action Run)[]
{
    ("Pin writes intent before changing the window", () => WithService((service, os, journal) =>
    {
        os.BeforeSet = () => Assert(journal.Document.Entries.Count == 1, "Recovery intent must exist before mutation.");
        Assert(service.Pin(100) == PinResult.Pinned);
        Assert(os.Windows[100].IsTopmost);
        Assert(service.Sessions.Count == 1);
        Assert(!service.Sessions[0].OriginallyTopmost);
    })),
    ("Pin and unpin are idempotent", () => WithService((service, os, _) =>
    {
        service.Pin(100);
        Assert(service.Pin(100) == PinResult.AlreadyPinned);
        var id = service.Sessions[0].Id;
        service.Unpin(id);
        service.Unpin(id);
        Assert(os.Mutations.SequenceEqual(new[] { true, false }));
        Assert(os.Markers.Count == 0);
    })),
    ("Toggle pins then restores", () => WithService((service, os, journal) =>
    {
        Assert(service.Toggle(100) == PinResult.Pinned);
        Assert(service.Toggle(100) == PinResult.Unpinned);
        Assert(!os.Windows[100].IsTopmost);
        Assert(journal.Document.Entries.Count == 0);
    })),
    ("Originally topmost windows retain their owner policy", () => WithService((service, os, _) =>
    {
        os.Windows[100] = os.Windows[100] with { IsTopmost = true };
        service.Pin(100);
        Assert(service.Sessions[0].OriginallyTopmost);
        service.UnpinAll();
        Assert(os.Windows[100].IsTopmost);
        Assert(os.Mutations.Count == 0);
    })),
    ("Application removing topmost releases session without fighting it", () => WithService((service, os, journal) =>
    {
        service.Pin(100);
        os.Windows[100] = os.Windows[100] with { IsTopmost = false };
        service.Reconcile();
        Assert(service.Sessions.Count == 0);
        Assert(os.Mutations.SequenceEqual(new[] { true }));
        Assert(journal.Document.Entries.Count == 0);
    })),
    ("Closing a window cleans recovery intent", () => WithService((service, os, journal) =>
    {
        service.Pin(100);
        os.Windows.Remove(100);
        os.Markers.Clear();
        service.Reconcile();
        Assert(service.Sessions.Count == 0);
        Assert(journal.Document.Entries.Count == 0);
    })),
    ("Reused HWND in same process without marker is never restored", () => WithService((service, os, _) =>
    {
        service.Pin(100);
        os.Markers.Clear(); // Windows destroys properties when the original window dies.
        os.Windows[100] = os.Windows[100] with { Title = "A different window" };
        service.UnpinAll();
        Assert(os.Windows[100].IsTopmost);
        Assert(os.Mutations.SequenceEqual(new[] { true }));
    })),
    ("Reused PID with different creation time is never restored", () => WithService((service, os, _) =>
    {
        service.Pin(100);
        os.Windows[100] = os.Windows[100] with { ProcessStartTimeUtcTicks = 999 };
        service.UnpinAll();
        Assert(os.Windows[100].IsTopmost);
        Assert(os.Mutations.SequenceEqual(new[] { true }));
    })),
    ("Toggle creates a fresh session when an HWND has been reused", () => WithService((service, os, _) =>
    {
        service.Pin(100);
        var originalId = service.Sessions[0].Id;
        os.Markers.Clear();
        os.Windows[100] = os.Windows[100] with { IsTopmost = false, Title = "Replacement window" };
        Assert(service.Toggle(100) == PinResult.Pinned);
        Assert(service.Sessions[0].Id != originalId);
        Assert(os.Windows[100].IsTopmost);
    })),
    ("Journal failure prevents setting topmost", () => WithService((service, os, journal) =>
    {
        journal.FailWrites = true;
        Throws<WindowOperationException>(() => service.Pin(100));
        Assert(os.Mutations.Count == 0);
        Assert(os.Markers.Count == 0);
        journal.FailWrites = false;
    })),
    ("Native pin failure rolls back marker and intent", () => WithService((service, os, journal) =>
    {
        os.FailPin = true;
        Throws<WindowOperationException>(() => service.Pin(100));
        Assert(service.Sessions.Count == 0);
        Assert(os.Markers.Count == 0);
        Assert(journal.Document.Entries.Count == 0);
    })),
    ("Unpin failure retains recovery ownership for a retry", () => WithService((service, os, journal) =>
    {
        service.Pin(100);
        os.FailRestore = true;
        Throws<AggregateException>(service.UnpinAll);
        Assert(service.Sessions.Count == 1);
        Assert(journal.Document.Entries.Count == 1);
        Assert(os.Markers.Count == 1);
        os.FailRestore = false;
    })),
    ("Changing a title refreshes session details", () => WithService((service, os, _) =>
    {
        service.Pin(100);
        os.Windows[100] = os.Windows[100] with { Title = "Updated title", IsMinimized = true };
        service.Reconcile();
        Assert(service.Sessions[0].Window.Title == "Updated title");
        Assert(service.Sessions[0].Window.IsMinimized);
    })),
    ("Recovery never alters an unmatched window", () =>
    {
        var os = new FakePlatform();
        var entry = Entry(os, false);
        var journal = new MemoryJournal { Document = new(1, [entry]) };
        var result = new RecoveryService(os, journal).Recover(skipLivingOwners: false);
        Assert(!result[0].Restored);
        Assert(os.Mutations.Count == 0);
        Assert(journal.Document.Entries.Count == 0);
    }),
    ("Recovery restores owned topmost and clears marker", () =>
    {
        var os = new FakePlatform();
        var entry = Entry(os, false);
        os.Markers[100] = entry.Identity.Marker;
        os.Windows[100] = os.Windows[100] with { IsTopmost = true };
        var journal = new MemoryJournal { Document = new(1, [entry]) };
        var result = new RecoveryService(os, journal).Recover(skipLivingOwners: false);
        Assert(result[0].Restored);
        Assert(!os.Windows[100].IsTopmost);
        Assert(os.Markers.Count == 0);
        Assert(journal.Document.Entries.Count == 0);
    }),
    ("Recovery preserves originally topmost", () =>
    {
        var os = new FakePlatform();
        var entry = Entry(os, true);
        os.Markers[100] = entry.Identity.Marker;
        os.Windows[100] = os.Windows[100] with { IsTopmost = true };
        var journal = new MemoryJournal { Document = new(1, [entry]) };
        new RecoveryService(os, journal).Recover(skipLivingOwners: false);
        Assert(os.Windows[100].IsTopmost);
        Assert(os.Mutations.Count == 0);
    }),
    ("Recovery retries failed native restoration", () =>
    {
        var os = new FakePlatform { FailRestore = true };
        var entry = Entry(os, false);
        os.Markers[100] = entry.Identity.Marker;
        os.Windows[100] = os.Windows[100] with { IsTopmost = true };
        var journal = new MemoryJournal { Document = new(1, [entry]) };
        var result = new RecoveryService(os, journal).Recover(skipLivingOwners: false);
        Assert(!result[0].Restored);
        Assert(journal.Document.Entries.Count == 1);
        Assert(os.Markers.Count == 1);
    }),
    ("A living owner is not recovered by a second process", () => WithService((service, os, journal) =>
    {
        service.Pin(100);
        var result = new RecoveryService(os, journal).Recover();
        Assert(!result[0].Restored);
        Assert(journal.Document.Entries.Count == 1);
        Assert(os.Windows[100].IsTopmost);
    })),
    ("A new service refuses to overwrite pending recovery", () =>
    {
        var os = new FakePlatform();
        var journal = new MemoryJournal { Document = new(1, [Entry(os, false)]) };
        Throws<WindowOperationException>(() => new WindowService(os, journal));
    }),
    ("Journal atomically round trips identities and overwrites entries", () => WithTempDirectory(directory =>
    {
        var journal = new FileRecoveryJournal(Path.Combine(directory, "recovery.json"));
        Assert(journal.Read().Entries.Count == 0);
        var entry = Entry(new FakePlatform(), false);
        journal.Write(new(1, [entry]));
        Assert(journal.Read().Entries[0] == entry);
        journal.Write(RecoveryDocument.Empty);
        Assert(journal.Read().Entries.Count == 0);
        Assert(Directory.GetFiles(directory).Length == 1);
    })),
    ("Malformed recovery is rejected without touching windows", () => WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "recovery.json");
        File.WriteAllText(path, "{ invalid");
        Throws<WindowOperationException>(() => new FileRecoveryJournal(path).Read());
    })),
    ("A null recovery entry is rejected with a clear error", () => WithTempDirectory(directory =>
    {
        var path = Path.Combine(directory, "recovery.json");
        File.WriteAllText(path, "{\"Version\":1,\"Entries\":[null]}");
        Throws<WindowOperationException>(() => new FileRecoveryJournal(path).Read());
    })),
};

var failures = 0;
foreach (var test in tests)
{
    try { test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception exception) { failures++; Console.Error.WriteLine($"FAIL {test.Name}\n{exception}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} core checks passed.");
return failures == 0 ? 0 : 1;

static void WithService(Action<WindowService, FakePlatform, MemoryJournal> action)
{
    using var os = new FakePlatform();
    var journal = new MemoryJournal();
    using var service = new WindowService(os, journal);
    action(service, os, journal);
}
static void WithTempDirectory(Action<string> action)
{
    var directory = Path.Combine(Path.GetTempPath(), "pindesk-core-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    try { action(directory); }
    finally { Directory.Delete(directory, recursive: true); }
}
static RecoveryEntry Entry(FakePlatform os, bool originallyTopmost) =>
    new(WindowIdentity.For(os.Windows[100], Guid.NewGuid()), originallyTopmost, int.MaxValue, 1, DateTimeOffset.UtcNow);
static void Assert(bool condition, string message = "Assertion failed.")
{ if (!condition) throw new InvalidOperationException(message); }
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

sealed class MemoryJournal : IRecoveryJournal
{
    public RecoveryDocument Document { get; set; } = RecoveryDocument.Empty;
    public bool FailWrites { get; set; }
    public RecoveryDocument Read() => Document;
    public void Write(RecoveryDocument document)
    {
        if (FailWrites) throw new WindowOperationException("Simulated disk failure.");
        Document = document;
    }
}

sealed class FakePlatform : IWindowPlatform
{
    public Dictionary<nint, WindowInfo> Windows { get; } = new() { [100] = new(100, 1234, 1000, "Test window", "TestApp", false) };
    public Dictionary<nint, Guid> Markers { get; } = [];
    public List<bool> Mutations { get; } = [];
    public bool FailPin { get; set; }
    public bool FailRestore { get; set; }
    public Action? BeforeSet { get; set; }
    public event EventHandler? WindowChanged { add { } remove { } }
    public WindowInfo? GetForeground() => ReadWindow(100);
    public IReadOnlyList<WindowInfo> GetWindows() => Windows.Values.ToArray();
    public WindowInfo? ReadWindow(nint handle) => Windows.GetValueOrDefault(handle);
    public void SetSessionMarker(WindowInfo expectedWindow, Guid marker) => Markers[expectedWindow.Handle] = marker;
    public bool Matches(WindowIdentity identity) => ReadWindow(identity.NativeHandle) is { } window &&
        window.ProcessId == identity.ProcessId && window.ProcessStartTimeUtcTicks == identity.ProcessStartTimeUtcTicks &&
        Markers.GetValueOrDefault(identity.NativeHandle) == identity.Marker;
    public void RemoveSessionMarker(WindowIdentity identity) { if (Matches(identity)) Markers.Remove(identity.NativeHandle); }
    public void SetTopmost(WindowIdentity identity, bool topmost)
    {
        BeforeSet?.Invoke();
        if (!Matches(identity)) throw new WindowOperationException("Identity mismatch.");
        if (topmost && FailPin || !topmost && FailRestore) throw new WindowOperationException("Simulated native failure.");
        Mutations.Add(topmost);
        Windows[identity.NativeHandle] = Windows[identity.NativeHandle] with { IsTopmost = topmost };
    }
    public void Dispose() { }
}
