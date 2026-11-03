// Cas d'usage : retrouver les passages qu'un utilisateur a le droit de lire.
//
// C'est la moitié déterministe de la chaîne, tant que le modèle d'embeddings ne change pas.
// AskQuestion s'en sert.

using Assistant.Domain;

namespace Assistant.Application;

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
    /// Avant de chercher, on vérifie que l'index existe.
    /// </summary>
    public IReadOnlyList<Passage> Execute(User user, string question, int topK)
    {
        if (!_index.IsBuilt())
        {
            throw new IndexNotBuiltException();
        }

        var query = _embedder.EmbedQuery(question);
        return _index.Search(query, topK, chunk => _access.CanRead(user, chunk));
    }
}
