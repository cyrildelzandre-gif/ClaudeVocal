using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.CoreAudioApi;

// ───────────────────────────────────────────────────────────────────────────
//  Coordination INTER-PROCESS entre les poulpes.
//
//  Chaque agent est un process ClaudeVocal séparé. Ils se coordonnent via un
//  dossier commun « %USERPROFILE%\.claudevocal » :
//    • agents\<pid>.json  : l'état publié de chaque agent (registre partagé).
//    • parole.lock        : un verrou de fichier estampillé du pid -> un SEUL
//                           agent parle (TTS) à la fois, tous process confondus.
//
//  Le registre sert aussi à : bloquer le réveil d'un agent quand un autre est
//  déjà en conversation, et mettre la parole en file d'attente quand l'utilisateur
//  est en train de dicter à un autre poulpe.
// ───────────────────────────────────────────────────────────────────────────
static class Salon
{
    // Dossier racine commun à tous les agents (mémoire + registre).
    public static readonly string Racine = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claudevocal");

    public static readonly string DossierAgents = Path.Combine(Racine, "agents");
    static readonly string _verrouParole = Path.Combine(Racine, "parole.lock");

    static Salon()
    {
        try { Directory.CreateDirectory(DossierAgents); } catch { }
    }

    // ── Registre des agents ───────────────────────────────────────────────────
    public record Agent(int Pid, string Nom, int Index, string Etat, string Tache, long Maj);

    static string FichierDe(int pid) => Path.Combine(DossierAgents, pid + ".json");

    // Publie / met à jour l'état de CET agent (etat = repos|ecoute|travail|parle).
    public static void Publier(string nom, int index, string etat, string tache)
    {
        try
        {
            var a = new Agent(Environment.ProcessId, nom, index, etat, tache ?? "",
                              DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            File.WriteAllText(FichierDe(Environment.ProcessId),
                              JsonSerializer.Serialize(a), new UTF8Encoding(false));
        }
        catch { /* registre best-effort */ }
    }

    // Retire CET agent du registre (fermeture propre).
    public static void Retirer()
    {
        try { File.Delete(FichierDe(Environment.ProcessId)); } catch { }
    }

    // Liste les agents VIVANTS, en purgeant au passage les fichiers de process morts.
    public static List<Agent> Agents()
    {
        var liste = new List<Agent>();
        string[] fichiers;
        try { fichiers = Directory.GetFiles(DossierAgents, "*.json"); }
        catch { return liste; }

        foreach (var f in fichiers)
        {
            try
            {
                var a = JsonSerializer.Deserialize<Agent>(File.ReadAllText(f));
                if (a is null) continue;
                if (!ProcessVivant(a.Pid)) { try { File.Delete(f); } catch { } continue; }
                liste.Add(a);
            }
            catch { /* fichier en cours d'écriture : on l'ignore ce tour-ci */ }
        }
        return liste;
    }

    static bool ProcessVivant(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch { return false; }
    }

    // Un AUTRE agent est-il déjà engagé avec l'utilisateur (écoute / travail / parole) ?
    public static bool OccupeAilleurs()
    {
        int moi = Environment.ProcessId;
        foreach (var a in Agents())
            if (a.Pid != moi && a.Etat is "ecoute" or "travail" or "parle")
                return true;
        return false;
    }

    // L'utilisateur est-il en train de DICTER à un autre agent ? (on met alors la parole en attente)
    public static bool DicteAilleurs()
    {
        int moi = Environment.ProcessId;
        foreach (var a in Agents())
            if (a.Pid != moi && a.Etat == "ecoute")
                return true;
        return false;
    }

    // ── Parole sérialisée (un agent parle à la fois) ──────────────────────────
    // 1) on attend que l'utilisateur ait fini de dicter ailleurs (file d'attente),
    // 2) on prend le verrou de parole (les autres agents attendent leur tour),
    // 3) on parle, 4) on libère. Tolérant : au pire on parle après le délai max.
    public static async Task Parler(Func<Task> dire)
    {
        for (int i = 0; i < 200 && DicteAilleurs(); i++)   // ~20 s max d'attente
            await Task.Delay(100);

        bool pris = await Acquerir(_verrouParole, TimeSpan.FromSeconds(30));
        Ducking.Baisser();                                  // on baisse les autres applis (vidéo, musique…)
        try { await dire(); }
        finally { Ducking.Restaurer(); if (pris) Liberer(_verrouParole); }
    }

    // Verrou par fichier estampillé du pid : si le détenteur est mort, on le vole
    // (auto-réparation après un crash / kill en pleine phrase).
    static async Task<bool> Acquerir(string chemin, TimeSpan max)
    {
        long debut = Environment.TickCount64;
        while (true)
        {
            try
            {
                using var fs = new FileStream(chemin, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var w = new StreamWriter(fs);
                w.Write(Environment.ProcessId);
                return true;
            }
            catch (IOException)
            {
                if (DetenteurMort(chemin)) { try { File.Delete(chemin); } catch { } continue; }
            }
            catch { /* autre souci : on réessaiera */ }

            if (Environment.TickCount64 - debut > max.TotalMilliseconds) return false;
            await Task.Delay(80);
        }
    }

    static bool DetenteurMort(string chemin)
    {
        try
        {
            if (int.TryParse(File.ReadAllText(chemin).Trim(), out var pid))
                return !ProcessVivant(pid);
        }
        catch { /* illisible : on suppose vivant pour ne pas voler à tort */ }
        return false;
    }

    static void Liberer(string chemin)
    {
        try { File.Delete(chemin); } catch { }
    }
}

// ───────────────────────────────────────────────────────────────────────────
//  Atténuation (« ducking ») des autres sorties audio pendant que l'agent parle :
//  on baisse le volume des sessions audio des AUTRES applications (vidéo, musique)
//  le temps du TTS, puis on rétablit. Best-effort : tout est tolérant aux erreurs.
// ───────────────────────────────────────────────────────────────────────────
static class Ducking
{
    const float Niveau = 0.2f;                  // volume cible des autres applis pendant qu'on parle
    static readonly object _verrou = new();
    static bool _baisse;
    static readonly List<(SimpleAudioVolume vol, float origine)> _sauv = new();

    public static void Baisser()
    {
        lock (_verrou)
        {
            if (_baisse) return;
            _baisse = true;
            _sauv.Clear();
            try
            {
                uint moi = (uint)Environment.ProcessId;
                var en = new MMDeviceEnumerator();
                var dev = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var sessions = dev.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    try
                    {
                        var s = sessions[i];
                        if (s.GetProcessID == moi) continue;   // on ne se baisse pas soi-même
                        var sv = s.SimpleAudioVolume;
                        float o = sv.Volume;
                        if (o <= Niveau) continue;             // déjà bas : on n'y touche pas
                        _sauv.Add((sv, o));
                        sv.Volume = Niveau;
                    }
                    catch { /* session volatile : on l'ignore */ }
                }
            }
            catch { /* pas d'accès audio : tant pis */ }
        }
    }

    public static void Restaurer()
    {
        lock (_verrou)
        {
            if (!_baisse) return;
            _baisse = false;
            foreach (var (vol, origine) in _sauv)
                try { vol.Volume = origine; } catch { }
            _sauv.Clear();
        }
    }
}
