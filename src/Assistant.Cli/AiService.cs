// Ce que la ligne de commande vérifie du service IA avant un long travail.

using System.Text.Json.Nodes;
using Assistant.Application;

namespace Assistant.Cli;

/// <summary>Le service IA doit répondre avant un banc ou une expérience : on le vérifie d'abord, en une requête.</summary>
public static class AiService
{
    /// <summary>Les deux types de modèles de GET /v1/models, nommés comme dans les messages.</summary>
    private static readonly Dictionary<string, string> Kinds = new()
    {
        ["embedding"] = "modèle d'embeddings", ["generation"] = "modèle de génération",
    };

    /// <summary>
    /// Vérifie que le service IA répond sur /health. Deux messages possibles : injoignable, ou joignable mais en
    /// erreur, avec l'adresse interrogée. GetAsync lit tout le corps sans l'interpréter : une réponse coupée ou trop
    /// lente compte comme « injoignable ».
    /// </summary>
    public static void Require(string baseUrl)
    {
        var url = baseUrl.TrimEnd('/') + "/health";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = http.GetAsync(url).GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                throw new AiServiceException($"le service IA répond HTTP {(int)response.StatusCode} sur {url}", transient: false);
            }
        }
        // Connexion refusée, délai dépassé, adresse invalide (UriFormatException, InvalidOperationException) ou
        // protocole autre que HTTP (NotSupportedException, qui finirait sinon en trace de pile) : un seul message.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or UriFormatException or NotSupportedException)
        {
            throw new AiServiceException($"service IA injoignable ({baseUrl}). Lancez-le d'abord : python -m ai_service", transient: false);
        }
    }

    /// <summary>
    /// Vérifie que l'alias --other d'une expérience est servi, et du bon type (GET /v1/models). On le fait avant toute
    /// indexation : sinon l'erreur n'apparaîtrait qu'après l'index et l'instantané « avant ».
    /// </summary>
    public static void RequireModel(string baseUrl, string kind, string alias)
    {
        var url = baseUrl.TrimEnd('/') + "/v1/models";
        var served = ServedAliases(url, kind)
                     ?? throw new AiServiceException($"le service IA ne donne pas la liste de ses modèles ({url})", transient: false);
        if (!served.Contains(alias))
        {
            throw new ArgumentException($"le service IA ne sert pas « {alias} » comme {Kinds[kind]} "
                                        + $"(servis : {(served.Count > 0 ? string.Join(", ", served) : "aucun")})");
        }
    }

    /// <summary>Les alias d'un type, dans l'ordre du service, ou null s'il est injoignable ou hors contrat.</summary>
    private static List<string>? ServedAliases(string url, string kind)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            // UTF-8 strict, BOM accepté, comme toute réponse du service IA : GetStringAsync remplacerait en silence un
            // octet qui n'est pas de l'UTF-8. Un statut d'erreur lève HttpRequestException.
            var body = TextFiles.DecodeUtf8(http.GetByteArrayAsync(url).GetAwaiter().GetResult());
            // JSON strict, au sens de JsonText : une clé en double, même là où rien n'est lu, met la réponse hors
            // contrat, tout comme un entier de plus de 4300 chiffres.
            if (JsonText.Parse(body, strings: true, maxDigits: JsonText.MaxDigits) is not JsonObject models
                || models[kind] is not JsonArray list)
            {
                return null;
            }
            var aliases = new List<string>();
            foreach (var item in list)
            {
                if (item is not JsonObject model || model["alias"] is not JsonValue alias || !alias.TryGetValue<string>(out var text))
                {
                    return null;   // un alias qui n'est pas un texte : hors contrat
                }
                aliases.Add(text);
            }
            return aliases;
        }
        // FormatException : des octets qui ne sont pas de l'UTF-8, ou un JSON qui n'est pas strict.
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidOperationException
                                               or System.Text.Json.JsonException or FormatException)
        {
            return null;
        }
    }
}
