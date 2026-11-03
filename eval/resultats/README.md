# Mesures de référence

Toutes ces mesures ont été prises le 12/09/2026 avec les commandes `assistant benchmark` et
`assistant experience` de ce dépôt, sur la machine de référence (RTX 3070 Laptop 8 Go, Ollama 0.34)
et avec le service IA en Python. Elles portent sur le corpus réduit, celui de `config/app-ollama.json` :
50 fiches, découpées en 740 morceaux. Les questions sont les 42 de
`eval/questions-service-public.json`, auxquelles le banc ajoute les 16 questions de validation.

Les rapports et les instantanés sont versionnés. Les index reconstruits ne le sont pas, car
`.gitignore` les exclut.

| Dossier | Commande | Ce qu'il montre |
|---|---|---|
| `2026-09-12-reduit-calibration/` | `benchmark --embedding bge-m3 nomic --generation llama3-2-3b --validate-with … --runs 1` | les seuils par modèle sur ce corpus, puis leur validation sur 16 questions jamais vues |
| `exp-stabilite-2026-09-12-reduit/` | `experience stabilite --runs 3` | la ligne de base : ce qui bouge alors que la configuration ne change pas |
| `exp-cace-decoupage-2026-09-12-reduit/` | `experience cace-decoupage` (de 800/120 à 300/50) | CACE : changer le découpage change tout |
| `exp-changement-embeddings-2026-09-12-reduit/` | `experience changement-embeddings --other nomic` | le second verdict : refus, puis réindexation et dérive |
| `exp-changement-generateur-2026-09-12-reduit/` | `experience changement-generateur --other qwen3-4b --limit 12` | le premier verdict : même index, mais d'autres réponses et une autre latence |
| `exp-prompt-v2-2026-09-12-reduit/` | `experience prompt-v2` | ce que change le prompt, et la position que ce dépôt retient dans l'ADR 0005 : un prompt construit et versionné dans l'application, à discuter en S3.3 |

## Comparer avec un rapport d'aujourd'hui

Ces rapports sont gardés dans leur forme d'origine. Un rapport produit aujourd'hui par le dépôt ne s'y
compare donc pas ligne à ligne, parce que quelques intitulés et quelques lectures ont changé.

- Ce que les rapports appellent « reformulations » s'appelle maintenant « textes modifiés (à
  relire) ». Un texte modifié n'est pas forcément une simple reformulation : le sens a pu changer,
  un Oui devenu Non par exemple. Les 26 « reformulations » de l'expérience de stabilité, dont parle
  la lecture plus bas, sont donc des textes à relire.
- « Refus justes (hors corpus) » s'appelle maintenant « refus justes (sans réponse accessible) ».
  L'indicateur couvre les questions hors corpus et celles dont l'accès est refusé, au banc comme
  dans les expériences.
- Les indicateurs des expériences ont une ligne de plus, « mots-clés (réponses données) ».
- Au banc, « Latence médiane (ms) » est devenue « Latence médiane des réponses générées (ms) ». La
  médiane et le p90 ne comptent plus les refus prononcés sans appeler le modèle, qui sont presque
  immédiats. La latence citée plus bas suit l'ancien calcul.
- La lecture de l'expérience prompt-v2 traitait le prompt comme « une configuration surveillée
  comme du métier », en citant l'ADR 0005. Or cette ADR ne parle pas de configuration. Elle décide
  que le prompt est construit et versionné dans l'application, et laisse à la séquence S3.3 la
  question de savoir s'il relève de la configuration ou du métier. La lecture d'aujourd'hui s'en
  tient à cette décision.

## Lecture

### Calibration

Avec `bge-m3`, un document attendu arrive en tête pour 97 % des questions répondables (hit@1 de
0,97). Le banc suggère un seuil de 0,64, très proche des 0,65 configurés, qui ont donc été conservés.
Sur les 16 questions jamais vues, ce seuil retient 92 % des questions répondables et refuse toutes
celles qui doivent l'être.

Avec `nomic`, le hit@1 est de 0,88. Le seuil suggéré est de 0,71 pour 0,73 configuré, conservé lui
aussi. En validation, il retient toutes les questions répondables, mais ne refuse que 67 % des
questions qui doivent l'être.

Ces seuils avaient été calibrés le 11/09/2026 sur le corpus complet (322 fiches), avec la version tout
en Python qui a précédé ce dépôt. Ils tiennent donc sur le corpus réduit, ce qui se comprend : même
modèle, même thème, même découpage. Ce n'est pas une règle générale, c'est une mesure.

Avec `llama3.2:3b` comme générateur, 97 % des questions répondables reçoivent une réponse sourcée, et
toutes les réponses données citent une bonne source. Il n'y a aucune fuite d'accès sur 84 appels. La
latence médiane reste sous la seconde. L'indexation prend une vingtaine de secondes avec `bge-m3`, et
moins de dix secondes avec `nomic`.

### Stabilité (S3.1)

La configuration ne change pas d'un passage à l'autre, et pourtant 70 % des réponses bougent. Sur
42 questions, 26 réponses ne changent que de texte, 2 à 3 changent de sources et une change de
statut : `sp-dir-inspection`, non sourcée au premier passage, reçoit une réponse aux deux suivants.

C'est ce genre de variation que la tolérance d'un test statistique doit absorber, et
`StatisticalTests.cs` le fait avec moins d'un faux échec pour 2 000 exécutions.

### CACE (S3.2)

On passe de 800 à 300 caractères par morceau. L'index compte alors 2 188 morceaux au lieu de 740, et
son `index_id` change. Le seuil, lui, est laissé tel quel. Résultat : 76 % de dérive, avec
2 changements de statut et 3 changements de sources.

Les métriques agrégées bougent à peine : la part de bonnes sources passe de 1,00 à 0,97. C'est la
dérive question par question qui compte, pas la moyenne.

### Changement d'embeddings (S2.3, S3.2)

Si l'on passe à `nomic` sans réindexer, l'application refuse de répondre : c'est l'erreur
`IndexModelMismatchException`. Son message est explicite, il met en regard le modèle et la dimension
de l'index, et ceux du modèle servi.

Après réindexation avec `nomic`, les refus justes tombent de 1,00 à 0,70 et les bonnes sources de
1,00 à 0,93. La dérive atteint 83 %, avec 6 changements de statut. Le générateur n'a pourtant pas
changé : il ne répond qu'à partir de ce qu'on lui donne.

### Changement de générateur (S2.3, S4.1)

On remplace `llama3.2:3b` par `qwen3:4b`, sur 12 questions. L'index reste le même
(`de693eb424f2`) et n'est jamais reconstruit. La part de bonnes sources ne bouge pas (0,92).

En revanche, une question prend environ 23 secondes au lieu d'à peine plus d'une seconde, soit une
vingtaine de fois plus, à cause du mode réflexion de `qwen3`. Tous les textes changent. Le garde-fou
de forme ne rejette aucune réponse : le budget de réflexion réglé dans `config/ai_service.toml` fait
son travail.

Changer de générateur tient en une ligne de configuration, c'est vrai. Mais ce n'est pas sans
conséquence.

### Prompt v2 (S3.3)

On garde le même index et le même générateur, et on change seulement le prompt. La longueur moyenne
des réponses tombe de 357 à 41 caractères. La dérive atteint 74 %, avec 2 changements de statut. La
version tracée passe de `v1+085b70e7` à `v2+1708960b` : la trace d'une réponse générée indique
toujours avec quel prompt elle a été produite.
