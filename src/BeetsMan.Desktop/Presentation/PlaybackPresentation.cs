using BeetsMan.Desktop.Application.Playback;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BeetsMan.Desktop.Presentation;

/// <summary>Shares one immutable session snapshot across playback, queue, and Library presentations.</summary>
/// <remarks>2026-11-24: Preserve and scope the active session for FR-PLAYER-001#ac-modes and FR-DISPATCH-001#ac-update.</remarks>
public sealed class PlaybackPresentation(Guid sessionId) : ObservableObject
{
    private PlaybackState? state;

    /// <summary>Gets the stable session identity across presentation modes.</summary>
    public Guid SessionId { get; } = sessionId;

    /// <summary>Gets the latest received snapshot for this session.</summary>
    public PlaybackState? State => state;

    internal bool Accepts(PlaybackState incoming) => incoming.SessionId == SessionId && (state is null || incoming.Revision >= state.Revision);
    internal void Apply(PlaybackState incoming) => SetProperty(ref state, incoming, nameof(State));
}
