using System.Collections.Immutable;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Reports the current confirmed connection of a registered Android peer.</summary>
/// <remarks>2026-11-24: Separate historical reconciliation from live STB21 arrival for FR-FILESYNC-001#ac-arrival.</remarks>
public sealed record AndroidArrivalPeer(Guid PeerId, bool Connected, long? ArrivedAtRevision);

/// <summary>Carries a current host-epoch snapshot and a cursor to wait for its next change.</summary>
public sealed record AndroidArrivalSnapshot(Guid Epoch, long Revision, bool Baseline, ImmutableArray<AndroidArrivalPeer> Peers);

/// <summary>Defines the authenticated Android connection observation route.</summary>
public static class AndroidArrivalRoute
{
    public const string ReadPattern = "/api/v1/libraries/{libraryId:guid}/media-sync/android-arrivals";

    /// <summary>Returns the relative route for the registered library.</summary>
    public static string ReadPath(Guid libraryId) => $"api/v1/libraries/{libraryId:D}/media-sync/android-arrivals";
}
