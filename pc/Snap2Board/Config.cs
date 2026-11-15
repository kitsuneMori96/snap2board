using System.Text.Json;

namespace Snap2Board;

internal sealed class Config
{
    public string Token { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; } = 28642;
    public long Cursor { get; set; }

    private static readonly object Gate = new();

    private static string Path => System.IO.Path.Combine(AppLog.Root, "config.json");

    public static Config Load()
    {
        try
        {
            if (File.Exists(Path))
            {
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(Path)) ?? new Config();
            }
        }
        catch
        {
        }
        return new Config();
    }

    public void Save()
    {
        lock (Gate)
        {
            try
            {
                File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch
            {
            }
        }
    }
}
