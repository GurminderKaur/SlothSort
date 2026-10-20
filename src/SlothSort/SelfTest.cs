using System.IO.Compression;
using System.Text;

namespace SlothSort;

/// <summary>Built-in checks for the parts that must never go wrong: names, safety rules, moves and undo.</summary>
public static class SelfTest
{
    private static int _pass, _fail;

    public static int Run()
    {
        var today = new DateTime(2026, 10, 20);
        var tmp = Path.Combine(Path.GetTempPath(), "slothsort-selftest-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        try
        {
            // ── Names ──────────────────────────────────────────────────────
            Eq("name: date + title + lower-case extension", Namer.Build("telus mobility bill", "2026-10-14", today, ".PDF", today), "2026-10-14 Telus Mobility Bill.pdf");
            Eq("name: invalid characters removed", Namer.Sanitize("Q3: report/final*?"), "Q3 Report Final");
            Eq("name: Windows reserved word avoided", Namer.Sanitize("con"), "Con file");
            Eq("date: far-future date ignored → file date used", Namer.Build("Bill", "2031-01-01", new DateTime(2026, 9, 1), ".pdf", today), "2026-09-01 Bill.pdf");
            Eq("date: nonsense ignored", Namer.ValidDate("next tuesday", today), (DateTime?)null);
            File.WriteAllText(Path.Combine(tmp, "A.pdf"), "x");
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Eq("unique: existing file gets (2)", Namer.Unique(tmp, "A.pdf", planned), "A (2).pdf");
            Eq("unique: planned name gets (3)", Namer.Unique(tmp, "A.pdf", planned), "A (3).pdf");

            // ── Rules (no AI) ──────────────────────────────────────────────
            var old = DateTime.UtcNow.AddHours(-1);
            Eq("rule: installer", Folders.Rule(Touch(tmp, "Zoom_setup.exe", old), false)?.Folder, Folders.Installers);
            Eq("rule: screenshot by name", Folders.Rule(Touch(tmp, "Screenshot 2026-10-01 093011.png", old), true)?.Folder, Folders.Screenshots);
            Eq("rule: photo when reading images is off", Folders.Rule(Touch(tmp, "IMG_2041.jpg", old), false)?.Folder, Folders.Photos);
            True("rule: photo goes to AI when reading images is on", Folders.Rule(Touch(tmp, "IMG_2042.jpg", old), true) == null);
            True("rule: PDF goes to AI", Folders.Rule(Touch(tmp, "document(7).pdf", old), false) == null);
            True("skip: still downloading", Folders.ShouldSkip(Touch(tmp, "movie.mp4.crdownload", old), DateTime.UtcNow));
            True("skip: modified seconds ago", Folders.ShouldSkip(Touch(tmp, "fresh.pdf", DateTime.UtcNow), DateTime.UtcNow));
            True("skip: Office lock file", Folders.ShouldSkip(Touch(tmp, "~$report.docx", old), DateTime.UtcNow));

            // ── Reading documents ──────────────────────────────────────────
            var pdf = Path.Combine(tmp, "bill.pdf");
            File.WriteAllBytes(pdf, MakePdf("TELUS Mobility  Statement date: Oct 14, 2026  Amount due $85.12"));
            True("pdf: text read from compressed stream", TextExtract.Excerpt(pdf).Contains("Amount due $85.12"));
            var docx = Path.Combine(tmp, "lease.docx");
            MakeDocx(docx, "Residential Tenancy Agreement", "Calgary, Alberta");
            True("docx: text read", TextExtract.Excerpt(docx).Contains("Residential Tenancy Agreement"));
            Eq("garbage text dropped", TextExtract.Clean("\u0001\u0002ÿþ\u0003ÿþÿþÿþ"), "");

            // ── Model output ───────────────────────────────────────────────
            var v = Classifier.Parse("""ok {"folder":"bills & receipts","title":"Telus Bill","date":"2026-10-14","sensitive":false,"reason":"phone bill"}""");
            Eq("parse: folder matched case-insensitively", v.Folder, Folders.Bills);
            True("private: banking/medical/ID folders get a lock, bills don't", Folders.IsPrivate(Folders.Medical) && !Folders.IsPrivate(Folders.Bills));
            Eq("parse: unknown folder → Other", Classifier.Parse("""{"folder":"Taxes 2026","title":"x","date":"","sensitive":false,"reason":""}""").Folder, Folders.Other);

            // ── Safety guard ───────────────────────────────────────────────
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var g = new Guard(new[] { tmp });
            True("guard: whole user folder refused", g.Check(home) != null);
            True("guard: AppData-like folder refused", g.Check(Path.Combine(home, ".ssh")) != null || !Directory.Exists(Path.Combine(home, ".ssh")));
            True("guard: allowed folder accepted", g.Check(tmp) == null);
            True("guard: missing folder refused", g.Check(Path.Combine(tmp, "nope")) != null);
            True("guard: system folder refused", g.Check(OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.Windows) : "/usr") != null);

            // ── Move + undo (never overwrite) ──────────────────────────────
            var src = Path.Combine(tmp, "inbox"); Directory.CreateDirectory(src);
            var data = Path.Combine(tmp, "data");
            File.WriteAllText(Path.Combine(src, "document(7).pdf"), "bill");
            File.WriteAllText(Path.Combine(src, "setup.exe"), "exe");
            var sorted = Path.Combine(src, "Sorted", Folders.Bills); Directory.CreateDirectory(sorted);
            File.WriteAllText(Path.Combine(sorted, "2026-10-14 Telus Bill.pdf"), "an older file with the same name");
            var engine = new Engine(new Classifier("none", "http://127.0.0.1:9"), g, data);
            engine.SaveSettings(new Settings { Folder = src });
            engine.UsePlan(new List<Proposal>
            {
                new() { Id = "a", SourcePath = Path.Combine(src, "document(7).pdf"), FileName = "document(7).pdf", Folder = Folders.Bills, NewName = "2026-10-14 Telus Bill.pdf" },
                new() { Id = "b", SourcePath = Path.Combine(src, "setup.exe"), FileName = "setup.exe", Folder = Folders.Installers, NewName = "setup.exe", Selected = false },
            }, src);
            var batch = engine.Apply();
            Eq("apply: only selected files moved", batch.Moves.Count, 1);
            True("apply: existing file NOT overwritten", File.ReadAllText(Path.Combine(sorted, "2026-10-14 Telus Bill.pdf")) == "an older file with the same name"
                                                    && File.Exists(Path.Combine(sorted, "2026-10-14 Telus Bill (2).pdf")));
            True("apply: unselected file untouched", File.Exists(Path.Combine(src, "setup.exe")));
            var (restored, problems) = engine.Undo(batch.Id);
            True("undo: file back under its original name", restored == 1 && problems.Count == 0 && File.Exists(Path.Combine(src, "document(7).pdf")));
            True("undo: journal survives a restart", new Engine(new Classifier("none", "http://127.0.0.1:9"), g, data).Journal().Single().Undone);
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { }
        }

        Console.WriteLine($"\n{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }

    private static FileInfo Touch(string dir, string name, DateTime utc)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllText(p, "x");
        File.SetLastWriteTimeUtc(p, utc);
        return new FileInfo(p);
    }

    /// <summary>A tiny valid PDF with one Flate-compressed text stream.</summary>
    public static byte[] MakePdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 720 Td ({text.Replace("(", "\\(").Replace(")", "\\)")}) Tj ET";
        using var zms = new MemoryStream();
        using (var z = new ZLibStream(zms, CompressionLevel.Optimal, leaveOpen: true)) z.Write(Encoding.Latin1.GetBytes(content));
        var stream = zms.ToArray();
        var ms = new MemoryStream();
        void W(string s) => ms.Write(Encoding.Latin1.GetBytes(s));
        W("%PDF-1.4\n1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        W("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >> endobj\n");
        W($"4 0 obj << /Length {stream.Length} /Filter /FlateDecode >>\nstream\n");
        ms.Write(stream);
        W("\nendstream\nendobj\ntrailer << /Root 1 0 R >>\n%%EOF\n");
        return ms.ToArray();
    }

    public static void MakeDocx(string path, params string[] paragraphs)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var e = zip.CreateEntry("word/document.xml");
        using var w = new StreamWriter(e.Open());
        w.Write("<?xml version=\"1.0\"?><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
        foreach (var p in paragraphs) w.Write($"<w:p><w:r><w:t>{System.Security.SecurityElement.Escape(p)}</w:t></w:r></w:p>");
        w.Write("</w:body></w:document>");
    }

    private static void Eq<T>(string name, T actual, T expected)
    {
        bool ok = EqualityComparer<T>.Default.Equals(actual, expected);
        Report(name, ok, ok ? "" : $"expected {expected}, got {actual}");
    }
    private static void True(string name, bool c) => Report(name, c, "");
    private static void Report(string name, bool ok, string detail)
    {
        if (ok) _pass++; else _fail++;
        if (!ok && Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true")
            Console.WriteLine($"::error title=Self-test failed::{name} {detail}");
        Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(ok ? "" : "  — " + detail)}");
    }
}
