// Présentation des résultats des cas d'usage, en JSON ou en texte pour le terminal.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Assistant.Application;
using Assistant.Domain;

namespace Assistant.Cli;

public static class Presenter
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string F(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Le résumé affiché par index : ce que l'indexation a traité.</summary>
    public static string SummaryToText(IndexSummary s) =>
        $"Index construit : {s.DocumentCount} documents, {s.ChunkCount} morceaux.";

    public static JsonObject AnswerToJson(Answer answer, bool includeRaw = false)
    {
        var t = answer.Trace;
        var trace = new JsonObject
        {
            ["retrieved"] = new JsonArray(t.Retrieved.Select(r => (JsonNode)new JsonObject { ["chunk_id"] = r.ChunkId, ["score"] = r.Score }).ToArray()),
            ["min_score"] = t.MinScore,
        };
        if (includeRaw)
        {
            trace["raw_outputs"] = new JsonArray(t.RawOutputs.Select(r => (JsonNode)r).ToArray());
        }
        return new JsonObject
        {
            ["question"] = answer.Question,
            ["status"] = StatusNames.Of(answer.Status),
            ["text"] = answer.Text,
            ["sources"] = new JsonArray(answer.Sources.Select(s => (JsonNode)new JsonObject
            {
                ["number"] = s.Number, ["document_id"] = s.DocumentId, ["document_title"] = s.DocumentTitle, ["chunk_id"] = s.ChunkId,
            }).ToArray()),
            ["trace"] = trace,
        };
    }

    /// <summary>
    /// Le JSON affiché par ask --json.
    /// Il est indenté et garde les accents tels quels.
    /// </summary>
    public static string ToJson(JsonNode node) => node.ToJsonString(Json);

    public static string AnswerToText(Answer answer, bool verbose = false)
    {
        var lines = new StringBuilder();
        lines.AppendLine(answer.Text).AppendLine();
        if (answer.Sources.Count > 0)
        {
            lines.AppendLine("Sources :");
            foreach (var s in answer.Sources)
            {
                lines.AppendLine($"  [{s.Number}] {s.DocumentTitle} ({s.ChunkId})");
            }
        }
        var t = answer.Trace;
        lines.AppendLine();
        lines.AppendLine($"statut={StatusNames.Of(answer.Status)}");
        if (verbose)
        {
            lines.AppendLine($"seuil={F(t.MinScore)}");
            foreach (var r in t.Retrieved)
            {
                lines.AppendLine($"  retrouvé {r.ChunkId} score={F(r.Score)}");
            }
            for (var i = 0; i < t.RawOutputs.Count; i++)
            {
                // Chaque sortie brute s'écrit en chaîne JSON, sauts de ligne échappés : elle tient sur une ligne.
                lines.AppendLine($"  sortie brute {i + 1} : {JsonSerializer.Serialize(t.RawOutputs[i], Json)}");
            }
        }
        return lines.ToString().TrimEnd();
    }
}
