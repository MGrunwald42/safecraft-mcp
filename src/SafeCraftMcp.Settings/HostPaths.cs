namespace SafeCraftMcp.Settings;

/// <summary>
/// The user folders every default and protected path is derived from. Never the current
/// directory: Claude Desktop starts the server in an unrelated one.
/// </summary>
public sealed record HostPaths
{
    public HostPaths(string userProfile, string appData, string localAppData, string? documents = null, string? temp = null)
    {
        temp ??= Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        // An empty known folder would turn every protected path into a relative one that matches
        // nothing, and the defaults would resolve from the current directory.
        foreach (var (name, value) in new[] { ("UserProfile", userProfile), ("AppData", appData), ("LocalAppData", localAppData), ("Temp", temp) })
            if (!RealPath.IsPlainLocal(value))
                throw new InvalidOperationException($"The {name} folder is '{value}', not an absolute local path; the gateway can't place its defaults.");
        UserProfile = userProfile;
        AppData = appData;
        LocalAppData = localAppData;
        Documents = documents is null || !RealPath.IsPlainLocal(documents) ? Path.Combine(userProfile, "Documents") : documents;
        Temp = temp;
    }

    public string UserProfile { get; }
    public string AppData { get; }
    public string LocalAppData { get; }

    /// <summary>The Documents known folder; it differs from <c>%USERPROFILE%\Documents</c> under OneDrive.</summary>
    public string Documents { get; }

    /// <summary>The user's temp folder (<c>%TEMP%</c>).</summary>
    public string Temp { get; }

    public static HostPaths Current() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));

    public string DefaultRepos => Path.Combine(UserProfile, "Documents", "GitHub");

    /// <summary>The gateway's own state: frozen binaries and logs.</summary>
    public string StateDir => Path.Combine(LocalAppData, "safecraft-mcp");

    /// <summary>
    /// The session folders (workspace and profile of each sandbox). They live under <c>%TEMP%</c>,
    /// not in <see cref="StateDir"/>: in a MXC read-write grant below <c>%LOCALAPPDATA%</c> (outside
    /// its Temp folder) the sandbox can create files but not rename or delete them, so an app's
    /// crash-safe save (write a temp file, rename it over the target) is denied (docs/sandbox-notes.md).
    /// </summary>
    public string SessionsDir => Path.Combine(Temp, "safecraft-mcp-sessions");

    /// <summary>Where sessions lived before they moved to <see cref="SessionsDir"/>; swept for orphans.</summary>
    public string LegacySessionsDir => Path.Combine(StateDir, "sessions");

    /// <summary>
    /// Folders and files a craft program must never reach, and no grant may point into: secrets,
    /// other repos, the gateway's state, and the MCP client configs that hold the gateway's
    /// options. Used both as MXC <c>DeniedPaths</c> and for grant and file checks.
    /// The repos folder from <c>--repos</c> is added by the settings (<see cref="GatewaySettings.ProtectedPaths"/>).
    /// </summary>
    public IReadOnlyList<string> Protected =>
    [
        .. new[] { ".ssh", ".aws", ".azure", ".config", ".gnupg", ".docker", ".kube", ".claude", ".claude.json", ".gitconfig", ".git-credentials", ".npmrc" }
            .Select(name => Path.Combine(UserProfile, name)),
        Path.Combine(UserProfile, "Documents", "GitHub"),
        Path.Combine(Documents, "GitHub"),
        Path.Combine(AppData, "Claude"),
        Path.Combine(AppData, "Microsoft", "Credentials"),
        Path.Combine(AppData, "Mozilla", "Firefox", "Profiles"),
        Path.Combine(LocalAppData, "Google", "Chrome", "User Data"),
        Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data"),
        Path.Combine(LocalAppData, "BraveSoftware", "Brave-Browser", "User Data"),
        Path.Combine(LocalAppData, "Microsoft", "Credentials"),
        // Claude Desktop from the Microsoft Store keeps its config here.
        Path.Combine(LocalAppData, "Packages"),
        StateDir,
        SessionsDir,
    ];

    /// <summary>Whether a fully qualified, normalized <paramref name="path"/> equals or is below <paramref name="root"/>.</summary>
    public static bool PathIsWithin(string path, string root)
    {
        var p = Path.TrimEndingDirectorySeparator(path);
        var r = Path.TrimEndingDirectorySeparator(root);
        // A drive root keeps its separator ("C:\"), so it's already the prefix.
        var prefix = Path.EndsInDirectorySeparator(r) ? r : r + Path.DirectorySeparatorChar;
        return p.Equals(r, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Whether <paramref name="path"/> is inside, equal to or above any of <paramref name="protectedPaths"/>.</summary>
    public static bool Overlaps(string path, IEnumerable<string> protectedPaths) =>
        protectedPaths.Any(p => PathIsWithin(path, p) || PathIsWithin(p, path));
}
