using BeetsMan.Host.Adapters.Persistence;
using BeetsMan.Host.Adapters.Syncthing;
using BeetsMan.Host.Api;
using BeetsMan.Host.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace BeetsMan.Host.Bootstrap;

/// <summary>Composes the configured library host and its durable export workers.</summary>
/// <remarks>2026-11-24: Bind authenticated routing and scoped export services for FR-RECORDS-001#ac-register and FR-FILESYNC-001#ac-safety.</remarks>
public static class LibraryHostStartup
{
    /// <summary>Builds the library host using the supplied listener configuration and credential services.</summary>
    public static WebApplication Build(WebApplicationBuilder builder, LibraryHostOptions options, IHostAccessTokenVerifier access,
        IRegisteredSyncthingTokenProvider receivers, IHostSenderTokenProvider senders)
    {
        var configured = Validate(options);
        IExportPlaylistFormatter[] formatters = [new M3u8PlaylistFormatter(), new PlsPlaylistFormatter()];
        var profiles = new AndroidLayoutProfiles(formatters).LoadBuiltIn();
        var services = builder.Services;
        services.AddSingleton(configured);
        services.AddSingleton<IHostAccessTokenVerifier>(new LibraryAccess(access, configured.LibraryId));
        services.AddSingleton(receivers);
        services.AddSingleton(senders);
        services.AddSingleton(_ => OpenStore(configured));
        services.AddSingleton<IDeviceExportPayloadStore>(new FileStagedPlaylistContent(configured.PrivateStageRoot));
        services.AddSingleton<ConfiguredAndroidExportRoutes>();
        services.AddSingleton<ISyncthingExportSenderRegistry>(provider => provider.GetRequiredService<ConfiguredAndroidExportRoutes>());
        services.AddSingleton<IDeviceExportScanStore>(Store);
        services.AddSingleton<IDeviceExportReceiverStore>(Store);
        services.AddSingleton<IDeviceExportTransferStore>(Store);
        services.AddHttpClient<SyncthingReceiverInventory>(client => client.Timeout = TimeSpan.FromSeconds(8)).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        });
        services.AddHttpClient<SyncthingPcMirrorControl>(client => client.Timeout = TimeSpan.FromSeconds(8))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddHttpClient<SyncthingMediaShareOffer>(client => client.Timeout = TimeSpan.FromSeconds(8))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        services.TryAddSingleton<IMediaShareOffer>(provider => provider.GetRequiredService<SyncthingMediaShareOffer>());
        services.AddSingleton<AndroidArrivalHub>();
        services.AddSingleton<RegisteredDeviceExportPresence>();
        services.AddHttpClient<SyncthingAndroidEvents>(client => client.Timeout = TimeSpan.FromSeconds(8))
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        services.AddHostedService<SyncthingAndroidArrivalWorker>();
        services.AddSingleton(provider =>
        {
            var arrivals = provider.GetRequiredService<AndroidArrivalHub>();
            return new HostPcMirrors(Store(provider).CreatePcMirrorStore(configured, profiles, arrivals),
                provider.GetRequiredService<SyncthingPcMirrorControl>(), provider.GetRequiredService<IMediaShareOffer>(),
                provider.GetRequiredService<RegisteredDeviceExportPresence>(), arrivals);
        });
        services.AddSingleton(provider => new HostPlaylistWriter(Store(provider)));
        services.AddSingleton(provider => new HostDeviceExportReader(Store(provider), profiles));
        services.AddSingleton(provider => new RegisteredDevicePlaylistResolver(Store(provider),
            provider.GetRequiredService<SyncthingReceiverInventory>(), receivers, profiles, configured.MusicRoot,
            provider.GetRequiredService<ConfiguredAndroidExportRoutes>()));
        services.AddSingleton(provider => new HostDeviceSyncListEditor(Store(provider), provider.GetRequiredService<RegisteredDevicePlaylistResolver>()));
        services.TryAddSingleton<IDeviceExportStepWriter>(provider => provider.GetRequiredService<ConfiguredAndroidExportRoutes>());
        services.AddSingleton(provider =>
        {
            var store = Store(provider);
            var resolver = provider.GetRequiredService<RegisteredDevicePlaylistResolver>();
            return new HostDevicePlaylistStager(store, resolver, new PlaylistExportPlanner(formatters), provider.GetRequiredService<IDeviceExportPayloadStore>());
        });
        services.AddSingleton(provider =>
        {
            var store = Store(provider);
            var routes = provider.GetRequiredService<ConfiguredAndroidExportRoutes>();
            var payloads = provider.GetRequiredService<IDeviceExportPayloadStore>();
            var presence = provider.GetRequiredService<RegisteredDeviceExportPresence>();
            return new StagedDeviceExportWorkflow(store, store, store, routes,
                new HostDeviceExportPublisher(store, store, provider.GetRequiredService<IDeviceExportStepWriter>()),
                payloads, configured.MusicRoot, new DeviceExportLayoutEditor(presence, payloads, formatters),
                new DeviceExportProfileEditor(store, store, presence, payloads, profiles, formatters));
        });
        services.AddDeviceExportReceiverVerification();

        var app = builder.Build();
        app.MapPlaylistPublication();
        app.MapDevicePlaylistStage();
        app.MapDeviceSyncListEdits();
        app.MapDeviceExportQueries();
        app.MapDeviceExportCommands();
        app.MapDeviceExportTransfers();
        app.MapMediaMirrors();
        app.MapAndroidArrivals();
        return app;
    }

    private static LiteDbHostPlaylistStore Store(IServiceProvider provider) => provider.GetRequiredService<LiteDbHostPlaylistStore>();

    private static LiteDbHostPlaylistStore OpenStore(LibraryHostOptions options)
    {
        foreach (var path in new[] { options.PrivateStageRoot, options.PublicationWorkRoot })
        {
            VerifyExistingAncestor(path);
            RequireOrdinaryDirectory(Directory.CreateDirectory(path).FullName);
        }
        VerifyExistingAncestor(Path.GetDirectoryName(options.DatabasePath)!);
        var database = new FileInfo(options.DatabasePath);
        if (Directory.Exists(options.DatabasePath) || database.LinkTarget is not null || database.Exists &&
            (database.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The configured host database must be a regular file.");
        return LiteDbHostPlaylistStore.Open(options.DatabasePath, options.CreateDatabaseIfMissing);
    }

    private static LibraryHostOptions Validate(LibraryHostOptions options)
    {
        if (options.LibraryId == Guid.Empty)
            throw new ArgumentException("A library ID is required for the host.", nameof(options));
        var database = Normalize(options.DatabasePath);
        var music = Normalize(options.MusicRoot);
        var stage = Normalize(options.PrivateStageRoot);
        var work = Normalize(options.PublicationWorkRoot);
        var seenDevices = new HashSet<Guid>();
        var seenFolders = new HashSet<(string SenderId, string FolderId)>();
        var exports = options.AndroidExports.IsDefault ? [] : options.AndroidExports;
        var routes = exports.ToBuilder();
        var shares = new List<string> { music };
        for (var index = 0; index < routes.Count; index++)
        {
            var route = routes[index];
            if (route is not { Sender: { Endpoint: { } endpoint } sender } || !SyncthingReceiverInventory.IsTrustedEndpoint(endpoint) ||
                string.IsNullOrWhiteSpace(route.FolderId) || string.IsNullOrWhiteSpace(sender.DeviceId) || route.DeviceId == Guid.Empty ||
                !seenDevices.Add(route.DeviceId) || !seenFolders.Add((sender.DeviceId, route.FolderId)) ||
                route.ReceiverEndpoint is { } receiver && (receiver == endpoint || !SyncthingReceiverInventory.IsTrustedEndpoint(receiver)) ||
                route.ReceiverRoot is { } receiverRoot && !PcMirrorNativePath.IsDedicatedRoot(receiverRoot))
                throw new ArgumentException("Each Android device needs a unique configured sender folder.", nameof(options));
            var root = Normalize(sender.ExportRoot);
            if (Path.GetPathRoot(root) == root)
                throw new ArgumentException("An Android export requires a dedicated root.", nameof(options));
            shares.Add(root);
            routes[index] = route with { Sender = sender with { ExportRoot = root } };
        }

        for (var index = 0; index < shares.Count; index++)
        {
            for (var earlier = 0; earlier < index; earlier++)
            {
                if (Overlap(shares[index], shares[earlier]))
                    throw new ArgumentException("Host music and Android export shares must be disjoint.", nameof(options));
            }
        }
        if (new[] { database, stage, work }.Any(path => shares.Any(share => Overlap(path, share))))
            throw new ArgumentException("Host database and private paths must be disjoint from media shares.", nameof(options));
        var mirrors = options.PcMirrors.IsDefault ? [] : options.PcMirrors;
        var receivers = new HashSet<(Uri, string)>();
        foreach (var mirror in mirrors)
        {
            if (mirror is not { ReceiverEndpoint: { } recipient, SenderEndpoint: { } source } || mirror.PeerId == Guid.Empty ||
                !seenDevices.Add(mirror.PeerId) || string.IsNullOrWhiteSpace(mirror.FolderId) || mirror.FolderId is "." or ".." ||
                string.IsNullOrWhiteSpace(mirror.SenderDeviceId) || source == recipient ||
                !SyncthingReceiverInventory.IsTrustedEndpoint(source) || !SyncthingReceiverInventory.IsTrustedEndpoint(recipient) ||
                !PcMirrorNativePath.IsDedicatedRoot(mirror.ReceiverRoot) || !receivers.Add((recipient, mirror.FolderId)) ||
                seenFolders.Contains((mirror.SenderDeviceId, mirror.FolderId)))
                throw new ArgumentException("Each PC mirror needs a distinct registered receiver and a full-library sender folder.", nameof(options));
        }
        RequireOrdinaryDirectory(music);
        foreach (var route in routes)
            RequireOrdinaryDirectory(route.Sender.ExportRoot);
        return options with { DatabasePath = database, MusicRoot = music, PrivateStageRoot = stage, PublicationWorkRoot = work,
            AndroidExports = routes.ToImmutable(), PcMirrors = mirrors };
    }

    private static string Normalize(string path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path)
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new ArgumentException("Host paths must be absolute.");

    private static bool Within(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool Overlap(string a, string b) => Within(a, b) || Within(b, a);

    private static void VerifyExistingAncestor(string path)
    {
        var parent = new DirectoryInfo(path);
        while (!parent.Exists && parent.Parent is { } next)
        {
            if (File.Exists(parent.FullName) || parent.LinkTarget is not null)
                throw new IOException("A configured private host path is obstructed.");
            parent = next;
        }
        RequireOrdinaryDirectory(parent.FullName);
    }

    private static void RequireOrdinaryDirectory(string path)
    {
        if (!FileDeviceExportSnapshotReader.IsOrdinaryDirectory(new DirectoryInfo(path)))
            throw new IOException("A configured host directory or its ancestor is missing or linked.");
    }

    private sealed class LibraryAccess(IHostAccessTokenVerifier access, Guid libraryId) : IHostAccessTokenVerifier
    {
        public async ValueTask<AuthenticatedHostInstance?> VerifyAsync(string bearerToken, CancellationToken cancellationToken)
        {
            var caller = await access.VerifyAsync(bearerToken, cancellationToken).ConfigureAwait(false);
            return caller?.LibraryId == libraryId ? caller : null;
        }
    }
}
