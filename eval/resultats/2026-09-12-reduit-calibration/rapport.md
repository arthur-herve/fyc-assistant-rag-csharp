# Banc d'essai — 2026-09-12 19:44

42 questions · 1 passage(s) par question · prompt `v1+085b70e7` · découpage `{"include_title":true,"max_chars":800,"overlap_chars":120}` · top_k=4 · température=0.2

## Recherche (sans génération)

| Embeddings | Modèle servi | Dim. | Morceaux | Indexation (s) | Hit@1 | Hit@k | Score top-1 médian (répondables) | (hors corpus) | Seuil configuré | Seuil suggéré | Séparation | Seuil utilisé |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bge-m3 | `ollama:bge-m3@790764642607` | 1024 | 740 | 19.90 | 0.97 | 0.97 | 0.74 | 0.54 | 0.65 | 0.64 | 1.00 | 0.65 |
| nomic | `ollama:nomic-embed-text@0a109f422b47` | 768 | 740 | 8.50 | 0.88 | 0.94 | 0.80 | 0.70 | 0.73 | 0.71 | 0.91 | 0.73 |

## Validation du seuil sur des questions jamais vues

| Embeddings | Seuil éprouvé | Questions | Hit@1 | Répondables retenues | Refus justes (hors corpus) |
|---|---|---|---|---|---|
| bge-m3 | 0.65 | 16 | 0.92 | 0.92 | 1.00 |
| nomic | 0.73 | 16 | 0.85 | 1.00 | 0.67 |

## Réponses (avec génération)

| Embeddings | Génération | Répond (répondables) | Bonne source | Mots-clés | Non sourcé | Refus justes (hors corpus) | Fuites d'accès | Stabilité | Tentatives | Latence médiane (ms) | p90 (ms) | Erreurs |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| bge-m3 | llama3-2-3b | 0.97 | 1.00 | 0.87 | 0.02 | 1.00 | 0 | — | 1.09 | 925 | 1763 | 0 |
| nomic | llama3-2-3b | 0.91 | 0.93 | 0.86 | 0.07 | 0.70 | 0 | — | 1.12 | 1124 | 1720 | 0 |

## Lecture

- **Hit@1 / Hit@k** : part des questions répondables dont un document attendu arrive en tête / figure dans les k passages retrouvés. Sur un petit corpus, Hit@k est vite saturé : regarder Hit@1.
- **Seuil suggéré** : sépare au mieux les questions répondables des questions hors corpus. Calibré sur ces mêmes questions, il est optimiste : la section « Validation » l'éprouve sur des questions jamais vues.
- **Bonne source** : parmi les réponses données, part qui cite un document attendu.
- **Mots-clés** : part des mots-clés attendus présents dans la réponse (indicateur grossier).
- **Non sourcé** : le modèle n'a pas cité correctement ses sources malgré les tentatives.
- **Fuites d'accès** : doit toujours valoir 0, le filtrage est fait avant le modèle.
- **Stabilité** : pour une même question, part des passages qui donnent le même statut et les mêmes documents cités (1 = parfaitement stable). Nécessite au moins 2 passages.
