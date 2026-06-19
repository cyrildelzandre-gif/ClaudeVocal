# 🎙️ Claude Vocal

Conversation **vocale mains-libres** avec **Claude Code**, en français.
Tu parles, Claude répond à voix haute, et la conversation continue toute seule — comme un appel téléphonique avec Claude Code.

---

## ✨ Fonctionnement

1. Dis **« claude »** → l'écoute démarre.
2. **Parle librement** (phrases longues OK).
3. Fais une **pause** (~2 s) → ta phrase est transcrite et envoyée à Claude.
4. Claude **répond à voix haute** (réponse courte, orale).
5. Ça **réécoute automatiquement** : tu réponds, et ainsi de suite.
6. Silence prolongé → retour en **veille** (redis « claude » pour reprendre).

Dis **« annule »** (ou « annuler ») pendant l'écoute pour abandonner la demande en cours.

---

## 🧠 Sous le capot

| Brique | Techno |
|---|---|
| Détection du mot-clé « claude » | `System.Speech` (moteur Windows, léger) |
| Transcription de la dictée | **Whisper.net** — modèle `large-v3-turbo`, **accéléré GPU (CUDA)** |
| Fin de phrase automatique | VAD maison (détection d'énergie / silence) |
| Cerveau | **process `claude` persistant** en mode `stream-json` (garde le contexte) |
| Synthèse vocale | Voix **neuronale Windows** (WinRT), repli SAPI |
| Anti-parasites | Baisse du volume des sorties de 70 % pendant la saisie |

- Un **seul** process `claude` est lancé au démarrage et reçoit chaque question par injection JSON sur son `stdin` → le fil de la conversation est conservé sans relancer le CLI.
- Claude tourne avec `--dangerously-skip-permissions` : il peut utiliser ses outils (lire des fichiers, lancer des commandes…) sans demander d'autorisation, indispensable en mode vocal.

---

## ✅ Prérequis

- **Windows 10/11 x64**
- **.NET 8 SDK**
- **Claude Code CLI** installé et fonctionnel (`claude` accessible dans le PATH)
- Un **micro**
- Recommandé : **GPU NVIDIA** (CUDA) pour Whisper en temps réel — repli CPU automatique sinon
- Pour une meilleure dictée FR : installer la **reconnaissance vocale française** de Windows
- Une **voix** Windows (par défaut l'app cherche « Microsoft Claude », fr-CA)

---

## 🚀 Build & lancement

```powershell
dotnet build
dotnet run
```

> ⚠️ **Premier lancement** : le modèle Whisper `large-v3-turbo` (~1,6 Go) est téléchargé automatiquement (une seule fois).

---

## ⚙️ Réglages

Dans `Program.cs` (constantes en haut de `OnReconnu`) :

| Constante | Rôle | Défaut |
|---|---|---|
| `SilenceFinMs` | pause (ms) qui déclenche l'envoi | `2000` |
| `SilenceInitMs` | silence avant retour en veille | `8000` |
| `DureeMaxMs` | sécurité : envoi forcé | `60000` |
| `ConfDemarrage` | confiance mini pour démarrer | `0.45` |
| `ConfAnnule` | confiance mini pour « annule » | `0.65` |

Dans `Transcription.cs` :

| Constante | Rôle | Défaut |
|---|---|---|
| `SeuilPlancher` | seuil mini : en dessous, jamais considéré comme voix | `180` |
| `MargeVoix` | voix = bruit de fond × cette marge (seuil **adaptatif**) | `3.0` |
| `FramesMiniVoix` | frames voisées d'affilée pour valider (anti-pic) | `2` |
| `Modele` / `Url` | modèle Whisper utilisé | `large-v3-turbo` |

> La détection voix/silence est **adaptative** : le bruit ambiant est estimé en
> continu pendant les silences, et on ne déclenche que si l'énergie dépasse
> nettement ce bruit. Plus besoin de régler un seuil fixe selon le micro.

**Voix** : par défaut « Claude ». Pour forcer une autre voix, définis la variable d'environnement
`CLAUDEVOCAL_VOIX` (ex. `Hortense`, `Julie`, `Paul`…).

---

## 📁 Structure

```
ClaudeVocal/
├── Program.cs         # machine à états, mot-clé, process claude, conversation
├── Transcription.cs   # micro (NAudio) + Whisper + VAD + volume
├── Voix.cs            # synthèse vocale (voix Windows choisie)
└── ClaudeVocal.csproj
```

---

## 🛠️ Dépannage

- **« rien entendu »** → micro non détecté ou seuil trop haut : vérifie le micro par défaut Windows, baisse `SeuilVoix`.
- **Ça envoie trop tôt** → augmente `SilenceFinMs`.
- **Transcription lente** → le GPU est sûrement occupé par une autre tâche (le repli/contention CPU ralentit Whisper).
- **Mauvaise voix lue** → vérifie la ligne `Voix utilisée (...)` au démarrage, ou définis `CLAUDEVOCAL_VOIX`.
