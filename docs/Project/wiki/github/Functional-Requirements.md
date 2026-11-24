# Functional Requirements (QBrain.AI)

## FR-BEETS-001 Manage the host music library

Library operator can manage the host music library. Open a library on a connected desktop. Upload, review, edit, organize or remove music. Review the resulting catalog and any errors.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] An authorized desktop can open or initialize the beets library and manage supported paths, naming, import behavior and installed plugins.
- [ ] Uploading makes staged copies. Guided import offers match, keep tags, or skip; failures show recoverable job state.
- [ ] Tag edits reject stale revisions. Organize previews affected paths; catalog removal and audio deletion are separate choices, and deletion requires confirmation.
- [ ] Partial beets changes are reconciled before publishing revisions or retrying; offline metadata mutations are unavailable.

## FR-CLI-001 Use an external playback strategy

Desktop operator can use an external playback strategy. Choose a CLI profile. Validate and switch backend; invoke transport. Observe structured status on success or an actionable error.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Profiles validate declared actions, arguments, capabilities and status mappings for one-shot commands and stdin/stdout sessions. Unsupported controls are identified.
- [ ] Switching stops the prior backend, retains queue and track, and awaits explicit Play. Paths are passed as data; timeout, exit, and malformed replies are surfaced.

## FR-CONFIG-001 Configure this desktop

Desktop operator can configure this desktop. Start with an absent configuration file. Launch the desktop. Inspect resolved settings; restart after editing a local value.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] First launch creates a populated per-user appsettings.json with Information by default, Microsoft at Warning, bounded rolling logs, a local LiteDB path, and a resolved Music folder.
- [ ] After restart, explicit logging levels, database location, and music root take effect. Upgrades supply missing defaults and retain explicit and unknown keys.
- [ ] Malformed configuration and database paths inside a share show an actionable repair state. A newly selected missing database requires explicit creation approval; an unavailable music root leaves cached browsing usable.
- [ ] Diagnostics expose category, event, correlation and plugin identity where relevant. Secrets, raw CLI arguments, and private media metadata are omitted, and high-rate events are bounded.

## FR-DISPATCH-001 Execute a desktop action

Desktop operator can execute a desktop action. Open Library. Invoke an action from a button, shortcut, or plugin. Observe the result after UI dispatch.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Each app action submits exactly one typed Unit of Work to its handler; real validation failures and cancellation produce a typed result.
- [ ] A result does not change bound UI state until a dispatcher callback applies it; a stale result cannot replace a newer view.
- [ ] A long import does not block commands for UI navigation. Commands writing the same state do not expose partially applied changes.

## FR-FILESYNC-001 Mirror media to PCs and Android

Library operator can mirror media to PCs and Android. Pair a receiving peer. Select a layout or confirm mirror and sync. Verify playable media or diagnose share and transfer errors.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] PC peers mirror the full music library, show connection and share status, and expose pause, resume and rescan. A pending file remains unavailable to play.
- [ ] Each Android device can choose a validated YAML layout with independent export and share ID. New devices default to musicolet.yaml with album-based paths and relative UTF-8 M3U8 playlists.
- [ ] Preview identifies file adds, moves and removals and profile-driven removals need confirmation. Interrupted exports preserve the source and other shares.
- [ ] A registered Android connection immediately selects Normal UI on Sync and the peer through CQRS. Playback and modal draft/focus survive. Duplicate, desktop and historical events do not trigger navigation.
- [ ] Dropping a saved playlist into a device's Sync list stages a playlist file in the selected app profile's format, preserving every track in order. Stage only audio missing at that device's export destination, reusing audio already on the device or already staged. When all audio is present, stage just the playlist file.

## FR-LIBRARY-001 Browse the music catalog

Listener can browse the music catalog. Open a cached or online catalog. Browse or search by artist, album or track. Select an available track or inspect a pending track.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Artists, albums, tracks, playlists, and search show matching catalog records and player actions.
- [ ] Cached records remain browsable offline. Downloaded tracks play from the local root and pending media are identified as unavailable.

## FR-PLAYER-001 Play and mix music

Listener can play and mix music. Cue two local tracks. Play and blend them; switch presentation. Observe continuity and return from fullscreen.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Play, pause, seek, previous, next, volume, shuffle and repeat update a persistent queue and the active deck.
- [ ] Two independently cueable decks expose per-deck transport and gain; manual crossfader and bypassable master EQ apply to the output.
- [ ] Automatic crossfade uses the configured duration; manual input takes over; an incoming-track failure preserves the outgoing track and surfaces the error.
- [ ] Minimum, Normal, bounded visualization, and full visualization keep the session and queue. Escape restores the prior windowed mode; missing telemetry preserves usable transport and exit controls.
- [ ] When the active queue is backed by a saved playlist, the user can turn automatic saving of queue edits to that playlist on or off.

## FR-PLUGINS-001 Install and run extensions

Desktop operator can install and run extensions. Select a local plugin package. Review compatibility and trust; enable it. Use its contribution or observe isolated failure.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Local plugin packages can be installed, trusted per version, enabled, disabled, updated, and removed; shared defaults cannot trust or activate code.
- [ ] Validated plugin contributions render in host-owned surfaces. Visualization receives bounded available telemetry and app commands enter CQRS.
- [ ] Worker timeout or crash removes its contribution while playback and navigation remain usable. Diagnostics identify plugin/version, and recovery startup disables plugins.

## FR-RECORDS-001 Sync catalog, listening records and defaults

Registered desktop operator can sync catalog, listening records and defaults. Register two desktops. Edit from one and reconnect the other. Observe independent catch-up and conflict handling.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] Registered instances can originate host changes; revocation prevents further access from that instance.
- [ ] Catalog, playlists, favorites, history and eligible defaults converge through stable IDs, revisions and tombstones. Applying a page and its checkpoint is atomic.
- [ ] Other registered recipients pull and acknowledge committed changes independently. Duplicate delivery does not reapply changes and an offline recipient cannot block others.
- [ ] Offline playlists, favorites and history retain stable operation IDs. Plays merge by play ID and conflicting drafts survive replay and snapshot recovery.

## FR-SETTINGS-001 Set shared and local preferences

Desktop operator can set shared and local preferences. Open Settings. Change an eligible default or apply a local override. Inspect the effective value and source.
Scope: layer-1+
**Acceptance Criteria:**
- [ ] An explicit local override wins over a shared default, which wins over a built-in default. Clearing the override restores inheritance.
- [ ] Offline operation uses the last valid shared defaults. Invalid values are rejected; credentials, local paths, plugin trust and OS registration stay local.
- [ ] Contextual key mappings identify conflicts. Opt-in system playback shortcuts report OS denial and leave in-app mappings usable.

