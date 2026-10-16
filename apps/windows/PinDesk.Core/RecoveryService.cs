using System.Diagnostics;

namespace PinDesk.Core;

/// <summary>Restores only windows still carrying this exact session's marker and process identity.</summary>
public sealed class RecoveryService(IWindowPlatform platform, IRecoveryJournal journal)
{
    public IReadOnlyList<RecoveryResult> Recover(bool skipLivingOwners = true)
    {
        var document = journal.Read();
        var remaining = new List<RecoveryEntry>();
        var results = new List<RecoveryResult>();
        foreach (var entry in document.Entries)
        {
            if (skipLivingOwners && IsProcessAlive(entry.OwnerProcessId, entry.OwnerProcessStartTimeUtcTicks))
            {
                remaining.Add(entry);
                results.Add(new(entry.Identity, false, "拥有此会话的 PinDesk 进程仍在运行，已跳过。"));
                continue;
            }
            try
            {
                if (!platform.Matches(entry.Identity))
                {
                    results.Add(new(entry.Identity, false, "窗口已关闭或身份不匹配，无需恢复。"));
                    continue;
                }
                // Originally-topmost windows belong to their applications. Never demote them.
                if (!entry.OriginallyTopmost)
                {
                    var current = platform.ReadWindow(entry.Identity.NativeHandle);
                    if (current?.IsTopmost == true) platform.SetTopmost(entry.Identity, false);
                }
                platform.RemoveSessionMarker(entry.Identity);
                results.Add(new(entry.Identity, true, "已恢复窗口原始置顶状态。"));
            }
            catch (Exception exception)
            {
                // Preserve failed entries so the next launch can retry instead of losing ownership.
                remaining.Add(entry);
                results.Add(new(entry.Identity, false, $"恢复失败：{exception.Message}"));
            }
        }
        journal.Write(new(1, remaining));
        return results;
    }

    public static bool IsProcessAlive(int processId, long expectedStartTimeUtcTicks)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == expectedStartTimeUtcTicks;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { return false; }
    }
}
