using System.Text;
using SafeCraftMcp.ChildRpc;

namespace SafeCraftMcp.Tests;

public sealed class LineLimitStreamTests
{
    private static (string Read, bool Overflowed, bool Killed) ReadAll(string input, long maxLine, int chunk)
    {
        var killed = false;
        using var stream = new LineLimitStream(new MemoryStream(Encoding.UTF8.GetBytes(input)), maxLine, () => killed = true);
        var output = new MemoryStream();
        var buffer = new byte[chunk];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return (Encoding.UTF8.GetString(output.ToArray()), stream.Overflowed, killed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4096)]
    public void Lines_Within_The_Limit_Pass_Unchanged_Whatever_The_Chunks(int chunk)
    {
        var input = "12345\nabc\n\n12345\n";

        Assert.Equal((input, false, false), ReadAll(input, 5, chunk));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(4096)]
    public void A_Line_Over_The_Limit_Ends_The_Stream_And_Kills_The_App(int chunk)
    {
        var (_, overflowed, killed) = ReadAll("ok\n123456\nnever read\n", 5, chunk);

        Assert.True(overflowed);
        Assert.True(killed);
    }

    [Fact]
    public void A_Line_Without_A_Newline_Counts_Across_Reads()
    {
        Assert.True(ReadAll(new string('x', 100), 50, 10).Overflowed);
    }
}
