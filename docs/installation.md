# Guide d'installation pas à pas (version C# / Python)

Cette version demande **deux environnements** : .NET 8 pour l'application, Python 3.11+ pour
le service IA (ADR 0009 : les modèles vivent dans un autre programme, dans un autre langage).
Durée réaliste : **20 minutes** pour tout ce qui tourne hors-ligne (séquences 1 à 2.2), **30 à
60 minutes de plus** pour les vrais modèles (téléchargements compris, selon la connexion).
Rien n'exige de carte graphique ; un portable de 8 Go de RAM suffit pour le hors-ligne, 16 Go
sont confortables pour les vrais modèles.

Si une étape résiste, tout le cours jusqu'à la séquence 2.2 fonctionne en **mode hors-ligne**
(étape 4) : avancez, revenez à l'installation d'Ollama plus tard.

## 1. Le SDK .NET 8 (5 min)

| Système | Comment | Vérification |
|---|---|---|
| Windows 10/11 | <https://dotnet.microsoft.com/download/dotnet/8.0> → « SDK 8.0.x » (installeur x64), ou `winget install Microsoft.DotNet.SDK.8` | `dotnet --version` → `8.0.…` |
| macOS | même page (installeur .pkg, choisir Arm64 ou x64 selon la puce) ou `brew install --cask dotnet-sdk@8` | `dotnet --version` |
| Linux (Debian/Ubuntu) | `sudo apt install dotnet-sdk-8.0` (dépôt Microsoft ou Ubuntu 22.04+) | `dotnet --version` |

`global.json` accepte toute version 8.0 ou plus récente (`rollForward: latestMajor`).
**Aucun paquet NuGet dans l'application** ; le projet de tests télécharge xUnit au premier
`dotnet test` (quelques Mo, une fois).

## 2. Python 3.11 ou plus, pour le service IA (5 min)

| Système | Comment | Vérification |
|---|---|---|
| Windows 10/11 | <https://www.python.org/downloads/windows/> → « Windows installer (64-bit) ». Cocher **« Add python.exe to PATH »**. Ou `winget install Python.Python.3.12`. | `py --version` (ou `python --version`) |
| macOS | <https://www.python.org/downloads/macos/> ou `brew install python@3.12` | `python3 --version` |
| Linux (Debian/Ubuntu) | `sudo apt install python3` (3.11+ sur Ubuntu 24.04, Debian 12) | `python3 --version` |

Dans la suite, `python` désigne `py` sous Windows, `python3` sous macOS et Linux.
**Aucune bibliothèque à installer** : le service IA n'utilise que la bibliothèque standard.

## 3. Git et le dépôt (5 min)

| Système | Comment |
|---|---|
| Windows | <https://git-scm.com/download/win> ou `winget install Git.Git` |
| macOS | `xcode-select --install` ou `brew install git` |
| Linux | `sudo apt install git` |

```bash
git clone https://github.com/arthur-herve/fyc-assistant-rag-csharp.git
cd fyc-assistant-rag-csharp
dotnet test --nologo                                   # l'application
python -m unittest discover -s tests_python -t .       # le service IA
```

Attendu : `Réussi! … total : 91` (le premier `dotnet test` compile tout : 30 à 60 secondes,
ensuite quelques secondes) et `Ran 22 tests … OK`. Si les deux sont verts, votre poste est prêt
pour les séquences 1 à 2.2.

## 4. Le mode hors-ligne (2 min)

Deux terminaux, depuis la racine du dépôt.

Terminal 1 — le service IA (il reste ouvert) :

```bash
python -m ai_service
```

Terminal 2 — l'application :

```bash
dotnet run --project src/Assistant.Cli -- index
dotnet run --project src/Assistant.Cli -- ask "Combien de jours de télétravail par semaine ?"
dotnet run --project src/Assistant.Cli -- status
```

Attendu : une réponse citée `[1]` avec la source `teletravail`, puis un verdict « à jour ». Les
modèles `hashing` (embeddings hachés) et `extractive` (recopie de la phrase la plus proche) sont
déterministes et sans réseau : ce sont ceux des tests. `dotnet run` recompile si besoin (une
seconde) ; pour aller plus vite, `dotnet build` une fois puis `dotnet run … --no-build`.

Sous Windows, si les accents du service IA s'affichent mal dans le terminal 1 :
`set PYTHONIOENCODING=utf-8` (cmd) ou `$env:PYTHONIOENCODING = "utf-8"` (PowerShell).
L'application C# écrit en UTF-8 quoi qu'il arrive.

## 5. Ollama et les modèles (10 min + téléchargements)

Ollama exécute les modèles ouverts en local et les expose sur `http://127.0.0.1:11434`.

| Système | Comment |
|---|---|
| Windows | <https://ollama.com/download/windows> ou `winget install Ollama.Ollama` — un service démarre en arrière-plan |
| macOS | <https://ollama.com/download/mac> ou `brew install ollama` puis `ollama serve` |
| Linux | `curl -fsSL https://ollama.com/install.sh \| sh` |

Vérification : `ollama --version`, puis `ollama list` (vide au début).

Modèles du cours, à télécharger dans cet ordre (6 Go en tout ; les quatre ont pris une dizaine de
minutes le 11/09/2026 sur une connexion fibre, comptez plus en ADSL) :

```bash
ollama pull bge-m3              # embeddings par défaut, 1,2 Go
ollama pull llama3.2:3b         # génération par défaut, 2,0 Go
ollama pull nomic-embed-text    # embeddings « de rupture », 274 Mo
ollama pull qwen3:4b            # génération « de rupture », 2,5 Go (séquences 3.3 et 4.1)
```

Les deux premiers suffisent pour suivre le cours ; les deux autres servent aux expériences de
changement de modèle. Si le disque ou la connexion manquent, `gemma3:1b` (815 Mo) remplace
`llama3.2:3b` avec une qualité moindre.

Sans Ollama mais avec un serveur compatible OpenAI (LM Studio, llama.cpp, vLLM, ou un service en
ligne) : `config/ai_service.toml` a un backend `openai-compatible` (exemple `[generation.lmstudio]`,
adresse dans `[defaults.openai-compatible]`, clé d'API lue dans une variable d'environnement si
`api_key_env` est renseigné). L'application, elle, ne voit aucune différence : un alias.

## 6. Premier vrai passage (5 min)

Relancer le service IA (terminal 1, Ctrl+C puis `python -m ai_service`), puis :

```bash
dotnet run --project src/Assistant.Cli -- index --config config/app-ollama.json
dotnet run --project src/Assistant.Cli -- ask "Combien de jours dure le congé de paternité ?" --config config/app-ollama.json -v
```

Attendu : l'indexation du corpus réduit (50 fiches, 740 morceaux) prend **10 à 20 secondes** avec
`bge-m3` sur la machine de référence (carte graphique) — elle ne se fait qu'une fois ; la réponse
arrive ensuite en 1 à 3 secondes et cite une fiche sur le congé de paternité (`F3156`). Le corpus
complet (322 fiches, `config/app-ollama-complet.json`) prend 1 à 3 minutes. Sur processeur seul,
comptez un ordre de grandeur de plus (non mesuré : à relever sur vos machines et à nous signaler).

## Déploiement sur deux machines

L'application et le service IA sont deux programmes : rien n'oblige à les faire tourner sur le même
poste. Sur la machine de calcul : `python -m ai_service --host 0.0.0.0` (et `[server] host` dans
`config/ai_service.toml`). Sur le serveur applicatif :

```bash
AI_SERVICE_URL=http://machine-gpu:8100 dotnet run --project src/Assistant.Cli -- serve --host 0.0.0.0
```

L'application répond alors sur `http://<serveur>:8000` (`/health`, `/v1/ask`, `/v1/status`, `/v1/index`).
Sous Windows, `HttpListener` peut demander un droit d'écoute pour `--host 0.0.0.0` : `netsh http add
urlacl url=http://+:8000/ user=%USERNAME%` (une fois, en administrateur) ; `127.0.0.1` n'en a pas besoin.

## Ce qui peut coincer

| Symptôme | Cause probable | Remède |
|---|---|---|
| `Erreur : service IA — …` ou `injoignable (http://127.0.0.1:8100)` | le terminal 1 n'est pas lancé | `python -m ai_service` |
| `HTTP 502 — … Ollama est-il lancé sur http://127.0.0.1:11434 ?` | Ollama arrêté ou modèle non téléchargé | `ollama serve` / `ollama pull <modèle>` |
| `L'index a été construit avec « … » mais le modèle d'embeddings actuel est « … »` | vous avez changé de modèle d'embeddings | c'est voulu (séquence 2.3) : `index` avec ce modèle, ou `index --if-stale` |
| `dotnet : commande introuvable` | SDK non installé ou PATH non mis à jour | rouvrir le terminal après l'installation ; `dotnet --info` |
| `NETSDK1045` ou « version du SDK » | SDK plus ancien que 8.0 | installer le SDK 8.0 (étape 1) |
| `python : commande introuvable` sous Windows | PATH non mis à jour à l'installation | utiliser `py`, ou réinstaller Python en cochant « Add to PATH » |
| Réponses très lentes (> 1 min) avec `qwen3:4b` | mode réflexion, budget de jetons | normal sur CPU ; utiliser `llama3.2:3b` hors expériences |
| `MemoryError` ou Ollama qui se ferme | modèle trop gros pour la RAM | modèle plus petit (`gemma3:1b`, `nomic-embed-text`) |

## Machine de référence du cours

Les durées et les rapports de `eval/resultats/` ont été mesurés sur : Windows 11, AMD Ryzen 7
5800H, 15,4 Go de RAM, NVIDIA RTX 3070 Laptop 8 Go, Ollama 0.34, .NET SDK 8.0.425, Python 3.13.
Installation complète des deux SDK sur cette machine : .NET SDK 8 par `winget`, 2 minutes ;
Python déjà présent. Les temps sans carte graphique n'ont pas encore été mesurés ; le cours reste
suivable, le mode hors-ligne couvre tout ce qui ne demande pas un vrai modèle.
