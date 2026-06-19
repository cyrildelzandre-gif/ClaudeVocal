using System.Diagnostics;
using System.Text;
using NAudio.CoreAudioApi;
using NAudio.Utils;
using NAudio.Wave;
using Whisper.net;

// ───────────────────────────────────────────────────────────────────────────
//  Capture micro (NAudio) + transcription locale (Whisper.net).
//  Whisper tourne en local, gratuit, offline. Le modèle est téléchargé une
//  seule fois au premier lancement.
// ───────────────────────────────────────────────────────────────────────────

static class Micro
{
    static WaveInEvent? _in;
    static readonly object _lock = new();
    static List<byte> _buf = new();      // PCM 16 kHz mono 16 bits accumulé

    // Détection voix/silence (VAD) pour la fin de phrase automatique.
    static readonly Stopwatch _chrono = new();
    static long _dernierSonMs;
    static volatile bool _aParle;
    const double SeuilVoix = 550;        // RMS au-dessus = voix (à ajuster selon le micro)

    // Atténuation du volume des sorties audio pendant la saisie (anti-parasites).
    static MMDevice? _sortie;
    static float _volSauve = -1f;
    const float Attenuation = 0.30f;     // 30 % du volume -> baisse de 70 %

    public static readonly WaveFormat Format = new(16000, 16, 1);

    public static void Demarrer()
    {
        Arreter();                        // sécurité si déjà en cours
        BaisserSorties();
        lock (_lock) _buf = new List<byte>(1 << 20);
        _aParle = false;
        _dernierSonMs = 0;
        _chrono.Restart();
        _in = new WaveInEvent { WaveFormat = Format, BufferMilliseconds = 80 };
        _in.DataAvailable += (_, e) =>
        {
            var slice = new byte[e.BytesRecorded];
            Array.Copy(e.Buffer, slice, e.BytesRecorded);
            lock (_lock) _buf.AddRange(slice);
            AnalyserVolume(e.Buffer, e.BytesRecorded);
        };
        try { _in.StartRecording(); }
        catch (Exception ex) { Console.WriteLine("(X) Micro (capture) : " + ex.Message); }
    }

    // Mesure l'énergie (RMS) du chunk pour détecter voix vs silence.
    static void AnalyserVolume(byte[] buf, int octets)
    {
        int n = octets / 2;
        if (n == 0) return;
        double somme = 0;
        for (int i = 0; i + 1 < octets; i += 2)
        {
            short v = (short)(buf[i] | (buf[i + 1] << 8));
            somme += (double)v * v;
        }
        double rms = Math.Sqrt(somme / n);
        if (rms >= SeuilVoix)
        {
            _aParle = true;
            _dernierSonMs = _chrono.ElapsedMilliseconds;
        }
    }

    public static bool AParle => _aParle;
    public static long DureeMs => _chrono.ElapsedMilliseconds;
    // Temps de silence depuis le dernier son (seulement si l'utilisateur a parlé).
    public static long SilenceMs => _aParle ? _chrono.ElapsedMilliseconds - _dernierSonMs : 0;

    // Baisse le volume maître de 70 % et mémorise la valeur d'origine.
    static void BaisserSorties()
    {
        try
        {
            var en = new MMDeviceEnumerator();
            _sortie = en.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            _volSauve = _sortie.AudioEndpointVolume.MasterVolumeLevelScalar;
            _sortie.AudioEndpointVolume.MasterVolumeLevelScalar = _volSauve * Attenuation;
        }
        catch { _sortie = null; _volSauve = -1f; }
    }

    // Restaure le volume maître d'origine.
    static void RestaurerSorties()
    {
        try
        {
            if (_sortie != null && _volSauve >= 0f)
                _sortie.AudioEndpointVolume.MasterVolumeLevelScalar = _volSauve;
        }
        catch { }
        finally { _sortie = null; _volSauve = -1f; }
    }

    /// Copie du PCM capturé jusqu'ici (sans arrêter l'enregistrement).
    public static byte[] Instantane()
    {
        lock (_lock) return _buf.ToArray();
    }

    /// Stoppe l'enregistrement et renvoie le PCM capturé.
    public static byte[] Arreter()
    {
        if (_in != null)
        {
            try { _in.StopRecording(); } catch { }
            _in.Dispose();
            _in = null;
        }
        RestaurerSorties();               // on remet le volume avant la réponse vocale
        lock (_lock)
        {
            var data = _buf.ToArray();
            _buf = new List<byte>();
            return data;
        }
    }
}

static class Transcripteur
{
    // Modèle "large-v3-turbo" : qualité maximale en français, accéléré sur GPU (CUDA).
    // Alternatives : ggml-large-v3.bin (un peu mieux, plus lent), ggml-medium.bin (plus léger).
    const string Modele = "ggml-large-v3-turbo.bin";
    const string Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo.bin";

    static WhisperFactory? _factory;
    static WhisperProcessor? _processor;
    static readonly SemaphoreSlim _gate = new(1, 1);   // une transcription à la fois

    public static async Task<bool> Init()
    {
        try
        {
            var chemin = Path.Combine(AppContext.BaseDirectory, Modele);
            if (!File.Exists(chemin))
                await Telecharger(chemin);

            _factory = WhisperFactory.FromPath(chemin);
            _processor = _factory.CreateBuilder().WithLanguage("fr").Build();
            Console.WriteLine("Whisper prêt (modèle " + Modele + ").");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine("(X) Initialisation Whisper : " + ex.Message);
            return false;
        }
    }

    static async Task Telecharger(string chemin)
    {
        Console.WriteLine("Téléchargement du modèle Whisper large-v3-turbo (~1,6 Go, une seule fois)...");
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var rep = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead);
        rep.EnsureSuccessStatusCode();

        var total = rep.Content.Headers.ContentLength ?? -1L;
        var tmp = chemin + ".part";
        await using (var src = await rep.Content.ReadAsStreamAsync())
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[1 << 16];
            long recu = 0; int n; int dernier = -1;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n));
                recu += n;
                if (total > 0)
                {
                    int pct = (int)(recu * 100 / total);
                    if (pct != dernier) { dernier = pct; Console.Write("\r   " + pct + " %   "); }
                }
            }
        }
        File.Move(tmp, chemin, true);
        Console.WriteLine("\r   téléchargé.        ");
    }

    /// Transcrit du PCM 16 kHz mono 16 bits en texte français.
    public static async Task<string> Transcrire(byte[] pcm)
    {
        if (_processor is null || pcm.Length == 0) return "";

        await _gate.WaitAsync();
        try
        {
            // Empaquète le PCM brut dans un flux WAV pour Whisper.net.
            using var wav = new MemoryStream();
            using (var w = new WaveFileWriter(new IgnoreDisposeStream(wav), Micro.Format))
                w.Write(pcm, 0, pcm.Length);
            wav.Position = 0;

            var sb = new StringBuilder();
            await foreach (var seg in _processor.ProcessAsync(wav))
                sb.Append(seg.Text);

            return sb.ToString().Trim();
        }
        finally { _gate.Release(); }
    }
}
