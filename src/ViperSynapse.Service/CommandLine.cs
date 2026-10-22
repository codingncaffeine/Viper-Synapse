using System.Diagnostics;
using System.Text;

namespace ViperSynapse.Service;

/// <summary>Starts the programs a Launch binding or macro step names. No shell is involved: the command line
/// is split the way a shell splits plain words and quotes, and the program runs detached from the service.</summary>
public static class CommandLine
{
    /// <summary>Splits on spaces; single and double quotes group, a backslash escapes the next character.</summary>
    public static IReadOnlyList<string> Split(string command)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        bool inWord = false;
        char quote = '\0';
        for (int i = 0; i < command.Length; i++)
        {
            char c = command[i];
            if (c == '\\' && quote != '\'' && i + 1 < command.Length)
            {
                word.Append(command[++i]);
                inWord = true;
            }
            else if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else word.Append(c);
            }
            else if (c is '"' or '\'')
            {
                quote = c;
                inWord = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (inWord) words.Add(word.ToString());
                word.Clear();
                inWord = false;
            }
            else
            {
                word.Append(c);
                inWord = true;
            }
        }
        if (inWord) words.Add(word.ToString());
        return words;
    }

    public static void Start(string command)
    {
        var words = Split(command);
        if (words.Count == 0)
            return;
        var info = new ProcessStartInfo(words[0]) { UseShellExecute = false, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var argument in words.Skip(1))
            info.ArgumentList.Add(argument);
        // Disposing the handle does not stop the program; it keeps running after the service moves on.
        using var process = Process.Start(info);
    }
}
