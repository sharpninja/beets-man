using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Library;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Projects each track's local catalog membership and proposed path from the active batch.</summary>
/// <remarks>2026-11-24: Keep pending organization and removal visible to the originating desktop for FR-LIBRARY-002#ac-batch-local and ac-batch-revert.</remarks>
internal sealed class LocalMaintenanceCatalog
{
    /// <summary>Represents the authoritative projection when there is no local origin scope.</summary>
    public static LocalMaintenanceCatalog Unscoped { get; } = new(null, 0, []);

    private readonly HashSet<Guid> removed = [];
    private readonly Dictionary<Guid, string> proposedPaths = [];

    /// <summary>Folds the ordered maintenance effects for the given origin and batch revision.</summary>
    public LocalMaintenanceCatalog(Guid? origin, long revision, ImmutableArray<LibraryBatchEntry> entries)
    {
        Origin = origin;
        Revision = revision;
        foreach (var entry in entries)
        {
            switch (entry.Change)
            {
                case LibraryBatchChange.Remove removal:
                    removed.UnionWith(removal.Plan.TrackIds);
                    break;
                case LibraryBatchChange.Organize organize:
                    foreach (var move in organize.Plan.Moves)
                        if (move.TrackId != Guid.Empty)
                            proposedPaths[move.TrackId] = move.Destination;
                    break;
            }
        }
    }

    /// <summary>Identifies the desktop scope used when issuing a page cursor.</summary>
    public Guid? Origin { get; }

    /// <summary>Gets the local revision used with the independent authoritative catalog revision.</summary>
    public long Revision { get; }

    /// <summary>Returns whether the working catalog still includes the track.</summary>
    public bool Includes(Guid trackId) => !removed.Contains(trackId);

    /// <summary>Returns the proposed display path or the committed path when no move is staged.</summary>
    public string? RelativePath(Guid trackId, string? committed) => proposedPaths.TryGetValue(trackId, out var proposed) ? proposed : committed;
}
