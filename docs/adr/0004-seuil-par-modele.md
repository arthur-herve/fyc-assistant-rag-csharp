# ADR 0004 — Le seuil de pertinence est calibré par modèle d'embeddings et par corpus

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées.

## Contexte

Le refus « je n'ai trouvé aucun document » est une règle métier : si aucun passage ne dépasse un
seuil de similarité, on n'appelle pas le modèle. Or les scores ne se comparent pas d'un modèle à
l'autre. Les mesures le montrent : sur Solvéo, les questions hors corpus se situent à 0,64 avec
`nomic`, contre 0,41 avec `bge-m3`. Les scores ne se comparent pas davantage d'un corpus à
l'autre : le seuil calibré pour `bge-m3` passe de 0,46 sur un corpus de 15 morceaux à 0,65 sur un
corpus de 3 505.

## Décision

Le seuil est une table indexée par alias de modèle, `"retrieval": {"min_score": …}`, rangée dans le
fichier de configuration propre au corpus : `app.json` pour Solvéo, `app-ollama.json` pour
Service-Public. Le banc d'essai propose un seuil pour chaque modèle (`suggested_threshold`) à partir
d'un jeu de calibration, et un jeu de validation distinct sert à le vérifier.

## Conséquences

- Changer de modèle d'embeddings ou de corpus oblige à recalibrer un paramètre métier. C'est un
  exemple concret du principe CACE (S3.2).
- Le seuil fait partie de l'empreinte de configuration des instantanés. Quand il change, le
  changement apparaît à côté de la dérive qu'il provoque.
- Cette solution a une limite, que l'on assume : un seuil global par modèle reste grossier. Un seuil
  par question ou un reclassement des résultats serait plus fin, mais sort du sujet.

## Écarté

La première version n'avait qu'un seuil, le même pour tous les modèles. C'est précisément ce « détail »
qui a cassé au premier vrai modèle.
