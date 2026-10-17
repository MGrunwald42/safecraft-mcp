using System.Text;

namespace SafeCraftMcp.Sandbox;

/// <summary>
/// Builds the one command string MXC's <c>ContainerRequest</c> takes, from an executable and an
/// argument list. Each argument is quoted by the rules Windows programs (and Rust's std, which the
/// craft CLIs use) split command lines with, so a workspace path with spaces, quotes or trailing
/// backslashes stays exactly one argument. This is the only place a command string is made.
/// </summary>
public static class CommandLine
{
    public static string Build(string executable, IEnumerable<string> arguments)
    {
        // The program name is parsed differently (no backslash escapes), and a Windows path can't
        // contain a quote, so plain quoting is exact for it.
        if (executable.Contains('"'))
            throw new ArgumentException($"An executable path can't contain a quote: {executable}", nameof(executable));
        var line = new StringBuilder().Append('"').Append(executable).Append('"');
        foreach (var argument in arguments)
            line.Append(' ').Append(Quote(argument));
        return line.ToString();
    }

    public static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return argument;
        var quoted = new StringBuilder("\"");
        for (var i = 0; ; i++)
        {
            var backslashes = 0;
            while (i < argument.Length && argument[i] == '\\')
            {
                backslashes++;
                i++;
            }
            if (i == argument.Length)
            {
                // Backslashes before the closing quote are doubled, or they'd escape it.
                quoted.Append('\\', backslashes * 2);
                break;
            }
            if (argument[i] == '"')
                quoted.Append('\\', backslashes * 2 + 1).Append('"');
            else
                quoted.Append('\\', backslashes).Append(argument[i]);
        }
        return quoted.Append('"').ToString();
    }
}
