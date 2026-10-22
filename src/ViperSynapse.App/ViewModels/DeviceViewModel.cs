using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;

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

    [ObservableProperty]
    public partial string Value { get; set; } = "";

    /// <summary>Changed from what the key does by itself: shown green, as Synapse does.</summary>
    [ObservableProperty]
    public partial bool Changed { get; set; }
}

public sealed record KeyGroup(string Title, IReadOnlyList<KeyRowViewModel> Keys);

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
        Pages = BuildPages();
        SelectedPage = Pages[0];
    }

    [ObservableProperty]
    public partial DeviceInfo Info { get; set; }

    public DeviceModel? Model { get; }
    public string Key => Info.Key;
    public override string Header => ShortName.ToUpperInvariant();
    public string Title => (Model?.Name ?? Info.Name).ToUpperInvariant();
    public string ShortName => (Model?.Name ?? Info.Name).Replace("Razer ", "", StringComparison.Ordinal);
    public bool IsKeypad => Model?.Type == "keypad";

    public IReadOnlyList<KeyRowViewModel> Rows { get; }
    public IReadOnlyList<KeyGroup> Groups { get; }
    public IReadOnlyList<string> Pages { get; }

    [ObservableProperty]
    public partial string SelectedPage { get; set; }

    public ObservableCollection<Profile> Profiles { get; } = [];

    [ObservableProperty]
    public partial Profile? SelectedProfile { get; set; }

    /// <summary>Replaced whole on every load: a combo box whose items are cleared under a two-way index binding
    /// loses its selection.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<string> Keymaps { get; set; } = [];

    [ObservableProperty]
    public partial int SelectedKeymap { get; set; }

    /// <summary>What the keymap box binds to. A box whose items are replaced sends null back; that is ignored.</summary>
    public string? SelectedKeymapName
    {
        get => SelectedKeymap >= 0 && SelectedKeymap < Keymaps.Count ? Keymaps[SelectedKeymap] : null;
        set
        {
            int index = value == null ? -1 : Keymaps.ToList().IndexOf(value);
            if (index >= 0)
                SelectedKeymap = index;
        }
    }

    [ObservableProperty]
    public partial bool Hypershift { get; set; }

    /// <summary>The Standard half of the layer switch.</summary>
    public bool Standard
    {
        get => !Hypershift;
        set => Hypershift = !value;
    }

    public bool IsCustomize => SelectedPage == "CUSTOMIZE";

    partial void OnSelectedPageChanged(string value) => OnPropertyChanged(nameof(IsCustomize));

    [ObservableProperty]
    public partial KeyRowViewModel? SelectedKey { get; set; }

    [ObservableProperty]
    public partial BindingEditorViewModel? Editor { get; set; }

    public string Status => Info.Problem ?? (Info.Remapping ? "Active" : "Ready");
    public string Details => $"{Info.Firmware}  ·  {(Info.Verified ? "Tested on this hardware" : Info.Known ? "From the OpenRazer catalogue, not yet tested here" : "Unknown model")}";

    partial void OnInfoChanged(DeviceInfo value)
    {
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Details));
    }

    IReadOnlyList<string> BuildPages()
    {
        var pages = new List<string> { "CUSTOMIZE" };
        if (Model?.Zones.Count > 0 || IsKeypad) pages.Add("LIGHTING");
        if (Model?.Has("dpi") == true || Model?.Has("poll_rate") == true) pages.Add("PERFORMANCE");
        if (Model?.Has("battery") == true) pages.Add("POWER");
        if (Model?.Type == "mouse") pages.Add("CALIBRATION");
        return pages;
    }

    static string GroupTitle(string group) => group switch
    {
        "main" => "KEYPAD",
        "other" => "OTHER",
        "mouse" => "MOUSE",
        "side" => "SIDE BUTTONS",
        _ => group.ToUpperInvariant(),
    };

    public async Task ReloadAsync()
    {
        try
        {
            var settings = await _main.Client.GetSettingsAsync(Key).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Load(settings));
        }
        catch (Exception e)
        {
            _main.Fail("Could not read the device settings", e);
        }
    }

    void Load(DeviceSettings settings)
    {
        _loading = true;
        try
        {
            _settings = settings;
            Profiles.Clear();
            foreach (var p in settings.Profiles)
                Profiles.Add(p);
            SelectedProfile = settings.Active;
            Keymaps = settings.Active.Keymaps.Select(k => k.Name).ToList();
            SelectedKeymap = Math.Clamp(settings.ActiveKeymap, 0, Math.Max(0, Keymaps.Count - 1));
            // The index often stays 0 across a load, which raises nothing; the box must still re-read its selection.
            OnPropertyChanged(nameof(SelectedKeymapName));
            RefreshRows();
        }
        finally
        {
            _loading = false;
        }
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

    partial void OnSelectedKeyChanged(KeyRowViewModel? value)
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
        OnPropertyChanged(nameof(SelectedKeymapName));
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
