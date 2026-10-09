# Changelog

## 0.1.5 (2026-10-09)

- **New: guides for Jira.** `docs\Espanso-Guide-Short` (for colleagues) and `docs\Espanso-Guide` (full), each as Markdown for Jira Cloud and `.jira.txt` wiki markup for Jira Server, with four mockup screenshots in `docs\images`.
- **New: `tools\md2jira.py`**, JOSIP's Markdown to Jira converter, extended for pictures (`!name.png|width=900!`), an "In short" panel, tip and warning boxes, and a table of contents. BUILD.bat runs it when Python is installed.
- The screenshots are mockups drawn from `docs\mockups\mockup.html` in the app's colours, not captures of the running app.
- No change to the app itself.

## 0.1.4 (2026-10-09)

- **New: formatted snippets.** A format switch on every snippet: Plain text, Jira Cloud (Markdown) or HTML (formatted paste).
  - The editor shows real bold, italic, code, three heading sizes, bullet and numbered lists, with buttons and Ctrl+B / Ctrl+I.
  - Jira Cloud saves Markdown text (`**bold**`, `* ` bullets), the style your `/jira` templates already use; such snippets now open formatted instead of showing the symbols. Only text that reads back exactly is shown this way, so nothing changes until you edit.
  - HTML saves Espanso `html:` and is pasted as rich text. Simple hand-written HTML opens for editing; anything else (links, tables, styles) stays read-only.
  - Switching to Plain text keeps the words and drops the formatting.
- On a copy of your `base.yml`: 6 snippets open as Jira Cloud, 16 as plain text, nothing changes on disk without an edit.
- **Code review fixes (before release):**
  - HTML snippets keep literal `*`, `` ` ``, `#`, `- ` and `1. ` as text instead of turning them into formatting.
  - Bold with a space at its edge, code inside bold, and bold around italic are written so they read back the same.
  - List items keep their own marker and indent (`-` or `*`, repeated `1.`, 4-space nesting) when you edit elsewhere in the snippet.
  - Ordinary text such as `2*3*4`, `*** WARNING ***`, a lone `- John` or `# comment` no longer opens as formatted.
  - Nested lists and line breaks inside list items in HTML stay read-only; nesting and Tab are off in HTML mode; Shift+Enter makes a normal new line.
  - Very long lines no longer slow parsing down; a warning when an HTML snippet uses the cursor position.

## 0.1.3 (2026-10-09)

- **Changed: "only where needed" is the default.** New snippets no longer start as whole word. Instead, when you add a trigger that would block a longer one (or be blocked by a shorter one), whole word is switched on for the short trigger by itself, with a message. The trigger box has three choices: only where needed, always, never.
- **Fixed (code review):**
  - Saving with about 2000 snippets froze the window for about 6 seconds; now under 1 second. The save no longer runs the blocked-trigger check, which never stops a save anyway.
  - `left_word` / `right_word` snippets were reported as blocking longer triggers when they cannot.
  - Missed warnings: a snippet whose own triggers block each other (`:br`, `:brx`), and "follow the case I type" triggers (`:ty` blocks `:Tyx`).
  - Switching whole word off deleted a comment on the `word: true` line. Such a snippet is now rewritten and the comment kept.
  - An unexpected value in settings.json for the whole-word choice is treated as "only where needed".

## 0.1.2 (2026-10-09)

- **New: the trigger box** ("Triggers…" in the toolbar, or "All snippets…" next to "Only as a whole word").
  - New snippets start as whole word. This is the default and can be switched off.
  - "Set all to whole word" and "Set all to none" for every editable snippet. Read-only snippets are counted and left alone.
  - "Whole word only where needed": just the short triggers that block longer ones.
  - Undo for the last change. Nothing is written until Ctrl+S.
- **New: blocked-trigger warnings.** A trigger that is the start of another one (`/jira` and `/jirauao`) fires first, so the longer one can never be typed. Both snippets get a warning in the library and a note that says how to fix it.
- **Changed: smaller saves.** Switching "only as a whole word" adds or removes one `word: true` line and leaves the rest of the snippet as it is. A snippet changed and changed back is not rewritten.
- **Changed: dark theme.** Windows 11 style dark controls, a dark palette for the app's own parts (chips, cards, toasts, status bar) and a dark title bar.

## 0.1.1 (2026-10-09)

- **Fixed:** the status pill said "espanso command not found" while Espanso was running, even though "Reload Espanso" worked.
  - Cause: on Windows, espanso (espansod.exe) is a program without a console window. When another program runs it hidden, the text it prints can get lost, and the app relied only on that text.
  - The app now checks for the running Espanso process first. Then it reads `espanso status` by its exit code: 0 means running, 4 means not running, and 9009 means the command is not on PATH.
- **Fixed:** the same lost text could stop "The folder Espanso uses" from being found on the start screen. Detection now also tries:
  - `espanso path config`
  - `espanso path` with its output sent to a temporary file
  - the portable `.espanso` folder next to a running Espanso
- **New:** every espanso command call is logged to `%LOCALAPPDATA%\EspansoSimpleEditor\espanso-cli.log`, and the status pill's tooltip explains how the status was decided.
- **Changed:** after "Reload Espanso", the status is checked again 2 seconds later, once Espanso has had time to start.

## 0.1.0 (2026-10-09)

- First version: load screen, library tree with groups, one snippet per tab or window, chips, in-place saving with verification, backups and conflict check.
