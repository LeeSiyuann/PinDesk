using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using PinDesk.Core;

namespace PinDesk.Windows;

internal sealed class Dashboard : Window
{
    private readonly WindowService _service;
    private readonly ComboBox _windows = new() { MinWidth = 420, DisplayMemberPath = nameof(WindowChoice.Label) };
    private readonly DataGrid _sessions = new() { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single, MinHeight = 160 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 0), Foreground = Brushes.DarkSlateGray };
    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? PinStarting;

    public Dashboard(WindowService service)
    {
        _service = service;
        Title = "PinDesk · 窗口固定"; Width = 800; Height = 550; MinWidth = 650; MinHeight = 430;
        FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 13; Background = Brushes.WhiteSmoke;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var root = new DockPanel { Margin = new Thickness(24) }; Content = root;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        header.Children.Add(new TextBlock { Text = "让参考窗口持续可见", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "选中目标后按快捷键，或从列表固定。Windows 版操作原窗口。", Margin = new Thickness(0, 8, 0, 18), TextWrapping = TextWrapping.Wrap });
        var chooser = new DockPanel { Margin = new Thickness(0, 0, 0, 16) }; header.Children.Add(chooser);
        var refresh = Button("刷新", (_, _) => RefreshChoices()); DockPanel.SetDock(refresh, Dock.Right); chooser.Children.Add(refresh);
        var pin = Button("固定所选窗口", (_, _) => Perform(() => { if (_windows.SelectedItem is WindowChoice choice) { PinStarting?.Invoke(this, EventArgs.Empty); _service.Pin(choice.Info.Handle); SetStatus("原窗口已置顶。解除后会还原本工具修改的状态。"); } }));
        DockPanel.SetDock(pin, Dock.Right); chooser.Children.Add(pin); chooser.Children.Add(_windows);
        var bottom = new StackPanel(); DockPanel.SetDock(bottom, Dock.Bottom); root.Children.Add(bottom);
        var commands = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) }; bottom.Children.Add(commands);
        commands.Children.Add(Button("解除所选", (_, _) => Perform(() => { if (_sessions.SelectedItem is SessionChoice selected) _service.Unpin(selected.Id); })));
        commands.Children.Add(Button("解除全部", (_, _) => Perform(_service.UnpinAll)));
        commands.Children.Add(Button("设置", (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty)));
        commands.Children.Add(Button("退出工具", (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty)));
        bottom.Children.Add(_status);
        _sessions.Columns.Add(new DataGridTextColumn { Header = "应用", Binding = new Binding(nameof(SessionChoice.Process)), Width = 145 });
        _sessions.Columns.Add(new DataGridTextColumn { Header = "窗口", Binding = new Binding(nameof(SessionChoice.Title)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _sessions.Columns.Add(new DataGridTextColumn { Header = "状态", Binding = new Binding(nameof(SessionChoice.State)), Width = 130 });
        root.Children.Add(_sessions);
        Loaded += (_, _) => { RefreshChoices(); RefreshSessions(); };
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void SetStatus(string message) => _status.Text = message;
    public void RefreshSessions() => _sessions.ItemsSource = _service.Sessions.Select(s => new SessionChoice(s.Id, s.Window.ProcessName, s.Window.Title, s.Window.IsMinimized ? "已最小化" : s.OriginallyTopmost ? "原本已置顶" : "原窗口已置顶")).ToArray();
    private void RefreshChoices() => Perform(() => { _windows.ItemsSource = _service.GetWindows().Select(w => new WindowChoice(w)).ToArray(); if (_windows.Items.Count > 0) _windows.SelectedIndex = 0; });
    private void Perform(Action action) { try { action(); RefreshSessions(); } catch (Exception ex) { SetStatus(ex.Message); } }
    private static Button Button(string text, RoutedEventHandler click) { var b = new Button { Content = text, Padding = new Thickness(12, 7, 12, 7), Margin = new Thickness(0, 0, 8, 0) }; b.Click += click; return b; }
    private sealed record WindowChoice(WindowInfo Info) { public string Label => $"{Info.ProcessName} · {Info.Title}"; }
    private sealed record SessionChoice(Guid Id, string Process, string Title, string State);
}

internal sealed class SettingsDialog : Window
{
    private readonly CheckBox _ctrl = new() { Content = "Ctrl", Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _alt = new() { Content = "Alt", Margin = new Thickness(0, 0, 14, 0) };
    private readonly CheckBox _shift = new() { Content = "Shift", Margin = new Thickness(0, 0, 14, 0) };
    private readonly ComboBox _key = new() { Width = 70, ItemsSource = Enumerable.Range('A', 26).Select(k => ((char)k).ToString()).ToArray() };
    private readonly CheckBox _border = new() { Content = "在置顶窗口周围显示边框", Margin = new Thickness(0, 20, 0, 20) };
    public UserSettings Result { get; private set; }
    public SettingsDialog(UserSettings settings)
    {
        Result = settings; Title = "PinDesk 设置"; Width = 430; Height = 255; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "固定／解除快捷键", FontWeight = FontWeights.SemiBold });
        var keys = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) }; panel.Children.Add(keys);
        foreach (var child in new UIElement[] { _ctrl, _alt, _shift, _key }) keys.Children.Add(child);
        _ctrl.IsChecked = (settings.Modifiers & 2) != 0; _alt.IsChecked = (settings.Modifiers & 1) != 0; _shift.IsChecked = (settings.Modifiers & 4) != 0;
        _key.SelectedItem = ((char)settings.VirtualKey).ToString(); _border.IsChecked = settings.ShowBorder; panel.Children.Add(_border);
        var save = new Button { Content = "保存", Padding = new Thickness(16, 6, 16, 6), HorizontalAlignment = HorizontalAlignment.Right };
        save.Click += (_, _) => { uint modifiers = (_ctrl.IsChecked == true ? 2u : 0) | (_alt.IsChecked == true ? 1u : 0) | (_shift.IsChecked == true ? 4u : 0); if (modifiers == 0) { System.Windows.MessageBox.Show(this, "至少选择一个修饰键。"); return; } Result = settings with { Modifiers = modifiers, VirtualKey = ((string)_key.SelectedItem)[0], ShowBorder = _border.IsChecked == true }; DialogResult = true; };
        panel.Children.Add(save);
    }
}
