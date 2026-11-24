"""Claude tool-use agent that edits a CapCut project."""
from __future__ import annotations

import base64
import io
import json
import os
import traceback
from typing import Any, Callable, Dict, List, Optional

import anthropic

import capcut_engine as E

MODEL = "claude-opus-5-5"

SYSTEM = """You are a video-editing assistant that edits CapCut desktop projects directly by changing the project file.
The user chats with you (usually in Hebrew) and you perform the edits with the tools. Reply in the user's language, short and friendly.

How the timeline works:
- Times are in seconds. Segment ids are the 8-char ids from get_project_overview (prefixes are fine).
- Tracks: "video#0" is the MAIN track (magnetic: clips sit back-to-back; inserting/deleting ripples). Extra video tracks
  ("video#1"...) are overlays / picture-in-picture. There are also audio#N, text#N, filter#N, effect#N tracks.
- Position x,y: units of HALF the canvas. (0,0)=center, x=1 right edge, x=-1 left edge, y=1 TOP, y=-1 BOTTOM.
  Text near the bottom is y≈-0.7, title near top y≈0.6. scale 1.0 = fit canvas. opacity 0..1. rotation in degrees.
- Volume 1.0 = 100%, 0 = mute, up to ~2.0 louder.
- Effects/transitions/animations/filters/fonts are referenced by catalog NAME. Many names are Chinese (e.g. 渐显 = fade in,
  渐隐 = fade out, 放大 = zoom in, 缩小 = zoom out, 叠化 = dissolve, 闪白 = white flash, 向左滑动 = slide left).
  Always call search_catalog to get an exact name before using one. Prefer items with used_before=true (already cached
  on this PC) and avoid vip=true items unless the user asks (they need CapCut Pro).

Working rules:
- Start a request by calling get_project_overview (unless you just did and nothing changed).
- When the user names a folder of photos/videos, call list_folder_media first. To pick or order photos by content
  (e.g. "start with the photo where everyone smiles") use look_at_images on a few candidates.
- Do the whole job in as few tool calls as reasonable (tools accept lists of ids). Then give a short summary of what changed.
- If a request is ambiguous in a way that matters (which song? which folder?), ask one short question instead of guessing.
- Every user request is one undo step; the user can press "undo" in the app or ask you to undo (tool undo_last_request).
- Changes are written to the project file immediately. Remind the user, only when relevant, that CapCut must be closed
  while editing, and they reopen the project in CapCut to see the result.
"""

S = {"type": "string"}
N = {"type": "number"}
B = {"type": "boolean"}
IDS = {"type": "array", "items": {"type": "string"}}

TOOLS: List[Dict[str, Any]] = [
    {"name": "get_project_overview", "description": "Return tracks and segments of the current project (ids, times, files, effects).",
     "input_schema": {"type": "object", "properties": {}}},
    {"name": "list_projects", "description": "List the CapCut projects on this PC.", "input_schema": {"type": "object", "properties": {}}},
    {"name": "switch_project", "description": "Open another existing project by its name.",
     "input_schema": {"type": "object", "properties": {"name": S}, "required": ["name"]}},
    {"name": "create_project", "description": "Create a new empty CapCut project and switch to it.",
     "input_schema": {"type": "object", "properties": {"name": S, "ratio": {"type": "string", "enum": ["16:9", "9:16", "1:1", "4:3", "3:4", "4:5", "21:9"]}},
                      "required": ["name"]}},
    {"name": "list_folder_media", "description": "List photos, videos and audio files in a folder on disk.",
     "input_schema": {"type": "object", "properties": {"folder": S, "recursive": B}, "required": ["folder"]}},
    {"name": "look_at_images", "description": "See up to 8 images (thumbnails) so you can choose/order them by content. Video files show nothing.",
     "input_schema": {"type": "object", "properties": {"paths": IDS}, "required": ["paths"]}},
    {"name": "add_media", "description": "Add photos/videos (files or whole folders) to the timeline. target='main' appends to the main track (or inserts at `start`, rippling later clips). target='overlay' puts them on an overlay track at half size starting at `start`; 'new_track' = overlay track at full size.",
     "input_schema": {"type": "object", "properties": {
         "paths": IDS, "target": {"type": "string", "enum": ["main", "overlay", "new_track"]},
         "start": N, "photo_duration": {"type": "number", "description": "seconds per photo, default 3"},
         "max_video_duration": N, "sort_by_name": B}, "required": ["paths"]}},
    {"name": "add_audio", "description": "Add music / sound. fit_to_video=true makes it span the whole video (looping if needed, else trimmed).",
     "input_schema": {"type": "object", "properties": {
         "path": S, "start": N, "duration": N, "trim_start": {"type": "number", "description": "skip this many seconds of the song"},
         "fit_to_video": B, "loop": B, "volume": N, "fade_in": N, "fade_out": N, "new_track": B}, "required": ["path"]}},
    {"name": "add_text", "description": "Add a text / title / caption.",
     "input_schema": {"type": "object", "properties": {
         "text": S, "start": N, "duration": N, "font_size": {"type": "number", "description": "default 8; titles 12-20"},
         "color": {"type": "string", "description": "#RRGGBB"}, "x": N, "y": N, "bold": B, "italic": B,
         "align": {"type": "string", "enum": ["left", "center", "right"]}, "stroke_color": S, "stroke_width": N,
         "background_color": S, "background_alpha": N, "shadow": B, "font": {"type": "string", "description": "font name from search_catalog('font')"}},
         "required": ["text", "start", "duration"]}},
    {"name": "edit_segments", "description": "Change properties of one or more segments. Only given fields change. duration changes on the main track ripple later clips.",
     "input_schema": {"type": "object", "properties": {
         "ids": IDS, "start": N, "duration": N, "x": N, "y": N, "scale": N, "rotation": N, "opacity": N,
         "volume": N, "speed": N, "flip_horizontal": B, "flip_vertical": B, "visible": B,
         "text": S, "font_size": N, "color": S, "bold": B}, "required": ["ids"]}},
    {"name": "move_segment", "description": "Move a segment in time and/or to another track ('video#1', 'text#0', 'audio#new'...).",
     "input_schema": {"type": "object", "properties": {"id": S, "start": N, "to_track": S}, "required": ["id"]}},
    {"name": "delete_segments", "description": "Delete segments. On the main track later clips close the gap unless ripple=false.",
     "input_schema": {"type": "object", "properties": {"ids": IDS, "ripple": B}, "required": ["ids"]}},
    {"name": "split_segment", "description": "Cut a segment in two at an absolute time (seconds).",
     "input_schema": {"type": "object", "properties": {"id": S, "at": N}, "required": ["id", "at"]}},
    {"name": "reorder_main_track", "description": "Reorder clips of the main track; listed ids go first in that order, others follow; gaps removed.",
     "input_schema": {"type": "object", "properties": {"order": IDS}, "required": ["order"]}},
    {"name": "close_gaps", "description": "Remove empty gaps on a track (default video#0).",
     "input_schema": {"type": "object", "properties": {"track": S}}},
    {"name": "set_track_mute", "description": "Mute/unmute a whole track, e.g. 'video#0' to silence original clip sound.",
     "input_schema": {"type": "object", "properties": {"track": S, "muted": B}, "required": ["track", "muted"]}},
    {"name": "set_canvas", "description": "Change aspect ratio of the project.",
     "input_schema": {"type": "object", "properties": {"ratio": {"type": "string", "enum": ["16:9", "9:16", "1:1", "4:3", "3:4", "4:5", "21:9", "2:1"]}}, "required": ["ratio"]}},
    {"name": "search_catalog", "description": "Search CapCut effects by name. Categories: transition, video_in, video_out, video_combo, text_in, text_out, text_loop, filter, effect, body_effect, font. Empty query lists the first items.",
     "input_schema": {"type": "object", "properties": {"category": S, "query": S, "limit": {"type": "integer"}}, "required": ["category"]}},
    {"name": "set_transitions", "description": "Set the transition after each given segment (between it and the next clip on its track). all_main=true applies to every clip of the main track except the last. name=null removes.",
     "input_schema": {"type": "object", "properties": {"ids": IDS, "all_main": B, "name": {"type": ["string", "null"]}, "duration": N}}},
    {"name": "set_animations", "description": "Set in/out/group animation on segments (video or text). For text use kind in/out/loop. name=null removes that kind.",
     "input_schema": {"type": "object", "properties": {"ids": IDS, "all_main": B, "kind": {"type": "string", "enum": ["in", "out", "group", "loop"]},
                                                       "name": {"type": ["string", "null"]}, "duration": N}, "required": ["kind"]}},
    {"name": "add_motion", "description": "Ken-Burns keyframe motion over the whole clip: zoom_in, zoom_out, pan_left, pan_right, pan_up, pan_down, fade_in_out, none. preset='alternate' cycles zoom_in/zoom_out/pan_left/pan_right across the given clips.",
     "input_schema": {"type": "object", "properties": {"ids": IDS, "all_main_photos": B, "preset": S, "strength": {"type": "number", "description": "0.05-0.4, default 0.15"}}, "required": ["preset"]}},
    {"name": "add_filter_or_effect", "description": "Add a filter (color look) or a video effect over a time range as its own track.",
     "input_schema": {"type": "object", "properties": {"category": {"type": "string", "enum": ["filter", "effect"]}, "name": S,
                                                       "start": N, "duration": N, "intensity": N}, "required": ["category", "name", "start", "duration"]}},
    {"name": "set_audio_fade", "description": "Set fade in/out (seconds) on audio or video segments' sound.",
     "input_schema": {"type": "object", "properties": {"ids": IDS, "fade_in": N, "fade_out": N}, "required": ["ids"]}},
    {"name": "undo_last_request", "description": "Restore the project to how it was before the previous request.",
     "input_schema": {"type": "object", "properties": {}}},
]

MUTATING = {"add_media", "add_audio", "add_text", "edit_segments", "move_segment", "delete_segments", "split_segment",
            "reorder_main_track", "close_gaps", "set_track_mute", "set_canvas", "set_transitions", "set_animations",
            "add_motion", "add_filter_or_effect", "set_audio_fade"}


def _thumb(path: str) -> Optional[Dict[str, Any]]:
    try:
        from PIL import Image, ImageOps
        im = Image.open(path)
        im = ImageOps.exif_transpose(im).convert("RGB")
        im.thumbnail((512, 512))
        buf = io.BytesIO()
        im.save(buf, "JPEG", quality=75)
        return {"type": "image", "source": {"type": "base64", "media_type": "image/jpeg",
                                            "data": base64.b64encode(buf.getvalue()).decode()}}
    except Exception:
        return None


class Agent:
    def __init__(self, api_key: str, on_event: Callable[[str, Any], None]):
        self.client = anthropic.Anthropic(api_key=api_key or None, max_retries=3, timeout=600)
        self.on_event = on_event
        self.catalog = E.Catalog()
        self.project: Optional[E.Project] = None
        self.messages: List[Dict[str, Any]] = []
        self._snapped = False
        self.stop_flag = False

    # ---- project
    def open_project(self, folder: str):
        self.project = E.Project(folder)
        self.messages = []
        self.on_event("project", {"name": self.project.name, "folder": folder})

    def _find_project(self, name: str) -> str:
        projs = E.list_projects()
        for p in projs:
            if p["name"].lower() == name.lower():
                return p["folder"]
        for p in projs:
            if name.lower() in p["name"].lower():
                return p["folder"]
        raise E.EditError(f"No project named '{name}'")

    def undo(self) -> str:
        if not self.project:
            raise E.EditError("No project open")
        label = self.project.undo()
        return label

    # ---- tools
    def _ids(self, inp, key="ids", all_key=None, photos_only=False) -> List[str]:
        ids = list(inp.get(key) or [])
        if all_key and inp.get(all_key):
            t = self.project.main_track(create=False)
            segs = t["segments"] if t else []
            if photos_only:
                segs = [s for s in segs if (self.project.find_material(s["material_id"])[1] or {}).get("type") == "photo"]
            ids += [s["id"] for s in segs]
        if not ids:
            raise E.EditError("No segment ids given")
        return ids

    def run_tool(self, name: str, inp: Dict[str, Any]):
        p = self.project
        if name == "list_projects":
            return [{"name": x["name"], "duration_s": x["duration_s"]} for x in E.list_projects()]
        if name == "switch_project":
            self.open_project_keep_chat(self._find_project(inp["name"]))
            return self.project.overview()
        if name == "create_project":
            folder = E.create_project(inp["name"], inp.get("ratio", "16:9"))
            self.open_project_keep_chat(folder)
            return {"created": os.path.basename(folder)}
        if name == "list_folder_media":
            return E.list_folder_media(inp["folder"], inp.get("recursive", False))
        if name == "look_at_images":
            blocks: List[Dict[str, Any]] = []
            for path in inp["paths"][:8]:
                th = _thumb(path)
                if th:
                    blocks.append({"type": "text", "text": os.path.basename(path)})
                    blocks.append(th)
            return blocks or "Could not open any of those images"
        if p is None:
            raise E.EditError("No project is open. Ask the user to pick one, or use list_projects/switch_project/create_project.")
        if name == "get_project_overview":
            return p.overview()
        if name == "undo_last_request":
            return {"undone": p.undo()}
        if name in MUTATING and not self._snapped:
            p.snapshot(self.current_request[:120])
            self._snapped = True
        cat = self.catalog
        if name == "add_media":
            return p.add_media(inp["paths"], inp.get("target", "main"), inp.get("start"), inp.get("photo_duration", 3.0),
                               inp.get("max_video_duration"), inp.get("sort_by_name", True))
        if name == "add_audio":
            return p.add_audio(inp["path"], inp.get("start", 0.0), inp.get("duration"), inp.get("trim_start", 0.0),
                               inp.get("fit_to_video", False), inp.get("loop", False), inp.get("volume", 1.0),
                               inp.get("fade_in", 0.0), inp.get("fade_out", 0.0), inp.get("new_track", False))
        if name == "add_text":
            kw = {k: v for k, v in inp.items() if k not in ("text", "start", "duration")}
            return p.add_text(inp["text"], inp["start"], inp["duration"], catalog=cat, **kw)
        if name == "edit_segments":
            ch = {k: v for k, v in inp.items() if k != "ids"}
            return [p.edit_segment(i, **ch) for i in inp["ids"]]
        if name == "move_segment":
            return p.move_segment(inp["id"], inp.get("start"), inp.get("to_track"))
        if name == "delete_segments":
            return {"deleted": p.delete_segments(inp["ids"], inp.get("ripple", True))}
        if name == "split_segment":
            return p.split_segment(inp["id"], inp["at"])
        if name == "reorder_main_track":
            return p.reorder_main(inp["order"])
        if name == "close_gaps":
            return {"moved": p.close_gaps(inp.get("track", "video#0"))}
        if name == "set_track_mute":
            p.set_track_mute(inp["track"], inp["muted"])
            return "ok"
        if name == "set_canvas":
            return p.set_canvas(inp["ratio"])
        if name == "search_catalog":
            return cat.search(inp["category"], inp.get("query", ""), inp.get("limit", 60))
        if name == "set_transitions":
            ids = self._ids(inp, all_key="all_main")
            if inp.get("all_main"):
                ids = ids[:-1]
            item = cat.get("transition", inp["name"]) if inp.get("name") else None
            return [p.set_transition(i, item, inp.get("duration")) for i in ids]
        if name == "set_animations":
            ids = self._ids(inp, all_key="all_main")
            out = []
            for i in ids:
                _, _, tr, _ = p.find_segment(i)
                is_text = tr["type"] == "text"
                kind = inp["kind"]
                catname = {("in", False): "video_in", ("out", False): "video_out", ("group", False): "video_combo",
                           ("in", True): "text_in", ("out", True): "text_out", ("loop", True): "text_loop",
                           ("group", True): "text_loop", ("loop", False): "video_combo"}[(kind, is_text)]
                if is_text and kind == "group":
                    kind = "loop"
                if not is_text and kind == "loop":
                    kind = "group"
                item = cat.get(catname, inp["name"]) if inp.get("name") else None
                out.append(p.set_animation(i, kind, item, inp.get("duration")))
            return out
        if name == "add_motion":
            ids = self._ids(inp, all_key="all_main_photos", photos_only=True)
            preset = inp["preset"]
            cycle = ["zoom_in", "pan_left", "zoom_out", "pan_right"]
            return [p.add_motion(i, cycle[n % 4] if preset == "alternate" else preset, inp.get("strength", 0.15))
                    for n, i in enumerate(ids)]
        if name == "add_filter_or_effect":
            item = cat.get(inp["category"], inp["name"])
            return p.add_track_effect(inp["category"], item, inp["start"], inp["duration"], inp.get("intensity", 1.0))
        if name == "set_audio_fade":
            return [p.set_audio_fade(i, inp.get("fade_in", 0.0), inp.get("fade_out", 0.0)) for i in inp["ids"]]
        raise E.EditError(f"Unknown tool {name}")

    def open_project_keep_chat(self, folder: str):
        self.project = E.Project(folder)
        self._snapped = False
        self.on_event("project", {"name": self.project.name, "folder": folder})

    # ---- chat loop
    def send(self, text: str):
        self.current_request = text
        self._snapped = False
        self.stop_flag = False
        ctx = ""
        if self.project:
            ctx = f"[Current project: {self.project.name}]\n"
        else:
            ctx = "[No project open yet]\n"
        self.messages.append({"role": "user", "content": ctx + text})
        for _ in range(40):
            if self.stop_flag:
                self.on_event("assistant", "⏹ נעצר.")
                return
            resp = self.client.beta.messages.create(
                model=MODEL,
                max_tokens=16000,
                system=[{"type": "text", "text": SYSTEM, "cache_control": {"type": "ephemeral"}}],
                tools=TOOLS,
                messages=self.messages,
                thinking={"type": "adaptive"},
                output_config={"effort": "medium"},
                betas=["server-side-fallback-2026-07-01"],
                fallbacks="default",
            )
            self.messages.append({"role": "assistant", "content": resp.content})
            for b in resp.content:
                if b.type == "text" and b.text.strip():
                    self.on_event("assistant", b.text)
            if resp.stop_reason == "refusal":
                self.on_event("error", "הבקשה נדחתה על ידי המודל.")
                return
            if resp.stop_reason == "pause_turn":
                continue
            if resp.stop_reason != "tool_use":
                if resp.stop_reason == "max_tokens":
                    self.on_event("error", "התשובה נקטעה (max_tokens).")
                return
            results = []
            changed = False
            for b in resp.content:
                if b.type != "tool_use":
                    continue
                self.on_event("tool", {"name": b.name, "input": b.input})
                try:
                    out = self.run_tool(b.name, dict(b.input or {}))
                    if b.name in MUTATING or b.name == "undo_last_request":
                        if b.name in MUTATING:
                            self.project.save()
                        changed = True
                    if isinstance(out, list) and out and isinstance(out[0], dict) and out[0].get("type") in ("image", "text"):
                        content = out
                    else:
                        content = json.dumps(out, ensure_ascii=False, default=str)
                    results.append({"type": "tool_result", "tool_use_id": b.id, "content": content})
                except E.EditError as e:
                    results.append({"type": "tool_result", "tool_use_id": b.id, "content": f"Error: {e}", "is_error": True})
                except Exception as e:
                    traceback.print_exc()
                    results.append({"type": "tool_result", "tool_use_id": b.id,
                                    "content": f"Error: {type(e).__name__}: {e}", "is_error": True})
            if changed:
                self.on_event("changed", None)
            self.messages.append({"role": "user", "content": results})
        self.on_event("error", "יותר מדי צעדים — עצרתי.")
