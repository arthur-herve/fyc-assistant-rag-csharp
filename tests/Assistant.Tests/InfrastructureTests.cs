// Adaptateurs, décorateurs, contrat HTTP (contre un faux service) et règle de dépendance.

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Cli;
using Assistant.Domain;
using Assistant.Infrastructure;
using Xunit;

namespace Assistant.Tests;

public class MarkdownCorpusTests
{
    [Fact]
    public void Parses_header_and_groups()
    {
        var doc = MarkdownCorpus.Parse("---\nid: rh-1\ntitre: Grille des salaires\ngroupes: rh, direction\n---\n# Grille\nTexte.");
        Assert.Equal("rh-1", doc.Id);
        Assert.Equal("Grille des salaires", doc.Title);
        Assert.Equal(new HashSet<string> { "rh", "direction" }, doc.AllowedGroups);
        Assert.StartsWith("# Grille", doc.Text);
    }

    [Fact]
    public void Document_without_groups_is_public() =>
        Assert.Contains("tous", MarkdownCorpus.Parse("---\nid: x\n---\nTexte.").AllowedGroups);

    [Fact]
    public void Id_is_mandatory() =>
        Assert.Throws<CorpusFormatException>(() => MarkdownCorpus.Parse("---\ntitre: Sans id\n---\nTexte."));

    [Fact]
    public void Project_corpora_load()
    {
        var root = AppConfig.ProjectRoot;
        Assert.True(new MarkdownCorpus(Path.Combine(root, "corpus", "solveo")).Load().Count >= 5);
        var real = new MarkdownCorpus(Path.Combine(root, "corpus", "service-public")).Load();
        Assert.True(real.Count >= 300);
        Assert.Equal(new HashSet<string> { "tous", "rh", "direction" }, real.SelectMany(d => d.AllowedGroups).ToHashSet());
    }
}

public class SplitterTests
{
    [Fact]
    public void Short_document_gives_one_chunk_with_title()
    {
        var chunks = new ParagraphSplitter().Split(Fakes.Doc("a", "Un paragraphe court."));
        Assert.Single(chunks);
        Assert.StartsWith("A\n", chunks[0].Text);
        Assert.Equal("a#0", chunks[0].Id);
    }

    [Fact]
    public void Long_document_is_cut_with_overlap()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 12).Select(i => $"Paragraphe numéro {i} " + new string('x', 150)));
        var chunks = new ParagraphSplitter(400, 60, includeTitle: false).Split(Fakes.Doc("a", text));
        Assert.True(chunks.Count > 3);
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 400 + 60));
        Assert.Contains(chunks[0].Text[^20..].Trim(), chunks[1].Text);   // recouvrement
    }

    [Fact]
    public void Describe_is_recorded_in_the_manifest()
    {
        var describe = new ParagraphSplitter(300, 50).Describe();
        Assert.Equal(300, describe["max_chars"]);
        Assert.Equal("paragraph", describe["type"]);
    }
}

public class JsonVectorIndexTests
{
    [Fact]
    public void Round_trip_through_the_file()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var path = Path.Combine(dir.FullName, "index.json");
            var index = new JsonVectorIndex(path);
            var manifest = new IndexManifest("id", "m", 2, "fp", new Dictionary<string, object> { ["type"] = "whole" }, 1, 2, "t");
            var chunks = new[] { new Chunk("a#0", "a", "A", "x", 0, new HashSet<string> { "tous" }), new Chunk("a#1", "a", "A", "y", 1, new HashSet<string> { "rh" }) };
            index.Replace(manifest, chunks, new[] { new[] { 1.0, 0.0 }, new[] { 0.0, 1.0 } });

            var reloaded = new JsonVectorIndex(path);
            Assert.Equal("id", reloaded.Manifest()!.IndexId);
            var hits = reloaded.Search(new[] { 0.9, 0.1 }, 2, c => c.AllowedGroups.Contains("tous"));
            Assert.Single(hits);
            Assert.Equal("a#0", hits[0].Chunk.Id);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Reads_and_searches_an_index_written_by_the_python_version()
    {
        // Même format JSON : la fixture a été produite par fyc-assistant-rag (Python, moteur hashing 64 dim.)
        // sur le corpus Solvéo. Le modèle d'embeddings doit correspondre, pas le langage de l'application.
        var index = new JsonVectorIndex(Path.Combine(AppConfig.ProjectRoot, "tests", "Assistant.Tests", "fixtures", "index-python-hashing.json"));
        var manifest = index.Manifest();
        Assert.NotNull(manifest);
        Assert.Equal("hashing-64-stem6", manifest!.EmbeddingModel);
        Assert.Equal(15, manifest.ChunkCount);
        Assert.Equal(800, manifest.Splitter["max_chars"]);
        // Le vecteur du morceau « télétravail » retrouve ce morceau en tête : les vecteurs sont lus et normalisés.
        var teletravail = index.Search(new double[64], 1, _ => true);   // vecteur nul : aucun score, mais aucune erreur
        Assert.Single(teletravail);
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(AppConfig.ProjectRoot, "tests", "Assistant.Tests", "fixtures", "index-python-hashing.json")))!;
        var vector = root["vectors"]![0]!.AsArray().Select(x => x!.GetValue<double>()).ToArray();
        var hits = index.Search(vector, 1, _ => true);
        Assert.Equal(root["chunks"]![0]!["id"]!.GetValue<string>(), hits[0].Chunk.Id);
        Assert.InRange(hits[0].Score, 0.999, 1.001);
    }

    [Fact]
    public void Index_id_matches_the_python_formula()
    {
        // Même corpus, même modèle, même découpage → même identifiant que json.dumps(sort_keys=True) en Python.
        var splitter = new Dictionary<string, object> { ["type"] = "paragraph", ["max_chars"] = 800, ["overlap_chars"] = 120, ["include_title"] = true };
        var identity = $"[{Fingerprints.PythonJson("fp")}, {Fingerprints.PythonJson("m")}, {64}, {Fingerprints.PythonJson(splitter)}]";
        Assert.Equal("[\"fp\", \"m\", 64, {\"include_title\": true, \"max_chars\": 800, \"overlap_chars\": 120, \"type\": \"paragraph\"}]", identity);
    }
}

public class PromptAndSnapshotFilesTests
{
    [Fact]
    public void Prompt_version_carries_a_fingerprint_of_the_file()
    {
        var template = new FilePromptRepository(Composition.PromptsDir).Get("answer");
        Assert.StartsWith("v1+", template.Version);
        Assert.Contains("{passages}", template.User);
        Assert.Contains("[1]", template.Render("Q ?", "[1] Titre\ntexte"));
    }

    [Fact]
    public void Snapshot_store_round_trip_and_names()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var store = new JsonSnapshotStore(Path.Combine(dir.FullName, "instantanes"));
            Assert.Empty(store.Names());
            var snapshot = new Snapshot("ref", "2026-09-11T12:00:00+00:00",
                new Dictionary<string, object?> { ["generation_model"] = "extractive", ["top_k"] = 4, ["seed"] = null, ["splitter"] = new Dictionary<string, object> { ["max_chars"] = 800 } },
                new[] { new SnapshotEntry("q1", "alice", "Q ?", "answered", new[] { "a", "b" }, "Texte [1]", 1) });
            store.Save(snapshot);
            var loaded = store.Load("ref");
            Assert.Equal(snapshot.Entries[0] with { CitedDocuments = Array.Empty<string>() }, loaded.Entries[0] with { CitedDocuments = Array.Empty<string>() });
            Assert.Equal(new[] { "a", "b" }, loaded.Entries[0].CitedDocuments);
            Assert.Equal("extractive", loaded.Configuration["generation_model"]);
            Assert.Equal(4, loaded.Configuration["top_k"]);
            Assert.Null(loaded.Configuration["seed"]);
            Assert.Empty(SnapshotComparer.Compare(snapshot, loaded).ConfigurationDifferences);   // relu du disque = construit en mémoire
            Assert.Equal(new[] { "ref" }, store.Names());
            Assert.Throws<SnapshotNotFoundException>(() => store.Load("absent"));
            Assert.Throws<InvalidSnapshotNameException>(() => store.Load("../autre"));
        }
        finally
        {
            dir.Delete(true);
        }
    }
}

public class DecoratorTests
{
    private static readonly GenerationRequest Request = new("système", "Passages :\n[1] x\n\nQuestion : ?", 0.2, 100);

    private sealed class Flaky : IGenerator
    {
        private readonly int _failures;
        public int Calls { get; private set; }
        public Flaky(int failures) => _failures = failures;

        public Generation Generate(GenerationRequest request)
        {
            Calls++;
            if (Calls <= _failures)
            {
                throw new AiServiceException("injoignable");
            }
            return new Generation("llm", "Deux jours [1].");
        }
    }

    [Fact]
    public void Cache_embeds_the_same_text_once()
    {
        var inner = new KeywordEmbedder();
        var cached = new CachedEmbedder(inner);
        var first = cached.EmbedQuery("télétravail");
        Assert.Same(first, cached.EmbedQuery("télétravail"));
        Assert.Single(inner.Calls);
        Assert.Equal((1, 1), (cached.Hits, cached.Misses));
    }

    [Fact]
    public void Cache_does_not_confuse_two_batches_with_the_same_joined_text()
    {
        var inner = new KeywordEmbedder();
        var cached = new CachedEmbedder(inner);
        cached.EmbedDocuments(new[] { "a b", "c" });
        cached.EmbedDocuments(new[] { "a", "b c" });
        Assert.Equal(2, inner.Calls.Count);
        Assert.Equal((0, 2), (cached.Hits, cached.Misses));
    }

    [Fact]
    public void Retries_a_transient_error_then_succeeds()
    {
        var inner = new Flaky(1);
        var slept = new List<TimeSpan>();
        var generation = new RetryingGenerator(inner, attempts: 1, sleep: slept.Add).Generate(Request);
        Assert.Equal("Deux jours [1].", generation.Text);
        Assert.Equal(2, inner.Calls);
        Assert.Single(slept);
    }

    [Fact]
    public void Does_not_retry_a_refused_request()
    {
        var calls = 0;
        var refusing = new LambdaGenerator(_ => { calls++; throw new AiServiceException("HTTP 404 — modèle inconnu", transient: false); });
        Assert.Throws<AiServiceException>(() => new RetryingGenerator(refusing, attempts: 3, sleep: _ => { }).Generate(Request));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Validation_rejects_leaked_reasoning()
    {
        var generator = new OutputValidatingGenerator(ScriptedGenerator.WithModel("qwen", "Okay, let's see. The user is asking… [1]"));
        var error = Assert.Throws<ModelOutputRejectedException>(() => generator.Generate(Request));
        Assert.Equal("qwen", error.Model);
        Assert.Contains(error.Problems, p => p.Contains("raisonnement"));
    }

    [Fact]
    public void Logging_reports_model_and_duration()
    {
        var lines = new List<string>();
        new LoggingGenerator(ScriptedGenerator.WithModel("llm-x", "Deux jours [1]."), lines.Add).Generate(Request);
        Assert.Contains("llm-x", lines[0]);
        Assert.Contains("ms", lines[0]);
    }

    private sealed class LambdaGenerator : IGenerator
    {
        private readonly Func<GenerationRequest, Generation> _f;
        public LambdaGenerator(Func<GenerationRequest, Generation> f) => _f = f;
        public Generation Generate(GenerationRequest request) => _f(request);
    }
}

/// <summary>Ce que l'application envoie au service IA, et ce qu'elle attend en retour (docs/contrat-http.md).</summary>
public class HttpContractTests : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly string _url;
    private readonly List<(string Path, JsonObject Body)> _requests = new();

    public HttpContractTests()
    {
        // Un port réellement libre, plutôt qu'un tirage au hasard.
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _url = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(_url);
        _listener.Start();
        _ = Task.Run(Serve);
    }

    private static readonly TimeSpan Short = TimeSpan.FromSeconds(5);

    private async Task Serve()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
            lock (_requests)
            {
                _requests.Add((context.Request.Url!.AbsolutePath, body));
            }
            string response;
            var status = 200;
            if (context.Request.Url.AbsolutePath == "/v1/embeddings")
            {
                var n = body["inputs"]!.AsArray().Count;
                response = JsonSerializer.Serialize(new { model = "ollama:nomic-embed-text@0a109f422b47", alias = "nomic", dimension = 3,
                                                         vectors = Enumerable.Repeat(new[] { 0.1, 0.2, 0.3 }, n) });
            }
            else if (body["model"]!.GetValue<string>() == "inconnu")
            {
                status = 404;
                response = """{"error": {"code": "unknown_model", "message": "modèle de génération inconnu : inconnu"}}""";
            }
            else
            {
                response = JsonSerializer.Serialize(new { model = "ollama:llama3.2:3b@a80c", alias = "llama3-2-3b", text = "Deux jours [1].", duration_ms = 5 });
            }
            var bytes = Encoding.UTF8.GetBytes(response);
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }

    [Fact]
    public void Embeddings_request_declares_an_intent_and_returns_the_concrete_model()
    {
        var batch = new HttpEmbedder(_url, "nomic", Short).EmbedQuery("Combien de jours ?");
        Assert.Equal("ollama:nomic-embed-text@0a109f422b47", batch.Model);
        Assert.Equal(3, batch.Dimension);
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/embeddings", path);
        Assert.Equal("nomic", body["model"]!.GetValue<string>());
        Assert.Equal("query", body["input_type"]!.GetValue<string>());
    }

    [Fact]
    public void Generation_request_sends_the_prompt_built_by_the_application()
    {
        var generation = new HttpGenerator(_url, "llama3-2-3b", Short).Generate(new GenerationRequest("sys", "prompt", 0.2, 150, 42));
        Assert.Equal("Deux jours [1].", generation.Text);
        Assert.Equal("ollama:llama3.2:3b@a80c", generation.Model);
        var (path, body) = _requests.Single();
        Assert.Equal("/v1/generate", path);
        Assert.Equal("llama3-2-3b", body["model"]!.GetValue<string>());
        Assert.Equal("sys", body["system"]!.GetValue<string>());
        Assert.Equal("prompt", body["prompt"]!.GetValue<string>());
        Assert.Equal(0.2, body["temperature"]!.GetValue<double>());
        Assert.Equal(150, body["max_tokens"]!.GetValue<int>());
        Assert.Equal(42, body["seed"]!.GetValue<int>());
    }

    [Fact]
    public void A_refused_request_is_not_transient()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpGenerator(_url, "inconnu", Short).Generate(new GenerationRequest("s", "p", 0.2, 10)));
        Assert.Contains("HTTP 404", error.Message);
        Assert.False(error.Transient);
    }

    [Fact]
    public void An_unreachable_service_is_transient() =>
        Assert.True(Assert.Throws<AiServiceException>(() => new HttpEmbedder("http://127.0.0.1:1", "x", TimeSpan.FromSeconds(2)).EmbedQuery("a")).Transient);

    [Fact]
    public void An_invalid_address_is_an_explicit_non_transient_error()
    {
        var error = Assert.Throws<AiServiceException>(() => new HttpEmbedder("localhost:8100", "x", Short).EmbedQuery("a"));
        Assert.False(error.Transient);
        Assert.Contains("invalide", error.Message);
    }
}

/// <summary>Règle de dépendance, vérifiée sur les assemblies compilés (séquence 2.2).</summary>
public class ArchitectureTests
{
    private static IEnumerable<string> References(Type anyTypeOfAssembly) =>
        anyTypeOfAssembly.Assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static readonly HashSet<string> DomainAllowed = new()
    {
        "System.Runtime", "System.Collections", "System.Linq", "System.Text.RegularExpressions", "System.Memory", "netstandard",
    };

    [Fact]
    public void Domain_depends_on_nothing_but_the_runtime() =>
        // Liste blanche : System.Net.Http ou System.Text.Json dans le domaine feraient échouer ce test.
        Assert.All(References(typeof(Document)), name => Assert.Contains(name, DomainAllowed));

    [Fact]
    public void Application_depends_only_on_the_domain() =>
        Assert.All(References(typeof(AskQuestion)).Where(n => !n.StartsWith("System", StringComparison.Ordinal)),
                   name => Assert.Equal("Assistant.Domain", name));

    [Fact]
    public void Infrastructure_does_not_reference_json_or_http_from_the_application_or_domain()
    {
        // L'application ne connaît ni HTTP ni JSON : ce sont des détails de l'infrastructure.
        Assert.DoesNotContain(References(typeof(AskQuestion)), n => n.StartsWith("System.Net", StringComparison.Ordinal));
        Assert.DoesNotContain(References(typeof(Document)), n => n.StartsWith("System.Text.Json", StringComparison.Ordinal));
    }

    [Fact]
    public void Adapters_are_assembled_only_by_the_composition_root()
    {
        // Aucun constructeur public de l'infrastructure ne prend un autre type concret de l'infrastructure :
        // les décorateurs ne reçoivent que des ports, et personne d'autre que Composition n'empile.
        var infrastructure = typeof(HttpEmbedder).Assembly;
        var concrete = infrastructure.GetTypes().Where(t => t.IsClass && !t.IsAbstract && t.IsPublic && !typeof(Delegate).IsAssignableFrom(t)).ToHashSet();
        foreach (var type in concrete)
        {
            foreach (var ctor in type.GetConstructors())
            {
                foreach (var parameter in ctor.GetParameters())
                {
                    Assert.False(concrete.Contains(parameter.ParameterType),
                                 $"{type.Name}({parameter.Name}) reçoit un adaptateur concret au lieu d'un port");
                }
            }
        }
    }
}
