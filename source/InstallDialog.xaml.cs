using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // Code-behind du dialogue de confirmation d'installation (ÉTAPE B).
    //
    // RÔLE : afficher le diagnostic automatique (résultat de l'analyse du
    // dossier source) et laisser l'utilisateur confirmer ou choisir une
    // méthode différente. C'est la seule interaction humaine de l'install.
    //
    // Le résultat est exposé via SelectedMode, lu par ArchiveStorePlugin
    // après ShowDialog() == true.
    //
    // MANETTE / CLAVIER (via ConsoleWindow) :
    //   haut/bas : parcourir la destination, les deux options, puis les boutons du bas ;
    //   A        : sur une option → la choisir ; sur la destination → changer ; sur un bouton → le valider ;
    //   B        : annuler.
    // ======================================================================
    public partial class InstallDialog : ConsoleWindow
    {
        /// <summary>Méthode d'installation retenue par l'utilisateur.</summary>
        public enum Mode { Installer, Copy }

        /// <summary>Mode choisi (lu par le plugin après ShowDialog()==true).</summary>
        public Mode SelectedMode { get; private set; }

        private readonly ScanResult _scanResult;
        private readonly PluginSettings _settings;

        // PLUSIEURS DESTINATIONS : liste proposée (null = une seule destination, rien à choisir).
        private readonly List<DestinationChoice> _destinations;

        /// <summary>
        /// Racine de destination retenue (lue par le plugin après ShowDialog()==true), ou null s'il n'y avait
        /// qu'une destination (le plugin garde alors la destination prévue).
        /// </summary>
        public string SelectedDestinationRoot { get; private set; }

        // Couleurs de la jauge d'espace disque (palette du thème : Argent s'il est actif, sinon Mocha).
        private static readonly Brush VertJauge =
            ThemePalette.Brush(ThemePalette.Green);
        private static readonly Brush Orange =
            ThemePalette.Brush(ThemePalette.Orange);
        private static readonly Brush Rouge =
            ThemePalette.Brush(ThemePalette.Red);

        // fullscreen : vrai si Playnite est en mode Plein écran. Sert uniquement à AVERTIR, dans le cas ambigu,
        // que l'installeur s'ouvrira avec sa propre fenêtre, qui ne se commande pas à la manette.
        private readonly bool _fullscreen;

        public InstallDialog(ScanResult scanResult, PluginSettings settings, List<DestinationChoice> destinations = null,
                             bool fullscreen = false)
        {
            InitializeComponent();
            _scanResult = scanResult;
            _settings = settings;
            _destinations = (destinations != null && destinations.Count > 1) ? destinations : null;
            _fullscreen = fullscreen;

            Header.Game = ArchiveScanner.CleanGameName(scanResult.FolderName);

            SetupUI();
            SetupDestinations();

            // Le libellé de la touche A suit l'élément qui a le focus (Valider / Choisir / Changer).
            GotKeyboardFocus += (s, e) => UpdateHintA(e.NewFocus);
        }

        // ------------------------------------------------------------------
        // PLUSIEURS DESTINATIONS : présélectionne la destination prévue (celle du ScanResult).
        // Les espaces libres sont DÉJÀ calculés par le plugin : aucun accès disque ici.
        // ------------------------------------------------------------------
        private void SetupDestinations()
        {
            if (_destinations == null) return;

            var planned = _destinations.FirstOrDefault(d =>
                              ArchiveScanner.SamePath(Path.Combine(d.FolderPath, _scanResult.FolderName), _scanResult.DestinationDirectory))
                          ?? _destinations[0];

            DestinationPanel.Visibility = Visibility.Visible;
            ApplyDestination(planned);
        }

        // Clic (ou bouton A) sur la destination : on ouvre la liste des destinations dans une
        // fenêtre console (celle de « Choisir la destination »), utilisable à la manette.
        private void BtnDestination_Click(object sender, RoutedEventArgs e)
        {
            if (_destinations == null) return;

            var picker = new DestinationPickerDialog(
                new List<string> { ArchiveScanner.CleanGameName(_scanResult.FolderName) },
                _destinations,
                SelectedDestinationRoot,
                "Le jeu sera installé dans la destination choisie. L'espace libre affiché se met à jour.")
            { Owner = this };

            if (picker.ShowDialog() != true) return;

            var choice = _destinations.FirstOrDefault(d => ArchiveScanner.SamePath(d.FolderPath, picker.SelectedRoot));
            if (choice != null) ApplyDestination(choice);

            // Le focus revient sur le sélecteur (le bouton Installer a pu être grisé entre-temps).
            BtnDestination.Focus();
        }

        // L'utilisateur change de destination : on met à jour tout ce qui en dépend
        // (espace libre, jauge, avertissement, bouton Installer, rappel du dossier à choisir).
        private void ApplyDestination(DestinationChoice choice)
        {
            SelectedDestinationRoot = choice.FolderPath;
            DestinationLabel.Text = choice.Label;
            DestinationLabel.Foreground = choice.IsAvailable
                ? (Brush)FindResource("TextBrush")
                : (Brush)FindResource("RedBrush");

            _scanResult.DestinationDirectory = Path.Combine(choice.FolderPath, _scanResult.FolderName);
            _scanResult.FreeSpaceBytes = choice.FreeBytes;

            // Rappel du dossier à choisir dans l'installeur (cas ambigu) : il suit la destination choisie.
            if (InstallerHint.Visibility == Visibility.Visible)
                InstallerHint.Text = "Dans l'installeur, choisissez EXACTEMENT ce dossier : " + _scanResult.DestinationDirectory;

            SetupSpaceInfo();

            // Destination absente (disque débranché) : on le dit et on grise Installer. Appelé APRÈS
            // SetupSpaceInfo, qui réinitialise l'avertissement et le bouton.
            if (!choice.IsAvailable)
            {
                SpaceWarning.Text =
                    "⚠ Cette destination n'est pas accessible (disque ou réseau débranché ?). " +
                    "Reconnectez-la, ou choisissez une autre destination dans la liste.";
                SpaceWarning.Visibility = Visibility.Visible;
                SpaceBar.Visibility = Visibility.Collapsed;
                BtnConfirm.IsEnabled = false;
            }
        }

        // ------------------------------------------------------------------
        // Configure le texte + la pré-sélection selon le type détecté.
        // Le titre est court ; le nom du fichier est sur sa propre ligne (en bleu, police à chasse fixe).
        // ------------------------------------------------------------------
        private void SetupUI()
        {
            string installerFile = string.IsNullOrEmpty(_scanResult.DetectedInstallerPath)
                ? null
                : Path.GetFileName(_scanResult.DetectedInstallerPath);

            switch (_scanResult.DetectedType)
            {
                case InstallerType.InnoSetup:
                    DiagnosticText.Text = "Installeur Inno Setup détecté";
                    ShowFileLine(installerFile);
                    DescriptionText.Text =
                        "L'installeur sera exécuté en mode totalement silencieux " +
                        "(aucune fenêtre), la destination sera imposée automatiquement.";
                    RadioInstaller.IsChecked = true;
                    break;

                case InstallerType.Nullsoft:
                    DiagnosticText.Text = "Installeur NSIS détecté";
                    ShowFileLine(installerFile);
                    DescriptionText.Text =
                        "L'installeur sera exécuté en mode silencieux, destination imposée.";
                    RadioInstaller.IsChecked = true;
                    break;

                case InstallerType.Msi:
                    DiagnosticText.Text = "Installeur MSI détecté";
                    ShowFileLine(installerFile);
                    DescriptionText.Text =
                        "Installation via msiexec en mode silencieux, destination imposée.";
                    RadioInstaller.IsChecked = true;
                    break;

                case InstallerType.None:
                    DiagnosticText.Text = "Jeu portable détecté (aucun installeur)";
                    DescriptionText.Text =
                        "Le dossier sera copié tel quel vers la destination. " +
                        "C'est la méthode adaptée aux jeux portabilisés.";
                    RadioCopy.IsChecked = true;
                    break;

                default: // Unknown : CAS AMBIGU (livraison B)
                    DiagnosticText.Text = "Situation ambiguë";
                    ShowFileLine(installerFile == null ? null : installerFile + " ressemble à un installeur");
                    DescriptionText.Text =
                        "Ce fichier n'est ni un installeur Inno Setup, ni NSIS, ni MSI : il ne peut pas être " +
                        "lancé en silence. À vous de décider. Si c'est bien un installeur, il s'ouvrira avec " +
                        "sa propre fenêtre. Si c'est un jeu portable, copiez le dossier.";

                    // L'option Installeur est ACTIVE ici (lancement avec la fenêtre de l'installeur, sans
                    // option silencieuse) et pré-cochée : un fichier qui ressemble à un installeur en est
                    // probablement un, et se tromper est sans conséquence (on peut annuler dans
                    // l'installeur), alors qu'une copie de plusieurs Go à tort fait perdre du temps.
                    RadioInstaller.Content = "Lancer l'installeur avec sa fenêtre (installation manuelle)";
                    RadioCopy.Content = "Copier le dossier (jeu portable)";
                    RadioInstaller.IsChecked = true;

                    InstallerHint.Text =
                        "Dans l'installeur, choisissez EXACTEMENT ce dossier : " +
                        (string.IsNullOrEmpty(_scanResult.DestinationDirectory) ? "(dossier du profil actif)" : _scanResult.DestinationDirectory);
                    InstallerHint.Visibility = Visibility.Visible;

                    // Plein écran : l'installeur lancé a SA fenêtre, que la manette ne sait pas piloter.
                    // On prévient (sans rien interdire) pour que le clavier et la souris soient à portée de main.
                    if (_fullscreen)
                    {
                        InstallerFullscreenWarning.Text =
                            "⚠ Cet installeur a sa propre fenêtre, qui ne se commande pas à la manette. " +
                            "Gardez un clavier et une souris à portée de main.";
                        InstallerFullscreenWarning.Visibility = Visibility.Visible;
                    }

                    CopyWarning.Text =
                        "⚠ Attention : si vous copiez un installeur, le bouton Jouer lancera l'installation.";
                    CopyWarning.Visibility = Visibility.Visible;
                    break;
            }

            SetupSpaceInfo();
        }

        private void ShowFileLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            FileText.Text = text;
            FileText.Visibility = Visibility.Visible;
        }

        // ------------------------------------------------------------------
        // Affiche la taille du jeu, l'espace libre, une jauge, et BLOQUE
        // l'installation si le disque de destination n'a pas assez de place.
        //
        // PÉDAGOGIE : ces chiffres sont calculés AVANT l'ouverture du dialogue
        // (étape A, thread d'arrière-plan). Le dialogue ne fait qu'AFFICHER :
        // aucun accès disque ici, donc aucune lenteur sur le thread UI.
        // ------------------------------------------------------------------
        private void SetupSpaceInfo()
        {
            long size = _scanResult.SourceSizeBytes;
            long free = _scanResult.FreeSpaceBytes; // -1 = inconnu

            SizeText.Text = size > 0
                ? "Taille du jeu : " + ArchiveScanner.FormatSize(size)
                : "Taille du jeu : indéterminée";

            FreeText.Text = free >= 0
                ? "Espace libre sur le disque de destination : " + ArchiveScanner.FormatSize(free)
                : "Espace libre sur le disque de destination : indisponible";

            // SetupSpaceInfo peut être rappelée (changement de destination) : on repart d'un état neutre.
            SpaceBar.Visibility = Visibility.Visible;
            SpaceWarning.Visibility = Visibility.Collapsed;
            BtnConfirm.IsEnabled = true;

            // Jauge : visible seulement si on connaît les deux valeurs.
            if (size > 0 && free > 0)
            {
                double ratio = (double)size / free;
                SpaceBar.Value = Math.Min(ratio, 1.0);

                // Vert = large ; orange = il restera peu de place ; rouge = ne rentre pas.
                SpaceBar.Foreground = ratio > 1.0 ? Rouge
                                    : ratio > 0.8 ? Orange
                                    : VertJauge;
            }
            else
            {
                SpaceBar.Visibility = Visibility.Collapsed;
            }

            // Espace insuffisant : message explicite + bouton "Installer" grisé.
            if (!ArchiveScanner.HasEnoughSpace(size, free))
            {
                long missing = ArchiveScanner.GetMissingSpace(size, free);
                SpaceWarning.Text =
                    "⚠ Espace insuffisant : il manque environ " + ArchiveScanner.FormatSize(missing) +
                    " (marge de sécurité comprise). Libérez de la place sur le disque de " +
                    "destination, puis relancez l'installation.";
                SpaceWarning.Visibility = Visibility.Visible;
                SpaceBar.Foreground = Rouge;
                BtnConfirm.IsEnabled = false;
            }

            // Avec un installeur, la taille affichée est celle de l'ARCHIVE (compressée) :
            // le jeu installé occupera souvent davantage. On le dit pour ne pas tromper.
            if (_scanResult.DetectedType == InstallerType.InnoSetup ||
                _scanResult.DetectedType == InstallerType.Nullsoft ||
                _scanResult.DetectedType == InstallerType.Msi)
            {
                SizeNote.Text =
                    "Avec un installeur, cette taille est celle de l'archive : une fois " +
                    "installé, le jeu peut occuper davantage de place.";
                SizeNote.Visibility = Visibility.Visible;
            }
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            // Focus initial sur le bouton principal → l'anneau de focus est visible dès l'ouverture
            // (essentiel à la manette / Plein écran). Si "Installer" est grisé (espace insuffisant),
            // un bouton désactivé ne peut pas recevoir le focus : on le donne à "Annuler".
            return BtnConfirm.IsEnabled ? (UIElement)BtnConfirm : BtnCancel;
        }

        protected override IList<UIElement> NavigationRows
        {
            // BtnDestination est ignoré automatiquement quand il est masqué (une seule destination).
            get { return new List<UIElement> { BtnDestination, RadioInstaller, RadioCopy }; }
        }

        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnCancel, BtnConfirm }; }
        }

        // A / Entrée : selon l'élément qui a le focus.
        protected override void OnConfirm()
        {
            if (RadioInstaller.IsKeyboardFocused || ReferenceEquals(CurrentFocus, RadioInstaller))
            {
                RadioInstaller.IsChecked = true;
                return;
            }
            if (RadioCopy.IsKeyboardFocused || ReferenceEquals(CurrentFocus, RadioCopy))
            {
                RadioCopy.IsChecked = true;
                return;
            }
            if (ReferenceEquals(CurrentFocus, BtnDestination))
            {
                BtnDestination_Click(BtnDestination, new RoutedEventArgs());
                return;
            }
            if (ClickFocusedButton()) return;

            // Rien de précis n'a le focus : A valide l'installation.
            Confirm();
        }

        private void UpdateHintA(IInputElement focused)
        {
            string label = "Valider";
            if (ReferenceEquals(focused, RadioInstaller) || ReferenceEquals(focused, RadioCopy)) label = "Choisir";
            else if (ReferenceEquals(focused, BtnDestination)) label = "Changer";
            HintA.Label = label;
        }

        // ------------------------------------------------------------------
        // Validation : on détermine le mode, puis on vérifie la cohérence.
        // ------------------------------------------------------------------
        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void Confirm()
        {
            if (!BtnConfirm.IsEnabled) return; // grisé (espace insuffisant, destination absente) : rien à faire

            SelectedMode = RadioInstaller.IsChecked == true
                ? Mode.Installer
                : Mode.Copy;

            // Détecte un choix CONTRAIRE à la détection automatique.
            bool mismatch =
                (_scanResult.DetectedType == InstallerType.None && SelectedMode == Mode.Installer) ||
                (_scanResult.DetectedType != InstallerType.None &&
                 _scanResult.DetectedType != InstallerType.Unknown &&
                 SelectedMode == Mode.Copy);

            if (mismatch)
            {
                // Second dialogue d'avertissement (exigence du cahier des charges), au style console
                // pour rester utilisable à la manette. "this" = fenêtre propriétaire.
                bool continuer = ConsoleConfirmDialog.Ask(
                    this,
                    "Confirmer ce choix ?",
                    "Votre choix est contraire à la détection automatique.\n\n" +
                    "• Copier un jeu qui possède un installeur ne l'installera pas correctement.\n" +
                    "• Exécuter un installeur sur un jeu portable est inutile.\n\n" +
                    "Voulez-vous vraiment continuer ?",
                    "Oui, continuer",
                    "Non, revenir");

                if (!continuer)
                    return; // on reste dans la fenêtre, l'utilisateur peut corriger
            }

            // Ferme la fenêtre avec le résultat « oui ».
            CloseDialog(true);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }
    }
}
