using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ArchiveStore
{
    // ======================================================================
    // Code-behind de la fenêtre « DLC et patchs ».
    //
    // RÔLE : quand des DLC ou des patchs ont été trouvés à côté du jeu, laisser l'utilisateur
    // COCHER, fichier par fichier, ce qu'il veut installer, en lui montrant la place que cela
    // prend sur le disque de destination (mêmes lignes et même jauge que la fenêtre
    // d'installation du jeu). Deux usages :
    //   - AVANT l'installation d'un jeu (showGame = true) : « aucun extra coché » veut dire
    //     « le jeu seul » ;
    //   - depuis le menu clic droit d'un jeu DÉJÀ installé (showGame = false) : on ne parle plus
    //     du jeu, il faut cocher au moins une case, et un bouton « Déjà installés » permet de
    //     NOTER des extras comme déjà installés (sans rien lancer) : utile pour un jeu installé
    //     avant que le plugin ne mémorise ses extras.
    //
    // PÉDAGOGIE : comme InstallDialog, cette fenêtre ne fait QU'AFFICHER des chiffres déjà
    // calculés par le plugin (thread d'arrière-plan) : aucun accès disque ici, donc aucune
    // lenteur sur le thread UI. Le choix est lu dans Choice et SelectedInstallers après ShowDialog().
    // Les cases sont créées PAR CODE (une par installeur) : leur nombre n'est pas connu à l'avance.
    //
    // MANETTE / CLAVIER (via ConsoleWindow) :
    //   haut/bas : parcourir les cases puis les boutons du bas ; gauche/droite : entre les boutons ;
    //   A : cocher/décocher la case (ou valider le bouton) ; X : tout cocher ; Y : tout décocher ; B : annuler.
    // ======================================================================
    public partial class ExtrasDialog : ConsoleWindow
    {
        /// <summary>Ce que l'utilisateur a décidé.</summary>
        public enum UserChoice { Cancel, Install, MarkInstalled }

        /// <summary>Décision retenue (Cancel si la fenêtre est fermée sans réponse).</summary>
        public UserChoice Choice { get; private set; } = UserChoice.Cancel;

        /// <summary>Installeurs cochés, dans l'ordre d'exécution (DLC puis patchs). Valable si Choice n'est pas Cancel.</summary>
        public List<ExtraInstaller> SelectedInstallers { get; private set; } = new List<ExtraInstaller>();

        // Couleurs de la jauge : mêmes que InstallDialog.
        private static readonly Brush VertJauge =
            new SolidColorBrush(Color.FromRgb(166, 227, 161)); // #a6e3a1
        private static readonly Brush Orange =
            new SolidColorBrush(Color.FromRgb(250, 179, 135)); // #fab387
        private static readonly Brush Rouge =
            new SolidColorBrush(Color.FromRgb(243, 139, 168)); // #f38ba8

        // Une ligne = une case + l'installeur qu'elle représente.
        private sealed class Row
        {
            public CheckBox Box;
            public ExtraInstaller Installer;
            public bool Manual;   // fichier NON RECONNU (lancement manuel) : décoché par défaut, jamais coché par « Tout cocher »
        }

        private readonly List<Row> _rows = new List<Row>();
        private readonly ExtraScanResult _extras;
        private readonly bool _showGame;
        private readonly long _gameBytes;
        private readonly long _freeBytes;

        // gameName      : nom du jeu (titre de la fenêtre).
        // headline      : « 2 DLC et 1 patch détectés ».
        // showGame      : vrai avant l'installation d'un jeu, faux pour un jeu déjà installé.
        // gameBytes     : taille du jeu SEUL (archive), extras exclus (ignorée si showGame est faux).
        // extras        : les installeurs proposés (déjà triés DLC puis patchs) et les tailles de leurs dossiers.
        // freeBytes     : espace libre du disque de destination (-1 = inconnu).
        //                 Les fichiers de type NON RECONNU (extras.Manual) sont proposés dans un groupe à part,
        //                 décochés par défaut : le plugin demandera confirmation avant de les lancer.
        // offerMarkInstalled : vrai = afficher le bouton « Déjà installés » (menu clic droit).
        // warnNoMemory  : vrai = avertir que le plugin ne sait pas ce qui est déjà installé (menu clic droit,
        //                 jeu installé avant la mémoire des extras).
        public ExtrasDialog(string gameName, string headline, bool showGame, long gameBytes,
                            ExtraScanResult extras, long freeBytes,
                            bool offerMarkInstalled, bool warnNoMemory)
        {
            InitializeComponent();

            _extras = extras;
            _showGame = showGame;
            _gameBytes = showGame ? gameBytes : 0;
            _freeBytes = freeBytes;

            Header.Game = gameName;

            // Phrase en plus quand des dossiers « en fichiers » (sans installeur) sont proposés : ils sont COPIÉS.
            string copyLine = extras.Installers.Any(x => x.IsCopy)
                ? "\nLes dossiers marqués « copie » ne contiennent pas d'installeur : leur contenu est copié dans le " +
                  "dossier du jeu, avec la même arborescence. Les fichiers du même nom sont remplacés, rien n'est supprimé."
                : "";

            // Phrase en plus quand des fichiers non reconnus sont proposés.
            string manualLine = extras.Manual.Count > 0
                ? "\nLes fichiers « non reconnus » ne sont pas cochés : s'ils le sont, ils s'ouvrent avec leur propre " +
                  "fenêtre d'installation (pas de mode silencieux), après les autres."
                : "";

            TitleText.Text = $"{headline} pour « {gameName} »";
            if (showGame)
            {
                DescriptionText.Text =
                    "Le jeu est installé en premier, puis les DLC (d'abord) et les patchs, " +
                    "l'un après l'autre, dès que le jeu est terminé. Cochez ce que vous voulez installer ; " +
                    "sans aucune case cochée, seul le jeu est installé." + copyLine + manualLine;
            }
            else
            {
                DescriptionText.Text =
                    "Le jeu est déjà installé. Cochez ce que vous voulez ajouter : les DLC d'abord, " +
                    "puis les patchs, l'un après l'autre." + copyLine + manualLine;
            }

            // Ligne « jeu » : seulement avant l'installation du jeu.
            if (showGame)
                GameSizeText.Text = gameBytes > 0
                    ? "Taille du jeu : " + ArchiveScanner.FormatSize(gameBytes)
                    : "Taille du jeu : indéterminée";
            else
                GameSizeText.Visibility = Visibility.Collapsed;

            // Bouton « Déjà installés » et avertissement : menu clic droit seulement.
            if (offerMarkInstalled)
            {
                BtnMarkInstalled.Visibility = Visibility.Visible;
                if (warnNoMemory)
                {
                    MemoryWarning.Text =
                        "⚠ Ce jeu a été installé avant que le plugin ne mémorise ses extras : il ne sait pas lesquels " +
                        "sont déjà installés et vous les reproposera. Décochez ceux que vous avez déjà, ou cochez-les " +
                        "puis cliquez sur « Déjà installés » pour qu'ils ne soient plus proposés.";
                    MemoryWarning.Visibility = Visibility.Visible;
                }
            }

            BuildList(extras);

            FreeText.Text = freeBytes >= 0
                ? "Espace libre sur le disque de destination : " + ArchiveScanner.FormatSize(freeBytes)
                : "Espace libre sur le disque de destination : indisponible";

            SizeNote.Text =
                "Avec un installeur, ces tailles sont celles des archives : une fois installés, " +
                "le jeu et ses extras peuvent occuper davantage de place.";

            Refresh();

            // Le libellé de la touche A suit l'élément qui a le focus (Cocher / Valider).
            GotKeyboardFocus += (s, e) => UpdateHintA(e.NewFocus);
        }

        // Crée une case par installeur, sous un titre « DLC » puis « Patchs » (la liste est déjà triée
        // DLC d'abord). Le texte de la case est le chemin relatif (ex. « DLC\setup_artbook.exe »).
        private void BuildList(ExtraScanResult extras)
        {
            var dlc = extras.Installers.Where(x => x.IsDlc).ToList();
            var patches = extras.Installers.Where(x => !x.IsDlc).ToList();

            AddGroup("DLC", dlc, false);
            AddGroup("Patchs", patches, false);

            // Fichiers de type NON RECONNU : à part, décochés par défaut (lancement manuel).
            AddGroup("Non reconnus — lancement manuel (DLC)", extras.Manual.Where(x => x.IsDlc).ToList(), true);
            AddGroup("Non reconnus — lancement manuel (patchs)", extras.Manual.Where(x => !x.IsDlc).ToList(), true);
        }

        private void AddGroup(string title, List<ExtraInstaller> installers, bool manual)
        {
            if (installers.Count == 0) return;

            // Titre de groupe en capitales, comme dans la maquette (style GroupStyle de ConsoleStyle.xaml).
            ListPanel.Children.Add(new TextBlock
            {
                Text = $"{title} ({installers.Count})".ToUpperInvariant(),
                Style = (Style)FindResource("GroupStyle"),
                Margin = new Thickness(0, _rows.Count == 0 ? 8 : 14, 0, 6)
            });

            foreach (var installer in installers)
            {
                var box = new CheckBox
                {
                    Content = installer.IsCopy
                        ? installer.Key + "   (copie des fichiers)"
                        : (string.IsNullOrEmpty(installer.Key) ? System.IO.Path.GetFileName(installer.Path) : installer.Key),
                    IsChecked = !manual,
                    ToolTip = installer.Path
                };
                // Fichiers non reconnus : texte orange (ils demandent plus d'attention).
                if (manual) box.Foreground = Orange;

                // Les gestionnaires sont attachés APRÈS la valeur initiale (sinon ils se déclencheraient
                // pendant la mise en place, avant que Refresh n'ait tous ses éléments).
                box.Checked += (s, e) => Refresh();
                box.Unchecked += (s, e) => Refresh();

                ListPanel.Children.Add(box);
                _rows.Add(new Row { Box = box, Installer = installer, Manual = manual });
            }
        }

        private List<ExtraInstaller> CheckedInstallers()
            => _rows.Where(r => r.Box.IsChecked == true).Select(r => r.Installer).ToList();

        // Recalcule le total, la jauge, l'avertissement et les boutons selon les cases cochées.
        private void Refresh()
        {
            var selected = CheckedInstallers();
            bool anyExtra = selected.Count > 0;

            long extrasBytes = _extras.BytesFor(selected);
            long total = _gameBytes + extrasBytes;

            // Ligne « Total »
            if (total > 0)
                TotalText.Text = "Total à installer : " + ArchiveScanner.FormatSize(total);
            else if (_showGame || anyExtra)
                TotalText.Text = "Total à installer : indéterminé";
            else
                TotalText.Text = "Rien n'est coché.";

            // Jauge : visible seulement si on connaît le total et l'espace libre.
            if (total > 0 && _freeBytes > 0)
            {
                double ratio = (double)total / _freeBytes;
                SpaceBar.Visibility = Visibility.Visible;
                SpaceBar.Value = Math.Min(ratio, 1.0);
                SpaceBar.Foreground = ratio > 1.0 ? Rouge
                                    : ratio > 0.8 ? Orange
                                    : VertJauge;
            }
            else
            {
                SpaceBar.Visibility = Visibility.Collapsed;
            }

            // Espace : un total inconnu (0) ne bloque rien.
            bool fits = total <= 0 || ArchiveScanner.HasEnoughSpace(total, _freeBytes);
            if (!fits)
            {
                long missing = ArchiveScanner.GetMissingSpace(total, _freeBytes);
                bool gameFits = _showGame && ArchiveScanner.HasEnoughSpace(_gameBytes, _freeBytes);

                SpaceWarning.Text =
                    "⚠ Espace insuffisant : il manque environ " +
                    ArchiveScanner.FormatSize(missing) + " (marge de sécurité comprise). " +
                    (anyExtra && (gameFits || !_showGame)
                        ? "Décochez des fichiers ou libérez de la place sur le disque de destination."
                        : "Libérez de la place sur le disque de destination, puis relancez l'installation.");
                SpaceWarning.Visibility = Visibility.Visible;
                SpaceBar.Foreground = Rouge;
            }
            else
            {
                SpaceWarning.Visibility = Visibility.Collapsed;
            }

            // Bouton « Installer » : libellé selon ce qui sera fait, utilisable seulement si ça rentre
            // (et, pour un jeu déjà installé, s'il y a quelque chose de coché).
            if (_showGame)
                BtnInstall.Content = anyExtra ? "Installer le jeu + la sélection" : "Installer le jeu seul";
            else
                BtnInstall.Content = "Installer la sélection";

            BtnInstall.IsEnabled = fits && (_showGame || anyExtra);

            // « Déjà installés » ne dépend pas de la place (rien n'est écrit sur le disque) : il suffit
            // d'avoir coché quelque chose.
            BtnMarkInstalled.IsEnabled = anyExtra;
        }

        // « Tout cocher » ne coche JAMAIS les fichiers non reconnus (il faut les cocher exprès) ;
        // « Tout décocher » décoche tout.
        private void SetAll(bool value)
        {
            foreach (var r in _rows)
            {
                if (value && r.Manual) continue;
                r.Box.IsChecked = value; // chaque changement appelle Refresh
            }
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            // Comme dans la maquette : la première case a le focus (A la coche/décoche aussitôt).
            // Sans case (cas théorique), on retombe sur le bouton principal, puis « Annuler ».
            if (_rows.Count > 0) return _rows[0].Box;
            return BtnInstall.IsEnabled ? (UIElement)BtnInstall : BtnCancel;
        }

        protected override IList<UIElement> NavigationRows
        {
            get { return _rows.Select(r => (UIElement)r.Box).ToList(); }
        }

        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnCancel, BtnMarkInstalled, BtnInstall }; }
        }

        // A / Entrée : sur une case → cocher/décocher ; sur un bouton → le valider.
        protected override void OnConfirm()
        {
            var box = CurrentFocus as CheckBox;
            if (box != null)
            {
                box.IsChecked = box.IsChecked != true;
                return;
            }
            ClickFocusedButton();
        }

        protected override void OnActionX()
        {
            SetAll(true);
        }

        protected override void OnActionY()
        {
            SetAll(false);
        }

        private void UpdateHintA(IInputElement focused)
        {
            HintA.Label = focused is CheckBox ? "Cocher" : "Valider";
        }

        private void HintX_Click(object sender, MouseButtonEventArgs e)
        {
            SetAll(true);
        }

        private void HintY_Click(object sender, MouseButtonEventArgs e)
        {
            SetAll(false);
        }

        private void BtnInstall_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnInstall.IsEnabled) return;
            SelectedInstallers = CheckedInstallers();
            Choice = UserChoice.Install;
            CloseDialog(true);
        }

        private void BtnMarkInstalled_Click(object sender, RoutedEventArgs e)
        {
            if (!BtnMarkInstalled.IsEnabled) return;
            SelectedInstallers = CheckedInstallers();
            Choice = UserChoice.MarkInstalled;
            CloseDialog(true);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = UserChoice.Cancel;
            CloseDialog(false);
        }

        // B / Échap : annuler (Choice reste « Cancel »).
        protected override void OnCancelPressed()
        {
            Choice = UserChoice.Cancel;
            CloseDialog(false);
        }
    }
}
