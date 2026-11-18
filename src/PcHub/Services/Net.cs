using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcHub.Services;

public static class PortProbe
{
    /// <summary>All TCP ports something is listening on (IPv4 and IPv6).</summary>
    public static HashSet<int> ListeningPorts()
    {
        var set = new HashSet<int>();
        try
        {
            foreach (var ep in System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners())
            {
                set.Add(ep.Port);
            }
        }
        catch (Exception ex) when (ex is System.Net.NetworkInformation.NetworkInformationException or PlatformNotSupportedException
                                       or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Table unavailable: tools just show "Running" instead of "Ready".
        }

        return set;
    }

    /// <summary>True when something accepts TCP connections on 127.0.0.1:port within the timeout.</summary>
    public static async Task<bool> IsOpenAsync(int port, int timeoutMs = 300)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(timeoutMs);
        try
        {
            await client.ConnectAsync("127.0.0.1", port, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException)
        {
            return false;
        }
    }
}

public sealed class OllamaModel
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("size_vram")] public long SizeVram { get; set; }
}

public sealed class OllamaPsResponse
{
    [JsonPropertyName("models")] public List<OllamaModel> Models { get; set; } = new();
}

public sealed class OllamaUnloadRequest
{
    [JsonPropertyName("model")] public string Model { get; set; } = "";
    [JsonPropertyName("keep_alive")] public int KeepAlive { get; set; }
}

[JsonSerializable(typeof(OllamaPsResponse))]
[JsonSerializable(typeof(OllamaUnloadRequest))]
internal sealed partial class OllamaJson : JsonSerializerContext;

/// <summary>Talks to a local Ollama server to list and unload models (unloading frees their VRAM).</summary>
public static class OllamaClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<IReadOnlyList<OllamaModel>> LoadedModelsAsync(int port, CancellationToken ct = default)
    {
        var resp = await Http.GetFromJsonAsync($"http://127.0.0.1:{port}/api/ps", OllamaJson.Default.OllamaPsResponse, ct).ConfigureAwait(false);
        return resp?.Models ?? [];
    }

    /// <summary>Unloads every loaded model. Returns the names unloaded; throws if the server can't be reached.</summary>
    public static async Task<IReadOnlyList<string>> UnloadAllAsync(int port, CancellationToken ct = default)
    {
        var models = await LoadedModelsAsync(port, ct).ConfigureAwait(false);
        var done = new List<string>();
        foreach (var m in models)
        {
            string id = string.IsNullOrEmpty(m.Model) ? m.Name : m.Model;
            var body = new OllamaUnloadRequest { Model = id, KeepAlive = 0 };
            using var resp = await Http.PostAsJsonAsync($"http://127.0.0.1:{port}/api/generate", body, OllamaJson.Default.OllamaUnloadRequest, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            done.Add(id);
        }

        return done;
    }
}
