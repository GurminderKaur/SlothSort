using System.Globalization;
using System.Text;

namespace SlothSort;

/// <summary>Builds safe, readable file names like "2026-10-14 Telus Mobility Bill.pdf".</summary>
public static class Namer
{
    private static readonly char[] Invalid = "<>:\"/\\|?*".ToCharArray();
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3" };

    public static string Build(string title, string date, DateTime modified, string ext, DateTime today)
    {
        var d = ValidDate(date, today) ?? modified.Date;
        var t = Sanitize(title);
        if (t.Length == 0) t = "Untitled";
        if (t.Length > 70) t = t[..70].TrimEnd();
        return $"{d:yyyy-MM-dd} {t}{ext.ToLowerInvariant()}";
    }

    public static DateTime? ValidDate(string s, DateTime today)
    {
        if (DateTime.TryParseExact(s?.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            && d.Year >= 1990 && d.Date <= today.Date.AddYears(2))   // trips and due dates can be in the future
            return d.Date;
        return null;
    }

    /// <summary>Remove characters Windows/macOS/Linux don't allow, tidy spacing, Title Case short words.</summary>
    public static string Sanitize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormC))
        {
            if (char.IsControl(c) || Array.IndexOf(Invalid, c) >= 0) sb.Append(' ');
            else sb.Append(c);
        }
        var words = sb.ToString().Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var outWords = words.Select(w => w.Length > 1 && w.All(char.IsLower) ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        var result = string.Join(' ', outWords).Trim().TrimEnd('.', ' ');
        if (Reserved.Contains(result)) result += " file";
        return result;
    }

    /// <summary>If the name is taken (or planned for another file), add " (2)", " (3)", …</summary>
    public static string Unique(string folderPath, string name, ISet<string> alreadyPlanned)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        var candidate = name;
        for (int i = 2; File.Exists(Path.Combine(folderPath, candidate)) || alreadyPlanned.Contains(Path.Combine(folderPath, candidate)); i++)
            candidate = $"{stem} ({i}){ext}";
        alreadyPlanned.Add(Path.Combine(folderPath, candidate));
        return candidate;
    }
}
