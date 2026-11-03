# Fil rouge, correction de S4.1 : stratégies d'isolation

> **Correction de la séquence S4.1.** Ce tag, `corrige/s4.1`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre travail et à récupérer les tests attendus. L'application complète,
> avec toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Les décorateurs sont au complet. Le cache, le journal et les tentatives réseau entourent les ports, et la
validation de la sortie du modèle s'y ajoute. Une sortie qui n'a pas la forme d'une réponse, parce
qu'elle est vide, trop longue, écrite dans une autre langue ou encombrée d'un raisonnement, est refusée
et compte comme une tentative ratée. La séquence S4.2 part de là, avec une question : que faut-il
versionner avec le code pour pouvoir reproduire une réponse et dire d'où elle vient ?

## Ce que la correction a changé

Au départ de la séquence, une sortie comme « Okay, let me check the documents. … [1] » recevait le statut
`answered`, puisque seules les citations étaient vérifiées. La correction ajoute
`OutputValidatingGenerator`, un décorateur de l'application qui applique au texte la règle du domaine
`OutputRules` et lève `ModelOutputRejectedException` quand la forme ne convient pas. `AskQuestion`
attrape ce rejet, le note dans la trace et passe à la tentative suivante. `Composition.Decorate` place la
validation sous le journal des générations, qui voit ainsi les rejets. Les clés
`decorators.validate_output` et `max_output_chars` arrivent dans la configuration, et dans celle des
instantanés.

Les protections du fil rouge se rangent désormais en deux familles. Celles qui isolent la technologie
rendent le modèle remplaçable :

- le service IA, qui sert de couche anticorruption
- le contrat HTTP, lu strictement
- le cache, le journal et les tentatives réseau

Celles qui isolent l'incertitude empêchent une sortie douteuse d'arriver jusqu'à l'utilisateur :

- les droits filtrés avant le prompt
- les citations vérifiées
- les tentatives du cas d'usage
- la validation de la forme

Pour lire la correction seule, puis ce qu'elle change dans le code et la configuration :

```bash
git diff --stat origin/etape/s4.1 corrige/s4.1
git diff origin/etape/s4.1 corrige/s4.1 -- src config
```

Relancez ensuite la démonstration de la séquence, le service IA lancé :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bavard -v
```

La réponse est refusée après deux tentatives : elle reçoit le statut `unsourced`, et l'utilisateur lit
« Je n'ai pas pu produire de réponse correctement sourcée ». Le journal montre les deux rejets, et la
trace garde les deux sorties, précédées de « <rejetée : … ». Le corrigé de l'énoncé, à la fin de
`exercices/s4.1-decorateur-de-validation/README.md`, répond aux questions de la séquence.

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
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" -v
```

La réponse se termine par une ligne qui nomme les modèles et le nombre de tentatives. Avec `-v`, elle
est suivie de sa trace : les passages retrouvés, leurs scores et la sortie brute du modèle. Après chaque
changement de branche, reconstruisez l'index avec `index` : le dossier `data/` n'est pas versionné, et
l'index d'une autre étape n'a pas forcément le même format. Dans ce cas, l'application refuse de le lire
et donne la commande qui le reconstruit.

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
  L'index vit dans l'application. Construit par un autre modèle, il est refusé au lieu de répondre à côté.
- Le seuil de pertinence se règle par modèle d'embeddings et par corpus, dans la configuration.
- Les droits sont filtrés avant le prompt, et le prompt est construit dans l'application. Il se choisit
  par son nom, dans la configuration ou avec `--prompt`.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
- Les décorateurs entourent les ports sans que les cas d'usage le sachent, et le cache suit l'index courant.
  La sortie du modèle passe par un décorateur de l'application, qui la valide avec une règle du domaine.
