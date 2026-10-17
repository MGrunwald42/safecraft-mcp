using System.Text.RegularExpressions;

namespace SafeCraftMcp.Settings;

/// <summary>
/// File type rules that no manifest or option can override: executables,
/// scripts and shortcuts could run on the host after an export; keys and certificates shouldn't
/// be handed to an untrusted program.
/// </summary>
public static partial class FileTypes
{
    public static IReadOnlySet<string> NeverAllowed { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Run when opened, or start something that runs.
        "exe", "dll", "com", "scr", "pif", "cpl", "sys", "drv", "ocx", "msi", "msp", "mst", "msix", "appx",
        "bat", "cmd", "ps1", "psm1", "psd1", "js", "jse", "vb", "vbs", "vbe", "bas", "wsf", "wsh", "ws", "wsc",
        "hta", "chm", "msc", "inf", "reg", "jar", "jnlp", "py", "pyw", "xll", "ade", "adp", "mde",
        "ps1xml", "psc1", "sct", "msu", "msixbundle", "appxbundle", "appinstaller", "gadget", "diagcab",
        "lnk", "url", "scf",
        // Mount as a drive (and autorun from it), or carry macros.
        "iso", "img", "vhd", "vhdx", "docm", "dotm", "xlsm", "xltm", "xlam", "pptm", "potm", "ppam",
        // Keys, certificates and secret stores.
        "p12", "pfx", "pem", "key", "cer", "der", "crt", "p7b", "p7c", "p8", "pk8", "ppk", "jks", "keystore",
        "kdbx", "gpg", "pgp", "asc",
    };

    /// <summary>A plain extension without the dot: lowercase letters and digits, up to 12.</summary>
    public static bool IsWellFormed(string extension) => ExtensionPattern().IsMatch(extension);

    [GeneratedRegex(@"\A[a-z0-9]{1,12}\z")]
    private static partial Regex ExtensionPattern();
}
