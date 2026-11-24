# Beets Man

Beets Man is a desktop music player and library manager designed around a beets library. The current workspace contains the Stage 0 requirements, architecture, and reviewable wireframes. The desktop application and library host are planned.

The design covers a Details grid for browsing, queue and playlist editing, playback and mixing, guided beets import, and per-device sync. Dragging a saved playlist to the Sync list stages the playlist in the selected player's format and adds only the audio missing from that device.

## Review the design

With Asset Review installed, run this from the repository root.

```powershell
asset-review --asset-root docs/design
```

- [Stage 0 scope and implementation order](docs/stage-0/README.md)
- [Architecture and C4 views](docs/architecture/README.md)
- [CQRS interfaces](docs/architecture/cqrs.md) and [LiteDB repositories](docs/architecture/persistence.md)
- [Asset Review storyboard manifest](docs/design/_manifest.md)
- [Use cases](docs/use-cases/README.md) and [UI test specifications](docs/testing/ui/index.md)

Implementation starts with the C4 and CQRS contracts. ViewModels and UI commands follow. Screens are implemented last and checked against the approved Asset Review states.
