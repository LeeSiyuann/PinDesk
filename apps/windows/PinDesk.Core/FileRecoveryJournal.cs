using System.Text.Json;

namespace PinDesk.Core;

/// <summary>
/// Writes recovery intent before changing another application's window. A flushed temporary file is
/// replaced atomically on the same volume, so an interrupted save cannot create partial JSON.
/// The app and watchdog must not write concurrently: the watchdog waits for its owner to exit.
/// </summary>
public sealed class FileRecoveryJournal(string path) : IRecoveryJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path = Path.GetFullPath(path);
    public string PathName => _path;

    public RecoveryDocument Read()
    {
        if (!File.Exists(_path)) return RecoveryDocument.Empty;
        try
        {
            using var stream = File.OpenRead(_path);
            var document = JsonSerializer.Deserialize<RecoveryDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("恢复日志为空。");
            if (document.Version != 1 || document.Entries is null)
                throw new InvalidDataException("不支持此恢复日志版本。");
            foreach (var entry in document.Entries)
            {
                if (entry is null || entry.Identity is null || entry.Identity.Handle == 0 || entry.Identity.ProcessId <= 0 ||
                    entry.Identity.ProcessStartTimeUtcTicks <= 0 || entry.Identity.Marker == Guid.Empty ||
                    entry.OwnerProcessId <= 0 || entry.OwnerProcessStartTimeUtcTicks <= 0)
                    throw new InvalidDataException("恢复日志包含无效窗口身份。");
            }
            return document;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            throw new WindowOperationException($"无法读取恢复日志：{_path}。为避免修改错误窗口，已停止恢复。", exception);
        }
    }

    public void Write(RecoveryDocument document)
    {
        var directory = Path.GetDirectoryName(_path)!;
        var temporaryPath = System.IO.Path.Combine(directory, $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new WindowOperationException("无法保存窗口恢复日志。请检查磁盘空间与本地应用数据目录权限。", exception);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }
}
