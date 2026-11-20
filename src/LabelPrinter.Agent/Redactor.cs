using System.Text.RegularExpressions;

namespace LabelPrinter.Agent;

/// <summary>Built-in server address and its removal from everything the control window shows. The address is
/// compiled in (so the window never needs to ask for or display it); text that reaches the window (messages, log
/// tail, error notes) is passed through <see cref="Apply"/> so a stray URL, host or port from an exception
/// message cannot leak the server or the local panel address.</summary>
internal static partial class Redactor
{
    /// <summary>The one production server. Only the command line (<c>pair --origin</c>) can override it, for tests.</summary>
    internal const string DefaultOrigin = "https://poshakerp.top";
    internal const string Placeholder = "[سرور]";

    [GeneratedRegex(@"[a-zA-Z][a-zA-Z0-9+.-]*://[^\s""'<>)\]]+", RegexOptions.CultureInvariant)]
    private static partial Regex Url();
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4();
    [GeneratedRegex(@"\b(?:localhost|[A-Za-z0-9-]+(?:\.[A-Za-z0-9-]+)+):\d{2,5}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex HostPort();
    [GeneratedRegex(@"\blocalhost\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Localhost();

    internal static string Apply(string? text, string? origin = null)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        // Whole URLs first (the placeholder contains ']', which would otherwise cut a URL short), then bare hosts.
        var t = Url().Replace(text, Placeholder);
        foreach (var host in Hosts(origin))
            t = Regex.Replace(t, Regex.Escape(host) + @"(?::\d{1,5})?", Placeholder, RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        t = HostPort().Replace(t, Placeholder);
        t = Ipv4().Replace(t, Placeholder);
        t = Localhost().Replace(t, Placeholder);
        return t;
    }

    private static IEnumerable<string> Hosts(string? origin)
    {
        yield return new Uri(DefaultOrigin).Host;
        if (origin is not null && Uri.TryCreate(origin, UriKind.Absolute, out var u) && !string.IsNullOrEmpty(u.Host)) yield return u.Host;
    }
}
