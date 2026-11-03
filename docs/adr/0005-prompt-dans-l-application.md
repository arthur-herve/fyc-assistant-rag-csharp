# ADR 0005 — Le prompt est construit et versionné dans l'application

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées.

## Contexte

Le prompt demande au modèle de répondre en français, en trois phrases au plus, et de citer ses
sources entre crochets. Il encode donc des règles métier, mais dans un texte faillible, réglé pour un
modèle donné. Relève-t-il de la configuration, de l'infrastructure ou du métier ? C'est la question
que pose la séquence S3.3.

## Décision

Le prompt est un fichier, `prompts/answer.json`, chargé par le port `IPromptRepository`. C'est
l'application qui le construit, en numérotant les passages et en y insérant la question. Elle
l'envoie ensuite tel quel au service IA, qui ne fait que le transmettre. Sa version déclarée et une
empreinte de son contenu sont inscrites dans chaque `AnswerTrace`.

## Conséquences

- Les règles métier ne dépendent pas du prompt. Les citations et la forme de la sortie sont vérifiées
  après coup, de façon déterministe, par `Citations` et `OutputRules`
  (`src/Assistant.Domain/Rules.cs`). Le prompt n'est qu'une tentative de persuasion : la garantie est
  ailleurs.
- Modifier une seule virgule change l'empreinte. Une dérive des réponses peut ainsi être rattachée au
  contenu exact du prompt qui l'a produite.
- Le prompt est réglé pour `llama3.2:3b`. Un autre générateur peut demander une autre formulation.
  En S3.3, on mesure ce que change une variante, la v2 (`prompts/answer-v2.json`).

## Écarté

Placer le prompt dans le service IA, au motif que « c'est un détail du modèle », aurait obligé le
service à connaître la numérotation des passages et l'obligation de citer, c'est-à-dire le métier.
