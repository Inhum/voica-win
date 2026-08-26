using System;
using System.Net;
using System.Net.Http;

namespace Voica;

/// <summary>
/// The single place where HTTP clients are made and where network failures become text (spec §9.5).
///
/// The app must work where the only way out is a proxy and VPNs are forbidden. The diagnosis is not
/// what it looks like: routing already works — .NET reads the system proxy settings by itself — what
/// fails is <b>authentication</b>. A corporate proxy answers <c>407</c>, nobody sends credentials,
/// and from the outside it reads as "the app cannot do proxies". A proxy that needs no credentials
/// has always worked.
///
/// Two things have to be shared, and the second is easy to forget: the client (so a setting reaches
/// every request — recognition, the model download, update checks) and the <b>translation of an
/// error into a sentence</b>. macOS unified the session but not the wording, and ended up naming the
/// proxy in one place while showing a raw error code in three others.
/// </summary>
public static class Net
{
    /// <summary>
    /// Overrides the system proxy for this process only — the way to exercise proxy behaviour
    /// without a corporate network (spec §9.5). Format: <c>host:port</c>.
    /// </summary>
    public const string ProxyOverrideVariable = "VOICA_PROXY";

    private static readonly object Gate = new();
    private static HttpClient? _shared;
    private static bool _builtWithSystemProxy;

    /// <summary>
    /// The client every network call goes through. Rebuilt when the proxy setting changes; the old
    /// instance is left to the GC rather than disposed, because requests may still be running on it.
    /// </summary>
    public static HttpClient Shared
    {
        get
        {
            lock (Gate)
            {
                if (_shared is null || _builtWithSystemProxy != Prefs.UseSystemProxy)
                {
                    _shared = Create();
                    _builtWithSystemProxy = Prefs.UseSystemProxy;
                }
                return _shared;
            }
        }
    }

    /// <summary>Where the proxy for <paramref name="target"/> comes from, or null when going direct.</summary>
    public static Uri? ProxyFor(Uri target) => Resolve(target).Address;

    /// <summary>Who decided the route: nobody, the system, or <see cref="ProxyOverrideVariable"/>.</summary>
    public enum ProxySource
    {
        /// <summary>Nothing stands in the way — the request goes straight out.</summary>
        Direct,
        /// <summary>The proxy Windows itself hands out for this address.</summary>
        System,
        /// <summary>The proxy this process was told to use, overriding the system (§9.5 test path).</summary>
        Forced,
    }

    /// <summary>
    /// The route for <paramref name="target"/>: where the proxy came from, and which one.
    ///
    /// Kept apart from <see cref="Describe"/> on purpose (spec §9.5): the error message carries the
    /// ADDRESS only, and the explanation of where that address came from is a line of its own on the
    /// Network tab. Mixing them is how macOS ended up with half a sentence in the wrong language.
    /// </summary>
    public static (ProxySource Source, Uri? Address) Resolve(Uri target)
    {
        if (Override() is { } forced) return (ProxySource.Forced, forced);
        if (!Prefs.UseSystemProxy) return (ProxySource.Direct, null);
        try
        {
            var proxy = WebRequest.DefaultWebProxy?.GetProxy(target);
            // A proxy that is the target itself means "go direct" — the older IWebProxy contract
            // answers that way instead of null, and taking it at face value would report a proxy
            // where there is none. The stub caught exactly the mirror image of this on macOS.
            if (proxy is null || (proxy.Host == target.Host && proxy.Port == target.Port))
                return (ProxySource.Direct, null);
            return (ProxySource.System, proxy);
        }
        catch { return (ProxySource.Direct, null); }
    }

    /// <summary>
    /// Turns a failed request into a sentence a person can act on (spec §9.5). Every network call
    /// must come through here: the point is that the same failure reads the same way everywhere.
    /// A proxy failure names the proxy — without the address there is nothing to go and fix.
    /// </summary>
    public static string Describe(Exception error, Uri target)
    {
        if (IsProxyAuthFailure(error))
        {
            var proxy = ProxyFor(target);
            return string.Format(S.ErrProxyAuthFmt, proxy is null ? "?" : $"{proxy.Host}:{proxy.Port}");
        }
        return error.Message;
    }

    /// <summary>
    /// Whether the request died on the proxy rather than on the far end. .NET 8 says so outright for
    /// a tunnelled request (HTTPS goes through CONNECT, and the proxy's answer never reaches the
    /// status code); a plain <c>407</c> covers the rest.
    /// </summary>
    public static bool IsProxyAuthFailure(Exception error)
    {
        for (Exception? e = error; e is not null; e = e.InnerException)
        {
            if (e is HttpRequestException http)
            {
                if (http.HttpRequestError == HttpRequestError.ProxyTunnelError) return true;
                if (http.StatusCode == HttpStatusCode.ProxyAuthenticationRequired) return true;
            }
        }
        return false;
    }

    /// <summary>The proxy forced by <see cref="ProxyOverrideVariable"/>, or null when it is unset.</summary>
    public static Uri? Override()
    {
        var value = Environment.GetEnvironmentVariable(ProxyOverrideVariable);
        return ParseOverride(value);
    }

    /// <summary>
    /// Parses <c>host:port</c> (a scheme is allowed and ignored). Null when unusable.
    ///
    /// The port has to be spelled out: without that rule any stray word parses as a host on port 80
    /// and the app quietly sends everything to a machine that does not exist.
    /// </summary>
    public static Uri? ParseOverride(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        var match = System.Text.RegularExpressions.Regex.Match(
            text, @"^(?:[a-zA-Z][a-zA-Z0-9+.-]*://)?([^:/\s]+):(\d{1,5})/?$");
        if (!match.Success) return null;

        return Uri.TryCreate($"http://{match.Groups[1].Value}:{match.Groups[2].Value}", UriKind.Absolute, out var uri)
            && uri.Port is > 0 and <= 65535
            ? uri
            : null;
    }

    private static HttpClient Create()
    {
        var handler = new HttpClientHandler
        {
            // The credentials of the signed-in user, so a domain proxy authenticates over SSO and
            // no password is ever typed into Voica (spec §9.5).
            DefaultProxyCredentials = CredentialCache.DefaultNetworkCredentials,
            UseProxy = true,
        };

        if (Override() is { } forced)
        {
            handler.Proxy = new WebProxy(forced) { UseDefaultCredentials = true };
            Log.Info($"network: proxy forced by {ProxyOverrideVariable} → {forced.Host}:{forced.Port}");
        }
        else if (!Prefs.UseSystemProxy)
        {
            // Going direct is a real need too: a proxy left misconfigured in the system settings
            // blocks the app as effectively as a missing one.
            handler.UseProxy = false;
            Log.Info("network: system proxy disabled in settings — going direct");
        }

        var client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Voica");
        return client;
    }
}
