using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using pxdChatterGuard.Core;
using pxdChatterGuard.Services;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Path = Avalonia.Controls.Shapes.Path;

namespace pxdChatterGuard.Views;

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
        // A security key (YubiKey) starts out left alone, as it is without settings of its own.
        _svc.Settings.Devices.Add(new DeviceProfile { Id = _dev, Name = name, Enabled = !GuardConfig.IsTokenDevice(_dev), GlobalMs = _svc.Settings.GlobalMs, Rules = DeviceProfile.KeysOnly(_svc.Settings.Rules) });
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
        Chrome.UseBrandMark(Ctl<Path>("Spade"));
        if (OperatingSystem.IsWindows() && Platform.Elevation.IsElevated)
            Title = "pxd Chatter Guard (administrator)";

        Load();
        Wire();
        UpdateStatus();

        svc.Changed += OnServiceChanged;
        svc.RulesChanged += OnLearned;
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
            svc.RulesChanged -= OnLearned;
        };

        ShowTrayIconState(pxdStandard.pxdLink.TrayIconVisible(Chrome.AppId));
        Ctl<ToggleSwitch>("TraySwitch").IsCheckedChanged += (_, _) =>
        {
            if (_loading)
                return;
            var v = Ctl<ToggleSwitch>("TraySwitch").IsChecked == true;
            if (SetTrayIcon is { } set)
                set(v);
            else
                pxdStandard.pxdLink.SetTrayIconVisible(Chrome.AppId, v);
            ShowTrayIconState(v);
        };
        Ctl<Button>("QuitButton").Click += (_, _) =>
        {
            if (QuitApp is { } quit)
                quit();
            else
                Close();
        };

        Chrome.Apply(this, "settings");
    }

    /// <summary>Shows or hides the tray icon (the app does that, and writes toolkit.json so pxd Toolkit agrees).</summary>
    public Action<bool>? SetTrayIcon { get; init; }

    /// <summary>Quits the whole app, filter and all.</summary>
    public Action? QuitApp { get; init; }

    /// <summary>Puts the "Show tray icon" switch in line with toolkit.json (changed here or from pxd Toolkit).</summary>
    public void ShowTrayIconState(bool visible)
    {
        var was = _loading;
        _loading = true;
        Ctl<ToggleSwitch>("TraySwitch").IsChecked = visible;
        Ctl<TextBlock>("TrayNote").Text = visible
            ? "Also set from pxd Toolkit"
            : "Hidden. Start the app again or use pxd Toolkit to open this window";
        _loading = was;
    }

    private T Ctl<T>(string name) where T : Control => this.FindControl<T>(name)!;

    /// <summary>Opens the chatter monitor (the app owns that window).</summary>
    public Action? OpenMonitor { get; init; }

    /// <summary>Restarts the app as administrator (the app does that); null once it did, otherwise why not.</summary>
    public Func<Task<string?>>? RestartAsAdmin { get; init; }

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
        Ctl<ToggleSwitch>("HoldSafeSwitch").IsChecked = s.HoldSafe;
        Ctl<Grid>("HoldSafeRow").IsVisible = _svc.Backend.SupportsHoldSafe;
        Ctl<Border>("MouseCard").IsVisible = _svc.Backend.SupportsMouse;
        Ctl<ToggleSwitch>("MouseSwitch").IsChecked = s.FilterMouse;
        Ctl<Slider>("MouseSlider").Value = Math.Clamp(s.MouseMs, 5, 150);
        Ctl<TextBlock>("MouseMsText").Text = $"{s.MouseMs} ms";
        ShowMouse();
        Ctl<Border>("PauseCard").IsVisible = OperatingSystem.IsWindows();
        BuildPrograms();
        Ctl<ToggleSwitch>("HotkeySwitch").IsChecked = s.HotkeyEnabled;
        Ctl<ToggleSwitch>("AutoSwitch").IsChecked = s.AutoAdjust;
        Ctl<NumericUpDown>("AutoMax").Value = s.AutoMaxMs;
        Ctl<Grid>("AutoLimitRow").IsVisible = s.AutoAdjust;
        ShowAutostart(null);
        Ctl<TextBlock>("BlockedText").Text = BlockedLine();

        BuildKeyBox();
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
        Ctl<ToggleSwitch>("KbOnSwitch").IsChecked = Prof?.Enabled ?? !GuardConfig.IsTokenDevice(_dev);
        Ctl<Button>("KbResetButton").IsVisible = Prof is not null;
        BuildKeyBox();
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
        Bind("HoldSafeSwitch", v => _svc.Settings.HoldSafe = v);
        Bind("MouseSwitch", v =>
        {
            _svc.Settings.FilterMouse = v;
            ShowMouse();
            BuildKeyBox();
        });
        Ctl<Slider>("MouseSlider").ValueChanged += (_, e) =>
        {
            Ctl<TextBlock>("MouseMsText").Text = $"{(int)e.NewValue} ms";
            if (_loading)
                return;
            _svc.Settings.MouseMs = (int)e.NewValue;
            Changed();
        };

        if (OperatingSystem.IsWindows())
        {
            var programs = Ctl<ComboBox>("ProgramBox");
            programs.DropDownOpened += (_, _) =>
            {
                if (!OperatingSystem.IsWindows())
                    return;

                // Listed fresh each time, so a game started a moment ago is there.
                var chosen = programs.SelectedItem as string;
                programs.Items.Clear();
                foreach (var name in Platform.ForegroundWatcher.RunningPrograms()
                             .Where(n => !_svc.Settings.PausedPrograms.Contains(n, StringComparer.OrdinalIgnoreCase)))
                    programs.Items.Add(name);
                if (chosen is not null && programs.Items.Contains(chosen))
                    programs.SelectedItem = chosen;
            };
            Ctl<Button>("AddProgramButton").Click += (_, _) =>
            {
                if (programs.SelectedItem is not string name
                    || _svc.Settings.PausedPrograms.Contains(name, StringComparer.OrdinalIgnoreCase))
                    return;
                _svc.Settings.PausedPrograms.Add(name);
                programs.Items.Remove(name);
                programs.SelectedItem = null;
                Changed();
                BuildPrograms();
            };
        }
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

        Ctl<ToggleSwitch>("AutostartSwitch").IsCheckedChanged += async (_, _) =>
        {
            if (!_loading)
                await ChangeAutostartAsync(Ctl<ToggleSwitch>("AutostartSwitch").IsChecked == true);
        };
        Ctl<Button>("AutostartAdminButton").Click += async (_, _) => await ChangeAutostartAsync(true);

        Ctl<Button>("AdminRestartButton").Click += async (_, _) =>
        {
            if (RestartAsAdmin is null)
                return;
            var button = Ctl<Button>("AdminRestartButton");
            button.IsEnabled = false;
            var error = await RestartAsAdmin(); // on success the app quits and this window with it
            button.IsEnabled = true;
            if (error is not null)
                Ctl<TextBlock>("AdminNote").Text = error;
        };
        Ctl<Button>("AdminHideButton").Click += (_, _) => _svc.HideElevatedNotice();

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
        Ctl<Button>("MonitorButton").Click += (_, _) => OpenMonitor?.Invoke();
        Ctl<Button>("RetryButton").Click += (_, _) => _svc.StartBackend();
        Ctl<Button>("TestButton").Click += async (_, _) => await RunTestAsync();
    }

    /// <summary>Switches starting at sign-in, off the UI thread: removing a start as administrator can make Windows ask first.</summary>
    private async Task ChangeAutostartAsync(bool on)
    {
        var toggle = Ctl<ToggleSwitch>("AutostartSwitch");
        var admin = Ctl<Button>("AutostartAdminButton");
        toggle.IsEnabled = admin.IsEnabled = false;
        string? error = null;
        try
        {
            await Task.Run(() => Autostart.Set(on));
        }
        catch (Exception ex)
        {
            error = "Couldn't change that: " + ex.Message;
        }

        toggle.IsEnabled = admin.IsEnabled = true;
        ShowAutostart(error);
    }

    /// <summary>Shows how starting at sign-in is set up now (or why changing it didn't work).</summary>
    private void ShowAutostart(string? error)
    {
        var mode = Autostart.Mode();
        var elevated = OperatingSystem.IsWindows() && Platform.Elevation.IsElevated;
        var was = _loading;
        _loading = true;
        Ctl<ToggleSwitch>("AutostartSwitch").IsChecked = mode != AutostartMode.Off;
        _loading = was;

        // Running as administrator, but set to start without those rights: offer to start as administrator instead.
        Ctl<Button>("AutostartAdminButton").IsVisible = error is null && elevated && mode == AutostartMode.On;
        // The start-as-administrator task from before 1.4.0 can only be moved to the new name by a copy running as administrator.
        var formerTask = !elevated && Autostart.FormerTaskPending();
        Ctl<TextBlock>("AutostartNote").Text = error ?? (mode, elevated) switch
        {
            (AutostartMode.AsAdministrator, false) when formerTask =>
                "Starts as administrator through the task an earlier version made. Restart as administrator once to update it",
            (AutostartMode.AsAdministrator, _) or (AutostartMode.Off, true) => "Runs quietly in the tray, as administrator, without asking",
            (AutostartMode.On, true) => "Starts without administrator rights",
            _ => "Runs quietly in the tray",
        };
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
        // A program that runs as administrator is in front, so nothing typed into it reaches the filter.
        var outOfReach = problem is null && _svc.Engine.Enabled && _svc.PausedFor is null ? _svc.ElevatedFront : null;
        string pill, border, dot;
        if (problem is not null) { pill = "#33141A"; border = "#7A2A33"; dot = "#F2666A"; }
        else if (_svc.IsFiltering) { pill = "#0C2A1D"; border = "#146B44"; dot = "#19F58C"; }
        else if (outOfReach is not null || _svc.StatusText.StartsWith("Paused", StringComparison.Ordinal)) { pill = "#33260F"; border = "#5C4515"; dot = "#F5B14C"; }
        else { pill = "#1A1E27"; border = "#2A3140"; dot = "#8A92A6"; }

        // The pill only has room for a few words; why goes in its tooltip (and the card below).
        Ctl<TextBlock>("StatusText").Text = outOfReach is not null ? $"Not filtering in {outOfReach}" : _svc.StatusText;
        var p = Ctl<Border>("StatusPill");
        ToolTip.SetTip(p, outOfReach is not null ? $"{outOfReach} runs as administrator, so Windows keeps the keys typed into it from this app." : null);
        p.Background = new SolidColorBrush(Color.Parse(pill));
        p.BorderBrush = new SolidColorBrush(Color.Parse(border));
        Ctl<Ellipse>("StatusDot").Fill = new SolidColorBrush(Color.Parse(dot));

        // Offer to restart as administrator once such a program has been in front (it stays, since this window being in
        // front means that program no longer is).
        var notice = OperatingSystem.IsWindows() && !Platform.Elevation.IsElevated ? _svc.ElevatedNotice : null;
        Ctl<Border>("AdminCard").IsVisible = notice is not null;
        if (notice is not null)
            Ctl<TextBlock>("AdminText").Text =
                $"{notice} runs as administrator. Windows doesn't let a program without administrator rights see the keys typed into it, so while {notice} is in front, nothing typed into it is filtered.";

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
            var number = new NumericUpDown { Minimum = 0, Maximum = 250, Value = ms, FormatString = "0", Width = 104, MinHeight = 32 };
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

    /// <summary>Whether mouse buttons are filtered (Windows), so their delay and exceptions are worth showing.</summary>
    private bool MouseOn => _svc.Backend.SupportsMouse && _svc.Settings.FilterMouse;

    /// <summary>
    /// The keys an exception can be added for: every key, plus the mouse buttons while those are filtered and the default
    /// list is shown (buttons always follow the default list, never a keyboard's own).
    /// </summary>
    private void BuildKeyBox()
    {
        var box = Ctl<ComboBox>("KeyBox");
        var chosen = (box.SelectedItem as ComboBoxItem)?.Tag;
        box.Items.Clear();
        var keys = _dev is null && MouseOn ? KeyCatalog.All.Concat(KeyCatalog.MouseButtons) : KeyCatalog.All;
        foreach (var (code, name) in keys)
            box.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => Equals(i.Tag, chosen));
        box.PlaceholderText = _dev is null && MouseOn ? "Choose a key or button…" : "Choose a key…";
    }

    /// <summary>Shows the mouse delay, and what protecting held keys does for buttons, only while buttons are filtered.</summary>
    private void ShowMouse()
    {
        Ctl<StackPanel>("MousePanel").IsVisible = MouseOn;
        Ctl<TextBlock>("HoldSafeMouseNote").IsVisible = MouseOn;
    }

    // ---- Caught keys ---------------------------------------------------------------------------------------------

    private sealed record CaughtRow(TextBlock Info, NumericUpDown Delay, Button Suggest, TextBlock Fits);

    private readonly Dictionary<int, CaughtRow> _caughtRows = new();

    /// <summary>A non-breaking space: keeps a number with its unit when a line wraps.</summary>
    private const char Nbsp = (char)0xA0;

    /// <summary>
    /// The delay a key really gets in the list being edited (Shift, Ctrl, Alt and Win get none unless filtered; mouse
    /// buttons get the mouse delay while they are filtered, none otherwise).
    /// </summary>
    private int EffectiveDelay(int key) =>
        CurRules.TryGetValue(key, out var own) ? own
        : KeyCatalog.IsMouseButton(key) ? MouseOn ? _svc.Settings.MouseMs : 0
        : KeyCatalog.IsModifier(key) && !_svc.Settings.FilterModifiers ? 0
        : CurMs;

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
                var info = new TextBlock { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right };
                var delay = new NumericUpDown { Minimum = 0, Maximum = 250, Value = EffectiveDelay(key), FormatString = "0", Width = 104, MinHeight = 32, VerticalAlignment = VerticalAlignment.Center };
                delay.ValueChanged += (_, e) =>
                {
                    if (_loading || e.NewValue is not { } v)
                        return;
                    EnsureProfile();
                    CurRules[key] = (int)v;
                    Changed();
                    BuildRules();
                    RefreshCaught();
                };

                // The suggested delay: a button that applies it, or a tick when the key already has it.
                var suggest = new Button { Classes = { "suggest" }, HorizontalAlignment = HorizontalAlignment.Right };
                suggest.Click += (_, _) =>
                {
                    if (suggest.Tag is not int ms)
                        return;
                    var was = _loading;
                    _loading = true;
                    delay.Value = ms;
                    _loading = was;
                    EnsureProfile();
                    CurRules[key] = ms;
                    Changed();
                    BuildRules();
                    RefreshCaught();
                };
                var fits = new TextBlock { FontSize = 11, Foreground = new SolidColorBrush(Color.Parse("#19F58C")), HorizontalAlignment = HorizontalAlignment.Right };
                var details = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { info, suggest, fits } };

                // The key's name keeps its room; the details wrap rather than push it out.
                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), ColumnSpacing = 8 };
                row.Children.Add(new TextBlock { Text = KeyCatalog.NameOf(key), VerticalAlignment = VerticalAlignment.Center, MinWidth = 64 });
                Grid.SetColumn(details, 1);
                row.Children.Add(details);
                Grid.SetColumn(delay, 2);
                row.Children.Add(delay);
                var ms = new TextBlock { Text = "ms", Classes = { "muted" }, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(ms, 3);
                row.Children.Add(ms);
                list.Children.Add(row);
                _caughtRows[key] = new CaughtRow(info, delay, suggest, fits);
            }

            if (caught.Count == 0)
                list.Children.Add(new TextBlock { Text = "Nothing caught yet.", Classes = { "muted" }, FontSize = 12 });
        }

        foreach (var c in caught)
        {
            var row = _caughtRows[c.Key];
            var current = EffectiveDelay(c.Key);
            var best = ChatterEngine.SuggestDelayMs(current, c.ChatterMaxMicros, c.HumanNextMicros, out var tight);
            var why = SuggestionReason(c, current, best, tight);
            row.Suggest.IsVisible = best != current;
            row.Suggest.Tag = best;
            row.Suggest.Content = $"Suggested {best}{Nbsp}ms · use";
            row.Fits.IsVisible = best == current;
            row.Fits.Text = best == 0 ? "✓ best left unfiltered" : $"✓ {best}{Nbsp}ms is best";
            ToolTip.SetTip(row.Suggest, why);
            ToolTip.SetTip(row.Fits, why);

            // One short line of what happened; the full story is in the chatter monitor.
            var parts = new List<string>();
            if (c.Count > 0)
                parts.Add($"{c.Count} blocked, up to {c.LongestGapMs}{Nbsp}ms");
            if (c.Slipped > 0)
                parts.Add($"{c.Slipped} slipped");
            if (c.ShortestPassedMs > 0)
                parts.Add($"closest {c.ShortestPassedMs}{Nbsp}ms");
            row.Info.Text = string.Join(" · ", parts);
        }
    }

    /// <summary>Why a delay is suggested for a key, in a sentence or two (the tooltip on the suggestion).</summary>
    private static string SuggestionReason(ChatterEngine.CaughtKey c, int current, int best, bool tight)
    {
        var seen = new List<string>();
        if (c.ChatterMaxMicros > 0)
            seen.Add($"its chatter came up to {MonitorWindow.Ms(c.ChatterMaxMicros)} after letting go");
        if (c.HumanNextMicros > 0)
            seen.Add($"your two quickest real re-presses of it were {MonitorWindow.Ms(c.HumanMinMicros)} and {MonitorWindow.Ms(c.HumanNextMicros)}");
        else if (c.HumanMinMicros > 0)
            seen.Add($"you re-pressed it once {MonitorWindow.Ms(c.HumanMinMicros)} after letting go (once could be a fluke, so that alone never lowers the delay)");
        if (seen.Count == 0)
            return "Nothing telling yet: keep the current delay and look again later.";

        var what = "So far " + string.Join(", and ", seen) + ". ";
        if (tight)
            return what + $"These overlap: {best} ms blocks the chatter but may also catch your very quickest presses.";
        if (best > current)
            return what + $"{best} ms blocks that chatter with some room to spare.";
        if (best < current)
            return what + (c.ChatterMaxMicros > 0
                ? $"{best} ms still blocks the chatter and lets your quick presses through."
                : $"{best} ms lets your quick presses through.");
        return what + "The current delay already fits.";
    }

    // ---- Pause for programs (Windows) ---------------------------------------------------------------------------

    private void BuildPrograms()
    {
        var list = Ctl<StackPanel>("ProgramList");
        list.Children.Clear();
        foreach (var name in _svc.Settings.PausedPrograms.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList())
        {
            var remove = new Button { Content = "✕", Classes = { "ghost" }, MinHeight = 30, Padding = new Thickness(10, 2) };
            var n = name;
            remove.Click += (_, _) =>
            {
                _svc.Settings.PausedPrograms.RemoveAll(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase));
                Changed();
                BuildPrograms();
            };

            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(remove, 1);
            row.Children.Add(remove);
            list.Children.Add(row);
        }

        if (_svc.Settings.PausedPrograms.Count == 0)
            list.Children.Add(new TextBlock { Text = "No programs: filtering never pauses.", Classes = { "muted" }, FontSize = 12 });
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
