using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Snap2Board;

internal sealed record HubItem(
    long Seq,
    string Id,
    string Sha256,
    string Mime,
    string Name,
    long Size,
    long CapturedAt,
    string Origin);

internal sealed record HubHealth(string Device, int Port);

internal sealed class HubClient
{
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(2),
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    public async Task<(HubHealth? Health, string Error)> ProbeAsync(string host, int port, CancellationToken ct)
    {
        try
        {
            using var response = await _http.GetAsync($"http://{host}:{port}/api/v1/health", ct);
            if (!response.IsSuccessStatusCode) return (null, $"HTTP {(int)response.StatusCode}");
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = json.RootElement;
            if (!root.TryGetProperty("service", out var service) || service.GetString() != "snap2board")
            {
                return (null, "端口上有响应但不是 snap2board");
            }
            var device = root.TryGetProperty("device", out var d) ? d.GetString() ?? "" : "";
            var p = root.TryGetProperty("port", out var pr) ? pr.GetInt32() : port;
            return (new HubHealth(device, p), "");
        }
        catch (OperationCanceledException)
        {
            return (null, "超时（1.5s 内无响应）");
        }
        catch (HttpRequestException ex)
        {
            return (null, ex.Message);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    public async Task<string?> PairAsync(string host, int port, string code, CancellationToken ct)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new { code, clientName = Environment.MachineName });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"http://{host}:{port}/api/v1/pair/claim", content, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return json.RootElement.TryGetProperty("token", out var token) ? token.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    public async Task<List<HubItem>> ItemsSinceAsync(
        string host, int port, string token, long since, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://{host}:{port}/api/v1/items?since={since}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var result = new List<HubItem>();
        if (!json.RootElement.TryGetProperty("items", out var items)) return result;
        foreach (var item in items.EnumerateArray())
        {
            result.Add(new HubItem(
                item.GetProperty("seq").GetInt64(),
                item.GetProperty("id").GetString() ?? "",
                item.TryGetProperty("sha256", out var sha) ? sha.GetString() ?? "" : "",
                item.TryGetProperty("mime", out var mime) ? mime.GetString() ?? "" : "",
                item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                item.TryGetProperty("size", out var size) ? size.GetInt64() : 0,
                item.TryGetProperty("capturedAt", out var captured) ? captured.GetInt64() : 0,
                item.TryGetProperty("origin", out var origin) ? origin.GetString() ?? "" : ""));
        }
        return result;
    }

    public async Task<bool> PushImageAsync(
        string host, int port, string token, byte[] png, string sha256, string name, long capturedAt,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, $"http://{host}:{port}/api/v1/upload");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.TryAddWithoutValidation("X-Sha256", sha256);
        request.Headers.TryAddWithoutValidation("X-Name", name);
        request.Headers.TryAddWithoutValidation("X-Captured-At", capturedAt.ToString());
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        request.Content = content;
        using var response = await _http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    public async Task<byte[]> DownloadAsync(
        string host, int port, string token, string id, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"http://{host}:{port}/api/v1/items/{id}/content");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await _http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(ct);
    }
}
