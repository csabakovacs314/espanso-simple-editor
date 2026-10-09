# Changelog

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
