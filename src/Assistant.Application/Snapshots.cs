// Cas d'usage : figer le comportement du système, puis mesurer ce qui a bougé.
// Un système dont un maillon est probabiliste ne se valide pas par assertion exacte
// (séquence 3.1). On enregistre ses réponses à un jeu de questions fixe avec la
// configuration qui les a produites, on change UNE chose, on enregistre à nouveau,
// et on compare : le taux de dérive n'a de sens que lu à côté des différences de
// configuration (séquence 3.2, principe CACE).

using System.Text.RegularExpressions;
using Assistant.Domain;

namespace Assistant.Application;

public sealed record SnapshotQuestion(string Id, User User, string Question);

public sealed class RecordSnapshot
{
    public static readonly Regex ValidName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$", RegexOptions.Compiled);

    private readonly AskQuestion _ask;
    private readonly ISnapshotStore _store;
    private readonly IClock _clock;
    private readonly IReadOnlyDictionary<string, string> _configuration;

    public RecordSnapshot(AskQuestion ask, ISnapshotStore store, IClock clock,
                          IReadOnlyDictionary<string, string> configuration)
    {
        _ask = ask;
        _store = store;
        _clock = clock;
        _configuration = configuration;
    }

    public Snapshot Execute(string name, IReadOnlyList<SnapshotQuestion> questions)
    {
        if (!ValidName.IsMatch(name))
        {
            throw new InvalidSnapshotNameException(name);   // avant de poser la moindre question
        }
        var configuration = new SortedDictionary<string, string>(_configuration.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal);
        var entries = new List<SnapshotEntry>();
        foreach (var q in questions)
        {
            // Une erreur (index absent, modèle incompatible, service IA en panne) est
            // systématique : on l'annonce plutôt que d'enregistrer N entrées en erreur.
            var answer = _ask.Execute(q.User, q.Question);
            var trace = answer.Trace;
            configuration.TryAdd("index_id", trace.IndexId);
            configuration.TryAdd("embedding_model_id", trace.EmbeddingModel);
            if (trace.GenerationModel is not null)
            {
                configuration.TryAdd("generation_model_id", trace.GenerationModel);
            }
            if (trace.PromptVersion is not null)
            {
                configuration.TryAdd("prompt_version", trace.PromptVersion);
            }
            entries.Add(new SnapshotEntry(
                q.Id, q.User.Id, q.Question, StatusNames.Of(answer.Status),
                answer.Sources.Select(s => s.DocumentId).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToList(),
                answer.Text, trace.Attempts));
        }
        var snapshot = new Snapshot(name, _clock.Now().ToString("yyyy-MM-dd'T'HH:mm:ssK"), configuration, entries);
        _store.Save(snapshot);
        return snapshot;
    }
}

/// <summary>Natures d'écart, de la plus bénigne à la plus grave.</summary>
public static class DifferenceKind
{
    public const string Identical = "identique";
    public const string TextChanged = "texte modifié";        // mêmes sources, même statut : reformulation
    public const string SourcesChanged = "sources modifiées"; // la réponse ne s'appuie plus sur le même matériau
    public const string StatusChanged = "statut modifié";     // un refus devient une réponse, ou l'inverse
    public const string Missing = "absente d'un des deux";
}

public sealed record EntryDifference(string QuestionId, string Kind, SnapshotEntry? Before, SnapshotEntry? After);

public sealed record ConfigurationDifference(string Key, string? Before, string? After);

public sealed record SnapshotComparison(
    string Baseline,
    string Candidate,
    IReadOnlyList<ConfigurationDifference> ConfigurationDifferences,
    IReadOnlyList<EntryDifference> Differences)
{
    public int Compared => Differences.Count(d => d.Kind != DifferenceKind.Missing);
    public int Changed => Differences.Count(d => d.Kind != DifferenceKind.Identical && d.Kind != DifferenceKind.Missing);
    public double? DriftRate => Compared == 0 ? null : Math.Round((double)Changed / Compared, 3);
    public int Count(string kind) => Differences.Count(d => d.Kind == kind);
}

public static class SnapshotComparer
{
    /// <summary>Fonction pure : aucune dépendance, testable exhaustivement.</summary>
    public static SnapshotComparison Compare(Snapshot baseline, Snapshot candidate)
    {
        var keys = baseline.Configuration.Keys.Union(candidate.Configuration.Keys).OrderBy(k => k, StringComparer.Ordinal);
        var configDiff = keys
            .Select(k => new ConfigurationDifference(k, baseline.Configuration.GetValueOrDefault(k), candidate.Configuration.GetValueOrDefault(k)))
            .Where(d => d.Before != d.After)
            .ToList();
        var before = baseline.Entries.ToDictionary(e => e.QuestionId);
        var after = candidate.Entries.ToDictionary(e => e.QuestionId);
        var order = before.Keys.Concat(after.Keys.Where(k => !before.ContainsKey(k))).ToList();
        var differences = new List<EntryDifference>();
        foreach (var id in order)
        {
            var a = before.GetValueOrDefault(id);
            var b = after.GetValueOrDefault(id);
            string kind;
            if (a is null || b is null)
            {
                kind = DifferenceKind.Missing;
            }
            else if (a.Status != b.Status)
            {
                kind = DifferenceKind.StatusChanged;
            }
            else if (!a.CitedDocuments.SequenceEqual(b.CitedDocuments))
            {
                kind = DifferenceKind.SourcesChanged;
            }
            else if (a.Text.Trim() != b.Text.Trim())
            {
                kind = DifferenceKind.TextChanged;
            }
            else
            {
                kind = DifferenceKind.Identical;
            }
            differences.Add(new EntryDifference(id, kind, a, b));
        }
        return new SnapshotComparison(baseline.Name, candidate.Name, configDiff, differences);
    }
}
