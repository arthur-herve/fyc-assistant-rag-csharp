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

## Corrigé

La correction est le tag `corrige/s4.1`. Pour voir ce qu'elle change dans le code et la configuration :

```bash
git diff origin/etape/s4.1 corrige/s4.1 -- src config
```

| Fichier | Ce qui change |
|---|---|
| `src/Assistant.Application/Errors.cs` | `ModelOutputRejectedException : AssistantApplicationException`, avec `Model`, `Text` et `Problems`. Son message énumère les problèmes. |
| `src/Assistant.Application/Guards.cs` | `OutputValidatingGenerator.Generate` appelle `inner.Generate`, passe le texte à `OutputRules.Check` et lève `ModelOutputRejectedException(model, text, problems)` si la sortie est refusée. |
| `src/Assistant.Application/AskQuestion.cs` | Dans la boucle des tentatives, `catch (ModelOutputRejectedException rejected)` garde le modèle, ajoute la sortie à la trace, précédée de `<rejetée : …>`, puis passe à la tentative suivante avec `continue`. |
| `src/Assistant.Cli/AppConfig.cs` et `config/*.json` | Les clés `validate_output`, vraie par défaut, et `max_output_chars`, 1500 par défaut. |
| `src/Assistant.Cli/Composition.cs` | `Decorate` enveloppe le générateur dans `OutputValidatingGenerator` quand `validate_output` est vrai. Côté génération, de l'intérieur vers l'extérieur, viennent les tentatives, la validation, puis le journal. Côté embeddings, rien ne change : les tentatives, le journal, puis le cache. `Build` ajoute les deux clés à la configuration des instantanés. |
| `src/Assistant.Cli/Experiments.cs` | Deux sections « Lecture » tiennent compte de la validation. Celle de `changement-generateur` compte les rejets du garde-fou de forme parmi les réponses non sourcées, et celle de `prompt-v2` compte la forme validée parmi ce que le domaine garantit. |

Les tests restaurés couvrent chaque pièce :

- `DecoratorTests.Validation_rejects_leaked_reasoning` vérifie le décorateur seul ;
- `AskQuestionTests.A_rejected_output_counts_as_a_failed_attempt`, qui remplace le test de
  caractérisation, vérifie le `catch` ;
- `CompositionRootTests.A_rejected_output_is_refused_and_logged_whatever_else_is_stacked` vérifie la
  place du décorateur dans la pile ;
- `ConfigTests.The_snapshot_configuration_includes_the_settings_that_change_statuses` vérifie que les
  instantanés voient les deux clés.

`ArchitectureTests.Application_depends_only_on_the_domain` reste vert : le décorateur n'a besoin que du
domaine.

Voici les réponses aux questions.

La règle est dans le domaine parce qu'elle dit ce qu'est une réponse acceptable pour le métier : la
langue, la longueur, l'absence de raisonnement. Elle survivrait à un changement complet de pile
technique et, comme toute règle du domaine, elle est pure et se teste en quelques microsecondes. Le
décorateur, lui, est un mécanisme : il sait qu'il existe un port `IGenerator` et une boucle de
tentatives. On retrouve la même séparation qu'entre `Citations.Check`, dans le domaine, et
`AskQuestion`, dans l'application.

La vérification ne va pas dans le service IA, parce que celui-ci neutralise ce qui appartient au
modèle : les balises `<think>` sont une convention de certains modèles. Le service ne sait pas ce qu'est
une réponse acceptable pour cette application précise, et une autre application pourrait vouloir des
réponses longues, ou en anglais. Mettre la règle dans le service ferait fuir le métier vers
l'infrastructure, soit l'inverse de ce qu'on cherche. Ici, en plus, le service est écrit dans un autre
langage : la règle y serait en Python, loin des tests du domaine.

Pour l'ordre des décorateurs, placer la validation sous le décorateur de tentatives du dépôt ne change
rien. Celui-ci ne relance qu'une `AiServiceException` passagère, c'est-à-dire une panne momentanée du
service IA, et une sortie rejetée le traverse sans nouvel appel au modèle. C'est ce filtre qui protège,
pas l'ordre. Un décorateur de tentatives qui attraperait n'importe quelle erreur, en revanche,
traiterait une sortie rejetée comme une panne : il relancerait le modèle avec le même prompt, en
consommant les tentatives prévues pour le réseau. Mettre la validation au-dessus rend la pile sûre quel
que soit ce filtre, et chaque décorateur reste à sa place. Les tentatives s'occupent du réseau, au plus
près de lui. La validation applique une règle métier, au plus près du cas d'usage. Seul le journal des
générations reste au-dessus d'elle, pour voir aussi les rejets.

Ce qui isole la technologie, c'est le service IA, qui offre le même contrat quel que soit le moteur, ce
contrat lu strictement par les adaptateurs, puis le cache, le journal et les tentatives réseau. Ce qui
isole l'incertitude, c'est autre chose : les droits filtrés avant le prompt, les citations vérifiées par
le domaine, les tentatives du cas d'usage, et désormais la validation de la forme. La première liste rend
le modèle remplaçable. La seconde empêche une sortie douteuse d'arriver jusqu'à l'utilisateur.

L'heuristique a ses limites. Elle peut rejeter à tort une réponse française qui cite un long intitulé
anglais (« the General Data Protection Regulation… »). Elle peut aussi laisser passer un raisonnement
déversé en français, s'il ne contient aucun des marqueurs recherchés. C'est la limite annoncée en S3.1 :
un test déterministe attrape une classe d'erreurs, et l'évaluation statistique, avec les instantanés,
mesure le reste.

Enfin, côté compilateur : un décorateur qui implémente `IGenerator` peut servir partout où le port est
attendu, et il ne peut ni oublier une méthode ni se tromper de signature. L'erreur apparaîtrait à la
compilation, et non à l'exécution devant un utilisateur. Le prix à payer : ajouter une méthode au port
oblige à modifier tous les décorateurs. C'est le signe qu'un port doit rester petit.
