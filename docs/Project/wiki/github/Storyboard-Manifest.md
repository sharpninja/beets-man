# Asset Review Manifest

Beets Man Stage 0. Each storyboard path is an ordered sequence of full-size state wireframes.
The [UI coverage inventory](../testing/ui/coverage.md) describes the actions and visible results.

- `SCR01.svg`
- `SCR02.svg`
- `SCR03.svg`
- `SCR04.svg`
- `SCR05.svg`
- `SCR06.svg`
- `SCR07.svg`
- `SCR08.svg`
- `SCR09.svg`
- `SCR10.svg`
- `SCR11.svg`
- `SCR12.svg`
- `SCR13.svg`
- `SCR14.svg`
- `SCR15.svg`
- `SCR16.svg`
- `SCR17.svg`
- `SCR18.svg`
- `SCR19.svg`
- `SCR20.svg`
- `SCR21.svg`
- `SCR22.svg`
- `SCR23.svg`
- `SCR24.svg`
- `SCR25.svg`
- `SCR26.svg`
- `SCR27.svg`
- `SCR28.svg`
- `SCR29.svg`
- `SCR30.svg`
- `SCR31.svg`
- `SCR32.svg`
- `SCR33.svg`
- `SCR34.svg`
- `SCR35.svg`
- `SCR36.svg`
- `SCR37.svg`
- `SCR38.svg`
- `SCR39.svg`
- `SCR40.svg`
- `SCR41.svg`
- `SCR42.svg`
- `SCR43.svg`
- `SCR44.svg`
- `SCR45.svg`
- `SCR46.svg`
- `SCR47.svg`
- `SCR48.svg`
- `SCR49.svg`
- `SCR50.svg`
- `SCR51.svg`
- `SCR52.svg`
- `SCR53.svg`
- `SCR54.svg`
- `SCR55.svg`

### Sequence: STB01 First run and applied override {#stb01-first-run-and-applied-override}

- `frames/STB01/01-first-run.svg` {#stb01-1}
  - Branch: Use defaults -> stb01-2
  - Branch: Choose locations -> stb01-5
- `frames/STB01/02-creating-configuration.svg` {#stb01-2}
  - Next: stb01-3
- `frames/STB01/03-default-settings-applied.svg` {#stb01-3}
  - Next: stb01-4
- `frames/STB01/04-music-root-after-restart.svg` {#stb01-4}
- `frames/STB01/05-choose-custom-locations.svg` {#stb01-5}
  - Branch: Enter custom paths and approve database creation -> stb01-6
  - Loop: Back -> stb01-1
- `frames/STB01/06-custom-database-approved.svg` {#stb01-6}
  - Branch: Use these locations -> stb01-7
  - Loop: Back -> stb01-1
- `frames/STB01/07-custom-locations-applied.svg` {#stb01-7}

### Sequence: STB02 Dispatch and validation result {#stb02-dispatch-and-validation-result}

- `frames/STB02/01-library-open.svg` {#stb02-1}
  - Branch: Open Playlists to submit a draft -> stb02-2
  - Branch: Inspect a running import -> stb02-6
- `frames/STB02/02-playlist-ready.svg` {#stb02-2}
  - Next: stb02-3
- `frames/STB02/03-invalid-playlist-draft.svg` {#stb02-3}
  - Next: stb02-4
- `frames/STB02/04-submitting-playlist.svg` {#stb02-4}
  - Next: stb02-5
- `frames/STB02/05-validation-displayed.svg` {#stb02-5}
  - Branch: Dismiss and return to the draft -> stb02-9
  - Loop: Retry with the same invalid draft -> stb02-4
- `frames/STB02/06-long-import-running.svg` {#stb02-6}
  - Next: stb02-7
- `frames/STB02/07-search-during-import.svg` {#stb02-7}
  - Next: stb02-8
- `frames/STB02/08-import-ready-for-review.svg` {#stb02-8}

- `frames/STB02/09-validation-dismissed-draft-retained.svg` {#stb02-9}

### Sequence: STB02 Navigate while an import runs

- `frames/STB02/06-long-import-running.svg`
- `frames/STB02/07-search-during-import.svg`
- `frames/STB02/08-import-ready-for-review.svg`

### Sequence: STB03 Build a playlist or queue tracks in Library {#stb03-browse-and-play-a-catalog-track}

- `frames/STB03/01-library-overview.svg` {#stb03-1}
  - Branch: Open Tracks and select two tracks -> stb03-5
  - Branch: Browse by artist, album, or search -> stb03-2
- `frames/STB03/02-artist-selected.svg` {#stb03-2}
  - Next: stb03-3
- `frames/STB03/03-album-selected.svg` {#stb03-3}
  - Next: stb03-4
- `frames/STB03/04-matching-results.svg` {#stb03-4}
  - Next: stb03-5
- `frames/STB03/05-local-track-selected.svg` {#stb03-5}
  - Branch: Build a playlist -> stb03-7
  - Branch: Add to queue -> stb03-12
  - Branch: Play Harbor Lights -> stb03-6
- `frames/STB03/06-selected-track-playing.svg` {#stb03-6}
- `frames/STB03/07-save-to-playlist.svg` {#stb03-7}
  - Branch: Drag selected tracks -> stb03-9
  - Branch: Add selected -> stb03-8
- `frames/STB03/09-playlist-tracks-dragging.svg` {#stb03-9}
  - Next: stb03-8
- `frames/STB03/08-playlist-draft.svg` {#stb03-8}
  - Next: stb03-10
- `frames/STB03/10-playlist-order-changed.svg` {#stb03-10}
  - Next: stb03-11
- `frames/STB03/11-playlist-saved-inline.svg` {#stb03-11}
- `frames/STB03/12-tracks-added-to-queue.svg` {#stb03-12}
  - Next: stb03-13
- `frames/STB03/13-queue-as-playlist-draft.svg` {#stb03-13}
  - Next: stb03-14
- `frames/STB03/14-queue-saved-as-playlist.svg` {#stb03-14}

### Sequence: STB04 Queue, mix, and change presentation {#stb04-queue-mix-and-change-presentation}

- `frames/STB04/01-transport-playing.svg` {#stb04-1}
  - Next: stb04-2
- `frames/STB04/02-queue-reordered.svg` {#stb04-2}
  - Branch: Drag the shared seek thumb to 2:45 -> stb04-9
  - Branch: Continue to the mixer -> stb04-3
- `frames/STB04/03-two-decks-cued.svg` {#stb04-3}
  - Next: stb04-4
- `frames/STB04/04-manual-blend.svg` {#stb04-4}
  - Next: stb04-5
- `frames/STB04/05-master-eq.svg` {#stb04-5}
  - Next: stb04-6
- `frames/STB04/06-bounded-visualization.svg` {#stb04-6}
  - Next: stb04-7
- `frames/STB04/07-full-visualization.svg` {#stb04-7}
  - Next: stb04-8
- `frames/STB04/08-visualization-restored.svg` {#stb04-8}
- `frames/STB04/09-shared-player-track-seek.svg` {#stb04-9}

### Sequence: STB05 Validate and activate the CLI engine

- `frames/STB05/01-built-in-engine.svg`
- `frames/STB05/02-cli-profile-draft.svg`
- `frames/STB05/03-cli-profile-validated.svg`
- `frames/STB05/04-switch-preview.svg`
- `frames/STB05/05-cli-engine-awaits-play.svg`
- `frames/STB05/06-cli-transport-playing.svg`

### Sequence: STB06 Install, trust and use an extension

- `frames/STB06/01-package-selected.svg`
- `frames/STB06/02-package-trust-review.svg`
- `frames/STB06/03-plugin-enabled.svg`
- `frames/STB06/04-approved-extension-panel.svg`
- `frames/STB06/05-extension-command-complete.svg`

### Sequence: STB07 Override a shared preference and inherit it again {#stb07-override-a-shared-preference-and-inherit-it-again}

- `frames/STB07/01-settings-categories.svg` {#stb07-1}
  - Branch: Open Appearance -> stb07-2
  - Branch: Open Notifications -> stb07-6
- `frames/STB07/02-theme-inherited.svg` {#stb07-2}
  - Next: stb07-3
- `frames/STB07/03-theme-draft.svg` {#stb07-3}
  - Next: stb07-4
- `frames/STB07/04-theme-local-override.svg` {#stb07-4}
  - Next: stb07-5
- `frames/STB07/05-theme-inherited-again.svg` {#stb07-5}
  - Loop: Choose Dark again -> stb07-3
- `frames/STB07/06-notifications-settings.svg` {#stb07-6}
  - Next: stb07-7
- `frames/STB07/07-reset-preview.svg` {#stb07-7}
  - Next: stb07-8
- `frames/STB07/08-notification-defaults-restored.svg` {#stb07-8}

### Sequence: STB07 Reset local notification preferences

- `frames/STB07/06-notifications-settings.svg`
- `frames/STB07/07-reset-preview.svg`
- `frames/STB07/08-notification-defaults-restored.svg`

### Sequence: STB08 Open the host and import an album {#stb08-open-the-host-and-import-an-album}

- `frames/STB08/01-host-settings.svg` {#stb08-1}
  - Next: stb08-2
- `frames/STB08/02-existing-library.svg` {#stb08-2}
  - Branch: Open library and import an album -> stb08-3
  - Branch: Open library and edit an existing track -> stb08-7
- `frames/STB08/03-select-import-files.svg` {#stb08-3}
  - Next: stb08-4
- `frames/STB08/04-upload-copies.svg` {#stb08-4}
  - Next: stb08-5
- `frames/STB08/05-review-autotag.svg` {#stb08-5}
  - Branch: Use match -> stb08-6
  - Branch: Keep tags -> stb08-10
  - Branch: Skip -> stb08-11
- `frames/STB08/06-import-published.svg` {#stb08-6}
- `frames/STB08/07-edit-existing-tags.svg` {#stb08-7}
  - Next: stb08-8
- `frames/STB08/08-tag-draft-changed.svg` {#stb08-8}
  - Next: stb08-9
- `frames/STB08/09-tag-edit-published.svg` {#stb08-9}

- `frames/STB08/10-original-tags-imported.svg` {#stb08-10}
- `frames/STB08/11-album-import-skipped.svg` {#stb08-11}

### Sequence: STB08 Edit an existing track

- `frames/STB08/07-edit-existing-tags.svg`
- `frames/STB08/08-tag-draft-changed.svg`
- `frames/STB08/09-tag-edit-published.svg`

### Sequence: STB09 Register, replicate and resolve a conflict {#stb09-register-replicate-and-resolve-a-conflict}

- `frames/STB09/01-first-desktop-registered.svg` {#stb09-1}
  - Next: stb09-2
- `frames/STB09/02-register-travel-laptop.svg` {#stb09-2}
  - Next: stb09-3
- `frames/STB09/03-desktop-registered.svg` {#stb09-3}
  - Branch: Continue to offline replication -> stb09-4
  - Branch: Manage Travel laptop access -> stb09-8
- `frames/STB09/04-offline-playlist-edited.svg` {#stb09-4}
  - Next: stb09-5
- `frames/STB09/05-records-catching-up.svg` {#stb09-5}
  - Next: stb09-6
- `frames/STB09/06-compare-offline-draft.svg` {#stb09-6}
  - Branch: Keep host -> stb09-11
  - Branch: Reapply local -> stb09-7
- `frames/STB09/07-local-draft-reapplied.svg` {#stb09-7}
- `frames/STB09/08-select-travel-laptop.svg` {#stb09-8}
  - Next: stb09-9
- `frames/STB09/09-review-revocation.svg` {#stb09-9}
  - Branch: Confirm Revoke -> stb09-10
  - Loop: Cancel revocation -> stb09-8
- `frames/STB09/10-desktop-revoked.svg` {#stb09-10}

- `frames/STB09/11-host-playlist-kept-after-catch-up.svg` {#stb09-11}

### Sequence: STB09 Revoke a registered desktop

- `frames/STB09/08-select-travel-laptop.svg`
- `frames/STB09/09-review-revocation.svg`
- `frames/STB09/10-desktop-revoked.svg`

### Sequence: STB10 Build a sync list or manage device sync {#stb10-pair-and-sync-an-android-device}

- `frames/STB10/15-sync-list-open.svg` {#stb10-15}
  - Branch: Drag selected tracks -> stb10-16
  - Branch: Drag a saved playlist -> stb10-21
  - Branch: Add selected -> stb10-17
  - Branch: Manage devices and shares -> stb10-1
- `frames/STB10/16-sync-tracks-dragging.svg` {#stb10-16}
  - Next: stb10-17
- `frames/STB10/17-sync-list-populated.svg` {#stb10-17}
  - Next: stb10-18
- `frames/STB10/18-sync-list-export-preview.svg` {#stb10-18}
  - Branch: Start sync -> stb10-19
  - Loop: Cancel preview and edit the list -> stb10-17
- `frames/STB10/19-sync-list-transferring.svg` {#stb10-19}
  - Next: stb10-20
- `frames/STB10/20-sync-list-complete.svg` {#stb10-20}

- `frames/STB10/21-playlist-dragging-to-sync.svg` {#stb10-21}
  - Next: stb10-22
- `frames/STB10/22-playlist-staged-for-sync.svg` {#stb10-22}
  - Next: stb10-23
- `frames/STB10/23-playlist-sync-export-preview.svg` {#stb10-23}
  - Branch: Start playlist sync -> stb10-24
  - Loop: Cancel preview and edit the list -> stb10-22
- `frames/STB10/24-playlist-sync-transferring.svg` {#stb10-24}
  - Next: stb10-25
- `frames/STB10/25-playlist-sync-complete.svg` {#stb10-25}

- `frames/STB10/01-sync-overview.svg` {#stb10-1}
  - Branch: Pair an Android peer -> stb10-2
  - Branch: Open PC Mirrors -> stb10-10
  - Branch: Inspect a transfer error already reported for Pixel 6 -> stb10-13
  - Loop: Edit the sync list and select tracks -> stb10-15
- `frames/STB10/02-pair-android-peer.svg` {#stb10-2}
  - Next: stb10-3
- `frames/STB10/03-choose-android-layout.svg` {#stb10-3}
  - Next: stb10-4
- `frames/STB10/04-publishable-export.svg` {#stb10-4}
  - Next: stb10-5
- `frames/STB10/05-confirm-owned-removal.svg` {#stb10-5}
  - Next: stb10-6
- `frames/STB10/06-owned-removal-approved.svg` {#stb10-6}
  - Next: stb10-7
- `frames/STB10/07-android-transferring.svg` {#stb10-7}
  - Branch: Transfer completes -> stb10-8
  - Branch: Receiving folder becomes unavailable -> stb10-13
- `frames/STB10/08-android-transfer-complete.svg` {#stb10-8}
- `frames/STB10/10-mirror-transferring.svg` {#stb10-10}
  - Next: stb10-11
- `frames/STB10/11-mirror-paused.svg` {#stb10-11}
  - Next: stb10-12
- `frames/STB10/12-mirror-resumed.svg` {#stb10-12}
- `frames/STB10/13-receiving-folder-error.svg` {#stb10-13}
  - Next: stb10-14
- `frames/STB10/14-receiving-folder-restored.svg` {#stb10-14}
  - Next: stb10-8

### Sequence: STB10 Pause and resume a PC mirror

- `frames/STB10/09-sync-defaults.svg`
- `frames/STB10/01-sync-overview.svg`
- `frames/STB10/10-mirror-transferring.svg`
- `frames/STB10/11-mirror-paused.svg`
- `frames/STB10/12-mirror-resumed.svg`

### Sequence: STB10 Diagnose and retry a failed transfer

- `frames/STB10/01-sync-overview.svg`
- `frames/STB10/13-receiving-folder-error.svg`
- `frames/STB10/14-receiving-folder-restored.svg`

### Sequence: STB11 Repair a malformed configuration {#stb11-repair-a-malformed-configuration}

- `frames/STB11/01-malformed-config.svg` {#stb11-1}
  - Next: stb11-2
  - Loop: Retry while the JSON is still malformed -> stb11-1
- `frames/STB11/02-config-repaired-retry-pending.svg` {#stb11-2}
  - Next: stb11-3
- `frames/STB11/03-existing-catalog-restored.svg` {#stb11-3}

### Sequence: STB12 Keep the newest search result

- `frames/STB12/01-search-query-a.svg`
- `frames/STB12/02-search-query-b.svg`
- `frames/STB12/03-newest-search-results.svg`
- `frames/STB12/04-late-response-ignored.svg`

### Sequence: STB13 Browse offline and play a downloaded track {#stb13-browse-offline-and-play-a-downloaded-track}

- `frames/STB13/01-cache-open-offline.svg` {#stb13-1}
  - Branch: Inspect the pending track -> stb13-2
  - Branch: Play the selected downloaded track -> stb13-4
- `frames/STB13/02-pending-track-disabled.svg` {#stb13-2}
  - Next: stb13-3
- `frames/STB13/03-downloaded-track-enabled.svg` {#stb13-3}
  - Next: stb13-4
- `frames/STB13/04-downloaded-track-playing.svg` {#stb13-4}

### Sequence: STB14 Incoming deck failure {#stb14-incoming-deck-failure}

- `frames/STB14/01-automatic-fade-ready.svg` {#stb14-1}
  - Next: stb14-2
- `frames/STB14/02-automatic-fade-active.svg` {#stb14-2}
  - Branch: Deck B reports a read error -> stb14-3
  - Branch: Move the crossfader by hand -> stb14-4
- `frames/STB14/03-deck-b-failed.svg` {#stb14-3}
- `frames/STB14/04-manual-fader-takeover.svg` {#stb14-4}

### Sequence: STB14 Manual takeover

- `frames/STB14/01-automatic-fade-ready.svg`
- `frames/STB14/02-automatic-fade-active.svg`
- `frames/STB14/04-manual-fader-takeover.svg`

### Sequence: STB15 Cancel a backend switch {#stb15-cancel-a-backend-switch}

- `frames/STB15/01-switch-preview.svg` {#stb15-1}
  - Branch: Cancel -> stb15-2
  - Branch: Switch to Studio CLI -> stb15-3
- `frames/STB15/02-switch-canceled.svg` {#stb15-2}
- `frames/STB15/03-switch-starting.svg` {#stb15-3}
  - Branch: The CLI process exits before it is ready -> stb15-4
  - Branch: The CLI session starts successfully -> stb05-5
- `frames/STB15/04-switch-process-failed.svg` {#stb15-4}
  - Branch: Cancel and leave playback stopped -> stb15-5
  - Loop: Try Switch again -> stb15-3
- `frames/STB15/05-failed-switch-canceled.svg` {#stb15-5}
- `frames/STB05/05-cli-engine-awaits-play.svg` {#stb05-5}
  - Next: stb05-6
- `frames/STB05/06-cli-transport-playing.svg` {#stb05-6}

### Sequence: STB15 Recover from a CLI process failure

- `frames/STB15/01-switch-preview.svg`
- `frames/STB15/03-switch-starting.svg`
- `frames/STB15/04-switch-process-failed.svg`

### Sequence: STB16 Detach a stopped worker and keep playback

- `frames/STB16/01-built-in-visualization-selected.svg`
- `frames/STB16/02-plugin-visualization-selected.svg`
- `frames/STB16/03-plugin-visualization-playing.svg`
- `frames/STB16/04-plugin-visualization-detached.svg`
- `frames/STB16/05-worker-timeout-details.svg`
- `frames/STB16/06-worker-restarting.svg`
- `frames/STB16/07-worker-restart-complete.svg`

### Sequence: STB17 Cancel conflicting reassignment {#stb17-cancel-conflicting-reassignment}

- `frames/STB17/01-existing-gesture-conflict.svg` {#stb17-1}
  - Branch: Cancel and retain the old binding -> stb17-2
  - Branch: Choose another gesture -> stb17-3
- `frames/STB17/02-old-binding-retained.svg` {#stb17-2}
- `frames/STB17/03-capture-replacement.svg` {#stb17-3}
  - Branch: Enter Ctrl+Shift+F -> stb17-4
  - Loop: Enter the conflicting Space gesture again -> stb17-1
- `frames/STB17/04-replacement-assigned.svg` {#stb17-4}

### Sequence: STB17 Choose a valid gesture

- `frames/STB17/01-existing-gesture-conflict.svg`
- `frames/STB17/03-capture-replacement.svg`
- `frames/STB17/04-replacement-assigned.svg`

### Sequence: STB17 Handle system registration denial

- `frames/STB17/05-system-shortcuts-off.svg`
- `frames/STB17/06-system-shortcut-denied.svg`

### Sequence: STB18 Organize or remove selected tracks {#stb18-cancel-an-organize-preview}

- `frames/STB18/05-online-tracks-selected.svg` {#stb18-5}
  - Branch: Organize files -> stb18-1
  - Branch: Remove music -> stb18-3
- `frames/STB18/01-moves-ready.svg` {#stb18-1}
  - Branch: Cancel -> stb18-2
  - Branch: Confirm moves -> stb18-6
- `frames/STB18/02-organize-canceled.svg` {#stb18-2}
  - Branch: Remove music -> stb18-3
  - Loop: Reopen Organize Preview -> stb18-1
- `frames/STB18/03-catalog-only-removal.svg` {#stb18-3}
  - Next: stb18-4
- `frames/STB18/04-catalog-records-removed.svg` {#stb18-4}
- `frames/STB18/06-moves-confirmed.svg` {#stb18-6}

### Sequence: STB18 Remove the catalog records and retain audio

- `frames/STB18/03-catalog-only-removal.svg`
- `frames/STB18/04-catalog-records-removed.svg`

### Sequence: STB19 Rebuild the snapshot and preserve the draft {#stb19-rebuild-the-snapshot-and-preserve-the-draft}

- `frames/STB19/01-expired-cursor.svg` {#stb19-1}
  - Next: stb19-2
- `frames/STB19/02-snapshot-downloading.svg` {#stb19-2}
  - Next: stb19-3
- `frames/STB19/03-conflict-after-snapshot.svg` {#stb19-3}
  - Branch: Keep host -> stb19-4
  - Branch: Reapply local -> stb09-7
- `frames/STB19/04-host-version-kept.svg` {#stb19-4}
- `frames/STB09/07-local-draft-reapplied.svg` {#stb09-7}

### Sequence: STB20 Review layout and cancel removal {#stb20-review-layout-and-cancel-removal}

- `frames/STB20/01-changed-android-profile.svg` {#stb20-1}
  - Next: stb20-2
- `frames/STB20/02-layout-collision-preview.svg` {#stb20-2}
  - Next: stb20-3
- `frames/STB20/03-layout-changes-validated.svg` {#stb20-3}
  - Next: stb20-4
- `frames/STB20/04-owned-path-confirmation.svg` {#stb20-4}
  - Branch: Cancel removal -> stb20-5
  - Branch: Acknowledge the owned path removal -> stb20-6
- `frames/STB20/05-previous-export-retained.svg` {#stb20-5}
  - Loop: Select layout and choose the proposed profile again -> stb20-1
- `frames/STB20/06-approve-owned-removal.svg` {#stb20-6}
  - Branch: Confirm and finish publishing -> stb20-9
  - Branch: Publication is interrupted -> stb20-7
- `frames/STB20/07-publication-interrupted.svg` {#stb20-7}
  - Next: stb20-8
- `frames/STB20/08-owned-paths-reconciled.svg` {#stb20-8}

- `frames/STB20/09-export-published.svg` {#stb20-9}

### Sequence: STB20 Recover an interrupted publication

- `frames/STB20/03-layout-changes-validated.svg`
- `frames/STB20/04-owned-path-confirmation.svg`
- `frames/STB20/06-approve-owned-removal.svg`
- `frames/STB20/07-publication-interrupted.svg`
- `frames/STB20/08-owned-paths-reconciled.svg`

### Sequence: STB21 Enter Sync from each player presentation {#stb21-arrive-from-normal}

- `frames/STB21/10-player-presentation-chooser.svg` {#stb21-10}
  - Branch: Choose Normal -> stb21-1
  - Branch: Choose Minimum -> stb21-4
  - Branch: Choose Bounded Visualization -> stb21-5
  - Branch: Choose Full Visualization -> stb21-6
  - Branch: Open Library and edit a playlist draft -> stb21-7
- `frames/STB21/01-normal-before-arrival.svg` {#stb21-1}
  - Next: stb21-2
- `frames/STB21/02-android-arrival-sync.svg` {#stb21-2}
  - Next: stb21-3
- `frames/STB21/03-android-arrival-playback-continues.svg` {#stb21-3}
- `frames/STB21/04-minimum-before-arrival.svg` {#stb21-4}
  - Next: stb21-2
- `frames/STB21/05-bounded-before-arrival.svg` {#stb21-5}
  - Next: stb21-2
- `frames/STB21/06-full-before-arrival.svg` {#stb21-6}
  - Next: stb21-2
- `frames/STB21/07-draft-before-android-arrival.svg` {#stb21-7}
  - Next: stb21-8
- `frames/STB21/08-arrival-keeps-window-owned-draft.svg` {#stb21-8}
  - Next: stb21-9
- `frames/STB21/09-sync-visible-after-draft-saved.svg` {#stb21-9}
  - Next: stb21-3

### Sequence: STB21 Arrive from Minimum

- `frames/STB21/04-minimum-before-arrival.svg`
- `frames/STB21/02-android-arrival-sync.svg`
- `frames/STB21/03-android-arrival-playback-continues.svg`

### Sequence: STB21 Arrive from Bounded Visualization

- `frames/STB21/05-bounded-before-arrival.svg`
- `frames/STB21/02-android-arrival-sync.svg`
- `frames/STB21/03-android-arrival-playback-continues.svg`

### Sequence: STB21 Arrive from Full Visualization

- `frames/STB21/06-full-before-arrival.svg`
- `frames/STB21/02-android-arrival-sync.svg`
- `frames/STB21/03-android-arrival-playback-continues.svg`

### Sequence: STB21 Arrive with an open playlist draft

- `frames/STB21/07-draft-before-android-arrival.svg`
- `frames/STB21/08-arrival-keeps-window-owned-draft.svg`
- `frames/STB21/09-sync-visible-after-draft-saved.svg`

