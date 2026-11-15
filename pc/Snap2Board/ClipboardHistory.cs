using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snap2Board;

internal sealed record HistoryEntry(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("mime")] string Mime,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("capturedAt")] long CapturedAt);

/// <summary>最近写进剪贴板的图，落盘保存，重启后还能找回上一张。</summary>
internal static class ClipboardHistory
{
    private const int Max = 10;
    private static readonly object Gate = new();
    private static readonly List<HistoryEntry> Items = Load();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath { get; } = System.IO.Path.Combine(AppLog.Root, "history.json");

    public static IReadOnlyList<HistoryEntry> All()
    {
        lock (Gate)
        {
            return Items.ToList();
        }
    }

    public static void Record(HistoryEntry entry)
    {
        lock (Gate)
        {
            Items.RemoveAll(e => e.Path == entry.Path);
            Items.Insert(0, entry);
            while (Items.Count > Max)
            {
                Items.RemoveAt(Items.Count - 1);
            }
            Save();
        }
    }

    public static HistoryEntry? Get(int index)
    {
        lock (Gate)
        {
            return index >= 0 && index < Items.Count ? Items[index] : null;
        }
    }

    private static List<HistoryEntry> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new List<HistoryEntry>();
            var text = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<List<HistoryEntry>>(text, Json) ?? new List<HistoryEntry>();
        }
        catch (Exception ex)
        {
            AppLog.Info($"历史读取失败: {ex.Message}");
            return new List<HistoryEntry>();
        }
    }

    private static void Save()
    {
        try
        {
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items, Json));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            AppLog.Info($"历史保存失败: {ex.Message}");
        }
    }
}
