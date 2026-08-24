# SPT-Forge page content

Upload details:

| Field | Value |
| --- | --- |
| Name | maschine-RaidReviewOverlay |
| Version | 1.0.0 |
| SPT version | 4.1.x |
| Category | Tools |
| License | MIT |
| Source | https://github.com/maschine34675/RaidReviewOverlay |
| Thumbnail | `assets/thumbnail.png` (512 px; `assets/thumbnail-144.png` if a small one is wanted) |
| Screenshot | `assets/preview.png` (the menu bar button in game) |
| Archive | `artifacts/maschine-RaidReviewOverlay-v1.0.0.zip` |
| Dependencies | Raid Review (required); Anvil-WebOverlay 1.7.0+ (optional) |


Teaser (max 100 characters):

```text
Opens Raid Review's web interface in a window over the game instead of a browser tab.
```

Description (markdown):

---

**RaidReviewOverlay** is a small addon for [Raid Review](https://sp-mod.com/mod/1479/raid-review):
its web interface opens as a window on top of EFT instead of an external browser tab.
Same page, same features, no alt-tab.

Raid Review does all the work — this only moves where its page appears.

## What it does

- **F5 opens Raid Review over the game.** That is Raid Review's own key: the addon takes
  it over, so the press you already use now shows the window instead of a browser tab.
  The window is movable, resizable and remembers where you put it; Escape or F5 closes it.
- **A RAID REVIEW button** in the bottom menu bar does the same. If Raid Review's own
  menu button is enabled, this one replaces it, so there are never two buttons that
  behave differently.
- **Shift+F5 still opens your browser**, for a second monitor or a screenshot.
- **Nothing is lost without the overlay library:** no Anvil-WebOverlay, no WebView2
  runtime, or exclusive fullscreen — every trigger falls back to the external browser,
  exactly what Raid Review does on its own.
- **Remote servers work:** the address is read from Raid Review on every press, so a
  custom server IP, port or TLS setting in its config is honoured.

## What it changes about Raid Review

Nothing on disk. Raid Review's "Open Webpage Keybind" is unbound in memory for the
session (this addon adopts the key), and its "Insert Menu Item" setting is turned off
while this addon's own button is enabled. Both are restored when the game closes, and
Raid Review's config file is never written to — uninstall this addon and Raid Review is
exactly as it was.

The redirect has to happen there: both of Raid Review's triggers end in
`Application.OpenURL`, an internal call with no IL body, which cannot be intercepted at
the call site.

## Usage

1. Install Raid Review, then drop this addon's `BepInEx` folder into your SPT
   installation. For the in-game window also install **Anvil-WebOverlay** 1.7.0 or newer
   (optional dependency; from the Forge or GitHub).
2. Press **F5** in game, or click the **RAID REVIEW** button in the bottom menu bar.

All keys and the button are configurable in the BepInEx configuration manager (F12).
