using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace NpcFaceManager.Core.Mugshots;

public sealed record FaceFinderFace(string ModName, string ImageUrl, DateTime UpdatedAt);

/// <summary>
/// Client for npcfacefinder.com. The access key is built into the program (users never type one).
/// While <see cref="ApiKey"/> is empty the feature is unavailable and the UI shows it grayed out.
/// </summary>
public sealed class FaceFinderClient
{
    /// <summary>Put the shared FaceFinder key here once you have it. Nothing else needs to change.</summary>
    internal const string ApiKey = "";

    public const string MetadataExtension = ".ffmeta.json";
    private const string BaseUrl = "https://npcfacefinder.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(4);

    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ApiKey);

    /// <summary>All faces FaceFinder knows for one NPC, across every mod.</summary>
    public async Task<IReadOnlyList<FaceFinderFace>> GetFacesForNpcAsync(FormKey npc, CancellationToken ct)
    {
        if (!IsAvailable) return [];
        var faces = new List<FaceFinderFace>();
        var formKey = WebUtility.UrlEncode($"{npc.ID:X8}:{npc.ModKey.FileName.String.ToLowerInvariant()}");

        for (int page = 1; page <= 20; page++)
        {
            using var doc = await GetJsonAsync($"/api/public/v2/npc/faces/search?formKey={formKey}&page={page}", ct);
            if (doc == null) break;
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in results.EnumerateArray())
                {
                    var modName = Str(entry, "mod", "name");
                    var image = Str(entry, "images", "full");
                    var updated = Str(entry, "updated_at");
                    if (modName == null || image == null || updated == null) continue;
                    if (!DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)) continue;
                    faces.Add(new FaceFinderFace(modName, image, updatedAt));
                }
            }
            bool hasMore = (root.TryGetProperty("hasMore", out var hm) || root.TryGetProperty("has_more", out hm)) &&
                           hm.ValueKind == JsonValueKind.True;
            if (!hasMore) break;
        }
        return faces;
    }

    /// <summary>Downloads an image to <paramref name="destination"/> and writes a small metadata file next to it.</summary>
    public async Task<bool> DownloadAsync(FaceFinderFace face, string destination, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var bytes = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, face.ImageUrl), ct, asBytes: true) as byte[];
            if (bytes == null || bytes.Length == 0) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await File.WriteAllBytesAsync(destination, bytes, ct);
            await File.WriteAllTextAsync(destination + MetadataExtension,
                JsonSerializer.Serialize(new { source = "FaceFinder", updatedAt = face.UpdatedAt }), ct);
            return true;
        }
        finally { Gate.Release(); }
    }

    public static DateTime? ReadCachedUpdatedAt(string imagePath)
    {
        try
        {
            var meta = imagePath + MetadataExtension;
            if (!File.Exists(meta)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            return doc.RootElement.TryGetProperty("updatedAt", out var u) ? u.GetDateTime() : null;
        }
        catch { return null; }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var text = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
                req.Headers.Add("X-API-Key", ApiKey);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            }, ct, asBytes: false) as string;
            return text == null ? null : JsonDocument.Parse(text);
        }
        catch (JsonException) { return null; }
        finally { Gate.Release(); }
    }

    /// <summary>Retries on rate limiting (429) and server errors; gives up quietly so pictures just fall back.</summary>
    private static async Task<object?> SendWithRetryAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct, bool asBytes)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var response = await Http.SendAsync(makeRequest(), ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 * (attempt + 1));
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode) return null;
                return asBytes ? await response.Content.ReadAsByteArrayAsync(ct) : await response.Content.ReadAsStringAsync(ct);
            }
            catch (HttpRequestException) { await Task.Delay(1000 * (attempt + 1), ct); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested) { /* timeout: retry */ }
        }
        return null;
    }

    private static string? Str(JsonElement e, params string[] path)
    {
        foreach (var p in path)
        {
            if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(p, out e)) return null;
        }
        return e.ValueKind == JsonValueKind.String ? e.GetString() : null;
    }
}
