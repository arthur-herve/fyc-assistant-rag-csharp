# Corpus documentaires

Le dépôt contient trois corpus. Chaque fichier `config/app*.json` choisit le sien avec la clé
`"corpus": {"directory": …}`.

| Dossier | Contenu | Usage |
|---|---|---|
| `solveo/` | 9 documents fictifs de Solvéo, une entreprise imaginaire (RH et informatique), dont 3 à accès restreint | tests automatisés, démarrage hors-ligne, exercices courts (`config/app.json`) |
| `service-public-reduit/` | 50 fiches réelles de Service-Public.gouv.fr, tirées du corpus complet : c'est le corpus du cours | fil rouge avec de vrais modèles, exercices, banc de calibration (`config/app-ollama.json`) |
| `service-public/` | 322 fiches réelles du thème « Travail - Formation », secteur privé | expériences à l'échelle (découpage, changement de modèle, stabilité) et mesure de ce que change le passage de 50 à 322 documents (`config/app-ollama-complet.json`) |

Le corpus réduit a la taille que demande le cahier des charges de la séquence S2.1 : « quelques
dizaines de documents ». Il s'indexe en une vingtaine de secondes avec `bge-m3` sur la machine de
référence, qui dispose d'une carte graphique, et presque instantanément avec `hashing`.

Les deux corpus Service-Public partagent les mêmes jeux de questions,
`eval/questions-service-public*.json`. Toutes les fiches que ces questions attendent se trouvent dans
le corpus réduit, et les questions hors corpus le restent dans les deux.

Un seuil de pertinence calibré sur un corpus ne vaut pas forcément pour l'autre. C'est l'effet CACE :
changer une chose peut tout changer. Ici, la mesure a montré que les seuils tiennent sur les deux
corpus. C'est le résultat d'une mesure, pas une règle, et le banc d'essai permet de la refaire.

CACE veut dire « Changing Anything Changes Everything ». Le principe vient de Sculley et al. (2015),
« Hidden Technical Debt in Machine Learning Systems ». Hors ligne, `experience cace-decoupage` le montre
sur le corpus Solvéo : en ne changeant que le découpage, un tiers des réponses changent, et la part des
refus justes tombe de 0,80 à 0,40.

## Format des documents

Chaque document est un fichier Markdown qui commence par un en-tête placé entre deux lignes `---`.
C'est `src/Assistant.Infrastructure/MarkdownCorpus.cs` qui le lit :

```markdown
---
id: identifiant-unique
titre: Titre du document
groupes: tous
---
Texte du document…
```

La ligne `groupes` est obligatoire. Elle vaut `tous` pour un document public. Sinon, elle donne des
noms de groupes en minuscules, séparés par des virgules (`rh, direction`), sans commentaire en fin de
ligne. Si elle manque, si elle est vide ou mal écrite, le chargement du corpus s'arrête avec un
message d'erreur : un oubli ne rend jamais un document public.

Une faute de frappe dans un nom de groupe, en revanche, passe sans erreur si le nom reste bien formé,
par exemple `rhh` au lieu de `rh`. Le document devient alors illisible pour tout le monde. C'est sans
danger, mais rien ne le signale : vérifiez les droits avec `ask --user`.

Les autres clés de l'en-tête (source, date, thème…) sont conservées pour information. L'application
n'en tient pas compte.

Les fichiers doivent être enregistrés en UTF-8. Un document enregistré dans un autre encodage, en
latin-1 par exemple, arrête le chargement, et le message indique le fichier à corriger.

Un dossier de corpus ne contient que des documents, puisque tout fichier `.md` doit porter cet
en-tête. C'est pour cette raison que la source et la licence des fiches sont décrites ici, et non
dans `service-public/`.

## `service-public/` : fiches Service-Public.gouv.fr, thème « Travail - Formation », secteur privé

### Source et licence

Les fiches viennent du jeu de données « Fiches pratiques et ressources de Service-Public.gouv.fr —
Particuliers », publié sur data.gouv.fr par la Direction de l'information légale et administrative
(DILA) :
<https://www.data.gouv.fr/datasets/fiches-pratiques-et-ressources-de-service-public-gouv-fr-particuliers>

Le fichier utilisé est `vosdroits-latest.zip` (flux XML, schéma 3.5). Il a été téléchargé le
11 septembre 2026 depuis
<https://lecomarquage.service-public.gouv.fr/vdd/3.5/part/zip/vosdroits-latest.zip>.

Les fiches sont publiées sous Licence Ouverte / Open Licence 2.0 (Etalab), avec une mention
obligatoire : *Service-Public.gouv.fr / DILA*. L'en-tête de chaque fiche donne l'adresse de la fiche
d'origine (`source`) et sa date de modification (`date`).

La conversion en Markdown est faite par
[`tools/import_service_public.py`](../tools/import_service_public.py), un script Python qui n'utilise
que la bibliothèque standard. Ses tests sont dans `tests_python/tools/`. La conversion garde le
texte, les titres, les listes, les tableaux et les encadrés. Elle laisse de côté les contacts, les
services en ligne, les références légales et les renvois. Le contenu des fiches, lui, n'est pas
modifié.

Pour obtenir une version à jour, lancez depuis la racine du dépôt :

```bash
python tools/import_service_public.py --download
```

Les fiches de `corpus/service-public/` sont alors remplacées. Celles de `service-public-reduit/` ne
bougent pas, car c'est une copie figée (voir plus bas). Il faut ensuite refaire la date et les
comptes de cette page, ainsi que les seuils et les mesures du corpus complet.

### Périmètre

Le corpus complet compte 322 fiches du thème « Travail - Formation ». Seuls les dossiers qui
concernent un salarié du secteur privé sont retenus : les dossiers « fonction publique » et
« particulier employeur » sont écartés.

### Droits d'accès : une simulation

Ces fiches sont publiques. Le fil rouge du cours joue pourtant un intranet d'entreprise, où
l'assistant ne montre à chaque salarié que ce qu'il a le droit de lire. Les droits ci-dessous sont
donc une règle pédagogique. Ils sont attribués par dossier, dans le tableau `DOSSIERS` de
`tools/import_service_public.py`.

| Groupe | Dossiers | Fiches |
|---|---|---|
| `tous` | Conditions de travail, Maladie ou accident du travail, Handicap et emploi, Congés, Contrats de travail, Contrats d'insertion, Retraite, Formation des salariés, Formation des personnes handicapées, Stage en entreprise, Temps de travail, Représentation du personnel | 248 |
| `rh` | Recrutement, Licenciement pour motif personnel, Licenciement économique, Rupture du contrat de travail | 61 |
| `direction` | Conflits du travail | 13 |

Les utilisateurs de démonstration sont définis dans `config/app-ollama.json`. `alice` lit le groupe
`tous`, `bruno` les groupes `tous` et `rh`, `claire` les groupes `tous` et `direction`.

### `service-public-reduit/` : comment les 50 fiches ont été choisies

Le corpus réduit est un sous-ensemble figé du corpus complet : ce sont les mêmes fichiers, copiés sans
modification. Il réunit deux sortes de fiches.

D'abord les 39 fiches que citent les deux jeux de questions, `eval/questions-service-public.json` et
`eval/questions-service-public-validation.json`, dans leurs champs `expected_documents` et
`forbidden_documents`.

Ensuite 11 fiches de diversion, prises pour l'essentiel dans des dossiers que les questions ne
couvrent pas : représentation du personnel, contrats de travail, licenciement économique, handicap,
retraite… Elles sont là pour que la recherche ait de quoi se tromper.

Le corpus réduit compte ainsi 38 fiches `tous`, 8 fiches `rh` et 4 fiches `direction`, venues de
17 dossiers. La liste complète est dans
[`LISTE-service-public-reduit.md`](LISTE-service-public-reduit.md). Elle se trouve à côté de ce
README, et non dans le dossier du corpus, pour la même raison que plus haut : un dossier de corpus
ne contient que des documents.

### Ce que cette conversion ne fait pas

Les fiches renvoient souvent à d'autres fiches, à des simulateurs ou à des textes de loi. Ces liens
sont retirés à la conversion, si bien qu'une réponse de l'assistant peut paraître incomplète par
rapport au site. Les montants et les délais sont ceux du jour du téléchargement, et ils changent.
**Pour toute décision réelle, consultez <https://www.service-public.gouv.fr>.**
