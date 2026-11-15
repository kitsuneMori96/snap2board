using System.Diagnostics;

namespace Snap2Board;

internal static class AppLog
{
    private static readonly object Gate = new();

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snap2Board");

    public static string MediaRoot { get; } = Path.Combine(Root, "media");

    public static string LogPath { get; } = Path.Combine(Root, "log.txt");

    static AppLog()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MediaRoot);
    }

    public static void Info(string message)
    {
        var line = $"{DateTime.Now:MM-dd HH:mm:ss.fff} {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    File.Move(LogPath, LogPath + ".old", true);
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
