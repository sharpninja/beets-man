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
    5["<div style='font-weight: bold'>Operating system</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides local credentials,<br />certificates, preferences<br />paths, windows, hotkeys, and<br />audio output.</div>"]
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
    5["<div style='font-weight: bold'>Operating system</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides local credentials,<br />certificates, preferences<br />paths, windows, hotkeys, and<br />audio output.</div>"]
    style 5 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
      26["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 26 fill:#ffffff,stroke:#444444,color:#444444
      38["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 38 fill:#ffffff,stroke:#444444,color:#444444
      39["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 39 fill:#ffffff,stroke:#444444,color:#444444
      40["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 40 fill:#ffffff,stroke:#444444,color:#444444
      7["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 7 fill:#ffffff,stroke:#444444,color:#444444
    end

    1-. "<div>Uses</div><div style='font-size: 70%'></div>" .->7
    7-. "<div>Uses playback, hotkeys, and<br />credential services</div><div style='font-size: 70%'></div>" .->5
    26-. "<div>Reads configured credentials<br />and the TLS certificate from<br />local OS stores</div><div style='font-size: 70%'></div>" .->5
    7-. "<div>Sends authenticated commands<br />and sync requests</div><div style='font-size: 70%'>[HTTPS JSON]</div>" .->26
    7-. "<div>Reads and commits desktop<br />state</div><div style='font-size: 70%'></div>" .->25
    7-. "<div>Exchanges telemetry,<br />declarations, commands, and<br />log events</div><div style='font-size: 70%'>[Versioned IPC]</div>" .->39
    7-. "<div>Invokes configured playback<br />actions</div><div style='font-size: 70%'></div>" .->40
    26-. "<div>Commits host state and<br />delivery state</div><div style='font-size: 70%'></div>" .->38
    26-. "<div>Runs journaled library<br />operations</div><div style='font-size: 70%'></div>" .->2
    26-. "<div>Uses authenticated REST and<br />device events</div><div style='font-size: 70%'></div>" .->3
    26-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->7

  end
```

## DesktopComponents

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Component View: Beets Man - Desktop app"]
    style diagram fill:#ffffff,stroke:#ffffff

    5["<div style='font-weight: bold'>Operating system</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides local credentials,<br />certificates, preferences<br />paths, windows, hotkeys, and<br />audio output.</div>"]
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
        20["<div style='font-weight: bold'>Host library client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Submits authenticated library<br />commands, controls registered<br />PC mirrors, prepares and<br />publishes Android exports,<br />and reads and retries<br />verified transfer checkpoints<br />over HTTPS.</div>"]
        style 20 fill:#ffffff,stroke:#444444,color:#444444
        21["<div style='font-weight: bold'>Android arrival monitor</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Uses a fresh baseline after<br />any feed interruption and<br />dispatches current registered<br />Android arrivals through the<br />existing shell command.</div>"]
        style 21 fill:#ffffff,stroke:#444444,color:#444444
        22["<div style='font-weight: bold'>Record sync client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies snapshots and<br />revisions, replays edits, and<br />acknowledges notifications.</div>"]
        style 22 fill:#ffffff,stroke:#444444,color:#444444
        23["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 23 fill:#ffffff,stroke:#444444,color:#444444
        24["<div style='font-weight: bold'>Logging adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>ILogger scopes, local<br />filters, rolling retention,<br />and redaction.</div>"]
        style 24 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
      26["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 26 fill:#ffffff,stroke:#444444,color:#444444
      39["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 39 fill:#ffffff,stroke:#444444,color:#444444
      40["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 40 fill:#ffffff,stroke:#444444,color:#444444
    end

    26-. "<div>Reads configured credentials<br />and the TLS certificate from<br />local OS stores</div><div style='font-size: 70%'></div>" .->5
    8-. "<div>Submits typed intent</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>Validates and applies desktop<br />bootstrap actions</div><div style='font-size: 70%'></div>" .->12
    12-. "<div>Resolves effective paths and<br />startup configuration</div><div style='font-size: 70%'></div>" .->13
    12-. "<div>Opens the validated desktop<br />store</div><div style='font-size: 70%'></div>" .->23
    23-. "<div>Queries collections and<br />commits local transactions</div><div style='font-size: 70%'></div>" .->25
    11-. "<div>Resolves preferences</div><div style='font-size: 70%'></div>" .->13
    11-. "<div>Queries Library views and<br />validates playable selections</div><div style='font-size: 70%'></div>" .->14
    14-. "<div>Reads indexed catalog and<br />availability projections</div><div style='font-size: 70%'></div>" .->23
    11-. "<div>Starts a draft, changes<br />order, or saves a playlist</div><div style='font-size: 70%'></div>" .->15
    15-. "<div>Stores the draft and<br />operation identity</div><div style='font-size: 70%'></div>" .->23
    15-. "<div>Hands off saved playlist<br />changes for replication</div><div style='font-size: 70%'></div>" .->22
    15-. "<div>Reads the current queue<br />snapshot for Save as playlist</div><div style='font-size: 70%'></div>" .->16
    11-. "<div>Executes transport and mixer<br />actions</div><div style='font-size: 70%'></div>" .->16
    16-. "<div>Restores and commits the<br />current queue</div><div style='font-size: 70%'></div>" .->23
    11-. "<div>Submits host commands or<br />requests current job state</div><div style='font-size: 70%'></div>" .->20
    20-. "<div>Uploads copies, submits typed<br />commands, and reads job<br />status</div><div style='font-size: 70%'>[HTTPS JSON and streamed files]</div>" .->26
    21-. "<div>Long polls authenticated<br />Android arrival frames</div><div style='font-size: 70%'></div>" .->20
    21-. "<div>Applies live arrival and<br />selects the device in Normal<br />Sync</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>Publishes typed outcomes</div><div style='font-size: 70%'></div>" .->10
    10-. "<div>Schedules immutable UI update</div><div style='font-size: 70%'></div>" .->9
    9-. "<div>Applies state on UIThread</div><div style='font-size: 70%'></div>" .->8
    13-. "<div>Reads and writes local<br />preference overrides</div><div style='font-size: 70%'></div>" .->23
    13-. "<div>Configures log levels and<br />sinks</div><div style='font-size: 70%'></div>" .->24
    11-. "<div>Logs outcomes within<br />correlation scopes</div><div style='font-size: 70%'></div>" .->24
    16-. "<div>Executes built-in playback</div><div style='font-size: 70%'></div>" .->17
    16-. "<div>Executes optional CLI<br />playback</div><div style='font-size: 70%'></div>" .->18
    17-. "<div>Outputs mixed audio</div><div style='font-size: 70%'></div>" .->5
    18-. "<div>Invokes direct arguments or<br />sends stdio requests</div><div style='font-size: 70%'></div>" .->40
    16-. "<div>Publishes bounded<br />visualization telemetry</div><div style='font-size: 70%'></div>" .->19
    19-. "<div>Exchanges validated IPC</div><div style='font-size: 70%'></div>" .->39
    19-. "<div>Submits extension intents and<br />status</div><div style='font-size: 70%'></div>" .->11
    19-. "<div>Forwards worker diagnostics</div><div style='font-size: 70%'></div>" .->24
    11-. "<div>Starts record operations</div><div style='font-size: 70%'></div>" .->22
    22-. "<div>Commits applied pages and<br />checkpoints</div><div style='font-size: 70%'></div>" .->23
    22-. "<div>Registers, fetches changes<br />and acknowledges</div><div style='font-size: 70%'></div>" .->26
    26-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->22
    22-. "<div>Reports sync and peer events</div><div style='font-size: 70%'></div>" .->11

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
    5["<div style='font-weight: bold'>Operating system</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Provides local credentials,<br />certificates, preferences<br />paths, windows, hotkeys, and<br />audio output.</div>"]
    style 5 fill:#ffffff,stroke:#444444,color:#444444

    subgraph 6 ["Beets Man"]
      style 6 fill:#ffffff,stroke:#444444,color:#444444

      subgraph 26 ["Library host"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444

        27["<div style='font-weight: bold'>Library host startup</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Loads reference-only<br />configuration, validates<br />local store and export paths,<br />and starts one HTTPS listener<br />with authenticated routes and<br />durable transfer workers.</div>"]
        style 27 fill:#ffffff,stroke:#444444,color:#444444
        28["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 28 fill:#ffffff,stroke:#444444,color:#444444
        29["<div style='font-weight: bold'>Import staging</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Accepts uploaded copies and<br />exposes staged inputs to<br />guided import. Keeps drafts<br />and temporary files separate<br />from the published library.</div>"]
        style 29 fill:#ffffff,stroke:#444444,color:#444444
        30["<div style='font-weight: bold'>beets job coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Journals, executes, and<br />reconciles external library<br />writes.</div>"]
        style 30 fill:#ffffff,stroke:#444444,color:#444444
        31["<div style='font-weight: bold'>Catalog and revision feed</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Projects beets records and<br />publishes consistent<br />snapshots and change pages.</div>"]
        style 31 fill:#ffffff,stroke:#444444,color:#444444
        32["<div style='font-weight: bold'>Notification queue</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Persists committed changes<br />and tracks acknowledged<br />recipients.</div>"]
        style 32 fill:#ffffff,stroke:#444444,color:#444444
        33["<div style='font-weight: bold'>Device export manager</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Stages tracks and ordered<br />playlists. Reviews layout<br />changes and owned removals<br />while the active profile<br />remains assigned. Commits the<br />new profile after verified<br />file effects.</div>"]
        style 33 fill:#ffffff,stroke:#444444,color:#444444
        34["<div style='font-weight: bold'>Media directory and pairing</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Scopes an approval to an<br />active peer and configured<br />destination. Persists the<br />original response and initial<br />Android profile, then<br />refreshes Android acceptance<br />from the approved folder.</div>"]
        style 34 fill:#ffffff,stroke:#444444,color:#444444
        35["<div style='font-weight: bold'>Syncthing adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Offers the approved send-only<br />folder. Verifies Android<br />receiver acceptance and scope<br />before and after index reads.<br />Reads PC mirror progress,<br />applies scoped controls, and<br />forwards live peer<br />transitions.</div>"]
        style 35 fill:#ffffff,stroke:#444444,color:#444444
        36["<div style='font-weight: bold'>Android arrival relay</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Publishes current registered<br />Android connections with live<br />arrival revisions. Reconciles<br />native restart or gaps<br />without replaying historical<br />arrivals.</div>"]
        style 36 fill:#ffffff,stroke:#444444,color:#444444
        37["<div style='font-weight: bold'>Host repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps authoritative documents,<br />operation receipts, revisions<br />and delivery records to host<br />LiteDB collections. Runs<br />atomic host writes.</div>"]
        style 37 fill:#ffffff,stroke:#444444,color:#444444
      end

      38["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 38 fill:#ffffff,stroke:#444444,color:#444444
      7["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 7 fill:#ffffff,stroke:#444444,color:#444444
    end

    27-. "<div>Reads scoped credentials and<br />the TLS certificate</div><div style='font-size: 70%'></div>" .->5
    27-. "<div>Opens and owns the configured<br />LiteDB store</div><div style='font-size: 70%'></div>" .->37
    27-. "<div>Binds the registered share<br />roots and workers</div><div style='font-size: 70%'></div>" .->33
    28-. "<div>Dispatches beets jobs</div><div style='font-size: 70%'></div>" .->30
    28-. "<div>Reads registered instance<br />state and authorized Android<br />bindings</div><div style='font-size: 70%'></div>" .->37
    37-. "<div>Queries collections and<br />commits host transactions</div><div style='font-size: 70%'></div>" .->38
    28-. "<div>Authenticates and accepts<br />uploaded album copies</div><div style='font-size: 70%'></div>" .->29
    30-. "<div>Consumes a completed staged<br />upload by ID</div><div style='font-size: 70%'></div>" .->29
    30-. "<div>Persists job intent and<br />outcome</div><div style='font-size: 70%'></div>" .->37
    30-. "<div>Executes or reconciles an<br />operation</div><div style='font-size: 70%'></div>" .->2
    30-. "<div>Publishes reconciled state</div><div style='font-size: 70%'></div>" .->31
    28-. "<div>Queries revision pages and<br />snapshots</div><div style='font-size: 70%'></div>" .->31
    31-. "<div>Commits authoritative<br />records, revisions and<br />delivery records together</div><div style='font-size: 70%'></div>" .->37
    31-. "<div>Enqueues correlated<br />publication</div><div style='font-size: 70%'></div>" .->32
    32-. "<div>Stores recipient progress</div><div style='font-size: 70%'></div>" .->37
    28-. "<div>Pulls and acknowledges queued<br />notifications</div><div style='font-size: 70%'></div>" .->32
    28-. "<div>Edits staged plans, prepares<br />and publishes reviewed plans,<br />and reads or retries<br />transfers</div><div style='font-size: 70%'></div>" .->33
    28-. "<div>Reads registered media state<br />and dispatches pairing and<br />mirror commands</div><div style='font-size: 70%'></div>" .->34
    34-. "<div>Offers the folder, verifies<br />receiver acceptance and<br />mirror progress, and applies<br />scoped controls</div><div style='font-size: 70%'></div>" .->35
    34-. "<div>Reserves approvals and stores<br />bindings, observations and<br />operation receipts</div><div style='font-size: 70%'></div>" .->37
    33-. "<div>Reads ordered playlist<br />entries and source track<br />identities</div><div style='font-size: 70%'></div>" .->31
    33-. "<div>Commits prepared preview<br />identity, operation receipts,<br />progress and export ownership</div><div style='font-size: 70%'></div>" .->37
    33-. "<div>Reads receiver inventory and<br />dispatches durable scans for<br />committed sender folders</div><div style='font-size: 70%'></div>" .->35
    35-. "<div>Offers scoped folders,<br />controls PC mirrors and<br />observes connections</div><div style='font-size: 70%'></div>" .->3
    35-. "<div>Publishes confirmed<br />connections from the<br />continuous native event<br />stream</div><div style='font-size: 70%'></div>" .->36
    28-. "<div>Waits for authenticated<br />Android connection<br />observations</div><div style='font-size: 70%'></div>" .->36
    7-. "<div>Uses playback, hotkeys, and<br />credential services</div><div style='font-size: 70%'></div>" .->5
    7-. "<div>Uploads copies, submits typed<br />commands, and reads job<br />status</div><div style='font-size: 70%'>[HTTPS JSON and streamed files]</div>" .->28
    28-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->7
    27-. "<div>Registers the authenticated<br />host routes</div><div style='font-size: 70%'></div>" .->28

  end
```

## DesktopLiteDbEntities

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Desktop state LiteDB entities"]
    style diagram fill:#ffffff,stroke:#ffffff

    126["<div style='font-weight: bold'>Desktop / catalog_artists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ArtistId (Guid).<br />LibraryId: Guid.<br />SourceRevision: Int64. Name<br />and SortName: string.</div>"]
    style 126 fill:#ffffff,stroke:#444444,color:#444444
    127["<div style='font-weight: bold'>Desktop / catalog_albums</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: AlbumId (Guid).<br />LibraryId: Guid.<br />AlbumArtistId: Guid?.<br />SourceRevision: Int64. Title<br />and SortTitle: string.</div>"]
    style 127 fill:#ffffff,stroke:#444444,color:#444444
    128["<div style='font-weight: bold'>Desktop / catalog_tracks</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: TrackId (Guid).<br />LibraryId: Guid. ArtistId and<br />AlbumId: Guid?.<br />SourceRevision: Int64. Title:<br />string. RelativePath and<br />Genre: string?. TrackNumber<br />and Rating: Int32?.<br />DurationTicks: Int64?. Tags:<br />versioned tag document.</div>"]
    style 128 fill:#ffffff,stroke:#444444,color:#444444
    129["<div style='font-weight: bold'>Desktop / catalog_playlists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlaylistId (Guid).<br />LibraryId: Guid.<br />SourceRevision and<br />PlaylistRevision: Int64.<br />Name: string. Entries:<br />ordered { EntryId: Guid,<br />TrackId: Guid } list.</div>"]
    style 129 fill:#ffffff,stroke:#444444,color:#444444
    130["<div style='font-weight: bold'>Desktop / listening_records</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlayId or (LibraryId,<br />ListenerId, TrackId) for<br />favorites, distinguished by<br />kind. LibraryId, ListenerId<br />and TrackId: Guid. Kind: Play<br />or Favorite. SourceRevision:<br />Int64. PlayedAtUtc: DateTime<br />for plays.</div>"]
    style 130 fill:#ffffff,stroke:#444444,color:#444444
    131["<div style='font-weight: bold'>Desktop / shared_preference_cache</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />PreferenceKey). LibraryId:<br />Guid. PreferenceKey: string.<br />RegistryVersion: Int32.<br />SourceRevision: Int64. Value:<br />registry-typed value.</div>"]
    style 131 fill:#ffffff,stroke:#444444,color:#444444
    132["<div style='font-weight: bold'>Desktop / playlist_drafts</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: DraftId (Guid).<br />PlaylistId: Guid?.<br />ExpectedHostRevision: Int64?.<br />DraftRevision: Int64.<br />ProposedName: string.<br />Entries: ordered { EntryId:<br />Guid, TrackId: Guid } list.</div>"]
    style 132 fill:#ffffff,stroke:#444444,color:#444444
    133["<div style='font-weight: bold'>Desktop / playback_sessions</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: SessionId (Guid).<br />CheckpointRevision,<br />QueueRevision and<br />PositionTicks: Int64.<br />ActiveEntryId: Guid?.<br />Shuffle: Boolean. Repeat:<br />string enum. Queue: ordered {<br />EntryId: Guid, TrackId: Guid<br />} list. PlaylistBacking:<br />optional { PlaylistId: Guid,<br />Revision: Int64, Name:<br />string, LastSave: operation<br />key? }.</div>"]
    style 133 fill:#ffffff,stroke:#444444,color:#444444
    134["<div style='font-weight: bold'>Desktop / preference_overrides</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PreferenceKey (string).<br />RegistryVersion: Int32.<br />LocalRevision: Int64. Value:<br />registry-typed value.</div>"]
    style 134 fill:#ffffff,stroke:#444444,color:#444444
    135["<div style='font-weight: bold'>Desktop / pending_operations</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (OriginInstanceId,<br />OperationId). Both IDs: Guid.<br />CommandType and State:<br />string. Payload: versioned<br />intent and receipt.<br />ExpectedRevision: Int64?.<br />DraftId and QueueSessionId:<br />Guid?. A queue save payload<br />carries queue revision,<br />intent hash, predecessor key<br />and revision pin state.</div>"]
    style 135 fill:#ffffff,stroke:#444444,color:#444444
    136["<div style='font-weight: bold'>Desktop / sync_checkpoints</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId, Feed).<br />LibraryId: Guid. Feed:<br />string. AppliedRevision:<br />Int64. Cursor: string?.</div>"]
    style 136 fill:#ffffff,stroke:#444444,color:#444444
    137["<div style='font-weight: bold'>Desktop / store_schema</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: singleton 'store'.<br />StoreId: Guid. StoreRole:<br />Desktop. SchemaVersion:<br />Int32.</div>"]
    style 137 fill:#ffffff,stroke:#444444,color:#444444

    127-. "<div>Optional AlbumArtistId</div><div style='font-size: 70%'></div>" .->126
    128-. "<div>Optional ArtistId</div><div style='font-size: 70%'></div>" .->126
    128-. "<div>Optional AlbumId</div><div style='font-size: 70%'></div>" .->127
    129-. "<div>Ordered entries by TrackId</div><div style='font-size: 70%'></div>" .->128
    130-. "<div>TrackId</div><div style='font-size: 70%'></div>" .->128
    132-. "<div>Optional target PlaylistId</div><div style='font-size: 70%'></div>" .->129
    132-. "<div>Ordered draft entries by<br />TrackId</div><div style='font-size: 70%'></div>" .->128
    133-. "<div>Ordered queue entries by<br />TrackId</div><div style='font-size: 70%'></div>" .->128
    135-. "<div>Optional DraftId retained for<br />reconciliation</div><div style='font-size: 70%'></div>" .->132
    135-. "<div>Optional QueueSessionId for<br />ordered auto-save replay</div><div style='font-size: 70%'></div>" .->133
    136-. "<div>Commits with the applied<br />artist page</div><div style='font-size: 70%'></div>" .->126
    136-. "<div>Commits with the applied<br />album page</div><div style='font-size: 70%'></div>" .->127
    136-. "<div>Commits with the applied<br />track page</div><div style='font-size: 70%'></div>" .->128
    136-. "<div>Commits with the applied<br />playlist page</div><div style='font-size: 70%'></div>" .->129
    136-. "<div>Commits with the applied<br />listening records</div><div style='font-size: 70%'></div>" .->130
    136-. "<div>Commits with the applied<br />shared defaults</div><div style='font-size: 70%'></div>" .->131
    134-. "<div>Resolves the next preference<br />source by key</div><div style='font-size: 70%'></div>" .->131

  end
```

## HostLiteDbEntities

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Host state LiteDB entities"]
    style diagram fill:#ffffff,stroke:#ffffff

    138["<div style='font-weight: bold'>Host / catalog_artists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ArtistId (Guid).<br />LibraryId: Guid. Revision:<br />Int64. Name and SortName:<br />string.</div>"]
    style 138 fill:#ffffff,stroke:#444444,color:#444444
    139["<div style='font-weight: bold'>Host / catalog_albums</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: AlbumId (Guid).<br />LibraryId: Guid.<br />AlbumArtistId: Guid?.<br />Revision: Int64. Title and<br />SortTitle: string.</div>"]
    style 139 fill:#ffffff,stroke:#444444,color:#444444
    140["<div style='font-weight: bold'>Host / catalog_tracks</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: TrackId (Guid).<br />LibraryId: Guid. ArtistId and<br />AlbumId: Guid?. Revision:<br />Int64. BeetsTrackId: Int64?.<br />Title: string. RelativePath<br />and Genre: string?.<br />TrackNumber and Rating:<br />Int32?. DurationTicks:<br />Int64?. Tags: versioned tag<br />document.</div>"]
    style 140 fill:#ffffff,stroke:#444444,color:#444444
    141["<div style='font-weight: bold'>Host / saved_playlists</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlaylistId (Guid).<br />LibraryId: Guid. Revision:<br />Int64. Name: string. Entries:<br />ordered { EntryId: Guid,<br />TrackId: Guid } list.</div>"]
    style 141 fill:#ffffff,stroke:#444444,color:#444444
    142["<div style='font-weight: bold'>Host / listening_records</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PlayId or (LibraryId,<br />ListenerId, TrackId) for<br />favorites, distinguished by<br />kind. LibraryId, ListenerId<br />and TrackId: Guid. Kind: Play<br />or Favorite. Revision: Int64.<br />PlayedAtUtc: DateTime for<br />plays.</div>"]
    style 142 fill:#ffffff,stroke:#444444,color:#444444
    143["<div style='font-weight: bold'>Host / shared_preferences</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />PreferenceKey). LibraryId:<br />Guid. PreferenceKey: string.<br />RegistryVersion: Int32.<br />Revision: Int64. Value:<br />registry-typed value.</div>"]
    style 143 fill:#ffffff,stroke:#444444,color:#444444
    144["<div style='font-weight: bold'>Host / registered_instances</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: InstanceId (Guid).<br />LibraryId: Guid. Role and<br />Status: string.<br />SyncthingDeviceId and<br />PendingMediaPairing: string?.<br />AndroidExport: optional {<br />Revision: Int64, ShareId and<br />ReceiverEndpoint: string,<br />CaseSensitivePaths: Boolean,<br />ReceiverRoot,<br />ApprovedSenderId and<br />InitialProfileId: string?,<br />InitialProfileRevision:<br />Int64?,<br />ReceiverAcceptedAtOffer:<br />Boolean?, ReceiverSharePhase<br />and ReceiverFailureCode:<br />string? }.</div>"]
    style 144 fill:#ffffff,stroke:#444444,color:#444444
    145["<div style='font-weight: bold'>Host / library_jobs</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: JobId (Guid).<br />OriginInstanceId and<br />OperationId: Guid. Kind and<br />Status: string. UploadId:<br />Guid?. ExpectedRevision and<br />PublishedRevision: Int64?.</div>"]
    style 145 fill:#ffffff,stroke:#444444,color:#444444
    146["<div style='font-weight: bold'>Host / operation_receipts</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (OriginInstanceId,<br />OperationId). Both IDs: Guid.<br />IntentFingerprint: string.<br />Receipt: typed, versioned<br />outcome.</div>"]
    style 146 fill:#ffffff,stroke:#444444,color:#444444
    147["<div style='font-weight: bold'>Host / revision_heads</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: LibraryId (Guid).<br />CurrentRevision and<br />MinRetainedRevision: Int64.</div>"]
    style 147 fill:#ffffff,stroke:#444444,color:#444444
    148["<div style='font-weight: bold'>Host / revision_changes</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId, Revision).<br />LibraryId, OriginInstanceId<br />and OperationId: Guid.<br />Revision: Int64. Changes:<br />versioned entity updates or<br />tombstones.</div>"]
    style 148 fill:#ffffff,stroke:#444444,color:#444444
    149["<div style='font-weight: bold'>Host / notification_deliveries</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId,<br />RecipientInstanceId,<br />Revision). Both IDs: Guid.<br />Revision: Int64. Status:<br />Pending or Acknowledged.</div>"]
    style 149 fill:#ffffff,stroke:#444444,color:#444444
    150["<div style='font-weight: bold'>Host / device_exports</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: ExportId (Guid).<br />LibraryId and DeviceId: Guid.<br />ScopeKey: unique library,<br />device and profile key.<br />ShareId and ProfileId:<br />string. ProfileVersion and<br />ManifestRevision (staged<br />revision): Int64.<br />CaseSensitivePaths: Boolean.<br />PublicationState: string.<br />PreparedPreviewIdentity:<br />optional SHA-256 digest of<br />the private preview<br />descriptor.</div>"]
    style 150 fill:#ffffff,stroke:#444444,color:#444444
    151["<div style='font-weight: bold'>Host / device_export_entries</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (ExportId,<br />hash(PathKey)). ExportId,<br />EntryId and SourceEntityId:<br />Guid. RelativePath and<br />PathKey: string. Ordinal:<br />Int32. SourceKind: Playlist<br />or Audio. Playlist: Format,<br />ContentIdentity and ordered {<br />TrackId, Destination,<br />AudioStatus } references.<br />Audio: AudioStatus and<br />optional IsDirect: Boolean.</div>"]
    style 151 fill:#ffffff,stroke:#444444,color:#444444
    152["<div style='font-weight: bold'>Host / device_export_manifests</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (LibraryId, DeviceId,<br />hash(ShareId)). LibraryId,<br />DeviceId and PublicationId:<br />Guid. ShareId: string.<br />Revision: Int64. Active<br />layout: ProfileId and<br />ProfileRevision, optional on<br />legacy manifests.</div>"]
    style 152 fill:#ffffff,stroke:#444444,color:#444444
    153["<div style='font-weight: bold'>Host / device_export_owned_files</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (ManifestKey,<br />hash(PathKey)). ManifestKey,<br />PathKey, RelativePath and<br />ContentIdentity: string.<br />SourceEntityId: Guid.<br />SourceKind: Int32.</div>"]
    style 153 fill:#ffffff,stroke:#444444,color:#444444
    154["<div style='font-weight: bold'>Host / device_export_publication_journal</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: PublicationId (Guid).<br />ManifestKey: unique share<br />scope. LibraryId, DeviceId,<br />PlanId, OriginInstanceId and<br />OperationId: Guid. ProfileId:<br />string. PlanRevision,<br />ProfileRevision and<br />ManifestRevision: Int64.<br />Destination: receiver<br />registration snapshot.<br />NextStep, StepCount and<br />OwnedFileCount: Int32.<br />LayoutEntryCount: optional<br />Int32. NextProfileId and<br />NextProfileRevision: optional<br />reviewed assignment committed<br />after file effects.</div>"]
    style 154 fill:#ffffff,stroke:#444444,color:#444444
    155["<div style='font-weight: bold'>Host / device_export_publication_items</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (PublicationId, Kind,<br />Ordinal). PublicationId:<br />Guid. Kind: Step, NextOwned<br />or LayoutEntry. Ordinal:<br />Int32. Payload: versioned<br />file operation, owned file,<br />or reviewed staged entry and<br />playlist digest.</div>"]
    style 155 fill:#ffffff,stroke:#444444,color:#444444
    156["<div style='font-weight: bold'>Host / device_export_scans</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: TransferId (the<br />PublicationId, Guid).<br />ManifestKey: registered share<br />scope. LibraryId and PlanId:<br />Guid. PlanRevision,<br />ManifestRevision and<br />Revision: Int64. Destination:<br />receiver registration<br />snapshot. State: queued,<br />awaiting receiver, completed,<br />blocked, or superseded.<br />Attempts and optional<br />ItemCount: Int32.<br />BytesVerified, optional<br />TotalBytes, DueUtcTicks and<br />optional LeaseUntilUtcTicks:<br />Int64. Optional LeaseId and<br />FailureCode.</div>"]
    style 156 fill:#ffffff,stroke:#444444,color:#444444
    157["<div style='font-weight: bold'>Host / device_export_scan_items</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: (TransferId,<br />hash(Path)). TransferId:<br />Guid. Path: string.<br />ContentIdentity: SHA-256 of<br />the requested file, or null<br />for removal. Bytes and<br />Version: optional sender<br />index evidence pinned before<br />observing the receiver.</div>"]
    style 157 fill:#ffffff,stroke:#444444,color:#444444
    158["<div style='font-weight: bold'>Host / pc_mirror_states</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: LibraryId (Guid).<br />Revision: Int64 for the<br />projection. Observations: by<br />ShareId, with native scope<br />fingerprint, control<br />revision, connection<br />generation, last verified<br />status and progress, and<br />optional pending operation<br />key.</div>"]
    style 158 fill:#ffffff,stroke:#444444,color:#444444
    159["<div style='font-weight: bold'>Host / store_schema</div><div style='font-size: 70%; margin-top: 0px'>[LiteDB collection]</div><div style='font-size: 80%; margin-top:10px'>_id: singleton 'store'.<br />StoreId: Guid. StoreRole:<br />Host. SchemaVersion: Int32.</div>"]
    style 159 fill:#ffffff,stroke:#444444,color:#444444

    139-. "<div>Optional AlbumArtistId</div><div style='font-size: 70%'></div>" .->138
    140-. "<div>Optional ArtistId</div><div style='font-size: 70%'></div>" .->138
    140-. "<div>Optional AlbumId</div><div style='font-size: 70%'></div>" .->139
    141-. "<div>Ordered entries by TrackId</div><div style='font-size: 70%'></div>" .->140
    142-. "<div>TrackId</div><div style='font-size: 70%'></div>" .->140
    145-. "<div>OriginInstanceId</div><div style='font-size: 70%'></div>" .->144
    145-. "<div>Submission receipt by origin<br />and operation ID</div><div style='font-size: 70%'></div>" .->146
    145-. "<div>PublishedRevision when<br />reconciled</div><div style='font-size: 70%'></div>" .->148
    146-. "<div>OriginInstanceId</div><div style='font-size: 70%'></div>" .->144
    146-. "<div>Feed revision in a playlist<br />save receipt</div><div style='font-size: 70%'></div>" .->148
    146-. "<div>Plan ID and original plan<br />revision in a device staging<br />receipt</div><div style='font-size: 70%'></div>" .->150
    148-. "<div>LibraryId and issued revision</div><div style='font-size: 70%'></div>" .->147
    148-. "<div>Versioned playlist updates<br />and tombstones</div><div style='font-size: 70%'></div>" .->141
    149-. "<div>LibraryId and revision to<br />deliver</div><div style='font-size: 70%'></div>" .->148
    149-. "<div>RecipientInstanceId</div><div style='font-size: 70%'></div>" .->144
    150-. "<div>Registered DeviceId</div><div style='font-size: 70%'></div>" .->144
    151-. "<div>ExportId</div><div style='font-size: 70%'></div>" .->150
    151-. "<div>SourceEntityId when<br />SourceKind is Playlist</div><div style='font-size: 70%'></div>" .->141
    151-. "<div>SourceEntityId when<br />SourceKind is Audio</div><div style='font-size: 70%'></div>" .->140
    152-. "<div>Registered DeviceId</div><div style='font-size: 70%'></div>" .->144
    153-. "<div>ManifestKey</div><div style='font-size: 70%'></div>" .->152
    153-. "<div>SourceEntityId when<br />SourceKind is Playlist</div><div style='font-size: 70%'></div>" .->141
    153-. "<div>SourceEntityId when<br />SourceKind is Audio</div><div style='font-size: 70%'></div>" .->140
    154-. "<div>PlanId and recorded plan<br />revision</div><div style='font-size: 70%'></div>" .->150
    154-. "<div>ManifestKey and base<br />ownership revision</div><div style='font-size: 70%'></div>" .->152
    155-. "<div>PublicationId</div><div style='font-size: 70%'></div>" .->154
    156-. "<div>ManifestKey and queued<br />ownership revision</div><div style='font-size: 70%'></div>" .->152
    156-. "<div>PlanId and published plan<br />revision</div><div style='font-size: 70%'></div>" .->150
    157-. "<div>TransferId and exact<br />published effects</div><div style='font-size: 70%'></div>" .->156
    146-. "<div>Reservation receipt before<br />file effects</div><div style='font-size: 70%'></div>" .->154
    146-. "<div>Committed device publication<br />and manifest revision</div><div style='font-size: 70%'></div>" .->152
    146-. "<div>Pending and completed PC<br />mirror controls</div><div style='font-size: 70%'></div>" .->158
    146-. "<div>Pairing reservation key and<br />the committed Android export<br />binding</div><div style='font-size: 70%'></div>" .->144
    158-. "<div>Directory revision advances<br />with changes in verified<br />Android folder status</div><div style='font-size: 70%'></div>" .->144

  end
```

## SelfHosted

```mermaid
graph LR
  linkStyle default fill:#ffffff

  subgraph diagram ["Deployment View: Self-hosted"]
    style diagram fill:#ffffff,stroke:#ffffff

    subgraph 211 ["Designated PC"]
      style 211 fill:#ffffff,stroke:#444444,color:#444444

      212["<div style='font-weight: bold'>Library host</div><div style='font-size: 70%; margin-top: 0px'>[Container: ASP.NET Core and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Authenticated HTTP API and<br />owner of synchronized<br />application state.</div>"]
      style 212 fill:#ffffff,stroke:#444444,color:#444444
      213["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 213 fill:#ffffff,stroke:#444444,color:#444444
    end

    subgraph 215 ["Registered desktop"]
      style 215 fill:#ffffff,stroke:#444444,color:#444444

      216["<div style='font-weight: bold'>Desktop app</div><div style='font-size: 70%; margin-top: 0px'>[Container: Avalonia 12 and .NET 10]</div><div style='font-size: 80%; margin-top:10px'>Local browsing, playback,<br />configuration, and user<br />actions.</div>"]
      style 216 fill:#ffffff,stroke:#444444,color:#444444
      219["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 219 fill:#ffffff,stroke:#444444,color:#444444
      221["<div style='font-weight: bold'>Plugin worker processes</div><div style='font-size: 70%; margin-top: 0px'>[Container: .NET 10 and versioned IPC]</div><div style='font-size: 80%; margin-top:10px'>Runs trusted local extensions<br />with desktop-user OS<br />permissions.</div>"]
      style 221 fill:#ffffff,stroke:#444444,color:#444444
      223["<div style='font-weight: bold'>Optional playback CLI process</div><div style='font-size: 70%; margin-top: 0px'>[Container: Process and structured stdio]</div><div style='font-size: 80%; margin-top:10px'>User-selected player or<br />adapter.</div>"]
      style 223 fill:#ffffff,stroke:#444444,color:#444444
    end

    subgraph 225 ["Android device"]
      style 225 fill:#ffffff,stroke:#444444,color:#444444

      226["<div style='font-weight: bold'>Android peer</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Syncthing-Fork receives a<br />device export. Musicolet<br />reads music and playlists.</div>"]
      style 226 fill:#ffffff,stroke:#444444,color:#444444
    end

    212-. "<div>Commits host state and<br />delivery state</div><div style='font-size: 70%'></div>" .->213
    216-. "<div>Sends authenticated commands<br />and sync requests</div><div style='font-size: 70%'>[HTTPS JSON]</div>" .->212
    212-. "<div>Forwards active Android peer<br />state</div><div style='font-size: 70%'></div>" .->216
    216-. "<div>Reads and commits desktop<br />state</div><div style='font-size: 70%'></div>" .->219
    216-. "<div>Exchanges telemetry,<br />declarations, commands, and<br />log events</div><div style='font-size: 70%'>[Versioned IPC]</div>" .->221
    216-. "<div>Invokes configured playback<br />actions</div><div style='font-size: 70%'></div>" .->223

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
        23["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 23 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
    end

    25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
    style 25 fill:#ffffff,stroke:#444444,color:#444444

    8-. "<div>1. Requests startup or<br />submits a repair</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Validates file, paths and<br />restart settings</div><div style='font-size: 70%'></div>" .->12
    12-. "<div>3. Resolves JSON and local<br />paths</div><div style='font-size: 70%'></div>" .->13
    12-. "<div>4. Opens the repository<br />boundary after validation</div><div style='font-size: 70%'></div>" .->23
    23-. "<div>5. Opens the store and<br />verifies its schema</div><div style='font-size: 70%'></div>" .->25
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
        23["<div style='font-weight: bold'>Desktop repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps owned desktop state and<br />materialized catalog queries<br />to local LiteDB collections.<br />Runs atomic local writes.</div>"]
        style 23 fill:#ffffff,stroke:#444444,color:#444444
        8["<div style='font-weight: bold'>Presentation boundary</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Avalonia binds MVVM Toolkit<br />ViewModels, typed CQRS<br />commands, and system wait<br />cursor state for logically<br />blocked scopes.</div>"]
        style 8 fill:#ffffff,stroke:#444444,color:#444444
        9["<div style='font-weight: bold'>Avalonia dispatcher adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Applies all<br />application-driven<br />presentation updates on<br />UIThread.</div>"]
        style 9 fill:#ffffff,stroke:#444444,color:#444444
      end

      25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
      style 25 fill:#ffffff,stroke:#444444,color:#444444
    end

    25["<div style='font-weight: bold'>Desktop state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Cache, preferences, queue,<br />drafts, and cursors.</div>"]
    style 25 fill:#ffffff,stroke:#444444,color:#444444

    8-. "<div>1. Requests a catalog view or<br />search</div><div style='font-size: 70%'></div>" .->11
    11-. "<div>2. Reads the selected Details<br />grid</div><div style='font-size: 70%'></div>" .->14
    14-. "<div>3. Queries indexed tracks and<br />media availability</div><div style='font-size: 70%'></div>" .->23
    23-. "<div>4. Reads the catalog<br />projection and revision</div><div style='font-size: 70%'></div>" .->25
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
        20["<div style='font-weight: bold'>Host library client</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Submits authenticated library<br />commands, controls registered<br />PC mirrors, prepares and<br />publishes Android exports,<br />and reads and retries<br />verified transfer checkpoints<br />over HTTPS.</div>"]
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

      subgraph 26 ["Library host"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444

        28["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 28 fill:#ffffff,stroke:#444444,color:#444444
        29["<div style='font-weight: bold'>Import staging</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Accepts uploaded copies and<br />exposes staged inputs to<br />guided import. Keeps drafts<br />and temporary files separate<br />from the published library.</div>"]
        style 29 fill:#ffffff,stroke:#444444,color:#444444
        30["<div style='font-weight: bold'>beets job coordinator</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Journals, executes, and<br />reconciles external library<br />writes.</div>"]
        style 30 fill:#ffffff,stroke:#444444,color:#444444
        31["<div style='font-weight: bold'>Catalog and revision feed</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Projects beets records and<br />publishes consistent<br />snapshots and change pages.</div>"]
        style 31 fill:#ffffff,stroke:#444444,color:#444444
        32["<div style='font-weight: bold'>Notification queue</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Persists committed changes<br />and tracks acknowledged<br />recipients.</div>"]
        style 32 fill:#ffffff,stroke:#444444,color:#444444
        37["<div style='font-weight: bold'>Host repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps authoritative documents,<br />operation receipts, revisions<br />and delivery records to host<br />LiteDB collections. Runs<br />atomic host writes.</div>"]
        style 37 fill:#ffffff,stroke:#444444,color:#444444
      end

      38["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
      style 38 fill:#ffffff,stroke:#444444,color:#444444
    end

    2["<div style='font-weight: bold'>beets</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Owns music tagging,<br />organization, and the source<br />catalog.</div>"]
    style 2 fill:#ffffff,stroke:#444444,color:#444444
    38["<div style='font-weight: bold'>Host state</div><div style='font-size: 70%; margin-top: 0px'>[Container: LiteDB]</div><div style='font-size: 80%; margin-top:10px'>Registrations, beets job<br />journal, catalog, shared<br />preferences, and notification<br />delivery.</div>"]
    style 38 fill:#ffffff,stroke:#444444,color:#444444

    28-. "<div>1. Accepts authenticated<br />album copies before an import</div><div style='font-size: 70%'></div>" .->29
    28-. "<div>2. Accepts an authorized<br />guided import choice</div><div style='font-size: 70%'></div>" .->30
    30-. "<div>3. Records job intent before<br />external effects</div><div style='font-size: 70%'></div>" .->37
    37-. "<div>4. Commits job identity and<br />intent</div><div style='font-size: 70%'></div>" .->38
    30-. "<div>5. Resolves the staged input<br />for an import job</div><div style='font-size: 70%'></div>" .->29
    30-. "<div>6. Executes or reconciles</div><div style='font-size: 70%'></div>" .->2
    30-. "<div>7. Publishes reconciled state</div><div style='font-size: 70%'></div>" .->31
    31-. "<div>8. Enqueues a committed<br />revision</div><div style='font-size: 70%'></div>" .->32
    31-. "<div>9. Saves reconciled catalog<br />changes, feed revision and<br />deliveries</div><div style='font-size: 70%'></div>" .->37
    37-. "<div>10. Commits publication as<br />one transaction</div><div style='font-size: 70%'></div>" .->38

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

      subgraph 26 ["Library host"]
        style 26 fill:#ffffff,stroke:#444444,color:#444444

        28["<div style='font-weight: bold'>Versioned host API</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Authenticates registered<br />devices and dispatches host<br />work.</div>"]
        style 28 fill:#ffffff,stroke:#444444,color:#444444
        35["<div style='font-weight: bold'>Syncthing adapter</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Offers the approved send-only<br />folder. Verifies Android<br />receiver acceptance and scope<br />before and after index reads.<br />Reads PC mirror progress,<br />applies scoped controls, and<br />forwards live peer<br />transitions.</div>"]
        style 35 fill:#ffffff,stroke:#444444,color:#444444
        36["<div style='font-weight: bold'>Android arrival relay</div><div style='font-size: 70%; margin-top: 0px'>[Component]</div><div style='font-size: 80%; margin-top:10px'>Publishes current registered<br />Android connections with live<br />arrival revisions. Reconciles<br />native restart or gaps<br />without replaying historical<br />arrivals.</div>"]
        style 36 fill:#ffffff,stroke:#444444,color:#444444
        37["<div style='font-weight: bold'>Host repositories</div><div style='font-size: 70%; margin-top: 0px'>[Component: LiteDB adapter]</div><div style='font-size: 80%; margin-top:10px'>Maps authoritative documents,<br />operation receipts, revisions<br />and delivery records to host<br />LiteDB collections. Runs<br />atomic host writes.</div>"]
        style 37 fill:#ffffff,stroke:#444444,color:#444444
      end

    end

    3["<div style='font-weight: bold'>Syncthing</div><div style='font-size: 70%; margin-top: 0px'>[Software System]</div><div style='font-size: 80%; margin-top:10px'>Moves media files between the<br />host and registered replicas.</div>"]
    style 3 fill:#ffffff,stroke:#444444,color:#444444

    35-. "<div>1. Reads live DeviceConnected<br />and DeviceDisconnected events</div><div style='font-size: 70%'></div>" .->3
    35-. "<div>2. Publishes the confirmed<br />connection after continuity<br />and registration checks</div><div style='font-size: 70%'></div>" .->36
    28-. "<div>3. Rechecks the authorized<br />Android bindings</div><div style='font-size: 70%'></div>" .->37
    28-. "<div>4. Returns the current<br />connection and live arrival<br />revision</div><div style='font-size: 70%'></div>" .->36

  end
```
