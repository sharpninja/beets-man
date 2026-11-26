using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Desktop.Presentation;
using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using BeetsMan.Host.Tests;
using Microsoft.AspNetCore.Builder;
using Xunit;
using static BeetsMan.Host.Tests.DesktopRegistrationAssertions;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    private const string RegistryOperatorCredential = "fixture-registry-operator";
    private const string RegistryListenerCredential = "fixture-registry-listener";
    private const string RegistryThirdCredential = "fixture-registry-third";

    /// <summary>Verifies the full independent registration lifecycle against HTTPS and LiteDB.</summary>
    /// <remarks>2026-11-25: Bind the mock lifecycle and exact revocation effects to production for TEST-RECORDS-001.</remarks>
    /// <remarks>2026-11-26: Verify authorized enrollment and revocation for FR-RECORDS-001#ac-register.</remarks>
    /// <remarks>2026-11-26: Keep the real registry fixture under the named FR-RECORDS-001#ac-register case.</remarks>
    internal async Task RegisteredDesktopEnrollmentAndRevocationMatchTheContract()
    {
        var listener = Guid.NewGuid();
        var enrollment = Guid.NewGuid();
        var revocation = Guid.NewGuid();
        using var certificate = CreateCertificate();
        var fixture = RegistrationSettings(certificate, listener, Guid.NewGuid());
        await using (var host = await OpenRegistrationHostAsync(fixture, certificate))
        {
            var address = new Uri(host.Urls.Single());
            using var admin = RegistrationClient(address, certificate, RegistryOperatorCredential);
            using var guest = RegistrationClient(address, certificate, RegistryListenerCredential);
            var register = new RegisterDesktopRequest(listener, enrollment, "Travel laptop", 1);
            var revoke = new RevokeDesktopRequest(instance, revocation, listener, 2);
            await VerifyAsync(async step =>
            {
                using var reply = step switch
                {
                    Step.OperatorPending or Step.ActiveRoster or Step.OperatorRosterAfterRevocation => await admin.GetAsync(RegistrationPath, Token),
                    Step.ListenerPending or Step.RevokedCannotReadRoster => await guest.GetAsync(RegistrationPath, Token),
                    Step.RegisterOperator => await RegisterAsync(admin, new(instance, Guid.NewGuid(), "Studio desktop", 0)),
                    Step.StaleListenerEnrollment => await RegisterAsync(guest, register with { ExpectedRevision = 0 }),
                    Step.RegisterListener or Step.ReplayEnrollment => await RegisterAsync(guest, register),
                    Step.DifferentEnrollmentIntent => await RegisterAsync(guest, register with { Name = "Different" }),
                    Step.ListenerCannotPublishBeforeEnrollment or Step.ListenerPublishes or Step.RevokedCannotPublish =>
                        await guest.PostAsJsonAsync(LibraryRecordRoutes.PublishPath(library), new PublishLibraryRecordRequest(listener,
                            Guid.NewGuid(), new LibraryRecordIntent.SharedDefault(SharedLibraryDefaults.Theme, null, 0, Text: "system")), Token),
                    Step.ListenerCannotRevoke => await RevokeAsync(guest, new(listener, Guid.NewGuid(), instance, 2)),
                    Step.StaleRevocation => await RevokeAsync(admin, revoke with { ExpectedRevision = 1 }),
                    Step.RevokeListener or Step.ReplayRevocation => await RevokeAsync(admin, revoke),
                    Step.RevokedCannotReadFeed => await guest.GetAsync(CatalogReplicationRoutes.ChangesPath(library, 0, null, 10), Token),
                    Step.RevokedCannotEnroll => await RegisterAsync(guest, register with { OperationId = Guid.NewGuid(), ExpectedRevision = 3 }),
                    _ => throw new ArgumentOutOfRangeException(nameof(step))
                };
                var status = (int)reply.StatusCode;
                if (reply.StatusCode == HttpStatusCode.Conflict)
                {
                    var problem = Assert.IsType<RegistrationHttpProblem>(await reply.Content.ReadFromJsonAsync<RegistrationHttpProblem>(Token));
                    return new(status, problem.ActualRevision, problem.Code);
                }
                if (!reply.IsSuccessStatusCode || step == Step.ListenerPublishes)
                    return new(status);
                if (step is Step.RegisterOperator or Step.RegisterListener or Step.ReplayEnrollment)
                {
                    var accepted = Assert.IsType<RegisterDesktopResponse>(await reply.Content.ReadFromJsonAsync<RegisterDesktopResponse>(Token));
                    Assert.Equal(library, accepted.LibraryId);
                    Assert.Equal(step == Step.RegisterOperator ? instance : listener, accepted.Instance.InstanceId);
                    if (step != Step.RegisterOperator)
                        Assert.Equal(enrollment, accepted.OperationId);
                    return new(status, accepted.Revision, Describe(accepted.Instance));
                }
                if (step is Step.RevokeListener or Step.ReplayRevocation)
                {
                    var accepted = Assert.IsType<RevokeDesktopResponse>(await reply.Content.ReadFromJsonAsync<RevokeDesktopResponse>(Token));
                    Assert.Equal(revocation, accepted.OperationId);
                    Assert.Equal(listener, accepted.InstanceId);
                    return Summarize(accepted.Directory);
                }
                return Summarize(Assert.IsType<DesktopRegistrationDirectory>(await reply.Content.ReadFromJsonAsync<DesktopRegistrationDirectory>(Token)));
            });
            await host.StopAsync(Token);
        }

        await using var restarted = await OpenRegistrationHostAsync(fixture, certificate);
        using var resumedOperator = RegistrationClient(new(restarted.Urls.Single()), certificate, RegistryOperatorCredential);
        using var replay = await RevokeAsync(resumedOperator, new(instance, revocation, listener, 2));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var durable = Assert.IsType<RevokeDesktopResponse>(await replay.Content.ReadFromJsonAsync<RevokeDesktopResponse>(Token));
        Assert.Equal(3, durable.Directory.Revision);
        Assert.True(durable.Directory.Instances.Single(item => item.InstanceId == listener).Revoked);
        await restarted.StopAsync(Token);

        Response Summarize(DesktopRegistrationDirectory directory)
        {
            Assert.Equal(library, directory.LibraryId);
            return new(200, directory.Revision, string.Join('|', directory.Instances.Select(Describe)));
        }
    }

    /// <summary>Runs the composed Replica ViewModel against the real registry without activating screens.</summary>
    /// <remarks>2026-11-25: Keep credentials scope-bound and use the reviewed revocation revision for TEST-RECORDS-001.</remarks>
    [Fact]
    public async Task DesktopCommandsHonorCredentialScopeAndTheReviewedRevocationRevision()
    {
        var listenerId = Guid.NewGuid();
        var third = Guid.NewGuid();
        using var certificate = CreateCertificate();
        var fixture = RegistrationSettings(certificate, listenerId, third);
        await using var host = await OpenRegistrationHostAsync(fixture, certificate);
        var address = new Uri(host.Urls.Single());
        using var operatorClient = new HttpClient(CreateClientHandler(certificate));
        using var listenerClient = new HttpClient(CreateClientHandler(certificate));
        var operatorCredentials = new RegistryFixtureSelection(library, instance, RegistryOperatorCredential);
        var listenerCredentials = new RegistryFixtureSelection(library, listenerId, RegistryListenerCredential);
        await using var desktop = new DesktopStartup(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "operator"), fixture.Host.MusicRoot),
            [], library, instance, host: new(address, operatorClient, operatorCredentials), presentationScheduler: new InlineScheduler());
        await using var listener = new DesktopStartup(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "listener"), fixture.Host.MusicRoot),
            [], library, listenerId, host: new(address, listenerClient, listenerCredentials), presentationScheduler: new InlineScheduler());
        var ui = Assert.IsType<DesktopPresentation>(Assert.IsType<Outcome<DesktopStartupState>.Success>(
            await desktop.StartAsync(Token)).Value.Presentation);
        var listenerUi = Assert.IsType<DesktopPresentation>(Assert.IsType<Outcome<DesktopStartupState>.Success>(
            await listener.StartAsync(Token)).Value.Presentation);
        await ui.Replica.LoadInstancesCommand.ExecuteAsync(null);
        Assert.Empty(Assert.IsType<RegisteredInstancesSnapshot>(ui.Replica.Instances).Instances);
        await ui.Replica.RegisterCommand.ExecuteAsync(new RegistrationDraft("Studio desktop", new("https://unconfigured.invalid/"), operatorCredentials.Selection));
        Assert.Empty(operatorCredentials.Selections);
        Assert.Equal("desktop-registration-host-or-selection-invalid",
            Assert.Single(Assert.IsType<PresentationFailure>(ui.Replica.Failure).Issues).Code);
        await ui.Replica.RegisterCommand.ExecuteAsync(new RegistrationDraft("Studio desktop", address, operatorCredentials.Selection));
        Assert.Equal(1, Assert.IsType<RegistrationReceipt>(ui.Replica.Registration).Revision);
        Assert.True(Assert.IsType<RegisteredInstancesSnapshot>(ui.Replica.Instances).CanManage);
        Assert.Equal(operatorCredentials.Selection, Assert.Single(operatorCredentials.Selections));

        await listenerUi.Replica.LoadInstancesCommand.ExecuteAsync(null);
        Assert.Equal(1, Assert.IsType<RegisteredInstancesSnapshot>(listenerUi.Replica.Instances).Revision);
        await listenerUi.Replica.RegisterCommand.ExecuteAsync(new RegistrationDraft("Travel laptop", address, listenerCredentials.Selection));
        Assert.False(Assert.IsType<RegisteredInstancesSnapshot>(listenerUi.Replica.Instances).CanManage);
        Assert.Equal(2, listenerUi.Replica.Instances!.Instances.Length);
        await listenerUi.Replica.OpenRevocationCommand.ExecuteAsync(instance);
        Assert.Empty(Assert.IsType<ShellState>(listenerUi.Shell.State).Overlays);

        await ui.Replica.LoadInstancesCommand.ExecuteAsync(null);
        await ui.Replica.RevokeCommand.ExecuteAsync(listenerId);
        Assert.False(ui.Replica.Instances!.Instances.Single(item => item.InstanceId == listenerId).Revoked);
        await ui.Replica.OpenRevocationCommand.ExecuteAsync(listenerId);
        var approved = Assert.Single(Assert.IsType<ShellState>(ui.Shell.State).Overlays);
        Assert.Equal(2, Assert.IsType<OverlayIntent.RevokeInstance>(approved.Intent).RegistryRevision);
        using var thirdClient = RegistrationClient(address, certificate, RegistryThirdCredential);
        using var enrolled = await RegisterAsync(thirdClient, new(third, Guid.NewGuid(), "Third desktop", 2));
        Assert.Equal(HttpStatusCode.OK, enrolled.StatusCode);
        await ui.Replica.LoadInstancesCommand.ExecuteAsync(null);
        Assert.Equal(3, ui.Replica.Instances!.Revision);
        await ui.Replica.RevokeCommand.ExecuteAsync(listenerId);
        Assert.Equal("stale-instance-registry", Assert.IsType<PresentationFailure>(ui.Replica.Failure).Code);
        Assert.False(ui.Replica.Instances!.Instances.Single(item => item.InstanceId == listenerId).Revoked);
        await ui.Shell.CancelDialogCommand.ExecuteAsync(approved.OverlayId);
        await ui.Replica.OpenRevocationCommand.ExecuteAsync(listenerId);
        await ui.Replica.RevokeCommand.ExecuteAsync(listenerId);
        Assert.Equal(4, ui.Replica.Instances!.Revision);
        Assert.True(ui.Replica.Instances.Instances.Single(item => item.InstanceId == listenerId).Revoked);
        Assert.False(ui.Replica.IsInteractionBlocked);
    }

    private string RegistrationPath => DesktopRegistrationRoutes.DirectoryPath(library);

    private LibraryHostLaunchSettings RegistrationSettings(X509Certificate2 certificate, Guid listener, Guid third)
    {
        var database = Path.Combine(root, "instance-registry.db");
        using (LiteDbHostPlaylistStore.Open(database, allowCreate: true)) { }
        return new(new(library, database, Directory.CreateDirectory(Path.Combine(root, "registry-music")).FullName,
                Path.Combine(root, "registry-stage"), Path.Combine(root, "registry-work"), []),
            new("127.0.0.1", 0, certificate.Thumbprint),
            new([new(instance, "operator", CanManageRegistrations: true), new(listener, "listener"), new(third, "third")], [], []));
    }

    private async Task<WebApplication> OpenRegistrationHostAsync(LibraryHostLaunchSettings settings, X509Certificate2 certificate)
    {
        var vault = new FixtureHostVault(new Dictionary<string, string>
        {
            ["operator"] = RegistryOperatorCredential, ["listener"] = RegistryListenerCredential, ["third"] = RegistryThirdCredential
        });
        var app = await LibraryHostLauncher.BuildAsync(settings, certificate, vault, Token);
        try
        {
            await app.StartAsync(Token);
            return app;
        }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private static HttpClient RegistrationClient(Uri address, X509Certificate2 certificate, string credential)
    {
        var client = new HttpClient(CreateClientHandler(certificate)) { BaseAddress = address };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        return client;
    }

    private Task<HttpResponseMessage> RegisterAsync(HttpClient client, RegisterDesktopRequest request) =>
        client.PostAsJsonAsync(RegistrationPath, request, Token);

    private Task<HttpResponseMessage> RevokeAsync(HttpClient client, RevokeDesktopRequest request) =>
        client.PostAsJsonAsync(DesktopRegistrationRoutes.RevokePath(library), request, Token);

    private static string Describe(DesktopRegistrationRow row) => $"{row.Name}:{row.Role}:{(row.Revoked ? "Revoked" : "Active")}";

    private sealed record RegistrationHttpProblem(string Code, long? ActualRevision);

    private sealed class RegistryFixtureSelection(Guid library, Guid instance, string credential) : IDesktopRegistrationCredentials
    {
        public Guid Selection { get; } = Guid.NewGuid();
        public List<Guid> Selections { get; } = [];

        public ValueTask<string?> GetTokenAsync(Guid requestedLibrary, Guid requestedInstance, CancellationToken token) =>
            ValueTask.FromResult<string?>(requestedLibrary == library && requestedInstance == instance ? credential : null);

        public ValueTask<string?> GetSelectedTokenAsync(Guid requestedLibrary, Guid requestedInstance, Guid selection, CancellationToken token)
        {
            Selections.Add(selection);
            return selection == Selection ? GetTokenAsync(requestedLibrary, requestedInstance, token) : ValueTask.FromResult<string?>(null);
        }
    }
}
