using System.Collections.Immutable;
using System.Globalization;

namespace BeetsMan.Host.Contracts.V1;

/// <summary>Captures one stable recovery operation for the authenticated recipient.</summary>
/// <remarks>2026-11-24: Bind snapshot recovery to the observed cursor for FR-RECORDS-001#ac-replicate and ac-offline.</remarks>
public sealed record BeginCatalogSnapshot(Guid RecipientInstanceId, Guid SnapshotId, long ExpectedCursor, int Limit);

/// <summary>Preserves the last authoritative change to an entity within a replacement snapshot.</summary>
public sealed record CatalogSnapshotRecord(long SourceRevision, CatalogFeedChange Change);

/// <summary>Contains a consecutive segment of the catalog captured at one upper revision.</summary>
public sealed record CatalogSnapshotPage(Guid LibraryId, Guid RecipientInstanceId, Guid SnapshotId, long UpperRevision, int TotalCount, int Offset,
    ImmutableArray<CatalogSnapshotRecord> Records)
{
    /// <summary>Returns the offset for the next request or the total count for the final page.</summary>
    public int NextOffset => checked(Offset + Records.Length);
}

/// <summary>Addresses authenticated snapshot capture and the pages of an existing capture.</summary>
/// <remarks>2026-11-24: Keep the fixed snapshot and its recipient scope for FR-RECORDS-001#ac-replicate.</remarks>
public static class CatalogSnapshotRoutes
{
    public const string BeginPattern = "/api/v1/libraries/{libraryId:guid}/catalog/snapshots";
    public const string PagePattern = BeginPattern + "/{snapshotId:guid}";

    /// <summary>Addresses snapshot capture for the registered library.</summary>
    public static string BeginPath(Guid library) => $"api/v1/libraries/{library:D}/catalog/snapshots";

    /// <summary>Addresses a bounded segment of the captured records.</summary>
    public static string PagePath(Guid library, Guid snapshot, int offset, int limit) =>
        string.Create(CultureInfo.InvariantCulture, $"{BeginPath(library)}/{snapshot:D}?offset={offset}&limit={limit}");
}

/// <summary>Checks the fixed scope, page interval, and live entities of a replacement snapshot.</summary>
/// <remarks>2026-11-24: Reject incomplete or malformed replacement input for FR-RECORDS-001#ac-replicate.</remarks>
public static class CatalogSnapshotProtocol
{
    /// <summary>Verifies the current page before it is staged.</summary>
    public static bool IsValid(CatalogSnapshotPage page) => page.LibraryId != Guid.Empty && page.RecipientInstanceId != Guid.Empty &&
        page.SnapshotId != Guid.Empty && page.UpperRevision >= 0 && page.Offset >= 0 && page.TotalCount >= page.Offset &&
        !page.Records.IsDefault && page.Records.Length <= CatalogFeedProtocol.MaximumPageSize && page.Records.Length <= page.TotalCount - page.Offset &&
        (!page.Records.IsEmpty || page.Offset == page.TotalCount) && page.Records.All(record => record is not null &&
            record.SourceRevision > 0 && record.SourceRevision <= page.UpperRevision &&
            record.Change is { Operation: CatalogEntityOperation.Upsert } or
                { Operation: CatalogEntityOperation.Delete, Kind: CatalogEntityKind.Favorite or CatalogEntityKind.SharedDefault or CatalogEntityKind.Play } &&
            CatalogFeedProtocol.ValidChange(record.Change, page.LibraryId));
}
