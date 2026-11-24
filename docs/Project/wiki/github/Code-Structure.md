# Code structure and implementation order

1. Define the [C4 boundaries](workspace.dsl) and [CQRS interfaces](cqrs.md). Establish request types, state ownership, transaction boundaries, result handling, and test seams.
2. Define [ViewModels](presentation.md) and [UI commands](ui-commands.md) against those application contracts. Specify ownership, typed request/result bindings, and projection rules for completion, cancellation, conflicts, and errors.
3. Implement screens last. Bind views to the defined ViewModels and UI commands, then verify each implemented change against the approved Asset Review state and storyboard path.

## .NET solution and planned project boundaries

The current solution contains `BeetsMan.Desktop.Application`, `BeetsMan.Desktop`, and `BeetsMan.Desktop.Tests`. `build.ps1` runs `Compile`, `Test`, or `TestGroups` through Nuke. The remaining projects and the desktop executable are planned.

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

## State ownership and entry points

- **Bootstrap and preferences.** Resolve and validate local appsettings, the database path, the music root, and logging before opening normal services. Start a recovery shell with the available diagnostics if configuration is invalid. Settings handlers resolve local override, last valid shared default, then built-in value. Restart-bound values stay in bootstrap configuration. Registry-backed ordinary preference edits update the relevant projection.
- **Dispatch and presentation.** The bound MVVM Toolkit command submits one typed request with a correlation ID and cancellation. The handler returns a scoped result. The presentation projection accepts only the current page request, dialog owner or playback session, and posts every application-driven update through one Avalonia dispatcher adapter. Serialize writers by the state they own. Keep navigation responsive while host operations run.
- **Catalog, queue, and playlist drafts.** Catalog queries return stable artist, album, track, and playlist IDs. The playlist workspace owns drafts and their ordered IDs. The playback coordinator owns the active queue. A new playlist draft can start from an immutable queue snapshot. Playlist save carries the stable operation ID and expected revision when editing a saved playlist. A validation failure preserves the draft.
- **Playback, CLI and extensions.** Transport commands target the active playback session. Presentation mode references that session by identity. The native audio and configured CLI adapters implement capability-aware playback contracts. Switching backends preserves the queue and selected track and waits for Play. A separately trusted worker can contribute a visualization or submit a registered action through CQRS. The audio callback exchanges bounded state and samples, so worker activity and logging do not block it.
- **Guided import and tag editing.** The desktop owns the review draft and focused dialog. The host accepts uploaded copies into staging, assigns stable job identity, journals before invoking beets, reconciles actual outcomes, and publishes the catalog revision and notifications together. The desktop can navigate while waiting and apply the job result when the matching draft is open. Use the same beets job boundary for tag changes and reject a stale revision at the host.
- **Device export.** Keep playlist formatting and missing-audio decisions at the host's device export boundary. Any future Sync drop submits stable playlist and destination IDs. It can reuse the catalog, list pane, and host client seams. The playlist file references all ordered entries and the export manager stages missing audio for the selected device. See [media layout and Android navigation](../stage-0/contracts.md#media-layout-and-android-navigation).

## Application dependencies

Bootstrap and dispatch provide the foundation. The indexed catalog and stable media IDs support playlist drafts and playback. Settings, CLI strategies, and plugins depend on the typed preference, playback, or contribution boundaries they extend. Guided import uses the versioned host contract and journaled host mutation. Device exports use the catalog's ordered playlist and track identities.

Use the existing [FR, TR, and TEST traceability](../testing/ui/index.md) for each increment. Retain passing independent fakes and deliberate negative-control results before implementation, then bind the same assertions to real services. Run narrowed test groups after edits and the full applicable gates at phase exit. [Stage 0 implementation sequence](../stage-0/README.md#implementation-gate-sequence)
