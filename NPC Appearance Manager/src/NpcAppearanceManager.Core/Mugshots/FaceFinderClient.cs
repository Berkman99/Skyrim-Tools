using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;

namespace NpcFaceManager.Core.Mugshots;

/// <summary>One face on FaceFinder: an NPC (its ID and plugin, as FaceFinder writes them) as one mod makes it look.</summary>
public sealed record FaceFinderFace(string ModName, string ImageUrl, DateTime UpdatedAt)
{
    public int ModId { get; init; }
    public string? NpcName { get; init; }

    /// <summary>The NPC: "0A2C8E:skyrim.esm" style (see <see cref="FaceFinderCatalog.NpcKey"/>).</summary>
    public string? NpcKey { get; init; }
}

/// <summary>A mod FaceFinder has faces for.</summary>
public sealed record FaceFinderMod(int Id, string Name, int? NexusId, string? Url);

/// <summary>
/// Client for npcfacefinder.com's public API. It's open: no key, plain GETs, 25 results a page. Uncached requests
/// are limited (100 per 10 seconds), so it waits whenever FaceFinder says so (429 + Retry-After).
/// </summary>
public sealed class FaceFinderClient
{
    private const string BaseUrl = "https://npcfacefinder.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly SemaphoreSlim Gate = new(4);

    // Pictures come from FaceFinder's CDN cache (uncached ones are limited to 300 per 10 seconds), so more at once.
    private static readonly SemaphoreSlim ImageGate = new(12);

    /// <summary>All faces FaceFinder knows for one NPC, across every mod.</summary>
    public async Task<IReadOnlyList<FaceFinderFace>> GetFacesForNpcAsync(FormKey npc, CancellationToken ct)
    {
        var formKey = WebUtility.UrlEncode($"{npc.ID:X8}:{npc.ModKey.FileName.String.ToLowerInvariant()}");
        var faces = new List<FaceFinderFace>();
        await foreach (var entry in PagesAsync($"/api/public/v2/npc/faces/search?formKey={formKey}", 20, ct))
            if (ReadFace(entry) is { } face) faces.Add(face);
        return faces;
    }

    /// <summary>Every mod FaceFinder has faces for.</summary>
    public async Task<List<FaceFinderMod>> GetModsAsync(CancellationToken ct)
    {
        var mods = new List<FaceFinderMod>();
        await foreach (var entry in PagesAsync("/api/public/v2/mods/search", 1000, ct))
        {
            if (!entry.TryGetProperty("id", out var id) || !id.TryGetInt32(out var modId)) continue;
            var name = Str(entry, "name");
            if (name == null) continue;
            int? nexus = int.TryParse(Str(entry, "external_id"), out var n) && n > 0 ? n : null;
            mods.Add(new FaceFinderMod(modId, name, nexus, Str(entry, "external_url")));
        }
        return mods;
    }

    /// <summary>Every face of one mod.</summary>
    public async Task<List<FaceFinderFace>> GetModFacesAsync(int modId, CancellationToken ct)
    {
        var faces = new List<FaceFinderFace>();
        await foreach (var entry in PagesAsync($"/api/public/v2/mod/faces/search?modId={modId}", 2000, ct))
            if (ReadFace(entry) is { } face) faces.Add(face with { ModId = face.ModId == 0 ? modId : face.ModId });
        return faces;
    }

    /// <summary>Goes through the pages of a list (1, 2, 3...) until FaceFinder says there are no more.</summary>
    private async IAsyncEnumerable<JsonElement> PagesAsync(string path, int maxPages,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var separator = path.Contains('?') ? '&' : '?';
        for (int page = 1; page <= maxPages; page++)
        {
            using var doc = await GetJsonAsync($"{path}{separator}page={page}", ct);
            if (doc == null) yield break;
            var root = doc.RootElement;
            if (root.TryGetProperty("results", out var results) && results.ValueKind == JsonValueKind.Array)
                foreach (var entry in results.EnumerateArray())
                    yield return entry.Clone();
            bool hasMore = (root.TryGetProperty("hasMore", out var hm) || root.TryGetProperty("has_more", out hm)) &&
                           hm.ValueKind == JsonValueKind.True;
            if (!hasMore) yield break;
        }
    }

    private static FaceFinderFace? ReadFace(JsonElement entry)
    {
        var modName = Str(entry, "mod", "name");
        var image = Str(entry, "images", "full");
        var updated = Str(entry, "updated_at");
        if (modName == null || image == null || updated == null) return null;
        if (!DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt)) return null;
        int modId = entry.TryGetProperty("mod", out var mod) && mod.ValueKind == JsonValueKind.Object &&
                    mod.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) ? i : 0;
        return new FaceFinderFace(modName, image, updatedAt)
        {
            ModId = modId,
            NpcName = Str(entry, "npc", "name"),
            NpcKey = FaceFinderCatalog.NpcKey(Str(entry, "npc", "ref_id"), Str(entry, "npc", "source")),
        };
    }

    /// <summary>Downloads a picture (the caller saves it).</summary>
    public static async Task<byte[]?> GetImageAsync(string url, CancellationToken ct)
    {
        await ImageGate.WaitAsync(ct);
        try
        {
            var bytes = await SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct, asBytes: true) as byte[];
            return bytes is { Length: > 0 } ? bytes : null;
        }
        finally { ImageGate.Release(); }
    }

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            var text = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                return req;
            }, ct, asBytes: false) as string;
            return text == null ? null : JsonDocument.Parse(text);
        }
        catch (JsonException) { return null; }
        finally { Gate.Release(); }
    }

    /// <summary>Waits and retries when rate limited (429) or on server errors; gives up quietly so pictures just fall back.</summary>
    private static async Task<object?> SendWithRetryAsync(Func<HttpRequestMessage> makeRequest, CancellationToken ct, bool asBytes)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var response = await Http.SendAsync(makeRequest(), ct);
                if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(response.StatusCode == HttpStatusCode.TooManyRequests ? 10 : 2 * (attempt + 1));
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
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.GetRawText(),
            _ => null,
        };
    }
}
