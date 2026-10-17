using System.Text.RegularExpressions;

namespace SafeCraftMcp.Settings;

/// <summary>
/// Checks a relative path an agent gives for a file in a grant or a workspace:
/// no absolute or UNC paths, no drive letters, no <c>..</c> or <c>.</c> segments, no alternate
/// data streams, no device names, no characters Windows reserves, no trailing dots or spaces
/// (Windows strips them, so <c>a.png.</c> and <c>a.png</c> are one file). The result uses
/// backslashes. This only checks the spelling; where the path really leads is checked on the
/// opened file handle (<see cref="RealPath"/>).
/// </summary>
public static partial class RelativePath
{
    private const int MaxLength = 240;

    /// <summary>The normalized path, or null with the reason in <paramref name="error"/>.</summary>
    public static string? Check(string path, out string error)
    {
        error = "";
        var segments = path.Replace('/', '\\').Split('\\');
        if (path.Length == 0 || path.Length > MaxLength)
            error = $"a path must have 1 to {MaxLength} characters";
        else if (path.Contains(':') || path.StartsWith('\\') || path.StartsWith('/'))
            error = $"'{path}' must be relative (no drive, no leading slash, no ':')";
        else if (segments.FirstOrDefault(s => s is "" or "." or "..") is { } bad)
            error = $"'{path}' has an empty, '.' or '..' segment ('{bad}')";
        else if (segments.FirstOrDefault(s => s.EndsWith('.') || s.EndsWith(' ') || s.StartsWith(' ')) is { } padded)
            error = $"'{path}' has a segment starting with a space or ending with a dot or space ('{padded}')";
        else if (segments.FirstOrDefault(s => s.Any(c => c < 32 || "<>\"|?*".Contains(c))) is { } reserved)
            error = $"'{path}' has a character Windows reserves in '{reserved}'";
        else if (segments.FirstOrDefault(s => DevicePattern().IsMatch(s.Split('.')[0])) is { } device)
            error = $"'{path}' names a device ('{device}')";
        return error.Length == 0 ? string.Join('\\', segments) : null;
    }

    /// <summary>A single file name (no folders), checked the same way.</summary>
    public static string? CheckName(string name, out string error)
    {
        var checkedName = Check(name, out error);
        if (checkedName is not null && checkedName.Contains('\\'))
        {
            error = $"'{name}' must be a file name without folders";
            return null;
        }
        return checkedName;
    }

    // CON, PRN, AUX, NUL, COM0-9, LPT0-9 (also with superscript digits), CONIN$, CONOUT$, CLOCK$.
    [GeneratedRegex(@"\A(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³]|CONIN\$|CONOUT\$|CLOCK\$)\s*\z", RegexOptions.IgnoreCase)]
    private static partial Regex DevicePattern();
}
