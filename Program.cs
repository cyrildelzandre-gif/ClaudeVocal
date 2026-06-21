using System.Diagnostics;
using System.Globalization;
using System.Speech.Recognition;
using System.Text;
using System.Text.Json;
using Avalonia;

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
    static volatile bool _occupe;                   // true = Claude travaille (pastille rouge)

    // Identité de CETTE instance (plusieurs poulpes possibles, un par agent).
    static string _nom = "claude";                  // mot-clé de réveil de cet agent
    static int _index;                              // position à l'écran / identifiant
    static string _dossierMemoire = "";             // dossier mémoire propre à CE profil
    public static string Nom => _nom;
    public static int Index => _index;

    // Dossier de mémoire persistante propre au profil (Claude, Jarvis, ...), pour
    // garder un flux de travail continu d'une session à l'autre. Stable entre les
    // recompilations (rangé sous le profil utilisateur, pas dans le dossier de build).
    static string DossierMemoire()
    {
        var dossier = Path.Combine(Salon.Racine, "profils", _nom);
        try { Directory.CreateDirectory(dossier); } catch { }
        return dossier;
    }

    // Publie l'état courant de cet agent dans le registre partagé (voir Salon),
    // pour que les autres poulpes (et leur LLM) sachent ce qu'on fait.
    static void PublierEtat(string etat)
    {
        var tache = _taches.FirstOrDefault(x => x.statut == "in_progress").contenu ?? "";
        Salon.Publier(_nom, _index, etat, tache);
    }

    // À chaque nouveau tour, la 1re phrase parlée est préfixée du nom de l'agent
    // (« Claude. … ») pour distinguer qui parle quand plusieurs poulpes se répondent.
    // Les phrases suivantes du même tour ne le sont pas.
    static volatile bool _debutTour;

    static string Prefixer(string phrase)
    {
        if (!_debutTour) return phrase;
        _debutTour = false;
        string nomAffiche = char.ToUpper(_nom[0]) + _nom[1..];
        return $"{nomAffiche}. {phrase}";
    }

    static Process? _claude;                          // le process claude persistant
    static string _dernierDit = "";                   // dernière phrase déjà lue (anti-doublon)

    // Historique des échanges (toi + Claude), pour la commande « historique N ».
    static readonly List<(string qui, string texte)> _historique = new();
    static readonly object _histVerrou = new();

    static void NoterHistorique(string qui, string texte)
    {
        texte = texte?.Trim() ?? "";
        if (texte.Length == 0) return;
        lock (_histVerrou)
        {
            _historique.Add((qui, texte));
            if (_historique.Count > 200) _historique.RemoveAt(0);   // borne mémoire
        }
    }

    // Consigne « conversation orale », envoyée avec chaque question.
    const string Consigne =
        "[Conversation VOCALE en français. Tu peux utiliser tes outils (lire des fichiers, " +
        "lancer des commandes, etc.) si c'est utile. " +
        "RÉACTIVITÉ : si la demande implique du travail (outils, fichiers, commandes), commence " +
        "TOUJOURS par une courte phrase parlée disant ce que tu vas faire (ex : « Ok, je vais " +
        "regarder le fichier X. ») AVANT d'utiliser le moindre outil. Puis fais le travail, et " +
        "termine par une phrase de conclusion brève. " +
        "Chaque phrase parlée doit être COURTE, naturelle, sans listes, sans code, sans markdown. " +
        "Pose une question si tu as besoin d'une précision.]";

    static readonly CultureInfo Fr = new("fr-FR");

    [STAThread]
    static void Main(string[] args)
    {
        // Nom (= mot-clé de réveil) et position de cet agent, passés par l'instance
        // parente quand on clique « + » ou qu'on dit « nouvel agent ».
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == "--nom") _nom = args[i + 1].Trim().ToLowerInvariant();
            else if (args[i] == "--index" && int.TryParse(args[i + 1], out var ix)) _index = ix;
        }

        // Le mot de réveil à retirer des phrases = le nom de CE profil
        // (« claude » pour Claude, « jarvis » pour Jarvis...).
        MotsCmd = new[] { _nom, "annuler", "annule" };

        _dossierMemoire = DossierMemoire();             // mémoire persistante de CE profil
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Salon.Retirer();   // sortie propre du registre

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* pas de console (WinExe) */ }
        // Avalonia prend le thread principal ; le vocal démarre quand l'UI est prête.
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<OverlayApp>().UsePlatformDetect().LogToTrace();

    // Appelé par l'overlay une fois la fenêtre affichée.
    public static void DemarrerVocal() => _ = Task.Run(BoucleVocale);

    static async Task BoucleVocale()
    {
        if (!LancerClaude())
            return;

        PublierEtat("repos");                            // on s'inscrit au registre partagé

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
        var motCle = new Grammar(ConstruireReveil(_nom, cult)) { Name = "wake" };
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
        _reco = reco;                                  // évite le ramasse-miettes

        DemarrerRappelInactif();                        // rappels « je suis inactif » toutes les 5 min

        await BriefingDemarrage();                       // Claude apprend qu'il parle en vocal + se présente

        // Plus de console : la fenêtre du poulpe gère la durée de vie de l'appli.
        await Task.Delay(System.Threading.Timeout.Infinite);
    }

    // Variantes phonétiques françaises du mot de réveil. Le moteur vocal FR reconnaît
    // mal un prénom anglais brut (ex. « jarvis ») : on lui propose, EN PLUS du nom,
    // des orthographes françaises qui sonnent pareil, ce qui débloque la détection.
    static GrammarBuilder ConstruireReveil(string nom, System.Globalization.CultureInfo cult)
    {
        var v = new List<string> { nom };
        switch (nom)
        {
            case "jarvis":   v.AddRange(new[] { "jarvisse", "djarvis", "djarvisse", "jarviss" }); break;
            case "friday":   v.AddRange(new[] { "fraïday", "fraïdé", "frydé" }); break;
            case "cortana":  v.AddRange(new[] { "cortanna", "kortana", "cortanah" }); break;
            case "samantha": v.AddRange(new[] { "samanta", "samentha" }); break;
            case "alexa":    v.AddRange(new[] { "alexah", "alèxa", "aleksa" }); break;
            case "edith":    v.AddRange(new[] { "édith", "édit", "idith" }); break;
            case "tars":     v.AddRange(new[] { "tarss", "tarce", "tarsse" }); break;
        }
        return new GrammarBuilder(new Choices(v.ToArray())) { Culture = cult };
    }

    static SpeechRecognitionEngine? _reco;             // gardé en vie tant que l'appli tourne

    // Rappel vocal périodique quand l'agent est inactif (pastille verte) : il dit
    // son nom pour signaler qu'il ne fait rien, sans couper une dictée en cours.
    static void DemarrerRappelInactif()
    {
        _ = Task.Run(async () =>
        {
            while (!_arretDemande)
            {
                try { await Task.Delay(300000); } catch { break; }
                if (_occupe || _enTrainDeParler) continue;          // occupé ou déjà en train de parler
                if (Salon.OccupeAilleurs()) continue;               // un autre agent est en conversation
                if (_etat == Etat.Ecoute && Micro.AParle) continue; // tu es en train de dicter
                try { await Tts.Parler($"Agent {_nom}, inactif."); } catch { }
            }
        });
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
                            "--verbose --dangerously-skip-permissions " +
                            $"--add-dir \"{Salon.Racine}\"",   // accès à sa mémoire + au registre des agents
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

                // Messages de l'assistant AU FIL DE L'EAU : on lit à voix haute chaque
                // bloc de texte dès qu'il arrive (ex : « Ok, je vais regarder X »), pour
                // répondre AVANT/PENDANT le travail au lieu d'attendre la fin.
                if (type == "assistant" &&
                    root.TryGetProperty("message", out var m) &&
                    m.TryGetProperty("content", out var contenu) &&
                    contenu.ValueKind == JsonValueKind.Array)
                {
                    foreach (var bloc in contenu.EnumerateArray())
                    {
                        if (bloc.TryGetProperty("type", out var bt) && bt.GetString() == "text" &&
                            bloc.TryGetProperty("text", out var tx))
                        {
                            var phrase = (tx.GetString() ?? "").Trim();
                            if (phrase.Length == 0 || phrase == _dernierDit) continue;
                            _dernierDit = phrase;
                            Console.WriteLine("\nClaude : " + phrase + "\n");
                            Overlay.Claude(phrase);
                            NoterHistorique("Claude", phrase);
                            await Tts.ParlerProtege(Prefixer(phrase));
                        }
                        // Claude met à jour sa to-do list -> on l'affiche à droite du poulpe.
                        else if (bloc.TryGetProperty("type", out var bt2) && bt2.GetString() == "tool_use" &&
                                 bloc.TryGetProperty("name", out var bn) && bn.GetString() == "TodoWrite" &&
                                 bloc.TryGetProperty("input", out var inp) &&
                                 inp.TryGetProperty("todos", out var lt) && lt.ValueKind == JsonValueKind.Array)
                        {
                            var liste = new List<(string, string)>();
                            foreach (var td in lt.EnumerateArray())
                            {
                                var c = td.TryGetProperty("content", out var cc) ? (cc.GetString() ?? "") : "";
                                var st = td.TryGetProperty("status", out var ss) ? (ss.GetString() ?? "") : "";
                                if (c.Trim().Length > 0) liste.Add((c.Trim(), st));
                            }
                            _taches = liste;
                            Overlay.Todos(liste);
                        }
                    }
                    continue;
                }

                // Événement final d'un tour : on a déjà parlé en streaming ; on ne
                // relit que si rien n'a encore été dit, puis on repasse en veille.
                if (type == "result")
                {
                    var reponse = root.TryGetProperty("result", out var r) &&
                                  r.ValueKind == JsonValueKind.String
                                  ? (r.GetString() ?? "").Trim() : "";
                    if (reponse.Length > 0 && reponse != _dernierDit)
                    {
                        Console.WriteLine("\nClaude : " + reponse + "\n");
                        Overlay.Claude(reponse);
                        NoterHistorique("Claude", reponse);
                        await Tts.ParlerProtege(Prefixer(reponse));
                    }
                    _dernierDit = "";
                    _occupe = false;
                    Overlay.Travail(false);           // Claude attend ta réponse -> rond vert
                    // On NE réécoute PAS tout seul : retour en veille, il faut
                    // redire « claude » pour relancer l'enregistrement.
                    PublierEtat("repos");
                    Overlay.Etat(false);
                    Console.WriteLine("\n[zzz] En veille — dis « claude » pour reprendre.");
                }
            }
            Console.WriteLine("(!) Le process claude s'est arrêté.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Lecture claude : " + ex.Message);
        }

        // Le process est mort (crash, déconnexion...) -> on le relance tout seul
        // pour que la conversation vocale puisse reprendre sans redémarrer l'appli.
        if (!_arretDemande)
            Relancer();
    }

    static volatile bool _arretDemande;        // vrai quand l'utilisateur quitte volontairement

    // Relance le process claude après une coupure imprévue (avec petit délai).
    static void Relancer()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (_arretDemande) return;
            Console.WriteLine("(↻) Redémarrage du process claude...");
            if (LancerClaude())
                await Tts.Parler("Je suis de retour.");
            else
                await Tts.Parler("Le moteur Claude ne répond plus.");
        });
    }

    // ── Briefing one-shot au démarrage : Claude apprend le contexte vocal ─────
    static async Task BriefingDemarrage()
    {
        if (_claude is null || _claude.HasExited) return;

        string nomAffiche = char.ToUpper(_nom[0]) + _nom[1..];
        string brief = Consigne + "\n\n" +
            $"[DÉMARRAGE] Tu es « {nomAffiche} », un agent assistant avec qui on discute À LA VOIX, " +
            "via un petit overlay en forme de poulpe posé sur l'écran. Ce n'est pas un terminal " +
            "classique : chaque message que tu reçois vient d'une dictée vocale de l'utilisateur, et " +
            "tes réponses sont lues à voix haute. Ne sois donc pas surpris par ce format conversationnel.\n\n" +
            $"[MÉMOIRE] Ton dossier de mémoire personnel est : {_dossierMemoire}. Il n'est qu'à toi (profil " +
            $"« {nomAffiche} »). Lis-y le fichier MEMOIRE.md s'il existe pour reprendre le fil de notre travail, " +
            "puis tiens-le à jour au fil de l'eau (tâches en cours, décisions, prochaines étapes) afin de garder " +
            "une continuité d'une session à l'autre.\n\n" +
            $"[AGENTS] Tu n'es pas seul : chaque poulpe publie son état dans {Salon.DossierAgents} (un fichier " +
            "JSON par agent : pid, nom, index, etat, tache). Pour connaître les autres agents et leur état, lis " +
            "ce dossier. Pour en arrêter un, tue son pid. Pour en créer un, lance ClaudeVocal.exe avec l'argument " +
            "--nom suivi du nom voulu.\n\n" +
            "Pour finir : présente-toi en UNE seule phrase courte et dis que tu es prêt à l'aider.";

        var msg = new { type = "user", message = new { role = "user", content = brief } };
        _occupe = true;
        _debutTour = true;
        PublierEtat("travail");
        Overlay.Travail(true);
        try
        {
            await _claude.StandardInput.WriteLineAsync(JsonSerializer.Serialize(msg));
            await _claude.StandardInput.FlushAsync();
        }
        catch { /* tant pis, on reste silencieux */ }
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
        Overlay.Vous(question);
        NoterHistorique("Vous", question);
        _occupe = true;
        _debutTour = true;                     // la 1re phrase parlée sera préfixée du nom
        PublierEtat("travail");
        Overlay.Travail(true);                 // Claude se met au travail -> rond rouge

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
    static string[] MotsCmd = { "claude", "annuler", "annule" };  // mis à jour selon _nom

    // Seuils réglables.
    const float ConfDemarrage   = 0.65f;   // confiance pour DÉMARRER l'écoute (plus strict = moins de faux déclenchements)
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
        if (Salon.OccupeAilleurs()) return;            // un autre poulpe est déjà en conversation
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
        PublierEtat("ecoute");
        Overlay.Etat(true);
        Overlay.Live("");                      // bulle bleue « en direct » -> tu parles
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
        PublierEtat("repos");                          // libère les autres agents
        Overlay.FinLive();
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
        PublierEtat("repos");                          // libère les autres agents
        Overlay.FinLive();
        Overlay.Etat(false);
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
                                    Overlay.Live(t);          // met à jour la bulle bleue en direct
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
        PublierEtat("travail");                // on ne dicte plus : on traite
        Overlay.FinLive();                     // la bulle finale « Vous » prend le relais
        Console.WriteLine("\n[■] Transcription...");

        var pcm = Micro.Arreter();
        var texte = Nettoyer(await Transcripteur.Transcrire(pcm));

        if (string.IsNullOrWhiteSpace(texte))
        {
            Console.WriteLine("(rien entendu)");
            return;
        }

        // Fermeture : on a demandé confirmation au tour précédent.
        if (_fermetureEnAttente)
        {
            _fermetureEnAttente = false;
            if (EstConfirmation(texte))
            {
                await Tts.Parler("D'accord, je me ferme. Au revoir.");
                TuerCetAgent();
                return;
            }
            await Tts.Parler("D'accord, je reste là.");
            Demarrer();
            return;
        }

        // « que fais-tu » -> statut local (fonctionne même quand je travaille).
        if (EstStatut(texte))
        {
            await RapporterStatut();
            Demarrer();
            return;
        }

        // « tue le poulpe » -> demande confirmation avant de fermer.
        if (EstFermeture(texte))
        {
            _fermetureEnAttente = true;
            await Tts.Parler("Tu veux vraiment me fermer ? Dis oui pour confirmer.");
            Demarrer();
            return;
        }

        // Création d'agent : on a demandé un nom au tour précédent -> on le prend ici.
        if (_nouvelAgentEnAttente)
        {
            _nouvelAgentEnAttente = false;
            var nomAgent = NettoyerNom(texte);
            if (nomAgent.Length == 0)
            {
                await Tts.Parler("Je n'ai pas compris le nom, j'annule.");
                Demarrer();
                return;
            }
            LancerNouvelAgent(nomAgent);
            await Tts.Parler($"Nouvel agent {nomAgent} créé.");
            Demarrer();
            return;
        }

        // « nouvel agent » -> on demande son nom, puis on le crée au tour suivant.
        if (EstNouvelAgent(texte))
        {
            _nouvelAgentEnAttente = true;
            await Tts.Parler("Quel nom pour le nouvel agent ?");
            Demarrer();
            return;
        }

        // Redémarrage : un « restart » demande d'abord confirmation, et c'est
        // seulement au tour suivant, si on dit « oui », qu'on recompile/relance.
        if (_redemarrageEnAttente)
        {
            _redemarrageEnAttente = false;
            if (EstConfirmation(texte))
            {
                await Redemarrer();
                return;
            }
            Console.WriteLine("[↻] Redémarrage annulé.");
            await Tts.Parler("D'accord, j'annule le redémarrage.");
            Demarrer();                                 // on continue à écouter
            return;
        }

        if (EstHistorique(texte, out int nHist))
        {
            int affiches = AfficherHistorique(nHist);
            await Tts.Parler(affiches > 0
                ? $"Voici les {affiches} derniers messages dans la console."
                : "L'historique est vide pour l'instant.");
            Demarrer();                                 // on continue à écouter
            return;
        }

        if (EstRedemarrage(texte))
        {
            _redemarrageEnAttente = true;
            Console.WriteLine("[↻] Confirmer le redémarrage ? Dis « oui ».");
            await Tts.Parler("Tu veux vraiment recompiler et redémarrer ? Dis oui pour confirmer.");
            Demarrer();                                 // on réécoute pour la confirmation
            return;
        }

        await EnvoyerAClaude(texte);
    }

    static volatile bool _redemarrageEnAttente;         // un « restart » attend confirmation

    // Vrai si la phrase est une confirmation (« oui », « confirme »...).
    static bool EstConfirmation(string t)
    {
        var mot = MotSeul(t);
        return mot is "oui" or "ouais" or "oui" or "ok" or "oke" or "okay"
            or "confirme" or "confirmer" or "vasy" or "go" or "daccord"
            or "restart" or "redemarre" or "redemarrer";
    }

    // Normalise une phrase courte en un mot : lettres seules, minuscules, sans accents.
    static string MotSeul(string t)
    {
        var mot = new string(t.Where(char.IsLetter).ToArray())
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD);
        return new string(mot
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
            .ToArray());
    }

    // Vrai si la phrase dictée est uniquement le mot-clé de redémarrage.
    static bool EstRedemarrage(string t)
    {
        var mot = MotSeul(t);
        return mot is "restart" or "restarte" or "ristart" or "reboot" or "rebuild"
            or "redemarre" or "redemarrer" or "redemarrage";
    }

    // Vrai si la phrase est la commande « historique [N] ». N par défaut = 10.
    static bool EstHistorique(string t, out int n)
    {
        n = 10;
        var tokens = t.Split(new[] { ' ', '\t', ',', '.' }, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) return false;
        if (MotSeul(tokens[0]) is not ("historique" or "history" or "historic")) return false;

        var chiffres = new string(t.Where(char.IsDigit).ToArray());
        if (chiffres.Length > 0 && int.TryParse(chiffres, out var k) && k > 0)
            n = Math.Min(k, 50);
        return true;
    }

    // Réaffiche les N derniers messages (toi + Claude) en bulles. Renvoie le nombre affiché.
    static int AfficherHistorique(int n)
    {
        List<(string qui, string texte)> snap;
        lock (_histVerrou)
            snap = _historique.Skip(Math.Max(0, _historique.Count - n)).ToList();

        Overlay.Historique(snap);
        return snap.Count;
    }

    // ── Création d'un nouvel agent (nouveau poulpe = nouvelle instance) ────────
    static volatile bool _nouvelAgentEnAttente;        // on attend que tu dises le nom

    static readonly string[] _nomsAuto =
        { "claude", "jarvis", "alexa", "friday", "cortana", "samantha", "tars", "edith" };

    static string NomAuto(int i) => i >= 0 && i < _nomsAuto.Length ? _nomsAuto[i] : "agent" + i;

    // Vrai si la phrase demande la création d'un nouvel agent.
    static bool EstNouvelAgent(string t)
    {
        var s = MotSeul(t);
        return s.Contains("nouvelagent") || s.Contains("nouveauagent")
            || s.Contains("nouveaupoulpe") || s.Contains("creeunagent")
            || s.Contains("creerunagent") || s.Contains("ajouteunagent")
            || s.Contains("nouvelagent");
    }

    // Garde 1 à 2 mots comme nom d'agent (minuscule, sans ponctuation).
    static string NettoyerNom(string t)
    {
        var mots = t.Split(new[] { ' ', '\t', '.', ',', '!', '?' }, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" ", mots.Take(2)).ToLowerInvariant().Trim();
    }

    // Dernières tâches connues (pour la commande « que fais-tu »).
    static volatile List<(string contenu, string statut)> _taches = new();

    // ── Statut : « que fais-tu », répondu localement (marche même occupé) ──────
    static bool EstStatut(string t)
    {
        var s = MotSeul(t);
        return s.Contains("quefaistu") || s.Contains("tufaisquoi")
            || s.Contains("tuenesou") || s == "statut" || s == "status";
    }

    static async Task RapporterStatut()
    {
        var taches = _taches;
        var enCours = taches.FirstOrDefault(x => x.statut == "in_progress");
        string phrase;
        if (_occupe)
            phrase = enCours.contenu is { Length: > 0 }
                ? $"Je travaille sur : {enCours.contenu}."
                : "Je suis en train de travailler.";
        else
        {
            int reste = taches.Count(x => x.statut != "completed");
            phrase = reste > 0
                ? $"Je suis inactif. Il reste {reste} tâche{(reste > 1 ? "s" : "")}."
                : "Je suis inactif, rien en cours.";
        }
        await Tts.Parler(phrase);
    }

    // ── Fermeture de cet agent (« tue le poulpe ») ────────────────────────────
    static volatile bool _fermetureEnAttente;          // on attend la confirmation

    static bool EstFermeture(string t)
    {
        var s = MotSeul(t);
        return s.Contains("tuelepoulpe") || s.Contains("tuetoi") || s.Contains("fermelagent")
            || s.Contains("fermetoi") || s.Contains("arretetoi") || s.Contains("supprimelagent")
            || s.Contains("tuelagent");
    }

    // Arrête le modèle de cet agent et ferme son poulpe (= ferme ce process).
    public static void TuerCetAgent()
    {
        _arretDemande = true;
        Salon.Retirer();
        try { _claude?.StandardInput.Close(); } catch { }
        try { _claude?.Kill(true); } catch { }
        Environment.Exit(0);
    }

    // Lance une nouvelle instance (un nouvel agent/poulpe) avec son propre nom.
    public static void LancerNouvelAgent(string? nom)
    {
        int n = Process.GetProcessesByName("ClaudeVocal").Length;   // instances déjà vivantes
        string vrai = string.IsNullOrWhiteSpace(nom) ? NomAuto(n) : nom.Trim().ToLowerInvariant();
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--nom \"{vrai}\" --index {n}",
                UseShellExecute = true
            });
        }
        catch { /* tant pis */ }
    }

    // Recompile le projet, ferme l'appli courante et relance la nouvelle.
    // L'exe étant verrouillé tant qu'on tourne, on délègue à un .bat qui :
    //   1) attend la fin de NOTRE process, 2) build, 3) relance le nouvel exe.
    static async Task Redemarrer()
    {
        Console.WriteLine("\n[↻] Redémarrage : compilation puis relance.");
        await Tts.Parler("Ok, je recompile et je redémarre.");

        string? csproj = TrouverCsproj();
        if (csproj is null)
        {
            Console.WriteLine("(X) Projet .csproj introuvable, redémarrage annulé.");
            await Tts.Parler("Je ne trouve pas le projet, j'annule le redémarrage.");
            return;
        }

        // On recompile DIRECTEMENT dans le dossier où tourne l'exe actuel (-o),
        // pour relancer la version fraîche peu importe la config de build.
        string exeDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string exe = Path.Combine(exeDir, "ClaudeVocal.exe");
        int pid = Environment.ProcessId;

        string script =
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "echo Attente de la fermeture de l'application...\r\n" +
            ":wait\r\n" +
            $"tasklist /FI \"PID eq {pid}\" | find \"{pid}\" >nul\r\n" +
            "if %errorlevel%==0 (\r\n" +
            "  timeout /t 1 /nobreak >nul\r\n" +
            "  goto wait\r\n" +
            ")\r\n" +
            "echo Compilation...\r\n" +
            $"dotnet build \"{csproj}\" -c Debug -o \"{exeDir}\"\r\n" +
            "if errorlevel 1 (\r\n" +
            "  echo *** BUILD ECHOUE - l'application n'a pas ete relancee. ***\r\n" +
            "  pause\r\n" +
            "  goto fin\r\n" +
            ")\r\n" +
            "echo Relance...\r\n" +
            $"start \"ClaudeVocal\" \"{exe}\"\r\n" +
            ":fin\r\n" +
            "del \"%~f0\"\r\n";

        string bat = Path.Combine(Path.GetTempPath(), "claudevocal_restart.bat");
        await File.WriteAllTextAsync(bat, script, new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = bat,
            UseShellExecute = true,        // fenêtre visible : on suit la compilation
        });

        // On se ferme proprement ; le .bat prendra le relais une fois l'exe libéré.
        _arretDemande = true;
        Salon.Retirer();
        try { _claude?.StandardInput.Close(); _claude?.Kill(true); } catch { }
        Environment.Exit(0);
    }

    // Remonte depuis le dossier de l'exe jusqu'à trouver le .csproj du projet.
    static string? TrouverCsproj()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var f = dir.GetFiles("*.csproj");
            if (f.Length > 0) return f[0].FullName;
            dir = dir.Parent;
        }
        return null;
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

        public static Task Parler(string texte) => Salon.Parler(() => VoixNaturelle.Lire(texte));
    }
}
