namespace ViperSynapse.Core.Bindings;

/// <summary>A global shortcut: holding <see cref="Keys"/> together on a Razer device does <see cref="Binding"/>, in
/// every profile and keymap (Synapse's Global Shortcuts). Keys are the device's own key codes, as keymaps use.</summary>
public sealed record GlobalShortcut(Guid Id, IReadOnlyList<ushort> Keys, Binding Binding)
{
    public static GlobalShortcut Create(IReadOnlyList<ushort> keys, Binding binding) => new(Guid.NewGuid(), keys, binding);
}

/// <summary>Every global shortcut, shared by all devices.</summary>
public sealed record ShortcutList(IReadOnlyList<GlobalShortcut> Shortcuts)
{
    public static ShortcutList Empty { get; } = new([]);

    /// <summary>The shortcut a press of <paramref name="code"/> completes while the keys <paramref name="held"/>
    /// says are down: the one with the most keys wins, so Ctrl+Shift+1 beats Shift+1.</summary>
    public GlobalShortcut? Completed(ushort code, Func<ushort, bool> held)
    {
        GlobalShortcut? best = null;
        foreach (var shortcut in Shortcuts)
        {
            if (shortcut.Keys.Count == 0 || !shortcut.Keys.Contains(code))
                continue;
            if (shortcut.Keys.All(k => k == code || held(k)) && (best == null || shortcut.Keys.Count > best.Keys.Count))
                best = shortcut;
        }
        return best;
    }
}
