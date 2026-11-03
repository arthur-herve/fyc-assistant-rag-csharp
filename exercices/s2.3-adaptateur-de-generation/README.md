# Exercice S2.3 — Écrire l'adaptateur de génération (C#)

Durée indicative : 45 minutes, après la vidéo. L'exercice se fait dans le fil rouge, sur la branche
`etape/s2.3`. Vous écrivez un seul adaptateur, `HttpGenerator`, vous le branchez, puis vous changez de
générateur sans toucher au cœur.

## Situation

Sur `etape/s2.3`, l'application est complète, sauf son générateur. Le cœur est celui de S2.2. Les
adaptateurs sont là : le corpus en Markdown, le découpage, l'index en JSON, les prompts et `HttpEmbedder`,
qui calcule les embeddings auprès du service IA. La ligne de commande sait indexer le corpus et poser une
question.

Le générateur, lui, est un bouchon. Dans `src/Assistant.Cli/Composition.cs`, la classe `MissingGenerator`
lève une erreur de l'application dès qu'on lui demande un texte. `index` fonctionne donc, puisqu'il
n'a pas besoin du modèle. Une question sans passage pertinent est refusée normalement, pour la même
raison. Mais une question qui trouve un passage s'arrête sur un message d'erreur qui renvoie à cet
énoncé.

Essayez. Dans un premier terminal, lancez le service IA hors ligne et laissez-le tourner :

```bash
python -m ai_service
```

Dans un second :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
```

## Ce qu'on vous demande

1. Créez votre branche et récupérez les tests attendus, en nommant les fichiers :

   ```bash
   git switch -c mon-s2.3 origin/etape/s2.3
   git restore --source corrige/s2.3 -- tests/Assistant.Tests/HttpContractTests.cs tests/Assistant.Tests/AiServiceResponseTests.cs tests/Assistant.Tests/CliAskTests.cs
   ```

   Les deux premiers fichiers vérifient le contrat HTTP, désormais aussi du côté de la génération. Le
   troisième lance `ask` pour de bon, contre un faux service IA. La compilation échoue sur
   `HttpGenerator`, qui n'existe pas encore : c'est votre tâche.

2. Écrivez `HttpGenerator` dans `src/Assistant.Infrastructure/HttpAiClient.cs`, à la suite
   d'`HttpEmbedder`, qui vous sert de modèle. La classe implémente `IGenerator`, et son constructeur a
   cette signature :

   ```csharp
   public HttpGenerator(string baseUrl, string model, TimeSpan? timeout = null, JsonTransport? transport = null)
   ```

   - `Generate(GenerationRequest request)` envoie la requête à la route `/v1/generate` du service, décrite
     dans `docs/contrat-http.md`. Le corps porte l'alias du modèle (`model`), puis `system`, `prompt`,
     `temperature` et `max_tokens`, pris dans la requête. Le contrat prévoit aussi un champ `seed`,
     facultatif : ne le mettez pas.
   - Quand `transport` est donné, il remplace le transport HTTP : c'est ainsi que les tests répondent sans
     réseau. Sinon, créez-en un avec `HttpTransport.Create`, comme `HttpEmbedder`. Une génération est plus
     lente qu'un calcul d'embeddings : prévoyez un délai par défaut plus long.
   - La réponse est lue strictement. Elle doit contenir `model` et `text`, et les deux doivent être des
     textes. Vérifiez-le avec `HttpTransport.Require(payload, "model", "text")`, les clés dans cet ordre,
     puis lisez chaque champ avec `HttpTransport.Text`. Les messages exacts que vérifient les tests
     viennent de ces deux fonctions, par exemple « Réponse du service IA incomplète, champs manquants :
     [model, text] » ou « Réponse du service IA incohérente : « text » doit être un texte ». N'écrivez
     pas vos propres messages.
   - Le port rend un `string` : `Generate` rend le texte. L'identifiant du modèle est lu et vérifié, puis
     laissé de côté.

   Vous avez déjà écrit un adaptateur de ce genre dans l'exemple S1.3. Celui du fil rouge lit la réponse
   plus strictement : une réponse qui n'a pas la forme du contrat est une erreur, jamais un plantage.

3. Branchez-le dans `src/Assistant.Cli/Composition.cs`, à la place du bouchon. Construisez-le comme
   `HttpEmbedder` : avec l'adresse du service IA, le modèle de génération choisi, celui de la
   configuration ou celui de `--generation-model`, et le délai configuré. Supprimez ensuite la classe
   `MissingGenerator`. Ni le domaine ni l'application ne changent.

4. Lancez `dotnet test`. Tous les tests doivent passer, y compris ceux de l'architecture.

5. Faites tourner l'application complète hors ligne, le service IA toujours lancé :

   ```bash
   dotnet run --project src/Assistant.Cli -- index
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la grille des salaires ?" --user alice -v
   dotnet run --project src/Assistant.Cli -- ask "Quelle est la grille des salaires ?" --user bruno -v
   ```

   alice n'est que dans le groupe `tous`, bruno est aussi aux RH. alice reçoit un refus : aucun document
   accessible ne répond. Avec `-v`, la trace montre que la grille ne figure pas parmi les passages
   retrouvés : elle a été écartée avant même la construction du prompt. bruno, lui, reçoit une réponse
   qui cite la grille des rémunérations.

   Posez ensuite aux deux la même question, formulée autrement : « Quelle est la fourchette de salaire
   d'un consultant senior ? », toujours avec `-v`. bruno obtient la fourchette. alice, elle, n'obtient
   pas un refus, mais une phrase sans rapport tirée de la fiche des congés, « jusqu'au 31 mai de l'année
   suivante. [1] », avec un score de 0,154 pour un seuil de 0,15. La règle des droits tient : la grille
   n'est jamais entrée dans le prompt d'alice.

6. Changez de générateur sur le même index, sans toucher au cœur ni réindexer. Comparez d'abord les deux
   générateurs sur la question du télétravail, puis sur celle d'un incident P1, chaque fois avec `-v` :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" -v
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-variante -v
   dotnet run --project src/Assistant.Cli -- ask "En combien de temps un incident P1 est-il pris en charge ?" -v
   dotnet run --project src/Assistant.Cli -- ask "En combien de temps un incident P1 est-il pris en charge ?" --generation-model extractive-variante -v
   ```

   `extractive-variante` est un autre générateur hors ligne. Il choisit sa phrase autrement
   qu'`extractive`, mais il cite toujours sa source et redonne la même réponse à chaque fois.

   D'un générateur à l'autre, les passages retrouvés, leurs scores et le seuil n'ont pas bougé : seules
   la réponse et la sortie brute ont changé. Sur le télétravail, la variante cite la bonne fiche, mais sa
   phrase ne donne plus le nombre de jours : elle répond moins bien. Sur le P1, elle répond mieux : elle
   donne le délai de prise en charge, là où `extractive` ne donne que le titre de la fiche. Si la
   variante vous rend exactement la réponse d'`extractive` sur le télétravail, vérifiez que votre
   branchement transmet bien l'alias choisi par `--generation-model`.

   Changer de générateur change les réponses, dans un sens ou dans l'autre, pas le cœur. Votre
   `HttpGenerator` n'est pour rien dans ces écarts : cherchez ce qui le montre dans la trace et dans
   votre code.

   Avant de lancer quoi que ce soit d'autre, prédisez par écrit ce que donnera la variante sur trois
   questions que vous n'avez pas encore posées. Pour chacune, notez si la variante répondra comme
   `extractive`, mieux ou moins bien, et pourquoi. Voici les trois commandes :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de congés payés ai-je par an ?" --generation-model extractive-variante -v
   dotnet run --project src/Assistant.Cli -- ask "Comment contacter le support en urgence ?" --generation-model extractive-variante -v
   dotnet run --project src/Assistant.Cli -- ask "Donne-moi une recette de tarte aux pommes." --generation-model extractive-variante -v
   ```

   Lancez ensuite ces trois commandes, puis chacune sans `--generation-model` pour avoir la réponse
   d'`extractive`. Complétez ce tableau, puis comparez vos prédictions à ce que vous constatez. Pour le
   télétravail et le P1, reprenez ce que vous aviez prévu devant la vidéo.

   | Question | `extractive` | `extractive-variante` | Votre prédiction | Constat |
   |---|---|---|---|---|
   | Télétravail | « …jusqu'à deux jours par semaine. [1] » | « Les jours de télétravail sont fixés d'un commun accord avec le manager… [1] » | | moins bien |
   | Incident P1 | « Support informatique et priorités des incidents [1] » | « Prise en charge sous 30 minutes, 24 h sur 24. [1] » | | mieux |
   | Congés payés | | | | |
   | Support en urgence | | | | |
   | Recette | | | | |

   Avec Ollama, essayez de même `--generation-model llama3-2-3b` sur la question du P1.

   `config/app-ollama.json` est une autre affaire. Cette configuration change aussi le corpus et le
   modèle d'embeddings, et il faut indexer à part avec elle. Elle ne montre donc pas une substitution du
   seul générateur.

## Questions à se poser en chemin

Gardez-les en tête pendant que vous codez : le corrigé y répond.

- `HttpGenerator` ne rend que le texte. Pourquoi vérifier quand même le champ `model` ?
- Les erreurs levées par `Require` et `Text` sont marquées comme non passagères. Pourquoi ?
- Quels fichiers avez-vous modifiés ? Qu'est-ce que cette liste dit de l'architecture ?
- Lequel des deux générateurs est le meilleur ? Sur quoi vous fondez-vous ?
- Qu'est-ce qui, dans la trace et dans votre code, montre que votre adaptateur n'est pour rien dans les
  réponses qui changent ?
- Pourquoi peut-on changer de générateur sur le même index, alors qu'il faudrait réindexer pour changer
  de modèle d'embeddings ?
