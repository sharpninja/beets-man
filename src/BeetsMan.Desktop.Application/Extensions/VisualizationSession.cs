using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;

namespace BeetsMan.Desktop.Application.Extensions;

/// <summary>Identifies a built-in visualization or a contribution by its exact installed version.</summary>
/// <remarks>2026-11-25: Bind the STB16 selection to FR-PLUGINS-001#ac-visualize.</remarks>
public sealed record VisualizationChoice(string ContributionId, string? PluginId = null, string? Version = null)
{
    public static readonly VisualizationChoice Spectrum = new("spectrum");
    public static readonly VisualizationChoice Waveform = new("waveform");
}

/// <summary>Contains normalized values for a host-owned visualization and its source sequence.</summary>
public sealed record VisualizationValues(long Sequence, ImmutableArray<float> Values);

/// <summary>Projects the selected visualization independently of transport and shell mode.</summary>
public sealed record VisualizationState(Guid SessionId, long Revision, VisualizationChoice Choice, bool Supported,
    VisualizationValues? Frame = null, string? DetachedReason = null);

/// <summary>Reads the currently selected visualization and available frame.</summary>
public sealed record GetVisualization(Guid SessionId) : IQuery<VisualizationState>;

/// <summary>Selects a built-in visualization or an exact ready worker contribution for the active session.</summary>
public sealed record SelectVisualization(Guid SessionId, VisualizationChoice Choice) : ICommand<VisualizationState>;

/// <summary>Owns a desktop session's visualization and drops replies from old audio or worker activations.</summary>
/// <remarks>2026-11-25: Dispatch FR-PLUGINS-001#ac-visualize and ac-recovery without holding the playback lane.</remarks>
public sealed class VisualizationSession(Guid sessionId, IPlaybackVisualizationSource? source,
    Func<VisualizationPlaybackContext> readPlayback, PluginWorkerSupervisor supervisor, TimeProvider? clock = null)
    : IQueryHandler<GetVisualization, VisualizationState>, ICommandHandler<SelectVisualization, VisualizationState>
{
    private readonly object gate = new();
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private VisualizationState state = new(sessionId, 0, VisualizationChoice.Spectrum, false);
    private VisualizationPlaybackContext? context;
    private Guid? activation;
    private long windowBegan;
    private long lastSequence;
    private long displayedAt;
    private long selectionRevision;

    /// <summary>Registers the typed selection and observation routes.</summary>
    public void Register(RequestDispatcher.Builder routes) =>
        routes.RegisterQuery<GetVisualization, VisualizationState>(this).RegisterCommand<SelectVisualization, VisualizationState>(this);

    /// <inheritdoc />
    public Task<Outcome<VisualizationState>> HandleAsync(GetVisualization request, RequestContext context, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested)
                return Task.FromResult<Outcome<VisualizationState>>(new Outcome<VisualizationState>.Cancelled());
            if (request.SessionId != sessionId)
                return Task.FromResult(Invalid("playback-session-not-found"));
            Refresh();
            return Task.FromResult(Success());
        }
    }

    /// <inheritdoc />
    public Task<Outcome<VisualizationState>> HandleAsync(SelectVisualization request, CommandContext context, CancellationToken token)
    {
        lock (gate)
        {
            if (token.IsCancellationRequested)
                return Task.FromResult<Outcome<VisualizationState>>(new Outcome<VisualizationState>.Cancelled());
            if (request.SessionId != sessionId)
                return Task.FromResult(Invalid("playback-session-not-found"));
            Refresh();
            if (!state.Supported)
                return Task.FromResult(Invalid("visualization-unavailable"));
            if (request.Choice is not { } target || !PluginRegistryHandlers.IsIdentifier(target.ContributionId))
                return Task.FromResult(Invalid("invalid-visualization-choice"));
            Guid? pinned = null;
            if (target.PluginId is null && target.Version is null)
            {
                if (target != VisualizationChoice.Waveform && target != VisualizationChoice.Spectrum)
                    return Task.FromResult(Invalid("invalid-visualization-choice"));
            }
            else
            {
                if (!PluginRegistryHandlers.IsIdentifier(target.PluginId) || !PluginRegistryHandlers.IsVersion(target.Version))
                    return Task.FromResult(Invalid("invalid-visualization-choice"));
                pinned = supervisor.FindVisualization(target.PluginId!, target.Version!, target.ContributionId).Activation;
                if (pinned is null)
                    return Task.FromResult(Invalid("plugin-visualization-unavailable"));
            }
            if (target != state.Choice || pinned != activation)
            {
                activation = pinned;
                selectionRevision++;
                windowBegan = time.GetTimestamp();
                Set(state with { Choice = target, Frame = null, DetachedReason = null });
            }
            return Task.FromResult(Success());
        }
    }

    /// <summary>Samples a fresh window and optionally renders it through the negotiated worker outside the state lock.</summary>
    public async Task ObserveAsync(CancellationToken token)
    {
        long selected;
        long afterSequence;
        IPluginWorker? worker;
        VisualizationPlaybackContext playback;
        VisualizationChoice choice;
        lock (gate)
        {
            worker = Refresh();
            if (!state.Supported || context?.Playing != true || state.DetachedReason is not null)
                return;
            playback = context;
            selected = selectionRevision;
            afterSequence = lastSequence;
            choice = state.Choice;
        }
        if (source!.ReadLatest(afterSequence) is not { } frame || !Valid(frame))
            return;
        lock (gate)
        {
            if (Refresh() != worker || !IsCurrent(selected, playback, frame) || frame.Sequence <= lastSequence)
                return;
            lastSequence = frame.Sequence;
        }

        ImmutableArray<float>? rendered = choice == VisualizationChoice.Waveform ? frame.Waveform : frame.Spectrum;
        if (worker is not null)
        {
            var reply = await worker.RenderVisualizationAsync(sessionId, choice.ContributionId, frame, token).ConfigureAwait(false);
            rendered = (reply as Outcome<ImmutableArray<float>?>.Success)?.Value;
        }
        lock (gate)
        {
            var current = Refresh();
            if (!token.IsCancellationRequested && rendered is { } levels && current == worker &&
                frame.Sequence == lastSequence && IsCurrent(selected, playback, frame))
            {
                displayedAt = frame.CapturedAt;
                Set(state with { Frame = new(frame.Sequence, levels) });
            }
        }
    }

    private IPluginWorker? Refresh()
    {
        var next = readPlayback();
        var supported = source is not null && next.Supported;
        if (next != context)
        {
            context = next;
            windowBegan = time.GetTimestamp();
            Set(state with { Supported = supported, Frame = null });
        }
        else if (state.Frame is not null && time.GetElapsedTime(displayedAt) >= PlaybackVisualizationFrame.MaximumAge)
            Set(state with { Frame = null });
        if (state.Choice is not { PluginId: { } id, Version: { } version } choice || activation is null)
            return null;
        var live = supervisor.FindVisualization(id, version, choice.ContributionId);
        if (live.Activation == activation)
            return live.Worker;
        Set(state with { Frame = null, DetachedReason = state.DetachedReason ?? live.Failure ?? "plugin-visualization-detached" });
        return null;
    }

    private bool IsCurrent(long selected, VisualizationPlaybackContext playback, PlaybackVisualizationFrame frame) =>
        selectionRevision == selected && context == playback && state.DetachedReason is null && state.Supported &&
        time.GetElapsedTime(frame.CapturedAt) is var age && age >= TimeSpan.Zero && age < PlaybackVisualizationFrame.MaximumAge &&
        time.GetElapsedTime(windowBegan, frame.CapturedAt).TotalSeconds >= (double)PlaybackVisualizationFrame.WindowSamples / frame.SampleRate;

    private static bool Valid(PlaybackVisualizationFrame frame) => frame.SampleRate is >= 8000 and <= 384000 &&
        !frame.Waveform.IsDefault && frame.Waveform.Length == PlaybackVisualizationFrame.WaveformPoints &&
        !frame.Spectrum.IsDefault && frame.Spectrum.Length == PlaybackVisualizationFrame.SpectrumBands &&
        frame.Waveform.All(value => value is >= -1 and <= 1) && frame.Spectrum.All(value => value is >= 0 and <= 1);

    private void Set(VisualizationState next)
    {
        if (next != state)
            state = next with { Revision = state.Revision + 1 };
    }

    private Outcome<VisualizationState> Success() => new Outcome<VisualizationState>.Success(state);
    private static Outcome<VisualizationState> Invalid(string code) => new Outcome<VisualizationState>.Invalid([new(code)]);
}
