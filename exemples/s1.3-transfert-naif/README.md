# Séquence 1.3 — Le transfert naïf : « le modèle est un détail d'infrastructure »

Cet exemple est un petit programme d'environ 150 lignes, indépendant du fil rouge. Il applique à la
lettre l'opinion couramment admise : on déclare un port dans le domaine, on l'implémente dans
l'infrastructure, puis on substitue un second fournisseur. Dans ce cas, l'approche tient réellement.
Vous allez d'abord réécrire l'adaptateur HTTP de l'exemple, puis changer de générateur pour le
constater. Pour aller plus loin, vous pourrez écrire un troisième adaptateur.

## Ce que vous avez

```
Domaine/Ports.cs             le port ITextGenerator, déclaré dans le domaine
Domaine/Assistant.cs         Assistant, qui répond à une question à partir de passages
Infrastructure/Providers.cs  FakeGenerator, sans réseau
                             HttpGenerator, qui appelle le service IA du fil rouge
Program.cs                   la commande generate, qui choisit l'adaptateur
```

Le service IA est celui du fil rouge. Lancez-le depuis la racine du dépôt avec `python -m ai_service` :
il écoute sur le port 8100. Sans lui, la commande fonctionne encore avec l'alias `fake`. Les alias
`extractive` et `extractive-variante` passent par le service, mais n'ont pas besoin d'Ollama.

## Avant de lire `HttpGenerator`

N'ouvrez pas tout de suite `Infrastructure/Providers.cs` : réécrivez d'abord vous-même l'adaptateur HTTP.
Lisez le port, dans `Domaine/Ports.cs`, puis `docs/contrat-http.md`, à la racine du dépôt, en
particulier la route `POST /v1/generate` et la section « Erreurs ».

1. Dans un nouveau fichier, `Infrastructure/MyHttpGenerator.cs`, écrivez une classe `MyHttpGenerator`
   qui implémente `ITextGenerator` en appelant le service IA. Son constructeur reçoit l'adresse du
   service et l'alias du modèle, comme celui d'`HttpGenerator`, construit dans `Program.cs`. Le nom
   change parce que `HttpGenerator` existe déjà.
2. Dans `Program.cs`, construisez votre classe à la place de `HttpGenerator`.
3. Le service IA lancé, posez la question par défaut, celle sur le télétravail :

   ```bash
   dotnet run --project exemples/s1.3-transfert-naif -- generate extractive
   ```

   Vous devez obtenir la phrase sur le télétravail, suivie de sa citation `[1]`.

Ouvrez alors `Infrastructure/Providers.cs` et comparez votre classe à `HttpGenerator` : la requête
envoyée, la lecture de la réponse, et ce qui se passe quand le service renvoie une erreur. Remettez
enfin `HttpGenerator` dans `Program.cs` : la suite de l'exemple s'en sert.

## La promesse tient

```bash
dotnet run --project exemples/s1.3-transfert-naif -- generate fake
dotnet run --project exemples/s1.3-transfert-naif -- generate extractive            # service IA, sans Ollama
dotnet run --project exemples/s1.3-transfert-naif -- generate extractive-variante   # service IA, sans Ollama
dotnet run --project exemples/s1.3-transfert-naif -- generate llama3-2-3b           # service IA et Ollama
```

D'une commande à l'autre, seul l'argument change, et `Assistant.cs` n'a pas bougé d'une ligne. Ici,
« le modèle est un détail » est vrai : le domaine ne connaît que le port, et changer de modèle revient
à changer d'adaptateur. Pour poser une autre question, ajoutez-la entre guillemets après l'alias.

Sur la question par défaut, `extractive-variante` répond comme `extractive` : un autre générateur ne
donne pas forcément une autre réponse. Pour voir une différence, posez-leur « Comment prendre ses
congés payés ? ». La variante recopie alors la phrase sur le télétravail : elle ne répond pas à la
question, mais elle cite bien son passage. C'est attendu : changer de générateur change les réponses,
pas le domaine.

## Questions à se poser

1. Qui, dans ce programme, connaît le nom du modèle ?

   Réponse : `Program.cs`, qui le lit sur la ligne de commande, et `HttpGenerator`, qui l'envoie au
   service IA avec chaque demande. Ni `Ports.cs` ni `Assistant.cs` ne le connaissent : pour eux, le
   modèle est bien un détail.

2. À quoi sert `FakeGenerator`, qui ne lit même pas les passages ?

   Réponse : à faire tourner `Assistant` sans service ni modèle, par exemple dans un test. Il
   implémente le même port que `HttpGenerator`, et le domaine ne voit pas la différence.

3. Ici, le port est déclaré dans le domaine. Où le placeriez-vous si les règles métier et le cas
   d'usage qui appelle le modèle étaient dans deux couches distinctes ?

   Réponse : là où vit le besoin. Dans ce programme, c'est `Assistant` qui a besoin d'un générateur,
   et `Assistant` est dans le domaine : le port est à côté de celui qui s'en sert. Quand les cas
   d'usage ont leur propre couche, ce sont eux qui appellent le modèle, et le port les suit dans la
   couche application. Le domaine garde alors ses règles, sans aucune interface tournée vers
   l'extérieur. Les deux placements respectent la règle de dépendance, puisque dans les deux cas le
   domaine ne dépend jamais de l'infrastructure.

## Pour aller plus loin

Cette partie est facultative : faites-la si le temps le permet.

### À vous : un troisième adaptateur

Écrivez un troisième adaptateur du port `ITextGenerator`, qui fonctionne sans réseau. Il recopie le
passage qui partage le plus de mots avec la question, et le cite par son numéro entre crochets. Pour
savoir où trouver les passages et la question, lisez la méthode `Answer` d'`Assistant` : c'est elle
qui écrit le prompt.

1. Écrivez la classe dans `Infrastructure/Providers.cs`.
2. Dans `Program.cs`, faites-la choisir par un nouvel alias, par exemple `copie`.
3. Posez-lui deux questions qui ne visent pas le même passage.

```bash
dotnet run --project exemples/s1.3-transfert-naif -- generate copie
dotnet run --project exemples/s1.3-transfert-naif -- generate copie "Quel est le plafond du repas ?"
```

C'est réussi quand `dotnet build exemples/s1.3-transfert-naif -warnaserror` passe sans erreur et que
chaque question reçoit son passage. Vous n'avez eu à toucher ni à `Ports.cs` ni à `Assistant.cs`.

### Une solution possible

Le générateur découpe le prompt tel qu'`Assistant` l'écrit : un bloc par passage, qui commence par son
numéro entre crochets, et la question dans le dernier bloc. Il garde le passage qui partage le plus de
mots avec la question, sans compter les mots de trois lettres ou moins comme « de » ou « par ». Dans
`Infrastructure/Providers.cs`, ajoutez `using System.Text.RegularExpressions;` en tête du fichier, puis
la classe :

```csharp
/// <summary>Un générateur sans réseau : il recopie le passage le plus proche de la question, et le cite.</summary>
public sealed class CopyGenerator : ITextGenerator
{
    public string Generate(string instructions, string prompt)
    {
        var blocks = prompt.Split("\n\n");
        var question = Words(blocks[^1]);
        var best = blocks
            .Where(block => block.StartsWith('['))
            .Select(block => block.Split('\n'))
            .MaxBy(lines => Words(lines[1]).Count(question.Contains));
        return best is null ? "Aucun passage ne permet de répondre." : $"{best[1]} {best[0].Split(' ')[0]}";
    }

    private static HashSet<string> Words(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"\W+").Where(word => word.Length > 3).ToHashSet();
}
```

Dans `Program.cs`, le choix de l'adaptateur devient un `switch` :

```csharp
ITextGenerator generator = alias switch
{
    "fake" => new FakeGenerator(),
    "copie" => new CopyGenerator(),
    _ => new HttpGenerator(baseUrl, alias),
};
```

La première question reçoit la phrase sur le télétravail, citée [1], et la seconde celle sur le repas,
citée [2]. Seuls `Providers.cs` et `Program.cs` ont changé : le domaine n'a rien su du nouveau
fournisseur.
