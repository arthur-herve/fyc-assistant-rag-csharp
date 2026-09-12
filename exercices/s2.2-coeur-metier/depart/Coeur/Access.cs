// Règle métier n° 1 : qui peut lire quoi.                                    — À ÉCRIRE

namespace Coeur;

/// <summary>
/// Le contrôle d'accès est appliqué de façon déterministe AVANT l'appel au modèle.
/// On ne demande jamais au modèle de « ne pas révéler » un document : un passage
/// interdit n'entre tout simplement pas dans le prompt.
///
/// Règle : un morceau dont les groupes contiennent <see cref="PublicGroup"/> est lisible
/// par tout le monde ; sinon, il faut au moins un groupe en commun entre l'utilisateur
/// et le morceau. Tests : Coeur.Tests/DomainTests.cs (AccessPolicyTests).
/// </summary>
public sealed class AccessPolicy
{
    public const string PublicGroup = "tous";

    public bool CanRead(User user, Chunk chunk)
    {
        throw new NotImplementedException("AccessPolicy.CanRead : à écrire (exercice S2.2)");
    }
}
