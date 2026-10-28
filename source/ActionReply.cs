using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;
using Playnite.SDK;

namespace ArchiveStore
{
    // ======================================================================
    // ActionReply — la réponse à une action que TU viens de faire dans un menu (« Installer les DLC et patchs »,
    // « Choisir la destination », « Réassocier l'exécutable », « Basculer le profil »).
    //
    // Deux façons de répondre, réglables catégorie par catégorie (Notifications et toasts > « Réponses à tes actions ») :
    //   • FENÊTRE (par défaut) : une petite fenêtre avec « OK », devant toi, utilisable à la manette ;
    //   • NOTIFICATION ET TOAST : comme avant (la notification de Playnite, plus le toast si sa case est cochée).
    // Ce qui arrive PLUS TARD (installation terminée, nouveaux DLC détectés…) n'est pas concerné : ça reste en notification.
    //
    // FOCUS : quand une fenêtre se ferme, Windows rend la main à sa fenêtre PROPRIÉTAIRE. Si ce propriétaire n'existe plus
    // (le menu du jeu de Playnite, fermé entre-temps), Windows la donne à une autre fenêtre (l'Explorateur, par exemple) :
    // Playnite reste affiché, mais la manette ne lui arrive plus. D'où : la fenêtre principale de Playnite comme
    // propriétaire, et ReturnToPlaynite() après nos fenêtres.
    //
    // Utilisable depuis n'importe quel thread (la fenêtre s'ouvre sur le thread de l'interface ; l'appelant attend sa fermeture).
    // ======================================================================
    internal static class ActionReply
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private const string Prefix = "ArchiveStore : ";

        /// <summary>
        /// Répond à une action de menu. asNotification : l'affichage « notification et toast » (celui d'avant), utilisé si la
        /// catégorie est réglée ainsi, ou si la fenêtre ne peut pas s'ouvrir.
        /// </summary>
        public static void Show(PluginSettings settings, ReplyCategory category, string title, string message,
                                bool isError, Action asNotification)
        {
            if (settings == null || !settings.IsReplyWindow(category))
            {
                asNotification?.Invoke();
                return;
            }

            try
            {
                UiHelper.OnUi(() => ShowWindow(title, message, isError));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : fenêtre de réponse impossible, notification à la place.");
                asNotification?.Invoke();
            }
        }

        private static void ShowWindow(string title, string message, bool isError)
        {
            var dialog = new ConsoleMenuDialog(isError ? "Action impossible" : "Information", title, Clean(message),
                new List<MenuChoice> { new MenuChoice("OK") }, null, 0);
            ConsoleDialogs.SetOwner(dialog, PlayniteMain());
            dialog.ShowDialog();
            ReturnToPlaynite();
        }

        // La fenêtre a déjà son en-tête « ArchiveStore » : le préfixe des notifications est inutile.
        private static string Clean(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            return message.StartsWith(Prefix, StringComparison.Ordinal) ? message.Substring(Prefix.Length) : message;
        }

        /// <summary>La fenêtre principale de Playnite (Bureau ou Plein écran), ou null si elle n'est pas affichée.</summary>
        public static Window PlayniteMain()
        {
            var app = Application.Current;
            var main = app != null ? app.MainWindow : null;
            return main != null && main.IsVisible ? main : null;
        }

        /// <summary>
        /// Après nos fenêtres : la main revient à Playnite, sauf si une autre de ses fenêtres l'a déjà (la fenêtre suivante
        /// d'ArchiveStore, une fenêtre de Playnite…). Sans effet si Playnite est déjà au premier plan.
        /// </summary>
        public static void ReturnToPlaynite()
        {
            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                try
                {
                    Window main = PlayniteMain();
                    if (main == null || main.IsActive) return;
                    foreach (Window w in app.Windows)
                    {
                        if (!ReferenceEquals(w, main) && w.IsVisible && w.IsActive) return;
                    }
                    main.Activate();
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "ArchiveStore : retour du premier plan sur Playnite impossible.");
                }
            }));
        }
    }
}
