using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT.Communications;
using RaidReviewOverlay.UI;
using System;
using System.Collections;
using UnityEngine;

namespace RaidReviewOverlay
{
    /// <summary>
    /// Shows Raid Review's web interface in a window over the game instead of
    /// in an external browser tab.
    ///
    /// Raid Review already serves a complete web client (its server mod runs
    /// its own Kestrel on port 7829); all this addon does is put that page in
    /// an Anvil-WebOverlay window and take over the two places Raid Review
    /// opens it from - its hotkey and, when enabled, its menu bar button.
    /// Both go through Application.OpenURL, which is an internal call with no
    /// IL body and therefore cannot be intercepted with Harmony, so the
    /// redirect happens at the triggers instead: the key is taken out of Raid
    /// Review's poll (see <see cref="RaidReviewBridge"/>) and the button gets
    /// this addon's own listener.
    ///
    /// Without Anvil-WebOverlay - or without a WebView2 runtime - everything
    /// falls back to the external browser, which is exactly what Raid Review
    /// does on its own, so the addon is never worse than not having it.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    // Soft, not hard, so a missing Raid Review produces one clear log line
    // instead of a BepInEx dependency error. Declaring it still settles load
    // order: Raid Review binds its config in Awake, and this addon reads it.
    [BepInDependency(RaidReviewBridge.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    // Same for the overlay library, whose own Update pumps main-thread events;
    // with the dependency declared it runs before ours in the frame.
    [BepInDependency(WebOverlayGate.LibraryGuid, BepInDependency.DependencyFlags.SoftDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.maschine.RaidReviewOverlay";
        public const string PluginName = "maschine-RaidReviewOverlay";
        public const string PluginVersion = "1.0.1";

        internal static Plugin Instance;
        internal static ManualLogSource Log;

        /// <summary>How long to keep retrying while Raid Review binds its config.</summary>
        private const float TakeoverTimeout = 30f;
        private const float TakeoverRetryInterval = 0.5f;

        private ConfigEntry<KeyboardShortcut> openOverlayKey;
        private ConfigEntry<KeyboardShortcut> openBrowserKey;
        private ConfigEntry<bool> showTaskBarButton;
        private ConfigEntry<bool> takeOverHotkey;
        private ConfigEntry<bool> useOverlay;
        private ConfigEntry<bool> showOverlayFrame;

        private bool active;
        private bool takeoverPending;
        private float takeoverDeadline;
        private float nextTakeoverAttempt;
        private bool fullscreenHintLogged;
        private bool libraryHintLogged;
        private bool versionHintLogged;

        internal bool ShowTaskBarButton
        {
            get { return this.showTaskBarButton.Value; }
        }

        private void Awake()
        {
            Instance = this;
            Log = this.Logger;

            this.openOverlayKey = this.Config.Bind(
                "Main", "Open overlay", new KeyboardShortcut(KeyCode.F5),
                "Opens or closes Raid Review over the game. Defaults to Raid Review's own key, which this"
                + " addon takes over; if you rebound that key and left this one alone, this one follows it.");
            this.openBrowserKey = this.Config.Bind(
                "Main", "Open in browser", new KeyboardShortcut(KeyCode.F5, KeyCode.LeftShift),
                "Forces Raid Review's page into your external browser, ignoring the overlay.");
            this.showTaskBarButton = this.Config.Bind(
                "Main", "Menu bar button", true,
                "Adds a RAID REVIEW button to the bottom menu bar. While this is on, Raid Review's own"
                + " menu button (its 'Insert Menu Item' setting) is suppressed for the session, so there"
                + " is one button rather than two that look alike and behave differently.");

            // No apostrophe in the key name: BepInEx rejects = \n \t \ " ' [ ]
            // in section and key names, and the exception aborts Awake - the
            // plugin does not load at all.
            this.takeOverHotkey = this.Config.Bind(
                "Integration", "Take over the Raid Review hotkey", true,
                "Stops Raid Review from opening its page in an external browser, so its key opens the"
                + " overlay instead. The change is made in memory only - Raid Review's own config file is"
                + " never written to, and the key is handed back when the game closes. Turn this off to"
                + " keep both: Raid Review's key opens the browser, this addon's key the overlay.");

            this.useOverlay = this.Config.Bind(
                "Overlay", "Use overlay", true,
                "Show the page in a window over the game (needs the Anvil-WebOverlay library). Off means"
                + " every trigger opens the external browser.");
            this.showOverlayFrame = this.Config.Bind(
                "Overlay", "Window frame", true,
                "Give the overlay window a title bar to drag and resize it. Frameless is cleaner but can"
                + " only be moved from inside the page. Read when the window is first created.");

            if (!RaidReviewBridge.IsInstalled)
            {
                this.Logger.LogWarning("Raid Review (" + RaidReviewBridge.PluginGuid + ") is not installed;"
                    + " there is no web interface to show. This addon stays inactive.");
                return;
            }

            this.active = true;
            this.beginTakeover();

            // Turning either setting on or off should take effect immediately -
            // they sit right next to the key in the configuration manager, and
            // a restart to try them out is a poor answer.
            this.takeOverHotkey.SettingChanged += (sender, args) =>
            {
                if (this.takeOverHotkey.Value)
                    this.beginTakeover();
                else if (RaidReviewBridge.HasTakenOverHotkey)
                {
                    RaidReviewBridge.RestoreHotkey();
                    this.Logger.LogInfo("Raid Review's own hotkey is active again.");
                }
            };
            this.showTaskBarButton.SettingChanged += (sender, args) =>
            {
                if (this.showTaskBarButton.Value)
                    this.beginTakeover();
                else if (RaidReviewBridge.HasSuppressedMenuButton)
                {
                    RaidReviewBridge.RestoreMenuButton();
                    this.Logger.LogInfo("Raid Review's own menu button setting is restored; both buttons"
                        + " reappear as configured the next time the menu bar is built.");
                }
            };

            try
            {
                new Patches.TaskBarButtonPatch().Enable();
            }
            catch (Exception ex)
            {
                // A cosmetic button must never cost the hotkey.
                this.Logger.LogWarning("the menu bar button could not be installed: " + ex.Message);
            }

            this.Logger.LogInfo(WebOverlayGate.IsUsable
                ? "ready; Raid Review opens over the game."
                : "ready; install Anvil-WebOverlay " + MinimumLibraryText()
                    + " to open Raid Review over the game - until then it opens in the browser.");
        }

        private void Update()
        {
            if (!this.active)
                return;

            if (this.takeoverPending)
                this.continueTakeover();

            // The browser key first: it carries a modifier on top of the same
            // main key, so testing the overlay key first would answer both.
            if (isPressed(this.openBrowserKey.Value))
                this.OpenWebInterface(true);
            else if (isPressed(this.openOverlayKey.Value))
                this.OpenWebInterface(false);
        }

        private void OnDestroy()
        {
            if (RaidReviewBridge.HasTakenOverHotkey)
                RaidReviewBridge.RestoreHotkey();
            if (RaidReviewBridge.HasSuppressedMenuButton)
                RaidReviewBridge.RestoreMenuButton();
            if (WebOverlayGate.IsUsable)
                WebOverlayGate.Shutdown();
        }

        /// <summary>Opens the interface from the menu bar button.</summary>
        internal void OpenWebInterfaceFromTaskBar()
        {
            this.OpenWebInterface(false);
        }

        /// <summary>
        /// Toggles the overlay, or opens the browser when the overlay is not
        /// the right answer (turned off, library missing or too old, exclusive
        /// fullscreen, or a browser that failed earlier this session).
        /// </summary>
        internal void OpenWebInterface(bool forceBrowser)
        {
            string url = RaidReviewBridge.GetWebInterfaceUrl(this.Logger.LogWarning);
            if (url == null)
            {
                // Two different failures with one symptom - say which one it
                // is, because only one of them is the player's to fix.
                bool installed = RaidReviewBridge.IsInstalled;
                this.Logger.LogWarning(installed
                    ? "Raid Review is loaded but its plugin class could not be reached; nothing to open."
                    : "Raid Review is not installed; nothing to open.");
                this.notify(installed ? "Raid Review could not be reached." : "Raid Review is not installed.");
                return;
            }

            if (!forceBrowser && this.tryToggleOverlay(url))
            {
                this.Logger.LogDebug("toggled the in-game overlay for " + url + ".");
                return;
            }

            this.Logger.LogDebug("opening " + url + " in the default browser.");
            Application.OpenURL(url);
        }

        private bool tryToggleOverlay(string url)
        {
            if (!this.useOverlay.Value)
                return false;

            // A window over an exclusive-fullscreen game minimises it.
            if (Screen.fullScreenMode == FullScreenMode.ExclusiveFullScreen)
            {
                if (!this.fullscreenHintLogged)
                {
                    this.fullscreenHintLogged = true;
                    this.Logger.LogInfo("exclusive fullscreen cannot host the overlay (borderless can);"
                        + " using the browser.");
                }
                return false;
            }

            if (!WebOverlayGate.IsUsable)
            {
                this.logLibraryHint();
                return false;
            }

            if (WebOverlayGate.HasFailed)
                return false;

            bool starting;
            bool toggled = WebOverlayGate.Toggle(
                url,
                this.overlayTitle(),
                this.showOverlayFrame.Value,
                this.openOverlayKey.Value,
                this.Logger.LogWarning,
                this.onOverlayFailed,
                out starting);

            // The first press starts the browser, which takes a few seconds on
            // a cold machine; without a hint the player presses again and
            // toggles the window shut before it ever appeared.
            if (starting)
                this.notify("Raid Review is starting - one moment...");

            return toggled;
        }

        /// <summary>
        /// A failure that surfaced after creation. Arrives on Unity's thread
        /// (the gate asks for main-thread dispatch), so a notification is safe
        /// here. When the window never became visible, the press that hit the
        /// failure still deserves its page.
        /// </summary>
        private void onOverlayFailed(string why, bool neverShown)
        {
            this.Logger.LogWarning("the overlay failed: " + why + ". The browser takes over from now on.");
            if (!neverShown)
                return;

            this.notify("the overlay failed; opening Raid Review in your browser.");
            string url = RaidReviewBridge.GetWebInterfaceUrl(this.Logger.LogWarning);
            if (url != null)
                Application.OpenURL(url);
        }

        private string overlayTitle()
        {
            KeyCode key = this.openOverlayKey.Value.MainKey;
            return key == KeyCode.None
                ? "Raid Review - press Escape to close"
                : "Raid Review - press " + key + " or Escape to close";
        }

        private void beginTakeover()
        {
            bool hotkeyWanted = this.takeOverHotkey.Value && !RaidReviewBridge.HasTakenOverHotkey;
            bool menuWanted = this.showTaskBarButton.Value && !RaidReviewBridge.HasSuppressedMenuButton;
            if (!hotkeyWanted && !menuWanted)
                return;

            this.takeoverPending = true;
            this.takeoverDeadline = Time.unscaledTime + TakeoverTimeout;
            this.nextTakeoverAttempt = 0f;
            this.continueTakeover();
        }

        /// <summary>
        /// Raid Review binds its config in its own Awake, which the declared
        /// dependency puts before ours - but load order is a promise about
        /// plugins, not about when a field is filled, so this retries for a
        /// while instead of assuming. Both takeovers read fields bound in that
        /// same Awake, so they succeed or wait together.
        /// </summary>
        private void continueTakeover()
        {
            if (Time.unscaledTime < this.nextTakeoverAttempt)
                return;
            this.nextTakeoverAttempt = Time.unscaledTime + TakeoverRetryInterval;

            bool hotkeyDone = !this.takeOverHotkey.Value;
            KeyboardShortcut original = KeyboardShortcut.Empty;
            if (!hotkeyDone)
                hotkeyDone = RaidReviewBridge.TryTakeOverHotkey(out original);

            bool menuDone = !this.showTaskBarButton.Value || RaidReviewBridge.TrySuppressMenuButton();

            if (!hotkeyDone || !menuDone)
            {
                if (Time.unscaledTime <= this.takeoverDeadline)
                    return;
                this.takeoverPending = false;
                this.Logger.LogWarning("Raid Review's own settings could not be read, so its key still opens"
                    + " the external browser. Unbind 'Open Webpage Keybind' in Raid Review's settings to stop"
                    + " the browser tab, or turn the takeover off here to silence this.");
                return;
            }

            this.takeoverPending = false;

            if (!this.takeOverHotkey.Value)
                return;

            if (original.MainKey == KeyCode.None)
            {
                this.Logger.LogInfo("Raid Review's own hotkey is unbound; " + this.openOverlayKey.Value
                    + " opens the overlay.");
                return;
            }

            this.adoptKey(original);
            this.Logger.LogInfo("took over Raid Review's " + original + "; it now opens the overlay instead"
                + " of an external browser tab.");
        }

        /// <summary>
        /// Follows the key the player is used to. Only when this addon's own
        /// key is still untouched - a key they set here is their decision and
        /// outranks Raid Review's.
        /// </summary>
        private void adoptKey(KeyboardShortcut original)
        {
            if (!this.openOverlayKey.Value.Equals(this.openOverlayKey.DefaultValue))
                return;
            if (original.Equals(this.openOverlayKey.Value))
                return;

            this.openOverlayKey.Value = original;
            this.Logger.LogInfo("adopted Raid Review's key " + original + " for the overlay.");

            // Keep the "force the browser" key next to it: same key, plus
            // shift - unless that is what was adopted, which would leave both
            // on the same combination.
            if (!this.openBrowserKey.Value.Equals(this.openBrowserKey.DefaultValue))
                return;
            var browserKey = new KeyboardShortcut(original.MainKey, KeyCode.LeftShift);
            if (!browserKey.Equals(original))
                this.openBrowserKey.Value = browserKey;
        }

        private void logLibraryHint()
        {
            if (!WebOverlayGate.IsLoaded)
            {
                if (this.libraryHintLogged)
                    return;
                this.libraryHintLogged = true;
                this.Logger.LogInfo("install the Anvil-WebOverlay library " + MinimumLibraryText()
                    + " for the in-game window; using the browser.");
                return;
            }

            if (this.versionHintLogged)
                return;
            this.versionHintLogged = true;
            this.Logger.LogInfo("Anvil-WebOverlay " + WebOverlayGate.FoundVersion + " is installed; this needs "
                + MinimumLibraryText() + " - using the browser instead.");
        }

        private static string MinimumLibraryText()
        {
            return WebOverlayGate.MinimumVersion + " or newer";
        }

        private void notify(string message)
        {
            try
            {
                NotificationManager.DisplayMessageNotification(message);
            }
            catch
            {
                // Notifications are unavailable outside the menu and in early
                // startup; the log line already carries the information.
            }
        }

        /// <summary>
        /// BepInEx's KeyboardShortcut.IsDown blocks while ANY unrelated key is
        /// held, so a toggle would be swallowed whenever the player is walking.
        /// Honor configured modifiers, ignore everything else.
        /// </summary>
        private static bool isPressed(KeyboardShortcut shortcut)
        {
            if (shortcut.MainKey == KeyCode.None || !Input.GetKeyDown(shortcut.MainKey))
                return false;
            foreach (KeyCode modifier in shortcut.Modifiers)
                if (!Input.GetKey(modifier))
                    return false;
            return true;
        }

        /// <summary>
        /// Runs an action a moment later on Unity's thread. The menu bar
        /// button needs this: what a freshly cloned button looks like is only
        /// settled a frame or two after the bar built it.
        /// </summary>
        internal void RunDelayed(float seconds, Action action)
        {
            this.StartCoroutine(runDelayedRoutine(seconds, action));
        }

        private static IEnumerator runDelayedRoutine(float seconds, Action action)
        {
            // Realtime: the menu bar is built while the game is loading, where
            // a scaled wait can sit still for as long as the load takes.
            yield return new WaitForSecondsRealtime(seconds);
            try
            {
                action();
            }
            catch (Exception ex)
            {
                if (Log != null)
                    Log.LogWarning("a delayed menu bar step failed: " + ex.Message);
            }
        }
    }
}
