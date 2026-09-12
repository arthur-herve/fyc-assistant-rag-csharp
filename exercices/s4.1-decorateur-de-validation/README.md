# Exercice S4.1 — Ajouter un décorateur de validation de la sortie du modèle (C#)

Durée indicative : 45 minutes · Exercice non QCM, code à écrire · Corrigé en fin de document.

## Situation

Vous avez branché un nouveau modèle de génération, `qwen3:4b`. Sur certaines questions, il
« réfléchit » à voix haute avant de répondre, en anglais :

> Okay, let's see. The user is asking how many days of remote work per week. First, I need to
> check the provided passages. Passage [1] says two days per week…

Le cas d'usage `AskQuestion` a accepté ce texte comme une réponse : il contient bien une citation
`[1]`, et la vérification des citations (`Citations.Check`) ne regarde que cela. Le banc d'essai
affichait 100 % de bonnes sources. Le texte a été montré à l'utilisateur.

## Ce qu'on vous demande

Ajouter une vérification **déterministe** de la forme de la sortie, **sans modifier**
`AskQuestion` (sauf le point 3), ni l'adaptateur `HttpGenerator`, ni le service IA.

1. Écrire la règle dans le domaine : `src/Assistant.Domain/Rules.cs`, classe statique
   `OutputRules` avec `Check(string text, int maxChars) → OutputCheck`, qui signale une réponse
   vide, trop longue, dans une autre langue que le français, ou qui contient un raisonnement déversé.
2. Écrire un **décorateur** du port `IGenerator` : `src/Assistant.Application/Guards.cs`, classe
   `OutputValidatingGenerator(IGenerator inner, int maxChars) : IGenerator`. Il appelle le
   générateur enveloppé, vérifie sa sortie et lève `ModelOutputRejectedException` (à ajouter dans
   `Errors.cs`) quand la forme est invalide.
3. Faire en sorte qu'une sortie rejetée compte comme une tentative ratée dans `AskQuestion`
   (c'est la seule modification autorisée du cas d'usage : attraper l'exception, tracer le rejet,
   passer à la tentative suivante).
4. Brancher le décorateur **uniquement** dans `src/Assistant.Cli/Composition.cs` (`Decorate`),
   activé par une clé de configuration `"decorators": { "validate_output": true }`.
5. Vérifier que `ArchitectureTests` passe toujours : `Assistant.Application` ne référence que
   `Assistant.Domain`, et aucun adaptateur n'en construit un autre hors de la racine de composition.

Les tests à faire passer : `OutputRulesTests` (`DomainTests.cs`), `DecoratorTests`
(`InfrastructureTests.cs`) et `A_rejected_output_counts_as_a_failed_attempt` (`UseCaseTests.cs`).
Point de départ : ce dépôt, en supprimant `OutputRules`, `OutputCheck`, `Guards.cs`,
`ModelOutputRejectedException` et le `catch` de `AskQuestion` (les tests, eux, restent — et ne
compilent plus : c'est votre liste de tâches). Une branche de départ prête à l'emploi sera
étiquetée avec le découpage du cours.

## Questions à se poser en chemin (elles seront reprises en correction)

- Pourquoi la règle « une réponse est en français et ne raisonne pas » est-elle dans le
  **domaine**, alors que le décorateur qui l'applique est dans l'**application** ?
- Pourquoi ne pas mettre cette vérification dans `ai_service/`, là où l'on neutralise déjà les
  balises `<think>` ?
- Que se passe-t-il si vous empilez le décorateur de validation **sous** le décorateur de
  nouvelles tentatives (`RetryingGenerator`) au lieu de le mettre au-dessus ?
- Cette vérification est une heuristique. Donnez une réponse française correcte qu'elle
  pourrait rejeter à tort, et une réponse fautive qu'elle laisserait passer.
- Bonus C# : `OutputValidatingGenerator : IGenerator`. Que garantit le compilateur qu'un
  décorateur Python (une classe qui *ressemble* au port) ne garantit pas ?

## Corrigé

Le corrigé est le code du dépôt :

| Étape | Fichier | Ce qu'il fait |
|---|---|---|
| 1 | `src/Assistant.Domain/Rules.cs` | `OutputRules.Check` : vide ; longueur ; marqueurs de raisonnement (`<think>`, « okay, let », « the user is asking »…, bornés par des mots) ; ratio de mots-outils anglais contre français sur les textes d'au moins 5 mots |
| 2 | `src/Assistant.Application/Guards.cs` | `OutputValidatingGenerator.Generate` : appelle `inner.Generate`, puis `OutputRules.Check` ; lève `ModelOutputRejectedException(model, text, problems)` |
| 2 | `src/Assistant.Application/Errors.cs` | `ModelOutputRejectedException : AssistantApplicationException` avec `Model`, `Text`, `Problems` |
| 3 | `src/Assistant.Application/AskQuestion.cs` | dans la boucle des tentatives : `catch (ModelOutputRejectedException rejected)` → `rawOutputs.Add("<rejetée : …> " + texte)`, `continue` |
| 4 | `src/Assistant.Cli/Composition.cs` | `Decorate()` : `if (config.Decorator("validate_output", true)) generator = new OutputValidatingGenerator(generator, maxChars)` ; ordre : tentatives → journal → cache → validation → journal des générations |
| 5 | `tests/Assistant.Tests/InfrastructureTests.cs` | `ArchitectureTests.Application_depends_only_on_the_domain`, `Adapters_are_assembled_only_by_the_composition_root` |

Réponses aux questions :

- **Domaine / application.** La règle dit ce qu'est une réponse acceptable pour le métier
  (langue, longueur, pas de raisonnement) : elle survivrait à un changement complet de pile
  technique, donc elle est dans le domaine, pure et testable en quelques millisecondes. Le
  décorateur, lui, est un mécanisme : il sait qu'il existe un port `IGenerator` et une boucle de
  tentatives. C'est la même séparation que `Citations.Check` (domaine) et `AskQuestion` (application).
- **Pas dans le service IA.** Le service IA neutralise ce qui appartient au *modèle* (les balises
  `<think>` sont une convention de qwen3). Il ne sait pas ce qu'est une réponse acceptable pour
  *cette* application : une autre application pourrait vouloir des réponses en anglais ou longues.
  Mettre la règle dans le service, c'est faire fuir le métier vers l'infrastructure — l'inverse
  de ce qu'on cherche. Et ici, le service est dans un autre langage : la règle y serait écrite
  en Python, loin des tests du domaine. Voir ADR 0008 et 0009.
- **Ordre des décorateurs.** Validation sous les tentatives réseau : une sortie rejetée serait
  prise pour une panne du service et relancée avec le même prompt, en consommant les tentatives
  réseau ; et une vraie panne réseau serait masquée par une erreur de validation. La validation
  est une règle métier : au plus près du cas d'usage, donc à l'extérieur de la pile.
- **Limites de l'heuristique.** Rejet à tort possible : une réponse française qui cite un intitulé
  anglais long (« the General Data Protection Regulation… ») ; acceptation à tort : un raisonnement
  déversé *en français* sans les marqueurs listés. C'est la limite annoncée en S3.1 : un test
  déterministe attrape une classe d'erreurs, l'évaluation statistique (banc d'essai, instantanés)
  mesure le reste.
- **Le compilateur.** Un décorateur qui implémente `IGenerator` est substituable partout où le
  port est attendu, et il ne peut pas oublier une méthode du port ni se tromper de signature :
  l'erreur serait à la compilation, pas à l'exécution devant un utilisateur. Le prix : ajouter une
  méthode au port oblige à toucher tous les décorateurs — c'est le signal qu'un port doit rester petit.

## Pour aller plus loin

Écrire un décorateur `CachedGenerator` et expliquer pourquoi il est plus dangereux qu'un
`CachedEmbedder` (indice : température, tentatives, traçabilité de la version du prompt).
