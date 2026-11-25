workspace "Beets Man" "Stage 0 architecture candidate" {
    model {
        listener = person "Listener and library operator"
        beets = softwareSystem "beets" "Owns music tagging, organization, and the source catalog."
        syncthing = softwareSystem "Syncthing" "Moves media files between the host and registered replicas."
        android = softwareSystem "Android peer" "Syncthing-Fork receives a device export. Musicolet reads music and playlists."
        os = softwareSystem "Operating system" "Provides local credentials, certificates, preferences paths, windows, hotkeys, and audio output."
        app = softwareSystem "Beets Man" "Desktop music playback, library management, and peer synchronization." {
            desktop = container "Desktop app" "Local browsing, playback, configuration, and user actions." "Avalonia 12 and .NET 10" {
                views = component "Presentation boundary" "Avalonia binds MVVM Toolkit ViewModels, typed CQRS commands, and system wait cursor state for logically blocked scopes."
                dispatcher = component "Avalonia dispatcher adapter" "Applies all application-driven presentation updates on UIThread."
                projection = component "Presentation projection" "Guards owner scope and revision, shares playback state, and exposes logical interaction blocking independently of background progress."
                cqrs = component "CQRS and Unit of Work" "Dispatches typed commands and queries to exactly one handler and returns a typed outcome. Coordinates writes by state owner."
                bootstrap = component "Desktop bootstrap" "Validates local configuration and resolves paths before opening desktop state. Reports initialization, repair, or ready state."
                config = component "Local configuration and settings" "Loads appsettings.json and resolves preference precedence."
                catalog = component "Catalog queries" "Reads indexed Library projections and reports track availability under the local music root."
                playlists = component "Playlist workspace" "Maintains ordered drafts and saves named playlists. Copies the current queue into a draft when requested."
                playback = component "Playback coordinator" "Owns the active queue and one playback session across views, transport and two independently cueable decks."
                soundflow = component "SoundFlow strategy" "Mixes decks, applies EQ, and produces audio telemetry."
                cli = component "External CLI strategy" "Translates YAML actions into command or session protocol calls."
                extensions = component "Plugin supervisor" "Manages local trust, worker IPC, and declarative contributions."
                hostClient = component "Host library client" "Submits authenticated library commands, controls registered PC mirrors, prepares and publishes Android exports, and reads and retries verified transfer checkpoints over HTTPS."
                androidMonitor = component "Android arrival monitor" "Uses a fresh baseline after any feed interruption and dispatches current registered Android arrivals through the existing shell command."
                replica = component "Record sync client" "Applies snapshots and revisions, replays edits, and acknowledges notifications."
                desktopPersistence = component "Desktop repositories" "Maps owned desktop state and materialized catalog queries to local LiteDB collections. Runs atomic local writes." "LiteDB adapter"
                log = component "Logging adapter" "ILogger scopes, local filters, rolling retention, and redaction."
            }
            local = container "Desktop state" "Cache, preferences, queue, drafts, and cursors." "LiteDB" {
                properties {
                    "Entity view" "DesktopLiteDbEntities"
                }
            }
            host = container "Library host" "Authenticated HTTP API and owner of synchronized application state." "ASP.NET Core and .NET 10" {
                hostStartup = component "Library host startup" "Loads reference-only configuration, validates local store and export paths, and starts one HTTPS listener with authenticated routes and durable transfer workers."
                api = component "Versioned host API" "Authenticates registered devices and dispatches host work."
                intake = component "Import staging" "Accepts uploaded copies and exposes staged inputs to guided import. Keeps drafts and temporary files separate from the published library."
                library = component "beets job coordinator" "Journals, executes, and reconciles external library writes."
                journal = component "Catalog and revision feed" "Projects beets records and publishes consistent snapshots and change pages."
                delivery = component "Notification queue" "Persists committed changes and tracks acknowledged recipients."
                exports = component "Device export manager" "Stages tracks and ordered playlists. Reviews layout changes and owned removals while the active profile remains assigned. Commits the new profile after verified file effects."
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
        host -> syncthing "Uses authenticated REST and device events"
        syncthing -> android "Transfers per-device exports"
        views -> cqrs "Submits typed intent"
        cqrs -> bootstrap "Validates and applies desktop bootstrap actions"
        bootstrap -> config "Resolves effective paths and startup configuration"
        bootstrap -> desktopPersistence "Opens the validated desktop store"
        desktopPersistence -> local "Queries collections and commits local transactions"
        cqrs -> config "Resolves preferences"
        cqrs -> catalog "Queries Library views and validates playable selections"
        catalog -> desktopPersistence "Reads indexed catalog and availability projections"
        cqrs -> playlists "Starts a draft, changes order, or saves a playlist"
        playlists -> desktopPersistence "Stores the draft and operation identity"
        playlists -> replica "Hands off saved playlist changes for replication"
        playlists -> playback "Reads the current queue snapshot for Save as playlist"
        cqrs -> playback "Executes transport and mixer actions"
        playback -> desktopPersistence "Restores and commits the current queue"
        cqrs -> hostClient "Submits host commands or requests current job state"
        hostClient -> api "Uploads copies, submits typed commands, and reads job status" "HTTPS JSON and streamed files"
        androidMonitor -> hostClient "Long polls authenticated Android arrival frames"
        androidMonitor -> cqrs "Applies live arrival and selects the device in Normal Sync"
        cqrs -> projection "Publishes typed outcomes"
        projection -> dispatcher "Schedules immutable UI update"
        dispatcher -> views "Applies state on UIThread"
        config -> desktopPersistence "Reads and writes local preference overrides"
        config -> log "Configures log levels and sinks"
        cqrs -> log "Logs outcomes within correlation scopes"
        playback -> soundflow "Executes built-in playback"
        playback -> cli "Executes optional CLI playback"
        soundflow -> os "Outputs mixed audio"
        cli -> external "Invokes direct arguments or sends stdio requests"
        playback -> extensions "Publishes bounded visualization telemetry"
        extensions -> workers "Exchanges validated IPC"
        extensions -> cqrs "Submits extension intents and status"
        extensions -> log "Forwards worker diagnostics"
        cqrs -> replica "Starts record operations"
        replica -> desktopPersistence "Commits applied pages and checkpoints"
        replica -> api "Registers, fetches changes and acknowledges"
        api -> replica "Forwards active Android peer state"
        replica -> cqrs "Reports sync and peer events"
        hostStartup -> api "Registers the authenticated host routes"
        hostStartup -> os "Reads scoped credentials and the TLS certificate"
        hostStartup -> hostPersistence "Opens and owns the configured LiteDB store"
        hostStartup -> exports "Binds the registered share roots and workers"
        api -> library "Dispatches beets jobs"
        api -> hostPersistence "Reads registered instance state and authorized Android bindings"
        hostPersistence -> data "Queries collections and commits host transactions"
        api -> intake "Authenticates and accepts uploaded album copies"
        library -> intake "Consumes a completed staged upload by ID"
        library -> hostPersistence "Persists job intent and outcome"
        library -> beets "Executes or reconciles an operation"
        library -> journal "Publishes reconciled state"
        api -> journal "Queries revision pages and snapshots"
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
            include desktopDraft desktopPlayback desktopOverride desktopOperation desktopCheckpoint desktopSchema
            autoLayout lr
        }
        custom "HostLiteDbEntities" "Host state LiteDB entities" "Document-level detail for the Host state container." {
            include hostArtist hostAlbum hostTrack hostPlaylist hostListening hostPreference hostInstance
            include hostJob hostReceipt hostRevision hostChange hostDelivery hostExport hostExportEntry hostSchema
            include hostExportManifest hostExportOwnedFile hostExportJournal hostExportJournalItem hostExportScan hostExportScanItem
            include hostPcMirrorState
            autoLayout lr
        }
        dynamic desktop "BootstrapDispatch" {
            views -> cqrs "Requests startup or submits a repair"
            cqrs -> bootstrap "Validates file, paths and restart settings"
            bootstrap -> config "Resolves JSON and local paths"
            bootstrap -> desktopPersistence "Opens the repository boundary after validation"
            desktopPersistence -> local "Opens the store and verifies its schema"
            cqrs -> projection "Publishes ready, first-run or repair state"
            projection -> dispatcher "Posts the matching startup state"
            dispatcher -> views "Applies the startup state on UIThread"
            autoLayout lr
        }
        dynamic desktop "LibraryWorkspace" {
            views -> cqrs "Requests a catalog view or search"
            cqrs -> catalog "Reads the selected Details grid"
            catalog -> desktopPersistence "Queries indexed tracks and media availability"
            desktopPersistence -> local "Reads the catalog projection and revision"
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
            api -> library "Accepts an authorized guided import choice"
            library -> hostPersistence "Records job intent before external effects"
            hostPersistence -> data "Commits job identity and intent"
            library -> intake "Resolves the staged input for an import job"
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
