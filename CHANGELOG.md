# Changelog

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
