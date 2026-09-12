// Le transfert naïf, tel que l'énonce l'opinion couramment admise : « on déclare une
// interface DANS LE DOMAINE (un générateur de texte), on l'implémente dans
// l'infrastructure, et changer de fournisseur ne touche pas au métier ».
//
// C'est exactement ce que fait ce fichier. Le fil rouge, lui, déclare ses ports dans la
// couche application (Assistant.Application/Ports.cs) : ce sont les cas d'usage qui ont
// besoin d'un générateur, pas les entités. La différence est expliquée dans le README.

namespace TransfertNaif.Domaine;

/// <summary>Un générateur de texte : un détail d'infrastructure, dit-on.</summary>
public interface ITextGenerator
{
    string Generate(string instructions, string prompt);
}

/// <summary>Un fournisseur de vecteurs : un autre détail d'infrastructure, dit-on aussi.</summary>
public interface IEmbeddingProvider
{
    double[] Embed(string text);
}
