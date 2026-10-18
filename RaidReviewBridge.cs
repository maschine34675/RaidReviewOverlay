using BepInEx.Bootstrap;
using BepInEx.Configuration;
using System;
using System.Collections.Generic;
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
        /// Used when neither Raid Review's settings nor its address can be
        /// read, and as the port added to a typed bare host when Raid Review's
        /// client plugin - or its port setting - is not available.
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
        private static readonly HashSet<string> warned = new HashSet<string>(StringComparer.Ordinal);

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
        /// The address of Raid Review's web interface, resolved fresh on every
        /// call, and in <paramref name="source"/> a few words on where it came
        /// from - the line a player pastes when it is wrong.
        ///
        /// In order:
        /// 1. the address the player typed into this addon's own setting -
        ///    always wins, and is used as written. It is the one answer for
        ///    setups nothing can detect: Raid Review's client plugin installed
        ///    only on a Fika headless host, a server reached through a tunnel,
        ///    a public IP the player's own router cannot loop back to;
        /// 2. Raid Review's live settings (its F12 Server IP, port and TLS), with
        ///    a loopback host replaced by the SPT server's when that is remote;
        /// 3. the address Raid Review computed at game start, then its default
        ///    port on the SPT server's host.
        ///
        /// Null when there is neither a typed address nor Raid Review - the
        /// caller then says so instead of opening a page nothing serves.
        /// </summary>
        public static string GetWebInterfaceUrl(string manualAddress, Action<string> logWarning, out string source)
        {
            source = null;

            if (!string.IsNullOrWhiteSpace(manualAddress))
            {
                string scheme, port;
                DefaultSchemeAndPort(out scheme, out port);
                string reason;
                string manual = AddressRules.NormalizeManual(manualAddress, scheme, port, out reason);
                if (manual != null)
                {
                    source = "set in this addon's 'Web Interface Address'";
                    return manual;
                }

                warnOnce(logWarning, "'Web Interface Address' is set to '" + AddressRules.ForLog(manualAddress.Trim())
                    + "', which cannot be used: " + reason + ". "
                    + (IsInstalled
                        ? "Using the address Raid Review provides instead."
                        : "Raid Review is not installed here either, so there is nothing to open."));
            }

            Type type = resolvePluginType();
            if (type == null)
                return null;

            // Raid Review's live settings first. It computes its own address
            // field ONCE, in Awake, and never again - so after the player
            // corrects its Server IP in F12, that field still holds the old
            // address until the game restarts, and Raid Review's own hotkey
            // keeps opening it. The config entries it builds the field from
            // are live; reading them makes a correction count on the next
            // press. The field stays as the fallback for a build that renamed
            // the entries.
            string url = buildAddressFromConfig(type);
            string origin = "from Raid Review's Server IP setting";

            if (string.IsNullOrEmpty(url))
            {
                url = readAddressField(type, logWarning);
                origin = "from the address Raid Review computed at game start";
                if (!string.IsNullOrEmpty(url))
                    warnOnce(logWarning, "Raid Review's Server IP setting could not be read; using the address"
                        + " it computed at game start, " + AddressRules.ForLog(url)
                        + ". A Server IP changed in its F12 since then needs a restart.");
            }

            if (string.IsNullOrEmpty(url))
            {
                url = defaultAddress();
                origin = "Raid Review's default port on the SPT server";
                warnOnce(logWarning, "Raid Review exposed neither its settings nor an address; falling back to "
                    + url + ".");
            }

            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)
                || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                string fallback = defaultAddress();
                warnOnce(logWarning, "Raid Review's server address '" + AddressRules.ForLog(url)
                    + "' is not an http(s) URL; falling back to " + fallback + ".");
                source = "Raid Review's default port on the SPT server";
                return fallback;
            }

            // Raid Review's web server is part of its SERVER mod, so it runs
            // wherever the SPT server runs - but its address defaults to
            // 127.0.0.1, which on a remote setup is the player's own PC.
            string backend = backendHost();
            bool replaced;
            string corrected = AddressRules.CorrectLoopback(parsed, backend, out replaced);
            if (replaced)
            {
                source = origin + ", with the SPT server's host " + backend + " in place of " + parsed.Host;
                warnOnce(logWarning, "Raid Review points at " + parsed.Host + ", but this client talks to "
                    + backend + " - its web server runs on the server machine, so the overlay opens "
                    + AddressRules.ForLog(corrected) + ". Set 'Server IP' in Raid Review's own config to silence this.");
            }
            else
            {
                source = origin;
            }
            return corrected;
        }

        /// <summary>
        /// Scheme and port for a typed address that names neither: what Raid
        /// Review itself would use when its client plugin is here - https with
        /// its TLS switch on, and its port setting, where an empty port means
        /// none, as behind a reverse proxy - otherwise http on its default
        /// port. So a bare host typed in matches the address Raid Review would
        /// open for the same host.
        /// </summary>
        public static void DefaultSchemeAndPort(out string scheme, out string port)
        {
            scheme = Uri.UriSchemeHttp;
            port = DefaultWebClientPort;

            Type type = resolvePluginType();
            if (type == null)
                return;

            ConfigEntry<bool> tls = staticConfigEntry<bool>(type, "ServerTLS");
            if (tls != null && tls.Value)
                scheme = Uri.UriSchemeHttps;

            ConfigEntry<string> configured = staticConfigEntry<string>(type, "ServerHttpPort");
            if (configured == null)
                return;
            string value = configured.Value == null ? string.Empty : configured.Value.Trim();
            int number;
            if (value.Length == 0)
                port = string.Empty;
            else if (int.TryParse(value, out number) && number > 0 && number < 65536)
                port = value;
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
            ConfigEntry<string> port = staticConfigEntry<string>(type, "ServerHttpPort");
            ConfigEntry<bool> tls = staticConfigEntry<bool>(type, "ServerTLS");

            // All three or nothing. A build that renamed only the port or TLS
            // entry would otherwise yield an address without its port, or http
            // where Raid Review uses https - wrong, and silently so - while the
            // address it computed itself is still right. Null hands over to
            // that one. An empty port VALUE stays legitimate: Raid Review then
            // writes no port either.
            if (address == null || port == null || tls == null)
                return null;

            string host = address.Value == null ? string.Empty : address.Value.Trim();
            if (host.Length == 0)
                return null;

            // Trimmed, where Raid Review takes the text raw: a stray space
            // pasted into its F12 field breaks its own address, not this one.
            string portValue = port.Value == null ? string.Empty : port.Value.Trim();
            string scheme = tls.Value ? "https://" : "http://";
            string portPart = portValue.Length > 0 ? ":" + portValue : string.Empty;
            return scheme + host + portPart;
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

        /// <summary>
        /// Each distinct warning once per session. One shared flag used to
        /// guard all of them, so whichever fired first silenced every later
        /// one - including a different problem that showed up afterwards, which
        /// is exactly the line a support question needs.
        /// </summary>
        private static void warnOnce(Action<string> logWarning, string message)
        {
            if (!warned.Add(message))
                return;
            if (logWarning != null)
                logWarning(message);
        }
    }
}
