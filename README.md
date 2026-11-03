# Fil rouge, correction de S4.2 : versionner ensemble

> **Correction de la séquence S4.2.** Ce tag, `corrige/s4.2`, est l'état du fil rouge à la fin de la
> séquence. Il sert à comparer votre tableau des artefacts au corrigé. L'application complète, avec
> toute sa documentation, est sur la branche `final`.

## Où en est le fil rouge

Toutes les parades du cours sont en place : les droits filtrés avant le prompt, les citations vérifiées,
les tentatives, le manifeste de l'index, les décorateurs et la validation de la sortie. Cette correction
ajoute la dernière, la traçabilité. On sait désormais quel index et quelle version du prompt ont produit
une réponse, et si l'index est encore valable. Il ne manque rien de ce que demandent les séquences : la
branche `final` y ajoute ce qui sert à la fin du cours. La séquence S4.3 part de là, avec une question :
ces parades valent-elles ce qu'elles coûtent, et à quel niveau de rigueur faut-il s'arrêter ?

## Ce que la correction a changé

Au départ de la séquence, un document modifié, un découpage changé ou un prompt réécrit sur place
laissaient des réponses périmées, ou les changeaient sans laisser de trace. La correction donne une
identité à chaque artefact. Le manifeste de l'index porte maintenant un identifiant, l'empreinte du
corpus, le découpage et la date de construction, en plus du modèle et de la dimension. Les prompts ont une
version déclarée, complétée par une empreinte de leur contenu, et chaque réponse comme chaque instantané
nomme l'index et la version du prompt qui l'ont produit. Enfin, `status` dit si l'index correspond encore
au corpus, au découpage et au modèle servi, et `index --if-stale` ne le reconstruit que s'il le faut.

Le corrigé du tableau est dans `docs/artefacts.md`. Pour lire la correction seule :

```bash
git diff --stat origin/etape/s4.2 corrige/s4.2
git diff origin/etape/s4.2 corrige/s4.2 -- src/Assistant.Domain src/Assistant.Application prompts
```

Refaites ensuite les manipulations de la séquence, le service IA lancé. Remplacez « deux jours » par
« trois jours » dans `corpus/solveo/teletravail.md` : `ask` répond encore « deux jours », mais `status`
signale que le corpus a changé depuis l'indexation et se termine avec le code 2. `index --if-stale`
reconstruit alors l'index, et la réponse suit le document. Remettez ensuite le document en état, puis
l'index avec la même commande :

```bash
dotnet run --project src/Assistant.Cli -- status
dotnet run --project src/Assistant.Cli -- index --if-stale
git restore corpus/solveo/teletravail.md
dotnet run --project src/Assistant.Cli -- index --if-stale
```

Remplacez enfin « Question : » par « Demande : » dans `prompts/answer.json`, entre deux instantanés :

```bash
dotnet run --project src/Assistant.Cli -- snapshot record avant
# remplacement dans prompts/answer.json
dotnet run --project src/Assistant.Cli -- snapshot record apres
dotnet run --project src/Assistant.Cli -- snapshot compare avant apres
```

La comparaison mesure 62 % de dérive, et elle en nomme cette fois la cause : `prompt_version` est la
seule différence de configuration. Remettez ensuite le prompt en état avec
`git restore prompts/answer.json`.

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

Dans un second terminal, construisez l'index, vérifiez-le, puis posez une question :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- status
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" -v
```

La réponse se termine par une ligne qui nomme les modèles, l'index et la version du prompt. Avec `-v`,
elle est suivie de sa trace complète : les passages retrouvés, leurs scores et la sortie brute du modèle.
Après chaque changement de branche, reconstruisez l'index avec `index` : le dossier `data/` n'est pas
versionné, et l'index d'une autre étape n'a pas forcément le même format. Dans ce cas, l'application
refuse de le lire et donne la commande qui le reconstruit.

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
- Les droits sont filtrés avant le prompt, et le prompt est construit et versionné dans l'application.
  Il se choisit par son nom, dans la configuration ou avec `--prompt`.
- La recherche, déterministe, se teste par assertion. La génération a droit à une seconde tentative,
  et une graine peut la rendre reproductible.
- Les décorateurs entourent les ports sans que les cas d'usage le sachent, et le cache suit l'index courant.
  La sortie du modèle passe par un décorateur de l'application, qui la valide avec une règle du domaine.
