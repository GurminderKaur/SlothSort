using System.Text.Json;
using System.Text.Json.Serialization;

namespace SlothSort;

/// <summary>
/// Plans a tidy-up (read-only), applies the moves you approve, and can undo any batch.
/// Sloth Sort never deletes and never overwrites: every change is a move, recorded in a journal.
/// </summary>
public sealed class Engine
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly object _lock = new();
    private readonly Classifier _ai;
    private readonly Guard _guard;
    private readonly string _dataDir;
    private List<Proposal> _plan = new();
    private readonly List<Batch> _journal;
    private CancellationTokenSource? _planCts;

    public Settings Settings { get; private set; }
    public string DataDir => _dataDir;
    public bool Planning { get; private set; }
    public int PlanTotal { get; private set; }
    public int PlanDone { get; private set; }
    public string PlanFolder { get; private set; } = "";
    public string? PlanError { get; private set; }

    public Engine(Classifier ai, Guard guard, string dataDir)
    {
        _ai = ai; _guard = guard; _dataDir = dataDir;
        Directory.CreateDirectory(dataDir);
        Settings = Load<Settings>("settings.json") ?? new Settings();
        _journal = Load<List<Batch>>("journal.json") ?? new();
    }

    public List<Proposal> Plan() { lock (_lock) return _plan.Select(Clone).ToList(); }
    public List<Batch> Journal() { lock (_lock) return _journal.OrderByDescending(b => b.WhenUtc).ToList(); }

    public string? SaveSettings(Settings s)
    {
        var problem = _guard.Check(s.Folder);
        if (problem != null) return problem;
        if (!string.IsNullOrWhiteSpace(s.Destination))
        {
            if (!_guard.Inside(s.Destination)) return "Destination: for safety, choose a folder inside your user folder.";
            Directory.CreateDirectory(s.Destination);
            var dp = _guard.Check(s.Destination);
            if (dp != null) return "Destination: " + dp;
        }
        lock (_lock) { Settings = s; Write("settings.json", s); }
        return null;
    }

    // ── Planning (read-only) ───────────────────────────────────────────────

    public string? StartPlan()
    {
        var s = Settings;
        var problem = _guard.Check(s.Folder);
        if (problem != null) return problem;
        lock (_lock)
        {
            if (Planning) return "Already looking through the folder.";
            _planCts?.Cancel();
            _planCts = new CancellationTokenSource();
            Planning = true; PlanError = null; PlanDone = 0; PlanTotal = 0; PlanFolder = s.Folder;
            _plan = new();
        }
        var ct = _planCts.Token;
        _ = Task.Run(async () =>
        {
            try { await BuildPlanAsync(s, p => { lock (_lock) _plan.Add(p); }, ct); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { PlanError = ex.Message; }
            finally { Planning = false; }
        });
        return null;
    }

    public void CancelPlan() => _planCts?.Cancel();

    /// <summary>Look at each file in the folder (not subfolders) and propose a folder + name. Changes nothing.</summary>
    public async Task BuildPlanAsync(Settings s, Action<Proposal> add, CancellationToken ct = default)
    {
        var folder = Guard.Full(s.Folder);
        var destRoot = Guard.Full(s.DestinationRoot());
        var now = DateTime.UtcNow;
        var files = new DirectoryInfo(folder).EnumerateFiles()
            .Where(f => !Folders.ShouldSkip(f, now))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PlanTotal = files.Count;
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var p = new Proposal
            {
                Id = Guid.NewGuid().ToString("N")[..10], SourcePath = f.FullName, FileName = f.Name,
                Size = f.Length, Modified = f.LastWriteTime,
            };
            try
            {
                var rule = Folders.Rule(f, s.ReadImages);
                if (rule is { } r)
                {
                    p.Folder = r.Folder; p.Reason = r.Reason; p.Source = "rule"; p.NewName = f.Name;
                }
                else
                {
                    var excerpt = TextExtract.Excerpt(f.FullName);
                    byte[]? image = Folders.ImageExt.Contains(f.Extension) && f.Length < 20_000_000 ? await File.ReadAllBytesAsync(f.FullName, ct) : null;
                    var (v, secs) = await _ai.ClassifyAsync(f, excerpt, image, ct);
                    p.Folder = v.Folder; p.Reason = v.Reason; p.Source = "ai"; p.Seconds = Math.Round(secs, 1);
                    p.NewName = Folders.KeepsOriginalName(v.Folder) ? f.Name : Namer.Build(v.Title, v.Date, f.LastWriteTime, f.Extension, DateTime.Today);
                }
                p.Sensitive = Folders.IsPrivate(p.Folder);
                p.NewName = Namer.Unique(Path.Combine(destRoot, p.Folder), p.NewName, planned);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                p.Status = ProposalStatus.Error; p.Selected = false;
                p.Error = ex is HttpRequestException ? "Couldn't reach Ollama. Is the Ollama app running?" : ex.Message;
            }
            add(p);
            PlanDone++;
        }
    }

    public void UsePlan(List<Proposal> plan, string folder)
    {
        lock (_lock) { _plan = plan; PlanFolder = folder; }
    }

    public Proposal? Edit(string id, string folder, string newName, bool selected)
    {
        lock (_lock)
        {
            var p = _plan.FirstOrDefault(x => x.Id == id);
            if (p == null || p.Status != ProposalStatus.Pending && p.Status != ProposalStatus.Error) return null;
            if (!Folders.All.Contains(folder)) throw new ArgumentException("Unknown folder.");
            var clean = Namer.Sanitize(Path.GetFileNameWithoutExtension(newName));
            if (clean.Length == 0) throw new ArgumentException("Name can't be empty.");
            var ext = Path.GetExtension(p.FileName);
            var name = clean + ext;
            if (p.Folder != folder || p.NewName != name) p.Source = "you";
            p.Folder = folder; p.NewName = name; p.Sensitive = Folders.IsPrivate(folder); p.Selected = selected && p.Status == ProposalStatus.Pending;
            return Clone(p);
        }
    }

    // ── Applying and undoing ───────────────────────────────────────────────

    /// <summary>Move every selected file. Never overwrites: if a target exists, a " (n)" suffix is added.</summary>
    public Batch Apply()
    {
        lock (_lock)
        {
            var destRoot = Guard.Full(Settings.DestinationRoot());
            var batch = new Batch { Id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss"), WhenUtc = DateTime.UtcNow, Folder = PlanFolder };
            var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in _plan.Where(p => p.Selected && p.Status == ProposalStatus.Pending))
            {
                try
                {
                    if (!File.Exists(p.SourcePath)) { p.Status = ProposalStatus.Skipped; p.Error = "File was moved or deleted since the preview."; continue; }
                    var dir = Path.Combine(destRoot, p.Folder);
                    Directory.CreateDirectory(dir);
                    var name = Namer.Unique(dir, p.NewName, planned);
                    var target = Path.Combine(dir, name);
                    File.Move(p.SourcePath, target, overwrite: false);
                    batch.Moves.Add(new Move { From = p.SourcePath, To = target });
                    batch.AiSeconds += p.Seconds ?? 0;
                    p.Status = ProposalStatus.Moved; p.NewName = name;
                }
                catch (Exception ex) { p.Status = ProposalStatus.Error; p.Error = ex is IOException ? "File is open in another program; skipped." : ex.Message; }
            }
            if (batch.Moves.Count > 0) { _journal.Add(batch); Write("journal.json", _journal); }
            return batch;
        }
    }

    /// <summary>Put every file in a batch back where it was (when it's still where we left it).</summary>
    public (int Restored, List<string> Problems) Undo(string batchId)
    {
        lock (_lock)
        {
            var b = _journal.FirstOrDefault(x => x.Id == batchId) ?? throw new ArgumentException("No such batch.");
            if (b.Undone) return (0, new() { "Already undone." });
            int ok = 0; var problems = new List<string>();
            foreach (var m in Enumerable.Reverse(b.Moves))
            {
                if (!File.Exists(m.To)) { problems.Add($"{Path.GetFileName(m.To)} isn't in the sorted folder any more."); continue; }
                if (File.Exists(m.From)) { problems.Add($"{Path.GetFileName(m.From)} already exists in the original folder; left the sorted copy alone."); continue; }
                try { Directory.CreateDirectory(Path.GetDirectoryName(m.From)!); File.Move(m.To, m.From, overwrite: false); ok++; }
                catch (Exception ex) { problems.Add($"{Path.GetFileName(m.To)}: {ex.Message}"); }
            }
            b.Undone = problems.Count == 0;
            Write("journal.json", _journal);
            // Remove category folders that are now empty (only ones Sloth Sort could have created).
            var dirs = b.Moves.Select(m => Path.GetDirectoryName(m.To)!).Distinct().ToList();
            foreach (var dir in dirs.Concat(dirs.Select(d => Path.GetDirectoryName(d)!).Distinct()))   // category folders, then the "Sorted" folder
                try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
            return (ok, problems);
        }
    }

    // ── Storage ────────────────────────────────────────────────────────────

    private void Write<T>(string name, T value)
    {
        var path = Path.Combine(_dataDir, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, overwrite: true);
    }

    private T? Load<T>(string name)
    {
        var path = Path.Combine(_dataDir, name);
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : default; }
        catch { return default; }
    }

    private static Proposal Clone(Proposal p) => JsonSerializer.Deserialize<Proposal>(JsonSerializer.Serialize(p, Json), Json)!;
}
