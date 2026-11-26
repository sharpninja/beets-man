using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Resolves host-owned panel data and sends validated values through the existing plugin action route.</summary>
/// <remarks>2026-11-25: Pin ready-worker activation and track context for FR-PLUGINS-001#ac-visualize.</remarks>
public sealed partial class PluginActionHandlers : IQueryHandler<GetPluginPanel, PluginPanelSnapshot>, ICommandHandler<SubmitPluginPanel, PluginActionReceipt>
{
    /// <inheritdoc />
    public async Task<Outcome<PluginPanelSnapshot>> HandleAsync(GetPluginPanel query, RequestContext context, CancellationToken token)
    {
        if (!ValidPanelIdentity(query.SessionId, query.PluginId, query.Version, query.PanelId))
            return new Outcome<PluginPanelSnapshot>.Invalid([new("invalid-plugin-panel")]);
        if (supervisor.FindPanel(query.PluginId, query.Version, query.PanelId) is not { } panel)
            return new Outcome<PluginPanelSnapshot>.Invalid([new("plugin-panel-unavailable")]);
        var track = await ReadPanelTrackAsync(panel.Definition, context, token).ConfigureAwait(false);
        if (track is not Outcome<Guid?>.Success current)
            return track switch
            {
                Outcome<Guid?>.DependencyFailure failure => new Outcome<PluginPanelSnapshot>.DependencyFailure(failure.Code, failure.Retryable),
                Outcome<Guid?>.Invalid rejected => new Outcome<PluginPanelSnapshot>.Invalid(rejected.Issues),
                Outcome<Guid?>.Conflict conflict => new Outcome<PluginPanelSnapshot>.Conflict(conflict.Code, conflict.ActualRevision),
                _ => new Outcome<PluginPanelSnapshot>.Cancelled()
            };
        if (supervisor.FindPanel(query.PluginId, query.Version, query.PanelId)?.Activation != panel.Activation)
            return new Outcome<PluginPanelSnapshot>.Invalid([new("stale-plugin-panel")]);
        return new Outcome<PluginPanelSnapshot>.Success(new(
            new(query.SessionId, query.PluginId, query.Version, query.PanelId, panel.Activation, current.Value), panel.Definition));
    }

    /// <inheritdoc />
    public async Task<Outcome<PluginActionReceipt>> HandleAsync(SubmitPluginPanel command, CommandContext context, CancellationToken token)
    {
        if (command.Panel is not { } reference || reference.ActivationId == Guid.Empty || context.OperationId == Guid.Empty ||
            !ValidPanelIdentity(reference.SessionId, reference.PluginId, reference.Version, reference.PanelId))
            return Invalid("invalid-plugin-panel");
        if (supervisor.FindPanel(reference.PluginId, reference.Version, reference.PanelId) is not { } panel ||
            panel.Activation != reference.ActivationId)
            return Invalid("stale-plugin-panel");
        var track = await ReadPanelTrackAsync(panel.Definition, context.Request, token).ConfigureAwait(false);
        if (track is not Outcome<Guid?>.Success current)
            return Forward(track);
        if (reference.TrackId != current.Value)
            return new Outcome<PluginActionReceipt>.Conflict("plugin-panel-track-changed", null);
        var values = BindFields(panel.Definition, command.Fields, current.Value);
        return values is null ? Invalid("invalid-plugin-panel-fields") : await ExecuteAsync(
            new(reference.SessionId, reference.PluginId, reference.Version, panel.Definition.ActionId, values), context, reference, token)
            .ConfigureAwait(false);
    }

    private bool ValidPanelIdentity(Guid session, string id, string version, string panel) => session == sessionId &&
        PluginRegistryHandlers.IsIdentifier(id) && PluginRegistryHandlers.IsVersion(version) && PluginRegistryHandlers.IsIdentifier(panel);

    private async Task<Outcome<Guid?>> ReadPanelTrackAsync(PluginPanelDefinition panel, RequestContext context, CancellationToken token)
    {
        if (!panel.Fields.Any(field => field.Kind == PluginPanelFieldKind.CurrentTrack))
            return new Outcome<Guid?>.Success(null);
        var result = await dispatcher().QueryAsync(new GetPlaybackState(sessionId), context, token).ConfigureAwait(false);
        return result switch
        {
            Outcome<PlaybackState>.Success active => new Outcome<Guid?>.Success(active.Value.ActiveTrackId),
            Outcome<PlaybackState>.Invalid rejected => new Outcome<Guid?>.Invalid(rejected.Issues),
            Outcome<PlaybackState>.Conflict conflict => new Outcome<Guid?>.Conflict(conflict.Code, conflict.ActualRevision),
            Outcome<PlaybackState>.DependencyFailure failure => new Outcome<Guid?>.DependencyFailure(failure.Code, failure.Retryable),
            _ => new Outcome<Guid?>.Cancelled()
        };
    }

    private static ImmutableDictionary<string, string>? BindFields(PluginPanelDefinition panel, ImmutableDictionary<string, string>? input, Guid? track)
    {
        if (input is null || input.Count > panel.Fields.Length || input.Keys.Any(key =>
                !panel.Fields.Any(field => field.Kind == PluginPanelFieldKind.Text && field.Id == key)))
            return null;
        var arguments = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var field in panel.Fields)
        {
            var value = field.Kind == PluginPanelFieldKind.CurrentTrack ? track?.ToString("D") : input.GetValueOrDefault(field.Id);
            if (field.Required && string.IsNullOrWhiteSpace(value) ||
                field.Kind == PluginPanelFieldKind.Text && value is not null && (value.Length > field.MaximumLength || value.Contains('\0')))
                return null;
            if (value is not null)
                arguments.Add(field.Id, value);
        }
        return arguments.ToImmutable();
    }
}
