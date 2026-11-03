# Corpus documentaires

Le dépôt contient trois corpus.

| Dossier | Contenu | Usage |
|---|---|---|
| `solveo/` | 9 documents fictifs de Solvéo, une entreprise imaginaire (RH et informatique), dont 3 à accès restreint | démarrage hors-ligne, exercices courts |
| `service-public-reduit/` | 50 fiches réelles de Service-Public.gouv.fr, tirées du corpus complet : c'est le corpus du cours | fil rouge avec de vrais modèles, exercices |
| `service-public/` | 322 fiches réelles du thème « Travail - Formation », secteur privé | mesures à plus grande échelle |

Le corpus réduit a la taille que demande le cahier des charges de la séquence S2.1 : « quelques
dizaines de documents ».

## Format des documents

Chaque document est un fichier Markdown qui commence par un en-tête placé entre deux lignes `---` :

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
ligne. Si elle manque, si elle est vide ou mal écrite, le document n'est pas valide : un oubli ne doit
jamais rendre un document public.

Une faute de frappe dans un nom de groupe, en revanche, passe sans erreur si le nom reste bien formé,
par exemple `rhh` au lieu de `rh`. Le document devient alors illisible pour tout le monde. C'est sans
danger, mais rien ne le signale.

Les autres clés de l'en-tête (source, date, thème…) sont conservées pour information.

Les fichiers doivent être enregistrés en UTF-8.

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
comptes de cette page.

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

Les utilisateurs de démonstration sont ceux du cahier des charges, `docs/cahier-des-charges.md`.
`alice` lit le groupe `tous`, `bruno` les groupes `tous` et `rh`, `claire` les groupes `tous` et
`direction`.

### `service-public-reduit/` : comment les 50 fiches ont été choisies

Le corpus réduit est un sous-ensemble figé du corpus complet : ce sont les mêmes fichiers, copiés sans
modification. Il réunit deux sortes de fiches.

D'abord 39 fiches retenues pour poser des questions sur le corpus.

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
