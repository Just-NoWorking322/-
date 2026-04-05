using Microsoft.Win32;

namespace NurMarketKassa.Services;

public static class AutostartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "NurMarketKassa";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, false);
            var v = k?.GetValue(ValueName) as string;
            return !string.IsNullOrEmpty(v);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enable)
    {
        try
        {
            using var k = Registry.CurrentUser.CreateSubKey(RunKey, true);
            if (k == null)
                return;
            if (!enable)
            {
                k.DeleteValue(ValueName, false);
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
                return;
            k.SetValue(ValueName, $"\"{exe}\"");
        }
        catch
        {
            /* ignore */
        }
    }

    public static void SyncFromPreference(bool wantAutostart)
    {
        if (wantAutostart)
            SetEnabled(true);
        else
            SetEnabled(false);
    }
}
