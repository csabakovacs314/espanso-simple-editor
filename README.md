# Espanso Simple Editor 0.1.2

Edit your Espanso snippets one at a time, as text, in a PhraseExpress-style window with a dark theme. You never see YAML. On save the editor writes valid Espanso YAML back, and Espanso reloads it by itself.

Idea by Csaba Kovacs.

## Run it

- `dist\small\EspansoSimpleEditor.exe` is a prebuilt copy. It needs the .NET 10 Desktop Runtime (included with the .NET 10 SDK).
- Or build it yourself: double-click `BUILD.bat`. It runs the tests, then makes:
  - `dist\small\EspansoSimpleEditor.exe` (small, needs .NET 10)
  - `dist\portable\EspansoSimpleEditor.exe` (larger, runs on any 64-bit Windows)
  - `dist\snipconv\snipconv.exe` (the checker, see below)

## Using it

1. **Open:** the app asks Espanso where its folder is (`espanso path`), or you pick the folder yourself. Tick "Open this folder automatically next time" to skip this step later.
2. **Library (left):** every match file is a folder. Comment lines like `# --- Customer Communication ---` show up as groups. `_` files are tagged "not auto-loaded" and installed packages are read-only.
3. **Snippet (right):** click a snippet to open it in a tab. "Own window" pops it out into a separate window. Each snippet has:
   - a description, shown in Espanso's search bar
   - where it is saved (file and group)
   - the text
   - triggers, "only as a whole word", "follow the case I type", "end with a line break", and the typing method
   - a note when a trigger blocks a longer one (see below)
4. **Insert:** adds a date, time, clipboard, cursor position, a question Espanso asks first ("ask when typing"), PowerShell output, a random choice or a global variable. They appear as coloured chips. Double-click a chip to change it.
5. **Save:** Ctrl+S. Espanso picks the change up by itself. "Reload Espanso" is only needed if `auto_restart` is off.
6. **Triggers… (toolbar):** the trigger box. "All snippets…" next to "Only as a whole word" opens it too.
   - **New snippets:** start as whole word. This is on by default; untick it to make new snippets fire immediately.
   - **All snippets:** "Set all to whole word" or "Set all to none".
   - **Only where needed:** whole word just for the short triggers that block longer ones.
   - **Undo** reverses the last of these. Nothing is written until Ctrl+S.

## Whole word, and triggers that block each other

Espanso expands a trigger the moment you finish typing it. If one trigger is the start of another (`/jira` and `/jirauao`), the short one always fires first, so the long one can never be typed. The library marks both with a warning sign, and the snippet explains it.

"Only as a whole word" (Espanso `word: true`) fixes it: the trigger then fires only after a space, Enter or punctuation, and that character stays after the text. Switching it changes one line per snippet in the file (`word: true` is added or removed), nothing else.

Keys: Ctrl+S save, Ctrl+N new snippet, Ctrl+F search, Ctrl+W close tab.

## How it keeps your files safe

- **Only changed snippets are rewritten.** Comments, blank lines, the order of everything and every snippet you did not touch stay byte-for-byte the same. Switching "only as a whole word" changes just that one line, and a snippet you changed and then changed back is not written at all.
- **Every save is checked first.** The new file is read back and compared with what is on screen. If anything would come out differently, or a file you did not touch would change, nothing is saved and you are told why.
- **Changes on disk are caught.** If a file changed on disk since you opened it (another editor, a package install), the app asks before overwriting.
- **A backup comes first.** Each save copies the current files to `%LOCALAPPDATA%\EspansoSimpleEditor\backups\<date-time>\`, outside the Espanso folder so Espanso never loads them. The last 30 saves are kept.
- **Writes are atomic.** New files are written next to the originals and then swapped in, so Espanso never reads a half-written file. If a swap fails partway, press Ctrl+S again and choose to save anyway.
- **Unknown features are left alone.** Snippets that use things the form does not cover open read-only and are kept exactly as they are: images, Markdown/HTML, regex triggers, choice lists in forms, scripts, and date offsets. The same goes for whole files that use YAML anchors, compact `[ ]` lists, or are not valid UTF-8.

## Check before you trust it: snipconv

`snipconv` shows what the editor sees in your files and re-saves every snippet in memory to prove it reads back identically. It never writes to your Espanso folder.

```
dist\snipconv\snipconv.exe check "%APPDATA%\espanso"
dist\snipconv\snipconv.exe resave "%APPDATA%\espanso\match\base.yml" "%TEMP%\base-resaved.yml"
```

## If the Espanso status or folder looks wrong

The status pill's tooltip says how it decided. Every espanso command the app runs is logged in `%LOCALAPPDATA%\EspansoSimpleEditor\espanso-cli.log`.

## Look

The app uses a dark theme: Windows 11 style dark controls and a dark title bar (Windows 10 20H1 or later). Windows' own message boxes and the folder picker follow your Windows setting.

## Not in 0.1 yet

- **Formatted text:** bold, italic and links (Espanso `markdown:`/`html:`). Those snippets open read-only for now.
- **Drag and drop:** moving snippets between groups. Use "Saved in" instead.
- **Backups:** there is no in-app backup browser. The backup folder is listed above.
- **Profiles:** editing `config\` app-specific profiles.
- **Copy and paste:** copying text pastes it as plain text, so chips are not carried across.

## Project layout

- `src\EspansoEditor.Core`: reading, writing, checking and saving. No UI, fully tested.
- `src\EspansoEditor.App`: the WPF window. Colours live in `App.xaml`.
- `src\snipconv`: the command-line checker.
- `tests\EspansoEditor.Tests`: 68 tests, including your file's style (groups, trailing spaces, no final line break).
