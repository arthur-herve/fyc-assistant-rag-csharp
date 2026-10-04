// Présentation des résultats des cas d'usage (JSON ou texte pour le terminal).

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

    /// <summary>Le découpage (valeurs typées) en JSON : la couche interface n'a pas besoin de l'infrastructure pour cela.</summary>
    public static JsonObject Splitter(IReadOnlyDictionary<string, object> splitter) =>
        JsonSerializer.SerializeToNode(splitter)!.AsObject();

    // Clés du découpage triées : la même sortie, quel que soit l'ordre des clés dans le fichier d'index.
    private static JsonObject SortedSplitter(IReadOnlyDictionary<string, object> splitter) =>
        Splitter(new SortedDictionary<string, object>(splitter.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal));

    public static JsonObject ManifestToJson(IndexManifest m) => new()
    {
        ["index_id"] = m.IndexId,
        ["embedding_model"] = m.EmbeddingModel,
        ["dimension"] = m.Dimension,
        ["corpus_fingerprint"] = m.CorpusFingerprint,
        ["splitter"] = SortedSplitter(m.Splitter),
        ["document_count"] = m.DocumentCount,
        ["chunk_count"] = m.ChunkCount,
        ["created_at"] = m.CreatedAt,
    };

    public static JsonObject AnswerToJson(Answer answer, bool includeRaw = false)
    {
        var t = answer.Trace;
        var trace = new JsonObject
        {
            ["index_id"] = t.IndexId,
            ["embedding_model"] = t.EmbeddingModel,
            ["generation_model"] = t.GenerationModel,
            ["prompt_version"] = t.PromptVersion,
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
    /// Le JSON affiché par index, ask --json et status --json, et le corps des réponses de l'API HTTP : indenté,
    /// accents tels quels.
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
        lines.AppendLine($"statut={StatusNames.Of(answer.Status)} · embeddings={t.EmbeddingModel} · génération={t.GenerationModel ?? "—"} · "
                         + $"prompt={t.PromptVersion ?? "—"} · index={t.IndexId} · tentatives={t.Attempts}");
        if (verbose)
        {
            lines.AppendLine($"seuil={F(t.MinScore)}");
            foreach (var r in t.Retrieved)
            {
                lines.AppendLine($"  retrouvé {r.ChunkId} score={F(r.Score)}");
            }
            for (var i = 0; i < t.RawOutputs.Count; i++)
            {
                // Une chaîne JSON : entre guillemets, sauts de ligne échappés, une sortie par ligne.
                lines.AppendLine($"  sortie brute {i + 1} : {JsonSerializer.Serialize(t.RawOutputs[i], Json)}");
            }
        }
        return lines.ToString().TrimEnd();
    }

    public static JsonObject StatusToJson(StatusReport r) => new()
    {
        ["up_to_date"] = r.UpToDate,
        ["unverified"] = r.Unverified,
        ["issues"] = new JsonArray(r.Issues.Select(i => (JsonNode)i).ToArray()),
        ["index"] = r.Index is null ? null : ManifestToJson(r.Index),
        ["corpus"] = new JsonObject { ["documents"] = r.CorpusDocuments, ["fingerprint"] = r.CorpusFingerprint },
        ["splitter"] = SortedSplitter(r.Splitter),
        ["ai_service"] = new JsonObject { ["embedding_model"] = r.EmbeddingModel, ["dimension"] = r.EmbeddingDimension, ["error"] = r.AiServiceError },
        ["prompt_version"] = r.PromptVersion,
    };

    public static string StatusToText(StatusReport r)
    {
        var lines = new StringBuilder();
        lines.AppendLine("État de l'assistant").AppendLine("===================");
        if (r.Index is null)
        {
            lines.AppendLine("Index      : aucun");
        }
        else
        {
            var m = r.Index;
            lines.AppendLine($"Index      : {m.IndexId} · {m.ChunkCount} morceaux de {m.DocumentCount} documents · construit le {m.CreatedAt}");
            lines.AppendLine($"             modèle d'embeddings {m.EmbeddingModel} ({m.Dimension} dim.) · découpage {CheckStatus.Describe(m.Splitter)}");
            lines.AppendLine($"             empreinte du corpus {m.CorpusFingerprint[..12]}…");
        }
        lines.AppendLine($"Corpus     : {r.CorpusDocuments} documents · empreinte {r.CorpusFingerprint[..12]}…");
        lines.AppendLine($"Découpage  : {CheckStatus.Describe(r.Splitter)}");
        lines.AppendLine(r.AiServiceError is not null
            ? $"Service IA : en erreur ({r.AiServiceError})"
            : $"Service IA : sert {r.EmbeddingModel} ({r.EmbeddingDimension} dim.)");
        lines.AppendLine($"Prompt     : {r.PromptVersion}").AppendLine();
        if (r.UpToDate)
        {
            lines.AppendLine("Verdict    : à jour, l'index est cohérent avec le corpus, le découpage et le modèle servi.");
        }
        else
        {
            lines.AppendLine(r.Unverified ? "Verdict    : NON VÉRIFIÉ (corpus et découpage cohérents, modèle servi inconnu)" : "Verdict    : À REFAIRE");
            foreach (var issue in r.Issues)
            {
                lines.AppendLine($"  - {issue}");
            }
        }
        return lines.ToString().TrimEnd();
    }

    /// <summary>La comparaison pour la console : <see cref="ComparisonLines"/>, avec les fins de ligne du système.</summary>
    public static string ComparisonToText(SnapshotComparison c, bool showChanges = true) =>
        string.Join(Environment.NewLine, ComparisonLines(c, showChanges));

    /// <summary>
    /// Les lignes de la comparaison : un rapport les écrit avec des « \n », sous Windows aussi, et un texte de réponse
    /// y reste tel quel.
    /// </summary>
    public static List<string> ComparisonLines(SnapshotComparison c, bool showChanges = true)
    {
        var lines = new List<string> { $"Comparaison : {c.Baseline} → {c.Candidate}", "", "Différences de configuration" };
        if (c.ConfigurationDifferences.Count > 0)
        {
            foreach (var d in c.ConfigurationDifferences)
            {
                lines.Add($"  - {d.Key} : {SnapshotComparer.Canonical(d.Before)} → {SnapshotComparer.Canonical(d.After)}");
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
        if (showChanges)
        {
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
        }
        return lines;
    }

    /// <summary>
    /// Les 90 premiers caractères Unicode (points de code), et non 90 unités UTF-16 : un emoji compte pour un et n'est
    /// jamais coupé en deux (sa moitié s'écrivait « � ») ; une moitié de paire isolée compte pour un et reste telle quelle.
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
