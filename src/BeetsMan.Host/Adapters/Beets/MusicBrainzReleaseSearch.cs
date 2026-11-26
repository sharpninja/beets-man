using System.Collections.Immutable;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeetsMan.Host.Application;

namespace BeetsMan.Host.Adapters.Beets;

/// <summary>Identifies an exact MusicBrainz release and its searchable, operator-visible edition details.</summary>
/// <remarks>2026-11-24: Preserve candidate identity while discovering guided import matches for FR-BEETS-001#ac-import.</remarks>
internal sealed record MusicBrainzRelease(Guid Id, string Title, string Artist, string? Date, string? Country, int? TrackCount)
{
    public string ProviderKey => $"musicbrainz:{Id:D}";
}

/// <summary>Retrieves ranked release IDs using only the staged album and album-artist tags.</summary>
/// <remarks>2026-11-24: Search public release metadata for FR-BEETS-001#ac-import without sending paths or audio.</remarks>
internal sealed class MusicBrainzReleaseSearch(HttpClient client, Uri server, TimeProvider time)
{
    private const int Limit = 5;
    private const long ResponseLimit = 1024 * 1024;
    private readonly SemaphoreSlim pace = new(1, 1);
    private long? previousRequest;

    /// <summary>Returns distinct release candidates in search order.</summary>
    public async Task<HostOutcome<ImmutableArray<MusicBrainzRelease>>> SearchAsync(string album, string? albumArtist, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(album))
            return new HostOutcome<ImmutableArray<MusicBrainzRelease>>.Success([]);
        var query = "release:" + Quote(album.Trim());
        if (!string.IsNullOrWhiteSpace(albumArtist))
            query += " AND artist:" + Quote(albumArtist.Trim());
        var path = $"/ws/2/release?fmt=json&limit={Limit}&query={Uri.EscapeDataString(query)}";

        try
        {
            await WaitForRequestAsync(token).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(server, path));
            request.Headers.UserAgent.ParseAdd("BeetsMan/1.0 (+https://github.com/sharpninja/beets-man)");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Unavailable(response.StatusCode == System.Net.HttpStatusCode.TooManyRequests ? "musicbrainz-rate-limited" : "musicbrainz-search-unavailable");
            await response.Content.LoadIntoBufferAsync(ResponseLimit, token).ConfigureAwait(false);
            var returned = await response.Content.ReadFromJsonAsync<SearchResult>(token).ConfigureAwait(false);
            if (returned?.Releases is null)
                return Unavailable("musicbrainz-search-malformed");
            var candidates = returned.Releases.Select(Parse).OfType<MusicBrainzRelease>().DistinctBy(release => release.Id).Take(Limit);
            return new HostOutcome<ImmutableArray<MusicBrainzRelease>>.Success([.. candidates]);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException ||
            error is OperationCanceledException && !token.IsCancellationRequested)
        {
            return Unavailable("musicbrainz-search-unavailable");
        }
    }

    /// <summary>Spaces discovery requests and native single-release preview lookups on this host.</summary>
    internal async Task WaitForRequestAsync(CancellationToken token)
    {
        await pace.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (previousRequest is long previous)
            {
                var remaining = TimeSpan.FromSeconds(1) - time.GetElapsedTime(previous);
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining, time, token).ConfigureAwait(false);
            }
            previousRequest = time.GetTimestamp();
        }
        finally { pace.Release(); }
    }

    internal Uri Endpoint => server;

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static MusicBrainzRelease? Parse(ReleaseDocument? release)
    {
        if (release is null || !Guid.TryParseExact(release.Id, "D", out var id) || id == Guid.Empty ||
            string.IsNullOrWhiteSpace(release.Title) || release.Title.Length > 512)
            return null;
        var artist = string.Concat((release.ArtistCredit ?? []).OfType<Credit>().Select(credit => credit.Name + credit.JoinPhrase));
        if (artist.Length > 512)
            return null;
        return new(id, release.Title, artist, release.Date, release.Country, release.TrackCount > 0 ? release.TrackCount : null);
    }

    private static HostOutcome<ImmutableArray<MusicBrainzRelease>> Unavailable(string code) =>
        new HostOutcome<ImmutableArray<MusicBrainzRelease>>.Unavailable(code);

    private sealed record SearchResult(ReleaseDocument?[]? Releases);
    private sealed record ReleaseDocument(string? Id, string? Title, string? Date, string? Country,
        [property: JsonPropertyName("track-count")] int? TrackCount,
        [property: JsonPropertyName("artist-credit")] Credit?[]? ArtistCredit);
    private sealed record Credit(string? Name, [property: JsonPropertyName("joinphrase")] string? JoinPhrase);
}
