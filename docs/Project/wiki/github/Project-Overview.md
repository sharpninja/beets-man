# Beets Man

Beets Man is a desktop music player and library manager designed around a beets library. The repository contains its Stage 0 requirements, architecture, reviewable wireframes, and the first .NET 10 presentation implementation. The desktop executable and library host are planned.

The design covers a Details grid for browsing, queue and playlist editing, playback and mixing, guided beets import, and per-device sync. Dragging a saved playlist to the Sync list stages the playlist in the selected player's format and adds only the audio missing from that device.

## Review the design

With Asset Review installed, run this from the repository root.

```powershell
asset-review --asset-root docs/design
```

- [Stage 0 scope and implementation order](docs/stage-0/README.md)
- [Architecture and C4 views](docs/architecture/README.md)
- [CQRS interfaces](docs/architecture/cqrs.md) and [LiteDB repositories](docs/architecture/persistence.md)
- [Toolkit ViewModels](docs/architecture/presentation.md) and [UI command catalog](docs/architecture/ui-commands.md)
- [Asset Review storyboard manifest](docs/design/_manifest.md)
- [Use cases](docs/use-cases/README.md) and [UI test specifications](docs/testing/ui/index.md)

## Build the implementation checkpoint

From the repository root with .NET 10 and PowerShell installed, compile the headless projects and run the isolated test suite through Nuke.

```powershell
pwsh -NoProfile -NonInteractive -File ./build.ps1 Compile
pwsh -NoProfile -NonInteractive -File ./build.ps1 Test
```

The checkpoint contains the typed CQRS request and result contracts, shared presentation dispatch and scheduling, and Toolkit bindings for the shell, bootstrap, diagnostics, and player. The command catalog also describes the remaining presentation work. Screens follow implementation of the ViewModels, commands, and application services.
