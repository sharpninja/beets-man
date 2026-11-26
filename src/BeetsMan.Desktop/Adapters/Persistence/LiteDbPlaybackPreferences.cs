using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Host.Contracts.V1;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Persists registered local overrides separately from the shared cache.</summary>
/// <remarks>2026-11-25: Keep restart opt-in desktop-local and resolve fade precedence for FR-PLAYER-001#ac-resume-playback-after-restart and FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-25: Expand typed setting layers and validate the reviewed revisions while committing an STB07 reset for FR-SETTINGS-001.</remarks>
/// <remarks>2026-11-25: Persist and validate the STB07 theme's text for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IDesktopPreferenceStore
{
    private const string PreferenceOverrides = "preference_overrides";

    /// <inheritdoc />
    Task<Outcome<StoredPreferences>> IDesktopPreferenceStore.ReadPreferencesAsync(Guid library, string category, CancellationToken token) =>
        OnLaneAsync(() => ReadPreferences(library, category), token);

    /// <inheritdoc />
    Task<Outcome<StoredPreferences>> IDesktopPreferenceStore.WritePreferencesAsync(Guid library, string category, long expectedRevision,
        ImmutableDictionary<string, PreferenceValue?> values, CancellationToken token, long? expectedSharedRevision) => OnLaneAsync(() => Transact(() =>
    {
        if (values.Any(change => PreferenceRegistry.Find(change.Key) is not { } definition ||
                definition.Category != category || !definition.Accepts(change.Value)))
            return new Outcome<StoredPreferences>.Invalid([new("invalid-playback-preferences")]);
        var read = ReadPreferences(library, category);
        if (read is not Outcome<StoredPreferences>.Success { Value: var current })
            return read;
        if (current.Revision != expectedRevision)
            return new Outcome<StoredPreferences>.Conflict("stale-local-preferences", current.Revision);
        if (expectedSharedRevision is { } expectedShared && current.SharedRevision != expectedShared)
            return new Outcome<StoredPreferences>.Conflict("stale-shared-preferences", current.SharedRevision);
        if (values.IsEmpty)
            return read;
        var overrides = database.GetCollection<BsonDocument>(PreferenceOverrides);
        var revision = checked(current.Revision + 1);
        foreach (var change in values)
        {
            var value = change.Value switch
            {
                PreferenceValue.Flag flag => new BsonValue(flag.Value),
                PreferenceValue.Duration duration => new BsonValue(duration.Value.Ticks),
                PreferenceValue.Text text => new BsonValue(text.Value),
                _ => BsonValue.Null
            };
            var existing = overrides.FindById(change.Key);
            if ((existing?["Value"] ?? BsonValue.Null) == value)
                continue;
            overrides.Upsert(new BsonDocument { ["_id"] = change.Key, ["RegistryVersion"] = SharedLibraryDefaults.RegistryVersion,
                ["LocalRevision"] = revision, ["Value"] = value });
        }
        return ReadPreferences(library, category);
    }, token), token);

    /// <inheritdoc />
    Task<Outcome<StoredPreferences>> IDesktopPreferenceStore.CacheSharedPreferenceAsync(Guid library, long revision, ReplicaSharedDefault value,
        CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var change = new CatalogFeedChange(CatalogEntityKind.SharedDefault, CatalogEntityOperation.Upsert,
            LibraryRecordIdentity.SharedDefault(library, value.Key), value);
        if (library == Guid.Empty || revision <= 0 || PreferenceRegistry.Find(value.Key) is not { Shareable: true } definition ||
            !value.IsValidUpsert())
            return new Outcome<StoredPreferences>.Invalid([new("invalid-shared-playback-preference")]);
        return ApplyCatalogChange(library, revision, change) ? ReadPreferences(library, definition.Category) :
            new Outcome<StoredPreferences>.Conflict("shared-playback-preference-scope-conflict", null);
    }, token), token);

    /// <summary>Reads both layers in one store view, retaining the revision of a cleared value.</summary>
    private Outcome<StoredPreferences> ReadPreferences(Guid library, string category)
    {
        var definitions = PreferenceRegistry.Category(category);
        if (definitions.IsEmpty)
            return new Outcome<StoredPreferences>.Invalid([new("unknown-preference-category")]);
        var overrides = database.GetCollection<BsonDocument>(PreferenceOverrides);
        var preferences = ImmutableArray.CreateBuilder<StoredPreference>(definitions.Length);
        var localRevision = 0L;
        var sharedRevision = 0L;
        foreach (var definition in definitions)
        {
            var localRow = overrides.FindById(definition.Key);
            var localValue = localRow?["Value"] ?? BsonValue.Null;
            var local = DecodePreference(localValue);
            if (localRow is not null && (localRow["RegistryVersion"].AsInt32 != SharedLibraryDefaults.RegistryVersion ||
                    !localRow["LocalRevision"].IsInt64 || localRow["LocalRevision"].AsInt64 <= 0 ||
                    (!localValue.IsNull && (local is null || !definition.Accepts(local)))))
                return Unavailable();
            localRevision = Math.Max(localRevision, localRow?["LocalRevision"].AsInt64 ?? 0);

            PreferenceValue? shared = null;
            if (library != Guid.Empty && definition.Shareable && CatalogProjectionTable(CatalogEntityKind.SharedDefault)
                    .FindById(LibraryRecordIdentity.SharedDefault(library, definition.Key)) is { } sharedRow)
            {
                if (!sharedRow["LibraryId"].IsGuid || sharedRow["LibraryId"].AsGuid != library ||
                    sharedRow["Kind"].AsString != nameof(CatalogEntityKind.SharedDefault) ||
                    !sharedRow["SourceRevision"].IsInt64 || sharedRow["SourceRevision"].AsInt64 <= 0)
                    return Unavailable();
                sharedRevision = Math.Max(sharedRevision, sharedRow["SourceRevision"].AsInt64);
                if (!RecordFlag(sharedRow, "Deleted"))
                {
                    if (sharedRow["PreferenceKey"].AsString != definition.Key ||
                        sharedRow["RegistryVersion"].AsInt32 != SharedLibraryDefaults.RegistryVersion)
                        return Unavailable();
                    var value = (sharedRow["DurationTicks"], sharedRow["Flag"], sharedRow["Text"]) switch
                    {
                        ({ IsInt64: true } duration, { IsNull: true }, { IsNull: true }) => duration,
                        ({ IsNull: true }, { IsBoolean: true } flag, { IsNull: true }) => flag,
                        ({ IsNull: true }, { IsNull: true }, { IsString: true } text) => text,
                        _ => BsonValue.Null
                    };
                    shared = DecodePreference(value);
                    if (shared is null || !definition.Accepts(shared))
                        return Unavailable();
                }
            }
            preferences.Add(new(definition, local, shared));
        }
        return new Outcome<StoredPreferences>.Success(new(category, localRevision, sharedRevision, preferences.MoveToImmutable()));

        static Outcome<StoredPreferences> Unavailable() => new Outcome<StoredPreferences>.DependencyFailure("playback-preferences-unavailable", false);
    }

    private static PreferenceValue? DecodePreference(BsonValue value) => value switch
    {
        { IsBoolean: true } => new PreferenceValue.Flag(value.AsBoolean),
        { IsInt64: true } => new PreferenceValue.Duration(TimeSpan.FromTicks(value.AsInt64)),
        { IsString: true } => new PreferenceValue.Text(value.AsString),
        _ => null
    };
}
