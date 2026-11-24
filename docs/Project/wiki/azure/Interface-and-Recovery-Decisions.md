# Stage 0 interface and recovery decisions

These are candidate implementation decisions for the recorded v1 scope. The requirement records define observable behavior. The interfaces below identify owners, failure handling, and the test seams needed to implement it.

## Desktop and audio

See the [C4 workspace](../architecture/workspace.dsl), [CQRS application interfaces](../architecture/cqrs.md), and [LiteDB repository design](../architecture/persistence.md) for component owners, request and handler signatures, and write transaction boundaries.

- The internal dispatcher accepts a typed request and dispatches to one registered handler. A request context carries correlation ID and cancellation; the result distinguishes success, validation, conflict, cancellation, and dependency failure. Queries read immutable projections. Serialize mutations by the state they modify. No library job may block a navigation handler.
- Route UI changes through an async adapter bound to `Dispatcher.UIThread`. A presentation delta carries its view or session key plus sequence. Ignore stale deltas after a newer update or when the owning view is closed. Keep dialog ownership at the window shell so changing presentation does not move draft or keyboard focus.
- The playback strategy exposes capabilities, transport, a current state snapshot, and optional bounded visualization telemetry. The app owns the queue. SoundFlow owns the audio graph. Stop the old backend before activating a new one, leave the selected track intact, and await Play.
- Use a linear deck fade with endpoint gains A=1, B=0 and A=0, B=1. At midpoint use equal gains of 0.5. Persist the selected automatic fade duration as a local override or an eligible shared default. Clamp a fade to actual remaining time and defer the fade until the incoming deck is ready. Seeking and explicit skipping abandon any pending automatic fade and recompute from the new active track. Pause freezes the active fade. Manual crossfader movement cancels automatic gain changes and takes over from the current position. Preserve the outgoing track if preparing the incoming deck fails. Treat the ten-band master equalizer as a candidate for Stage 0 review.

## Local configuration and settings

- `appsettings.json` is local bootstrap input. Expand only the built-in absolute path variables for first-run defaults. Preserve unknown keys and a valid existing configuration. Apply the selected path and log filters at restart. Never use the process working directory to resolve a persistent location.
- Ordinary preferences are defined by a typed registry with scope, validation, schema version, and default. Cache eligible shared values with their host revision. A local action may remove a local override; it cannot change the shared value unless it submits a separately authorized host command.
- Contextual shortcut dispatch evaluates the active modal or view scope first. Expose the winning action in the editor and require conflict resolution before changing an existing binding. Global registrations are opt-in and carry only playback action IDs. Registration denial does not alter local bindings.

## CLI and plugin messages

- CLI YAML declares a schema version, invocation mode, executable, argument tokens, action-to-request mapping, reply/status mapping, capabilities, and timeouts. One-shot commands own separate processes. A session strategy owns one worker and correlates newline-delimited JSON replies by request ID. Never execute YAML content as shell input. Unknown or missing capabilities disable the dependent controls.
- Plugin packages declare a stable ID, version, contract range, entry point, and contributions. The host starts a locally trusted version and negotiates a protocol before showing contributions. IPC carries version, request/event kind, correlation, plugin identity, and a bounded payload. The plugin supplies declarative UI and invokes registered actions through CQRS. Ignore stale telemetry, have a timeout, and detach failed contributions. Worker processes share user permissions; transport validation and worker lifecycle provide crash containment.

## Host and replication

- The private HTTPS API scopes registration, catalog snapshots and pages, library jobs, listening edits, shared preferences, notification pulls and acknowledgments, and media peer operations. Store credentials in the OS credential store. A command carries its origin instance, stable operation ID, and expected revision when overwriting prior state.
- Persist host revisions monotonically per library, allocate stable IDs independently of disk paths, and publish tombstones for deletions. A snapshot fixes an inclusive upper revision before paging. Fetch subsequent changes after that upper revision. On a replica, persist received records and the contiguous cursor together. Acknowledge notifications only through that committed cursor. On expiry, rebuild only the synchronized projection and preserve local registrations, settings, drafts, and pending work.
- Before invoking beets, store intent and stable operation ID. After successful or interrupted execution, reconcile observed catalog and files against prior state. Publish revisions and delivery records together after reconciliation. A retry of uncertain external work must resolve what happened first. For listening history, distinct play IDs append and duplicate play IDs are idempotent. Conflicting playlist and favorite edits preserve a draft for explicit resolution.

## Media layout and Android navigation

- Each Android profile declares a player and template version, suggested root, relative path template, and optional relative playlist format. Validate relative ownership, reserved and colliding names, and expected source readability before publication. Keep staged bytes outside the active share until publication. Track uploaded files in a device-scoped manifest. Reconciliation may touch only paths owned by that manifest.
- Dropping a saved playlist in the Sync list stages a playlist file in the selected player profile's format, with every track in playlist order. Stage audio only when it is missing from the selected device at the intended export destination. Reuse audio already present or staged for that destination. With all audio present, the playlist file is the only item added. The preview distinguishes audio already on the device from audio queued for transfer.
- Syncthing assigns separate IDs to PC mirror and Android export shares. Relay live events keyed by registered Syncthing ID. An Android arrival enters Normal Sync through CQRS, preserves the current modal and playback, and ignores repeated or historical notifications. Resolve uncertain stream gaps by reconciling current peer state without treating historical messages as user navigation.

Before approval, verify the runtime dependency pins against package metadata, lock any remaining timing and retention constants, and bind the reviewed UI cases to the model's exact screen and branch references.
