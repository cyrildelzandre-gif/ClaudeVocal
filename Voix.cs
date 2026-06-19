using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;
using WinSynth = Windows.Media.SpeechSynthesis.SpeechSynthesizer;

// ───────────────────────────────────────────────────────────────────────────
//  Synthèse vocale.
//  On respecte LA VOIX CHOISIE DANS WINDOWS (Paramètres > Voix). On l'utilise
//  via le moteur neuronal (WinRT, plus naturel) si cette voix y existe, sinon
//  via SAPI (System.Speech) dont la voix par défaut = le même choix Windows.
// ───────────────────────────────────────────────────────────────────────────
static class VoixNaturelle
{
    static WinSynth? _winrt;
    static System.Speech.Synthesis.SpeechSynthesizer? _sapi;

    static VoixNaturelle()
    {
        // Voix souhaitée : variable d'env CLAUDEVOCAL_VOIX, sinon "Claude" par défaut.
        var souhaitee = Environment.GetEnvironmentVariable("CLAUDEVOCAL_VOIX");
        if (string.IsNullOrWhiteSpace(souhaitee)) souhaitee = "Claude";
        Console.WriteLine("Voix souhaitée : " + souhaitee);

        _winrt = CreerWinRT(souhaitee);
        if (_winrt is null)                           // voix non dispo en neuronal -> SAPI
            _sapi = CreerSapi();
    }

    public static async Task Lire(string texte)
    {
        if (string.IsNullOrWhiteSpace(texte)) return;

        if (_winrt is not null)
        {
            try { await LireWinRT(texte); return; }
            catch { /* repli */ }
        }
        _sapi ??= CreerSapi();
        _sapi?.Speak(texte);
    }

    // Nom de la voix par défaut configurée dans Windows (via SAPI/System.Speech).
    static string? VoixWindows()
    {
        try
        {
            using var s = new System.Speech.Synthesis.SpeechSynthesizer();
            return s.Voice?.Name;
        }
        catch { return null; }
    }

    // ── Moteur neuronal WinRT, configuré sur la voix souhaitée ────────────────
    static WinSynth? CreerWinRT(string souhaitee)
    {
        try
        {
            var synth = new WinSynth();

            // 1) voix dont le nom contient le terme souhaité (ex. "Claude")
            var voix = WinSynth.AllVoices.FirstOrDefault(v =>
                           v.DisplayName.Contains(souhaitee, StringComparison.OrdinalIgnoreCase))
                       // 2) sinon la voix par défaut de Windows
                       ?? TrouverDefaut();

            if (voix is null) return null;            // rien en WinRT -> on laissera SAPI
            synth.Voice = voix;
            Console.WriteLine("Voix utilisée (neuronal) : " + voix.DisplayName + " [" + voix.Language + "]");
            return synth;
        }
        catch { return null; }
    }

    // Voix WinRT correspondant au défaut Windows, sinon le défaut WinRT.
    static VoiceInformation? TrouverDefaut()
    {
        var nom = VoixWindows();
        if (!string.IsNullOrEmpty(nom))
        {
            var v = WinSynth.AllVoices.FirstOrDefault(x =>
                nom.Contains(x.DisplayName, StringComparison.OrdinalIgnoreCase) ||
                x.DisplayName.Contains(nom, StringComparison.OrdinalIgnoreCase));
            if (v != null) return v;
        }
        return WinSynth.DefaultVoice;
    }

    static async Task LireWinRT(string texte)
    {
        using var flux = await _winrt!.SynthesizeTextToStreamAsync(texte);

        var fini = new TaskCompletionSource();
        var player = new MediaPlayer { AutoPlay = false };
        player.MediaEnded += (_, _) => fini.TrySetResult();
        player.MediaFailed += (_, _) => fini.TrySetResult();
        player.Source = MediaSource.CreateFromStream(flux, flux.ContentType);
        player.Play();

        await fini.Task;
        player.Dispose();
    }

    // ── SAPI : utilise la voix par défaut de Windows (= le choix de l'utilisateur) ──
    static System.Speech.Synthesis.SpeechSynthesizer? CreerSapi()
    {
        try
        {
            var s = new System.Speech.Synthesis.SpeechSynthesizer();
            s.SetOutputToDefaultAudioDevice();
            Console.WriteLine("Voix utilisée (SAPI) : " + s.Voice?.Name);
            return s;                                 // pas de SelectVoice -> on garde le choix Windows
        }
        catch { return null; }
    }
}
