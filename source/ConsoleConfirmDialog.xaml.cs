using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace ArchiveStore
{
    // ======================================================================
    // ConsoleConfirmDialog — question « Oui / Non » au style console, utilisable à la manette.
    //
    // Utilisation :
    //     bool oui = ConsoleConfirmDialog.Ask(this, "Annuler l'installation ?", "Les fichiers seront supprimés.",
    //                                         "Oui, annuler", "Non, continuer");
    //
    // Par sécurité : le bouton « Non » a le focus à l'ouverture, et B (ou Échap) répond « Non ».
    // ======================================================================
    public partial class ConsoleConfirmDialog : ConsoleWindow
    {
        private ConsoleConfirmDialog(string title, string message, string yesText, string noText)
        {
            InitializeComponent();
            Title = "ArchiveStore — " + title;
            TitleText.Text = title;
            MessageText.Text = message;
            BtnYes.Content = yesText;
            BtnNo.Content = noText;
        }

        /// <summary>Pose la question et renvoie vrai si l'utilisateur a répondu « Oui ».</summary>
        public static bool Ask(Window owner, string title, string message, string yesText, string noText)
        {
            var dialog = new ConsoleConfirmDialog(title, message, yesText, noText);
            if (owner != null)
            {
                // Un propriétaire pas (ou plus) affiché ferait échouer l'affectation : on s'en passe alors.
                try { dialog.Owner = owner; }
                catch (System.InvalidOperationException) { }
                catch (System.ArgumentException) { }
            }
            return dialog.ShowDialog() == true;
        }

        protected override UIElement InitialFocusElement()
        {
            return BtnNo;
        }

        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnNo, BtnYes }; }
        }

        private void BtnYes_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(true);
        }

        private void BtnNo_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }
    }
}
