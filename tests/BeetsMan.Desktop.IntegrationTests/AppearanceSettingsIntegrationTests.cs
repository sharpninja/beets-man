using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using BeetsMan.Host.Contracts.V1;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Drives the STB07 Appearance commands through the registered CQRS handlers and LiteDB.</summary>
/// <remarks>2026-11-25: Verify draft isolation, source changes, and durable inheritance for TEST-SETTINGS-001.</remarks>
/// <remarks>2026-11-26: Verify local override and restoration of shared appearance for FR-SETTINGS-001#ac-precedence.</remarks>
public sealed class AppearanceSettingsIntegrationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-appearance-settings-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task PreviewApplyAndInheritPreserveTheSharedDefault()
    {
        var path = Path.Combine(root.FullName, "settings.db");
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        using (var store = LiteDbDesktopPlaylistStore.Open(path, allowCreate: true))
        {
            var repository = (IDesktopPreferenceStore)store;
            var dispatcher = new PlaybackPreferenceHandlers(library, repository).Register(new()).Build();
            var builtIn = Value(await dispatcher.QueryAsync(new GetPreference(AppearancePreferences.Theme), context, token));
            Assert.Equal(new PreferenceValue.Text("system"), builtIn.Value);
            Assert.Equal(PreferenceSource.BuiltIn, builtIn.Source);
            Assert.Null(builtIn.SharedDefault);
            Value(await repository.CacheSharedPreferenceAsync(library, 4,
                new("appearance.theme", 1, null, Text: "system"), token));

            using var settings = new SettingsViewModel(new(dispatcher, new InlineScheduler(), origin), Guid.NewGuid());
            await settings.OpenCategoryCommand.ExecuteAsync(AppearancePreferences.Category);
            var initial = Assert.IsType<EffectivePreference>(settings.EffectiveAppearance);
            settings.StageAppearanceCommand.Execute("dark");
            var draft = Assert.IsType<PreferenceEdit>(settings.AppearanceDraft);
            Assert.Equal(initial.Revision, draft.Revision);
            var whileDrafting = Observe(settings.EffectiveAppearance);
            var draftWroteNothing = initial == Value(await dispatcher.QueryAsync(new GetPreference(initial.Key), context, token));
            await settings.ApplyAppearanceCommand.ExecuteAsync(null);
            var applied = Assert.IsType<EffectivePreference>(settings.EffectiveAppearance);
            var draftCleared = settings.AppearanceDraft is null;
            Assert.True(applied.Revision > initial.Revision);
            await settings.InheritAppearanceCommand.ExecuteAsync(null);
            var inherited = Observe(settings.EffectiveAppearance);
            var current = Assert.IsType<EffectivePreference>(settings.EffectiveAppearance);
            await settings.ApplyAppearanceCommand.ExecuteAsync(new PreferenceEdit(current.Key, new PreferenceValue.Text("invalid"), current.Revision));
            var issue = Assert.Single(Assert.IsType<PresentationFailure>(settings.Failure).Issues);
            Assert.Equal(current, Value(await dispatcher.QueryAsync(new GetPreference(current.Key), context, token)));
            AppearancePreferenceAssertions.Verify(new(Observe(initial), Assert.IsType<PreferenceValue.Text>(draft.Value).Value,
                whileDrafting, Observe(applied), inherited, Assert.IsType<PreferenceValue.Text>(applied.SharedDefault).Value,
                draftCleared, draftWroteNothing, issue.Code));
        }

        using var reopened = LiteDbDesktopPlaylistStore.Open(path, allowCreate: false);
        var offline = new PlaybackPreferenceHandlers(library, reopened).Register(new()).Build();
        var durable = Value(await offline.QueryAsync(new GetPreference(AppearancePreferences.Theme), context, token));
        Assert.Equal(new AppearancePreferenceAssertions.Theme("system", "HostDefault"), Observe(durable));
    }

    private static AppearancePreferenceAssertions.Theme Observe(EffectivePreference? state)
    {
        var theme = Assert.IsType<EffectivePreference>(state);
        return new(Assert.IsType<PreferenceValue.Text>(theme.Value).Value, theme.Source.ToString());
    }

    private static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    public void Dispose() => root.Delete(recursive: true);

    private sealed class InlineScheduler : IUiScheduler
    {
        public Task PostAsync(Action action, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            action();
            return Task.CompletedTask;
        }
    }
}
