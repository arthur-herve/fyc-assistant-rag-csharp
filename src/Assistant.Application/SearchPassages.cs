// Cas d'usage : retrouver les passages qu'un utilisateur a le droit de lire.
//
// C'est la moitié déterministe de la chaîne, tant que le modèle d'embeddings ne change pas.
// AskQuestion s'en sert, et le banc d'essai aussi : il mesure la recherche seule pour
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

    public SearchPassages(IEmbedder embedder, IVectorIndex index)
    {
        _embedder = embedder;
        _index = index;
        _access = new AccessPolicy();
    }

    /// <summary>
    /// Cherche les <paramref name="topK"/> passages les plus proches de la question parmi ceux que
    /// l'utilisateur peut lire. Les droits sont donc filtrés ici, avant tout prompt.
    /// Avant de chercher, on vérifie que l'index existe et qu'il a été construit avec le modèle qui a
    /// produit le vecteur de la question, c'est-à-dire celui que sert le service IA. Si l'index vient
    /// d'un autre modèle, on lève <see cref="IndexModelMismatchException"/> : il faut réindexer,
    /// jamais contourner.
    /// </summary>
    public Retrieval Execute(User user, string question, int topK)
    {
        var manifest = _index.Manifest() ?? throw new IndexNotBuiltException();

        var query = _embedder.EmbedQuery(question);
        if (query.Model != manifest.EmbeddingModel || query.Dimension != manifest.Dimension)
        {
            throw new IndexModelMismatchException(manifest.EmbeddingModel, manifest.Dimension, query.Model, query.Dimension);
        }

        var passages = _index.Search(query.Vectors[0], topK, chunk => _access.CanRead(user, chunk));
        return new Retrieval(manifest, passages);
    }
}
