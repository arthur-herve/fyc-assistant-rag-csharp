// Cas d'usage : retrouver les passages qu'un utilisateur a le droit de lire.
//
// C'est la moitié déterministe de la chaîne (à modèle d'embeddings fixé) : elle sert
// à AskQuestion, mais aussi au banc d'essai, qui mesure la recherche seule pour
// calibrer le seuil de pertinence sans appeler le générateur.

using Assistant.Domain;

namespace Assistant.Application;

/// <summary>Résultat d'une recherche : les passages accessibles et l'index qui les a fournis.</summary>
public sealed record Retrieval(IndexManifest Manifest, IReadOnlyList<Passage> Passages);

public sealed class SearchPassages
{
    private readonly IEmbedder _embedder;
    private readonly IVectorIndex _index;
    private readonly AccessPolicy _access;

    public SearchPassages(IEmbedder embedder, IVectorIndex index, AccessPolicy? accessPolicy = null)
    {
        _embedder = embedder;
        _index = index;
        _access = accessPolicy ?? new AccessPolicy();
    }

    /// <summary>
    /// Vérifie que l'index existe et qu'il a été construit avec le modèle qui a produit les
    /// vecteurs de la question — servi, ou mémorisé pour cet index par le cache (ADR 0010) —
    /// (sinon <see cref="IndexModelMismatchException"/> : réindexer, jamais contourner),
    /// puis cherche les <paramref name="topK"/> passages les plus proches parmi ceux que
    /// l'utilisateur peut lire. Les droits sont filtrés ici, avant tout prompt.
    /// L'index peut être reconstruit par un autre processus entre la vérification et la recherche
    /// (<c>serve</c> relit le fichier quand il change) : la recherche ne porte que sur l'index contrôlé
    /// (<c>indexId</c>), et l'index lève <see cref="IndexReplacedException"/> s'il a été remplacé
    /// entre-temps, même par un index d'une autre dimension. On recommence alors une fois.
    /// </summary>
    public Retrieval Execute(User user, string question, int topK)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var manifest = _index.Manifest() ?? throw new IndexNotBuiltException();

            var query = _embedder.EmbedQuery(question);
            if (query.Model != manifest.EmbeddingModel || query.Dimension != manifest.Dimension)
            {
                throw new IndexModelMismatchException(manifest.EmbeddingModel, manifest.Dimension, query.Model, query.Dimension);
            }

            IReadOnlyList<Passage> passages;
            try
            {
                passages = _index.Search(query.Vectors[0], topK, chunk => _access.CanRead(user, chunk), indexId: manifest.IndexId);
            }
            catch (IndexReplacedException)
            {
                continue;
            }
            return new Retrieval(manifest, passages);
        }
        throw new IndexReplacedException();
    }
}
