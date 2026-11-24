using Microsoft.Win32;

namespace MKNote.App.Services;

/// <summary>
/// 当前用户级 Windows 开机自启注册服务（写入 HKCU\Software\Microsoft\Windows\CurrentVersion\Run），无需管理员权限即可启用或关闭开机静默驻留托盘。
/// </summary>
public static class AutoStartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string AppValueName = "MKNoteNext";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            var val = key?.GetValue(AppValueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunKeyPath);

        if (enable)
        {
            var exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath))
            {
                key.SetValue(AppValueName, $"\"{exePath}\" --autostart", RegistryValueKind.String);
            }
        }
        else
        {
            key.DeleteValue(AppValueName, throwOnMissingValue: false);
        }
    }
}
