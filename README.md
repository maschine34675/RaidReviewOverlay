# maschine-RaidReviewOverlay

Opens [Raid Review](https://sp-mod.com/mod/1479/raid-review)'s web interface **in a
window over the game** instead of an external browser tab — same page, same features,
without alt-tabbing out of EFT.

This is a small addon, not a fork: Raid Review does all the work (recording, the
server, the web client). All this does is put its page in an
[Anvil-WebOverlay](https://github.com/maschine34675/WebOverlay) window and redirect
the two places Raid Review opens it from.

## Requirements

- **Raid Review** (`ekky.raidreview`) — the addon stays inactive without it and says so
  once in the log.
- **Anvil-WebOverlay 1.7.0 or newer** — optional. Without it (or without a WebView2
  runtime) everything falls back to the external browser, exactly like Raid Review on
  its own, so the addon is never worse than not having it.

## Usage

- **F5** (configurable): opens or closes Raid Review over the game. This is Raid
  Review's own key — the addon takes it over, so the press you are used to now shows
  the window instead of a browser tab.
- **Shift+F5**: forces the page into your external browser, ignoring the window.
- **RAID REVIEW** in the bottom menu bar: same as the hotkey.
- **Escape** or the same hotkey closes the window while it has focus.

The window is movable and resizable and remembers its position and size. While it has
focus it takes mouse and keyboard itself; one click into the game gives both back.

## Settings

BepInEx configuration manager (F12), section `RaidReviewOverlay`:

| Setting | Default | What it does |
| --- | --- | --- |
| `Main / Open overlay` | F5 | Shows or hides Raid Review over the game. |
| `Main / Open in browser` | Shift+F5 | Forces the external browser. |
| `Main / Menu bar button` | on | Adds the RAID REVIEW button to the bottom menu bar, and suppresses Raid Review's own one for the session so there is only one. |
| `Integration / Take over the Raid Review hotkey` | on | Stops Raid Review from opening a browser tab on its own key. Off means both work: its key opens the browser, this addon's key the window. |
| `Overlay / Use overlay` | on | Off sends every trigger to the external browser. |
| `Overlay / Window frame` | on | Title bar to drag and resize. Frameless is cleaner but can only be moved from inside the page. Read when the window is first created. |

## What it changes about Raid Review

Both of Raid Review's triggers end in `Application.OpenURL`, which is an internal call
with no IL body — Harmony cannot intercept it at the call site. So the redirect happens
at the triggers instead, and only in memory:

- Its `Open Webpage Keybind` is set to unbound for the session, and this addon adopts
  the key. Raid Review polls that key five times a second and answers every poll with a
  browser tab; taking the key out of the poll is the one place to stop it.
- Its `Insert Menu Item` setting is turned off for the session while this addon's own
  menu button is enabled, so there is one RAID REVIEW button, not two that behave
  differently.

**Raid Review's own config file is never written to.** Both values are restored when the
game closes, and BepInEx's save-on-set is suppressed while they are changed, so
uninstalling this addon leaves Raid Review exactly as it was. Turning either setting off
in the configuration manager restores the corresponding value immediately.

The address comes from Raid Review itself (`RAID_REVIEW_HTTP_Server`), read fresh on
every press, so a custom server IP, port or TLS setting in its config is honoured — a
server on another machine included. If that field cannot be read, the addon falls back
to Raid Review's default `http://127.0.0.1:7829` and says so once.

Everything about Raid Review is reached through reflection: this plugin is not built
against `RAID_REVIEW.dll`, so a missing, renamed or newer Raid Review costs a log line,
not a crash.

## When the window is not used

The external browser takes over, with a line in the log, when:

- `Overlay / Use overlay` is off, or Shift+F5 was pressed;
- Anvil-WebOverlay is missing or older than 1.7.0;
- the game runs in **exclusive fullscreen** (a window over it would minimise the game —
  borderless works);
- no WebView2 runtime is installed, or the browser process failed. A failure during the
  first press still opens the browser for that press, so no press is lost.

## Building

```
dotnet build RaidReviewOverlay.csproj -c Release
```

Assembly references are relative to the SPT installation two directories up
(`..\..\EscapeFromTarkov_Data\Managed`, `..\..\BepInEx`), so the repository is expected
to live in `<SPT>\Development\RaidReviewOverlay`. The build deploys to
`<SPT>\BepInEx\plugins` as a single DLL; `-p:DeployToSpt=false` skips that.

`scripts\Test-SoftDependency.ps1` verifies the Anvil-WebOverlay soft dependency (rule 5
of the library's `docs/SOFT-DEPENDENCY.md`): no field, base type, interface, generic
argument or method signature may name a library type, and only the gate class may use
them in method bodies.

`scripts\Test-ConfigKeys.ps1` runs every `Config.Bind` section and key name through
BepInEx's own `ConfigDefinition` constructor. BepInEx rejects `= \n \t \ " ' [ ]` there
and throws out of `Awake`, so one apostrophe in a key name keeps the whole plugin from
loading — invisible to the compiler, and only visible on a real game start.

`scripts\New-ReleasePackage.ps1` builds, runs both checks and writes the release archive
to `artifacts\`.

`scripts\Test-RaidReviewFields.ps1` checks a `RAID_REVIEW.dll` for the three static
members this addon reflects on — worth running against a new Raid Review release before
assuming this addon still redirects it.

## The button icon

A hexagon with three rising bars, matching the shape and muted gold of the glyphs the
bottom bar already has. `tools\build-icon.py` draws it to `assets\task-bar-icon.png`
(needs Pillow) and the build embeds that PNG in the assembly.

Three details in there were paid for in the game rather than in a preview, and the
script keeps them:

- **Hexagon, not circle.** A ring reads as visibly ragged at the ~24 px the bar gives an
  icon, because a curve that size is all antialiasing.
- **The colour is baked into the PNG.** The button's animator writes `Image.color` every
  frame, so tinting a white glyph from code loses and the icon shows up plain white.
- **The sprite is scaled to the one it replaces.** An `Image` reports its preferred size
  as `sprite.rect.width / sprite.pixelsPerUnit`, so a sprite at the default 100
  pixels-per-unit asks the layout for a much larger glyph — which grows the button and
  leaves the icon floating in the space that opened up.

The icon is this project's own work — no third-party artwork ships with it.

## License

MIT — see [LICENSE](LICENSE).
