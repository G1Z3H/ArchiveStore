using System;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ArchiveStore
{
    // ======================================================================
    // ToastWindow — petite carte en bas à droite de l'écran (« toast »).
    //
    // CE QUE C'EST : une fenêtre WPF dessinée par le plugin (ce n'est ni une notification Windows ni une
    // fonction du SDK de Playnite). Technique déjà utilisée et validée dans le plugin Steam Silent.
    //
    // PÉDAGOGIE — pourquoi elle ne gêne jamais un jeu :
    //   • ShowActivated="False" : elle s'affiche SANS prendre le focus (le jeu garde clavier et manette) ;
    //   • Topmost="True"        : elle reste au-dessus des autres fenêtres ;
    //   • IsHitTestVisible="False" + Focusable="False" : aucun clic ne la touche (le clic traverse) ;
    //   • elle disparaît toute seule après VisibleSeconds secondes (fondu en entrée et en sortie).
    //
    // Le choix d'afficher ou non un toast, leur ordre et leur empilement sont décidés par ToastHelper :
    // cette classe ne sait que s'afficher à l'endroit demandé.
    // ======================================================================
    public partial class ToastWindow : Window
    {
        private const double VisibleSeconds = 7.0;
        private const double ScreenMargin = 8.0;

        private readonly DispatcherTimer _timer;
        private double _bottomOffset;      // hauteur occupée par les toasts situés SOUS celui-ci
        private bool _fading;

        public ToastWindow(string title, string message, string hint)
        {
            InitializeComponent();
            TitleText.Text = title ?? "";
            MessageText.Text = message ?? "";

            if (!string.IsNullOrEmpty(hint))
            {
                HintText.Text = hint;
                HintText.Visibility = Visibility.Visible;
            }

            Opacity = 0;
            Loaded += OnToastLoaded;
            Closed += (s, e) => { if (_timer != null) _timer.Stop(); };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(VisibleSeconds) };
            _timer.Tick += (s, e) => { _timer.Stop(); FadeOutAndClose(); };
        }

        private void OnToastLoaded(object sender, RoutedEventArgs e)
        {
            PlaceAt(_bottomOffset);
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(250)));
            _timer.Start();

            // Maintenant que sa HAUTEUR est connue, ToastHelper replace toute la pile.
            ToastHelper.Relayout();
        }

        /// <summary>
        /// Place le toast dans le coin inférieur droit de la zone de travail (hors barre des tâches),
        /// remonté de bottomOffset pixels (les toasts déjà affichés en dessous). Thread UI.
        /// </summary>
        public void PlaceAt(double bottomOffset)
        {
            _bottomOffset = bottomOffset;
            if (!IsLoaded) return; // la hauteur n'est pas encore connue : OnToastLoaded s'en charge

            var area = SystemParameters.WorkArea;
            Left = area.Right - ActualWidth - ScreenMargin;
            Top = area.Bottom - ActualHeight - ScreenMargin - bottomOffset;
        }

        private void FadeOutAndClose()
        {
            if (_fading) return;
            _fading = true;

            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(400));
            fade.Completed += (s, e) =>
            {
                try { Close(); }
                catch (InvalidOperationException) { /* déjà fermée */ }
            };
            BeginAnimation(OpacityProperty, fade);
        }
    }
}
