using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using SlothSort;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var opt = Options.Parse(args);
if (opt.Has("help")) { Options.Help(); return 0; }
if (opt.Command == "selftest") return SelfTest.Run();
if (opt.Command == "peek")   // debugging aid: show the text excerpt the model would see
{
    foreach (var f in opt.Positional) Console.WriteLine($"── {Path.GetFileName(f)}\n{TextExtract.Excerpt(f)}\n");
    return 0;
}

var ai = new Classifier(opt.Model, opt.Ollama);
// Folders named on the command line are trusted (you typed them); the web page is limited to your user folder.
var cliRoots = opt.AllowRoots.Concat(opt.Command is "plan" or "tidy" && opt.Positional.Count > 0 ? new[] { opt.Positional[0] } : Array.Empty<string>()).ToList();
var guard = new Guard(cliRoots);
var engine = new Engine(ai, guard, opt.DataDir);

if (opt.Command is "plan" or "tidy") return await Cli.Run(opt, engine, ai);
if (opt.Command is not (null or "serve")) { Options.Help(); return 1; }

// ── Local web app ───────────────────────────────────────────────────────────
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = Array.Empty<string>() });
builder.Logging.SetMinimumLevel(LogLevel.Warning);
builder.WebHost.UseUrls($"http://127.0.0.1:{opt.Port}");   // this computer only
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));
var app = builder.Build();

// Only answer requests addressed to this machine; require a custom header on changes,
// so another website open in your browser can't trigger a tidy-up or an undo.
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
    if (!HttpMethods.IsGet(ctx.Request.Method) && ctx.Request.Headers["X-Sloth"] != "1") { ctx.Response.StatusCode = 403; return; }
    await next();
});

var page = ReadEmbedded("index.html");
app.MapGet("/", () => Results.Content(page, "text/html; charset=utf-8"));

app.MapGet("/api/status", async () =>
{
    var problem = await ai.CheckAsync();
    return new
    {
        ready = problem == null, problem, model = ai.Model, dataDir = engine.DataDir, version = "0.1.0",
        planning = engine.Planning, done = engine.PlanDone, total = engine.PlanTotal, planError = engine.PlanError,
    };
});

app.MapGet("/api/folders", () => Folders.All);
app.MapGet("/api/settings", () => new { settings = engine.Settings, destinationRoot = engine.Settings.DestinationRoot() });
app.MapPut("/api/settings", (Settings s) =>
{
    s.Folder = s.Folder.Trim(); s.Destination = s.Destination.Trim();
    var problem = engine.SaveSettings(s);
    return problem == null ? Results.Ok(new { settings = engine.Settings, destinationRoot = engine.Settings.DestinationRoot() }) : Results.BadRequest(problem);
});

app.MapPost("/api/plan", () => engine.StartPlan() is { } problem ? Results.BadRequest(problem) : Results.Ok());
app.MapPost("/api/plan/cancel", () => { engine.CancelPlan(); return Results.Ok(); });
app.MapGet("/api/plan", () => engine.Plan());
app.MapPut("/api/plan/{id}", (string id, EditRequest e) =>
{
    try { return engine.Edit(id, e.Folder, e.NewName, e.Selected) is { } p ? Results.Ok(p) : Results.NotFound(); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

app.MapPost("/api/apply", () =>
{
    var b = engine.Apply();
    return Results.Ok(new { batchId = b.Id, moved = b.Moves.Count, aiSeconds = Math.Round(b.AiSeconds, 1) });
});
app.MapGet("/api/journal", () => engine.Journal().Select(b => new { b.Id, b.WhenUtc, b.Folder, moved = b.Moves.Count, b.Undone, aiSeconds = Math.Round(b.AiSeconds, 1) }));
app.MapPost("/api/undo/{id}", (string id) =>
{
    try { var (n, problems) = engine.Undo(id); return Results.Ok(new { restored = n, problems }); }
    catch (ArgumentException ex) { return Results.BadRequest(ex.Message); }
});

// Thumbnail for images in the current preview only (never an arbitrary path).
app.MapGet("/api/preview/{id}", (string id) =>
{
    var p = engine.Plan().FirstOrDefault(x => x.Id == id);
    if (p == null || p.Status != ProposalStatus.Pending || !Folders.VisionReadable(Path.GetExtension(p.FileName)) || !File.Exists(p.SourcePath)) return Results.NotFound();
    return Results.File(p.SourcePath, Path.GetExtension(p.FileName).Equals(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
});

var url = $"http://127.0.0.1:{opt.Port}";
Console.WriteLine($"""

  🦥 Sloth Sort is running at {url}
  Model: {ai.Model} (local, via Ollama). Nothing leaves this computer.
  Undo history is kept in: {engine.DataDir}
  Press Ctrl+C to stop.

""");
if (!opt.Has("no-browser")) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }
await app.RunAsync();
return 0;

static string ReadEmbedded(string name)
{
    using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
    using var r = new StreamReader(s);
    return r.ReadToEnd();
}

namespace SlothSort
{
    public sealed record EditRequest(string Folder, string NewName, bool Selected);

    internal sealed class Options
    {
        public string? Command { get; private set; }
        public List<string> Positional { get; } = new();
        public List<string> AllowRoots { get; } = new();
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase) { "help", "no-browser", "read-images", "yes" };

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (a.StartsWith("--"))
                {
                    var k = a[2..]; string? v = null;
                    if (k.Contains('=')) { v = k[(k.IndexOf('=') + 1)..]; k = k[..k.IndexOf('=')]; }
                    else if (!Flags.Contains(k) && i + 1 < args.Length && !args[i + 1].StartsWith("--")) v = args[++i];
                    if (k.Equals("allow", StringComparison.OrdinalIgnoreCase) && v != null) o.AllowRoots.Add(v);
                    o._named[k] = v;
                }
                else if (o.Command == null) o.Command = a.ToLowerInvariant();
                else o.Positional.Add(a);
            }
            return o;
        }

        public bool Has(string k) => _named.ContainsKey(k);
        public string? Get(string k) => _named.TryGetValue(k, out var v) ? v : null;
        private string? GetOrEnv(string k, string env) => Get(k) ?? Environment.GetEnvironmentVariable(env);
        public int Port => int.TryParse(Get("port"), out var p) ? p : 5179;
        public string Model => GetOrEnv("model", "SLOTHSORT_MODEL") ?? "gemma3:4b";
        public string Ollama => GetOrEnv("ollama", "OLLAMA_HOST") is { } h ? (h.StartsWith("http") ? h : "http://" + h) : "http://localhost:11434";
        public string DataDir => GetOrEnv("data", "SLOTHSORT_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".slothsort");

        public static void Help() => Console.WriteLine("""
            🦥 Sloth Sort — a local AI tidies your messy folders while you do nothing.

              slothsort                     Open the app in your browser (http://127.0.0.1:5179)
              slothsort plan <folder>       Show what it WOULD do (changes nothing)
              slothsort tidy <folder> --yes Do it: move + rename, with undo history
              slothsort selftest            Run the built-in checks

            Options
              --read-images                 Also look inside photos with the vision model (slower)
              --dest <folder>               Where sorted files go (default: <folder>/Sorted)
              --model gemma3:4b             Any Ollama model (env SLOTHSORT_MODEL)
              --json <file>                 (plan) also write the plan as JSON
              --port 5179  --no-browser  --ollama http://localhost:11434  --data <folder>

            Never deletes, never overwrites. Every move can be undone from the app.
            """);
    }

    internal static class Cli
    {
        public static async Task<int> Run(Options o, Engine engine, Classifier ai)
        {
            var folder = o.Positional.FirstOrDefault();
            if (folder == null) { Console.Error.WriteLine($"Usage: slothsort {o.Command} <folder>"); return 1; }
            var s = new Settings { Folder = Path.GetFullPath(folder), Destination = o.Get("dest") is { } d ? Path.GetFullPath(d) : "", ReadImages = o.Has("read-images") };
            if (engine.SaveSettings(s) is { } problem) { Console.Error.WriteLine(problem); return 1; }
            if (await ai.CheckAsync() is { } p2) { Console.Error.WriteLine(p2); return 2; }

            Console.WriteLine($"🦥 Looking through {s.Folder} with {ai.Model} (local)…\n");
            var plan = new List<Proposal>();
            var sw = Stopwatch.StartNew();
            await engine.BuildPlanAsync(s, p =>
            {
                plan.Add(p);
                var tag = p.Status == ProposalStatus.Error ? "ERROR" : p.Source.ToUpperInvariant();
                Console.WriteLine($"{Trunc(p.FileName, 34),-34} → {p.Folder,-17} {Trunc(p.NewName, 44),-44} [{tag}{(p.Seconds is { } x ? $" {x}s" : "")}]{(p.Sensitive ? " 🔒" : "")}");
                if (p.Error != null) Console.WriteLine($"{"",36}✖ {p.Error}");
            });
            Console.WriteLine($"\n{plan.Count} files planned in {sw.Elapsed.TotalSeconds:F0}s ({plan.Count(p => p.Source == "rule")} by rules, {plan.Count(p => p.Source == "ai")} by AI, {plan.Count(p => p.Status == ProposalStatus.Error)} errors).");
            if (o.Get("json") is { } jsonPath) File.WriteAllText(Path.GetFullPath(jsonPath), JsonSerializer.Serialize(plan, Engine.Json));

            if (o.Command == "tidy")
            {
                if (!o.Has("yes")) { Console.WriteLine("Nothing moved. Add --yes to tidy for real."); return 0; }
                engine.UsePlan(plan, s.Folder);   // same apply path as the app, so the undo journal is written
                var b = engine.Apply();
                Console.WriteLine($"Moved {b.Moves.Count} files. Undo any time from the app (batch {b.Id}).");
            }
            else Console.WriteLine("This was a preview: nothing was moved.");
            return plan.Any(p => p.Status == ProposalStatus.Error) ? 3 : 0;
        }

        private static string Trunc(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";
    }
}
