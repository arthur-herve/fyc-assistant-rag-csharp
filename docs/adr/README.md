# Décisions d'architecture (ADR)

Une décision par fichier : contexte, décision, conséquences, ce qu'on a écarté. Elles se lisent
dans l'ordre. Une ADR n'est jamais modifiée après coup : une décision qui change donne une
nouvelle ADR qui remplace l'ancienne.

| N° | Décision | Séquences |
|---|---|---|
| [0001](0001-service-ia-separe-en-http.md) | Le service IA est un déployable séparé, joint en HTTP | S2.3, S4.1 |
| [0002](0002-index-cote-application.md) | L'index vectoriel vit côté application, pas côté service IA | S3.2, S4.2 |
| [0003](0003-index-incompatible-erreur.md) | Un index construit avec un autre modèle est une erreur, pas un avertissement | S2.3, S3.2 |
| [0004](0004-seuil-par-modele.md) | Le seuil de pertinence est calibré par modèle d'embeddings et par corpus | S3.2 |
| [0005](0005-prompt-dans-l-application.md) | Le prompt est construit et versionné dans l'application | S3.3 |
| [0006](0006-droits-filtres-avant-le-prompt.md) | Les droits d'accès sont filtrés avant le prompt, jamais confiés au modèle | S4.1 |
| [0007](0007-bibliotheque-standard.md) | Bibliothèque standard uniquement dans l'application | S1.1, S4.3 |
| [0008](0008-decorateurs-et-validation-de-sortie.md) | Les garde-fous sont des décorateurs de ports ; la forme de la sortie est une règle métier | S4.1 |
| [0009](0009-application-csharp-modeles-python.md) | Application en C#, modèles en Python : la frontière est le contrat, pas le langage | S1.3, S2.3, S4.2, S5.3 |

Les ADR 0001 à 0008 ont été prises pour la version Python du fil rouge et reprises telles quelles
ici (les références de fichiers sont celles de ce dépôt). L'ADR 0009 est propre à cette version.
