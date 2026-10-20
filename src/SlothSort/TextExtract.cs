using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace SlothSort;

/// <summary>
/// Pulls a short text excerpt out of common document types using only the .NET base library.
/// The excerpt is all the model needs to decide what a file is; the file itself is never changed.
/// </summary>
public static class TextExtract
{
    public const int MaxChars = 1500;

    public static string Excerpt(string path)
    {
        try
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            string text = ext switch
            {
                ".pdf" => Pdf(path),
                ".docx" => OfficeXml(path, e => e.FullName == "word/document.xml"),
                ".xlsx" => OfficeXml(path, e => e.FullName == "xl/sharedStrings.xml"),
                ".pptx" => OfficeXml(path, e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.Ordinal)),
                _ when Folders.TextExt.Contains(ext) => PlainText(path),
                _ => "",
            };
            return Clean(text, pdfCheck: ext == ".pdf");
        }
        catch
        {
            return ""; // unreadable or locked: the file name alone still helps
        }
    }

    private static string PlainText(string path)
    {
        using var s = File.OpenRead(path);
        var buf = new byte[8192];
        int n = s.Read(buf, 0, buf.Length);
        var text = Encoding.UTF8.GetString(buf, 0, n);
        if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
            text = Regex.Replace(Regex.Replace(text, "<(script|style)[^>]*>.*?</\\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase), "<[^>]+>", " ");
        return text;
    }

    /// <summary>Word/Excel/PowerPoint files are zip archives of XML: read the text nodes.</summary>
    private static string OfficeXml(string path, Func<ZipArchiveEntry, bool> pick)
    {
        using var zip = ZipFile.OpenRead(path);
        var sb = new StringBuilder();
        foreach (var entry in zip.Entries.Where(pick).OrderBy(e => e.FullName, StringComparer.Ordinal))
        {
            if (entry.Length > 20_000_000) continue; // zip-bomb guard
            using var s = entry.Open();
            using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            while (r.Read() && sb.Length < MaxChars * 2)
            {
                if (r.NodeType == XmlNodeType.Text) sb.Append(r.Value).Append(' ');
                else if (r.NodeType == XmlNodeType.EndElement && (r.LocalName is "p" or "si")) sb.Append('\n');
            }
            if (sb.Length >= MaxChars * 2) break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Minimal PDF text reader: inflates compressed content streams and collects text drawn with
    /// Tj / TJ / ' / " operators. Works for most simple PDFs (bills, statements, letters).
    /// Scanned PDFs or ones with custom-encoded fonts yield little or nothing; the file name is then used.
    /// </summary>
    public static string Pdf(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length > 30_000_000) bytes = bytes[..30_000_000];
        var latin = Encoding.Latin1.GetString(bytes);
        var sb = new StringBuilder();

        int pos = 0;
        while (sb.Length < MaxChars * 2)
        {
            int s = latin.IndexOf("stream", pos, StringComparison.Ordinal);
            if (s < 0) break;
            // Skip "endstream" matches
            if (s >= 3 && latin.AsSpan(s - 3, 3).SequenceEqual("end")) { pos = s + 6; continue; }
            int dataStart = s + 6;
            if (dataStart < latin.Length && latin[dataStart] == '\r') dataStart++;
            if (dataStart < latin.Length && latin[dataStart] == '\n') dataStart++;
            int e = latin.IndexOf("endstream", dataStart, StringComparison.Ordinal);
            if (e < 0) break;

            int dictStart = latin.LastIndexOf("<<", s, Math.Min(s, 2000), StringComparison.Ordinal);
            var dict = dictStart >= 0 ? latin[dictStart..s] : "";
            pos = e + 9;
            if (dict.Contains("/Image", StringComparison.Ordinal) || dict.Contains("/XObject", StringComparison.Ordinal)) continue;

            var raw = bytes.AsSpan(dataStart, e - dataStart).ToArray();
            byte[] data;
            if (dict.Contains("/FlateDecode", StringComparison.Ordinal))
            {
                try
                {
                    using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    var buf = new byte[81920];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0 && outMs.Length < 5_000_000) outMs.Write(buf, 0, n);
                    data = outMs.ToArray();
                }
                catch { continue; }
            }
            else if (dict.Contains("/Filter", StringComparison.Ordinal)) continue; // other filters: skip
            else data = raw;

            ExtractTextOps(Encoding.Latin1.GetString(data), sb);
        }
        return sb.ToString();
    }

    private static readonly Regex TextOps = new(@"(\((?:\\.|[^\\)])*\))\s*(?:Tj|'|"")|\[((?:\\.|[^\]])*)\]\s*TJ|(T\*|Td|TD|ET)", RegexOptions.Compiled);
    private static readonly Regex StringsInArray = new(@"\((?:\\.|[^\\)])*\)", RegexOptions.Compiled);

    private static void ExtractTextOps(string content, StringBuilder sb)
    {
        foreach (Match m in TextOps.Matches(content))
        {
            if (m.Groups[1].Success) sb.Append(Unescape(m.Groups[1].Value));
            else if (m.Groups[2].Success)
                foreach (Match s in StringsInArray.Matches(m.Groups[2].Value)) sb.Append(Unescape(s.Value));
            else sb.Append(' ');
            if (sb.Length >= MaxChars * 2) return;
        }
    }

    private static string Unescape(string lit)
    {
        var s = lit[1..^1];
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c != '\\' || i + 1 >= s.Length) { sb.Append(c); continue; }
            char n = s[++i];
            switch (n)
            {
                case 'n': sb.Append('\n'); break;
                case 'r': sb.Append(' '); break;
                case 't': sb.Append(' '); break;
                case '(': case ')': case '\\': sb.Append(n); break;
                default:
                    if (n is >= '0' and <= '7')
                    {
                        int v = n - '0', k = 1;
                        while (k < 3 && i + 1 < s.Length && s[i + 1] is >= '0' and <= '7') { v = v * 8 + (s[++i] - '0'); k++; }
                        sb.Append((char)v);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Collapse whitespace, drop control characters and garbage, cap the length.</summary>
    public static string Clean(string s, bool pdfCheck = true)
    {
        var sb = new StringBuilder(Math.Min(s.Length, MaxChars + 10));
        bool space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = sb.Length > 0; continue; }
            if (space) { sb.Append(' '); space = false; }
            sb.Append(c);
            if (sb.Length >= MaxChars) break;
        }
        var t = sb.ToString();
        // PDFs with custom-encoded fonts come out as garbage; that's useless to the model. (Other formats keep any language.)
        // PDFs read this way are Latin-1, so real text is overwhelmingly plain ASCII.
        int good = t.Count(ch => ch is >= ' ' and <= '~');
        return pdfCheck && t.Length > 0 && good < t.Length * 0.7 ? "" : t;
    }
}
