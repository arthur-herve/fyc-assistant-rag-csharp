# Expérience « cace-decoupage » — 2026-09-12 19:56

Configuration `config/app-ollama.json` · 42 questions de `eval/questions-service-public.json` · corpus `service-public-reduit`.

Une seule chose change : la taille des morceaux. Même corpus, même modèle d'embeddings, même seuil, même prompt, même générateur.

## Les deux index

| Mesure | avant (800 / 120) | après (300 / 50) |
|---|---|---|
| morceaux | 740 | 2188 |
| indexation (s) | 15.80 | 39.40 |
| identifiant de l'index | de693eb424f2 | 7fa313af1493 |
| répond (répondables) | 0.97 | 0.97 |
| bonne source | 1.00 | 0.97 |
| refus justes (hors corpus) | 1.00 | 1.00 |
| non sourcé | 0.02 | 0.00 |
| fuites d'accès | 0.00 | 0.00 |

## Dérive

| Mesure | avant → après |
|---|---|
| questions comparées | 42 |
| réponses modifiées | 32 |
| taux de dérive | 0.76 |
| changements de statut | 2 |
| changements de sources | 3 |
| reformulations | 27 |

## Détail

```
Comparaison : avant → apres

Différences de configuration
  - index_id : de693eb424f2 → 7fa313af1493
  - splitter : {include_title=true, max_chars=800, overlap_chars=120, type=paragraph} → {include_title=true, max_chars=300, overlap_chars=50, type=paragraph}

Dérive
  questions comparées : 42
  réponses modifiées  : 32
  taux de dérive      : 76 %

| Nature | Nombre | Lecture |
|---|---|---|
| statut modifié | 2 | changement de comportement : refus devenu réponse, ou l'inverse |
| sources modifiées | 3 | même décision, autres documents cités |
| texte modifié | 27 | mêmes sources, même décision : reformulation, la dérive la plus bénigne |
| identique | 10 | rien n'a bougé |
| absente d'un des deux | 0 | question présente d'un seul côté |

sp-conges-acquis [texte modifié]
  avant : answered [F18260] « Selon l'article [1], un salarié a droit à 2,5 jours ouvrables par mois de travail effectif »
  après : answered [F18260] « Selon les passages, le salarié acquiert 24 jours ouvrables de congés payés par mois de tra »
sp-conges-report [sources modifiées]
  avant : answered [F12410] « Non, les congés payés non pris à la fin de la période de prise ne sont pas nécessairement  »
  après : answered [F12410, F37482] « Non, vos congés payés non pris à la fin de la période ne sont pas perdus [1]. 

En effet,  »
sp-conges-fermeture [texte modifié]
  avant : answered [F1016] « La fermeture annuelle de l'entreprise pour congés peut durer un maximum de 24 jours ouvrab »
  après : answered [F1016] « La durée de la fermeture annuelle de l'entreprise pour congés ne peut pas dépasser 24 jour »
sp-conges-cdd [texte modifié]
  avant : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, mais le montant est calculé en fonction d »
  après : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, mais le calcul de ces congés est effectué »
sp-conges-demission [statut modifié]
  avant : unsourced [F2265] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
  après : answered [F2265] « Non, vous ne pouvez pas démissionner pendant vos congés payés. C'est une disposition légal »
sp-conges-sans-solde [texte modifié]
  avant : answered [F10431] « Le congé sans solde n'est pas prévu par la législation, mais il peut être accordé par l'em »
  après : answered [F10431] « Le congé sans solde n'est pas prévu par la législation [2]. Il n'existe pas de formalisme  »
sp-conges-proche-aidant [texte modifié]
  avant : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
  après : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
sp-temps-partiel-min [sources modifiées]
  avant : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures, s »
  après : answered [F1915] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures pa »
sp-duree-legale [texte modifié]
  avant : answered [F1911] « La durée légale du travail pour un temps complet du salarié du secteur privé est fixée à 3 »
  après : answered [F1911] « La durée légale du travail pour un temps complet du salarié du secteur privé est fixée à 3 »
sp-duree-max-jour [texte modifié]
  avant : answered [F1911] « La durée de travail effectif ne doit pas dépasser la durée maximale de 10 heures par jour. »
  après : answered [F1911] « La durée de travail effectif maximale par jour est de 10 heures, sauf en cas d'activité ac »
sp-heures-sup [texte modifié]
  avant : answered [F2391] « Les huit premières heures supplémentaires de la semaine sont majorées de 25 % [1]. 

Ces h »
  après : answered [F2391] « La majoration s'applique sur le montant brut du salaire et pour les 8 premières heures sup »
sp-dimanche [texte modifié]
  avant : answered [F13887] « Non, on n'est pas payé double quand on travaille le dimanche. La loi n'impose pas le paiem »
  après : answered [F13887] « Non, on ne travaille pas le dimanche, mais si on travaille le dimanche, on est payé au moi »
sp-teletravail-volontaire [texte modifié]
  avant : answered [F13851] « Non, l'employeur ne peut pas imposer le télétravail à un salarié sans son accord, à moins  »
  après : answered [F13851] « Non, votre employeur ne peut pas vous imposer le télétravail sans votre accord, à l'except »
sp-paternite [texte modifié]
  avant : answered [F3156] « Le congé de paternité et d'accueil de l'enfant dure 25 jours calendaires pour une naissanc »
  après : answered [F3156] « Le congé de paternité et d'accueil de l'enfant dure 25 jours calendaires. [3] »
sp-naissance [sources modifiées]
  avant : answered [F2266] « La durée du congé de naissance est fixée à 3 jours ouvrables pour chaque naissance survenu »
  après : answered [F12647] « Le congé de naissance dure 3 jours ouvrables. Cependant, il est possible que la durée soit »
sp-maternite [texte modifié]
  avant : answered [F2265] « La durée du congé de maternité pour un premier enfant est de 16 semaines, composée de 6 se »
  après : answered [F2265] « La durée du congé de maternité pour un premier enfant est de 16 semaines, composée de 6 se »
sp-grossesse [texte modifié]
  avant : answered [F1144] « Une salariée enceinte n'a pas l'obligation d'informer son employeur de son état de grosses »
  après : answered [F1144] « Non, une salariée enceinte n'a pas l'obligation d'informer son employeur de son état de gr »
sp-cpf-montant [texte modifié]
  avant : answered [F10705] « Selon les informations fournies, votre compte personnel de formation (CPF) est alimenté de »
  après : answered [F10705] « Selon les informations fournies, votre compte personnel de formation est alimenté à hauteu »
sp-stage-gratification [texte modifié]
  avant : answered [F16734] « La gratification est obligatoire à partir de 2 mois consécutifs de stage, soit 44 jours à  »
  après : answered [F16734] « La gratification est obligatoire à partir de la 309e heure de stage, même si le stage est  »
sp-pmsmp [texte modifié]
  avant : answered [F14102] « Une période de mise en situation en milieu professionnel (PMSMP) sert à tester vos choix d »
  après : answered [F14102] « Une période de mise en situation en milieu professionnel (PMSMP) vous permet de tester vos »
sp-arret-maladie-sorties [texte modifié]
  avant : answered [F12415] « Pour un salarié en arrêt maladie, il est obligatoire d'être présent à son domicile de 9 h  »
  après : answered [F12415] « Selon les règles établies, vous devez être présent à votre domicile de 9 h à 11 h et de 14 »
sp-droit-retrait [texte modifié]
  avant : answered [F1136] « Oui, si un salarié pense avoir un motif raisonnable de croire à un danger possible, il peu »
  après : answered [F1136] « Non, le salarié qui exerce son droit de retrait de manière justifiée ne peut pas être sanc »
sp-titres-restaurant [texte modifié]
  avant : answered [F21059] « L'employeur doit financer entre 50 % et 60 % de la valeur du titre-restaurant, tandis que  »
  après : answered [F21059] « L'employeur doit financer entre 50 % et 60 % de la valeur du titre-restaurant. Ce montant  »
sp-retraite-age [texte modifié]
  avant : answered [F14043] « Un salarié né en 1970 peut partir en retraite à partir de l'âge de 67 ans [1]. »
  après : answered [F14043] « Un salarié né en 1970 peut partir en retraite à partir de 67 ans [3]. 

Si le salarié a en »
sp-rh-age-minimum [texte modifié]
  avant : answered [F1649] « Un jeune peut être embauché à partir de 16 ans, sauf pour les activités interdites avant 1 »
  après : answered [F1649] « On peut embaucher un jeune dès l'âge de 16 ans, quel que soit le type de contrat. Tout min »
sp-rh-abandon-poste [texte modifié]
  avant : answered [F31209] « Un salarié qui abandonne son poste doit reprendre son travail dans un délai de 15 jours ca »
  après : answered [F31209] « Selon les règles en vigueur, un salarié doit avoir au moins 15 jours calendaires pour repr »
sp-rh-faute-simple [texte modifié]
  avant : answered [F1137] « Oui, le salarié perçoit une indemnité de licenciement s'il remplit les conditions d'ancien »
  après : answered [F1137] « Non, le salarié ne perçoit pas d'indemnité de licenciement s'il est licencié pour faute si »
sp-rh-promesse [texte modifié]
  avant : answered [F10403] « Si l'employeur ne respecte pas une promesse d'embauche unilatérale, il peut être considéré »
  après : answered [F10403] « L'employeur qui ne respecte pas une promesse d'embauche unilatérale peut être condamné à v »
sp-rh-essai-renouvellement [texte modifié]
  avant : answered [F13919] « La période d'essai d'un CDI peut être renouvelée si les 3 conditions suivantes sont réunie »
  après : answered [F13919] « Non, la période d'essai d'un CDI n'est pas renouvelable. 

La durée maximale totale de la  »
sp-dir-greve [statut modifié]
  avant : answered [F117] « Une grève doit réunir les 3 conditions suivantes pour être licite : arrêt total du travail »
  après : no_relevant_source [] « Je n'ai trouvé aucun document accessible qui réponde à cette question. »
sp-dir-prudhommes [texte modifié]
  avant : answered [F1052] « Une affaire devant le conseil de prud'hommes commence par une requête auprès du CPH, qui e »
  après : answered [F1052] « Une affaire devant le conseil de prud'hommes commence par une saisine du conseil de prud'h »
sp-dir-inspection [texte modifié]
  avant : answered [F107] « Les missions de l'inspection du travail sont les suivantes : contrôler, conseiller, concil »
  après : answered [F107] « Les missions de l'inspection du travail sont les suivantes : contrôler, conseiller et conc »
```

## Lecture

- L'identifiant de l'index change : le découpage fait partie de ce qui définit un index (manifeste), au même titre que le corpus et le modèle.
- Le seuil de pertinence n'a pas été recalibré : des morceaux plus courts donnent des scores différents, donc des refus et des réponses qui bougent sans qu'aucune règle métier n'ait changé. C'est le principe CACE : *changing anything changes everything*.
- Ce que le taux de dérive ne dit pas : laquelle des deux versions répond le mieux. Pour cela, regarder « bonne source » et « refus justes » ci-dessus, question par question.
