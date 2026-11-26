using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Stores the latest library record or tombstone and its originating publication receipt.</summary>
/// <remarks>2026-11-25: Keep the record, feed revision, and deliveries atomic for FR-RECORDS-001#ac-replicate, ac-notify, and ac-offline.</remarks>
public sealed partial class LiteDbHostPlaylistStore : IHostLibraryRecordStore
{
    private const string ListeningRecords = "listening_records";
    private const string SharedPreferences = "shared_preferences";
    private const string LibraryRecordReceiptType = "LibraryRecord";

    /// <inheritdoc />
    Task<HostOutcome<LibraryRecordReceipt>> IHostLibraryRecordStore.PublishRecordAsync(Guid library, AuthenticatedHostInstance caller,
        PublishLibraryRecordRequest request, CancellationToken token) => ExecuteAsync<LibraryRecordReceipt>(_ =>
    {
        if (!IsImportCaller(library, caller) || request.OriginInstanceId != caller.InstanceId)
            return new HostOutcome<LibraryRecordReceipt>.Forbidden();

        var origin = new HostOperationKey(caller.InstanceId, request.OperationId);
        var operation = OperationId(origin);
        var fingerprint = HashKey($"library-record/v1/{library:N}/" + JsonSerializer.Serialize(request.Change));
        var receipts = database.GetCollection<BsonDocument>("operation_receipts");
        if (receipts.FindById(operation) is { } prior)
        {
            var stored = prior["Receipt"].AsDocument;
            return Text(prior, "IntentFingerprint") == fingerprint && Text(stored, "Type") == LibraryRecordReceiptType && stored["Version"].AsInt32 == 1
                ? new HostOutcome<LibraryRecordReceipt>.Success(ReadStored<LibraryRecordReceipt>(stored["Result"])) : Conflict("operation-intent-mismatch");
        }

        var (kind, id, expected, payload) = DescribeLibraryRecord(library, request.Change);
        var table = database.GetCollection<BsonDocument>(kind == CatalogEntityKind.SharedDefault ? SharedPreferences : ListeningRecords);
        var existing = table.FindById(id);
        if (existing is not null && (existing["LibraryId"].AsGuid != library || Text(existing, "Kind") != kind.ToString()))
            return Conflict("library-record-scope-conflict");
        var revision = existing?["Revision"].AsInt64 ?? 0;
        if (expected.HasValue && expected.Value != revision)
            return Conflict("stale-library-record", revision);

        var remove = payload is null;
        var active = existing is not null && !IsLibraryRecordDeleted(existing);
        var sameValue = active && payload is not null && payload.All(field => existing![field.Key] == field.Value);
        if (kind == CatalogEntityKind.Play && existing is not null && !sameValue)
            return Conflict("play-identity-conflict", revision);

        var head = ReadFeedHead(database, library);
        var current = head["CurrentRevision"].AsInt64;
        if (remove ? active : !sameValue)
        {
            revision = current = checked(current + 1);
            var row = payload ?? new BsonDocument { ["Deleted"] = true };
            row["_id"] = id;
            row["LibraryId"] = library;
            row["Kind"] = kind.ToString();
            row["Revision"] = revision;
            table.Upsert(row);
            var change = remove ? new BsonDocument { ["Version"] = 1, ["Kind"] = kind.ToString(), ["Operation"] = "Delete", ["Payload"] = row } :
                FeedUpsert(kind.ToString(), row);
            AppendFeed(database, head, revision, origin, new BsonArray(change));
        }

        var receipt = new LibraryRecordReceipt(library, origin.OriginInstanceId, origin.OperationId, id, revision, current);
        receipts.Insert(new BsonDocument { ["_id"] = operation, ["OriginInstanceId"] = origin.OriginInstanceId, ["OperationId"] = origin.OperationId,
            ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument { ["Type"] = LibraryRecordReceiptType, ["Version"] = 1,
                ["Result"] = JsonSerializer.Serialize(receipt) } });
        return new HostOutcome<LibraryRecordReceipt>.Success(receipt);

        static HostOutcome<LibraryRecordReceipt> Conflict(string code, long? actual = null) => new HostOutcome<LibraryRecordReceipt>.Rejected(code, actual, Conflict: true);
    }, token);

    /// <summary>Builds the typed canonical record body before it is combined with its scope and revision.</summary>
    private static (CatalogEntityKind Kind, Guid Id, long? Expected, BsonDocument? Payload) DescribeLibraryRecord(Guid library, LibraryRecordIntent change) => change switch
    {
        LibraryRecordIntent.Favorite favorite => (CatalogEntityKind.Favorite, LibraryRecordIdentity.Favorite(library, favorite.TrackId), favorite.ExpectedRevision,
            favorite.IsFavorite ? new BsonDocument { ["TrackId"] = favorite.TrackId, ["ListenerId"] = library } : null),
        LibraryRecordIntent.Play play => (CatalogEntityKind.Play, play.PlayId, null,
            new BsonDocument { ["TrackId"] = play.TrackId, ["ListenerId"] = library, ["PlayedAtUtcTicks"] = play.PlayedAtUtc.UtcTicks }),
        LibraryRecordIntent.SharedDefault setting => (CatalogEntityKind.SharedDefault, LibraryRecordIdentity.SharedDefault(library, setting.Key),
            setting.ExpectedRevision, DescribeSharedPreference(setting)),
        _ => throw new InvalidDataException("The validated library record is unknown.")
    };

    /// <summary>Stores one registered value in its native BSON type.</summary>
    /// <remarks>2026-11-25: Keep the shared notification flag typed for FR-SETTINGS-001#ac-availability.</remarks>
    /// <remarks>2026-11-25: Store the canonical Appearance theme as BSON text for FR-SETTINGS-001#ac-precedence.</remarks>
    private static BsonDocument? DescribeSharedPreference(LibraryRecordIntent.SharedDefault setting)
    {
        if (setting.DurationTicks is null && setting.Flag is null && setting.Text is null)
            return null;
        var payload = new BsonDocument { ["PreferenceKey"] = setting.Key, ["RegistryVersion"] = SharedLibraryDefaults.RegistryVersion };
        if (setting.DurationTicks is { } ticks)
            payload["DurationTicks"] = ticks;
        if (setting.Flag is { } flag)
            payload["Flag"] = flag;
        if (setting.Text is { } text)
            payload["Text"] = text;
        return payload;
    }

    /// <summary>Identifies a retained latest deletion for a versioned record.</summary>
    private static bool IsLibraryRecordDeleted(BsonDocument row) => row["Deleted"].IsBoolean && row["Deleted"].AsBoolean;
}
