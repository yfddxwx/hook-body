using System.IO;

namespace AuthIsland.Cli;

/// <summary>
/// 安装位置与运行时路径。默认装到 %LOCALAPPDATA%\AuthIsland，
/// 这样不碰注册表、不需要管理员权限，卸载就是删目录。
/// </summary>
public static class AppPaths
{
    public static string Home { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "AuthIsland");

    public static string TargetExe => Path.Combine(Home, "AuthIsland.exe");

    public static string HookLog => Path.Combine(Home, "hook.log");

    public static string ErrorLog => Path.Combine(Home, "error.log");

    public static string SettingsJson => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".workbuddy", "settings.json");

    public static bool IsInstalled
    {
        get
        {
            try
            {
                var exe = Environment.ProcessPath ?? "";
                return string.Equals(Path.GetFullPath(exe), Path.GetFullPath(TargetExe), StringComparison.OrdinalIgnoreCase)
                       && File.Exists(Path.Combine(Home, ".installed"));
            }
            catch
            {
                return false;
            }
        }
    }

    public static void EnsureHome()
    {
        try { Directory.CreateDirectory(Home); } catch { }
    }
}
