# Stage 0 UI coverage inventory

The links below identify proposed design-to-test coverage. See [Stage 0 status](../../stage-0/README.md) for review and execution status.

- SCR01 [First Run](../../design/SCR01.svg) (screen). State SCR01.S0; controls SCR01.C01, SCR01.C02. `FR-CONFIG-001#ac-defaults`, `FR-CONFIG-001#ac-overrides`, `FR-CONFIG-001#ac-repair`, `FR-CONFIG-001#ac-logs` → `TEST-CONFIG-001`.
- SCR02 [Configuration Repair](../../design/SCR02.svg) (dialog). State SCR02.S0; controls SCR02.C01, SCR02.C02. `FR-CONFIG-001#ac-defaults`, `FR-CONFIG-001#ac-overrides`, `FR-CONFIG-001#ac-repair`, `FR-CONFIG-001#ac-logs` → `TEST-CONFIG-001`.
- SCR03 [Diagnostics](../../design/SCR03.svg) (tab). State SCR03.S0; controls SCR03.C01, SCR03.C02. `FR-CONFIG-001#ac-defaults`, `FR-CONFIG-001#ac-overrides`, `FR-CONFIG-001#ac-repair`, `FR-CONFIG-001#ac-logs` → `TEST-CONFIG-001`.
- SCR04 [Action Error](../../design/SCR04.svg) (dialog). State SCR04.S0; controls SCR04.C01, SCR04.C02. `FR-DISPATCH-001#ac-routing`, `FR-DISPATCH-001#ac-update`, `FR-DISPATCH-001#ac-responsive` → `TEST-DISPATCH-001`.
- SCR05 [Library](../../design/SCR05.svg) (screen). State SCR05.S0; controls SCR05.C01, SCR05.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR06 [Artists](../../design/SCR06.svg) (tab). State SCR06.S0; controls SCR06.C01, SCR06.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR07 [Albums](../../design/SCR07.svg) (tab). State SCR07.S0; controls SCR07.C01, SCR07.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR08 [Tracks](../../design/SCR08.svg) (tab). State SCR08.S0; controls SCR08.C01, SCR08.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR09 [Search](../../design/SCR09.svg) (tab). State SCR09.S0; controls SCR09.C01, SCR09.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR10 [Playlists](../../design/SCR10.svg) (tab). State SCR10.S0; controls SCR10.C01, SCR10.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR11 [Playlist Editor](../../design/SCR11.svg) (dialog). State SCR11.S0; controls SCR11.C01, SCR11.C02. `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` → `TEST-LIBRARY-001`.
- SCR12 [Now Playing](../../design/SCR12.svg) (tab). State SCR12.S0; controls SCR12.C01, SCR12.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR13 [Queue](../../design/SCR13.svg) (tab). State SCR13.S0; controls SCR13.C01, SCR13.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR14 [Mixer](../../design/SCR14.svg) (tab). State SCR14.S0; controls SCR14.C01, SCR14.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR15 [Equalizer](../../design/SCR15.svg) (dialog). State SCR15.S0; controls SCR15.C01, SCR15.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR16 [Minimum Player](../../design/SCR16.svg) (screen). State SCR16.S0; controls SCR16.C01, SCR16.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR17 [Bounded Visualization](../../design/SCR17.svg) (screen). State SCR17.S0; controls SCR17.C01, SCR17.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR18 [Full Visualization](../../design/SCR18.svg) (screen). State SCR18.S0; controls SCR18.C01, SCR18.C02. `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes` → `TEST-PLAYER-001`.
- SCR19 [Playback Settings](../../design/SCR19.svg) (tab). State SCR19.S0; controls SCR19.C01, SCR19.C02. `FR-CLI-001#ac-profiles`, `FR-CLI-001#ac-switch` → `TEST-CLI-001`.
- SCR20 [CLI Profile](../../design/SCR20.svg) (dialog). State SCR20.S0; controls SCR20.C01, SCR20.C02. `FR-CLI-001#ac-profiles`, `FR-CLI-001#ac-switch` → `TEST-CLI-001`.
- SCR21 [Backend Switch](../../design/SCR21.svg) (dialog). State SCR21.S0; controls SCR21.C01, SCR21.C02. `FR-CLI-001#ac-profiles`, `FR-CLI-001#ac-switch` → `TEST-CLI-001`.
- SCR22 [Plugins](../../design/SCR22.svg) (tab). State SCR22.S0; controls SCR22.C01, SCR22.C02. `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` → `TEST-PLUGINS-001`.
- SCR23 [Plugin Trust](../../design/SCR23.svg) (dialog). State SCR23.S0; controls SCR23.C01, SCR23.C02. `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` → `TEST-PLUGINS-001`.
- SCR24 [Plugin Diagnostics](../../design/SCR24.svg) (dialog). State SCR24.S0; controls SCR24.C01, SCR24.C02. `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` → `TEST-PLUGINS-001`.
- SCR25 [Visualization Picker](../../design/SCR25.svg) (dialog). State SCR25.S0; controls SCR25.C01, SCR25.C02. `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` → `TEST-PLUGINS-001`.
- SCR26 [Extension Panel](../../design/SCR26.svg) (tab). State SCR26.S0; controls SCR26.C01, SCR26.C02. `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` → `TEST-PLUGINS-001`.
- SCR27 [Settings](../../design/SCR27.svg) (screen). State SCR27.S0; controls SCR27.C01, SCR27.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR28 [Appearance](../../design/SCR28.svg) (tab). State SCR28.S0; controls SCR28.C01, SCR28.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR29 [Library Settings](../../design/SCR29.svg) (tab). State SCR29.S0; controls SCR29.C01, SCR29.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR30 [Sync Settings](../../design/SCR30.svg) (tab). State SCR30.S0; controls SCR30.C01, SCR30.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR31 [Notifications Settings](../../design/SCR31.svg) (tab). State SCR31.S0; controls SCR31.C01, SCR31.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR32 [Shortcuts](../../design/SCR32.svg) (tab). State SCR32.S0; controls SCR32.C01, SCR32.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR33 [Shortcut Conflict](../../design/SCR33.svg) (dialog). State SCR33.S0; controls SCR33.C01, SCR33.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR34 [Confirm Reset](../../design/SCR34.svg) (dialog). State SCR34.S0; controls SCR34.C01, SCR34.C02. `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` → `TEST-SETTINGS-001`.
- SCR35 [Library Setup](../../design/SCR35.svg) (dialog). State SCR35.S0; controls SCR35.C01, SCR35.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR36 [Import Source](../../design/SCR36.svg) (dialog). State SCR36.S0; controls SCR36.C01, SCR36.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR37 [Autotag Review](../../design/SCR37.svg) (dialog). State SCR37.S0; controls SCR37.C01, SCR37.C02, SCR37.C03. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR38 [Library Jobs](../../design/SCR38.svg) (tab). State SCR38.S0; controls SCR38.C01, SCR38.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR39 [Edit Tags](../../design/SCR39.svg) (dialog). State SCR39.S0; controls SCR39.C01, SCR39.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR40 [Organize Preview](../../design/SCR40.svg) (dialog). State SCR40.S0; controls SCR40.C01, SCR40.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR41 [Remove Music](../../design/SCR41.svg) (dialog). State SCR41.S0; controls SCR41.C01, SCR41.C02. `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` → `TEST-BEETS-001`.
- SCR42 [Registered Instances](../../design/SCR42.svg) (tab). State SCR42.S0; controls SCR42.C01, SCR42.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR43 [Register Instance](../../design/SCR43.svg) (dialog). State SCR43.S0; controls SCR43.C01, SCR43.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR44 [Revoke Instance](../../design/SCR44.svg) (dialog). State SCR44.S0; controls SCR44.C01, SCR44.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR45 [Record Sync Status](../../design/SCR45.svg) (tab). State SCR45.S0; controls SCR45.C01, SCR45.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR46 [Resolve Offline Edit](../../design/SCR46.svg) (dialog). State SCR46.S0; controls SCR46.C01, SCR46.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR47 [Snapshot Recovery](../../design/SCR47.svg) (dialog). State SCR47.S0; controls SCR47.C01, SCR47.C02. `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` → `TEST-RECORDS-001`.
- SCR48 [Sync](../../design/SCR48.svg) (screen). State SCR48.S0; controls SCR48.C01, SCR48.C02, SCR48.C03. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR49 [PC Mirrors](../../design/SCR49.svg) (tab). State SCR49.S0; controls SCR49.C01, SCR49.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR50 [Android Devices](../../design/SCR50.svg) (tab). State SCR50.S0; controls SCR50.C01, SCR50.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR51 [Pair Media Peer](../../design/SCR51.svg) (dialog). State SCR51.S0; controls SCR51.C01, SCR51.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR52 [Android Layout](../../design/SCR52.svg) (dialog). State SCR52.S0; controls SCR52.C01, SCR52.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR53 [Export Preview](../../design/SCR53.svg) (dialog). State SCR53.S0; controls SCR53.C01, SCR53.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR54 [Confirm Export Removal](../../design/SCR54.svg) (dialog). State SCR54.S0; controls SCR54.C01, SCR54.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.
- SCR55 [Transfer Error](../../design/SCR55.svg) (dialog). State SCR55.S0; controls SCR55.C01, SCR55.C02. `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` → `TEST-FILESYNC-001`.

### Playlist drop into Sync

`UC-FILESYNC-001` · `FR-FILESYNC-001#ac-playlist-drop` → `TEST-FILESYNC-001`. Follow **Drag a saved playlist** in the [STB10 Asset Review graph](../../design/_manifest.md#stb10-pair-and-sync-an-android-device).

- [STB10-21 · SCR08](../../design/frames/STB10/21-playlist-dragging-to-sync.svg) — Drag Night Drive from Saved Playlists. The Sync list highlights the drop target for Pixel 6.
- [STB10-22 · SCR08](../../design/frames/STB10/22-playlist-staged-for-sync.svg) — Drop the playlist. The Sync list contains the M3U8 file and two missing audio files.
- [STB10-23 · SCR53](../../design/frames/STB10/23-playlist-sync-export-preview.svg) — Open the preview. All five entries appear in playlist order with device-relative references and individual transfer status. Cancel returns to the staged list.
- [STB10-24 · SCR50](../../design/frames/STB10/24-playlist-sync-transferring.svg) — Start the sync. Device progress counts the three staged files.
- [STB10-25 · SCR50](../../design/frames/STB10/25-playlist-sync-complete.svg) — Wait for the sync. The playlist and all five tracks are available on Pixel 6.

The linked test specification also covers all tracks already present, overlapping drops, and two player formats.

## Storyboards

Each path lists its UI states in order. The Asset Review manifest connects these paths through choices and loops.

### STB01 Configure this desktop

Use case `UC-CONFIG-001` · `FR-CONFIG-001#ac-defaults`, `FR-CONFIG-001#ac-overrides`, `FR-CONFIG-001#ac-repair`, `FR-CONFIG-001#ac-logs` · `TEST-CONFIG-001`, `TEST-CONFIG-002`

**First run and applied override**

- [STB01-1 · SCR01](../../design/frames/STB01/01-first-run.svg) — Launch without a configuration file. The first-run page shows defaults for configuration, database and music.
- [STB01-2 · SCR01](../../design/frames/STB01/02-creating-configuration.svg) — Choose Use defaults. First Run shows that the local configuration and catalog are being created.
- [STB01-3 · SCR03](../../design/frames/STB01/03-default-settings-applied.svg) — Open Diagnostics after startup. The local cache is open and the startup event lists effective defaults.
- [STB01-4 · SCR03](../../design/frames/STB01/04-music-root-after-restart.svg) — Change MusicRoot locally and restart. Diagnostics shows the new resolved music folder and a new configuration event.

**Choose custom storage locations**

- [STB01-1 · SCR01](../../design/frames/STB01/01-first-run.svg) — Launch without a configuration file. The first-run page shows defaults for configuration, database and music.
- [STB01-5 · SCR01](../../design/frames/STB01/05-choose-custom-locations.svg) — Choose locations. First Run opens editable path fields and asks for approval to create the missing local database.
- [STB01-6 · SCR01](../../design/frames/STB01/06-custom-database-approved.svg) — Enter custom paths and approve database creation. Use these locations becomes available after explicit approval.
- [STB01-7 · SCR03](../../design/frames/STB01/07-custom-locations-applied.svg) — Choose Use these locations. Startup creates the catalog at the approved path and uses the selected music folder.

### STB02 Execute a desktop action

Use case `UC-DISPATCH-001` · `FR-DISPATCH-001#ac-routing`, `FR-DISPATCH-001#ac-update`, `FR-DISPATCH-001#ac-responsive` · `TEST-DISPATCH-001`, `TEST-DISPATCH-002`

**Dispatch and validation result**

- [STB02-1 · SCR05](../../design/frames/STB02/01-library-open.svg) — Open Library. The desktop shows the catalog and continues to accept navigation.
- [STB02-2 · SCR10](../../design/frames/STB02/02-playlist-ready.svg) — Open Playlists. The saved playlists and inline list pane stay in the Library workspace.
- [STB02-3 · SCR10](../../design/frames/STB02/03-invalid-playlist-draft.svg) — Choose New playlist and leave Name empty. The list pane shows the empty name field and ordered tracks beside the catalog.
- [STB02-4 · SCR10](../../design/frames/STB02/04-submitting-playlist.svg) — Choose Save playlist. The list pane shows the pending request and prevents a second submission.
- [STB02-5 · SCR04](../../design/frames/STB02/05-validation-displayed.svg) — Apply the result on the UI dispatcher. Action Error shows the validation message over the inline draft.

**Navigate while an import runs**

- [STB02-6 · SCR38](../../design/frames/STB02/06-long-import-running.svg) — Start the staged import and open Library jobs. Job #27 is selected and upload progress is visible.
- [STB02-7 · SCR09](../../design/frames/STB02/07-search-during-import.svg) — Switch to Search while the job runs. The new view responds and catalog matches remain available.
- [STB02-8 · SCR38](../../design/frames/STB02/08-import-ready-for-review.svg) — Return to Library jobs after upload. The import has reached Match review without blocking Search.

**Dismiss validation and return to the draft**

- [STB02-1 · SCR05](../../design/frames/STB02/01-library-open.svg) — Open Library. The desktop shows the catalog and continues to accept navigation.
- [STB02-2 · SCR10](../../design/frames/STB02/02-playlist-ready.svg) — Open Playlists. The saved playlists and inline list pane stay in the Library workspace.
- [STB02-3 · SCR10](../../design/frames/STB02/03-invalid-playlist-draft.svg) — Choose New playlist and leave Name empty. The list pane shows the empty name field and ordered tracks beside the catalog.
- [STB02-4 · SCR10](../../design/frames/STB02/04-submitting-playlist.svg) — Choose Save playlist. The list pane shows the pending request and prevents a second submission.
- [STB02-5 · SCR04](../../design/frames/STB02/05-validation-displayed.svg) — Apply the result on the UI dispatcher. Action Error shows the validation message over the inline draft.
- [STB02-9 · SCR10](../../design/frames/STB02/09-validation-dismissed-draft-retained.svg) — Choose Dismiss. The inline playlist draft keeps its track order and focuses its empty name for correction.

### STB03 Browse the music catalog

Use case `UC-LIBRARY-001` · `FR-LIBRARY-001#ac-browse`, `FR-LIBRARY-001#ac-offline` · `TEST-LIBRARY-001`, `TEST-LIBRARY-002`

**Browse and play a catalog track**

- [STB03-1 · SCR05](../../design/frames/STB03/01-library-overview.svg) — Open Library. The sidebar, catalog, and right-hand queue are visible together.
- [STB03-2 · SCR06](../../design/frames/STB03/02-artist-selected.svg) — Choose Artists and select Lena Vale. Lena Vale’s albums appear in the catalog while the queue stays visible.
- [STB03-3 · SCR07](../../design/frames/STB03/03-album-selected.svg) — Choose Albums and select Northern Lines. Northern Lines is highlighted in the album grid beside the same queue.
- [STB03-4 · SCR09](../../design/frames/STB03/04-matching-results.svg) — Search for Lena. Search results replace the catalog while the queue stays visible.
- [STB03-5 · SCR08](../../design/frames/STB03/05-local-track-selected.svg) — Choose Tracks and select Harbor Lights and Night Crossing. Both tracks are selected in the catalog. The Queue, Playlist, and Sync tabs stay available.
- [STB03-6 · SCR12](../../design/frames/STB03/06-selected-track-playing.svg) — Choose Play track. Now Playing shows the selected title, waveform and active transport.

**Open the playlist pane and add tracks**

- [STB03-7 · SCR08](../../design/frames/STB03/07-save-to-playlist.svg) — Open Playlist in the list pane. The selected tracks stay visible and a new playlist can be named in the same window.
- [STB03-8 · SCR08](../../design/frames/STB03/08-playlist-draft.svg) — Drop the tracks into the playlist. Both tracks appear in Playlist order and Save playlist becomes available.

**Build, reorder, and save a list**

- [STB03-1 · SCR05](../../design/frames/STB03/01-library-overview.svg) — Open Library. The sidebar, catalog, and right-hand queue are visible together.
- [STB03-2 · SCR06](../../design/frames/STB03/02-artist-selected.svg) — Choose Artists and select Lena Vale. Lena Vale’s albums appear in the catalog while the queue stays visible.
- [STB03-3 · SCR07](../../design/frames/STB03/03-album-selected.svg) — Choose Albums and select Northern Lines. Northern Lines is highlighted in the album grid beside the same queue.
- [STB03-4 · SCR09](../../design/frames/STB03/04-matching-results.svg) — Search for Lena. Search results replace the catalog while the queue stays visible.
- [STB03-5 · SCR08](../../design/frames/STB03/05-local-track-selected.svg) — Choose Tracks and select Harbor Lights and Night Crossing. Both tracks are selected in the catalog. The Queue, Playlist, and Sync tabs stay available.
- [STB03-7 · SCR08](../../design/frames/STB03/07-save-to-playlist.svg) — Open Playlist in the list pane. The selected tracks stay visible and a new playlist can be named in the same window.
- [STB03-9 · SCR08](../../design/frames/STB03/09-playlist-tracks-dragging.svg) — Drag the selected tracks toward the Playlist pane. The Playlist drop target highlights while the selected tracks remain visible in the catalog.
- [STB03-8 · SCR08](../../design/frames/STB03/08-playlist-draft.svg) — Drop the tracks into the playlist. Both tracks appear in Playlist order and Save playlist becomes available.
- [STB03-10 · SCR08](../../design/frames/STB03/10-playlist-order-changed.svg) — Drag Night Crossing above Harbor Lights. The playlist shows the new order in the right pane.
- [STB03-11 · SCR08](../../design/frames/STB03/11-playlist-saved-inline.svg) — Choose Save playlist. Harbor Set appears under Saved playlists and the right pane shows the saved order.

**Add the selection to the queue beside the catalog**

- [STB03-1 · SCR05](../../design/frames/STB03/01-library-overview.svg) — Open Library. The sidebar, catalog, and right-hand queue are visible together.
- [STB03-5 · SCR08](../../design/frames/STB03/05-local-track-selected.svg) — Choose Tracks and select Harbor Lights and Night Crossing. Both tracks are selected in the catalog. The Queue, Playlist, and Sync tabs stay available.
- [STB03-12 · SCR08](../../design/frames/STB03/12-tracks-added-to-queue.svg) — Choose Add to queue. The selected tracks appear in Playing next without leaving the Tracks catalog.

**Save the queue as a playlist**

- [STB03-1 · SCR05](../../design/frames/STB03/01-library-overview.svg) — Open Library. The sidebar, catalog, and right-hand queue are visible together.
- [STB03-5 · SCR08](../../design/frames/STB03/05-local-track-selected.svg) — Choose Tracks and select Harbor Lights and Night Crossing. Both tracks are selected in the catalog. The Queue, Playlist, and Sync tabs stay available.
- [STB03-12 · SCR08](../../design/frames/STB03/12-tracks-added-to-queue.svg) — Choose Add to queue. The selected tracks appear in Playing next without leaving the Tracks catalog.
- [STB03-13 · SCR08](../../design/frames/STB03/13-queue-as-playlist-draft.svg) — Choose Save as playlist and name the list. Playlist order starts with the queue’s four tracks in the same order.
- [STB03-14 · SCR08](../../design/frames/STB03/14-queue-saved-as-playlist.svg) — Choose Save playlist. Evening Queue appears under Saved playlists with the original queue order.

### STB04 Play and mix music

Use case `UC-PLAYER-001` · `FR-PLAYER-001#ac-transport`, `FR-PLAYER-001#ac-mix`, `FR-PLAYER-001#ac-fade`, `FR-PLAYER-001#ac-modes`, `FR-PLAYER-001#ac-queue-autosave` · `TEST-PLAYER-001`, `TEST-PLAYER-002`

**Queue, mix, and change presentation**

- [STB04-1 · SCR12](../../design/frames/STB04/01-transport-playing.svg) — Start Harbor Lights. Now Playing displays active transport and the next track.
- [STB04-2 · SCR13](../../design/frames/STB04/02-queue-reordered.svg) — Move Low Current ahead of Night Crossing. The upcoming order changes while Harbor Lights continues.
- [STB04-3 · SCR14](../../design/frames/STB04/03-two-decks-cued.svg) — Cue Low Current on Deck B. Deck A plays while Deck B is cued at the start.
- [STB04-4 · SCR14](../../design/frames/STB04/04-manual-blend.svg) — Play Deck B and move the crossfader. Both decks play, the fader moves toward B and automatic fading gives way to manual control.
- [STB04-5 · SCR15](../../design/frames/STB04/05-master-eq.svg) — Open Master EQ. Ten bands and the bypass control are available for the mixed output.
- [STB04-6 · SCR17](../../design/frames/STB04/06-bounded-visualization.svg) — Open bounded visualization. Spectrum and transport remain in a resizable player window.
- [STB04-7 · SCR18](../../design/frames/STB04/07-full-visualization.svg) — Choose Full screen. Spectrum fills the display and keeps Pause and Exit full screen.
- [STB04-8 · SCR17](../../design/frames/STB04/08-visualization-restored.svg) — Press Escape. The prior bounded presentation returns with playback farther along.

**Seek from the shared player**

- [STB04-1 · SCR12](../../design/frames/STB04/01-transport-playing.svg) — Start Harbor Lights. Now Playing displays active transport and the next track.
- [STB04-2 · SCR13](../../design/frames/STB04/02-queue-reordered.svg) — Move Low Current ahead of Night Crossing. The upcoming order changes while Harbor Lights continues.
- [STB04-10 · SCR13](../../design/frames/STB04/10-playlist-queue-loaded.svg) — Load Evening Queue from Saved playlists. Queue shows the saved track order and the Auto-save playlist switch, initially off.
- [STB04-9 · SCR13](../../design/frames/STB04/09-shared-player-track-seek.svg) — Drag the shared player's seek thumb to 2:45. The elapsed time, thumb, and played portion update together. Evening Queue and its off switch stay visible.
- [STB04-11 · SCR13](../../design/frames/STB04/11-playlist-queue-auto-save-enabled.svg) — Turn on Auto-save playlist. Subsequent queue edits are eligible for auto-save to Evening Queue. Turning it off returns to STB04-9.

### STB05 Use an external playback strategy

Use case `UC-CLI-001` · `FR-CLI-001#ac-profiles`, `FR-CLI-001#ac-switch` · `TEST-CLI-001`, `TEST-CLI-002`

**Validate and activate the CLI engine**

- [STB05-1 · SCR19](../../design/frames/STB05/01-built-in-engine.svg) — Open Playback settings. SoundFlow is active and the CLI profile can be edited.
- [STB05-2 · SCR20](../../design/frames/STB05/02-cli-profile-draft.svg) — Open the Studio CLI profile. The form shows the executable, arguments and declared action mappings.
- [STB05-3 · SCR20](../../design/frames/STB05/03-cli-profile-validated.svg) — Choose Validate. The same form shows an accepted profile and which controls it supports.
- [STB05-4 · SCR21](../../design/frames/STB05/04-switch-preview.svg) — Choose Studio CLI. Switch preview shows the current and proposed engines and the loss of unsupported controls.
- [STB05-5 · SCR19](../../design/frames/STB05/05-cli-engine-awaits-play.svg) — Confirm Switch. Studio CLI becomes active, queue selection remains and playback awaits Play.
- [STB05-6 · SCR12](../../design/frames/STB05/06-cli-transport-playing.svg) — Choose Play. Harbor Lights plays through Studio CLI and transport reports playing.

### STB06 Install and run extensions

Use case `UC-PLUGINS-001` · `FR-PLUGINS-001#ac-manage`, `FR-PLUGINS-001#ac-visualize`, `FR-PLUGINS-001#ac-recovery` · `TEST-PLUGINS-001`, `TEST-PLUGINS-002`

**Install, trust and use an extension**

- [STB06-1 · SCR22](../../design/frames/STB06/01-package-selected.svg) — Choose Install package and select Listening Notes. Plugins lists the version as awaiting local trust.
- [STB06-2 · SCR23](../../design/frames/STB06/02-package-trust-review.svg) — Review package identity and permissions. Trust prompts for this version and this desktop.
- [STB06-3 · SCR22](../../design/frames/STB06/03-plugin-enabled.svg) — Choose Trust locally, then enable Listening Notes. The selected package shows Trusted and Running.
- [STB06-4 · SCR26](../../design/frames/STB06/04-approved-extension-panel.svg) — Open the extension contribution. The host shows the approved Listening Notes form and command.
- [STB06-5 · SCR26](../../design/frames/STB06/05-extension-command-complete.svg) — Run the note command. The same panel confirms that the note was saved for the current track.

### STB07 Set shared and local preferences

Use case `UC-SETTINGS-001` · `FR-SETTINGS-001#ac-precedence`, `FR-SETTINGS-001#ac-availability`, `FR-SETTINGS-001#ac-shortcuts` · `TEST-SETTINGS-001`, `TEST-SETTINGS-002`

**Override a shared preference and inherit it again**

- [STB07-1 · SCR27](../../design/frames/STB07/01-settings-categories.svg) — Open Settings. The categories show examples of effective value sources.
- [STB07-2 · SCR28](../../design/frames/STB07/02-theme-inherited.svg) — Open Appearance. Theme follows the system from the shared default.
- [STB07-3 · SCR28](../../design/frames/STB07/03-theme-draft.svg) — Choose Dark. The field and preview update while the saved source remains visible.
- [STB07-4 · SCR28](../../design/frames/STB07/04-theme-local-override.svg) — Choose Apply. Dark is the effective local override; the shared value is still Follow system.
- [STB07-5 · SCR28](../../design/frames/STB07/05-theme-inherited-again.svg) — Choose Inherit. Follow system is effective again and the source returns to Shared default.

**Reset local notification preferences**

- [STB07-6 · SCR31](../../design/frames/STB07/06-notifications-settings.svg) — Open Notifications. Toggle states and their effective sources are visible.
- [STB07-7 · SCR34](../../design/frames/STB07/07-reset-preview.svg) — Choose Reset. The confirmation lists the overrides and their inherited values.
- [STB07-8 · SCR31](../../design/frames/STB07/08-notification-defaults-restored.svg) — Confirm Reset. Both alerts turn on and their shared and built-in sources appear.

### STB08 Manage the host music library

Use case `UC-BEETS-001` · `FR-BEETS-001#ac-open`, `FR-BEETS-001#ac-import`, `FR-BEETS-001#ac-edit`, `FR-BEETS-001#ac-recover` · `TEST-BEETS-001`, `TEST-BEETS-002`

**Open the host and import an album**

- [STB08-1 · SCR29](../../design/frames/STB08/01-host-settings.svg) — Open Library settings. The desktop shows its local Music Root and the host import defaults.
- [STB08-2 · SCR35](../../design/frames/STB08/02-existing-library.svg) — Choose View configuration and select Open existing library. The existing host configuration is selected.
- [STB08-3 · SCR36](../../design/frames/STB08/03-select-import-files.svg) — Open the library and select the three FLAC files. The file list and staged upload destination are visible.
- [STB08-4 · SCR36](../../design/frames/STB08/04-upload-copies.svg) — Choose Upload copies. Two of three source copies have arrived in staging.
- [STB08-5 · SCR37](../../design/frames/STB08/05-review-autotag.svg) — Review the strongest candidate. Source and proposed tags can be compared before choosing Use match.
- [STB08-6 · SCR38](../../design/frames/STB08/06-import-published.svg) — Choose Use match and open Library jobs. Job #27 shows the published album and completed import.

**Edit an existing track**

- [STB08-7 · SCR39](../../design/frames/STB08/07-edit-existing-tags.svg) — Open Edit Tags for Harbor Lights. The current fields and expected revision 184 are visible.
- [STB08-8 · SCR39](../../design/frames/STB08/08-tag-draft-changed.svg) — Change Genre to Downtempo. The draft shows the new genre while retaining revision 184.
- [STB08-9 · SCR38](../../design/frames/STB08/09-tag-edit-published.svg) — Choose Save and inspect the job. The tag-update job is published and displays the new revision.

**Import using the original tags**

- [STB08-1 · SCR29](../../design/frames/STB08/01-host-settings.svg) — Open Library settings. The desktop shows its local Music Root and the host import defaults.
- [STB08-2 · SCR35](../../design/frames/STB08/02-existing-library.svg) — Choose View configuration and select Open existing library. The existing host configuration is selected.
- [STB08-3 · SCR36](../../design/frames/STB08/03-select-import-files.svg) — Open the library and select the three FLAC files. The file list and staged upload destination are visible.
- [STB08-4 · SCR36](../../design/frames/STB08/04-upload-copies.svg) — Choose Upload copies. Two of three source copies have arrived in staging.
- [STB08-5 · SCR37](../../design/frames/STB08/05-review-autotag.svg) — Review the strongest candidate. Source and proposed tags can be compared before choosing Use match.
- [STB08-10 · SCR38](../../design/frames/STB08/10-original-tags-imported.svg) — Choose Keep tags. Library jobs shows three imported tracks with their original embedded tags.

**Skip the album import**

- [STB08-1 · SCR29](../../design/frames/STB08/01-host-settings.svg) — Open Library settings. The desktop shows its local Music Root and the host import defaults.
- [STB08-2 · SCR35](../../design/frames/STB08/02-existing-library.svg) — Choose View configuration and select Open existing library. The existing host configuration is selected.
- [STB08-3 · SCR36](../../design/frames/STB08/03-select-import-files.svg) — Open the library and select the three FLAC files. The file list and staged upload destination are visible.
- [STB08-4 · SCR36](../../design/frames/STB08/04-upload-copies.svg) — Choose Upload copies. Two of three source copies have arrived in staging.
- [STB08-5 · SCR37](../../design/frames/STB08/05-review-autotag.svg) — Review the strongest candidate. Source and proposed tags can be compared before choosing Use match.
- [STB08-11 · SCR38](../../design/frames/STB08/11-album-import-skipped.svg) — Choose Skip. Library jobs records the skipped album and confirms that no catalog records were added.

### STB09 Sync catalog, listening records and defaults

Use case `UC-RECORDS-001` · `FR-RECORDS-001#ac-register`, `FR-RECORDS-001#ac-replicate`, `FR-RECORDS-001#ac-notify`, `FR-RECORDS-001#ac-offline` · `TEST-RECORDS-001`, `TEST-RECORDS-002`

**Register, replicate and resolve a conflict**

- [STB09-1 · SCR42](../../design/frames/STB09/01-first-desktop-registered.svg) — Confirm Studio desktop is already registered. Studio desktop is active and Travel laptop has yet to register.
- [STB09-2 · SCR43](../../design/frames/STB09/02-register-travel-laptop.svg) — Enter host, desktop name, and credential. The credential is masked and the host is ready to register Travel laptop.
- [STB09-3 · SCR42](../../design/frames/STB09/03-desktop-registered.svg) — Choose Register. Travel laptop shows Active alongside the other registered desktops.
- [STB09-4 · SCR11](../../design/frames/STB09/04-offline-playlist-edited.svg) — Edit Night Drive while Travel laptop is offline. The reordered local draft remains queued for sync.
- [STB09-5 · SCR45](../../design/frames/STB09/05-records-catching-up.svg) — Reconnect the offline desktop. The desktop shows its committed cursor, undelivered records, and two pending edits.
- [STB09-6 · SCR46](../../design/frames/STB09/06-compare-offline-draft.svg) — Open the conflicting playlist draft. Host and local track orders appear side by side.
- [STB09-7 · SCR45](../../design/frames/STB09/07-local-draft-reapplied.svg) — Choose Reapply local. Record sync advances to r190 and confirms the playlist was applied.

**Revoke a registered desktop**

- [STB09-8 · SCR42](../../design/frames/STB09/08-select-travel-laptop.svg) — Select Travel laptop in Instances. The active desktop is selected and Revoke is available.
- [STB09-9 · SCR44](../../design/frames/STB09/09-review-revocation.svg) — Choose Revoke. The dialog names the instance and explains loss of host access.
- [STB09-10 · SCR42](../../design/frames/STB09/10-desktop-revoked.svg) — Confirm Revoke. Travel laptop shows Revoked and its Revoke action becomes unavailable.

**Keep the host version during ordinary catch-up**

- [STB09-1 · SCR42](../../design/frames/STB09/01-first-desktop-registered.svg) — Confirm Studio desktop is already registered. Studio desktop is active and Travel laptop has yet to register.
- [STB09-2 · SCR43](../../design/frames/STB09/02-register-travel-laptop.svg) — Enter host, desktop name, and credential. The credential is masked and the host is ready to register Travel laptop.
- [STB09-3 · SCR42](../../design/frames/STB09/03-desktop-registered.svg) — Choose Register. Travel laptop shows Active alongside the other registered desktops.
- [STB09-4 · SCR11](../../design/frames/STB09/04-offline-playlist-edited.svg) — Edit Night Drive while Travel laptop is offline. The reordered local draft remains queued for sync.
- [STB09-5 · SCR45](../../design/frames/STB09/05-records-catching-up.svg) — Reconnect the offline desktop. The desktop shows its committed cursor, undelivered records, and two pending edits.
- [STB09-6 · SCR46](../../design/frames/STB09/06-compare-offline-draft.svg) — Open the conflicting playlist draft. Host and local track orders appear side by side.
- [STB09-11 · SCR45](../../design/frames/STB09/11-host-playlist-kept-after-catch-up.svg) — Choose Keep host. The host playlist is applied at the committed r189 cursor and the favorite change remains pending.

### STB10 Mirror media to PCs and Android

Use case `UC-FILESYNC-001` · `FR-FILESYNC-001#ac-mirror`, `FR-FILESYNC-001#ac-layouts`, `FR-FILESYNC-001#ac-safety`, `FR-FILESYNC-001#ac-arrival` · `TEST-FILESYNC-001`, `TEST-FILESYNC-002`

**Pair and sync an Android device**

- [STB10-1 · SCR48](../../design/frames/STB10/01-sync-overview.svg) — Open Sync. PC mirrors and the paired phone appear in the device list.
- [STB10-2 · SCR51](../../design/frames/STB10/02-pair-android-peer.svg) — Choose Pair peer and select Android export. The dialog shows the device ID and share destination.
- [STB10-3 · SCR52](../../design/frames/STB10/03-choose-android-layout.svg) — Accept the share and keep the Musicolette layout. The default Android profile, storage root, and relative playlist paths appear.
- [STB10-4 · SCR53](../../design/frames/STB10/04-publishable-export.svg) — Choose Preview. The preview shows device-owned changes and Publish becomes available when the collision is resolved.
- [STB10-5 · SCR54](../../design/frames/STB10/05-confirm-owned-removal.svg) — Choose Publish. The receiving device and the single owned path are listed for confirmation.
- [STB10-6 · SCR54](../../design/frames/STB10/06-owned-removal-approved.svg) — Check the acknowledgment. Confirm is enabled for the listed device-owned path.
- [STB10-7 · SCR50](../../design/frames/STB10/07-android-transferring.svg) — Confirm publication. The assigned layout remains visible beside transfer progress.
- [STB10-8 · SCR50](../../design/frames/STB10/08-android-transfer-complete.svg) — Wait for the phone to receive the export. Progress reaches 60 of 60 files.

**Pause and resume a PC mirror**

- [STB10-9 · SCR30](../../design/frames/STB10/09-sync-defaults.svg) — Open Sync settings. Device defaults and the local share status are visible.
- [STB10-1 · SCR48](../../design/frames/STB10/01-sync-overview.svg) — Open Sync. PC mirrors and the paired phone appear in the device list.
- [STB10-10 · SCR49](../../design/frames/STB10/10-mirror-transferring.svg) — Open PC Mirrors. Studio PC is transferring an accepted mirror share.
- [STB10-11 · SCR49](../../design/frames/STB10/11-mirror-paused.svg) — Pause the Studio PC mirror. The same card preserves progress at 78 files and offers Resume.
- [STB10-12 · SCR49](../../design/frames/STB10/12-mirror-resumed.svg) — Resume Studio PC. Progress advances from 78 to 84 files.

**Diagnose and retry a failed transfer**

- [STB10-1 · SCR48](../../design/frames/STB10/01-sync-overview.svg) — Open Sync. PC mirrors and the paired phone appear in the device list.
- [STB10-13 · SCR55](../../design/frames/STB10/13-receiving-folder-error.svg) — Open the Pixel 6 transfer error. The affected folder and last successful sync are visible.
- [STB10-14 · SCR50](../../design/frames/STB10/14-receiving-folder-restored.svg) — Restore folder access and choose Retry. The device resumes at 38 of 60 files.

**Build a sync list from the Library**

- [STB10-15 · SCR08](../../design/frames/STB10/15-sync-list-open.svg) — Open the Sync list in Library and select two tracks. Pixel 6 and the default Musicolette profile are visible beside the selected tracks.
- [STB10-16 · SCR08](../../design/frames/STB10/16-sync-tracks-dragging.svg) — Drag the selection toward the Sync list. The Sync drop target highlights while the selected tracks remain visible in the catalog.
- [STB10-17 · SCR08](../../design/frames/STB10/17-sync-list-populated.svg) — Drop the tracks into the Sync list. Both tracks are staged for Pixel 6 and Preview export becomes available.
- [STB10-18 · SCR53](../../design/frames/STB10/18-sync-list-export-preview.svg) — Choose Preview export. The preview shows the two selected device paths and offers Start sync or Cancel.
- [STB10-19 · SCR50](../../design/frames/STB10/19-sync-list-transferring.svg) — Choose Start sync. Android Devices shows the selected list transferring to Pixel 6.
- [STB10-20 · SCR50](../../design/frames/STB10/20-sync-list-complete.svg) — Wait for transfer to complete. Both selected tracks are available on Pixel 6.

### STB11 Configuration recovery

Use case `UC-CONFIG-001` · `FR-CONFIG-001#ac-repair` · `TEST-CONFIG-001`, `TEST-CONFIG-002`

**Repair a malformed configuration**

- [STB11-1 · SCR02](../../design/frames/STB11/01-malformed-config.svg) — Launch with malformed JSON. The repair dialog points to line 23, column 4.
- [STB11-2 · SCR02](../../design/frames/STB11/02-config-repaired-retry-pending.svg) — Open configuration, correct the file, and return. The dialog identifies the corrected location and offers Retry.
- [STB11-3 · SCR03](../../design/frames/STB11/03-existing-catalog-restored.svg) — Choose Retry. Diagnostics reports that the repaired configuration loaded and the original cache opened.

### STB12 Preserve the newer UI result

Use case `UC-DISPATCH-001` · `FR-DISPATCH-001#ac-update` · `TEST-DISPATCH-001`, `TEST-DISPATCH-002`

**Keep the newest search result**

- [STB12-1 · SCR09](../../design/frames/STB12/01-search-query-a.svg) — Search for Lena. The first asynchronous search is pending.
- [STB12-2 · SCR09](../../design/frames/STB12/02-search-query-b.svg) — Replace the query with Juniper. The search field shows Juniper while the latest request is pending.
- [STB12-3 · SCR09](../../design/frames/STB12/03-newest-search-results.svg) — Complete Juniper first and apply its result. Juniper results appear under the latest query.
- [STB12-4 · SCR09](../../design/frames/STB12/04-late-response-ignored.svg) — Deliver the older Lena response. The same Juniper result remains visible after the stale response is ignored.

### STB13 Browse without downloaded media

Use case `UC-LIBRARY-001` · `FR-LIBRARY-001#ac-offline` · `TEST-LIBRARY-001`, `TEST-LIBRARY-002`

**Browse offline and play a downloaded track**

- [STB13-1 · SCR08](../../design/frames/STB13/01-cache-open-offline.svg) — Disconnect the host. Tracks remains browsable from cache with a downloaded row selected.
- [STB13-2 · SCR08](../../design/frames/STB13/02-pending-track-disabled.svg) — Select Night Crossing. The Pending row is highlighted and Play track is disabled.
- [STB13-3 · SCR08](../../design/frames/STB13/03-downloaded-track-enabled.svg) — Select Harbor Lights. Selection returns to a downloaded row and Play track is enabled.
- [STB13-4 · SCR08](../../design/frames/STB13/04-downloaded-track-playing.svg) — Choose Play track. Harbor Lights gains a playing indicator and the pending track remains unavailable.

### STB14 Recover an automatic crossfade

Use case `UC-PLAYER-001` · `FR-PLAYER-001#ac-fade` · `TEST-PLAYER-001`, `TEST-PLAYER-002`

**Incoming deck failure**

- [STB14-1 · SCR14](../../design/frames/STB14/01-automatic-fade-ready.svg) — Cue Low Current and enable auto-fade. The fader is at Deck A and Low Current is cued.
- [STB14-2 · SCR14](../../design/frames/STB14/02-automatic-fade-active.svg) — Start the automatic crossfade. The fader advances as the incoming deck joins.
- [STB14-3 · SCR14](../../design/frames/STB14/03-deck-b-failed.svg) — Deck B reports a read error. The fader returns to Deck A, Harbor Lights keeps playing, and the error names Deck B.

**Manual takeover**

- [STB14-1 · SCR14](../../design/frames/STB14/01-automatic-fade-ready.svg) — Cue Low Current and enable auto-fade. The fader is at Deck A and Low Current is cued.
- [STB14-2 · SCR14](../../design/frames/STB14/02-automatic-fade-active.svg) — Start the automatic crossfade. The fader advances as the incoming deck joins.
- [STB14-4 · SCR14](../../design/frames/STB14/04-manual-fader-takeover.svg) — Move the fader by hand. Automatic updates stop and the fader stays at the manually selected position.

### STB15 Cancel or fail a CLI switch

Use case `UC-CLI-001` · `FR-CLI-001#ac-switch` · `TEST-CLI-001`, `TEST-CLI-002`

**Cancel a backend switch**

- [STB15-1 · SCR21](../../design/frames/STB15/01-switch-preview.svg) — Choose Studio CLI. Switch preview shows the current and proposed engines and the loss of unsupported controls.
- [STB15-2 · SCR19](../../design/frames/STB15/02-switch-canceled.svg) — Choose Cancel. The SoundFlow settings return with the active engine and queue intact.

**Recover from a CLI process failure**

- [STB15-1 · SCR21](../../design/frames/STB15/01-switch-preview.svg) — Choose Studio CLI. Switch preview shows the current and proposed engines and the loss of unsupported controls.
- [STB15-3 · SCR21](../../design/frames/STB15/03-switch-starting.svg) — Choose Switch. The dialog shows startup in progress and disables the Switch action.
- [STB15-4 · SCR21](../../design/frames/STB15/04-switch-process-failed.svg) — The process exits before it is ready. The dialog identifies the startup failure and says which track remains selected.

**Cancel after CLI startup fails**

- [STB15-1 · SCR21](../../design/frames/STB15/01-switch-preview.svg) — Choose Studio CLI. Switch preview shows the current and proposed engines and the loss of unsupported controls.
- [STB15-3 · SCR21](../../design/frames/STB15/03-switch-starting.svg) — Choose Switch. The dialog shows startup in progress and disables the Switch action.
- [STB15-4 · SCR21](../../design/frames/STB15/04-switch-process-failed.svg) — The process exits before it is ready. The dialog identifies the startup failure and says which track remains selected.
- [STB15-5 · SCR19](../../design/frames/STB15/05-failed-switch-canceled.svg) — Choose Cancel after the CLI startup error. Playback Settings keeps SoundFlow selected, retains the queue, and waits for an explicit Play command.

### STB16 Recover from a stopped plugin worker

Use case `UC-PLUGINS-001` · `FR-PLUGINS-001#ac-recovery` · `TEST-PLUGINS-001`, `TEST-PLUGINS-002`

**Detach a stopped worker and keep playback**

- [STB16-1 · SCR25](../../design/frames/STB16/01-built-in-visualization-selected.svg) — Open Visualization Picker. The built-in Spectrum is selected and has a live preview.
- [STB16-2 · SCR25](../../design/frames/STB16/02-plugin-visualization-selected.svg) — Choose Studio Spectrum. The visualization picker shows the plugin as available and previews it.
- [STB16-3 · SCR17](../../design/frames/STB16/03-plugin-visualization-playing.svg) — Apply the plugin visualization. The plugin spectrum is active and player transport is visible.
- [STB16-4 · SCR17](../../design/frames/STB16/04-plugin-visualization-detached.svg) — The visualization worker times out. The spectrum is detached and the track advances with transport still usable.
- [STB16-5 · SCR24](../../design/frames/STB16/05-worker-timeout-details.svg) — Open diagnostics. The stopped worker, correlation ID, and detached contribution are shown.

**Restart the worker from diagnostics**

- [STB16-1 · SCR25](../../design/frames/STB16/01-built-in-visualization-selected.svg) — Open Visualization Picker. The built-in Spectrum is selected and has a live preview.
- [STB16-2 · SCR25](../../design/frames/STB16/02-plugin-visualization-selected.svg) — Choose Studio Spectrum. The visualization picker shows the plugin as available and previews it.
- [STB16-3 · SCR17](../../design/frames/STB16/03-plugin-visualization-playing.svg) — Apply the plugin visualization. The plugin spectrum is active and player transport is visible.
- [STB16-4 · SCR17](../../design/frames/STB16/04-plugin-visualization-detached.svg) — The visualization worker times out. The spectrum is detached and the track advances with transport still usable.
- [STB16-5 · SCR24](../../design/frames/STB16/05-worker-timeout-details.svg) — Open diagnostics. The stopped worker, correlation ID, and detached contribution are shown.
- [STB16-6 · SCR24](../../design/frames/STB16/06-worker-restarting.svg) — Choose Restart worker. Diagnostics stays open and shows startup in progress while audio keeps playing.
- [STB16-7 · SCR24](../../design/frames/STB16/07-worker-restart-complete.svg) — Wait for the worker to report ready. Plugin Diagnostics shows a healthy heartbeat and a restored visualization contribution while playback continues.

### STB17 Resolve a shortcut conflict

Use case `UC-SETTINGS-001` · `FR-SETTINGS-001#ac-shortcuts` · `TEST-SETTINGS-001`, `TEST-SETTINGS-002`

**Cancel conflicting reassignment**

- [STB17-1 · SCR33](../../design/frames/STB17/01-existing-gesture-conflict.svg) — Capture Space for Toggle auto-fade. The dialog identifies the existing Play or pause binding.
- [STB17-2 · SCR32](../../design/frames/STB17/02-old-binding-retained.svg) — Choose Cancel. The shortcut table still assigns Space to Play or pause.

**Choose a valid gesture**

- [STB17-1 · SCR33](../../design/frames/STB17/01-existing-gesture-conflict.svg) — Capture Space for Toggle auto-fade. The dialog identifies the existing Play or pause binding.
- [STB17-3 · SCR32](../../design/frames/STB17/03-capture-replacement.svg) — Choose another. The reassignment panel waits for a replacement gesture.
- [STB17-4 · SCR32](../../design/frames/STB17/04-replacement-assigned.svg) — Enter Ctrl+Shift+F. The Player mapping is saved and the original Space binding remains available.

**Handle system registration denial**

- [STB17-5 · SCR32](../../design/frames/STB17/05-system-shortcuts-off.svg) — Open Shortcuts before opting in. In-app gestures are assigned and system playback keys are off.
- [STB17-6 · SCR32](../../design/frames/STB17/06-system-shortcut-denied.svg) — Enable system playback keys. The OS denial is visible and in-app shortcuts remain assigned.

### STB18 Cancel a move or delete only catalog records

Use case `UC-BEETS-001` · `FR-BEETS-001#ac-edit` · `TEST-BEETS-001`, `TEST-BEETS-002`

**Cancel an organize preview**

- [STB18-1 · SCR40](../../design/frames/STB18/01-moves-ready.svg) — Open Organize Preview for the selected tracks. The source and destination paths are visible before any move.
- [STB18-2 · SCR08](../../design/frames/STB18/02-organize-canceled.svg) — Choose Cancel. The online track selection remains and the original file paths are unchanged.

**Remove the catalog records and retain audio**

- [STB18-3 · SCR41](../../design/frames/STB18/03-catalog-only-removal.svg) — Select Remove catalog records only. The dialog distinguishes catalog removal from deleting the audio.
- [STB18-4 · SCR08](../../design/frames/STB18/04-catalog-records-removed.svg) — Choose Confirm removal. The two selected titles are absent from the online catalog and the audio was retained.

**Select tracks, organize, and confirm**

- [STB18-5 · SCR08](../../design/frames/STB18/05-online-tracks-selected.svg) — Select Harbor Lights and Night Crossing in the online library. The same selection can launch Organize Preview or Remove Music.
- [STB18-1 · SCR40](../../design/frames/STB18/01-moves-ready.svg) — Open Organize Preview for the selected tracks. The source and destination paths are visible before any move.
- [STB18-6 · SCR08](../../design/frames/STB18/06-moves-confirmed.svg) — Choose Confirm moves. The same two records remain selected and the catalog shows their new host paths.

**Select tracks and cancel organize**

- [STB18-5 · SCR08](../../design/frames/STB18/05-online-tracks-selected.svg) — Select Harbor Lights and Night Crossing in the online library. The same selection can launch Organize Preview or Remove Music.
- [STB18-1 · SCR40](../../design/frames/STB18/01-moves-ready.svg) — Open Organize Preview for the selected tracks. The source and destination paths are visible before any move.
- [STB18-2 · SCR08](../../design/frames/STB18/02-organize-canceled.svg) — Choose Cancel. The online track selection remains and the original file paths are unchanged.

**Select tracks and remove catalog records**

- [STB18-5 · SCR08](../../design/frames/STB18/05-online-tracks-selected.svg) — Select Harbor Lights and Night Crossing in the online library. The same selection can launch Organize Preview or Remove Music.
- [STB18-3 · SCR41](../../design/frames/STB18/03-catalog-only-removal.svg) — Select Remove catalog records only. The dialog distinguishes catalog removal from deleting the audio.
- [STB18-4 · SCR08](../../design/frames/STB18/04-catalog-records-removed.svg) — Choose Confirm removal. The two selected titles are absent from the online catalog and the audio was retained.

### STB19 Recover an expired cursor and retain edits

Use case `UC-RECORDS-001` · `FR-RECORDS-001#ac-offline` · `TEST-RECORDS-001`, `TEST-RECORDS-002`

**Rebuild the snapshot and preserve the draft**

- [STB19-1 · SCR47](../../design/frames/STB19/01-expired-cursor.svg) — Reconnect with an expired cursor. Snapshot Recovery identifies the expired cursor and the two drafts that will be preserved.
- [STB19-2 · SCR47](../../design/frames/STB19/02-snapshot-downloading.svg) — Choose Refresh snapshot. The same dialog shows download progress and keeps the pending edits visible.
- [STB19-3 · SCR46](../../design/frames/STB19/03-conflict-after-snapshot.svg) — Finish rebuilding and replay pending edits. The conflicting playlist draft is preserved beside the latest host version.
- [STB19-4 · SCR45](../../design/frames/STB19/04-host-version-kept.svg) — Choose Keep host. The committed cursor reaches r189, with the favorite change still pending.

### STB20 Preview and recover a changed Android profile

Use case `UC-FILESYNC-001` · `FR-FILESYNC-001#ac-safety` · `TEST-FILESYNC-001`, `TEST-FILESYNC-002`

**Review layout and cancel removal**

- [STB20-1 · SCR52](../../design/frames/STB20/01-changed-android-profile.svg) — Choose a new profile for Pixel 6. The newly selected profile uses a versioned artist/album layout.
- [STB20-2 · SCR53](../../design/frames/STB20/02-layout-collision-preview.svg) — Choose Preview. The path comparison shows a collision blocking publication.
- [STB20-3 · SCR53](../../design/frames/STB20/03-layout-changes-validated.svg) — Resolve the collision in Export Preview. The new profile can publish after confirmation of the single owned removal.
- [STB20-4 · SCR54](../../design/frames/STB20/04-owned-path-confirmation.svg) — Choose Publish. Confirm Export Removal shows the path owned by Pixel 6.
- [STB20-5 · SCR50](../../design/frames/STB20/05-previous-export-retained.svg) — Choose Cancel. The device retains its previous export and layout assignment.

**Recover an interrupted publication**

- [STB20-3 · SCR53](../../design/frames/STB20/03-layout-changes-validated.svg) — Resolve the collision in Export Preview. The new profile can publish after confirmation of the single owned removal.
- [STB20-4 · SCR54](../../design/frames/STB20/04-owned-path-confirmation.svg) — Choose Publish. Confirm Export Removal shows the path owned by Pixel 6.
- [STB20-6 · SCR54](../../design/frames/STB20/06-approve-owned-removal.svg) — Check the acknowledgment. Confirm becomes available for the single owned path.
- [STB20-7 · SCR55](../../design/frames/STB20/07-publication-interrupted.svg) — Confirm and interrupt the publish. Transfer Error identifies the interrupted export and last successful sync.
- [STB20-8 · SCR50](../../design/frames/STB20/08-owned-paths-reconciled.svg) — Choose Retry. Android Devices shows the reconciled export and completed progress.

**Publish without interruption**

- [STB20-1 · SCR52](../../design/frames/STB20/01-changed-android-profile.svg) — Choose a new profile for Pixel 6. The newly selected profile uses a versioned artist/album layout.
- [STB20-2 · SCR53](../../design/frames/STB20/02-layout-collision-preview.svg) — Choose Preview. The path comparison shows a collision blocking publication.
- [STB20-3 · SCR53](../../design/frames/STB20/03-layout-changes-validated.svg) — Resolve the collision in Export Preview. The new profile can publish after confirmation of the single owned removal.
- [STB20-4 · SCR54](../../design/frames/STB20/04-owned-path-confirmation.svg) — Choose Publish. Confirm Export Removal shows the path owned by Pixel 6.
- [STB20-6 · SCR54](../../design/frames/STB20/06-approve-owned-removal.svg) — Check the acknowledgment. Confirm becomes available for the single owned path.
- [STB20-9 · SCR50](../../design/frames/STB20/09-export-published.svg) — Choose Confirm and let publication finish. Android Devices shows the new assigned layout and a completed export with the approved playlist path removed.

### STB21 Enter Sync from each player presentation

Use case `UC-FILESYNC-001` · `FR-FILESYNC-001#ac-arrival` · `TEST-FILESYNC-001`, `TEST-FILESYNC-002`

**Arrive from Normal**

- [STB21-1 · SCR12](../../design/frames/STB21/01-normal-before-arrival.svg) — Play Harbor Lights in Normal. The track is playing at 1:42.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Arrive from Minimum**

- [STB21-4 · SCR16](../../design/frames/STB21/04-minimum-before-arrival.svg) — Play Harbor Lights in Minimum. The compact window shows the same track and elapsed position.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Arrive from Bounded Visualization**

- [STB21-5 · SCR17](../../design/frames/STB21/05-bounded-before-arrival.svg) — Play Harbor Lights in bounded visualization. Spectrum and transport are visible while the track plays.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Arrive from Full Visualization**

- [STB21-6 · SCR18](../../design/frames/STB21/06-full-before-arrival.svg) — Play Harbor Lights in full visualization. Full-screen spectrum is active at 1:42.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Arrive with an open playlist draft**

- [STB21-7 · SCR11](../../design/frames/STB21/07-draft-before-android-arrival.svg) — Edit the unsaved playlist name. The editor contains the Night drive edits draft as playback continues.
- [STB21-8 · SCR48](../../design/frames/STB21/08-arrival-keeps-window-owned-draft.svg) — Receive the live connection while the editor is open. Sync selects Pixel 6 beneath the still-open draft. The draft and playback position are preserved.
- [STB21-9 · SCR48](../../design/frames/STB21/09-sync-visible-after-draft-saved.svg) — Choose Save. The editor closes to the selected phone and the track continues.

**Choose Normal before arrival**

- [STB21-10 · SCR12](../../design/frames/STB21/10-player-presentation-chooser.svg) — Open Presentation while Harbor Lights plays. The player exposes all four presentation modes while Library remains available for editing a playlist draft.
- [STB21-1 · SCR12](../../design/frames/STB21/01-normal-before-arrival.svg) — Play Harbor Lights in Normal. The track is playing at 1:42.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Choose Minimum before arrival**

- [STB21-10 · SCR12](../../design/frames/STB21/10-player-presentation-chooser.svg) — Open Presentation while Harbor Lights plays. The player exposes all four presentation modes while Library remains available for editing a playlist draft.
- [STB21-4 · SCR16](../../design/frames/STB21/04-minimum-before-arrival.svg) — Play Harbor Lights in Minimum. The compact window shows the same track and elapsed position.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Choose Bounded Visualization before arrival**

- [STB21-10 · SCR12](../../design/frames/STB21/10-player-presentation-chooser.svg) — Open Presentation while Harbor Lights plays. The player exposes all four presentation modes while Library remains available for editing a playlist draft.
- [STB21-5 · SCR17](../../design/frames/STB21/05-bounded-before-arrival.svg) — Play Harbor Lights in bounded visualization. Spectrum and transport are visible while the track plays.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Choose Full Visualization before arrival**

- [STB21-10 · SCR12](../../design/frames/STB21/10-player-presentation-chooser.svg) — Open Presentation while Harbor Lights plays. The player exposes all four presentation modes while Library remains available for editing a playlist draft.
- [STB21-6 · SCR18](../../design/frames/STB21/06-full-before-arrival.svg) — Play Harbor Lights in full visualization. Full-screen spectrum is active at 1:42.
- [STB21-2 · SCR48](../../design/frames/STB21/02-android-arrival-sync.svg) — Receive a live connection from Pixel 6. Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.

**Open a playlist draft before arrival**

- [STB21-10 · SCR12](../../design/frames/STB21/10-player-presentation-chooser.svg) — Open Presentation while Harbor Lights plays. The player exposes all four presentation modes while Library remains available for editing a playlist draft.
- [STB21-7 · SCR11](../../design/frames/STB21/07-draft-before-android-arrival.svg) — Edit the unsaved playlist name. The editor contains the Night drive edits draft as playback continues.
- [STB21-8 · SCR48](../../design/frames/STB21/08-arrival-keeps-window-owned-draft.svg) — Receive the live connection while the editor is open. Sync selects Pixel 6 beneath the still-open draft. The draft and playback position are preserved.
- [STB21-9 · SCR48](../../design/frames/STB21/09-sync-visible-after-draft-saved.svg) — Choose Save. The editor closes to the selected phone and the track continues.
- [STB21-3 · SCR48](../../design/frames/STB21/03-android-arrival-playback-continues.svg) — Let playback continue. The selected device stays open and elapsed playback advances.
