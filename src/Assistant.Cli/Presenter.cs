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
        JsonSerializer.SerializeToNode(splitter, Json)!.AsObject();

    public static JsonObject ManifestToJson(IndexManifest m) => new()
    {
        ["index_id"] = m.IndexId,
        ["embedding_model"] = m.EmbeddingModel,
        ["dimension"] = m.Dimension,
        ["corpus_fingerprint"] = m.CorpusFingerprint,
        ["splitter"] = Splitter(m.Splitter),
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
                lines.AppendLine($"  sortie brute {i + 1} : \"{t.RawOutputs[i]}\"");
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
        ["splitter"] = Splitter(r.Splitter),
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
            ? $"Service IA : injoignable ({r.AiServiceError})"
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

    public static string ComparisonToText(SnapshotComparison c, bool showChanges = true)
    {
        var lines = new StringBuilder();
        lines.AppendLine($"Comparaison : {c.Baseline} → {c.Candidate}").AppendLine();
        lines.AppendLine("Différences de configuration");
        if (c.ConfigurationDifferences.Count > 0)
        {
            foreach (var d in c.ConfigurationDifferences)
            {
                lines.AppendLine($"  - {d.Key} : {SnapshotComparer.Canonical(d.Before)} → {SnapshotComparer.Canonical(d.After)}");
            }
        }
        else
        {
            lines.AppendLine("  (aucune : même configuration des deux côtés)");
        }
        lines.AppendLine().AppendLine("Dérive");
        lines.AppendLine($"  questions comparées : {c.Compared}");
        lines.AppendLine($"  réponses modifiées  : {c.Changed}");
        lines.AppendLine($"  taux de dérive      : {(c.DriftRate is null ? "—" : c.DriftRate.Value.ToString("P0", CultureInfo.InvariantCulture))}").AppendLine();
        lines.AppendLine("| Nature | Nombre | Lecture |").AppendLine("|---|---|---|");
        var readings = new (string Kind, string Reading)[]
        {
            (DifferenceKind.StatusChanged, "changement de comportement : refus devenu réponse, ou l'inverse"),
            (DifferenceKind.SourcesChanged, "même décision, autres documents cités"),
            (DifferenceKind.TextChanged, "mêmes sources, même décision : reformulation, la dérive la plus bénigne"),
            (DifferenceKind.Identical, "rien n'a bougé"),
            (DifferenceKind.Missing, "question présente d'un seul côté"),
        };
        foreach (var (kind, reading) in readings)
        {
            lines.AppendLine($"| {kind} | {c.Count(kind)} | {reading} |");
        }
        if (showChanges)
        {
            var changes = c.Differences.Where(d => d.Kind != DifferenceKind.Identical && d.Kind != DifferenceKind.Missing).ToList();
            if (changes.Count > 0)
            {
                lines.AppendLine();
                foreach (var d in changes)
                {
                    lines.AppendLine($"{d.QuestionId} [{d.Kind}]");
                    lines.AppendLine($"  avant : {d.Before!.Status} [{string.Join(", ", d.Before.CitedDocuments)}] « {Head(d.Before.Text)} »");
                    lines.AppendLine($"  après : {d.After!.Status} [{string.Join(", ", d.After.CitedDocuments)}] « {Head(d.After.Text)} »");
                }
            }
        }
        return lines.ToString().TrimEnd();
    }

    private static string Head(string text) => text.Length <= 90 ? text : text[..90];
}
