using System.Net;
using System.Net.Http.Json;
using BeetsMan.Desktop.Adapters.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.PresentationState;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Desktop.Bootstrap;
using BeetsMan.Host.Application;
using BeetsMan.Host.Bootstrap;
using BeetsMan.Host.Contracts.V1;
using LiteDB;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using static BeetsMan.Host.Tests.DesktopActivityAssertions;

namespace BeetsMan.Host.IntegrationTests;

public sealed partial class PlaylistHttpsIntegrationTests
{
    /// <summary>Verifies Last seen through real catalog polling, Toolkit commands and a host restart.</summary>
    /// <remarks>2026-11-25: Bind the SCR42 activity and approval cases to real HTTPS and LiteDB for TEST-RECORDS-002.</remarks>
    /// <remarks>2026-11-26: Verify current registered desktop activity and preserve the enrollment revision for FR-RECORDS-001#ac-register.</remarks>
    [Fact]
    public async Task RegisteredActivitySurvivesRestartWithoutInvalidatingReviewedRevocation()
    {
        var listener = Guid.NewGuid();
        var legacy = Guid.NewGuid();
        var clock = new ActivityClock(EnrolledAt);
        var database = Path.Combine(root, "activity-host.db");
        var music = Directory.CreateDirectory(Path.Combine(root, "activity-music")).FullName;
        SeedHost(database);
        using (var store = new LiteDatabase(database))
            store.GetCollection<BsonDocument>("registered_instances").Insert(new BsonDocument
            {
                ["_id"] = legacy, ["LibraryId"] = library, ["Role"] = "Desktop", ["Status"] = "Active", ["Name"] = "Legacy"
            });
        using var certificate = CreateCertificate();
        var verifier = new ReplicaFixtureVerifier(new Dictionary<string, AuthenticatedHostInstance>
        {
            [secret] = new(library, instance, CanManageRegistrations: true),
            [RegistryListenerCredential] = new(library, listener)
        });
        var credentials = new MirrorNativeCredentials(library, Guid.NewGuid(), Guid.NewGuid());
        var options = new LibraryHostOptions(library, database, music, Path.Combine(root, "activity-stage"), Path.Combine(root, "activity-work"), []);
        DateTimeOffset? legacySeen, enrollment, coalesced, activity, regressedClock, rejectedScope, afterRevocation;
        long approvalRevision, revisionAfterActivity, revokedRevision;

        await using (var host = await StartHostAsync())
        {
            var address = new Uri(host.Urls.Single());
            using var operatorClient = new HttpClient(CreateClientHandler(certificate));
            using var guest = RegistrationClient(address, certificate, RegistryListenerCredential);
            await using (var desktop = new DesktopStartup(JsonBootstrapFile.ResolveDefaults(Path.Combine(root, "activity-desktop"), music),
                [], library, instance, host: new(address, operatorClient, new TokenProvider(library, instance, secret)),
                presentationScheduler: new InlineScheduler()))
            {
                var active = Assert.IsType<Outcome<DesktopStartupState>.Success>(await desktop.StartAsync(Token)).Value;
                var presentation = Assert.IsType<DesktopPresentation>(active.Presentation);
                var roster = presentation.Replica;
                await roster.LoadInstancesCommand.ExecuteAsync(null);
                legacySeen = Assert.IsType<RegisteredInstancesSnapshot>(roster.Instances).Instances.Single(row => row.InstanceId == legacy).LastSeenUtc;
                using var enrolled = await RegisterAsync(guest, new(listener, Guid.NewGuid(), "Active listener", 0));
                var receipt = Assert.IsType<RegisterDesktopResponse>(await enrolled.Content.ReadFromJsonAsync<RegisterDesktopResponse>(Token));
                enrollment = receipt.Instance.LastSeenUtc;
                await roster.LoadInstancesCommand.ExecuteAsync(null);
                Assert.Equal(enrollment, Current().LastSeenUtc);
                await roster.OpenRevocationCommand.ExecuteAsync(listener);
                var approved = Assert.Single(Assert.IsType<ShellState>(presentation.Shell.State).Overlays);
                approvalRevision = Assert.IsType<OverlayIntent.RevokeInstance>(approved.Intent).RegistryRevision;

                clock.Set(EnrolledAt.AddSeconds(59));
                await PollAsync(library, HttpStatusCode.OK);
                coalesced = await ReadSeenAsync();
                clock.Set(ObservedAt);
                await PollAsync(library, HttpStatusCode.OK);
                activity = await ReadSeenAsync();
                revisionAfterActivity = roster.Instances!.Revision;

                clock.Set(EnrolledAt.AddSeconds(5));
                await PollAsync(library, HttpStatusCode.OK);
                regressedClock = await ReadSeenAsync();
                clock.Set(ObservedAt.AddMinutes(3));
                await PollAsync(Guid.NewGuid(), HttpStatusCode.Forbidden);
                rejectedScope = await ReadSeenAsync();

                await roster.RevokeCommand.ExecuteAsync(listener);
                revokedRevision = roster.Instances!.Revision;
                Assert.True(Current().Revoked);
                await PollAsync(library, HttpStatusCode.Forbidden);
                afterRevocation = await ReadSeenAsync();

                RegisteredInstance Current() => Assert.IsType<RegisteredInstancesSnapshot>(roster.Instances).Instances.Single(row => row.InstanceId == listener);
                async Task<DateTimeOffset?> ReadSeenAsync()
                {
                    await roster.LoadInstancesCommand.ExecuteAsync(null);
                    return Current().LastSeenUtc;
                }
            }
            await host.StopAsync(Token);

            async Task PollAsync(Guid scope, HttpStatusCode expected)
            {
                using var result = await guest.GetAsync(CatalogReplicationRoutes.ChangesPath(scope, 0, null, 10), Token);
                Assert.Equal(expected, result.StatusCode);
            }
        }

        await using var restarted = await StartHostAsync();
        using var admin = RegistrationClient(new(restarted.Urls.Single()), certificate, secret);
        var persisted = Assert.IsType<DesktopRegistrationDirectory>(await admin.GetFromJsonAsync<DesktopRegistrationDirectory>(RegistrationPath, Token));
        var target = persisted.Instances.Single(item => item.InstanceId == listener);
        Assert.True(target.Revoked);
        Verify(new(legacySeen, enrollment, coalesced, activity, regressedClock, rejectedScope, approvalRevision,
            revisionAfterActivity, revokedRevision, afterRevocation, target.LastSeenUtc));
        await restarted.StopAsync(Token);

        async Task<WebApplication> StartHostAsync()
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
            builder.Logging.ClearProviders();
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.WebHost.ConfigureKestrel(server => server.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
            var app = LibraryHostStartup.Build(builder, options, verifier, credentials, credentials);
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
    }

    private sealed class ActivityClock(DateTimeOffset start) : TimeProvider
    {
        private long ticks = start.UtcTicks;

        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);

        internal void Set(DateTimeOffset time) => Interlocked.Exchange(ref ticks, time.UtcTicks);
    }
}
