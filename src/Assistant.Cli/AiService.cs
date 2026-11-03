// Ce que la ligne de commande vérifie du service IA avant un long travail.

using Assistant.Application;

namespace Assistant.Cli;

/// <summary>Le service IA doit répondre avant une expérience : on le vérifie d'abord, en une requête.</summary>
public static class AiService
{
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
}
