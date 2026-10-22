using ViperSynapse.Core.Bindings;
using ViperSynapse.Core.Input;

namespace ViperSynapse.Core.Engine;

/// <summary>
/// Turns text into key presses for a US layout, the layout the virtual keyboard declares. Characters a
/// US keyboard cannot type are skipped and counted.
/// </summary>
public static class TextTyper
{
    static readonly Dictionary<char, (ushort Code, bool Shift)> Keys = Build();

    static Dictionary<char, (ushort, bool)> Build()
    {
        var map = new Dictionary<char, (ushort, bool)>();
        void Add(string plain, string shifted, params string[] names)
        {
            for (int i = 0; i < names.Length; i++)
            {
                if (!KeyCodes.TryParse(names[i], out var code))
                    throw new InvalidOperationException($"Unknown key {names[i]}");
                map[plain[i]] = (code, false);
                if (shifted.Length > i && shifted[i] != ' ')
                    map[shifted[i]] = (code, true);
            }
        }
        for (char c = 'a'; c <= 'z'; c++)
        {
            KeyCodes.TryParse("KEY_" + char.ToUpperInvariant(c), out var code);
            map[c] = (code, false);
            map[char.ToUpperInvariant(c)] = (code, true);
        }
        Add("1234567890", "!@#$%^&*()", "KEY_1", "KEY_2", "KEY_3", "KEY_4", "KEY_5", "KEY_6", "KEY_7", "KEY_8", "KEY_9", "KEY_0");
        Add("-=[]\\;',./`", "_+{}|:\"<>?~", "KEY_MINUS", "KEY_EQUAL", "KEY_LEFTBRACE", "KEY_RIGHTBRACE", "KEY_BACKSLASH",
            "KEY_SEMICOLON", "KEY_APOSTROPHE", "KEY_COMMA", "KEY_DOT", "KEY_SLASH", "KEY_GRAVE");
        Add(" \n\t", "", "KEY_SPACE", "KEY_ENTER", "KEY_TAB");
        return map;
    }

    public static bool CanType(char c) => Keys.ContainsKey(c);

    /// <summary>The key steps that type <paramref name="text"/>.</summary>
    public static IEnumerable<KeyStep> Steps(string text)
    {
        foreach (char c in text.Replace("\r\n", "\n"))
        {
            if (!Keys.TryGetValue(c, out var key))
                continue;
            if (key.Shift) yield return new KeyStep(KeyCodes.LeftShift, true);
            yield return new KeyStep(key.Code, true);
            yield return new KeyStep(key.Code, false);
            if (key.Shift) yield return new KeyStep(KeyCodes.LeftShift, false);
        }
    }
}
