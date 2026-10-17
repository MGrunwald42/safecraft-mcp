using System.Text.Json.Serialization;

namespace SafeCraftMcp.Registry;

/// <summary>
/// One app as the gateway knows it, from an embedded <c>apps/&lt;id&gt;.json</c>. The child's
/// command line comes only from <see cref="Args"/>; placeholders are <c>{workspace}</c> and
/// <c>{profile}</c>. Changing a manifest needs a rebuild, not a file edit.
/// </summary>
public sealed record AppManifest
{
    public required string Id { get; init; }
    public required string Display { get; init; }

    /// <summary>The GitHub repository, <c>owner/name</c>.</summary>
    public required string Repo { get; init; }

    /// <summary>The CLI's file name, e.g. <c>photocraft-cli.exe</c>.</summary>
    public required string Binary { get; init; }

    /// <summary>Folders to look in, in order; placeholders <c>{repos}</c> and <c>{ProgramFiles}</c>.</summary>
    public required IReadOnlyList<string> Search { get; init; }

    public required IReadOnlyList<string> Args { get; init; }
    // Optional fields keep their default in the getter: the JSON source generator sets every init
    // property, absent ones too (to null), so an initializer alone would be lost.
    public IReadOnlyDictionary<string, string> Env { get => field ?? new Dictionary<string, string>(); init; }

    /// <summary>False until the app had its M0-style sandbox pass; such an app can't be opened.</summary>
    public required bool Ready { get; init; }

    public required int CallTimeoutSeconds { get; init; }
    public required IReadOnlyList<string> Import { get; init; }
    public required IReadOnlyList<string> Export { get; init; }
    public required int MaxFileMb { get; init; }

    /// <summary>Tools that return an image; the gateway sets their <c>max_side</c> to at most <c>--max-preview-side</c>.</summary>
    public IReadOnlyList<string> PreviewTools { get => field ?? []; init; }

    /// <summary>Tools that write or export files; they get <c>--export-timeout</c> instead of the call timeout.</summary>
    public IReadOnlyList<string> LongTools { get => field ?? []; init; }

    /// <summary>The app's tool that lists its commands (the suite's convention: <c>command_list</c>), for <c>craft_commands</c>.</summary>
    public string CatalogTool { get => field ?? "command_list"; init; }

    /// <summary>The app's tool that opens a document (for <c>session_open</c>'s <c>project</c>); null when it has none.</summary>
    public string? OpenTool { get; init; }

    /// <summary>The argument of <see cref="OpenTool"/> that takes the workspace-relative path.</summary>
    public string? OpenToolPathArgument { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    RespectRequiredConstructorParameters = true)]
[JsonSerializable(typeof(AppManifest))]
internal sealed partial class ManifestJsonContext : JsonSerializerContext;
