# Stage 0 readback

Workspace: `beets-man`. Effective layer: `layer-1`.

- QBrain accepted 40 requirement records in request `req-20261123T171752Z-9936`. The effective query `req-20261123T172054Z-36aa` returned 10 FRs, 10 TRs, 20 TEST requirements, and 10 mapping records.
- Final comparison `req-20261123T172911Z-8ac4` checked all 40 record IDs and titles, all 33 criterion texts and unsatisfied states, and the TR/TEST edges for all 10 FRs against the workspace payload.
- Use-case coverage query `req-20261123T172105Z-ad7a` returned 10 linked use cases for 10 FRs, with no missing Realizes links. All 10 drafts received a primary actor and three structured basic-flow steps.
- All 10 [UML graphs](../use-cases/qbrain-graphs.json) are saved on their QBrain use cases. Live PlantUML exports matched the Beets Man boundary, goal, actor nodes, and association edges.
- The Codex bridge reports an unsupported result type for native graph GET and PUT. The dedicated live UML export readback verifies stored semantic content, but not exact coordinates. The first empty export was `req-20261123T172608Z-0b38`. After the write response error, the live export `req-20261123T172642Z-bbe7` returned the boundary, actor, and goal. QBrain triage accepted `triage-report-041d285efd764068813f0ba0460bb71f`.
- Asset Review 0.2.1 loaded the [review catalog](../design/_manifest.md) and served all 190 wireframes. It returned all 37 sequences in their declared frame order. Those sequences cover 21 storyboards, 135 state wireframes, and 55 base wireframes.
- Librsvg rendered all 190 wireframes. Of 36 same-screen transitions, 35 changed the rendered UI and the ignored stale search response preserved the newer results pixel for pixel. Use-case IDs matched the candidate, the saved QBrain graphs, the diagram files, and the storyboard mappings. Every use case has both wireframe and storyboard links in the [use-case index](../use-cases/README.md).
- The routing review added 14 state wireframes. The graph-capable Asset Review build served all 204 SVGs and preserved all 37 sequence IDs. Its `/api/assets` response matched the manifest entries, nodes, labels, and destinations across all 15 graphs with empty diagnostics. The plugin restart remains an ordered linear sequence.
- The Library review verified the inline playlist, save-queue, and Android sync-list paths. Librsvg rendered all 216 SVGs. Asset Review preserved all 37 sequence IDs and returned all 16 graphs with empty diagnostics. The existing use cases still have wireframe and storyboard coverage.
- Every one of the 33 acceptance criteria has a named unit case in the test specifications. These are design and traceability checks. Application tests have not been executed.
- Structurizr validated the DSL and exported eight diagram views using the pinned 2026.09.19-noble image. See the [C4 exports](../architecture/views.md).
- The architecture-first pass validated the updated workspace and compared all 11 published Mermaid views with the pinned Structurizr exports. The original eight view keys are present alongside BootstrapDispatch, LibraryWorkspace, and GuidedImport. The [CQRS interface declarations](../architecture/cqrs.md) compiled in scratch space with .NET SDK 10.0.403. [Current C4 exports](../architecture/views.md)
- The repository design check confirmed one component-level access boundary per LiteDB file, compared all 11 updated Mermaid views with the pinned exports, and compiled the repository declarations alongside the CQRS interfaces using .NET SDK 10.0.403. [Repository contracts](../architecture/persistence.md) · [Current C4 exports](../architecture/views.md)
- The entity-model check matched all 27 collection definitions to the repository inventory. The exported entity views matched 12 desktop entities and 16 relationships, and 15 host entities and 18 relationships. All 13 published views matched the pinned Structurizr exports. [Entity definitions](../architecture/litedb-entities.dsl) · [Entity views](../architecture/views.md#desktoplitedbentities)

## QBrain UML export receipts

- UC-CONFIG-001 · QBrain 10026 · `req-20261123T172720Z-63c4`
- UC-DISPATCH-001 · QBrain 10027 · `req-20261123T172729Z-f1bf`
- UC-LIBRARY-001 · QBrain 10028 · `req-20261123T172736Z-15c3`
- UC-PLAYER-001 · QBrain 10029 · `req-20261123T172746Z-08a5`
- UC-CLI-001 · QBrain 10030 · `req-20261123T172754Z-0836`
- UC-PLUGINS-001 · QBrain 10031 · `req-20261123T172803Z-7639`
- UC-SETTINGS-001 · QBrain 10032 · `req-20261123T172813Z-bf70`
- UC-BEETS-001 · QBrain 10033 · `req-20261123T172820Z-c5f7`
- UC-RECORDS-001 · QBrain 10034 · `req-20261123T172826Z-18bd`
- UC-FILESYNC-001 · QBrain 10035 · `req-20261123T172833Z-7ac3`

## Open checks

- The asset matrix documents the current proposed screens and flows. Acceptance of complete, branch-specific coverage and final contract pins remains open.
- No independent HV verdict or Stage 0 approval has been recorded. Product implementation has not started.
