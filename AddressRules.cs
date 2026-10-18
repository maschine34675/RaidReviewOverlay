using System;
using System.Net;
using System.Text.RegularExpressions;

namespace RaidReviewOverlay
{
    /// <summary>
    /// The address rules on their own, with nothing from Unity, BepInEx or SPT
    /// in them, so scripts\Test-AddressRules.ps1 can compile this very file and
    /// run it on the game's own runtime against the cases there - the parsing is
    /// where address bugs hide, and a reimplementation in a test would only
    /// prove itself right.
    ///
    /// Every URL leaves here as Uri.AbsoluteUri, never Uri.ToString(): the
    /// latter unescapes on some runtimes (%26 becomes a literal ampersand and
    /// changes what a query means), AbsoluteUri keeps the escaping everywhere.
    /// </summary>
    internal static class AddressRules
    {
        /// <summary>
        /// "host" or "host:port", host being a name, an IPv4 address or an IPv6
        /// address in brackets. A bare IPv6 address is deliberately not matched:
        /// its colons cannot be told apart from a port.
        /// </summary>
        private static readonly Regex HostWithPort = new Regex(
            @"^(\[[^\]]*\]|[^:\[\]]*):\d+$", RegexOptions.CultureInvariant);

        /// <summary>A URI scheme at the start of the text, with its "://".</summary>
        private static readonly Regex SchemePrefix = new Regex(@"^[A-Za-z][A-Za-z0-9+.\-]*://", RegexOptions.CultureInvariant);

        /// <summary>What may follow a bracketed IPv6 host: nothing, or :port.</summary>
        private static readonly Regex AfterBracket = new Regex(@"^(:\d+)?$", RegexOptions.CultureInvariant);

        /// <summary>
        /// Turns what a player types into the address setting into a URL, or
        /// null with a reason. Two forms, because players paste both:
        ///
        /// - with a scheme ("http://192.168.1.20:7829", "https://raid.example")
        ///   the URL is used as written - that is what works in their browser,
        ///   and a reverse proxy on 443 must not grow a port;
        /// - without one ("192.168.1.20", "192.168.1.20:7829") it is a host, and
        ///   scheme and a missing port come from Raid Review's own settings
        ///   (<paramref name="defaultScheme"/>, <paramref name="defaultPort"/>).
        ///   An empty default port means none, as in Raid Review's own address.
        /// </summary>
        public static string NormalizeManual(string input, string defaultScheme, string defaultPort, out string error)
        {
            error = null;
            string text = input == null ? string.Empty : input.Trim();
            if (text.Length == 0)
            {
                error = "it is empty";
                return null;
            }

            if (text.IndexOf("://", StringComparison.Ordinal) < 0)
            {
                // Authority ends at the first path, query or fragment marker;
                // credentials in front of it are not part of the host.
                int end = text.IndexOfAny(new[] { '/', '?', '#' });
                string authority = end < 0 ? text : text.Substring(0, end);
                string rest = end < 0 ? string.Empty : text.Substring(end);
                int at = authority.LastIndexOf('@');
                string userInfo = at < 0 ? string.Empty : authority.Substring(0, at + 1);
                string hostPart = at < 0 ? authority : authority.Substring(at + 1);

                if (hostPart.StartsWith("[", StringComparison.Ordinal))
                {
                    if (!bracketsClosedProperly(hostPart, out error))
                        return null;
                }
                else if (!HostWithPort.IsMatch(hostPart))
                {
                    int colons = hostPart.Length - hostPart.Replace(":", string.Empty).Length;
                    if (colons > 1)
                    {
                        error = "an IPv6 address needs brackets, like [fd00::20]:7829";
                        return null;
                    }
                    if (colons == 1)
                    {
                        error = "the port after ':' is not a number";
                        return null;
                    }
                }

                if (!HostWithPort.IsMatch(hostPart) && !string.IsNullOrEmpty(defaultPort))
                    hostPart = hostPart + ":" + defaultPort;

                string scheme = string.IsNullOrEmpty(defaultScheme) ? Uri.UriSchemeHttp : defaultScheme;
                text = scheme + "://" + userInfo + hostPart + rest;
            }
            else
            {
                // A pasted URL gets the same bracket check: Uri takes anything
                // after ']' for a path, so "http://[fd00::20]7829" would open
                // port 80 at /7829 without a word.
                int hostStart = text.IndexOf("://", StringComparison.Ordinal) + 3;
                int end = text.IndexOfAny(new[] { '/', '?', '#' }, hostStart);
                string authority = end < 0 ? text.Substring(hostStart) : text.Substring(hostStart, end - hostStart);
                string hostPart = authority.Substring(authority.LastIndexOf('@') + 1);
                if (hostPart.StartsWith("[", StringComparison.Ordinal) && !bracketsClosedProperly(hostPart, out error))
                    return null;
            }

            Uri parsed;
            if (!Uri.TryCreate(text, UriKind.Absolute, out parsed))
            {
                error = "it is not an address";
                return null;
            }

            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            {
                error = "only http:// and https:// addresses can be opened";
                return null;
            }

            if (string.IsNullOrEmpty(parsed.Host))
            {
                error = "it names no host";
                return null;
            }

            return parsed.AbsoluteUri;
        }

        /// <summary>
        /// Replaces a loopback host with the server this client talks to,
        /// keeping scheme and port. Only ever loopback to remote: an address
        /// that already names another machine is returned untouched, and so is
        /// everything on an install where the server is local too.
        /// </summary>
        public static string CorrectLoopback(Uri address, string backendHost, out bool replaced)
        {
            replaced = false;
            if (address == null)
                return null;
            if (!IsLoopback(address.Host) || string.IsNullOrEmpty(backendHost) || IsLoopback(backendHost))
                return address.AbsoluteUri;

            var builder = new UriBuilder(address);
            builder.Host = backendHost;
            // UriBuilder writes the default port explicitly; keep the address
            // as short as it came in.
            if (address.IsDefaultPort)
                builder.Port = -1;

            replaced = true;
            return builder.Uri.AbsoluteUri;
        }

        /// <summary>
        /// A bracketed IPv6 host may be followed by nothing or ":port".
        /// "[fd00::20]7829" or "[fd00::20] :7829" would otherwise parse as a
        /// path and open port 80 without a word.
        /// </summary>
        private static bool bracketsClosedProperly(string hostPart, out string error)
        {
            int close = hostPart.IndexOf(']');
            if (close < 0 || !AfterBracket.IsMatch(hostPart.Substring(close + 1)))
            {
                error = "after the closing ']' only a port may follow, like [fd00::20]:7829";
                return false;
            }
            error = null;
            return true;
        }

        public static bool IsLoopback(string host)
        {
            if (string.IsNullOrEmpty(host))
                return false;
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                return true;

            // Uri.Host keeps the brackets of an IPv6 literal; IPAddress does not
            // parse them.
            string bare = host.Length > 1 && host[0] == '[' && host[host.Length - 1] == ']'
                ? host.Substring(1, host.Length - 2)
                : host;
            IPAddress parsed;
            return IPAddress.TryParse(bare, out parsed) && IPAddress.IsLoopback(parsed);
        }

        /// <summary>
        /// The address as it may appear in a log: everything between the scheme
        /// (or the start) and the LAST '@' replaced by ***. Players paste the
        /// log into support threads, and an address with credentials would take
        /// their password along. Cutting at the last '@' rather than parsing is
        /// deliberate: a password typed raw may itself contain '@', '/', '?',
        /// '#' or spaces, and those are exactly the addresses that fail to parse
        /// and get quoted in a warning. The price is that an '@' in a path hides
        /// the host too - harmless in a log line, unlike a leaked password.
        /// </summary>
        public static string ForLog(string address)
        {
            if (string.IsNullOrEmpty(address))
                return address;
            int at = address.LastIndexOf('@');
            if (at < 0)
                return address;
            // Only a real scheme at the very start is kept: "://" inside a
            // password typed without one ("admin:pa://ss@host") must not pass
            // for the end of a scheme and leave the user name readable.
            Match scheme = SchemePrefix.Match(address);
            int start = scheme.Success && scheme.Length <= at ? scheme.Length : 0;
            return address.Substring(0, start) + "***" + address.Substring(at);
        }
    }
}
