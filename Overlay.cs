using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

// ───────────────────────────────────────────────────────────────────────────
//  Overlay graphique : un petit poulpe pixel-art en haut à gauche de l'écran,
//  avec des bulles de conversation (ce que TU dis, ce que Claude répond).
//  La fenêtre est transparente, sans bordure et toujours au premier plan.
// ───────────────────────────────────────────────────────────────────────────

public class OverlayApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var win = new OverlayWindow(Program.Nom, Program.Index);
            Overlay.Init(win);
            desktop.MainWindow = win;
            Program.DemarrerVocal();              // l'UI est prête : on lance le vocal
        }
        base.OnFrameworkInitializationCompleted();
    }
}

// Pont statique appelé depuis le code vocal (n'importe quel thread).
public static class Overlay
{
    static OverlayWindow? _win;

    public static void Init(OverlayWindow w) => _win = w;

    public static void Vous(string texte)   => Poster(() => _win?.AjouterBulle(texte, true));
    public static void Claude(string texte) => Poster(() => _win?.AjouterBulle(texte, false));
    public static void Etat(bool ecoute)     => Poster(() => _win?.DefinirEcoute(ecoute));

    // Bulle bleue « en direct » : affichée tant que tu parles, mise à jour au fil
    // de la transcription, puis retirée quand l'écoute se termine.
    public static void Live(string texte)    => Poster(() => _win?.MajLive(texte));
    public static void FinLive()             => Poster(() => _win?.FinLive());

    // Liste des tâches en cours (vient des TodoWrite émis par Claude).
    public static void Todos(IReadOnlyList<(string contenu, string statut)> todos)
        => Poster(() => _win?.MajTodos(todos));

    // Réaffiche les derniers messages (commande « historique »).
    public static void Historique(IReadOnlyList<(string qui, string texte)> items)
        => Poster(() => _win?.AfficherHistorique(items));

    // true = Claude travaille (rouge) ; false = Claude attend ta réponse (vert).
    public static void Travail(bool occupe)  => Poster(() => _win?.DefinirTravail(occupe));

    static void Poster(Action a)
    {
        try { Dispatcher.UIThread.Post(a); } catch { /* UI pas encore prête */ }
    }
}

public class OverlayWindow : Window
{
    readonly StackPanel _bulles;
    readonly OctopusControl _poulpe;
    readonly Ellipse _statut;
    readonly StackPanel _todos;        // liste des tâches en cours, à droite du poulpe
    readonly Border _todosCadre;
    Border? _bulleLive;          // bulle bleue « en direct » pendant que tu parles

    public OverlayWindow(string nom, int index)
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // Chaque agent se place un peu plus à droite pour ne pas se superposer.
        Position = new PixelPoint(24 + index * 360, 24);

        // Couleur propre à chaque agent (corps + contour + joue dérivés).
        var (corps, contour, joue) = Palette(index);
        _poulpe = new OctopusControl
        {
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Left,
            Corps = corps,
            Contour = contour,
            Joue = joue
        };

        // Rond de statut (en haut à gauche pour laisser le « + » en haut à droite).
        _statut = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = _vert,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 2, 0, 0)
        };

        // Petit « + » en haut à droite : crée un nouvel agent.
        var plus = new Button
        {
            Content = "+",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 15,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 0, 0)
        };
        plus.Click += (_, _) => Program.LancerNouvelAgent(null);

        // Petit couteau en bas à droite : ferme cet agent (et arrête son modèle).
        var couteau = new Button
        {
            Content = "🔪",
            Width = 22,
            Height = 22,
            Padding = new Thickness(0),
            FontSize = 12,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(11),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 0)
        };
        ToolTip.SetTip(couteau, "Fermer cet agent");
        couteau.Click += (_, _) => Program.TuerCetAgent();

        // Étiquette du nom « sur le front » du poulpe.
        var etiquette = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#99000000")),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(5, 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0),
            Child = new TextBlock
            {
                Text = Capitaliser(nom),
                Foreground = Brushes.White,
                FontSize = 10,
                FontWeight = FontWeight.Bold
            }
        };

        // Poulpe + rond de statut + bouton + + étiquette du nom, superposés.
        var tete = new Grid { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left };
        tete.Children.Add(_poulpe);
        tete.Children.Add(etiquette);
        tete.Children.Add(_statut);
        tete.Children.Add(plus);
        tete.Children.Add(couteau);

        // Info-bulle : toutes les commandes vocales (au survol du poulpe).
        ToolTip.SetTip(_poulpe,
            "Commandes vocales :\n" +
            $"• dis « {nom} » pour me réveiller\n" +
            "• « historique » — revoir les derniers messages\n" +
            "• « nouvel agent » — créer un poulpe (ou le bouton +)\n" +
            "• « que fais-tu » — mon statut\n" +
            "• « annule » — abandonner la dictée\n" +
            "• « restart » — recompiler et relancer");

        // Panneau de tâches (to-do), affiché à droite du poulpe quand Claude en a.
        _todos = new StackPanel { Spacing = 3 };
        _todosCadre = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#8C2A2A2A")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 8),
            MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            IsVisible = false,
            Child = _todos
        };

        // Ligne du haut : poulpe à gauche, tâches à droite.
        var haut = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        haut.Children.Add(tete);
        haut.Children.Add(_todosCadre);

        _bulles = new StackPanel { Spacing = 6 };

        var racine = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8
        };
        racine.Children.Add(haut);
        racine.Children.Add(_bulles);
        Content = racine;
    }

    // Petite réaction visuelle quand on est en écoute.
    public void DefinirEcoute(bool ecoute)
    {
        _poulpe.Opacity = ecoute ? 1.0 : 0.85;
    }

    // Couleur du rond de statut.
    public void DefinirTravail(bool occupe)
    {
        _statut.Fill = occupe ? _rouge : _vert;
    }

    static readonly IBrush _vert  = new SolidColorBrush(Color.Parse("#3FBF5F"));
    static readonly IBrush _rouge = new SolidColorBrush(Color.Parse("#E0483D"));
    static readonly IBrush _bleu  = new SolidColorBrush(Color.Parse("#8C2F6FB0"));

    // Affiche / met à jour la bulle bleue « en direct » avec ce que tu dis.
    public void MajLive(string texte)
    {
        texte = texte?.Trim() ?? "";
        if (texte.Length > 220) texte = texte[..220] + "…";

        if (_bulleLive is null)
        {
            _bulleLive = new Border
            {
                Background = _bleu,
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(11, 7),
                MaxWidth = 300,
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = new TextBlock
                {
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.White,
                    FontSize = 13
                }
            };
            _bulles.Children.Add(_bulleLive);
        }

        ((TextBlock)_bulleLive.Child!).Text = "🗣  " + (texte.Length == 0 ? "…" : texte);
    }

    // Retire la bulle « en direct » (fin d'écoute, envoi ou annulation).
    public void FinLive()
    {
        if (_bulleLive is not null)
        {
            _bulles.Children.Remove(_bulleLive);
            _bulleLive = null;
        }
    }

    static readonly IBrush _todoFait    = new SolidColorBrush(Color.Parse("#7FBF8F"));
    static readonly IBrush _todoActif   = new SolidColorBrush(Color.Parse("#FFFFFF"));
    static readonly IBrush _todoAttente = new SolidColorBrush(Color.Parse("#B0B0B0"));

    // Met à jour la liste des tâches à droite du poulpe (vide -> panneau masqué).
    public void MajTodos(IReadOnlyList<(string contenu, string statut)> todos)
    {
        _todos.Children.Clear();
        if (todos is null || todos.Count == 0)
        {
            _todosCadre.IsVisible = false;
            return;
        }

        foreach (var (contenu, statut) in todos)
        {
            var (glyphe, brosse, barre) = statut switch
            {
                "completed"   => ("✔", _todoFait, true),
                "in_progress" => ("▶", _todoActif, false),
                _             => ("○", _todoAttente, false),
            };

            var tb = new TextBlock
            {
                Text = glyphe + "  " + contenu,
                TextWrapping = TextWrapping.Wrap,
                Foreground = brosse,
                FontSize = 12
            };
            if (barre) tb.TextDecorations = TextDecorations.Strikethrough;
            _todos.Children.Add(tb);
        }
        _todosCadre.IsVisible = true;
    }

    // Ajoute une bulle : « vous » à gauche en bleu, Claude en gris.
    public void AjouterBulle(string texte, bool vous, int vieSecondes = 15, bool plafonner = true)
    {
        texte = texte?.Trim() ?? "";
        if (texte.Length == 0) return;
        if (texte.Length > 220) texte = texte[..220] + "…";

        var bulle = new Border
        {
            Background = new SolidColorBrush(Color.Parse(vous ? "#8C2F6FB0" : "#8C3A3A3A")),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(11, 7),
            MaxWidth = 300,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = (vous ? "🗣  " : "🐙  ") + texte,
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.White,
                FontSize = 13
            }
        };

        _bulles.Children.Add(bulle);
        if (plafonner)
            while (_bulles.Children.Count > 6)
                _bulles.Children.RemoveAt(0);

        // La bulle s'efface toute seule au bout d'un moment.
        DispatcherTimer.RunOnce(() => _bulles.Children.Remove(bulle), TimeSpan.FromSeconds(vieSecondes));
    }

    // Réaffiche une série de messages (commande « historique »), un peu plus longtemps.
    public void AfficherHistorique(IReadOnlyList<(string qui, string texte)> items)
    {
        if (items is null) return;
        foreach (var (qui, texte) in items)
            AjouterBulle(texte, qui == "Vous", 25, plafonner: false);
    }

    // Couleur propre à chaque agent, dérivée de son index.
    static (IBrush corps, IBrush contour, IBrush joue) Palette(int index)
    {
        string[] bases = { "#E0875A", "#5A9BE0", "#5AC07A", "#A87AD0",
                           "#E07AA8", "#4FB0B0", "#E0B85A", "#E05A5A" };
        var c = Color.Parse(bases[((index % bases.Length) + bases.Length) % bases.Length]);
        return (new SolidColorBrush(c),
                new SolidColorBrush(Assombrir(c, 0.65)),
                new SolidColorBrush(Eclaircir(c, 0.35)));
    }

    static Color Assombrir(Color c, double f)
        => Color.FromRgb((byte)(c.R * f), (byte)(c.G * f), (byte)(c.B * f));

    static Color Eclaircir(Color c, double f)
        => Color.FromRgb((byte)(c.R + (255 - c.R) * f),
                         (byte)(c.G + (255 - c.G) * f),
                         (byte)(c.B + (255 - c.B) * f));

    static string Capitaliser(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}

// Poulpe dessiné pixel par pixel (style 2D pixel-art, palette corail originale).
public class OctopusControl : Control
{
    // 12 × 12. '.' = transparent, B = corps, D = contour, E = œil,
    // P = pupille, C = joue.
    static readonly string[] Motif =
    {
        "...DDDDDD...",
        "..DBBBBBBD..",
        ".DBBBBBBBBD.",
        "DBBBBBBBBBBD",
        "DBBEEBBEEBBD",
        "DBBEPBBEPBBD",
        "DBBEEBBEEBBD",
        "DBCBBBBBBCBD",
        "DBBBBBBBBBBD",
        ".DBBBBBBBBD.",
        ".BDBBDBBDBD.",
        ".D.D.D.D.D..",
    };

    // Couleurs propres à cet agent (par défaut : palette corail d'origine).
    public IBrush Corps   { get; set; } = new SolidColorBrush(Color.Parse("#E0875A"));
    public IBrush Contour { get; set; } = new SolidColorBrush(Color.Parse("#A85A33"));
    public IBrush Joue    { get; set; } = new SolidColorBrush(Color.Parse("#F2B79C"));

    public override void Render(DrawingContext ctx)
    {
        int lignes = Motif.Length;
        int cols   = Motif[0].Length;
        double cw = Bounds.Width  / cols;
        double cha = Bounds.Height / lignes;

        for (int r = 0; r < lignes; r++)
        {
            var ligne = Motif[r];
            for (int c = 0; c < ligne.Length; c++)
            {
                var brosse = Couleur(ligne[c]);
                if (brosse is null) continue;
                // +0.6 pour éviter les lignes fines entre pixels.
                ctx.FillRectangle(brosse, new Rect(c * cw, r * cha, cw + 0.6, cha + 0.6));
            }
        }
    }

    IBrush? Couleur(char c) => c switch
    {
        'B' => Corps,
        'D' => Contour,
        'E' => _oeil,
        'P' => _pupille,
        'C' => Joue,
        _   => null,
    };

    static readonly IBrush _oeil    = new SolidColorBrush(Color.Parse("#FFFFFF"));
    static readonly IBrush _pupille = new SolidColorBrush(Color.Parse("#2B2B2B"));
}
