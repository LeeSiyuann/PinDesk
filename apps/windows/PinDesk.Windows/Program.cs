using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows;
using System.Windows.Threading;
using PinDesk.Core;
using PinDesk.Native;
using Forms = System.Windows.Forms;

namespace PinDesk.Windows;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 5 && args[0] == "--recover") return Recover(args);
        var dataIndex = Array.IndexOf(args, "--data-dir");
        var dataDirectory = dataIndex >= 0 && dataIndex + 1 < args.Length ? Path.GetFullPath(args[dataIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PinDesk");
        using var mutex = new Mutex(false, MutexName(dataDirectory));
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(0); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { System.Windows.MessageBox.Show("PinDesk 已在运行，请使用系统托盘打开。", "PinDesk"); return 0; }
            Directory.CreateDirectory(dataDirectory);
            return new PinDeskApp(dataDirectory).Run();
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message, "PinDesk 启动失败"); return 1; }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private static int Recover(string[] args)
    {
        try
        {
            if (int.TryParse(args[1], out var pid) && long.TryParse(args[2], out var start))
            {
                try
                {
                    using var parent = Process.GetProcessById(pid);
                    if (parent.StartTime.ToUniversalTime().Ticks == start) parent.WaitForExit();
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { /* Already exited: continue recovery. */ }
            }
            // A new GUI instance and an old watchdog must never race read/modify/write on the journal.
            using var mutex = new Mutex(false, args[4]);
            var acquired = false;
            try
            {
                try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
                if (!acquired) return 0;
                using var platform = new Win32WindowPlatform(enableEvents: false);
                var journal = new FileRecoveryJournal(args[3]);
                new RecoveryService(platform, journal).Recover();
                return journal.Read().Entries.Count == 0 ? 0 : 1;
            }
            finally { if (acquired) mutex.ReleaseMutex(); }
        }
        catch { return 1; } // Preserve the journal for the next visible startup recovery attempt.
    }

    internal static string MutexName(string directory) => "Local\\PinDesk." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())))[..20];
}

internal sealed class PinDeskApp(string dataDirectory) : System.Windows.Application
{
    private Win32WindowPlatform _platform = null!;
    private WindowService _service = null!;
    private FileRecoveryJournal _journal = null!;
    private GlobalHotkey _hotkey = null!;
    private Forms.NotifyIcon _tray = null!;
    private Dashboard _dashboard = null!;
    private UserSettings _settings = new();
    private readonly Dictionary<Guid, PinBorder> _borders = [];
    private DispatcherTimer _borderTimer = null!;
    private bool _closing, _watchdogStarted;
    private string SettingsPath => Path.Combine(dataDirectory, "settings.json");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e); ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _platform = new Win32WindowPlatform(); _journal = new FileRecoveryJournal(Path.Combine(dataDirectory, "recovery.json"));
        new RecoveryService(_platform, _journal).Recover();
        _service = new WindowService(_platform, _journal);
        _dashboard = new Dashboard(_service);
        _dashboard.SettingsRequested += (_, _) => ShowSettings(); _dashboard.QuitRequested += (_, _) => ExitSafely();
        _dashboard.PinStarting += (_, _) => EnsureWatchdog();
        _service.Changed += (_, _) => Dispatcher.BeginInvoke(() => { if (!_closing) { _dashboard.RefreshSessions(); SyncBorders(); } });
        _service.Faulted += (_, error) => Dispatcher.BeginInvoke(() => Report(error.Exception.Message));
        _borderTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => SyncBorders(), Dispatcher); _borderTimer.Stop();
        _hotkey = new GlobalHotkey(); _hotkey.Pressed += (_, _) => ToggleForeground();
        var menu = new Forms.ContextMenuStrip();
        // Opening a tray menu changes foreground ownership. Choose explicitly instead of guessing its old target.
        menu.Items.Add("选择窗口固定", null, (_, _) => Dispatcher.Invoke(ShowDashboard));
        menu.Items.Add("管理窗口", null, (_, _) => Dispatcher.Invoke(ShowDashboard));
        menu.Items.Add("解除全部", null, (_, _) => Dispatcher.Invoke(() => Attempt(_service.UnpinAll)));
        menu.Items.Add("设置", null, (_, _) => Dispatcher.Invoke(ShowSettings));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Dispatcher.Invoke(ExitSafely));
        _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "PinDesk 窗口固定", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowDashboard);
        _dashboard.SetStatus($"快捷键：{_settings.Shortcut}。关闭此窗口后继续在托盘运行。");
        try { _settings = UserSettings.Load(SettingsPath); _dashboard.SetStatus($"快捷键：{_settings.Shortcut}。关闭此窗口后继续在托盘运行。"); } catch (Exception ex) { Report(ex.Message); }
        Attempt(() => _hotkey.Bind(_settings.Modifiers, _settings.VirtualKey));
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        if (smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length)
        {
            _dashboard.ShowActivated = false; _dashboard.ShowInTaskbar = false;
            _dashboard.WindowStartupLocation = WindowStartupLocation.Manual; _dashboard.Left = -10000; _dashboard.Top = -10000;
            _dashboard.Show();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            timer.Tick += (_, _) =>
            {
                timer.Stop(); _dashboard.UpdateLayout();
                var bitmap = new RenderTargetBitmap((int)_dashboard.ActualWidth, (int)_dashboard.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(_dashboard);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using (var image = File.Create(e.Args[smokeIndex + 1])) encoder.Save(image);
                ExitSafely();
            };
            timer.Start();
        }
        else ShowDashboard();
    }

    private void ToggleForeground() => Attempt(() =>
    {
        var window = _service.GetForeground();
        if (window is null) { ShowDashboard(); Report("请选择普通应用窗口，或从窗口列表固定。"); return; }
        EnsureWatchdog();
        var result = _service.Toggle(window.Handle);
        Report(result == PinResult.Unpinned ? "已解除固定。" : "原窗口已置顶。", balloon: true);
    });

    private void EnsureWatchdog()
    {
        if (_watchdogStarted) return;
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位恢复助手入口。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(typeof(Program).Assembly.Location);
        using var process = Process.GetCurrentProcess();
        foreach (var arg in new[] { "--recover", Environment.ProcessId.ToString(), process.StartTime.ToUniversalTime().Ticks.ToString(), _journal.PathName, Program.MutexName(dataDirectory) }) start.ArgumentList.Add(arg);
        using var watchdog = Process.Start(start) ?? throw new InvalidOperationException("无法启动恢复助手，已停止固定操作。");
        _watchdogStarted = true;
    }

    private void ShowDashboard() { _dashboard.Show(); _dashboard.Activate(); }
    private void ShowSettings()
    {
        var dialog = new SettingsDialog(_settings) { Owner = _dashboard.IsVisible ? _dashboard : null };
        if (dialog.ShowDialog() != true) return;
        Attempt(() =>
        {
            var previous = _settings;
            _hotkey.Bind(dialog.Result.Modifiers, dialog.Result.VirtualKey);
            try { dialog.Result.Save(SettingsPath); }
            catch { _hotkey.Bind(previous.Modifiers, previous.VirtualKey); throw; }
            _settings = dialog.Result; SyncBorders(); Report($"设置已保存。快捷键：{_settings.Shortcut}");
        });
    }

    private void SyncBorders()
    {
        if (_closing) return;
        var sessions = _settings.ShowBorder ? _service.Sessions : [];
        var active = sessions.Select(s => s.Id).ToHashSet();
        foreach (var id in _borders.Keys.Where(id => !active.Contains(id)).ToArray()) { _borders[id].Close(); _borders.Remove(id); }
        foreach (var session in sessions)
        {
            if (!_borders.TryGetValue(session.Id, out var border)) _borders.Add(session.Id, border = new PinBorder(session.Window.Handle));
            border.Synchronize();
        }
        if (sessions.Count > 0) _borderTimer.Start(); else _borderTimer.Stop();
    }

    private void Attempt(Action action) { try { action(); } catch (Exception ex) { Report(ex.Message, balloon: true); } }
    private void Report(string message, bool balloon = false)
    {
        _dashboard.SetStatus(message);
        if (balloon && _tray is not null) { _tray.BalloonTipTitle = "PinDesk"; _tray.BalloonTipText = message; _tray.ShowBalloonTip(2500); }
    }
    private void ExitSafely()
    {
        if (_closing) return; _closing = true;
        _borderTimer.Stop(); _hotkey.Dispose();
        foreach (var border in _borders.Values) border.Close();
        try { _service.Dispose(); } catch (Exception ex) { System.Windows.MessageBox.Show(ex.Message + "\n恢复记录已保留，下次启动会重试。", "PinDesk"); }
        _platform.Dispose(); _tray.Visible = false; _tray.Dispose(); Shutdown();
    }
}
