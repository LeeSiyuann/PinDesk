using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PinDesk.Core;
using PinDesk.Native;

if (!OperatingSystem.IsWindows())
{
    Console.Error.WriteLine("FAIL Windows integration checks require a real Windows desktop session.");
    return 1;
}
if (args.Length == 3 && args[0] == "--crash-pin")
{
    // Deliberately do not dispose: this models a process exiting without its graceful cleanup.
    var childPlatform = new Win32WindowPlatform(enableEvents: false);
    var childService = new WindowService(childPlatform, new FileRecoveryJournal(args[2]));
    childService.Pin((nint)long.Parse(args[1], CultureInfo.InvariantCulture));
    Environment.Exit(86);
    return 86;
}

var testDirectory = Path.Combine(Path.GetTempPath(), "pindesk-win32-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testDirectory);
using var host = new TestWindowHost();
using var platform = new Win32WindowPlatform(excludeCurrentProcess: false, enableEvents: false);
var journal = new FileRecoveryJournal(Path.Combine(testDirectory, "recovery.json"));
using var service = new WindowService(platform, journal);
var checks = 0;
try
{
    Check("Enumerate real top-level test windows", () =>
    {
        var windows = platform.GetWindows();
        Assert(windows.Any(window => window.Handle == host.First));
        Assert(windows.Any(window => window.Handle == host.Second));
    });
    Check("SetWindowPos pins without moving, resizing or activating the target", () =>
    {
        Win32.SetForegroundWindow(host.First);
        WaitUntil(() => Win32.GetForegroundWindow() == host.First, "Could not activate test anchor window on this desktop.");
        var foreground = Win32.GetForegroundWindow();
        Win32.GetWindowRect(host.Second, out var rectangle);
        service.Pin(host.Second);
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == true);
        Assert(Win32.GetForegroundWindow() == foreground, "Pin stole keyboard focus.");
        Win32.GetWindowRect(host.Second, out var after);
        Assert(rectangle.Equals(after), "Pin moved or resized the window.");
        service.UnpinAll();
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == false);
        Assert(Win32.GetForegroundWindow() == foreground, "Unpin stole keyboard focus.");
    });
    Check("Originally topmost window remains topmost after unpin", () =>
    {
        RawTopmost(host.Second, true);
        service.Pin(host.Second);
        Assert(service.Sessions.Single().OriginallyTopmost);
        service.UnpinAll();
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == true);
        RawTopmost(host.Second, false);
    });
    Check("External topmost removal releases ownership", () =>
    {
        service.Pin(host.Second);
        RawTopmost(host.Second, false);
        service.Reconcile();
        Assert(service.Sessions.Count == 0);
        Assert(journal.Read().Entries.Count == 0);
    });
    Check("Minimizing a window keeps the reversible session", () =>
    {
        service.Pin(host.Second);
        Win32.ShowWindow(host.Second, 6); // SW_MINIMIZE
        WaitUntil(() => platform.ReadWindow(host.Second)?.IsMinimized == true, "Minimize was not observed.");
        service.Reconcile();
        Assert(service.Sessions.Count == 1);
        Assert(service.Sessions[0].Window.IsMinimized);
        Win32.ShowWindow(host.Second, 9); // SW_RESTORE
        service.UnpinAll();
    });
    Check("A wrong process identity is rejected before native mutation", () =>
    {
        service.Pin(host.Second);
        var identity = service.Sessions[0].Identity;
        var mismatched = identity with { ProcessStartTimeUtcTicks = identity.ProcessStartTimeUtcTicks + 1 };
        try { platform.SetTopmost(mismatched, false); throw new InvalidOperationException("Expected an identity error."); }
        catch (WindowOperationException) { }
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == true);
        service.UnpinAll();
    });
    Check("An abruptly exited owner is recovered from its flushed journal", () =>
    {
        var crashJournalPath = Path.Combine(testDirectory, "crash.json");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate test executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-pin");
        start.ArgumentList.Add(((long)host.Second).ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add(crashJournalPath);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Cannot launch crash simulation child.");
        Assert(child.WaitForExit(15000), "Crash simulation child did not exit.");
        Assert(child.ExitCode == 86, $"Crash simulation failed with {child.ExitCode}.");
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == true);
        var crashJournal = new FileRecoveryJournal(crashJournalPath);
        Assert(crashJournal.Read().Entries.Count == 1);
        var results = new RecoveryService(platform, crashJournal).Recover();
        Assert(results.Count == 1 && results[0].Restored);
        Assert(platform.ReadWindow(host.Second)?.IsTopmost == false);
        Assert(crashJournal.Read().Entries.Count == 0);
    });
    Check("Destroying a real window clears sessions and invalidates the marker", () =>
    {
        service.Pin(host.Second);
        var identity = service.Sessions[0].Identity;
        Win32.PostMessage(host.Second, 0x0010, 0, 0); // WM_CLOSE
        WaitUntil(() => platform.ReadWindow(host.Second) is null, "Test window did not close.");
        WaitUntil(() => service.Sessions.Count == 0, "Lifecycle reconciliation did not clear the closed window.");
        Assert(!platform.Matches(identity));
        Assert(journal.Read().Entries.Count == 0);
    });
    Console.WriteLine($"{checks}/{checks} real Win32 integration checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"FAIL after {checks} passed checks\n{exception}");
    return 1;
}
finally
{
    try { service.Dispose(); }
    finally { Directory.Delete(testDirectory, recursive: true); }
}

void Check(string name, Action test) { test(); checks++; Console.WriteLine($"PASS {name}"); }
static void Assert(bool condition, string message = "Assertion failed.")
{ if (!condition) throw new InvalidOperationException(message); }
static void WaitUntil(Func<bool> condition, string failure)
{
    var deadline = Stopwatch.StartNew();
    while (!condition())
    {
        if (deadline.Elapsed > TimeSpan.FromSeconds(5)) throw new InvalidOperationException(failure);
        Thread.Sleep(20);
    }
}
static void RawTopmost(nint handle, bool value)
{
    if (!Win32.SetWindowPos(handle, value ? -1 : -2, 0, 0, 0, 0, 0x213))
        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
}

sealed class TestWindowHost : IDisposable
{
    private readonly Thread _thread;
    private uint _threadId;
    private readonly TaskCompletionSource<(nint First, nint Second)> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public nint First { get; }
    public nint Second { get; }

    [SupportedOSPlatform("windows")]
    public TestWindowHost()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PinDesk integration window pump" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        var handles = _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        First = handles.First;
        Second = handles.Second;
    }
    private void Run()
    {
        try
        {
            _threadId = Win32.GetCurrentThreadId();
            const uint style = 0x10CF0000; // WS_VISIBLE | WS_OVERLAPPEDWINDOW
            var first = Win32.CreateWindowEx(0, "STATIC", "PinDesk integration anchor", style, 100, 100, 360, 220, 0, 0, 0, 0);
            var second = Win32.CreateWindowEx(0, "STATIC", "PinDesk integration target", style, 500, 100, 360, 220, 0, 0, 0, 0);
            if (first == 0 || second == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            _ready.TrySetResult((first, second));
            while (Win32.GetMessage(out var message, 0, 0, 0) > 0)
            { Win32.TranslateMessage(ref message); Win32.DispatchMessage(ref message); }
            Win32.DestroyWindow(second);
            Win32.DestroyWindow(first);
        }
        catch (Exception exception) { _ready.TrySetException(exception); }
    }
    public void Dispose()
    {
        Win32.PostThreadMessage(_threadId, 0x0012, 0, 0); // WM_QUIT
        if (!_thread.Join(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Test window pump failed to stop.");
    }
}

static class Win32
{
    [StructLayout(LayoutKind.Sequential)] internal struct Message { public nint Handle; public uint Id; public nuint WParam; public nint LParam; public uint Time; public int X; public int Y; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] internal struct Rect { public int Left; public int Top; public int Right; public int Bottom; }
    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern nint CreateWindowEx(uint extendedStyle, string className, string title, uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DestroyWindow(nint handle);
    [DllImport("user32.dll")] internal static extern int GetMessage(out Message message, nint handle, uint minimum, uint maximum);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TranslateMessage(ref Message message);
    [DllImport("user32.dll")] internal static extern nint DispatchMessage(ref Message message);
    [DllImport("user32.dll", EntryPoint = "PostMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostMessage(nint handle, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll", EntryPoint = "PostThreadMessageW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetForegroundWindow(nint handle);
    [DllImport("user32.dll")] internal static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShowWindow(nint handle, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetWindowRect(nint handle, out Rect rectangle);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);
}
