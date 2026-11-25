using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BeetsMan.Host.Application;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using JsonSerializer = System.Text.Json.JsonSerializer;

namespace BeetsMan.Host.Adapters.Persistence;

/// <summary>Maps device staging onto the same database lane as the authoritative playlists and operation receipts.</summary>
/// <remarks>2026-11-24: Persist the staged device plan and its ordered entries for FR-FILESYNC-001#ac-playlist-drop and ac-safety.</remarks>
public sealed partial class LiteDbHostPlaylistStore
{
    private const string DeviceExportCollection = "device_exports";
    private const string DeviceEntryCollection = "device_export_entries";
    private const string StagedReceiptType = "DevicePlaylistStaged";

    /// <inheritdoc />
    public Task<HostOutcome<RegisteredAndroidExport>> ReadExportRegistrationAsync(Guid libraryId, AuthenticatedHostInstance caller,
        Guid deviceId, CancellationToken token) => OnLaneAsync(() => ReadAuthorizedExport(libraryId, caller, deviceId), token);

    /// <inheritdoc />
    public Task<HostOutcome<(DeviceLayoutAssignment? Profile, bool HasPublished)>> ReadExportAssignmentAsync(Guid libraryId,
        AuthenticatedHostInstance caller, Guid deviceId, CancellationToken token) => OnLaneAsync<(DeviceLayoutAssignment?, bool)>(() =>
        {
            var registration = ReadAuthorizedExport(libraryId, caller, deviceId);
            if (registration is not HostOutcome<RegisteredAndroidExport>.Success { Value: var target })
                return registration.PropagateFailure<(DeviceLayoutAssignment?, bool)>();
            return new HostOutcome<(DeviceLayoutAssignment?, bool)>.Success(ReadExportAssignment(target));
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<(DeviceExportPlan? Plan, DeviceLayoutAssignment? Assignment)>> FindExportPlanAsync(Guid libraryId,
        AuthenticatedHostInstance caller, DeviceExportPlanLookup lookup, CancellationToken token) => OnLaneAsync<(DeviceExportPlan?, DeviceLayoutAssignment?)>(() =>
        {
            var authorization = ReadAuthorizedExport(libraryId, caller, lookup.DeviceId);
            if (authorization is not HostOutcome<RegisteredAndroidExport>.Success { Value: var target })
                return authorization.PropagateFailure<(DeviceExportPlan?, DeviceLayoutAssignment?)>();
            var rows = database.GetCollection<BsonDocument>(DeviceExportCollection);
            var assignment = lookup.PlanId is null && lookup.ProfileId is null ? ReadExportAssignment(target).Profile : null;
            var row = lookup.PlanId is { } planId ? rows.FindById(planId) :
                FindPlanRow(libraryId, lookup.DeviceId, lookup.ProfileId ?? assignment?.ProfileId ?? AndroidLayoutProfile.DefaultId);
            if (row is null && lookup.PlanId is null)
                return new HostOutcome<(DeviceExportPlan?, DeviceLayoutAssignment?)>.Success((null, assignment));
            if (row is null || row["LibraryId"].AsGuid != libraryId || row["DeviceId"].AsGuid != lookup.DeviceId ||
                lookup.ProfileId is not null && row["ProfileId"].AsString != lookup.ProfileId)
                return new HostOutcome<(DeviceExportPlan?, DeviceLayoutAssignment?)>.Rejected("export-plan-unavailable");
            if (row["ShareId"].AsString != target.ShareId || row["CaseSensitivePaths"].AsBoolean != target.CaseSensitivePaths)
                return new HostOutcome<(DeviceExportPlan?, DeviceLayoutAssignment?)>.Rejected("export-share-changed", row["ManifestRevision"].AsInt64, Conflict: true);
            return new HostOutcome<(DeviceExportPlan?, DeviceLayoutAssignment?)>.Success((ReadPlan(row), assignment));
        }, token);

    /// <summary>Uses the initial approved device profile until a published manifest takes precedence.</summary>
    /// <remarks>2026-11-24: Retain the STB10 profile for FR-FILESYNC-001#ac-layouts.</remarks>
    private (DeviceLayoutAssignment? Profile, bool HasPublished) ReadExportAssignment(RegisteredAndroidExport target)
    {
        var manifest = database.GetCollection<BsonDocument>(ExportManifestCollection).FindById(ManifestKey(target));
        if (manifest is null)
        {
            var registration = database.GetCollection<BsonDocument>("registered_instances").FindById(target.DeviceId);
            var export = registration?["AndroidExport"].AsDocument;
            return Text(export, "InitialProfileId") is { Length: > 0 } initialId &&
                export!["InitialProfileRevision"].IsNumber && export["InitialProfileRevision"].AsInt64 > 0
                ? (new(initialId, export["InitialProfileRevision"].AsInt64), false)
                : (null, false);
        }
        if (Text(manifest, "ProfileId") is { Length: > 0 } id && manifest["ProfileRevision"].IsNumber && manifest["ProfileRevision"].AsInt64 > 0)
            return (new(id, manifest["ProfileRevision"].AsInt64), true);

        var legacy = database.GetCollection<BsonDocument>(DeviceExportCollection).Find(Query.EQ("DeviceId", target.DeviceId))
            .Where(row => row["LibraryId"].AsGuid == target.LibraryId && row["ShareId"].AsString == target.ShareId &&
                Text(row, "PublicationState") == "Published").Take(2).ToArray();
        return (legacy is [var single] ? new(single["ProfileId"].AsString, single["ProfileVersion"].AsInt64) : null, true);
    }

    /// <inheritdoc />
    public Task<HostOutcome<DevicePlaylistSources>> ReadDevicePlaylistSourcesAsync(Guid libraryId, Guid deviceId,
        IEnumerable<Guid> trackIds, CancellationToken token) => OnLaneAsync(() => ReadExportSources(libraryId, deviceId, trackIds, token), token);

    private HostOutcome<DevicePlaylistSources> ReadExportSources(Guid libraryId, Guid deviceId,
        IEnumerable<Guid> trackIds, CancellationToken token)
    {
        var destination = ReadRegisteredAndroidExport(libraryId, deviceId);
        if (destination is null)
            return new HostOutcome<DevicePlaylistSources>.Rejected("android-export-target-unavailable");

        var tracks = database.GetCollection<BsonDocument>("catalog_tracks");
        var albums = database.GetCollection<BsonDocument>("catalog_albums");
        var artists = database.GetCollection<BsonDocument>("catalog_artists");
        var sources = ImmutableDictionary.CreateBuilder<Guid, DeviceExportTrackSource>();
        foreach (var trackId in trackIds.Distinct())
        {
            token.ThrowIfCancellationRequested();
            var track = CatalogRow(tracks, trackId, libraryId);
            if (Text(track, "RelativePath") is not { } relative)
                return new HostOutcome<DevicePlaylistSources>.Rejected("export-track-unavailable");
            var album = CatalogRow(albums, track!["AlbumId"], libraryId);
            var artist = CatalogRow(artists, track["ArtistId"], libraryId);
            var albumArtist = album is null ? null : CatalogRow(artists, album["AlbumArtistId"], libraryId);
            sources.Add(trackId, new(relative, Text(artist, "Name"), Text(albumArtist, "Name"), Text(album, "Title")));
        }
        return new HostOutcome<DevicePlaylistSources>.Success(new(destination, sources.ToImmutable()));
    }

    /// <inheritdoc />
    public Task<HostOutcome<DeviceStagePreparation>> PrepareStageAsync(Guid libraryId, StageDevicePlaylistRequest request,
        string fingerprint, CancellationToken token) => OnLaneAsync(() =>
        {
            var repositories = new Repositories(database);
            try { return ReadStage(repositories, libraryId, request, fingerprint); }
            finally { repositories.Close(); }
        }, token);

    /// <inheritdoc />
    public Task<HostOutcome<StageDevicePlaylistResponse>> CommitStageAsync(Guid libraryId, StageDevicePlaylistRequest request,
        string fingerprint, long playlistRevision, ResolvedDevicePlaylist target, PlaylistStageMutation mutation,
        string contentIdentity, CancellationToken token) => ExecuteAsync<StageDevicePlaylistResponse>(repositories =>
        {
            var preparation = ReadStage(repositories, libraryId, request, fingerprint);
            switch (preparation)
            {
                case HostOutcome<DeviceStagePreparation>.Forbidden:
                    return new HostOutcome<StageDevicePlaylistResponse>.Forbidden();
                case HostOutcome<DeviceStagePreparation>.Rejected rejected:
                    return new HostOutcome<StageDevicePlaylistResponse>.Rejected(rejected.Code, rejected.ActualRevision, rejected.Conflict);
                case HostOutcome<DeviceStagePreparation>.Success { Value.Replay: { } replay }:
                    return new HostOutcome<StageDevicePlaylistResponse>.Success(replay);
                case HostOutcome<DeviceStagePreparation>.Success { Value.Playlist.Revision: var revision } when revision != playlistRevision:
                    return new HostOutcome<StageDevicePlaylistResponse>.Rejected("stale-saved-playlist", revision, Conflict: true);
                case HostOutcome<DeviceStagePreparation>.Success:
                    break;
                default:
                    throw new InvalidOperationException("Unexpected outcome when rechecking a staged playlist.");
            }

            var plan = mutation.Plan;
            var id = plan.PlanId ?? throw new ArgumentException("A new or revised plan needs an ID.", nameof(mutation));
            var destination = ReadRegisteredAndroidExport(libraryId, request.DeviceId);
            if (destination is null || destination != target.Destination)
                return new HostOutcome<StageDevicePlaylistResponse>.Rejected("export-registration-changed", destination?.Revision, Conflict: true);
            var exports = database.GetCollection<BsonDocument>(DeviceExportCollection);
            if (exports.FindById(id) is { } existing && (existing["ShareId"].AsString != destination.ShareId ||
                existing["CaseSensitivePaths"].AsBoolean != target.Export.CaseSensitivePaths))
                return new HostOutcome<StageDevicePlaylistResponse>.Rejected("export-share-changed", plan.PlanRevision - 1, Conflict: true);
            var priorDigests = ReadPlaylistDigests(id);
            WriteStagedPlan(plan, destination, [.. plan.Entries.Select(entry => new ReviewedDeviceExportEntry(entry,
                entry.EntryId == mutation.PlaylistEntryId ? contentIdentity : priorDigests.GetValueOrDefault(entry.EntryId)))]);

            var response = new StageDevicePlaylistResponse(request.OperationId, plan);
            var key = new HostOperationKey(request.OriginInstanceId, request.OperationId);
            database.GetCollection<BsonDocument>("operation_receipts").Insert(new BsonDocument
            {
                ["_id"] = OperationId(key), ["OriginInstanceId"] = key.OriginInstanceId, ["OperationId"] = key.OperationId,
                ["IntentFingerprint"] = fingerprint, ["Receipt"] = new BsonDocument
                {
                    ["Type"] = StagedReceiptType, ["Version"] = 1, ["Plan"] = JsonSerializer.Serialize(response)
                }
            });
            return new HostOutcome<StageDevicePlaylistResponse>.Success(response);
        }, token);

    private HostOutcome<DeviceStagePreparation> ReadStage(IHostPlaylistWriteRepositories repositories, Guid libraryId,
        StageDevicePlaylistRequest request, string fingerprint)
    {
        if (!repositories.Registry.IsActive(libraryId, request.OriginInstanceId))
            return new HostOutcome<DeviceStagePreparation>.Forbidden();

        var key = new HostOperationKey(request.OriginInstanceId, request.OperationId);
        var previous = database.GetCollection<BsonDocument>("operation_receipts").FindById(OperationId(key));
        if (previous is not null)
        {
            var receipt = previous["Receipt"].AsDocument;
            if (previous["IntentFingerprint"].AsString != fingerprint || receipt["Type"].AsString != StagedReceiptType ||
                receipt["Version"].AsInt32 != 1)
                return new HostOutcome<DeviceStagePreparation>.Rejected("operation-intent-mismatch", Conflict: true);
            return new HostOutcome<DeviceStagePreparation>.Success(new(null, null, ReadReplay(receipt)));
        }

        if (ReadRegisteredAndroidExport(libraryId, request.DeviceId) is { } target && HasPendingPublication(target))
            return new HostOutcome<DeviceStagePreparation>.Rejected("export-publication-in-progress", Conflict: true);

        var export = FindPlanRow(libraryId, request.DeviceId, request.ProfileId);
        var planId = export?["_id"].AsGuid;
        var planRevision = export?["ManifestRevision"].AsInt64;
        if (planId != request.PlanId || planRevision != request.ExpectedPlanRevision ||
            export is not null && export["ProfileVersion"].AsInt64 != request.ProfileRevision)
            return new HostOutcome<DeviceStagePreparation>.Rejected("export-plan-changed", planRevision, Conflict: true);
        var playlist = repositories.Playlists.Find(libraryId, request.PlaylistId);
        if (playlist is null)
            return new HostOutcome<DeviceStagePreparation>.Rejected("saved-playlist-unavailable");

        var existing = export is null ? null : ReadPlan(export);
        return new HostOutcome<DeviceStagePreparation>.Success(new(playlist, existing, null));
    }

    private BsonDocument? FindPlanRow(Guid libraryId, Guid device, string profile) => database.GetCollection<BsonDocument>(DeviceExportCollection)
        .FindOne(Query.EQ("ScopeKey", ScopeKey(libraryId, device, profile)));

    private DeviceExportPlan ReadPlan(BsonDocument row)
    {
        var id = row["_id"].AsGuid;
        return new(id, row["ManifestRevision"].AsInt64, row["DeviceId"].AsGuid, row["ProfileId"].AsString,
            row["ProfileVersion"].AsInt64, [.. ReadEntryRows(id).OrderBy(item => item["Ordinal"].AsInt32).Select(ReadEntry)]);
    }

    private void WriteStagedPlan(DeviceExportPlan plan, RegisteredAndroidExport target, ImmutableArray<ReviewedDeviceExportEntry> entries)
    {
        var id = plan.PlanId!.Value;
        database.GetCollection<BsonDocument>(DeviceExportCollection).Upsert(new BsonDocument
        {
            ["_id"] = id, ["LibraryId"] = target.LibraryId, ["ScopeKey"] = ScopeKey(target.LibraryId, target.DeviceId, plan.ProfileId),
            ["DeviceId"] = target.DeviceId, ["ShareId"] = target.ShareId, ["ProfileId"] = plan.ProfileId,
            ["ProfileVersion"] = plan.ProfileRevision, ["ManifestRevision"] = plan.PlanRevision!.Value, ["PublicationState"] = "Staged",
            ["CaseSensitivePaths"] = target.CaseSensitivePaths
        });
        WriteEntries(id, target.CaseSensitivePaths, entries);
    }

    private HostOutcome<RegisteredAndroidExport> ReadAuthorizedExport(Guid libraryId, AuthenticatedHostInstance caller, Guid deviceId)
    {
        var instances = database.GetCollection<BsonDocument>("registered_instances");
        if (libraryId == Guid.Empty || caller.LibraryId != libraryId || caller.InstanceId == Guid.Empty ||
            !IsActiveInstance(instances, libraryId, caller.InstanceId))
            return new HostOutcome<RegisteredAndroidExport>.Forbidden();
        return ReadRegisteredAndroidExport(libraryId, deviceId) is { } target ? new HostOutcome<RegisteredAndroidExport>.Success(target) :
            new HostOutcome<RegisteredAndroidExport>.Rejected("android-export-target-unavailable");
    }

    private static StageDevicePlaylistResponse ReadReplay(BsonDocument receipt)
    {
        try
        {
            return JsonSerializer.Deserialize<StageDevicePlaylistResponse>(receipt["Plan"].AsString) ??
                throw new IOException("Stored device staging receipt is empty.");
        }
        catch (JsonException exception) { throw new IOException("Stored device staging receipt is unreadable.", exception); }
    }

    private RegisteredAndroidExport? ReadRegisteredAndroidExport(Guid libraryId, Guid deviceId)
    {
        var row = database.GetCollection<BsonDocument>("registered_instances").FindById(deviceId);
        if (row is null || !row["LibraryId"].IsGuid || row["LibraryId"].AsGuid != libraryId || Text(row, "Role") != "Android" ||
            Text(row, "Status") != "Active" || Text(row, "SyncthingDeviceId") is not { Length: > 0 } peer || !row["AndroidExport"].IsDocument)
            return null;
        var scope = row["AndroidExport"].AsDocument;
        if (!scope["Revision"].IsNumber || scope["Revision"].AsInt64 <= 0 || !scope["CaseSensitivePaths"].IsBoolean ||
            Text(scope, "ShareId") is not { Length: > 0 } share || !Uri.TryCreate(Text(scope, "ReceiverEndpoint"), UriKind.Absolute, out var endpoint))
            return null;
        return new(libraryId, deviceId, scope["Revision"].AsInt64, share, peer, endpoint, scope["CaseSensitivePaths"].AsBoolean)
            { ReceiverRoot = Text(scope, "ReceiverRoot"), ApprovedSenderId = Text(scope, "ApprovedSenderId") };
    }

    /// <summary>Adds an existing approved receiver root to older work only while its other registration fields still match.</summary>
    /// <remarks>2026-11-24: Preserve pending work while verifying the receiver scope for FR-FILESYNC-001#ac-safety.</remarks>
    private RegisteredAndroidExport ReadStoredExportTarget(BsonValue value)
    {
        var stored = ReadStored<RegisteredAndroidExport>(value);
        if (ReadRegisteredAndroidExport(stored.LibraryId, stored.DeviceId) is not { ReceiverRoot: not null } current)
            return stored;
        var compatible = current with
        {
            ReceiverRoot = stored.ReceiverRoot is null ? null : current.ReceiverRoot,
            ApprovedSenderId = stored.ApprovedSenderId is null ? null : current.ApprovedSenderId
        };
        return compatible == stored ? current : stored;
    }

    private static BsonDocument? CatalogRow(ILiteCollection<BsonDocument> collection, BsonValue id, Guid libraryId) =>
        id.IsGuid && collection.FindById(id) is { } item && item["LibraryId"].IsGuid && item["LibraryId"].AsGuid == libraryId ? item : null;

    private static string? Text(BsonDocument? row, string key) => row is not null && row[key].IsString ? row[key].AsString : null;

    private IEnumerable<BsonDocument> ReadEntryRows(Guid id) =>
        database.GetCollection<BsonDocument>(DeviceEntryCollection).Find(Query.EQ("ExportId", id));

    private static ExportEntry ReadEntry(BsonDocument row)
    {
        var id = row["EntryId"].AsGuid;
        var path = row["RelativePath"].AsString;
        var source = row["SourceEntityId"].AsGuid;
        return row["SourceKind"].AsString switch
        {
            "Audio" => new ExportEntry.Audio(id, path, source, (ExportAudioStatus)row["AudioStatus"].AsInt32)
                { IsDirect = row["IsDirect"].IsBoolean && row["IsDirect"].AsBoolean },
            "Playlist" => new ExportEntry.Playlist(id, path, source, row["Format"].AsString, [.. row["Tracks"].AsArray.Select(track =>
                new ExportTrackReference(track.AsDocument["TrackId"].AsGuid, track.AsDocument["Destination"].AsString,
                    (ExportAudioStatus)track.AsDocument["AudioStatus"].AsInt32))]),
            _ => throw new IOException("Stored device export entry has an unknown source kind.")
        };
    }

    private ImmutableDictionary<Guid, string> ReadPlaylistDigests(Guid id) => ReadEntryRows(id)
        .Where(row => row["SourceKind"].AsString == "Playlist" && row["ContentIdentity"].IsString)
        .ToImmutableDictionary(row => row["EntryId"].AsGuid, row => row["ContentIdentity"].AsString);

    private void WriteEntries(Guid exportId, bool caseSensitive, ImmutableArray<ReviewedDeviceExportEntry> entries)
    {
        var collection = database.GetCollection<BsonDocument>(DeviceEntryCollection);
        collection.DeleteMany(Query.EQ("ExportId", exportId));
        var ordinal = 0;
        foreach (var (entry, contentIdentity) in entries)
        {
            var relativePath = entry.Destination;
            var pathKey = caseSensitive ? relativePath : relativePath.ToUpperInvariant();
            var key = $"{exportId:N}/{HashKey(pathKey)}";
            var row = new BsonDocument
            {
                ["_id"] = key, ["ExportId"] = exportId, ["PathKey"] = pathKey, ["EntryId"] = entry.EntryId,
                ["RelativePath"] = relativePath, ["Ordinal"] = ordinal++
            };
            switch (entry)
            {
                case ExportEntry.Audio audio:
                    row["SourceKind"] = "Audio";
                    row["SourceEntityId"] = audio.TrackId;
                    row["AudioStatus"] = (int)audio.Status;
                    if (audio.IsDirect)
                        row["IsDirect"] = true;
                    break;
                case ExportEntry.Playlist playlist:
                    row["SourceKind"] = "Playlist";
                    row["SourceEntityId"] = playlist.PlaylistId;
                    row["Format"] = playlist.Format;
                    row["ContentIdentity"] = contentIdentity is null ? BsonValue.Null : new BsonValue(contentIdentity);
                    row["Tracks"] = new BsonArray(playlist.Tracks.Select(track => (BsonValue)new BsonDocument
                    {
                        ["TrackId"] = track.TrackId, ["Destination"] = track.Destination, ["AudioStatus"] = (int)track.Status
                    }));
                    break;
            }
            collection.Insert(row);
        }
    }

    private static string ScopeKey(Guid library, Guid device, string profile) => $"{library:N}/{device:N}/{HashKey(profile)}";
    private static string HashKey(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static void EnsureDeviceExportIndexes(ILiteDatabase database)
    {
        database.GetCollection<BsonDocument>(DeviceExportCollection).EnsureIndex("ScopeKey", unique: true);
        database.GetCollection<BsonDocument>(DeviceEntryCollection).EnsureIndex("ExportId");
    }
}
