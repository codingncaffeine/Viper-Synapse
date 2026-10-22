using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Input;

namespace ViperSynapse.App.ViewModels;

/// <summary>The functions on the flyout's rail, in Synapse's order.</summary>
public enum FunctionKind
{
    Default,
    Keyboard,
    Mouse,
    Macro,
    Hypershift,
    SwitchProfile,
    SwitchKeymap,
    Launch,
    Multimedia,
    Text,
    Disable,
}

public sealed record FunctionEntry(FunctionKind Kind, string Title, string Glyph);

public sealed record Choice<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The flyout that opens on a key: the function rail on its left, the chosen function's settings on its
/// right, CANCEL and SAVE at the bottom. Nothing changes until SAVE.
/// </summary>
public sealed partial class BindingEditorViewModel : ObservableObject
{
    readonly Action<KeyRowViewModel, Binding> _save;
    readonly Action _close;

    public static IReadOnlyList<FunctionEntry> Functions { get; } =
    [
        new(FunctionKind.Default, "DEFAULT", "↺"),
        new(FunctionKind.Keyboard, "KEYBOARD FUNCTION", "⌨"),
        new(FunctionKind.Mouse, "MOUSE FUNCTION", "🖱"),
        new(FunctionKind.Macro, "MACRO", "M"),
        new(FunctionKind.Hypershift, "RAZER HYPERSHIFT", "⇧"),
        new(FunctionKind.SwitchProfile, "SWITCH PROFILE", "⇄"),
        new(FunctionKind.SwitchKeymap, "SWITCH KEYMAP", "▦"),
        new(FunctionKind.Launch, "LAUNCH PROGRAM", "▶"),
        new(FunctionKind.Multimedia, "MULTIMEDIA", "♪"),
        new(FunctionKind.Text, "TEXT FUNCTION", "T"),
        new(FunctionKind.Disable, "DISABLE", "⊘"),
    ];

    public static IReadOnlyList<Choice<MouseFunction>> MouseFunctions { get; } =
        Enum.GetValues<MouseFunction>().Select(f => new Choice<MouseFunction>(f, BindingText.MouseLabel(f))).ToList();

    public static IReadOnlyList<Choice<MacroPlayback>> Playbacks { get; } =
    [
        new(MacroPlayback.Once, "Play once"),
        new(MacroPlayback.Times, "Play a set number of times"),
        new(MacroPlayback.WhileHeld, "Repeat while the key is held"),
        new(MacroPlayback.Toggle, "Toggle: press to start, press again to stop"),
    ];

    public static IReadOnlyList<Choice<ushort>> MediaKeys { get; } = new[]
    {
        "KEY_VOLUMEUP", "KEY_VOLUMEDOWN", "KEY_MUTE", "KEY_MICMUTE", "KEY_PLAYPAUSE", "KEY_PREVIOUSSONG", "KEY_NEXTSONG", "KEY_STOPCD",
    }.Select(n => KeyCodes.TryParse(n, out var c) ? new Choice<ushort>(c, BindingText.KeyLabel(c)) : null).OfType<Choice<ushort>>().ToList();

    public IReadOnlyList<Choice<Guid?>> ProfileChoices { get; }
    public IReadOnlyList<Choice<int>> KeymapChoices { get; }
    public IReadOnlyList<Choice<Guid>> MacroChoices { get; }

    public KeyRowViewModel Row { get; }
    public string Title { get; }

    public BindingEditorViewModel(KeyRowViewModel row, Binding binding, MacroLibrary macros, IReadOnlyList<Profile> profiles, int keymaps,
        Action<KeyRowViewModel, Binding> save, Action close)
    {
        Row = row;
        _save = save;
        _close = close;
        Title = $"{row.Number}  {row.Key.Label}".Trim();
        ProfileChoices = [new(null, "Cycle Up Profile"), new(Guid.Empty, "Cycle Down Profile"), .. profiles.Select(p => new Choice<Guid?>(p.Id, p.Name))];
        KeymapChoices = [new(-1, "Cycle Up Keymap"), new(-2, "Cycle Down Keymap"), .. Enumerable.Range(0, Math.Max(1, keymaps)).Select(i => new Choice<int>(i, $"Keymap {i + 1}"))];
        MacroChoices = macros.Macros.Select(m => new Choice<Guid>(m.Id, m.Name)).ToList();
        SelectedMouse = MouseFunctions[0];
        SelectedPlayback = Playbacks[0];
        SelectedMedia = MediaKeys[0];
        SelectedProfileChoice = ProfileChoices[0];
        SelectedKeymapChoice = KeymapChoices[0];
        SelectedMacro = MacroChoices.FirstOrDefault();
        LoadFrom(binding);
    }

    [ObservableProperty]
    public partial FunctionEntry SelectedFunction { get; set; } = Functions[0];

    public FunctionKind Kind => SelectedFunction.Kind;

    partial void OnSelectedFunctionChanged(FunctionEntry value) => OnPropertyChanged(nameof(Kind));

    // Keyboard function
    [ObservableProperty] public partial ushort CapturedKey { get; set; }
    [ObservableProperty] public partial bool Ctrl { get; set; }
    [ObservableProperty] public partial bool Shift { get; set; }
    [ObservableProperty] public partial bool Alt { get; set; }
    [ObservableProperty] public partial bool Super { get; set; }
    [ObservableProperty] public partial bool Turbo { get; set; }
    [ObservableProperty] public partial double TurboRate { get; set; } = 10;

    public string CapturedText => CapturedKey == 0 ? "Press a key" : BindingText.KeyLabel(CapturedKey);

    partial void OnCapturedKeyChanged(ushort value) => OnPropertyChanged(nameof(CapturedText));

    // Mouse function
    [ObservableProperty] public partial Choice<MouseFunction> SelectedMouse { get; set; }

    // Macro
    [ObservableProperty] public partial Choice<Guid>? SelectedMacro { get; set; }
    [ObservableProperty] public partial Choice<MacroPlayback> SelectedPlayback { get; set; }
    [ObservableProperty] public partial double PlayTimes { get; set; } = 2;

    public bool HasMacros => MacroChoices.Count > 0;
    public bool TimesVisible => SelectedPlayback.Value == MacroPlayback.Times;

    partial void OnSelectedPlaybackChanged(Choice<MacroPlayback> value) => OnPropertyChanged(nameof(TimesVisible));

    // Switches, launch, media, text
    [ObservableProperty] public partial Choice<Guid?> SelectedProfileChoice { get; set; }
    [ObservableProperty] public partial Choice<int> SelectedKeymapChoice { get; set; }
    [ObservableProperty] public partial string Command { get; set; } = "";
    [ObservableProperty] public partial Choice<ushort> SelectedMedia { get; set; }
    [ObservableProperty] public partial string Text { get; set; } = "";

    public bool CanSave => Kind switch
    {
        FunctionKind.Keyboard => CapturedKey != 0,
        FunctionKind.Macro => SelectedMacro != null,
        FunctionKind.Launch => !string.IsNullOrWhiteSpace(Command),
        FunctionKind.Text => Text.Length > 0,
        _ => true,
    };

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName != nameof(CanSave))
            OnPropertyChanged(nameof(CanSave));
    }

    void LoadFrom(Binding binding)
    {
        FunctionKind kind = FunctionKind.Default;
        switch (binding)
        {
            case KeyboardBinding k when MediaKeys.Any(m => m.Value == k.Key) && k.Modifiers == Modifiers.None && !k.Turbo:
                kind = FunctionKind.Multimedia;
                SelectedMedia = MediaKeys.First(m => m.Value == k.Key);
                break;
            case KeyboardBinding k:
                kind = FunctionKind.Keyboard;
                CapturedKey = k.Key;
                (Ctrl, Shift, Alt, Super) = (k.Modifiers.HasFlag(Modifiers.Ctrl), k.Modifiers.HasFlag(Modifiers.Shift), k.Modifiers.HasFlag(Modifiers.Alt), k.Modifiers.HasFlag(Modifiers.Super));
                (Turbo, TurboRate) = (k.Turbo, k.TurboRate);
                break;
            case MouseBinding m:
                kind = FunctionKind.Mouse;
                SelectedMouse = MouseFunctions.First(f => f.Value == m.Function);
                (Turbo, TurboRate) = (m.Turbo, m.TurboRate);
                break;
            case MacroBinding m:
                kind = FunctionKind.Macro;
                SelectedMacro = MacroChoices.FirstOrDefault(c => c.Value == m.MacroId);
                SelectedPlayback = Playbacks.First(p => p.Value == m.Playback);
                PlayTimes = m.Times;
                break;
            case HypershiftBinding:
                kind = FunctionKind.Hypershift;
                break;
            case SwitchProfileBinding p:
                kind = FunctionKind.SwitchProfile;
                SelectedProfileChoice = p.Direction switch
                {
                    CycleDirection.Next => ProfileChoices[0],
                    CycleDirection.Previous => ProfileChoices[1],
                    _ => ProfileChoices.FirstOrDefault(c => c.Value == p.ProfileId) ?? ProfileChoices[0],
                };
                break;
            case SwitchKeymapBinding k:
                kind = FunctionKind.SwitchKeymap;
                SelectedKeymapChoice = k.Direction switch
                {
                    CycleDirection.Next => KeymapChoices[0],
                    CycleDirection.Previous => KeymapChoices[1],
                    _ => KeymapChoices.FirstOrDefault(c => c.Value == k.Keymap) ?? KeymapChoices[0],
                };
                break;
            case LaunchBinding l:
                kind = FunctionKind.Launch;
                Command = l.Command;
                break;
            case TextBinding t:
                kind = FunctionKind.Text;
                Text = t.Text;
                break;
            case DisableBinding:
                kind = FunctionKind.Disable;
                break;
        }
        SelectedFunction = Functions.First(f => f.Kind == kind);
    }

    public Binding Build()
    {
        var modifiers = (Ctrl ? Modifiers.Ctrl : 0) | (Shift ? Modifiers.Shift : 0) | (Alt ? Modifiers.Alt : 0) | (Super ? Modifiers.Super : 0);
        return Kind switch
        {
            FunctionKind.Keyboard => new KeyboardBinding(CapturedKey, modifiers, Turbo, (int)TurboRate),
            FunctionKind.Mouse => new MouseBinding(SelectedMouse.Value, Turbo, (int)TurboRate),
            FunctionKind.Macro => new MacroBinding(SelectedMacro!.Value, SelectedPlayback.Value, (int)PlayTimes),
            FunctionKind.Hypershift => new HypershiftBinding(),
            FunctionKind.SwitchProfile => SelectedProfileChoice.Value switch
            {
                null => new SwitchProfileBinding(CycleDirection.Next),
                var id when id == Guid.Empty => new SwitchProfileBinding(CycleDirection.Previous),
                var id => new SwitchProfileBinding(CycleDirection.Specific, id),
            },
            FunctionKind.SwitchKeymap => SelectedKeymapChoice.Value switch
            {
                -1 => new SwitchKeymapBinding(CycleDirection.Next),
                -2 => new SwitchKeymapBinding(CycleDirection.Previous),
                var i => new SwitchKeymapBinding(CycleDirection.Specific, i),
            },
            FunctionKind.Launch => new LaunchBinding(Command.Trim()),
            FunctionKind.Multimedia => new KeyboardBinding(SelectedMedia.Value),
            FunctionKind.Text => new TextBinding(Text),
            FunctionKind.Disable => new DisableBinding(),
            _ => DefaultBinding.Instance,
        };
    }

    [RelayCommand]
    void Save()
    {
        if (CanSave)
            _save(Row, Build());
    }

    [RelayCommand]
    void Cancel() => _close();
}
