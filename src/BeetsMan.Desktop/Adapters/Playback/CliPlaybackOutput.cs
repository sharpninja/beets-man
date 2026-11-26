using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;

namespace BeetsMan.Desktop.Adapters.Playback;

/// <summary>Adapts a validated CLI profile to the desktop single-deck playback transport.</summary>
/// <remarks>2026-11-25: Project CLI status without starting a worker before Play for FR-CLI-001#ac-switch and TEST-CLI-001/002.</remarks>
public sealed class CliPlaybackOutput : IDesktopPlaybackOutput, ICliPlaybackTarget
{
    private readonly CliProfileDraft profile;
    private readonly ImmutableDictionary<CliPlaybackAction, CliActionMapping> actions;
    private readonly Func<Guid, CancellationToken, Task<Outcome<(TrackAvailability Availability, string? FullPath)>>> resolveTrack;
    private readonly CliPlaybackProcess process;
    private readonly SemaphoreSlim lane = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private DeckState? active;
    private string? activePath;
    private int disposed;

    /// <summary>Captures a valid profile and the local catalog path resolver. Process creation is deferred until an action.</summary>
    /// <exception cref="ArgumentException">The draft lacks a valid CLI transport mapping.</exception>
    public CliPlaybackOutput(CliProfileDraft profile,
        Func<Guid, CancellationToken, Task<Outcome<(TrackAvailability Availability, string? FullPath)>>> resolveTrack)
    {
        var validation = CliProfileHandlers.Validate(profile);
        if (!validation.Issues.IsEmpty || !validation.Capabilities.HasFlag(PlaybackCapabilities.Transport))
            throw new ArgumentException("The CLI profile cannot provide the playback transport.", nameof(profile));
        this.profile = profile;
        this.resolveTrack = resolveTrack;
        Capabilities = validation.Capabilities;
        actions = profile.Actions.ToImmutableDictionary(mapping => mapping.Action);
        process = new(profile);
    }

    /// <summary>Gets the controls supported by the selected profile's mappings.</summary>
    public PlaybackCapabilities Capabilities { get; }

    /// <inheritdoc />
    public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid trackId, TimeSpan? startPosition, TransportStatus status, CancellationToken token) =>
        OnLaneAsync(async ct =>
        {
            if (deck != DeckId.A || trackId == Guid.Empty || !Enum.IsDefined(status) || startPosition < TimeSpan.Zero ||
                startPosition.HasValue && status != TransportStatus.Playing)
                return Invalid("invalid-cli-transport");
            var action = status switch
            {
                TransportStatus.Playing => CliPlaybackAction.Play,
                TransportStatus.Paused => CliPlaybackAction.Pause,
                _ => CliPlaybackAction.Stop
            };
            if (startPosition is { } start)
            {
                var source = await resolveTrack(trackId, ct).ConfigureAwait(false);
                return source switch
                {
                    Outcome<(TrackAvailability, string?)>.Success { Value: (TrackAvailability.Downloaded, { Length: > 0 } path) }
                        => await ApplyAsync(trackId, path, action, start, status, null, ct).ConfigureAwait(false),
                    Outcome<(TrackAvailability, string?)>.Success => Invalid("track-not-available"),
                    _ => CopyFailure(source)
                };
            }
            if (active is not { } selected || trackId != selected.TrackId)
                return new Outcome<DeckState>.Conflict("cli-track-changed", null);
            if (status == TransportStatus.Stopped && profile.Mode == CliInvocationMode.Session && !process.HasSession)
            {
                active = selected with { Status = TransportStatus.Stopped, Position = TimeSpan.Zero };
                return new Outcome<DeckState>.Success(active);
            }
            var position = status == TransportStatus.Stopped ? TimeSpan.Zero : selected.Position;
            return await ApplyAsync(trackId, activePath!, action, position, status, selected.Duration, ct).ConfigureAwait(false);
        }, token);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid trackId, TimeSpan position, CancellationToken token) => OnLaneAsync(async ct =>
    {
        if (!Capabilities.HasFlag(PlaybackCapabilities.Seek))
            return Invalid("cli-seek-unsupported");
        if (active is not { } selected || deck != selected.DeckId || trackId != selected.TrackId)
            return new Outcome<DeckState>.Conflict("cli-track-changed", null);
        if (position < TimeSpan.Zero || position > selected.Duration)
            return Invalid("seek-position-out-of-range");
        return await ApplyAsync(trackId, activePath!, CliPlaybackAction.Seek, position, selected.Status, selected.Duration, ct).ConfigureAwait(false);
    }, token);

    /// <inheritdoc />
    public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid trackId, CancellationToken token) => OnLaneAsync(async ct =>
    {
        if (active is not { } selected || deck != selected.DeckId || trackId != selected.TrackId)
            return new Outcome<DeckState>.Conflict("cli-track-changed", null);
        if (profile.Mode == CliInvocationMode.Session && !process.HasSession)
            return selected.Status == TransportStatus.Stopped ? new Outcome<DeckState>.Success(selected) :
                new Outcome<DeckState>.DependencyFailure("cli-process-exited", true);
        return actions.ContainsKey(CliPlaybackAction.Status)
            ? await ApplyAsync(trackId, activePath!, CliPlaybackAction.Status, selected.Position, null, selected.Duration, ct).ConfigureAwait(false)
            : new Outcome<DeckState>.Success(selected);
    }, token);

    /// <summary>Cancels active commands before disposing the current session worker.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
            return;
        lifetime.Cancel();
        lane.Wait();
        try { process.Dispose(); }
        finally
        {
            lifetime.Dispose();
            lane.Release();
        }
    }

    private async Task<Outcome<DeckState>> ApplyAsync(Guid track, string path, CliPlaybackAction action, TimeSpan fallbackPosition,
        TransportStatus? expectedStatus, TimeSpan? fallbackDuration, CancellationToken ct)
    {
        var result = await process.InvokeAsync(actions[action], path, fallbackPosition, action == CliPlaybackAction.Play, ct).ConfigureAwait(false);
        if (result is not Outcome<CliPlaybackProcess.Reply>.Success { Value: var reply })
            return CopyFailure(result);
        var position = reply.Position ?? fallbackPosition;
        var duration = reply.Duration ?? fallbackDuration;
        if (expectedStatus is { } status && reply.Status != status || position > duration)
        {
            process.Dispose();
            return new Outcome<DeckState>.DependencyFailure("invalid-cli-reply", false);
        }
        active = new(DeckId.A, track, position, duration, reply.Status, true, 1);
        activePath = path;
        return new Outcome<DeckState>.Success(active);
    }

    private async Task<Outcome<DeckState>> OnLaneAsync(Func<CancellationToken, Task<Outcome<DeckState>>> operation, CancellationToken token)
    {
        try
        {
            await lane.WaitAsync(token).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref disposed) != 0)
                    return new Outcome<DeckState>.Cancelled();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, token);
                return await operation(linked.Token).ConfigureAwait(false);
            }
            finally { lane.Release(); }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested || Volatile.Read(ref disposed) != 0)
        { return new Outcome<DeckState>.Cancelled(); }
    }

    private static Outcome<DeckState> Invalid(string code) => new Outcome<DeckState>.Invalid([new(code)]);

    private static Outcome<DeckState> CopyFailure<T>(Outcome<T> outcome) => outcome switch
    {
        Outcome<T>.Cancelled => new Outcome<DeckState>.Cancelled(),
        Outcome<T>.Conflict conflict => new Outcome<DeckState>.Conflict(conflict.Code, conflict.ActualRevision),
        Outcome<T>.Invalid invalid => new Outcome<DeckState>.Invalid(invalid.Issues),
        Outcome<T>.DependencyFailure failure => new Outcome<DeckState>.DependencyFailure(failure.Code, failure.Retryable),
        _ => throw new InvalidOperationException("Expected a CLI playback failure.")
    };
}
