# Changelog

## 1.0.1

### Fixed

- With the window open you can look around again. The window covers the middle
  of the picture, and that is the exact point the game reads mouse movement
  from while you turn - so the movement went to the replay and the player stood
  still, with nothing anywhere to say why. The mouse now reaches the game
  whenever the game is the window in front; press F5 to bring the replay back.
  Needs Anvil-WebOverlay 1.8.8 or newer; with an older one the window behaves
  as before rather than disappearing.

## 1.0.0

First release.

- Raid Review's web interface opens in an Anvil-WebOverlay window over the game,
  movable and resizable, with position and size remembered.
- Takes over Raid Review's own hotkey (default F5) so the press players already know
  shows the window instead of an external browser tab. The key is adopted when it was
  rebound; Raid Review's config file is never written to and both changed values are
  restored on shutdown.
- RAID REVIEW button in the bottom menu bar, replacing Raid Review's own button when
  that one is enabled. Its glyph - a hexagon with three rising bars, matching the shape
  and muted gold of the icons the bar already has - is drawn by `tools/build-icon.py`
  and embedded in the assembly.
- Shift+F5 forces the external browser, which is also the automatic fallback when
  Anvil-WebOverlay is missing or older than 1.7.0, when no WebView2 runtime is
  installed, when the browser process fails, and in exclusive fullscreen.
- The server address is read from Raid Review on every press, so a custom IP, port or
  TLS setting is honoured.
