using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Input;
using Playnite.SDK;

namespace ArchiveStore
{
    // ======================================================================
    // ConsoleDialogs — petites fenêtres utilitaires pour les réglages « manette ».
    //
    //   Info(...)        : message avec un seul bouton « OK »
    //   Ask(...)         : question Oui / Non (ConsoleConfirmDialog)
    //   Choose(...)      : menu de choix (ConsoleMenuDialog)
    //   AskText(...)     : saisie de texte avec le CLAVIER À L'ÉCRAN de Playnite (SelectString)
    //   PickFolder(...)  : choix d'un dossier (FolderBrowserDialog, avec en plus la « Boîte Windows »)
    //
    // Toutes ces méthodes sont à appeler depuis le thread de l'interface.
    // ======================================================================
    internal static class ConsoleDialogs
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        // Donne un propriétaire à une fenêtre fille (centrage et superposition corrects).
        // Un propriétaire pas (ou plus) affiché ferait échouer l'affectation : on s'en passe alors.
        internal static void SetOwner(Window child, Window owner)
        {
            if (child == null || owner == null) return;
            try { child.Owner = owner; }
            catch (InvalidOperationException) { }
            catch (ArgumentException) { }
        }

        /// <summary>Message d'information avec un seul bouton « OK ».</summary>
        public static void Info(ConsoleWindow owner, string title, string message)
        {
            var dialog = new ConsoleMenuDialog("Information", title, message,
                new List<MenuChoice> { new MenuChoice("OK") }, null, 0);
            SetOwner(dialog, owner);
            dialog.ShowDialog();
        }

        /// <summary>Question Oui / Non. Le bouton « Non » a le focus (sécurité). Renvoie vrai pour « Oui ».</summary>
        public static bool Ask(ConsoleWindow owner, string title, string message, string yesText, string noText)
        {
            return ConsoleConfirmDialog.Ask(owner, title, message, yesText, noText);
        }

        /// <summary>Menu de choix. Renvoie la ligne choisie, ou null si l'utilisateur a fait « Retour ».</summary>
        public static MenuChoice Choose(ConsoleWindow owner, string section, string title, string message,
                                        IList<MenuChoice> choices, int initialIndex)
        {
            var dialog = new ConsoleMenuDialog(section, title, message, choices, "Retour", initialIndex);
            SetOwner(dialog, owner);
            return dialog.ShowDialog() == true ? dialog.SelectedChoice : null;
        }

        // ------------------------------------------------------------------
        // Fenêtres de PLAYNITE (clavier à l'écran, sélecteur de dossier Windows)
        //
        // PÉDAGOGIE : ces fenêtres-là ne sont pas les nôtres. Pour être sûrs qu'elles ne passent pas
        // DERRIÈRE notre fenêtre (ce qui bloquerait tout), on rend notre fenêtre invisible (Opacity = 0)
        // pendant qu'elles sont ouvertes, puis on la rend visible. C'est plus sûr que de la cacher avec
        // Hide() : une fenêtre modale cachée peut se fermer ou perdre son état.
        // Au retour, on relance aussi le petit délai « ignore la manette » : le bouton B qui ferme la fenêtre
        // de Playnite est AUSSI transmis à notre plugin, et il ne doit pas être compris deux fois.
        // ------------------------------------------------------------------
        internal static T RunOutside<T>(ConsoleWindow window, Func<T> action)
        {
            double oldOpacity = 1.0;
            UIElement oldFocus = null;
            if (window != null)
            {
                oldOpacity = window.Opacity;
                oldFocus = Keyboard.FocusedElement as UIElement;
                window.Opacity = 0;
            }
            try
            {
                return action();
            }
            finally
            {
                if (window != null)
                {
                    window.Opacity = oldOpacity;
                    window.RearmGamepadDelay();
                    try
                    {
                        window.Activate();
                        if (oldFocus != null) oldFocus.Focus();
                    }
                    catch (Exception) { /* fenêtre en cours de fermeture : rien à faire */ }
                }
            }
        }

        /// <summary>
        /// Saisie de texte avec le clavier à l'écran de Playnite (mode Plein écran) ou sa petite boîte de saisie
        /// (mode Bureau). Renvoie faux si l'utilisateur annule. Le texte renvoyé est « trimé » ; il peut être vide.
        /// </summary>
        public static bool AskText(IPlayniteAPI api, ConsoleWindow owner, string title, string message,
                                   string current, out string text)
        {
            text = null;
            try
            {
                StringSelectionDialogResult result = RunOutside(owner,
                    () => api.Dialogs.SelectString(message, title, current ?? ""));
                if (result == null || !result.Result) return false;
                text = (result.SelectedString ?? "").Trim();
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : saisie de texte impossible.");
                Info(owner, "Saisie impossible", "Le clavier de Playnite n'a pas pu s'ouvrir : " + ex.Message);
                return false;
            }
        }

        /// <summary>Choix d'un dossier. Renvoie le chemin choisi, ou null si l'utilisateur annule.</summary>
        public static string PickFolder(IPlayniteAPI api, ConsoleWindow owner, string title, string current)
        {
            var dialog = new FolderBrowserDialog(api, title, current);
            SetOwner(dialog, owner);
            return dialog.ShowDialog() == true ? dialog.SelectedPath : null;
        }
    }
}
