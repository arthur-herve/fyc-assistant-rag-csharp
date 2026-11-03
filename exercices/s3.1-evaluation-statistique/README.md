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

## Corrigé

La correction est le tag `corrige/s3.1`. Pour voir ce qu'elle change dans le code et dans les tests :

```bash
git diff origin/etape/s3.1 corrige/s3.1 -- src tests
```

### Ce que la correction ajoute

Les tests statistiques sont dans `tests/Assistant.Tests/StatisticalTests.cs`. Le générateur
`ForgetfulGenerator` est celui des étapes 0 et 2, et `BinomialCdf` fait le calcul de la loi binomiale.
Trois tests s'en servent :

- `Answer_rate_with_a_single_attempt_stays_above_its_tolerance`, avec une seule génération : taux
  attendu de 0,70, tolérance de 0,50 ;
- `Answer_rate_stays_above_the_tolerance`, avec deux tentatives : taux attendu de 0,91, tolérance de
  0,75 ;
- `The_false_failure_probability_is_documented_and_small`, qui vérifie l'ordre de grandeur des
  probabilités annoncées en commentaire, pour une et pour deux tentatives. La documentation des tests
  est elle-même testée.

Votre test déterministe a ses équivalents dans `AskQuestionTests.cs`, en particulier
`Refuses_an_unsourced_answer_after_all_attempts`, `Refusing_an_unsourced_answer_still_gives_the_passages`,
`Retries_when_the_model_forgets_to_cite` et `A_huge_citation_number_is_invalid_not_a_crash`.

Dans le code, la frontière passe désormais entre ce qui est déterministe et ce qui ne l'est pas :

- `SearchPassages` sort d'`AskQuestion`. C'est la moitié déterministe du cas d'usage, la recherche
  filtrée par les droits, et elle se teste par assertion.
- `AskQuestion` refait une tentative quand les citations échouent : c'est `AskSettings.MaxAttempts`, avec
  la clé `generation.max_attempts`, qui vaut 2 dans `config/app.json`.
- Une graine peut être fixée : `AskSettings.Seed`, avec la clé `generation.seed` et l'option `--seed` de
  `experience stabilite`. Elle augmente d'une unité à chaque tentative, ce que vérifie
  `Seed_changes_between_attempts`, et `HttpGenerator` l'envoie désormais au service IA.
- La trace et les instantanés comptent les tentatives.

### Le test de l'étape 0

Chaque lancement passe avec la probabilité 0,70. Dix lancements de suite passent avec la probabilité
0,7¹⁰, soit 2,8 %. Le test échoue donc au moins une fois sur dix lancements avec une probabilité de
97,2 %. Ce n'est pas un test, c'est un tirage : son résultat ne dit rien du code.

### Le choix de la tolérance

Avec une seule génération, le taux attendu est de 0,70. Avec `MinAnswerRate = 0.50`, le test échoue si
24 essais sur 50 ou moins sont sourcés. Pour X ~ B(50 ; 0,70), P(X ≤ 24) vaut 0,093 %, soit un faux
échec pour 1 070 exécutions environ. Une tolérance de 0,48 convient aussi : le test échoue alors à 23
succès ou moins, avec une probabilité de 0,034 %.

Avec deux tentatives, une question obtient une réponse sourcée si l'une des deux cite sa source. La
probabilité passe à 1 − 0,3² = 0,91. Avec `MinAnswerRate = 0.75`, le test échoue à 37 succès ou moins,
avec une probabilité de 0,037 %, soit un faux échec pour 2 700 exécutions environ.

Une tolérance trop serrée devient vite fragile. Le tableau donne la probabilité de faux échec pour
quelques tolérances.

| Tolérance | Le test échoue à | Une génération (0,70) | Deux tentatives (0,91) |
|---|---|---|---|
| 0,48 | 23 succès ou moins | 0,034 % | négligeable |
| 0,50 | 24 succès ou moins | 0,093 % | négligeable |
| 0,55 | 27 succès ou moins | 1,2 % | négligeable |
| 0,60 | 29 succès ou moins | 4,8 % | négligeable |
| 0,75 | 37 succès ou moins | 77,7 % | 0,037 % |
| 0,80 | 39 succès ou moins | 92 % | 0,43 % |
| 0,85 | 42 succès ou moins | 99 % | 7,7 % |

À 0,80, le test à deux tentatives échoue pour rien une fois sur 230 : c'est trop pour une intégration
continue. À 0,85, c'est une fois sur treize. La tolérance dépend aussi du mécanisme testé, pas seulement
du modèle : la tolérance de 0,75, sûre à deux tentatives, ferait échouer le test à une seule génération
77,7 fois sur 100. C'est pourquoi chacun des deux tests a la sienne.

### Ce que chaque test vérifie

| | Test déterministe | Évaluation statistique |
|---|---|---|
| Ce qu'il vérifie | la règle métier : aucune réponse non sourcée n'est affichée | la qualité du comportement : assez de réponses sourcées |
| Entrées | des sorties choisies pour couvrir les cas limites | des sorties tirées au hasard |
| Résultat | vrai ou faux, et reproductible | une proportion, avec une tolérance et un risque de faux échec documentés |
| Ce qu'il ne vérifie pas | que le modèle réel cite souvent ses sources | que la règle tient dans tous les cas, car il peut passer à côté d'un cas rare |
| S'il échoue | il **bloque la livraison**, car une garantie est cassée | c'est une alerte : le modèle, le prompt ou le seuil a dérivé, à examiner avec les instantanés (`snapshot compare`) |

Le test de l'étape 0 ne se range dans aucune des deux colonnes : il affirme une valeur exacte sur une
sortie tirée au hasard. La frontière du cours passe exactement ici. D'un côté, ce qui est déterministe :
les droits, la recherche et la vérification des citations, que l'on teste par une assertion. De l'autre,
ce qui relève du modèle, que l'on évalue par une proportion, avec le test statistique et
`experience stabilite`.

### Ce que montre la mesure

Avec la correction, relancez `experience stabilite --runs 3 --generation-model extractive-bruite` : elle
tourne désormais avec deux tentatives. Pour retrouver une seule génération, passez par `--config` une
copie de `config/app.json` où `generation.max_attempts` vaut 1. Relancez enfin avec une graine, par
exemple `--seed 7`. Voici l'ordre de grandeur de ce que donnent, hors ligne, des séries de trois
passages sans graine, puis avec une graine.

| | Une génération | Deux tentatives | Deux tentatives et une graine |
|---|---|---|---|
| Dérive moyenne | environ la moitié | un peu moins de la moitié | 0 % |
| Changements de statut dans une série | une quinzaine | environ deux fois moins | 0 |
| Taux de non sourcé d'un passage | 0,23 en moyenne | 0,06 en moyenne | fixé par la graine |

La seconde tentative fait à peine baisser la dérive : le générateur tire toujours sa phrase au hasard, et
le texte change d'un passage à l'autre. Elle fait en revanche chuter le non sourcé et les changements de
statut. Le calcul le prévoyait : 18 des 24 questions atteignent le modèle, et chacune reste non sourcée
avec la probabilité 0,3 à une génération, 0,09 à deux tentatives, soit des taux attendus de 0,225 et de
0,068.

Seule la graine ramène la dérive à zéro. Mais elle rend le tirage reproductible, pas bon. Avec
`--seed 7`, toutes les réponses sont citées. Avec `--seed 42`, le même tirage fait oublier la citation
aux deux tentatives, et les 18 questions deviennent non sourcées, à chaque passage. Deux symptômes, deux
parades : les tentatives contre les citations oubliées, la graine contre la variabilité. Ni l'une ni
l'autre ne remplace le test statistique.
