"""CapCut AI Editor - chat window that edits CapCut projects with Claude."""
from __future__ import annotations

import json
import os
import sys
import threading
import traceback

import webview

import capcut_engine as E
from agent import Agent

VERSION = "1.0.0"
SETTINGS = os.path.join(E.APP_DATA, "settings.json")


def resource(name: str) -> str:
    base = getattr(sys, "_MEIPASS", os.path.dirname(os.path.abspath(__file__)))
    return os.path.join(base, name)


def load_settings() -> dict:
    try:
        with open(SETTINGS, encoding="utf-8") as f:
            return json.load(f)
    except Exception:
        return {}


def save_settings(s: dict):
    os.makedirs(E.APP_DATA, exist_ok=True)
    with open(SETTINGS, "w", encoding="utf-8") as f:
        json.dump(s, f, ensure_ascii=False, indent=1)


class Api:
    def __init__(self):
        self.window = None
        self.settings = load_settings()
        self.agent: Agent | None = None
        self.busy = False

    # ---- plumbing
    def _emit(self, kind, data):
        if self.window:
            self.window.evaluate_js(f"onEvent({json.dumps(kind)}, {json.dumps(data, ensure_ascii=False, default=str)})")

    def _key(self) -> str:
        return self.settings.get("api_key") or os.environ.get("ANTHROPIC_API_KEY", "")

    def _ensure_agent(self) -> Agent:
        if self.agent is None:
            self.agent = Agent(self._key(), self._emit)
            last = self.settings.get("last_project")
            if last and os.path.isdir(last):
                try:
                    self.agent.open_project(last)
                except Exception:
                    pass
        return self.agent

    # ---- called from JS
    def init(self):
        has_key = bool(self._key())
        ag = None
        if has_key:
            try:
                ag = self._ensure_agent()
            except Exception as e:
                return {"has_key": False, "error": str(e), "version": VERSION}
        return {"has_key": has_key, "version": VERSION,
                "project": ag.project.name if ag and ag.project else None,
                "project_folder": ag.project.folder if ag and ag.project else None}

    def set_api_key(self, key: str):
        self.settings["api_key"] = key.strip()
        save_settings(self.settings)
        self.agent = None
        return self.init()

    def projects(self):
        return [{"name": p["name"], "folder": p["folder"], "duration": p["duration_s"]} for p in E.list_projects()]

    def open_project(self, folder: str):
        ag = self._ensure_agent()
        ag.open_project(folder)
        self.settings["last_project"] = folder
        save_settings(self.settings)
        return ag.project.overview()

    def new_project(self, name: str, ratio: str):
        folder = E.create_project(name, ratio)
        return self.open_project(folder)

    def overview(self):
        ag = self._ensure_agent()
        if not ag.project:
            return None
        ag.project.reload()
        return ag.project.overview()

    def capcut_status(self):
        return {"running": E.capcut_running()}

    def close_capcut(self):
        return {"closed": E.close_capcut()}

    def open_capcut(self):
        return {"ok": E.open_capcut()}

    def undo(self):
        ag = self._ensure_agent()
        try:
            label = ag.undo()
            ag.messages.append({"role": "user", "content": f"[The user pressed UNDO; the request '{label}' was reverted.]"})
            ag.messages.append({"role": "assistant", "content": "OK, noted the undo."})
            return {"ok": True, "label": label}
        except Exception as e:
            return {"ok": False, "error": str(e)}

    def new_chat(self):
        if self.agent:
            self.agent.messages = []
        return True

    def stop(self):
        if self.agent:
            self.agent.stop_flag = True
        return True

    def pick_folder(self):
        r = self.window.create_file_dialog(webview.FileDialog.FOLDER)
        return r[0] if r else None

    def pick_files(self):
        r = self.window.create_file_dialog(
            webview.FileDialog.OPEN, allow_multiple=True,
            file_types=("Media (*.jpg;*.jpeg;*.png;*.webp;*.heic;*.mp4;*.mov;*.avi;*.mkv;*.mp3;*.wav;*.m4a;*.aac;*.flac)",
                        "All files (*.*)"))
        return list(r) if r else []

    def send(self, text: str):
        if self.busy:
            return {"ok": False, "error": "busy"}
        if not self._key():
            return {"ok": False, "error": "nokey"}
        self.busy = True

        def work():
            try:
                ag = self._ensure_agent()
                if ag.project:
                    ag.project.reload()  # pick up changes made in CapCut since last time
                ag.send(text)
            except Exception as e:
                traceback.print_exc()
                msg = str(e)
                try:
                    import anthropic
                    if isinstance(e, anthropic.AuthenticationError):
                        msg = "מפתח ה-API לא תקין. פתח הגדרות והזן מפתח חדש."
                    elif isinstance(e, anthropic.RateLimitError):
                        msg = "הגעת למגבלת שימוש של ה-API, נסה שוב בעוד רגע."
                    elif isinstance(e, anthropic.APIConnectionError):
                        msg = "אין חיבור לאינטרנט / לשרת של Claude."
                except Exception:
                    pass
                self._emit("error", msg)
            finally:
                self.busy = False
                self._emit("done", None)

        threading.Thread(target=work, daemon=True).start()
        return {"ok": True}


def main():
    api = Api()
    window = webview.create_window(f"CapCut AI Editor {VERSION}", resource("ui.html"), js_api=api,
                                   width=1100, height=820, min_size=(720, 520), background_color="#0f1115")
    api.window = window
    webview.start(debug=bool(os.environ.get("CCAI_DEBUG")))


if __name__ == "__main__":
    main()
