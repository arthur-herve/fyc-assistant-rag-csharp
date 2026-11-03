// Le faux service IA des tests qui passent par HTTP : ligne de commande et expériences. Un seul
// double pour tous, sur un port libre (TestPorts), plutôt qu'un par fichier de tests.

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Assistant.Tests;

/// <summary>
/// Faux service IA qui respecte le contrat v1 : /health, des embeddings par mots-clés et une génération
/// qui cite le premier passage. Chaque mot du vocabulaire a son axe, et un axe constant garantit qu'aucun vecteur
/// n'est nul. Le modèle servi s'appelle « faux:&lt;alias&gt; », si bien que deux alias donnent deux modèles.
/// </summary>
public sealed class FakeAiService : IDisposable
{
    private readonly HttpListener _listener;
    private int _requests;

    public FakeAiService()
    {
        _listener = TestPorts.Listener();
        Url = _listener.Prefixes.Single().TrimEnd('/');
        _ = Task.Run(Serve);
    }

    public string Url { get; }

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
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        var body = JsonNode.Parse(await reader.ReadToEndAsync())!.AsObject();
        var alias = body["model"]!.GetValue<string>();
        if (alias.Length == 0)
        {
            // Le vrai service répond 404 à un alias qu'il ne connaît pas, et il n'en connaît aucun de vide : ce double
            // refuse donc l'alias vide, lui aussi.
            throw new InvalidOperationException("alias vide");
        }
        var model = $"faux:{alias}";
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

    private static double[] Vector(string text)
    {
        var lowered = text.ToLowerInvariant();
        return Fakes.Vocabulary.Select(w => lowered.Contains(w) ? 1.0 : 0.0).Prepend(1.0).ToArray();
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}
