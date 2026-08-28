using BepInEx.Bootstrap;
using BepInEx.Configuration;
using System;
using System.Net;
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
        /// Only used when the plugin is present but its address is not.
        /// </summary>
        public const string DefaultWebClientPort = "7829";

        private const string UrlFieldName = "RAID_REVIEW_HTTP_Server";
        private const string HotkeyFieldName = "LaunchWebpageKey";
        private const string MenuItemFieldName = "InsertMenuItem";

        /// <summary>
        /// FlattenHierarchy matters: without it, reflection does not see a
        /// static member a plugin class inherited from a base class, and every
        /// lookup here would quietly fail on a build that refactored one out.
        /// </summary>
        private const BindingFlags StaticFieldFlags =
            BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy;

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

            string url = readAddressField(type, logWarning);

            // Raid Review builds that field in its own Awake. If it is missing
            // or empty - an older build, a variant that renamed it - the same
            // address can be assembled from the config entries it builds it
            // from, which is a far better answer than a guess at localhost.
            if (string.IsNullOrEmpty(url))
            {
                url = buildAddressFromConfig(type);
                if (!string.IsNullOrEmpty(url))
                    warnOnce(logWarning, "Raid Review did not expose its server address (no field '"
                        + UrlFieldName + "'); assembled " + url + " from its config instead.");
            }

            if (string.IsNullOrEmpty(url))
            {
                url = defaultAddress();
                warnOnce(logWarning, "Raid Review exposed neither a server address nor the config to build"
                    + " one; falling back to " + url + ".");
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                string fallback = defaultAddress();
                warnOnce(logWarning, "Raid Review's server address '" + url
                    + "' is not an http(s) URL; falling back to " + fallback + ".");
                return fallback;
            }

            return correctLoopback(parsed, logWarning);
        }

        /// <summary>
        /// Replaces a loopback host with the server this client actually talks
        /// to. Raid Review's web server is part of its SERVER mod, so it runs
        /// wherever the SPT server runs. When those are different machines, an
        /// address of 127.0.0.1 points at the player's own PC, where nothing
        /// is listening - which is what a remote-server player sees as "the
        /// overlay always opens localhost". Raid Review's own hotkey has the
        /// same problem, since both read the same address.
        ///
        /// This only ever rewrites loopback to a remote host, never the other
        /// way round: an address the player deliberately pointed somewhere
        /// else is left exactly as configured.
        /// </summary>
        private static string correctLoopback(Uri address, Action<string> logWarning)
        {
            if (!isLoopback(address.Host))
                return address.ToString();

            string backend = backendHost();
            if (string.IsNullOrEmpty(backend) || isLoopback(backend))
                return address.ToString();

            var builder = new UriBuilder(address);
            builder.Host = backend;
            // UriBuilder writes the default port explicitly; keep the address
            // as short as it came in.
            if (address.IsDefaultPort)
                builder.Port = -1;

            string corrected = builder.Uri.ToString();
            warnOnce(logWarning, "Raid Review points at " + address.Host + ", but this client talks to "
                + backend + " - its web server runs on the server machine, so the overlay opens "
                + corrected + ". Set 'Server IP' in Raid Review's own config to silence this.");
            return corrected;
        }

        private static bool isLoopback(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;
            IPAddress parsed;
            return IPAddress.TryParse(host, out parsed) && IPAddress.IsLoopback(parsed);
        }

        /// <summary>The host this client reaches its SPT server under.</summary>
        private static string backendHost()
        {
            try
            {
                Uri backend;
                if (Uri.TryCreate(SPT.Common.Http.RequestHandler.Host, UriKind.Absolute, out backend)
                    && !string.IsNullOrWhiteSpace(backend.Host))
                    return backend.Host;
            }
            catch
            {
                // Before the client has a backend there is nothing to learn
                // from it; the configured address stands.
            }
            return null;
        }

        /// <summary>
        /// Raid Review's default web client port, on whichever machine the
        /// server runs - not blindly on this one.
        /// </summary>
        private static string defaultAddress()
        {
            string host = backendHost();
            return "http://" + (string.IsNullOrEmpty(host) ? IPAddress.Loopback.ToString() : host)
                + ":" + DefaultWebClientPort;
        }

        private static string readAddressField(Type type, Action<string> logWarning)
        {
            try
            {
                FieldInfo field = type.GetField(UrlFieldName, StaticFieldFlags);
                return field == null ? null : field.GetValue(null) as string;
            }
            catch (Exception ex)
            {
                warnOnce(logWarning, "could not read Raid Review's server address (" + ex.Message + ").");
                return null;
            }
        }

        /// <summary>
        /// The address as Raid Review itself assembles it, from the three
        /// config entries it uses: scheme from TLS, host, and port when set.
        /// </summary>
        private static string buildAddressFromConfig(Type type)
        {
            ConfigEntry<string> address = staticConfigEntry<string>(type, "ServerAddress");
            if (address == null || string.IsNullOrEmpty(address.Value))
                return null;

            ConfigEntry<string> port = staticConfigEntry<string>(type, "ServerHttpPort");
            ConfigEntry<bool> tls = staticConfigEntry<bool>(type, "ServerTLS");

            string scheme = tls != null && tls.Value ? "https://" : "http://";
            string portPart = port != null && !string.IsNullOrEmpty(port.Value) ? ":" + port.Value : string.Empty;
            return scheme + address.Value + portPart;
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

            ConfigEntry<KeyboardShortcut> entry = staticConfigEntry<KeyboardShortcut>(type, HotkeyFieldName);
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

            ConfigEntry<bool> entry = staticConfigEntry<bool>(type, MenuItemFieldName);
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
        /// One of Raid Review's public static config entries, or null when
        /// this build does not have it. Every lookup here goes through this,
        /// so a missing member is a null to handle rather than an exception
        /// out of a hotkey press.
        /// </summary>
        private static ConfigEntry<T> staticConfigEntry<T>(Type type, string fieldName)
        {
            try
            {
                FieldInfo field = type.GetField(fieldName, StaticFieldFlags);
                return field == null ? null : field.GetValue(null) as ConfigEntry<T>;
            }
            catch
            {
                return null;
            }
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
