using System.Diagnostics;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Desktop.Tests;
using Xunit;

namespace BeetsMan.Desktop.IntegrationTests;

/// <summary>Runs the SCR03 command against activated JSON settings and a controlled shell handoff.</summary>
/// <remarks>2026-11-25: Bind TEST-CONFIG-002 to the real restart and log-folder routes.</remarks>
/// <remarks>2026-11-26: Bind the configured native log-folder handoff for FR-CONFIG-001#ac-logs.</remarks>
public sealed class LogFolderIntegrationTests : IDisposable
{
    private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("beets-log-folder-");
    private readonly CancellationToken token = TestContext.Current.CancellationToken;

    [Fact]
    public async Task DiagnosticsUsesTheActiveDirectoryUntilRestart()
    {
        var defaults = JsonBootstrapFile.ResolveDefaults(root.FullName, root.FullName);
        var initialFolder = Assert.IsType<RollingLogSettings>(defaults.RollingLog).Directory;
        var configuredFolder = Path.Combine(root.FullName, "new rolling logs");
        var browser = new RecordingBrowser();
        var library = Guid.NewGuid();
        var origin = Guid.NewGuid();
        var context = new RequestContext(Guid.NewGuid(), origin);
        var attempt = new CommandContext(context, Guid.NewGuid());
        string beforeRestart;
        await using (var startup = Open())
        {
            var active = Value(await startup.StartAsync(token));
            var original = Assert.IsType<BootstrapSettings>(active.Bootstrap.Effective);
            var changed = original with { RollingLog = original.RollingLog! with { Directory = configuredFolder } };
            var saved = Value(await startup.Bootstrap.SendAsync(new SaveBootstrapSettings(changed, active.Bootstrap.Revision), attempt, token));
            Assert.Equal(BootstrapStatus.RestartRequired, saved.Status);
            var diagnostics = Assert.IsType<DesktopPresentation>(active.Presentation).Diagnostics;
            await diagnostics.OpenLogFolderCommand.ExecuteAsync(null);
            beforeRestart = Assert.IsType<FolderOpenReceipt>(diagnostics.LogFolder).FolderPath;
        }

        await using var restarted = Open();
        var applied = Value(await restarted.StartAsync(token));
        var presentation = Assert.IsType<DesktopPresentation>(applied.Presentation);
        await presentation.Diagnostics.OpenLogFolderCommand.ExecuteAsync(null);
        var afterRestart = Assert.IsType<FolderOpenReceipt>(presentation.Diagnostics.LogFolder).FolderPath;
        browser.Accept = false;
        using var rejected = new DiagnosticsViewModel(presentation.Services, Guid.NewGuid());
        await rejected.OpenLogFolderCommand.ExecuteAsync(null);
        var failure = Assert.IsType<PresentationFailure>(rejected.Failure);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var count = browser.Handoffs.Count;
        var skip = await applied.Workspace!.SendAsync(new OpenLogFolder(), new(context, Guid.NewGuid()), cancelled.Token);
        LogFolderAssertions.Verify(new([.. browser.Handoffs], beforeRestart, afterRestart, failure.Code!,
            rejected.LogFolder is null, skip is Outcome<FolderOpenReceipt>.Cancelled && browser.Handoffs.Count == count),
            initialFolder, configuredFolder);

        DesktopStartup Open() => new(defaults, [], library, origin, presentationScheduler: new InlineScheduler(), logFolderBrowser: browser);
    }

    [Fact]
    public async Task ShellHandoffPassesOneAbsoluteDirectoryAndDoesNotCreateDisabledLogFolders()
    {
        var requests = new List<ProcessStartInfo>();
        var browser = new ShellLogFolderBrowser(request =>
        {
            requests.Add(request);
            return true;
        });
        var folder = Path.Combine(root.FullName, "rolling logs (library)");
        Assert.False(await browser.OpenAsync(folder, createIfMissing: false, token));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(requests);
        Assert.True(await browser.OpenAsync(folder, createIfMissing: true, token));
        Assert.True(Directory.Exists(folder));
        Assert.True(await browser.OpenAsync(folder, createIfMissing: false, token));
        Assert.All(requests, request =>
        {
            Assert.Equal(folder, request.FileName);
            Assert.True(request.UseShellExecute);
            Assert.Empty(request.ArgumentList);
        });
        Assert.False(await browser.OpenAsync("relative", createIfMissing: false, token));
        Assert.Equal(2, requests.Count);
    }

    public void Dispose() => root.Delete(recursive: true);

    private static T Value<T>(Outcome<T> outcome) => Assert.IsType<Outcome<T>.Success>(outcome).Value;

    private sealed class RecordingBrowser : ILogFolderBrowser
    {
        public bool Accept { get; set; } = true;
        public List<LogFolderAssertions.Handoff> Handoffs { get; } = [];

        public Task<bool> OpenAsync(string absoluteDirectory, bool createIfMissing, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Handoffs.Add(new(absoluteDirectory, createIfMissing));
            return Task.FromResult(Accept);
        }
    }

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
