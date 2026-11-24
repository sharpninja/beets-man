# C4 views exported from Structurizr

Generated from [workspace.dsl](workspace.dsl) using the pinned 2026.09.19-noble image. The [architecture guide](README.md) contains code-level views and decisions.

## SystemContext

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["System Context View: Beets Man"]
    style diagram fill:#ffffff,stroke:#ffffff

    1["<div style='font-weight: bold'>Listener and library operator</div><div style='font-size: 70%; margin-top: 0px'>[Person]</div>"]
    style 1 fill:#ffffff,stroke:#444444,color:#444444
    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444
    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444
    5["<div style='font-weight: bold'>Desktop OS</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides preferences paths,<br />credential storage, hotkeys,<br />windows, and audio output.</div>"]
    style 5 fill:#ffffff,stroke:#444444,color:#444444
    6["<div style='font-weight: bold'>Beets Man</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Desktop music playback,<br />library management, and peer<br />synchronization.</div>"]
    style 6 fill:#ffffff,stroke:#444444,color:#444444

    1-. "<div>Uses</div><div style='font-size: 70%'></div>" .->6
    6-. "<div>Uses playback, hotkeys, and<br />credential services</div><div style='font-size: 70%'></div>" .->5
    6-. "<div>Runs journaled library<br />operations</div><div style='font-size: 70%'></div>" .->2
    6-. "<div>Uses authenticated REST and<br />device events</div><div style='font-size: 70%'></div>" .->3

  end
```

## Containers

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Container View: Beets Man"]
    style diagram fill:#ffffff,stroke:#ffffff

    1["<div style='font-weight: bold'>Listener and library operator</div><div style='font-size: 70%; margin-top: 0px'>[Person]</div>"]
    style 1 fill:#ffffff,stroke:#444444,color:#444444
    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444
    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444
    5["<div style='font-weight: bold'>Desktop OS</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides preferences paths,<br />credential storage, hotkeys,<br />windows, and audio output.</div>"]
    style 5 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 24 fill:#ffffff,stroke:#444444,color:#444444
      25["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
      34["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 34 fill:#ffffff,stroke:#444444,color:#444444
      35["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 35 fill:#ffffff,stroke:#444444,color:#444444
      36["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 36 fill:#ffffff,stroke:#444444,color:#444444
      7["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 7 fill:#ffffff,stroke:#444444,color:#444444
    end

    1-. "<div>Uses</div><div style='font-size: 70%'></div>" .->7
    7-. "<div>Uses playback, hotkeys, and<br />credential services</div><div style='font-size: 70%'></div>" .->5
    7-. "<div>Sends authenticated commands<br />and sync requests</div><div style='font-size: 70%'>[HTTPS JSON]</div>" .->25
    7-. "<div>Reads and commits desktop<br />state</div><div style='font-size: 70%'></div>" .->24
    7-. "<div>Exchanges telemetry,<br />declarations, commands, and<br />log events</div><div style='font-size: 70%'>[Versioned IPC]</div>" .->35
    7-. "<div>Invokes configured playback<br />actions</div><div style='font-size: 70%'></div>" .->36
    25-. "<div>Commits host state and<br />delivery state</div><div style='font-size: 70%'></div>" .->34
    25-. "<div>Runs journaled library<br />operations</div><div style='font-size: 70%'></div>" .->2
    25-. "<div>Uses authenticated REST and<br />device events</div><div style='font-size: 70%'></div>" .->3
    25-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->7

  end
```

## DesktopComponents

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Component View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    5["<div style='font-weight: bold'>Desktop OS</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides preferences paths,<br />credential storage, hotkeys,<br />windows, and audio output.</div>"]
    style 5 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 7 ["Desktop app"]
        style 7 fill:#ffffff,stroke:#444444,color:#444444

        10["<div style='font-weight: bold'>Presentation projection</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Guards owner scope and<br />revision, shares playback<br />state, and exposes logical<br />interaction blocking<br />independently of background<br />progress.</div>"]
        style 10 fill:#ffffff,stroke:#444444,color:#444444
        11["<div style='font-weight: bold'>CQRS and Unit of Work</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Dispatches typed commands and<br />queries to exactly one<br />handler and returns a typed<br />outcome. Coordinates writes<br />by state owner.</div>"]
        style 11 fill:#ffffff,stroke:#444444,color:#444444
        12["<div style='font-weight: bold'>Desktop bootstrap</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Validates local configuration<br />and resolves paths before<br />opening desktop state.<br />Reports initialization,<br />repair, or ready state.</div>"]
        style 12 fill:#ffffff,stroke:#444444,color:#444444
        13["<div style='font-weight: bold'>Local configuration and settings</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Loads appsettings.json and<br />resolves preference<br />precedence.</div>"]
        style 13 fill:#ffffff,stroke:#444444,color:#444444
        14["<div style='font-weight: bold'>Catalog queries</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Reads indexed Library<br />projections and reports track<br />availability under the local<br />music root.</div>"]
        style 14 fill:#ffffff,stroke:#444444,color:#444444
        15["<div style='font-weight: bold'>Playlist workspace</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Maintains ordered drafts and<br />saves named playlists. Copies<br />the current queue into a<br />draft when requested.</div>"]
        style 15 fill:#ffffff,stroke:#444444,color:#444444
        16["<div style='font-weight: bold'>Playback coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Owns the active queue and one<br />playback session across<br />views, transport and two<br />independently cueable decks.</div>"]
        style 16 fill:#ffffff,stroke:#444444,color:#444444
        17["<div style='font-weight: bold'>SoundFlow strategy</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Mixes decks, applies EQ, and<br />produces audio telemetry.</div>"]
        style 17 fill:#ffffff,stroke:#444444,color:#444444
        18["<div style='font-weight: bold'>External CLI strategy</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Translates YAML actions into<br />command or session protocol<br />calls.</div>"]
        style 18 fill:#ffffff,stroke:#444444,color:#444444
        19["<div style='font-weight: bold'>Plugin supervisor</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Manages local trust, worker<br />IPC, and declarative<br />contributions.</div>"]
        style 19 fill:#ffffff,stroke:#444444,color:#444444
        20["<div style='font-weight: bold'>Host library client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Submits authenticated import<br />and tag commands, preserves<br />edit drafts, and observes<br />asynchronous job status.</div>"]
        style 20 fill:#ffffff,stroke:#444444,color:#444444
        21["<div style='font-weight: bold'>Record sync client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies snapshots and<br />revisions, replays edits, and<br />acknowledges notifications.</div>"]
        style 21 fill:#ffffff,stroke:#444444,color:#444444
        22["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 22 fill:#ffffff,stroke:#444444,color:#444444
        23["<div style='font-weight: bold'>Logging adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>ILogger scopes, local<br />filters, rolling retention,<br />and redaction.</div>"]
        style 23 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 24 fill:#ffffff,stroke:#444444,color:#444444
      25["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
      35["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 35 fill:#ffffff,stroke:#444444,color:#444444
      36["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 36 fill:#ffffff,stroke:#444444,color:#444444
    end

    8-. "<div>Submits typed intent</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>Validates and applies desktop<br />bootstrap actions</div><div style='font-size: 70%'></div>" .->12
    12-. "<div>Resolves effective paths and<br />startup configuration</div><div style='font-size: 70%'></div>" .->13
    12-. "<div>Opens the validated desktop<br />store</div><div style='font-size: 70%'></div>" .->22
    22-. "<div>Queries collections and<br />commits local transactions</div><div style='font-size: 70%'></div>" .->24
    11-. "<div>Resolves preferences</div><div style='font-size: 70%'></div>" .->13
    11-. "<div>Queries Library views and<br />validates playable selections</div><div style='font-size: 70%'></div>" .->14
    14-. "<div>Reads indexed catalog and<br />availability projections</div><div style='font-size: 70%'></div>" .->22
    11-. "<div>Starts a draft, changes<br />order, or saves a playlist</div><div style='font-size: 70%'></div>" .->15
    15-. "<div>Stores the draft and<br />operation identity</div><div style='font-size: 70%'></div>" .->22
    15-. "<div>Hands off saved playlist<br />changes for replication</div><div style='font-size: 70%'></div>" .->21
    15-. "<div>Reads the current queue<br />snapshot for Save as playlist</div><div style='font-size: 70%'></div>" .->16
    11-. "<div>Executes transport and mixer<br />actions</div><div style='font-size: 70%'></div>" .->16
    16-. "<div>Restores and commits the<br />current queue</div><div style='font-size: 70%'></div>" .->22
    11-. "<div>Submits host commands or<br />requests current job state</div><div style='font-size: 70%'></div>" .->20
    20-. "<div>Uploads copies, submits typed<br />commands, and reads job<br />status</div><div style='font-size: 70%'>[HTTPS JSON and streamed files]</div>" .->25
    11-. "<div>Publishes typed outcomes</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>Schedules immutable UI update</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>Applies state on UIThread</div><div style='font-size: 70%'></div>" .->8
    13-. "<div>Reads and writes local<br />preference overrides</div><div style='font-size: 70%'></div>" .->22
    13-. "<div>Configures log levels and<br />sinks</div><div style='font-size: 70%'></div>" .->23
    11-. "<div>Logs outcomes within<br />correlation scopes</div><div style='font-size: 70%'></div>" .->23
    16-. "<div>Executes built-in playback</div><div style='font-size: 70%'></div>" .->17
    16-. "<div>Executes optional CLI<br />playback</div><div style='font-size: 70%'></div>" .->18
    17-. "<div>Outputs mixed audio</div><div style='font-size: 70%'></div>" .->5
    18-. "<div>Invokes direct arguments or<br />sends stdio requests</div><div style='font-size: 70%'></div>" .->36
    16-. "<div>Publishes bounded<br />visualization telemetry</div><div style='font-size: 70%'></div>" .->19
    19-. "<div>Exchanges validated IPC</div><div style='font-size: 70%'></div>" .->35
    19-. "<div>Submits extension intents and<br />status</div><div style='font-size: 70%'></div>" .->11
    19-. "<div>Forwards worker diagnostics</div><div style='font-size: 70%'></div>" .->23
    11-. "<div>Starts record operations</div><div style='font-size: 70%'></div>" .->21
    21-. "<div>Commits applied pages and<br />checkpoints</div><div style='font-size: 70%'></div>" .->22
    21-. "<div>Registers, fetches changes<br />and acknowledges</div><div style='font-size: 70%'></div>" .->25
    25-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->21
    21-. "<div>Reports sync and peer events</div><div style='font-size: 70%'></div>" .->11

  end
```

## HostComponents

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Component View: Beets Man - Library host"]
    style diagram fill:#ffffff,stroke:#ffffff

    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444
    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 25 ["Library host"]
        style 25 fill:#ffffff,stroke:#444444,color:#444444

        26["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444
        27["<div style='font-weight: bold'>Import staging</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Accepts uploaded copies and<br />exposes staged inputs to<br />guided import. Keeps drafts<br />and temporary files separate<br />from the published library.</div>"]
        style 27 fill:#ffffff,stroke:#444444,color:#444444
        28["<div style='font-weight: bold'>beets job coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Journals, executes, and<br />reconciles external library<br />writes.</div>"]
        style 28 fill:#ffffff,stroke:#444444,color:#444444
        29["<div style='font-weight: bold'>Catalog and revision feed</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Projects beets records and<br />publishes consistent<br />snapshots and change pages.</div>"]
        style 29 fill:#ffffff,stroke:#444444,color:#444444
        30["<div style='font-weight: bold'>Notification queue</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Persists committed changes<br />and tracks acknowledged<br />recipients.</div>"]
        style 30 fill:#ffffff,stroke:#444444,color:#444444
        31["<div style='font-weight: bold'>Device export manager</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Plans exports against the<br />selected device and player<br />profile. Preserves playlist<br />order and stages only missing<br />audio for a playlist drop.</div>"]
        style 31 fill:#ffffff,stroke:#444444,color:#444444
        32["<div style='font-weight: bold'>Syncthing adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Manages music shares and<br />forwards live peer connection<br />transitions.</div>"]
        style 32 fill:#ffffff,stroke:#444444,color:#444444
        33["<div style='font-weight: bold'>Host repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps authoritative documents,<br />operation receipts, revisions<br />and delivery records to host<br />LiteDB collections. Runs<br />atomic host writes.</div>"]
        style 33 fill:#ffffff,stroke:#444444,color:#444444
      end

      34["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 34 fill:#ffffff,stroke:#444444,color:#444444
      7["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 7 fill:#ffffff,stroke:#444444,color:#444444
    end

    26-. "<div>Queries revision pages and<br />snapshots</div><div style='font-size: 70%'></div>" .->29
    29-. "<div>Commits authoritative<br />records, revisions and<br />delivery records together</div><div style='font-size: 70%'></div>" .->33
    29-. "<div>Enqueues correlated<br />publication</div><div style='font-size: 70%'></div>" .->30
    30-. "<div>Stores recipient progress</div><div style='font-size: 70%'></div>" .->33
    26-. "<div>Pulls and acknowledges queued<br />notifications</div><div style='font-size: 70%'></div>" .->30
    26-. "<div>Previews and publishes a<br />device layout</div><div style='font-size: 70%'></div>" .->31
    31-. "<div>Reads ordered playlist<br />entries and source track<br />identities</div><div style='font-size: 70%'></div>" .->29
    31-. "<div>Tracks committed export<br />ownership</div><div style='font-size: 70%'></div>" .->33
    31-. "<div>Reads destination<br />availability and requests<br />scan after publication</div><div style='font-size: 70%'></div>" .->32
    32-. "<div>Manages shares and observes<br />connections</div><div style='font-size: 70%'></div>" .->3
    32-. "<div>Reports current Android<br />connection transition</div><div style='font-size: 70%'></div>" .->26
    7-. "<div>Uploads copies, submits typed<br />commands, and reads job<br />status</div><div style='font-size: 70%'>[HTTPS JSON and streamed files]</div>" .->26
    26-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->7
    26-. "<div>Dispatches beets jobs</div><div style='font-size: 70%'></div>" .->28
    26-. "<div>Reads registered instance<br />state</div><div style='font-size: 70%'></div>" .->33
    33-. "<div>Queries collections and<br />commits host transactions</div><div style='font-size: 70%'></div>" .->34
    26-. "<div>Authenticates and accepts<br />uploaded album copies</div><div style='font-size: 70%'></div>" .->27
    28-. "<div>Consumes a completed staged<br />upload by ID</div><div style='font-size: 70%'></div>" .->27
    28-. "<div>Persists job intent and<br />outcome</div><div style='font-size: 70%'></div>" .->33
    28-. "<div>Executes or reconciles an<br />operation</div><div style='font-size: 70%'></div>" .->2
    28-. "<div>Publishes reconciled state</div><div style='font-size: 70%'></div>" .->29

  end
```

## DesktopLiteDbEntities

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Desktop state LiteDB entities"]
    style diagram fill:#ffffff,stroke:#ffffff

    111["<div style='font-weight: bold'>Desktop / catalog_artists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ArtistId (Guid).<br />LibraryId: Guid.<br />SourceRevision: Int64. Name<br />and SortName: string.</div>"]
    style 111 fill:#ffffff,stroke:#444444,color:#444444
    112["<div style='font-weight: bold'>Desktop / catalog_albums</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: AlbumId (Guid).<br />LibraryId: Guid.<br />AlbumArtistId: Guid?.<br />SourceRevision: Int64. Title<br />and SortTitle: string.</div>"]
    style 112 fill:#ffffff,stroke:#444444,color:#444444
    113["<div style='font-weight: bold'>Desktop / catalog_tracks</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: TrackId (Guid).<br />LibraryId: Guid. ArtistId and<br />AlbumId: Guid?.<br />SourceRevision: Int64. Title:<br />string. RelativePath and<br />Genre: string?. TrackNumber<br />and Rating: Int32?.<br />DurationTicks: Int64?. Tags:<br />versioned tag document.</div>"]
    style 113 fill:#ffffff,stroke:#444444,color:#444444
    114["<div style='font-weight: bold'>Desktop / catalog_playlists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlaylistId (Guid).<br />LibraryId: Guid.<br />SourceRevision and<br />PlaylistRevision: Int64.<br />Name: string. Entries:<br />ordered { EntryId: Guid,<br />TrackId: Guid } list.</div>"]
    style 114 fill:#ffffff,stroke:#444444,color:#444444
    115["<div style='font-weight: bold'>Desktop / listening_records</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlayId or (LibraryId,<br />ListenerId, TrackId) for<br />favorites, distinguished by<br />kind. LibraryId, ListenerId<br />and TrackId: Guid. Kind: Play<br />or Favorite. SourceRevision:<br />Int64. PlayedAtUtc: DateTime<br />for plays.</div>"]
    style 115 fill:#ffffff,stroke:#444444,color:#444444
    116["<div style='font-weight: bold'>Desktop / shared_preference_cache</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />PreferenceKey). LibraryId:<br />Guid. PreferenceKey: string.<br />RegistryVersion: Int32.<br />SourceRevision: Int64. Value:<br />registry-typed value.</div>"]
    style 116 fill:#ffffff,stroke:#444444,color:#444444
    117["<div style='font-weight: bold'>Desktop / playlist_drafts</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: DraftId (Guid).<br />PlaylistId: Guid?.<br />ExpectedHostRevision: Int64?.<br />DraftRevision: Int64.<br />ProposedName: string.<br />Entries: ordered { EntryId:<br />Guid, TrackId: Guid } list.</div>"]
    style 117 fill:#ffffff,stroke:#444444,color:#444444
    118["<div style='font-weight: bold'>Desktop / playback_sessions</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: SessionId (Guid).<br />QueueRevision and<br />PositionTicks: Int64.<br />ActiveEntryId: Guid?. Queue:<br />ordered { EntryId: Guid,<br />TrackId: Guid } list.</div>"]
    style 118 fill:#ffffff,stroke:#444444,color:#444444
    119["<div style='font-weight: bold'>Desktop / preference_overrides</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PreferenceKey (string).<br />RegistryVersion: Int32.<br />LocalRevision: Int64. Value:<br />registry-typed value.</div>"]
    style 119 fill:#ffffff,stroke:#444444,color:#444444
    120["<div style='font-weight: bold'>Desktop / pending_operations</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (OriginInstanceId,<br />OperationId). Both IDs: Guid.<br />CommandType and State:<br />string. Payload: versioned<br />command document.<br />ExpectedRevision: Int64?.<br />DraftId: Guid?.</div>"]
    style 120 fill:#ffffff,stroke:#444444,color:#444444
    121["<div style='font-weight: bold'>Desktop / sync_checkpoints</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId, Feed).<br />LibraryId: Guid. Feed:<br />string. AppliedRevision:<br />Int64. Cursor: string?.</div>"]
    style 121 fill:#ffffff,stroke:#444444,color:#444444
    122["<div style='font-weight: bold'>Desktop / store_schema</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: singleton 'store'.<br />StoreId: Guid. StoreRole:<br />Desktop. SchemaVersion:<br />Int32.</div>"]
    style 122 fill:#ffffff,stroke:#444444,color:#444444

    112-. "<div>Optional AlbumArtistId</div><div style='font-size: 70%'></div>" .->111
    113-. "<div>Optional ArtistId</div><div style='font-size: 70%'></div>" .->111
    113-. "<div>Optional AlbumId</div><div style='font-size: 70%'></div>" .->112
    114-. "<div>Ordered entries by TrackId</div><div style='font-size: 70%'></div>" .->113
    115-. "<div>TrackId</div><div style='font-size: 70%'></div>" .->113
    117-. "<div>Optional target PlaylistId</div><div style='font-size: 70%'></div>" .->114
    117-. "<div>Ordered draft entries by<br />TrackId</div><div style='font-size: 70%'></div>" .->113
    118-. "<div>Ordered queue entries by<br />TrackId</div><div style='font-size: 70%'></div>" .->113
    120-. "<div>Optional DraftId retained for<br />reconciliation</div><div style='font-size: 70%'></div>" .->117
    121-. "<div>Commits with the applied<br />artist page</div><div style='font-size: 70%'></div>" .->111
    121-. "<div>Commits with the applied<br />album page</div><div style='font-size: 70%'></div>" .->112
    121-. "<div>Commits with the applied<br />track page</div><div style='font-size: 70%'></div>" .->113
    121-. "<div>Commits with the applied<br />playlist page</div><div style='font-size: 70%'></div>" .->114
    121-. "<div>Commits with the applied<br />listening records</div><div style='font-size: 70%'></div>" .->115
    121-. "<div>Commits with the applied<br />shared defaults</div><div style='font-size: 70%'></div>" .->116
    119-. "<div>Resolves the next preference<br />source by key</div><div style='font-size: 70%'></div>" .->116

  end
```

## HostLiteDbEntities

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Host state LiteDB entities"]
    style diagram fill:#ffffff,stroke:#ffffff

    123["<div style='font-weight: bold'>Host / catalog_artists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ArtistId (Guid).<br />LibraryId: Guid. Revision:<br />Int64. Name and SortName:<br />string.</div>"]
    style 123 fill:#ffffff,stroke:#444444,color:#444444
    124["<div style='font-weight: bold'>Host / catalog_albums</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: AlbumId (Guid).<br />LibraryId: Guid.<br />AlbumArtistId: Guid?.<br />Revision: Int64. Title and<br />SortTitle: string.</div>"]
    style 124 fill:#ffffff,stroke:#444444,color:#444444
    125["<div style='font-weight: bold'>Host / catalog_tracks</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: TrackId (Guid).<br />LibraryId: Guid. ArtistId and<br />AlbumId: Guid?. Revision:<br />Int64. BeetsTrackId: Int64?.<br />Title: string. RelativePath<br />and Genre: string?.<br />TrackNumber and Rating:<br />Int32?. DurationTicks:<br />Int64?. Tags: versioned tag<br />document.</div>"]
    style 125 fill:#ffffff,stroke:#444444,color:#444444
    126["<div style='font-weight: bold'>Host / saved_playlists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlaylistId (Guid).<br />LibraryId: Guid. Revision:<br />Int64. Name: string. Entries:<br />ordered { EntryId: Guid,<br />TrackId: Guid } list.</div>"]
    style 126 fill:#ffffff,stroke:#444444,color:#444444
    127["<div style='font-weight: bold'>Host / listening_records</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlayId or (LibraryId,<br />ListenerId, TrackId) for<br />favorites, distinguished by<br />kind. LibraryId, ListenerId<br />and TrackId: Guid. Kind: Play<br />or Favorite. Revision: Int64.<br />PlayedAtUtc: DateTime for<br />plays.</div>"]
    style 127 fill:#ffffff,stroke:#444444,color:#444444
    128["<div style='font-weight: bold'>Host / shared_preferences</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />PreferenceKey). LibraryId:<br />Guid. PreferenceKey: string.<br />RegistryVersion: Int32.<br />Revision: Int64. Value:<br />registry-typed value.</div>"]
    style 128 fill:#ffffff,stroke:#444444,color:#444444
    129["<div style='font-weight: bold'>Host / registered_instances</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: InstanceId (Guid).<br />LibraryId: Guid. Role and<br />Status: string.<br />SyncthingDeviceId: string?.</div>"]
    style 129 fill:#ffffff,stroke:#444444,color:#444444
    130["<div style='font-weight: bold'>Host / library_jobs</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: JobId (Guid).<br />OriginInstanceId and<br />OperationId: Guid. Kind and<br />Status: string. UploadId:<br />Guid?. ExpectedRevision and<br />PublishedRevision: Int64?.</div>"]
    style 130 fill:#ffffff,stroke:#444444,color:#444444
    131["<div style='font-weight: bold'>Host / operation_receipts</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (OriginInstanceId,<br />OperationId). Both IDs: Guid.<br />IntentFingerprint: string.<br />Receipt: typed, versioned<br />outcome.</div>"]
    style 131 fill:#ffffff,stroke:#444444,color:#444444
    132["<div style='font-weight: bold'>Host / revision_heads</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: LibraryId (Guid).<br />CurrentRevision and<br />MinRetainedRevision: Int64.</div>"]
    style 132 fill:#ffffff,stroke:#444444,color:#444444
    133["<div style='font-weight: bold'>Host / revision_changes</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId, Revision).<br />LibraryId, OriginInstanceId<br />and OperationId: Guid.<br />Revision: Int64. Changes:<br />versioned entity updates or<br />tombstones.</div>"]
    style 133 fill:#ffffff,stroke:#444444,color:#444444
    134["<div style='font-weight: bold'>Host / notification_deliveries</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />RecipientInstanceId,<br />Revision). Both IDs: Guid.<br />Revision: Int64. Status:<br />Pending or Acknowledged.</div>"]
    style 134 fill:#ffffff,stroke:#444444,color:#444444
    135["<div style='font-weight: bold'>Host / device_exports</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ExportId (Guid).<br />DeviceId: Guid. ShareId:<br />string. ProfileVersion and<br />ManifestRevision: Int64.<br />PublicationState: string.</div>"]
    style 135 fill:#ffffff,stroke:#444444,color:#444444
    136["<div style='font-weight: bold'>Host / device_export_entries</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (ExportId, PathKey).<br />ExportId and SourceEntityId:<br />Guid. RelativePath, PathKey<br />and ContentIdentity: string.<br />SourceKind: Playlist or<br />Audio.</div>"]
    style 136 fill:#ffffff,stroke:#444444,color:#444444
    137["<div style='font-weight: bold'>Host / store_schema</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: singleton 'store'.<br />StoreId: Guid. StoreRole:<br />Host. SchemaVersion: Int32.</div>"]
    style 137 fill:#ffffff,stroke:#444444,color:#444444

    124-. "<div>Optional AlbumArtistId</div><div style='font-size: 70%'></div>" .->123
    125-. "<div>Optional ArtistId</div><div style='font-size: 70%'></div>" .->123
    125-. "<div>Optional AlbumId</div><div style='font-size: 70%'></div>" .->124
    126-. "<div>Ordered entries by TrackId</div><div style='font-size: 70%'></div>" .->125
    127-. "<div>TrackId</div><div style='font-size: 70%'></div>" .->125
    130-. "<div>OriginInstanceId</div><div style='font-size: 70%'></div>" .->129
    130-. "<div>Submission receipt by origin<br />and operation ID</div><div style='font-size: 70%'></div>" .->131
    130-. "<div>PublishedRevision when<br />reconciled</div><div style='font-size: 70%'></div>" .->133
    131-. "<div>OriginInstanceId</div><div style='font-size: 70%'></div>" .->129
    131-. "<div>Feed revision in a playlist<br />save receipt</div><div style='font-size: 70%'></div>" .->133
    133-. "<div>LibraryId and issued revision</div><div style='font-size: 70%'></div>" .->132
    133-. "<div>Versioned playlist updates<br />and tombstones</div><div style='font-size: 70%'></div>" .->126
    134-. "<div>LibraryId and revision to<br />deliver</div><div style='font-size: 70%'></div>" .->133
    134-. "<div>RecipientInstanceId</div><div style='font-size: 70%'></div>" .->129
    135-. "<div>Registered DeviceId</div><div style='font-size: 70%'></div>" .->129
    136-. "<div>ExportId</div><div style='font-size: 70%'></div>" .->135
    136-. "<div>SourceEntityId when<br />SourceKind is Playlist</div><div style='font-size: 70%'></div>" .->126
    136-. "<div>SourceEntityId when<br />SourceKind is Audio</div><div style='font-size: 70%'></div>" .->125

  end
```

## SelfHosted

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Deployment View: Self-hosted"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 172 ["Designated PC"]
      style 172 fill:#ffffff,stroke:#444444,color:#444444

      173["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 173 fill:#ffffff,stroke:#444444,color:#444444
      174["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 174 fill:#ffffff,stroke:#444444,color:#444444
    end

    subgraph 176 ["Registered desktop"]
      style 176 fill:#ffffff,stroke:#444444,color:#444444

      177["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 177 fill:#ffffff,stroke:#444444,color:#444444
      180["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 180 fill:#ffffff,stroke:#444444,color:#444444
      182["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 182 fill:#ffffff,stroke:#444444,color:#444444
      184["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 184 fill:#ffffff,stroke:#444444,color:#444444
    end

    subgraph 186 ["Android device"]
      style 186 fill:#ffffff,stroke:#444444,color:#444444

      187["<div style='font-weight: bold'>Android peer</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Syncthing-Fork receives a<br />device export. Musicolet<br />reads music and playlists.</div>"]
      style 187 fill:#ffffff,stroke:#444444,color:#444444
    end

    173-. "<div>Commits host state and<br />delivery state</div><div style='font-size: 70%'></div>" .->174
    177-. "<div>Sends authenticated commands<br />and sync requests</div><div style='font-size: 70%'>[HTTPS JSON]</div>" .->173
    173-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->177
    177-. "<div>Reads and commits desktop<br />state</div><div style='font-size: 70%'></div>" .->180
    177-. "<div>Exchanges telemetry,<br />declarations, commands, and<br />log events</div><div style='font-size: 70%'>[Versioned IPC]</div>" .->182
    177-. "<div>Invokes configured playback<br />actions</div><div style='font-size: 70%'></div>" .->184

  end
```

## BootstrapDispatch

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 7 ["Desktop app"]
        style 7 fill:#ffffff,stroke:#444444,color:#444444

        10["<div style='font-weight: bold'>Presentation projection</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Guards owner scope and<br />revision, shares playback<br />state, and exposes logical<br />interaction blocking<br />independently of background<br />progress.</div>"]
        style 10 fill:#ffffff,stroke:#444444,color:#444444
        11["<div style='font-weight: bold'>CQRS and Unit of Work</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Dispatches typed commands and<br />queries to exactly one<br />handler and returns a typed<br />outcome. Coordinates writes<br />by state owner.</div>"]
        style 11 fill:#ffffff,stroke:#444444,color:#444444
        12["<div style='font-weight: bold'>Desktop bootstrap</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Validates local configuration<br />and resolves paths before<br />opening desktop state.<br />Reports initialization,<br />repair, or ready state.</div>"]
        style 12 fill:#ffffff,stroke:#444444,color:#444444
        13["<div style='font-weight: bold'>Local configuration and settings</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Loads appsettings.json and<br />resolves preference<br />precedence.</div>"]
        style 13 fill:#ffffff,stroke:#444444,color:#444444
        22["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 22 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 24 fill:#ffffff,stroke:#444444,color:#444444
    end

    24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
    style 24 fill:#ffffff,stroke:#444444,color:#444444

    8-. "<div>1. Requests startup or<br />submits a repair</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Validates file, paths and<br />restart settings</div><div style='font-size: 70%'></div>" .->12
    12-. "<div>3. Resolves JSON and local<br />paths</div><div style='font-size: 70%'></div>" .->13
    12-. "<div>4. Opens the repository<br />boundary after validation</div><div style='font-size: 70%'></div>" .->22
    22-. "<div>5. Opens the store and<br />verifies its schema</div><div style='font-size: 70%'></div>" .->24
    11-. "<div>6. Publishes ready, first-run<br />or repair state</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>7. Posts the matching startup<br />state</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>8. Applies the startup state<br />on UIThread</div><div style='font-size: 70%'></div>" .->8

  end
```

## LibraryWorkspace

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 7 ["Desktop app"]
        style 7 fill:#ffffff,stroke:#444444,color:#444444

        10["<div style='font-weight: bold'>Presentation projection</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Guards owner scope and<br />revision, shares playback<br />state, and exposes logical<br />interaction blocking<br />independently of background<br />progress.</div>"]
        style 10 fill:#ffffff,stroke:#444444,color:#444444
        11["<div style='font-weight: bold'>CQRS and Unit of Work</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Dispatches typed commands and<br />queries to exactly one<br />handler and returns a typed<br />outcome. Coordinates writes<br />by state owner.</div>"]
        style 11 fill:#ffffff,stroke:#444444,color:#444444
        14["<div style='font-weight: bold'>Catalog queries</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Reads indexed Library<br />projections and reports track<br />availability under the local<br />music root.</div>"]
        style 14 fill:#ffffff,stroke:#444444,color:#444444
        15["<div style='font-weight: bold'>Playlist workspace</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Maintains ordered drafts and<br />saves named playlists. Copies<br />the current queue into a<br />draft when requested.</div>"]
        style 15 fill:#ffffff,stroke:#444444,color:#444444
        22["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 22 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 24 fill:#ffffff,stroke:#444444,color:#444444
    end

    24["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
    style 24 fill:#ffffff,stroke:#444444,color:#444444

    8-. "<div>1. Requests a catalog view or<br />search</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Reads the selected Details<br />grid</div><div style='font-size: 70%'></div>" .->14
    14-. "<div>3. Queries indexed tracks and<br />media availability</div><div style='font-size: 70%'></div>" .->22
    22-. "<div>4. Reads the catalog<br />projection and revision</div><div style='font-size: 70%'></div>" .->24
    11-. "<div>5. Publishes scoped rows and<br />selection</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>6. Posts the current Library<br />projection</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>7. Updates the grid beside<br />the active list pane</div><div style='font-size: 70%'></div>" .->8
    8-. "<div>8. Drops selected tracks into<br />the Playlist list pane</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>9. Updates the ordered draft</div><div style='font-size: 70%'></div>" .->15
    11-. "<div>10. Publishes the revised<br />list state</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>11. Posts the list change</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>12. Updates the list pane on<br />UIThread</div><div style='font-size: 70%'></div>" .->8

  end
```

## GuidedImport

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 7 ["Desktop app"]
        style 7 fill:#ffffff,stroke:#444444,color:#444444

        10["<div style='font-weight: bold'>Presentation projection</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Guards owner scope and<br />revision, shares playback<br />state, and exposes logical<br />interaction blocking<br />independently of background<br />progress.</div>"]
        style 10 fill:#ffffff,stroke:#444444,color:#444444
        11["<div style='font-weight: bold'>CQRS and Unit of Work</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Dispatches typed commands and<br />queries to exactly one<br />handler and returns a typed<br />outcome. Coordinates writes<br />by state owner.</div>"]
        style 11 fill:#ffffff,stroke:#444444,color:#444444
        20["<div style='font-weight: bold'>Host library client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Submits authenticated import<br />and tag commands, preserves<br />edit drafts, and observes<br />asynchronous job status.</div>"]
        style 20 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

    end

    8-. "<div>1. Submits uploaded copies or<br />an import choice</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Submits the operation<br />without blocking navigation</div><div style='font-size: 70%'></div>" .->20
    11-. "<div>3. Publishes job identity and<br />current status</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>4. Posts the import status</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>5. Renders progress or tagged<br />choices</div><div style='font-size: 70%'></div>" .->8
    8-. "<div>6. Continues navigation or<br />requests a job refresh</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>7. Reads the latest job<br />snapshot by identity</div><div style='font-size: 70%'></div>" .->20
    11-. "<div>8. Publishes only the<br />matching job revision</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>9. Posts the scoped update</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>10. Applies the job result<br />beside the active view</div><div style='font-size: 70%'></div>" .->8

  end
```

## PlaybackDispatch

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 7 ["Desktop app"]
        style 7 fill:#ffffff,stroke:#444444,color:#444444

        10["<div style='font-weight: bold'>Presentation projection</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Guards owner scope and<br />revision, shares playback<br />state, and exposes logical<br />interaction blocking<br />independently of background<br />progress.</div>"]
        style 10 fill:#ffffff,stroke:#444444,color:#444444
        11["<div style='font-weight: bold'>CQRS and Unit of Work</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Dispatches typed commands and<br />queries to exactly one<br />handler and returns a typed<br />outcome. Coordinates writes<br />by state owner.</div>"]
        style 11 fill:#ffffff,stroke:#444444,color:#444444
        16["<div style='font-weight: bold'>Playback coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Owns the active queue and one<br />playback session across<br />views, transport and two<br />independently cueable decks.</div>"]
        style 16 fill:#ffffff,stroke:#444444,color:#444444
        17["<div style='font-weight: bold'>SoundFlow strategy</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Mixes decks, applies EQ, and<br />produces audio telemetry.</div>"]
        style 17 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

    end

    8-. "<div>1. Submits typed playback<br />intent</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Runs a transport or mix<br />command</div><div style='font-size: 70%'></div>" .->16
    16-. "<div>3. Applies the deck and fade<br />decisions</div><div style='font-size: 70%'></div>" .->17
    11-. "<div>4. Publishes result</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>5. Posts UI delta</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>6. Applies current snapshot</div><div style='font-size: 70%'></div>" .->8

  end
```

## HostPublication

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Library host"]
    style diagram fill:#ffffff,stroke:#ffffff

    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 25 ["Library host"]
        style 25 fill:#ffffff,stroke:#444444,color:#444444

        26["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444
        27["<div style='font-weight: bold'>Import staging</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Accepts uploaded copies and<br />exposes staged inputs to<br />guided import. Keeps drafts<br />and temporary files separate<br />from the published library.</div>"]
        style 27 fill:#ffffff,stroke:#444444,color:#444444
        28["<div style='font-weight: bold'>beets job coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Journals, executes, and<br />reconciles external library<br />writes.</div>"]
        style 28 fill:#ffffff,stroke:#444444,color:#444444
        29["<div style='font-weight: bold'>Catalog and revision feed</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Projects beets records and<br />publishes consistent<br />snapshots and change pages.</div>"]
        style 29 fill:#ffffff,stroke:#444444,color:#444444
        30["<div style='font-weight: bold'>Notification queue</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Persists committed changes<br />and tracks acknowledged<br />recipients.</div>"]
        style 30 fill:#ffffff,stroke:#444444,color:#444444
        33["<div style='font-weight: bold'>Host repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps authoritative documents,<br />operation receipts, revisions<br />and delivery records to host<br />LiteDB collections. Runs<br />atomic host writes.</div>"]
        style 33 fill:#ffffff,stroke:#444444,color:#444444
      end

      34["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 34 fill:#ffffff,stroke:#444444,color:#444444
    end

    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444
    34["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
    style 34 fill:#ffffff,stroke:#444444,color:#444444

    26-. "<div>1. Accepts authenticated<br />album copies before an import</div><div style='font-size: 70%'></div>" .->27
    26-. "<div>2. Accepts an authorized<br />guided import choice</div><div style='font-size: 70%'></div>" .->28
    28-. "<div>3. Records job intent before<br />external effects</div><div style='font-size: 70%'></div>" .->33
    33-. "<div>4. Commits job identity and<br />intent</div><div style='font-size: 70%'></div>" .->34
    28-. "<div>5. Resolves the staged input<br />for an import job</div><div style='font-size: 70%'></div>" .->27
    28-. "<div>6. Executes or reconciles</div><div style='font-size: 70%'></div>" .->2
    28-. "<div>7. Publishes reconciled state</div><div style='font-size: 70%'></div>" .->29
    29-. "<div>8. Enqueues a committed<br />revision</div><div style='font-size: 70%'></div>" .->30
    29-. "<div>9. Saves reconciled catalog<br />changes, feed revision and<br />deliveries</div><div style='font-size: 70%'></div>" .->33
    33-. "<div>10. Commits publication as<br />one transaction</div><div style='font-size: 70%'></div>" .->34

  end
```

## AndroidArrival

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Dynamic View: Beets Man - Library host"]
    style diagram fill:#ffffff,stroke:#ffffff

    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 25 ["Library host"]
        style 25 fill:#ffffff,stroke:#444444,color:#444444

        26["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444
        32["<div style='font-weight: bold'>Syncthing adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Manages music shares and<br />forwards live peer connection<br />transitions.</div>"]
        style 32 fill:#ffffff,stroke:#444444,color:#444444
      end

    end

    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444

    32-. "<div>1. Reads live DeviceConnected<br />event</div><div style='font-size: 70%'></div>" .->3
    32-. "<div>2. Forwards the registered<br />peer transition</div><div style='font-size: 70%'></div>" .->26

  end
```
