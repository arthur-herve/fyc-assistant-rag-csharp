// Adaptateurs : le modèle est derrière une frontière réseau.
// Le service IA (ai_service/, en Python) est un déployable séparé ; ces adaptateurs
// traduisent les ports IEmbedder et IGenerator en appels HTTP selon docs/contrat-http.md.
// Le service peut être écrit dans n'importe quel langage : seul le contrat compte.

using System.Text.Json;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>Une fonction (url, charge utile) → réponse JSON. Remplaçable dans les tests.</summary>
public delegate JsonElement JsonTransport(string url, object payload);

public static class HttpTransport
{
    public static JsonTransport Create(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        return (url, payload) =>
        {
            HttpResponseMessage response;
            string body;
            try
            {
                // Corps sérialisé d'un bloc, avec Content-Length : le service (bibliothèque standard
                // Python) ne lit pas les envois en morceaux (chunked).
                var json = JsonSerializer.Serialize(payload, payload.GetType());
                using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                response = client.PostAsync(url, content).GetAwaiter().GetResult();
                body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException or IOException)
            {
                throw new AiServiceException($"Service IA injoignable ({url}) : {error.Message}");
            }
            catch (Exception error) when (error is NotSupportedException or UriFormatException or InvalidOperationException)
            {
                throw new AiServiceException($"Adresse du service IA invalide ({url}) : {error.Message}", transient: false);
            }
            using var _ = response;
            if (!response.IsSuccessStatusCode)
            {
                var detail = body;
                var retryable = true;
                try
                {
                    using var error = JsonDocument.Parse(body);
                    var problem = error.RootElement.GetProperty("error");
                    detail = problem.GetProperty("message").GetString() ?? body;
                    retryable = !(problem.TryGetProperty("retryable", out var flag) && flag.ValueKind == JsonValueKind.False);
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // corps non JSON : on garde le texte brut
                }
                // 5xx : passager, sauf si le service dit que réessayer ne changera rien (modèle absent…).
                throw new AiServiceException($"Service IA : HTTP {(int)response.StatusCode} — {detail}",
                                             transient: (int)response.StatusCode >= 500 && retryable);
            }
            try
            {
                using var document = JsonDocument.Parse(body);
                return document.RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw new AiServiceException($"Réponse du service IA illisible : {error.Message}", transient: false);
            }
        };
    }

    public static void Require(JsonElement payload, params string[] keys)
    {
        var missing = keys.Where(k => payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty(k, out _)).ToList();
        if (missing.Count > 0)
        {
            throw new AiServiceException($"Réponse du service IA incomplète, champs manquants : [{string.Join(", ", missing)}]",
                                         transient: false);
        }
    }

    /// <summary>Lit une réponse qui a les bons champs mais peut-être pas les bons types.</summary>
    public static T Read<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is InvalidOperationException or FormatException)
        {
            throw new AiServiceException($"Réponse du service IA incohérente : {error.Message}", transient: false);
        }
    }
}

public sealed class HttpEmbedder : IEmbedder
{
    private readonly string _url;
    private readonly string _model;
    private readonly JsonTransport _post;

    public HttpEmbedder(string baseUrl, string model, TimeSpan? timeout = null, JsonTransport? transport = null)
    {
        _url = baseUrl.TrimEnd('/') + "/v1/embeddings";
        _model = model;
        _post = transport ?? HttpTransport.Create(timeout ?? TimeSpan.FromSeconds(120));
    }

    public EmbeddingBatch EmbedDocuments(IReadOnlyList<string> texts) => Embed(texts, "document");

    public EmbeddingBatch EmbedQuery(string text) => Embed(new[] { text }, "query");

    private EmbeddingBatch Embed(IReadOnlyList<string> texts, string inputType)
    {
        var payload = _post(_url, new { model = _model, input_type = inputType, inputs = texts });
        HttpTransport.Require(payload, "model", "dimension", "vectors");
        var (model, dimension, vectors) = HttpTransport.Read(() => (
            payload.GetProperty("model").GetString() ?? throw new FormatException("« model » est nul"),
            payload.GetProperty("dimension").GetInt32(),
            payload.GetProperty("vectors").EnumerateArray()
                .Select(v => v.EnumerateArray().Select(x => x.GetDouble()).ToArray())
                .ToList()));
        if (vectors.Count != texts.Count || vectors.Any(v => v.Length != dimension))
        {
            throw new AiServiceException(
                $"Réponse du service IA incohérente : {texts.Count} vecteurs de {dimension} dimensions attendus, reçu {vectors.Count} "
                + $"de [{string.Join(", ", vectors.Select(v => v.Length).Distinct().Order())}] dimensions", transient: false);
        }
        return new EmbeddingBatch(model, dimension, vectors);
    }
}

public sealed class HttpGenerator : IGenerator
{
    private readonly string _url;
    private readonly string _model;
    private readonly JsonTransport _post;

    public HttpGenerator(string baseUrl, string model, TimeSpan? timeout = null, JsonTransport? transport = null)
    {
        _url = baseUrl.TrimEnd('/') + "/v1/generate";
        _model = model;
        _post = transport ?? HttpTransport.Create(timeout ?? TimeSpan.FromSeconds(300));
    }

    public Generation Generate(GenerationRequest request)
    {
        var payload = _post(_url, new
        {
            model = _model,
            system = request.System,
            prompt = request.Prompt,
            temperature = request.Temperature,
            max_tokens = request.MaxTokens,
            seed = request.Seed,
        });
        HttpTransport.Require(payload, "model", "text");
        return HttpTransport.Read(() => new Generation(
            payload.GetProperty("model").GetString() ?? throw new FormatException("« model » est nul"),
            payload.GetProperty("text").GetString() ?? ""));
    }
}
