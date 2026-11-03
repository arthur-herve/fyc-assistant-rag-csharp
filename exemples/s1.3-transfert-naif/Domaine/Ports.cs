// Le transfert naïf suit l'approche couramment admise : on déclare une interface dans le
// domaine, on l'implémente dans l'infrastructure, et changer de fournisseur ne touche pas au
// métier. Ce fichier déclare l'interface, Infrastructure/Providers.cs l'implémente.

namespace TransfertNaif.Domaine;

/// <summary>Le générateur de texte. Le modèle qui le sert est un détail d'infrastructure.</summary>
public interface ITextGenerator
{
    string Generate(string instructions, string prompt);
}
