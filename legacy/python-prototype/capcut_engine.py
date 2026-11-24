"""CapCut draft editing engine.

Edits CapCut (desktop, 9.x) draft_content.json files in place. Everything that is
not touched is preserved byte-for-byte as JSON, so existing projects keep all their
settings. Times inside the draft are microseconds; the public API here uses seconds.
"""
from __future__ import annotations

import copy
import glob
import json
import os
import shutil
import subprocess
import time
import uuid
from typing import Any, Dict, List, Optional

US = 1_000_000
PHOTO_DURATION = 10_800_000_000  # what CapCut stores for still images

LOCALAPPDATA = os.environ.get("LOCALAPPDATA", os.path.expanduser("~\\AppData\\Local"))
CAPCUT_DIR = os.path.join(LOCALAPPDATA, "CapCut")
DRAFTS_ROOT = os.path.join(CAPCUT_DIR, "User Data", "Projects", "com.lveditor.draft")
APP_DATA = os.path.join(os.environ.get("APPDATA", os.path.expanduser("~")), "CapCutAIEditor")
BACKUP_ROOT = os.path.join(APP_DATA, "backups")

IMAGE_EXT = {".jpg", ".jpeg", ".png", ".bmp", ".webp", ".heic", ".gif", ".tif", ".tiff"}
VIDEO_EXT = {".mp4", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".webm", ".flv", ".mts", ".3gp"}
AUDIO_EXT = {".mp3", ".wav", ".m4a", ".aac", ".flac", ".ogg", ".wma", ".opus"}

RENDER_BASE = {"video": 0, "audio": 0, "effect": 10000, "filter": 11000, "sticker": 14000, "text": 15000}


class EditError(Exception):
    pass


def new_id() -> str:
    u = str(uuid.uuid4()).upper()
    # CapCut writes the 3rd group in lowercase; mimic it
    parts = u.split("-")
    parts[2] = parts[2].lower()
    return "-".join(parts)


def sec(us: int) -> float:
    return round(us / US, 3)


def to_us(s: float) -> int:
    return int(round(float(s) * US))


# --------------------------------------------------------------------------- CapCut process

def capcut_exe() -> Optional[str]:
    hits = sorted(glob.glob(os.path.join(CAPCUT_DIR, "Apps", "*", "CapCut.exe")))
    if hits:
        return hits[-1]
    p = os.path.join(CAPCUT_DIR, "Apps", "CapCut.exe")
    return p if os.path.exists(p) else None


def capcut_running() -> bool:
    try:
        out = subprocess.run(["tasklist", "/FI", "IMAGENAME eq CapCut.exe", "/NH"], capture_output=True,
                             text=True, creationflags=0x08000000).stdout
        return "CapCut.exe" in out
    except Exception:
        return False


def close_capcut(force_after: float = 8.0) -> bool:
    """Ask CapCut to close (it autosaves), force it if it does not exit in time."""
    flags = 0x08000000
    subprocess.run(["taskkill", "/IM", "CapCut.exe"], capture_output=True, creationflags=flags)
    t0 = time.time()
    while time.time() - t0 < force_after:
        if not capcut_running():
            return True
        time.sleep(0.5)
    subprocess.run(["taskkill", "/F", "/IM", "CapCut.exe"], capture_output=True, creationflags=flags)
    time.sleep(1)
    return not capcut_running()


def open_capcut() -> bool:
    exe = capcut_exe()
    if not exe:
        return False
    subprocess.Popen([exe], cwd=os.path.dirname(exe), creationflags=0x00000008)  # DETACHED_PROCESS
    return True


# --------------------------------------------------------------------------- media probing

def media_kind(path: str) -> Optional[str]:
    ext = os.path.splitext(path)[1].lower()
    if ext in IMAGE_EXT:
        return "photo"
    if ext in VIDEO_EXT:
        return "video"
    if ext in AUDIO_EXT:
        return "audio"
    return None


def probe(path: str) -> Dict[str, Any]:
    """Return {kind, duration_us, width, height, has_audio}."""
    if not os.path.isfile(path):
        raise EditError(f"File not found: {path}")
    kind = media_kind(path)
    if kind is None:
        raise EditError(f"Unsupported file type: {path}")
    info = {"kind": kind, "duration_us": 0, "width": 0, "height": 0, "has_audio": False}
    try:
        from pymediainfo import MediaInfo
        mi = MediaInfo.parse(path)
        for t in mi.tracks:
            tt = t.track_type
            if tt in ("Video", "Image") and not info["width"]:
                info["width"] = int(t.width or 0)
                info["height"] = int(t.height or 0)
                rot = str(getattr(t, "rotation", "") or "")
                if rot.startswith("90") or rot.startswith("270"):
                    info["width"], info["height"] = info["height"], info["width"]
            if tt == "Audio":
                info["has_audio"] = True
            if tt in ("General", "Video", "Audio") and t.duration and not info["duration_us"]:
                info["duration_us"] = int(float(t.duration) * 1000)
    except Exception as e:  # pragma: no cover
        if kind != "photo":
            raise EditError(f"Could not read media info for {path}: {e}")
    if kind == "photo":
        info["duration_us"] = PHOTO_DURATION
        if not info["width"]:
            info["width"], info["height"] = 1920, 1080
    elif info["duration_us"] <= 0:
        raise EditError(f"Could not determine duration of {path}")
    return info


# --------------------------------------------------------------------------- material builders

def _speed():
    return {"id": new_id(), "type": "speed", "mode": 0, "speed": 1.0, "curve_speed": None}


def _canvas():
    return {"id": new_id(), "type": "canvas_color", "color": "", "blur": 0.0, "image": "", "album_image": "",
            "image_id": "", "image_name": "", "source_platform": 0, "team_id": ""}


def _sound_map():
    return {"id": new_id(), "type": "none", "audio_channel_mapping": 0, "is_config_open": False}


def _vocal_sep():
    return {"id": new_id(), "type": "vocal_separation", "choice": 0, "removed_sounds": [], "time_range": None,
            "production_path": "", "final_algorithm": "", "enter_from": ""}


def _crop():
    return {"upper_left_x": 0.0, "upper_left_y": 0.0, "upper_right_x": 1.0, "upper_right_y": 0.0,
            "lower_left_x": 0.0, "lower_left_y": 1.0, "lower_right_x": 1.0, "lower_right_y": 1.0}


def _clip(x=0.0, y=0.0, scale=1.0, rotation=0.0, alpha=1.0):
    return {"scale": {"x": scale, "y": scale}, "rotation": rotation, "rotation3d": [],
            "transform": {"x": x, "y": y}, "flip": {"vertical": False, "horizontal": False}, "alpha": alpha}


def _base_segment(material_id: str, start: int, duration: int, source_start: Optional[int],
                  refs: List[str], visual: bool, render_index: int, volume: float = 1.0) -> Dict[str, Any]:
    seg = {
        "id": new_id(),
        "source_timerange": None if source_start is None else {"start": source_start, "duration": duration},
        "target_timerange": {"start": start, "duration": duration},
        "render_timerange": {"start": 0, "duration": 0},
        "desc": "", "state": 0, "speed": 1.0, "is_loop": False, "is_tone_modify": False, "reverse": False,
        "intensifies_audio": False, "cartoon": False, "volume": volume, "last_nonzero_volume": 1.0,
        "clip": _clip() if visual else None,
        "uniform_scale": {"on": True, "value": 1.0} if visual else None,
        "material_id": material_id,
        "extra_material_refs": refs,
        "render_index": render_index, "keyframe_refs": [],
        "enable_lut": visual, "enable_adjust": visual, "enable_hsl": False, "visible": True, "group_id": "",
        "enable_color_curves": True, "enable_hsl_curves": True, "track_render_index": 0,
        "hdr_settings": {"mode": 1, "intensity": 1.0, "nits": 1000} if visual else None,
        "enable_color_wheels": True, "track_attribute": 0, "is_placeholder": False, "template_id": "",
        "enable_smart_color_adjust": False, "template_scene": "default", "common_keyframes": [],
        "caption_info": None,
        "responsive_layout": {"enable": False, "target_follow": "", "size_layout": 0,
                              "horizontal_pos_layout": 0, "vertical_pos_layout": 0},
        "enable_color_match_adjust": False, "enable_color_correct_adjust": False, "enable_adjust_mask": False,
        "raw_segment_id": "", "lyric_keyframes": None, "enable_video_mask": True,
        "digital_human_template_group_id": "", "color_correct_alg_result": "", "source": "segmentsourcenormal",
        "enable_mask_stroke": False, "enable_mask_shadow": False, "enable_color_adjust_pro": False,
        "segment_color_tag": "", "lumi_occlusion": None,
    }
    return seg


def hex_to_rgb(color: str) -> List[float]:
    c = color.strip().lstrip("#")
    if len(c) == 3:
        c = "".join(ch * 2 for ch in c)
    if len(c) != 6:
        raise EditError(f"Bad color '{color}', use #RRGGBB")
    return [int(c[i:i + 2], 16) / 255.0 for i in (0, 2, 4)]


# --------------------------------------------------------------------------- catalog

class Catalog:
    """Names -> CapCut resource ids for transitions, animations, filters, effects, fonts.

    Effects the user already used in their own drafts are preferred (CapCut has them cached),
    then the large built-in catalog shipped with pycapcut.
    """

    CATS = {
        "transition": "TransitionType",
        "video_in": "IntroType",
        "video_out": "OutroType",
        "video_combo": "GroupAnimationType",
        "text_in": "TextIntro",
        "text_out": "TextOutro",
        "text_loop": "TextLoopAnim",
        "filter": "FilterType",
        "effect": "VideoSceneEffectType",
        "body_effect": "VideoCharacterEffectType",
        "font": "FontType",
    }

    def __init__(self):
        self.items: Dict[str, List[Dict[str, Any]]] = {k: [] for k in self.CATS}
        try:
            import pycapcut.metadata as md
            for cat, cls_name in self.CATS.items():
                enum = getattr(md, cls_name, None)
                if enum is None:
                    continue
                for member in enum:
                    v = member.value
                    item = {"name": getattr(v, "name", None) or getattr(v, "title", member.name),
                            "key": member.name,
                            "effect_id": v.effect_id, "resource_id": v.resource_id,
                            "vip": bool(getattr(v, "is_vip", False))}
                    if hasattr(v, "default_duration"):
                        item["duration"] = v.default_duration
                        item["is_overlap"] = v.is_overlap
                    if hasattr(v, "duration") and not hasattr(v, "default_duration"):
                        item["duration"] = v.duration
                    self.items[cat].append(item)
        except Exception:
            pass
        self._scan_user_drafts()

    def _scan_user_drafts(self):
        seen = set()
        for path in glob.glob(os.path.join(DRAFTS_ROOT, "*", "draft_content.json")):
            try:
                with open(path, encoding="utf-8") as f:
                    d = json.load(f)
            except Exception:
                continue
            m = d.get("materials", {})
            for t in m.get("transitions", []):
                key = ("transition", t.get("effect_id"))
                if key in seen:
                    continue
                seen.add(key)
                self.items["transition"].insert(0, {
                    "name": t.get("name"), "key": t.get("name"), "effect_id": t.get("effect_id"),
                    "resource_id": t.get("resource_id"), "path": t.get("path", ""),
                    "duration": t.get("duration", 500000), "is_overlap": t.get("is_overlap", True),
                    "category_id": t.get("category_id", ""), "used_before": True, "vip": False})
            for ma in m.get("material_animations", []):
                for a in ma.get("animations", []):
                    cat = {"in": "video_in", "out": "video_out", "group": "video_combo"}.get(a.get("type"))
                    if ma.get("type") != "video_animation" or not cat:
                        continue
                    key = (cat, a.get("id"))
                    if key in seen:
                        continue
                    seen.add(key)
                    self.items[cat].insert(0, {
                        "name": a.get("name"), "key": a.get("name"), "effect_id": a.get("id"),
                        "resource_id": a.get("resource_id"), "path": a.get("path", ""),
                        "duration": a.get("duration", 500000), "category_id": a.get("category_id", ""),
                        "used_before": True, "vip": False})

    def search(self, category: str, query: str = "", limit: int = 60) -> List[Dict[str, Any]]:
        if category not in self.items:
            raise EditError(f"Unknown category '{category}'. Use one of {list(self.items)}")
        q = (query or "").lower().strip()
        out = []
        for it in self.items[category]:
            if q and q not in str(it["name"]).lower() and q not in str(it["key"]).lower():
                continue
            out.append({"name": it["name"], "vip": it.get("vip", False),
                        "used_before": it.get("used_before", False),
                        "default_duration_s": sec(it["duration"]) if it.get("duration") else None})
            if len(out) >= limit:
                break
        return out

    def get(self, category: str, name: str) -> Dict[str, Any]:
        items = self.items.get(category)
        if items is None:
            raise EditError(f"Unknown category '{category}'")
        n = name.lower().strip()
        for it in items:
            if str(it["name"]).lower().strip() == n or str(it["key"]).lower() == n:
                return it
        for it in items:
            if n in str(it["name"]).lower() or n in str(it["key"]).lower():
                return it
        raise EditError(f"No {category} named '{name}'. Call search_catalog first.")


# --------------------------------------------------------------------------- projects

def list_projects() -> List[Dict[str, Any]]:
    out = []
    root_meta = os.path.join(DRAFTS_ROOT, "root_meta_info.json")
    names = {}
    try:
        with open(root_meta, encoding="utf-8") as f:
            for e in json.load(f).get("all_draft_store", []):
                names[os.path.normcase(os.path.normpath(e.get("draft_fold_path", "")))] = e
    except Exception:
        pass
    for folder in glob.glob(os.path.join(DRAFTS_ROOT, "*")):
        content = os.path.join(folder, "draft_content.json")
        if not os.path.isfile(content):
            continue
        e = names.get(os.path.normcase(os.path.normpath(folder)), {})
        out.append({
            "name": e.get("draft_name") or os.path.basename(folder),
            "folder": folder,
            "modified": os.path.getmtime(content),
            "duration_s": sec(e.get("tm_duration", 0) or 0),
        })
    out.sort(key=lambda p: -p["modified"])
    return out


class Project:
    def __init__(self, folder: str):
        self.folder = folder
        self.content_path = os.path.join(folder, "draft_content.json")
        if not os.path.isfile(self.content_path):
            raise EditError(f"Not a CapCut project folder: {folder}")
        self.reload()

    # ---- io
    def reload(self):
        with open(self.content_path, encoding="utf-8") as f:
            self.d = json.load(f)
        self.dirty = False

    @property
    def name(self) -> str:
        meta = os.path.join(self.folder, "draft_meta_info.json")
        try:
            with open(meta, encoding="utf-8") as f:
                return json.load(f).get("draft_name") or os.path.basename(self.folder)
        except Exception:
            return os.path.basename(self.folder)

    @property
    def backup_dir(self) -> str:
        return os.path.join(BACKUP_ROOT, self.d.get("id", "unknown"))

    def _content_targets(self) -> List[str]:
        targets = [self.content_path]
        tl = os.path.join(self.folder, "Timelines", self.d.get("id", ""), "draft_content.json")
        if os.path.isfile(tl):
            targets.append(tl)
        return targets

    def snapshot(self, label: str):
        """Save the on-disk state before an edit so it can be undone."""
        os.makedirs(self.backup_dir, exist_ok=True)
        stamp = time.strftime("%Y%m%d-%H%M%S") + f"-{int(time.time() * 1000) % 1000:03d}"
        dst = os.path.join(self.backup_dir, f"{stamp}.json")
        shutil.copy2(self.content_path, dst)
        with open(dst + ".label", "w", encoding="utf-8") as f:
            f.write(label)
        # keep the last 60
        files = sorted(glob.glob(os.path.join(self.backup_dir, "*.json")))
        for old in files[:-60]:
            for p in (old, old + ".label"):
                try:
                    os.remove(p)
                except OSError:
                    pass

    def save(self):
        self._recompute_duration()
        data = json.dumps(self.d, ensure_ascii=False, separators=(",", ":"))
        for target in self._content_targets():
            tmp = target + ".aitmp"
            with open(tmp, "w", encoding="utf-8") as f:
                f.write(data)
            os.replace(tmp, target)
        self._update_meta()
        self.dirty = False

    def undo(self) -> str:
        files = sorted(glob.glob(os.path.join(self.backup_dir, "*.json")))
        if not files:
            raise EditError("Nothing to undo")
        last = files[-1]
        label = ""
        try:
            with open(last + ".label", encoding="utf-8") as f:
                label = f.read()
        except OSError:
            pass
        with open(last, encoding="utf-8") as f:
            self.d = json.load(f)
        self.save()
        for p in (last, last + ".label"):
            try:
                os.remove(p)
            except OSError:
                pass
        return label

    def _update_meta(self):
        dur = self.d.get("duration", 0)
        now = int(time.time() * 1_000_000)
        meta = os.path.join(self.folder, "draft_meta_info.json")
        try:
            with open(meta, encoding="utf-8") as f:
                m = json.load(f)
            m["tm_duration"] = dur
            m["tm_draft_modified"] = now
            with open(meta, "w", encoding="utf-8") as f:
                json.dump(m, f, ensure_ascii=False)
        except Exception:
            pass
        root = os.path.join(DRAFTS_ROOT, "root_meta_info.json")
        try:
            with open(root, encoding="utf-8") as f:
                r = json.load(f)
            me = os.path.normcase(os.path.normpath(self.folder))
            for e in r.get("all_draft_store", []):
                if os.path.normcase(os.path.normpath(e.get("draft_fold_path", ""))) == me:
                    e["tm_duration"] = dur
                    e["tm_draft_modified"] = now
            with open(root, "w", encoding="utf-8") as f:
                json.dump(r, f, ensure_ascii=False)
        except Exception:
            pass

    # ---- helpers
    @property
    def m(self) -> Dict[str, list]:
        return self.d["materials"]

    def mlist(self, key: str) -> list:
        if not isinstance(self.m.get(key), list):
            self.m[key] = []
        return self.m[key]

    def _recompute_duration(self):
        end = 0
        for t in self.d.get("tracks", []):
            for s in t["segments"]:
                tr = s["target_timerange"]
                end = max(end, tr["start"] + tr["duration"])
        self.d["duration"] = end

    def find_material(self, mid: str):
        for key, lst in self.m.items():
            if isinstance(lst, list):
                for it in lst:
                    if isinstance(it, dict) and it.get("id") == mid:
                        return key, it
        return None, None

    def find_segment(self, sid: str):
        for ti, t in enumerate(self.d["tracks"]):
            for si, s in enumerate(t["segments"]):
                if s["id"] == sid or s["id"].lower().startswith(sid.lower()):
                    return ti, si, t, s
        raise EditError(f"Segment '{sid}' not found. Call get_project_overview for ids.")

    def short(self, sid: str) -> str:
        return sid[:8]

    def tracks_of(self, ttype: str) -> List[Dict[str, Any]]:
        return [t for t in self.d["tracks"] if t["type"] == ttype]

    def add_track(self, ttype: str, name: str = "") -> Dict[str, Any]:
        t = {"id": new_id(), "type": ttype, "segments": [], "flag": 0, "attribute": 0, "name": name,
             "is_default_name": not name}
        self.d["tracks"].append(t)
        return t

    def track_render_index(self, track: Dict[str, Any]) -> int:
        same = self.tracks_of(track["type"])
        return RENDER_BASE.get(track["type"], 0) + same.index(track)

    def main_track(self, create=True) -> Optional[Dict[str, Any]]:
        vids = self.tracks_of("video")
        if vids:
            return vids[0]
        return self.add_track("video") if create else None

    @staticmethod
    def track_end(track) -> int:
        return max([s["target_timerange"]["start"] + s["target_timerange"]["duration"] for s in track["segments"]],
                   default=0)

    @staticmethod
    def free_at(track, start: int, dur: int, ignore_id: str = "") -> bool:
        for s in track["segments"]:
            if s["id"] == ignore_id:
                continue
            a = s["target_timerange"]["start"]
            b = a + s["target_timerange"]["duration"]
            if start < b and a < start + dur:
                return False
        return True

    def pick_track(self, ttype: str, start: int, dur: int, track_index: Optional[int] = None,
                   new_track: bool = False) -> Dict[str, Any]:
        same = self.tracks_of(ttype)
        if track_index is not None:
            if track_index < 0 or track_index >= len(same):
                raise EditError(f"There is no {ttype} track #{track_index}; there are {len(same)}")
            return same[track_index]
        if not new_track:
            start_idx = 1 if ttype == "video" else 0  # don't put overlays on the main track implicitly
            for t in same[start_idx:]:
                if self.free_at(t, start, dur):
                    return t
        return self.add_track(ttype)

    @staticmethod
    def sort_track(track):
        track["segments"].sort(key=lambda s: s["target_timerange"]["start"])

    # ---- overview
    def overview(self) -> Dict[str, Any]:
        self._recompute_duration()
        canvas = self.d.get("canvas_config", {})
        res = {"project": self.name, "duration_s": sec(self.d.get("duration", 0)),
               "canvas": f'{canvas.get("width")}x{canvas.get("height")} ({canvas.get("ratio")})',
               "fps": self.d.get("fps"), "tracks": []}
        counters: Dict[str, int] = {}
        for t in self.d["tracks"]:
            idx = counters.get(t["type"], 0)
            counters[t["type"]] = idx + 1
            label = f'{t["type"]}#{idx}' + (" (main)" if t["type"] == "video" and idx == 0 else "")
            segs = []
            for s in t["segments"]:
                segs.append(self.describe_segment(s))
            res["tracks"].append({"track": label, "muted": bool(t.get("attribute", 0) & 1), "segments": segs})
        return res

    def describe_segment(self, s) -> Dict[str, Any]:
        tr = s["target_timerange"]
        info: Dict[str, Any] = {"id": self.short(s["id"]), "start": sec(tr["start"]),
                                "end": sec(tr["start"] + tr["duration"]), "dur": sec(tr["duration"])}
        key, mat = self.find_material(s["material_id"])
        if mat is not None:
            if key == "videos":
                info["kind"] = mat.get("type")
                info["file"] = os.path.basename(mat.get("path", "")) or mat.get("material_name")
            elif key == "audios":
                info["kind"] = "audio"
                info["file"] = mat.get("name") or os.path.basename(mat.get("path", ""))
            elif key == "texts":
                info["kind"] = "text"
                try:
                    info["text"] = json.loads(mat.get("content", "{}")).get("text", "")[:80]
                except Exception:
                    info["text"] = "?"
            else:
                info["kind"] = mat.get("type") or key.rstrip("s")
                if mat.get("name"):
                    info["name"] = mat.get("name")
        clip = s.get("clip")
        if clip:
            if clip["transform"]["x"] or clip["transform"]["y"]:
                info["pos"] = [round(clip["transform"]["x"], 3), round(clip["transform"]["y"], 3)]
            if clip["scale"]["x"] != 1.0 or clip["scale"]["y"] != 1.0:
                info["scale"] = round(clip["scale"]["x"], 3)
            if clip.get("rotation"):
                info["rot"] = clip["rotation"]
            if clip.get("alpha", 1.0) != 1.0:
                info["opacity"] = clip["alpha"]
        if s.get("volume", 1.0) != 1.0 and info.get("kind") in ("video", "audio"):
            info["volume"] = s["volume"]
        if s.get("speed", 1.0) != 1.0:
            info["speed"] = s["speed"]
        extras = []
        for r in s.get("extra_material_refs", []):
            k, em = self.find_material(r)
            if k == "transitions":
                extras.append(f'transition:{em.get("name")}')
            elif k == "material_animations":
                for a in em.get("animations", []):
                    extras.append(f'anim-{a.get("type")}:{a.get("name")}')
            elif k == "audio_fades":
                extras.append(f'fade:{sec(em.get("fade_in_duration", 0))}/{sec(em.get("fade_out_duration", 0))}')
        if s.get("common_keyframes"):
            extras.append("keyframes")
        if extras:
            info["fx"] = extras
        return info

    # ---- media
    def _video_material(self, path: str, pr: Dict[str, Any]) -> Dict[str, Any]:
        # reuse an existing material for the same file
        for v in self.mlist("videos"):
            if os.path.normcase(v.get("path", "")) == os.path.normcase(path.replace("\\", "/")) or \
                    os.path.normcase(v.get("path", "")) == os.path.normcase(path):
                return v
        mat = {
            "id": new_id(), "unique_id": "", "type": pr["kind"], "duration": pr["duration_us"],
            "path": path.replace("\\", "/"), "media_path": "", "local_id": "", "has_audio": pr["has_audio"],
            "width": pr["width"], "height": pr["height"], "category_id": "", "category_name": "local",
            "material_id": "", "material_name": os.path.basename(path), "material_url": "",
            "crop": _crop(), "crop_ratio": "free", "audio_fade": None, "crop_scale": 1.0,
            "extra_type_option": 0, "source": 0, "source_platform": 0, "formula_id": "",
            "check_flag": 62978047, "local_material_id": str(uuid.uuid4()), "origin_material_id": "",
            "request_id": "", "has_sound_separated": False, "picture_from": "none",
        }
        self.mlist("videos").append(mat)
        return mat

    def _audio_material(self, path: str, pr: Dict[str, Any]) -> Dict[str, Any]:
        for a in self.mlist("audios"):
            if os.path.normcase(a.get("path", "")) == os.path.normcase(path.replace("\\", "/")):
                return a
        mid = new_id()
        mat = {"app_id": 0, "category_id": "", "category_name": "local", "check_flag": 3,
               "copyright_limit_type": "none", "duration": pr["duration_us"], "effect_id": "", "formula_id": "",
               "id": mid, "local_material_id": str(uuid.uuid4()), "music_id": str(uuid.uuid4()),
               "name": os.path.basename(path), "path": path.replace("\\", "/"), "source_platform": 0,
               "type": "extract_music", "wave_points": []}
        self.mlist("audios").append(mat)
        return mat

    def _visual_refs(self) -> List[str]:
        sp, cv, sm, vs = _speed(), _canvas(), _sound_map(), _vocal_sep()
        self.mlist("speeds").append(sp)
        self.mlist("canvases").append(cv)
        self.mlist("sound_channel_mappings").append(sm)
        self.mlist("vocal_separations").append(vs)
        return [sp["id"], cv["id"], sm["id"], vs["id"]]

    def add_media(self, paths: List[str], target: str = "main", start: Optional[float] = None,
                  photo_duration: float = 3.0, max_video_duration: Optional[float] = None,
                  sort_by_name: bool = True) -> List[Dict[str, Any]]:
        files: List[str] = []
        for p in paths:
            p = os.path.expandvars(os.path.expanduser(p))
            if os.path.isdir(p):
                files += [os.path.join(p, f) for f in os.listdir(p)
                          if media_kind(f) in ("photo", "video")]
            else:
                files.append(p)
        if sort_by_name:
            files.sort(key=lambda f: os.path.basename(f).lower())
        if not files:
            raise EditError("No images/videos found in the given paths")
        added = []
        if target == "main":
            track = self.main_track()
            cursor = to_us(start) if start is not None else self.track_end(track)
        else:
            track = None
            cursor = to_us(start or 0)
        for f in files:
            pr = probe(f)
            if pr["kind"] == "audio":
                raise EditError(f"{f} is audio; use add_audio")
            mat = self._video_material(os.path.abspath(f), pr)
            if pr["kind"] == "photo":
                dur = to_us(photo_duration)
                src = 0
            else:
                dur = pr["duration_us"]
                if max_video_duration:
                    dur = min(dur, to_us(max_video_duration))
                src = 0
            if target == "main":
                tr = track
                if not self.free_at(tr, cursor, dur):
                    # insert: push later segments on the main track to the right
                    for s in tr["segments"]:
                        if s["target_timerange"]["start"] >= cursor:
                            s["target_timerange"]["start"] += dur
            else:
                tr = self.pick_track("video", cursor, dur, new_track=(target == "new_track"))
            seg = _base_segment(mat["id"], cursor, dur, src, self._visual_refs(), True,
                                self.track_render_index(tr))
            seg["track_render_index"] = self.track_render_index(tr)
            if target != "main":
                seg["clip"]["scale"] = {"x": 0.5, "y": 0.5} if target == "overlay" else seg["clip"]["scale"]
            tr["segments"].append(seg)
            self.sort_track(tr)
            added.append(self.describe_segment(seg))
            if target == "main":
                cursor += dur
        return added

    def add_audio(self, path: str, start: float = 0.0, duration: Optional[float] = None,
                  trim_start: float = 0.0, fit_to_video: bool = False, loop: bool = False,
                  volume: float = 1.0, fade_in: float = 0.0, fade_out: float = 0.0,
                  new_track: bool = False) -> List[Dict[str, Any]]:
        path = os.path.abspath(os.path.expandvars(os.path.expanduser(path)))
        pr = probe(path)
        if pr["kind"] == "photo":
            raise EditError("That's an image, not audio")
        mat = self._audio_material(path, pr)
        st = to_us(start)
        src0 = to_us(trim_start)
        avail = pr["duration_us"] - src0
        if avail <= 0:
            raise EditError("trim_start is beyond the end of the audio")
        if fit_to_video:
            self._recompute_duration()
            vid_end = max([self.track_end(t) for t in self.tracks_of("video")], default=0)
            total = max(vid_end - st, 0)
            if total <= 0:
                raise EditError("There is no video to fit the audio to")
        elif duration is not None:
            total = to_us(duration)
        else:
            total = avail
        pieces = []
        if loop or fit_to_video:  # fit_to_video loops the song if it is shorter than the video
            remaining, t = total, st
            while remaining > 0:
                d = min(remaining, avail)
                pieces.append((t, d))
                t += d
                remaining -= d
        else:
            pieces.append((st, min(total, avail)))
        added = []
        tr = None
        for i, (t, d) in enumerate(pieces):
            if tr is None:
                full = pieces[-1][0] + pieces[-1][1] - pieces[0][0]
                tr = self.pick_track("audio", pieces[0][0], full, new_track=new_track)
            sp, sm, vs = _speed(), _sound_map(), _vocal_sep()
            self.mlist("speeds").append(sp)
            self.mlist("sound_channel_mappings").append(sm)
            self.mlist("vocal_separations").append(vs)
            refs = [sp["id"], sm["id"], vs["id"]]
            fi = to_us(fade_in) if i == 0 else 0
            fo = to_us(fade_out) if i == len(pieces) - 1 else 0
            if fi or fo:
                fade = {"id": new_id(), "fade_in_duration": fi, "fade_out_duration": fo, "fade_type": 0,
                        "type": "audio_fade"}
                self.mlist("audio_fades").append(fade)
                refs.append(fade["id"])
            seg = _base_segment(mat["id"], t, d, src0, refs, False, 0, volume=volume)
            tr["segments"].append(seg)
            added.append(self.describe_segment(seg))
        self.sort_track(tr)
        return added

    # ---- text
    def add_text(self, text: str, start: float, duration: float, font_size: float = 8.0,
                 color: str = "#FFFFFF", x: float = 0.0, y: float = -0.7, bold: bool = False,
                 italic: bool = False, align: str = "center", stroke_color: Optional[str] = None,
                 stroke_width: float = 0.08, background_color: Optional[str] = None,
                 background_alpha: float = 0.6, font: Optional[str] = None, catalog: Optional[Catalog] = None,
                 shadow: bool = False) -> Dict[str, Any]:
        rgb = hex_to_rgb(color)
        style: Dict[str, Any] = {
            "fill": {"alpha": 1.0, "content": {"render_type": "solid", "solid": {"alpha": 1.0, "color": rgb}}},
            "range": [0, len(text)], "size": font_size, "bold": bold, "italic": italic, "underline": False,
            "strokes": [],
        }
        check = 7
        if stroke_color:
            style["strokes"] = [{"content": {"solid": {"alpha": 1.0, "color": hex_to_rgb(stroke_color)}},
                                 "width": stroke_width}]
            check |= 8
        if shadow:
            style["shadows"] = [{"diffuse": 15 / 100 / 6, "alpha": 0.8, "distance": 5.0,
                                 "content": {"solid": {"color": [0, 0, 0]}}, "angle": -45.0}]
            check |= 32
        if font and catalog:
            f = catalog.get("font", font)
            style["font"] = {"id": f["resource_id"], "path": "D:"}
        content = {"styles": [style], "text": text}
        mat = {"id": new_id(), "content": json.dumps(content, ensure_ascii=False),
               "typesetting": 0, "alignment": {"left": 0, "center": 1, "right": 2}.get(align, 1),
               "letter_spacing": 0.0, "line_spacing": 0.02, "line_feed": 1, "line_max_width": 0.82,
               "force_apply_line_max_width": False, "check_flag": check, "type": "text", "global_alpha": 1.0}
        if background_color:
            mat.update({"background_style": 1, "background_color": background_color,
                        "background_alpha": background_alpha, "background_round_radius": 0.0,
                        "background_height": 0.14, "background_width": 0.14,
                        "background_horizontal_offset": 0.5, "background_vertical_offset": 0.5})
            mat["check_flag"] |= 16
        self.mlist("texts").append(mat)
        st, du = to_us(start), to_us(duration)
        tr = self.pick_track("text", st, du)
        seg = _base_segment(mat["id"], st, du, None, [], True, self.track_render_index(tr))
        seg["clip"]["transform"] = {"x": x, "y": y}
        seg["track_render_index"] = self.track_render_index(tr)
        tr["segments"].append(seg)
        self.sort_track(tr)
        return self.describe_segment(seg)

    # ---- editing
    def edit_segment(self, sid: str, **ch) -> Dict[str, Any]:
        ti, si, track, s = self.find_segment(sid)
        tr = s["target_timerange"]
        old_end = tr["start"] + tr["duration"]
        is_main = track is self.main_track(create=False)
        try:
            return self._edit_segment(track, s, ch)
        finally:
            # main track is magnetic in CapCut: keep later clips butted up against this one
            new_end = tr["start"] + tr["duration"]
            if is_main and new_end != old_end and ch.get("start") is None:
                for o in track["segments"]:
                    if o is not s and o["target_timerange"]["start"] >= old_end:
                        o["target_timerange"]["start"] += new_end - old_end

    def _edit_segment(self, track, s, ch) -> Dict[str, Any]:
        tr = s["target_timerange"]
        if ch.get("start") is not None:
            tr["start"] = to_us(ch["start"])
        if ch.get("duration") is not None:
            new_d = to_us(ch["duration"])
            key, mat = self.find_material(s["material_id"])
            if s.get("source_timerange") and key in ("videos", "audios") and mat.get("type") != "photo":
                spd = s.get("speed", 1.0) or 1.0
                max_d = int((mat["duration"] - s["source_timerange"]["start"]) / spd)
                if new_d > max_d:
                    raise EditError(f"The clip is only {sec(max_d)}s long from its trim point")
                s["source_timerange"]["duration"] = int(new_d * spd)
            elif s.get("source_timerange"):
                s["source_timerange"]["duration"] = new_d
            tr["duration"] = new_d
        clip = s.get("clip")
        if clip is not None:
            if ch.get("x") is not None:
                clip["transform"]["x"] = float(ch["x"])
            if ch.get("y") is not None:
                clip["transform"]["y"] = float(ch["y"])
            if ch.get("scale") is not None:
                clip["scale"] = {"x": float(ch["scale"]), "y": float(ch["scale"])}
                if s.get("uniform_scale"):
                    s["uniform_scale"]["value"] = 1.0
            if ch.get("rotation") is not None:
                clip["rotation"] = float(ch["rotation"])
            if ch.get("opacity") is not None:
                clip["alpha"] = float(ch["opacity"])
            if ch.get("flip_horizontal") is not None:
                clip["flip"]["horizontal"] = bool(ch["flip_horizontal"])
            if ch.get("flip_vertical") is not None:
                clip["flip"]["vertical"] = bool(ch["flip_vertical"])
        if ch.get("volume") is not None:
            s["volume"] = float(ch["volume"])
            if s["volume"] > 0:
                s["last_nonzero_volume"] = s["volume"]
        if ch.get("speed") is not None:
            spd = float(ch["speed"])
            if spd <= 0:
                raise EditError("speed must be > 0")
            src = s.get("source_timerange")
            if src:
                tr["duration"] = int(src["duration"] / spd)
            s["speed"] = spd
            for r in s["extra_material_refs"]:
                k, em = self.find_material(r)
                if k == "speeds":
                    em["speed"] = spd
        if ch.get("visible") is not None:
            s["visible"] = bool(ch["visible"])
        # text-specific
        key, mat = self.find_material(s["material_id"])
        if key == "texts":
            content = json.loads(mat["content"])
            st = content["styles"][0] if content.get("styles") else {}
            if ch.get("text") is not None:
                content["text"] = ch["text"]
                for sty in content.get("styles", []):
                    sty["range"] = [0, len(ch["text"])]
                content["styles"] = content.get("styles", [])[:1] or [st]
            if ch.get("font_size") is not None:
                for sty in content["styles"]:
                    sty["size"] = float(ch["font_size"])
            if ch.get("color") is not None:
                for sty in content["styles"]:
                    sty.setdefault("fill", {"alpha": 1.0, "content": {"render_type": "solid", "solid": {"alpha": 1.0}}})
                    sty["fill"]["content"]["solid"]["color"] = hex_to_rgb(ch["color"])
            if ch.get("bold") is not None:
                for sty in content["styles"]:
                    sty["bold"] = bool(ch["bold"])
            mat["content"] = json.dumps(content, ensure_ascii=False)
            if "words" in mat and isinstance(mat.get("words"), dict):
                mat["words"] = {"start_time": [], "end_time": [], "text": []}
        self.sort_track(track)
        return self.describe_segment(s)

    def move_segment(self, sid: str, start: Optional[float] = None, to_track: Optional[str] = None) -> Dict[str, Any]:
        ti, si, track, s = self.find_segment(sid)
        if start is not None:
            s["target_timerange"]["start"] = to_us(start)
        if to_track:
            ttype, _, idx = to_track.partition("#")
            if ttype != track["type"]:
                raise EditError(f"Can't move a {track['type']} segment to a {ttype} track")
            if idx == "new":
                dest = self.add_track(ttype)
            else:
                same = self.tracks_of(ttype)
                i = int(idx or 0)
                if i >= len(same):
                    dest = self.add_track(ttype)
                else:
                    dest = same[i]
            if dest is not track:
                track["segments"].remove(s)
                dest["segments"].append(s)
                ri = self.track_render_index(dest)
                s["render_index"] = ri
                s["track_render_index"] = ri
                track = dest
        self.sort_track(track)
        return self.describe_segment(s)

    def delete_segments(self, ids: List[str], ripple: bool = True) -> int:
        n = 0
        for sid in ids:
            ti, si, track, s = self.find_segment(sid)
            tr = s["target_timerange"]
            track["segments"].remove(s)
            n += 1
            is_main = track is self.main_track(create=False)
            if ripple and is_main:
                for o in track["segments"]:
                    if o["target_timerange"]["start"] >= tr["start"]:
                        o["target_timerange"]["start"] -= tr["duration"]
        self.d["tracks"] = [t for t in self.d["tracks"]
                            if t["segments"] or t is self.d["tracks"][0]]
        return n

    def split_segment(self, sid: str, at: float) -> List[Dict[str, Any]]:
        ti, si, track, s = self.find_segment(sid)
        tr = s["target_timerange"]
        cut = to_us(at) - tr["start"]
        if cut <= 0 or cut >= tr["duration"]:
            raise EditError("Split point must be inside the segment")
        b = copy.deepcopy(s)
        b["id"] = new_id()
        # give the copy its own speed/canvas etc. materials
        new_refs = []
        for r in b["extra_material_refs"]:
            k, em = self.find_material(r)
            if k in ("speeds", "canvases", "sound_channel_mappings", "vocal_separations", "material_colors",
                     "placeholder_infos", "material_animations", "audio_fades"):
                cp = copy.deepcopy(em)
                cp["id"] = new_id()
                self.mlist(k).append(cp)
                new_refs.append(cp["id"])
            elif k == "transitions":
                continue  # keep the transition on the second half only
            else:
                new_refs.append(r)
        b["extra_material_refs"] = new_refs
        s["extra_material_refs"] = [r for r in s["extra_material_refs"]
                                    if self.find_material(r)[0] != "transitions"]
        spd = s.get("speed", 1.0) or 1.0
        b["target_timerange"] = {"start": tr["start"] + cut, "duration": tr["duration"] - cut}
        if s.get("source_timerange"):
            src = s["source_timerange"]
            b["source_timerange"] = {"start": src["start"] + int(cut * spd), "duration": int((tr["duration"] - cut) * spd)}
            src["duration"] = int(cut * spd)
        tr["duration"] = cut
        b["common_keyframes"] = []
        track["segments"].insert(si + 1, b)
        return [self.describe_segment(s), self.describe_segment(b)]

    def close_gaps(self, track_label: str = "video#0") -> int:
        ttype, _, idx = track_label.partition("#")
        same = self.tracks_of(ttype)
        i = int(idx or 0)
        if i >= len(same):
            raise EditError(f"No track {track_label}")
        t = same[i]
        self.sort_track(t)
        cur = 0
        moved = 0
        for s in t["segments"]:
            if s["target_timerange"]["start"] != cur:
                s["target_timerange"]["start"] = cur
                moved += 1
            cur += s["target_timerange"]["duration"]
        return moved

    def reorder_main(self, order: List[str]) -> List[Dict[str, Any]]:
        t = self.main_track(create=False)
        if not t:
            raise EditError("No main track")
        by = {}
        for sid in order:
            _, _, tr, s = self.find_segment(sid)
            if tr is not t:
                raise EditError(f"{sid} is not on the main track")
            by[s["id"]] = s
        rest = [s for s in t["segments"] if s["id"] not in by]
        t["segments"] = [by[self.find_segment(sid)[3]["id"]] for sid in order] + rest
        cur = 0
        for s in t["segments"]:
            s["target_timerange"]["start"] = cur
            cur += s["target_timerange"]["duration"]
        return [self.describe_segment(s) for s in t["segments"]]

    def set_track_mute(self, track_label: str, muted: bool) -> None:
        ttype, _, idx = track_label.partition("#")
        same = self.tracks_of(ttype)
        i = int(idx or 0)
        if i >= len(same):
            raise EditError(f"No track {track_label}")
        same[i]["attribute"] = (same[i].get("attribute", 0) | 1) if muted else (same[i].get("attribute", 0) & ~1)

    def set_canvas(self, ratio: str) -> str:
        presets = {"16:9": (1920, 1080), "9:16": (1080, 1920), "1:1": (1080, 1080), "4:3": (1440, 1080),
                   "3:4": (1080, 1440), "21:9": (2560, 1080), "4:5": (1080, 1350), "2:1": (2160, 1080)}
        if ratio not in presets:
            raise EditError(f"Ratio must be one of {list(presets)}")
        w, h = presets[ratio]
        self.d["canvas_config"].update({"ratio": ratio, "width": w, "height": h})
        return f"{w}x{h}"

    # ---- effects
    def set_transition(self, sid: str, item: Optional[Dict[str, Any]], duration: Optional[float]) -> str:
        """Transition between segment `sid` and the next one on its track. item=None removes."""
        _, si, track, s = self.find_segment(sid)
        for r in list(s["extra_material_refs"]):
            if self.find_material(r)[0] == "transitions":
                s["extra_material_refs"].remove(r)
        if item is None:
            return "removed"
        dur = to_us(duration) if duration else int(item.get("duration") or 500000)
        nxt = track["segments"][si + 1] if si + 1 < len(track["segments"]) else None
        if nxt is not None:
            dur = min(dur, s["target_timerange"]["duration"] // 2, nxt["target_timerange"]["duration"] // 2)
        mat = {"id": new_id(), "type": "transition", "name": item["name"], "effect_id": item["effect_id"],
               "resource_id": item["resource_id"], "third_resource_id": "", "source_platform": 1, "sub_type": 0,
               "adjust_params": [], "path": item.get("path", ""), "duration": dur,
               "is_overlap": item.get("is_overlap", True), "platform": "all",
               "category_id": item.get("category_id", ""), "category_name": "", "request_id": "",
               "is_ai_transition": False, "video_path": "", "task_id": ""}
        self.mlist("transitions").append(mat)
        s["extra_material_refs"].append(mat["id"])
        return f'{item["name"]} {sec(dur)}s'

    def set_animation(self, sid: str, kind: str, item: Optional[Dict[str, Any]], duration: Optional[float]) -> str:
        """kind: in / out / group (video) or in / out / loop (text)."""
        _, _, track, s = self.find_segment(sid)
        is_text = track["type"] == "text"
        anim_mat = None
        for r in s["extra_material_refs"]:
            k, em = self.find_material(r)
            if k == "material_animations":
                anim_mat = em
        if anim_mat is None:
            if item is None:
                return "nothing to remove"
            anim_mat = {"id": new_id(), "type": "sticker_animation" if is_text else "video_animation",
                        "animations": [], "multi_language_current": "none"}
            self.mlist("material_animations").append(anim_mat)
            s["extra_material_refs"].append(anim_mat["id"])
        # group (combo) replaces in/out and vice versa
        drop = {kind} | ({"in", "out"} if kind in ("group", "loop") else {"group", "loop"})
        anim_mat["animations"] = [a for a in anim_mat["animations"] if a.get("type") not in drop]
        if item is None:
            return "removed"
        seg_d = s["target_timerange"]["duration"]
        if kind in ("group", "loop"):
            dur = to_us(duration) if duration else seg_d
            start = 0
        else:
            dur = min(to_us(duration) if duration else int(item.get("duration") or 500000), seg_d)
            start = 0 if kind == "in" else seg_d - dur
        anim_mat["animations"].append({
            "id": item["effect_id"], "type": kind, "start": start, "duration": dur, "path": item.get("path", ""),
            "platform": "all", "resource_id": item["resource_id"], "third_resource_id": "",
            "source_platform": 1, "name": item["name"], "category_id": item.get("category_id", ""),
            "category_name": "", "panel": "" if is_text else "video",
            "material_type": "sticker" if is_text else "video", "anim_adjust_params": None, "request_id": ""})
        return f'{kind}: {item["name"]} {sec(dur)}s'

    def add_track_effect(self, category: str, item: Dict[str, Any], start: float, duration: float,
                         intensity: float = 1.0) -> Dict[str, Any]:
        st, du = to_us(start), to_us(duration)
        if category == "filter":
            mat = {"adjust_params": [], "algorithm_artifact_path": "", "apply_target_type": 0, "bloom_params": None,
                   "category_id": "", "category_name": "",
                   "color_match_info": {"source_feature_path": "", "target_feature_path": "", "target_image_path": ""},
                   "effect_id": item["effect_id"], "enable_skin_tone_correction": False, "exclusion_group": [],
                   "face_adjust_params": [], "formula_id": "", "id": new_id(), "intensity_key": "",
                   "multi_language_current": "", "name": item["name"], "panel_id": "", "platform": "all",
                   "resource_id": item["resource_id"], "source_platform": 1, "sub_type": "none",
                   "time_range": None, "type": "filter", "value": intensity, "version": ""}
            self.mlist("effects").append(mat)
            ttype = "filter"
        else:
            mat = {"adjust_params": [], "apply_target_type": 2, "apply_time_range": None, "category_id": "",
                   "category_name": "", "common_keyframes": [], "disable_effect_faces": [],
                   "effect_id": item["effect_id"], "formula_id": "", "id": new_id(), "name": item["name"],
                   "platform": "all", "render_index": 11000, "resource_id": item["resource_id"],
                   "source_platform": 0, "time_range": None, "track_render_index": 0,
                   "type": "video_effect", "value": 1.0, "version": ""}
            self.mlist("video_effects").append(mat)
            ttype = "effect"
        tr = self.pick_track(ttype, st, du)
        seg = _base_segment(mat["id"], st, du, None, [], False, self.track_render_index(tr))
        tr["segments"].append(seg)
        self.sort_track(tr)
        return self.describe_segment(seg)

    def add_motion(self, sid: str, preset: str, strength: float = 0.15) -> str:
        """Ken-Burns style keyframe motion over the whole segment."""
        _, _, track, s = self.find_segment(sid)
        if s.get("clip") is None:
            raise EditError("Only visual segments can move")
        d = s["target_timerange"]["duration"]
        base_scale = s["clip"]["scale"]["x"]
        bx, by = s["clip"]["transform"]["x"], s["clip"]["transform"]["y"]
        z = 1 + strength
        presets = {
            "zoom_in": {"KFTypeScaleX": (base_scale, base_scale * z), "KFTypeScaleY": (base_scale, base_scale * z)},
            "zoom_out": {"KFTypeScaleX": (base_scale * z, base_scale), "KFTypeScaleY": (base_scale * z, base_scale)},
            "pan_left": {"KFTypeScaleX": (base_scale * z, base_scale * z), "KFTypeScaleY": (base_scale * z, base_scale * z),
                         "KFTypePositionX": (bx + strength / 2, bx - strength / 2)},
            "pan_right": {"KFTypeScaleX": (base_scale * z, base_scale * z), "KFTypeScaleY": (base_scale * z, base_scale * z),
                          "KFTypePositionX": (bx - strength / 2, bx + strength / 2)},
            "pan_up": {"KFTypeScaleX": (base_scale * z, base_scale * z), "KFTypeScaleY": (base_scale * z, base_scale * z),
                       "KFTypePositionY": (by - strength / 2, by + strength / 2)},
            "pan_down": {"KFTypeScaleX": (base_scale * z, base_scale * z), "KFTypeScaleY": (base_scale * z, base_scale * z),
                         "KFTypePositionY": (by + strength / 2, by - strength / 2)},
            "fade_in_out": {"KFTypeAlpha": None},
            "none": {},
        }
        if preset not in presets:
            raise EditError(f"preset must be one of {list(presets)}")
        s["common_keyframes"] = [k for k in s.get("common_keyframes", [])
                                 if k.get("property_type") not in ("KFTypeScaleX", "KFTypeScaleY", "KFTypePositionX",
                                                                   "KFTypePositionY", "KFTypeAlpha", "UNIFORM_SCALE")]
        if preset == "none":
            return "motion removed"
        if s.get("uniform_scale"):
            s["uniform_scale"]["on"] = False

        def kf(t, v):
            return {"curveType": "Line", "graphID": "", "left_control": {"x": 0.0, "y": 0.0},
                    "right_control": {"x": 0.0, "y": 0.0}, "id": new_id(), "time_offset": t, "values": [v]}

        for prop, vals in presets[preset].items():
            if prop == "KFTypeAlpha":
                f = min(500000, d // 4)
                lst = [kf(0, 0.0), kf(f, 1.0), kf(d - f, 1.0), kf(d, 0.0)]
            else:
                lst = [kf(0, vals[0]), kf(d, vals[1])]
            s["common_keyframes"].append({"id": new_id(), "keyframe_list": lst, "material_id": "",
                                          "property_type": prop})
        return f"{preset} on {self.short(s['id'])}"

    def set_audio_fade(self, sid: str, fade_in: float, fade_out: float) -> str:
        _, _, track, s = self.find_segment(sid)
        for r in list(s["extra_material_refs"]):
            if self.find_material(r)[0] == "audio_fades":
                s["extra_material_refs"].remove(r)
        if fade_in or fade_out:
            fade = {"id": new_id(), "fade_in_duration": to_us(fade_in), "fade_out_duration": to_us(fade_out),
                    "fade_type": 0, "type": "audio_fade"}
            self.mlist("audio_fades").append(fade)
            s["extra_material_refs"].append(fade["id"])
        return f"fade {fade_in}s/{fade_out}s"


def list_folder_media(folder: str, recursive: bool = False) -> Dict[str, Any]:
    folder = os.path.expandvars(os.path.expanduser(folder))
    if not os.path.isdir(folder):
        raise EditError(f"Folder not found: {folder}")
    res = {"photo": [], "video": [], "audio": [], "subfolders": []}
    if recursive:
        walker = os.walk(folder)
    else:
        walker = [(folder, [d for d in os.listdir(folder) if os.path.isdir(os.path.join(folder, d))],
                   [f for f in os.listdir(folder) if os.path.isfile(os.path.join(folder, f))])]
    for root, dirs, files in walker:
        if root == folder:
            res["subfolders"] = dirs[:50]
        for f in sorted(files, key=str.lower):
            k = media_kind(f)
            if k:
                res[k].append(os.path.join(root, f))
    for k in ("photo", "video", "audio"):
        if len(res[k]) > 300:
            res[k] = res[k][:300] + [f"... and {len(res[k]) - 300} more"]
    return res


def create_project(name: str, ratio: str = "16:9") -> str:
    """Create a new empty project by cloning the skeleton of an existing one."""
    safe = "".join(c for c in name if c not in '<>:"/\\|?*').strip() or "AI Project"
    folder = os.path.join(DRAFTS_ROOT, safe)
    n = 1
    while os.path.exists(folder):
        n += 1
        folder = os.path.join(DRAFTS_ROOT, f"{safe} ({n})")
    projects = list_projects()
    if not projects:
        raise EditError("No existing CapCut project to use as a template. Create one empty project in CapCut first.")
    src = min(projects, key=lambda p: os.path.getsize(os.path.join(p["folder"], "draft_content.json")))["folder"]
    with open(os.path.join(src, "draft_content.json"), encoding="utf-8") as f:
        d = json.load(f)
    old_id = d["id"]
    d["id"] = new_id()
    d["tracks"] = []
    for k, v in d["materials"].items():
        if isinstance(v, list):
            d["materials"][k] = []
    d["duration"] = 0
    d["keyframes"] = {k: [] for k in d.get("keyframes", {})} if isinstance(d.get("keyframes"), dict) else d.get("keyframes")
    os.makedirs(folder)
    tl_dir = os.path.join(folder, "Timelines", d["id"])
    os.makedirs(tl_dir)
    for fn in ("draft_agency_config.json", "draft_settings", "timeline_layout.json", "attachment_pc_common.json"):
        if os.path.exists(os.path.join(src, fn)):
            shutil.copy2(os.path.join(src, fn), os.path.join(folder, fn))
    data = json.dumps(d, ensure_ascii=False)
    for p in (os.path.join(folder, "draft_content.json"), os.path.join(tl_dir, "draft_content.json")):
        with open(p, "w", encoding="utf-8") as f:
            f.write(data)
    proj = os.path.join(src, "Timelines", "project.json")
    if os.path.exists(proj):
        with open(proj, encoding="utf-8") as f:
            pj = json.load(f)
        now = int(time.time() * 1_000_000)
        pj["id"] = new_id()
        pj["main_timeline_id"] = d["id"]
        pj["create_time"] = pj["update_time"] = now
        pj["timelines"] = [{"create_time": now, "id": d["id"], "is_marked_delete": False,
                            "name": "Timeline 01", "update_time": now}]
        with open(os.path.join(folder, "Timelines", "project.json"), "w", encoding="utf-8") as f:
            json.dump(pj, f, ensure_ascii=False)
    with open(os.path.join(src, "draft_meta_info.json"), encoding="utf-8") as f:
        meta = json.load(f)
    now = int(time.time() * 1_000_000)
    meta.update({"draft_id": new_id(), "draft_name": os.path.basename(folder),
                 "draft_fold_path": folder.replace("\\", "/"), "draft_root_path": DRAFTS_ROOT,
                 "draft_materials": [], "tm_duration": 0, "tm_draft_create": now, "tm_draft_modified": now,
                 "draft_cover": "", "draft_timeline_materials_size_": 0})
    with open(os.path.join(folder, "draft_meta_info.json"), "w", encoding="utf-8") as f:
        json.dump(meta, f, ensure_ascii=False)
    # register in the home screen list
    root = os.path.join(DRAFTS_ROOT, "root_meta_info.json")
    try:
        with open(root, encoding="utf-8") as f:
            r = json.load(f)
        tmpl = copy.deepcopy(r["all_draft_store"][0]) if r.get("all_draft_store") else {}
        tmpl.update({"draft_cover": "", "draft_fold_path": folder.replace("\\", "/"), "draft_id": meta["draft_id"],
                     "draft_json_file": os.path.join(folder, "draft_content.json").replace("\\", "/"),
                     "draft_name": os.path.basename(folder), "tm_draft_create": now, "tm_draft_modified": now,
                     "tm_duration": 0, "draft_timeline_materials_size": 0})
        r.setdefault("all_draft_store", []).insert(0, tmpl)
        r["draft_ids"] = r.get("draft_ids", 0) + 1 if isinstance(r.get("draft_ids"), int) else r.get("draft_ids")
        with open(root, "w", encoding="utf-8") as f:
            json.dump(r, f, ensure_ascii=False)
    except Exception:
        pass
    p = Project(folder)
    p.set_canvas(ratio)
    p.save()
    return folder
