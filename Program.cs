using System.Diagnostics;
using System.Globalization;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;

// ───────────────────────────────────────────────────────────────────────────
//  Claude Vocal — conversation orale avec Claude Code.
//
//  • UN SEUL process « claude » est lancé au démarrage (mode streaming JSON).
//    Il garde le fil de la conversation tout seul : on lui INJECTE chaque
//    question sur son stdin, on lit ses réponses sur son stdout.
//  • Le mot-clé « claude » est détecté par le moteur Windows (léger, fiable
//    pour un seul mot). La DICTÉE, elle, est enregistrée au micro et
//    transcrite par Whisper en local (bien plus précise en français).
//      1er « claude » -> on enregistre ta dictée (micro).
//      2e  « claude » -> Whisper transcrit, puis on injecte dans claude.
//  • La réponse (résumé oral) est lue à voix haute.
// ───────────────────────────────────────────────────────────────────────────

class Program
{
    enum Etat { Repos, Ecoute }

    static Etat _etat = Etat.Repos;
    static readonly object _verrou = new();
    static volatile bool _enTrainDeParler;          // pour ne pas s'auto-écouter pendant le TTS
    static readonly Stopwatch _depuisBascule = Stopwatch.StartNew();

    static Process? _claude;                          // le process claude persistant

    // Consigne « résumé oral », envoyée avec chaque question.
    const string Consigne =
        "[Réponds en français, à l'oral, en 2 phrases maximum, comme dans une conversation " +
        "parlée. Pas de listes, pas de code, pas de markdown : juste un résumé clair.]";

    static readonly CultureInfo Fr = new("fr-FR");

    static async Task Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== Claude Vocal ===");

        if (!LancerClaude())
            return;

        if (!await Transcripteur.Init())
        {
            Console.WriteLine("(X) Whisper indisponible, arrêt.");
            return;
        }

        // ── Mot-clé (moteur Windows) ─────────────────────────────────────────
        SpeechRecognitionEngine reco;
        try { reco = new SpeechRecognitionEngine(Fr); }
        catch
        {
            Console.WriteLine("(!) Reconnaissance FR indisponible, langue par défaut utilisée.");
            Console.WriteLine("    -> Installe « Reconnaissance vocale » FR dans Windows.");
            reco = new SpeechRecognitionEngine();
        }

        // Deux grammaires : le mot-clé « claude » (bascule) et « annule » (abandon).
        // La dictée elle-même passe par Whisper (streaming).
        var cult = reco.RecognizerInfo.Culture;
        var motCle = new Grammar(new GrammarBuilder("claude") { Culture = cult }) { Name = "wake" };
        var annulerG = new Grammar(new GrammarBuilder(new Choices("annule", "annuler")) { Culture = cult })
        {
            Name = "cancel"
        };
        reco.LoadGrammar(motCle);
        reco.LoadGrammar(annulerG);
        reco.SpeechRecognized += OnReconnu;

        try { reco.SetInputToDefaultAudioDevice(); }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Aucun micro détecté : " + ex.Message);
            return;
        }

        reco.RecognizeAsync(RecognizeMode.Multiple);

        await Tts.Parler("Bonjour. Dis Claude pour me parler.");
        Console.WriteLine("En écoute du mot-clé « claude »...  (Entrée pour quitter)");
        Console.ReadLine();

        reco.RecognizeAsyncStop();
        try { _claude?.StandardInput.Close(); _claude?.Kill(true); } catch { }
    }

    // ── Lancement du process claude persistant (streaming JSON) ───────────────
    static bool LancerClaude()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                // Mode programmatique : reste vivant, lit du JSON, écrit du JSON.
                // --dangerously-skip-permissions : agit sans demander (obligatoire en vocal).
                Arguments = "/c claude -p --input-format stream-json --output-format stream-json " +
                            "--verbose --dangerously-skip-permissions",
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
            };

            _claude = Process.Start(psi);
            if (_claude is null) throw new InvalidOperationException("Process.Start a renvoyé null.");

            _ = Task.Run(LireSortieClaude);          // lecteur de réponses en tâche de fond
            _ = Task.Run(async () =>                   // draine stderr
            {
                string? l;
                while ((l = await _claude!.StandardError.ReadLineAsync()) != null)
                    if (l.Trim().Length > 0) Console.WriteLine("[claude:err] " + l);
            });

            Console.WriteLine("Process claude démarré (conversation persistante).");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Impossible de lancer claude : " + ex.Message);
            Console.WriteLine("    Vérifie que « claude » fonctionne dans un terminal.");
            return false;
        }
    }

    // ── Lecture des réponses (une ligne JSON par événement) ───────────────────
    static async Task LireSortieClaude()
    {
        try
        {
            string? ligne;
            while ((ligne = await _claude!.StandardOutput.ReadLineAsync()) != null)
            {
                ligne = ligne.Trim();
                if (ligne.Length == 0 || ligne[0] != '{') continue;

                JsonElement root;
                try { root = JsonDocument.Parse(ligne).RootElement; }
                catch { continue; }

                var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

                // Événement final d'un tour : contient la réponse complète.
                if (type == "result" &&
                    root.TryGetProperty("result", out var r) &&
                    r.ValueKind == JsonValueKind.String)
                {
                    var reponse = (r.GetString() ?? "").Trim();
                    if (reponse.Length == 0) continue;
                    Console.WriteLine("\nClaude : " + reponse + "\n");
                    await Tts.ParlerProtege(reponse);
                    Demarrer();                       // conversation continue : on réécoute
                }
            }
            Console.WriteLine("(!) Le process claude s'est arrêté.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Lecture claude : " + ex.Message);
        }
    }

    // ── Injection d'une question dans le process claude ───────────────────────
    static async Task EnvoyerAClaude(string question)
    {
        if (_claude is null || _claude.HasExited)
        {
            Console.WriteLine("(X) Le process claude n'est plus actif.");
            await Tts.Parler("Le moteur Claude n'est plus actif.");
            return;
        }

        Console.WriteLine("Vous : " + question);

        // Message au format attendu par claude (stream-json input).
        var msg = new
        {
            type = "user",
            message = new
            {
                role = "user",
                content = Consigne + "\n\n" + question
            }
        };
        var json = JsonSerializer.Serialize(msg);

        try
        {
            await _claude.StandardInput.WriteLineAsync(json);
            await _claude.StandardInput.FlushAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Envoi à claude : " + ex.Message);
        }
    }

    // ── Reconnaissance ────────────────────────────────────────────────────────
    static CancellationTokenSource? _liveCts;          // boucle écoute/live
    static volatile bool _liveEnCours;                 // une transcription live à la fois
    static readonly string[] MotsCmd = { "claude", "annuler", "annule" };

    // Seuils réglables.
    const float ConfDemarrage   = 0.45f;   // confiance pour DÉMARRER l'écoute
    const float ConfAnnule      = 0.65f;   // confiance pour ANNULER (strict)
    const int   AntiRebondMs    = 700;
    const int   SilenceFinMs    = 2000;    // pause (ms) qui déclenche l'envoi
    const int   SilenceInitMs   = 8000;    // si rien dit au bout de ça -> on annule
    const int   DureeMaxMs      = 60000;   // sécurité : envoi forcé après 60 s

    static void OnReconnu(object? s, SpeechRecognizedEventArgs e)
    {
        if (_enTrainDeParler) return;                 // on ignore notre propre voix
        var nom = e.Result.Grammar?.Name;
        if (nom != "wake" && nom != "cancel") return;

        var conf = e.Result.Confidence;
        bool enEcoute = _etat == Etat.Ecoute;

        // « annule » -> abandon (uniquement pendant l'écoute, confiance élevée).
        if (nom == "cancel")
        {
            if (!enEcoute || conf < ConfAnnule) return;
            if (_depuisBascule.ElapsedMilliseconds < AntiRebondMs) return;
            _depuisBascule.Restart();
            Annuler();
            return;
        }

        // « claude » -> DÉMARRE seulement. L'arrêt se fait au silence (pas de coupure).
        if (enEcoute) return;
        if (conf < ConfDemarrage) return;
        if (_depuisBascule.ElapsedMilliseconds < AntiRebondMs) return;
        _depuisBascule.Restart();
        Demarrer();
    }

    static void Demarrer()
    {
        lock (_verrou)
        {
            if (_etat != Etat.Repos) return;
            _etat = Etat.Ecoute;
            Micro.Demarrer();
            DemarrerBoucle();
            Console.WriteLine("\n[●] Écoute... parle librement, fais une PAUSE pour envoyer (« annule » pour abandonner).");
        }
        Bip(true);
    }

    // Fin de phrase détectée (silence) ou durée max -> on envoie.
    static void DeclencherEnvoi()
    {
        bool go = false;
        lock (_verrou)
        {
            if (_etat == Etat.Ecoute) { _etat = Etat.Repos; go = true; }
        }
        if (!go) return;
        Bip(false);
        _ = TraiterTour();
    }

    static void Annuler()
    {
        bool annule = false;
        lock (_verrou)
        {
            if (_etat == Etat.Ecoute)
            {
                _etat = Etat.Repos;
                annule = true;
            }
        }
        if (!annule) return;

        ArreterBoucle();
        Micro.Arreter();                               // on jette l'audio
        Console.WriteLine("\n[✕] Annulé.");
        Bip(false);
    }

    // Fin de conversation (silence prolongé) -> retour en veille (mot-clé requis).
    static void Veille()
    {
        bool veille = false;
        lock (_verrou)
        {
            if (_etat == Etat.Ecoute) { _etat = Etat.Repos; veille = true; }
        }
        if (!veille) return;

        ArreterBoucle();
        Micro.Arreter();
        Console.WriteLine("\n[zzz] En veille — dis « claude » pour reprendre.");
    }

    // Boucle : surveille le silence (fin de phrase) + retranscrit en direct.
    static void DemarrerBoucle()
    {
        _liveCts = new CancellationTokenSource();
        var ct = _liveCts.Token;
        _ = Task.Run(async () =>
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(300, ct); } catch { break; }
                if (ct.IsCancellationRequested) break;

                // Rien dit depuis trop longtemps -> fin de conversation, retour en veille.
                if (!Micro.AParle && Micro.DureeMs > SilenceInitMs) { Veille(); break; }

                // Pause après avoir parlé -> fin de phrase, on envoie.
                if (Micro.AParle && Micro.SilenceMs > SilenceFinMs) { DeclencherEnvoi(); break; }

                // Sécurité durée max.
                if (Micro.DureeMs > DureeMaxMs) { DeclencherEnvoi(); break; }

                // Transcription live ~ toutes les 1,2 s, sans bloquer la surveillance.
                if (++tick % 4 == 0 && !_liveEnCours)
                {
                    var pcm = Micro.Instantane();
                    if (pcm.Length >= Micro.Format.AverageBytesPerSecond)
                    {
                        _liveEnCours = true;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var t = Nettoyer(await Transcripteur.Transcrire(pcm));
                                if (!ct.IsCancellationRequested && t.Length > 0)
                                {
                                    var largeur = Math.Max(20, (Console.WindowWidth > 8 ? Console.WindowWidth : 80) - 6);
                                    var aff = t.Length > largeur ? "…" + t[^largeur..] : t;
                                    Console.Write("\r   ✎ " + aff.PadRight(largeur));
                                }
                            }
                            finally { _liveEnCours = false; }
                        });
                    }
                }
            }
        }, ct);
    }

    static void ArreterBoucle()
    {
        try { _liveCts?.Cancel(); } catch { }
        _liveCts = null;
    }

    // Stop boucle + micro -> transcription finale -> injection dans claude.
    static async Task TraiterTour()
    {
        ArreterBoucle();
        Console.WriteLine("\n[■] Transcription...");

        var pcm = Micro.Arreter();
        var texte = Nettoyer(await Transcripteur.Transcrire(pcm));

        if (string.IsNullOrWhiteSpace(texte))
        {
            Console.WriteLine("(rien entendu)");
            return;
        }
        await EnvoyerAClaude(texte);
    }

    // Retire les mots de commande (« claude », « annule »...) en début/fin de phrase.
    static string Nettoyer(string t)
    {
        if (string.IsNullOrEmpty(t)) return "";

        bool change = true;
        while (change)                                 // fin
        {
            change = false;
            t = t.Trim().TrimEnd('.', ',', '!', '?', ';', ':', '…').Trim();
            foreach (var m in MotsCmd)
                if (t.Length >= m.Length &&
                    t[^m.Length..].Equals(m, StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == m.Length || !char.IsLetter(t[^(m.Length + 1)])))
                {
                    t = t[..^m.Length];
                    change = true;
                    break;
                }
        }
        change = true;
        while (change)                                 // début
        {
            change = false;
            t = t.Trim().TrimStart('.', ',', '!', '?', ';', ':', '…').Trim();
            foreach (var m in MotsCmd)
                if (t.Length >= m.Length &&
                    t[..m.Length].Equals(m, StringComparison.OrdinalIgnoreCase) &&
                    (t.Length == m.Length || !char.IsLetter(t[m.Length])))
                {
                    t = t[m.Length..];
                    change = true;
                    break;
                }
        }
        return t.Trim();
    }

    static void Bip(bool aigu)
    {
        try { Console.Beep(aigu ? 880 : 440, 120); } catch { }
    }

    // Garde le flag _enTrainDeParler pour ne pas se réécouter pendant le TTS.
    static class Tts
    {
        public static async Task ParlerProtege(string texte)
        {
            _enTrainDeParler = true;
            try { await Parler(texte); }
            finally { _enTrainDeParler = false; }
        }

        public static Task Parler(string texte) => VoixNaturelle.Lire(texte);
    }
}
