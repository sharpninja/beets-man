# UI commands and CQRS requests

Command names are scoped to their [owning ViewModels](presentation.md). A name such as `Queue.SetAutoSave` identifies the Toolkit-generated `QueueViewModel.SetAutoSaveCommand` property. Each command row gives its application request, the `T` in `Outcome<T>`, and the projection applied on success. **C** uses `SendAsync` and **Q** uses `QueryAsync` in the ViewModel command method. Application request names are shared with [the CQRS contract](cqrs.md). `expectedRevision` belongs to the identified aggregate or proposal. When a row specifies an option, it is a typed variant in the request. `SCR` IDs trace the bindings to the [existing control inventory](../ui-asset-manifest.json). `STB` IDs trace additional actions to the [existing storyboards](../design/_manifest.md).

## Shell, bootstrap, and errors

- **Shell.`Navigate`** · C `NavigateWorkspace(windowId, route)` → `ShellState`. Selects the workspace and starts its load query with a new scope. Library routes that must first resolve a selected entity use the read binding below. STB02, STB03, STB21.
- **Shell.`OpenPresentationChooser`** · C `OpenOverlay(windowId, ownerId, intent=ChoosePresentation)` → `ShellState`. Opens the mode chooser for the current playback presentation. STB21.
- **Shell.`ChangePresentation`** · C `ChangePresentation(windowId, modeOrRestore)` → `ShellState`. Updates mode or restores the previous windowed mode and its focus. Keeps the playback session. `SCR16.C02`, `SCR17.C01`, `SCR17.C02`, `SCR18.C01` and the mode chooser and Escape in STB04 and STB21.
- **Shell.`CancelDialog`** · C `CloseOverlay(windowId, overlayId, Cancel)` → `ShellState`. Restores the owning workspace and focus. The owning ViewModel releases the matching transient proposal. The playlist editor retains the draft and the export preview retains the editable staged list. `SCR11.C02`, `SCR21.C02`, `SCR23.C02`, `SCR33.C02`, `SCR34.C02`, `SCR36.C02`, `SCR39.C02`, `SCR40.C02`, `SCR41.C02`, `SCR43.C02`, `SCR44.C02`, `SCR51.C02`, `SCR52.C02`, `SCR53.C02`, `SCR54.C02`.
- **Shell.`CloseDialog`** · C `CloseOverlay(windowId, overlayId, Close)` → `ShellState`. Closes the matching overlay and restores focus without changing the job, worker, or transfer it was showing. `SCR24.C02`, `SCR25.C02`, `SCR26.C02`, `SCR47.C02`, `SCR55.C02`.
- **Bootstrap.`Load`** · Q `GetBootstrapState()` → `BootstrapState`. Reads effective paths, validation and readiness for the startup mode. STB01, STB11.
- **Bootstrap.`Retry`** · C `RetryDesktopBootstrap()` → `BootstrapState`. Re-reads repaired settings and retries opening the validated desktop state. `SCR02.C01`, STB11.
- **Bootstrap.`UseDefaults`** · C `InitializeDesktop(defaultSettings, creationApproval?)` → `BootstrapState`. Applies effective paths or returns the exact pending database creation proposal. `SCR01.C01`.
- **Bootstrap.`EditLocations`** · Q `GetBootstrapState()` → `BootstrapState`. Opens the owner-scoped location draft with current fields and validation. `SCR01.C02`, `SCR02.C02`, `SCR29.C01`.
- **Bootstrap.`BackToDefaults`** · Q `GetBootstrapState()` → `BootstrapState`. Returns to the first-run choices with the current defaults. STB01.
- **Bootstrap.`ApplyLocations`** · C `SaveBootstrapSettings(settings, expectedRevision, creationApproval?)` → `BootstrapState`. Applies valid first-run paths or reports restart required for an existing installation. Yes to create the database reuses this binding or `UseDefaults` according to the originating proposal, with its scoped approval. STB01, STB11.
- **Diagnostics.`Load` / `ClearFilter`** · Q `GetDiagnostics(filter, page)` → `DiagnosticsPage`. Replaces current rows using the supplied filter, empty for Clear. `SCR03.C02`.
- **Diagnostics.`OpenLogFolder`** · C `OpenLogFolder()` → `FolderOpenReceipt`. Reports the platform handoff for the effective log location. `SCR03.C01`.
- **ActionError.`Dismiss`** · C `CloseOverlay(windowId, overlayId, Dismiss)` → `ShellState`. Restores focus and keeps the failed action's draft. `SCR04.C01`.
- **ActionError.`Retry`** · Sends the captured `ICommand<T>` with the existing operation ID, or queries the captured `IQuery<T>` → `T` from the original binding. The Toolkit command invokes a stored execution delegate that retains the request and its typed projector. A new correlation ID identifies the attempt. `SCR04.C02`, STB02, STB15.

## Catalog and playlist drafts

- **Library.`Browse` / `Search` / `ClearSearch` / `OpenArtist` / `OpenAlbum`** · Q `BrowseCatalog(category, filter, sort, page)` → `CatalogPage`. The filter carries the stable artist/album ID or search text. Clear uses an empty search. The current query replaces the rows, selection scope, page cursor and catalog revision. A late earlier search has no presentation effect. `SCR05.C01`, `SCR05.C02`, `SCR06.C01`, `SCR07.C01`, `SCR09.C01`, STB03, STB12, STB13.
- **Library.`RefreshAvailability`** · Q `GetTrackAvailability(trackIds)` → `TrackAvailabilitySnapshot`. Projects playable and unavailable IDs for the current catalog selection, including downloaded audio offline. STB13.
- **Library.`SetFavorite`** · C `SetFavorite(trackId, isFavorite, expectedRevision)` → `FavoriteState`. Projects the committed local preference and its pending sync status. An offline change retains its operation ID for replay. `FR-RECORDS-001#ac-offline`.
- **Library.`PlaySelection` / `PlayTrack` / `PlaySearchResult` / `PlayPlaylist`** · C `EnqueueTracks(sessionId, orderedTrackIds, PlayNow)` → `PlaybackState`. Activates the requested selection and returns queue, active track and supported transport. `SCR06.C02`, `SCR08.C01`, `SCR09.C02`, STB03, STB13.
- **Library.`QueueAlbum` / `AddToQueue`** · C `EnqueueTracks(sessionId, orderedTrackIds, Append)` → `PlaybackState`. Appends the ordered, playable selection and updates the shared queue projection. `SCR07.C02`, `SCR08.C02`, STB03.
- **PlaylistDraft.`New` / `CreateFromQueue`** · C `CreatePlaylistDraft(source)` → `PlaylistDraft`. Source is empty or a frozen `QueueSnapshot(sessionId, revision, orderedTrackIds)`. Opens the new draft and projects its base revision. `SCR10.C01`, STB03, STB04.
- **PlaylistDraft.`Open`** · Q `GetPlaylistDraft(draftId)` → `PlaylistDraft`. Loads an existing draft by identity. STB03.
- **PlaylistDraft.`EditSavedPlaylist`** · C `CreatePlaylistDraft(source=SavedPlaylist(playlistId, expectedRevision))` → `PlaylistDraft`. Opens a draft based on the specified saved playlist revision. STB03, STB21.
- **PlaylistDraft.`AddSelected` / `DropTracks` / `Reorder` / `RemoveTracks`** · C `EditPlaylistDraft(draftId, operation, expectedRevision)` → `PlaylistDraft`. Add Selected and drop both send `Append(orderedTrackIds)`. The remaining variants are `Reorder(orderedEntryIds)` and `Remove(entryIds)`. Updates the order from the returned revision. `SCR10.C02`, STB03.
- **PlaylistDraft.`Save`** · C `SavePlaylist(draftId, name, expectedDraftRevision, expectedPlaylistRevision?)` → `PlaylistSavedReceipt`. Projects the saved playlist ID and revision and refreshes the catalog playlist collection. Validation keeps the draft and field values. `SCR11.C01`, STB02, STB03, STB04.

## Playback and queue

- **Playback.`Load`** · Q `GetPlaybackState(sessionId)` → `PlaybackState`. Loads transport, queue, track, position, decks and capability flags. The queue uses the same snapshot. STB04, STB21.
- **Playback.`PlayPause` / `Pause` / `Next` / `Previous` / `Stop` / `PlayDeck`** · C `Transport(sessionId, action, deckId?)` → `PlaybackState`. Resolves Play/Pause to the explicit action from current state. Applies the returned track, deck and fade state. `SCR12.C01`, `SCR12.C02`, `SCR16.C01`, `SCR18.C02`, STB04, STB05, STB14, STB15.
- **Playback.`CommitSeek`** · C `Seek(sessionId, activeTrackId, position, deckId?)` → `PlaybackState`. Applies the acknowledged position and recalculated fade state. A changed target or vanished capability is handled as a typed non-success. Shared player and STB04.
- **Playback.`CueDeck`** · C `CueDeck(sessionId, deckId, trackId)` → `PlaybackState`. Projects the actual cue/readiness state. `SCR14.C01`, STB04, STB14.
- **Playback.`SetVolume` / `SetDeckGain` / `MoveCrossfader` / `ToggleAutoFade`** · C `SetMix(sessionId, edit)` → `PlaybackState`. Variants are `MasterGain`, `DeckGain`, `CrossfaderPosition`, and `AutoFadeEnabled`. Projects applied gains and fade state. `SCR14.C02`, STB04, STB14.
- **Playback.`ApplyEqualizer` / `ResetEqualizer` / `SetEqBypass`** · C `SetEqualizer(sessionId, edit)` → `PlaybackState`. Variants are `ApplyBands`, `ResetBands`, and `Bypass(bool)`. Projects applied master EQ state. `SCR15.C01`, `SCR15.C02`, STB04.
- **Playback.`SetShuffle` / `SetRepeat`** · C `SetQueuePlaybackOptions(sessionId, edit)` → `PlaybackState`. Applies the specified shuffle or repeat setting and returns the resulting queue playback options. Shared player and STB04.
- **Queue.`LoadPlaylist`** · C `LoadPlaylistQueue(sessionId, playlistId, expectedPlaylistRevision)` → `PlaybackState`. Loads the ordered entries and establishes the saved-playlist backing. STB04, `FR-PLAYER-001#ac-queue-autosave`.
- **Queue.`Reorder` / `Remove`** · C `EditQueue(sessionId, edit, expectedQueueRevision, expectedPlaylistRevision?)` → `PlaybackState`. `Reorder(orderedEntryIds)` or `Remove(entryIds)` returns the authoritative order and any auto-save receipt or write status. `SCR13.C01`, `SCR13.C02`, STB04.
- **Queue.`SetAutoSave`** · C `SetQueueAutoSave(sessionId, enabled, expectedQueueRevision)` → `PlaybackState`. Sets the effective auto-save value for the currently backed queue. STB04, `FR-PLAYER-001#ac-queue-autosave`.

## Playback backend and extensions

- **CliPlayback.`ChooseEngine`** · Q `GetPlaybackStrategyProposal(sessionId, strategyId, profileId?)` → `PlaybackStrategyProposal`. Loads available capabilities or a revision-bound proposal requiring Switch. `SCR19.C01`, STB05, STB15.
- **CliPlayback.`EditProfile`** · Q `GetCliProfile(profileId?)` → `CliProfileDraft`. Opens proposed local profile fields or defaults with their revision. `SCR19.C02`.
- **CliPlayback.`ValidateProfile`** · Q `ValidateCliProfile(profileDraft)` → `CliProfileValidation`. Applies schema and capability validation without switching. `SCR20.C01`.
- **CliPlayback.`SaveProfile`** · C `SaveCliProfile(profileDraft, expectedRevision)` → `CliProfileReceipt`. Updates the stored local profile and revision. `SCR20.C02`.
- **CliPlayback.`Switch`** · C `ChangePlaybackStrategy(sessionId, proposalId, expectedRevision)` → `PlaybackState`. Projects the stopped new backend, retained queue and selected track, and supported capabilities. `SCR21.C01`, STB05, STB15.
- **Extensions.`Load` / `InspectDiagnostics` / `OpenVisualizationPicker`** · Q `GetPluginState(pluginId?, version?)` → `PluginState`. Loads the matching local trust, worker state and contributions for the active owner. STB06, STB16.
- **Extensions.`Install` / `Update`** · C `InstallPluginPackage(packageSelectionToken)` → `PluginState`. Projects installed identity/version and compatibility, including whether this version needs local trust. `SCR22.C01`, STB06.
- **Extensions.`Enable` / `Disable`** · C `SetPluginEnabled(pluginId, version, enabled)` → `PluginState`. Projects worker and contribution status. `SCR22.C02`, STB06, STB16.
- **Extensions.`TrustLocally`** · C `SetPluginTrust(pluginId, version, trust=Local)` → `PluginState`. Projects trust for the exact installed version. `SCR23.C01`.
- **Extensions.`Remove`** · C `RemovePlugin(pluginId, version)` → `PluginState`. Projects the remaining installed versions and contributions. Extension lifecycle.
- **Extensions.`RestartWorker`** · C `RestartPluginWorker(pluginId, version)` → `PluginState`. Projects current worker and negotiated contributions. `SCR24.C01`, STB16.
- **Extensions.`SelectVisualization`** · C `SelectVisualization(sessionId, contributionId)` → `PlaybackState`. Projects the available visualization for the active session. `SCR25.C01`, STB06.
- **Extensions.`RunContribution`** · Dispatches the descriptor's registered `ICommand<T>` or `IQuery<T>` → `T` from that binding. The Toolkit command takes the contribution invocation as its parameter. The registered descriptor validates the arguments and supplies the typed request and result projector. `SCR26.C01`, STB06.

## Settings and shortcuts

- **Settings.`OpenCategory`** · Q `GetPreferenceCategory(categoryId)` → `PreferenceCategorySnapshot`. Loads the typed settings, sources, revisions, and editability. `SCR27.C01`, STB07, STB08.
- **Settings.`RefreshPreference`** · Q `GetPreference(key)` → `EffectivePreference`. Projects one setting with its effective value, source, and revision. STB07.
- **Settings.`ApplyAppearance`** · C `SetLocalOverride(key, value, expectedRevision)` → `EffectivePreference`. Projects the effective theme and its local source. `SCR28.C01`, STB07.
- **Settings.`InheritAppearance`** · C `ClearLocalOverride(key, expectedRevision)` → `EffectivePreference`. Projects the newly inherited value and its source. `SCR28.C02`, STB07.
- **Settings.`EditImportDefaults`** · Q `GetPreferenceCategory(categoryId)` → `PreferenceCategorySnapshot`. Loads eligible fields and the permitted writer for the selected scope. `SCR29.C02`.
- **Settings.`OpenDevices`** · Q `GetMediaSyncState()` → `MediaSyncState`. Opens device management with current shares. `SCR30.C02`.
- **Settings.`ApplyNotifications` / `SetSyncDefaults` / `ApplyLocalDefaults`** · C `SetLocalPreferences(categoryId, changes, expectedRevision)` → `PreferenceCategorySnapshot`. Commits the scoped batch and projects the effective local values. `SCR30.C01`, `SCR31.C01`.
- **Settings.`ApplySharedDefaults`** · C `SetSharedPreferences(categoryId, changes, expectedHostRevision)` → `PreferenceCategorySnapshot`. Requires an authorized host writer. Projects the effective host revision without clearing local overrides. STB07, STB08.
- **Settings.`PreviewReset`** · Q `GetPreferenceResetPlan(categoryId)` → `PreferenceResetPlan`. Shows the local keys that will be cleared and their resulting effective values. `SCR27.C02`, `SCR31.C02`, STB07.
- **Settings.`ConfirmReset`** · C `ResetLocalPreferences(planId, expectedRevision)` → `PreferenceCategorySnapshot`. Applies the approved local reset and reloads category values. `SCR34.C01`, STB07.
- **Shortcuts.`OpenCapture` / `ChooseAnother`** · Q `GetShortcutBindings(scope)` → `ShortcutSnapshot`. Opens capture for the selected action with the current binding and precedence. `SCR32.C01`, `SCR33.C01`, STB17.
- **Shortcuts.`AssignCaptured`** · C `AssignShortcut(actionId, scope, gesture, expectedRevision)` → `ShortcutSnapshot`. Updates the binding on success. A stable conflict code identifies the collision and a current binding query supplies its winning action. The proposal stays open for another gesture or cancel. STB17.
- **Shortcuts.`ResetBindings`** · C `ResetShortcuts(scope, expectedRevision)` → `ShortcutSnapshot`. Projects the reset bindings in that scope. `SCR32.C02`.
- **Shortcuts.`SetSystemPlaybackKeys`** · C `SetGlobalPlaybackShortcuts(enabled, requestedBindings, expectedRevision)` → `ShortcutSnapshot`. Projects actual platform registration. On platform denial the opt-in stays unapplied and the local bindings are retained. STB17.

## Host library, import, and tags

- **HostLibrary.`OpenLibrary`** · Q `GetHostLibraryState(libraryId)` → `HostLibraryState`. Loads the existing host library and initialization state. `SCR35.C01`, STB08.
- **HostLibrary.`InitializeLibrary`** · C `InitializeHostLibrary(libraryId, approvedSettings, expectedRevision)` → `HostLibraryState`. Projects the newly initialized library or connects to the known result on retry. `SCR35.C02`.
- **HostLibrary.`OpenConfiguration`** · Q `GetHostLibraryConfiguration(libraryId)` → `HostLibraryConfiguration`. Loads the editable host paths, naming, import behavior, and beets plugin configuration allowed for the current operator. STB08, `FR-BEETS-001#ac-open`.
- **HostLibrary.`ApplyConfiguration`** · C `SaveHostLibraryConfiguration(libraryId, edits, expectedRevision)` → `HostLibraryConfiguration`. Projects the saved settings and revision. Uses the authorized host writer. `FR-BEETS-001#ac-open`.
- **HostLibrary.`PickSources`** · C `SelectImportSources(ownerId)` → `ImportSourceSelection`. Returns approved local file selections or cancellation through the desktop platform adapter. STB08.
- **HostLibrary.`UploadCopies`** · C `UploadImportSources(libraryId, sourceSelectionToken)` → `LibraryJobReceipt`. Stages copies and submits `StartImport(libraryId, uploadId)` only after the upload commits. The application adapter coordinates both steps and returns the accepted job identity. Incomplete staging has no accepted import job. `SCR36.C01`, STB08.
- **HostLibrary.`ChooseImportMatch` / `KeepTags` / `SkipImportItem`** · C `ChooseImportAction(jobId, reviewItemId, choice, expectedRevision)` → `LibraryJobReceipt`. Choice is `UseMatch(matchId)`, `KeepTags`, or `Skip`. Projects the new accepted revision for that job and then follows the job result. `SCR37.C01`, `SCR37.C02`, `SCR37.C03`, STB08.
- **HostLibrary.`InspectJob`** · Q `GetLibraryJob(jobId)` → `LibraryJobStatus`. Opens state, current review item, available next actions and any publication revision. `SCR38.C02`, STB02, STB08.
- **HostLibrary.`RetryJob`** · C `RetryLibraryJob(jobId, expectedRevision)` → `LibraryJobReceipt`. Reconciles a prior external attempt before doing more work. Steps that already committed return their recorded result. `SCR38.C01`.
- **TagEdit.`Open`** · Q `GetTrackTags(trackId)` → `TrackTagSnapshot`. Opens editable metadata at its catalog revision. STB08.
- **TagEdit.`Save`** · C `EditTrackTags(trackId, tagChanges, expectedCatalogRevision)` → `LibraryJobReceipt`. Attaches the tag edit to the accepted job and refreshes the track after that job publishes. `SCR39.C01`, STB08.
- **HostLibrary.`PreviewOrganize`** · Q `GetOrganizePlan(libraryId, trackIds, catalogRevision)` → `OrganizePlan`. Loads the exact proposed moves and base revision. STB18.
- **HostLibrary.`ConfirmMoves`** · C `OrganizeTracks(planId, expectedCatalogRevision)` → `LibraryJobReceipt`. Accepts the previewed moves and follows the job. `SCR40.C01`, STB18.
- **HostLibrary.`PreviewRemoval`** · Q `GetRemovalPlan(libraryId, trackIds, removalMode, catalogRevision)` → `RemovalPlan`. Loads the preview with the selected catalog-only scope and any explicit file effect requiring approval. STB18.
- **HostLibrary.`ConfirmRemoval`** · C `RemoveTracks(planId, expectedCatalogRevision, approval)` → `LibraryJobReceipt`. Submits the exact approved preview. The catalog-only plan retains audio. `SCR41.C01`, STB18.

## Registered instances and record replication

- **Replica.`LoadInstances`** · Q `GetRegisteredInstances(libraryId)` → `RegisteredInstancesSnapshot`. Loads stable instance IDs, access state and revision. STB09.
- **Replica.`OpenRegistration` / `OpenRevocation`** · C `OpenOverlay(windowId, ownerId, intent)` → `ShellState`. Opens the scoped registration form or revoke confirmation. Revocation intent carries the selected instance ID and registry revision. `SCR42.C01`, `SCR42.C02`, STB09.
- **Replica.`Register`** · C `RegisterInstance(libraryId, registrationDraft, expectedRevision)` → `RegistrationReceipt`. Projects the redacted registered instance and status, with credential handling in the OS adapter. `SCR43.C01`, STB09.
- **Replica.`Revoke`** · C `RevokeInstance(libraryId, instanceId, expectedRevision)` → `RegisteredInstancesSnapshot`. Projects revoked access and the new registry revision. `SCR44.C01`, STB09.
- **Replica.`RefreshStatus`** · Q `GetReplicationStatus(libraryId, instanceId)` → `ReplicationState`. Reads the committed cursor, pending count and conflicts. `SCR45.C02`, STB09, STB19.
- **Replica.`RetrySync` / `RefreshSnapshot`** · C `ResumeReplication(libraryId, instanceId, recoveryMode, expectedCursor)` → `ReplicationState`. Resumes incremental delivery or performs snapshot recovery. The returned state identifies the current recovery operation and the committed cursor. `SCR45.C01`, `SCR47.C01`, STB09, STB19.
- **Replica.`KeepHost` / `ReapplyLocal`** · C `ResolveReplicaConflict(conflictId, resolution, expectedHostRevision)` → `ReplicaConflictReceipt`. `KeepHost` resolves the recorded local proposal without a new host edit. `ReapplyLocal` applies the draft using the expected host revision and returns its recorded outcome. `SCR46.C01`, `SCR46.C02`, STB09, STB19.

## Media Sync and device export

- **MediaSync.`Load` / `OpenMirrors`** · Q `GetMediaSyncState()` → `MediaSyncState`. Loads paired peers, connection and transfer state. With an arrival route, select its peer by ID after loading the list. STB10, STB21.
- **MediaSync.`BeginPairing`** · C `OpenOverlay(windowId, ownerId, intent=PairMediaPeer)` → `ShellState`. Opens the pairing proposal for the current owner. `SCR48.C01`, STB10.
- **MediaSync.`ShareFiles`** · C `PairMediaPeer(peerId, shareSelection, expectedRevision)` → `MediaSyncState`. Projects the registered peer and its approved share configuration. `SCR51.C01`, STB10.
- **MediaSync.`Rescan`** · C `RescanMediaShare(shareId)` → `MediaSyncState`. Projects acceptance and ensuing current share status. `SCR48.C02`.
- **MediaSync.`PauseMirror` / `ResumeMirror`** · C `SetMirrorPaused(shareId, paused, expectedRevision)` → `MediaSyncState`. Projects the recorded pause or resume status for that PC share. `SCR49.C01`, `SCR49.C02`, STB10.
- **MediaSync.`InspectTransfer`** · Q `GetMediaTransfer(transferId)` → `MediaTransferState`. Loads the transfer, failure details, and retry eligibility. STB10, STB20.
- **MediaSync.`RetryTransfer`** · C `ResumeMediaTransfer(transferId, expectedRevision)` → `MediaTransferState`. Reconciles the recorded checkpoint for the same transfer or interrupted publication and projects progress. `SCR55.C01`, STB10, STB20.
- **SyncList.`Open`** · Q `GetDeviceExportPlan(deviceId, planId?)` → `DeviceExportPlan`. Loads the same stageable list for that device. `SCR48.C03`, STB10.
- **SyncList.`AddSelected` / `DropTracks`** · C `StageTracksForDevice(orderedTrackIds, deviceId, profileId, profileRevision, planId?, expectedPlanRevision?)` → `DeviceExportPlan`. Adds the selected tracks and returns the revised plan. Supply the current plan ID and revision together. With both absent, the intent expects an empty staged list for this device and profile. STB10 frames 15–18.
- **SyncList.`DropSavedPlaylist`** · C `StagePlaylistForDevice(playlistId, deviceId, profileId, profileRevision, planId?, expectedPlanRevision?)` → `DeviceExportPlan`. Uses the same plan identity rule as track staging. Adds the selected app's playlist file with all ordered tracks, plus unique audio copies missing at their intended destination. Existing and already staged audio are reused. When all audio is present, only the playlist file is added. STB10 frames 21–23, `FR-FILESYNC-001#ac-playlist-drop`.
- **SyncList.`RemoveStagedEntry`** · C `EditDeviceExportPlan(planId, Remove(entryIds), expectedPlanRevision)` → `DeviceExportPlan`. Recalculates shared audio dependencies for the remaining list. This is an edit of staged intent. STB10.
- **DeviceExport.`SelectLayout`** · Q `GetDeviceLayouts(deviceId)` → `DeviceLayoutsSnapshot`. Loads eligible player profiles and their revisions. `SCR50.C01`, STB10, STB20.
- **DeviceExport.`Preview` / `ResolveLayoutCollision`** · C `PrepareDeviceExportPreview(deviceId, profileId, profileRevision, planId?, expectedPlanRevision?, layoutEdits?)` → `DeviceExportPreview`. Returns the staged plan ID and revision, format, paths, existing versus missing audio, validation, and any owned paths proposed for removal. Collision resolution resubmits the edited layout for the same plan. A blocked preview keeps the issues and prevents publication. This request can prepare or revise a staged plan while its files stay outside the active share. `SCR50.C02`, `SCR52.C01`, STB10, STB20.
- **DeviceExport.`Publish`** · C `PublishDeviceExport(planId, expectedPlanRevision, removalApproval?)` → `DeviceExportSubmission`. The `Published(ExportPublicationReceipt)` case attaches transfer progress. `ConfirmationRequired(OwnedRemovalProposal)` opens approval for the same plan and proposed paths. `SCR53.C01`, STB10, STB20.
- **DeviceExport.`ConfirmOwnedRemoval`** · C `PublishDeviceExport(planId, expectedPlanRevision, removalApproval)` → `DeviceExportSubmission`. Sends approval for the exact proposal. A new removal set returns a new proposal for review. The committed publication reconciles only manifest-owned paths. The acknowledgement checkbox supplies command input and has no file effect on its own. `SCR54.C01`, STB10, STB20.

## Application inputs and feedback

- A newly received, live registered Android arrival dispatches C `ApplyAndroidPeerArrival(windowId, peerId, connectionGeneration)` → `ShellState`. Normal Sync becomes the active workspace and starts its usual load query with that peer selected. The application ignores historical, repeated, and unregistered arrivals and retains the active overlay, draft, and playback session. STB21.
- New results for the accepted library job, transfer, replica, playback session, or worker feed update the matching owner by ID and revision. Those updates drive storyboard progress, review-ready, completion, or failure states. STB02, STB04, STB06, STB08, STB09, STB10, STB14, STB16, STB19, STB20.
- Route map and state projection trace to `FR-DISPATCH-001`, `TR-DISPATCH-ARCH-001`, and `TEST-DISPATCH-001`/`TEST-DISPATCH-002`. The workflow bindings use the corresponding CONFIG, LIBRARY, PLAYER, CLI, PLUGINS, SETTINGS, BEETS, RECORDS and FILESYNC cases in the [test index](../testing/ui/index.md).
