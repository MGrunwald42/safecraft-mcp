using System.Runtime.InteropServices;
using SafeCraftMcp.Sandbox;

namespace SafeCraftMcp.Tests;

// Round trip through Windows' own splitter, CommandLineToArgvW, which follows the same rules as
// the MSVC runtime and Rust's std (the craft CLIs).
public sealed partial class CommandLineTests
{
    [LibraryImport("shell32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr CommandLineToArgvW(string commandLine, out int count);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr LocalFree(IntPtr memory);

    private static string[] Split(string commandLine)
    {
        var argv = CommandLineToArgvW(commandLine, out var count);
        try
        {
            return [.. Enumerable.Range(0, count).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(argv, i * IntPtr.Size))!)];
        }
        finally
        {
            LocalFree(argv);
        }
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    [InlineData(@"C:\Users\Jane Doe\AppData\Local\safecraft-mcp\sessions\1-s1\work")]
    [InlineData(@"C:\trailing backslash\")]
    [InlineData(@"C:\two trailing\\")]
    [InlineData("has \"quotes\" inside")]
    [InlineData(@"back\""slash quote")]
    [InlineData("tab\there")]
    [InlineData(@"\\server\share")]
    [InlineData("ümlaut and 日本語")]
    [InlineData("\" --bridge 127.0.0.1:9876 \"")]
    public void Every_Argument_Comes_Back_As_One_Argument(string argument)
    {
        var line = CommandLine.Build(@"C:\Users\Jane Doe\bin\photocraft-cli.exe", ["mcp", "--root", argument, "--last"]);

        var argv = Split(line);

        Assert.Equal([@"C:\Users\Jane Doe\bin\photocraft-cli.exe", "mcp", "--root", argument, "--last"], argv);
    }

    [Fact]
    public void An_Executable_With_A_Quote_Is_Refused()
    {
        Assert.Throws<ArgumentException>(() => CommandLine.Build("C:\\a\"b.exe", []));
    }
}
