// Règle métier n° 1 : qui peut lire quoi.

namespace Coeur;

/// <summary>
/// Le contrôle d'accès est appliqué de façon déterministe AVANT l'appel au modèle.
/// On ne demande jamais au modèle de « ne pas révéler » un document : un passage
/// interdit n'entre tout simplement pas dans le prompt.
/// </summary>
public sealed class AccessPolicy
{
    public const string PublicGroup = "tous";

    public bool CanRead(User user, Chunk chunk)
    {
        if (chunk.AllowedGroups.Contains(PublicGroup))
        {
            return true;
        }
        return user.Groups.Overlaps(chunk.AllowedGroups);
    }
}
