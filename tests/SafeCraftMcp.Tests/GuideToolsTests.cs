using System.Diagnostics;
using System.Text.Json;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.Tests;

// A fake repos folder in temp: photocraft with a built "binary" (just bytes), docs, a junction and a
// hard link in its docs folder leading out of it, and an oversized doc; pdfcraft whose whole docs
// folder is a junction to somewhere else. No session is opened.
public sealed class GuideToolsTests : IAsyncDisposable
{
    private static readonly HostPaths Host = new(@"C:\Users\me", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\AppData\Local");

    private readonly string _repos = Directory.CreateTempSubdirectory("safecraft-guides-").FullName;
    private readonly SessionManager _sessions;
    private readonly GuideTools _guides;

    public GuideToolsTests()
    {
        var photocraft = Path.Combine(_repos, "photocraft");
        Directory.CreateDirectory(Path.Combine(photocraft, "target", "release"));
        File.WriteAllText(Path.Combine(photocraft, "target", "release", "photocraft-cli.exe"), "not really an exe");
        Directory.CreateDirectory(Path.Combine(photocraft, "docs"));
        File.WriteAllText(Path.Combine(photocraft, "docs", "mcp.md"), "# MCP conventions");
        File.WriteAllText(Path.Combine(photocraft, "docs", "notes.txt"), "not markdown");
        var outside = Directory.CreateDirectory(Path.Combine(_repos, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "secret.md"), "outside the docs");
        Cmd("/J", Path.Combine(photocraft, "docs", "linked"), outside);
        Cmd("/H", Path.Combine(photocraft, "docs", "hard.md"), Path.Combine(outside, "secret.md"));
        File.WriteAllText(Path.Combine(photocraft, "docs", "huge.md"), new string('#', 1024 * 1024 + 1));
        var pdfcraft = Path.Combine(_repos, "pdfcraft");
        Directory.CreateDirectory(Path.Combine(pdfcraft, "target", "release"));
        File.WriteAllText(Path.Combine(pdfcraft, "target", "release", "pdfcraft-cli.exe"), "not really an exe");
        Cmd("/J", Path.Combine(pdfcraft, "docs"), outside);
        var settings = GatewaySettings.Defaults(Host) with { Repos = _repos };
        var registry = AppRegistry.LoadEmbedded();
        var binaries = new BinaryResolver(settings);
        _sessions = new SessionManager(settings, registry, binaries, Host, NullLoggerFactory.Instance);
        _guides = new GuideTools(_sessions, registry, binaries, settings);
    }

    private static void Cmd(string kind, string link, string target)
    {
        using var mklink = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", kind, link, target]) { CreateNoWindow = true, RedirectStandardOutput = true })!;
        mklink.WaitForExit();
        Assert.Equal(0, mklink.ExitCode);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        Directory.Delete(Path.Combine(_repos, "photocraft", "docs", "linked"));
        Directory.Delete(Path.Combine(_repos, "pdfcraft", "docs"));
        Directory.Delete(_repos, recursive: true);
    }

    private async Task<CallToolResult> Call(string tool, object args) =>
        await new ToolCatalog([.. _guides.Tools()]).CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args)), null, CancellationToken.None);

    [Fact]
    public async Task The_PhotoCraft_Guide_Is_Embedded_And_Others_Say_They_Have_None()
    {
        var photocraft = await Call("craft_guide", new { app = "PhotoCraft" });
        var filmcraft = await Call("craft_guide", new { app = "filmcraft" });
        var unknown = await Call("craft_guide", new { app = "paintcraft" });

        Assert.StartsWith("# PhotoCraft", ((TextContentBlock)photocraft.Content[0]).Text);
        Assert.Contains("needsDocument", ((TextContentBlock)photocraft.Content[0]).Text);
        Assert.True(filmcraft.IsError);
        Assert.True(unknown.IsError);
    }

    [Fact]
    public void Each_App_With_A_Guide_Has_A_Starter_Prompt()
    {
        var prompts = _guides.ListPrompts().Prompts;
        var prompt = _guides.GetPrompt("photocraft-start", new Dictionary<string, JsonElement> { ["task"] = JsonSerializer.SerializeToElement("a poster") });

        Assert.Equal(["pdfcraft-start", "photocraft-start", "vectorcraft-start"], prompts.Select(p => p.Name));
        var text = ((TextContentBlock)Assert.Single(prompt.Messages).Content).Text;
        Assert.Contains("craft_guide", text);
        Assert.EndsWith("The task: a poster", text);
    }

    [Fact]
    public void Unknown_Prompts_And_Arguments_Are_Invalid_Params()
    {
        var unknown = Assert.Throws<McpProtocolException>(() => _guides.GetPrompt("filmcraft-start", null));
        var argument = Assert.Throws<McpProtocolException>(() => _guides.GetPrompt("photocraft-start", new Dictionary<string, JsonElement> { ["tier"] = default }));

        var notText = Assert.Throws<McpProtocolException>(() => _guides.GetPrompt("photocraft-start", new Dictionary<string, JsonElement> { ["task"] = JsonSerializer.SerializeToElement(5) }));

        Assert.Equal(McpErrorCode.InvalidParams, unknown.ErrorCode);
        Assert.Equal(McpErrorCode.InvalidParams, argument.ErrorCode);
        Assert.Equal(McpErrorCode.InvalidParams, notText.ErrorCode);
    }

    [Fact]
    public void Upstream_Docs_Are_The_Repo_Docs_Folder_Markdown_Labeled()
    {
        var listed = _guides.ListDocs().Resources.Select(r => r.Uri);
        var read = (TextResourceContents)Assert.Single(_guides.ReadDoc("safecraft://photocraft/docs/mcp.md").Contents);

        Assert.Equal(["safecraft://photocraft/docs/hard.md", "safecraft://photocraft/docs/huge.md", "safecraft://photocraft/docs/mcp.md"], listed);
        Assert.StartsWith("[Untrusted text from the PhotoCraft repository", read.Text);
        Assert.EndsWith("# MCP conventions", read.Text);
    }

    [Theory]
    [InlineData("""[{"id":"a"},{"id":"b"}]""", 2)]
    [InlineData("""{"commands":[{"id":"a"}]}""", 1)]
    [InlineData("""{"commands":[{"id":5}]}""", null)]
    [InlineData("""[1,[2],null]""", null)]
    [InlineData("""{"tools":[]}""", null)]
    [InlineData("<html>", null)]
    public void Both_Catalog_Shapes_Are_Read_And_Malformed_Ones_Refused(string text, int? rows)
    {
        Assert.Equal(rows, GuideTools.ParseRows(text)?.Count);
    }

    [Theory]
    [InlineData("safecraft://photocraft/docs/../Cargo.toml")]
    [InlineData("safecraft://photocraft/docs/linked/secret.md")]
    [InlineData("safecraft://photocraft/docs/hard.md")]
    [InlineData("safecraft://photocraft/docs/huge.md")]
    [InlineData("safecraft://pdfcraft/docs/secret.md")]
    [InlineData("safecraft://photocraft/docs/notes.txt")]
    [InlineData("safecraft://photocraft/docs/missing.md")]
    [InlineData("safecraft://filmcraft/docs/mcp.md")]
    [InlineData("file:///C:/Windows/win.ini")]
    public void Anything_Else_Is_Not_A_Doc(string uri)
    {
        Assert.Throws<McpProtocolException>(() => _guides.ReadDoc(uri));
    }
}
