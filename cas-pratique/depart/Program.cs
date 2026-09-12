// Assistant documentaire RAG pour l'intranet — version 0.3
//
// Répond aux questions des salariés à partir des fiches Markdown du dossier corpus/,
// en citant les fiches utilisées. Les fiches RH et direction sont réservées.
//
// Usage :
//     dotnet run -- index
//     dotnet run -- ask "Combien de jours de congés ?" --user alice
//     dotnet run -- ask "..." --user bruno --model qwen3-4b
//
// Nécessite le service IA (python -m ai_service, port 8100) et Ollama.
// TODO: ajouter des tests, gérer le cas où le service est down, passer à une vraie base vectorielle.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

static class Program
{
    static string AI_URL = "http://127.0.0.1:8100";
    static string EMBED_MODEL = "bge-m3";
    static string GEN_MODEL = "llama3-2-3b";
    static readonly string CORPUS_DIR = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "corpus");
    static readonly string INDEX_FILE = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "index.bin");
    const int CHUNK_SIZE = 800;
    const int TOP_K = 4;
    const double THRESHOLD = 0.65;   // ajusté à la main sur quelques questions, marche bien avec bge-m3
    const int MAX_TOKENS = 400;

    static readonly Dictionary<string, string[]> USERS = new()
    {
        ["alice"] = new[] { "tous" },
        ["bruno"] = new[] { "tous", "rh" },
        ["claire"] = new[] { "tous", "direction" },
    };

    const string PROMPT = @"Tu es l'assistant documentaire interne de l'entreprise.
Tu réponds en français, uniquement à partir des passages fournis, en trois phrases maximum.
Après chaque affirmation, indique entre crochets le numéro du passage, par exemple [1].
Si un passage est marqué (réservé), ne le cite pas et ne l'utilise pas si l'utilisateur n'y a pas droit.
Si les passages ne permettent pas de répondre, dis-le.

Passages :
{passages}

Question : {question}
";

    static List<Entry> INDEX;  // chargé à la demande
    static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(5) };

    class Doc { public string id; public string title; public string text; public string[] groups; }
    class Entry { public string doc { get; set; } public string title { get; set; } public string[] groups { get; set; } public int n { get; set; } public string text { get; set; } public double[] vector { get; set; } }

    // -----------------------------------------------------------------------
    // Corpus
    // -----------------------------------------------------------------------

    static List<Doc> LoadDocs()
    {
        var docs = new List<Doc>();
        foreach (var path in Directory.GetFiles(CORPUS_DIR, "*.md").OrderBy(p => p))
        {
            var content = File.ReadAllText(path).Replace("\r\n", "\n");
            var m = Regex.Match(content, @"^---\n(.*?)\n---\n(.*)", RegexOptions.Singleline);
            if (!m.Success)
            {
                Console.WriteLine("fichier ignoré (pas d'en-tête): " + Path.GetFileName(path));
                continue;
            }
            var meta = new Dictionary<string, string>();
            foreach (var line in m.Groups[1].Value.Split('\n'))
            {
                var i = line.IndexOf(':');
                if (i > 0) meta[line[..i].Trim()] = line[(i + 1)..].Trim();
            }
            var name = Path.GetFileNameWithoutExtension(path);
            docs.Add(new Doc
            {
                id = meta.GetValueOrDefault("id", name),
                title = meta.GetValueOrDefault("titre", name),
                text = m.Groups[2].Value.Trim(),
                groups = meta.GetValueOrDefault("groupes", "tous").Split(',').Select(g => g.Trim()).ToArray(),
            });
        }
        return docs;
    }

    static List<string> Split(string text)
    {
        var chunks = new List<string>();
        var current = "";
        foreach (var para in text.Split("\n\n"))
        {
            if (current.Length + para.Length > CHUNK_SIZE && current.Length > 0)
            {
                chunks.Add(current.Trim());
                current = "";
            }
            current += para + "\n\n";
        }
        if (current.Trim().Length > 0) chunks.Add(current.Trim());
        return chunks;
    }

    // -----------------------------------------------------------------------
    // Service IA
    // -----------------------------------------------------------------------

    static JsonElement Call(string path, object payload)
    {
        var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = http.PostAsync(AI_URL + path, body).Result;
        return JsonSerializer.Deserialize<JsonElement>(resp.Content.ReadAsStringAsync().Result);
    }

    static List<double[]> Embed(List<string> texts, string kind = "document")
    {
        var vectors = new List<double[]>();
        for (var i = 0; i < texts.Count; i += 32)
        {
            var r = Call("/v1/embeddings", new { model = EMBED_MODEL, input_type = kind, inputs = texts.Skip(i).Take(32) });
            foreach (var v in r.GetProperty("vectors").EnumerateArray())
                vectors.Add(v.EnumerateArray().Select(x => x.GetDouble()).ToArray());
        }
        return vectors;
    }

    static string Generate(string prompt, int? seed = null)
    {
        var r = Call("/v1/generate", new { model = GEN_MODEL, system = "", prompt, temperature = 0.2, max_tokens = MAX_TOKENS, seed });
        var text = r.GetProperty("text").GetString();
        // qwen3 met parfois son raisonnement entre <think>…</think>, on l'enlève
        text = Regex.Replace(text, "<think>.*?</think>", "", RegexOptions.Singleline).Trim();
        return text;
    }

    // -----------------------------------------------------------------------
    // Index
    // -----------------------------------------------------------------------

    static void BuildIndex()
    {
        var docs = LoadDocs();
        var entries = new List<Entry>();
        foreach (var d in docs)
        {
            var n = 0;
            foreach (var chunk in Split(d.text))
                entries.Add(new Entry { doc = d.id, title = d.title, groups = d.groups, n = n++, text = d.title + "\n" + chunk });
        }
        Console.WriteLine($"{entries.Count} morceaux pour {docs.Count} documents, vectorisation...");
        var vectors = Embed(entries.Select(e => e.text).ToList());
        for (var i = 0; i < entries.Count; i++)
        {
            var v = vectors[i];
            var norm = Math.Sqrt(v.Sum(x => x * x));
            if (norm == 0) norm = 1.0;
            entries[i].vector = v.Select(x => x / norm).ToArray();
        }
        File.WriteAllBytes(INDEX_FILE, JsonSerializer.SerializeToUtf8Bytes(entries));
        Console.WriteLine("index écrit : " + Path.GetFullPath(INDEX_FILE));
    }

    static List<Entry> GetIndex()
    {
        if (INDEX == null)
        {
            if (!File.Exists(INDEX_FILE))
            {
                Console.WriteLine("Pas d'index, lancez d'abord : dotnet run -- index");
                Environment.Exit(1);
            }
            INDEX = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllBytes(INDEX_FILE));
        }
        return INDEX;
    }

    static List<(double score, Entry e)> Search(string question, string user)
    {
        var q = Embed(new List<string> { question }, "query")[0];
        var norm = Math.Sqrt(q.Sum(x => x * x));
        if (norm == 0) norm = 1.0;
        q = q.Select(x => x / norm).ToArray();
        var scored = new List<(double, Entry)>();
        foreach (var e in GetIndex())
        {
            var score = q.Zip(e.vector, (a, b) => a * b).Sum();
            scored.Add((score, e));
        }
        var top = scored.OrderByDescending(t => t.Item1).Take(TOP_K).ToList();
        // on retire ce que l'utilisateur n'a pas le droit de lire
        var allowed = new List<(double, Entry)>();
        foreach (var (score, e) in top)
        {
            if (e.groups.Contains("tous") || e.groups.Intersect(USERS[user]).Any())
                allowed.Add((score, e));
            else
                Console.WriteLine($"  (passage réservé ignoré : {e.doc}#{e.n}, score {score:0.00})");
        }
        return allowed.Where(t => t.Item1 >= THRESHOLD).ToList();
    }

    // -----------------------------------------------------------------------
    // Réponse
    // -----------------------------------------------------------------------

    static (string text, List<string> sources) Ask(string question, string user)
    {
        if (!USERS.ContainsKey(user))
        {
            Console.WriteLine("utilisateur inconnu : " + user);
            Environment.Exit(1);
        }
        var passages = Search(question, user);
        if (passages.Count == 0)
            return ("Je n'ai trouvé aucun document qui réponde à cette question.", new List<string>());
        var blocks = new List<string>();
        for (var i = 0; i < passages.Count; i++)
        {
            var e = passages[i].e;
            var tag = e.groups.Contains("tous") ? "" : " (réservé)";
            blocks.Add($"[{i + 1}] {e.title}{tag}\n{e.text}");
        }
        var prompt = PROMPT.Replace("{passages}", string.Join("\n\n", blocks)).Replace("{question}", question);
        var text = Generate(prompt);
        if (!text.Contains('['))
        {
            // le modèle a oublié de citer, on lui redemande
            text = Generate(prompt + "\nN'oublie pas les numéros de passage entre crochets.", seed: 1);
        }
        var cited = Regex.Matches(text, @"\[(\d+)\]").Select(m => int.Parse(m.Groups[1].Value)).Distinct().OrderBy(n => n).ToList();
        var sources = cited.Where(n => n >= 1 && n - 1 < passages.Count).Select(n => passages[n - 1].e.doc).ToList();
        return (text, sources);
    }

    static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        if (args.Length == 0) { Console.WriteLine("usage : index [--embed alias] | ask \"question\" [--user alice] [--model alias] [--embed alias]"); return 1; }
        string user = "alice", question = null;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--user") user = args[++i];
            else if (args[i] == "--model") GEN_MODEL = args[++i];
            else if (args[i] == "--embed") EMBED_MODEL = args[++i];
            else question = args[i];
        }
        if (args[0] == "index")
        {
            BuildIndex();
        }
        else
        {
            var (text, sources) = Ask(question, user);
            Console.WriteLine(text);
            if (sources.Count > 0) Console.WriteLine("Sources : " + string.Join(", ", sources));
        }
        return 0;
    }
}
