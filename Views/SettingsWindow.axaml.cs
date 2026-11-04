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
/// The whole interface: one small window. It is created when opened and thrown away when closed, so while the app sits
/// in the tray nothing of it exists and it uses no time at all.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly GuardService _svc;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _loading;
    private string? _dev;                 // the keyboard being edited; null = all keyboards (the default settings)
    private string _kbSignature = "";
    private Action? svcLearned;

    private DeviceProfile? Prof => _dev is null ? null : _svc.Settings.Devices.FirstOrDefault(d => d.Id == _dev);
    private int CurMs => Prof?.GlobalMs ?? _svc.Settings.GlobalMs;
    private Dictionary<int, int> CurRules => Prof?.Rules ?? _svc.Settings.Rules;

    /// <summary>Gives the chosen keyboard its own settings (copied from the defaults) the first time it is changed.</summary>
    private void EnsureProfile()
    {
        if (_dev is null || Prof is not null)
            return;
        var name = _svc.Backend.Seen.FirstOrDefault(d => d.Path == _dev)?.Name ?? _dev;
        _svc.Settings.Devices.Add(new DeviceProfile { Id = _dev, Name = name, GlobalMs = _svc.Settings.GlobalMs, Rules = new Dictionary<int, int>(_svc.Settings.Rules) });
        Ctl<Button>("KbResetButton").IsVisible = true;
    }

    public SettingsWindow()
    {
        InitializeComponent();
        _svc = null!;
    }

    public SettingsWindow(GuardService svc)
    {
        InitializeComponent();
        _svc = svc;
        Icon = SpadeIcon.CreateWindowIcon();
        Ctl<Path>("Spade").Data = SpadeIcon.Geometry;

        Load();
        Wire();
        UpdateStatus();

        svc.Changed += OnServiceChanged;
        svc.LearnedChanged += OnLearned;
        _timer.Tick += (_, _) =>
        {
            Ctl<TextBlock>("BlockedText").Text = BlockedLine();
            RefreshKeyboards();
            RefreshCaught();
        };
        _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            svc.Changed -= OnServiceChanged;
            svc.LearnedChanged -= OnLearned;
        };
    }

    private T Ctl<T>(string name) where T : Control => this.FindControl<T>(name)!;

    private void OnLearned()
    {
        // Auto-adjust changed some delays: show them, unless the box being edited is one of them.
        var was = _loading;
        _loading = true;
        svcLearned?.Invoke();
        _loading = was;
    }

    private void OnServiceChanged() => Dispatcher.UIThread.Post(UpdateStatus);

    private string BlockedLine() =>
        _svc.Engine.TotalBlocked == 0 ? "nothing blocked yet" : $"{_svc.Engine.TotalBlocked:N0} blocked since the app started";

    // ---- Settings → controls -------------------------------------------------------------------------------------

    private void Load()
    {
        var s = _svc.Settings;
        _loading = true;
        Ctl<ToggleSwitch>("FilterSwitch").IsChecked = s.Enabled;
        Ctl<Slider>("MsSlider").Value = Math.Clamp(CurMs, 5, 100);
        Ctl<TextBlock>("MsText").Text = $"{CurMs} ms";
        Ctl<ToggleSwitch>("ModifiersSwitch").IsChecked = s.FilterModifiers;
        Ctl<ToggleSwitch>("HotkeySwitch").IsChecked = s.HotkeyEnabled;
        Ctl<ToggleSwitch>("AutoSwitch").IsChecked = s.AutoAdjust;
        Ctl<NumericUpDown>("AutoMax").Value = s.AutoMaxMs;
        Ctl<Grid>("AutoLimitRow").IsVisible = s.AutoAdjust;
        Ctl<ToggleSwitch>("AutostartSwitch").IsChecked = Autostart.IsEnabled();
        Ctl<TextBlock>("BlockedText").Text = BlockedLine();

        var keys = Ctl<ComboBox>("KeyBox");
        foreach (var (code, name) in KeyCatalog.All)
            keys.Items.Add(new ComboBoxItem { Content = name, Tag = code });

        BuildRules();
        BuildDevices();
        RefreshKeyboards();
        RefreshCaught();
        _loading = false;
    }

    // ---- Which keyboard -------------------------------------------------------------------------------------------

    private void RefreshKeyboards()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var all = new Dictionary<string, string>();
        foreach (var d in _svc.Settings.Devices)
            all[d.Id] = d.Name;
        foreach (var d in _svc.Backend.Seen)
            all[d.Path] = d.Name;

        var signature = string.Join("|", all.OrderBy(k => k.Key).Select(k => k.Key + k.Value));
        Ctl<Border>("KbCard").IsVisible = all.Count > 0;
        if (signature == _kbSignature)
            return;
        _kbSignature = signature;

        var was = _loading;
        _loading = true;
        var box = Ctl<ComboBox>("KbBox");
        box.Items.Clear();
        box.Items.Add(new ComboBoxItem { Content = "All keyboards (default)", Tag = "" });
        foreach (var (id, name) in all.OrderBy(k => k.Value))
            box.Items.Add(new ComboBoxItem { Content = name, Tag = id });
        box.SelectedIndex = Math.Max(0, box.Items.OfType<ComboBoxItem>().ToList().FindIndex(i => (string?)i.Tag == (_dev ?? "")));
        _loading = was;
    }

    /// <summary>Shows the settings of the keyboard just chosen.</summary>
    private void SwitchKeyboard(string? id)
    {
        _dev = string.IsNullOrEmpty(id) ? null : id;
        _loading = true;
        Ctl<Slider>("MsSlider").Value = Math.Clamp(CurMs, 5, 100);
        Ctl<TextBlock>("MsText").Text = $"{CurMs} ms";
        Ctl<Grid>("KbOnRow").IsVisible = _dev is not null;
        Ctl<ToggleSwitch>("KbOnSwitch").IsChecked = Prof?.Enabled ?? true;
        Ctl<Button>("KbResetButton").IsVisible = Prof is not null;
        BuildRules();
        _caughtRows.Clear();
        Ctl<StackPanel>("CaughtList").Children.Clear();
        RefreshCaught();
        _loading = false;
    }

    private void Changed()
    {
        _svc.Settings.Save();
        _svc.ApplyConfig();
    }

    private void Wire()
    {
        Ctl<ToggleSwitch>("FilterSwitch").IsCheckedChanged += (_, _) =>
        {
            if (!_loading)
                _svc.SetEnabled(Ctl<ToggleSwitch>("FilterSwitch").IsChecked == true);
        };
        Ctl<Slider>("MsSlider").ValueChanged += (_, e) =>
        {
            Ctl<TextBlock>("MsText").Text = $"{(int)e.NewValue} ms";
            if (_loading)
                return;
            EnsureProfile();
            if (Prof is { } p)
                p.GlobalMs = (int)e.NewValue;
            else
                _svc.Settings.GlobalMs = (int)e.NewValue;
            Changed();
        };

        Bind("ModifiersSwitch", v => _svc.Settings.FilterModifiers = v);
        Bind("HotkeySwitch", v => _svc.Settings.HotkeyEnabled = v);
        Bind("AutoSwitch", v =>
        {
            _svc.Settings.AutoAdjust = v;
            Ctl<Grid>("AutoLimitRow").IsVisible = v;
        });
        Ctl<NumericUpDown>("AutoMax").ValueChanged += (_, e) =>
        {
            if (_loading || e.NewValue is not { } v)
                return;
            _svc.Settings.AutoMaxMs = (int)v;
            Changed();
        };
        svcLearned = () => { BuildRules(); _caughtRows.Clear(); Ctl<StackPanel>("CaughtList").Children.Clear(); };

        Ctl<ToggleSwitch>("AutostartSwitch").IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            try
            {
                Autostart.Set(Ctl<ToggleSwitch>("AutostartSwitch").IsChecked == true);
            }
            catch (Exception ex)
            {
                Ctl<TextBlock>("AutostartNote").Text = "Couldn't change that: " + ex.Message;
            }
        };

        Ctl<ComboBox>("KbBox").SelectionChanged += (_, _) =>
        {
            if (!_loading && Ctl<ComboBox>("KbBox").SelectedItem is ComboBoxItem { Tag: string id })
                SwitchKeyboard(id);
        };
        Ctl<ToggleSwitch>("KbOnSwitch").IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            EnsureProfile();
            if (Prof is { } p)
                p.Enabled = Ctl<ToggleSwitch>("KbOnSwitch").IsChecked == true;
            Changed();
        };
        Ctl<Button>("KbResetButton").Click += (_, _) =>
        {
            _svc.Settings.Devices.RemoveAll(d => d.Id == _dev);
            Changed();
            SwitchKeyboard(_dev);
        };

        Ctl<Button>("AddButton").Click += (_, _) => AddRule();
        Ctl<Button>("RetryButton").Click += (_, _) => _svc.StartBackend();
        Ctl<Button>("TestButton").Click += async (_, _) => await RunTestAsync();
    }

    private void Bind(string switchName, Action<bool> apply)
    {
        var sw = Ctl<ToggleSwitch>(switchName);
        sw.IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            apply(sw.IsChecked == true);
            Changed();
        };
    }

    // ---- Status --------------------------------------------------------------------------------------------------

    private void UpdateStatus()
    {
        var problem = _svc.Problem;
        string pill, border, dot;
        if (problem is not null) { pill = "#33141A"; border = "#7A2A33"; dot = "#F2666A"; }
        else if (_svc.IsFiltering) { pill = "#0C2A1D"; border = "#146B44"; dot = "#19F58C"; }
        else if (_svc.StatusText.StartsWith("Paused", StringComparison.Ordinal)) { pill = "#33260F"; border = "#5C4515"; dot = "#F5B14C"; }
        else { pill = "#1A1E27"; border = "#2A3140"; dot = "#8A92A6"; }

        Ctl<TextBlock>("StatusText").Text = _svc.StatusText;
        var p = Ctl<Border>("StatusPill");
        p.Background = new SolidColorBrush(Color.Parse(pill));
        p.BorderBrush = new SolidColorBrush(Color.Parse(border));
        Ctl<Ellipse>("StatusDot").Fill = new SolidColorBrush(Color.Parse(dot));

        _loading = true;
        Ctl<ToggleSwitch>("FilterSwitch").IsChecked = _svc.Settings.Enabled;
        _loading = false;

        Ctl<Border>("ProblemCard").IsVisible = problem is not null;
        Ctl<TextBlock>("ProblemText").Text = problem ?? "";
        Ctl<Border>("SetupCard").IsVisible = problem is not null && _svc.Backend.SetupHint is not null;
        Ctl<TextBox>("SetupText").Text = _svc.Backend.SetupHint ?? "";
        BuildDevices();
    }

    // ---- Exceptions ----------------------------------------------------------------------------------------------

    private void BuildRules()
    {
        var list = Ctl<StackPanel>("RulesList");
        list.Children.Clear();
        foreach (var (key, ms) in CurRules.OrderBy(kv => KeyCatalog.NameOf(kv.Key), StringComparer.OrdinalIgnoreCase).ToList())
        {
            var number = new NumericUpDown { Minimum = 0, Maximum = 250, Value = ms, FormatString = "0", Width = 130, MinHeight = 32 };
            var k = key;
            number.ValueChanged += (_, e) =>
            {
                if (_loading || e.NewValue is not { } v)
                    return;
                EnsureProfile();
                CurRules[k] = (int)v;
                Changed();
            };

            var remove = new Button { Content = "✕", Classes = { "ghost" }, MinHeight = 32, Padding = new Thickness(10, 2) };
            remove.Click += (_, _) =>
            {
                EnsureProfile();
                CurRules.Remove(k);
                Changed();
                BuildRules();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = KeyCatalog.NameOf(key), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(number, 1);
            row.Children.Add(number);
            var unit = new TextBlock { Text = ms == 0 ? "never filtered" : "ms", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, MinWidth = 76 };
            number.ValueChanged += (_, e) => unit.Text = (e.NewValue ?? 0) == 0 ? "never filtered" : "ms";
            Grid.SetColumn(unit, 2);
            row.Children.Add(unit);
            Grid.SetColumn(remove, 3);
            row.Children.Add(remove);
            list.Children.Add(row);
        }
    }

    private void AddRule()
    {
        if (Ctl<ComboBox>("KeyBox").SelectedItem is not ComboBoxItem { Tag: int code })
            return;
        EnsureProfile();
        CurRules[code] = (int)(Ctl<NumericUpDown>("NewMs").Value ?? 60);
        Changed();
        BuildRules();
    }

    // ---- Caught keys ---------------------------------------------------------------------------------------------

    private readonly Dictionary<int, (TextBlock Info, NumericUpDown Delay)> _caughtRows = new();

    private void RefreshCaught()
    {
        var caught = _svc.Engine.CaughtKeys(_dev);
        var list = Ctl<StackPanel>("CaughtList");

        // Rebuild only when a new key shows up, so a box being edited is never pulled out from under the user.
        if (list.Children.Count == 0 || caught.Count != _caughtRows.Count || caught.Any(c => !_caughtRows.ContainsKey(c.Key)))
        {
            list.Children.Clear();
            _caughtRows.Clear();
            foreach (var c in caught)
            {
                var key = c.Key;
                var info = new TextBlock { Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
                var effective = CurRules.TryGetValue(key, out var own) ? own : CurMs;
                var delay = new NumericUpDown { Minimum = 0, Maximum = 250, Value = effective, FormatString = "0", Width = 130, MinHeight = 32 };
                delay.ValueChanged += (_, e) =>
                {
                    if (_loading || e.NewValue is not { } v)
                        return;
                    EnsureProfile();
                    CurRules[key] = (int)v;
                    Changed();
                    BuildRules();
                };

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = KeyCatalog.NameOf(key), VerticalAlignment = VerticalAlignment.Center });
                Grid.SetColumn(info, 1);
                row.Children.Add(info);
                Grid.SetColumn(delay, 2);
                row.Children.Add(delay);
                var ms = new TextBlock { Text = "ms", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ms, 3);
                row.Children.Add(ms);
                list.Children.Add(row);
                _caughtRows[key] = (info, delay);
            }

            if (caught.Count == 0)
                list.Children.Add(new TextBlock { Text = "Nothing caught yet.", Classes = { "muted" }, FontSize = 12 });
        }

        foreach (var c in caught)
            _caughtRows[c.Key].Info.Text = c.Count > 0
                ? $"blocked {c.Count}× (up to {c.LongestGapMs} ms)" + (c.ShortestPassedMs > 0 ? $" · closest let through {c.ShortestPassedMs} ms" : "")
                : $"none blocked · closest re-press {c.ShortestPassedMs} ms";
    }

    // ---- Linux: choose keyboards ---------------------------------------------------------------------------------

    private void BuildDevices()
    {
        var devices = _svc.Backend.Devices;
        var card = Ctl<Border>("DevicesCard");
        card.IsVisible = devices.Count > 0;
        var list = Ctl<StackPanel>("DeviceList");
        if (devices.Count == 0 || list.Children.Count == devices.Count)
            return;

        list.Children.Clear();
        var chosen = _svc.Settings.LinuxDevices;
        foreach (var d in devices)
        {
            var box = new CheckBox { Content = d.Name, IsChecked = chosen.Count == 0 || chosen.Contains(d.Name) };
            box.IsCheckedChanged += (_, _) => SaveDeviceSelection();
            list.Children.Add(box);
        }
    }

    private void SaveDeviceSelection()
    {
        var boxes = Ctl<StackPanel>("DeviceList").Children.OfType<CheckBox>().ToList();
        var ticked = boxes.Where(b => b.IsChecked == true).Select(b => (string)b.Content!).ToList();
        // Everything ticked means "all keyboards", which also covers keyboards plugged in later.
        _svc.Settings.LinuxDevices = ticked.Count == boxes.Count ? new List<string>() : ticked;
        Changed();
    }

    // ---- Try it --------------------------------------------------------------------------------------------------

    private async Task RunTestAsync()
    {
        var result = Ctl<TextBlock>("TestResult");
        var pad = Ctl<TextBox>("TestPad");

        // The test types real keystrokes into whatever window is in front, so make sure that is this one.
        if (!IsActive)
        {
            result.Text = "Bring this window to the front first.";
            return;
        }

        pad.Text = "";
        pad.Focus();
        await Task.Delay(150);

        if (!_svc.Backend.SendSelfTest(0x2D, 8))
        {
            result.Text = "The filter isn't running, so there is nothing to test.";
            return;
        }

        result.Text = "Testing…";
        await Task.Delay(900);
        var typed = (pad.Text ?? "").Count(c => c is 'x' or 'X');

        result.Text = _svc.Engine.Active
            ? typed == 1 ? "✓ Working: the bounce typed one x." : typed == 0 ? "Nothing typed. Click in the box and retry." : $"{typed} x typed. Try raising the sensitivity."
            : typed >= 2 ? "Filter is off, so the bounce typed x twice. That is chatter." : "Filter is off or paused.";
    }
}
