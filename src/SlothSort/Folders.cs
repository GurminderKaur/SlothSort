using System.Text.RegularExpressions;

namespace SlothSort;

/// <summary>The destination folders, and the rules that need no AI at all.</summary>
public static class Folders
{
    public const string Bills = "Bills & Receipts";
    public const string Money = "Banking & Taxes";
    public const string Work = "Work";
    public const string School = "School";
    public const string Travel = "Travel";
    public const string Medical = "Medical";
    public const string Home = "Home & Car";
    public const string Personal = "IDs & Personal";
    public const string Photos = "Photos";
    public const string Screenshots = "Screenshots";
    public const string Installers = "Installers";
    public const string Archives = "Archives";
    public const string Media = "Music & Video";
    public const string Other = "Other";

    public static readonly IReadOnlyList<string> All = new[]
    {
        Bills, Money, Work, School, Travel, Medical, Home, Personal, Photos, Screenshots, Installers, Archives, Media, Other,
    };

    /// <summary>Folders the AI may choose from (the rest are decided by rules).</summary>
    public static readonly IReadOnlyList<string> AiChoices = new[]
    {
        Bills, Money, Work, School, Travel, Medical, Home, Personal, Photos, Other,
    };

    public static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".csv", ".json", ".xml", ".html", ".htm", ".log", ".ics", ".eml", ".rtf" };
    public static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".pdf", ".docx", ".xlsx", ".pptx" };
    public static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".jpg", ".jpeg", ".png", ".webp", ".gif", ".heic", ".bmp" };
    private static readonly HashSet<string> InstallerExt = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".msix", ".appx", ".dmg", ".pkg", ".deb", ".rpm", ".appimage" };
    private static readonly HashSet<string> ArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz" };
    private static readonly HashSet<string> MediaExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mp3", ".wav", ".m4a", ".flac", ".ogg", ".aac", ".mp4", ".mov", ".mkv", ".avi", ".webm", ".wmv" };

    /// <summary>Files that are still downloading or are system/temp files: never touched.</summary>
    private static readonly HashSet<string> SkipExt = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".download", ".tmp", ".temp", ".opdownload", ".lnk", ".ini", ".url" };

    private static readonly Regex ScreenshotName = new(@"^(screenshot|screen shot|capture|snip|scr_|screen_)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static bool ShouldSkip(FileInfo f, DateTime nowUtc) =>
        f.Name.StartsWith('.') || f.Name.StartsWith("~$")
        || SkipExt.Contains(f.Extension)
        || f.Attributes.HasFlag(FileAttributes.Hidden) || f.Attributes.HasFlag(FileAttributes.System)
        || f.Attributes.HasFlag(FileAttributes.ReparsePoint)          // don't follow links
        || nowUtc - f.LastWriteTimeUtc < TimeSpan.FromMinutes(2);       // probably still being written

    /// <summary>Rule-based decision; null means "ask the AI".</summary>
    public static (string Folder, string Reason)? Rule(FileInfo f, bool readImages)
    {
        var ext = f.Extension;
        if (InstallerExt.Contains(ext)) return (Installers, "Program installer");
        if (ArchiveExt.Contains(ext)) return (Archives, "Compressed archive");
        if (MediaExt.Contains(ext)) return (Media, "Audio or video file");
        if (ImageExt.Contains(ext))
        {
            if (ScreenshotName.IsMatch(f.Name)) return (Screenshots, "Name says it's a screenshot");
            if (!readImages || !VisionReadable(ext)) return (Photos, "Image (reading images is off)");
        }
        return null;
    }

    /// <summary>Formats the vision model can read directly.</summary>
    public static bool VisionReadable(string ext) => ext.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
        || ext.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) || ext.Equals(".png", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rule-decided files keep their original name (installers etc. are recognised by name).</summary>
    public static bool KeepsOriginalName(string folder) =>
        folder is Installers or Archives or Media or Screenshots or Photos;
}
