// Décorateurs de ports côté application : des garde-fous autour du modèle (séquence 4.1).
// Ce n'est pas tout à fait la couche anticorruption d'Evans, qui traduit un modèle étranger dans le
// nôtre. Ici, ce rôle revient au service IA, qui offre le même contrat pour tous les moteurs, et aux
// adaptateurs HTTP.
// Un décorateur implémente le même port que l'objet qu'il enveloppe, si bien que le cas d'usage ne
// sait pas s'il parle au modèle ou à un garde-fou. Les décorateurs qui portent une règle métier vivent
// ici. Ceux qui sont purement techniques, comme le cache, le journal ou les nouvelles tentatives, vivent
// dans l'infrastructure. Seule la racine de composition les assemble.

using Assistant.Domain;

namespace Assistant.Application;

/// <summary>
/// Refuse une sortie qui n'a pas la forme d'une réponse : vide, trop longue, dans une autre langue
/// ou encombrée du raisonnement du modèle. Le cas d'usage compte le rejet comme une tentative ratée.
/// </summary>
public sealed class OutputValidatingGenerator : IGenerator
{
    private readonly IGenerator _inner;
    private readonly int _maxChars;

    public OutputValidatingGenerator(IGenerator inner, int maxChars = 1500)
    {
        _inner = inner;
        _maxChars = maxChars;
    }

    public Generation Generate(GenerationRequest request)
    {
        var generation = _inner.Generate(request);
        var check = OutputRules.Check(generation.Text, _maxChars);
        if (!check.IsValid)
        {
            throw new ModelOutputRejectedException(generation.Model, generation.Text, check.Problems);
        }
        return generation;
    }
}
