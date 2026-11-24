# ViewModels and UI command contracts

The [C4 workspace](workspace.dsl) identifies the desktop presentation, projection, scheduler, and CQRS components. ViewModels and [UI commands](ui-commands.md) live under `BeetsMan.Desktop/Presentation` and use Microsoft's MVVM Toolkit (`CommunityToolkit.Mvvm`). Typed CQRS requests and results live in `BeetsMan.Desktop.Application`. Avalonia views bind to the Toolkit command properties in the last implementation stage.

## Binding contract

```csharp
public readonly record struct PresentationScope(Guid OwnerId, long Epoch);
public readonly record struct RequestStamp(
    PresentationScope Scope, Guid CorrelationId, long Sequence);

public interface IUiScheduler
{
    Task PostAsync(Action apply, CancellationToken cancellationToken);
}
```

- Implement ViewModels as `partial` classes derived from `ObservableObject`. Use `[ObservableProperty]` for bindable inputs and notified state, or `SetProperty` for explicit setters. Declare UI command methods with `[RelayCommand]`. `void` methods for synchronous local actions generate `IRelayCommand` properties. Methods returning `Task` generate `IAsyncRelayCommand` properties for application requests. A typed input yields the generic command interface. Avalonia binds each generated property directly through `System.Windows.Input.ICommand`. [ObservableObject](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/observableobject), [RelayCommand source generator](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand).
- Each ViewModel command method snapshots its input, owner and expected revision, then invokes `IRequestDispatcher.SendAsync` or `QueryAsync` once. `Outcome<T>` stays in the ViewModel execution path. The ViewModel posts the scoped projection through `IUiScheduler`, whose desktop adapter uses `Dispatcher.UIThread`. Shortcuts invoke the same ViewModel command on the UI thread. The application assembly stays independent of the Toolkit and Avalonia.
- Set `[RelayCommand(CanExecute = nameof(...))]` from current selection, editable state, capabilities, and any owner-level write gate shared by multiple commands. Use `[NotifyCanExecuteChangedFor]` for observable dependencies and `NotifyCanExecuteChanged()` after applying other relevant state. Handlers revalidate those inputs against authoritative state. `IAsyncRelayCommand.IsRunning` is command-level status. Owner-scoped status still tracks accepted jobs and controls that share a write gate. [RelayCommand source generator](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand).
- By default, an asynchronous command reports `CanExecute = false` to Avalonia while it is running. Programmatic invocations check `CanExecute` before execution, and the owner gate protects writes shared across command properties. Replaceable search enables `AllowConcurrentExecutions` and accepts a `CancellationToken`, so new input can cancel an earlier search. Keep the query sequence and owner checks because an earlier handler can still finish. Link the Toolkit token with the owning ViewModel scope. Translate expected failures into typed outcomes and apply cancellation feedback on the active scope even when the operation token is canceled. Use `IncludeCancelCommand` only for a binding with an explicit in-progress cancellation action. Dialog dismissal uses its existing `CloseOverlay` mapping. [AsyncRelayCommand](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/asyncrelaycommand), [RelayCommand source generator](https://learn.microsoft.com/dotnet/communitytoolkit/mvvm/generators/relaycommand).
- UI text, a captured gesture, unsubmitted fields, and pointer previews are local inputs. A drop, seek commit, or reorder captures stable IDs and dispatches one operation. Manual crossfader movement takes effect during adjustment. The binding submits the first manual position and coalesces subsequent positions by playback session. Key bindings and worker contributions resolve to a registered intent with typed arguments. Transport shortcuts use the same capability checks and results as their direct bindings.
- One ViewModel owns each shown state. Child ViewModels keep references to owner IDs and an immutable copy of the last applied snapshot. Closing or replacing an owner scope invalidates its previous epoch. A late completion can update its durable owner through the normal refresh or feed while its obsolete presentation callback is discarded.
- An operation that logically blocks the owning UI sets `IsInteractionBlocked` until it returns, fails, or is canceled. The eventual Avalonia binding supplies `StandardCursorType.Wait` through `WaitCursorConverter` during that interval. Independent host jobs and transfers report progress while the owning scope remains interactive. The binding follows the scope and clears the wait cursor override when it ends. `FR-DISPATCH-001#ac-wait-pointer`.

For example, these members of `QueueViewModel` generate an `IAsyncRelayCommand<bool>` property named `SetAutoSaveCommand`. `DispatchQueueCommandAsync` uses the typed dispatcher and applies the result to the active queue scope through `IUiScheduler`.

```csharp
[ObservableProperty]
[NotifyCanExecuteChangedFor(nameof(SetAutoSaveCommand))]
private bool isPlaylistBacked;

private bool CanSetAutoSave(bool enabled) => IsPlaylistBacked;

[RelayCommand(CanExecute = nameof(CanSetAutoSave))]
private Task SetAutoSaveAsync(bool enabled, CancellationToken cancellationToken) =>
    DispatchQueueCommandAsync(
        new SetQueueAutoSave(SessionId, enabled, QueueRevision), cancellationToken);
```

## ViewModel ownership

### Desktop shell and startup

- **`ShellViewModel`** is window-scoped. It owns current workspace, presentation mode and prior windowed mode, the overlay stack, saved focus targets, and the playback session ID it is presenting. A mode or workspace change reprojects the same playback session. Navigation updates the shell first. The newly active ViewModel then issues its load query under a fresh scope. Closing an overlay checks its owner and top-of-stack ID before restoring focus. Live registered Android arrival requests Normal Sync for that peer through the shell and preserves the active modal, draft, and playback session.
- **`BootstrapViewModel`** lasts through startup or repair. It owns the applied and proposed paths, validation, pending database creation approval, and restart status. Draft edits retain their values on failure. Approval is bound to the exact validated database path. The application opens the normal workspace after initialization succeeds or records restart-required settings for the next launch.
- **`DiagnosticsViewModel`** owns the category/filter and the current result set, which can load under the bootstrap registrations. Its log-folder action returns a typed platform handoff result.
- **`ActionErrorViewModel`** owns the active error, correlation ID, originating scope and optional typed retry binding. A retry retains the original request and projector, and uses the original operation ID for a command and a new correlation ID. The STB02 playlist-validation retry can repeat the same validation using the intact draft. Resolving a revision conflict requires a fresh explicit intent. Dismiss restores focus and preserves the owning draft.

### Catalog, playlists, and playback

- **`LibraryViewModel`** owns category, filters, sort, page, stable selection and catalog revision. Artist, album and search actions load one query scope. Each query's selected criteria travel with its result. Track actions carry stable track IDs and their displayed order. Local availability determines the playable selection, including while offline. Saved playlists can be the source of playback, an editable draft, or a Sync drop.
- **`PlaylistDraftViewModel`** owns the active draft ID, revision, ordered draft entries, name input, validation and saved-playlist revision. The playlist workspace owns the actual draft snapshot. A queue-backed draft uses a frozen, ordered queue snapshot and its session/revision. Validation, transport failure and conflict retain the draft and pending text. Only a saved receipt advances the saved-playlist version. Cancel detaches the editor from the draft. Any draft deletion requires a separate intent.
- **`PlaybackViewModel`** is playback-session-scoped. It owns the current track, duration, position, backend capability set, deck and master output projections. All presentation modes bind to this instance. During seek preview it holds the track ID and proposed position. The commit carries both so an intervening track change cannot seek another track. Seeking or skipping abandons pending automatic fade. Incoming deck failure keeps the outgoing deck and publishes a scoped error. Pause freezes the active fade. Manual crossfader input takes over from the current gain position.
- **`QueueViewModel`** projects the queue for the same playback session and owns selection by queue-entry ID, ordered edit state, and the saved-playlist backing and auto-save status. Each row has its own identity even when tracks repeat. It applies queue edits from `PlaybackState` and uses the returned saved-playlist receipt or explicit write status for auto-save. A revision conflict preserves the proposed order for explicit resolution. The application handler coordinates playlist saves through the playlist owner. It does not report a saved playlist from a queue-only commit.
- **`CliPlaybackViewModel`** owns the local profile draft, validation and scoped switch proposal. Unknown capabilities disable the corresponding actions. Successful switch keeps the queue and selected track and returns a stopped, ready session awaiting Play. Closing a proposal drops its pending selection. Failure keeps the actionable prior or recovery state reported by the playback owner.

### Preferences and extensions

- **`SettingsViewModel`** is category-scoped. It owns registry-backed input, the effective values with source and revisions, and the pending reset plan. Apply scopes each preference change to its registered writer. Inherit clears the local override and reprojects the effective value. Reset commits only the approved category and revision. Bootstrap changes use the bootstrap owner and local restart semantics.
- **`ShortcutsViewModel`** owns gesture capture, the binding scope, the proposed action/gesture pair, and the conflicting winning action when one exists. Global registration is an explicit opt-in for playback actions. An OS denial leaves the local binding intact. Cancel clears the proposal and retains the existing binding.
- **`ExtensionsViewModel`** owns package selection, local trust for the exact version, worker state, accepted contribution descriptors, diagnostics selection and visualization choice. A contribution is usable when its worker and declared capability are available. Detaching a worker clears its contribution state without changing the playback queue or transport. The shell controls any contribution overlay or visualization mode.

### Host library, replicas, and Sync

- **`HostLibraryViewModel`** owns the selected library, staged upload receipt, selected job, current review-item identity, and preview state for organization or removal. Each accepted job has its own ID and revision. The host owns job state and beets mutation. Navigating away leaves jobs active. The ViewModel reads or subscribes by job ID and revision when it returns. Close or cancel before submission clears the proposal, and cancellation of a pending UI read leaves any accepted job intact.
- **`TagEditViewModel`** owns a stable track ID, base catalog revision, edited fields and any accepted job ID. The form stays actionable on validation or conflict. It attaches to the accepted job and refreshes metadata from its published catalog revision.
- **`ReplicaViewModel`** owns the selected registered instance, redacted registration state, committed replication cursor, pending edit and conflict resolution draft. A fresh snapshot replaces the synchronized projection while preserving local settings, registrations, drafts and unresolved edits. Reapply local uses the current host revision captured for explicit resolution. Retried uncertain edits reconcile by operation ID before another external effect.
- **`MediaSyncViewModel`** owns selected device/share, pairing proposal, PC mirror status and selected transfer. The selected stable peer identity and live connection generation scope status updates. The media owner supplies resumable transfer state. The shell handles arrival navigation independently of transfer status, even with a modal open.
- **`SyncListViewModel`** owns the staged device plan ID/revision, selected scoped entries and the selection sent from Library. Track additions and saved-playlist drops use the same target. A saved-playlist drop stages one file in the selected app's format, preserving every track in order, plus distinct audio missing at the intended device paths. Canceling the preview returns to the same staged list. Only a publish action initiates transfer.
- **`DeviceExportViewModel`** owns the selected layout/profile revision, preview, confirmation for manifest-owned removals, and publication or transfer ID. The preview separates already-present audio, copies needed, and owned paths proposed for removal. Confirmation is bound to the exact plan revision and removal set. Cancel returns to an editable plan. A retry reads the publication's recorded checkpoint before attempting more work.

## Shared result fields

- `PlaybackState` carries `SessionId`, `Revision`, active track and position, capabilities, decks, master output, and an ordered queue snapshot. The queue carries its own `QueueRevision`, entry IDs, optional saved-playlist ID and revision, effective auto-save setting, and save status. A successful playlist write includes `PlaylistSavedReceipt`. Durable playback and playlist owners provide their own revisions.
- `PlaylistDraft` carries `DraftId`, `Revision`, name, ordered `(EntryId, TrackId)` entries, and optional base playlist ID and revision. `PlaylistSavedReceipt` carries `DraftId`, the committed playlist ID, and the committed playlist revision. The command input supplies the expected revision for each owner it edits.
- `DeviceExportPlan` carries `PlanId`, `PlanRevision`, `DeviceId`, the selected profile ID and revision, and entries keyed by stable source and resolved destination. A playlist entry contains its formatted path and every ordered track reference. Audio references include destination availability and staged-copy status. `DeviceExportPreview` carries this plan, blocking issues and any proposed owned removals. `ExportPublicationReceipt` carries `PublicationId`, `PlanId`, `PlanRevision`, and the transfer ID when one is scheduled.
- `LibraryJobReceipt` carries `JobId`, `LibraryId`, and accepted job revision. `LibraryJobStatus` carries the same identity and latest revision, the current import review item when applicable, recovery details, and a catalog revision after publication. Receipt and terminal status remain separate so other UI commands stay available while a job runs.

## Applying results and updates

- **`Success<T>`** applies to the matching active owner epoch. For a replaceable query, the request sequence must still be current for that query slot. A versioned snapshot also needs a revision at least as new as the one applied for that state owner. Write receipts and job identity are recorded independently of display. If a receipt arrives after navigation, the next load reads the committed state.
- **`Invalid<T>`** applies stable issue codes to matching input fields or the action. The draft retains its input. A subsequent corrected submission is a new intent. **`Conflict<T>`** retains the proposal and surfaces the actual revision when supplied. Reload through the matching query before explicit reapply. **`Cancelled<T>`** clears the corresponding pending state. **`DependencyFailure<T>`** exposes a scoped failure and only enables Retry when the original request is safe to replay. An accepted job is followed by job identity even if the UI call is canceled.
- Apply scheduler acknowledgments only to the matching request. Load, progress, and error indicators have the same scope as the action or subscription that originated them. Feed updates carry the owning session, job, or transfer ID and revision. Durable events and audio telemetry are application inputs. A loading, transferring, or playing indicator is a projection of those results.
- Auto-save applies to queue edits that target a saved-playlist-backed session and carries the saved-playlist revision. The handler reports whether a requested save was applied and returns its receipt when committed. The current criterion leaves the initial toggle value and its lifetime across sessions open. The command consumes the effective session state supplied by the owner.

See the [command catalog](ui-commands.md) for the result applied for each binding and the [Stage 0 contracts](../stage-0/contracts.md) for the data and recovery rules its handlers use.
