using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ViperSynapse.App.ViewModels;

/// <summary>One device's choice for a game: which of its profiles the game brings forward, or none.</summary>
public sealed partial class GameLink : ObservableObject
{
    readonly LinkedGamesViewModel _owner;
    readonly string _program;
    bool _loading;

    public GameLink(LinkedGamesViewModel owner, string program, DeviceViewModel device)
    {
        _owner = owner;
        _program = program;
        Device = device;
        Choices = [new Choice<Guid?>(null, "No profile"), .. device.CurrentSettings.Profiles.Select(p => new Choice<Guid?>(p.Id, p.Name))];
        var linked = device.CurrentSettings.Profiles.FirstOrDefault(p => p.LinkedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase));
        _loading = true;
        Selected = Choices.FirstOrDefault(c => c.Value == linked?.Id) ?? Choices[0];
        _loading = false;
    }

    public DeviceViewModel Device { get; }
    public IReadOnlyList<Choice<Guid?>> Choices { get; }

    [ObservableProperty]
    public partial Choice<Guid?> Selected { get; set; }

    partial void OnSelectedChanged(Choice<Guid?> value)
    {
        if (!_loading)
            _owner.Link(_program, Device, value.Value);
    }
}

/// <summary>A game or program and its profile on each device.</summary>
public sealed record GameRow(string Program, string Name, IReadOnlyList<GameLink> Links);

/// <summary>A device and the games linked to its profiles, for the DEVICES view.</summary>
public sealed record DeviceGames(DeviceViewModel Device, IReadOnlyList<string> Lines);

/// <summary>
/// The LINKED GAMES tab: games and programs that bring a profile forward while they are in front. The links live
/// on the device profiles (each profile lists its programs); this tab is the games-first view of them, as Synapse 4
/// has it, and the place to add a game.
/// </summary>
public sealed partial class LinkedGamesViewModel(MainViewModel main) : TabViewModel
{
    public override string Header => "LINKED GAMES";
    public override TabIcon Icon => TabIcon.LinkedGames;

    public static IReadOnlyList<string> Views { get; } = ["GAMES", "DEVICES"];

    [ObservableProperty] public partial string SelectedView { get; set; } = "GAMES";
    [ObservableProperty] public partial string NewProgram { get; set; } = "";
    [ObservableProperty] public partial GameRow? SelectedGame { get; set; }

    public ObservableCollection<GameRow> Games { get; } = [];
    public ObservableCollection<DeviceGames> DeviceList { get; } = [];

    public bool ShowGames => SelectedView == "GAMES";
    public bool ShowDevices => SelectedView == "DEVICES";
    public bool HasDevices => main.Devices.Any();

    partial void OnSelectedViewChanged(string value)
    {
        OnPropertyChanged(nameof(ShowGames));
        OnPropertyChanged(nameof(ShowDevices));
    }

    // Games added here but not linked to any profile yet; they stay listed until linked or removed.
    readonly List<string> _unlinked = [];

    /// <summary>Rebuilds the lists from the devices' current profiles.</summary>
    public void Refresh()
    {
        var selected = SelectedGame?.Program;
        var programs = main.Devices
            .SelectMany(d => d.CurrentSettings.Profiles.SelectMany(p => p.LinkedPrograms))
            .Concat(_unlinked)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => DisplayName(p), StringComparer.OrdinalIgnoreCase)
            .ToList();
        Games.Clear();
        foreach (var program in programs)
            Games.Add(new GameRow(program, DisplayName(program), main.Devices.Select(d => new GameLink(this, program, d)).ToList()));
        SelectedGame = Games.FirstOrDefault(g => g.Program == selected);
        DeviceList.Clear();
        foreach (var device in main.Devices)
        {
            var lines = device.CurrentSettings.Profiles
                .SelectMany(p => p.LinkedPrograms.Select(g => $"{DisplayName(g)}  →  {p.Name}"))
                .ToList();
            DeviceList.Add(new DeviceGames(device, lines));
        }
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>"game.exe" and "/home/me/Games/Game/game.exe" both show as "game".</summary>
    public static string DisplayName(string program)
    {
        var name = Path.GetFileName(program.Trim().Trim('"'));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    [RelayCommand]
    void AddGame()
    {
        var program = NewProgram.Trim();
        if (program.Length == 0)
            return;
        AddProgram(program);
        NewProgram = "";
    }

    public void AddProgram(string program)
    {
        if (!_unlinked.Contains(program, StringComparer.OrdinalIgnoreCase))
            _unlinked.Add(program);
        Refresh();
        SelectedGame = Games.FirstOrDefault(g => string.Equals(g.Program, program, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    void RemoveGame()
    {
        if (SelectedGame is not { } game)
            return;
        _unlinked.RemoveAll(p => string.Equals(p, game.Program, StringComparison.OrdinalIgnoreCase));
        foreach (var device in main.Devices)
            device.LinkProgram(game.Program, null);
        SelectedGame = null;
        Refresh();
    }

    /// <summary>Links the game to one profile of the device (taking it off the device's other profiles), or to none.</summary>
    public void Link(string program, DeviceViewModel device, Guid? profileId)
    {
        device.LinkProgram(program, profileId);
        if (profileId == null)
        {
            // Unlinked everywhere it stays listed, so it can be linked again.
            if (!_unlinked.Contains(program, StringComparer.OrdinalIgnoreCase) && main.Devices.All(d => !d.CurrentSettings.Profiles.Any(p => p.LinkedPrograms.Contains(program, StringComparer.OrdinalIgnoreCase))))
                _unlinked.Add(program);
            return;
        }
        _unlinked.RemoveAll(p => string.Equals(p, program, StringComparison.OrdinalIgnoreCase));
    }
}
