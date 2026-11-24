# Stage 0 test specifications

Candidate mapping and test specifications. See [Stage 0 status](../../stage-0/README.md) for QBrain and review evidence. The first isolated presentation gate exercises independent mock fixtures and deliberate negative controls. macOS is an authored-only validation lane.

## TEST-CONFIG-001 · Configure this desktop

- Requirement: `FR-CONFIG-001`; technical obligation: `TR-CONFIG-ARCH-001`; actor: Desktop operator.
- Preconditions and data: Inject a Windows or Linux profile root, absent JSON, configured overrides, malformed JSON, and a seeded private field. Compare generated/resolved keys and filtered events.
- Isolated cases
  - `CONFIGAcceptanceTests.Defaults_MatchesContract` (`FR-CONFIG-001#ac-defaults`): First launch creates a populated per-user appsettings.json with Information by default, Microsoft at Warning, bounded rolling logs, a local LiteDB path, and a resolved Music folder.
  - `CONFIGAcceptanceTests.Overrides_MatchesContract` (`FR-CONFIG-001#ac-overrides`): After restart, explicit logging levels, database location, and music root take effect. Upgrades supply missing defaults and retain explicit and unknown keys.
  - `CONFIGAcceptanceTests.Repair_MatchesContract` (`FR-CONFIG-001#ac-repair`): Malformed configuration and database paths inside a share show an actionable repair state. A newly selected missing database requires explicit creation approval; an unavailable music root leaves cached browsing usable.
  - `CONFIGAcceptanceTests.Logs_MatchesContract` (`FR-CONFIG-001#ac-logs`): Diagnostics expose category, event, correlation and plugin identity where relevant. Secrets, raw CLI arguments, and private media metadata are omitted, and high-rate events are bounded.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-CONFIG-002`: Run first launch and restart against disposable directories. Inspect actual JSON, real LiteDB, and rolling app and worker log files. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb01-configure-this-desktop)

## TEST-DISPATCH-001 · Execute a desktop action

- Requirement: `FR-DISPATCH-001`; technical obligation: `TR-DISPATCH-ARCH-001`; actor: Desktop operator.
- Preconditions and data: Use recording handlers and a queued fake dispatcher. Deliver query revision 2 before revision 1 and hold a fake import open while navigating.
- Isolated cases
  - `DISPATCHAcceptanceTests.Routing_MatchesContract` (`FR-DISPATCH-001#ac-routing`): Each app action submits exactly one typed Unit of Work to its handler; real validation failures and cancellation produce a typed result.
  - `DISPATCHAcceptanceTests.Update_MatchesContract` (`FR-DISPATCH-001#ac-update`): A result does not change bound UI state until a dispatcher callback applies it; a stale result cannot replace a newer view.
  - `DISPATCHAcceptanceTests.Responsive_MatchesContract` (`FR-DISPATCH-001#ac-responsive`): A long import does not block commands for UI navigation. Commands writing the same state do not expose partially applied changes.
  - Planned logical blocking test (`FR-DISPATCH-001#ac-wait-pointer`): Hold startup on a controlled operation, verify the owning scope requests the system wait pointer, and verify that completion, failure, cancellation, and owner replacement clear the override. Independent background work keeps the scope interactive.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-DISPATCH-002`: Exercise an actual Avalonia window, dispatcher, focus, modal draft and bound collection changes with background callbacks. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb02-execute-a-desktop-action)

## TEST-LIBRARY-001 · Browse the music catalog

- Requirement: `FR-LIBRARY-001`; technical obligation: `TR-LIBRARY-ARCH-001`; actor: Listener.
- Preconditions and data: Seed album A with matching downloaded track T1 and pending track T2. Disconnect the host and compare filtering, status, and the playback request.
- Isolated cases
  - `LIBRARYAcceptanceTests.Browse_MatchesContract` (`FR-LIBRARY-001#ac-browse`): Artists, albums, tracks, playlists, and search show matching catalog records and player actions.
  - `LIBRARYAcceptanceTests.Offline_MatchesContract` (`FR-LIBRARY-001#ac-offline`): Cached records remain browsable offline. Downloaded tracks play from the local root and pending media are identified as unavailable.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-LIBRARY-002`: Open a real LiteDB cache and resolve tracks against a prepared local music tree while disconnected. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb03-browse-the-music-catalog)

## TEST-PLAYER-001 · Play and mix music

- Requirement: `FR-PLAYER-001`; technical obligation: `TR-PLAYER-ARCH-001`; actor: Listener.
- Preconditions and data: Use a fake backend, two tracks A and B and a controllable audio clock. Compare queue, channel targets, fade cancellation, and state before and after each mode transition.
- Isolated cases
  - `PLAYERAcceptanceTests.Transport_MatchesContract` (`FR-PLAYER-001#ac-transport`): Play, pause, seek, previous, next, volume, shuffle and repeat update a persistent queue and the active deck.
  - `PLAYERAcceptanceTests.QueueAutoSave_MatchesContract` (`FR-PLAYER-001#ac-queue-autosave`): Toggle auto-save for a queue backed by a saved playlist. Check that ordered queue edits persist to that playlist when enabled and that the saved playlist keeps its prior order when disabled.
  - `PLAYERAcceptanceTests.Mix_MatchesContract` (`FR-PLAYER-001#ac-mix`): Two independently cueable decks expose per-deck transport and gain; manual crossfader and bypassable master EQ apply to the output.
  - `PLAYERAcceptanceTests.Fade_MatchesContract` (`FR-PLAYER-001#ac-fade`): Automatic crossfade uses the configured duration; manual input takes over; an incoming-track failure preserves the outgoing track and surfaces the error.
  - `PLAYERAcceptanceTests.Modes_MatchesContract` (`FR-PLAYER-001#ac-modes`): Minimum, Normal, bounded visualization, and full visualization keep the session and queue. Escape restores the prior windowed mode; missing telemetry preserves usable transport and exit controls.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-PLAYER-002`: Verify SoundFlow native output by codec, mixer and EQ response, physical output and window transitions. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb04-play-and-mix-music)

## TEST-CLI-001 · Use an external playback strategy

- Requirement: `FR-CLI-001`; technical obligation: `TR-CLI-ARCH-001`; actor: Desktop operator.
- Preconditions and data: Supply synthetic one-shot and session replies for Play, an unsupported EQ action, an exit failure and a timeout. Compare the structured state and exact argument tokens.
- Isolated cases
  - `CLIAcceptanceTests.Profiles_MatchesContract` (`FR-CLI-001#ac-profiles`): Profiles validate declared actions, arguments, capabilities and status mappings for one-shot commands and stdin/stdout sessions. Unsupported controls are identified.
  - `CLIAcceptanceTests.Switch_MatchesContract` (`FR-CLI-001#ac-switch`): Switching stops the prior backend, retains queue and track, and awaits explicit Play. Paths are passed as data; timeout, exit, and malformed replies are surfaced.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-CLI-002`: Use fixture executables with spaced arguments, timeout, process exit, request/reply and malformed records in both invocation modes. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb05-use-an-external-playback-strategy)

## TEST-PLUGINS-001 · Install and run extensions

- Requirement: `FR-PLUGINS-001`; technical obligation: `TR-PLUGINS-ARCH-001`; actor: Desktop operator.
- Preconditions and data: Supply plugin sample ID example.scope at versions 1 and 2, a valid view, an invalid view, a stale frame and a simulated dead worker. Observe trust, rendered contribution and recovery.
- Isolated cases
  - `PLUGINSAcceptanceTests.Manage_MatchesContract` (`FR-PLUGINS-001#ac-manage`): Local plugin packages can be installed, trusted per version, enabled, disabled, updated, and removed; shared defaults cannot trust or activate code.
  - `PLUGINSAcceptanceTests.Visualize_MatchesContract` (`FR-PLUGINS-001#ac-visualize`): Validated plugin contributions render in host-owned surfaces. Visualization receives bounded available telemetry and app commands enter CQRS.
  - `PLUGINSAcceptanceTests.Recovery_MatchesContract` (`FR-PLUGINS-001#ac-recovery`): Worker timeout or crash removes its contribution while playback and navigation remain usable. Diagnostics identify plugin/version, and recovery startup disables plugins.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-PLUGINS-002`: Run real workers and send valid/invalid IPC; kill a worker during playback. Inspect categorized logs and recovery startup. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb06-install-and-run-extensions)

## TEST-SETTINGS-001 · Set shared and local preferences

- Requirement: `FR-SETTINGS-001`; technical obligation: `TR-SETTINGS-ARCH-001`; actor: Desktop operator.
- Preconditions and data: Use fade duration 0 built-in, 4 shared, and 2 local. Drop connectivity, clear the local override, and deny a synthetic global shortcut.
- Isolated cases
  - `SETTINGSAcceptanceTests.Precedence_MatchesContract` (`FR-SETTINGS-001#ac-precedence`): An explicit local override wins over a shared default, which wins over a built-in default. Clearing the override restores inheritance.
  - `SETTINGSAcceptanceTests.Availability_MatchesContract` (`FR-SETTINGS-001#ac-availability`): Offline operation uses the last valid shared defaults. Invalid values are rejected; credentials, local paths, plugin trust and OS registration stay local.
  - `SETTINGSAcceptanceTests.Shortcuts_MatchesContract` (`FR-SETTINGS-001#ac-shortcuts`): Contextual key mappings identify conflicts. Opt-in system playback shortcuts report OS denial and leave in-app mappings usable.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-SETTINGS-002`: Propagate defaults between running desktops and exercise OS-level hotkey registrations and conflicts. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb07-set-shared-and-local-preferences)

## TEST-BEETS-001 · Manage the host music library

- Requirement: `FR-BEETS-001`; technical obligation: `TR-BEETS-ARCH-001`; actor: Library operator.
- Preconditions and data: Inject a journal and fake beets results for upload A, a stale revision, a move preview, catalog removal and a write failure after one result. Compare intended changes and published revisions.
- Isolated cases
  - `BEETSAcceptanceTests.Open_MatchesContract` (`FR-BEETS-001#ac-open`): An authorized desktop can open or initialize the beets library and manage supported paths, naming, import behavior and installed plugins.
  - `BEETSAcceptanceTests.Import_MatchesContract` (`FR-BEETS-001#ac-import`): Uploading makes staged copies. Guided import offers match, keep tags, or skip; failures show recoverable job state.
  - `BEETSAcceptanceTests.Edit_MatchesContract` (`FR-BEETS-001#ac-edit`): Tag edits reject stale revisions. Organize previews affected paths; catalog removal and audio deletion are separate choices, and deletion requires confirmation.
  - `BEETSAcceptanceTests.Recover_MatchesContract` (`FR-BEETS-001#ac-recover`): Partial beets changes are reconciled before publishing revisions or retrying; offline metadata mutations are unavailable.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-BEETS-002`: Use real beets and a disposable library, inspect staged bytes, tags, filenames, catalog revisions and partial job recovery. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb08-manage-the-host-music-library)

## TEST-RECORDS-001 · Sync catalog, listening records and defaults

- Requirement: `FR-RECORDS-001`; technical obligation: `TR-RECORDS-ARCH-001`; actor: Registered desktop operator.
- Preconditions and data: Register A, B and C. Deliver the same change twice to B while C is offline, then reconnect C with an expired cursor and a conflicting draft.
- Isolated cases
  - `RECORDSAcceptanceTests.Register_MatchesContract` (`FR-RECORDS-001#ac-register`): Registered instances can originate host changes; revocation prevents further access from that instance.
  - `RECORDSAcceptanceTests.Replicate_MatchesContract` (`FR-RECORDS-001#ac-replicate`): Catalog, playlists, favorites, history and eligible defaults converge through stable IDs, revisions and tombstones. Applying a page and its checkpoint is atomic.
  - `RECORDSAcceptanceTests.RepositoryWrites_MatchesContract` (`FR-RECORDS-001#ac-replicate`, `TR-RECORDS-ARCH-001#ac-litedb-repositories`): Exercise stale revisions, duplicate operation keys with different intent, and an injected failure during a write. Check atomic visibility of records, retry receipts, and applicable revision or delivery state.
  - `RECORDSAcceptanceTests.Notify_MatchesContract` (`FR-RECORDS-001#ac-notify`): Other registered recipients pull and acknowledge committed changes independently. Duplicate delivery does not reapply changes and an offline recipient cannot block others.
  - `RECORDSAcceptanceTests.Offline_MatchesContract` (`FR-RECORDS-001#ac-offline`): Offline playlists, favorites and history retain stable operation IDs. Plays merge by play ID and conflicting drafts survive replay and snapshot recovery.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-RECORDS-002`: Restart live host and replicas around commit and acknowledgment, revoke an instance, and prove catch-up after expiry. Clean up only case-owned files, workers, and shares.
- Planned persistence verification in `TEST-RECORDS-002` covers rollback across LiteDB collections, receipt lookup after reopening, atomic page/checkpoint writes, and C4-to-collection coverage. [Repository design](../../architecture/persistence.md#verification-seams)
- [Storyboard paths](coverage.md#stb09-sync-catalog-listening-records-and-defaults)

## TEST-FILESYNC-001 · Mirror media to PCs and Android

- Requirement: `FR-FILESYNC-001`; technical obligation: `TR-FILESYNC-ARCH-001`; actor: Library operator.
- Preconditions and data: Inject Android and desktop IDs, overlapping template paths, one interrupted export and a modal draft. Compare export ownership, preview, allowed removals and the final selected peer.
- Isolated cases
  - `FILESYNCAcceptanceTests.Mirror_MatchesContract` (`FR-FILESYNC-001#ac-mirror`): PC peers mirror the full music library, show connection and share status, and expose pause, resume and rescan. A pending file remains unavailable to play.
  - `FILESYNCAcceptanceTests.Layouts_MatchesContract` (`FR-FILESYNC-001#ac-layouts`): Each Android device can choose a validated YAML layout with independent export and share ID. New devices default to musicolet.yaml with album-based paths and relative UTF-8 M3U8 playlists.
  - `FILESYNCAcceptanceTests.PlaylistDrop_MatchesContract` (`FR-FILESYNC-001#ac-playlist-drop`): Exercise mixed and fully present device inventory, repeated or overlapping drops, and two app profiles. Check playlist format and order and the exact missing audio staged for the device.
  - `FILESYNCAcceptanceTests.Safety_MatchesContract` (`FR-FILESYNC-001#ac-safety`): Preview identifies file adds, moves and removals and profile-driven removals need confirmation. Interrupted exports preserve the source and other shares.
  - `FILESYNCAcceptanceTests.Arrival_MatchesContract` (`FR-FILESYNC-001#ac-arrival`): A registered Android connection immediately selects Normal UI on Sync and the peer through CQRS. Playback and modal draft/focus survive. Duplicate, desktop and historical events do not trigger navigation.
- Timing: await the typed outcome or run the queued dispatcher callback; use a controllable clock for time-dependent cases.
- Negative control: replace the relevant fake response with an incorrect outcome and require this case's assertion to reject it.
- Integration `TEST-FILESYNC-002`: Use two PC replicas and physical Android with Syncthing-Fork and Musicolet. Verify arrival from all modes, share isolation, playlist order, restart recovery and deletion propagation. Clean up only case-owned files, workers, and shares.
- [Storyboard paths](coverage.md#stb10-mirror-media-to-pcs-and-android)
