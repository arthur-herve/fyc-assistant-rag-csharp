# ADR 0006 — Les droits d'accès sont filtrés avant le prompt, jamais confiés au modèle

Date : 11/09/2026

Statut : acceptée. Reprise dans la version C# le 12/09/2026, avec des références de fichiers et des
mesures adaptées. Un erratum du 21/09/2026 figure en fin de document.

## Contexte

Un assistant qui répond à partir de documents internes ne doit montrer à un salarié que ce qu'il a
le droit de lire. Demander au modèle de « ne pas révéler » un document revient à lui donner une
consigne, et une consigne n'est pas une garantie.

## Décision

La règle d'accès, `AccessPolicy.CanRead(user, chunk)`, vit dans le domaine. Elle est appliquée comme
prédicat de la recherche : un morceau que l'utilisateur n'a pas le droit de lire n'entre ni dans le
classement, ni dans le prompt, ni dans la trace.

Chaque document porte les groupes autorisés à le lire, et ses morceaux en héritent. Ces groupes
entrent dans l'empreinte du corpus. Changer un droit revient donc à modifier le corpus, et il faut
alors réindexer.

## Conséquences

- Les bancs Solvéo et Service-Public du 11/09/2026 n'ont relevé aucune fuite sur 936 appels, pas
  plus que les bancs qwen3 et de validation.
- La règle vit dans le domaine, et elle se teste sans IA ni réseau.
- Une nuance à enseigner : filtrer avant le prompt garantit la confidentialité, **pas le silence**.
  Une fiche publique voisine peut très bien donner une réponse partielle à une question dont la
  vraie réponse est réservée.
- Coût : le filtrage se fait après coup, sur un top-k calculé sur tout l'index. Ce top-k est donc
  parfois appauvri, parce que des documents interdits y ont pris des places avant d'être retirés.
  Le pré-filtrage côté index, écarté par l'ADR 0002, éviterait cela, mais au prix d'une règle
  métier placée dans un composant technique.

## Écarté

Compter sur le prompt, avec une consigne comme « ne cite pas les documents RH », ou se contenter de
vérifier les citations après coup. Dans les deux cas, le modèle aurait vu le contenu interdit.

## Erratum (21/09/2026)

Le point « Coût » ci-dessus est faux. L'index applique le prédicat d'accès avant de trier les
passages et de couper le top-k (`InMemoryVectorIndex.Search`) : c'est bien un pré-filtrage. Un
passage interdit ne prend donc jamais de place dans le top-k, et le test
`Forbidden_passages_take_no_place_in_the_top_k` le vérifie. Ce pré-filtrage ne met pas non plus de
règle métier dans un composant technique. La règle reste dans le domaine (`AccessPolicy`), et
l'index ne reçoit qu'un prédicat.

La mesure de fuites citée plus haut vient de la version Python. Pour cette version-ci, le banc du
12/09/2026 ne relève aucune fuite sur 84 appels (`eval/resultats/README.md`).
