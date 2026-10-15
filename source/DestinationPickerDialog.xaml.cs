using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ArchiveStore
{
    // ======================================================================
    // DestinationPickerDialog — « Choisir la destination d'installation (ArchiveStore) ».
    //
    // RÔLE : affecter à l'avance une destination à un ou plusieurs jeux PAS ENCORE installés
    // (ex. envoyer « Far Cry 2 » sur le disque Windows C: avant de l'installer). Rien n'est
    // installé ici : on mémorise seulement le choix (PluginSettings.ChosenDestinations).
    //
    // Cette même fenêtre sert aussi de « liste déroulante » à la fenêtre d'installation
    // (clic sur la destination) : le paramètre facultatif helpText remplace alors l'explication.
    //
    // La fenêtre ne fait AUCUN accès disque : le plugin lui donne la liste déjà prête.
    //
    // MANETTE / CLAVIER (via ConsoleWindow) : haut/bas = changer de ligne, A = valider, B = annuler.
    // ======================================================================
    public partial class DestinationPickerDialog : ConsoleWindow
    {
        /// <summary>Racine de destination choisie (lue par le plugin après ShowDialog()==true).</summary>
        public string SelectedRoot { get; private set; }

        /// <param name="gameNames">Noms des jeux concernés (affichés dans la fenêtre).</param>
        /// <param name="choices">Destinations du profil (la première est la destination par défaut).</param>
        /// <param name="currentRoot">
        /// Destination actuelle si elle est la même pour tous les jeux concernés, sinon null (rien de présélectionné).
        /// </param>
        /// <param name="helpText">Explication affichée sous la liste (null = l'explication habituelle).</param>
        public DestinationPickerDialog(IList<string> gameNames, IList<DestinationChoice> choices, string currentRoot,
                                       string helpText = null)
        {
            InitializeComponent();

            var names = gameNames ?? new List<string>();
            TitleText.Text = names.Count > 1
                ? "Où installer ces " + names.Count + " jeux ?"
                : "Où installer ce jeu ?";

            // Liste des jeux concernés : on en montre quelques-uns, puis « … et N autres ».
            const int maxShown = 8;
            string shown = names.Count == 1
                ? names[0]
                : string.Join("\n", names.Take(maxShown).Select(n => "• " + n));
            if (names.Count > maxShown)
                shown += "\n… et " + (names.Count - maxShown) + " autre(s)";
            GamesText.Text = shown;

            DestinationList.ItemsSource = choices;

            // Présélection : la destination actuelle (si tous les jeux ont la même).
            if (!string.IsNullOrEmpty(currentRoot) && choices != null)
            {
                var current = choices.FirstOrDefault(c => ArchiveScanner.SamePath(c.FolderPath, currentRoot));
                if (current != null) DestinationList.SelectedItem = current;
            }

            HelpText.Text = helpText ??
                ("Rien n'est installé maintenant : le choix est mémorisé, et l'installation du jeu (bouton Installer) " +
                 "utilisera cette destination. Vous pourrez le changer à tout moment tant que le jeu n'est pas installé.");
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            return DestinationList;
        }

        // Haut/bas : on déplace NOUS-MÊMES la sélection (même comportement au clavier, en Plein écran
        // et à la croix directionnelle du mode Bureau).
        protected override bool OnNavigate(GamepadAction action)
        {
            if (action == GamepadAction.Up) MoveSelection(-1);
            else if (action == GamepadAction.Down) MoveSelection(+1);
            return true;
        }

        private void MoveSelection(int delta)
        {
            int count = DestinationList.Items.Count;
            if (count == 0) return;

            int index = DestinationList.SelectedIndex < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Max(0, Math.Min(count - 1, DestinationList.SelectedIndex + delta));

            DestinationList.SelectedIndex = index;
            DestinationList.ScrollIntoView(DestinationList.SelectedItem);
            DestinationList.Focus();
        }

        protected override void OnConfirm()
        {
            Confirm();
        }

        // « Valider » n'est actif que lorsqu'une destination est sélectionnée.
        private void DestinationList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            BtnConfirm.IsEnabled = DestinationList.SelectedItem is DestinationChoice;
        }

        // Double-clic sur une destination = Valider.
        private void DestinationList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (DestinationList.SelectedItem is DestinationChoice) Confirm();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void Confirm()
        {
            var choice = DestinationList.SelectedItem as DestinationChoice;
            if (choice == null) return;
            SelectedRoot = choice.FolderPath;
            CloseDialog(true); // ferme aussi la fenêtre
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }
    }
}
