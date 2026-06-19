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
            var win = new OverlayWindow();
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

    public OverlayWindow()
    {
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Topmost = true;
        ShowInTaskbar = false;
        CanResize = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Position = new PixelPoint(24, 24);

        _poulpe = new OctopusControl
        {
            Width = 96,
            Height = 96,
            HorizontalAlignment = HorizontalAlignment.Left
        };

        // Rond de statut : vert = Claude attend ta réponse, rouge = il travaille.
        _statut = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = _vert,
            Stroke = Brushes.White,
            StrokeThickness = 2,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0)
        };

        // Poulpe + rond de statut superposés (le rond en haut à droite du poulpe).
        var tete = new Grid { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left };
        tete.Children.Add(_poulpe);
        tete.Children.Add(_statut);

        _bulles = new StackPanel { Spacing = 6 };

        var racine = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
            Width = 320
        };
        racine.Children.Add(tete);
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

    // Ajoute une bulle : « vous » à gauche en bleu, Claude en gris.
    public void AjouterBulle(string texte, bool vous)
    {
        texte = texte?.Trim() ?? "";
        if (texte.Length == 0) return;
        if (texte.Length > 220) texte = texte[..220] + "…";

        var bulle = new Border
        {
            Background = new SolidColorBrush(Color.Parse(vous ? "#2F6FB0" : "#3A3A3A")),
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
        while (_bulles.Children.Count > 6)
            _bulles.Children.RemoveAt(0);

        // La bulle s'efface toute seule au bout d'un moment.
        DispatcherTimer.RunOnce(() => _bulles.Children.Remove(bulle), TimeSpan.FromSeconds(15));
    }
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

    static IBrush? Couleur(char c) => c switch
    {
        'B' => _corps,
        'D' => _contour,
        'E' => _oeil,
        'P' => _pupille,
        'C' => _joue,
        _   => null,
    };

    static readonly IBrush _corps   = new SolidColorBrush(Color.Parse("#E0875A"));
    static readonly IBrush _contour = new SolidColorBrush(Color.Parse("#A85A33"));
    static readonly IBrush _oeil    = new SolidColorBrush(Color.Parse("#FFFFFF"));
    static readonly IBrush _pupille = new SolidColorBrush(Color.Parse("#2B2B2B"));
    static readonly IBrush _joue    = new SolidColorBrush(Color.Parse("#F2B79C"));
}
