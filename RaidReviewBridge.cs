using BepInEx.Bootstrap;
using BepInEx.Configuration;
using System;
using System.Reflection;
using UnityEngine;

namespace RaidReviewOverlay
{
    /// <summary>
    /// The only place that knows anything about Raid Review. Everything here
    /// goes through reflection on purpose: this addon is not built against
    /// RAID_REVIEW.dll, so it loads, logs and stays harmless on an install
    /// where Raid Review is missing, renamed or a different version - there is
    /// no assembly reference to resolve and nothing to fail at JIT time.
    ///
    /// Two things are needed from it: the address its web interface is served
    /// under (which the player can point at another machine), and its own
    /// hotkey, which this addon takes over so a single press does not open the
    /// overlay AND an external browser tab.
    /// </summary>
    internal static class RaidReviewBridge
    {
        public const string PluginGuid = "ekky.raidreview";

        /// <summary>
        /// Raid Review's own default: its server mod serves the web client on
        /// 7829 (7828 is the WebSocket port the client sends telemetry to).
        /// Only used when the plugin is present but its address field is not.
        /// </summary>
        public const string FallbackUrl = "http://127.0.0.1:7829";

        private const string UrlFieldName = "RAID_REVIEW_HTTP_Server";
        private const string HotkeyFieldName = "LaunchWebpageKey";
        private const string MenuItemFieldName = "InsertMenuItem";

        private static Type pluginType;
        private static bool pluginTypeResolved;
        private static bool urlWarned;

        private static ConfigEntry<KeyboardShortcut> takenOverEntry;
        private static KeyboardShortcut takenOverOriginal;

        private static ConfigEntry<bool> menuItemEntry;
        private static bool menuItemOriginal;

        /// <summary>Whether Raid Review is loaded in this session.</summary>
        public static bool IsInstalled
        {
            get { return Chainloader.PluginInfos.ContainsKey(PluginGuid); }
        }

        /// <summary>Whether Raid Review's hotkey is currently taken over.</summary>
        public static bool HasTakenOverHotkey
        {
            get { return takenOverEntry != null; }
        }

        /// <summary>Whether Raid Review's own menu bar button is suppressed.</summary>
        public static bool HasSuppressedMenuButton
        {
            get { return menuItemEntry != null; }
        }

        /// <summary>
        /// The address of Raid Review's web interface, read fresh on every
        /// call: the plugin builds it from its own config (server IP, HTTP
        /// port, TLS), and the player can change that at runtime through the
        /// configuration manager. Null when Raid Review is not there at all -
        /// the caller then says so instead of opening a page nothing serves.
        /// </summary>
        public static string GetWebInterfaceUrl(Action<string> logWarning)
        {
            Type type = resolvePluginType();
            if (type == null)
                return null;

            string url;
            try
            {
                FieldInfo field = type.GetField(UrlFieldName, BindingFlags.Public | BindingFlags.Static);
                url = field == null ? null : field.GetValue(null) as string;
            }
            catch (Exception ex)
            {
                warnOnce(logWarning, "could not read Raid Review's server address (" + ex.Message
                    + "); falling back to " + FallbackUrl + ".");
                return FallbackUrl;
            }

            if (string.IsNullOrEmpty(url))
            {
                warnOnce(logWarning, "Raid Review did not expose a server address (no field '" + UrlFieldName
                    + "'); falling back to " + FallbackUrl + ". A custom server IP or port set in Raid Review's"
                    + " own config will not be picked up.");
                return FallbackUrl;
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                warnOnce(logWarning, "Raid Review's server address '" + url
                    + "' is not an http(s) URL; falling back to " + FallbackUrl + ".");
                return FallbackUrl;
            }

            return url;
        }

        /// <summary>
        /// Silences Raid Review's own "open webpage" key for this session and
        /// hands back what it was bound to, so this addon can adopt it. Raid
        /// Review polls that key five times a second and answers it with
        /// Application.OpenURL - an internal call with no IL body, which
        /// Harmony cannot intercept at the call site. Taking the key out of
        /// that poll is the one place the redirect can happen.
        ///
        /// Returns false while Raid Review has not bound its config yet, so
        /// the caller can retry. The value is written WITHOUT saving: a saved
        /// neutral key would outlive this addon and leave the player without a
        /// hotkey after uninstalling it.
        /// </summary>
        public static bool TryTakeOverHotkey(out KeyboardShortcut original)
        {
            if (takenOverEntry != null)
            {
                original = takenOverOriginal;
                return true;
            }

            original = KeyboardShortcut.Empty;

            Type type = resolvePluginType();
            if (type == null)
                return false;

            ConfigEntry<KeyboardShortcut> entry;
            try
            {
                FieldInfo field = type.GetField(HotkeyFieldName, BindingFlags.Public | BindingFlags.Static);
                entry = field == null ? null : field.GetValue(null) as ConfigEntry<KeyboardShortcut>;
            }
            catch
            {
                return false;
            }

            if (entry == null)
                return false;

            takenOverEntry = entry;
            takenOverOriginal = entry.Value;
            original = takenOverOriginal;

            // Already unbound by the player: nothing to take over. The entry
            // is still remembered so the restore stays a no-op instead of
            // writing back a key they removed on purpose.
            if (original.MainKey != KeyCode.None)
                setWithoutSaving(entry, KeyboardShortcut.Empty);

            return true;
        }

        /// <summary>
        /// Gives Raid Review its key back - on shutdown, or when the player
        /// turns the takeover off. Also without saving: the config file was
        /// never touched, and it should stay that way.
        /// </summary>
        public static void RestoreHotkey()
        {
            if (takenOverEntry == null)
                return;

            ConfigEntry<KeyboardShortcut> entry = takenOverEntry;
            KeyboardShortcut original = takenOverOriginal;
            takenOverEntry = null;
            setWithoutSaving(entry, original);
        }

        /// <summary>
        /// Keeps Raid Review from building its own menu bar button, so this
        /// addon's button is not shadowed by a second one that opens the
        /// external browser. Raid Review reads this setting on every
        /// MenuTaskBar.Awake, so switching it off before the menu is built is
        /// enough; the value is again written without saving.
        ///
        /// Returns false while Raid Review has not bound its config yet.
        /// </summary>
        public static bool TrySuppressMenuButton()
        {
            if (menuItemEntry != null)
                return true;

            Type type = resolvePluginType();
            if (type == null)
                return false;

            ConfigEntry<bool> entry;
            try
            {
                FieldInfo field = type.GetField(MenuItemFieldName, BindingFlags.Public | BindingFlags.Static);
                entry = field == null ? null : field.GetValue(null) as ConfigEntry<bool>;
            }
            catch
            {
                return false;
            }

            if (entry == null)
                return false;

            menuItemEntry = entry;
            menuItemOriginal = entry.Value;

            if (menuItemOriginal)
                setWithoutSaving(entry, false);

            return true;
        }

        /// <summary>Gives Raid Review its own menu button back.</summary>
        public static void RestoreMenuButton()
        {
            if (menuItemEntry == null)
                return;

            ConfigEntry<bool> entry = menuItemEntry;
            bool original = menuItemOriginal;
            menuItemEntry = null;
            setWithoutSaving(entry, original);
        }

        /// <summary>
        /// Writes a config value without letting BepInEx flush the file. The
        /// setter triggers ConfigFile.Save whenever SaveOnConfigSet is on -
        /// which is the default - and that would persist this addon's runtime
        /// change into Raid Review's own .cfg.
        /// </summary>
        private static void setWithoutSaving<T>(ConfigEntry<T> entry, T value)
        {
            ConfigFile file = null;
            bool saveOnSet = false;
            try
            {
                file = entry.ConfigFile;
                if (file != null)
                {
                    saveOnSet = file.SaveOnConfigSet;
                    file.SaveOnConfigSet = false;
                }
                entry.Value = value;
            }
            catch
            {
                // A key that could not be changed costs the redirect, not the
                // game: this addon's own key still opens the overlay.
            }
            finally
            {
                if (file != null)
                    file.SaveOnConfigSet = saveOnSet;
            }
        }

        /// <summary>
        /// Raid Review's plugin type, taken from the instance BepInEx built,
        /// so a renamed class or namespace still resolves. The assembly scan
        /// is only a fallback for the case where the plugin is registered but
        /// not instantiated yet.
        /// </summary>
        private static Type resolvePluginType()
        {
            if (pluginTypeResolved)
                return pluginType;

            BepInEx.PluginInfo info;
            if (!Chainloader.PluginInfos.TryGetValue(PluginGuid, out info) || info == null)
                return null;                  // not loaded (yet): stay unresolved and retry later

            if (info.Instance != null)
            {
                pluginType = info.Instance.GetType();
                pluginTypeResolved = true;
                return pluginType;
            }

            try
            {
                Assembly assembly = findLoadedAssembly(info.Location);
                // GetType(name, false) never throws, unlike GetTypes() over an
                // assembly whose references cannot all be resolved.
                pluginType = assembly == null ? null : assembly.GetType("RAID_REVIEW.RAID_REVIEW", false);
            }
            catch
            {
                pluginType = null;
            }

            pluginTypeResolved = pluginType != null;
            return pluginType;
        }

        private static Assembly findLoadedAssembly(string location)
        {
            string name;
            try
            {
                name = System.IO.Path.GetFileNameWithoutExtension(location);
            }
            catch
            {
                return null;
            }

            if (string.IsNullOrEmpty(name))
                return null;

            foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (string.Equals(candidate.GetName().Name, name, StringComparison.OrdinalIgnoreCase))
                    return candidate;
            }
            return null;
        }

        private static void warnOnce(Action<string> logWarning, string message)
        {
            if (urlWarned)
                return;
            urlWarned = true;
            if (logWarning != null)
                logWarning(message);
        }
    }
}
