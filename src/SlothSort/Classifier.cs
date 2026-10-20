using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlothSort;

/// <summary>
/// Asks a local open-weight model (default: Gemma 3 4B via Ollama) what a file is.
/// Only a short text excerpt — or, if you turn it on, the image itself — is sent, and only to localhost.
/// </summary>
public sealed class Classifier
{
    private readonly HttpClient _http;
    public string Model { get; }

    public Classifier(string model, string baseUrl)
    {
        Model = model;
        _http = new HttpClient { BaseAddress = new Uri(baseUrl.TrimEnd('/')), Timeout = TimeSpan.FromMinutes(10) };
    }

    public async Task<string?> CheckAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync("/api/tags"));
            var names = doc.RootElement.GetProperty("models").EnumerateArray().Select(m => m.GetProperty("name").GetString() ?? "").ToList();
            bool ok = names.Any(n => n == Model || n == Model + ":latest" || (!Model.Contains(':') && n.StartsWith(Model + ":")));
            return ok ? null : $"Ollama is running, but the model \"{Model}\" isn't downloaded. Run: ollama pull {Model}";
        }
        catch { return "Ollama isn't running. Start the Ollama app (or run 'ollama serve'), then refresh."; }
    }

    private static readonly JsonObject Schema = (JsonObject)JsonNode.Parse($$"""
        {
          "type": "object",
          "properties": {
            "folder":    { "type": "string", "enum": {{JsonSerializer.Serialize(Folders.AiChoices)}} },
            "title":     { "type": "string" },
            "date":      { "type": "string" },
            "reason":    { "type": "string" }
          },
          "required": ["folder", "title", "date", "reason"]
        }
        """)!;

    private static string Prompt(string folderList) => $"""
        You help a busy person tidy their Downloads folder. Decide what ONE file is, from the evidence below.
        Reply with:
        - folder: the best folder from this list: {folderList}.
          Bills & Receipts = bills, invoices, receipts, order confirmations. Banking & Taxes = bank/credit card statements, tax forms (T4, T5, NOA), pay stubs.
          IDs & Personal = passports, licences, birth certificates, resumes, personal letters. Home & Car = lease, mortgage, insurance, utilities setup, car documents.
          Work = work documents, contracts, reports, presentations. School = courses, assignments, transcripts. Travel = tickets, boarding passes, bookings, itineraries.
          Medical = health records, prescriptions, test results. Photos = pictures of people, places or things. Other = anything else.
        - title: 2 to 6 words naming the file the way a person would, e.g. "Telus Mobility Bill", "Air Canada Boarding Pass", "Rental Lease Agreement". No date in the title. Use names from the content if present.
        - date: the main date the document is about (bill date, statement date, travel date) as YYYY-MM-DD, or "" if there isn't one.
        - reason: under 12 words, why you chose the folder.
        Use only the evidence. Do not invent names, dates or companies.
        """;

    public async Task<(Verdict Verdict, double Seconds)> ClassifyAsync(FileInfo f, string excerpt, byte[]? image, CancellationToken ct = default)
    {
        var evidence = new StringBuilder();
        evidence.AppendLine($"File name: {f.Name}");
        evidence.AppendLine($"Type: {f.Extension.TrimStart('.').ToUpperInvariant()}, {f.Length / 1024.0:F0} KB, last modified {f.LastWriteTime:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(excerpt)) evidence.AppendLine($"Text from the file (first part):\n\"\"\"\n{excerpt}\n\"\"\"");
        else if (image == null) evidence.AppendLine("No readable text: decide from the file name and type.");
        if (image != null) evidence.AppendLine("The image is attached.");

        var message = new JsonObject { ["role"] = "user", ["content"] = Prompt(string.Join(", ", Folders.AiChoices)) + "\n\nEvidence:\n" + evidence };
        if (image != null) message["images"] = new JsonArray(Convert.ToBase64String(image));

        var body = new JsonObject
        {
            ["model"] = Model,
            ["stream"] = false,
            ["format"] = Schema.DeepClone(),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_ctx"] = 4096 },
            ["messages"] = new JsonArray(message),
        };

        var sw = Stopwatch.StartNew();
        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync("/api/chat", content, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode) throw new InvalidOperationException($"Ollama returned {(int)resp.StatusCode}: {(text.Length > 200 ? text[..200] : text)}");
        using var doc = JsonDocument.Parse(text);
        return (Parse(doc.RootElement.GetProperty("message").GetProperty("content").GetString() ?? "{}"), sw.Elapsed.TotalSeconds);
    }

    public static Verdict Parse(string json)
    {
        int a = json.IndexOf('{'), b = json.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidOperationException("The model didn't return JSON.");
        using var doc = JsonDocument.Parse(json[a..(b + 1)]);
        var e = doc.RootElement;
        string Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        var folder = Str("folder");
        return new Verdict
        {
            Folder = Folders.AiChoices.FirstOrDefault(x => x.Equals(folder, StringComparison.OrdinalIgnoreCase)) ?? Folders.Other,
            Title = Str("title"),
            Date = Str("date"),
            Sensitive = e.TryGetProperty("sensitive", out var s) && s.ValueKind == JsonValueKind.True,
            Reason = Str("reason"),
        };
    }
}
