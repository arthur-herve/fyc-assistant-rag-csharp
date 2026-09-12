# Corpus documentaires

Trois corpus, choisis par `"corpus": {"directory": …}` dans `config/app*.json` (fichiers, licence et simulation des droits identiques à la version Python).

| Dossier | Contenu | Usage |
|---|---|---|
| `solveo/` | 9 documents **fictifs** (entreprise Solvéo : RH et informatique), dont 3 à accès restreint | tests automatisés, démarrage hors-ligne, exercices courts (`config/app.json`) |
| `service-public-reduit/` | **50 fiches réelles** de Service-Public.gouv.fr, sous-ensemble du corpus complet : le corpus du cours, « quelques dizaines de documents » (cahier des charges S2.1), indexé en quelques secondes sans GPU | fil rouge avec de vrais modèles, exercices, banc de calibration (`config/app-ollama.json`) |
| `service-public/` | 322 fiches **réelles**, thème « Travail - Formation », secteur privé | expériences à l'échelle (découpage, changement de modèle, stabilité) et mesure de ce que le passage de 50 à 322 documents change (`config/app-ollama-complet.json`) |

Les deux corpus Service-Public partagent les jeux de questions `eval/questions-service-public*.json` :
toutes les fiches qu'ils attendent sont dans le corpus réduit, et les questions « hors corpus » le
restent dans les deux. Un seuil de pertinence calibré sur l'un ne vaut pas pour l'autre (voir
`config/app-ollama*.json`) : c'est un des effets CACE que le cours mesure.

Format commun : un fichier Markdown par document, avec un en-tête entre deux lignes `---`
lu par `src/Assistant.Infrastructure/MarkdownCorpus.cs` :

```markdown
---
id: identifiant-unique
titre: Titre du document
groupes: tous            # ou : rh, direction, … (séparés par des virgules)
---
Texte du document…
```

Un document sans `groupes` est lisible par tous. Les autres clés de l'en-tête (source, date,
thème…) sont conservées à titre documentaire et ignorées par l'application.

> Un dossier de corpus ne contient que des documents : tout fichier `.md` doit porter l'en-tête, c'est pourquoi l'attribution ci-dessous n'est pas dans `service-public/`.

## `service-public/` : fiches Service-Public.gouv.fr, thème « Travail - Formation », secteur privé

### Source et licence

- **Source** : « Fiches pratiques et ressources de Service-Public.gouv.fr — Particuliers »,
  Direction de l'information légale et administrative (DILA), publiées sur data.gouv.fr :
  <https://www.data.gouv.fr/datasets/fiches-pratiques-et-ressources-de-service-public-gouv-fr-particuliers>
- **Fichier** : `vosdroits-latest.zip` (flux XML, schéma 3.5), téléchargé le **11 septembre 2026** depuis
  <https://lecomarquage.service-public.gouv.fr/vdd/3.5/part/zip/vosdroits-latest.zip>.
  Date de dernière modification des fiches retenues : 2026-09-11.
- **Licence** : Licence Ouverte / Open Licence 2.0 (Etalab). Mention obligatoire :
  *Service-Public.gouv.fr / DILA*. Chaque fiche porte dans son en-tête l'URL de la fiche
  d'origine (`source`) et sa date de modification (`date`).
- Les fiches sont converties en Markdown par `tools/import_service_public.py` de la version Python
  ([https://github.com/arthur-herve/fyc-assistant-rag-python-full](https://github.com/arthur-herve/fyc-assistant-rag-python-full), bibliothèque standard). La conversion conserve le texte, les titres, les listes, les tableaux
  et les encadrés ; elle écarte les contacts, services en ligne, références légales et renvois.
  **Le contenu n'est pas modifié.** Pour une version à jour : depuis le dépôt Python, `python tools/import_service_public.py --download`, puis copier `corpus/service-public/` ici.

### Périmètre

322 fiches du thème « Travail - Formation », limitées aux dossiers qui concernent un salarié du
secteur privé (les dossiers « fonction publique » et « particulier employeur » sont écartés).

### Droits d'accès : une simulation

Ces fiches sont publiques. Le fil rouge du cours joue un **intranet d'entreprise** où l'assistant
ne montre à chaque salarié que ce qu'il a le droit de lire : les droits ci-dessous sont donc une
règle pédagogique, attribuée par dossier dans `tools/import_service_public.py` de la version Python (tableau `DOSSIERS`).

| Groupe | Dossiers | Fiches |
|---|---|---|
| `tous` | Conditions de travail · Maladie ou accident du travail · Handicap et emploi · Congés · Contrats de travail · Contrats d'insertion · Retraite · Formation des salariés · Formation des personnes handicapées · Stage en entreprise · Temps de travail · Représentation du personnel | 248 |
| `rh` | Recrutement · Licenciement pour motif personnel · Licenciement économique · Rupture du contrat de travail | 61 |
| `direction` | Conflits du travail | 13 |

Utilisateurs de démonstration (`config/app-ollama.json`) : `alice` (tous), `bruno` (tous, rh),
`claire` (tous, direction).

### `service-public-reduit/` : comment les 50 fiches ont été choisies

Le corpus réduit est un sous-ensemble figé du corpus complet (mêmes fichiers, copiés sans
modification) : les **39 fiches** que citent les deux jeux de questions (`expected_documents`
et `forbidden_documents` de `eval/questions-service-public.json` et
`eval/questions-service-public-validation.json`), plus **11 fiches de diversion** prises dans
les dossiers que les questions ne couvrent pas (représentation du personnel, contrats de
travail, licenciement économique, handicap, retraite…), pour que la recherche ait de quoi se
tromper. Répartition : 38 `tous`, 8 `rh`, 4 `direction` ; 17 dossiers représentés. La liste est
dans [`LISTE-service-public-reduit.md`](LISTE-service-public-reduit.md), à côté de ce README et
hors du dossier de corpus, parce qu'un dossier de corpus ne contient que des documents.

### Ce que cette conversion ne fait pas

Les fiches renvoient souvent à d'autres fiches, à des simulateurs ou à des textes de loi : ces liens
sont retirés, une réponse de l'assistant peut donc paraître incomplète par rapport au site. Les
montants et délais sont ceux de la date de téléchargement ; ils changent. Pour toute décision
réelle, consulter <https://www.service-public.gouv.fr>.
