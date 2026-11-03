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
            ["attempts"] = t.Attempts,
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
        lines.AppendLine($"statut={StatusNames.Of(answer.Status)} · tentatives={t.Attempts}");
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

    /// <summary>
    /// La comparaison pour la console : les lignes de <see cref="ComparisonLines"/>, avec les fins de ligne du système.
    /// </summary>
    public static string ComparisonToText(SnapshotComparison c) =>
        string.Join(Environment.NewLine, ComparisonLines(c));

    /// <summary>
    /// Les lignes de la comparaison, sans fin de ligne. Le texte d'une réponse y reste tel quel.
    /// </summary>
    public static List<string> ComparisonLines(SnapshotComparison c)
    {
        var lines = new List<string> { $"Comparaison : {c.Baseline} → {c.Candidate}", "", "Différences de configuration" };
        if (c.ConfigurationDifferences.Count > 0)
        {
            foreach (var d in c.ConfigurationDifferences)
            {
                lines.Add($"  - {d.Key} : {ConfigurationValues.Canonical(d.Before)} → {ConfigurationValues.Canonical(d.After)}");
            }
        }
        else
        {
            lines.Add("  (aucune : même configuration des deux côtés)");
        }
        lines.Add("");
        lines.Add("Dérive");
        lines.Add($"  questions comparées : {c.Compared}");
        lines.Add($"  réponses modifiées  : {c.Changed}");
        lines.Add($"  taux de dérive      : {(c.DriftRate is null ? "—" : c.DriftRate.Value.ToString("P0", CultureInfo.InvariantCulture))}");
        lines.Add("");
        lines.Add("| Nature | Nombre | Lecture |");
        lines.Add("|---|---|---|");
        var readings = new (string Kind, string Reading)[]
        {
            (DifferenceKind.StatusChanged, "changement de comportement : refus devenu réponse, ou l'inverse"),
            (DifferenceKind.SourcesChanged, "même décision, autres documents cités"),
            (DifferenceKind.TextChanged, "mêmes sources, même statut, texte différent : à relire, le sens a pu changer (Oui devenu Non…)"),
            (DifferenceKind.Identical, "rien n'a bougé"),
            (DifferenceKind.Missing, "question présente d'un seul côté"),
        };
        foreach (var (kind, reading) in readings)
        {
            lines.Add($"| {kind} | {c.Count(kind)} | {reading} |");
        }
        var changes = c.Differences.Where(d => d.Kind != DifferenceKind.Identical && d.Kind != DifferenceKind.Missing).ToList();
        if (changes.Count > 0)
        {
            lines.Add("");
            foreach (var d in changes)
            {
                lines.Add($"{d.QuestionId} [{d.Kind}]");
                lines.Add($"  avant : {d.Before!.Status} [{string.Join(", ", d.Before.CitedDocuments)}] « {Head(d.Before.Text)} »");
                lines.Add($"  après : {d.After!.Status} [{string.Join(", ", d.After.CitedDocuments)}] « {Head(d.After.Text)} »");
            }
        }
        return lines;
    }

    /// <summary>
    /// Les 90 premiers caractères Unicode (points de code), et non 90 unités UTF-16 : un emoji compte pour un et n'est
    /// jamais coupé en deux, ce qui afficherait « � ». Une moitié de paire isolée compte aussi pour un et reste
    /// telle quelle.
    /// </summary>
    private static string Head(string text)
    {
        var end = 0;
        for (var count = 0; count < 90 && end < text.Length; count++)
        {
            end += char.IsSurrogatePair(text, end) ? 2 : 1;
        }
        return text[..end];
    }
}
