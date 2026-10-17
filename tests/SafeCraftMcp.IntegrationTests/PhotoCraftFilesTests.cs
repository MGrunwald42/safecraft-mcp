using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;
using SafeCraftMcp.Registry;
using SafeCraftMcp.Sandbox;
using SafeCraftMcp.Server;
using SafeCraftMcp.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Protocol;

namespace SafeCraftMcp.IntegrationTests;

// The rest of PhotoCraft's sandbox pass: camera raw decoding and PSB-size documents
// under the session sandbox and its Job Object limits. The raw decoder is pure Rust (no Windows
// codecs), so what the sandbox can break is memory and threads; a 24 MP raw exercises both.
[Trait("Category", "Mxc")]
public sealed class PhotoCraftFilesTests : IAsyncDisposable
{
    private readonly string _state = Directory.CreateTempSubdirectory("safecraft-pc-files-").FullName;
    private readonly string _in;
    private readonly SessionManager _sessions;
    private readonly ToolCatalog _catalog;

    public PhotoCraftFilesTests()
    {
        var host = MxcHost.TestHost(_state);
        _in = RealPath.ResolveFolder(Directory.CreateTempSubdirectory("safecraft-pc-files-in-").FullName, out _)!;
        var settings = GatewaySettings.Defaults(host) with
        {
            Repos = HostPaths.Current().DefaultRepos,
            Grants = [new Grant("in", _in, GrantAccess.Read)],
        };
        var registry = AppRegistry.LoadEmbedded();
        var binaries = new BinaryResolver(settings);
        _sessions = new SessionManager(settings, registry, binaries, host, NullLoggerFactory.Instance);
        var workspace = new WorkspaceTools(_sessions, settings, host);
        _catalog = new ToolCatalog(
        [
            .. new SessionTools(_sessions, workspace, settings, NullLogger<SessionTools>.Instance).Tools(),
            .. workspace.Tools(),
        ]);
    }

    public async ValueTask DisposeAsync()
    {
        await _sessions.DisposeAsync();
        DeleteFolder(_state);
        Directory.Delete(_in, recursive: true);
    }

    private async Task<CallToolResult> Call(string tool, object args) =>
        await _catalog.CallAsync(tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(args))!, null, CancellationToken.None);

    private async Task<CallToolResult> App(string session, string tool, object arguments)
    {
        var result = await Call("session_call", new { session, tool, arguments });
        Assert.False(result.IsError, Text(result));
        return result;
    }

    private static JsonNode Json(CallToolResult result) => JsonNode.Parse(((TextContentBlock)result.Content[^1]).Text)!;

    private static (int Width, int Height) PngSize(CallToolResult preview)
    {
        var png = Assert.Single(preview.Content.OfType<ImageContentBlock>()).DecodedData.Span;
        return (BinaryPrimitives.ReadInt32BigEndian(png[16..]), BinaryPrimitives.ReadInt32BigEndian(png[20..]));
    }

    [MxcFact]
    public async Task A_Camera_Raw_Opens_And_Previews_In_The_Sandbox()
    {
        File.WriteAllBytes(Path.Combine(_in, "shot.dng"), Dng.Cfa(6000, 4000));

        var opened = await Call("session_open", new { app = "photocraft", project = "in/shot.dng" });
        Assert.False(opened.IsError, Text(opened));
        var session = (string)JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!["session"]!;
        var inspect = Json(await App(session, "doc_inspect", new { }));
        var preview = await App(session, "render_preview", new { max_side = 1024 });

        Assert.Equal(4000, (int)inspect["height"]!);
        Assert.Equal(16, (int)inspect["depth"]!);
        Assert.Equal("[0,0,6000,4000]", inspect["layers"]![0]!["bounds"]!.ToJsonString());
        Assert.Equal((1024, 683), PngSize(preview));
    }

    [MxcFact]
    public async Task A_Document_Past_The_Psd_Limit_Saves_And_Reopens_As_Psb()
    {
        var opened = await Call("session_open", new { app = "photocraft" });
        Assert.False(opened.IsError, Text(opened));
        var session = (string)JsonNode.Parse(((TextContentBlock)opened.Content[0]).Text)!["session"]!;

        // PSD stops at 30000 px a side; PSB goes to 300000.
        await App(session, "doc_new", new { width = 40000, height = 400 });
        await App(session, "doc_save", new { path = "wide.psb" });
        await App(session, "doc_open", new { path = "wide.psb" });
        var inspect = Json(await App(session, "doc_inspect", new { }));

        Assert.Equal(400, (int)inspect["height"]!);
        Assert.Equal("[0,0,40000,400]", inspect["layers"]![0]!["bounds"]!.ToJsonString());
        Assert.Equal(["Open"], inspect["history"]!.AsArray().Select(h => (string?)h));
    }

    /// <summary>
    /// A minimal DNG as PhotoCraft's own test generator writes one (crates/raw/src/testgen.rs):
    /// IFD0 with a 2 x 2 RGB thumbnail and the DNG tags, and a SubIFD with 16-bit RGGB CFA data
    /// in one uncompressed strip. Little-endian; tags in ascending order, as TIFF requires.
    /// </summary>
    private static class Dng
    {
        private const ushort Byte = 1, Ascii = 2, Short = 3, Long = 4;

        private sealed record Entry(ushort Tag, ushort Type, uint Count, byte[] Data);

        public static byte[] Cfa(int width, int height)
        {
            var thumb = new byte[12];
            Array.Fill(thumb, (byte)128);
            var raw = new byte[width * height * 2];
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                    BinaryPrimitives.WriteUInt16LittleEndian(raw.AsSpan((y * width + x) * 2), (ushort)(4096 + (x * 40000 / width) + (y * 20000 / height)));

            // Layout: header, IFD0, raw IFD, thumbnail, raw strip. Offsets are known up front
            // because every out-of-line value is placed right after its IFD.
            List<Entry> RawIfd(uint stripOffset) =>
            [
                new(254, Long, 1, U32(0)),
                new(256, Long, 1, U32((uint)width)),
                new(257, Long, 1, U32((uint)height)),
                new(258, Short, 1, U16(16)),
                new(259, Short, 1, U16(1)),
                new(262, Short, 1, U16(32803)),
                new(273, Long, 1, U32(stripOffset)),
                new(277, Short, 1, U16(1)),
                new(278, Long, 1, U32((uint)height)),
                new(279, Long, 1, U32((uint)raw.Length)),
                new(284, Short, 1, U16(1)),
                new(33421, Short, 2, [.. U16(2), .. U16(2)]),
                new(33422, Byte, 4, [0, 1, 1, 2]),
                new(50710, Byte, 3, [0, 1, 2]),
                new(50711, Short, 1, U16(1)),
                new(50713, Short, 2, [.. U16(1), .. U16(1)]),
                new(50714, Long, 1, U32(0)),
                new(50717, Long, 1, U32(65535)),
            ];
            List<Entry> Ifd0(uint thumbOffset, uint rawIfdOffset) =>
            [
                new(254, Long, 1, U32(1)),
                new(256, Long, 1, U32(2)),
                new(257, Long, 1, U32(2)),
                new(258, Short, 3, [.. U16(8), .. U16(8), .. U16(8)]),
                new(259, Short, 1, U16(1)),
                new(262, Short, 1, U16(2)),
                new(271, Ascii, 11, "Photocraft\0"u8.ToArray()),
                new(272, Ascii, 10, "Synthetic\0"u8.ToArray()),
                new(273, Long, 1, U32(thumbOffset)),
                new(274, Short, 1, U16(1)),
                new(277, Short, 1, U16(3)),
                new(278, Long, 1, U32(2)),
                new(279, Long, 1, U32(12)),
                new(330, Long, 1, U32(rawIfdOffset)),
                new(50706, Byte, 4, [1, 4, 0, 0]),
                new(50708, Ascii, 21, "Photocraft Synthetic\0"u8.ToArray()),
            ];

            var ifd0Size = IfdSize(Ifd0(0, 0));
            var rawIfdOffset = 8 + ifd0Size;
            var thumbOffset = rawIfdOffset + IfdSize(RawIfd(0));
            var stripOffset = thumbOffset + thumb.Length;
            using var file = new MemoryStream();
            file.Write("II*\0"u8);
            file.Write(U32(8));
            WriteIfd(file, Ifd0((uint)thumbOffset, (uint)rawIfdOffset));
            WriteIfd(file, RawIfd((uint)stripOffset));
            file.Write(thumb);
            file.Write(raw);
            return file.ToArray();
        }

        private static int IfdSize(List<Entry> entries) =>
            2 + 12 * entries.Count + 4 + entries.Where(e => e.Data.Length > 4).Sum(e => e.Data.Length + e.Data.Length % 2);

        // The IFD, then its out-of-line values (word-aligned) right behind it.
        private static void WriteIfd(MemoryStream file, List<Entry> entries)
        {
            var start = (int)file.Position;
            var extra = start + 2 + 12 * entries.Count + 4;
            using var values = new MemoryStream();
            file.Write(U16((ushort)entries.Count));
            foreach (var e in entries)
            {
                file.Write(U16(e.Tag));
                file.Write(U16(e.Type));
                file.Write(U32(e.Count));
                if (e.Data.Length <= 4)
                {
                    file.Write(e.Data);
                    file.Write(new byte[4 - e.Data.Length]);
                }
                else
                {
                    file.Write(U32((uint)(extra + values.Length)));
                    values.Write(e.Data);
                    if (e.Data.Length % 2 == 1)
                        values.WriteByte(0);
                }
            }
            file.Write(U32(0));
            values.WriteTo(file);
            Assert.Equal(start + IfdSize(entries), file.Position);
        }

        private static byte[] U16(ushort v)
        {
            var b = new byte[2];
            BinaryPrimitives.WriteUInt16LittleEndian(b, v);
            return b;
        }

        private static byte[] U32(uint v)
        {
            var b = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(b, v);
            return b;
        }
    }
}
