# Beets Man

Beets Man is a desktop music player and library manager designed around a beets library. The verified .NET 10 pre-UI checkpoint includes a runnable library host, desktop application services, CommunityToolkit.Mvvm ViewModels and commands, and audio adapters. The screen designs are available as reviewable wireframes.

- Browse a local LiteDB catalog, edit playlists and queues, seek during playback, and opt in to resume a playlist after restart.
- Batch library edits locally, review the staged changes, and commit them for peer synchronization or revert the batch.
- Run guided imports with existing tags or an approved MusicBrainz match, edit track tags, and recover interrupted host jobs.
- Mirror media to registered PCs and stage Android playlists in the selected player's format with only the audio missing from the device.

The full pre-UI suites passed 377 tests on Linux and 374 on Windows. The [verification readback](docs/stage-0/readback.md#independent-review-remediation) records the test runs, native Syncthing transfer, and independent review. Screen implementation and Stage 0 design approval are separate checkpoints.

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

From the repository root with .NET 10 and PowerShell installed, compile the projects and run the unit and integration suites through Nuke.

```powershell
pwsh -NoProfile -NonInteractive -File ./build.ps1 Compile
pwsh -NoProfile -NonInteractive -File ./build.ps1 Test
pwsh -NoProfile -NonInteractive -File ./build.ps1 MigrationIntegrationTests
```

Configure the host's local storage, TLS listener, and OS credential references before starting it. The [library host configuration guide](docs/architecture/code-structure.md#standalone-host) documents the launch command and the Android and PC share settings. The [UI command catalog](docs/architecture/ui-commands.md) maps desktop commands to their CQRS requests and results.
