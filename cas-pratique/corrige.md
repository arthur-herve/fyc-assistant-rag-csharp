# Cas pratique S5.1 — Corrigé de référence

Ce corrigé se lit après votre rendu. Le dépôt principal (`src/`, `ai_service/`, `tests/`) sert de
solution de référence. Il va bien au-delà de ce qu'on attend en 45 minutes : comparez-vous à lui pour
les frontières et leurs raisons, pas pour la quantité de code.

## Le diagnostic

Dans le diagnostic, on attend au moins les défauts suivants :

- Les droits sont filtrés après le top-k et doublés d'une consigne au modèle, qui ne protège rien de
  plus. L'identifiant du morceau réservé s'affiche dans la console avec son score.
- Les citations ne sont pas vérifiées par rapport aux passages fournis.
- Un changement de modèle d'embeddings sans réindexation passe en silence.
- Le seuil de 0,65 est écrit en dur et réglé à la main pour `bge-m3` : il ne vaut que pour ce modèle
  et ce corpus.
- La balise `<think>` est traitée dans l'application.
- Les modèles, les utilisateurs et l'adresse du service sont des champs statiques globaux.
- Il n'y a ni manifeste ni trace.
- L'index est un état global, `INDEX`.
- `Environment.Exit` est appelé au milieu de la logique.
- Les avertissements du compilateur sont désactivés.
- Il n'y a aucun test.

La correction commentée en vidéo (S5.2) parcourt le tableau ci-dessous, un défaut après l'autre.

| Défaut dans `depart/Program.cs` | Catégorie | Demande bloquée | Où c'est corrigé dans le dépôt |
|---|---|---|---|
| Les droits sont appliqués après le top-k, dans `Search`. Un passage réservé prend une place, puis il est retiré, et son identifiant s'affiche avec son score dans la console de l'utilisateur. Le prompt ajoute une consigne pour les passages marqués (réservé) : « ne le cite pas et ne l'utilise pas si l'utilisateur n'y a pas droit ». Or les passages interdits ont déjà été retirés, et cette consigne ne protège rien. | règle métier mal placée | La 2. Un salarié voit l'identifiant et le score d'un morceau réservé, et la règle, enfouie dans la recherche, ne se teste pas sans IA. | `AccessPolicy` dans `Assistant.Domain/Rules.cs`, et le prédicat passé à `IVectorIndex.Search` dans `SearchPassages`. ADR 0006. |
| Les citations sont extraites par une expression régulière, sans vérifier qu'elles renvoient à un passage fourni : une citation comme `[7]` passe sans que rien ne le signale. Une réponse sans citation est redemandée une fois, puis affichée telle quelle. La règle « toute réponse cite ses sources » est confiée au prompt. | règle métier mal placée | Aucune directement. Le programme promet de citer les fiches utilisées, et rien ne le garantit. | `Citations.Check` et le statut `Unsourced`, puis `OutputRules` pour la forme. |
| Avec `--embed nomic` sans réindexer, `Zip` tronque les vecteurs et les scores deviennent du bruit. L'application répond « aucun document », sans la moindre erreur. | absence de traçabilité, et absence de frontière | La 1. Le changement de modèle passe en silence au lieu d'être refusé. | `IndexManifest` et `IndexModelMismatchException` (ADR 0003), ainsi que les commandes `status` et `index --if-stale`. |
| `Regex.Replace("<think>…")` dans `Generate`. | particularité de modèle qui a fui | Aucune directement. Ce code lie l'application à un modèle de génération précis. | `ai_service/registry.py` retire les balises, quel que soit le moteur. Dans `ai_service/backends/ollama.py`, la réflexion est renvoyée à part, avec son propre budget. ADR 0001 et 0009. |
| `THRESHOLD = 0.65`, « ajusté à la main », ne vaut que pour un modèle et un corpus. | règle métier mal placée, et particularité de modèle qui a fui | La 1. Le seuil réglé pour `bge-m3` ne vaut plus pour `nomic-embed-text`. | `"retrieval": { "min_score": … }`, réglé par modèle et par corpus, et calibré avec `benchmark`. ADR 0004. |
| `PROMPT` est une constante, ni versionnée ni tracée. | absence de traçabilité | La 3. On ne peut pas dire quelle version du prompt a produit une réponse. | `prompts/answer.json`, dont la version et l'empreinte figurent dans `AnswerTrace`. ADR 0005. |
| `EMBED_MODEL`, `GEN_MODEL`, `USERS` et `AI_URL` sont des champs statiques globaux, et `Main` en réassigne deux. `INDEX` est global, et `Environment.Exit` est appelé dans la logique. | absence de frontière | Les trois. Changer le modèle par défaut oblige à modifier le code, et cet état global rend difficiles les tests sans IA qui doivent prouver chaque demande. | `Composition.cs`, le seul endroit qui connaît tout, et `AppConfig`. Des exceptions typées, attrapées dans `Program.Main`. |
| `index.bin` n'a pas de manifeste : l'index ne sait pas de quoi il est dérivé. | absence de traçabilité | La 1 et la 3. Rien ne détecte un index construit avec un autre modèle, et on ne peut pas dire quel index a produit une réponse. | `JsonVectorIndex`, un fichier JSON lisible accompagné de son manifeste. Voir aussi `docs/artefacts.md`. |
| Le corpus est lu, découpé, vectorisé, interrogé et passé au générateur dans la même classe statique. Aucun test n'est possible sans service IA. `TreatWarningsAsErrors` et `Nullable` sont désactivés dans le `.csproj`. | absence de frontière | Les trois, car aucune ne peut être prouvée par un test sans IA. | Les ports `IDocumentSource`, `ITextSplitter`, `IEmbedder`, `IVectorIndex` et `IGenerator`, avec leurs doubles dans `tests/Assistant.Tests/Fakes.cs`. Tous les tests tournent sans réseau. Les options du compilateur sont fixées dans `Directory.Build.props`. |

Les défauts de `--embed nomic` et de `THRESHOLD` relèvent chacun de deux catégories : l'une comme
l'autre est acceptée. Les avertissements désactivés, eux, ne relèvent vraiment d'aucune des quatre
catégories : toute catégorie justifiée est acceptée.

## Après la refonte

Une fois la refonte faite, voici ce que deviennent les trois demandes :

1. Changer de modèle d'embeddings revient à changer un alias dans la configuration, puis à réindexer.
   Tant que ce n'est pas fait, l'application refuse de répondre.
2. La confidentialité devient une règle du domaine, testée sur le prompt envoyé au modèle. Le banc
   d'essai mesure les fuites d'accès, et il en compte zéro.
3. Chaque réponse porte sa trace, et chaque index son manifeste.

## Plusieurs solutions défendables

Le corrigé n'est pas la seule bonne réponse. Les écarts suivants restent défendables, si la note les
justifie :

- Deux ou trois projets au lieu de quatre, avec un test de la règle de dépendance, comme l'explique
  la partie « Combien de projets ? » de l'[énoncé](README.md).
- Le prompt dans une constante qui porte un numéro de version, plutôt que dans un fichier, si la
  note reconnaît qu'une modification faite sans changer ce numéro passerait inaperçue.
- La balise `<think>` laissée au service IA, comme dans le dépôt, ou retirée par l'adaptateur HTTP.
  Dans les deux cas, le cas d'usage ne la connaît plus.

## Les trois demandes et la problématique

La problématique du cours demande jusqu'où l'on peut isoler le modèle. Elle invite à chercher où les
principes de la Clean Architecture tiennent, où ils se déforment et où ils cèdent. Les trois demandes
en donnent chacune un exemple.

La demande 2 montre un endroit où l'isolation tient. La règle d'accès vit dans le domaine et
s'applique avant le prompt. Le modèle ne voit jamais un passage interdit, et un test le prouve sans
IA. Pour cette règle, le modèle est bien un détail d'infrastructure.

La demande 3 montre un endroit où l'isolation se déforme. Le modèle, le prompt et l'index changent le
comportement de l'application sans toucher à son code. Les frontières restent en place, mais il faut
inscrire ces trois éléments dans chaque réponse, comme on le ferait pour une version du code.

La demande 1 montre un endroit où l'isolation cède. Le port cache le fournisseur, mais pas le lien
entre le modèle, l'index et le seuil : l'index a été calculé avec ce modèle, et le seuil a été réglé
pour lui. Changer de modèle d'embeddings oblige donc à réindexer et à recalibrer. L'architecture ne
peut pas cacher ce changement. Elle peut seulement le rendre explicite, en refusant un index
incompatible au lieu de se tromper en silence.

C'est une lecture possible, pas la seule. On peut soutenir, par exemple, que le prompt relève de la
logique métier plutôt que de la configuration. La demande 3 montrerait alors un endroit où
l'isolation cède, puisqu'une partie du métier vivrait hors du code. C'est le débat de la séquence 3.3,
et votre note d'architecture peut y prendre position.
