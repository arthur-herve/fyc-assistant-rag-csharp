// Cas d'usage : répondre à une question à partir des documents accessibles.
//
// Déroulé, du plus déterministe au moins déterministe :
// 1. règles métier sur la question (déterministe) ;
// 2. recherche des passages, filtrée par les droits d'accès (déterministe à
//    modèle d'embeddings fixé) ;
// 3. seuil de pertinence : sans passage pertinent, on n'appelle pas le modèle ;
// 4. génération (probabiliste), encadrée par deux vérifications déterministes :
//    la forme de la sortie (décorateur de validation) et les citations.

using System.Text;
using Assistant.Domain;

namespace Assistant.Application;

public sealed record AskSettings(
    int TopK = 4,
    // Attention : ce seuil n'a de sens que pour UN modèle d'embeddings et UN corpus donnés.
    double MinScore = 0.35,
    int MaxAttempts = 2,
    double Temperature = 0.2,
    int MaxTokens = 400,
    int? Seed = null,
    string PromptName = "answer");

public sealed class AskQuestion
{
    private readonly SearchPassages _search;
    private readonly IGenerator _generator;
    private readonly IPromptRepository _prompts;
    private readonly AskSettings _settings;

    public AskQuestion(IEmbedder embedder, IVectorIndex index, IGenerator generator,
                       IPromptRepository prompts, AskSettings? settings = null, AccessPolicy? accessPolicy = null)
    {
        _search = new SearchPassages(embedder, index, accessPolicy);
        _generator = generator;
        _prompts = prompts;
        _settings = settings ?? new AskSettings();
    }

    public AskSettings Settings => _settings;

    public static string FormatPassages(IReadOnlyList<Passage> passages)
    {
        var blocks = new StringBuilder();
        for (var i = 0; i < passages.Count; i++)
        {
            if (i > 0)
            {
                blocks.Append("\n\n");
            }
            blocks.Append('[').Append(i + 1).Append("] ").Append(passages[i].Chunk.DocumentTitle)
                  .Append('\n').Append(passages[i].Chunk.Text.Trim());
        }
        return blocks.ToString();
    }

    public Answer Execute(User user, string question)
    {
        question = question.Trim();
        if (question.Length == 0)
        {
            throw new EmptyQuestionException();
        }

        // Recherche filtrée par les droits (cas d'usage SearchPassages) : sans index,
        // ou avec un index construit par un autre modèle, on s'arrête ici.
        var (manifest, passages) = _search.Execute(user, question, _settings.TopK);
        var relevant = passages.Where(p => p.Score >= _settings.MinScore).ToList();
        var retrieved = passages.Select(p => new Retrieved(p.Chunk.Id, Math.Round(p.Score, 4))).ToList();

        if (relevant.Count == 0)
        {
            return new Answer(question, AnswerStatus.NoRelevantSource, Messages.NoRelevantSource, Array.Empty<Source>(),
                new AnswerTrace(manifest.IndexId, manifest.EmbeddingModel, null, null, retrieved,
                                _settings.MinScore, 0, Array.Empty<string>()));
        }

        var template = _prompts.Get(_settings.PromptName);
        var prompt = template.Render(question, FormatPassages(relevant));

        var rawOutputs = new List<string>();
        string? generationModel = null;
        for (var attempt = 1; attempt <= _settings.MaxAttempts; attempt++)
        {
            var seed = _settings.Seed is null ? (int?)null : _settings.Seed + attempt - 1;
            Generation generation;
            try
            {
                generation = _generator.Generate(new GenerationRequest(
                    template.System, prompt, _settings.Temperature, _settings.MaxTokens, seed));
            }
            catch (ModelOutputRejectedException rejected)
            {
                // Garde-fou de forme (décorateur de validation) : tentative ratée, tracée.
                generationModel = rejected.Model;
                rawOutputs.Add($"<rejetée : {string.Join(" ; ", rejected.Problems)}> {rejected.Text}");
                continue;
            }
            generationModel = generation.Model;
            rawOutputs.Add(generation.Text);
            var check = Citations.Check(generation.Text, relevant.Count);
            if (check.IsValid)
            {
                var sources = check.Cited
                    .Select(n => new Source(n, relevant[n - 1].Chunk.DocumentId, relevant[n - 1].Chunk.DocumentTitle, relevant[n - 1].Chunk.Id))
                    .ToList();
                return new Answer(question, AnswerStatus.Answered, generation.Text.Trim(), sources,
                    Trace(manifest, generationModel, template.Version, retrieved, attempt, rawOutputs));
            }
        }

        // Règle métier : pas de réponse non sourcée. On renvoie les passages.
        var all = relevant.Select((p, i) => new Source(i + 1, p.Chunk.DocumentId, p.Chunk.DocumentTitle, p.Chunk.Id)).ToList();
        return new Answer(question, AnswerStatus.Unsourced, Messages.Unsourced, all,
            Trace(manifest, generationModel, template.Version, retrieved, _settings.MaxAttempts, rawOutputs));
    }

    private AnswerTrace Trace(IndexManifest manifest, string? generationModel, string promptVersion,
                              IReadOnlyList<Retrieved> retrieved, int attempts, List<string> rawOutputs) =>
        new(manifest.IndexId, manifest.EmbeddingModel, generationModel, promptVersion, retrieved,
            _settings.MinScore, attempts, rawOutputs.ToList());
}
