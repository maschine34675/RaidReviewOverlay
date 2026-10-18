# Changelog

## 1.0.2

### Added

- **Web Interface Address** (F12, section Server): the address to open, for the setups
  nothing on the player's PC can detect. Type the address your browser ends up on -
  `http://192.168.1.20:7829`, or just `192.168.1.20`; a bare host gets the scheme and
  port Raid Review itself would use. It wins over everything automatic and applies on
  the next press.
- With an address entered, the addon also works on a PC **without Raid Review's client
  plugin** - common on Fika headless setups, where the headless host that records the
  raids has it. Before, the addon logged at startup that Raid Review was not installed
  and then did nothing on F5 or the button, with no hint that anything could be entered.
  Without a plugin and without an address it still stays inactive, and the log now says
  what to enter. Entered while the game runs, the address turns on F5 and the menu bar
  button at once.
- Every press logs the address it opened and where it came from, once per change - the
  one line a support question needs. User name and password in it are shown as `***`.
- Readable names and a deliberate order in the F12 menu. The `.cfg` keys are unchanged,
  so existing settings carry over.

### Fixed

- A Server IP corrected in Raid Review's own F12 menu was ignored until the game
  restarted: Raid Review computes its address once at startup, and that is what was
  read. Its live settings are read now, so the correction counts on the next press.
- Pressing F5 after changing the address could close the window instead of showing the
  new page - the press toggled a window that was still open on the old one. A new
  address now always opens.
- The first address warning of a session silenced every later one, including unrelated
  ones that came afterwards. Each distinct warning now appears once.

## 1.0.1

### Fixed

- With the window open you can look around again. The window covers the middle
  of the picture, and that is the exact point the game reads mouse movement
  from while you turn - so the movement went to the replay and the player stood
  still, with nothing anywhere to say why. The mouse now reaches the game
  whenever the game is the window in front; press F5 to bring the replay back.
  Needs Anvil-WebOverlay 1.8.8 or newer; with an older one the window behaves
  as before rather than disappearing.
- With the SPT server on another machine, the overlay opened `127.0.0.1` and
  found nothing there. Raid Review's web server is part of its *server* mod and
  runs on the server's host, but the address it hands out defaults to loopback -
  which on a remote setup is the player's own PC. A loopback address is now
  replaced by the host this client actually talks to, keeping scheme and port.
  An address deliberately pointed somewhere else is never rewritten.
- If Raid Review does not expose its address field at all, the address is
  assembled from the config entries it builds that field from, rather than
  guessing at localhost; the last-resort default follows the server's host too.
  Reflection into Raid Review also uses `FlattenHierarchy` now, so a static
  member it inherited from a base class is still found.

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
