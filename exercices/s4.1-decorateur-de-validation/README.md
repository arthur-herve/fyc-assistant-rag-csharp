# Exercice S4.1 — Ajouter un décorateur de validation de la sortie du modèle (C#)

Durée indicative : 45 minutes. L'exercice se fait dans le fil rouge, sur la branche `etape/s4.1`. Vous
écrivez le décorateur, son exception et ce qui l'intègre à l'application. Les tests attendus viennent de
la correction.

## Situation

Sur `etape/s4.1`, des décorateurs sont déjà en place, dans `src/Assistant.Infrastructure/Decorators.cs`.
`CachedEmbedder` garde en cache les embeddings des questions. `LoggingEmbedder` et `LoggingGenerator`
journalisent le modèle et la durée de chaque appel. `RetryingEmbedder` et `RetryingGenerator` relancent
un appel quand le service IA a une panne passagère. `Composition.Decorate`, dans
`src/Assistant.Cli/Composition.cs`, les empile selon la section `decorators` de la configuration. Lisez
ces décorateurs : ce sont les exemples commentés de la séquence.

Le domaine fournit aussi la règle dont vous aurez besoin. Dans `src/Assistant.Domain/Rules.cs`,
`OutputRules.Check(text, maxChars)` rend un `OutputCheck`. Elle signale une réponse vide, trop longue,
écrite dans une autre langue que le français, ou qui déverse un raisonnement. Ses tests, dans
`OutputRulesTests.cs`, passent. Mais personne ne l'appelle encore.

Certains modèles « réfléchissent » à voix haute avant de répondre, souvent en anglais. Hors ligne,
l'alias `extractive-bavard` du service IA imite ce défaut : il fait précéder sa réponse d'un
raisonnement en anglais. Voyez-le, le service IA lancé :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bavard -v
```

La réponse commence par « Okay, let me… » et se termine par une citation `[1]`. Elle reçoit le statut
`answered`, puisque `Citations.Check` ne regarde que les citations, et elle est montrée à l'utilisateur.
Le journal affiché par `-v` ne montre qu'une génération, sans erreur ni nouvelle tentative : ni les
tentatives ni le journal ne vérifient ce que le modèle a répondu. Le cache, lui, n'enveloppe que les
embeddings. Ces décorateurs isolent la technologie, pas l'incertitude.

Un test de caractérisation fige ce comportement : Leaked_reasoning_with_a_citation_is_shown_as_an_answer,
dans `AskQuestionTests.cs`. Il passe, parce qu'il affirme ce que fait le code aujourd'hui, pas ce qu'il
devrait faire. Lancez-le avec les autres tests du cas d'usage :

```bash
dotnet test --filter "FullyQualifiedName~AskQuestionTests"
```

Il disparaîtra à la restauration : la version d'`AskQuestionTests.cs` de la correction le remplace par un
test qui affirme le contraire.

## Ce qu'on vous demande

Il s'agit d'ajouter une vérification déterministe de la forme de la sortie, sans toucher à
l'adaptateur `HttpGenerator` ni au service IA.

1. Créez votre branche et récupérez les tests attendus, en nommant les fichiers :

   ```bash
   git switch -c mon-s4.1 origin/etape/s4.1
   git restore --source corrige/s4.1 -- tests/Assistant.Tests/DecoratorTests.cs tests/Assistant.Tests/AskQuestionTests.cs tests/Assistant.Tests/CompositionRootTests.cs tests/Assistant.Tests/ConfigTests.cs
   ```

2. Lancez `dotnet build`. Les erreurs de compilation forment votre liste de tâches : deux types manquent.

   - `ModelOutputRejectedException`, dans `src/Assistant.Application/Errors.cs`. C'est une
     `AssistantApplicationException` qui expose `Model`, `Text` et `Problems`. Son constructeur est
     `ModelOutputRejectedException(string model, string text, IReadOnlyList<string> problems)`.
   - `OutputValidatingGenerator`, dans un nouveau fichier, `src/Assistant.Application/Guards.cs`. Son
     constructeur est `OutputValidatingGenerator(IGenerator inner, int maxChars = 1500)`, et la classe
     implémente `IGenerator`. `Generate` appelle le générateur qu'elle enveloppe, passe le texte à
     `OutputRules.Check` avec la même limite, et lève `ModelOutputRejectedException` quand la forme n'est
     pas valide. Sinon, elle rend la génération telle quelle. Gardez la valeur par défaut
     `maxChars = 1500` : la plupart des tests ne donnent pas de limite.

   La liste peut aussi montrer une erreur CS0619 sur `Assert.Throws`. Elle vient de l'exception
   manquante, et disparaît dès que celle-ci est écrite.

3. Une fois ces deux types écrits, tout compile, mais deux tests restent rouges, ainsi que plusieurs
   cas de `ConfigTests.cs`. Le premier test, dans `AskQuestionTests.cs`, vérifie qu'une sortie rejetée
   compte comme une tentative ratée. Le second, dans `CompositionRootTests.cs`, empile tous les
   décorateurs et vérifie qu'une sortie rejetée est refusée et journalisée. Les cas de `ConfigTests.cs`
   lisent `validate_output` et `max_output_chars`, et l'un d'eux vérifie la configuration des
   instantanés. Faites-les passer dans cet ordre.

   - **Le `catch` du cas d'usage.** Dans la boucle des tentatives d'`AskQuestion`, attrapez
     `ModelOutputRejectedException` autour de l'appel au générateur. Notez le rejet dans les sorties
     brutes de la trace, sous la forme `<rejetée : problème ; autre problème> texte rejeté`. Gardez le
     modèle qui a répondu, puis passez à la tentative suivante. C'est la seule modification du cas
     d'usage.
   - **La place dans la composition.** Dans `Composition.Decorate`, et nulle part ailleurs, enveloppez le
     générateur dans `OutputValidatingGenerator` quand `validate_output` est vrai, ce qui est le cas par
     défaut, avec la limite `max_output_chars`, 1500 par défaut. Placez la validation sous le journal des
     générations, entre les tentatives et `LoggingGenerator` : le journal voit ainsi aussi les rejets.
   - **Les clés.** Dans `AppConfig`, la table des décorateurs connus, `DecoratorKinds`, gagne
     `validate_output`, un booléen, et `max_output_chars`, un entier d'au moins 1. Ajoutez-les à la
     section `decorators` des trois configurations, `config/app.json`, `config/app-ollama.json` et
     `config/app-ollama-complet.json`, avec les valeurs `true` et `1500`. Ajoutez-les enfin à la
     configuration des instantanés, dans `Composition.Build` : elles changent les statuts, et deux
     instantanés doivent pouvoir les distinguer.

4. Lancez tous les tests avec `dotnet test`, ceux de l'architecture compris : `Guards.cs` vit dans
   l'application, qui ne doit référencer que le domaine. Relancez ensuite la démonstration :

   ```bash
   dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bavard -v
   ```

   La réponse est maintenant refusée après deux tentatives : elle reçoit le statut `unsourced`, et
   l'utilisateur lit « Je n'ai pas pu produire de réponse correctement sourcée ». La trace montre les
   deux sorties précédées de « <rejetée : … ».

## Questions à se poser en chemin

Gardez-les en tête pendant que vous codez : le corrigé y répond.

- Pourquoi la règle « une réponse est en français et ne raisonne pas » est-elle dans le domaine, alors
  que le décorateur qui l'applique est dans l'application ?
- Pourquoi ne pas mettre cette vérification dans `ai_service/`, là où l'on retire déjà les balises
  `<think>` ?
- Que se passe-t-il si vous empilez la validation sous le décorateur de tentatives
  (`RetryingGenerator`) au lieu de la mettre au-dessus ?
- Les décorateurs fournis isolent la technologie. Qu'est-ce qui, dans le fil rouge, isole
  l'incertitude ?
- Cette vérification est une heuristique. Donnez une réponse française correcte qu'elle pourrait
  rejeter à tort, et une réponse fautive qu'elle laisserait passer.
- Bonus : `OutputValidatingGenerator : IGenerator`. En Python, un décorateur n'est qu'une classe qui
  ressemble au port. Que garantit le compilateur C# que Python ne garantit pas ?

## Pour aller plus loin

Écrivez un décorateur `CachedGenerator` et expliquez pourquoi il est plus dangereux qu'un
`CachedEmbedder`. Indice : pensez à la température, aux tentatives, et à la possibilité de dire quel
prompt a produit une réponse.
