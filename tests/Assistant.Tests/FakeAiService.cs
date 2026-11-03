// Le faux service IA des tests qui passent par HTTP : composition, ligne de commande, banc et expériences. Un seul
// double pour tous, sur un port libre (TestPorts), plutôt qu'un par fichier de tests.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Tests;

/// <summary>
/// Faux service IA qui respecte le contrat v1 : /health, /v1/models, des embeddings par mots-clés et une génération
/// qui cite le premier passage. Chaque mot du vocabulaire a son axe, et un axe constant (<see cref="Offset"/>)
/// garantit qu'aucun vecteur n'est nul. Le modèle servi s'appelle « faux:&lt;alias&gt; », si bien que deux alias
/// donnent deux modèles. On peut imposer un <see cref="Model"/> pour tous les alias. Lui et <see cref="Offset"/> se
/// changent à chaud, comme une équipe qui modifie ce qu'elle sert derrière l'alias pendant que l'application tourne.
/// </summary>
public sealed class FakeAiService : IDisposable
{
    /// <summary>
    /// Les alias que /v1/models déclare pour chaque type. Les embeddings et la génération, eux, répondent à n'importe
    /// quel alias.
    /// </summary>
    public static readonly string[] EmbeddingAliases = { "hashing", "hashing-512" }, GenerationAliases = { "extractive" };

    private readonly HttpListener _listener;
    private readonly object _gate = new();
    private string? _model;
    private double _offset = 1.0;
    private int _requests;

    public FakeAiService(string? model = null)
    {
        _model = model;
        _listener = TestPorts.Listener();
        Url = _listener.Prefixes.Single().TrimEnd('/');
        _ = Task.Run(Serve);
    }

    public string Url { get; }

    /// <summary>Le modèle servi pour tous les alias. À null, chaque alias sert « faux:&lt;alias&gt; ».</summary>
    public string? Model
    {
        get { lock (_gate) { return _model; } }
        set { lock (_gate) { _model = value; } }
    }

    /// <summary>
    /// Change les vecteurs sans changer l'identifiant du modèle, comme des préfixes modifiés ou un moteur sans empreinte.
    /// </summary>
    public double Offset
    {
        get { lock (_gate) { return _offset; } }
        set { lock (_gate) { _offset = value; } }
    }

    /// <summary>Le nombre de requêtes reçues, toutes routes confondues.</summary>
    public int Requests => Volatile.Read(ref _requests);

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
            Interlocked.Increment(ref _requests);
            object response;
            try
            {
                response = await Answer(context.Request);
            }
            catch (Exception error)
            {
                // Une requête inattendue échoue tout de suite, au lieu de laisser le client attendre.
                context.Response.StatusCode = 500;
                response = new { error = new { code = "faux_service", message = error.Message } };
            }
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(response));
            context.Response.ContentType = "application/json";
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
            context.Response.Close();
        }
    }

    private async Task<object> Answer(HttpListenerRequest request)
    {
        var path = request.Url!.AbsolutePath;
        if (path == "/health")
        {
            return new { status = "ok" };
        }
        if (path == "/v1/models")
        {
            return new { embedding = Describe(EmbeddingAliases), generation = Describe(GenerationAliases) };
        }
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        var alias = body["model"]!.GetValue<string>();
        var model = Model ?? $"faux:{alias}";
        switch (path)
        {
            case "/v1/embeddings":
                var vectors = body["inputs"]!.AsArray().Select(t => Vector(t!.GetValue<string>())).ToList();
                return new { model, alias, dimension = vectors[0].Length, vectors };
            case "/v1/generate":
                return new { model, alias, text = "Réponse tirée du passage [1]." };
            default:
                throw new InvalidOperationException($"route inconnue : {path}");
        }
    }

    private static object[] Describe(IEnumerable<string> aliases) =>
        aliases.Select(alias => (object)new { alias, backend = "faux", description = "" }).ToArray();

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
