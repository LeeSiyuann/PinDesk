using System.IO;
using System.Text.Json;

namespace PinDesk.Windows;

internal sealed record UserSettings(int SchemaVersion = 1, uint Modifiers = 3, uint VirtualKey = 0x50, bool ShowBorder = true)
{
    public string Shortcut => string.Join(" + ", new[] { (Modifiers & 2) != 0 ? "Ctrl" : null, (Modifiers & 1) != 0 ? "Alt" : null, (Modifiers & 4) != 0 ? "Shift" : null, ((char)VirtualKey).ToString() }.Where(s => s is not null));

    public static UserSettings Load(string path)
    {
        if (!File.Exists(path)) return new();
        var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path)) ?? throw new InvalidDataException("设置文件为空。");
        if (settings.SchemaVersion != 1 || settings.Modifiers is 0 or > 7 || settings.VirtualKey is < 0x41 or > 0x5A)
            throw new InvalidDataException("设置格式不受支持，请保留原文件后恢复默认设置。");
        return settings;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}
