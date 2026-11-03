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

## Corrigé

La correction est le tag `corrige/s2.3`. Sur `src/`, elle ne touche que deux fichiers :

```bash
git diff origin/etape/s2.3 corrige/s2.3 -- src
```

Dans `HttpAiClient.cs`, `HttpGenerator` suit `HttpEmbedder` pas à pas. Voici sa méthode `Generate` :

```csharp
public string Generate(GenerationRequest request)
{
    var payload = _post(_url, new JsonObject
    {
        ["model"] = _model,
        ["system"] = request.System,
        ["prompt"] = request.Prompt,
        ["temperature"] = request.Temperature,
        ["max_tokens"] = request.MaxTokens,
    });
    HttpTransport.Require(payload, "model", "text");
    // « model » est vérifié comme « text ».
    _ = HttpTransport.Text(payload, "model");
    return HttpTransport.Text(payload, "text");
}
```

Le constructeur calcule l'adresse `<baseUrl>/v1/generate`, garde l'alias et prend le transport donné, ou
en crée un, avec un délai par défaut plus long que celui d'`HttpEmbedder`. Dans `Composition.cs`, la
ligne du bouchon devient la construction d'`HttpGenerator`, et `MissingGenerator` disparaît.

Les tests restaurés vérifient les deux côtés du contrat.
`Generation_request_sends_the_prompt_built_by_the_application` contrôle le corps envoyé, champ par champ.
`A_generation_response_of_another_shape_is_a_non_transient_error_not_a_crash` passe en revue les
réponses mal formées et les messages attendus. `docs/contrat-http.md` gagne, avec la correction, une
section qui décrit cette lecture stricte.

Vous avez maintenant la version du fil rouge où « le modèle est un détail » tient : on change de
générateur par une option, et le cœur ne bouge pas. La suite du cours la met sous tension.

Voici les réponses aux questions.

Une réponse sans `model` n'est pas une réponse du contrat. L'accepter quand même cacherait un service
qui ne parle pas ce contrat, ou une page d'erreur renvoyée par un intermédiaire.

Une réponse mal formée le restera si l'on repose la même question au même service : réessayer ne
changerait rien, sinon le temps perdu. Les erreurs passagères sont d'une autre nature, un service
injoignable, un délai dépassé, une erreur 5xx. C'est `HttpTransport.Create` qui les reconnaît.

Vous avez modifié `HttpAiClient.cs`, dans l'infrastructure, et `Composition.cs`, la racine de
composition. Le domaine et l'application n'ont pas bougé : `AskQuestion` ne connaît que le port
`IGenerator`, et ce qu'il y a derrière ne le regarde pas. C'est l'argument de l'approche, et la commande
suivante ne montre aucune différence :

```bash
git diff origin/etape/s2.3 corrige/s2.3 -- src/Assistant.Domain src/Assistant.Application
```

À l'étape 6, la variante répond moins bien qu'`extractive` sur le télétravail et l'urgence, et mieux sur
le P1. La question des congés reçoit la même réponse des deux générateurs, et celle de la recette le même
refus. Pour la recette, `-v` n'affiche aucune sortie brute : aucun passage n'atteint le seuil, et le
modèle n'est pas appelé. L'urgence est le miroir du P1, sur le même passage : cette fois, c'est la
variante qui répond par le titre de la fiche. Elle ne « comprend » donc pas mieux le P1. Ces générateurs
sont des doubles de test, pas de vrais modèles : pour certaines questions, la variante prend la deuxième
phrase la mieux classée au lieu de la première. Sur le P1, la première était le titre de la fiche. Sur
l'urgence, c'était la bonne phrase.

On ne peut pas classer deux générateurs sur cinq questions, d'autant qu'elles ont été choisies pour
montrer chaque cas. Posez-leur d'autres questions sur le corpus : le plus souvent, quand leurs réponses
diffèrent, c'est la variante qui répond moins bien.

Si vous posez la question du P1 pour un incident P2 ou P3, sous la même forme, la variante donne le délai
du P1, avec une citation valide. Sous une autre forme, comme « Quel est le délai de prise en charge d'un
incident P2 ? », c'est `extractive` qui donne le délai du P1, et la variante celui du P2. Une citation dit
d'où vient une phrase, pas que cette phrase répond à la question. Le cœur garantit qu'une réponse publiée
cite ses sources parmi les passages fournis au modèle, ceux qui atteignent le seuil, et que les droits
sont filtrés avant le prompt. La justesse de la réponse, le prompt la demande, mais rien ne la vérifie.

Pour votre adaptateur, la trace montre les mêmes passages et les mêmes scores avec les deux générateurs.
Le prompt est donc le même, puisque c'est le cœur qui le construit. Dans votre code, c'est le même
`HttpGenerator` dans les deux cas, et seul l'alias change. Les tests restaurés vérifient qu'il envoie
l'alias reçu et qu'il rend le texte de la réponse. La sortie brute, à elle seule, ne le disculpe pas :
c'est le texte que votre adaptateur a rendu.

Enfin, l'index contient des vecteurs calculés par un modèle d'embeddings. Une question doit être
vectorisée par le même modèle pour être comparée à eux : avec un autre, les vecteurs ne vivent plus dans
le même espace. Le générateur, lui, ne reçoit que du texte, le prompt construit par l'application. Il ne
voit jamais les vecteurs, et n'importe quel générateur peut donc travailler sur le même index.
