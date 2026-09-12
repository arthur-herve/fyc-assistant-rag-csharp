# Expérience « changement-embeddings » — 2026-09-12 19:47

Configuration `config/app-ollama.json` · 42 questions de `eval/questions-service-public.json` · corpus `service-public-reduit`.

Une seule chose change : le modèle d'embeddings, `bge-m3` → `nomic`. Même corpus, même découpage, même prompt, même générateur ; le seuil est celui configuré pour chaque modèle.

## 1. Sans réindexer : l'application refuse

```
L'index a été construit avec « ollama:bge-m3@790764642607 » (1024 dim.) mais le modèle d'embeddings actuel est « ollama:nomic-embed-text@0a109f422b47 » (768 dim.). Il faut réindexer le corpus.
```

## 2. Après réindexation

| Mesure | avant (`bge-m3`) | après (`nomic`) |
|---|---|---|
| modèle servi | ollama:bge-m3@790764642607 | ollama:nomic-embed-text@0a109f422b47 |
| dimension | 1024 | 768 |
| morceaux | 740 | 740 |
| indexation (s) | 15.90 | 8.40 |
| seuil | 0.65 | 0.73 |
| répond (répondables) | 0.97 | 0.94 |
| bonne source | 1.00 | 0.93 |
| refus justes (hors corpus) | 1.00 | 0.70 |
| non sourcé | 0.02 | 0.05 |
| fuites d'accès | 0.00 | 0.00 |

## Dérive

| Mesure | avant → après |
|---|---|
| questions comparées | 42 |
| réponses modifiées | 35 |
| taux de dérive | 0.83 |
| changements de statut | 6 |
| changements de sources | 3 |
| reformulations | 26 |

## Détail

```
Comparaison : avant → apres

Différences de configuration
  - embedding_model : bge-m3 → nomic
  - embedding_model_id : ollama:bge-m3@790764642607 → ollama:nomic-embed-text@0a109f422b47
  - index_id : de693eb424f2 → 5ebab885dc09
  - min_score : 0.65 → 0.73

Dérive
  questions comparées : 42
  réponses modifiées  : 35
  taux de dérive      : 83 %

| Nature | Nombre | Lecture |
|---|---|---|
| statut modifié | 6 | changement de comportement : refus devenu réponse, ou l'inverse |
| sources modifiées | 3 | même décision, autres documents cités |
| texte modifié | 26 | mêmes sources, même décision : reformulation, la dérive la plus bénigne |
| identique | 7 | rien n'a bougé |
| absente d'un des deux | 0 | question présente d'un seul côté |

sp-conges-acquis [sources modifiées]
  avant : answered [F18260] « Le salarié a droit à 2,5 jours ouvrables par mois de travail effectif, soit 30 jours ouvra »
  après : answered [F2931] « Le salarié acquiert des jours de congés payés équivalents à 1/12 de son temps de travail e »
sp-conges-report [sources modifiées]
  avant : answered [F12410] « Non, les congés payés non pris à la fin de la période ne sont pas nécessairement perdus. E »
  après : answered [F37482] « Oui, les congés payés non pris par le salarié à la fin de la période de 15 mois seront per »
sp-conges-fermeture [texte modifié]
  avant : answered [F1016] « La durée de la fermeture annuelle de l'entreprise pour congés ne peut pas dépasser 24 jour »
  après : answered [F1016] « La fermeture annuelle de l'entreprise pour congés peut durer un maximum de 24 jours ouvrab »
sp-conges-cdd [texte modifié]
  avant : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, mais le calcul de ces congés est effectué »
  après : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, il bénéficie des mêmes droits à congés pa »
sp-conges-demission [statut modifié]
  avant : unsourced [F2265] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
  après : answered [F2265, F37482] « Non, vous ne pouvez pas démissionner pendant vos congés payés. Vous devez avertir votre em »
sp-conges-sans-solde [texte modifié]
  avant : answered [F10431] « Non, le congé sans solde n'est pas prévu par la législation. [4] 

L'employeur n'est pas o »
  après : answered [F10431] « Le congé sans solde n'est pas prévu par la législation, mais il peut être rémunéré dans le »
sp-conges-proche-aidant [texte modifié]
  avant : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
  après : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
sp-temps-partiel-min [texte modifié]
  avant : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures pa »
  après : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures, s »
sp-duree-legale [sources modifiées]
  avant : answered [F1911] « La durée légale du travail pour un temps complet du salarié du secteur privé est fixée à 3 »
  après : answered [F1911, F32428] « La durée légale du travail par semaine pour un temps plein est de 35 heures [2, 3]. »
sp-duree-max-jour [texte modifié]
  avant : answered [F1911] « La durée de travail effectif ne doit pas dépasser la durée maximale de 10 heures par jour. »
  après : answered [F1911] « La durée de travail effectif ne doit pas dépasser la durée maximale de 10 heures par jour. »
sp-heures-sup [texte modifié]
  avant : answered [F2391] « Les huit premières heures supplémentaires de la semaine sont majorées de 25 % [1]. 

Ces h »
  après : answered [F2391] « Les huit premières heures supplémentaires de la semaine sont majorées de 25 % [1]. 

Ces h »
sp-dimanche [texte modifié]
  avant : answered [F13887] « Non, on n'est pas payé double quand on travaille le dimanche. La loi n'impose pas le paiem »
  après : answered [F13887] « Non, il n'est pas payé double quand on travaille le dimanche. [1] et [2] mentionnent que l »
sp-teletravail-volontaire [texte modifié]
  avant : answered [F13851] « Non, votre employeur ne peut pas vous imposer le télétravail sans votre accord. En effet,  »
  après : answered [F13851] « Non, le salarié peut refuser le passage en télétravail [2]. 

L'employeur doit fournir, in »
sp-paternite [texte modifié]
  avant : answered [F3156] « Le congé de paternité et d'accueil de l'enfant dure 25 jours calendaires pour une naissanc »
  après : answered [F3156] « Le congé de paternité et d'accueil de l'enfant dure 25 jours calendaires, soit 32 jours en »
sp-naissance [texte modifié]
  avant : answered [F2266] « La durée du congé de naissance est fixée à 3 jours ouvrables pour chaque naissance survenu »
  après : answered [F2266] « Le congé de naissance dure 3 jours ouvrables pour chaque naissance survenue au foyer, sauf »
sp-maternite [texte modifié]
  avant : answered [F2265] « La durée du congé de maternité pour un premier enfant est de 16 semaines, composée de 6 se »
  après : answered [F2265] « La durée du congé de maternité pour un premier enfant est de 16 semaines, composée de 6 se »
sp-grossesse [texte modifié]
  avant : answered [F1144] « Une salariée enceinte n'a pas l'obligation d'informer son employeur de son état de grosses »
  après : answered [F1144] « Une salariée enceinte n'a pas l'obligation d'informer son employeur de son état de grosses »
sp-cpf-montant [texte modifié]
  avant : answered [F10705] « Selon les passages fournis, l'alimentation du compte personnel de formation (CPF) d'un sal »
  après : answered [F10705] « Selon les informations fournies, votre compte personnel de formation (CPF) est alimenté à  »
sp-stage-gratification [texte modifié]
  avant : answered [F16734] « La gratification est obligatoire à partir de 2 mois consécutifs de stage, soit 44 jours à  »
  après : answered [F16734] « La gratification est obligatoire à partir de la 309e heure de stage même s'il est effectué »
sp-pmsmp [texte modifié]
  avant : answered [F14102] « Une période de mise en situation en milieu professionnel (PMSMP) vous permet de tester vos »
  après : answered [F14102] « Une période de mise en situation en milieu professionnel (PMSMP) vous permet de tester vos »
sp-arret-maladie-sorties [texte modifié]
  avant : answered [F12415] « Vous devez être présent à votre domicile de 9 h à 11 h et de 14 h à 16 h, y compris les sa »
  après : answered [F12415] « Pendant un arrêt maladie, vous devez être présent à votre domicile de 9 h à 11 h et de 14  »
sp-droit-retrait [texte modifié]
  avant : answered [F1136] « Oui, si un salarié pense avoir un motif raisonnable de croire à un danger possible, il peu »
  après : answered [F1136] « Un salarié peut refuser de travailler s'il pense être exposé à une situation dangereuse, m »
sp-titres-restaurant [texte modifié]
  avant : answered [F21059] « L'employeur doit financer entre 50 % et 60 % de la valeur du titre-restaurant, tandis que  »
  après : answered [F21059] « L'employeur doit financer entre 50 % et 60 % de la valeur du titre-restaurant, tandis que  »
sp-retraite-age [texte modifié]
  avant : answered [F14043] « Un salarié né en 1970 peut partir en retraite à partir de l'âge de 67 ans [1]. »
  après : answered [F14043] « Selon le passage [1], un salarié né en 1970 peut partir en retraite à partir de 64 ans, ca »
sp-rh-age-minimum [statut modifié]
  avant : answered [F1649] « Il est possible de travailler dès l'âge de 16 ans, quel que soit le type de contrat (CDI,  »
  après : no_relevant_source [] « Je n'ai trouvé aucun document accessible qui réponde à cette question. »
sp-rh-abandon-poste [texte modifié]
  avant : answered [F31209] « Un salarié qui abandonne son poste dans le secteur privé doit reprendre son poste dans un  »
  après : answered [F31209] « Le salarié doit reprendre son poste dans un délai de 15 jours calendaires après la récepti »
sp-rh-faute-simple [texte modifié]
  avant : answered [F1137] « Oui, le salarié perçoit une indemnité de licenciement s'il remplit les conditions d'ancien »
  après : answered [F1137] « Oui, le salarié perçoit une indemnité de licenciement s'il remplit les conditions d'ancien »
sp-rh-promesse [texte modifié]
  avant : answered [F10403] « Si l'employeur ne respecte pas une promesse d'embauche unilatérale, il peut être considéré »
  après : answered [F10403] « L'employeur qui ne respecte pas une promesse d'embauche unilatérale peut être assimilé à u »
sp-rh-essai-renouvellement [texte modifié]
  avant : answered [F13919] « Non, la période d'essai d'un CDI n'est pas renouvelable, car il n'y a pas de mention de re »
  après : answered [F13919] « La période d'essai d'un CDI est renouvelable 1 fois, mais la durée maximale totale de la p »
sp-dir-greve [statut modifié]
  avant : answered [F117] « Une grève doit réunir les 3 conditions suivantes : arrêt total du travail, arrêt collectif »
  après : unsourced [F117] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
sp-dir-prudhommes [texte modifié]
  avant : answered [F1052] « Une affaire devant le conseil de prud'hommes commence par une requête auprès du conseil de »
  après : answered [F1052] « Une affaire devant le conseil de prud'hommes commence par la saisine du conseil de prud'ho »
sp-dir-inspection [texte modifié]
  avant : answered [F107] « Les missions de l'inspection du travail sont les suivantes : contrôler, conseiller, concil »
  après : answered [F107] « Les missions de l'inspection du travail sont les suivantes : contrôler, conseiller, concil »
sp-acces-abandon-refuse [statut modifié]
  avant : no_relevant_source [] « Je n'ai trouvé aucun document accessible qui réponde à cette question. »
  après : answered [F1915, F32428] « Il n'y a pas de réponse claire dans les passages fournis. Le passage [1] mentionne que le  »
sp-hors-difficile-fonction-publique [statut modifié]
  avant : no_relevant_source [] « Je n'ai trouvé aucun document accessible qui réponde à cette question. »
  après : unsourced [F3156] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
sp-hors-difficile-impots [statut modifié]
  avant : no_relevant_source [] « Je n'ai trouvé aucun document accessible qui réponde à cette question. »
  après : answered [F2391] « Les heures supplémentaires d'un salarié du secteur privé sont exonérées de l'impôt sur le  »
```

## Lecture

- Changer de modèle d'embeddings coûte une réindexation complète (voir la durée) : les vecteurs stockés vivent dans l'espace du modèle qui les a produits.
- Le refus est explicite parce que le service IA renvoie l'identifiant concret du modèle et que l'application le compare au manifeste à chaque question (ADR 0003). Sans cela, à dimension égale, l'index aurait répondu à côté sans rien signaler.
- Après réindexation, le générateur n'a pas changé et pourtant les réponses bougent : il ne répond qu'à partir de ce que la recherche lui donne.
