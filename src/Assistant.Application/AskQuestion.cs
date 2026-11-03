// Cas d'usage : répondre à une question à partir des documents accessibles.
//
// Les étapes vont du plus déterministe au moins déterministe :
// 1. Les règles métier sur la question.
// 2. La recherche des passages, filtrée par les droits d'accès. Elle reste déterministe tant que
//    le modèle d'embeddings ne change pas.
// 3. Le seuil de pertinence. Sans passage pertinent, on n'appelle pas le modèle.
// 4. La génération, seule étape probabiliste. Deux vérifications déterministes l'encadrent : la
//    forme de la sortie, contrôlée par le décorateur de validation, puis les citations.

using System.Text;
using Assistant.Domain;

namespace Assistant.Application;

public sealed record AskSettings(
    // Attention : ce seuil n'a de sens que pour UN modèle d'embeddings et UN corpus donnés.
    // La racine de composition passe en général celui que la configuration donne pour l'alias utilisé.
    // Si l'alias n'en a pas, elle prend `default`, et la ligne de commande, le banc d'essai et les
    // expériences le signalent.
    double MinScore,
    int TopK = 4,
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
                       IPromptRepository prompts, AskSettings settings)
    {
        _search = new SearchPassages(embedder, index);
        _generator = generator;
        _prompts = prompts;
        _settings = settings;
    }

    /// <summary>
    /// Numérote les passages pour que le modèle puisse les citer. Quand le découpage a déjà mis le titre dans
    /// le morceau, il apparaît deux fois. On garde cette redondance de quelques mots : la retirer changerait les
    /// prompts, et donc les mesures déjà faites.
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

        // La recherche filtrée par les droits est celle du cas d'usage SearchPassages. Sans index,
        // ou avec un index construit par un autre modèle, elle lève une exception et on s'arrête là.
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
                // Le décorateur de validation a refusé la forme de la sortie. La tentative est ratée,
                // mais on garde le texte rejeté dans la trace.
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
