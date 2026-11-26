workspace "Beets Man" "Stage 0 architecture candidate" {
    model {
        listener = person "Listener and library operator"
        beets = softwareSystem "beets" "Owns music tagging, organization, and the source catalog."
        musicBrainz = softwareSystem "MusicBrainz" "Supplies candidate release identities and metadata for guided import."
        syncthing = softwareSystem "Syncthing" "Moves media files between the host and registered replicas."
        android = softwareSystem "Android peer" "Syncthing-Fork receives a device export. Musicolet reads music and playlists."
        os = softwareSystem "Operating system" "Provides local credentials, certificates, preferences paths, windows, hotkeys, and audio output."
        app = softwareSystem "Beets Man" "Desktop music playback, library management, and peer synchronization." {
            desktop = container "Desktop app" "Local browsing, playback, configuration, and user actions." "Avalonia 12 and .NET 10" {
                views = component "Presentation boundary" "Avalonia binds MVVM Toolkit ViewModels, typed CQRS commands, and system wait cursor state for logically blocked scopes."
                dispatcher = component "Avalonia dispatcher adapter" "Applies all application-driven presentation updates on UIThread."
                projection = component "Presentation projection" "Owns active ViewModels, shares playback state, follows confirmed Android transfers, and delivers live alerts using effective notification settings."
                batchPresentation = component "Library batch ViewModel" "Projects the toggle, stage receipts from library edits, ordered review, commit recovery, revert, and guided import decisions."
                cqrs = component "CQRS and Unit of Work" "Dispatches typed commands and queries to exactly one handler and returns a typed outcome. Coordinates writes by state owner."
                bootstrap = component "Desktop bootstrap" "Composes first-run and repair commands, waits for the explicit interactive setup choice, and opens the active workspace when configuration is ready. Checkpoints playback before releasing its resources."
                config = component "Local configuration and settings" "Loads appsettings.json and resolves Appearance, playback, and notification preferences. Previews reset against the local and last-known shared layers."
                shortcuts = component "Shortcut settings" "Resolves the active dialog and views in precedence order, commits contextual overrides, and coordinates opt-in system playback registrations through a platform port."
                windowsKeys = component "Windows playback keys" "Owns registered user32 hotkeys and delivers accepted activations from a background message thread without creating a window."
                linuxKeys = component "Linux playback keys" "Owns an approved XDG GlobalShortcuts session, reads actual trigger descriptions, and removes revoked registrations."
                catalog = component "Catalog queries" "Reads indexed Library projections and applies the originating desktop's pending maintenance. Resolves playback from committed paths."
                playlists = component "Playlist workspace" "Maintains ordered drafts and saves named playlists. Replays original pending saves after reconnect and retains a draft when its host version has changed."
                batches = component "Local library batch control" "Stores ordered changes and the approved revision. Revert discards the local entries after withdrawing any pending host approval."
                desktopMaintenance = component "Library maintenance handlers" "Loads exact organization or removal previews. Routes a confirmation to the registered host or preserves the reviewed selection, paths, and mode in the local batch."
                batchImportFiles = component "Private import staging" "Keeps verified, operation-scoped import copies outside the music tree. Reclaims unadmitted or retired copies from persistent claims."
                playback = component "Playback coordinator" "Owns the active queue, transport, and two independently cueable decks. Advances automatic transitions from the outgoing audio clock and restores an opted-in saved playlist on startup."
                soundflow = component "SoundFlow strategy" "Owns two independently cueable decks and processes blend, master volume, and EQ. Captures a bounded post-mix window for waveform and spectrum analysis outside the render callback."
                cli = component "External CLI strategy" "Runs one-shot actions with separate process arguments or owns one correlated JSON session. Projects bounded worker replies into single-deck playback state."
                cliProfiles = component "Local CLI profiles" "Validates versioned action and reply mappings. Saves a YAML profile at its expected revision."
                extensions = component "Plugin supervisor" "Reconciles trusted enabled versions, starts workers outside workspace commands, and removes contributions when a worker exits or is retired."
                pluginPackages = component "Local plugin packages" "Acquires a desktop selection, validates the archive and manifest, and retains the exact private bytes by digest."
                pluginProcess = component "Plugin worker IPC" "Starts a pinned .NET worker in a private workspace and validates its correlated versioned greeting before exposing descriptors."
                visualization = component "Visualization session" "Selects Waveform, Spectrum, or an exact ready worker activation and projects fresh bounded frames independently of transport."
                hostClient = component "Host library client" "Reads and writes authorized host configuration, submits library commands, controls registered PC mirrors, prepares and publishes Android exports, and reads and retries verified transfer checkpoints over HTTPS."
                androidMonitor = component "Android arrival monitor" "Uses a fresh baseline after any feed interruption and dispatches current registered Android arrivals through the existing shell command."
                replica = component "Record sync client" "Registers the provisioned desktop and exposes the roster. Pulls incremental catalog changes, applies pages or pinned replacement snapshots, and acknowledges the durable local checkpoint after commit."
                listenerOutbox = component "Listening record outbox" "Captures offline favorites and stable plays. Replays the pinned request and preserves conflicting drafts for a reviewed decision."
                desktopPersistence = component "Desktop repositories" "Maps owned desktop state and materialized catalog queries to local LiteDB collections. Runs atomic local writes." "LiteDB adapter"
                log = component "Logging adapter" "ILogger scopes, local filters, rolling retention, redacted diagnostics, and the effective log-folder handoff."
            }
            local = container "Desktop state" "Cache, preferences, queue, drafts, pending library batches, and cursors." "LiteDB" {
                properties {
                    "Entity view" "DesktopLiteDbEntities"
                }
            }
            host = container "Library host" "Authenticated HTTP API and owner of synchronized application state." "ASP.NET Core and .NET 10" {
                hostStartup = component "Library host startup" "Loads reference-only configuration, validates local store and export paths, and starts one HTTPS listener with authenticated routes and durable transfer workers."
                api = component "Versioned host API" "Authenticates registered devices and dispatches host work."
                intake = component "Import staging" "Reserves an ordered manifest for a registered desktop. Validates each private copy and accepts a durable import job only after every file matches the manifest."
                library = component "beets job coordinator" "Journals, executes, and reconciles external library writes."
                beetsSetup = component "Native library setup and configuration" "Reports the provisioned library, initializes its catalog and YAML, and commits the Operator's approved naming, import behavior, and native plugin edits."
                trackTags = component "Track tag edits" "Reads revisioned track tags, admits one patch and receipt, and verifies the native tag write before publishing a catalog revision."
                maintenance = component "Library maintenance previews" "Uses the selected catalog revision to preview organization or removal. Records a scoped plan and admits one exact mode and operation."
                approvedBatches = component "Approved library batch service" "Journals the exact approved review and publishes playlist-only batches. Records pre-write withdrawal so a delayed approval cannot start."
                nativeBatches = component "Approved native batch worker" "Preflights and reconciles organization, tag edits, approved removals, and decided imports on the shared beets lane. Publishes verified effects together."
                approvedBatchCopies = component "Approved batch import staging" "Receives and verifies the missing copies declared in the approved review. Stores them privately by approval, entry and ordinal."
                approvedBatchReviews = component "Approved batch import review" "Offers guided choices for verified private albums and records each originating desktop decision against its exact review revision."
                removal = component "Library removal worker" "Removes approved catalog records by exact native identity and path. Deletes pinned audio only after a separate explicit authorization. Reconciles both effects before advancing the catalog feed."
                organization = component "Organization worker" "Checks approved source and destination paths, copies the exact audio bytes while retaining the source, and reconciles the native record before publishing."
                matchPreview = component "Album match preview" "Searches release metadata, runs the native matcher in disposable catalogs, and compares suggested titles in staged source order."
                journal = component "Catalog and revision feed" "Publishes verified catalog and shared user records in consistent change pages and snapshots."
                feedReader = component "Catalog replication routes" "Reads a fixed upper revision, captures recipient-scoped snapshots, and serves notification pulls and acknowledgements over authenticated HTTPS."
                recordWriter = component "Library record publication" "Validates eligible defaults, checks favorite revisions, and merges listening history by play ID."
                desktopRegistry = component "Desktop registration" "Enrolls the provisioned desktop and returns the roster with host activity observations. Active Operators can revoke another desktop at its reviewed registry revision."
                delivery = component "Notification queue" "Persists committed changes and tracks acknowledged recipients."
                exports = component "Device export manager" "Uses bundled and validated local YAML player profiles. Stages ordered playlist bytes in the selected format and adds only missing audio. Commits profile changes after verified file effects."
                mediaDirectory = component "Media directory and pairing" "Scopes an approval to an active peer and configured destination. Persists the original response and initial Android profile, then refreshes Android acceptance from the approved folder."
                peers = component "Syncthing adapter" "Offers the approved send-only folder. Verifies Android receiver acceptance and scope before and after index reads. Reads PC mirror progress, applies scoped controls, and forwards live peer transitions."
                androidRelay = component "Android arrival relay" "Publishes current registered Android connections with live arrival revisions. Reconciles native restart or gaps without replaying historical arrivals."
                hostPersistence = component "Host repositories" "Maps authoritative documents, operation receipts, revisions and delivery records to host LiteDB collections. Runs atomic host writes." "LiteDB adapter"
            }
            data = container "Host state" "Registrations, beets job journal, catalog, shared preferences, and notification delivery." "LiteDB" {
                properties {
                    "Entity view" "HostLiteDbEntities"
                }
            }
            workers = container "Plugin worker processes" "Runs trusted local extensions with desktop-user OS permissions." ".NET 10 and versioned IPC"
            external = container "Optional playback CLI process" "User-selected player or adapter." "Process and structured stdio"
        }
        listener -> desktop "Uses"
        desktop -> os "Uses playback, hotkeys, and credential services"
        host -> os "Reads configured credentials and the TLS certificate from local OS stores"
        desktop -> host "Sends authenticated commands and sync requests" "HTTPS JSON"
        desktop -> local "Reads and commits desktop state"
        desktop -> workers "Exchanges telemetry, declarations, commands, and log events" "Versioned IPC"
        desktop -> external "Invokes configured playback actions"
        host -> data "Commits host state and delivery state"
        host -> beets "Runs journaled library operations"
        host -> musicBrainz "Looks up release metadata for album review when matching is enabled" "HTTPS JSON"
        host -> syncthing "Uses authenticated REST and device events"
        syncthing -> android "Transfers per-device exports"
        views -> cqrs "Submits typed intent"
        cqrs -> bootstrap "Validates and applies desktop bootstrap actions"
        bootstrap -> cqrs "Initializes playback, advances it from the native clock, and checkpoints the stable session on shutdown"
        bootstrap -> config "Resolves effective paths and startup configuration"
        bootstrap -> log "Exposes redacted, in-memory diagnostics for configuration repair before the workspace opens"
        bootstrap -> desktopPersistence "Opens the validated desktop store"
        bootstrap -> batches "Supplies a single mode gate to the queue, playlist, tag, import, organize and removal handlers"
        bootstrap -> hostClient "Binds the registered HTTPS endpoint and credentials to library, replication, and Sync requests"
        bootstrap -> replica "Starts one independent refresh loop for the registered desktop and joins it before closing the store"
        bootstrap -> androidMonitor "Starts one registered feed after the shell and Sync ViewModels activate and cancels it on shutdown"
        desktopPersistence -> local "Queries collections and commits local transactions"
        cqrs -> config "Resolves preferences"
        cqrs -> cliProfiles "Loads, validates, and saves an editable profile"
        cqrs -> catalog "Queries Library views and validates playable selections"
        catalog -> desktopPersistence "Reads indexed catalog, availability, and the scoped maintenance effects on one store lane"
        cqrs -> playlists "Starts a draft, changes order, or saves a playlist"
        playlists -> desktopPersistence "Stores drafts, exact save payloads, receipts, and conflicts. Reads pending named saves for the registered origin."
        cqrs -> batches "Reads mode and review, controls the toggle, and resolves the approved or reverted revision"
        views -> batchPresentation "Binds batch-mode, approval, revert, and guided import commands"
        batchPresentation -> cqrs "Dispatches exact reviewed revisions and projects the resulting batch state"
        batchPresentation -> dispatcher "Shares scoped mode updates and wait pointer state with the active presentation"
        cqrs -> desktopMaintenance "Previews maintenance and confirms the same displayed plan"
        desktopMaintenance -> hostClient "Reads effects and sends confirmations when the batch is off"
        desktopMaintenance -> batches "Stages the reviewed effect locally when the batch is on"
        batches -> desktopPersistence "Stores scoped entries, approval, and replay receipts atomically"
        batches -> api "Submits or withdraws an exact approval and retries transfers and receipts after interruption"
        playlists -> batches "Routes named saves through the current mode and retains the reviewed order of entries"
        hostClient -> batches "Routes track tag edits and import copies through the current mode"
        batches -> batchImportFiles "Verifies pending copies, retains reviewed hashes, and releases retired data"
        playlists -> replica "Hands off saved playlist changes for replication"
        playlists -> playback "Reads the current queue snapshot for Save as playlist"
        cqrs -> playback "Executes lifecycle, transport and mixer commands"
        playback -> desktopPersistence "Restores the exact queue entry and position, and saves the checkpoint with the confirmed play"
        playback -> config "Reads the local restart opt-in and the effective automatic fade duration"
        playback -> batches "Stages queue auto-save with the playback checkpoint and pauses background playlist replay in batch mode"
        cqrs -> hostClient "Submits host commands or requests current job state"
        hostClient -> api "Uploads copies, submits typed commands, and reads job status" "HTTPS JSON and streamed files"
        androidMonitor -> hostClient "Long polls authenticated Android arrival frames"
        androidMonitor -> cqrs "Applies a fresh Android arrival, selects Normal Sync, and reads the selected peer"
        cqrs -> projection "Publishes typed outcomes"
        projection -> dispatcher "Schedules immutable UI update"
        dispatcher -> views "Applies state on UIThread"
        config -> desktopPersistence "Reads local and last-known shared preferences and clears approved overrides in one revision-checked transaction"
        cqrs -> shortcuts "Reads, edits and resolves contextual gestures or requests system playback keys"
        shortcuts -> desktopPersistence "Reads and replaces the device-local override set at its expected revision"
        shortcuts -> windowsKeys "Applies the opt-in key set and keeps prior registrations when an attempted replacement is denied"
        windowsKeys -> os "Registers, receives, and releases owned playback key gestures" "User32"
        windowsKeys -> cqrs "An accepted activation dispatches a bounded typed playback transport request"
        shortcuts -> linuxKeys "Creates a candidate session and retains the prior registration on denial"
        linuxKeys -> os "Requests opt-in GlobalShortcuts and receives session-scoped activations" "D-Bus"
        linuxKeys -> cqrs "Routes accepted activations and refreshes the editor when the portal changes or revokes keys"
        config -> log "Configures log levels and sinks"
        cqrs -> log "Logs outcomes within correlation scopes"
        log -> os "Opens the effective logs directory in the system file manager"
        playback -> soundflow "Reads the deck clock, prepares the incoming deck, and applies transport, blend, gain, and EQ"
        playback -> cli "Activates a reviewed CLI profile without starting audio and invokes its mapped transport"
        playback -> cliProfiles "Reads the exact profile revision for engine proposal and confirmation"
        soundflow -> os "Outputs mixed audio"
        cli -> external "Runs direct argv or correlated newline-delimited JSON with a deadline and worker cleanup"
        cliProfiles -> os "Reads and atomically writes local YAML beside the desktop settings"
        cqrs -> visualization "Selects and queries the session visualization"
        visualization -> playback "Reads backend capability and current deck context without holding the transport lane"
        visualization -> soundflow "Reads the latest bounded mixed audio window"
        visualization -> extensions "Resolves the selected worker activation and observes detach"
        visualization -> pluginProcess "Sends fresh telemetry and validates correlated bounded levels"
        visualization -> projection "Publishes visualization and detach state separately from playback"
        cqrs -> extensions "Installs, reviews trust, enables, disables, and removes an exact local package version"
        bootstrap -> extensions "Starts worker supervision after workspace activation and joins it before closing the store. Recovery mode suppresses launches."
        extensions -> pluginPackages "Validates the captured local selection before creating a version"
        extensions -> desktopPersistence "Persists version-scoped trust and enablement in a local LiteDB transaction"
        extensions -> projection "Publishes negotiated contributions and panel activation identity, and detaches them when the worker retires"
        extensions -> log "Records worker failures with the plugin ID and version"
        pluginPackages -> os "Copies selected archives to private digest-addressed storage"
        extensions -> pluginProcess "Starts the exact locally trusted and enabled version"
        pluginProcess -> pluginPackages "Rechecks the admitted digest and opens an isolated working copy"
        pluginProcess -> workers "Launches a child and negotiates bounded newline-delimited JSON"
        extensions -> cqrs "Reads host-owned playback context for panels and submits a typed host command when a live worker reply matches the installed grant"
        extensions -> log "Forwards worker diagnostics"
        cqrs -> replica "Starts record operations"
        replica -> desktopPersistence "Commits incremental pages or the complete staged replacement with its checkpoint"
        cqrs -> listenerOutbox "Stages favorite edits or plays and resolves local listening conflicts"
        listenerOutbox -> desktopPersistence "Stores chronological operations, predecessor revisions, and conflict decisions"
        replica -> listenerOutbox "Replays original operations when reconnecting"
        replica -> playlists "Resumes pending named saves after verifying the registered host feed"
        replica -> playback "Resumes pending queue auto-saves after verifying the registered host feed and leaves transport available during publication"
        listenerOutbox -> api "Publishes the exact scoped listening intent and reads its receipt"
        replica -> api "Enrolls a provisioned identity, reads the registry, and pulls and acknowledges library changes"
        api -> replica "Forwards active Android peer state"
        replica -> cqrs "Reports sync and peer events"
        hostStartup -> api "Registers the authenticated host routes"
        hostStartup -> os "Reads scoped credentials and the TLS certificate"
        hostStartup -> hostPersistence "Opens and owns the configured LiteDB store"
        hostStartup -> library "Starts the configured native import worker"
        hostStartup -> exports "Binds the registered share roots and workers"
        api -> library "Dispatches beets jobs"
        api -> recordWriter "Accepts listening and default changes from an authenticated registered desktop"
        api -> desktopRegistry "Dispatches authenticated roster, enrollment and revocation requests"
        api -> hostPersistence "Records coalesced activity for authenticated same-library desktops"
        desktopRegistry -> hostPersistence "Commits the registration state, registry revision and exact operation receipt"
        recordWriter -> hostPersistence "Commits the record or tombstone with its exact operation receipt"
        recordWriter -> journal "Publishes favorites, distinct plays and eligible defaults to registered peers"
        api -> hostPersistence "Reads registered instance state and authorized Android bindings"
        hostPersistence -> data "Queries collections and commits host transactions"
        api -> intake "Authenticates and accepts uploaded album copies"
        api -> trackTags "Reads track tags and admits revision-pinned edits"
        trackTags -> hostPersistence "Reads scoped catalog records and commits the patch, baseline, job and receipt"
        trackTags -> beets "Applies one item-level edit in place and independently reads its catalog and embedded tags"
        trackTags -> journal "Commits the verified edit and its originating operation as one revision"
        api -> maintenance "Previews and confirms organization or the selected removal mode"
        api -> approvedBatches "Submits, withdraws or reads the originating desktop's approved batch"
        api -> approvedBatchCopies "Streams copies for exact approved import file identities"
        approvedBatches -> hostPersistence "Reserves operation IDs and serializes withdrawal, native start and publication"
        approvedBatchCopies -> hostPersistence "Reads the approved manifest and records receipts after byte verification"
        api -> approvedBatchReviews "Records Keep tags, Use match, or Skip for the exact approved entry"
        library -> approvedBatchReviews "Discovers choices for entries whose private uploads have finished"
        approvedBatchReviews -> matchPreview "Uses the guided matcher with the private batch staging namespace"
        approvedBatchReviews -> hostPersistence "Persists the public review, private match identities and original decision receipts"
        approvedBatches -> journal "Publishes all playlist effects from an approved playlist-only batch together"
        library -> nativeBatches "Resumes the approved batch before starting other native work"
        nativeBatches -> hostPersistence "Checks all import decisions, pins targets and playlists, and stores durable file, tag and import evidence"
        nativeBatches -> beets "Prepares matched copies in private and reconciles exact import, edit, organization and removal effects"
        nativeBatches -> journal "Publishes all verified changes as one revision and settles skip-only batches at the existing head"
        maintenance -> beets "Previews exact organization destinations and fingerprints the selected audio files"
        maintenance -> hostPersistence "Pins the plan and admits the approval as one durable job and receipt"
        library -> removal "Drains catalog and explicitly approved audio removals on the shared beets lane"
        removal -> hostPersistence "Fences the native attempt and commits track tombstones, the job status and peer deliveries"
        removal -> beets "Removes matching catalog records, checks surviving native references, and verifies the exact approved file effect"
        removal -> journal "Publishes the verified deletion as one revision"
        library -> organization "Executes and recovers approved organization on the shared beets lane"
        organization -> hostPersistence "Persists the source digest and approved path mapping before writing, then commits verified paths"
        organization -> beets "Updates only the pinned item's path after copying and verifying its media"
        organization -> journal "Publishes the verified track paths together in one revision"
        intake -> hostPersistence "Stores manifests, accepts a job once per upload, and returns original receipts"
        library -> intake "Consumes a completed staged upload by ID"
        library -> matchPreview "Obtains release comparisons when matching is enabled"
        matchPreview -> intake "Reads verified staged source paths"
        matchPreview -> beets "Previews exact releases in disposable catalogs"
        matchPreview -> musicBrainz "Searches using album and album-artist tags" "HTTPS JSON"
        library -> hostPersistence "Persists job intent and outcome"
        api -> beetsSetup "Reads the provisioned library and dispatches versioned initialization and configuration"
        beetsSetup -> hostPersistence "Commits setup checkpoints, effective native options, and exact write receipts"
        beetsSetup -> beets "Creates a private native catalog and verifies selected local plugins"
        library -> hostPersistence "Loads the committed native settings revision for a new command"
        library -> beets "Executes or reconciles an operation"
        library -> journal "Publishes reconciled state"
        api -> journal "Queries revision pages and snapshots"
        api -> feedReader "Dispatches catalog change and notification requests"
        feedReader -> hostPersistence "Reads contiguous revisions, captures the scoped catalog, and updates recipient deliveries"
        journal -> hostPersistence "Commits authoritative records, revisions and delivery records together"
        journal -> delivery "Enqueues correlated publication"
        delivery -> hostPersistence "Stores recipient progress"
        api -> delivery "Pulls and acknowledges queued notifications"
        api -> exports "Edits staged plans, prepares and publishes reviewed plans, and reads or retries transfers"
        api -> mediaDirectory "Reads registered media state and dispatches pairing and mirror commands"
        mediaDirectory -> peers "Offers the folder, verifies receiver acceptance and mirror progress, and applies scoped controls"
        mediaDirectory -> hostPersistence "Reserves approvals and stores bindings, observations and operation receipts"
        exports -> journal "Reads ordered playlist entries and source track identities"
        exports -> hostPersistence "Commits prepared preview identity, operation receipts, progress and export ownership"
        exports -> peers "Reads receiver inventory and dispatches durable scans for committed sender folders"
        peers -> syncthing "Offers scoped folders, controls PC mirrors and observes connections"
        peers -> androidRelay "Publishes confirmed connections from the continuous native event stream"
        api -> androidRelay "Waits for authenticated Android connection observations"
        !include litedb-entities.dsl
        deploymentEnvironment "Self-hosted" {
            main = deploymentNode "Designated PC" "Windows or Linux host, or intended macOS host" {
                containerInstance host
                containerInstance data
            }
            workstation = deploymentNode "Registered desktop" "Windows, Linux or intended macOS" {
                containerInstance desktop
                containerInstance local
                containerInstance workers
                containerInstance external
            }
            phone = deploymentNode "Android device" "Syncthing-Fork and a player" {
                softwareSystemInstance android
            }
        }
    }
    views {
        systemContext app "SystemContext" {
            include *
            autoLayout lr
        }
        container app "Containers" {
            include *
            autoLayout lr
        }
        component desktop "DesktopComponents" {
            include *
            autoLayout lr
        }
        component host "HostComponents" {
            include *
            autoLayout lr
        }
        custom "DesktopLiteDbEntities" "Desktop state LiteDB entities" "Document-level detail for the Desktop state container." {
            include desktopArtist desktopAlbum desktopTrack desktopPlaylist desktopListening desktopSharedPreference
            include desktopDraft desktopPlayback desktopOverride desktopShortcuts desktopPluginRegistry desktopOperation desktopCheckpoint desktopSchema
            include desktopBatchMode desktopBatchEntry desktopBatchReceipt desktopBatchImportCopy
            include desktopSnapshot desktopSnapshotRow
            autoLayout lr
        }
        custom "HostLiteDbEntities" "Host state LiteDB entities" "Document-level detail for the Host state container." {
            include hostArtist hostAlbum hostTrack hostPlaylist hostListening hostPreference hostInstance hostDesktopRegistryHead hostBeetsSetup
            include hostJob hostMaintenancePlan hostImportUpload hostReceipt hostRevision hostChange hostDelivery hostExport hostExportEntry hostSchema
            include hostExportManifest hostExportOwnedFile hostExportJournal hostExportJournalItem hostExportScan hostExportScanItem
            include hostPcMirrorState
            include hostSnapshot hostSnapshotRow
            include hostBatchPublication hostBatchEntry hostBatchImportPart
            autoLayout lr
        }
        dynamic desktop "BootstrapDispatch" {
            bootstrap -> cqrs "Requests startup configuration and initializes first-run defaults"
            cqrs -> bootstrap "Validates file, paths and restart settings"
            bootstrap -> config "Resolves JSON and local paths"
            bootstrap -> desktopPersistence "Opens the repository boundary after validation"
            desktopPersistence -> local "Opens the store and verifies its schema"
            bootstrap -> cqrs "Initializes playback for the validated workspace"
            cqrs -> playback "Restores the existing session and applies the restart preference"
            cqrs -> projection "Publishes ready, first-run or repair state"
            projection -> dispatcher "Posts the matching startup state"
            dispatcher -> views "Applies the startup state on UIThread"
            autoLayout lr
        }
        dynamic desktop "LibraryWorkspace" {
            views -> cqrs "Requests a catalog view or search"
            cqrs -> catalog "Reads the selected Details grid"
            catalog -> desktopPersistence "Queries indexed tracks, availability and staged maintenance"
            desktopPersistence -> local "Reads the catalog and the active batch with their independent revisions"
            cqrs -> projection "Publishes scoped rows and selection"
            projection -> dispatcher "Posts the current Library projection"
            dispatcher -> views "Updates the grid beside the active list pane"
            views -> cqrs "Drops selected tracks into the Playlist list pane"
            cqrs -> playlists "Updates the ordered draft"
            cqrs -> projection "Publishes the revised list state"
            projection -> dispatcher "Posts the list change"
            dispatcher -> views "Updates the list pane on UIThread"
            autoLayout lr
        }
        dynamic desktop "GuidedImport" {
            views -> cqrs "Submits uploaded copies or an import choice"
            cqrs -> hostClient "Submits the operation without blocking navigation"
            cqrs -> projection "Publishes job identity and current status"
            projection -> dispatcher "Posts the import status"
            dispatcher -> views "Renders progress or tagged choices"
            views -> cqrs "Continues navigation or requests a job refresh"
            cqrs -> hostClient "Reads the latest job snapshot by identity"
            cqrs -> projection "Publishes only the matching job revision"
            projection -> dispatcher "Posts the scoped update"
            dispatcher -> views "Applies the job result beside the active view"
            autoLayout lr
        }
        dynamic desktop "PlaybackDispatch" {
            views -> cqrs "Submits typed playback intent"
            cqrs -> playback "Runs a transport or mix command"
            playback -> soundflow "Applies the deck and fade decisions"
            cqrs -> projection "Publishes result"
            projection -> dispatcher "Posts UI delta"
            dispatcher -> views "Applies current snapshot"
            autoLayout lr
        }
        dynamic host "HostPublication" {
            api -> intake "Accepts authenticated album copies before an import"
            intake -> hostPersistence "Records the manifest and accepts the job after copy verification"
            library -> matchPreview "Previews candidate tags when matching is enabled"
            api -> library "Accepts an authorized guided import choice"
            library -> intake "Resolves the staged input and prepares verified matched copies in private"
            library -> hostPersistence "Records the approved choice and prepared manifest before a live beets attempt"
            hostPersistence -> data "Commits job identity and intent"
            library -> beets "Executes or reconciles"
            library -> journal "Publishes reconciled state"
            journal -> delivery "Enqueues a committed revision"
            journal -> hostPersistence "Saves reconciled catalog changes, feed revision and deliveries"
            hostPersistence -> data "Commits publication as one transaction"
            autoLayout lr
        }
        dynamic host "AndroidArrival" {
            peers -> syncthing "Reads live DeviceConnected and DeviceDisconnected events"
            peers -> androidRelay "Publishes the confirmed connection after continuity and registration checks"
            api -> hostPersistence "Rechecks the authorized Android bindings"
            api -> androidRelay "Returns the current connection and live arrival revision"
            autoLayout lr
        }
        deployment * "Self-hosted" "SelfHosted" {
            include *
            autoLayout lr
        }
    }
}
