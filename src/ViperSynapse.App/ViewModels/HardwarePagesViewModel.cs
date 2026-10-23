using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ViperSynapse.Core.Devices;
using ViperSynapse.Core.Lighting;

namespace ViperSynapse.App.ViewModels;

/// <summary>Coalesces a burst of edits (a slider being dragged) into one save, 300 ms after the last.</summary>
sealed class Debounce(Action action)
{
    readonly DispatcherTimer _timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(300) };
    bool _wired;

    public void Poke()
    {
        if (!_wired)
        {
            _timer.Tick += (_, _) => { _timer.Stop(); action(); };
            _wired = true;
        }
        _timer.Stop();
        _timer.Start();
    }
}

/// <summary>One LED of the plain grid editor.</summary>
public sealed record LedCell(int Row, int Column, IBrush Brush);

public sealed record EffectChoice(EffectKind Kind, string Label, IReadOnlyList<ColorMode> Modes)
{
    public override string ToString() => Label;
}

/// <summary>The LIGHTING page: brightness, the zone, the quick effect and its options.</summary>
public sealed partial class LightingViewModel : ObservableObject
{
    readonly DeviceModel? _model;
    readonly Action<LightingSettings> _save;
    readonly Debounce _debounce;
    Dictionary<string, ZoneLighting> _zones = [];
    bool _loading;

    public LightingViewModel(DeviceModel? model, Action<LightingSettings> save)
    {
        _model = model;
        _save = save;
        _debounce = new Debounce(Commit);
        Zones = model?.Zones.Keys.Order().ToList() is { Count: > 0 } zones ? zones : ["main"];
        SyncAll = Zones.Count > 1;
        SelectedZone = Zones[0];
    }

    public IReadOnlyList<string> Zones { get; }
    public bool HasZones => Zones.Count > 1;

    [ObservableProperty] public partial bool SyncAll { get; set; }
    [ObservableProperty] public partial string SelectedZone { get; set; }
    [ObservableProperty] public partial IReadOnlyList<EffectChoice> Effects { get; set; } = [];
    [ObservableProperty] public partial EffectChoice? SelectedEffect { get; set; }
    [ObservableProperty] public partial IReadOnlyList<ColorMode> ColorModes { get; set; } = [];
    [ObservableProperty] public partial ColorMode SelectedColorMode { get; set; }
    [ObservableProperty] public partial Color Color1 { get; set; } = Color.Parse("#44D62C");
    [ObservableProperty] public partial Color Color2 { get; set; } = Color.Parse("#0000FF");
    [ObservableProperty] public partial double Speed { get; set; } = 2;
    [ObservableProperty] public partial bool Reverse { get; set; }
    [ObservableProperty] public partial double Brightness { get; set; } = 100;

    public bool ShowColor1 => SelectedEffect?.Kind is EffectKind.Static or EffectKind.Reactive
        || SelectedEffect?.Kind is EffectKind.Breathing or EffectKind.Starlight && SelectedColorMode != ColorMode.Random;
    public bool ShowColor2 => SelectedEffect?.Kind is EffectKind.Breathing or EffectKind.Starlight && SelectedColorMode == ColorMode.Dual;
    public bool ShowModes => ColorModes.Count > 1;
    public bool ShowSpeed => SelectedEffect?.Kind is EffectKind.Reactive or EffectKind.Starlight;
    public bool ShowDirection => SelectedEffect?.Kind is EffectKind.Wave or EffectKind.Wheel;

    // ---- Per-key (Custom) ----------------------------------------------------------------------------

    /// <summary>The device has an LED grid the custom effect can paint.</summary>
    public bool HasFrame => _model is { MatrixRows: > 0, MatrixColumns: > 0 } && _model.Has("custom_frame");

    /// <summary>Keys know their LEDs (the Tartarus): paint on the drawn device. Otherwise paint a plain LED grid.</summary>
    public bool PaintsKeys => _model?.Keys.Any(k => k.Leds.Count > 0) == true;

    public bool ShowFrameEditor => SelectedEffect?.Kind == EffectKind.Custom;
    public bool ShowKeyCanvas => ShowFrameEditor && PaintsKeys;
    public bool ShowLedGrid => ShowFrameEditor && !PaintsKeys;

    CustomFrame _frame = CustomFrame.Filled(1, 1, Rgb.RazerGreen);

    [ObservableProperty] public partial Color PaintColor { get; set; } = Color.Parse("#44D62C");

    /// <summary>Key number to the colour under it, for the drawing.</summary>
    [ObservableProperty] public partial IReadOnlyDictionary<int, Color>? KeyColors { get; set; }

    /// <summary>The LED grid for devices whose keys are not drawn.</summary>
    [ObservableProperty] public partial IReadOnlyList<LedCell> Cells { get; set; } = [];

    public int GridColumns => _frame.Columns;

    Rgb Paint => new(PaintColor.R, PaintColor.G, PaintColor.B);

    public void PaintKey(DeviceKey key)
    {
        if (key.Leds.Count == 0)
            return;
        SetFrame(_frame.With(key.Leds, Paint));
    }

    public void PaintCell(LedCell cell) => SetFrame(_frame.With([(cell.Row, cell.Column)], Paint));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void FillAll() => SetFrame(CustomFrame.Filled(_frame.Rows, _frame.Columns, Paint));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void AllOff() => SetFrame(CustomFrame.Filled(_frame.Rows, _frame.Columns, new Rgb(0, 0, 0)));

    void SetFrame(CustomFrame frame)
    {
        _frame = frame;
        ShowFrame();
        Changed();
    }

    void ShowFrame()
    {
        var colors = new Dictionary<int, Color>();
        foreach (var key in _model?.Keys ?? [])
            if (key.Leds.Count > 0)
            {
                var c = _frame[key.Leds[0].Row, key.Leds[0].Column];
                colors[key.Number] = Color.FromRgb(c.R, c.G, c.B);
            }
        KeyColors = colors;
        var cells = new List<LedCell>();
        for (int r = 0; r < _frame.Rows; r++)
            for (int c = 0; c < _frame.Columns; c++)
            {
                var rgb = _frame[r, c];
                cells.Add(new LedCell(r, c, new SolidColorBrush(Color.FromRgb(rgb.R, rgb.G, rgb.B))));
            }
        Cells = cells;
        OnPropertyChanged(nameof(GridColumns));
    }

    public static string ZoneLabel(string zone) => zone switch
    {
        "main" => "Device",
        "logo" => "Logo",
        "scroll" => "Scroll Wheel",
        "right" => "Side Grid",
        "underglow" => "Underglow",
        "left" => "Left Side",
        "backlight" => "Backlight",
        _ => zone,
    };

    /// <summary>The effects a zone takes, from the catalogue's names (static, breath_single, starlight_random, ...).</summary>
    IReadOnlyList<EffectChoice> EffectsFor(string zone)
    {
        var names = _model?.Zones.TryGetValue(zone, out var z) == true ? z.Effects : ["static", "spectrum", "none"];
        var choices = new List<EffectChoice>();
        void Add(EffectKind kind, string label, string prefix)
        {
            var modes = names.Where(n => n == prefix || n.StartsWith(prefix + "_", StringComparison.Ordinal))
                .Select(n => n.EndsWith("_dual", StringComparison.Ordinal) ? ColorMode.Dual
                    : n.EndsWith("_random", StringComparison.Ordinal) ? ColorMode.Random : ColorMode.Single)
                .Distinct().Order().ToList();
            if (modes.Count > 0)
                choices.Add(new EffectChoice(kind, label, modes));
        }
        Add(EffectKind.Static, "Static", "static");
        Add(EffectKind.Breathing, "Breathing", "breath");
        Add(EffectKind.Spectrum, "Spectrum Cycling", "spectrum");
        Add(EffectKind.Wave, "Wave", "wave");
        Add(EffectKind.Reactive, "Reactive", "reactive");
        Add(EffectKind.Starlight, "Starlight", "starlight");
        Add(EffectKind.Wheel, "Wheel", "wheel");
        Add(EffectKind.Off, "Off", "none");
        if (HasFrame)
            choices.Add(new EffectChoice(EffectKind.Custom, "Custom (per key)", [ColorMode.Single]));
        return choices;
    }

    public void Load(LightingSettings? settings)
    {
        _loading = true;
        try
        {
            _zones = settings?.Zones.ToDictionary(z => z.Key, z => z.Value) ?? [];
            LightingOn = settings?.Off != true;
            OffWhenDisplayOff = settings?.OffWhenDisplayOff == true;
            OffWhenIdle = settings?.OffWhenIdleMinutes > 0;
            IdleMinutes = settings?.OffWhenIdleMinutes > 0 ? settings.OffWhenIdleMinutes : 1;
            _frame = settings?.Frame is { } f && f.Rows == _model?.MatrixRows && f.Columns == _model?.MatrixColumns
                ? f
                : CustomFrame.Filled(Math.Max(1, _model?.MatrixRows ?? 1), Math.Max(1, _model?.MatrixColumns ?? 1), Rgb.RazerGreen);
            ShowFrame();
            ShowZone(SelectedZone);
        }
        finally
        {
            _loading = false;
        }
    }

    void ShowZone(string zone)
    {
        bool was = _loading;
        _loading = true;
        try
        {
            Effects = EffectsFor(zone);
            var current = _zones.TryGetValue(zone, out var z) ? z : new ZoneLighting(ZoneEffect.Default);
            SelectedEffect = Effects.FirstOrDefault(e => e.Kind == current.Effect.Kind) ?? Effects.FirstOrDefault();
            ColorModes = SelectedEffect?.Modes ?? [];
            SelectedColorMode = ColorModes.Contains(current.Effect.Colors) ? current.Effect.Colors : ColorModes.FirstOrDefault();
            Color1 = Color.FromRgb(current.Effect.Color1.R, current.Effect.Color1.G, current.Effect.Color1.B);
            Color2 = Color.FromRgb(current.Effect.Color2.R, current.Effect.Color2.G, current.Effect.Color2.B);
            Speed = current.Effect.Speed;
            Reverse = current.Effect.Reverse;
            Brightness = current.Brightness;
            Notify();
        }
        finally
        {
            _loading = was;
        }
    }

    void Notify()
    {
        OnPropertyChanged(nameof(EffectTab));
        OnPropertyChanged(nameof(ShowQuick));
        OnPropertyChanged(nameof(QuickEffects));
        OnPropertyChanged(nameof(ShowFrameEditor));
        OnPropertyChanged(nameof(ShowKeyCanvas));
        OnPropertyChanged(nameof(ShowLedGrid));
        OnPropertyChanged(nameof(ShowColor1));
        OnPropertyChanged(nameof(ShowColor2));
        OnPropertyChanged(nameof(ShowModes));
        OnPropertyChanged(nameof(ShowSpeed));
        OnPropertyChanged(nameof(ShowDirection));
    }

    partial void OnSelectedZoneChanged(string value)
    {
        if (!_loading)
            ShowZone(value);
    }

    partial void OnSelectedEffectChanged(EffectChoice? value)
    {
        if (value != null && !_loading)
        {
            _loading = true;
            ColorModes = value.Modes;
            if (!ColorModes.Contains(SelectedColorMode))
                SelectedColorMode = ColorModes.FirstOrDefault();
            _loading = false;
        }
        Notify();
        Changed();
    }

    partial void OnSelectedColorModeChanged(ColorMode value) { Notify(); Changed(); }
    partial void OnColor1Changed(Color value) => Changed();
    partial void OnColor2Changed(Color value) => Changed();
    partial void OnSpeedChanged(double value) => Changed();
    partial void OnReverseChanged(bool value) => Changed();
    partial void OnBrightnessChanged(double value) => Changed();
    partial void OnSyncAllChanged(bool value) => Changed();

    void Changed()
    {
        if (_loading || SelectedEffect == null)
            return;
        var effect = new ZoneEffect(SelectedEffect.Kind, new Rgb(Color1.R, Color1.G, Color1.B), new Rgb(Color2.R, Color2.G, Color2.B),
            SelectedColorMode, (int)Speed, Reverse);
        var lighting = new ZoneLighting(effect, (int)Brightness);
        foreach (var zone in SyncAll ? Zones : [SelectedZone])
        {
            // A synced effect a zone cannot show (wave on a logo) leaves that zone as it was.
            if (zone == SelectedZone || EffectsFor(zone).Any(e => e.Kind == effect.Kind))
                _zones[zone] = lighting;
        }
        _debounce.Poke();
    }

    void Commit() => _save(Current());

    LightingSettings Current() => new(new Dictionary<string, ZoneLighting>(_zones),
        _zones.Values.Any(z => z.Effect.Kind == EffectKind.Custom) ? _frame : null,
        Off: !LightingOn, OffWhenDisplayOff: OffWhenDisplayOff, OffWhenIdleMinutes: OffWhenIdle ? (int)IdleMinutes : 0);

    // ---- Synapse 4 cards: the Brightness switch, Switch off lighting, Quick and Advanced effects ---------

    [ObservableProperty] public partial bool LightingOn { get; set; } = true;
    [ObservableProperty] public partial bool OffWhenDisplayOff { get; set; }
    [ObservableProperty] public partial bool OffWhenIdle { get; set; }
    [ObservableProperty] public partial double IdleMinutes { get; set; } = 1;

    public static IReadOnlyList<string> EffectTabs { get; } = ["Quick Effects", "Advanced Effects"];

    /// <summary>Advanced is Synapse's Chroma Studio; here it is the per-key frame.</summary>
    public string EffectTab
    {
        get => SelectedEffect?.Kind == EffectKind.Custom ? EffectTabs[1] : EffectTabs[0];
        set
        {
            if (value == EffectTab)
                return;
            SelectedEffect = value == EffectTabs[1]
                ? Effects.FirstOrDefault(e => e.Kind == EffectKind.Custom)
                : Effects.FirstOrDefault(e => e.Kind == EffectKind.Spectrum) ?? Effects.FirstOrDefault(e => e.Kind != EffectKind.Custom);
        }
    }

    public bool ShowQuick => EffectTab == EffectTabs[0];

    /// <summary>The quick effects, without the per-key one (that is the Advanced side).</summary>
    public IReadOnlyList<EffectChoice> QuickEffects => Effects.Where(e => e.Kind != EffectKind.Custom).ToList();

    partial void OnLightingOnChanged(bool value) => Changed();
    partial void OnOffWhenDisplayOffChanged(bool value) => Changed();
    partial void OnOffWhenIdleChanged(bool value) => Changed();
    partial void OnIdleMinutesChanged(double value) => Changed();

    /// <summary>Synapse's "Apply to other Chroma-enabled devices": the chosen quick effect goes to every other device
    /// that can show it.</summary>
    public event Action<ZoneEffect>? ApplyToOthers;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void ApplyToOtherDevices()
    {
        if (SelectedEffect is { Kind: not EffectKind.Custom } effect)
            ApplyToOthers?.Invoke(new ZoneEffect(effect.Kind, new Rgb(Color1.R, Color1.G, Color1.B), new Rgb(Color2.R, Color2.G, Color2.B), SelectedColorMode, (int)Speed, Reverse));
    }

    /// <summary>Takes a quick effect from another device: every zone that can show it.</summary>
    public void Take(ZoneEffect effect)
    {
        foreach (var zone in Zones)
            if (EffectsFor(zone).Any(e => e.Kind == effect.Kind))
                _zones[zone] = new ZoneLighting(effect, _zones.TryGetValue(zone, out var z) ? z.Brightness : 100);
        Load(Current());
        _debounce.Poke();
    }
}

/// <summary>One row of the sensitivity stages.</summary>
public sealed partial class StageRow(int number, PerformanceViewModel owner) : ObservableObject
{
    public int Number { get; } = number;
    public int DpiMax => owner.DpiMax;
    public bool SeparateXY => owner.SeparateXY;

    internal void SeparateXYChanged() => OnPropertyChanged(nameof(SeparateXY));
    [ObservableProperty] public partial double X { get; set; }
    [ObservableProperty] public partial double Y { get; set; }
    [ObservableProperty] public partial bool Active { get; set; }

    partial void OnXChanged(double value) => owner.StageChanged(this, x: true);
    partial void OnYChanged(double value) => owner.StageChanged(this, x: false);
}

/// <summary>The PERFORMANCE page: sensitivity stages (X and Y tied unless untied) and the polling rate.</summary>
public sealed partial class PerformanceViewModel : ObservableObject
{
    readonly Action<PerformanceSettings> _save;
    readonly Debounce _debounce;
    bool _loading;

    public PerformanceViewModel(DeviceModel? model, Action<PerformanceSettings> save)
    {
        _save = save;
        _debounce = new Debounce(Commit);
        DpiMax = model?.DpiMax > 0 ? model.DpiMax : 20000;
        PollRates = model?.PollRates.Count > 0 ? model.PollRates : [125, 500, 1000];
        HasStages = model?.Has("dpi_stages") == true;
    }

    public int DpiMax { get; }
    public bool HasStages { get; }
    public IReadOnlyList<int> PollRates { get; }
    public ObservableCollection<StageRow> Stages { get; } = [];

    [ObservableProperty] public partial double StageCount { get; set; } = 5;
    [ObservableProperty] public partial bool SeparateXY { get; set; }
    [ObservableProperty] public partial int PollingRate { get; set; }
    [ObservableProperty] public partial int ActiveStage { get; set; }

    public void Load(PerformanceSettings settings)
    {
        _loading = true;
        try
        {
            Stages.Clear();
            for (int i = 0; i < settings.Stages.Count; i++)
                Stages.Add(new StageRow(i + 1, this) { X = settings.Stages[i].X, Y = settings.Stages[i].Y, Active = i == settings.ActiveStage });
            StageCount = settings.Stages.Count;
            ActiveStage = settings.ActiveStage;
            SeparateXY = settings.Stages.Any(s => s.X != s.Y);
            // 0 = not known: nothing is shown chosen rather than a guess.
            PollingRate = settings.PollingRate;
            // The count often stays the same across a load, which raises nothing; the switch must still re-read it.
            OnPropertyChanged(nameof(StagesEnabled));
        }
        finally
        {
            _loading = false;
        }
    }

    internal void StageChanged(StageRow row, bool x)
    {
        if (_loading)
            return;
        if (!SeparateXY)
        {
            _loading = true;
            if (x) row.Y = row.X; else row.X = row.Y;
            _loading = false;
        }
        _debounce.Poke();
    }

    public void Activate(StageRow row)
    {
        foreach (var s in Stages)
            s.Active = s == row;
        ActiveStage = row.Number - 1;
    }

    partial void OnSeparateXYChanged(bool value)
    {
        foreach (var s in Stages)
            s.SeparateXYChanged();
        if (!_loading)
            _debounce.Poke();
    }

    partial void OnActiveStageChanged(int value) { if (!_loading) _debounce.Poke(); }
    partial void OnPollingRateChanged(int value) { if (!_loading) _debounce.Poke(); }

    /// <summary>Synapse's "Sensitivity Stages" switch: off keeps one sensitivity (the active stage), on brings the
    /// stages back as they were.</summary>
    public bool StagesEnabled
    {
        get => Stages.Count > 1;
        set
        {
            if (value == StagesEnabled)
                return;
            if (value)
            {
                StageCount = Math.Max(2, _stagesBefore);
            }
            else
            {
                _stagesBefore = Stages.Count;
                var keep = Stages[Math.Clamp(ActiveStage, 0, Stages.Count - 1)];
                Stages.Clear();
                Stages.Add(new StageRow(1, this) { X = keep.X, Y = keep.Y, Active = true });
                ActiveStage = 0;
                StageCount = 1;
                _debounce.Poke();
            }
            OnPropertyChanged();
        }
    }

    int _stagesBefore = PerformanceSettings.MaxStages;

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void AddStage() => StageCount = Math.Min(PerformanceSettings.MaxStages, Stages.Count + 1);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    void RemoveStage() => StageCount = Math.Max(1, Stages.Count - 1);

    partial void OnStageCountChanged(double value)
    {
        OnPropertyChanged(nameof(StagesEnabled));
        if (_loading)
            return;
        int count = Math.Clamp((int)value, 1, PerformanceSettings.MaxStages);
        while (Stages.Count < count)
        {
            int dpi = Math.Min(DpiMax, (int)(Stages.LastOrDefault()?.X ?? 400) * 2);
            Stages.Add(new StageRow(Stages.Count + 1, this) { X = dpi, Y = dpi });
        }
        while (Stages.Count > count)
            Stages.RemoveAt(Stages.Count - 1);
        if (ActiveStage >= count)
            Activate(Stages[^1]);
        _debounce.Poke();
    }

    void Commit()
    {
        var stages = Stages.Select(s => new DpiStage(Round(s.X), Round(SeparateXY ? s.Y : s.X))).ToList();
        _save(new PerformanceSettings(stages, Math.Clamp(ActiveStage, 0, stages.Count - 1), PollingRate));
    }

    /// <summary>Synapse moves sensitivity in steps of 50.</summary>
    int Round(double dpi) => Math.Clamp((int)Math.Round(dpi / 50) * 50, 100, DpiMax);
}

/// <summary>The POWER page: battery, sleep timer, low-power warning.</summary>
public sealed partial class PowerViewModel(Action<PowerSettings> save) : ObservableObject
{
    bool _loading;

    [ObservableProperty] public partial double SleepMinutes { get; set; } = 5;
    [ObservableProperty] public partial double LowBattery { get; set; } = 15;
    [ObservableProperty] public partial string Battery { get; set; } = "";

    public void Load(PowerSettings settings, int? battery, bool? charging)
    {
        _loading = true;
        SleepMinutes = settings.SleepSeconds / 60.0;
        LowBattery = settings.LowBatteryPercent;
        Battery = battery is int b ? $"{b}%{(charging == true ? "  (charging)" : "")}" : "Unknown";
        _loading = false;
    }

    partial void OnSleepMinutesChanged(double value) => Changed();
    partial void OnLowBatteryChanged(double value) => Changed();

    void Changed()
    {
        if (!_loading)
            save(new PowerSettings((int)(SleepMinutes * 60), (int)LowBattery));
    }
}

/// <summary>The Scroll Wheel card on a mouse's Customize page: scroll speed (lines per notch, done in software, so any
/// mouse has it) and, where the firmware has them, scroll acceleration and Smart-Reel.</summary>
public sealed partial class ScrollWheelViewModel(Action<ScrollSettings> save) : ObservableObject
{
    bool _loading;

    [ObservableProperty] public partial double LinesPerNotch { get; set; } = 1;
    [ObservableProperty] public partial bool Acceleration { get; set; }
    [ObservableProperty] public partial bool SmartReel { get; set; }

    public void Load(ScrollSettings settings)
    {
        _loading = true;
        LinesPerNotch = settings.LinesPerNotch;
        Acceleration = settings.Acceleration;
        SmartReel = settings.SmartReel;
        _loading = false;
    }

    partial void OnLinesPerNotchChanged(double value) => Changed();
    partial void OnAccelerationChanged(bool value) => Changed();
    partial void OnSmartReelChanged(bool value) => Changed();

    void Changed()
    {
        if (!_loading)
            save(new ScrollSettings(Math.Clamp((int)Math.Round(LinesPerNotch), 1, ScrollSettings.MaxLinesPerNotch), Acceleration, SmartReel));
    }
}

/// <summary>The CALIBRATION page: Smart Tracking on or off (off leaves the sensor at its own default) and the tracking
/// distance LOW / MEDIUM / HIGH (1, 2, 3 mm).</summary>
public sealed partial class CalibrationViewModel(Action<CalibrationSettings> save) : ObservableObject
{
    bool _loading;

    [ObservableProperty] public partial bool SmartTracking { get; set; }

    /// <summary>0 LOW, 1 MEDIUM, 2 HIGH.</summary>
    [ObservableProperty] public partial double Distance { get; set; } = 1;

    public void Load(CalibrationSettings settings)
    {
        _loading = true;
        SmartTracking = settings.LiftOffLevel >= 0;
        Distance = settings.LiftOffLevel >= 0 ? settings.LiftOffLevel : 1;
        _loading = false;
    }

    partial void OnSmartTrackingChanged(bool value) => Save();
    partial void OnDistanceChanged(double value) => Save();

    void Save()
    {
        if (!_loading)
            save(new CalibrationSettings(SmartTracking ? (int)Math.Round(Distance) : -1));
    }
}
