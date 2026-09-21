// La racine de composition, montée avec la configuration livrée (cache d'embeddings compris)
// contre un faux service IA dont on change ce qu'il sert en cours de route (ADR 0003 et 0010, S4.2).

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Cli;
using Xunit;

namespace Assistant.Tests;

/// <summary>
/// Faux service IA (contrat v1, <c>/v1/embeddings</c> seulement) : un axe par mot du vocabulaire,
/// plus un axe constant (<see cref="Offset"/>) pour qu'aucun vecteur ne soit nul. <see cref="Model"/>
/// et <see cref="Offset"/> se changent à chaud, comme une équipe qui modifie ce qui est servi derrière
/// l'alias pendant que l'application tourne.
/// </summary>
public sealed class FakeAiService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly object _gate = new();
    private string _model = "faux:modele-a";
    private double _offset = 1.0;

    public string Url { get; }

    public string Model
    {
        get { lock (_gate) { return _model; } }
        set { lock (_gate) { _model = value; } }
    }

    /// <summary>Change les vecteurs sans changer l'identifiant (préfixes modifiés, moteur sans empreinte).</summary>
    public double Offset
    {
        get { lock (_gate) { return _offset; } }
        set { lock (_gate) { _offset = value; } }
    }

    public FakeAiService()
    {
        // Un port libre : la sonde puis l'écoute ne sont pas atomiques, on réessaie si un autre processus s'est glissé entre.
        for (var attempt = 0; ; attempt++)
        {
            var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                (_listener, Url) = (listener, $"http://127.0.0.1:{port}");
                break;
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
        _ = Task.Run(Serve);
    }

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
            byte[] bytes;
            try
            {
                using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
                var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
                var vectors = body["inputs"]!.AsArray().Select(t => Vector(t!.GetValue<string>())).ToList();
                bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    model = Model, alias = body["model"]!.GetValue<string>(), dimension = vectors[0].Length, vectors,
                }));
            }
            catch (Exception error)
            {
                // Une requête inattendue échoue tout de suite, au lieu de laisser le client attendre.
                context.Response.StatusCode = 500;
                bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { error = new { code = "faux_service", message = error.Message } }));
            }
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private double[] Vector(string text)
    {
        var lowered = text.ToLowerInvariant();
        return Fakes.Vocabulary.Select(w => lowered.Contains(w) ? 1.0 : 0.0).Prepend(Offset).ToArray();
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

/// <summary>
/// Processus long (<c>serve</c>), configuration livrée (cache d'embeddings compris) : ce qui est servi
/// derrière l'alias change, puis on réindexe (ADR 0003 et 0010, séquence 4.2).
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private const string Question = "Combien de jours de télétravail par semaine ?";
    private readonly FakeAiService _ai = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("fyc-composition-").FullName;
    private readonly AppConfig _config;

    public CompositionTests()
    {
        // Délai court : si le faux service ne répond pas, le test échoue vite au lieu d'attendre 5 minutes.
        _config = AppConfig.Load() with { AiBaseUrl = _ai.Url, Timeout = TimeSpan.FromSeconds(5) };
        Assert.True(_config.Decorator("cache_embeddings", false));   // la configuration livrée
    }

    public void Dispose()
    {
        _ai.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private Container Build() => Composition.Build(_config, new Overrides(IndexPath: Path.Combine(_dir, "index.json")));

    [Fact]
    public void Reindexing_follows_the_served_model_and_the_cache_does_not_hide_it()
    {
        var container = Build();
        var alice = _config.User("alice");

        Assert.Equal("faux:modele-a", container.IndexCorpus.Execute().EmbeddingModel);
        container.SearchPassages.Execute(alice, Question, 4);                  // mémorisée par le cache
        Assert.True(container.CheckStatus.Execute().UpToDate);

        // Nouveau modèle derrière le même alias, MÊME dimension : sans contrôle, la panne serait silencieuse.
        _ai.Model = "faux:modele-b";
        // Question déjà posée : vecteurs du même modèle que l'index, la recherche reste cohérente.
        Assert.Equal("faux:modele-a", container.SearchPassages.Execute(alice, Question, 4).Manifest.EmbeddingModel);
        Assert.Throws<IndexModelMismatchException>(                           // question nouvelle : détecté
            () => container.SearchPassages.Execute(alice, "Quel est le plafond d'un repas d'affaires ?", 4));
        Assert.False(container.CheckStatus.Execute().UpToDate);                // status n'utilise pas le cache

        // La réindexation suit le modèle servi, et la question déjà posée repart au service.
        Assert.Equal("faux:modele-b", container.IndexCorpus.Execute().EmbeddingModel);
        Assert.Equal("faux:modele-b", container.SearchPassages.Execute(alice, Question, 4).Manifest.EmbeddingModel);
        Assert.True(container.CheckStatus.Execute().UpToDate);
    }

    [Fact]
    public void New_vectors_under_the_same_model_name_are_picked_up_by_reindexing()
    {
        // Préfixes changés côté service : même identifiant de modèle, autres vecteurs.
        var container = Build();
        var alice = _config.User("alice");
        var before = container.IndexCorpus.Execute();
        container.SearchPassages.Execute(alice, Question, 4);                   // mémorisée par le cache

        _ai.Offset = 3.0;
        var after = container.IndexCorpus.Execute();
        Assert.Equal(before.IndexId, after.IndexId);   // rien, dans l'identifiant, ne trahit le changement
        var again = container.SearchPassages.Execute(alice, Question, 4).Passages.Select(p => (p.Chunk.Id, p.Score));
        var fresh = Build().SearchPassages.Execute(alice, Question, 4).Passages.Select(p => (p.Chunk.Id, p.Score));
        Assert.Equal(fresh, again);
    }
}

public class CompositionRootTests
{
    private const string Leak = "Okay, let's see. The user is asking about remote work. The passage [1] says two days per week.";
    private static readonly GenerationRequest Request = new("système", "Passages :\n[1] x\n\nQuestion : ?", 0.2, 100, null);

    [Fact]
    public void A_rejected_output_is_refused_and_logged_whatever_else_is_stacked()
    {
        var logs = new List<string>();
        var config = AppConfig.Load() with
        {
            Decorators = new Dictionary<string, string> { ["cache_embeddings"] = "true", ["retries"] = "2", ["log"] = "true" },
        };
        var (_, generator) = Composition.Decorate(new KeywordEmbedder(), ScriptedGenerator.WithModel("qwen", Leak), config, logs.Add, () => null);
        Assert.Throws<ModelOutputRejectedException>(() => generator.Generate(Request));
        Assert.Contains(logs, line => line.Contains("rejetée"));   // la validation est sous le journal des générations

        var off = config with { Decorators = new Dictionary<string, string>(config.Decorators) { ["validate_output"] = "false" } };
        var (_, unchecked_) = Composition.Decorate(new KeywordEmbedder(), ScriptedGenerator.WithModel("qwen", Leak), off, _ => { }, () => null);
        Assert.Equal(Leak, unchecked_.Generate(Request).Text);
    }

    [Fact]
    public void The_threshold_follows_the_embedding_model_chosen_on_the_command_line()
    {
        var config = AppConfig.Load();
        Assert.NotEqual(config.MinScoreFor("nomic"), config.MinScoreFor(config.EmbeddingModel));
        Assert.Equal(config.MinScoreFor("nomic"), Composition.Build(config, embeddingModel: "nomic").Settings.MinScore);
        Assert.Equal(0.9, Composition.Build(config, embeddingModel: "nomic", minScore: 0.9).Settings.MinScore);
    }
}
