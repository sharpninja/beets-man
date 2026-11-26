using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Extensions;
using LiteDB;

namespace BeetsMan.Desktop.Adapters.Persistence;

/// <summary>Stores desktop-only plugin permissions and pinned package identities outside synchronized collections.</summary>
/// <remarks>2026-11-25: Commit version-scoped trust and enablement together for FR-PLUGINS-001#ac-manage and TR-PLUGINS-ARCH-001.</remarks>
public sealed partial class LiteDbDesktopPlaylistStore : IPluginRegistryStore
{
    private const string PluginRegistry = "plugin_registry";
    private const string LocalPluginRegistry = "local";

    /// <inheritdoc />
    public Task<Outcome<PluginRegistrySnapshot>> ReadPluginsAsync(CancellationToken token) =>
        ReadAsync<PluginRegistrySnapshot>(db => new Outcome<PluginRegistrySnapshot>.Success(ReadRegistry(db)), token);

    /// <inheritdoc />
    public Task<Outcome<PluginRegistrySnapshot>> EditPluginsAsync(Func<PluginRegistrySnapshot, Outcome<PluginRegistrySnapshot>> edit,
        CancellationToken token) => OnLaneAsync(() => Transact(() =>
    {
        var current = ReadRegistry(database);
        var result = edit(current);
        if (result is not Outcome<PluginRegistrySnapshot>.Success { Value: var changed } || changed == current)
            return result;
        var next = changed with { Revision = checked(current.Revision + 1) };
        database.GetCollection<BsonDocument>(PluginRegistry).Upsert(new BsonDocument
        {
            ["_id"] = LocalPluginRegistry, ["Revision"] = next.Revision,
            ["Versions"] = new BsonArray(next.Versions.Select(item => (BsonValue)WriteVersion(item)))
        });
        return new Outcome<PluginRegistrySnapshot>.Success(next);
    }, token), token);

    private static PluginRegistrySnapshot ReadRegistry(ILiteDatabase database)
    {
        var row = database.GetCollection<BsonDocument>(PluginRegistry).FindById(LocalPluginRegistry);
        return row is null ? new(0, []) : new(row["Revision"].AsInt64,
            [.. row["Versions"].AsArray.Select(value => ReadVersion(value.AsDocument))]);
    }

    private static BsonDocument WriteVersion(InstalledPluginPackage item)
    {
        var package = item.Package;
        return new()
        {
            ["PluginId"] = package.PluginId, ["Version"] = package.Version,
            ["MinimumProtocol"] = package.MinimumProtocol, ["MaximumProtocol"] = package.MaximumProtocol,
            ["EntryPoint"] = package.EntryPoint, ["ArchiveSha256"] = package.ArchiveSha256,
            ["Trust"] = item.Trust.ToString(), ["Enabled"] = item.Enabled,
            ["Contributions"] = new BsonArray(package.Contributions.Select(contribution => (BsonValue)WriteContribution(contribution)))
        };
    }

    private static InstalledPluginPackage ReadVersion(BsonDocument row)
    {
        ImmutableArray<PluginContribution> contributions = [.. row["Contributions"].AsArray.Select(value => ReadContribution(value.AsDocument))];
        var package = new PluginPackage(row["PluginId"].AsString, row["Version"].AsString, row["MinimumProtocol"].AsInt32,
            row["MaximumProtocol"].AsInt32, row["EntryPoint"].AsString, contributions, row["ArchiveSha256"].AsString);
        return new(package, Enum.Parse<PluginTrust>(row["Trust"].AsString), row["Enabled"].AsBoolean);
    }

    /// <summary>Stores the pinned panel declaration beside its contribution without altering legacy rows.</summary>
    /// <remarks>2026-11-25: Persist the exact plugin panel schema for FR-PLUGINS-001#ac-visualize.</remarks>
    private static BsonDocument WriteContribution(PluginContribution item) => new()
    {
        ["Id"] = item.ContributionId, ["Kind"] = item.Kind.ToString(),
        ["HostAction"] = item.HostAction is { } action ? action : BsonValue.Null,
        ["Panel"] = item.Panel is { } panel ? new BsonDocument
        {
            ["Title"] = panel.Title, ["ActionId"] = panel.ActionId,
            ["Fields"] = new BsonArray(panel.Fields.Select(field => (BsonValue)new BsonDocument
            {
                ["Id"] = field.Id, ["Label"] = field.Label, ["Kind"] = field.Kind.ToString(),
                ["Required"] = field.Required, ["MaximumLength"] = field.MaximumLength
            }))
        } : BsonValue.Null
    };

    private static PluginContribution ReadContribution(BsonDocument item)
    {
        var panel = item["Panel"].IsDocument ? item["Panel"].AsDocument : null;
        return new(item["Id"].AsString, Enum.Parse<PluginContributionKind>(item["Kind"].AsString),
            item["HostAction"].IsString ? item["HostAction"].AsString : null,
            panel is null ? null : new(panel["Title"].AsString, panel["ActionId"].AsString,
                [.. panel["Fields"].AsArray.Select(field => field.AsDocument).Select(field => new PluginPanelField(
                    field["Id"].AsString, field["Label"].AsString, Enum.Parse<PluginPanelFieldKind>(field["Kind"].AsString),
                    field["Required"].AsBoolean, field["MaximumLength"].AsInt32))]));
    }
}
