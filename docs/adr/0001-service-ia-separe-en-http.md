# ADR 0001 — Le service IA est un déployable séparé, joint en HTTP

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées.

## Contexte

Le fil rouge doit montrer jusqu'où l'on peut traiter un modèle d'IA comme un « détail
d'infrastructure ». En entreprise, les modèles tournent sur des machines de calcul équipées de GPU,
que l'équipe IA administre, et l'application tourne sur des serveurs applicatifs ordinaires. Une
bibliothèque importée dans l'application aurait masqué cette réalité.

## Décision

Le projet se compose de deux programmes. L'application, écrite en C#, se trouve dans
`src/Assistant.*`. Le service IA, qui calcule les embeddings et génère le texte, se trouve dans
`ai_service/`. Ils ne partagent aucun code et ne communiquent que par le contrat décrit dans
`docs/contrat-http.md`. Dans ce dépôt, ils ne pourraient d'ailleurs pas en partager, puisqu'ils ne
sont pas écrits dans le même langage (ADR 0009). Le service IA ne sait rien des documents, des
droits d'accès ni des citations.

## Conséquences

- Les particularités des modèles restent enfermées dans `ai_service/` : les préfixes `query:` et
  `passage:`, les balises `<think>`, le budget de réflexion. L'application se contente de déclarer
  une intention, par exemple vectoriser une question plutôt qu'un document.
- Le service renvoie toujours l'identifiant concret du modèle qui a servi, c'est-à-dire son nom et
  l'empreinte de ses poids. C'est cet identifiant qui permet à l'application de détecter un index
  incompatible (ADR 0003).
- Ce choix a des coûts, que l'on assume : un second processus à lancer, une latence réseau, des
  pannes du service à gérer avec le décorateur de nouvelles tentatives (ADR 0008), et un contrat à
  versionner, d'où le `/v1/` dans les routes.
- Une nuance mérite d'être enseignée : la frontière réseau isole la technologie, pas le couplage.
  L'index, côté application, dépend toujours du modèle servi côté IA.

## Écarté

Importer un client Ollama ou une bibliothèque d'embeddings dans l'application aurait simplifié
l'installation. Mais la dépendance au modèle serait alors devenue invisible dans l'architecture.
