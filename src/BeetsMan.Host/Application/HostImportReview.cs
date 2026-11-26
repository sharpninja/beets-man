using System.Collections.Immutable;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Host.Application;

/// <summary>Holds a match offered by the host's beets adapter before public match IDs are assigned.</summary>
/// <remarks>2026-11-24: Bind the recorded candidate to the approved album choice for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record BeetsImportCandidate(string ProviderKey, string Title, string Artist, ImportMatchComparison? Comparison = null,
    ApprovedBeetsMatch? NativeMatch = null);

/// <summary>Pins the exact native release and displayed tag suggestions before the desktop can approve it.</summary>
/// <remarks>2026-11-24: Preserve release identity and tag comparisons across recovery for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed record ApprovedBeetsMatch(Guid ReleaseId, string Album, string AlbumArtist, string Year, string Month, string Day, string Country,
    ImmutableArray<ApprovedBeetsTrack> Tracks);

/// <summary>Identifies the approved native track and proposed metadata for one staged input ordinal.</summary>
internal sealed record ApprovedBeetsTrack(Guid ReleaseTrackId, string Title, string Artist);

/// <summary>Retains immutable album proposals and approves one choice in the same transaction as its receipt.</summary>
internal interface IImportReviewStore
{
    Task<HostOutcome<ImportJobSnapshot>> OfferAsync(Guid libraryId, Guid jobId, Guid uploadId,
        ImmutableArray<BeetsImportCandidate> candidates, CancellationToken token);

    Task<HostOutcome<ChooseImportReceipt>> ChooseAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        ChooseImportRequest request, CancellationToken token);
}

/// <summary>Publishes proposals from a trusted host worker and accepts choices from the originating desktop.</summary>
/// <remarks>2026-11-24: Enforce the single album review boundary in STB08 for FR-BEETS-001#ac-import and ac-recover.</remarks>
internal sealed class HostImportReview(IImportReviewStore store)
{
    /// <summary>Offers one review of a known staged album, including when no match is available.</summary>
    public Task<HostOutcome<ImportJobSnapshot>> OfferAsync(Guid libraryId, Guid jobId, Guid uploadId,
        ImmutableArray<BeetsImportCandidate> candidates, CancellationToken token)
    {
        if (!ValidProposals(candidates))
            return Task.FromResult<HostOutcome<ImportJobSnapshot>>(new HostOutcome<ImportJobSnapshot>.Rejected("invalid-import-proposals"));
        return store.OfferAsync(libraryId, jobId, uploadId, candidates, token);
    }

    /// <summary>Validates the command and journals it without invoking beets on the request path.</summary>
    public Task<HostOutcome<ChooseImportReceipt>> ChooseAsync(Guid libraryId, Guid jobId, AuthenticatedHostInstance caller,
        ChooseImportRequest request, CancellationToken token)
    {
        if (request.OriginInstanceId != caller.InstanceId)
            return Task.FromResult<HostOutcome<ChooseImportReceipt>>(new HostOutcome<ChooseImportReceipt>.Forbidden());
        if (jobId == Guid.Empty || request.OperationId == Guid.Empty || request.ReviewItemId == Guid.Empty ||
            request.ExpectedRevision <= 0 || !ValidChoice(request.Choice, request.MatchId))
            return Task.FromResult<HostOutcome<ChooseImportReceipt>>(new HostOutcome<ChooseImportReceipt>.Rejected("invalid-import-choice"));
        return store.ChooseAsync(libraryId, jobId, caller, request, token);
    }

    /// <summary>Validates native matcher proposals identically for individual and batched guided imports.</summary>
    internal static bool ValidProposals(ImmutableArray<BeetsImportCandidate> candidates) => !candidates.IsDefault && candidates.All(IsValid) &&
        candidates.Select(candidate => candidate.ProviderKey).Distinct(StringComparer.Ordinal).Count() == candidates.Length;

    private static bool IsValid(BeetsImportCandidate? candidate) => candidate is
        { ProviderKey.Length: > 0 and <= 2048, Title.Length: > 0 and <= 512, Artist.Length: <= 512 } &&
        !string.IsNullOrWhiteSpace(candidate.ProviderKey) && !string.IsNullOrWhiteSpace(candidate.Title) &&
        (candidate.Comparison is null || ValidComparison(candidate.Comparison)) &&
        (candidate.NativeMatch is null || ValidNativeMatch(candidate));

    private static bool ValidNativeMatch(BeetsImportCandidate candidate) => candidate.NativeMatch is { } native &&
        native.ReleaseId != Guid.Empty && $"musicbrainz:{native.ReleaseId:D}" == candidate.ProviderKey &&
        candidate.Comparison is { CanApply: true } comparison && !native.Tracks.IsDefaultOrEmpty && native.Tracks.Length == comparison.Tracks.Length &&
        native.Tracks.All(track => track is not null && track.ReleaseTrackId != Guid.Empty && !string.IsNullOrWhiteSpace(track.Title)) &&
        native.Tracks.Select(track => track.ReleaseTrackId).Distinct().Count() == native.Tracks.Length &&
        native.Tracks.Select(track => track.Title).SequenceEqual(comparison.Tracks.Select(track => track.ProposedTitle));

    private static bool ValidComparison(ImportMatchComparison comparison) =>
        !comparison.Tracks.IsDefaultOrEmpty && (comparison.ReleaseTrackCount is null or > 0) &&
        comparison.Tracks.All(track => track is not null && !string.IsNullOrWhiteSpace(track.OriginalTitle) &&
            (track.ProposedTitle is null ? !comparison.CanApply : !string.IsNullOrWhiteSpace(track.ProposedTitle))) &&
        comparison.Tracks.Select(track => track.SourceIndex).SequenceEqual(Enumerable.Range(0, comparison.Tracks.Length));

    /// <summary>Validates the combination of an explicit action and public match identity.</summary>
    internal static bool ValidChoice(ImportChoiceKind choice, Guid? match) => choice switch
    {
        ImportChoiceKind.UseMatch => match is Guid matchId && matchId != Guid.Empty,
        ImportChoiceKind.KeepTags or ImportChoiceKind.Skip => match is null,
        _ => false
    };
}
