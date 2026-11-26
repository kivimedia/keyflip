namespace KeyFlip;

/// <summary>
/// The few things the site lookup reads from a UI Automation element. There is deliberately no way to
/// list children: on 26-Nov-2026 a downward search (FindFirst over TreeScope.Descendants) through Chrome's
/// window kept Chrome "Not Responding" for minutes every time the address bar was clicked, because Chrome's
/// UI thread answers every node of that walk one cross-process call at a time.
/// </summary>
public interface IUiNode
{
    string Name { get; }
    bool IsEdit { get; }
    bool IsDocument { get; }
    /// <summary>The ValuePattern value, or null. A Chromium web page element's value is its URL.</summary>
    string? Value { get; }
    IUiNode? Parent { get; }
}

public static class SiteFinder
{
    public const string AddressBarName = "Address and search bar";

    /// <summary>Upper bound on the walk up; a real field sits 10-40 levels under the window.</summary>
    public const int MaxParents = 80;

    public static bool IsAddressBar(IUiNode n) =>
        n.IsEdit && n.Name.Equals(AddressBarName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The URL for the focused element, reading only the element and its ancestors: the address bar's own
    /// text when the bar is focused, else the OUTERMOST web page above the element (the tab's page, not an
    /// iframe inside it). Null when there is no page above it (browser UI, a dialog).
    /// </summary>
    public static string? UrlFor(IUiNode focused, out int steps)
    {
        steps = 0;
        if (IsAddressBar(focused)) return focused.Value;
        string? url = null;
        for (IUiNode? n = focused; n != null && steps <= MaxParents; n = n.Parent, steps++)
        {
            if (!n.IsDocument) continue;
            var v = n.Value;
            if (!string.IsNullOrEmpty(v) && v.Contains("://")) url = v;
        }
        return url;
    }

    /// <summary>"https://www.snov.io/x" -> "snov.io"; a bare "snov.io" typed in the bar works too.</summary>
    public static string HostOf(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!url.Contains("://")) url = "https://" + url;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : "";
    }
}
