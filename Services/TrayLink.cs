using Avalonia.Threading;
using pxdStandard;

namespace pxdChatterGuard.Services;

/// <summary>
/// The pxd Toolkit link for the tray icon: whether it shows comes from toolkit.json (pxd Toolkit or this app's own
/// "Show tray icon" switch), and a change to that file shows or hides the icon live.
/// </summary>
public sealed class TrayLink : IDisposable
{
    private readonly string _appId;
    private readonly Action<bool> _apply;
    private readonly IDisposable? _watch;

    /// <param name="apply">Shows (true) or hides the tray icon. Called on the UI thread whenever the setting changes.</param>
    public TrayLink(string appId, Action<bool> apply)
    {
        _appId = appId;
        _apply = apply;
        Visible = pxdLink.TrayIconVisible(appId);
        try
        {
            _watch = pxdLink.Watch(() => Dispatcher.UIThread.Post(Refresh));
        }
        catch
        {
            // No shared folder to watch: the icon still follows this app's own switch.
        }
    }

    public bool Visible { get; private set; }

    /// <summary>The app's own switch: writes toolkit.json (so pxd Toolkit shows the same) and applies it. UI thread.</summary>
    public void Set(bool visible)
    {
        pxdLink.SetTrayIconVisible(_appId, visible);
        Refresh();
    }

    /// <summary>Reads toolkit.json again and applies a change. UI thread.</summary>
    public void Refresh()
    {
        var v = pxdLink.TrayIconVisible(_appId);
        if (v == Visible)
            return;
        Visible = v;
        _apply(v);
    }

    public void Dispose() => _watch?.Dispose();
}
