// Cas d'usage : l'index est-il encore valable ? (séquence 4.2)
// Un index dépend de trois choses qui vivent ailleurs que dans le code : le corpus,
// le découpage et le modèle d'embeddings. Si l'une a changé depuis l'indexation,
// les réponses ne sont plus explicables. La réindexation est au RAG ce que le
// réentraînement est à un modèle appris.

namespace Assistant.Application;

public sealed record StatusReport(
    IndexManifest? Index,
    int CorpusDocuments,
    string CorpusFingerprint,
    IReadOnlyDictionary<string, object> Splitter,
    string? EmbeddingModel,
    int? EmbeddingDimension,
    string? AiServiceError,
    string PromptVersion,
    IReadOnlyList<string> Issues)
{
    public bool UpToDate => Issues.Count == 0;

    /// <summary>Rien à refaire de connu, mais le modèle servi n'a pas pu être vérifié.</summary>
    public bool Unverified => AiServiceError is not null && Issues.Count == 1;
}

public sealed class CheckStatus
{
    private readonly IDocumentSource _source;
    private readonly ITextSplitter _splitter;
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly IPromptRepository _prompts;
    private readonly string _promptName;

    public CheckStatus(IDocumentSource source, ITextSplitter splitter, IEmbedder embedder,
                       IVectorIndex index, IPromptRepository prompts, string promptName = "answer")
    {
        _source = source;
        _splitter = splitter;
        _embedder = embedder;
        _index = index;
        _prompts = prompts;
        _promptName = promptName;
    }

    public StatusReport Execute()
    {
        var manifest = _index.Manifest();
        var documents = _source.Load();
        var fingerprint = Fingerprints.Corpus(documents);
        var splitter = _splitter.Describe();
        var promptVersion = _prompts.Get(_promptName).Version;

        string? model = null;
        int? dimension = null;
        string? serviceError = null;
        try
        {
            var probe = _embedder.EmbedQuery("vérification du modèle servi");
            (model, dimension) = (probe.Model, probe.Dimension);
        }
        catch (AiServiceException error)
        {
            serviceError = error.Message;
        }

        var issues = new List<string>();
        if (manifest is null)
        {
            issues.Add("aucun index : lancer l'indexation");
        }
        else
        {
            if (fingerprint != manifest.CorpusFingerprint)
            {
                issues.Add($"corpus modifié depuis l'indexation ({manifest.DocumentCount} documents indexés, "
                           + $"{documents.Count} aujourd'hui) : réindexer");
            }
            if (!SameSplitter(splitter, manifest.Splitter))
            {
                issues.Add($"découpage modifié ({Describe(manifest.Splitter)} → {Describe(splitter)}) : réindexer");
            }
            if (model is not null && (model != manifest.EmbeddingModel || dimension != manifest.Dimension))
            {
                issues.Add($"le service IA sert « {model} » ({dimension} dim.) mais l'index a été construit "
                           + $"avec « {manifest.EmbeddingModel} » ({manifest.Dimension} dim.) : réindexer");
            }
        }
        if (serviceError is not null)
        {
            issues.Add($"service IA injoignable, modèle servi non vérifié : {serviceError}");
        }

        return new StatusReport(manifest, documents.Count, fingerprint, splitter, model, dimension,
                                serviceError, promptVersion, issues);
    }

    public static string Describe(IReadOnlyDictionary<string, object> splitter) =>
        "{" + string.Join(", ", splitter.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                                       .Select(kv => $"{kv.Key}: {kv.Value}")) + "}";

    private static bool SameSplitter(IReadOnlyDictionary<string, object> a, IReadOnlyDictionary<string, object> b) =>
        Describe(a) == Describe(b);
}
