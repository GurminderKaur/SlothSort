namespace SlothSort;

/// <summary>
/// Decides which folders Sloth Sort may touch. By default: folders inside your user folder,
/// but never your user folder itself, app data, or system folders. Extra roots can be allowed
/// explicitly on the command line (--allow).
/// </summary>
public sealed class Guard
{
    private readonly List<string> _roots = new();
    private static readonly StringComparison Cmp = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public Guard(IEnumerable<string> extraRoots)
    {
        _roots.Add(Full(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        _roots.AddRange(extraRoots.Select(Full));
    }

    public string? Check(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "Choose a folder.";
        string full;
        try { full = Full(folder); } catch { return "That isn't a valid folder path."; }
        if (!Directory.Exists(full)) return $"Folder not found: {full}";
        if (new DirectoryInfo(full).Attributes.HasFlag(FileAttributes.ReparsePoint)) return "That folder is a link; choose the real folder.";

        var home = _roots[0];
        if (string.Equals(full, home, Cmp)) return "That's your whole user folder. Pick a specific folder like Downloads.";
        // A folder you named yourself on the command line (--allow) wins over the app-data rule,
        // e.g. a test folder under %TEMP%, which on Windows lives inside AppData.
        bool named = _roots.Skip(1).Any(r => Under(full, r) || string.Equals(full, r, Cmp));
        if (!named)
            foreach (var bad in new[] { "AppData", "Library", ".config", ".local", ".ssh", ".gnupg", ".slothsort" })
                if (Under(full, Path.Combine(home, bad)) || string.Equals(full, Path.Combine(home, bad), Cmp)) return $"Sloth Sort won't touch {bad} — pick a normal folder like Downloads.";
        foreach (var sys in SystemFolders())
            if (!string.IsNullOrEmpty(sys) && (string.Equals(full, sys, Cmp) || Under(full, sys))) return "Sloth Sort won't touch system or program folders.";

        bool allowed = _roots.Any(r => Under(full, r)) || _roots.Skip(1).Any(r => string.Equals(full, r, Cmp));
        if (!allowed)
            return "For safety, Sloth Sort only tidies folders inside your user folder (like Downloads or Desktop).";
        return null;
    }

    /// <summary>True if the path is somewhere under your user folder or a folder you allowed (it may not exist yet).</summary>
    public bool Inside(string path)
    {
        try { var full = Full(path); return _roots.Any(r => Under(full, r)); } catch { return false; }
    }

    private static IEnumerable<string> SystemFolders() => new[]
    {
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "/bin", "/usr", "/etc", "/System", "/Applications",
    }.Where(s => !string.IsNullOrEmpty(s)).Select(Full);

    public static string Full(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    public static bool Under(string path, string root)
    {
        root = Path.TrimEndingDirectorySeparator(root);
        return path.Length > root.Length && path.StartsWith(root, Cmp)
               && (path[root.Length] == Path.DirectorySeparatorChar || path[root.Length] == Path.AltDirectorySeparatorChar);
    }
}
