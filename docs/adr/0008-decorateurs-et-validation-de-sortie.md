# ADR 0008 — Les garde-fous sont des décorateurs de ports ; la forme de la sortie est une règle métier

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées. Complétée par l'ADR 0010 le 21/09/2026.

## Contexte

Pendant le banc d'essai du 11/09/2026, qwen3 a déversé son raisonnement, en anglais, dans la
réponse. Ce texte contenait un « [1] », et la vérification des citations l'a donc accepté. Il
fallait un garde-fou qui juge le fond de la réponse, et pas seulement ses citations.

Plus largement, il fallait pouvoir ajouter un cache, un journal ou de nouvelles tentatives sans
toucher ni au cas d'usage ni à l'adaptateur HTTP (S4.1).

## Décision

- Un décorateur implémente le même port que l'objet qu'il enveloppe. Les décorateurs techniques
  (`CachedEmbedder`, `LoggingEmbedder` et `LoggingGenerator`, `RetryingEmbedder` et
  `RetryingGenerator`) vivent dans `src/Assistant.Infrastructure/Decorators.cs`. Celui qui porte une
  règle métier, `OutputValidatingGenerator`, vit dans `src/Assistant.Application/Guards.cs`. Il
  s'appuie sur `OutputRules` (`src/Assistant.Domain/Rules.cs`), qui écarte une réponse vide, trop
  longue, écrite dans une autre langue, ou qui déverse le raisonnement du modèle.
- L'empilement est décidé dans `src/Assistant.Cli/Composition.cs`, par `Decorate()`, à partir de la
  section `"decorators"` de la configuration. De l'intérieur vers l'extérieur, on trouve les
  nouvelles tentatives, le journal, le cache, puis la validation.
- Pour `AskQuestion`, une sortie rejetée est une tentative ratée. Elle est tracée sous la forme
  `<rejetée : …>`, et le cas d'usage réessaie. S'il n'obtient pas de réponse acceptable, il renvoie
  « non sourcé ».

## Conséquences

- Les cas d'usage ne voient que les ports. Le test d'architecture vérifie que seule la composition
  importe l'infrastructure.
- Les règles de forme sont des heuristiques : elles cherchent des marqueurs de raisonnement et
  regardent la part de mots anglais. Elles peuvent se tromper. Elles sont testées sur les cas réels
  rencontrés, et elles vivent dans le domaine parce qu'elles disent ce qu'est une réponse
  acceptable pour le métier.
- Un cache d'embeddings est un artefact lié au modèle (S4.2). Le nôtre est en mémoire. Un cache
  persistant devrait porter l'identifiant du modèle dans sa clé.

## Écarté

- Mettre la validation dans le service IA, au motif que « c'est le modèle qui bavarde ». Le service
  ne sait pas ce qu'est une réponse acceptable pour l'application.
- Mettre la validation directement dans `AskQuestion`. Elle y serait invisible et on ne pourrait
  pas la substituer. L'exercice « ajouter un décorateur » n'aurait plus de sens.
