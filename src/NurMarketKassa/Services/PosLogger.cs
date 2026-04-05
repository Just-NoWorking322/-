using System.IO;

namespace NurMarketKassa.Services;

/// <summary>Файловый лог рядом с exe (<c>app_debug.log</c>) — оплата, печать, OSK.</summary>
public static class PosLogger
{
    private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app_debug.log");

    private static readonly object FileLock = new();

    public static void Log(string message, string category = "INFO")
    {
        try
        {
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{category}] {message}{Environment.NewLine}";
            lock (FileLock)
                File.AppendAllText(LogPath, line);
        }
        catch
        {
            /* не роняем кассу из-за лога */
        }
    }
}
