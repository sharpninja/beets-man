using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Identifies a user text field or the host-owned current track field.</summary>
/// <remarks>2026-11-25: Define host-renderable fields for FR-PLUGINS-001#ac-visualize.</remarks>
public enum PluginPanelFieldKind { Text = 1, CurrentTrack = 2 }

/// <summary>Describes one field in the manifest's display order.</summary>
public sealed record PluginPanelField(string Id, string Label, PluginPanelFieldKind Kind, bool Required, int MaximumLength);

/// <summary>Declares one host-rendered form linked to a negotiated action on the same worker.</summary>
public sealed record PluginPanelDefinition(string Title, string ActionId, ImmutableArray<PluginPanelField> Fields);

/// <summary>Identifies the live activation and playback context displayed when a panel was opened.</summary>
public sealed record PluginPanelReference(Guid SessionId, string PluginId, string Version, string PanelId, Guid ActivationId, Guid? TrackId);

/// <summary>Contains the read-only panel definition and the exact context for submission.</summary>
public sealed record PluginPanelSnapshot(PluginPanelReference Reference, PluginPanelDefinition Definition);

/// <summary>Loads a negotiated panel from the currently active worker.</summary>
public sealed record GetPluginPanel(Guid SessionId, string PluginId, string Version, string PanelId) : IQuery<PluginPanelSnapshot>;

/// <summary>Submits only user-editable fields against the displayed panel context.</summary>
public sealed record SubmitPluginPanel(PluginPanelReference Panel, ImmutableDictionary<string, string> Fields) : ICommand<PluginActionReceipt>;

/// <summary>Validates pinned panel definitions and compares them across persistence boundaries.</summary>
/// <remarks>2026-11-25: Bind local declaration, metadata limits, and linked actions for FR-PLUGINS-001#ac-visualize.</remarks>
public static class PluginPanelRules
{
    /// <summary>Checks the bounded field set and its declared action at package admission.</summary>
    public static bool IsValid(PluginContribution contribution, IReadOnlyList<PluginContribution> declarations)
    {
        if (contribution.Panel is not { } panel)
            return true;
        if (contribution.Kind != PluginContributionKind.Panel || !IsLabel(panel.Title) || !PluginRegistryHandlers.IsIdentifier(panel.ActionId) ||
            panel.Fields.IsDefaultOrEmpty || panel.Fields.Length > 16 ||
            !declarations.Any(item => item is { Kind: PluginContributionKind.Action } && item.ContributionId == panel.ActionId))
            return false;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        return panel.Fields.All(field => field is not null && PluginRegistryHandlers.IsIdentifier(field.Id) && IsLabel(field.Label) &&
            ids.Add(field.Id) && (field.Kind switch
            {
                PluginPanelFieldKind.Text => field.MaximumLength is >= 1 and <= 1024,
                PluginPanelFieldKind.CurrentTrack => field.MaximumLength == 0,
                _ => false
            }));
    }

    /// <summary>Compares the exact declarations including ordered panel fields across independent immutable snapshots.</summary>
    public static bool SameDeclarations(ImmutableArray<PluginContribution> first, ImmutableArray<PluginContribution> second) =>
        first.Length == second.Length && first.Zip(second).All(pair =>
            pair.First with { Panel = pair.Second.Panel } == pair.Second && SamePanel(pair.First.Panel, pair.Second.Panel));

    private static bool SamePanel(PluginPanelDefinition? first, PluginPanelDefinition? second) =>
        first is null ? second is null : second is not null && first with { Fields = second.Fields } == second &&
            first.Fields.SequenceEqual(second.Fields);

    private static bool IsLabel(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 96 && !text.Any(char.IsControl);
}
