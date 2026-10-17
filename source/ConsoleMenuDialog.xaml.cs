using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // MenuChoice — une réponse proposée par ConsoleMenuDialog.
    //   Label  : le texte de la ligne.
    //   Detail : une petite phrase sous le texte (facultative).
    //   Tag    : une valeur libre que l'appelant retrouve pour savoir quelle ligne a été choisie.
    // ======================================================================
    public class MenuChoice
    {
        public string Label { get; set; }
        public string Detail { get; set; }
        public object Tag { get; set; }

        public MenuChoice(string label, string detail = null, object tag = null)
        {
            Label = label;
            Detail = detail;
            Tag = tag;
        }

        // Utilisé par la liste (binding) : la ligne de détail disparaît quand il n'y a pas de détail.
        public Visibility DetailVisibility
        {
            get { return string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    // ======================================================================
    // ConsoleMenuDialog — petit menu de choix au style console, utilisable à la manette.
    //
    // Utilisation (voir ConsoleDialogs.Choose / ConsoleDialogs.Info) :
    //     var d = new ConsoleMenuDialog("Section", "Titre", "Explication", choix, "Retour", 0);
    //     if (d.ShowDialog() == true) { var reponse = d.SelectedChoice; }
    //
    // cancelText = null : pas de bouton « Retour » (cas d'une simple boîte de message).
    // B (ou Échap) ferme toujours la fenêtre en « annulé ».
    // ======================================================================
    public partial class ConsoleMenuDialog : ConsoleWindow
    {
        /// <summary>Ligne choisie (valide si ShowDialog() a renvoyé vrai).</summary>
        public MenuChoice SelectedChoice { get; private set; }

        public ConsoleMenuDialog(string section, string title, string message,
                                 IList<MenuChoice> choices, string cancelText, int initialIndex)
        {
            InitializeComponent();

            Header.Section = section ?? "Choix";
            Title = "ArchiveStore — " + (title ?? "");
            TitleText.Text = title ?? "";

            if (string.IsNullOrEmpty(message)) MessageText.Visibility = Visibility.Collapsed;
            else MessageText.Text = message;

            if (string.IsNullOrEmpty(cancelText))
            {
                BtnCancel.Visibility = Visibility.Collapsed;
                HintB.Label = "Fermer";
            }
            else
            {
                BtnCancel.Content = cancelText;
                HintB.Label = cancelText;
            }

            var list = choices ?? new List<MenuChoice>();
            ChoiceList.ItemsSource = list;
            if (list.Count > 0)
                ChoiceList.SelectedIndex = Math.Max(0, Math.Min(list.Count - 1, initialIndex));
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            return ChoiceList;
        }

        // Haut/bas : on déplace NOUS-MÊMES la sélection (même comportement au clavier, en Plein écran
        // et à la croix directionnelle du mode Bureau). Gauche/droite : rien.
        protected override bool OnNavigate(GamepadAction action)
        {
            if (action == GamepadAction.Up) MoveSelection(-1);
            else if (action == GamepadAction.Down) MoveSelection(+1);
            return true;
        }

        private void MoveSelection(int delta)
        {
            int count = ChoiceList.Items.Count;
            if (count == 0) return;

            int index = ChoiceList.SelectedIndex < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Max(0, Math.Min(count - 1, ChoiceList.SelectedIndex + delta));

            ChoiceList.SelectedIndex = index;
            ChoiceList.ScrollIntoView(ChoiceList.SelectedItem);
            ChoiceList.Focus();
        }

        protected override void OnConfirm()
        {
            Confirm();
        }

        // Un clic sur une ligne la choisit tout de suite (c'est un menu, pas une liste de sélection).
        private void ChoiceList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var item = ItemsControl.ContainerFromElement(ChoiceList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (item == null) return;
            item.IsSelected = true;
            Confirm();
        }

        private void Confirm()
        {
            var choice = ChoiceList.SelectedItem as MenuChoice;
            if (choice == null) return;
            SelectedChoice = choice;
            CloseDialog(true);
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }
    }
}
