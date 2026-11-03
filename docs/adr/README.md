# Décisions d'architecture (ADR)

Chaque fichier de ce dossier consigne une décision d'architecture : le contexte qui l'a motivée, la
décision elle-même, ses conséquences et les options écartées. Les ADR se lisent dans l'ordre.

Une ADR n'est jamais modifiée après coup. Quand une décision change, on écrit une nouvelle ADR qui
remplace l'ancienne. Seules deux exceptions sont admises, toujours datées : la ligne de statut, qui
peut indiquer par exemple qu'une ADR est « complétée par l'ADR … », et un erratum en fin de document
lorsqu'un fait énoncé se révèle faux.

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
| [0010](0010-cache-et-identite-du-modele.md) | Le cache d'embeddings dérive de l'index ; l'identité d'un modèle Ollama est relue à chaque appel | S4.1, S4.2 |
| [0011](0011-fil-rouge-en-branches-d-etape.md) | Le fil rouge est livré en branches d'étape | toutes |

Les ADR 0001 à 0008 ont été prises pour la version du projet écrite entièrement en Python, qui a
précédé ce dépôt (voir l'ADR 0009). Elles ont été reprises ici sans changer de décision : seules les
références de fichiers et les mesures ont été adaptées, en particulier dans les ADR 0002 et 0007.
Les ADR 0009 et 0011 sont propres à ce dépôt. L'ADR 0010 a été prise en même temps dans la version
Python, où elle porte le numéro 0009, ce qui explique sa ligne de statut.
