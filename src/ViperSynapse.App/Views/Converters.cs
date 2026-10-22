using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Media;
using ViperSynapse.Core.Input;

namespace ViperSynapse.App.Views;

public static class Converters
{
    /// <summary>True when the bound enum's name equals the parameter: shows the flyout section for one function.</summary>
    public static readonly IValueConverter Is = new FuncValueConverter<object?, string?, bool>((value, parameter) =>
        value?.ToString() == parameter);

    /// <summary>Green for a changed binding, white for a key doing what it does by itself.</summary>
    public static readonly IValueConverter ChangedBrush = new FuncValueConverter<bool, IBrush>(changed =>
        changed ? new SolidColorBrush(Color.Parse("#44D62C")) : Brushes.White);

    /// <summary>Keyboard and mouse functions offer Turbo.</summary>
    public static readonly IValueConverter IsTurbo = new FuncValueConverter<object?, bool>(value =>
        value?.ToString() is "Keyboard" or "Mouse");

    /// <summary>Full strength when true, faded when false: controls the device handles by itself.</summary>
    public static readonly IValueConverter Dim = new FuncValueConverter<bool, double>(on => on ? 1 : 0.5);

    public static readonly IValueConverter ZoneLabel = new FuncValueConverter<string?, string>(z => ViewModels.LightingViewModel.ZoneLabel(z ?? ""));

    public static readonly IValueConverter StickDirections = new FuncValueConverter<ViperSynapse.Core.Bindings.ThumbstickMode, int>(m => m switch
    {
        ViperSynapse.Core.Bindings.ThumbstickMode.FourWay => 4,
        ViperSynapse.Core.Bindings.ThumbstickMode.EightWay => 8,
        _ => 0,
    });

    // Top-bar tab icons. Synapse 4 draws Linked Games and Macro as a black glyph in a green disc, device tabs and
    // Settings as a light outline glyph.
    public static readonly IValueConverter TabInDisc = new FuncValueConverter<ViewModels.TabIcon, bool>(i =>
        i is ViewModels.TabIcon.LinkedGames or ViewModels.TabIcon.Macro);

    public static readonly IValueConverter TabGlyph = new FuncValueConverter<ViewModels.TabIcon, string>(i => i switch
    {
        ViewModels.TabIcon.Keypad => "▦",
        ViewModels.TabIcon.Mouse => "⬮",
        ViewModels.TabIcon.Keyboard => "⌨",
        ViewModels.TabIcon.Device => "◻",
        ViewModels.TabIcon.LinkedGames => "◉◉",
        ViewModels.TabIcon.Macro => "M",
        ViewModels.TabIcon.Settings => "⚙",
        _ => "",
    });

    public static readonly IValueConverter TabGlyphBrush = new FuncValueConverter<ViewModels.TabIcon, IBrush>(i =>
        i is ViewModels.TabIcon.LinkedGames or ViewModels.TabIcon.Macro ? Brushes.Black : new SolidColorBrush(Color.Parse("#D0D0D0")));

    public static readonly IValueConverter Equal = new FuncValueConverter<object?, object?, bool>((value, parameter) =>
        Equals(value?.ToString(), parameter?.ToString()));
}

/// <summary>Physical keys as Avalonia reports them, as Linux key codes: what the "press a key" box records.</summary>
public static class PhysicalKeys
{
    public static ushort ToLinux(PhysicalKey key)
    {
        string name = key switch
        {
            >= PhysicalKey.A and <= PhysicalKey.Z => "KEY_" + key.ToString(),
            >= PhysicalKey.Digit0 and <= PhysicalKey.Digit9 => "KEY_" + key.ToString()[5..],
            >= PhysicalKey.F1 and <= PhysicalKey.F24 => "KEY_" + key.ToString(),
            >= PhysicalKey.NumPad0 and <= PhysicalKey.NumPad9 => "KEY_KP" + key.ToString()[6..],
            PhysicalKey.Escape => "KEY_ESC",
            PhysicalKey.Backquote => "KEY_GRAVE",
            PhysicalKey.Minus => "KEY_MINUS",
            PhysicalKey.Equal => "KEY_EQUAL",
            PhysicalKey.Backspace => "KEY_BACKSPACE",
            PhysicalKey.Tab => "KEY_TAB",
            PhysicalKey.BracketLeft => "KEY_LEFTBRACE",
            PhysicalKey.BracketRight => "KEY_RIGHTBRACE",
            PhysicalKey.Backslash => "KEY_BACKSLASH",
            PhysicalKey.CapsLock => "KEY_CAPSLOCK",
            PhysicalKey.Semicolon => "KEY_SEMICOLON",
            PhysicalKey.Quote => "KEY_APOSTROPHE",
            PhysicalKey.Enter => "KEY_ENTER",
            PhysicalKey.ShiftLeft => "KEY_LEFTSHIFT",
            PhysicalKey.ShiftRight => "KEY_RIGHTSHIFT",
            PhysicalKey.Comma => "KEY_COMMA",
            PhysicalKey.Period => "KEY_DOT",
            PhysicalKey.Slash => "KEY_SLASH",
            PhysicalKey.ControlLeft => "KEY_LEFTCTRL",
            PhysicalKey.ControlRight => "KEY_RIGHTCTRL",
            PhysicalKey.AltLeft => "KEY_LEFTALT",
            PhysicalKey.AltRight => "KEY_RIGHTALT",
            PhysicalKey.MetaLeft => "KEY_LEFTMETA",
            PhysicalKey.MetaRight => "KEY_RIGHTMETA",
            PhysicalKey.Space => "KEY_SPACE",
            PhysicalKey.ContextMenu => "KEY_COMPOSE",
            PhysicalKey.Insert => "KEY_INSERT",
            PhysicalKey.Delete => "KEY_DELETE",
            PhysicalKey.Home => "KEY_HOME",
            PhysicalKey.End => "KEY_END",
            PhysicalKey.PageUp => "KEY_PAGEUP",
            PhysicalKey.PageDown => "KEY_PAGEDOWN",
            PhysicalKey.ArrowUp => "KEY_UP",
            PhysicalKey.ArrowDown => "KEY_DOWN",
            PhysicalKey.ArrowLeft => "KEY_LEFT",
            PhysicalKey.ArrowRight => "KEY_RIGHT",
            PhysicalKey.PrintScreen => "KEY_SYSRQ",
            PhysicalKey.ScrollLock => "KEY_SCROLLLOCK",
            PhysicalKey.Pause => "KEY_PAUSE",
            PhysicalKey.NumLock => "KEY_NUMLOCK",
            PhysicalKey.NumPadDivide => "KEY_KPSLASH",
            PhysicalKey.NumPadMultiply => "KEY_KPASTERISK",
            PhysicalKey.NumPadSubtract => "KEY_KPMINUS",
            PhysicalKey.NumPadAdd => "KEY_KPPLUS",
            PhysicalKey.NumPadEnter => "KEY_KPENTER",
            PhysicalKey.NumPadDecimal => "KEY_KPDOT",
            PhysicalKey.IntlBackslash => "KEY_102ND",
            _ => "",
        };
        return KeyCodes.TryParse(name.ToUpper(CultureInfo.InvariantCulture), out var code) ? code : (ushort)0;
    }
}
