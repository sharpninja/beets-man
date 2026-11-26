using System.Collections.Immutable;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

public sealed partial class SETTINGSAcceptanceTests
{
    /// <summary>Applies built-in, shared and local precedence and restores inheritance on clearing an override.</summary>
    /// <remarks>2026-11-26: Bind the named layered preference case for FR-SETTINGS-001#ac-precedence.</remarks>
    [Fact]
    public Task Precedence_MatchesContract() => PlaybackRestartIntegrationTests.PlaybackPreferenceLayerPreservesLocalOverrideAndLastSharedDefault();

    /// <summary>Retains cached defaults offline and rejects invalid or local-only shared values.</summary>
    /// <remarks>2026-11-26: Bind the named offline preference case for FR-SETTINGS-001#ac-availability.</remarks>
    [Fact]
    public async Task Availability_MatchesContract()
    {
        var path = Path.Combine(root.FullName, "preferences.db");
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var request = new RequestContext(Guid.NewGuid(), origin);
        var host = new PlaybackRestartIntegrationTests.PreferenceHost(library, origin);
        var fade = PlaybackPreferences.AutomaticFadeDuration;
        var original = new PreferenceValue.Duration(TimeSpan.FromSeconds(4));
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var connected = new PlaybackPreferenceHandlers(library, store, host).Register(new()).Build();
            var accepted = await WriteShared(connected, fade, original, 0);
            Assert.Equal(1, Success(accepted).HostRevision);
            var recorded = host.LastChange;
            foreach (var key in new[] { PlaybackPreferences.ResumeAfterRestart, "credentials", "library-local-path", "plugin-trust", "system-shortcut-registration" })
                Assert.Equal("invalid-shared-playback-preference", Assert.Single(Assert.IsType<Outcome<PreferenceCategorySnapshot>.Invalid>(
                    await WriteShared(connected, key, new PreferenceValue.Flag(true), 1)).Issues).Code);
            Assert.Same(recorded, host.LastChange);
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var offline = new PlaybackPreferenceHandlers(library, reopened).Register(new()).Build();
        var before = Success(await offline.QueryAsync(new GetPreference(fade), request, token));
        Assert.Equal(original, before.Value);
        Assert.Equal(PreferenceSource.HostDefault, before.Source);
        var invalid = await offline.SendAsync(new SetLocalOverride(fade, new PreferenceValue.Duration(TimeSpan.FromSeconds(-1)), before.Revision),
            new(request, Guid.NewGuid()), token);
        Assert.IsType<Outcome<EffectivePreference>.Invalid>(invalid);
        var failed = await WriteShared(offline, fade, new PreferenceValue.Duration(TimeSpan.FromSeconds(8)), 1);
        Assert.Equal("shared-preference-host-unavailable", Assert.IsType<Outcome<PreferenceCategorySnapshot>.DependencyFailure>(failed).Code);
        Assert.Equal(before, Success(await offline.QueryAsync(new GetPreference(fade), request, token)));

        Task<Outcome<PreferenceCategorySnapshot>> WriteShared(IRequestDispatcher bus, string key, PreferenceValue value, long revision) =>
            bus.SendAsync(new SetSharedPreferences(PlaybackPreferences.Category,
                ImmutableDictionary<string, PreferenceValue>.Empty.Add(key, value), revision), new(request, Guid.NewGuid()), token);
    }
}
