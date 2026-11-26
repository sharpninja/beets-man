using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Describes one reconciled beets item with its verified staged-input ordinal and catalog metadata.</summary>
/// <remarks>2026-11-24: Publish only reconciled import results for FR-BEETS-001#ac-recover and FR-RECORDS-001#ac-replicate.</remarks>
/// <remarks>2026-11-24: Preserve the observed disc number and year for FR-BEETS-001#ac-edit.</remarks>
internal sealed record ObservedImportTrack(int InputIndex, long BeetsTrackId, string RelativePath, string Title, string? Artist,
    int? TrackNumber, long? DurationTicks, string? Genre,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? DiscNumber = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Year = null);

/// <summary>Binds verified beets effects to the approved job, upload and choice operation.</summary>
internal sealed record ReconciledImportAlbum(Guid LibraryId, Guid JobId, Guid UploadId, Guid ChoiceOperationId, long JobRevision,
    string? ProviderKey, long BeetsAlbumId, string Title, string? AlbumArtist, ImmutableArray<ObservedImportTrack> Tracks);

/// <summary>Atomically projects verified beets results and publishes their originating job and feed revision.</summary>
internal interface IImportCatalogPublicationStore
{
    /// <summary>Commits one complete album or returns the original result of an identical committed operation.</summary>
    Task<HostOutcome<ImportJobSnapshot>> CommitImportPublicationAsync(ReconciledImportAlbum observed, string fingerprint, CancellationToken token);
}

/// <summary>Validates reconciled results before they reach the host catalog transaction.</summary>
/// <remarks>2026-11-24: Prevent partial or ambiguous native effects from advancing the catalog for FR-BEETS-001#ac-recover.</remarks>
internal sealed class ImportCatalogPublication(IImportCatalogPublicationStore store)
{
    /// <summary>Publishes a canonical album observation once, scoped to its journaled beets operation.</summary>
    public Task<HostOutcome<ImportJobSnapshot>> PublishAsync(ReconciledImportAlbum observed, CancellationToken token)
    {
        if (Normalize(observed) is not { } normalized)
            return Reject("invalid-import-reconciliation");
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(normalized)));
        return store.CommitImportPublicationAsync(normalized, fingerprint, token);
    }

    /// <summary>Validates the native album and orders its tracks by the uploaded file ordinal.</summary>
    /// <remarks>2026-11-24: Share import validation with the approved batch transaction for FR-LIBRARY-002#ac-batch-commit.</remarks>
    internal static ReconciledImportAlbum? Normalize(ReconciledImportAlbum observed)
    {
        if (observed.LibraryId == Guid.Empty || observed.JobId == Guid.Empty || observed.UploadId == Guid.Empty ||
            observed.ChoiceOperationId == Guid.Empty || observed.JobRevision <= 0 || observed.BeetsAlbumId <= 0 ||
            string.IsNullOrWhiteSpace(observed.Title) || observed.Tracks.IsDefaultOrEmpty || observed.Tracks.Any(track => !ValidTrack(track)))
            return null;

        var tracks = observed.Tracks.OrderBy(track => track.InputIndex).ToImmutableArray();
        if (!tracks.Select(track => track.InputIndex).SequenceEqual(Enumerable.Range(0, tracks.Length)) ||
            tracks.Select(track => track.BeetsTrackId).Distinct().Count() != tracks.Length ||
            tracks.Select(track => track.RelativePath).Distinct(PathComparer).Count() != tracks.Length)
            return null;
        return observed with { Tracks = tracks };
    }

    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool ValidTrack(ObservedImportTrack? track) => track is { BeetsTrackId: > 0, RelativePath: { Length: > 0 } path } &&
        !string.IsNullOrWhiteSpace(track.Title) && (track.TrackNumber is null or > 0) && (track.DurationTicks is null or >= 0) &&
        (track.DiscNumber is null or > 0) && (track.Year is null or (> 0 and <= 9999)) &&
        !Path.IsPathRooted(path) && !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) &&
        path.Split('/').All(segment => segment is not ("" or "." or ".."));

    private static Task<HostOutcome<ImportJobSnapshot>> Reject(string code) =>
        Task.FromResult<HostOutcome<ImportJobSnapshot>>(new HostOutcome<ImportJobSnapshot>.Rejected(code));
}
