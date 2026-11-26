using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Tests;
using LiteDB;
using Xunit;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Opens the real host configuration from Settings and applies it through the shared editor.</summary>
    /// <remarks>2026-11-25: Bind SCR29 permission, version, and offline behavior to TEST-BEETS-002.</remarks>
    /// <remarks>2026-11-26: Verify the authorized host import settings and its offline behavior for FR-BEETS-001#ac-open.</remarks>
    [Fact]
    public async Task SettingsImportDefaultsUsesTheSharedHostEditorAndRevision()
    {
        var executable = Environment.GetEnvironmentVariable("BEETSMAN_TEST_BEETS") ?? (OperatingSystem.IsLinux() ? "/usr/bin/beet" : null);
        Assert.True(executable is not null && File.Exists(executable), "Set BEETSMAN_TEST_BEETS to the installed beets executable.");
        const string listenerCredential = "fixture-import-defaults-listener";
        var listener = Guid.NewGuid();
        var database = Path.Combine(root, "import-defaults.db");
        var catalog = Path.Combine(root, "native.db");
        var yaml = Path.Combine(root, "beets.yaml");
        var music = Directory.CreateDirectory(Path.Combine(root, "music")).FullName;
        SeedHost(database);
        using (var store = new LiteDatabase(database))
            store.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
                { ["_id"] = listener, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active" });
        await PrepareNativeFixtureAsync(executable!, yaml, catalog, music);
        var ffmpeg = Environment.GetEnvironmentVariable("BEETSMAN_TEST_FFMPEG") ?? executable!;
        var options = new LibraryHostOptions(library, database, music, Path.Combine(root, "uploads"), Path.Combine(root, "work"), [],
            Beets: new(executable!, catalog, Matching: new(ffmpeg, new("http://127.0.0.1:1/")), ConfigurationPath: yaml));
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        {
            [secret] = new(library, instance, CanManageRegistrations: true),
            [listenerCredential] = new(library, listener)
        });
        using var certificate = CreateCertificate();
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        await using var host = await StartSetupHostAsync(options, certificate, verifier, credentials);
        var address = new Uri(host.Urls.Single());
        using var operatorClient = new HttpClient(CreateClientHandler(certificate));
        using var listenerClient = new HttpClient(CreateClientHandler(certificate));
        await using var owner = OpenDesktop("operator", instance, new(address, operatorClient, new TokenProvider(library, instance, secret)));
        await using var reader = OpenDesktop("listener", listener, new(address, listenerClient, new TokenProvider(library, listener, listenerCredential)));
        await using var disconnected = OpenDesktop("disconnected", instance, null);
        var op = await StartPresentationAsync(owner);
        var other = await StartPresentationAsync(reader);
        var offline = await StartPresentationAsync(disconnected);

        await op.Settings.OpenCategoryCommand.ExecuteAsync(NotificationPreferences.Category);
        var local = Assert.IsType<PreferenceCategorySnapshot>(op.Settings.Category);
        Assert.True(op.Settings.EditImportDefaultsCommand.CanExecute(null));
        await op.Settings.EditImportDefaultsCommand.ExecuteAsync(null);
        var editor = Assert.IsType<HostLibraryViewModel>(op.Settings.ImportDefaultsEditor);
        var initial = Assert.IsType<HostLibraryConfiguration>(editor.Configuration);
        Assert.Equal(library, initial.LibraryId);
        var modified = initial.Settings with { ImportDefaults = initial.Settings.ImportDefaults.SetItem("behavior", "keep-tags") };
        Assert.True(editor.ApplyConfigurationCommand.CanExecute(modified));
        await editor.ApplyConfigurationCommand.ExecuteAsync(modified);
        Assert.Null(editor.Failure);
        Assert.False(editor.IsInteractionBlocked);
        await op.HostLibrary.OpenConfigurationCommand.ExecuteAsync(null);
        var saved = Assert.IsType<HostLibraryConfiguration>(op.HostLibrary.Configuration);

        await other.Settings.EditImportDefaultsCommand.ExecuteAsync(null);
        var listenerEditor = Assert.IsType<HostLibraryViewModel>(other.Settings.ImportDefaultsEditor);
        var listenerState = Assert.IsType<HostLibraryConfiguration>(listenerEditor.Configuration);
        var change = listenerState.Settings with { ImportDefaults = listenerState.Settings.ImportDefaults.SetItem("behavior", "review-suggested-matches") };
        var denied = !listenerState.CanEdit && !listenerEditor.ApplyConfigurationCommand.CanExecute(change);
        await listenerEditor.ApplyConfigurationCommand.ExecuteAsync(change);
        await op.HostLibrary.OpenConfigurationCommand.ExecuteAsync(null);
        denied &= op.HostLibrary.Configuration?.Revision == saved.Revision;
        Assert.Equal(saved.Settings.ImportDefaults["behavior"], op.HostLibrary.Configuration?.Settings.ImportDefaults["behavior"]);

        await offline.Settings.EditImportDefaultsCommand.ExecuteAsync(null);
        var offlineEditor = Assert.IsType<HostLibraryViewModel>(offline.Settings.ImportDefaultsEditor);
        Assert.Null(offlineEditor.Configuration);
        var failure = Assert.IsType<PresentationFailure>(offlineEditor.Failure);
        ImportDefaultsEntryAssertions.Verify(new(ReferenceEquals(editor, op.HostLibrary), initial.Settings.ImportDefaults["behavior"], initial.Revision,
            saved.Settings.ImportDefaults["behavior"], saved.Revision, ReferenceEquals(local, op.Settings.Category), denied, failure.Code!));

        async Task<DesktopPresentation> StartPresentationAsync(DesktopStartup desktop) =>
            Assert.IsType<DesktopPresentation>(Assert.IsType<Outcome<DesktopStartupState>.Success>(await desktop.StartAsync(Token)).Value.Presentation);

        DesktopStartup OpenDesktop(string name, Guid origin, DesktopHostBinding? binding) =>
            new(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, name), music), [], library, origin, host: binding,
                presentationScheduler: new InlineScheduler());
    }
}
