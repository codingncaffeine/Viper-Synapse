using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Input;

namespace ViperSynapse.App.ViewModels;

/// <summary>One row of the key list: the key's number and what it does now.</summary>
public sealed partial class KeyRowViewModel(DeviceKey key) : ObservableObject
{
    public DeviceKey Key { get; } = key;
    public string Number => Key.Number.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Controls with no key of their own (the Tartarus's Hypershift button) are handled by the device.</summary>
    public bool Bindable => Key.Source != 0;

    /// <summary>Set on the first key of each group: the list shows it as a heading (KEYPAD, OTHER, SIDE BUTTONS).</summary>
    public string? GroupTitle { get; set; }

    [ObservableProperty]
    public partial string Category { get; set; } = "";

    /// <summary>The list's shorter caption: KEYBOARD, not KEYBOARD FUNCTION, as Synapse 4 prints it there.</summary>
    public string ListCategory => Category.EndsWith(" FUNCTION", StringComparison.Ordinal) ? Category[..^" FUNCTION".Length] : Category;

    partial void OnCategoryChanged(string value) => OnPropertyChanged(nameof(ListCategory));

    /// <summary>The key whose binding is open: its cell in the list is highlighted.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    /// <summary>Changed from what the key does by itself: shown green, as Synapse does.</summary>
    [ObservableProperty]
    public partial bool Changed { get; set; }
}

/// <summary>A keymap in the dropdown, with the colour Synapse gives it (the keypad's keymap light).</summary>
public sealed record KeymapItem(int Index, string Name)
{
    static readonly string[] Colors = ["#F2A33A", "#E5484D", "#44D62C", "#3D8BFD", "#2EC4D6", "#C04BD8", "#F2D43A", "#E6E6E6"];

    static readonly Avalonia.Media.IBrush[] Brushes = Colors.Select(c => (Avalonia.Media.IBrush)new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(c))).ToArray();

    // Computed, not stored: a record compares its fields, and a stored brush would make the same keymap from two
    // loads unequal, so the box would drop its selection each time the settings are read again.
    public Avalonia.Media.IBrush Color => Brushes[Index % Brushes.Length];

    public override string ToString() => Name;
}

public sealed record KeyGroup(string Title, IReadOnlyList<KeyRowViewModel> Keys);

/// <summary>A line of the key list: a group heading, or one key or two side by side (Synapse 4 runs a long group
/// down two columns).</summary>
public sealed record KeyListLine(string? Title, KeyRowViewModel? Left, KeyRowViewModel? Right);

/// <summary>A button the device's firmware handles itself (the keypad's Hypershift, keymap and profile buttons): listed
/// with what it does, not remappable.</summary>
public sealed record FirmwareButtonLine(string Category, string Value, string Where = "Keypad");

/// <summary>A device tab: its pages (Customize, Lighting, ...), profiles, keymaps and the key list.</summary>
public sealed partial class DeviceViewModel : TabViewModel
{
    readonly MainViewModel _main;
    DeviceSettings _settings = DeviceSettings.Create();
    bool _loading;

    public DeviceViewModel(DeviceInfo info, MainViewModel main)
    {
        _main = main;
        Info = info;
        Model = DeviceCatalogue.Default.Find(Convert.ToUInt16(info.ProductId, 16));
        Rows = (Model?.Keys ?? []).Select(k => new KeyRowViewModel(k)).ToList();
        Groups = Rows.GroupBy(r => r.Key.Group).Select(g => new KeyGroup(GroupTitle(g.Key), g.ToList())).ToList();
        foreach (var group in Groups)
            group.Keys[0].GroupTitle = group.Title;
        KeyFilters = ["All Buttons", .. Groups.Select(g => FilterName(g.Title))];
        SelectedKeyFilter = KeyFilters[0];
        if (Model?.Has("keypad_buttons") == true)
            FirmwareButtons = [new("RAZER HYPERSHIFT", "Razer Hypershift"), new("SWITCH KEYMAP", "Cycle Keymap"), new("SWITCH PROFILE", "Cycle Profile")];
        else
            // A mouse's firmware-only buttons are catalogue keys with no key code (the Basilisk V4 Pro's DPI, profile,
            // scroll-mode, clutch and M1 buttons): listed under the others with what they do.
            FirmwareButtons = Rows.Where(r => !r.Bindable).Select(r => new FirmwareButtonLine(FirmwareCategory(r.Key.Label), r.Key.Label, "Mouse")).ToList();
        Lighting = new LightingViewModel(Model, l => PushProfile(p => p with { Lighting = l }));
        Lighting.ApplyToOthers += effect =>
        {
            foreach (var other in _main.Devices.Where(d => d != this))
                other.Lighting.Take(effect);
        };
        Performance = new PerformanceViewModel(Model, p => PushProfile(x => x with { Performance = p }));
        Power = new PowerViewModel(p => PushProfile(x => x with { Power = p }));
        Calibration = new CalibrationViewModel(c => PushProfile(x => x with { Calibration = c }));
        ScrollWheel = new ScrollWheelViewModel(s => PushProfile(x => x with { Scroll = s }));
        Pages = BuildPages();
        SelectedPage = Pages[0];
        if (HasThumbstick)
        {
            var watch = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            watch.Tick += (_, _) => KeepWatchingStick();
            watch.Start();
        }
        if (HasBattery)
        {
            // The charge changes on its own: read it again every minute, battery only, so a page being edited is left be.
            var battery = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(1) };
            battery.Tick += (_, _) => _ = RefreshBatteryAsync();
            battery.Start();
        }
        _ = Services.DeviceImages.LoadAsync(Model).ContinueWith(t =>
        {
            if (t.Result is { } photo)
                Dispatcher.UIThread.Post(() => Photo = photo);
        }, TaskScheduler.Default);
    }

    /// <summary>The model's photo from Razer's servers, once fetched; null until then or when there is none.</summary>
    [ObservableProperty]
    public partial Avalonia.Media.Imaging.Bitmap? Photo { get; set; }

    /// <summary>Customize shows the photo instead of the drawing (the drawing is the one you can click).</summary>
    [ObservableProperty]
    public partial bool ShowPhoto { get; set; }

    public bool ShowDrawing => !ShowPhoto || Photo == null;

    partial void OnShowPhotoChanged(bool value) => OnPropertyChanged(nameof(ShowDrawing));

    partial void OnPhotoChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(ShowDrawing));

    [ObservableProperty]
    public partial DeviceInfo Info { get; set; }

    public DeviceModel? Model { get; }
    public string Key => Info.Key;
    public override string Header => ShortName.ToUpperInvariant();
    public override string Title => (Model?.Name ?? Info.Name).ToUpperInvariant();
    public override TabIcon Icon => Model?.Type switch
    {
        "keypad" => TabIcon.Keypad,
        "mouse" => TabIcon.Mouse,
        "keyboard" => TabIcon.Keyboard,
        _ => TabIcon.Device,
    };

    /// <summary>The profile in use, shown in green on the dashboard card.</summary>
    public string ProfileName => SelectedProfile?.Name ?? "";

    partial void OnSelectedProfileChanged(Profile? oldValue, Profile? newValue) => OnPropertyChanged(nameof(ProfileName));

    /// <summary>The settings as last read from or sent to the service.</summary>
    public DeviceSettings CurrentSettings => _settings;

    /// <summary>Links a program to one of this device's profiles (taking it off the others), or to none.</summary>
    public void LinkProgram(string program, Guid? profileId)
    {
        var profiles = _settings.Profiles.Select(p =>
        {
            var programs = p.LinkedPrograms.Where(x => !string.Equals(x, program, StringComparison.OrdinalIgnoreCase)).ToList();
            if (p.Id == profileId)
                programs.Add(program);
            return p with { LinkedPrograms = programs };
        }).ToList();
        Push(_settings with { Profiles = profiles });
    }
    public string ShortName => (Model?.Name ?? Info.Name).Replace("Razer ", "", StringComparison.Ordinal);
    public bool IsKeypad => Model?.Type == "keypad";

    public IReadOnlyList<KeyRowViewModel> Rows { get; }
    public IReadOnlyList<KeyGroup> Groups { get; }

    /// <summary>The key list's "All Buttons" box: everything, or one group.</summary>
    public IReadOnlyList<string> KeyFilters { get; }

    [ObservableProperty]
    public partial string SelectedKeyFilter { get; set; } = "";

    partial void OnSelectedKeyFilterChanged(string value) => OnPropertyChanged(nameof(KeyLines));

    static string FilterName(string upper) => System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(upper.ToLowerInvariant());

    /// <summary>The key list as Synapse 4 lays it out: groups of more than six keys run down two columns (1-13 on the
    /// left, 14-27 on the right). Keys the firmware keeps for itself are left to <see cref="FirmwareButtons"/>.</summary>
    public IReadOnlyList<KeyListLine> KeyLines
    {
        get
        {
            var lines = new List<KeyListLine>();
            foreach (var group in Groups)
            {
                if (SelectedKeyFilter != KeyFilters[0] && SelectedKeyFilter != FilterName(group.Title))
                    continue;
                var keys = group.Keys.Where(k => k.Bindable || FirmwareButtons.Count == 0).ToList();
                if (keys.Count == 0)
                    continue;
                lines.Add(new KeyListLine(group.Title, null, null));
                // Synapse 4 runs a keypad's keys down two columns and a mouse's down one.
                if (keys.Count <= 6 || !IsKeypad)
                {
                    lines.AddRange(keys.Select(k => new KeyListLine(null, k, null)));
                    continue;
                }
                // An odd count leaves the extra key on the right, as Synapse puts 1-13 beside 14-27.
                int half = keys.Count / 2;
                for (int i = 0; i < keys.Count - half; i++)
                    lines.Add(new KeyListLine(null, i < half ? keys[i] : null, keys[half + i]));
            }
            return lines;
        }
    }

    public IReadOnlyList<FirmwareButtonLine> FirmwareButtons { get; } = [];

    public bool HasFirmwareButtons => FirmwareButtons.Count > 0;

    [RelayCommand]
    void SelectKey(KeyRowViewModel row) => SelectedKey = row;
    public IReadOnlyList<string> Pages { get; }
    public LightingViewModel Lighting { get; }
    public PerformanceViewModel Performance { get; }
    public PowerViewModel Power { get; }
    public ScrollWheelViewModel ScrollWheel { get; }

    /// <summary>Every mouse gets the Scroll Wheel card (scroll speed); acceleration and Smart-Reel need the firmware's.</summary>
    public bool IsMouse => Model?.Type == "mouse";

    public bool HasScrollFirmware => Model?.Has("scroll_wheel") == true;
    public CalibrationViewModel Calibration { get; }

    [ObservableProperty]
    public partial string SelectedPage { get; set; }

    public ObservableCollection<Profile> Profiles { get; } = [];

    [ObservableProperty]
    public partial Profile? SelectedProfile { get; set; }

    /// <summary>Replaced whole on every load: a combo box whose items are cleared under a two-way index binding
    /// loses its selection.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<KeymapItem> Keymaps { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedKeymap { get; set; }

    /// <summary>What the keymap box binds to. A box whose items are replaced sends null back; that is ignored.</summary>
    public KeymapItem? SelectedKeymapItem
    {
        get => SelectedKeymap >= 0 && SelectedKeymap < Keymaps.Count ? Keymaps[SelectedKeymap] : null;
        set
        {
            if (value != null)
                SelectedKeymap = value.Index;
        }
    }

    /// <summary>Synapse 4's remap menu: the key list beside the device, shown or hidden by the button next to
    /// Standard and Hypershift.</summary>
    [ObservableProperty]
    public partial bool ShowKeyList { get; set; } = true;

    [RelayCommand]
    void ToggleKeyList() => ShowKeyList = !ShowKeyList;

    [ObservableProperty]
    public partial bool Hypershift { get; set; }

    /// <summary>The Standard half of the layer switch.</summary>
    public bool Standard
    {
        get => !Hypershift;
        set => Hypershift = !value;
    }

    public bool IsCustomize => SelectedPage == "CUSTOMIZE";

    // ---- Customize cards (Synapse 4: thumbstick, polling rate, macro interruption) -----------------------

    public bool HasThumbstick => Model?.Has("thumbstick") == true;

    /// <summary>Keypads show their polling rate on Customize; mice have it on Performance.</summary>
    public bool HasPollingCard => Model?.Has("poll_rate") == true && Model?.Has("dpi") != true;

    [ObservableProperty] public partial ThumbstickMode StickMode { get; set; }
    [ObservableProperty] public partial double StickX { get; set; }
    [ObservableProperty] public partial double StickY { get; set; }
    [ObservableProperty] public partial MacroInterruption Interruption { get; set; }

    public bool StickAnalog { get => StickMode == ThumbstickMode.Analog; set { if (value) SetStick(ThumbstickMode.Analog); } }
    public bool StickFourWay { get => StickMode == ThumbstickMode.FourWay; set { if (value) SetStick(ThumbstickMode.FourWay); } }
    public bool StickEightWay { get => StickMode == ThumbstickMode.EightWay; set { if (value) SetStick(ThumbstickMode.EightWay); } }
    public string StickText => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"X: {Percent(StickX)}%    Y: {Percent(-StickY)}%");

    // An int has no negative zero, which a formatted double would show as "-0".
    static int Percent(double v) => (int)Math.Round(v * 100);
    public string StickHint => StickMode switch
    {
        ThumbstickMode.FourWay => "Four directions, bound like keys (THUMBSTICK in the key list). Diagonals pick the stronger direction.",
        ThumbstickMode.EightWay => "Eight directions, bound like keys (THUMBSTICK in the key list). Diagonals press two directions.",
        _ => "The stick is the analog stick of the keypad's gamepad, for games that take a controller.",
    };

    public bool InterruptDedicated { get => Interruption == MacroInterruption.DedicatedKey; set { if (value) SetInterruption(MacroInterruption.DedicatedKey); } }
    public bool InterruptAnyKey { get => Interruption == MacroInterruption.AnyKey; set { if (value) SetInterruption(MacroInterruption.AnyKey); } }

    partial void OnStickModeChanged(ThumbstickMode value)
    {
        OnPropertyChanged(nameof(StickAnalog));
        OnPropertyChanged(nameof(StickFourWay));
        OnPropertyChanged(nameof(StickEightWay));
        OnPropertyChanged(nameof(StickHint));
    }

    partial void OnStickXChanged(double value) => OnPropertyChanged(nameof(StickText));
    partial void OnStickYChanged(double value) => OnPropertyChanged(nameof(StickText));

    partial void OnInterruptionChanged(MacroInterruption value)
    {
        OnPropertyChanged(nameof(InterruptDedicated));
        OnPropertyChanged(nameof(InterruptAnyKey));
    }

    void SetStick(ThumbstickMode mode)
    {
        if (mode != StickMode)
            PushProfile(p => p with { Thumbstick = mode });
    }

    void SetInterruption(MacroInterruption value)
    {
        if (value != Interruption)
            PushProfile(p => p with { Interruption = value });
    }

    /// <summary>Called on the UI thread when the service reports the stick.</summary>
    public void StickAt(double x, double y) => (StickX, StickY) = (x, y);

    /// <summary>While Customize is on screen, keep asking the service to report the stick.</summary>
    void KeepWatchingStick()
    {
        if (!HasThumbstick || _main.SelectedTab != this || !IsCustomize)
            return;
        _ = Task.Run(async () =>
        {
            try { await _main.Client.WatchStickAsync(Key).ConfigureAwait(false); }
            catch (Exception e) when (e is Tmds.DBus.Protocol.DBusErrorReplyException or InvalidOperationException) { }
        });
    }

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomize));
        KeepWatchingStick();
    }

    /// <summary>The tab came to the front.</summary>
    public void Shown() => KeepWatchingStick();

    [ObservableProperty]
    public partial KeyRowViewModel? SelectedKey { get; set; }

    [ObservableProperty]
    public partial BindingEditorViewModel? Editor { get; set; }

    // ---- Battery: the charge readout in the header, on the Power page and on the dashboard card ------------------

    static readonly Avalonia.Media.IBrush BatteryGreen = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#44D62C"));
    static readonly Avalonia.Media.IBrush BatteryRed = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse("#E5484D"));

    public bool HasBattery => Model?.Has("battery") == true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText), nameof(BatteryFill), nameof(BatteryKnown), nameof(BatteryBrush))]
    public partial int? BatteryPercent { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatteryText))]
    public partial bool Charging { get; set; }

    public bool BatteryKnown => HasBattery && BatteryPercent != null;

    public string BatteryText => BatteryPercent is int p ? $"{p} %" + (Charging ? "  ⚡" : "") : "";

    /// <summary>The width of the gauge's fill, out of 18.</summary>
    public double BatteryFill => Math.Clamp(BatteryPercent ?? 0, 0, 100) * 18 / 100.0;

    /// <summary>Green, red once the charge is at or below the low power level.</summary>
    public Avalonia.Media.IBrush BatteryBrush =>
        BatteryPercent is int p && p <= (_settings.Active.Power ?? _devicePower ?? Core.Lighting.PowerSettings.Default).LowBatteryPercent
            ? BatteryRed : BatteryGreen;

    void ShowBattery(int? percent, bool? charging)
    {
        Charging = charging == true;
        BatteryPercent = percent;
    }

    async Task RefreshBatteryAsync()
    {
        try
        {
            if (await _main.Client.GetDeviceStateAsync(Key).ConfigureAwait(false) is { } state && state.BatteryPercent != null)
                await Dispatcher.UIThread.InvokeAsync(() => ShowBattery(state.BatteryPercent, state.Charging));
        }
        catch (Exception e) when (e is Tmds.DBus.Protocol.DBusErrorReplyException or InvalidOperationException or System.Text.Json.JsonException) { }
    }

    public string Status => Info.Problem ?? (Info.Remapping ? "Active" : "Ready");
    public string Details => $"{Info.Firmware}  ·  {(Info.Verified ? "Tested on this hardware" : Info.Known ? "From the OpenRazer catalogue, not yet tested here" : "Unknown model")}";

    partial void OnInfoChanged(DeviceInfo value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Details));
    }

    IReadOnlyList<string> BuildPages()
    {
        // Synapse 4's order: Customize, Performance, Lighting, Power, Calibration.
        var pages = new List<string> { "CUSTOMIZE" };
        // Keypads show their polling rate on Customize; Performance is the mouse page.
        if (Model?.Has("dpi") == true) pages.Add("PERFORMANCE");
        if (Model?.Zones.Count > 0 || IsKeypad) pages.Add("LIGHTING");
        if (Model?.Has("battery") == true) pages.Add("POWER");
        if (Model?.Has("lift_off") == true) pages.Add("CALIBRATION");
        return pages;
    }

    static string FirmwareCategory(string label) => label switch
    {
        _ when label.Contains("Profile", StringComparison.Ordinal) => "SWITCH PROFILE",
        _ when label.Contains("Sensitivity", StringComparison.Ordinal) => "SENSITIVITY",
        _ when label.Contains("Hypershift", StringComparison.Ordinal) => "RAZER HYPERSHIFT",
        _ when label.Contains("Scroll", StringComparison.Ordinal) => "MOUSE",
        _ => "FIRMWARE",
    };

    static string GroupTitle(string group) => group switch
    {
        "main" => "KEYPAD",
        "other" => "OTHER",
        "mouse" => "MOUSE",
        "side" => "SIDE BUTTONS",
        "thumbstick" => "THUMBSTICK",
        _ => group.ToUpperInvariant(),
    };

    public async Task ReloadAsync()
    {
        try
        {
            var settings = await _main.Client.GetSettingsAsync(Key).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Load(settings, fromService: true));
            await ReadDeviceAsync().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _main.Fail("Could not read the device settings", e);
        }
    }

    bool _loadedOnce;

    /// <summary>A profile or keymap that changed from outside the window (a linked game, the keypad's own buttons)
    /// is announced on screen when the user wants that. Changes made here never arrive as a difference.</summary>
    void AnnounceOutsideChange(DeviceSettings settings)
    {
        if (!_loadedOnce || !_main.Settings.Notifications)
            return;
        string? what = settings.ActiveProfile != _settings.ActiveProfile ? $"Profile: {settings.Active.Name}"
            : settings.ActiveKeymap != _settings.ActiveKeymap && IsKeypad ? $"Keymap: {settings.ActiveKeymapOrFirst.Name}"
            : null;
        if (what != null)
            _ = _main.Client.NotifyAsync(ShortName, what);
    }

    void Load(DeviceSettings settings, bool fromService = false)
    {
        if (fromService)
            AnnounceOutsideChange(settings);
        _loadedOnce = true;
        _loading = true;
        try
        {
            _settings = settings;
            Profiles.Clear();
            foreach (var p in settings.Profiles)
                Profiles.Add(p);
            SelectedProfile = settings.Active;
            Keymaps = settings.Active.Keymaps.Select((k, i) => new KeymapItem(i, k.Name)).ToList();
            SelectedKeymap = Math.Clamp(settings.ActiveKeymap, 0, Math.Max(0, Keymaps.Count - 1));
            // The index often stays 0 across a load, which raises nothing; the box must still re-read its selection.
            OnPropertyChanged(nameof(SelectedKeymapItem));
            OnPropertyChanged(nameof(QuickRemapped));
            RefreshRows();
            StickMode = settings.Active.Thumbstick;
            Interruption = settings.Active.Interruption;
            Lighting.Load(settings.Active.Lighting);
            Performance.Load(settings.Active.Performance ?? _deviceDefaults ?? Core.Lighting.PerformanceSettings.Default);
            Power.Load(settings.Active.Power ?? _devicePower ?? Core.Lighting.PowerSettings.Default);
            OnPropertyChanged(nameof(BatteryBrush));
            ScrollWheel.Load(settings.Active.Scroll ?? _deviceScroll ?? Core.Lighting.ScrollSettings.Default);
            Calibration.Load(settings.Active.Calibration ?? new Core.Lighting.CalibrationSettings(_liftOff));
        }
        finally
        {
            _loading = false;
        }
        _main.Macros.KeymapsChanged();
    }

    Core.Lighting.PerformanceSettings? _deviceDefaults;
    int _liftOff = -1;
    Core.Lighting.PowerSettings? _devicePower;
    Core.Lighting.ScrollSettings? _deviceScroll;

    /// <summary>Reads what the device itself is set to, so Performance and Power show real values before the user
    /// changes anything.</summary>
    async Task ReadDeviceAsync()
    {
        try
        {
            if (await _main.Client.GetDeviceStateAsync(Key).ConfigureAwait(false) is not { } state)
                return;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // A keypad reports a polling rate and no stages; its rate is still the device's own value.
                if (state.Stages is { Count: > 0 } || state.PollingRate > 0)
                    _deviceDefaults = new Core.Lighting.PerformanceSettings(state.Stages ?? [], state.ActiveStage, state.PollingRate);
                // A mouse asleep answers nothing: keep the last reading rather than blanking it.
                if (state.BatteryPercent != null)
                    ShowBattery(state.BatteryPercent, state.Charging);
                _liftOff = state.LiftOffLevel;
                if (state.SleepSeconds > 0)
                    _devicePower = new Core.Lighting.PowerSettings(state.SleepSeconds, state.LowBatteryPercent > 0 ? state.LowBatteryPercent : Core.Lighting.PowerSettings.Default.LowBatteryPercent);
                if (state.ScrollAcceleration != null || state.SmartReel != null)
                    _deviceScroll = new Core.Lighting.ScrollSettings(1, state.ScrollAcceleration == true, state.SmartReel == true);
                Load(_settings);
            });
        }
        catch (Exception e)
        {
            _main.Fail("Could not read the device", e);
        }
    }

    void PushProfile(Func<Profile, Profile> change)
    {
        if (_loading)
            return;
        var profile = change(_settings.Active);
        Push(_settings with { Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList() });
    }

    Keymap CurrentKeymap => _settings.ActiveKeymapOrFirst;

    void RefreshRows()
    {
        foreach (var row in Rows)
        {
            var binding = CurrentKeymap.Resolve(row.Key.Source, Hypershift);
            if (!row.Bindable)
            {
                (row.Category, row.Value, row.Changed) = ("DEVICE FUNCTION", row.Key.Label, false);
                continue;
            }
            var (category, value) = BindingText.Describe(binding, row.Key.Code, id => _main.MacroLibrary.Find(id)?.Name);
            if (binding is DefaultBinding && row.Key.Code == 0)
                (category, value) = (SourceKeys.IsWheel(row.Key.Source) ? "MOUSE FUNCTION" : category, row.Key.Label);
            (row.Category, row.Value, row.Changed) = (category, value, binding is not DefaultBinding);
        }
    }

    partial void OnHypershiftChanged(bool value)
    {
        OnPropertyChanged(nameof(Standard));
        RefreshRows();
        if (SelectedKey != null)
            OpenEditor(SelectedKey);
    }

    partial void OnSelectedKeyChanged(KeyRowViewModel? oldValue, KeyRowViewModel? newValue)
    {
        if (oldValue != null)
            oldValue.IsSelected = false;
        if (newValue != null)
            newValue.IsSelected = true;
        OnKeyChosen(newValue);
    }

    void OnKeyChosen(KeyRowViewModel? value)
    {
        if (value == null || !value.Bindable)
        {
            Editor = null;
            return;
        }
        OpenEditor(value);
    }

    void OpenEditor(KeyRowViewModel row)
    {
        var layer = Hypershift ? CurrentKeymap.Hypershift : CurrentKeymap.Standard;
        var binding = layer.TryGetValue(row.Key.Source, out var b) ? b : DefaultBinding.Instance;
        Editor = new BindingEditorViewModel(row, binding, _main.MacroLibrary, _settings.Profiles, Keymaps.Count, Save, () => SelectedKey = null);
    }

    partial void OnSelectedProfileChanged(Profile? value)
    {
        if (_loading || value == null || value.Id == _settings.ActiveProfile)
            return;
        Push(_settings with { ActiveProfile = value.Id, ActiveKeymap = 0 });
    }

    partial void OnSelectedKeymapChanged(int value)
    {
        OnPropertyChanged(nameof(SelectedKeymapItem));
        OnPropertyChanged(nameof(QuickRemapped));
        if (_loading || value < 0 || value == _settings.ActiveKeymap)
            return;
        Push(_settings with { ActiveKeymap = value });
    }

    void Save(KeyRowViewModel row, Binding binding)
    {
        var keymap = CurrentKeymap;
        var layer = new Dictionary<ushort, Binding>(Hypershift ? keymap.Hypershift : keymap.Standard);
        if (binding is DefaultBinding) layer.Remove(row.Key.Source);
        else layer[row.Key.Source] = binding;
        keymap = Hypershift ? keymap with { Hypershift = layer } : keymap with { Standard = layer };
        Push(ReplaceKeymap(keymap));
        SelectedKey = null;
    }

    /// <summary>Binds a key in the Standard layer of the keymap in use (the MACRO tab's KEY BINDS assigns this way).</summary>
    public void Bind(DeviceKey key, Binding binding) => Bind([(key, binding)]);

    void Bind(IEnumerable<(DeviceKey Key, Binding Binding)> changes)
    {
        var keymap = CurrentKeymap;
        var layer = new Dictionary<ushort, Binding>(keymap.Standard);
        foreach (var (key, binding) in changes)
        {
            if (binding is DefaultBinding) layer.Remove(key.Source);
            else layer[key.Source] = binding;
        }
        Push(ReplaceKeymap(keymap with { Standard = layer }));
        OnPropertyChanged(nameof(QuickRemapped));
    }

    // ---- Quick remapping: W, A, S, D work the virtual gamepad's left stick, Q and E its triggers ----------------

    static readonly (string Key, GamepadControl Control)[] QuickMap =
    [
        ("KEY_W", GamepadControl.LeftStickUp), ("KEY_A", GamepadControl.LeftStickLeft), ("KEY_S", GamepadControl.LeftStickDown),
        ("KEY_D", GamepadControl.LeftStickRight), ("KEY_Q", GamepadControl.LeftTrigger), ("KEY_E", GamepadControl.RightTrigger),
    ];

    /// <summary>The device's own keys that type W, A, S, D, Q and E (not the thumbstick's directions, which only
    /// default to them), with the gamepad control each stands in for.</summary>
    IEnumerable<(DeviceKey Key, GamepadControl Control)> QuickKeys =>
        QuickMap.SelectMany(m => Rows.Where(r => r.Bindable && r.Key.Source == r.Key.Code && KeyCodes.Name(r.Key.Code) == m.Key)
                                     .Select(r => (r.Key, m.Control)));

    public bool HasQuickRemapping => IsKeypad && QuickKeys.Any();

    /// <summary>The keymap in use has Quick remapping on: every one of those keys works the gamepad.</summary>
    public bool QuickRemapped => QuickKeys.Any() && QuickKeys.All(q =>
        CurrentKeymap.Standard.TryGetValue(q.Key.Source, out var b) && b == new GamepadBinding(q.Control));

    [RelayCommand]
    void QuickRemap() => Bind(QuickKeys.Select(q => (q.Key, (Binding)new GamepadBinding(q.Control))));

    [RelayCommand]
    void UndoQuickRemap() => Bind(QuickKeys.Where(q => CurrentKeymap.Standard.GetValueOrDefault(q.Key.Source) is GamepadBinding)
                                           .Select(q => (q.Key, (Binding)DefaultBinding.Instance)));

    DeviceSettings ReplaceKeymap(Keymap keymap)
    {
        var profile = _settings.Active;
        var keymaps = profile.Keymaps.ToList();
        if (keymaps.Count == 0) keymaps.Add(keymap);
        else keymaps[Math.Clamp(_settings.ActiveKeymap, 0, keymaps.Count - 1)] = keymap;
        profile = profile with { Keymaps = keymaps };
        return _settings with { Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList() };
    }

    [RelayCommand]
    void AddProfile()
    {
        var profile = Profile.Create($"Profile {_settings.Profiles.Count + 1}");
        Push(_settings with { Profiles = [.. _settings.Profiles, profile], ActiveProfile = profile.Id, ActiveKeymap = 0 });
    }

    [RelayCommand]
    void DeleteProfile()
    {
        if (_settings.Profiles.Count <= 1)
            return;
        var rest = _settings.Profiles.Where(p => p.Id != _settings.ActiveProfile).ToList();
        Push(_settings with { Profiles = rest, ActiveProfile = rest[0].Id, ActiveKeymap = 0 });
    }

    [RelayCommand]
    void AddKeymap()
    {
        var profile = _settings.Active;
        if (profile.Keymaps.Count >= Profile.MaxKeymaps)
            return;
        profile = profile with { Keymaps = [.. profile.Keymaps, Keymap.Empty($"Keymap {profile.Keymaps.Count + 1}")] };
        Push(_settings with
        {
            Profiles = _settings.Profiles.Select(p => p.Id == profile.Id ? profile : p).ToList(),
            ActiveKeymap = profile.Keymaps.Count - 1,
        });
    }

    [RelayCommand]
    void ResetKeymap()
    {
        var name = CurrentKeymap.Name;
        Push(ReplaceKeymap(Keymap.Empty(name)));
    }

    /// <summary>Shows the change at once and sends it to the service, which saves and applies it.</summary>
    void Push(DeviceSettings settings)
    {
        Load(settings);
        _ = Task.Run(async () =>
        {
            try { await _main.Client.SetSettingsAsync(Key, settings).ConfigureAwait(false); }
            catch (Exception e) { _main.Fail("Could not save the device settings", e); }
        });
    }
}
