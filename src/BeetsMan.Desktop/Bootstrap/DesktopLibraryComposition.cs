using BeetsMan.Desktop.Adapters.Host;
using BeetsMan.Desktop.Adapters.Persistence;
using BeetsMan.Desktop.Application.Configuration;
using BeetsMan.Desktop.Application.Dispatch;
using BeetsMan.Desktop.Application.HostLibrary;
using BeetsMan.Desktop.Application.Library;
using BeetsMan.Desktop.Application.Playback;
using BeetsMan.Desktop.Application.Replication;
using BeetsMan.Host.Contracts.V1;

namespace BeetsMan.Desktop.Bootstrap;

/// <summary>Supplies the already registered library endpoint and the caller-owned authenticated dependencies.</summary>
/// <remarks>2026-11-25: Bind FR-RECORDS-001#ac-replicate and FR-LIBRARY-002#ac-batch-commit to the configured host. The private import stage belongs outside media shares.</remarks>
public sealed record DesktopHostBinding(Uri Endpoint, HttpClient Client, IRegisteredHostTokenProvider Credentials,
    IImportSourcePicker? ImportPicker = null, ILibraryBatchImportFiles? PrivateImports = null);

public sealed partial class DesktopStartup
{
    /// <summary>Registers the local library state and binds every implemented library mutation to one batch owner.</summary>
    /// <remarks>2026-11-25: Compose FR-LIBRARY-002#ac-batch-local, ac-batch-review, ac-batch-commit and ac-batch-revert, FR-PLAYER-001#ac-queue-autosave and FR-RECORDS-001#ac-replicate.</remarks>
    private (QueuePlaylistPublication Queue, PlaybackPreferenceHandlers Preferences, PlaylistWorkspaceHandlers Playlists) RegisterLibrary(RequestDispatcher.Builder routes,
        LiteDbDesktopPlaylistStore store, LiteDbCatalogReader catalog, LibraryBatchScope scope)
    {
        var offline = new UnconfiguredLibraryHost();
        IPlaylistPublicationHost playlistHost = offline;
        IApprovedLibraryBatchPublisher batchHost = offline;
        ILibraryRecordHost recordHost = offline;
        ICatalogReplicaHost replicaHost = offline;
        if (host is { } connected)
        {
            playlistHost = new HttpPlaylistPublicationHost(connected.Client, connected.Endpoint, library, connected.Credentials);
            batchHost = new HttpApprovedLibraryBatchPublisher(connected.Client, connected.Endpoint, library, connected.Credentials, connected.PrivateImports);
            recordHost = new HttpLibraryRecordHost(connected.Client, connected.Endpoint, library, origin, connected.Credentials);
            replicaHost = new HttpCatalogReplicaHost(connected.Client, connected.Endpoint, library, origin, connected.Credentials);
        }

        var batches = new LibraryBatchHandlers(scope, store, batchHost, host?.PrivateImports);
        batches.Register(routes);
        var playlists = new PlaylistWorkspaceHandlers(store, store, catalog, playlistHost, origin, batches);
        playlists.Register(routes);
        var listener = new LibraryRecordHandlers(new(library, origin), store, recordHost);
        new CatalogReplicaHandlers(library, origin, replicaHost, store, listener).Register(routes);
        // 2026-11-25: Register scoped enrollment and revocation for FR-RECORDS-001#ac-register.
        new DesktopRegistrationHandlers(library, origin, host is { } binding
            ? new HttpDesktopRegistrationHost(binding.Client, binding.Endpoint, library, origin, binding.Credentials) : null).Register(routes);
        // 2026-11-25: Bind native library setup and provisioned settings for FR-BEETS-001#ac-open.
        new HostLibrarySetupHandlers(library, origin, host is { } setup
            ? new HttpBeetsLibrarySetup(setup.Client, setup.Endpoint, library, origin, setup.Credentials) : null).Register(routes);
        if (host is { } registered)
        {
            var intake = new HttpImportIntakeHost(registered.Client, registered.Endpoint, library, registered.Credentials);
            new ImportIntakeHandlers(library, registered.ImportPicker, intake, batches, catalog).Register(routes);
            var maintenance = new HttpLibraryMaintenanceHost(registered.Client, registered.Endpoint, library, registered.Credentials);
            new LibraryMaintenanceHandlers(scope, maintenance, batches).Register(routes);
        }

        var preferences = new PlaybackPreferenceHandlers(library, store, host is null ? null : recordHost);
        preferences.Register(routes);
        return (new QueuePlaylistPublication(catalog, playlistHost, store, origin, batches), preferences, playlists);
    }

    /// <summary>Allows local reads and staging before an authenticated host transport is configured.</summary>
    private sealed class UnconfiguredLibraryHost : IPlaylistPublicationHost, IApprovedLibraryBatchPublisher, IApprovedLibraryBatchWithdrawal,
        ILibraryRecordHost, ICatalogReplicaHost
    {
        public Task<Outcome<PlaylistSavedReceipt>> SaveAsync(PendingPlaylistSave operation, RequestContext request, CancellationToken token) =>
            Unavailable<PlaylistSavedReceipt>();

        public Task<Outcome<LibraryBatchCommitReceipt>> PublishAsync(LibraryBatchScope scope, LibraryBatchReview approved, Guid operationId,
            CancellationToken token) => Unavailable<LibraryBatchCommitReceipt>();

        public Task<Outcome<LibraryBatchWithdrawalReceipt>> WithdrawAsync(LibraryBatchScope scope, LibraryBatchApproval approval, Guid operationId,
            CancellationToken token) => Task.FromResult<Outcome<LibraryBatchWithdrawalReceipt>>(
                new Outcome<LibraryBatchWithdrawalReceipt>.DependencyFailure("library-batch-withdraw-host-unavailable", true));

        public Task<Outcome<LibraryRecordReceipt>> PublishAsync(LibraryRecordIntent change, Guid operation, CancellationToken token) =>
            Unavailable<LibraryRecordReceipt>();

        public Task<Outcome<CatalogFeedPage>> ReadChangesAsync(long after, long? upper, CancellationToken token) => Unavailable<CatalogFeedPage>();

        public Task<Outcome<CatalogAcknowledgementReceipt>> AcknowledgeAsync(long through, CancellationToken token) => Unavailable<CatalogAcknowledgementReceipt>();

        public Task<Outcome<CatalogSnapshotPage>> BeginSnapshotAsync(Guid snapshotId, long expectedCursor, CancellationToken token) =>
            Unavailable<CatalogSnapshotPage>();

        public Task<Outcome<CatalogSnapshotPage>> ReadSnapshotAsync(Guid snapshotId, int offset, CancellationToken token) => Unavailable<CatalogSnapshotPage>();

        private static Task<Outcome<T>> Unavailable<T>() => Task.FromResult<Outcome<T>>(new Outcome<T>.DependencyFailure("library-host-unconfigured", true));
    }
}
