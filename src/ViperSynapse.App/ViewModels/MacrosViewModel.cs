using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Devices;

namespace ViperSynapse.App.ViewModels;

/// <summary>One line of the macro's action list, with the colour Synapse gives its kind of step.</summary>
public sealed record StepRow(int Index, MacroStep Step, int Depth, Func<Guid, string?> MacroName)
{
    public string Number => (Index + 1).ToString(CultureInfo.InvariantCulture);

    public string Glyph => Step switch
    {
        KeyStep { Down: true } k when Core.Input.KeyCodes.IsMouseButton(k.Code) => "🖱↓",
        KeyStep k when Core.Input.KeyCodes.IsMouseButton(k.Code) => "🖱↑",
        KeyStep { Down: true } => "⌨↓",
        KeyStep => "⌨↑",
        DelayStep or RandomDelayStep => "⏱",
        ScrollStep => "⇕",
        TextStep => "T",
        RunStep => ">_",
        LaunchStep => "↗",
        MacroCallStep => "M",
        LoopStartStep => "⟳",
        LoopEndStep => "⟲",
        _ => "",
    };

    public IBrush GlyphBrush => new SolidColorBrush(Color.Parse(Step switch
    {
        KeyStep k when Core.Input.KeyCodes.IsMouseButton(k.Code) => "#E8902E",
        KeyStep => "#44D62C",
        DelayStep or RandomDelayStep => "#BBBBBB",
        TextStep => "#3D8BFD",
        RunStep => "#2EC4D6",
        LaunchStep => "#E040A0",
        MacroCallStep => "#44D62C",
        LoopStartStep or LoopEndStep => "#E5484D",
        _ => "#CCCCCC",
    }));

    public string Text => Step switch
    {
        KeyStep k => $"{BindingText.KeyLabel(k.Code)}  {(k.Down ? "down" : "up")}",
        DelayStep d => string.Create(CultureInfo.InvariantCulture, $"{d.Milliseconds / 1000.0:0.000}s"),
        RandomDelayStep r => string.Create(CultureInfo.InvariantCulture, $"{r.MinMilliseconds / 1000.0:0.000}s – {r.MaxMilliseconds / 1000.0:0.000}s (random)"),
        ScrollStep s => s.Horizontal ? (s.Clicks > 0 ? $"Scroll Right ×{s.Clicks}" : $"Scroll Left ×{-s.Clicks}") : (s.Clicks > 0 ? $"Scroll Up ×{s.Clicks}" : $"Scroll Down ×{-s.Clicks}"),
        TextStep t => $"Type \"{t.Text}\"",
        RunStep r => $"Run {r.Command}",
        LaunchStep l => $"Launch {l.Target}",
        MacroCallStep m => $"Play macro \"{MacroName(m.MacroId) ?? "missing"}\"",
        LoopStartStep l => $"Loop ×{l.Count}",
        LoopEndStep => "End of loop",
        _ => "",
    };

    public Avalonia.Thickness Indent => new(4 + Depth * 22, 0, 0, 0);
}

/// <summary>A key that plays a macro, for the KEY BINDS page.</summary>
public sealed record MacroAssignment(DeviceViewModel Device, DeviceKey Key, MacroBinding Binding, string MacroName)
{
    public string DeviceName => Device.ShortName;
    public string KeyName => $"Key {Key.Number} · {Key.Label}";
    public string Playback => Binding.Playback switch
    {
        MacroPlayback.Times => $"Play {Binding.Times} times",
        MacroPlayback.WhileHeld => "Repeat while held",
        MacroPlayback.Toggle => "Toggle",
        _ => "Play once",
    };
}

/// <summary>
/// The MACRO tab, as Synapse 4 lays it out: a macro dropdown with New Macro, MY MACROS (the ADD panel, the action
/// list, RECORD with its options, undo/redo and SAVE) and KEY BINDS (the keys that play macros, and assigning one).
/// Edits stay in a working copy until SAVE; switching to another macro saves first.
/// </summary>
public sealed partial class MacrosViewModel(DaemonClient client, MainViewModel main) : TabViewModel
{
    public override string Header => "MACRO";
    public override TabIcon Icon => TabIcon.Macro;

    public MacroLibrary Library { get; private set; } = MacroLibrary.Empty;
    public ObservableCollection<Macro> Macros { get; } = [];
    public ObservableCollection<StepRow> Steps { get; } = [];

    public static IReadOnlyList<string> Pages { get; } = ["MY MACROS", "KEY BINDS"];
    public static IReadOnlyList<Choice<int>> StartDelays { get; } = [.. Enumerable.Range(0, 6).Select(s => new Choice<int>(s, $"{s}s"))];
    public static IReadOnlyList<Choice<MouseFunction>> MouseChoices => BindingEditorViewModel.MouseFunctions;
    public static IReadOnlyList<Choice<MacroPlayback>> Playbacks => BindingEditorViewModel.Playbacks;

    [ObservableProperty] public partial string SelectedPage { get; set; } = "MY MACROS";
    [ObservableProperty] public partial Macro? SelectedMacro { get; set; }
    [ObservableProperty] public partial StepRow? SelectedStep { get; set; }
    [ObservableProperty] public partial string MacroName { get; set; } = "";
    [ObservableProperty] public partial bool Dirty { get; set; }

    public bool ShowMyMacros => SelectedPage == "MY MACROS";
    public bool ShowKeyBinds => SelectedPage == "KEY BINDS";
    public bool HasMacro => SelectedMacro != null;

    partial void OnSelectedPageChanged(string value)
    {
        OnPropertyChanged(nameof(ShowMyMacros));
        OnPropertyChanged(nameof(ShowKeyBinds));
        if (value == "KEY BINDS")
            RefreshAssignments();
    }

    // ---- The working copy, with undo and redo ---------------------------------------------------------

    List<MacroStep> _work = [];
    MacroType _type;
    readonly Stack<(List<MacroStep>, MacroType)> _undo = new(), _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string Duration => string.Create(CultureInfo.InvariantCulture, $"{new Macro(Guid.Empty, "", _work).Duration.TotalSeconds:0.000}s");

    [ObservableProperty] public partial bool SequenceMacro { get; set; }

    partial void OnSequenceMacroChanged(bool value)
    {
        var type = value ? MacroType.Sequence : MacroType.Standard;
        if (_loadingMacro || type == _type)
            return;
        Edit(_work, type);
    }

    void Edit(List<MacroStep> steps, MacroType? type = null, int? select = null)
    {
        _undo.Push((_work, _type));
        _redo.Clear();
        Show(steps, type ?? _type, select);
        Dirty = true;
    }

    void Show(List<MacroStep> steps, MacroType type, int? select)
    {
        _work = steps;
        _type = type;
        _loadingMacro = true;
        SequenceMacro = type == MacroType.Sequence;
        _loadingMacro = false;
        Steps.Clear();
        int depth = 0;
        for (int i = 0; i < steps.Count; i++)
        {
            if (steps[i] is LoopEndStep)
                depth = Math.Max(0, depth - 1);
            Steps.Add(new StepRow(i, steps[i], depth, id => Library.Find(id)?.Name));
            if (steps[i] is LoopStartStep)
                depth++;
        }
        SelectedStep = select is int s && s >= 0 && s < Steps.Count ? Steps[s] : null;
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRedo));
        OnPropertyChanged(nameof(Duration));
    }

    bool _loadingMacro;

    [RelayCommand]
    void Undo()
    {
        if (_undo.Count == 0)
            return;
        _redo.Push((_work, _type));
        var (steps, type) = _undo.Pop();
        Show(steps, type, null);
        Dirty = true;
    }

    [RelayCommand]
    void Redo()
    {
        if (_redo.Count == 0)
            return;
        _undo.Push((_work, _type));
        var (steps, type) = _redo.Pop();
        Show(steps, type, null);
        Dirty = true;
    }

    [RelayCommand]
    void Save()
    {
        if (SelectedMacro is not { } macro || !Dirty)
            return;
        var saved = macro with { Steps = _work.ToList(), Type = _type };
        Dirty = false;
        Commit(new MacroLibrary(Library.Macros.Select(m => m.Id == saved.Id ? saved : m).ToList()), saved.Id);
    }

    // ---- The library -----------------------------------------------------------------------------------

    public async Task LoadAsync()
    {
        var library = await client.GetMacrosAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => SetLibrary(library, SelectedMacro?.Id));
    }

    bool _switching;

    void SetLibrary(MacroLibrary library, Guid? select)
    {
        _switching = true;
        Library = library;
        Macros.Clear();
        foreach (var m in library.Macros)
            Macros.Add(m);
        SelectedMacro = Macros.FirstOrDefault(m => m.Id == select) ?? Macros.FirstOrDefault();
        _switching = false;
        Open(SelectedMacro);
        KeymapsChanged();
    }

    partial void OnSelectedMacroChanged(Macro? oldValue, Macro? newValue)
    {
        if (_switching)
            return;
        // Leaving a macro with changes saves them, so nothing is lost by switching.
        if (Dirty && oldValue != null)
        {
            var saved = oldValue with { Steps = _work.ToList(), Type = _type };
            Dirty = false;
            Commit(new MacroLibrary(Library.Macros.Select(m => m.Id == saved.Id ? saved : m).ToList()), newValue?.Id);
            return;
        }
        Open(newValue);
    }

    void Open(Macro? macro)
    {
        _undo.Clear();
        _redo.Clear();
        Dirty = false;
        _renaming = true;
        MacroName = macro?.Name ?? "";
        _renaming = false;
        Show(macro?.Steps.ToList() ?? [], macro?.Type ?? MacroType.Standard, null);
        OnPropertyChanged(nameof(HasMacro));
    }

    bool _renaming;

    partial void OnMacroNameChanged(string value)
    {
        if (_renaming || SelectedMacro is not { } macro || string.IsNullOrWhiteSpace(value) || value.Trim() == macro.Name)
            return;
        var renamed = macro with { Name = value.Trim(), Steps = Dirty ? _work.ToList() : macro.Steps, Type = Dirty ? _type : macro.Type };
        Dirty = false;
        Commit(new MacroLibrary(Library.Macros.Select(m => m.Id == macro.Id ? renamed : m).ToList()), macro.Id);
    }

    [RelayCommand]
    void NewMacro()
    {
        Save();
        var macro = Macro.Create($"Macro {Library.Macros.Count + 1}", []);
        Commit(new MacroLibrary([.. Library.Macros, macro]), macro.Id);
    }

    [RelayCommand]
    void DuplicateMacro()
    {
        if (SelectedMacro is not { } macro)
            return;
        var copy = Macro.Create($"{macro.Name} copy", Dirty ? _work.ToList() : macro.Steps) with { Type = Dirty ? _type : macro.Type };
        Dirty = false;
        Commit(new MacroLibrary([.. Library.Macros, copy]), copy.Id);
    }

    [RelayCommand]
    void DeleteMacro()
    {
        if (SelectedMacro is not { } doomed)
            return;
        Dirty = false;
        Commit(new MacroLibrary(Library.Macros.Where(m => m.Id != doomed.Id).ToList()), null);
    }

    void Commit(MacroLibrary library, Guid? select)
    {
        SetLibrary(library, select);
        _ = Task.Run(async () =>
        {
            try { await client.SetMacrosAsync(library).ConfigureAwait(false); }
            catch (Exception e) { main.Fail("Could not save the macros", e); }
        });
    }

    // ---- Steps: the ADD panel and the list --------------------------------------------------------------

    /// <summary>Which ADD item's settings are showing under the list, or null.</summary>
    [ObservableProperty] public partial string? Adding { get; set; }

    public bool AddingDelay => Adding == "delay";
    public bool AddingKey => Adding == "key";
    public bool AddingMouse => Adding == "mouse";
    public bool AddingMacro => Adding == "macro";
    public bool AddingLaunch => Adding == "launch";
    public bool AddingRun => Adding == "run";
    public bool AddingText => Adding == "text";
    public bool AddingLoop => Adding == "loop";

    partial void OnAddingChanged(string? value)
    {
        foreach (var name in new[] { nameof(AddingDelay), nameof(AddingKey), nameof(AddingMouse), nameof(AddingMacro), nameof(AddingLaunch), nameof(AddingRun), nameof(AddingText), nameof(AddingLoop) })
            OnPropertyChanged(name);
    }

    [ObservableProperty] public partial double InsertDelayMs { get; set; } = 100;
    [ObservableProperty] public partial Choice<MouseFunction> InsertMouse { get; set; } = BindingEditorViewModel.MouseFunctions[0];
    [ObservableProperty] public partial Macro? InsertMacro { get; set; }
    [ObservableProperty] public partial string InsertText { get; set; } = "";
    [ObservableProperty] public partial double LoopCount { get; set; } = 2;

    /// <summary>Other macros a macro can play (not itself).</summary>
    public IEnumerable<Macro> OtherMacros => Macros.Where(m => m.Id != SelectedMacro?.Id);

    [RelayCommand]
    void StartAdding(string what)
    {
        if (SelectedMacro == null)
            NewMacro();
        Adding = Adding == what ? null : what;
        OnPropertyChanged(nameof(OtherMacros));
    }

    [RelayCommand]
    void InsertDelay() => Insert(new DelayStep((int)InsertDelayMs));

    /// <summary>A keystroke caught in the ADD box: a press and a release.</summary>
    public void InsertKeystroke(ushort code) => Insert(new KeyStep(code, true), new KeyStep(code, false));

    [RelayCommand]
    void InsertMouseFunction()
    {
        switch (InsertMouse.Value)
        {
            case MouseFunction.ScrollUp: Insert(new ScrollStep(1)); break;
            case MouseFunction.ScrollDown: Insert(new ScrollStep(-1)); break;
            case MouseFunction.ScrollLeft: Insert(new ScrollStep(-1, true)); break;
            case MouseFunction.ScrollRight: Insert(new ScrollStep(1, true)); break;
            case MouseFunction.DoubleClick:
                Insert(new KeyStep(Core.Input.KeyCodes.BtnLeft, true), new KeyStep(Core.Input.KeyCodes.BtnLeft, false),
                       new KeyStep(Core.Input.KeyCodes.BtnLeft, true), new KeyStep(Core.Input.KeyCodes.BtnLeft, false));
                break;
            default:
                ushort button = InsertMouse.Value switch
                {
                    MouseFunction.RightClick => Core.Input.KeyCodes.BtnRight,
                    MouseFunction.ScrollClick => Core.Input.KeyCodes.BtnMiddle,
                    MouseFunction.Back => Core.Input.KeyCodes.BtnSide,
                    MouseFunction.Forward => Core.Input.KeyCodes.BtnExtra,
                    _ => Core.Input.KeyCodes.BtnLeft,
                };
                Insert(new KeyStep(button, true), new KeyStep(button, false));
                break;
        }
    }

    [RelayCommand]
    void InsertMacroCall()
    {
        if (InsertMacro is { } macro)
            Insert(new MacroCallStep(macro.Id));
    }

    [RelayCommand]
    void InsertLaunch()
    {
        if (!string.IsNullOrWhiteSpace(InsertText))
            Insert(new LaunchStep(InsertText.Trim()));
    }

    [RelayCommand]
    void InsertRun()
    {
        if (!string.IsNullOrWhiteSpace(InsertText))
            Insert(new RunStep(InsertText.Trim()));
    }

    [RelayCommand]
    void InsertTextStep()
    {
        if (InsertText.Length > 0)
            Insert(new TextStep(InsertText));
    }

    /// <summary>A loop goes around the selected step (or at the end, empty, to fill).</summary>
    [RelayCommand]
    void InsertLoop()
    {
        var steps = _work.ToList();
        if (SelectedStep is { } row)
        {
            steps.Insert(row.Index + 1, new LoopEndStep());
            steps.Insert(row.Index, new LoopStartStep((int)LoopCount));
            Edit(steps, select: row.Index);
        }
        else
        {
            steps.Add(new LoopStartStep((int)LoopCount));
            steps.Add(new LoopEndStep());
            Edit(steps, select: steps.Count - 2);
        }
    }

    /// <summary>Puts steps after the selected one, or at the end.</summary>
    void Insert(params MacroStep[] added)
    {
        var steps = _work.ToList();
        int at = SelectedStep is { } row ? row.Index + 1 : steps.Count;
        steps.InsertRange(at, added);
        Edit(steps, select: at + added.Length - 1);
    }

    [RelayCommand]
    void DeleteStep()
    {
        if (SelectedStep is not { } row)
            return;
        var steps = _work.ToList();
        steps.RemoveAt(row.Index);
        Edit(steps, select: Math.Min(row.Index, steps.Count - 1));
    }

    [RelayCommand]
    void MoveUp() => Move(-1);

    [RelayCommand]
    void MoveDown() => Move(1);

    void Move(int by)
    {
        if (SelectedStep is not { } row)
            return;
        int to = row.Index + by;
        if (to < 0 || to >= _work.Count)
            return;
        var steps = _work.ToList();
        (steps[row.Index], steps[to]) = (steps[to], steps[row.Index]);
        Edit(steps, select: to);
    }

    [RelayCommand]
    void RemoveDelays() => Edit(_work.Where(s => s is not (DelayStep or RandomDelayStep)).ToList());

    /// <summary>Changes the selected delay's length.</summary>
    public void SetDelay(int milliseconds)
    {
        if (SelectedStep is not { Step: DelayStep } row)
            return;
        var steps = _work.ToList();
        steps[row.Index] = new DelayStep(Math.Max(0, milliseconds));
        Edit(steps, select: row.Index);
    }

    // ---- Recording ---------------------------------------------------------------------------------------

    /// <summary>Asks the view to open the RECORD options (for screenshots).</summary>
    public event Action? RecordOptionsRequested;

    public void ShowRecordOptions() => RecordOptionsRequested?.Invoke();

    [ObservableProperty] public partial Choice<int> StartDelay { get; set; } = StartDelays[0];
    [ObservableProperty] public partial bool DelayRecorded { get; set; } = true;
    [ObservableProperty] public partial bool DelayFixed { get; set; }
    [ObservableProperty] public partial bool DelayRandom { get; set; }
    [ObservableProperty] public partial bool DelayNone { get; set; }
    [ObservableProperty] public partial double FixedDelaySeconds { get; set; } = 0.05;
    [ObservableProperty] public partial double RandomMinSeconds { get; set; } = 0.02;
    [ObservableProperty] public partial double RandomMaxSeconds { get; set; } = 0.08;
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string RecordLabel { get; set; } = "RECORD";

    RecordDelayMode Mode => DelayFixed ? RecordDelayMode.Default : DelayRandom ? RecordDelayMode.Randomized : DelayNone ? RecordDelayMode.None : RecordDelayMode.Recorded;

    CancellationTokenSource? _countdown;

    [RelayCommand]
    async Task Record()
    {
        if (SelectedMacro == null)
            NewMacro();
        if (IsRecording)
        {
            await StopRecordingAsync();
            return;
        }
        if (_countdown != null)
        {
            // A second press during the countdown calls it off.
            _countdown.Cancel();
            return;
        }
        if (StartDelay.Value > 0)
        {
            _countdown = new CancellationTokenSource();
            try
            {
                for (int s = StartDelay.Value; s > 0; s--)
                {
                    RecordLabel = $"STARTING IN {s}";
                    await Task.Delay(1000, _countdown.Token);
                }
            }
            catch (OperationCanceledException)
            {
                RecordLabel = "RECORD";
                return;
            }
            finally
            {
                _countdown = null;
            }
        }
        try
        {
            await client.StartRecordingAsync();
            IsRecording = true;
            RecordLabel = "STOP";
        }
        catch (Exception e)
        {
            RecordLabel = "RECORD";
            main.Fail("Could not start recording", e);
        }
    }

    async Task StopRecordingAsync()
    {
        IsRecording = false;
        RecordLabel = "RECORD";
        try
        {
            var recorded = await client.StopRecordingAsync(Mode, (int)Math.Round(FixedDelaySeconds * 1000),
                (int)Math.Round(RandomMinSeconds * 1000), (int)Math.Round(RandomMaxSeconds * 1000));
            Insert([.. recorded]);
        }
        catch (Exception e)
        {
            main.Fail("Could not stop recording", e);
        }
    }

    // ---- KEY BINDS ---------------------------------------------------------------------------------------

    public ObservableCollection<MacroAssignment> Assignments { get; } = [];

    [ObservableProperty] public partial bool Assigning { get; set; }
    [ObservableProperty] public partial DeviceViewModel? AssignDevice { get; set; }
    [ObservableProperty] public partial DeviceKey? AssignKey { get; set; }
    [ObservableProperty] public partial Macro? AssignMacro { get; set; }
    [ObservableProperty] public partial Choice<MacroPlayback> AssignPlayback { get; set; } = BindingEditorViewModel.Playbacks[0];

    public IEnumerable<DeviceViewModel> Devices => main.Devices;
    public IReadOnlyList<DeviceKey> AssignKeys => AssignDevice?.Rows.Where(r => r.Bindable).Select(r => r.Key).ToList() ?? [];

    partial void OnAssignDeviceChanged(DeviceViewModel? value)
    {
        OnPropertyChanged(nameof(AssignKeys));
        AssignKey = AssignKeys.FirstOrDefault();
    }

    /// <summary>A device's keys or the library changed: the KEY BINDS list follows when it is showing.</summary>
    public void KeymapsChanged()
    {
        if (ShowKeyBinds)
            RefreshAssignments();
    }

    public void RefreshAssignments()
    {
        Assignments.Clear();
        foreach (var device in main.Devices)
        {
            var keymap = device.CurrentSettings.ActiveKeymapOrFirst;
            foreach (var row in device.Rows)
                if (keymap.Standard.TryGetValue(row.Key.Source, out var b) && b is MacroBinding m)
                    Assignments.Add(new MacroAssignment(device, row.Key, m, Library.Find(m.MacroId)?.Name ?? "missing"));
        }
        OnPropertyChanged(nameof(Devices));
    }

    [RelayCommand]
    void StartAssigning()
    {
        Assigning = true;
        AssignDevice ??= main.Devices.FirstOrDefault();
        AssignMacro ??= SelectedMacro ?? Macros.FirstOrDefault();
    }

    [RelayCommand]
    void Assign()
    {
        if (AssignDevice is not { } device || AssignKey is not { } key || AssignMacro is not { } macro)
            return;
        device.Bind(key, new MacroBinding(macro.Id, AssignPlayback.Value, 2));
        Assigning = false;
        RefreshAssignments();
    }

    [RelayCommand]
    void Unassign(MacroAssignment assignment)
    {
        assignment.Device.Bind(assignment.Key, DefaultBinding.Instance);
        RefreshAssignments();
    }
}
