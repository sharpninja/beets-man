using System.Collections.Immutable;
using BeetsMan.Host.Adapters.Beets;
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
/// <remarks>2026-11-24: Compose the guided album review and approval boundary for FR-BEETS-001#ac-import.</remarks>
/// <remarks>2026-11-24: Compose the import publication transaction for FR-BEETS-001#ac-recover.</remarks>
/// <remarks>2026-11-24: Activate native import execution when beets is configured for FR-BEETS-001#ac-import and ac-recover.</remarks>
/// <remarks>2026-11-24: Route registered track tag reads and edit admission for FR-BEETS-001#ac-edit and ac-recover.</remarks>
/// <remarks>2026-11-24: Compose the shared native import and track tag execution lane for FR-BEETS-001#ac-edit.</remarks>
/// <remarks>2026-11-24: Compose the native read-only maintenance preview and confirmation for FR-BEETS-001#ac-edit.</remarks>
/// <remarks>2026-11-24: Activate catalog-only removal on the shared native lane for FR-BEETS-001#ac-edit and ac-recover.</remarks>
/// <remarks>2026-11-24: Compose revision reads and per-recipient acknowledgements for FR-RECORDS-001#ac-replicate and ac-notify.</remarks>
/// <remarks>2026-11-24: Compose batch approval and publication for FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-24: Compose private transfer for approved imports under FR-LIBRARY-002#ac-batch-commit.</remarks>
/// <remarks>2026-11-25: Bind the versioned desktop roster for FR-RECORDS-001#ac-register.</remarks>
/// <remarks>2026-11-25: Provision distinct native paths and bind the beets setup journal for FR-BEETS-001#ac-open.</remarks>
/// <remarks>2026-11-25: Bind the host activity clock and roster observer for FR-RECORDS-001#ac-register.</remarks>
public static class LibraryHostStartup
{
    /// <summary>Builds the library host using the supplied listener configuration and credential services.</summary>
    public static WebApplication Build(WebApplicationBuilder builder, LibraryHostOptions options, IHostAccessTokenVerifier access,
        IRegisteredSyncthingTokenProvider receivers, IHostSenderTokenProvider senders)
    {
        var configured = Validate(options);
        IExportPlaylistFormatter[] formatters = [new M3u8PlaylistFormatter(), new PlsPlaylistFormatter()];
        var profiles = LoadAndroidLayouts(configured, new AndroidLayoutProfiles(formatters));
        var services = builder.Services;
        services.AddSingleton(configured);
        services.AddSingleton<IHostAccessTokenVerifier>(new LibraryAccess(access, configured.LibraryId));
        services.AddSingleton(receivers);
        services.AddSingleton(senders);
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(provider => OpenStore(configured, provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHostDesktopActivity>(Store);
        services.AddSingleton(new FileImportStage(configured.PrivateStageRoot));
        services.AddSingleton(provider => new HostImportIntake(Store(provider), provider.GetRequiredService<FileImportStage>(), Store(provider)));
        services.AddSingleton(provider => new HostImportReview(Store(provider)));
        services.AddSingleton(provider => new HostTrackTags(Store(provider)));
        services.AddSingleton(provider => new HostLibraryMaintenance(Store(provider), provider.GetService<IBeetsMaintenancePlanner>()));
        services.AddSingleton(provider => new ImportCatalogPublication(Store(provider)));
        services.AddSingleton<IHostCatalogReplication>(Store);
        services.AddSingleton(provider => new HostLibraryRecords(Store(provider)));
        services.AddSingleton(provider => new HostDesktopRegistration(Store(provider)));
        services.AddSingleton(provider => new HostBeetsLibrarySetup(Store(provider), configured.Beets is { } native
            ? new BeetsLibrarySetupFiles(native, configured.MusicRoot, configured.PublicationWorkRoot, provider.GetRequiredService<BeetsGuidedImport>())
            : null));
        services.AddSingleton(provider => new HostApprovedLibraryBatches(Store(provider)));
        services.AddSingleton<Func<Guid, IImportFileStage>>(_ => id => new FileImportStage(configured.PrivateStageRoot, id));
        services.AddSingleton(provider => new HostApprovedBatchImports(Store(provider), provider.GetRequiredService<Func<Guid, IImportFileStage>>()));
        services.AddSingleton(provider => new HostApprovedBatchImportReviews(Store(provider)));
        if (configured.Beets is { } beets)
        {
            if (beets.Matching is { } matching)
            {
                services.AddHttpClient("guided-import-musicbrainz", client => client.Timeout = TimeSpan.FromSeconds(15))
                    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                    .AddTypedClient(client => new MusicBrainzReleaseSearch(client, matching.Endpoint!, TimeProvider.System));
            }
            services.AddSingleton(provider => new BeetsGuidedImport(beets, configured.MusicRoot,
                configured.PublicationWorkRoot, provider.GetRequiredService<FileImportStage>(), provider.GetService<MusicBrainzReleaseSearch>(),
                token => ((IHostBeetsSetupStore)Store(provider)).ReadBeetsNativeConfigurationAsync(configured.LibraryId, token)));
            services.TryAddSingleton<IBeetsGuidedImport>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.TryAddSingleton<Func<Guid, IBeetsGuidedImport>>(provider => approval => new BeetsGuidedImport(beets, configured.MusicRoot,
                configured.PublicationWorkRoot, new FileImportStage(configured.PrivateStageRoot, approval), provider.GetService<MusicBrainzReleaseSearch>(),
                token => ((IHostBeetsSetupStore)Store(provider)).ReadBeetsNativeConfigurationAsync(configured.LibraryId, token)));
            services.AddSingleton(provider => new ApprovedBatchImportReviewWorker(configured.LibraryId, Store(provider),
                provider.GetRequiredService<Func<Guid, IBeetsGuidedImport>>()));
            services.TryAddSingleton<IBeetsTrackTags>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.TryAddSingleton<IBeetsMaintenancePlanner>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.TryAddSingleton<IBeetsCatalogRemoval>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.TryAddSingleton<IAudioRemovalFiles>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.TryAddSingleton<IBeetsOrganization>(provider => provider.GetRequiredService<BeetsGuidedImport>());
            services.AddSingleton(provider => new CatalogRemovalWorker(configured.LibraryId, Store(provider), provider.GetRequiredService<IBeetsCatalogRemoval>(),
                provider.GetRequiredService<IAudioRemovalFiles>()));
            services.AddSingleton(provider => new OrganizationWorker(configured.LibraryId, Store(provider), provider.GetRequiredService<IBeetsOrganization>()));
            services.AddSingleton(provider => new TrackTagWorker(configured.LibraryId, Store(provider), provider.GetRequiredService<IBeetsTrackTags>()));
            services.AddSingleton(provider => new NativeBatchWorker(configured.LibraryId, Store(provider), provider.GetRequiredService<IBeetsTrackTags>(),
                provider.GetRequiredService<IBeetsCatalogRemoval>(), provider.GetRequiredService<IBeetsOrganization>(), provider.GetRequiredService<IAudioRemovalFiles>(),
                provider.GetRequiredService<Func<Guid, IBeetsGuidedImport>>(), provider.GetRequiredService<Func<Guid, IImportFileStage>>()));
            services.AddSingleton(provider => new GuidedImportWorker(configured.LibraryId, Store(provider), provider.GetRequiredService<HostImportReview>(),
                provider.GetRequiredService<ImportCatalogPublication>(), provider.GetRequiredService<IBeetsGuidedImport>()));
            services.AddHostedService<GuidedImportHostedService>();
        }
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
        app.MapImportIntake();
        app.MapTrackTags();
        app.MapLibraryMaintenance();
        app.MapCatalogReplication();
        app.MapDesktopRegistration();
        app.MapBeetsLibrarySetup();
        app.MapApprovedLibraryBatches();
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

    /// <summary>Reads bounded local YAML and combines custom profiles with the bundled layouts.</summary>
    /// <remarks>2026-11-25: Share validated app profiles with the directory, staging and publication for FR-FILESYNC-001#ac-layouts and ac-playlist-drop.</remarks>
    private static ImmutableArray<AndroidLayoutProfile> LoadAndroidLayouts(LibraryHostOptions options, AndroidLayoutProfiles loader)
    {
        var profiles = loader.LoadBuiltIn().ToBuilder();
        if (options.AndroidLayoutFiles.IsDefaultOrEmpty)
            return profiles.ToImmutable();
        var identities = profiles.Select(profile => profile.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var buffer = new char[AndroidLayoutProfiles.MaximumYamlLength + 1];
        foreach (var input in options.AndroidLayoutFiles)
        {
            var path = Normalize(input);
            if (Within(path, options.MusicRoot) || options.AndroidExports.Any(route => Within(path, route.Sender.ExportRoot)))
                throw new ArgumentException("Android layout files must be outside the synchronized media trees.", nameof(options));
            RequireOrdinaryDirectory(Path.GetDirectoryName(path)!);
            var file = new FileInfo(path);
            if (!file.Exists || file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException($"Android layout file '{path}' must be an ordinary local file.", nameof(options));
            using var reader = file.OpenText();
            var length = reader.ReadBlock(buffer.AsSpan());
            var loaded = loader.Parse(new string(buffer, 0, length));
            if (loaded is not HostOutcome<AndroidLayoutProfile>.Success { Value: var profile })
            {
                var issue = (HostOutcome<AndroidLayoutProfile>.Rejected)loaded;
                throw new ArgumentException($"Android layout file '{path}' is invalid ({issue.Code}).", nameof(options));
            }
            if (!identities.Add(profile.Id))
                throw new ArgumentException($"Android layout ID '{profile.Id}' is configured more than once.", nameof(options));
            profiles.Add(profile);
        }
        return profiles.ToImmutable();
    }

    private static LiteDbHostPlaylistStore OpenStore(LibraryHostOptions options, TimeProvider activityClock)
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
        return LiteDbHostPlaylistStore.Open(options.DatabasePath, options.CreateDatabaseIfMissing, activityClock);
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
        var beets = options.Beets;
        if (beets is not null)
        {
            var catalog = Normalize(beets.CatalogPath);
            var config = Normalize(beets.ConfigurationPath ?? Path.Combine(work, "beets-library", "config.yaml"));
            if (Overlap(catalog, stage) || Overlap(catalog, database) || shares.Any(share => Overlap(catalog, share)) ||
                Directory.Exists(catalog) || new FileInfo(catalog).LinkTarget is not null)
                throw new ArgumentException("The beets catalog must be a distinct host-local file outside every media share and import staging directory.", nameof(options));
            if (Overlap(config, catalog) || Overlap(config, stage) || Overlap(config, database) ||
                shares.Any(share => Overlap(config, share)) || Directory.Exists(config) || new FileInfo(config).LinkTarget is not null)
                throw new ArgumentException("The beets configuration must be a distinct host-local file outside media and staging paths.", nameof(options));
            if (beets.AlbumPathTemplate is { } template && !BeetsConfigurationValues.IsNamingTemplate(template))
                throw new ArgumentException("The beets album naming template must describe a relative library path.", nameof(options));
            var pluginIds = beets.AllowedPluginIds.IsDefault ? [] : beets.AllowedPluginIds;
            if (pluginIds.Any(id => !BeetsConfigurationValues.IsPluginId(id)) || pluginIds.Distinct(StringComparer.Ordinal).Count() != pluginIds.Length)
                throw new ArgumentException("Approved native beets plugins need unique module IDs.", nameof(options));
            VerifyExistingAncestor(Path.GetDirectoryName(catalog)!);
            VerifyExistingAncestor(Path.GetDirectoryName(config)!);
            beets = beets with { CatalogPath = catalog, ConfigurationPath = config, ExecutablePath = Normalize(beets.ExecutablePath),
                AllowedPluginIds = [.. pluginIds.Order(StringComparer.Ordinal)] };
            if (beets.Matching is { } matching)
            {
                var endpoint = matching.Endpoint ?? new Uri("https://musicbrainz.org");
                if (!endpoint.IsAbsoluteUri || (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
                    !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                    throw new ArgumentException("The MusicBrainz endpoint must be an HTTPS origin or a loopback HTTP origin.", nameof(options));
                beets = beets with { Matching = matching with { Endpoint = endpoint, FfmpegExecutablePath = Normalize(matching.FfmpegExecutablePath) } };
            }
        }
        return options with { DatabasePath = database, MusicRoot = music, PrivateStageRoot = stage, PublicationWorkRoot = work,
            AndroidExports = routes.ToImmutable(), PcMirrors = mirrors, Beets = beets };
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
