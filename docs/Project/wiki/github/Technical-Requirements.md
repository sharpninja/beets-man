# Technical Requirements (QBrain.AI)

## TR-BEETS-ARCH-001

**Manage the host music library architecture** — Authenticated host service, serialized journaled beets adapter, catalog reconciliation
**Covered by:** FR: FR-BEETS-001; TEST: TEST-BEETS-001, TEST-BEETS-002
**Status:** pending
Scope: layer-1+

## TR-CLI-ARCH-001

**Use an external playback strategy architecture** — Versioned YAML, direct process arguments, per-command and session strategies
**Covered by:** FR: FR-CLI-001; TEST: TEST-CLI-001, TEST-CLI-002
**Status:** pending
Scope: layer-1+

## TR-CONFIG-ARCH-001

**Configure this desktop architecture** — Typed JSON bootstrap, platform path resolver, and filtered ILogger providers
**Covered by:** FR: FR-CONFIG-001; TEST: TEST-CONFIG-001, TEST-CONFIG-002
**Status:** pending
Scope: layer-1+

## TR-DISPATCH-ARCH-001

**Execute a desktop action architecture** — Typed internal CQRS dispatcher, read projections, transaction ownership, and one Avalonia UIThread adapter
**Covered by:** FR: FR-DISPATCH-001; TEST: TEST-DISPATCH-001, TEST-DISPATCH-002
**Status:** pending
Scope: layer-1+

## TR-FILESYNC-ARCH-001

**Mirror media to PCs and Android architecture** — Syncthing REST/events and per-device send-only export trees
**Covered by:** FR: FR-FILESYNC-001; TEST: TEST-FILESYNC-001, TEST-FILESYNC-002
**Status:** pending
Scope: layer-1+

## TR-LIBRARY-ARCH-001

**Browse the music catalog architecture** — Indexed LiteDB read projections and path mapping under the configured local root
**Covered by:** FR: FR-LIBRARY-001; TEST: TEST-LIBRARY-001, TEST-LIBRARY-002
**Status:** pending
Scope: layer-1+

## TR-PLAYER-ARCH-001

**Play and mix music architecture** — SoundFlow, MiniAudio, FFmpeg, independent deck state, and one playback session
**Covered by:** FR: FR-PLAYER-001; TEST: TEST-PLAYER-001, TEST-PLAYER-002
**Status:** pending
Scope: layer-1+

## TR-PLUGINS-ARCH-001

**Install and run extensions architecture** — Versioned worker IPC, declarative host rendering, local trust, ILoggerFactory SDK
**Covered by:** FR: FR-PLUGINS-001; TEST: TEST-PLUGINS-001, TEST-PLUGINS-002
**Status:** pending
Scope: layer-1+

## TR-RECORDS-ARCH-001

**Sync catalog, listening records and defaults architecture** — Host and local LiteDB, consistent snapshots, cursor checkpoints, and durable notification delivery
**Covered by:** FR: FR-RECORDS-001; TEST: TEST-RECORDS-001, TEST-RECORDS-002
**Status:** pending
Scope: layer-1+
**Acceptance Criteria:**
- [ ] The desktop and host use separate LiteDB files behind typed repository adapters. Each write transaction atomically commits the related records, operation receipts, and applicable revision or delivery state.
- [ ] The C4 workspace defines the desktop and host LiteDB collections, their document keys and fields, and their logical relationships. The entity views match the repository collection inventory.

## TR-SETTINGS-ARCH-001

**Set shared and local preferences architecture** — Typed, versioned registry and revisioned host settings with device-local storage
**Covered by:** FR: FR-SETTINGS-001; TEST: TEST-SETTINGS-001, TEST-SETTINGS-002
**Status:** pending
Scope: layer-1+

