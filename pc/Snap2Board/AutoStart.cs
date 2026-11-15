using Microsoft.Win32;

namespace Snap2Board;

internal static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Snap2Board";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            AppLog.Info($"读取开机自启状态失败: {ex.Message}");
            return false;
        }
    }

    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(ValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(ValueName, false);
            }
            AppLog.Info(enabled ? "已开启开机自启" : "已关闭开机自启");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Info($"设置开机自启失败: {ex.Message}");
            return false;
        }
    }
}
