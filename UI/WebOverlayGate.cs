using BepInEx.Configuration;
using System;
using System.Runtime.CompilerServices;

namespace RaidReviewOverlay.UI
{
    /// <summary>
    /// The only place that touches Anvil-WebOverlay types. The library is a
    /// soft dependency: without it this addon simply opens Raid Review's page
    /// in the external browser, exactly as Raid Review does on its own. That
    /// only works because every member here that uses a library type is
    /// NoInlining and called strictly behind <see cref="IsUsable"/> - a method
    /// body referencing a missing assembly, or a missing member, fails at JIT
    /// time, so those bodies must never be compiled unless the library is
    /// present AND new enough for everything they use.
    ///
    /// The full rule set lives in the library's own docs/SOFT-DEPENDENCY.md;
    /// the <c>object</c> capture below is rule 2 there.
    /// </summary>
    internal static class WebOverlayGate
    {
        public const string LibraryGuid = "com.anvil.weboverlay";

        /// <summary>
        /// Newest members used here: WebOverlayPlugin.CloseKeysFor and
        /// OverlayOptions.FreeCursorWhileShown (1.7.0); classified failures
        /// and main-thread dispatch (1.4.0). An older library gets the
        /// external browser instead - announced once, because silence here
        /// reads as a bug in this mod.
        /// </summary>
        public static readonly Version MinimumVersion = new Version(1, 7, 0);

        private static bool? loaded;
        private static Version foundVersion;
        private static object overlay;
        private static string currentUrl;
        private static volatile bool overlayFailed;
        private static volatile bool overlayWasReady;

        public static bool IsLoaded
        {
            get
            {
                if (loaded == null)
                {
                    BepInEx.PluginInfo info;
                    loaded = BepInEx.Bootstrap.Chainloader.PluginInfos.TryGetValue(LibraryGuid, out info);
                    if (loaded.Value && info != null && info.Metadata != null)
                        foundVersion = info.Metadata.Version;
                }
                return loaded.Value;
            }
        }

        /// <summary>The installed library version, once IsLoaded was consulted.</summary>
        public static Version FoundVersion
        {
            get { return IsLoaded ? foundVersion : null; }
        }

        /// <summary>Present and new enough - the only state in which the bodies below may run.</summary>
        public static bool IsUsable
        {
            get { return IsLoaded && foundVersion != null && foundVersion >= MinimumVersion; }
        }

        /// <summary>A dead overlay stays dead for the session; the browser takes over.</summary>
        public static bool HasFailed
        {
            get { return overlayFailed; }
        }

        /// <summary>
        /// Shows, hides or retargets the overlay. Returns false when the
        /// caller should fall back to the browser, and sets
        /// <paramref name="starting"/> when this press kicked off the browser
        /// start, so the caller can tell the player it is warming up.
        ///
        /// <paramref name="onFailed"/> reports a failure that surfaced later,
        /// with a log-ready reason and whether the overlay never became
        /// visible - in which case the press that hit it still deserves a
        /// page, so the caller opens the browser. With main-thread dispatch it
        /// arrives on Unity's thread, where showing a notification is safe.
        ///
        /// Window frame, close keys and cursor behaviour are applied when the
        /// overlay is first created and stick for the session.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool Toggle(string url, string title, bool showFrame, KeyboardShortcut toggleKey,
            Action<string> logWarning, Action<string, bool> onFailed, out bool starting)
        {
            starting = false;
            if (overlayFailed)
                return false;

            var handle = overlay as WebOverlay.IWebOverlay;
            if (handle == null)
            {
                handle = WebOverlay.WebOverlays.Create(title, new WebOverlay.OverlayOptions
                {
                    Frame = showFrame,
                    // Escape plus the key that opened it, so the same press
                    // closes the window while it holds the keyboard.
                    CloseKeys = WebOverlay.WebOverlayPlugin.CloseKeysFor(toggleKey),
                    // Raid Review's page is a mouse-driven map replay. In a
                    // raid the game keeps the cursor captured while the window
                    // holds the foreground; the library hands it back and
                    // returns it as soon as the game has focus again.
                    FreeCursorWhileShown = true,
                    // Ready/Failed on Unity's thread, so the caller may show an
                    // EFT notification straight from the handler.
                    DispatchOnMainThread = true,
                    // The title carries the configured hotkey, so it changes
                    // when the player rebinds it - a fixed key keeps the
                    // remembered window position across that change.
                    PersistenceKey = "RaidReviewOverlay/window",
                });
                if (handle == null)
                {
                    logWarning("overlays are unavailable (is the WebView2 runtime installed?); using the browser.");
                    overlayFailed = true;
                    return false;
                }

                // State first: Ready and Failed are latched and can fire during
                // the subscription itself, so the handlers must only capture
                // locals and never read a field assigned further down.
                overlay = handle;
                currentUrl = url;
                overlayWasReady = false;

                // object, NOT IWebOverlay: the variable is captured by the
                // Failed lambda and therefore becomes a FIELD of a
                // compiler-generated closure class. A field of an
                // Anvil-WebOverlay type makes Assembly.GetTypes() over this
                // plugin throw as soon as the library is missing - and other
                // mods (WTT-ClientCommonLib among them) scan every loaded
                // assembly that way. The cast happens in the lambda BODY,
                // which is resolved lazily.
                object created = handle;
                handle.Ready += () => overlayWasReady = true;
                handle.Failed += () =>
                {
                    bool neverShown = !overlayWasReady;
                    overlayFailed = true;
                    var dead = (WebOverlay.IWebOverlay)created;
                    string why;
                    switch (dead.Failure)
                    {
                        case WebOverlay.OverlayFailure.RuntimeMissing:
                            why = "no WebView2 runtime is installed - install it from Microsoft and the window will work";
                            break;
                        case WebOverlay.OverlayFailure.LibraryIncomplete:
                            why = "Anvil-WebOverlay is incomplete (WebView2Loader.dll missing) - reinstall the library";
                            break;
                        case WebOverlay.OverlayFailure.EnvironmentFailed:
                            why = "the shared browser could not start this session";
                            break;
                        case WebOverlay.OverlayFailure.RendererCrashed:
                            why = "the browser process died";
                            break;
                        default:
                            why = dead.Failure.ToString();
                            break;
                    }
                    why = why + " (" + dead.FailureMessage + ")";
                    dead.Dispose();
                    if (ReferenceEquals(overlay, created))
                        overlay = null;
                    if (onFailed != null)
                        onFailed(why, neverShown);
                };

                handle.Navigate(url);
                starting = true;
                return true;
            }

            // The address can move between presses - Raid Review's server IP,
            // port and TLS setting are all live config. Renavigate only then,
            // to avoid reloading the replay on every toggle.
            if (!string.Equals(currentUrl, url, StringComparison.Ordinal))
            {
                currentUrl = url;
                handle.Navigate(url);
            }

            handle.Toggle();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void Shutdown()
        {
            var handle = overlay as WebOverlay.IWebOverlay;
            overlay = null;
            if (handle != null)
                handle.Dispose();
        }
    }
}
