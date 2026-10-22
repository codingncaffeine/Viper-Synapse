using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.ViewModels;

/// <summary>One global shortcut as the list shows it: the keys, and what they do.</summary>
public sealed record ShortcutRow(GlobalShortcut Shortcut, string Keys, string Category, string Function);

/// <summary>
/// GLOBAL SHORTCUTS on the SYNAPSE tab: key combinations on a Razer device that do one function in every profile.
/// The keys are picked from a device's key list (they are that device's own keys, as the service sees them before
/// any remapping); the function comes from the same editor a key binding uses.
/// </summary>
public sealed partial class ShortcutsViewModel(MainViewModel main) : ObservableObject
{
    public const int MaxKeys = 3;

    ShortcutList _list = ShortcutList.Empty;

    public ObservableCollection<ShortcutRow> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public async Task LoadAsync()
    {
        try
        {
            var list = await main.Client.GetShortcutsAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() => Show(list));
        }
        catch (Exception e)
        {
            main.Fail("Could not read the global shortcuts", e);
        }
    }

    void Show(ShortcutList list)
    {
        _list = list;
        Rows.Clear();
        foreach (var s in list.Shortcuts)
        {
            var (category, function) = BindingText.Describe(s.Binding, 0, id => main.MacroLibrary.Find(id)?.Name);
            Rows.Add(new ShortcutRow(s, string.Join(" + ", s.Keys.Select(BindingText.KeyLabel)), category, function));
        }
        OnPropertyChanged(nameof(IsEmpty));
    }

    void Commit(ShortcutList list)
    {
        Show(list);
        _ = Task.Run(async () =>
        {
            try { await main.Client.SetShortcutsAsync(list).ConfigureAwait(false); }
            catch (Exception e) { main.Fail("Could not save the global shortcuts", e); }
        });
    }

    // ---- Adding or changing one -------------------------------------------------------------------------

    Guid? _editing;

    [ObservableProperty] public partial bool Editing { get; set; }
    [ObservableProperty] public partial DeviceViewModel? EditDevice { get; set; }
    [ObservableProperty] public partial DeviceKey? Key1 { get; set; }
    [ObservableProperty] public partial DeviceKey? Key2 { get; set; }
    [ObservableProperty] public partial DeviceKey? Key3 { get; set; }
    [ObservableProperty] public partial BindingEditorViewModel? Editor { get; set; }
    [ObservableProperty] public partial string? Problem { get; set; }

    public IEnumerable<DeviceViewModel> Devices => main.Devices;

    /// <summary>The chosen device's keys that can be part of a shortcut.</summary>
    public IReadOnlyList<DeviceKey> KeyChoices => EditDevice?.Rows.Where(r => r.Bindable).Select(r => r.Key).ToList() ?? [];

    partial void OnEditDeviceChanged(DeviceViewModel? value) => OnPropertyChanged(nameof(KeyChoices));

    [RelayCommand]
    void Add() => Open(null, [], new KeyboardBinding(0));

    [RelayCommand]
    void Change(ShortcutRow row) => Open(row.Shortcut.Id, row.Shortcut.Keys, row.Shortcut.Binding);

    [RelayCommand]
    void Delete(ShortcutRow row)
    {
        if (_editing == row.Shortcut.Id)
            Close();
        Commit(new ShortcutList(_list.Shortcuts.Where(s => s.Id != row.Shortcut.Id).ToList()));
    }

    void Open(Guid? id, IReadOnlyList<ushort> keys, Binding binding)
    {
        _editing = id;
        Problem = null;
        OnPropertyChanged(nameof(Devices));
        // The device whose keys these are, else the first one.
        EditDevice = main.Devices.FirstOrDefault(d => keys.Count > 0 && keys.All(k => d.Rows.Any(r => r.Key.Source == k)))
                     ?? main.Devices.FirstOrDefault();
        DeviceKey? Pick(int i) => i < keys.Count ? KeyChoices.FirstOrDefault(k => k.Source == keys[i]) : null;
        Key1 = Pick(0);
        Key2 = Pick(1);
        Key3 = Pick(2);
        var row = new KeyRowViewModel(new DeviceKey(0, "Shortcut", 0, 0, "other", 0));
        Editor = new BindingEditorViewModel(row, binding, main.MacroLibrary, [], 8, (_, b) => Save(b), Close,
            id == null ? "NEW SHORTCUT" : "EDIT SHORTCUT");
        Editing = true;
    }

    void Save(Binding binding)
    {
        var keys = new[] { Key1, Key2, Key3 }.OfType<DeviceKey>().Select(k => k.Source).Distinct().ToList();
        if (keys.Count < 2)
        {
            Problem = "A shortcut is a combination: pick at least two keys.";
            return;
        }
        if (binding is DefaultBinding)
        {
            Problem = "Pick what the shortcut does.";
            return;
        }
        if (_list.Shortcuts.Any(s => s.Id != _editing && s.Keys.Order().SequenceEqual(keys.Order())))
        {
            Problem = "Another shortcut already uses these keys.";
            return;
        }
        var list = _editing is Guid id
            ? _list.Shortcuts.Select(s => s.Id == id ? s with { Keys = keys, Binding = binding } : s).ToList()
            : [.. _list.Shortcuts, GlobalShortcut.Create(keys, binding)];
        Close();
        Commit(new ShortcutList(list));
    }

    void Close()
    {
        Editing = false;
        Editor = null;
        _editing = null;
    }
}
