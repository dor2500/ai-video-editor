# AI Video Editor 🎬

**[עברית](#עברית) · [English](#english)**

Chat with an AI that edits your video project for you: *"take all the photos from this folder, 4 seconds each, slow zoom, fade transitions, and put the song from Downloads under the whole thing"*.

![screenshot](docs/screenshot.png)

---

## עברית

תוכנה לווינדוס עם חלון צ'אט. כותבים מה רוצים לערוך, וה-AI (Claude או Gemini) עורך בעצמו את הפרויקט בתוכנת העריכה שלכם.

### התקנה
1. הורידו את `AIVideoEditor-Setup-<גרסה>.exe` מ-[Releases](https://github.com/dor2500/ai-video-editor/releases/latest) והריצו. לא צריך הרשאות מנהל.
2. בפתיחה הראשונה הזינו מפתח API:
   - **Claude** — מ-[console.anthropic.com](https://console.anthropic.com/settings/keys), או
   - **Gemini** — מ-[aistudio.google.com](https://aistudio.google.com/apikey).
   המפתחות נשמרים מוצפנים (Windows DPAPI) רק במחשב שלכם.

יש גם גרסה ניידת (`portable.exe`) שלא דורשת התקנה.

### תוכנות עריכה נתמכות
| תוכנה | איך זה עובד |
|---|---|
| **CapCut** | עריכה ישירה של הפרויקט. אפקטים, מעברים, אנימציות, פילטרים ופונטים נלקחים מהמטמון של CapCut במחשב שלכם |
| **Shotcut** | עריכה ישירה של קובץ ה-`.mlt` |
| **OpenShot** | עריכה ישירה של קובץ ה-`.osp` |
| **Adobe Premiere Pro** | התוכנה כותבת קובץ `.xml` (Final Cut Pro 7 XML) ← File → Import |
| **DaVinci Resolve** | קובץ `.fcpxml` (וגם `.otio`) ← File → Import → Timeline |
| **Final Cut Pro** | קובץ `.fcpxml` ← File → Import → XML |
| **Kdenlive** | קובץ `.otio` ← File → Import → OpenTimelineIO |
| **VEGAS Pro / Filmora** | קובץ `.xml` (FCP7) + `.edl` |
| **כל תוכנה אחרת** | כל הפורמטים יחד: `.fcpxml`, `.xml`, `.otio`, `.edl`, `.mlt` |

רוב התוכנות (Premiere, Resolve, Vegas…) לא מאפשרות לערוך את קובץ הפרויקט שלהן מבחוץ בבטחה. לכן התוכנה שומרת ציר זמן משלה, ובכל שינוי כותבת מחדש קובץ ייבוא שהתוכנה פותחת.

### מה אפשר לבקש
- להוסיף תמונות או סרטונים (קבצים או תיקייה שלמה), לטראק הראשי או כשכבה עליונה
- להוסיף מוזיקה לכל אורך הסרטון (עם לולאה, חיתוך, עוצמה ו-fade)
- להוסיף טקסטים וכותרות (צבע, גודל, מיקום, מסגרת, רקע, פונט)
- להזיז, לקצר, לחתוך, למחוק ולסדר מחדש
- לשנות גודל, מיקום, סיבוב, שקיפות, מהירות ועוצמה
- להשתיק טראק, להוסיף מעברים, אנימציות כניסה ויציאה, זום ופאן (Ken Burns), פילטרים ואפקטים (ב-CapCut), ולשנות יחס מסך
- ה-AI יכול גם **להסתכל על התמונות** כדי לבחור ולסדר אותן לפי התוכן

כל בקשה היא צעד אחד של ↶ ביטול. הגיבויים נשמרים ב-`%APPDATA%\AIVideoEditor\backups`.

> ⚠️ ב-CapCut, Shotcut ו-OpenShot צריך לסגור את התוכנה בזמן העריכה, אחרת היא דורסת את השינויים. האפליקציה מציעה לסגור אותה בשבילכם.

---

## English

A Windows app with a chat window. Describe the edit; the AI (Claude or Gemini) changes the project in your editor.

### Install
Download `AIVideoEditor-Setup-<version>.exe` from [Releases](https://github.com/dor2500/ai-video-editor/releases/latest). It installs per user and needs no admin rights. A portable exe is also attached. On first start, enter a **Claude** ([console.anthropic.com](https://console.anthropic.com/settings/keys)) or **Gemini** ([aistudio.google.com](https://aistudio.google.com/apikey)) API key. Keys are stored encrypted with Windows DPAPI.

### Editors
- **Direct editing:** CapCut (draft JSON), Shotcut (`.mlt`), OpenShot (`.osp`).
- **Import files**, rewritten on every change: Premiere Pro (FCP7 `.xml`), DaVinci Resolve (`.fcpxml` / `.otio`), Final Cut Pro (`.fcpxml`), Kdenlive (`.otio`), VEGAS / Filmora (`.xml` + `.edl`), and "other" (all formats at once).

CapCut effect, transition, animation, filter and font names come from CapCut's own local cache and from effects already used in your drafts. No third-party effect catalog is bundled.

### Build
Requirements: Windows, .NET SDK 10+, and Visual Studio Build Tools with the C++ workload (for the NativeAOT installer).
```powershell
./build.ps1 -Version 2.0.0     # app + self-test + dist\AIVideoEditor-Setup-2.0.0.exe
dotnet run --project tests/SelfTest -- --media "C:\photos" --audio "C:\song.mp3" --capcut "<a CapCut draft folder to COPY>"
```

### Layout
```
src/AIVideoEditor/
  Core/CapCut/     CapCut draft engine + catalog from CapCut's cache
  Core/Timeline/   editor-neutral timeline model and editing operations
  Core/Formats/    Shotcut MLT, OpenShot, FCPXML, FCP7 XML, OTIO, EDL
  AI/              tool definitions, agent loop, Claude (Anthropic SDK) and Gemini (REST) providers
  wwwroot/ui.html  chat UI (WebView2)
installer/Setup/   NativeAOT per-user installer
tests/SelfTest/    engine and format tests (no AI calls)
legacy/python-prototype/   first version (Python + pywebview, CapCut only)
```

### Notes and limits
- Only the CapCut engine has been checked against real project files (it edits copies of real drafts in the self-test). Writers for the other editors follow each format's published structure and pass the round-trip and parse tests, but have not been opened in every editor yet. Please open an issue if an import fails.
- Transitions outside CapCut: Cross Dissolve, Dip to Black, Dip to White, Wipe. Animations outside CapCut: fade in and fade out.
- Media duration and size are read through Windows' property system. A file Windows has no codec for (some MKV or HEVC files) may fail to load.

Thanks to [pyJianYingDraft / pyCapCut](https://github.com/GuanYixuan/pyJianYingDraft) for documenting the CapCut draft format. No code from them is included in the .NET app.

MIT License.
