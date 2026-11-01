using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;

namespace JustMd.Core
{
    public enum LinkKind
    {
        /// <summary>Empty link; nothing to do.</summary>
        None,

        /// <summary>In-page anchor (<c>#section</c>); scroll without a warning.</summary>
        Anchor,

        /// <summary>http, https or mailto; needs user confirmation before opening in the browser.</summary>
        External,

        /// <summary>An existing local Markdown file; opens in the viewer.</summary>
        LocalMarkdown,

        /// <summary>Any other local file or folder; never opened (shown to the user only).</summary>
        LocalFile,

        /// <summary>Disallowed scheme (javascript:, file:, data:, custom protocols) or unusable path.</summary>
        Blocked,
    }

    /// <summary>Result of classifying a link the user clicked in the document.</summary>
    public sealed class LinkTarget
    {
        internal LinkTarget(LinkKind kind, string href)
        {
            Kind = kind;
            Href = href;
        }

        public LinkKind Kind { get; }

        /// <summary>The href exactly as it appeared in the document.</summary>
        public string Href { get; }

        /// <summary>Parsed URI for <see cref="LinkKind.External"/> links.</summary>
        public Uri? Uri { get; internal set; }

        /// <summary>Resolved full path for local links.</summary>
        public string? LocalPath { get; internal set; }

        /// <summary>Decoded fragment (without '#') for anchors and local Markdown links.</summary>
        public string? Fragment { get; internal set; }

        /// <summary>Lower-case scheme for external or blocked links that had one.</summary>
        public string? Scheme { get; internal set; }
    }

    /// <summary>Display parts of an external URL so the host name can be emphasised.</summary>
    public sealed class ExternalLinkParts
    {
        public ExternalLinkParts(string prefix, string host, string suffix)
        {
            Prefix = prefix;
            Host = host;
            Suffix = suffix;
        }

        public string Prefix { get; }

        public string Host { get; }

        public string Suffix { get; }

        public string FullText => Prefix + Host + Suffix;
    }

    /// <summary>
    /// Decides what a clicked link is allowed to do. Markdown is untrusted input, so anything
    /// that is not clearly an anchor, a web/mail link or a local Markdown file is refused.
    /// </summary>
    public static class LinkClassifier
    {
        public const int MaxHrefLength = 8192;

        public static LinkTarget Classify(string? href, string? documentPath)
        {
            string raw = href ?? string.Empty;
            string trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                return new LinkTarget(LinkKind.None, raw);
            }

            if (trimmed.Length > MaxHrefLength)
            {
                return new LinkTarget(LinkKind.Blocked, raw);
            }

            if (trimmed[0] == '#')
            {
                return new LinkTarget(LinkKind.Anchor, raw) { Fragment = LocalPaths.SafeUnescape(trimmed.Substring(1)) };
            }

            string? scheme = LocalPaths.GetScheme(trimmed);
            if (scheme != null)
            {
                return TryValidateExternal(trimmed, out Uri? uri)
                    ? new LinkTarget(LinkKind.External, raw) { Uri = uri, Scheme = scheme }
                    : new LinkTarget(LinkKind.Blocked, raw) { Scheme = scheme };
            }

            if (LocalPaths.IsNetworkOrProtocolRelative(trimmed) || string.IsNullOrEmpty(documentPath))
            {
                return new LinkTarget(LinkKind.Blocked, raw);
            }

            return ClassifyLocal(raw, trimmed, documentPath!);
        }

        /// <summary>
        /// Final gate before handing a URL to the shell: absolute, well-formed and http, https or mailto.
        /// Used both when classifying and again right before opening.
        /// </summary>
        public static bool TryValidateExternal(string? url, [NotNullWhen(true)] out Uri? uri)
        {
            uri = null;
            if (string.IsNullOrWhiteSpace(url) || url!.Length > MaxHrefLength
                || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? parsed))
            {
                return false;
            }

            bool web = parsed.Scheme == Uri.UriSchemeHttp || parsed.Scheme == Uri.UriSchemeHttps;
            bool mail = parsed.Scheme == Uri.UriSchemeMailto;
            if (!web && !mail)
            {
                return false;
            }

            if (web && (string.IsNullOrEmpty(parsed.Host) || parsed.IsFile || parsed.IsUnc))
            {
                return false;
            }

            if (parsed.AbsoluteUri.IndexOfAny(new[] { '\r', '\n', '\0', '"' }) >= 0)
            {
                return false;
            }

            uri = parsed;
            return true;
        }

        /// <summary>Splits an external URL into prefix / host / suffix for display.</summary>
        public static ExternalLinkParts GetDisplayParts(Uri uri)
        {
            string host = uri.Host;
            if (uri.Scheme == Uri.UriSchemeMailto)
            {
                string full = uri.OriginalString.Trim();
                string address = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
                string prefix = "mailto:" + (address.Length > 0 ? address + "@" : string.Empty);
                string rest = uri.GetComponents(UriComponents.Query, UriFormat.UriEscaped);
                string suffix = rest.Length > 0 ? "?" + rest : string.Empty;
                if (host.Length == 0)
                {
                    return new ExternalLinkParts(string.Empty, full, string.Empty);
                }

                return new ExternalLinkParts(prefix, host, suffix);
            }

            string userInfo = uri.GetComponents(UriComponents.UserInfo, UriFormat.UriEscaped);
            string start = uri.Scheme + "://" + (userInfo.Length > 0 ? userInfo + "@" : string.Empty);
            string port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            string end = port + uri.GetComponents(UriComponents.PathAndQuery | UriComponents.Fragment, UriFormat.UriEscaped);
            return new ExternalLinkParts(start, host, end);
        }

        private static LinkTarget ClassifyLocal(string raw, string trimmed, string documentPath)
        {
            string pathPart = LocalPaths.StripQueryAndFragment(trimmed, out string? fragment);
            string? documentFull = LocalPaths.TryGetFullPath(documentPath);
            string? baseDirectory = documentFull == null ? null : Path.GetDirectoryName(documentFull);
            if (baseDirectory == null)
            {
                return new LinkTarget(LinkKind.Blocked, raw);
            }

            if (pathPart.Length == 0)
            {
                // "?x#y" style link: treat as the current document.
                return new LinkTarget(LinkKind.LocalMarkdown, raw) { LocalPath = documentFull, Fragment = fragment };
            }

            string? full = LocalPaths.Resolve(pathPart, baseDirectory);
            if (full == null)
            {
                return new LinkTarget(LinkKind.Blocked, raw);
            }

            // Never touch a different network share: even a File.Exists check would make Windows
            // try to authenticate against an attacker-chosen server.
            if (LocalPaths.IsUncPath(full) && !LocalPaths.HaveSameRoot(full, documentFull!))
            {
                return new LinkTarget(LinkKind.Blocked, raw);
            }

            bool isMarkdown = MarkdownFiles.IsMarkdownPath(full) && File.Exists(full);
            return new LinkTarget(isMarkdown ? LinkKind.LocalMarkdown : LinkKind.LocalFile, raw)
            {
                LocalPath = full,
                Fragment = fragment,
            };
        }
    }
}
