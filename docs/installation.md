# Guide d'installation pas à pas (.NET 8 et Python)

Le projet a besoin de deux environnements : .NET 8 pour l'application et Python 3.11 ou plus récent
pour le service IA. C'est un choix voulu. Les modèles vivent dans un autre programme, écrit dans un
autre langage, et l'application ne leur parle que par HTTP.

Comptez une vingtaine de minutes pour tout ce qui fonctionne hors-ligne. Ollama, qui fait tourner de
vrais modèles sur votre machine, est facultatif : le mode hors-ligne suffit pour suivre tout le cours.

Aucune carte graphique n'est nécessaire. Un portable avec 8 Go de RAM suffit pour le mode hors-ligne.

## 1. Le SDK .NET 8 (5 min)

| Système | Comment | Vérification |
|---|---|---|
| Windows 10/11 | <https://dotnet.microsoft.com/download/dotnet/8.0>, puis « SDK 8.0.x » (installeur x64), ou `winget install Microsoft.DotNet.SDK.8` | `dotnet --version` affiche `8.0.…` |
| macOS | la même page (installeur .pkg, Arm64 ou x64 selon la puce), ou `brew install --cask dotnet-sdk@8` | `dotnet --version` |
| Linux (Debian/Ubuntu) | `sudo apt install dotnet-sdk-8.0` (dépôt Microsoft, ou Ubuntu 22.04 et plus) | `dotnet --version` |

`global.json` accepte aussi un SDK plus récent que le 8.0. Mais l'application et les tests visent
.NET 8, et il leur faut donc le runtime 8. Il est livré avec le SDK 8. Si vous n'avez qu'un SDK 9 ou
10, installez en plus le runtime 8, depuis la même page (« .NET Runtime 8.0.x »).

L'application n'utilise aucun paquet NuGet. Seul le projet de tests télécharge xUnit, une seule fois,
au premier `dotnet test`. Cela ne pèse que quelques mégaoctets.

## 2. Python 3.11 ou plus, pour le service IA (5 min)

| Système | Comment | Vérification |
|---|---|---|
| Windows 10/11 | <https://www.python.org/downloads/windows/>, puis « Windows installer (64-bit) ». Cochez **« Add python.exe to PATH »**. Ou bien `winget install Python.Python.3.12`. | `py --version` (ou `python --version`) |
| macOS | <https://www.python.org/downloads/macos/> ou `brew install python@3.12` | `python3 --version` |
| Linux (Debian/Ubuntu) | `sudo apt install python3` (3.11 ou plus sur Ubuntu 24.04 et Debian 12) | `python3 --version` |

Dans la suite de ce guide, `python` désigne `py` sous Windows et `python3` sous macOS et Linux.

Il n'y a aucune bibliothèque Python à installer pour suivre le cours : le service IA n'utilise que la
bibliothèque standard.

## 3. Git et le dépôt (5 min)

| Système | Comment |
|---|---|
| Windows | <https://git-scm.com/download/win> ou `winget install Git.Git` |
| macOS | `xcode-select --install` ou `brew install git` |
| Linux | `sudo apt install git` |

Récupérez le dépôt. Le clone vous place sur `main`, le point de départ du cours, où l'application
n'est pas encore écrite. Pour tester l'application, passez sur la branche de votre séquence, par
exemple `etape/s2.2`, la première qui la contient. Lancez ensuite les tests des deux programmes :

```bash
git clone https://github.com/arthur-herve/fyc-assistant-rag-csharp.git
cd fyc-assistant-rag-csharp
git switch etape/s2.2
dotnet test --nologo                                   # l'application
python -m unittest discover -s tests_python -t .       # le service IA et l'outil d'import du corpus
```

Les deux commandes doivent se terminer sans échec. La première fois, la commande compile tout : elle
est plus lente, c'est normal.

Si tout passe, les deux programmes sont prêts.

## 4. Le mode hors-ligne (2 min)

Ouvrez deux terminaux à la racine du dépôt.

Dans le premier, lancez le service IA et laissez-le tourner :

```bash
python -m ai_service
```

Dans le second, faites répondre l'exemple de la séquence 1.3 par le service :

```bash
dotnet run --project exemples/s1.3-transfert-naif -- generate extractive
```

Vous devez obtenir la phrase sur le télétravail, suivie de sa citation `[1]`.

Ce mode repose sur deux modèles très simples. `hashing` calcule des embeddings par hachage des mots,
sans rien avoir appris, et `extractive` recopie la phrase la plus proche de la question. Tous deux sont
déterministes et se passent du réseau.

`dotnet run` vérifie la compilation à chaque lancement, ce qui prend quelques secondes même quand il
n'y a rien à recompiler. Pour aller plus vite, lancez `dotnet build` une fois, puis ajoutez
`--no-build` juste après `dotnet run`.

Sous Windows, il arrive que les accents du service IA s'affichent mal dans le premier terminal. Tapez
alors `set PYTHONIOENCODING=utf-8` dans cmd, ou `$env:PYTHONIOENCODING = "utf-8"` dans PowerShell,
avant de relancer le service. L'application C#, elle, écrit toujours en UTF-8.

## 5. Ollama, si vous le pouvez

Ollama fait tourner des modèles ouverts sur votre machine et les rend accessibles à l'adresse
`http://127.0.0.1:11434`. Si son installation résiste, ne restez pas bloqué : avancez en mode
hors-ligne, et revenez-y plus tard.

| Système | Comment |
|---|---|
| Windows | <https://ollama.com/download/windows> ou `winget install Ollama.Ollama`. Un service démarre ensuite en arrière-plan. |
| macOS | <https://ollama.com/download/mac> ou `brew install ollama`, puis `ollama serve` |
| Linux | `curl -fsSL https://ollama.com/install.sh \| sh` |

Pour vérifier l'installation, lancez `ollama --version`, puis `ollama list`. Au début, la liste est
vide.

L'exemple de la séquence 1.3 peut alors appeler un vrai modèle. Téléchargez-le, puis posez-lui la
question, le service IA toujours lancé dans le premier terminal :

```bash
ollama pull llama3.2:3b
dotnet run --project exemples/s1.3-transfert-naif -- generate llama3-2-3b
```

Le service IA connaît ce modèle sous l'alias `llama3-2-3b`, déclaré dans `config/ai_service.toml`.

## Ce qui peut coincer

| Symptôme | Cause probable | Que faire |
|---|---|---|
| `Erreur : … (127.0.0.1:8100)`, avec un message de connexion refusée | le service IA ne tourne pas dans le premier terminal | lancez `python -m ai_service` |
| `Erreur : service IA : … Ollama est-il lancé sur http://127.0.0.1:11434 ?` | Ollama est arrêté | `ollama serve` |
| `Erreur : service IA : empreinte de llama3.2:3b introuvable …` | le modèle n'est pas téléchargé | `ollama pull llama3.2:3b` |
| `dotnet : commande introuvable` | SDK non installé, ou PATH pas encore mis à jour | rouvrez le terminal après l'installation, puis vérifiez avec `dotnet --info` |
| `NETSDK1045`, ou un message sur la « version du SDK » | SDK plus ancien que 8.0 | installez le SDK 8.0 (étape 1) |
| « You must install or update .NET to run this application » (`Microsoft.NETCore.App` 8.0) | un SDK 9 ou 10 seul, sans le runtime 8 | installez le runtime .NET 8 (étape 1) |
| `python : commande introuvable` sous Windows | PATH non mis à jour pendant l'installation | utilisez `py`, ou réinstallez Python en cochant « Add python.exe to PATH » |

## Machine de référence du cours

Les durées de cette page ont été mesurées sur une machine sous Windows 11 : processeur AMD
Ryzen 7 5800H, 15,4 Go de RAM, carte graphique NVIDIA RTX 3070 Laptop de 8 Go, avec
le SDK .NET 8.0.425 et Python 3.13. Sur cette machine, l'installation du SDK .NET 8 par `winget` a
pris 2 minutes, et Python était déjà présent.
