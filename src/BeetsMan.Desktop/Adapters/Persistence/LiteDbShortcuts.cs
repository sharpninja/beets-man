using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Stores one revisioned set of device-local contextual overrides in LiteDB.</summary>
/// <remarks>2026-11-25: Persist FR-SETTINGS-001#ac-shortcuts without exporting keys or OS state.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IShortcutStore
{
    private const string ShortcutBindings = "shortcut_bindings";
    private const int ShortcutRegistryVersion = 1;

    /// <inheritdoc />
    Task<Outcome<StoredShortcuts>> IShortcutStore.ReadShortcutsAsync(CancellationToken token) =>
        OnLaneAsync(ReadShortcuts, token);

    /// <inheritdoc />
    Task<Outcome<StoredShortcuts>> IShortcutStore.WriteShortcutsAsync(long expectedRevision, ImmutableArray<ShortcutBinding> bindings,
        CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var result = ReadShortcuts();
        if (result is not Outcome<StoredShortcuts>.Success current)
            return result;
        if (current.Value.Revision != expectedRevision)
            return new Outcome<StoredShortcuts>.Conflict("stale-shortcut-bindings", current.Value.Revision);
        var revision = checked(expectedRevision + 1);
        var rows = new BsonArray(bindings.Select(binding => new BsonDocument
        {
            ["ActionId"] = binding.ActionId, ["Scope"] = binding.Scope, ["Gesture"] = binding.Gesture
        }));
        database.GetCollection<BsonDocument>(ShortcutBindings).Upsert(new BsonDocument
        {
            ["_id"] = "local", ["RegistryVersion"] = ShortcutRegistryVersion, ["Revision"] = revision, ["Overrides"] = rows
        });
        return new Outcome<StoredShortcuts>.Success(new(revision, bindings));
    }, token), token);

    private Outcome<StoredShortcuts> ReadShortcuts()
    {
        var row = database.GetCollection<BsonDocument>(ShortcutBindings).FindById("local");
        if (row is null)
            return new Outcome<StoredShortcuts>.Success(new(0, []));
        if (!row["RegistryVersion"].IsInt32 || row["RegistryVersion"].AsInt32 != ShortcutRegistryVersion ||
            !row["Revision"].IsInt64 || row["Revision"].AsInt64 <= 0 || !row["Overrides"].IsArray || row["Overrides"].AsArray.Count > 16)
            return Unavailable();
        var stored = row["Overrides"].AsArray;
        var bindings = ImmutableArray.CreateBuilder<ShortcutBinding>(stored.Count);
        var keys = new HashSet<(string, string)>();
        var gestures = new HashSet<(string, string)>();
        foreach (var item in stored)
        {
            if (!item.IsDocument)
                return Unavailable();
            var binding = item.AsDocument;
            if (!binding["ActionId"].IsString || !binding["Scope"].IsString || !binding["Gesture"].IsString)
                return Unavailable();
            var action = binding["ActionId"].AsString;
            var scope = binding["Scope"].AsString;
            var gesture = binding["Gesture"].AsString;
            if (!ShortcutCatalog.AllowedInContext(action, scope) || ShortcutCatalog.NormalizeGesture(gesture) != gesture ||
                !keys.Add((scope, action)) || !gestures.Add((scope, gesture)))
                return Unavailable();
            bindings.Add(new(action, scope, gesture));
        }
        return new Outcome<StoredShortcuts>.Success(new(row["Revision"].AsInt64, bindings.MoveToImmutable()));
    }

    private static Outcome<StoredShortcuts> Unavailable() => new Outcome<StoredShortcuts>.DependencyFailure("shortcut-bindings-unavailable", false);
}
