"""Build self-contained Asset Review SVGs from the wireframe sources and storyboards."""

from collections import defaultdict
from copy import deepcopy
from dataclasses import dataclass
from functools import cache
import json
from pathlib import Path
import re
from xml.etree import ElementTree as ET


DOCS = Path(__file__).resolve().parents[1]
DESIGN = DOCS / "design"
SOURCES = DOCS / "wireframes"
COMPONENTS = SOURCES / "shared"
MANIFEST = DOCS / "ui-asset-manifest.json"
NS = "http://www.w3.org/2000/svg"
ET.register_namespace("", NS)

INK = "#202724"
MUTED = "#4c5953"
LINE = "#d4dbd5"
PAPER = "#ffffff"
BG = "#f4f5f2"
GREEN = "#235943"
TINT = "#e0ede4"
WARN = "#fbeddc"
AMBER = "#704621"
BLUSH = "#f8e9e7"
RED = "#8e3431"
DARK = "#1e312a"


def tag(name):
    return "{" + NS + "}" + name


def element(name, **attributes):
    return ET.Element(tag(name), {key.replace("_", "-"): str(value) for key, value in attributes.items()})


@cache
def svg_source(path):
    return ET.parse(path).getroot()


@cache
def fragment(path, ident):
    matches = [node for node in svg_source(path).iter() if node.get("id") == ident]
    if len(matches) != 1:
        raise ValueError(f"Expected one SVG fragment {path}#{ident}, found {len(matches)}")
    return matches[0]


def expand_components(parent, source, trail=()):
    """Inline unpositioned group references before applying the storyboard edits."""
    children = []
    for node in parent:
        if node.tag != tag("use"):
            expand_components(node, source, trail)
            children.append(node)
            continue
        if set(node.attrib) - {"href", "data-omit-controls"}:
            raise ValueError(f"Unsupported component attributes in {source}: {node.attrib}")
        name, separator, ident = node.get("href", "").partition("#")
        path = (source.parent / name).resolve() if name else source
        reference = (path, ident)
        if not separator or not ident or path.suffix != ".svg" or not path.is_relative_to(SOURCES):
            raise ValueError(f"Invalid SVG component reference in {source}: {node.get('href')}")
        if reference in trail:
            raise ValueError(f"Recursive SVG component reference at {path}#{ident}")
        component = deepcopy(fragment(path, ident))
        if component.tag != tag("g"):
            raise ValueError(f"Expected an SVG group at {path}#{ident}")
        if set(component.attrib) - {"id", "font-family"}:
            raise ValueError(f"Put styling and transforms on the children of {path}#{ident}")
        expand_components(component, path, (*trail, reference))
        if node.get("data-omit-controls") == "true":
            for descendant in component.iter():
                descendant.attrib.pop("data-control-id", None)
        children.extend(component)
    parent[:] = children


def component_nodes(filename, ident):
    path = COMPONENTS / filename
    group = deepcopy(fragment(path, ident))
    expand_components(group, path, ((path, ident),))
    return list(group)


def load_screen(ident):
    path = SOURCES / "screens" / f"{ident}.svg"
    root = deepcopy(svg_source(path))
    expand_components(root, path)
    root.find(tag("g")).attrib.pop("id", None)
    return root


def rect(x, y, w, h, fill, stroke="none", radius=0):
    return element("rect", x=x, y=y, width=w, height=h, fill=fill, stroke=stroke, rx=radius)


def text(x, y, label, size=13, color=INK, bold=False):
    node = element("text", x=x, y=y, fill=color, font_size=size, font_weight=700 if bold else 400)
    node.text = label
    return node


def button(x, y, w, label, tone="primary", control=None, *, height=37):
    fill, border, color = {
        "primary": (GREEN, GREEN, PAPER),
        "normal": (PAPER, LINE, INK),
        "disabled": ("#e9eeea", LINE, MUTED),
    }[tone]
    label_y = y+24 if height == 37 else y+height/2+5
    label_element = text(x + w / 2, label_y, label, color=color, bold=True)
    label_element.set("text-anchor", "middle")
    if control:
        label_element.set("data-control-id", control)
    return [rect(x, y, w, height, fill, border, 5), label_element]


BUTTON_LABELS = {
    "add": ("Add selected","Initialize"),
    "album": ("Open album",),
    "back": ("Back","Exit mode"),
    "check": ("Apply","Confirm","Confirm moves","Use defaults","Use match","Use these locations"),
    "close": ("Cancel","Close","Close panel","Dismiss"),
    "disable": ("Disable","Revoke"),
    "edit": ("Edit CLI profile","Edit import defaults","Edit sync list"),
    "filter-clear": ("Clear filter",),
    "folder": ("Browse files","Browse…","Choose locations","Open category","Open library","Open log folder","Organize files"),
    "fullscreen": ("Exit full screen","Full screen"),
    "inherit": ("Inherit",),
    "install": ("Install package",),
    "key": ("Choose another","Reassign"),
    "layout": ("Select layout",),
    "list": ("Inspect","Preview","Preview export"),
    "list-add": ("Add to queue","New playlist","Queue album"),
    "log": ("Diagnostics","Open configuration","Open diagnostics","View configuration"),
    "next": ("Next","Next track"),
    "pair": ("Pair peer",),
    "pause": ("Pause","Ⅱ","Ⅱ  Pause"),
    "person": ("Open artist","Register"),
    "play": ("Play","Play result","Play selection","Play track","Resume","Run command"),
    "play-pause": ("Play / pause",),
    "previous": ("Previous","Seek start"),
    "refresh": ("Refresh","Refresh snapshot","Rescan","Restart worker","Retry"),
    "repeat": ("Repeat",),
    "repeat-off": ("Repeat: off",),
    "reorder": ("Reorder",),
    "restore": ("Reapply local","Reset","Restore defaults"),
    "save": ("Save","Save as playlist","Save playlist"),
    "search": ("Search library",),
    "search-clear": ("Clear search",),
    "share": ("Publish","Share files","Upload copies"),
    "shield": ("Trust locally",),
    "shuffle": ("Shuffle",),
    "skip": ("Cue A","Cue track","Skip"),
    "sliders": ("Choose engine","Master EQ","Set defaults","Toggle auto-fade"),
    "sync": ("Start sync","Switch"),
    "tag": ("Keep tags",),
    "trash": ("Confirm removal","Remove","Remove music"),
    "validate": ("Validate",),
    "window": ("Normal view","Open devices","Presentation: Normal"),
    "database": ("Keep host",),
}
ICONS_BY_BUTTON = {label: icon for icon, labels in BUTTON_LABELS.items() for label in labels}


def add_button_icons(root):
    if root.get("data-screen-id") in {"SCR01", "SCR02"}:
        return
    for group in list(root.iter(tag("g"))):
        nodes = list(group)
        updated = []
        for i,node in enumerate(nodes):
            updated.append(node)
            if i == 0 or node.tag != tag("text") or node.get("text-anchor") != "middle":
                continue
            box = nodes[i - 1]
            if box.tag != tag("rect") or not box.get("stroke") or box.get("rx") not in {"4","5"}:
                continue
            label = node.text
            if not label or label == "Space":
                continue
            icon = ICONS_BY_BUTTON[label]
            x,y,w,h = (float(box.get(k)) for k in ("x","y","width","height"))
            size = 18 if h >= 32 else 16
            icon_x = x+(w-size)/2
            accessible_name = "Pause" if label in {"Ⅱ","Ⅱ  Pause"} else label
            node.set("data-button-label","icon-only")
            node.set("visibility","hidden")
            glyph = element("g", transform=f"translate({icon_x:g} {y+(h-size)/2:g}) scale({size/24:g})",
                            data_button_icon=icon, role="img", aria_label=accessible_name)
            shape = deepcopy(fragment(COMPONENTS / "icons.svg", icon))
            del shape.attrib["id"]
            shape.set("stroke", node.get("fill", INK))
            glyph.append(shape)
            updated.append(glyph)
        group[:] = updated


def banner(x, y, w, title, detail, tone="info"):
    fill, color = {"info": (TINT, GREEN), "warn": (WARN, AMBER), "error": (BLUSH, RED)}[tone]
    return [rect(x, y, w, 56, fill, radius=5), rect(x, y, 3, 56, color),
            text(x+13, y+21, title, color=color, bold=True), text(x+13, y+42, detail, 12, color)]


def progress(x, y, w, percent, dark=False):
    return [rect(x, y, w, 6, "#526b60" if dark else "#e9eeea", radius=3),
            rect(x, y, w * percent / 100, 6, "#a7d8b6" if dark else GREEN, radius=3)]


def field(x, y, w, label, value):
    return [text(x, y, label, 12, MUTED, True), rect(x, y+10, w, 36, PAPER, LINE, 4),
            text(x+11, y+33, value)]


LIBRARY_PAGES = {5: "Overview", 6: "Artists", 7: "Albums", 8: "Tracks", 9: "Search", 10: "Playlists"}
TRACKS = (
    ("Harbor Lights", "Lena Vale", "Northern Lines", "4:26"),
    ("Night Crossing", "Lena Vale", "Northern Lines", "3:58"),
    ("Ridge Line", "Lena Vale", "Northern Lines", "5:08"),
    ("Low Current", "Juniper Static", "Low Current", "3:43"),
    ("Branch Signal", "The Switchyards", "Quiet Stations", "4:02"),
)
TRACKS_NAMES = tuple(track[0] for track in TRACKS)


def rule(x1, y1, x2, y2):
    return element("line", x1=x1, y1=y1, x2=x2, y2=y2, stroke=LINE)


def library_navigation(page, saved=None):
    nodes = component_nodes("library.svg", f"sidebar-{page.lower()}")
    if saved:
        nodes.append(text(23,519,saved,13,GREEN,True))
    return nodes


def library_shell(page, saved):
    return [*component_nodes("app-chrome.svg","library-workspace-header"),
            *library_navigation(page,saved),*component_nodes("transport.svg","library-transport")]


def library_track_rows(rows, selected, pending=False, playing=False):
    nodes = [rect(204,242,564,34,"#e9eeea",LINE,4)]
    for x,label in [(239,"Track"),(463,"Artist"),(611,"Media"),(717,"Time")]:
        nodes.append(text(x,263,label,12,MUTED,True))
    for i,(name,artist,_,duration) in enumerate(rows):
        y=276+i*46
        active=name in selected
        availability="Pending" if pending and name=="Night Crossing" else "Downloaded"
        nodes += [rect(204,y,564,46,TINT if active else PAPER,LINE),
                  text(217,y+28,"▶" if playing and name=="Harbor Lights" else "⠿",13,GREEN if active else MUTED),
                  text(239,y+28,name,14,INK,active),text(463,y+28,artist,12,MUTED),
                  text(611,y+28,availability,12,AMBER if availability=="Pending" else GREEN),
                  text(717,y+28,duration,12,MUTED)]
    return nodes


LIBRARY_ALBUMS = (("Northern Lines","Lena Vale","#cbd9cd"),
                  ("Low Current","Juniper Static","#d8d2bf"),
                  ("Quiet Stations","The Switchyards","#c9d5dd"))


def album_tiles(albums=LIBRARY_ALBUMS, selected=None):
    nodes = []
    for i,(title,artist,color) in enumerate(albums):
        x=204+i*192
        nodes += [rect(x,244,180,189,TINT if title==selected else PAPER,GREEN if title==selected else LINE,5),
                  rect(x+10,254,160,120,color,radius=4),
                  element("circle",cx=x+119,cy=280,r=15,fill="#f0f0df"),
                  element("path",d=f"M{x+24} 352l34 -38 26 17 25 -29 43 50z",fill="#668276"),
                  text(x+10,400,title,14,INK,True),text(x+10,421,artist,12,MUTED)]
    return nodes


def library_catalog(screen, page, selected, rows, offline, query, busy, activity, playing, host_actions):
    heading = "Library" if page=="Overview" else "Search library" if page=="Search" else page
    details = {
        "Overview":"Browse music and build a list alongside the catalog.",
        "Artists":"Browse by album artist.",
        "Albums":"Choose an album to see its tracks.",
        "Tracks":"Cached library · Host offline" if offline else "Music library · Studio host connected",
        "Search":"Find tracks, albums and artists across your library.",
        "Playlists":"Saved lists in your music library.",
    }
    nodes = [text(204,111,heading,27,bold=True),text(204,140,details[page],13,MUTED)]
    control = lambda i: f"{screen}.C{i:02}"
    if page in ("Overview","Albums","Artists"):
        if page == "Overview":
            nodes += button(204,174,139,"Search library","normal",control(1))
            nodes += button(354,174,124,"Open album","normal",control(2))
        elif page == "Albums":
            nodes += button(204,174,123,"Open album","normal",control(1))
            nodes += button(338,174,125,"Queue album","normal",control(2))
        else:
            nodes += button(204,174,122,"Open artist","normal",control(1))
            nodes += button(337,174,128,"Play selection","normal",control(2))
        if page == "Artists":
            nodes += album_tiles((LIBRARY_ALBUMS[0],("Blue Meridian","Lena Vale","#d0d9d5")))
            nodes += [text(206,471,"Lena Vale",17,INK,True),text(206,494,"Northern Lines and Blue Meridian · 19 tracks",13,MUTED)]
        else:
            nodes += album_tiles(selected="Northern Lines" if page=="Albums" else None)
    elif page == "Search":
        nodes += field(204,167,421,"Search library",query)
        nodes += button(636,176,132,"Clear search","normal",control(1))
        if busy:
            nodes += [text(204,272,"Searching…",17,bold=True),*progress(204,292,292,35)]
        else:
            nodes += library_track_rows(rows,selected,offline)
            nodes += button(204,533,125,"Play result","normal",control(2))
    elif page == "Playlists":
        nodes += button(204,174,124,"New playlist","normal",control(1))
        nodes += button(340,174,109,"Reorder","normal",control(2))
        nodes += [text(204,242,"Night Drive",19,bold=True),text(204,264,"5 tracks · Sync pending",12,MUTED)]
        for i,(name,artist,_,duration) in enumerate(rows):
            y=280+i*43
            nodes += [rect(204,y,564,43,PAPER,LINE),text(221,y+27,name,14),
                      text(477,y+27,artist,12,MUTED),text(715,y+27,duration,12,MUTED)]
    else:
        pending = offline and selected == ("Night Crossing",)
        nodes += button(204,174,105,"Play track","disabled" if pending or not selected else "normal",control(1))
        nodes += button(320,174,125,"Add to queue","disabled" if not selected else "normal",control(2))
        if host_actions:
            nodes += button(456,174,136,"Organize files","normal" if selected else "disabled")
            nodes += button(603,174,134,"Remove music","normal" if selected else "disabled")
        nodes += library_track_rows(rows,selected,offline,playing)
        if pending:
            nodes.append(text(205,539,"Night Crossing is pending and cannot play from this desktop.",12,AMBER))
        elif playing:
            nodes.append(text(205,539,"Playing Harbor Lights from this desktop.",12,GREEN,True))
    if activity:
        nodes += banner(204,574,564,*activity)
    return nodes


def library_list_pane(mode, entries, playlist, saved, dragging, draft_status, can_add):
    nodes = component_nodes("library.svg", f"list-tabs-{mode.lower()}")
    if mode == "Playlist":
        name_field = field(808,174,292,"Playlist name",playlist)
        if draft_status == "invalid":
            name_field[1].set("stroke", RED)
            name_field.append(rect(820,194,1,16,INK))
        nodes += name_field
        if draft_status == "invalid":
            nodes.append(text(808,241,"Enter a playlist name.",12,RED,True))
        label, action = "Playlist order", "Save playlist"
    elif mode == "Sync":
        nodes += field(808,174,292,"Sync to device","Pixel 6  ▾")
        nodes.append(text(808,241,"Musicolette · Default Android profile",12,MUTED))
        label, action = "Sync list", "Preview export"
    else:
        nodes += [text(808,177,"Playing next",18,bold=True),text(808,204,"Harbor Lights is playing",12,MUTED)]
        label, action = "Queued tracks", "Save as playlist"
    nodes += [text(808,270,label,14,bold=True),text(1037,270,f"{len(entries)} tracks",12,MUTED)]
    for i,name in enumerate(entries):
        y=284+i*44
        nodes += [rect(808,y,292,44,PAPER,LINE),text(819,y+27,"⠿",14,MUTED),
                  text(841,y+27,name,13),text(1074,y+27,"×",16,MUTED)]
    top=293+len(entries)*44
    if saved:
        nodes += banner(808,515,292,"Playlist saved","Find it under Saved playlists.")
    else:
        drop = rect(808,top,292,75,TINT if dragging else BG,GREEN if dragging else LINE,5)
        drop.set("stroke-dasharray","5 4")
        nodes += [drop,text(827,top+34,"Drop selected tracks here" if dragging else "Drag tracks or albums here",13,GREEN if dragging else MUTED,dragging),
                  text(827,top+54,"Or use Add selected.",12,MUTED)]
        if draft_status == "saving":
            nodes += banner(808,515,292,"Saving playlist","Checking the playlist name and track order.")
    nodes += button(808,591,116,"Add selected","normal" if can_add and not saved else "disabled")
    usable = entries and not saved and draft_status != "saving"
    nodes += button(935,591,165,action,"primary" if usable else "disabled")
    return nodes


def library_state(page, *, mode="Queue", entries=("Low Current","Branch Signal"), selected=(),
                  rows=TRACKS, offline=False, query="lena", busy=False, activity=None, playing=False,
                  host_actions=False, playlist="", saved=None, dragging=False, draft_status=None):
    def apply(root):
        screen = root.get("data-screen-id")
        nodes = library_shell(page,saved)
        nodes += library_catalog(screen,page,selected,rows,offline,query,busy,activity,playing,host_actions)
        nodes += library_list_pane(mode,entries,playlist,saved,dragging,draft_status,
                                   bool(selected) or page in ("Artists","Albums"))
        if dragging:
            nodes += [rect(640,330,160,37,GREEN,LINE,5),text(657,354,"2 selected tracks",13,PAPER,True),
                      element("path",d="M814 328L826 349L829 338L839 335Z",fill=PAPER,stroke=DARK)]
        find_layer(root,"body")[:] = nodes
        if playing:
            seek_to(3)(root)
    return apply


def library_dialog_backdrop(page, **parameters):
    def apply(root):
        shell = element("svg", data_screen_id="SCR05")
        shell.append(element("g"))
        library_state(page, **parameters)(shell)
        content = list(find_layer(shell,"body"))
        for node in content:
            node.attrib.pop("data-control-id",None)
        dialog = find_layer(root,"modal")
        find_layer(root,"body")[:] = [*content,*component_nodes("dialogs.svg","scrim"),dialog]
    return apply


def replace(old, new, layer="all", index=None):
    """Replace exact labels, failing if the canonical wireframe changed."""
    def apply(root):
        source = root if layer == "all" else find_layer(root, layer)
        matching = [node for node in source.iter(tag("text")) if node.text == old]
        if not matching:
            raise ValueError(f"Missing label {old!r} in {root.get('data-screen-id')}")
        targets = matching if index is None else [matching[index]]
        for node in targets:
            node.text = new
    return apply


def find_layer(root, layer):
    if layer == "modal":
        return root.find(f".//{tag('g')}[@role='group']")
    return root.find(tag("g"))


def draw(*nodes, layer="body"):
    def apply(root):
        find_layer(root, layer).extend(deepcopy(nodes))
    return apply


def over(x, y, w, h, *nodes, fill=PAPER, layer="body"):
    return draw(rect(x, y, w, h, fill), *nodes, layer=layer)


def restyle_button(control, tone, label=None):
    def apply(root):
        for group in root.iter(tag("g")):
            for i, node in enumerate(group):
                if node.get("data-control-id") != control:
                    continue
                old = group[i - 1]
                x, y, w = (float(old.get(key)) for key in ("x", "y", "width"))
                background, caption = button(x, y, w, label or node.text, tone, control)
                group[i - 1:i + 1] = [background, caption]
                return
        raise ValueError(f"Missing control {control}")
    return apply


def recolor_badge(old, new, tone, layer="body", index=0):
    def apply(root):
        nodes = find_layer(root, layer)
        matches = [i for i, node in enumerate(nodes) if node.tag == tag("text") and node.text == old]
        i = matches[index]
        box, label = nodes[i - 1], nodes[i]
        fill, ink = {"green": (TINT, GREEN), "neutral": ("#e9eeea", MUTED), "warn": (WARN, AMBER)}[tone]
        box.set("fill", fill)
        box.set("width", str(max(float(box.get("width")), len(new)*6 + 18)))
        label.set("fill", ink)
        label.text = new
    return apply


def relocate_selection(x, y, new_y, layer="body"):
    def apply(root):
        nodes = find_layer(root, layer)
        matched = [n for n in nodes if n.tag == tag("rect") and n.get("y") == str(y)
                   and n.get("x") == str(x) and n.get("fill") in (GREEN, TINT)]
        if len(matched) != 2:
            raise ValueError(f"Could not locate table selection at {x},{y}")
        for node in matched:
            nodes.remove(node)
        target = next(i for i, node in enumerate(nodes) if node.tag == tag("line")
                      and node.get("x1") == str(x) and node.get("y1") == str(new_y))
        for node in matched:
            node.set("y", str(new_y))
        nodes[target:target] = matched
    return apply


def table_text(y, values, layer="body"):
    def apply(root):
        row = [n for n in find_layer(root, layer) if n.tag == tag("text") and n.get("y") == str(y)]
        if len(row) != len(values):
            raise ValueError(f"Unexpected table cells on row {y}")
        for cell, value in zip(row, values):
            if value is not None:
                cell.text = value
    return apply


def remove_text(old, layer="body"):
    def apply(root):
        nodes = find_layer(root, layer)
        match = next(node for node in nodes if node.tag == tag("text") and node.text == old)
        nodes.remove(match)
    return apply


def seek(x, y, w, percent, dark=False):
    return [*progress(x, y-3, w, percent, dark),
            element("circle", cx=x + w*percent/100, cy=y, r=6, fill="#a7d8b6" if dark else GREEN)]


def seek_to(seconds):
    def apply(root):
        slider = root.find(f".//{tag('g')}[@data-seek-slider='track']")
        if slider is None:
            raise ValueError(f"Missing shared seek slider in {root.get('data-screen-id')}")
        duration = int(slider.get("aria-valuemax"))
        if not 0 <= seconds <= duration:
            raise ValueError(f"Seek position {seconds} is outside the track duration {duration}")
        parts = {node.get("data-seek-part"): node for node in slider}
        track = parts["track"]
        width = float(track.get("width")) * seconds / duration
        parts["played"].set("width", f"{width:.3f}")
        parts["thumb"].set("cx", f"{float(track.get('x'))+width:.3f}")
        elapsed = f"{seconds//60}:{seconds%60:02}"
        parts["elapsed"].text = elapsed
        slider.set("aria-valuenow", str(seconds))
        slider.set("aria-valuetext", f"{elapsed} of {parts['duration'].text}")
    return apply


def carry_editor(root):
    """Attach the existing window-owned playlist draft to the Sync window."""
    ui = find_layer(root, "body")
    original = load_screen("SCR11")
    ui.extend(component_nodes("dialogs.svg","scrim"))
    draft = deepcopy(find_layer(original, "modal"))
    draft.set("transform", "translate(209 71)")
    for node in draft.iter():
        node.attrib.pop("data-control-id", None)
    ui.append(draft)


@dataclass(eq=False, frozen=True)
class Frame:
    screen: int
    slug: str
    action: str
    result: str
    changes: tuple = ()


def frame(screen, slug, action, result, *changes):
    return Frame(screen, slug, action, result, changes)


# The first path in each storyboard is the primary path. Alternatives can share
# frame objects, so they keep the same step ID at split and merge points.
BOARDS = {}


def board(number, *paths):
    BOARDS[f"STB{number:02}"] = paths


def path(label, *frames):
    return label, frames


def extend_board(number, label, *steps):
    """Append a documented route while retaining every existing step ID."""
    ident = f"STB{number:02}"
    existing = list(dict.fromkeys(step for _, frames in BOARDS[ident] for step in frames))
    frames = tuple(existing[step - 1] if isinstance(step, int) else step for step in steps)
    BOARDS[ident] += (path(label, *frames),)


def track_status(selected, playback=False):
    return (library_state("Tracks", selected=(TRACKS[selected][0],), offline=True, playing=playback),)


def search_panel(query, results, busy=False):
    return library_state("Search", query=query, rows=results, busy=busy)


def mixer_state(ratio, deck_status, mode, time, detail, tone="info"):
    return (
        replace("Cued", deck_status),
        replace("0:00 / 3:43", time),
        recolor_badge("Auto-fade on", mode, "warn" if tone == "error" else "green"),
        over(74, 511, 550, 30, *seek(82, 526, 529, ratio*100)),
        draw(*banner(36, 582, 1048, mode, detail, tone)),
    )


# Configuration, dispatch, library and player

board(1, path("First run and applied override",
    frame(1,"first-run","Launch without a configuration file","The first-run page shows defaults for configuration, database and music."),
    frame(1,"creating-configuration","Choose Use defaults","First Run shows that the local configuration and catalog are being created.",
          over(388, 528, 657, 121, *banner(388, 535, 657, "Creating configuration", "Writing appsettings.json and opening the local catalog…"),
               *button(717, 602, 173, "Choose locations", "disabled", "SCR01.C02"),
               *button(904, 602, 141, "Use defaults", "disabled", "SCR01.C01"), fill=BG)),
    frame(3,"default-settings-applied","Open Diagnostics after startup","The local cache is open and the startup event lists effective defaults.",
          relocate_selection(37,406,320),
          replace("Frame delivery timed out. The visualization was detached.", "Logging Information · Microsoft Warning · Rolling logs enabled"),
          replace("Event WorkerFrameTimeout · Correlation viz-93cd", "Database ~/.local/share/beets-man/library.db   ·   Music ~/Music")),
    frame(3,"music-root-after-restart","Change MusicRoot locally and restart","Diagnostics shows the new resolved music folder and a new configuration event.",
          relocate_selection(37,406,320),
          replace("Settings resolved", "Settings reloaded"), replace("cfg-28b7", "cfg-c716"),
          replace("Frame delivery timed out. The visualization was detached.", "Configuration reloaded after restart"),
          replace("Event WorkerFrameTimeout · Correlation viz-93cd", "Database ~/.local/share/beets-man/library.db   ·   Music ~/Music/Records")),
))

PLAYLIST_TRACKS = ("Harbor Lights", "Night Crossing")

board(2, path("Dispatch and validation result",
    frame(5,"library-open","Open Library","The desktop shows the catalog and continues to accept navigation."),
    frame(10,"playlist-ready","Open Playlists","The saved playlists and inline list pane stay in the Library workspace."),
    frame(10,"invalid-playlist-draft","Choose New playlist and leave Name empty","The list pane shows the empty name field and ordered tracks beside the catalog.",
          library_state("Playlists",mode="Playlist",entries=PLAYLIST_TRACKS)),
    frame(10,"submitting-playlist","Choose Save playlist","The list pane shows the pending request and prevents a second submission.",
          library_state("Playlists",mode="Playlist",entries=PLAYLIST_TRACKS,draft_status="saving")),
    frame(4,"validation-displayed","Apply the result on the UI dispatcher","Action Error shows the validation message over the inline draft.",
          library_dialog_backdrop("Playlists",mode="Playlist",entries=PLAYLIST_TRACKS))),
    path("Navigate while an import runs",
        frame(38,"long-import-running","Start the staged import and open Library jobs","Job #27 is selected and upload progress is visible.",
              replace("Match review","Upload copies"), recolor_badge("Needs input","Running","green"),
              relocate_selection(37,288,245),
              over(37,397,1046,199,text(54,431,"Job #27 · Import Northern Lines",21,bold=True),
                   text(54,468,"Staging 2 of 3 source files",13,MUTED),*progress(54,486,728,63))),
        frame(9,"search-during-import","Switch to Search while the job runs","The new view responds and catalog matches remain available.",
              library_state("Search", rows=TRACKS[:3], activity=("Library import running","2 of 3 files uploaded."))),
        frame(38,"import-ready-for-review","Return to Library jobs after upload","The import has reached Match review without blocking Search.",
              relocate_selection(37,288,245),
              over(37,397,1046,199,text(54,431,"Job #27 · Import Northern Lines",21,bold=True),
                   *banner(54,465,731,"Ready for match review","All 3 source copies are staged.")))),
)

board(3, path("Browse and play a catalog track",
    frame(5,"library-overview","Open Library","The sidebar, catalog, and right-hand queue are visible together."),
    frame(6,"artist-selected","Choose Artists and select Lena Vale","Lena Vale’s albums appear in the catalog while the queue stays visible."),
    frame(7,"album-selected","Choose Albums and select Northern Lines","Album art appears in the catalog beside the same queue."),
    frame(9,"matching-results","Search for Lena","Search results replace the catalog while the queue stays visible."),
    frame(8,"local-track-selected","Choose Tracks and select Harbor Lights and Night Crossing",
          "Both tracks are selected in the catalog. The Queue, Playlist, and Sync tabs stay available.",
          library_state("Tracks", selected=PLAYLIST_TRACKS)),
    frame(12,"selected-track-playing","Choose Play track","Now Playing shows the selected title, waveform and active transport."),
), path("Open the playlist pane and add tracks",
    frame(8,"save-to-playlist","Open Playlist in the list pane","The selected tracks stay visible and a new playlist can be named in the same window.",
          library_state("Tracks", mode="Playlist", entries=(),selected=PLAYLIST_TRACKS,playlist="Harbor Set")),
    frame(8,"playlist-draft","Drop the tracks into the playlist","Both tracks appear in Playlist order and Save playlist becomes available.",
          library_state("Tracks", mode="Playlist", entries=PLAYLIST_TRACKS,selected=PLAYLIST_TRACKS,playlist="Harbor Set")),
))

extend_board(3,"Build, reorder, and save a list",1,2,3,4,5,7,
    frame(8,"playlist-tracks-dragging","Drag the selected tracks toward the Playlist pane",
          "The Playlist drop target highlights while the selected tracks remain visible in the catalog.",
          library_state("Tracks", mode="Playlist", entries=(),selected=PLAYLIST_TRACKS,playlist="Harbor Set",dragging=True)),
    8,
    frame(8,"playlist-order-changed","Drag Night Crossing above Harbor Lights",
          "The playlist shows the new order in the right pane.",
          library_state("Tracks", mode="Playlist", entries=PLAYLIST_TRACKS[::-1],selected=PLAYLIST_TRACKS,playlist="Harbor Set")),
    frame(8,"playlist-saved-inline","Choose Save playlist","Harbor Set appears under Saved playlists and the right pane shows the saved order.",
          library_state("Tracks", mode="Playlist", entries=PLAYLIST_TRACKS[::-1],playlist="Harbor Set",saved="Harbor Set")))
extend_board(3,"Add the selection to the queue beside the catalog",1,5,
    frame(8,"tracks-added-to-queue","Choose Add to queue","The selected tracks appear in Playing next without leaving the Tracks catalog.",
          library_state("Tracks", entries=("Low Current","Branch Signal",*PLAYLIST_TRACKS),selected=PLAYLIST_TRACKS)))
extend_board(3,"Save the queue as a playlist",1,5,12,
    frame(8,"queue-as-playlist-draft","Choose Save as playlist and name the list",
          "Playlist order starts with the queue’s four tracks in the same order.",
          library_state("Tracks",mode="Playlist",entries=("Low Current","Branch Signal",*PLAYLIST_TRACKS),
                        playlist="Evening Queue")),
    frame(8,"queue-saved-as-playlist","Choose Save playlist",
          "Evening Queue appears under Saved playlists with the original queue order.",
          library_state("Tracks",mode="Playlist",entries=("Low Current","Branch Signal",*PLAYLIST_TRACKS),
                        playlist="Evening Queue",saved="Evening Queue")))

queue_reorder = (
    table_text(371.5,[None,"Low Current","Juniper Static","Low Current","3:43"]),
    table_text(414.5,[None,"Night Crossing","Lena Vale","Northern Lines","3:58"]),
    table_text(457.5,[None,"Ridge Line","Lena Vale","Northern Lines","5:08"]),
    relocate_selection(37,389,346),
)
board(4, path("Queue, mix, and change presentation",
    frame(12,"transport-playing","Start Harbor Lights","Now Playing displays active transport and the next track."),
    frame(13,"queue-reordered","Move Low Current ahead of Night Crossing","The upcoming order changes while Harbor Lights continues.",
          *queue_reorder),
    frame(14,"two-decks-cued","Cue Low Current on Deck B","Deck A plays while Deck B is cued at the start."),
    frame(14,"manual-blend","Play Deck B and move the crossfader","Both decks play, the fader moves toward B and automatic fading gives way to manual control.",
          *mixer_state(.64, "Playing", "Manual mix", "0:28 / 3:43", "Deck B is audible. The two deck transports remain independent.")),
    frame(15,"master-eq","Open Master EQ","Ten bands and the bypass control are available for the mixed output."),
    frame(17,"bounded-visualization","Open bounded visualization","Spectrum and transport remain in a resizable player window."),
    frame(18,"full-visualization","Choose Full screen","Spectrum fills the display and keeps Pause and Exit full screen."),
    frame(17,"visualization-restored","Press Escape","The prior bounded presentation returns with playback farther along.",
          replace("1:42 / 4:26","2:03 / 4:26"), over(15, 536, 872, 15, *seek(22, 543, 858, 46), fill=BG)),
))
extend_board(4,"Seek from the shared player",1,2,
    frame(13,"shared-player-track-seek","Drag the shared player’s seek thumb to 2:45",
          "The elapsed time, thumb, and played portion update together while the queue stays visible.",
          *queue_reorder,seek_to(165)))

# CLI playback, extensions, and preference inheritance

cli_draft = frame(20,"cli-profile-draft","Open the Studio CLI profile","The form shows the executable, arguments and declared action mappings.",
                  over(24, 412, 798, 54, *banner(24,412,798,"Validation required","Validate the edited profile before saving.","warn"), layer="modal"),
                  restyle_button("SCR20.C02", "disabled"))
cli_preview = frame(21,"switch-preview","Choose Studio CLI","Switch preview shows the current and proposed engines and the loss of unsupported controls.")

board(5, path("Validate and activate the CLI engine",
    frame(19,"built-in-engine","Open Playback settings","SoundFlow is active and the CLI profile can be edited."),
    cli_draft,
    frame(20,"cli-profile-validated","Choose Validate","The same form shows an accepted profile and which controls it supports."),
    cli_preview,
    frame(19,"cli-engine-awaits-play","Confirm Switch","Studio CLI becomes active, queue selection remains and playback awaits Play.",
          replace("SoundFlow · Built-in audio","Studio CLI · Session mode"),
          replace("Dual-deck mixing","Dual-deck mixing unavailable"),
          replace("Master equalizer","Master EQ unavailable"),
          replace("Visualization telemetry","Visualization unavailable"),
          over(580, 652, 80, 34, *button(585, 648, 66, "Play"), fill=PAPER)),
    frame(12,"cli-transport-playing","Choose Play","Harbor Lights plays through Studio CLI and transport reports playing.",
          replace("Deck A · Playing","Studio CLI · Playing"), replace("1:42","0:08"),
          over(456, 394, 633, 15, *seek(462,401,620,3), fill=BG)),
))

board(6, path("Install, trust and use an extension",
    frame(22,"package-selected","Choose Install package and select Listening Notes","Plugins lists the version as awaiting local trust.",
          recolor_badge("Trusted","Review needed","warn",index=1), recolor_badge("Disabled","Awaiting trust","neutral"),
          relocate_selection(37,245,288), replace("Studio Spectrum","Listening Notes",index=1),
          replace("example.scope · 2.0.0","example.notes · 1.4.2"),
          replace("Contributes a spectrum visualization to Now Playing.","Adds the Listening Notes panel during playback."),
          replace("Worker protocol connected","The Listening Notes worker has not been started."),
          restyle_button("SCR22.C02", "disabled")),
    frame(23,"package-trust-review","Review package identity and permissions","Trust prompts for this version and this desktop.",
          replace("Studio Spectrum","Listening Notes","modal"), replace("example.scope · Version 2.0.0","example.notes · Version 1.4.2","modal"),
          replace("Spectrum visualization","Listening Notes extension panel","modal"),
          replace("Trust applies to version 2.0.0 on this desktop. Review a new version before enabling it.",
                  "Trust applies to version 1.4.2 on this desktop. Review a new version before enabling it.","modal")),
    frame(22,"plugin-enabled","Choose Trust locally, then enable Listening Notes","The selected package shows Trusted and Running.",
          recolor_badge("Disabled", "Running", "green"), relocate_selection(37,245,288),
          replace("Studio Spectrum","Listening Notes",index=1),
          replace("example.scope · 2.0.0","example.notes · 1.4.2"),
          replace("Contributes a spectrum visualization to Now Playing.","Adds the Listening Notes panel during playback.")),
    frame(26,"approved-extension-panel","Open the extension contribution","The host shows the approved Listening Notes form and command."),
    frame(26,"extension-command-complete","Run the note command","The same panel confirms that the note was saved for the current track.",
          replace("Save note to listening history","Note saved to listening history"),
          draw(*banner(362, 531, 703, "Note saved", "Check the mix at 1:42 · Harbor Lights"))),
))

board(7, path("Override a shared preference and inherit it again",
    frame(27,"settings-categories","Open Settings","The categories show examples of effective value sources."),
    frame(28,"theme-inherited","Open Appearance","Theme follows the system from the shared default."),
    frame(28,"theme-draft","Choose Dark","The field and preview update while the saved source remains visible.",
          replace("Follow system","Dark"), replace("Source · Shared default","Draft · Shared default currently applied"),
          over(653,304,407,164,rect(653,304,407,164,DARK), text(672,345,"Harbor Lights",17,PAPER,True),
               text(672,371,"Lena Vale",13,"#c8ded0"),*progress(672,395,360,39,True),*button(672,416,82,"Pause"))),
    frame(28,"theme-local-override","Choose Apply","Dark is the effective local override; the shared value is still Follow system.",
          replace("Follow system","Dark"), replace("Source · Shared default","Source · Local override · Shared default is Follow system"),
          over(653,304,407,164,rect(653,304,407,164,DARK), text(672,345,"Harbor Lights",17,PAPER,True),
               text(672,371,"Lena Vale",13,"#c8ded0"),*progress(672,395,360,39,True),*button(672,416,82,"Pause"))),
    frame(28,"theme-inherited-again","Choose Inherit","Follow system is effective again and the source returns to Shared default.")),
    path("Reset local notification preferences",
        frame(31,"notifications-settings","Open Notifications","Toggle states and their effective sources are visible.",
              replace("Shared default","Local override"),
              over(1026,273,45,28,rect(1029,272,37,21,"#e9eeea",LINE,10),element("circle",cx=1040,cy=283,r=7,fill=MUTED))),
        frame(34,"reset-preview","Choose Reset","The confirmation lists the overrides and their inherited values."),
        frame(31,"notification-defaults-restored","Confirm Reset","Both alerts turn on and their shared and built-in sources appear.",
              replace("Local override","Built-in default"),
              over(1026,435,45,28,rect(1029,436,37,21,GREEN,radius=10),element("circle",cx=1055,cy=447,r=7,fill=PAPER)))),
)

board(8,
    path("Open the host and import an album",
        frame(29,"host-settings","Open Library settings","The desktop shows its local Music Root and the host import defaults."),
        frame(35,"existing-library","Choose View configuration and select Open existing library","The existing host configuration is selected."),
        frame(36,"select-import-files","Open the library and select the three FLAC files","The file list and staged upload destination are visible."),
        frame(36,"upload-copies","Choose Upload copies","Two of three source copies have arrived in staging.",
              recolor_badge("Ready","Uploaded","green","modal"), recolor_badge("Ready","Uploaded","green","modal"),
              recolor_badge("Ready","Uploading","warn","modal"), restyle_button("SCR36.C01", "disabled"),
              over(24, 379, 786, 65, text(39,404,"Uploaded 2 of 3 files · 50.2 MB of 80.3 MB",13,bold=True),
                   *progress(39,426,754,63), fill=BG, layer="modal")),
        frame(37,"review-autotag","Review the strongest candidate","Source and proposed tags can be compared before choosing Use match."),
        frame(38,"import-published","Choose Use match and open Library jobs","Job #27 shows the published album and completed import.",
              replace("Match review","Published"), recolor_badge("Needs input","Complete","green"),
              relocate_selection(37,288,245),
              over(37,397,1046,199, text(54,431,"Job #27 · Import Northern Lines",21,bold=True),
                   text(54,465,"Upload copies  →  Review match  →  Write tags  →  Publish",13,MUTED),
                   *banner(54,493,731,"Import complete","3 tracks added to the host catalog.")))),
    path("Edit an existing track",
        frame(39,"edit-existing-tags","Open Edit Tags for Harbor Lights","The current fields and expected revision 184 are visible."),
        frame(39,"tag-draft-changed","Change Genre to Downtempo","The draft shows the new genre while retaining revision 184.",
              replace("Ambient","Downtempo","modal")),
        frame(38,"tag-edit-published","Choose Save and inspect the job","The tag-update job is published and displays the new revision.",
              relocate_selection(37,288,331),
              over(37,397,1046,199,text(54,431,"Job #25 · Update track tags",21,bold=True),
                   text(54,465,"Harbor Lights · Genre is now Downtempo",13,MUTED),
                   *banner(54,493,731,"Update complete","Published at revision 185.")))),
)

board(9,
    path("Register, replicate and resolve a conflict",
        frame(42,"first-desktop-registered","Confirm Studio desktop is already registered","Studio desktop is active and Travel laptop has yet to register.",
              recolor_badge("Active","Unregistered","neutral",index=2),
              relocate_selection(37,332,246),
              replace("Travel laptop","Studio desktop",index=1),
              replace("Listener · Can access catalog and sync listening records","Operator · Current desktop"),
              replace("Selected instance · desktop-0d22","Selected instance · desktop-a91f")),
        frame(43,"register-travel-laptop","Enter host, desktop name, and credential","The credential is masked and the host is ready to register Travel laptop."),
        frame(42,"desktop-registered","Choose Register","Travel laptop shows Active alongside the other registered desktops."),
        frame(11,"offline-playlist-edited","Edit Night Drive while Travel laptop is offline","The reordered local draft remains queued for sync.",
              replace("Studio desktop","Travel laptop"),
              over(24,174,622,199,
                   rect(24,174,622,34,"#e9eeea",LINE,4),text(36,195,"Order"),text(96,195,"Track"),
                   *[node for i,name in enumerate(("Harbor Lights","Night Crossing","Ridge Line"))
                     for node in (rect(24,208+43*i,622,42,TINT if i == 1 else PAPER,LINE,2),
                                  text(36,234+43*i,str(i+1)),text(96,234+43*i,name))],layer="modal")),
        frame(45,"records-catching-up","Reconnect the offline desktop","The desktop shows its committed cursor, undelivered records, and two pending edits."),
        frame(46,"compare-offline-draft","Open the conflicting playlist draft","Host and local track orders appear side by side."),
        frame(45,"local-draft-reapplied","Choose Reapply local","Record sync advances to r190 and confirms the playlist was applied.",
              replace("Host is at r189","Host is at r190"), replace("r184","r190"),
              replace("1 playlist · 1 favorite","1 favorite"), replace("2","1",index=0),
              recolor_badge("1 local edit pending","Playlist reapplied","green"),
              recolor_badge("Pulling changes","Current","green"), replace("Receiving page 3 of 5","Synced through r190"),
              over(36,591,322,14,*progress(36,594,322,100),fill=BG))),
    path("Revoke a registered desktop",
        frame(42,"select-travel-laptop","Select Travel laptop in Instances","The active desktop is selected and Revoke is available."),
        frame(44,"review-revocation","Choose Revoke","The dialog names the instance and explains loss of host access."),
        frame(42,"desktop-revoked","Confirm Revoke","Travel laptop shows Revoked and its Revoke action becomes unavailable.",
              recolor_badge("Active","Revoked","neutral",index=2),
              replace("Listener · Can access catalog and sync listening records","Listener · Host access revoked"),
              restyle_button("SCR42.C02", "disabled"))),
)

media_sync = frame(48,"sync-overview","Open Sync","PC mirrors and the paired phone appear in the device list.")
board(10,
    path("Pair and sync an Android device",
        media_sync,
        frame(51,"pair-android-peer","Choose Pair peer and select Android export","The dialog shows the device ID and share destination."),
        frame(52,"choose-android-layout","Accept the share and keep the Musicolette layout","The default Android profile, storage root, and relative playlist paths appear."),
        frame(53,"publishable-export","Choose Preview","The preview shows device-owned changes and Publish becomes available when the collision is resolved.",
              recolor_badge("Collision","Ready","green","modal"), recolor_badge("1 collision","No collisions","green","modal"),
              over(24,387,896,65,*banner(24,387,896,"Export paths validated","Review the owned removal before publishing."),layer="modal"),
              restyle_button("SCR53.C01", "primary")),
        frame(54,"confirm-owned-removal","Choose Publish","The receiving device and the single owned path are listed for confirmation."),
        frame(54,"owned-removal-approved","Check the acknowledgment","Confirm is enabled for the listed device-owned path.",
              draw(rect(37,314,15,15,GREEN,radius=2),element("path",d="M40 321l3 3 6-7",stroke=PAPER,fill="none",stroke_width=2),layer="modal"),
              restyle_button("SCR54.C01", "primary")),
        frame(50,"android-transferring","Confirm publication","The assigned layout remains visible beside transfer progress."),
        frame(50,"android-transfer-complete","Wait for the phone to receive the export","Progress reaches 60 of 60 files.",
              replace("38 of 60 files · 1.4 GB of 2.2 GB","60 of 60 files · 2.2 GB · Up to date"),
              over(368,452,697,16,*progress(368,456,697,100)))),
    path("Pause and resume a PC mirror",
        frame(30,"sync-defaults","Open Sync settings","Device defaults and the local share status are visible."),
        media_sync,
        frame(49,"mirror-transferring","Open PC Mirrors","Studio PC is transferring an accepted mirror share."),
        frame(49,"mirror-paused","Pause the Studio PC mirror","The same card preserves progress at 78 files and offers Resume.",
              recolor_badge("Transferring","Paused","warn"), restyle_button("SCR49.C01", "primary", "Resume"),
              replace("78 of 100 files","78 of 100 files · Paused")),
        frame(49,"mirror-resumed","Resume Studio PC","Progress advances from 78 to 84 files.",
              replace("78 of 100 files","84 of 100 files"),
              over(52,339,482,18,*progress(52,343,482,84)))),
    path("Diagnose and retry a failed transfer",
        media_sync,
        frame(55,"receiving-folder-error","Open the Pixel 6 transfer error","The affected folder and last successful sync are visible."),
        frame(50,"receiving-folder-restored","Restore folder access and choose Retry","The device resumes at 38 of 60 files.",
              draw(*banner(368,519,523,"Transfer resumed","Pixel 6 can write to the receiving folder again.")))),
)

# Alternate and recovery paths

board(11, path("Repair a malformed configuration",
    frame(2,"malformed-config","Launch with malformed JSON","The repair dialog points to line 23, column 4."),
    frame(2,"config-repaired-retry-pending","Open configuration, correct the file, and return","The dialog identifies the corrected location and offers Retry.",
          replace("Malformed JSON","Configuration file changed","modal"),
          replace("Expected a closing brace on line 23, column 4.","Retry to validate and reload the saved changes.","modal"),
          replace('22    "MusicRoot": "~/Music",','22    "MusicRoot": "~/Music"',"modal")),
    frame(3,"existing-catalog-restored","Choose Retry","Diagnostics reports that the repaired configuration loaded and the original cache opened.",
          relocate_selection(37,406,320),
          replace("Settings resolved","Configuration repaired"), replace("Cache opened","Existing cache opened"),
          replace("Frame delivery timed out. The visualization was detached.","Configuration repaired · Existing catalog reopened"),
          replace("Event WorkerFrameTimeout · Correlation viz-93cd","Catalog path ~/.local/share/beets-man/library.db")),
))

latest_results = [("Low Current","Juniper Static","Low Current","3:43")]
board(12, path("Keep the newest search result",
    frame(9,"search-query-a","Search for Lena","The first asynchronous search is pending.",search_panel("lena",[],True)),
    frame(9,"search-query-b","Replace the query with Juniper","The search field shows Juniper while the latest request is pending.",search_panel("juniper",[],True)),
    frame(9,"newest-search-results","Complete Juniper first and apply its result","Juniper results appear under the latest query.",search_panel("juniper",latest_results)),
    frame(9,"late-response-ignored","Deliver the older Lena response","The same Juniper result remains visible after the stale response is ignored.",search_panel("juniper",latest_results)),
))

board(13, path("Browse offline and play a downloaded track",
    frame(8,"cache-open-offline","Disconnect the host","Tracks remains browsable from cache with a downloaded row selected.",*track_status(0)),
    frame(8,"pending-track-disabled","Select Night Crossing","The Pending row is highlighted and Play track is disabled.",*track_status(1)),
    frame(8,"downloaded-track-enabled","Select Harbor Lights","Selection returns to a downloaded row and Play track is enabled.",*track_status(0)),
    frame(8,"downloaded-track-playing","Choose Play track","Harbor Lights gains a playing indicator and the pending track remains unavailable.",*track_status(0,True)),
))

fade_ready = frame(14,"automatic-fade-ready","Cue Low Current and enable auto-fade","The fader is at Deck A and Low Current is cued.",
                  *mixer_state(0,"Cued","Auto-fade ready","0:00 / 3:43","Automatic fade starts when the incoming deck is ready."))
fade_active = frame(14,"automatic-fade-active","Start the automatic crossfade","The fader advances as the incoming deck joins.",
                   *mixer_state(.28,"Playing","Auto-fade active","0:01 / 3:43","Fading from Harbor Lights to Low Current over 4 seconds."))

board(14,
    path("Incoming deck failure",
        fade_ready, fade_active,
        frame(14,"deck-b-failed","Deck B reports a read error","The fader returns to Deck A, Harbor Lights keeps playing, and the error names Deck B.",
              *mixer_state(0,"Failed","Deck B unavailable","0:01 / 3:43","Could not read Low Current. Deck A is still playing.","error"))),
    path("Manual takeover",
        fade_ready, fade_active,
        frame(14,"manual-fader-takeover","Move the fader by hand","Automatic updates stop and the fader stays at the manually selected position.",
              *mixer_state(.58,"Playing","Manual control","0:02 / 3:43","Manual crossfader is controlling the mix."))),
)

board(15,
    path("Cancel a backend switch",
        cli_preview,
        frame(19,"switch-canceled","Choose Cancel","The SoundFlow settings return with the active engine and queue intact.")),
    path("Recover from a CLI process failure",
        cli_preview,
        frame(21,"switch-starting","Choose Switch","The dialog shows startup in progress and disables the Switch action.",
              over(24,233,608,62,*banner(24,233,608,"Starting Studio CLI","Waiting for the external session to respond."),layer="modal"),
              restyle_button("SCR21.C01", "disabled")),
        frame(21,"switch-process-failed","The process exits before it is ready","The dialog identifies the startup failure and says which track remains selected.",
              over(24,233,608,62,*banner(24,233,608,"Studio CLI could not start","Exit code 2 · Check the profile before trying again.","error"),layer="modal"),
              replace("Unavailable with Studio CLI · Master EQ and visualizations","Harbor Lights and your queue are retained.","modal"))),
)

board(16, path("Detach a stopped worker and keep playback",
    frame(25,"built-in-visualization-selected","Open Visualization Picker","The built-in Spectrum is selected and has a live preview."),
    frame(25,"plugin-visualization-selected","Choose Studio Spectrum","The visualization picker shows the plugin as available and previews it.",
          replace("Built-in · Available","Plugin · Available",index=0),replace("Spectrum","Studio Spectrum",index=0),
          replace("Studio Spectrum","Spectrum",index=1),replace("Plugin · Available","Built-in · Available",index=1),
          replace("Spectrum · Built-in","Studio Spectrum · Plugin"),
          replace("Uses the active engine’s frequency data.","Plugin preview uses the active engine’s frequency data.")),
    frame(17,"plugin-visualization-playing","Apply the plugin visualization","The plugin spectrum is active and player transport is visible.",
          replace("Spectrum","Studio Spectrum")),
    frame(17,"plugin-visualization-detached","The visualization worker times out","The spectrum is detached and the track advances with transport still usable.",
          over(20,73,860,379,rect(20,73,860,379,DARK,radius=7),
               text(54,177,"Visualization unavailable",23,PAPER,True),
               text(54,209,"Studio Spectrum stopped responding.",14,"#dbe7df"),
               *button(54,239,147,"Open diagnostics"),fill=BG),
          replace("1:42 / 4:26","1:48 / 4:26")),
    frame(24,"worker-timeout-details","Open diagnostics","The stopped worker, correlation ID, and detached contribution are shown.",
          recolor_badge("Running","Stopped","warn"),replace("Worker protocol connected","Worker disconnected"),
          seek_to(109)),
))

conflict = frame(33,"existing-gesture-conflict","Capture Space for Toggle auto-fade","The dialog identifies the existing Play or pause binding.")
board(17,
    path("Cancel conflicting reassignment",
        conflict,
        frame(32,"old-binding-retained","Choose Cancel","The shortcut table still assigns Space to Play or pause.",
              draw(*banner(36,575,520,"Shortcut unchanged","Space remains assigned to Play or pause.")))),
    path("Choose a valid gesture",
        conflict,
        frame(32,"capture-replacement","Choose another","The reassignment panel waits for a replacement gesture.",
              draw(*banner(36,575,520,"Listening for a shortcut","Press a key combination for Toggle auto-fade."))),
        frame(32,"replacement-assigned","Enter Ctrl+Shift+F","The Player mapping is saved and the original Space binding remains available.",
              replace("Search library","Toggle auto-fade"),replace("Ctrl+F","Ctrl+Shift+F"),
              replace("Library","Player",index=2))),
    path("Handle system registration denial",
        frame(32,"system-shortcuts-off","Open Shortcuts before opting in","In-app gestures are assigned and system playback keys are off.",
              recolor_badge("Registration denied","Off","neutral"),
              over(618,510,466,66,fill=BG),
              over(517,512,44,27,rect(519,517,37,21,"#e9eeea",LINE,10),element("circle",cx=530,cy=527,r=7,fill=MUTED))),
        frame(32,"system-shortcut-denied","Enable system playback keys","The OS denial is visible and in-app shortcuts remain assigned.")),
)

board(19, path("Rebuild the snapshot and preserve the draft",
    frame(47,"expired-cursor","Reconnect with an expired cursor","Snapshot Recovery identifies the expired cursor and the two drafts that will be preserved."),
    frame(47,"snapshot-downloading","Choose Refresh snapshot","The same dialog shows download progress and keeps the pending edits visible.",
          replace("Waiting to start","Receiving snapshot page 3 of 5","modal"),
          over(37,213,608,15,*progress(38,217,606,60),fill=BG,layer="modal")),
    frame(46,"conflict-after-snapshot","Finish rebuilding and replay pending edits","The conflicting playlist draft is preserved beside the latest host version."),
    frame(45,"host-version-kept","Choose Keep host","The committed cursor reaches r189, with the favorite change still pending.",
          replace("r184","r189"),replace("2","1",index=0),replace("1 playlist · 1 favorite","1 favorite"),
          recolor_badge("1 local edit pending","Host version kept","green"),
          replace("Receiving page 3 of 5","Snapshot committed through r189"),
          over(36,591,322,14,*progress(36,594,322,100),fill=BG)),
))

layout_changes = frame(53,"layout-changes-validated","Resolve the collision in Export Preview","The new profile can publish after confirmation of the single owned removal.",
                       replace("Pixel 6 · Musicolette export · Changes owned by this device",
                               "Pixel 6 · Poweramp export · Changes owned by this device","modal"),
                       recolor_badge("Collision","Ready","green","modal"),recolor_badge("1 collision","No collisions","green","modal"),
                       over(24,387,896,65,*banner(24,387,896,"Export paths validated","A device-owned playlist path is marked for removal."),layer="modal"),
                       restyle_button("SCR53.C01", "primary"))
remove_prompt = frame(54,"owned-path-confirmation","Choose Publish","Confirm Export Removal shows the path owned by Pixel 6.")
board(20,
    path("Review layout and cancel removal",
        frame(52,"changed-android-profile","Choose a new profile for Pixel 6","The newly selected profile uses a versioned artist/album layout.",
              replace("Musicolette · Artist / Album · v2","Poweramp · Artist / Album · v3","modal")),
        frame(53,"layout-collision-preview","Choose Preview","The path comparison shows a collision blocking publication.",
              replace("Pixel 6 · Musicolette export · Changes owned by this device",
                      "Pixel 6 · Poweramp export · Changes owned by this device","modal")),
        layout_changes, remove_prompt,
        frame(50,"previous-export-retained","Choose Cancel","The device retains its previous export and layout assignment.",
              draw(*banner(368,506,527,"Previous export retained","No device-owned paths were removed.")))),
    path("Recover an interrupted publication",
        layout_changes, remove_prompt,
        frame(54,"approve-owned-removal","Check the acknowledgment","Confirm becomes available for the single owned path.",
              draw(rect(37,314,15,15,GREEN,radius=2),element("path",d="M40 321l3 3 6-7",stroke=PAPER,fill="none",stroke_width=2),layer="modal"),
              restyle_button("SCR54.C01", "primary")),
        frame(55,"publication-interrupted","Confirm and interrupt the publish","Transfer Error identifies the interrupted export and last successful sync.",
              replace("The receiving folder is unavailable","Publication interrupted","modal"),
              replace("Syncthing-Fork cannot write to the Music folder on Pixel 6.","The prior export remains visible while owned paths are reconciled.","modal"),
              replace("Reconnect Pixel 6 or restore folder access, then retry.","Retry reconciles only paths owned by the Pixel 6 export.","modal")),
        frame(50,"owned-paths-reconciled","Choose Retry","Android Devices shows the reconciled export and completed progress.",
              replace("Musicolette · Artist / Album · M3U8","Poweramp · Artist / Album · v3 · M3U8"),
              replace("38 of 60 files · 1.4 GB of 2.2 GB","60 of 60 files · 2.2 GB · Up to date"),
              over(368,452,697,16,*progress(368,456,697,100)),
              draw(*banner(368,506,527,"Export reconciled","Only device-owned paths were changed.")))),
)

arrival_sync = frame(48,"android-arrival-sync","Receive a live connection from Pixel 6","Normal Sync opens with Pixel 6 selected. Harbor Lights is still playing.")
arrival_elapsed = frame(48,"android-arrival-playback-continues","Let playback continue","The selected device stays open and elapsed playback advances.",
                        seek_to(110))
board(21,
    path("Arrive from Normal",
        frame(12,"normal-before-arrival","Play Harbor Lights in Normal","The track is playing at 1:42."), arrival_sync, arrival_elapsed),
    path("Arrive from Minimum",
        frame(16,"minimum-before-arrival","Play Harbor Lights in Minimum","The compact window shows the same track and elapsed position."),
        arrival_sync, arrival_elapsed),
    path("Arrive from Bounded Visualization",
        frame(17,"bounded-before-arrival","Play Harbor Lights in bounded visualization","Spectrum and transport are visible while the track plays."),
        arrival_sync, arrival_elapsed),
    path("Arrive from Full Visualization",
        frame(18,"full-before-arrival","Play Harbor Lights in full visualization","Full-screen spectrum is active at 1:42."), arrival_sync, arrival_elapsed),
    path("Arrive with an open playlist draft",
        frame(11,"draft-before-android-arrival","Edit the unsaved playlist name","The editor contains the Night drive edits draft as playback continues.",
              replace("Night Drive","Night drive edits","modal")),
        frame(48,"arrival-keeps-window-owned-draft","Receive the live connection while the editor is open","Sync selects Pixel 6 beneath the still-open draft. The draft and playback position are preserved.",
              carry_editor,replace("Night Drive","Night drive edits","modal")),
        frame(48,"sync-visible-after-draft-saved","Choose Save","The editor closes to the selected phone and the track continues.",
              seek_to(108))),
)


def location_picker(approved):
    database = ("~/.local/share/beets-man/catalog/library.db" if approved else
                "~/.local/share/beets-man/library.db")
    config = "~/.config/beets-man/appsettings.json"
    form = []
    for y, label, value in [(158, "Configuration file", config),
                            (238, "Local database", database),
                            (318, "Music folder", "~/Music/Records" if approved else "~/Music")]:
        form += field(388, y, 529, label, value)
        form += button(934, y+10, 111, "Browse…", "normal")
    mark = [element("path", d="M404 441l3 3 6-7", fill="none", stroke=PAPER, stroke_width=2)] if approved else []
    return over(338,0,782,690,
                text(388,97,"Choose storage locations",29,bold=True),
                text(388,125,"Review the paths before creating the local configuration.",14,MUTED),
                *form,
                rect(388,398,657,93,WARN,radius=5),
                text(401,420,"A catalog does not exist at the selected path",13,AMBER,True),
                rect(401,433,16,16,GREEN if approved else PAPER,GREEN if approved else LINE,3),*mark,
                text(428,446,"Create a local database at this path",13),
                text(428,471,"The database must stay on this computer.",12,MUTED),
                *button(785,544,100,"Back","normal"),
                *button(895,544,150,"Use these locations","primary" if approved else "disabled"),fill=BG)


extend_board(1,"Choose custom storage locations",1,
    frame(1,"choose-custom-locations","Choose locations","First Run opens editable path fields and asks for approval to create the missing local database.",
          location_picker(False)),
    frame(1,"custom-database-approved","Enter custom paths and approve database creation","Use these locations becomes available after explicit approval.",
          location_picker(True)),
    frame(3,"custom-locations-applied","Choose Use these locations","Startup creates the catalog at the approved path and uses the selected music folder.",
          relocate_selection(37,406,320),replace("Settings resolved","Custom paths applied"),
          replace("Frame delivery timed out. The visualization was detached.","Database ~/.local/share/beets-man/catalog/library.db"),
          replace("Event WorkerFrameTimeout · Correlation viz-93cd","Music folder ~/Music/Records · Local database created with approval")))

extend_board(2,"Dismiss validation and return to the draft",1,2,3,4,5,
    frame(10,"validation-dismissed-draft-retained","Choose Dismiss",
          "The inline playlist draft keeps its track order and focuses its empty name for correction.",
          library_state("Playlists",mode="Playlist",entries=PLAYLIST_TRACKS,draft_status="invalid")))


def import_outcome(kept):
    status = "Complete" if kept else "Skipped"
    phase = "Published" if kept else "Review closed"
    detail = ("3 tracks added using the original embedded tags." if kept else
              "No records added. The original audio files are unchanged.")
    return (replace("Match review",phase),recolor_badge("Needs input",status,"green" if kept else "neutral"),
            relocate_selection(37,288,245),
            over(37,397,1046,199,text(54,431,"Job #27 · Import Northern Lines",21,bold=True),
                 text(54,463,"Autotag review decision",13,MUTED),
                 *banner(54,486,731,"Imported with existing tags" if kept else "Import skipped",detail),
                 *button(892,508,83,"Inspect","normal","SCR38.C02"),
                 *button(983,508,83,"Retry","disabled","SCR38.C01")))


extend_board(8,"Import using the original tags",1,2,3,4,5,
    frame(38,"original-tags-imported","Choose Keep tags","Library jobs shows three imported tracks with their original embedded tags.",
          *import_outcome(True)))
extend_board(8,"Skip the album import",1,2,3,4,5,
    frame(38,"album-import-skipped","Choose Skip","Library jobs records the skipped album and confirms that no catalog records were added.",
          *import_outcome(False)))

extend_board(9,"Keep the host version during ordinary catch-up",1,2,3,4,5,6,
    frame(45,"host-playlist-kept-after-catch-up","Choose Keep host","The host playlist is applied at the committed r189 cursor and the favorite change remains pending.",
          *(replace("r184","r189",index=0) for _ in range(4)),
          replace("r186","r189"),replace("r182","r185"),replace("2","1",index=0),
          replace("1 playlist · 1 favorite","1 favorite"),
          recolor_badge("Pulling changes","Current","green"),
          recolor_badge("1 local edit pending","Host version kept","green"),
          replace("Receiving page 3 of 5","Caught up through r189 · Host playlist retained"),
          over(36,591,322,14,*progress(36,594,322,100),fill=BG)))

extend_board(15,"Cancel after CLI startup fails",1,3,4,
    frame(19,"failed-switch-canceled","Choose Cancel after the CLI startup error","Playback Settings keeps SoundFlow selected, retains the queue, and waits for an explicit Play command.",
          draw(*banner(728,506,355,"SoundFlow selected · Playback stopped","Choose Play to resume Harbor Lights.")),
          over(580,648,77,40,*button(585,650,66,"Play"))))

extend_board(16,"Restart the worker from diagnostics",1,2,3,4,5,
    frame(24,"worker-restarting","Choose Restart worker","Diagnostics stays open and shows startup in progress while audio keeps playing.",
          recolor_badge("Running","Starting","neutral"),replace("Worker protocol connected","Waiting for worker handshake"),
          recolor_badge("Worker stopped","Starting worker","neutral","modal"),
          replace("Last heartbeat 11:08:01","Waiting for handshake","modal"),
          over(24,139,706,62,*banner(24,139,706,"Restarting Studio Spectrum","Playback is still active. Waiting for the worker to report ready."),layer="modal"),
          restyle_button("SCR24.C01","disabled"),seek_to(110)),
    frame(24,"worker-restart-complete","Wait for the worker to report ready","Plugin Diagnostics shows a healthy heartbeat and a restored visualization contribution while playback continues.",
          recolor_badge("Worker stopped","Worker running","green","modal"),
          replace("Last heartbeat 11:08:01","Last heartbeat 11:08:06","modal"),
          over(24,139,706,62,*banner(24,139,706,"Worker ready","Visualization contribution restored. Playback is still active."),layer="modal"),
          replace("11:08:01  INFO   Worker heartbeat","11:08:04  INFO   Restart requested","modal"),
          replace("11:08:03  WARN   WorkerFrameTimeout","11:08:05  INFO   Worker ready","modal"),
          replace("11:08:03  INFO   Contribution detached","11:08:06  INFO   Contribution attached","modal"),
          restyle_button("SCR24.C01","disabled"),seek_to(111)))


def online_tracks(outcome="selected"):
    notes = {
        "selected": ("2 tracks selected", "Choose Organize files to preview paths or Remove music to review removal."),
        "canceled": ("Organize canceled", "Both tracks are still selected. Their original paths remain in place."),
        "removed": ("2 catalog records removed", "The audio files remain in their original locations."),
        "moved": ("2 files moved · Catalog updated", "Lena Vale/Northern Lines/01 Harbor Lights.flac · 02 Night Crossing.flac"),
    }
    return library_state("Tracks", rows=TRACKS[2:] if outcome=="removed" else TRACKS,
                         selected=() if outcome=="removed" else PLAYLIST_TRACKS,
                         host_actions=True, activity=notes[outcome])


use_online_tracks_backdrop = library_dialog_backdrop("Tracks", selected=PLAYLIST_TRACKS, host_actions=True)


# Both host-write dialogs use the same authorized, online selection.
board(18,
    path("Cancel an organize preview",
        frame(40,"moves-ready","Open Organize Preview for the selected tracks","The source and destination paths are visible before any move.",
              use_online_tracks_backdrop,recolor_badge("1 collision","No collisions","green","modal"),
              recolor_badge("Collision","Ready","green","modal"),
              over(24,311,852,71,*banner(24,311,852,"Paths validated","Confirm moves to apply the proposed destinations."),layer="modal"),
              restyle_button("SCR40.C01","primary")),
        frame(8,"organize-canceled","Choose Cancel","The online track selection remains and the original file paths are unchanged.",
              online_tracks("canceled"))),
    path("Remove the catalog records and retain audio",
        frame(41,"catalog-only-removal","Select Remove catalog records only","The dialog distinguishes catalog removal from deleting the audio.",
              use_online_tracks_backdrop),
        frame(8,"catalog-records-removed","Choose Confirm removal","The two selected titles are absent from the online catalog and the audio was retained.",
              online_tracks("removed"))),
)
online_selection = frame(8,"online-tracks-selected","Select Harbor Lights and Night Crossing in the online library",
                         "The same selection can launch Organize Preview or Remove Music.",
                         online_tracks())
extend_board(18,"Select tracks, organize, and confirm",online_selection,1,
    frame(8,"moves-confirmed","Choose Confirm moves","The same two records remain selected and the catalog shows their new host paths.",
          online_tracks("moved")))
extend_board(18,"Select tracks and cancel organize",online_selection,1,2)
extend_board(18,"Select tracks and remove catalog records",online_selection,3,4)

extend_board(20,"Publish without interruption",1,2,3,4,6,
    frame(50,"export-published","Choose Confirm and let publication finish","Android Devices shows the new assigned layout and a completed export with the approved playlist path removed.",
          replace("Musicolette · Artist / Album · M3U8","Poweramp · Artist / Album · v3 · M3U8"),
          replace("38 of 60 files · 1.4 GB of 2.2 GB","60 of 60 files · 2.2 GB · Transfer complete"),
          over(367,452,700,16,*progress(368,456,697,100)),
          draw(*banner(368,506,527,"Layout published","Removed approved device path M3U/Night Drive.m3u."))))


presentation_chooser = frame(12,"player-presentation-chooser","Open Presentation while Harbor Lights plays",
    "The player exposes all four presentation modes while Library remains available for editing a playlist draft.",
    draw(*button(843,143,241,"Presentation: Normal"),
             rect(832,188,252,205,PAPER,LINE,5),
             rect(840,223,236,32,TINT,radius=3),
             text(851,212,"Player presentation",12,MUTED,True),
             text(853,244,"Normal  ✓",13,GREEN,True),
             text(853,281,"Minimum"),
             text(853,318,"Bounded Visualization"),
             text(853,355,"Full Visualization")))
extend_board(21,"Choose Normal before arrival",presentation_chooser,1,2,3)
extend_board(21,"Choose Minimum before arrival",presentation_chooser,4,2,3)
extend_board(21,"Choose Bounded Visualization before arrival",presentation_chooser,5,2,3)
extend_board(21,"Choose Full Visualization before arrival",presentation_chooser,6,2,3)
extend_board(21,"Open a playlist draft before arrival",presentation_chooser,7,8,9,3)


def sync_list_review(root):
    library_dialog_backdrop("Tracks",mode="Sync",entries=PLAYLIST_TRACKS,selected=PLAYLIST_TRACKS)(root)
    review = find_layer(root,"modal")
    review[:] = [rect(0,0,944,596,PAPER,LINE,10),text(24,39,"Export Preview",24,bold=True),
                 text(24,62,"Pixel 6 · Musicolette · 2 tracks in this sync list",13,MUTED),rule(0,76,944,76),
                 rect(24,103,896,41,TINT,radius=5),text(38,129,"2 additions · No removals",14,GREEN,True),
                 rect(24,166,896,34,"#e9eeea",LINE,5),text(41,188,"Track",12,MUTED,True),
                 text(253,188,"Device path",12,MUTED,True),text(815,188,"Status",12,MUTED,True)]
    for i,name in enumerate(PLAYLIST_TRACKS):
        y=201+54*i
        review.extend([rect(24,y,896,54,PAPER,LINE,1),text(41,y+34,name,14),
                       text(253,y+34,f"Music/Beets/Lena Vale/Northern Lines/0{i+1} {name}.flac",13),
                       text(815,y+34,"Ready",13,GREEN,True)])
    review.extend([*banner(24,337,896,"Ready to sync","Syncthing transfers these files after the export is published."),
                   rule(0,530,944,530),*button(705,545,94,"Cancel","normal","SCR53.C02"),
                   *button(810,545,110,"Start sync","primary","SCR53.C01")])


def sync_list_transfer(complete=False):
    status = "2 of 2 tracks · Up to date" if complete else "1 of 2 tracks · Transferring"
    title = "Sync list complete" if complete else "Sync list started"
    detail = "Both tracks are available on Pixel 6." if complete else "Syncthing is sending the two selected tracks to Pixel 6."
    return (replace("38 of 60 files · 1.4 GB of 2.2 GB",status),
            over(367,452,700,16,*progress(368,456,697,100 if complete else 50)),
            draw(*banner(368,513,526,title,detail)))


extend_board(10,"Build a sync list from the Library",
    frame(8,"sync-list-open","Open the Sync list in Library and select two tracks",
          "Pixel 6 and the default Musicolette profile are visible beside the selected tracks.",
          library_state("Tracks",mode="Sync",entries=(),selected=PLAYLIST_TRACKS)),
    frame(8,"sync-tracks-dragging","Drag the selection toward the Sync list",
          "The Sync drop target highlights while the selected tracks remain visible in the catalog.",
          library_state("Tracks",mode="Sync",entries=(),selected=PLAYLIST_TRACKS,dragging=True)),
    frame(8,"sync-list-populated","Drop the tracks into the Sync list",
          "Both tracks are staged for Pixel 6 and Preview export becomes available.",
          library_state("Tracks",mode="Sync",entries=PLAYLIST_TRACKS,selected=PLAYLIST_TRACKS)),
    frame(53,"sync-list-export-preview","Choose Preview export",
          "The preview shows the two selected device paths and offers Start sync or Cancel.",sync_list_review),
    frame(50,"sync-list-transferring","Choose Start sync",
          "Android Devices shows the selected list transferring to Pixel 6.",*sync_list_transfer()),
    frame(50,"sync-list-complete","Wait for transfer to complete",
          "Both selected tracks are available on Pixel 6.",*sync_list_transfer(True)))


def svg_data(root):
    ET.indent(root)
    return ET.tostring(root, encoding="unicode") + "\n"


def render_screens(manifest):
    assets = {}
    for screen in manifest["screens"]:
        root = load_screen(screen["id"])
        add_button_icons(root)
        assets[DOCS.parent / screen["asset"]] = svg_data(root)
    return assets


def render_frame(storyboard_id, state_id, step_id, step, source):
    root = load_screen(source["id"])
    for change in step.changes:
        change(root)
    # An overlay can cover a canonical control with a new representation of
    # the same action. Retain the accessible marker on the visible instance.
    controls = {}
    for node in root.iter():
        control = node.get("data-control-id")
        if control in controls:
            controls[control].attrib.pop("data-control-id")
        if control:
            controls[control] = node
    root.set("data-state-id", state_id)
    root.set("data-storyboard-id", storyboard_id)
    root.set("data-step-id", step_id)
    root.find(tag("title")).text = f"{source['title']} — {step.result}"
    add_button_icons(root)
    return svg_data(root)


def sequences_for(manifest):
    screens = {s["id"]: s for s in manifest["screens"]}
    checked = {s["id"] for s in manifest["storyboards"]}
    if checked != set(BOARDS):
        raise ValueError("Storyboard IDs differ between sources")
    assets = {}
    for storyboard in manifest["storyboards"]:
        ident = storyboard["id"]
        paths = BOARDS[ident]
        frames = list(dict.fromkeys(step for _, steps in paths for step in steps))
        if len(frames) != len({step.slug for step in frames}):
            raise ValueError(f"Duplicate frame slug in {ident}")
        actor = storyboard["steps"][0]["actor"]
        ids = {step: f"{ident}-{i}" for i, step in enumerate(frames, 1)}
        transitions = defaultdict(dict)
        for label, steps in paths:
            for i, step in enumerate(steps):
                transitions[step][label] = ids[steps[i+1]] if i+1 < len(steps) else "END"
        storyboard["sequences"] = []
        storyboard["steps"] = []
        storyboard.pop("asset", None)
        directory = DESIGN / "frames" / ident
        for step in frames:
            step_id = ids[step]
            number = step_id.rsplit("-",1)[-1].zfill(2)
            screen_id = f"SCR{step.screen:02}"
            state_id = f"{screen_id}.{step_id}"
            dest = directory / f"{number}-{step.slug}.svg"
            asset = dest.relative_to(DOCS.parent).as_posix()
            source = screens[screen_id]
            assets[dest] = render_frame(ident, state_id, step_id, step, source)
            source["states"][state_id] = step.result
            source["state_specs"][state_id] = {"description": step.result, "asset": asset, "step_id": step_id}
            edges = transitions[step]
            primary = next(iter(edges.values()))
            storyboard["steps"].append({
                "step_id": step_id, "screen_id": screen_id, "state_id": state_id, "asset": asset,
                "actor": actor, "action": step.action, "result": step.result,
                "next_success": primary, "next_by_path": edges,
            })
        for label, steps in paths:
            storyboard["sequences"].append({"title": label, "steps": [ids[step] for step in steps]})
    return assets


def asset_review(manifest):
    filename = DESIGN / "_manifest.md"
    if filename.exists() and re.search(r"^- `[^`\n]+` \{#[^}]+\}$", filename.read_text(encoding="utf-8"), re.M):
        # Authored graph edges are owned by Asset Review's manifest.
        return
    lines = [
        "# Asset Review Manifest", "",
        "Beets Man Stage 0. Each storyboard path is an ordered sequence of full-size state wireframes.",
        "The [UI coverage inventory](../testing/ui/coverage.md) describes the actions and visible results.", "",
        *(f"- `{Path(screen['asset']).name}`" for screen in manifest["screens"]),
    ]
    for storyboard in manifest["storyboards"]:
        by_id = {step["step_id"]: step for step in storyboard["steps"]}
        for sequence in storyboard["sequences"]:
            title = sequence["title"]
            lines.extend(["", f"### Sequence: {storyboard['id']} {title}", ""])
            for step_id in sequence["steps"]:
                asset = Path(by_id[step_id]["asset"]).relative_to("docs/design").as_posix()
                lines.append(f"- `{asset}`")
    filename.write_text("\n".join(lines) + "\n", encoding="utf-8")


def coverage(manifest):
    filename = DOCS / "testing/ui/coverage.md"
    markers = ["\n## Storyboards\n", "\n## Workflow transitions\n"]
    original = filename.read_text(encoding="utf-8")
    prefix = original.split(markers[0], 1)[0].split(markers[1], 1)[0].rstrip()
    lines = [
        prefix,"", "## Storyboards", "",
        "Each path lists its UI states in order. The Asset Review manifest connects these paths through choices and loops.", "",
    ]
    for storyboard in manifest["storyboards"]:
        by_id = {step["step_id"]: step for step in storyboard["steps"]}
        lines.extend([f"### {storyboard['id']} {storyboard['title']}", "",
                      f"Use case `{storyboard['use_case']}` · "
                      f"{', '.join(f'`{ref}`' for ref in storyboard['criteria'])} · "
                      f"{', '.join(f'`{ref}`' for ref in storyboard['tests'])}", ""])
        for seq in storyboard["sequences"]:
            lines.extend([f"**{seq['title']}**", ""])
            for sid in seq["steps"]:
                s = by_id[sid]
                rel = "../../" + Path(s["asset"]).relative_to("docs").as_posix()
                lines.append(f"- [{s['step_id']} · {s['screen_id']}]({rel}) — {s['action']}. {s['result']}")
            lines.append("")
    filename.write_text("\n".join(lines).rstrip() + "\n", encoding="utf-8")


def use_case_index(manifest):
    candidate = json.loads((DOCS / "stage-0/requirements-candidate.json").read_text())
    use_cases = candidate["useCases"]
    graphs = json.loads((DOCS / "use-cases/qbrain-graphs.json").read_text())
    graph_files = {f.stem for f in (DOCS / "use-cases").glob("UC-*.svg")}
    by_case = defaultdict(list)
    for b in manifest["storyboards"]:
        by_case[b["use_case"]].append(b)
    assert set(by_case) == {uc["id"] for uc in use_cases} == graph_files == {g["id"] for g in graphs}
    assert len(graphs) == len(use_cases)
    lines = [
        "# UML use-case diagrams", "",
        "Each use case below is linked to its UI wireframes and Asset Review storyboard paths.", "",
        "[Workspace-wide PlantUML model](overview.puml)", "",
        "| Use case | Wireframes | Storyboards |",
        "| --- | --- | --- |",
    ]
    for uc in use_cases:
        ident = uc["id"]
        boards = by_case[ident]
        assert boards[0]["id"] == uc["storyboard"] and all(b["steps"] for b in boards)
        screen_ids = sorted({step["screen_id"] for b in boards for step in b["steps"]})
        links = ", ".join(f"[{s}](../design/{s}.svg)" for s in screen_ids)
        paths = ", ".join(f"[{b['id']}](../testing/ui/coverage.md#{anchor(b)})" for b in boards)
        lines.append(f"| [{ident} · {uc['title']}]({ident}.svg) · [PlantUML]({ident}.puml) | {links} | {paths} |")
    lines.extend(["", "See the [QBrain readback receipts](../stage-0/readback.md) for the saved graph exports.", ""])
    (DOCS / "use-cases/README.md").write_text("\n".join(lines), encoding="utf-8")


def anchor(board):
    return re.sub(r"[^a-z0-9\s-]", "", f"{board['id']} {board['title']}".lower()).replace(" ", "-")


def main():
    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    manifest["schema_version"] = "1.3.0"
    old_assets = {step["asset"] for b in manifest["storyboards"] for step in b["steps"] if "asset" in step}
    for screen in manifest["screens"]:
        for field_name in ("states", "state_specs"):
            screen[field_name] = {key: value for key, value in screen[field_name].items() if ".STB" not in key}
    assets = {**render_screens(manifest), **sequences_for(manifest)}
    for file, svg in assets.items():
        if not file.is_file() or file.read_text(encoding="utf-8") != svg:
            file.parent.mkdir(parents=True, exist_ok=True)
            file.write_text(svg, encoding="utf-8")
    MANIFEST.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    asset_review(manifest)
    coverage(manifest)
    use_case_index(manifest)
    current_assets = {step["asset"] for b in manifest["storyboards"] for step in b["steps"]}
    for asset in sorted(old_assets - current_assets):
        stale = DOCS.parent / asset
        if stale.is_relative_to(DESIGN / "frames") and stale.is_file():
            stale.unlink()
    board_count = len(manifest["storyboards"])
    paths = sum(len(b["sequences"]) for b in manifest["storyboards"])
    steps = sum(len(b["steps"]) for b in manifest["storyboards"])
    print(f"{board_count} storyboards, {paths} ordered review sequences, {steps} state wireframes.")


if __name__ == "__main__":
    main()
