# Beets Man v1 Stage 0

This workspace contains the current requirements and design for Beets Man v1. The first headless .NET projects define the typed application boundary and begin the desktop presentation implementation. The desktop executable and library host are planned. Asset Review approval applies to the individual wireframe states under review.

- [QBrain requirements payload and proposed mappings](requirements-candidate.json)
- [Structurizr workspace and architecture decisions](../architecture/README.md)
- [Rendered use-case diagrams and PlantUML sources](../use-cases/README.md)
- [Wireframe and storyboard traceability manifest](../ui-asset-manifest.json)
- [Asset Review catalog](../design/_manifest.md)
- [Test specifications](../testing/ui/index.md) and [asset traceability inventory](../testing/ui/coverage.md)
- [Local appsettings template contract](appsettings.template.json)
- [Interface and recovery decisions](contracts.md)
- [QBrain and design verification receipts](readback.md)

## Baseline and acceptance

The QBrain baseline uses effective layer `layer-1`. See the [requirement source payload](requirements-candidate.json) for current records and the [readback](readback.md) for historical counts and verification. TEST records ending in 001 identify isolated xUnit v3 cases. Those ending in 002 are Windows/Linux integration obligations. macOS is a target with authored-only tests and no release or HV gate.

The SVGs are draft wireframes. In Asset Review, each storyboard path is an ordered sequence of full-size wireframes. Open the review catalog from the repository root.

```sh
asset-review --asset-root docs/design
```

The [storyboard index](../testing/ui/coverage.md#storyboards) lists the action and visible result for each step. The [use-case index](../use-cases/README.md) connects all ten use cases to their wireframes and storyboard paths.

Library keeps collection navigation, the Details grid, and the Queue, Playlist, or Sync list in one window. Library, Tracks, Search, and saved playlists show the same default song columns in order: #, Title, Length, Rating, Contributing artist, Album, and Genre. Artists and Albums use album rows. Users can drag tracks or use **Add selected**, reorder a playlist, and save it beside the catalog. Dragging a saved playlist to Sync stages its playlist file in the selected player's format and any missing audio together. The preview shows all playlist entries in order and which audio files will transfer. Device preview and PC mirror management remain available from Sync.

Edit the [screen sources](../wireframes/screens/) for screen-specific changes and the [shared SVGs](../wireframes/shared/) for headers, navigation, transport, Library panels, dialog backdrops, and icons. The screen sources reference shared groups with `<use href="../shared/transport.svg#desktop-transport" />`. Group children inherit the screen’s font. Put any transforms or appearance unique to a component on its children.

Run `python docs/design/build_storyboards.py` after changing an SVG source or storyboard recipe. It expands shared references into self-contained review files in `docs/design` for Asset Review’s browser previews and preserves the authored graph in `_manifest.md`.

After the storyboard export, run `pwsh -NoProfile -NonInteractive -File docs/design/Update-LibraryGrids.ps1`. This applies the shared Library grid to the generated Track, Search, and Playlist states while carrying over each state's rows, selection, and cache status.

Run `pwsh -NoProfile -NonInteractive -File docs/design/Publish-StoryboardWireframes.ps1` to publish the authored frames in [wireframes/frames](../wireframes/frames/), including the playlist sync route. Their routes are defined in the [Asset Review manifest](../design/_manifest.md#stb10-pair-and-sync-an-android-device).

The stage remains open until branch-specific coverage is accepted, the proposed architecture and versioned interfaces are locked, and the independent review and operator approval are obtained.

## Implementation gate sequence

Start with [C4 and CQRS interfaces](../architecture/cqrs.md). Define [ViewModels](../architecture/presentation.md) and [UI commands](../architecture/ui-commands.md) against those contracts next. Implement screens last, using the approved Asset Review states for screen acceptance. The [code structure](../architecture/code-structure.md) assigns the corresponding project and state boundaries.

### Application capability order

1. Bootstrap configuration, rolling logs, typed Unit of Work dispatch, playback strategies, and the read-only catalog.
2. Playback presentation state, settings inheritance, command registry, shortcuts, and process-isolated extensions.
3. Authenticated beets operations and guided import with durable job recovery.
4. Record snapshots and changes, registration, offline replay, settings propagation, and durable notification fan-out.
5. PC mirroring, Android layouts and automatic Sync navigation, Windows/Linux packaging and physical Android validation.

For each slice, implement the named FR-only tests first. Use independent fakes and prove deliberate negative controls fail the expected assertions inside the green mock harness. Bind the same assertions to production. Use Nuke TestGroups after relevant edits and the full applicable gates at phase exit. Retain reports and paired review JSONL with the complete review in QBrain. A per-task and overall AGREE with at least 98% on both scores is required before marking work complete.
