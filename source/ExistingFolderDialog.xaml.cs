using System.Collections.Generic;
using System.Windows;

namespace ArchiveStore
{
    /// <summary>Réponse de l'utilisateur dans la fenêtre « Ce dossier existe déjà ».</summary>
    public enum ExistingFolderChoice
    {
        /// <summary>Rien n'est fait (bouton Annuler, B ou Échap).</summary>
        Cancel,
        /// <summary>Le jeu est déclaré installé avec ce que le dossier contient : rien n'est copié ni lancé.</summary>
        UseAsIs,
        /// <summary>L'installation (ou la copie) se fait PAR-DESSUS : même nom = remplacé, le reste est conservé.</summary>
        Overwrite
    }

    // ======================================================================
    // ExistingFolderDialog — le dossier de destination existe déjà et n'est pas vide.
    //
    // RÈGLES (validées avec l'utilisateur) :
    //   • le plugin n'écrase jamais rien sans que l'utilisateur l'ait choisi ;
    //   • le plugin ne SUPPRIME jamais rien ici : pas de bouton « Supprimer et réinstaller » ;
    //   • « Utiliser ce dossier tel quel » n'est proposé que s'il contient au moins un exécutable de jeu
    //     (sinon le bouton est grisé et la fenêtre dit pourquoi) ;
    //   • le bouton « Annuler » a le focus à l'ouverture : un appui trop vif sur A ne détruit rien.
    //
    // Utilisation (thread UI) :
    //     var dlg = new ExistingFolderDialog(...) { Owner = ... };
    //     dlg.ShowDialog();
    //     ExistingFolderChoice choix = dlg.Choice;   // Cancel si la fenêtre est fermée autrement
    // ======================================================================
    public partial class ExistingFolderDialog : ConsoleWindow
    {
        /// <summary>Choix retenu (Cancel par défaut, y compris si la fenêtre est fermée par B ou Échap).</summary>
        public ExistingFolderChoice Choice { get; private set; }

        /// <param name="gameFolderName">Nom du dossier du jeu (affiché dans l'en-tête, nettoyé).</param>
        /// <param name="folder">Chemin EXACT du dossier existant.</param>
        /// <param name="hasGameExe">Vrai si le dossier contient au moins un exécutable de jeu.</param>
        /// <param name="usesInstaller">Vrai si la méthode choisie est « Exécuter l'installeur » (sinon : copie d'un jeu portable).</param>
        /// <param name="unrecognizedInstaller">Vrai si l'installeur n'est pas reconnu (cas ambigu : il s'ouvrira avec sa fenêtre).</param>
        /// <param name="fullscreen">Vrai si Playnite est en mode Plein écran.</param>
        public ExistingFolderDialog(string gameFolderName, string folder, bool hasGameExe,
                                    bool usesInstaller, bool unrecognizedInstaller, bool fullscreen)
        {
            InitializeComponent();
            Choice = ExistingFolderChoice.Cancel;

            Header.Game = ArchiveScanner.CleanGameName(gameFolderName ?? "");
            PathText.Text = folder ?? "";

            ChoicesText.Text =
                "• Utiliser ce dossier tel quel : le jeu passe « installé » avec ce que le dossier contient. " +
                "Rien n'est copié, rien n'est lancé.\n" +
                "• Écraser : l'installation ou la copie se fait par-dessus. Les fichiers du même nom sont remplacés, " +
                "les autres sont conservés (par exemple des mods ou des scripts ajoutés). Rien n'est supprimé.\n" +
                "• Annuler : rien n'est modifié.";

            // Avertissements : textes validés. Un installeur (reconnu ou non) peut remplacer des fichiers ;
            // pour un jeu portable, on sait exactement ce que fait la copie.
            if (usesInstaller)
            {
                WarnLine1.Text =
                    "⚠ Un installeur peut remplacer des fichiers de ce dossier. Nous ne pouvons pas garantir que les " +
                    "fichiers qui s'y trouvent déjà seront conservés. Si certains comptent pour vous, faites-en une " +
                    "sauvegarde avant de continuer.";
                WarnLine1.Visibility = Visibility.Visible;

                if (unrecognizedInstaller)
                {
                    WarnLine2.Text = "⚠ Cet installeur n'est pas reconnu : il s'ouvrira avec sa propre fenêtre.";
                    WarnLine2.Visibility = Visibility.Visible;

                    if (fullscreen)
                    {
                        WarnLine3.Text =
                            "Cette fenêtre ne se commande pas à la manette. " +
                            "Gardez un clavier et une souris à portée de main.";
                        WarnLine3.Visibility = Visibility.Visible;
                    }
                }
            }
            else
            {
                WarnLine1.Text = "Les fichiers du même nom seront remplacés, les autres seront conservés.";
                WarnLine1.Visibility = Visibility.Visible;
            }
            WarnCard.Visibility = Visibility.Visible;

            // « Tel quel » n'a de sens que s'il y a un jeu à lancer dans le dossier.
            if (!hasGameExe)
            {
                BtnAsIs.IsEnabled = false;
                NoExeText.Text = "Aucun exécutable de jeu n'a été trouvé dans ce dossier.";
                NoExeText.Visibility = Visibility.Visible;
            }
        }

        // Focus initial : « Annuler » (le choix sans conséquence).
        protected override UIElement InitialFocusElement()
        {
            return BtnCancel;
        }

        // Boutons du pied de fenêtre, de gauche à droite. Un bouton grisé est ignoré par la navigation.
        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnCancel, BtnOverwrite, BtnAsIs }; }
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFolderChoice.Cancel;
            CloseDialog(false);
        }

        private void BtnOverwrite_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFolderChoice.Overwrite;
            CloseDialog(true);
        }

        private void BtnAsIs_Click(object sender, RoutedEventArgs e)
        {
            Choice = ExistingFolderChoice.UseAsIs;
            CloseDialog(true);
        }
    }
}
