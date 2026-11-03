# Fil rouge, correction de S2.3 : brancher le modèle

> **Correction de la séquence S2.3.** Ce tag, `corrige/s2.3`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre travail et à récupérer les tests attendus. L'application complète,
> avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Le fil rouge fonctionne de bout en bout, dans sa version idéale : le cœur ne connaît le modèle que par
ses ports, les droits sont filtrés avant le prompt, et chaque réponse doit citer ses sources. On change
de générateur par une option, sans toucher au cœur ni à l'index. La séquence S3.1 part de là, avec une
question : un cas d'usage qui appelle un modèle reste-t-il une fonction déterministe, et sinon,
comment le tester ?

## Ce que la correction a changé

Au départ de la séquence, le générateur était un bouchon, `MissingGenerator`, et `ask` s'arrêtait dès
qu'il fallait générer. La correction écrit `HttpGenerator` dans
`src/Assistant.Infrastructure/HttpAiClient.cs`, d'après `docs/contrat-http.md`. Il envoie à la route
`/v1/generate` le prompt construit par l'application, vérifie que la réponse porte `model` et `text`, et
rend le texte. `src/Assistant.Cli/Composition.cs` le construit à la place du bouchon, qui
disparaît, et ni le domaine ni l'application ne bougent. Les tests de l'exercice vérifient le contrat du
côté de la génération et lancent `ask` contre un faux service IA.

Pour lire la correction dans le code et dans les tests :

```bash
git diff origin/etape/s2.3 corrige/s2.3 -- src tests
```

`docs/contrat-http.md` dit maintenant comment l'application lit les réponses du service. Le corrigé de
l'énoncé, à la fin de `exercices/s2.3-adaptateur-de-generation/README.md`, répond aux questions de la
séquence.

## Démarrer

Si votre poste n'est pas encore prêt, suivez [`docs/installation.md`](docs/installation.md). Vérifiez
ensuite les deux programmes :

```bash
dotnet test
python -m unittest discover -s tests_python -t .
```

Les deux commandes doivent se terminer sans échec. Lancez ensuite le service IA hors ligne dans un
premier terminal, et laissez-le tourner :

```bash
python -m ai_service
```

Dans un second terminal, construisez l'index, puis posez une question :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
```

`index` affiche le nombre de documents et de morceaux qu'il a indexés. La réponse cite sa source avec
`[1]`, ici la politique de télétravail, et se termine par une ligne qui donne son statut. Après chaque
changement de branche, reconstruisez l'index avec `index` : le dossier `data/` n'est pas versionné, et
l'index d'une autre étape n'a pas forcément le même format. Dans ce cas, l'application refuse de le lire
et donne la commande qui le reconstruit.

Posez ensuite la même question à alice et à bruno. alice n'est que dans le groupe `tous`, bruno est
aussi aux RH. L'option `-v` affiche la trace de la réponse : les passages retrouvés, leurs scores et la
sortie brute, c'est-à-dire le texte que `HttpGenerator` a rendu.

```bash
dotnet run --project src/Assistant.Cli -- ask "Quelle est la grille des salaires ?" --user alice -v
dotnet run --project src/Assistant.Cli -- ask "Quelle est la grille des salaires ?" --user bruno -v
```

alice reçoit un refus : aucun document qu'elle a le droit de lire ne répond. La trace montre que la
grille ne figure pas parmi ses passages, car elle a été écartée avant même la construction du prompt.
bruno, lui, reçoit une réponse qui cite la grille des rémunérations.

Posée autrement, « Quelle est la fourchette de salaire d'un consultant senior ? », la question donne la
fourchette à bruno. alice n'obtient pas de refus, mais une phrase sans rapport tirée de la fiche des
congés, avec un score de 0,154 pour un seuil de 0,15. La règle des droits tient, puisque la grille
n'entre jamais dans son prompt.

Enfin, le générateur se remplace sur le même index, sans toucher au cœur ni réindexer :

```bash
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-variante -v
dotnet run --project src/Assistant.Cli -- ask "En combien de temps un incident P1 est-il pris en charge ?" --generation-model extractive-variante -v
```

`extractive-variante` est un autre générateur hors ligne. Il choisit sa phrase autrement
qu'`extractive`, mais il cite toujours sa source et redonne la même réponse à chaque fois. Relancez les
deux commandes sans `--generation-model` pour comparer : les passages retrouvés et leurs scores n'ont pas
bougé, seule la réponse change. Sur la première question, la variante répond moins bien : sa phrase vient
de la fiche du télétravail, mais elle ne donne pas le nombre de jours. Sur la seconde, elle répond mieux :
elle donne le délai de prise en charge, là où `extractive` ne donne que le titre de la fiche. Ces
questions ont été choisies pour montrer les deux sens : sur d'autres, quand la variante ne répond pas
comme `extractive`, elle répond le plus souvent moins bien. Changer de générateur change les réponses,
dans un sens ou dans l'autre, pas le cœur. Avec Ollama, essayez de même `--generation-model llama3-2-3b`
sur la question du P1.

## Les branches du cours

| Séquence | Où l'on travaille | Correction |
|---|---|---|
| S1.1, S1.3 et S2.1 | `main` | `corrige/s2.1` |
| S2.2 Cœur sans IA | `etape/s2.2` | `corrige/s2.2` |
| S2.3 Brancher le modèle | `etape/s2.3` | `corrige/s2.3` |
| S3.1 Non-déterminisme | `etape/s3.1` | `corrige/s3.1` |
| S3.2 Les données sont du code | `etape/s3.2` | `corrige/s3.2` |
| S3.3 Frontière métier et modèle | `etape/s3.3` | pas de correction, c'est un débat |
| S4.1 Stratégies d'isolation | `etape/s4.1` | `corrige/s4.1` |
| S4.2 Versionner | `etape/s4.2` | `corrige/s4.2` |
| S4.3 et le module 5 | `final` | `final` |

Pour changer d'étape : `git switch etape/s3.2`, par exemple. Les corrections sont des tags : on les
compare à une branche avec `git diff origin/etape/s3.2 corrige/s3.2`, et on en récupère un fichier avec
`git restore --source corrige/s3.2 -- <fichier>`. La commande de restauration nomme toujours des fichiers,
jamais un dossier.

## Les choix de cette étape

- L'application C# et le service IA en Python ne partagent que le contrat de `docs/contrat-http.md`.
- L'index vit dans l'application, dans un fichier JSON.
- Le seuil de pertinence est un réglage de la configuration.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application.
