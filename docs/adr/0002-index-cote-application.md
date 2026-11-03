# ADR 0002 — L'index vectoriel vit côté application, pas côté service IA

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées. Un erratum du 21/09/2026 figure en fin de document.

## Contexte

Un membre de l'équipe avait auparavant exploré une autre piste, `AssistantQR`, où l'index vivait dans
le service Python. Ce service exposait `/index/reset`, `/index/upsert` et `/index/search`, cette
dernière route acceptant un filtre d'accès. Il fallait choisir où placer l'index.

## Décision

L'index est un adaptateur de l'application, placé derrière le port `IVectorIndex` et enregistré dans
les fichiers `data/index*.json`. Le service IA ne fait que deux choses : produire des vecteurs et
produire du texte.

## Conséquences

- La règle métier qui filtre selon les droits d'accès reste dans le domaine (`AccessPolicy`). Elle
  s'applique sous la forme d'un prédicat passé à la recherche, ce qui permet de la tester sans réseau
  (ADR 0006).
- Le manifeste de l'index (`IndexManifest`) est une donnée de l'application. Il décrit l'empreinte du
  corpus, le découpage, le modèle concret et la date de construction. La commande `status` peut ainsi
  le confronter à l'état courant (S4.2).
- Le service IA reste sans état. On peut le redémarrer, le remplacer ou le partager entre plusieurs
  applications.
- En contrepartie, l'application doit envoyer tous les morceaux au service pour les vectoriser, puis
  faire elle-même une recherche exhaustive. En C#, celle-ci prend quelques dizaines de millisecondes
  pour 3 500 morceaux, et c'est l'appel d'embeddings qui coûte le plus. Cela suffit ici, mais c'est
  une limite à nommer en S4.3.

## Écarté

Placer l'index dans le service IA aurait réduit les transferts et permis de filtrer avant de retenir
le top-k (pré-filtrage). Mais la règle d'accès aurait migré dans un composant technique, hors de
portée des tests du domaine, et l'index serait devenu invisible pour l'application, qui en dépend
pourtant. Le dilemme entre pré-filtrage et post-filtrage reste une démonstration optionnelle.

## Erratum (21/09/2026)

Le pré-filtrage n'est pas réservé à un index placé dans le service IA. L'index de l'application le
pratique déjà : le cas d'usage lui transmet le prédicat d'accès du domaine (voir l'erratum de
l'ADR 0006). Des deux arguments opposés à l'index côté service, c'est donc le second qui reste
retenu : l'application dépendrait d'un index qu'elle ne voit pas.
