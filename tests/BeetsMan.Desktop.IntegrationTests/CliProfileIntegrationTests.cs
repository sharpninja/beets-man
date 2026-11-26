using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Exercises CLI editing through the desktop dispatcher and a disposable local YAML store.</summary>
/// <remarks>2026-11-25: Bind TEST-CLI-001 and FR-CLI-001#ac-profiles to both documented invocation modes.</remarks>
public sealed partial class CLIAcceptanceTests : IDisposable
{
    private readonly DirectoryInfo directory = Directory.CreateTempSubdirectory("beets-cli-profiles-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;
    private static readonly ImmutableDictionary<string, TransportStatus> Statuses =
        new Dictionary<string, TransportStatus>
        {
            ["running"] = TransportStatus.Playing, ["paused"] = TransportStatus.Paused, ["idle"] = TransportStatus.Stopped
        }.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Validates both invocation modes and round-trips exact profile data without starting playback.</summary>
    /// <remarks>2026-11-26: Bind the named production profile case for FR-CLI-001#ac-profiles.</remarks>
    [Fact]
    public async Task Profiles_MatchesContract()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(directory.FullName, directory.FullName);
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        CliProfileDraft session, oneShot;
        CliProfileReceipt receipt;
        string[] exact = ["--session", "path with spaces", "--literal=$value"];
        var audio = new UntouchedOutput();
        DesktopStartup Open() => new(defaults, [], library, origin, _ => audio);
        await using (var desktop = Open())
        {
            var ready = Success(await desktop.StartAsync(token));
            Assert.Equal(TransportStatus.Stopped, Success(ready.Playback!).Status);
            var bus = Assert.IsAssignableFrom<IRequestDispatcher>(ready.Workspace);
            var draft = Success(await bus.QueryAsync(new GetCliProfile(), request, token));
            Assert.Equal(0, draft.Revision);
            oneShot = AsMode(draft, CliInvocationMode.OneShot, exact);
            session = AsMode(draft with { ProfileId = Guid.NewGuid() }, CliInvocationMode.Session, exact);
            CliProfileAssertions.Verify(await Validate(bus, oneShot), oneShot, PlaybackCapabilities.Transport);
            CliProfileAssertions.Verify(await Validate(bus, session), session, PlaybackCapabilities.Transport | PlaybackCapabilities.Seek);
            var bad = oneShot with { Actions = [], Reply = null };
            CliProfileAssertions.Verify(await Validate(bus, bad), bad, PlaybackCapabilities.None,
                "cli-transport-mappings-required", "invalid-cli-status-map");
            var unboundArg = oneShot with
            {
                Actions = oneShot.Actions.SetItem(0, new(CliPlaybackAction.Play, ["wrong token"]))
            };
            Assert.Contains((await Validate(bus, unboundArg)).Issues, issue => issue.Code == "invalid-cli-action-map");
            var unboundSeek = session with { Reply = session.Reply! with { PositionMillisecondsField = null } };
            Assert.Contains((await Validate(bus, unboundSeek)).Issues, issue => issue.Code == "cli-seek-mapping-required");
            Assert.IsType<Outcome<CliProfileReceipt>.Invalid>(await Save(bus, bad));
            Success(await Save(bus, oneShot));
            receipt = Success(await Save(bus, session));
            Assert.Equal(1, Assert.IsType<Outcome<CliProfileReceipt>.Conflict>(await Save(bus, session)).ActualRevision);
            var update = session with { Revision = 1, Timeout = TimeSpan.FromSeconds(3) };
            var writes = await Task.WhenAll(Save(bus, update), Save(bus, update));
            receipt = Assert.Single(writes.OfType<Outcome<CliProfileReceipt>.Success>()).Value;
            Assert.Equal(2, Assert.Single(writes.OfType<Outcome<CliProfileReceipt>.Conflict>()).ActualRevision);
            Success(await desktop.StopAsync(token));
        }

        await using var reopened = Open();
        var launched = Success(await reopened.StartAsync(token));
        var recoveredBus = Assert.IsAssignableFrom<IRequestDispatcher>(launched.Workspace);
        var recovered = Success(await recoveredBus.QueryAsync(new GetCliProfile(session.ProfileId), request, token));
        CliProfileAssertions.VerifyReadback(recovered, receipt, session.ProfileId, 2, exact);
        Assert.Equal(TimeSpan.FromSeconds(3), recovered.Timeout);
        Assert.Equal("seek-position", Assert.Single(recovered.Actions, action => action.Action == CliPlaybackAction.Seek).RequestKind);
        Assert.Equal(TransportStatus.Playing, recovered.Reply!.StatusValues["running"]);
        Assert.Equal("positionMs", recovered.Reply.PositionMillisecondsField);
        var restoredOneShot = Success(await recoveredBus.QueryAsync(new GetCliProfile(oneShot.ProfileId), request, token));
        Assert.Equal(new[] { "play command", "{path}" }, restoredOneShot.Actions[0].Arguments.ToArray());
        var path = Path.Combine(Path.GetDirectoryName(defaults.SettingsPath)!, "playback-profiles", $"{session.ProfileId:D}.yaml");
        var yaml = await File.ReadAllTextAsync(path, token);
        Assert.Contains("schemaVersion: 1", yaml);
        Assert.Contains("mode: session", yaml);
        var corrupt = yaml + "\nrevision: 2\n";
        await File.WriteAllTextAsync(path, corrupt, token);
        Assert.Equal("invalid-cli-profile-file", Assert.IsType<Outcome<CliProfileDraft>.DependencyFailure>(
            await recoveredBus.QueryAsync(new GetCliProfile(session.ProfileId), request, token)).Code);
        Assert.Equal("invalid-cli-profile-file", Assert.IsType<Outcome<CliProfileReceipt>.DependencyFailure>(
            await Save(recoveredBus, recovered)).Code);
        Assert.Equal(corrupt, await File.ReadAllTextAsync(path, token));
        Assert.Equal(0, audio.Invocations);

        async Task<CliProfileValidation> Validate(IRequestDispatcher bus, CliProfileDraft profile) =>
            Success(await bus.QueryAsync(new ValidateCliProfile(profile), request, token));
        Task<Outcome<CliProfileReceipt>> Save(IRequestDispatcher bus, CliProfileDraft profile) =>
            bus.SendAsync(new SaveCliProfile(profile, profile.Revision), new(request, Guid.NewGuid()), token);
    }

    /// <summary>Builds the independent fixture's two profile shapes from a fresh app draft.</summary>
    private static CliProfileDraft AsMode(CliProfileDraft draft, CliInvocationMode mode, string[] tokens) => draft with
    {
        Mode = mode, Executable = "standalone-player", Arguments = [.. tokens],
        Capabilities = PlaybackCapabilities.Transport | PlaybackCapabilities.Equalizer |
            (mode == CliInvocationMode.Session ? PlaybackCapabilities.Seek : PlaybackCapabilities.None),
        Actions = mode == CliInvocationMode.OneShot
            ? [new(CliPlaybackAction.Play, ["play command", "{path}"]), new(CliPlaybackAction.Pause, ["pause"]),
                new(CliPlaybackAction.Stop, ["stop"])]
            : [new(CliPlaybackAction.Play, [], "play"), new(CliPlaybackAction.Pause, [], "pause"), new(CliPlaybackAction.Stop, [], "stop"),
                new(CliPlaybackAction.Seek, [], "seek-position")],
        Reply = new("playerState", Statuses, mode == CliInvocationMode.Session ? "positionMs" : null)
    };

    public void Dispose() => directory.Delete(recursive: true);

    private static T Success<T>(Outcome<T> result) => Assert.IsType<Outcome<T>.Success>(result).Value;

    private sealed class UntouchedOutput : IDesktopPlaybackOutput
    {
        internal int Invocations { get; private set; }
        public Task<Outcome<DeckState>> ChangeAsync(DeckId deck, Guid track, TimeSpan? position, TransportStatus status, CancellationToken token) => Reject();
        public Task<Outcome<DeckState>> SeekAsync(DeckId deck, Guid track, TimeSpan position, CancellationToken token) => Reject();
        public Task<Outcome<DeckState>> ReadAsync(DeckId deck, Guid track, CancellationToken token) => Reject();
        public void Dispose() { }

        private Task<Outcome<DeckState>> Reject()
        {
            Invocations++;
            return Task.FromResult<Outcome<DeckState>>(new Outcome<DeckState>.DependencyFailure("unexpected-audio-invocation", false));
        }
    }
}
