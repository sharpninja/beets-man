using System.Collections.Immutable;
using BeetsMan.Desktop.Application.Dispatch;

namespace BeetsMan.Desktop.Application.Playback;

/// <summary>Contains source gains, manual blend, and the equalizer settings for the mixed output.</summary>
/// <remarks>2026-11-25: Define the backend control snapshot for FR-PLAYER-001#ac-mix.</remarks>
public sealed record PlaybackMixSettings(double MasterGain, double DeckAGain, double DeckBGain, double CrossfaderPosition,
    ImmutableArray<double> EqualizerBands, bool EqualizerBypassed)
{
    /// <summary>Uses unity gain, a flat master EQ, and the A end of the crossfader.</summary>
    public static PlaybackMixSettings Default { get; } = new(1, 1, 1, 0, [0, 0, 0, 0, 0, 0, 0, 0, 0, 0], false);

    /// <summary>Returns the independent gain control for the identified deck.</summary>
    public double DeckGain(DeckId deck) => deck == DeckId.A ? DeckAGain : DeckBGain;

    /// <summary>Validates normalized gains and ten bounded, finite EQ adjustments.</summary>
    public bool IsValid() => IsGain(MasterGain) && IsGain(DeckAGain) && IsGain(DeckBGain) && IsGain(CrossfaderPosition) && AreBandsValid(EqualizerBands);

    internal static bool IsGain(double value) => value is >= 0 and <= 1;
    internal static bool AreBandsValid(ImmutableArray<double> bands) => !bands.IsDefault && bands.Length == Default.EqualizerBands.Length &&
        bands.All(band => band is >= -12 and <= 12);
}

/// <summary>Applies the complete mixer snapshot to live or future decks and the shared output.</summary>
/// <remarks>2026-11-25: Apply FR-PLAYER-001#ac-mix through the selected audio backend.</remarks>
public interface IPlaybackMixTarget
{
    /// <summary>Applies a validated snapshot and interrupts a pending automatic blend when manual input takes over.</summary>
    Task<Outcome<bool>> ApplyMixAsync(PlaybackMixSettings settings, bool abandonAutomaticFade, CancellationToken cancellationToken);
}

/// <summary>Dispatches mixer and master equalizer intents under the playback session's existing serialization.</summary>
/// <remarks>2026-11-25: Map FR-PLAYER-001#ac-mix to the audio port and report only confirmed state.</remarks>
public sealed partial class PlaybackSessionHandlers : ICommandHandler<SetMix, PlaybackState>, ICommandHandler<SetEqualizer, PlaybackState>
{
    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(SetMix command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ct => ApplyMixEditAsync(command.Edit, ct), token);

    /// <inheritdoc />
    public Task<Outcome<PlaybackState>> HandleAsync(SetEqualizer command, CommandContext context, CancellationToken token) =>
        RunCommandAsync(command.SessionId, command, context, ct => ApplyEqualizerEditAsync(command.Edit, ct), token);

    private Task<Outcome<PlaybackState>> ApplyMixEditAsync(MixEdit edit, CancellationToken token)
    {
        var capability = edit is MixEdit.MasterGain ? PlaybackCapabilities.Volume : PlaybackCapabilities.Mixer;
        if (MixBackend is not { } target || !state.Capabilities.HasFlag(capability))
            return Task.FromResult(Invalid("playback-mix-unsupported"));
        if (edit is MixEdit.AutoFadeEnabled enabled)
            return SetAutomaticFadeAsync(enabled.Enabled, token);
        var next = edit switch
        {
            MixEdit.MasterGain gain when PlaybackMixSettings.IsGain(gain.Gain) => mix with { MasterGain = gain.Gain },
            MixEdit.DeckGain { DeckId: DeckId.A } gain when PlaybackMixSettings.IsGain(gain.Gain) => mix with { DeckAGain = gain.Gain },
            MixEdit.DeckGain { DeckId: DeckId.B } gain when PlaybackMixSettings.IsGain(gain.Gain) => mix with { DeckBGain = gain.Gain },
            MixEdit.CrossfaderPosition blend when PlaybackMixSettings.IsGain(blend.Position) => mix with { CrossfaderPosition = blend.Position },
            _ => null
        };
        return next is null ? Task.FromResult(Invalid("invalid-playback-mix")) :
            ApplyMixAsync(target, next, edit is MixEdit.CrossfaderPosition, token);
    }

    private Task<Outcome<PlaybackState>> ApplyEqualizerEditAsync(EqualizerEdit edit, CancellationToken token)
    {
        if (MixBackend is not { } target || !state.Capabilities.HasFlag(PlaybackCapabilities.Equalizer))
            return Task.FromResult(Invalid("playback-equalizer-unsupported"));
        var next = edit switch
        {
            EqualizerEdit.ApplyBands bands when PlaybackMixSettings.AreBandsValid(bands.Bands) => mix with { EqualizerBands = bands.Bands },
            EqualizerEdit.ResetBands => mix with { EqualizerBands = PlaybackMixSettings.Default.EqualizerBands },
            EqualizerEdit.Bypass bypass => mix with { EqualizerBypassed = bypass.Enabled },
            _ => null
        };
        return next is null ? Task.FromResult(Invalid("invalid-playback-equalizer")) : ApplyMixAsync(target, next, false, token);
    }

    private async Task<Outcome<PlaybackState>> ApplyMixAsync(IPlaybackMixTarget target, PlaybackMixSettings next, bool abandon, CancellationToken token)
    {
        if (!abandon && mix == next)
            return new Outcome<PlaybackState>.Success(state);
        var applied = await target.ApplyMixAsync(next, abandon, token).ConfigureAwait(false);
        if (applied is not Outcome<bool>.Success confirmed)
            return CopyFailure(applied);
        if (!confirmed.Value)
            return new Outcome<PlaybackState>.DependencyFailure("invalid-playback-mix-receipt", false);
        mix = next;
        var current = state.Output;
        if (abandon && automaticFadePreference is not null)
        {
            automaticFade = null;
            manualFadeEntry = state.ActiveQueueEntryId;
        }
        state = state with
        {
            Revision = checked(state.Revision + 1),
            Output = new(next.MasterGain, next.CrossfaderPosition, current?.AutoFadeEnabled ?? false,
                !abandon && current is { FadeActive: true }, next.EqualizerBands, next.EqualizerBypassed, current?.FadeErrorCode),
            Decks = [.. state.Decks.Select(deck => deck with { Gain = next.DeckGain(deck.DeckId) })]
        };
        return new Outcome<PlaybackState>.Success(state);
    }

    private static PlaybackMixSettings InitialMix(PlaybackState initial, DeckId primary) => PlaybackMixSettings.Default with
    {
        MasterGain = initial.Output?.Gain ?? 1,
        CrossfaderPosition = initial.Output?.CrossfaderPosition ?? (primary == DeckId.A ? 0 : 1),
        EqualizerBands = initial.Output is { EqualizerBands.IsDefaultOrEmpty: false } output ? output.EqualizerBands : PlaybackMixSettings.Default.EqualizerBands,
        EqualizerBypassed = initial.Output?.EqualizerBypassed ?? false,
        DeckAGain = initial.Decks.IsDefault ? 1 : initial.Decks.FirstOrDefault(deck => deck.DeckId == DeckId.A)?.Gain ?? 1,
        DeckBGain = initial.Decks.IsDefault ? 1 : initial.Decks.FirstOrDefault(deck => deck.DeckId == DeckId.B)?.Gain ?? 1
    };

    private static MasterOutput? InitialOutput(PlaybackState initial, DeckId primary) => initial.Output ??
        ((initial.Capabilities & (PlaybackCapabilities.Volume | PlaybackCapabilities.Mixer | PlaybackCapabilities.Equalizer)) != 0
            ? new(1, primary == DeckId.A ? 0 : 1, false, false, PlaybackMixSettings.Default.EqualizerBands, false) : null);
}
