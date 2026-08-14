using System.Net;
using System.Text.Json;

namespace NodePilot.Core.Clients;

/// <summary>
/// Response plumbing shared by the two HTTP-only clients: every non-2xx becomes an
/// <see cref="ApiException"/> carrying the <c>ProblemDetails</c> title/detail, so callers
/// branch on a single exception type. Each client passes its own
/// <see cref="JsonSerializerOptions"/> — the deserializer settings stay client-owned.
/// </summary>
public static class ApiResponseReader
{
    public static async Task<T> ParseAsync<T>(HttpResponseMessage res, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        await EnsureSuccessAsync(res, ct);
        if (res.StatusCode == HttpStatusCode.NoContent || res.Content.Headers.ContentLength == 0)
            return default!;
        var stream = await res.Content.ReadAsStreamAsync(ct);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, jsonOptions, ct);
        if (value is null) throw new ApiException(res.StatusCode, "EmptyBody", "Server returned empty body.", null);
        return value;
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var body = await res.Content.ReadAsStringAsync(ct);
        string? title = null, detail = null;
        if (!string.IsNullOrWhiteSpace(body) && body.TrimStart().StartsWith('{'))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("title", out var t)) title = t.GetString();
                if (doc.RootElement.TryGetProperty("detail", out var d)) detail = d.GetString();
                if (detail is null && doc.RootElement.TryGetProperty("error", out var e)) detail = e.GetString();
            }
            catch (JsonException) { /* leave body as raw */ }
        }
        throw new ApiException(res.StatusCode, title, detail, body);
    }
}
