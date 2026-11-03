# Exercice S3.1 — Un test déterministe et une évaluation statistique (C#)

Durée indicative : 45 minutes. L'exercice se fait dans le fil rouge, sur la branche `etape/s3.1`, dans
`tests/Assistant.Tests/`. Il n'y a rien à restaurer : vous écrivez vos propres tests. Le corrigé arrive
avec la correction, `corrige/s3.1`.

## Situation

Le cas d'usage `AskQuestion` appelle le générateur une seule fois. Si la sortie ne cite pas correctement
ses sources, il rend une réponse « non sourcée » (`AnswerStatus.Unsourced`) plutôt qu'un texte sans
citation.

Le générateur `extractive-bruite` du service IA imite un modèle imparfait. Il oublie de citer sa source
3 fois sur 10, et il choisit au hasard entre les deux phrases les plus proches de la question. Avec lui,
la même question ne donne pas toujours la même réponse. Voyez-le, le service IA lancé :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?" --generation-model extractive-bruite
dotnet run --project src/Assistant.Cli -- experience stabilite --runs 3 --generation-model extractive-bruite
```

Posez la question cinq fois : la phrase change, et la réponse devient parfois « non sourcée ».
L'expérience, elle, pose trois fois de suite les 24 questions de `eval/questions.json`, sans rien changer
à la configuration. Son rapport appelle chaque tour un « passage » et compare les suivants au premier.
Hors ligne, environ une réponse sur deux change d'un passage à l'autre : c'est la dérive que mesure le
rapport. Il compte aussi une quinzaine de changements de statut entre `answered` et `unsourced`, et ces
chiffres varient d'une série à l'autre. Avec `extractive`, rien ne bouge.

Un test qui affirme « la réponse est *Deux jours par semaine [1]* » échouerait donc au hasard. C'est ce
que l'exercice vous fait constater, puis corriger.

## Ce qu'on vous demande

Créez votre branche :

```bash
git switch -c mon-s3.1 origin/etape/s3.1
```

Écrivez vos tests dans un fichier à vous, par exemple `tests/Assistant.Tests/MesTests.cs`, classe
`MesTests`. Ils ne doivent faire appel ni à une IA ni au réseau : utilisez les doubles de `Fakes.cs`,
comme `Build.Ask`, `ScriptedGenerator` et `Fakes.Alice`.

0. **Le test qu'on écrirait sans y penser.** Écrivez un générateur, c'est-à-dire un `IGenerator`, qui
   s'appuie sur un `Random` sans graine pour oublier la citation 3 fois sur 10 : il rend
   « Deux jours [1]. » ou « Deux jours. ». Écrivez ensuite un test qui pose la question « Combien de jours
   de télétravail ? » avec ce générateur, et qui affirme que la réponse a le statut `Answered`. Lancez-le
   dix fois :

   ```bash
   dotnet build tests/Assistant.Tests
   for i in 1 2 3 4 5 6 7 8 9 10; do dotnet test tests/Assistant.Tests --no-build --filter "FullyQualifiedName~MesTests" | grep -E "Passed!|Failed!"; done
   ```

   Sous PowerShell, la boucle s'écrit ainsi :

   ```powershell
   1..10 | ForEach-Object { dotnet test tests/Assistant.Tests --no-build --filter "FullyQualifiedName~MesTests" | Select-String "Passed!|Failed!" }
   ```

   Quelle est la probabilité que ce test passe dix fois de suite ?

   Une fois la question tranchée, supprimez ce test, mais gardez le générateur, qui resservira à
   l'étape 2 : laissé dans `MesTests`, le test ferait échouer `dotnet test` au hasard.

1. **Un test déterministe de la règle métier.** Quelle que soit la sortie du modèle, l'assistant
   n'affiche jamais une réponse sans citation valide. Utilisez `ScriptedGenerator` avec des sorties que
   vous choisissez : une sans citation, une qui cite un passage inexistant (`[7]`), une vide et une avec
   une citation valide. Le modèle n'étant appelé qu'une fois par question, chaque sortie correspond à une
   question posée. Vérifiez le statut et les sources de chaque réponse. Ce test doit passer cent fois sur
   cent.

2. **Une évaluation statistique du comportement.** Reprenez le générateur de l'étape 0, avec
   `ForgetRate = 0.3` et toujours sans graine. Posez la même question `Trials = 50` fois, et vérifiez que
   le taux de réponses sourcées atteint au moins une tolérance `MinAnswerRate` que vous choisissez
   vous-même.

   Dans le test, documentez trois choses :
   - le taux attendu, calculé et non deviné ;
   - la tolérance retenue ;
   - la probabilité que le test échoue alors que le système est correct, ce qu'on appelle un « faux
     échec ».

3. **Les deux régimes.** Répondez par écrit : que vérifie chacun des deux tests, et que ne vérifie-t-il
   pas ? Lequel doit bloquer une livraison s'il échoue ? Et que valait le test de l'étape 0 ?

## Indications

- Avec une seule génération, une question obtient une réponse sourcée si cette génération cite sa
  source. La probabilité vaut donc 1 − 0,3 = 0,70.
- Sur 50 essais, le nombre de succès suit une loi binomiale B(50 ; 0,70). Le test échoue quand ce nombre
  tombe sous la tolérance. La probabilité de faux échec est donc P(X ≤ k), où k est le plus grand nombre
  de succès qui fait encore échouer le test. Elle se calcule en quelques lignes, avec un coefficient
  binomial en `double`, sans bibliothèque.
- Un faux échec coûte cher : le développeur relance, se met à douter du test et finit par l'ignorer.
  Visez moins de 0,1 %.

## Pour aller plus loin

Avec Ollama, lancez l'expérience de stabilité avec de vrais modèles :

```bash
dotnet run --project src/Assistant.Cli -- experience stabilite --config config/app-ollama.json --questions eval/questions-service-public.json --runs 3
```

La dérive mesurée à configuration constante, c'est le non-déterminisme réel : celui que la tolérance
d'un test statistique doit absorber.
