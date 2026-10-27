# SLiveTranslator

Live, on-screen translation for games on Windows. It reads text off the screen with the OCR engine
built into Windows, translates it, and draws the translation on a transparent click-through overlay
on top of the game.

Two ways to use it:

* **Subtitle mode** – pick the strip where the game shows subtitles once. The app watches it and shows
  the translation right next to it. The last 3 lines stay on screen (older ones dimmed) and remain for
  a few seconds after the game hides them, so fast cutscene dialog can still be read. Lines are
  translated in the background, several at once, so no line is skipped while another one translates.
* **Dialog history** – a panel with the recent dialog (translation plus the original) for catching up
  on anything that went by too fast.
* **Translate screen** – press a hotkey while hovering an item, reading a tooltip, or looking at a
  menu. Every piece of text in the game window is translated in place, each label drawn over the
  original text. Labels disappear after a few seconds (configurable) or when you press the clear hotkey.

Translations are cached on disk, so item names and lines you have seen before appear instantly
without any network call.

Status messages (subtitles on/off, translating, errors) appear at the top of the game screen. Windows
hides tray notifications while a game is running, so the app does not rely on them. While subtitle
mode runs, a small green "Subtitles on" badge sits at the corner of the region and the tray icon turns
green. Only one copy of the app runs at a time.

## Requirements

* Windows 10 version 2004 or later / Windows 11 (the overlay uses `WDA_EXCLUDEFROMCAPTURE` so that
  it never captures its own labels).
* .NET 10 SDK to build (`dotnet --version` should print 10.x).
* A Windows OCR language for the language the game is in. English is normally installed already.
  To add another one: **Settings › Time & Language › Language & region › Add a language**, keep
  *Optical character recognition* ticked. Or from an elevated PowerShell:

  ```powershell
  Add-WindowsCapability -Online -Name "Language.OCR~~~ja-JP~0.0.1.0"
  ```

  (`Get-WindowsCapability -Online -Name "Language.OCR*"` lists what is available.)
* The game must run in **borderless or windowed mode**. Exclusive full-screen games cannot be captured
  and do not show overlays.

## Build and run

```bash
dotnet build
dotnet run --project src/SLiveTranslator
```

Or run the built executable directly:

```
src\SLiveTranslator\bin\Debug\net10.0-windows10.0.22621.0\SLiveTranslator.exe
```

The app lives in the system tray (blue "T" icon). The first start opens the settings window so you
can choose the language to translate into.

Run the tests with:

```bash
dotnet test
```

(One MyMemory test needs internet access.)

## Using it

| Default hotkey   | Action                                                                 |
| ---------------- | ---------------------------------------------------------------------- |
| `Ctrl+Shift+T`   | Translate the active (game) window; text under the mouse comes first   |
| `Ctrl+Shift+S`   | Subtitle mode on/off (asks for the region the first time)              |
| `Ctrl+Shift+R`   | Pick / change the subtitle region (drag a rectangle, Esc cancels)      |
| `Ctrl+Shift+H`   | Dialog history panel on/off                                            |
| `Ctrl+Shift+X`   | Clear the overlay and stop subtitle mode                               |

All hotkeys can be changed in Settings (tray icon › Settings, or double-click the icon).

Typical flow for a game with dialogue subtitles:

1. Start the game in borderless mode, start SLiveTranslator.
2. When the first subtitle appears, press `Ctrl+Shift+R` and drag a box around **only the subtitle
   lines** (for most games a wide, low strip near the bottom). Not the whole screen: that is slow,
   picks up the HUD, and misses lines.
3. Press `Ctrl+Shift+S`. "Subtitles ON" appears at the top of the screen and the translation follows
   the dialogue just below (or above) that box. Press `Ctrl+Shift+H` to re-read earlier lines.
4. Open your inventory, hover an item and press `Ctrl+Shift+T` to translate the tooltip and item names.

## Settings

| Setting                        | Notes                                                                                         |
| ------------------------------ | --------------------------------------------------------------------------------------------- |
| Game text language (OCR)       | One of the Windows OCR languages installed on this PC. Must match the game's language.         |
| Translate into                 | Google-style code: `vi`, `en`, `ja`, `ko`, `zh-CN`, …                                          |
| Translation service            | **Free**: Google Translate, with MyMemory as automatic backup (no key), or **Claude API**      |
| MyMemory email                 | Optional. Raises the MyMemory free limit from 5,000 to 50,000 characters per day.              |
| Claude API key / model         | Only for the Claude service. Leave the key empty to use `ANTHROPIC_API_KEY` or an `ant auth login` profile. |
| Subtitle region                | Only the strip where subtitles appear. A near-full-screen region burns the free quota fast.    |
| Subtitle check interval        | How often the subtitle region is checked (ms). 300 is a good default.                          |
| Screen labels auto-hide        | Seconds before the translate-screen labels disappear (0 = stay until cleared).                 |

Settings, the translation cache and a log file are stored in `%APPDATA%\SLiveTranslator`
(tray icon › *Open data folder*).

### Claude as the translator

Claude gets all the text from one screen in a single request with instructions that it is game UI
text, so it keeps item names, numbers and context consistent and quietly fixes OCR mistakes.
It is slower than Google for the first look at a screen but noticeably better for dialogue and
tooltips. The default model is `claude-opus-5-5` with low effort; server-side refusal fallbacks are
enabled so a safety-classifier decline is re-served by a fallback model within the same call.

## How it works

```
screen region ──► Windows.Media.Ocr ──► lines ──► paragraph grouping ──► translator (cached) ──► overlay
```

* `Capture/ScreenCapture.cs` – GDI capture of a rectangle or of the foreground window.
* `Ocr/WindowsOcrEngine.cs` – Windows OCR, one call at a time. Subtitles are read at 1x (fast) unless
  the text is small; translate-screen upscales 2x for small UI text.
* `Services/TextUtil.cs` – merges stacked OCR lines into paragraphs so tooltips translate as one unit.
* `Services/SubtitleTracker.cs` – turns per-frame OCR text into dialog lines: a line counts once it is
  read twice (re-checked after 100 ms). A line seen in the last 12 seconds is never reported again, however
  differently OCR misreads it (letters-only trigram comparison, tuned on real misreadings). Garbled readings
  are held back until a clean one arrives; a growing line updates instead of repeating.
* `Services/SubtitleWatcher.cs` – polls the region, skips OCR when pixels did not change, and reads each
  frame twice: as is and through a white-text mask (`Ocr/ImageFilters.cs`) that removes the moving scene
  behind white subtitles; the cleaner reading wins. Separate text blocks (subtitle, a dialog choice, a key
  hint) are translated separately. Lines are translated in the background (up to 3 at once). It warns when
  subtitles touch the edge of the region.
* `Services/ScreenTranslator.cs` – one-shot capture → OCR → translate for the translate-screen hotkey;
  text nearest the mouse is translated first and labels appear batch by batch.
* `UI/SubtitlePresenter.cs` – keeps the session's dialog lines for the subtitle box and the history panel.
* `Translation/` – `GoogleTranslator` (all strings of a screen in one request, pauses 10, 30, then 60 minutes when blocked),
  `MyMemoryTranslator` (uses MyMemory's machine translation, not its crowd-sourced matches, which are
  often unrelated movie lines), `FallbackTranslator` (tries the next service when one is blocked or over quota),
  `ClaudeTranslator` and the on-disk `CachedTranslator`.
* `UI/OverlayWindow.xaml` – transparent, topmost, click-through window stretched over the monitor
  that contains the game; excluded from screen capture.
* `Hotkeys/HotkeyManager.cs` – system-wide hotkeys via `RegisterHotKey`.

## Diagnostics

Two command-line modes help when something looks wrong:

```
SLiveTranslator.exe --selftest <folder>          # renders sample labels; writes selftest_overlay.png, selftest_screen.png, selftest.txt
SLiveTranslator.exe --translate-screen <folder>  # OCR + translate the primary monitor; writes translate_screen.txt and an annotated png
```

## Known limitations

* Windows OCR is fast and needs no downloads, but heavily stylised fonts can be misread. White subtitles
  over snow or bright sky are the hardest case: neither the plain frame nor the white-text mask reads them
  well. A misread word ("Ild" for "I'd") is left untranslated, so a few English fragments can remain.
* The free Google endpoint is unofficial. Google blocks networks it considers automated (HTTP 429 or a
  captcha page); some shared or office networks are blocked from the start. The app then pauses Google
  (10, 30, then 60 minutes) and uses MyMemory. The first translation after starting the app pays about
  half a second for the Google check.
* Typical subtitle delay with MyMemory: the line is detected 0.1–0.4 s after it appears and the
  translation shows about 1–1.6 s after it appears. Most of that is the MyMemory request itself.
* MyMemory allows 5,000 characters per day without an email, 50,000 with one. Item names and lines are
  cached, so repeats cost nothing. For long sessions or better quality use the Claude API.
* Layout is computed in physical pixels and converted with the monitor's DPI scale. Mixed-DPI
  multi-monitor setups are supported in principle but have only been checked at 100 %.
* Anti-cheat systems generally do not mind screen capture + an overlay window, but the app does not
  inject into the game in any way, so it cannot translate text that is not drawn on screen.
