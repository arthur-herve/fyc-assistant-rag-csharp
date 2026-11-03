// Cas d'usage : indexer le corpus documentaire.

namespace Assistant.Application;

/// <summary>Ce que l'indexation a traité : le nombre de documents et de morceaux.</summary>
public sealed record IndexSummary(int DocumentCount, int ChunkCount);

public sealed class IndexCorpus
{
    private readonly IDocumentSource _source;
    private readonly ITextSplitter _splitter;
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly int _batchSize;

    public IndexCorpus(IDocumentSource source, ITextSplitter splitter, IEmbedder embedder,
                       IVectorIndex index, int batchSize = 32)
    {
        _source = source;
        _splitter = splitter;
        _embedder = embedder;
        _index = index;
        _batchSize = batchSize;
    }

    public IndexSummary Execute()
    {
        var documents = _source.Load();
        var chunks = documents.SelectMany(d => _splitter.Split(d)).ToList();
        if (chunks.Count == 0)
        {
            throw new EmptyCorpusException();
        }

        var vectors = new List<double[]>();
        for (var start = 0; start < chunks.Count; start += _batchSize)
        {
            var batch = chunks.Skip(start).Take(_batchSize).ToList();
            var result = _embedder.EmbedDocuments(batch.Select(c => c.Text).ToList());
            if (result.Count != batch.Count)
            {
                throw new InconsistentEmbeddingsException(
                    $"{batch.Count} textes envoyés, {result.Count} vecteurs reçus");
            }
            vectors.AddRange(result);
        }

        _index.Replace(chunks, vectors);
        return new IndexSummary(documents.Count, chunks.Count);
    }
}
