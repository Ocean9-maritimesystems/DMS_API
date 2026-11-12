namespace DMS.Data.EF.Features;

/// <summary>
/// File types never taken as an attachment: programs and scripts, and pages a browser would run (HTML, SVG) - opened
/// from this app they would run with the user's login. Everything else (PDF, Office, images, text, archives...) is
/// accepted as before. Checked where users upload, so they get the reason at once.
/// </summary>
public static class FileRules
{
    private static readonly HashSet<string> Blocked = new(StringComparer.OrdinalIgnoreCase)
    {
        "exe", "dll", "msi", "msp", "bat", "cmd", "com", "scr", "pif", "cpl", "ps1", "psm1", "psd1", "vbs", "vbe",
        "js", "jse", "mjs", "wsf", "wsh", "hta", "jar", "lnk", "reg", "inf", "sh", "php", "asp", "aspx", "jsp",
        "html", "htm", "xhtml", "shtml", "svg", "svgz", "mht", "mhtml",
    };

    /// <summary>Why this file cannot be attached, or null when it can.</summary>
    public static string? Problem(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").TrimStart('.');
        return ext.Length > 0 && Blocked.Contains(ext)
            ? $"\"{Path.GetFileName(fileName)}\" cannot be attached: .{ext.ToLowerInvariant()} files are not allowed (programs, scripts and web pages)."
            : null;
    }

    /// <summary>Throws ArgumentException (answered as 400 with the reason) when the file cannot be attached.</summary>
    public static void Check(string? fileName)
    {
        var problem = Problem(fileName);
        if (problem != null) throw new ArgumentException(problem);
    }
}
