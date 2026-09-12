# Expérience « changement-generateur » — 2026-09-12 19:51

Configuration `config/app-ollama.json` · 12 questions de `eval/questions-service-public.json` · corpus `service-public-reduit`.

Une seule chose change : le modèle de génération, `llama3-2-3b` → `qwen3-4b`. L'index `de693eb424f2` est construit une fois et partagé : **il n'est reconstruit à aucun moment**.

## Les deux générateurs

| Mesure | avant (`llama3-2-3b`) | après (`qwen3-4b`) |
|---|---|---|
| modèle servi | ollama:llama3.2:3b@a80c4f17acd5 | ollama:qwen3:4b@359d7dd4bcda |
| durée totale (s) | 13.50 | 277.20 |
| par question (s) | 1.10 | 23.10 |
| réponses non sourcées | 0 | 0 |
| répond (répondables) | 1.00 | 1.00 |
| bonne source | 0.92 | 0.92 |
| refus justes (hors corpus) | — | — |
| non sourcé | 0.00 | 0.00 |
| fuites d'accès | 0.00 | 0.00 |

## Dérive

| Mesure | avant → après |
|---|---|
| questions comparées | 12 |
| réponses modifiées | 12 |
| taux de dérive | 1.00 |
| changements de statut | 0 |
| changements de sources | 1 |
| reformulations | 11 |

## Détail

```
Comparaison : avant → apres

Différences de configuration
  - generation_model : llama3-2-3b → qwen3-4b
  - generation_model_id : ollama:llama3.2:3b@a80c4f17acd5 → ollama:qwen3:4b@359d7dd4bcda

Dérive
  questions comparées : 12
  réponses modifiées  : 12
  taux de dérive      : 100 %

| Nature | Nombre | Lecture |
|---|---|---|
| statut modifié | 0 | changement de comportement : refus devenu réponse, ou l'inverse |
| sources modifiées | 1 | même décision, autres documents cités |
| texte modifié | 11 | mêmes sources, même décision : reformulation, la dérive la plus bénigne |
| identique | 0 | rien n'a bougé |
| absente d'un des deux | 0 | question présente d'un seul côté |

sp-conges-acquis [sources modifiées]
  avant : answered [F18260] « Selon l'article [1], un salarié a droit à 2,5 jours ouvrables par mois de travail effectif »
  après : answered [F18260, F2931] « On acquiert 2,5 jours ouvrables de congés payés par mois de travail effectif [1, 2].  
Cet »
sp-conges-report [texte modifié]
  avant : answered [F12410] « Non, les congés payés non pris à la fin de la période de prise ne sont pas nécessairement  »
  après : answered [F12410] « Oui, mais uniquement en cas d'accord entre l'employeur et le salarié. [4] »
sp-conges-fermeture [texte modifié]
  avant : answered [F1016] « La durée de la fermeture annuelle de l'entreprise pour congés ne peut pas dépasser 24 jour »
  après : answered [F1016] « La durée de la fermeture annuelle de l'entreprise ne peut pas être supérieure à 24 jours o »
sp-conges-cdd [texte modifié]
  avant : answered [F2931] « Oui, le salarié en CDD a droit aux congés payés, mais le calcul de ces congés est effectué »
  après : answered [F2931] « Oui, un salarié en CDD a droit à des congés payés, comme un salarié en CDI, avec un calcul »
sp-conges-demission [texte modifié]
  avant : answered [F2265] « Non, vous ne pouvez pas démissionner pendant vos congés payés. Vous devez avertir votre em »
  après : answered [F2265] « Les passages fournis ne traitent pas de la démission pendant les congés payés, car ils se  »
sp-conges-sans-solde [texte modifié]
  avant : answered [F10431] « Non, le congé sans solde n'est pas prévu par la législation. [4] 

L'employeur n'est pas o »
  après : answered [F10431] « Non, le congé sans solde n'est pas prévu par la loi [1]. »
sp-conges-proche-aidant [texte modifié]
  avant : answered [F16920] « Le congé de proche aidant ne peut pas dépasser 1 an sur l'ensemble de la carrière du salar »
  après : answered [F16920] « La durée maximale du congé de proche aidant sur l'ensemble de la carrière du salarié ne pe »
sp-temps-partiel-min [texte modifié]
  avant : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures pa »
  après : answered [F1915, F32428] « La durée minimale hebdomadaire de travail d'un salarié à temps partiel est de 24 heures pa »
sp-duree-legale [texte modifié]
  avant : answered [F1911] « La durée légale de travail par semaine pour un temps plein est de 35 heures. [2] »
  après : answered [F1911] « La durée légale du travail par semaine pour un temps plein est de 35 heures [1]. »
sp-duree-max-jour [texte modifié]
  avant : answered [F1911] « La durée de travail effectif ne doit pas dépasser la durée maximale de 10 heures par jour. »
  après : answered [F1911] « La durée maximale de travail effectif par jour est de 10 heures. [1,4]  
Des dérogations p »
sp-heures-sup [texte modifié]
  avant : answered [F2391] « Les huit premières heures supplémentaires de la semaine sont majorées de 25 % [1]. 

La ma »
  après : answered [F2391] « 25 % [1] »
sp-dimanche [texte modifié]
  avant : answered [F13887] « Non, on n'est pas payé double quand on travaille le dimanche. La loi n'impose pas le paiem »
  après : answered [F13887] « Non, la loi ne nécessite pas de paiement double lorsqu'un salarié travaille le dimanche [1 »
```

## Lecture

- Aucune donnée stockée n'a changé : même index, même manifeste. « Le générateur est un détail » est vrai pour les données.
- Mais les textes, la latence et le nombre de sorties rejetées par le garde-fou de forme changent : le prompt est réglé pour un modèle, chaque modèle a ses manies (longueur, langue, raisonnement). Une ligne de configuration, oui ; sans conséquence, non.
