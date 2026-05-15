namespace SIDM.Core;

/// <summary>
/// Browsers SIDM ships an extension for. All Chromium-family entries use the
/// same extension package; Firefox uses its own. The split exists so the
/// in-app installer can present per-browser status + open the right page.
/// </summary>
public enum BrowserKind
{
    Chrome,
    Edge,
    Brave,
    Firefox,
}

public static class BrowserKindExtensions
{
    /// <summary>Same extension pack works for Chrome, Edge, Brave.</summary>
    public static bool IsChromium(this BrowserKind kind) =>
        kind is BrowserKind.Chrome or BrowserKind.Edge or BrowserKind.Brave;

    public static string DisplayName(this BrowserKind kind) => kind switch
    {
        BrowserKind.Chrome => "Google Chrome",
        BrowserKind.Edge => "Microsoft Edge",
        BrowserKind.Brave => "Brave",
        BrowserKind.Firefox => "Mozilla Firefox",
        _ => kind.ToString(),
    };
}
