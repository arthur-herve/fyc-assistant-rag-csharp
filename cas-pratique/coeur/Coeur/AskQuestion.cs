// Cas d'usage : répondre à une question à partir des documents accessibles.
//
// Les étapes vont du plus déterministe au moins déterministe :
// 1. Les règles métier sur la question.
// 2. La recherche des passages, filtrée par les droits d'accès. Elle reste déterministe tant que
//    le modèle d'embeddings ne change pas.
// 3. Le seuil de pertinence. Sans passage pertinent, on n'appelle pas le modèle.
// 4. La génération, seule étape probabiliste. Une vérification déterministe la suit : les citations.

using System.Text;

namespace Coeur;

public sealed record AskSettings(
    // Attention : ce seuil n'a de sens que pour UN modèle d'embeddings et UN corpus donnés (ADR 0004).
    // La racine de composition doit donc passer celui que la configuration donne pour le modèle utilisé.
    double MinScore,
    int TopK = 4,
    int MaxAttempts = 2,
    double Temperature = 0.2,
    int MaxTokens = 400,
    int? Seed = null,
    string PromptName = "answer");

public sealed class AskQuestion
{
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly IGenerator _generator;
    private readonly IPromptRepository _prompts;
    private readonly AskSettings _settings;
    private readonly AccessPolicy _access;

    public AskQuestion(IEmbedder embedder, IVectorIndex index, IGenerator generator,
                       IPromptRepository prompts, AskSettings settings)
    {
        _embedder = embedder;
        _index = index;
        _generator = generator;
        _prompts = prompts;
        _settings = settings;
        _access = new AccessPolicy();
    }

    /// <summary>
    /// Numérote les passages pour que le modèle puisse les citer. Quand le découpage a déjà mis le titre dans
    /// le morceau, il apparaît deux fois. On garde cette redondance de quelques mots pour que les prompts restent
    /// ceux des mesures de référence (eval/resultats).
    /// </summary>
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

        var manifest = _index.Manifest() ?? throw new IndexNotBuiltException();

        var query = _embedder.EmbedQuery(question);
        if (query.Model != manifest.EmbeddingModel || query.Dimension != manifest.Dimension)
        {
            throw new IndexModelMismatchException(manifest.EmbeddingModel, manifest.Dimension, query.Model, query.Dimension);
        }

        var passages = _index.Search(query.Vectors[0], _settings.TopK, chunk => _access.CanRead(user, chunk));
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
            var generation = _generator.Generate(new GenerationRequest(
                template.System, prompt, _settings.Temperature, _settings.MaxTokens, seed));
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

        // Règle métier : pas de réponse non sourcée. On renvoie plutôt les passages, que l'utilisateur lira lui-même.
        var all = relevant.Select((p, i) => new Source(i + 1, p.Chunk.DocumentId, p.Chunk.DocumentTitle, p.Chunk.Id)).ToList();
        return new Answer(question, AnswerStatus.Unsourced, Messages.Unsourced, all,
            Trace(manifest, generationModel, template.Version, retrieved, _settings.MaxAttempts, rawOutputs));
    }

    private AnswerTrace Trace(IndexManifest manifest, string? generationModel, string promptVersion,
                              IReadOnlyList<Retrieved> retrieved, int attempts, List<string> rawOutputs) =>
        new(manifest.IndexId, manifest.EmbeddingModel, generationModel, promptVersion, retrieved,
            _settings.MinScore, attempts, rawOutputs.ToList());
}
