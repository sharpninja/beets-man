# CQRS application interfaces

These interfaces define the application boundary for `FR-DISPATCH-001` and `TR-DISPATCH-ARCH-001`. The [C4 workspace](workspace.dsl) shows the owners and dependencies. The [ViewModel contracts](presentation.md) and [UI command catalog](ui-commands.md) specify how those interfaces bind to desktop intents.

## Requests, handlers, and results

```csharp
public interface ICommand<TResult> { }
public interface IQuery<TResult> { }

public readonly record struct RequestContext(Guid CorrelationId, Guid? OriginInstanceId);
public readonly record struct CommandContext(RequestContext Request, Guid OperationId);

public interface IRequestDispatcher
{
    Task<Outcome<TResult>> SendAsync<TResult>(
        ICommand<TResult> command, CommandContext context, CancellationToken cancellationToken);

    Task<Outcome<TResult>> QueryAsync<TResult>(
        IQuery<TResult> query, RequestContext context, CancellationToken cancellationToken);
}

public interface ICommandHandler<in TCommand, TResult>
    where TCommand : ICommand<TResult>
{
    Task<Outcome<TResult>> HandleAsync(
        TCommand command, CommandContext context, CancellationToken cancellationToken);
}

public interface IQueryHandler<in TQuery, TResult>
    where TQuery : IQuery<TResult>
{
    Task<Outcome<TResult>> HandleAsync(
        TQuery query, RequestContext context, CancellationToken cancellationToken);
}

public sealed record ValidationIssue(string Code, string? Field);

public abstract record Outcome<TResult>
{
    public sealed record Success(TResult Value) : Outcome<TResult>;
    public sealed record Invalid(IReadOnlyList<ValidationIssue> Issues) : Outcome<TResult>;
    public sealed record Conflict(string Code, long? ActualRevision) : Outcome<TResult>;
    public sealed record Cancelled : Outcome<TResult>;
    public sealed record DependencyFailure(string Code, bool Retryable) : Outcome<TResult>;
}
```

- Register exactly one handler per concrete request type and result type. Validate registration at composition time. Configuration and diagnostics can run with the bootstrap registrations before opening the normal stores and services.
- The entry point creates a new correlation ID for each dispatch. Retried commands carry the original operation ID for the same intent. The host binds origin identity to the authenticated instance. Commands replacing durable state carry the relevant expected revision in the command payload.
- Return typed validation, conflict, cancellation, and dependency outcomes. Validation codes remain stable for their callers. A durable commit or accepted job retains its receipt even if the caller cancels after that point. Check the recorded operation or job identity before retrying an uncertain external effect.
- Queries read immutable snapshots within the relevant owner scope. Versioned query results include that owner's revision when needed. The presentation owner and request stamp govern projection into the current ViewModel. Route application-driven presentation updates through the Avalonia scheduler.

## Ownership, mutation, and recovery

Handlers use the typed [LiteDB repository ports and write unit of work](persistence.md). Repositories for one command share the same transaction. CQRS outcomes reach a caller after that transaction commits or rolls back.

- **Desktop bootstrap.** The configuration handler validates the proposed effective paths and restart-bound configuration. It preserves known and unknown JSON keys and opens local state only after path validation and any required creation approval. Repair is available before playback, plugins, and the normal Library start.
- **Desktop state.** The playlist workspace owns draft writes by draft identity. Playback owns active queue writes by session. Settings own preference overrides. The adapter schedules writes per database file and commits changes with their replay record when applicable. Handlers return snapshots or revisions after commit.
- **Host state.** Host commands validate authorization and expected revision against the host's state. For external library commands, persist accepted intent and stable job identity before calling beets. Reconcile actual catalog and file effects, then commit the new revision and delivery records together. The submission's success value is a job receipt. Query job status independently of the submitting request.
- **File publication.** Device export mutations scope work to the selected device, share, and profile version. Compare intended paths against destination availability and the export manifest before publication. After publication, `GetMediaTransfer` reads the verified receiver checkpoint. `ResumeMediaTransfer` makes eligible work due again at the expected revision and can replay its original accepted response. Device export owns staged files and any replay or reconciliation records.
- **Media pairing.** `PairMediaPeer` submits the registered peer, share kind, approved destination and optional Android layout at the expected media directory revision. The host resolves native identities and endpoints from its registration and local configuration, persists the operation fence, and offers the selected sender folder. The response returns the registered peer and share snapshot, and retries with the same operation and intent recover that original result. The receiving peer accepts the share through Syncthing. `GetMediaSyncState` checks that the accepted Android folder still matches the approved scope and advances the directory revision when its observed condition changes.

## Initial request families

The names and fields below specify application intents and results. The feature adapters can exercise these boundaries with headless callers.

| Owner | Queries and result | Commands and result |
| --- | --- | --- |
| Configuration | `GetBootstrapState → BootstrapState`, `GetPreference → EffectivePreference` with its source and revision | `InitializeDesktop` and `SaveBootstrapSettings` take settings and creation approval when required. `SetLocalOverride` and `ClearLocalOverride` return the recomputed preference. |
| Catalog | `BrowseCatalog` takes category, filter, sort and page. `GetTrackAvailability` takes stable track IDs. Results carry catalog revision, stable IDs, metadata, and availability. | Catalog housekeeping and host mutations use their respective writers. |
| Playlist workspace | `GetPlaylistDraft` takes draft ID and returns ordered track IDs and revision. | `CreatePlaylistDraft`, `EditPlaylistDraft`, and `SavePlaylist` return the resulting draft or saved playlist receipt. Edits specify ordered IDs and expected revision. Creation from a queue takes its snapshot. |
| Playback | `GetPlaybackState` takes playback session ID and returns queue, active track, deck state, and capabilities. | `EnqueueTracks`, `Transport`, `Seek`, and `ChangePlaybackStrategy` take the session ID and return current playback state. Playback keeps its session identity through presentation changes. |
| Extensions | `GetPluginState` returns local trust, active worker status, and validated contribution metadata. | `SetPluginTrust` and `SetPluginEnabled` take plugin ID and version, returning the updated local state. A contributed action enters the same dispatcher as an application action. |
| Host library | `GetLibraryJob` takes a job ID and returns the current status, review choices, recovery state, or publication revision. | `StartImport` takes a completed upload ID. `ChooseImportAction` takes the job ID, choice and expected revision. Both return a job receipt. `EditTrackTags` targets a stable track ID and expected catalog revision. |
| PC mirror | `GetMediaSyncState` returns the registered directory, including confirmed PC mirror connections and file progress. The stored observation keeps the last verified count available during pause and host restart. | `SetMirrorPaused` carries the selected PC share and its expected control revision. `RescanMediaShare` rescans that mirror's receiving folder. Both return `MediaSyncState` and preserve the original response for the same authenticated operation. |
| Device export | `GetDeviceLayouts` returns the registered device's player profiles, revisions, path templates, playlist formats and suggested roots. `GetDeviceExportPlan` takes the device ID, the selected profile ID and revision, and an optional plan ID. It returns the saved ordered playlist entries and staged audio for that scope, or an empty plan when the selected profile has no staged entries. | `StageTracksForDevice` and `StagePlaylistForDevice` take the device ID, profile ID and revision, and optional existing plan ID and revision. Direct tracks retain their intent even when a playlist also needs the same audio. `EditDeviceExportPlan` removes selected entry IDs at the expected plan revision and retains audio still needed by another playlist. Staged edits advance the revision and invalidate prior review. `PrepareDeviceExportPreview` returns the reviewed plan revision, ordered file changes, blocking issues and proposed removal paths. `PublishDeviceExport` takes a plan ID, expected plan revision and optional scoped approval, and returns `Published(ExportPublicationReceipt)` or `ConfirmationRequired(OwnedRemovalProposal)`. The HTTPS handlers preserve the originating instance and CQRS operation ID across retries. |

The [UI command catalog](ui-commands.md) specifies how MVVM Toolkit commands dispatch these application requests and project the corresponding typed results. Shell handlers own process-local navigation state. Host and persistence boundaries retain the ownership defined above.

## Verification seams

`DISPATCHAcceptanceTests.Routing_MatchesContract` exercises unique registration, typed validation and cancellation. `DISPATCHAcceptanceTests.Responsive_MatchesContract` holds a host job open while sending independent requests and tests that mutations publish consistently. `DISPATCHAcceptanceTests.Update_MatchesContract` exercises result scope and out-of-order completion when the presentation contracts bind to this dispatcher. Use a recording handler, an immutable query fake, a controllable job adapter and a queued scheduler. Each case rejects a deliberate incorrect response in the mock harness before binding to production. [Test specifications](../testing/ui/index.md#test-dispatch-001--execute-a-desktop-action)
