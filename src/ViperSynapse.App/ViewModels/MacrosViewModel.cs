using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.App.Services;
using ViperSynapse.Core.Bindings;

namespace ViperSynapse.App.ViewModels;

/// <summary>One line of the macro's action list.</summary>
public sealed record StepRow(int Index, MacroStep Step)
{
    public string Number => (Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public string Glyph => Step switch
    {
        KeyStep { Down: true } => "↓",
        KeyStep => "↑",
        DelayStep => "⏱",
        ScrollStep => "⇕",
        TextStep => "T",
        RunStep => "▶",
        _ => "",
    };

    public string Text => Step switch
    {
        KeyStep k => BindingText.KeyLabel(k.Code),
        DelayStep d => $"{d.Milliseconds / 1000.0:0.000} s",
        ScrollStep s => s.Horizontal ? (s.Clicks > 0 ? $"Scroll Right ×{s.Clicks}" : $"Scroll Left ×{-s.Clicks}") : (s.Clicks > 0 ? $"Scroll Up ×{s.Clicks}" : $"Scroll Down ×{-s.Clicks}"),
        TextStep t => $"Type \"{t.Text}\"",
        RunStep r => $"Run {r.Command}",
        _ => "",
    };

    public bool IsDelay => Step is DelayStep;
}

/// <summary>The MACROS tab: the macro library, the recorder and the action list editor. Every change is saved
/// to the service as soon as it is made, as Synapse does.</summary>
public sealed partial class MacrosViewModel(DaemonClient client, MainViewModel main) : TabViewModel
{
    public override string Header => "MACRO";
    public override TabIcon Icon => TabIcon.Macro;

    public MacroLibrary Library { get; private set; } = MacroLibrary.Empty;

    public ObservableCollection<Macro> Macros { get; } = [];
    public ObservableCollection<StepRow> Steps { get; } = [];

    public static IReadOnlyList<Choice<RecordDelayMode>> DelayModes { get; } =
    [
        new(RecordDelayMode.Recorded, "Record Delay"),
        new(RecordDelayMode.Default, "Default Delay"),
        new(RecordDelayMode.None, "No Delay"),
    ];

    [ObservableProperty] public partial Macro? SelectedMacro { get; set; }
    [ObservableProperty] public partial StepRow? SelectedStep { get; set; }
    [ObservableProperty] public partial Choice<RecordDelayMode> DelayMode { get; set; } = DelayModes[0];
    [ObservableProperty] public partial double DefaultDelayMs { get; set; } = 50;
    [ObservableProperty] public partial double StartDelaySeconds { get; set; } = 3;
    [ObservableProperty] public partial bool UseStartDelay { get; set; }
    [ObservableProperty] public partial bool IsRecording { get; set; }
    [ObservableProperty] public partial string RecordLabel { get; set; } = "RECORD";
    [ObservableProperty] public partial string MacroName { get; set; } = "";
    [ObservableProperty] public partial string InsertText { get; set; } = "";
    [ObservableProperty] public partial double InsertDelayMs { get; set; } = 100;

    public bool HasMacro => SelectedMacro != null;
    public bool DefaultDelayVisible => DelayMode.Value == RecordDelayMode.Default;

    partial void OnDelayModeChanged(Choice<RecordDelayMode> value) => OnPropertyChanged(nameof(DefaultDelayVisible));

    CancellationTokenSource? _countdown;

    public async Task LoadAsync()
    {
        var library = await client.GetMacrosAsync().ConfigureAwait(false);
        await Dispatcher.UIThread.InvokeAsync(() => SetLibrary(library, SelectedMacro?.Id));
    }

    void SetLibrary(MacroLibrary library, Guid? select)
    {
        Library = library;
        Macros.Clear();
        foreach (var m in library.Macros)
            Macros.Add(m);
        SelectedMacro = Macros.FirstOrDefault(m => m.Id == select) ?? Macros.FirstOrDefault();
    }

    partial void OnSelectedMacroChanged(Macro? value)
    {
        Steps.Clear();
        if (value != null)
            for (int i = 0; i < value.Steps.Count; i++)
                Steps.Add(new StepRow(i, value.Steps[i]));
        _renaming = true;
        MacroName = value?.Name ?? "";
        _renaming = false;
        OnPropertyChanged(nameof(HasMacro));
    }

    bool _renaming;

    partial void OnMacroNameChanged(string value)
    {
        if (_renaming || SelectedMacro == null || string.IsNullOrWhiteSpace(value) || value == SelectedMacro.Name)
            return;
        Replace(SelectedMacro with { Name = value.Trim() });
    }

    [RelayCommand]
    void NewMacro()
    {
        var macro = Macro.Create($"Macro {Library.Macros.Count + 1}", []);
        Commit(new MacroLibrary([.. Library.Macros, macro]), macro.Id);
    }

    [RelayCommand]
    void DeleteMacro()
    {
        if (SelectedMacro is not { } doomed)
            return;
        Commit(new MacroLibrary(Library.Macros.Where(m => m.Id != doomed.Id).ToList()), null);
    }

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
        if (UseStartDelay)
        {
            _countdown = new CancellationTokenSource();
            try
            {
                for (int s = (int)StartDelaySeconds; s > 0; s--)
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
            var steps = await client.StopRecordingAsync(DelayMode.Value, (int)DefaultDelayMs);
            if (SelectedMacro is { } macro)
                Replace(macro with { Steps = [.. macro.Steps, .. steps] });
        }
        catch (Exception e)
        {
            main.Fail("Could not stop recording", e);
        }
    }

    [RelayCommand]
    void AddDelay() => Insert(new DelayStep((int)InsertDelayMs));

    [RelayCommand]
    void AddText()
    {
        if (InsertText.Length > 0)
            Insert(new TextStep(InsertText));
    }

    [RelayCommand]
    void AddRun()
    {
        if (!string.IsNullOrWhiteSpace(InsertText))
            Insert(new RunStep(InsertText.Trim()));
    }

    /// <summary>A keystroke caught in the editor: a press and a release.</summary>
    public void AddKeystroke(ushort code)
    {
        Insert(new KeyStep(code, true));
        Insert(new KeyStep(code, false));
    }

    [RelayCommand]
    void DeleteStep()
    {
        if (SelectedMacro is not { } macro || SelectedStep is not { } row)
            return;
        var steps = macro.Steps.ToList();
        steps.RemoveAt(row.Index);
        Replace(macro with { Steps = steps }, Math.Min(row.Index, steps.Count - 1));
    }

    [RelayCommand]
    void MoveUp() => Move(-1);

    [RelayCommand]
    void MoveDown() => Move(1);

    [RelayCommand]
    void RemoveDelays()
    {
        if (SelectedMacro is { } macro)
            Replace(macro with { Steps = macro.Steps.Where(s => s is not DelayStep).ToList() });
    }

    void Move(int by)
    {
        if (SelectedMacro is not { } macro || SelectedStep is not { } row)
            return;
        int to = row.Index + by;
        if (to < 0 || to >= macro.Steps.Count)
            return;
        var steps = macro.Steps.ToList();
        (steps[row.Index], steps[to]) = (steps[to], steps[row.Index]);
        Replace(macro with { Steps = steps }, to);
    }

    /// <summary>Puts a step after the selected one, or at the end.</summary>
    void Insert(MacroStep step)
    {
        if (SelectedMacro is not { } macro)
            return;
        var steps = macro.Steps.ToList();
        int at = SelectedStep is { } row ? row.Index + 1 : steps.Count;
        steps.Insert(at, step);
        Replace(macro with { Steps = steps }, at);
    }

    void Replace(Macro macro, int? selectStep = null)
    {
        Commit(new MacroLibrary(Library.Macros.Select(m => m.Id == macro.Id ? macro : m).ToList()), macro.Id);
        if (selectStep is int i && i >= 0 && i < Steps.Count)
            SelectedStep = Steps[i];
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
}
