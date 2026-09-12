// Adaptateurs : le modèle est derrière une frontière réseau.
// Le service IA (ai_service/, en Python) est un déployable séparé ; ces adaptateurs
// traduisent les ports IEmbedder et IGenerator en appels HTTP selon docs/contrat-http.md.
// Le service peut être écrit dans n'importe quel langage : seul le contrat compte.

using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Assistant.Application;

namespace Assistant.Infrastructure;

/// <summary>Une fonction (url, charge utile) → réponse JSON. Remplaçable dans les tests.</summary>
public delegate JsonElement JsonTransport(string url, object payload);

public static class HttpTransport
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

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
                var json = JsonSerializer.Serialize(payload, payload.GetType(), Options);
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
                try
                {
                    detail = JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("message").GetString() ?? body;
                }
                catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
                {
                    // corps non JSON : on garde le texte brut
                }
                throw new AiServiceException($"Service IA : HTTP {(int)response.StatusCode} — {detail}",
                                             transient: (int)response.StatusCode >= 500);
            }
            try
            {
                return JsonDocument.Parse(body).RootElement.Clone();
            }
            catch (JsonException error)
            {
                throw new AiServiceException($"Réponse du service IA illisible : {error.Message}", transient: false);
            }
        };
    }

    public static void Require(JsonElement payload, params string[] keys)
    {
        var missing = keys.Where(k => !payload.TryGetProperty(k, out _)).ToList();
        if (missing.Count > 0)
        {
            throw new AiServiceException($"Réponse du service IA incomplète, champs manquants : [{string.Join(", ", missing)}]",
                                         transient: false);
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
        var vectors = payload.GetProperty("vectors").EnumerateArray()
            .Select(v => v.EnumerateArray().Select(x => x.GetDouble()).ToArray())
            .ToList();
        return new EmbeddingBatch(payload.GetProperty("model").GetString()!, payload.GetProperty("dimension").GetInt32(), vectors);
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
        return new Generation(payload.GetProperty("model").GetString()!, payload.GetProperty("text").GetString() ?? "");
    }
}
