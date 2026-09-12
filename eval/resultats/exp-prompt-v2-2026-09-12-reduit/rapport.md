# Expérience « prompt-v2 » — 2026-09-12 19:49

Configuration `config/app-ollama.json` · 42 questions de `eval/questions-service-public.json` · corpus `service-public-reduit`.

Une seule chose change : le prompt, `answer` → `answer-v2` (versions `v1+085b70e7` → `v2+1708960b`). Même index `de693eb424f2`, même générateur, même seuil.

## Les deux prompts

| Mesure | avant (`answer`) | après (`answer-v2`) |
|---|---|---|
| version tracée | v1+085b70e7 | v2+1708960b |
| longueur moyenne des réponses | 357 | 41 |
| répond (répondables) | 1.00 | 0.94 |
| bonne source | 0.97 | 0.97 |
| refus justes (hors corpus) | 1.00 | 1.00 |
| non sourcé | 0.00 | 0.05 |
| fuites d'accès | 0.00 | 0.00 |

## Dérive

| Mesure | avant → après |
|---|---|
| questions comparées | 42 |
| réponses modifiées | 31 |
| taux de dérive | 0.74 |
| changements de statut | 2 |
| changements de sources | 3 |
| reformulations | 26 |

## Détail

```
Comparaison : avant → apres

Différences de configuration
  - prompt : answer → answer-v2
  - prompt_version : v1+085b70e7 → v2+1708960b

Dérive
  questions comparées : 42
  réponses modifiées  : 31
  taux de dérive      : 74 %

| Nature | Nombre | Lecture |
|---|---|---|
| statut modifié | 2 | changement de comportement : refus devenu réponse, ou l'inverse |
| sources modifiées | 3 | même décision, autres documents cités |
| texte modifié | 26 | mêmes sources, même décision : reformulation, la dérive la plus bénigne |
| identique | 11 | rien n'a bougé |
| absente d'un des deux | 0 | question présente d'un seul côté |

sp-conges-acquis [sources modifiées]
  avant : answered [F18260, F2931, F37482] « Selon les règles établies, un salarié a droit à 2,5 jours ouvrables par mois de travail ef »
  après : answered [F18260] « 2,5 jours ouvrables par mois de travail effectif (soit 30 jours ouvrables de congés annuel »
sp-conges-report [texte modifié]
  avant : answered [F12410] « Non, les congés payés non pris à la fin de la période de prise ne sont pas automatiquement »
  après : answered [F12410] « Non [4]. »
sp-conges-fermeture [texte modifié]
  avant : answered [F1016] « La fermeture de l'entreprise pour congés annuels ne peut pas durer plus de 24 jours ouvrab »
  après : answered [F1016] « 24 jours ouvrables [1, 2, 3, 4] »
sp-conges-cdd [texte modifié]
  avant : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, mais le montant est calculé en fonction d »
  après : answered [F2931] « Oui [1, 2, 3] »
sp-conges-demission [texte modifié]
  avant : answered [F2265] « Non, vous ne pouvez pas démissionner pendant vos congés payés. Vous devez avertir votre em »
  après : answered [F2265] « Non [1] »
sp-conges-sans-solde [texte modifié]
  avant : answered [F10431] « Non, le congé sans solde n'est pas prévu par la législation. [4] 

L'employeur n'est pas o »
  après : answered [F10431] « Non [1, 2, 3, 4]. »
sp-conges-proche-aidant [texte modifié]
  avant : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
  après : answered [F16920] « 1 an. [1, 2] »
sp-temps-partiel-min [texte modifié]
  avant : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures. [ »
  après : answered [F1915, F32428] « 24 heures [1, 2, 3, 4] »
sp-duree-legale [texte modifié]
  avant : answered [F1911] « La durée légale de travail par semaine pour un temps plein est de 35 heures. [1]

La durée »
  après : answered [F1911] « 35 heures [1, 2]. »
sp-duree-max-jour [texte modifié]
  avant : answered [F1911] « La durée de travail effectif ne doit pas dépasser la durée maximale de 10 heures par jour. »
  après : answered [F1911] « 10 heures [1] »
sp-heures-sup [texte modifié]
  avant : answered [F2391] « Les huit premières heures supplémentaires de la semaine sont majorées de 25 % [1]. 

Ces h »
  après : answered [F2391] « 25 % [1] »
sp-dimanche [texte modifié]
  avant : answered [F13887] « Non, on n'est pas payé double quand on travaille le dimanche. La loi n'impose pas le paiem »
  après : answered [F13887] « Non [1, 2, 3, 4] »
sp-teletravail-volontaire [texte modifié]
  avant : answered [F13851] « Non, votre employeur ne peut pas vous imposer le télétravail sans votre accord. Cependant, »
  après : answered [F13851] « Non [1, 2, 3, 4]. »
sp-paternite [sources modifiées]
  avant : answered [F3156] « Le congé de paternité et d'accueil de l'enfant dure 25 jours calendaires, soit 2 périodes  »
  après : answered [F12647, F3156] « 25 jours calendaires [1, 2, 3, 4]. »
sp-naissance [texte modifié]
  avant : answered [F2266] « Le congé de naissance dure 3 jours ouvrables sauf si des dispositions conventionnelles le  »
  après : answered [F2266] « 3 jours ouvrables. [1] »
sp-maternite [texte modifié]
  avant : answered [F2265] « La durée du congé de maternité pour un premier enfant est de 16 semaines, composée de 6 se »
  après : answered [F2265] « 16 semaines [1] et [2]. »
sp-grossesse [sources modifiées]
  avant : answered [F1144] « Une salariée enceinte n'a pas l'obligation d'informer son employeur de son état de grosses »
  après : answered [F1144, F2265] « Non [1, 2, 3]. »
sp-cpf-montant [texte modifié]
  avant : answered [F10705] « Selon les informations fournies, le compte personnel de formation (CPF) d'un salarié du se »
  après : answered [F10705] « 800 € par année, jusqu'à atteindre 8 000 € maximum. [1], [2], [3], [4] »
sp-stage-gratification [texte modifié]
  avant : answered [F16734] « La gratification est obligatoire à partir de 2 mois consécutifs de stage, soit 44 jours à  »
  après : answered [F16734] « La gratification est obligatoire à partir de 2 mois consécutifs de stage. [1] »
sp-pmsmp [statut modifié]
  avant : answered [F14102] « Une période de mise en situation en milieu professionnel (PMSMP) vous permet de tester vos »
  après : unsourced [F14102] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
sp-arret-maladie-sorties [texte modifié]
  avant : answered [F12415] « Si votre arrêt de travail prévoit des sorties autorisées avec restriction d'horaires, vous »
  après : answered [F12415] « 9 h à 11 h et de 14 h à 16 h, y compris les samedis, dimanches et jours fériés [1, 2, 3]. »
sp-droit-retrait [texte modifié]
  avant : answered [F1136] « Oui, si un salarié pense avoir un motif raisonnable de croire à un danger possible, il peu »
  après : answered [F1136] « Non [1, 2, 3, 4]. »
sp-titres-restaurant [texte modifié]
  avant : answered [F21059] « L'employeur doit financer entre 50 % et 60 % de la valeur du titre-restaurant, tandis que  »
  après : answered [F21059] « Entre 50 % et 60 % de la valeur du titre-restaurant. [1] »
sp-retraite-age [statut modifié]
  avant : answered [F14043] « Un salarié né en 1970 peut partir en retraite à partir de l'âge de 67 ans [1]. »
  après : unsourced [F14043] « Je n'ai pas pu produire de réponse correctement sourcée. Consultez directement les passage »
sp-rh-age-minimum [texte modifié]
  avant : answered [F1649] « Il est possible d'embaucher un jeune dès l'âge de 16 ans, sauf pour effectuer certains tra »
  après : answered [F1649] « 16 ans [1, 3] »
sp-rh-abandon-poste [texte modifié]
  avant : answered [F31209] « Un salarié qui abandonne son poste doit reprendre son travail dans un délai de 15 jours ca »
  après : answered [F31209] « 15 jours calendaires [1]. »
sp-rh-faute-simple [texte modifié]
  avant : answered [F1137] « Oui, le salarié perçoit une indemnité de licenciement s'il remplit les conditions d'ancien »
  après : answered [F1137] « Oui [1, 2] »
sp-rh-promesse [texte modifié]
  avant : answered [F10403] « Si l'employeur ne respecte pas une promesse d'embauche unilatérale, il peut être considéré »
  après : answered [F10403] « Le juge peut condamner l'employeur à vous verser des dommages et intérêts en raison du pré »
sp-rh-essai-renouvellement [texte modifié]
  avant : answered [F13919] « Non, la période d'essai d'un CDI ne peut pas être renouvelée, sauf si les 3 conditions sui »
  après : answered [F13919] « Non [1, 2, 3, 4]. »
sp-dir-greve [texte modifié]
  avant : answered [F117] « Une grève doit réunir les 3 conditions suivantes pour être licite : arrêt total du travail »
  après : answered [F117] « Une grève doit réunir les 3 conditions suivantes : arrêt total du travail, arrêt collectif »
sp-dir-prudhommes [texte modifié]
  avant : answered [F1052] « Une affaire devant le conseil de prud'hommes commence par une requête auprès du conseil de »
  après : answered [F1052] « La phase de conciliation. [1, 2] »
```

## Lecture

- La version du prompt (déclarée + empreinte du contenu) est dans chaque trace : la dérive est attribuable à cette seule modification.
- Ce que le prompt change (forme, longueur, ton) n'est pas ce que le domaine garantit (citations vérifiées, forme validée, droits filtrés) : c'est ce qui permet de le traiter comme une configuration *surveillée comme du métier* (ADR 0005).
- Avec `extractive` (hors-ligne), 0 % de dérive : ce générateur ignore les consignes. Un modèle de langage, lui, les suit — et c'est précisément ce qui rend le prompt sensible.
