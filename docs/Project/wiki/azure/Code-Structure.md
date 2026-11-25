# Code structure and implementation order

1. Define the [C4 boundaries](workspace.dsl) and [CQRS interfaces](cqrs.md). Establish request types, state ownership, transaction boundaries, result handling, and test seams.
2. Define [ViewModels](presentation.md) and [UI commands](ui-commands.md) against those application contracts. Specify ownership, typed request/result bindings, and projection rules for completion, cancellation, conflicts, and errors.
3. Implement screens last. Bind views to the defined ViewModels and UI commands, then verify each implemented change against the approved Asset Review state and storyboard path.

## .NET solution and planned project boundaries

The solution contains the desktop application and adapter projects, the host application and versioned contracts, and separate unit and integration projects for both. `build.ps1` runs `Compile`, `Test`, `TestGroups`, and `MigrationIntegrationTests` through Nuke. `LibraryHostStartup` composes the HTTPS API, LiteDB store and transfer workers. The standalone host binds one configured HTTPS listener and reads its credentials and certificate from the launching user's OS stores. The desktop executable remains planned.

```text
BeetsMan.slnx
src/
  BeetsMan.Desktop/
    Bootstrap/                 Composition root and process startup
    Presentation/              Toolkit ViewModels and relay commands, projection, UI scheduler
    Views/                     Avalonia views and bindings at the final stage
    Adapters/                  Local paths, logging, CLI, SoundFlow, host HTTP
      Persistence/             Desktop LiteDB repositories, mappings, transactions
  BeetsMan.Desktop.Application/
    Dispatch/                  Typed requests, one handler, typed results
    Configuration/             Bootstrap policy, preference registry, overrides
    Library/                   Catalog queries and ordered playlist drafts
    Playback/                  Session, active queue, transport, deck and fade policy
    Extensions/                Trust, worker lifecycle, declarative contributions
    HostLibrary/               Guided import and tag-edit workflow handlers
    PresentationState/        UI-neutral shell navigation and overlay intents
    Replication/               Registration, replication, snapshot and conflict intents
    MediaSync/                 Peer, staged list, layout and publication intents
  BeetsMan.Host/
    Bootstrap/                 HTTPS launcher, local credential adapters and registered share routing
    Api/                       Authentication, uploads, operation and job endpoints
    Application/               Job coordination, catalog revisions, publication
    Adapters/                  beets process, staging files, Syncthing
      Persistence/             Host LiteDB repositories, mappings, transactions
  BeetsMan.Host.Contracts/     Versioned HTTP and job-status payloads
  BeetsMan.Plugin.Protocol/    Versioned worker messages and contribution contracts
tests/
  BeetsMan.Desktop.Tests/
  BeetsMan.Host.Tests/
  BeetsMan.Desktop.IntegrationTests/
  BeetsMan.Host.IntegrationTests/
```

`Desktop` references `Desktop.Application`, `Host.Contracts`, `Plugin.Protocol`, and `CommunityToolkit.Mvvm`. `Host` references `Host.Contracts`. The Desktop composition root binds application interfaces to adapters. `Desktop.Application` keeps its request, result and port types independent of the MVVM Toolkit, Avalonia, LiteDB, process execution, and HTTP payloads. Both protocol projects contain transport-facing records and no runtime state. Host job decisions depend on interfaces for storage and external commands. UI bindings, LiteDB documents, and HTTP payloads each stay inside their own boundary. [LiteDB repository contracts and collection ownership](persistence.md)

## Standalone host

Build with `./build.ps1 Compile` and run `dotnet <compiled-host-dll> --config <host-settings.json>` under the account that owns the credentials and TLS certificate. The example uses Linux paths and binds to loopback. Replace its IDs and paths with local values and select the intended private network address for remote access. The certificate thumbprint selects a valid certificate with its private key from the current user's Personal certificate store. Desktops must trust that certificate and use a host name or address covered by it.

```json
{
  "host": {
    "libraryId": "c2320065-563c-4bf9-821f-9e25f6f2a679",
    "databasePath": "/srv/beets-man/private/host.db",
    "musicRoot": "/srv/beets-man/music",
    "privateStageRoot": "/srv/beets-man/private/stage",
    "publicationWorkRoot": "/srv/beets-man/private/work",
    "androidExports": []
  },
  "listener": {
    "address": "127.0.0.1",
    "port": 8443,
    "certificateThumbprint": "0000000000000000000000000000000000000000"
  },
  "credentials": {
    "clients": [{ "instanceId": "28800bc6-eb1d-4fa7-817d-546587e2eceb", "reference": "desktop-a" }],
    "receivers": [],
    "senders": []
  }
}
```

Use existing library and registered desktop IDs. Provision Linux Secret Service entries with attributes `application=beets-man` and `reference=<reference>`. On Windows, provision a Generic Credential named `BeetsMan/<reference>` with a UTF-16 password. The host reads client credentials at startup and checks each request against the instance's current registration. Restart the host after changing a client credential.

Each Android export adds an `androidExports` entry with `deviceId`, `folderId`, and a `sender` object containing the local Syncthing `endpoint`, `deviceId`, and dedicated `exportRoot`. Add a `receivers` entry keyed by `deviceId` and a `senders` entry keyed by `syncthingDeviceId`, each with its own `reference`. The host looks up those credentials on demand.

To pair an active Android registration before it has an export binding, set `receiverEndpoint` on its `androidExports` entry. `receiverRoot` can specify a dedicated receiver path, otherwise the host uses the approved profile's suggested root. Set `caseSensitivePaths` if the new receiver requires case-sensitive path comparisons. Existing bindings supply their registered endpoint if the host option is absent.

The optional `pcMirrors` array has `peerId`, `folderId`, `receiverEndpoint`, `receiverRoot`, `senderEndpoint`, and `senderDeviceId`. Use an active desktop registration with a matching native Syncthing ID. Add the peer's `receivers` reference and the sender's `senders` reference. The full library's configured `musicRoot` is the expected send-only folder path. The desktop accepts the matching receive-only folder at its configured root.

## State ownership and entry points

- **Bootstrap and preferences.** Resolve and validate local appsettings, the database path, the music root, and logging before opening normal services. Start a recovery shell with the available diagnostics if configuration is invalid. Settings handlers resolve local override, last valid shared default, then built-in value. Restart-bound values stay in bootstrap configuration. Registry-backed ordinary preference edits update the relevant projection.
- **Dispatch and presentation.** The bound MVVM Toolkit command submits one typed request with a correlation ID and cancellation. The handler returns a scoped result. The presentation projection accepts only the current page request, dialog owner or playback session, and posts every application-driven update through one Avalonia dispatcher adapter. Serialize writers by the state they own. Keep navigation responsive while host operations run.
- **Catalog, queue, and playlist drafts.** Catalog queries return stable artist, album, track, and playlist IDs. The playlist workspace owns drafts and their ordered IDs. The playback coordinator owns the active queue. A new playlist draft can start from an immutable queue snapshot. Playlist save carries the stable operation ID and expected revision when editing a saved playlist. A validation failure preserves the draft.
- **Playback, CLI and extensions.** Transport commands target the active playback session. Presentation mode references that session by identity. The native audio and configured CLI adapters implement capability-aware playback contracts. Switching backends preserves the queue and selected track and waits for Play. A separately trusted worker can contribute a visualization or submit a registered action through CQRS. The audio callback exchanges bounded state and samples, so worker activity and logging do not block it.
- **Guided import and tag editing.** The desktop owns the review draft and focused dialog. The host accepts uploaded copies into staging, assigns stable job identity, journals before invoking beets, reconciles actual outcomes, and publishes the catalog revision and notifications together. The desktop can navigate while waiting and apply the job result when the matching draft is open. Use the same beets job boundary for tag changes and reject a stale revision at the host.
- **Device export.** The Sync drop submits stable playlist and destination IDs. The host reads the saved playlist, resolves the selected player profile against the device inventory, and generates the playlist in its original order. `HostDevicePlaylistStager` keeps generated bytes outside the active share and commits the plan, missing-audio entries, and replayable receipt together. See [media layout and Android navigation](../stage-0/contracts.md#media-layout-and-android-navigation).
- **PC mirror control.** `HostPcMirrors` serves the authenticated media directory and routes the existing desktop CQRS commands through `SyncthingPcMirrorControl`. The adapter verifies the native IDs, full-library sender root and exact receive-only folder. It patches only the receiver's pause flag and rescans only that folder. LiteDB keeps the most recent verified file count while the mirror is paused and retains operation receipts across host restart.
- **Media share offer.** `SyncthingMediaShareOffer` verifies the registered native sender and receiver, then offers the configured full-library mirror or dedicated Android export on the sender. A PC mirror can add one recipient while preserving the earlier recipients and their settings. Android offers restrict the sender folder to the host and selected device. The receiving device accepts the folder through Syncthing. [Syncthing configuration API](https://docs.syncthing.net/rest/config.html)
- **Pairing approval.** `MediaSyncViewModel.ShareFilesCommand` dispatches `PairMediaPeer` through the desktop's authenticated host adapter. `HostPcMirrors` serializes it with the mirror operations. The host claims the proposal at its expected media directory revision, then calls `SyncthingMediaShareOffer`. Its LiteDB repository commits the approved binding and original response together. The paired Android layout becomes available to the existing layout and export-plan queries.
- **Android arrivals.** `SyncthingAndroidArrivalWorker` verifies the configured sender and advances through the native connection stream. Startup, native restart and a cursor gap establish a silent baseline from current connections. The authenticated host feed exposes confirmed live arrivals for current registrations. `AndroidArrivalMonitor` dispatches `ApplyAndroidPeerArrival` and loads the selected peer through `GetMediaSyncState`. A desktop feed reconnect also starts from a silent baseline.
- **Receiver inventory.** `SyncthingReceiverInventory` verifies the receiver identity and reads exact paths from its local index. For paired exports, inventory and transfer checks both require the approved receive-only folder, exact root, and approved sender and receiver before and after the index read. The media status query reports an accepted and unpaused folder as `Ready`. Unknown receiver state, ambiguous absence and occupied non-file paths prevent using those index results. [Syncthing configuration API](https://docs.syncthing.net/rest/config.html) · [Syncthing file index](https://docs.syncthing.net/rest/db-file-get.html)
- **Player layouts.** `AndroidLayoutProfiles` validates the versioned YAML and supported playlist format. The bundled Musicolet v2 and Poweramp v3 layouts use album-based paths and relative M3U8. A custom profile can select another registered format, including Poweramp PLS. `RegisteredDevicePlaylistResolver` joins the selected layout with the library-owned catalog and the registered receiver before staging. [Poweramp playlist formats](https://powerampapp.com/)
- **Device export reads.** `HostDeviceExportReader` reloads layout choices and a scoped plan over authenticated HTTPS. The desktop includes the selected profile version and optional plan ID. The host serves ordered persisted entries and checks both the caller and the current Android share before returning them.
- **Host composition.** `LibraryHostStartup` validates the configured music, private storage and Android export paths and registers all implemented playlist and device-export routes. The snapshot reader, file publisher and Syncthing sender share one configured library, device and folder binding. Caller authentication and sender and receiver credentials enter through separate services.
- **Export publication preview.** `FileDeviceExportSnapshotReader` inspects the configured share and hashes relevant file contents. `DeviceExportPublicationPlanner` compares the staged files with the prior ownership manifest and observed share contents. Ordinary publication preserves owned files outside the staged list. Layout reconciliation recognizes reviewed moves by stable source and content and requires explicit approval for standalone removals. A changed share registration, plan, manifest, or action set invalidates an earlier approval.
- **Export publication journal.** `IDeviceExportPublicationJournal` binds one active publication to the registered share and the originating operation. The host can reopen the same file actions after interruption. Verified step progress leaves prior ownership intact. After the final step, a single LiteDB transaction publishes the next ownership manifest and stores the original receipt.
- **File application.** `HostDeviceExportPublisher` rechecks the registered share and records each verified step. `FileDeviceExportStepWriter` retains the previous owned bytes in private work and checks source and destination digests before changing the share. Publication uses same-directory temporary files that Syncthing excludes from synchronization. Retrying an interrupted step checks its private record and resulting bytes before proceeding. [Syncthing temporary files](https://docs.syncthing.net/users/syncing#temporary-files)

## Application dependencies

Bootstrap and dispatch provide the foundation. The indexed catalog and stable media IDs support playlist drafts and playback. Settings, CLI strategies, and plugins depend on the typed preference, playback, or contribution boundaries they extend. Guided import uses the versioned host contract and journaled host mutation. Device exports use the catalog's ordered playlist and track identities.

Use the existing [FR, TR, and TEST traceability](../testing/ui/index.md) for each increment. Retain passing independent fakes and deliberate negative-control results before implementation, then bind the same assertions to real services. Run narrowed test groups after edits and the full applicable gates at phase exit. [Stage 0 implementation sequence](../stage-0/README.md#implementation-gate-sequence)
