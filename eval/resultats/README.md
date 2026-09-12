# Mesures de référence (version C#)

Toutes produites le 12/09/2026 par `assistant benchmark` et `assistant experience` de ce dépôt,
sur la machine de référence (RTX 3070 Laptop 8 Go, Ollama 0.34), service IA Python, corpus
**réduit** (`config/app-ollama.json`, 50 fiches, 740 morceaux), 42 questions de
`eval/questions-service-public.json` (+ 16 de validation). Les index reconstruits ne sont pas
versionnés (`.gitignore`), les rapports et les instantanés le sont.

| Dossier | Commande | Ce qu'il montre |
|---|---|---|
| `2026-09-12-reduit-calibration/` | `benchmark --embedding bge-m3 nomic --generation llama3-2-3b --validate-with … --runs 1` | seuils par modèle sur ce corpus ; validation sur 16 questions jamais vues |
| `exp-stabilite-2026-09-12-reduit/` | `experience stabilite --runs 3` | la ligne de base : dérive à configuration constante |
| `exp-cace-decoupage-2026-09-12-reduit/` | `experience cace-decoupage` (800/120 → 300/50) | CACE : le découpage change tout |
| `exp-changement-embeddings-2026-09-12-reduit/` | `experience changement-embeddings --other nomic` | second verdict : refus, réindexation, dérive |
| `exp-changement-generateur-2026-09-12-reduit/` | `experience changement-generateur --other qwen3-4b --limit 12` | premier verdict : même index, autres réponses, autre latence |
| `exp-prompt-v2-2026-09-12-reduit/` | `experience prompt-v2` | le prompt : configuration surveillée comme du métier |

## Lecture

**Calibration.** `bge-m3` : hit@1 0,97, seuil suggéré 0,64 pour 0,65 configuré — conservé ;
sur les 16 questions jamais vues, 92 % des répondables retenues et 100 % de refus justes.
`nomic` : hit@1 0,88, suggéré 0,71 pour 0,73 — conservé ; validation 100 % / 67 %. Les seuils
calibrés sur le corpus complet (322 fiches, version Python) tiennent donc sur le corpus réduit :
même modèle, même thème, même découpage — ce n'est pas une règle générale, c'est une mesure.
Avec `llama3.2:3b` : 97 % de réponses sourcées, 100 % de bonnes sources, 0 fuite d'accès sur
84 appels, latence médiane 0,9 s. Indexation : 20 s (`bge-m3`), 8,5 s (`nomic`).

**Stabilité (S3.1).** Rien ne change et pourtant **70 % des réponses bougent** d'un passage à
l'autre (26 reformulations sur 42, 2 à 3 changements de sources, 1 changement de statut :
`sp-dir-inspection`, non sourcé puis répondu). C'est la mesure que la tolérance d'un test
statistique doit absorber, et ce que `StatisticalTests.cs` fait avec 0,04 % de faux échec.

**CACE (S3.2).** Passer de 800 à 300 caractères par morceau : 740 → 2 188 morceaux, un autre
`index_id`, **76 % de dérive** (2 changements de statut, 3 de sources), pour un seuil laissé tel
quel. Les métriques agrégées bougent à peine (bonne source 1,00 → 0,97) : c'est la dérive
question par question qui compte, pas la moyenne.

**Changement d'embeddings (S2.3, S3.2).** Sans réindexer : `IndexModelMismatchException`,
message explicite (modèle et dimension de l'index contre modèle servi). Après réindexation avec
`nomic` : refus justes 1,00 → 0,70, bonne source 1,00 → 0,93, **83 % de dérive et 6 changements
de statut**. Le générateur n'a pas changé ; il ne répond qu'à partir de ce qu'on lui donne.

**Changement de générateur (S2.3, S4.1).** `llama3.2:3b` → `qwen3:4b` sur 12 questions : même
index (`de693eb424f2`, jamais reconstruit), mêmes taux de bonne source (0,92), **1,1 s → 23 s par
question** (×21, mode réflexion), 100 % de dérive textuelle, 0 rejet du garde-fou de forme (le
budget de réflexion de `config/ai_service.toml` fait son travail). « Une ligne de configuration »,
oui ; sans conséquence, non.

**Prompt v2 (S3.3).** Même index, même générateur : réponses de 357 à 41 caractères en moyenne,
74 % de dérive, 2 changements de statut, et la version tracée passe de `v1+085b70e7` à
`v2+1708960b` — identique à ce que trace la version Python pour les mêmes fichiers.

## Comparaison avec la version Python

Mêmes ordres de grandeur que `eval/resultats/` du dépôt Python sur le corpus complet (dérive de
base ≈ 70 %, prompt v2 qui divise la longueur par 7 à 8, qwen ×20 en latence, nomic moins bon que
bge-m3 sur les refus). Le temps est passé dans le service IA : le langage de l'application ne
change rien aux mesures, seul le corpus (50 contre 322 fiches) change les durées d'indexation.
