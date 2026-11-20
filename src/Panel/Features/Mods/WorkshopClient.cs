using System.Text.Json;

namespace Panel.Features.Mods;

public sealed record WorkshopDetails(long Id, bool Ok, string? Title, long? FileSize, DateTime? TimeUpdated, string? PreviewUrl, long? AppId);

public interface IWorkshopClient
{
    Task<WorkshopDetails[]> DetailsAsync(IReadOnlyList<long> ids, CancellationToken ct = default);
}

// Steam's public GetPublishedFileDetails (no API key; Phase 5a Ruling 10), 100 items per request.
public sealed class WorkshopClient(HttpClient http) : IWorkshopClient
{
    public const string Url = "https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/";

    public async Task<WorkshopDetails[]> DetailsAsync(IReadOnlyList<long> ids, CancellationToken ct = default)
    {
        var all = new List<WorkshopDetails>();
        foreach (var chunk in ids.Distinct().Chunk(100))
        {
            var form = new List<KeyValuePair<string, string>> { new("itemcount", chunk.Length.ToString()) };
            form.AddRange(chunk.Select((id, i) => new KeyValuePair<string, string>($"publishedfileids[{i}]", id.ToString())));
            using var response = await http.PostAsync(Url, new FormUrlEncodedContent(form), ct);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            // Anything but the documented shape is Steam misbehaving; callers report a JsonException as Steam being unreachable.
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("response", out var body) || body.ValueKind != JsonValueKind.Object
                || !body.TryGetProperty("publishedfiledetails", out var details) || details.ValueKind != JsonValueKind.Array)
                throw new JsonException("Steam's reply had no publishedfiledetails list.");
            foreach (var d in details.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object))
                all.Add(new WorkshopDetails(Long(d, "publishedfileid") ?? 0, Long(d, "result") == 1, Text(d, "title"), Long(d, "file_size"),
                    Long(d, "time_updated") is { } t ? DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime : null, Text(d, "preview_url"), Long(d, "consumer_app_id")));
        }
        return all.ToArray();
    }

    // Steam sends some numbers as strings.
    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number when v.TryGetInt64(out var n) => n,
                JsonValueKind.String when long.TryParse(v.GetString(), out var s) => s,
                _ => null,
            }
            : null;

    private static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
