// Décorateurs de ports côté application : la couche anticorruption (séquence 4.1).
// Un décorateur implémente le même port que l'objet qu'il enveloppe ; le cas d'usage
// ne sait pas s'il parle au modèle ou à un garde-fou. Ceux qui portent une règle
// métier vivent ici ; les purement techniques (cache, journal, tentatives) vivent
// dans l'infrastructure. L'assemblage se fait uniquement dans la racine de composition.

using Assistant.Domain;

namespace Assistant.Application;

/// <summary>
/// Refuse une sortie qui n'a pas la forme d'une réponse (vide, trop longue, autre langue,
/// raisonnement déversé). Le cas d'usage compte le rejet comme une tentative ratée.
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
