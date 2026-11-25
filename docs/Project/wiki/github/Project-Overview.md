# Beets Man

Beets Man is a desktop music player and library manager designed around a beets library. This .NET 10 checkpoint includes a runnable library host and the desktop application, presentation, and audio layers. The desktop screens are represented by reviewable wireframes.

Implemented workflows include catalog browsing, queue and playlist editing, playback and seeking, and authenticated media sync. The host offers Syncthing shares to registered PCs and Android devices. Staging a saved playlist produces the selected player's playlist format and stages only the audio missing from that device. Guided beets import and tag editing have CQRS and ViewModel contracts and are awaiting host workflow implementation.

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

From the repository root with .NET 10 and PowerShell installed, compile the projects and run the isolated test suite through Nuke.

```powershell
pwsh -NoProfile -NonInteractive -File ./build.ps1 Compile
pwsh -NoProfile -NonInteractive -File ./build.ps1 Test
```

Configure the host's local storage, TLS listener, and OS credential references before starting it. The [library host configuration guide](docs/architecture/code-structure.md#standalone-host) documents the launch command and the Android and PC share settings. The [UI command catalog](docs/architecture/ui-commands.md) maps desktop commands to their CQRS requests and results.
