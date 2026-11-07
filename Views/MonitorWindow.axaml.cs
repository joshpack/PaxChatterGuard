using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using PaxChatterGuard.Core;
using PaxChatterGuard.Services;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Path = Avalonia.Controls.Shapes.Path;

namespace PaxChatterGuard.Views;

/// <summary>
/// The chatter monitor: every press that looked like chatter, newest first, blocked or not, with its timing and a button to
/// put the key on the exception list. Like the settings window it exists only while open; the log itself is a small fixed
/// buffer inside the engine that costs nothing while nobody looks at it.
/// </summary>
public partial class MonitorWindow : Window
{
    private static readonly IBrush BlockedBrush = new SolidColorBrush(Color.Parse("#19F58C"));
    private static readonly IBrush SlippedBrush = new SolidColorBrush(Color.Parse("#F5B14C"));
    private static readonly IBrush NotFilteredBrush = new SolidColorBrush(Color.Parse("#00C8FF"));
    private static readonly IBrush CloseBrush = new SolidColorBrush(Color.Parse("#8A92A6"));
    private static readonly IBrush RowBrush = new SolidColorBrush(Color.Parse("#12161D"));
    private static readonly IBrush CapBrush = new SolidColorBrush(Color.Parse("#1D212C"));

    private readonly GuardService _svc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Dictionary<long, Row> _rows = new();
    private long _version = -1;
    private List<ChatterEvent> _events = new();

    public MonitorWindow()
    {
        InitializeComponent();
        _svc = null!;
    }

    public MonitorWindow(GuardService svc)
    {
        InitializeComponent();
        _svc = svc;
        Icon = SpadeIcon.CreateWindowIcon();
        Ctl<Path>("Spade").Data = SpadeIcon.Geometry;

        Ctl<CheckBox>("CloseCallsBox").IsCheckedChanged += (_, _) => ApplyVisibility();
        Ctl<CheckBox>("TopmostBox").IsCheckedChanged += (_, _) => Topmost = Ctl<CheckBox>("TopmostBox").IsChecked == true;
        Ctl<Button>("ClearButton").Click += (_, _) =>
        {
            _svc.Engine.ClearLog();
            Refresh();
        };
        Ctl<Button>("CopyButton").Click += async (_, _) => await CopyAsync();

        svc.Changed += OnServiceChanged;
        svc.RulesChanged += OnRulesChanged;
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            svc.Changed -= OnServiceChanged;
            svc.RulesChanged -= OnRulesChanged;
        };

        UpdateStatus();
        Refresh();
    }

    private T Ctl<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnServiceChanged() => Dispatcher.UIThread.Post(UpdateStatus);

    private void OnRulesChanged()
    {
        // A key's delay changed (here, in the settings or by auto-adjust): the buttons may say something else now.
        foreach (var row in _rows.Values)
            row.Update(row.Event, this);
    }

    private void UpdateStatus()
    {
        string pill, border, dot, text;
        if (_svc.Problem is not null) { pill = "#33141A"; border = "#7A2A33"; dot = "#F2666A"; text = "Needs attention"; }
        else if (_svc.IsFiltering) { pill = "#0C2A1D"; border = "#146B44"; dot = "#19F58C"; text = "Filtering on"; }
        else if (_svc.PausedFor is not null) { pill = "#33260F"; border = "#5C4515"; dot = "#F5B14C"; text = "Paused"; }
        else { pill = "#1A1E27"; border = "#2A3140"; dot = "#8A92A6"; text = "Filtering off"; }

        Ctl<TextBlock>("StatusText").Text = text;
        var p = Ctl<Border>("StatusPill");
        p.Background = new SolidColorBrush(Color.Parse(pill));
        p.BorderBrush = new SolidColorBrush(Color.Parse(border));
        Ctl<Ellipse>("StatusDot").Fill = new SolidColorBrush(Color.Parse(dot));
    }

    // ---- The list ------------------------------------------------------------------------------------------------

    private void Refresh()
    {
        var mode = _svc.IsFiltering ? "watching and filtering"
            : _svc.PausedFor is { } program ? $"paused while {program} is in front"
            : "watching only, nothing is blocked";
        Ctl<TextBlock>("SinceText").Text = $"Since {_svc.Engine.LogSinceUtc.ToLocalTime():HH:mm} · {mode}";

        var version = _svc.Engine.LogVersion;
        if (version == _version)
            return;
        _version = version;
        _events = _svc.Engine.RecentEvents();

        // Keep the rows already shown (so a button isn't pulled away mid-click), add new ones on top, drop the gone ones.
        var list = Ctl<StackPanel>("EventList");
        var live = new HashSet<long>(_events.Select(e => e.Seq));
        foreach (var gone in _rows.Keys.Where(k => !live.Contains(k)).ToList())
        {
            list.Children.Remove(_rows[gone].Root);
            _rows.Remove(gone);
        }

        for (var i = 0; i < _events.Count; i++)
        {
            var ev = _events[i];
            if (_rows.TryGetValue(ev.Seq, out var row))
            {
                if (row.Event != ev)
                    row.Update(ev, this);
                continue;
            }

            row = new Row();
            row.Update(ev, this);
            _rows[ev.Seq] = row;
            list.Children.Insert(Math.Min(i, list.Children.Count), row.Root);
        }

        int blocked = 0, slipped = 0, notFiltered = 0, close = 0;
        foreach (var ev in _events.Where(e => (e.Flags & ChatterFlags.Test) == 0))
        {
            switch (ev.Kind)
            {
                case ChatterKind.Blocked: blocked += ev.Bounces; break;
                case ChatterKind.Slipped: slipped++; break;
                case ChatterKind.NotFiltered: notFiltered += ev.Bounces; break;
                case ChatterKind.CloseCall: close++; break;
            }
        }

        Ctl<TextBlock>("BlockedCount").Text = blocked.ToString("N0", CultureInfo.InvariantCulture);
        Ctl<TextBlock>("SlippedCount").Text = slipped.ToString("N0", CultureInfo.InvariantCulture);
        Ctl<TextBlock>("NotFilteredCount").Text = notFiltered.ToString("N0", CultureInfo.InvariantCulture);
        Ctl<TextBlock>("CloseCount").Text = close.ToString("N0", CultureInfo.InvariantCulture);
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        var showClose = Ctl<CheckBox>("CloseCallsBox").IsChecked == true;
        var visible = 0;
        foreach (var row in _rows.Values)
        {
            row.Root.IsVisible = showClose || row.Event.Kind != ChatterKind.CloseCall;
            if (row.Root.IsVisible)
                visible++;
        }

        Ctl<StackPanel>("EmptyState").IsVisible = visible == 0;
    }

    private async Task CopyAsync()
    {
        var text = new StringBuilder();
        foreach (var ev in _events)
            text.AppendLine(string.Join("  ", new[]
            {
                ev.TimeUtc.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
                KeyCatalog.NameOf(ev.Key),
                KindLabel(ev).ToLowerInvariant(),
                "gap " + Ms(ev.GapMicros),
                "held " + (ev.HeldMicros < 0 ? "-" : Ms(ev.HeldMicros)),
                "press before " + (ev.PrevHeldMicros < 0 ? "-" : Ms(ev.PrevHeldMicros)),
                $"delay {ev.DelayMs} ms",
                ev.Bounces > 1 ? $"{ev.Bounces} bounces" : "",
                _svc.DeviceName(ev.Device) ?? "",
            }.Where(s => s.Length > 0)));

        if (Clipboard is { } clipboard)
            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text.Length == 0 ? "No chatter logged." : text.ToString());
        Ctl<Button>("CopyButton").Content = "Copied";
        DispatcherTimer.RunOnce(() => Ctl<Button>("CopyButton").Content = "Copy", TimeSpan.FromSeconds(1.5));
    }

    // ---- One line ------------------------------------------------------------------------------------------------

    internal static string Ms(long micros)
    {
        if (micros < 1000)
            return "<1 ms";
        var ms = micros / 1000.0;
        if (ms >= 1000)
            return $"{(ms / 1000).ToString("0.#", CultureInfo.InvariantCulture)} s";
        return ms < 10 ? $"{ms.ToString("0.#", CultureInfo.InvariantCulture)} ms" : $"{Math.Round(ms).ToString(CultureInfo.InvariantCulture)} ms";
    }

    internal static string KindLabel(ChatterEvent ev) => ev.Kind switch
    {
        ChatterKind.Blocked => "Blocked",
        ChatterKind.Slipped => "Got through",
        ChatterKind.NotFiltered => "Not filtered",
        _ => "Close call",
    };

    private static IBrush KindBrush(ChatterKind kind) => kind switch
    {
        ChatterKind.Blocked => BlockedBrush,
        ChatterKind.Slipped => SlippedBrush,
        ChatterKind.NotFiltered => NotFilteredBrush,
        _ => CloseBrush,
    };

    /// <summary>The delay that would have caught this press: a little above its gap.</summary>
    internal static int Suggest(ChatterEvent ev) => Math.Clamp((int)Math.Ceiling(ev.GapMicros / 1000.0) + 5, 10, 250);

    /// <summary>The delay that would have let this press through: a little below its gap.</summary>
    internal static int Lower(ChatterEvent ev) => Math.Max(5, (int)Math.Floor(ev.GapMicros / 1000.0) - 2);

    private sealed class Row
    {
        public readonly Border Root;
        private readonly Border _cap;
        private readonly TextBlock _capText, _title, _time, _detail, _sub;
        private readonly Button _action;
        private Action? _onAction;
        public ChatterEvent Event;

        public Row()
        {
            _capText = new TextBlock { FontWeight = FontWeight.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis };
            _cap = new Border { Width = 64, Height = 44, CornerRadius = new CornerRadius(8), Background = CapBrush, BorderThickness = new Thickness(1), Padding = new Thickness(4, 0), Child = _capText, VerticalAlignment = VerticalAlignment.Top };
            _title = new TextBlock { FontSize = 11, FontWeight = FontWeight.SemiBold, LetterSpacing = 0.6 };
            _detail = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
            _sub = new TextBlock { FontSize = 11, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
            _time = new TextBlock { FontSize = 11, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Right };
            _action = new Button { MinHeight = 30, Padding = new Thickness(10, 3), FontSize = 12, HorizontalAlignment = HorizontalAlignment.Right };
            _action.Click += (_, _) => _onAction?.Invoke();

            var texts = new StackPanel { Spacing = 2, Margin = new Thickness(12, 0, 8, 0), Children = { _title, _detail, _sub } };
            var right = new StackPanel { Spacing = 6, Children = { _time, _action } };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(_cap);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Grid.SetColumn(right, 2);
            grid.Children.Add(right);

            Root = new Border
            {
                Background = RowBrush,
                CornerRadius = new CornerRadius(10),
                BorderThickness = new Thickness(3, 0, 0, 0),
                Padding = new Thickness(10, 9, 10, 9),
                Child = grid,
            };
        }

        public void Update(ChatterEvent ev, MonitorWindow w)
        {
            Event = ev;
            var svc = w._svc;
            var brush = KindBrush(ev.Kind);
            var key = KeyCatalog.NameOf(ev.Key);
            var test = (ev.Flags & ChatterFlags.Test) != 0;

            Root.BorderBrush = brush;
            _cap.BorderBrush = brush;
            _capText.Text = key;
            _capText.FontSize = key.Length <= 2 ? 16 : key.Length <= 5 ? 13 : 11;
            _title.Foreground = brush;
            _title.Text = KindLabel(ev).ToUpperInvariant()
                          + (ev.Bounces > 1 ? $" · {ev.Bounces} BOUNCES" : "")
                          + (test ? " · TEST" : "");
            _time.Text = ev.TimeUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);

            var detail = $"pressed again {Ms(ev.GapMicros)} after letting go";
            if (ev.HeldMicros >= 0)
                detail += $" · held {Ms(ev.HeldMicros)}";
            else if (ev.Kind is ChatterKind.Blocked or ChatterKind.NotFiltered)
                detail += " · still down";
            _detail.Text = detail;

            var sub = new List<string>();
            switch (ev.Kind)
            {
                case ChatterKind.Blocked:
                    sub.Add((ev.Flags & ChatterFlags.HeldOn) != 0 ? "You held it down, so it was let through once it repeated"
                        : (ev.Flags & ChatterFlags.KeptHeld) != 0 ? "Its release was held back, so programs saw the key stay down"
                        : $"Under this key's {ev.DelayMs} ms delay, so no program saw it");
                    break;
                case ChatterKind.Slipped:
                    sub.Add(ev.HeldMicros >= 0 && ev.HeldMicros < ChatterEngine.TinyPressMicros
                        ? $"A {Ms(ev.HeldMicros)} press is too short for a finger"
                        : $"The press before it lasted only {Ms(ev.PrevHeldMicros)}, too short for a finger");
                    sub.Add($"past the {ev.DelayMs} ms delay");
                    break;
                case ChatterKind.NotFiltered:
                    sub.Add(ev.Why switch
                    {
                        Exempt.FilterOff => $"Filtering is off; it would have been blocked ({ev.DelayMs} ms delay)",
                        Exempt.Modifier => "Shift, Ctrl, Alt and Win aren't filtered (see the settings)",
                        Exempt.KeyRule => "This key is set to never be filtered",
                        Exempt.KeyboardOff => "This keyboard is set to be left alone",
                        Exempt.Paused => $"Paused for a program on the pause list; it would have been blocked ({ev.DelayMs} ms delay)",
                        _ => "Not filtered",
                    });
                    break;
                default:
                    sub.Add($"Just past the {ev.DelayMs} ms delay, probably you");
                    break;
            }

            if (ev.Device is not null && (svc.Backend.Seen.Count > 1 || svc.HasOwnSettings(ev.Device)))
                sub.Add(svc.DeviceName(ev.Device) ?? ev.Device);
            _sub.Text = string.Join(" · ", sub);

            // What the button offers depends on what happened and on the key's delay now.
            _onAction = null;
            _action.IsVisible = !test;
            _action.IsEnabled = true;
            _action.Classes.Remove("primary");
            ToolTip.SetTip(_action, null);
            if (test)
                return;

            var now = svc.DelayFor(ev.Device, ev.Key);
            var device = ev.Device;
            var code = ev.Key;
            switch (ev.Kind)
            {
                case ChatterKind.Blocked:
                {
                    // Nobody lets go of a key and presses it again in under 10 ms: that was the switch, nothing to offer.
                    if (ev.GapMicros < ChatterEngine.HumanGapMicros)
                    {
                        _action.IsVisible = false;
                        break;
                    }

                    var lower = Lower(ev);
                    if (now <= lower)
                    {
                        _action.IsEnabled = false;
                        _action.Content = now == 0 ? "Never filtered" : $"Now {now} ms";
                        break;
                    }

                    _action.Content = "Was me";
                    ToolTip.SetTip(_action, $"Lower {key}'s delay to {lower} ms so a press like this gets through");
                    _onAction = () => svc.SetRule(device, code, lower);
                    break;
                }

                case ChatterKind.NotFiltered when ev.Why is Exempt.FilterOff or Exempt.KeyboardOff or Exempt.Paused:
                    _action.IsVisible = false;
                    break;

                default:
                {
                    var suggest = Math.Max(Suggest(ev), ev.Kind == ChatterKind.NotFiltered ? ev.DelayMs : 0);
                    if (now >= suggest)
                    {
                        _action.IsEnabled = false;
                        _action.Content = svc.RuleFor(device, code) is { } own ? $"In list · {own} ms" : $"Covered · {now} ms";
                        break;
                    }

                    _action.Content = $"Add to list · {suggest} ms";
                    if (ev.Kind == ChatterKind.Slipped)
                        _action.Classes.Add("primary");
                    ToolTip.SetTip(_action, $"Give {key} its own delay of {suggest} ms, so a bounce like this is blocked");
                    _onAction = () => svc.SetRule(device, code, suggest);
                    break;
                }
            }
        }
    }
}
