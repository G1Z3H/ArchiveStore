using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // ToastHelper — décide si un toast peut s'afficher, dans quel ordre, puis l'affiche.
    //
    // LES TOASTS S'AJOUTENT AUX NOTIFICATIONS DE PLAYNITE, ils ne les remplacent jamais : la notification
    // est toujours créée comme avant ; le toast n'est qu'un « coup de projecteur » supplémentaire, si la
    // case correspondante est cochée dans les réglages (groupe « Toasts »). Tout est coché par défaut ;
    // l'utilisateur décoche ce qui le dérange.
    //
    // POINTS D'ENTRÉE (utilisables depuis N'IMPORTE QUEL thread : l'affichage est renvoyé sur le thread UI) :
    //   • ShowForNotification : branché dans Notify() (installation, extras, erreurs…) ;
    //   • ShowForCategory     : pour les notifications créées directement (doublons, menus, résumés…) ;
    //   • ShowError           : erreurs et blocages ;
    //   • ShowTest            : bouton « Afficher des toasts de test » des réglages.
    //
    // L'AFFICHAGE (carte en bas à droite, pile commune, règle « pendant une partie ») est fait
    // par ToastService, dans la boîte à outils partagée ArgentToolkit. Ici, on garde ce qui est propre à
    // ArchiveStore : les cases par catégorie, les titres, le nettoyage du texte et les réglages.
    // ======================================================================
    internal static class ToastHelper
    {
        private const int MaxMessageChars = 240; // un message plus long est coupé (le détail reste dans la notification)

        // ------------------------------------------------------------------
        // Points d'entrée
        // ------------------------------------------------------------------

        /// <summary>
        /// Toast pour une notification émise par Notify() : catégorie connue → sa case « toast » ;
        /// sinon seules les ERREURS peuvent faire un toast (case « Erreurs et blocages »).
        /// hint = ligne facultative sous le message (ex. « Ouvrez les notifications de Playnite pour agir »).
        /// </summary>
        public static void ShowForNotification(IPlayniteAPI api, PluginSettings settings, string baseId,
                                               NotificationType type, string message, string hint = null)
        {
            if (settings == null || !settings.IsToastAllowed(baseId, type)) return;
            Enqueue(api, settings, TitleFor(settings.GetNotificationCategory(baseId)), message, hint, type == NotificationType.Error);
        }

        /// <summary>Toast d'une notification créée directement, si la case « toast » de sa catégorie est cochée.</summary>
        public static void ShowForCategory(IPlayniteAPI api, PluginSettings settings, NotifyCategory category,
                                           string message, string hint = null)
        {
            if (settings == null || !settings.IsToastEnabled(category)) return;
            Enqueue(api, settings, TitleFor(category), message, hint);
        }

        /// <summary>Toast d'une erreur ou d'un blocage, si la case « Erreurs et blocages » est cochée.</summary>
        public static void ShowError(IPlayniteAPI api, PluginSettings settings, string message)
        {
            if (settings == null || !settings.ToastErrors) return;
            Enqueue(api, settings, TitleFor(null), message, null, true);
        }

        /// <summary>
        /// Trois toasts de démonstration, sans tenir compte des cases par catégorie, mais AVEC les réglages
        /// « pendant une partie » et « disposition » : l'utilisateur voit exactement ce que donneront ses choix.
        /// </summary>
        public static void ShowTest(IPlayniteAPI api, PluginSettings settings)
        {
            if (settings == null) return;
            Enqueue(api, settings, "ArchiveStore", "Toast n° 1 : ceci est un test.", null);
            Enqueue(api, settings, "ArchiveStore", "Toast n° 2 : les toasts ne prennent jamais le focus.", null);
            Enqueue(api, settings, "ArchiveStore", "Toast n° 3 : ils disparaissent tout seuls après quelques secondes.",
                    "Pour agir sur une notification, ouvrez celles de Playnite.");
        }

        // ------------------------------------------------------------------
        // Titres (une carte bien identifiée vaut mieux qu'un « ArchiveStore » partout)
        // ------------------------------------------------------------------
        private static string TitleFor(NotifyCategory? category)
        {
            if (!category.HasValue) return "ArchiveStore — Problème";
            switch (category.Value)
            {
                case NotifyCategory.NewExtras: return "Nouveaux DLC et patchs";
                case NotifyCategory.MissingPaths: return "Disque ou dossier inaccessible";
                case NotifyCategory.Duplicates: return "Doublons entre sources";
                case NotifyCategory.InstallInfo: return "Installation";
                default: return "ArchiveStore";
            }
        }

        // Le texte des notifications commence par « ArchiveStore : » : redondant dans une carte déjà titrée.
        private static string CleanMessage(string message)
        {
            string text = (message ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            const string prefix = "ArchiveStore : ";
            if (text.StartsWith(prefix, StringComparison.Ordinal))
                text = text.Substring(prefix.Length).TrimStart();
            if (text.Length > MaxMessageChars)
                text = text.Substring(0, MaxMessageChars).TrimEnd() + "…";
            return text;
        }

        // ------------------------------------------------------------------
        // Transmission à ToastService (boîte à outils partagée)
        // ------------------------------------------------------------------
        private static void Enqueue(IPlayniteAPI api, PluginSettings settings, string title, string message, string hint, bool isError = false)
        {
            string cleanText = CleanMessage(message);
            if (cleanText.Length == 0) return;

            // Toujours empilés : pile commune des plugins Argent (durée et nombre réglés dans Argent, voir ToastService).
            // Une erreur le dit à la pile : avec Argent, « Ne pas déranger » peut la laisser passer.
            ToastService.ShowFrom(null, isError ? ToastService.KindError : ToastService.KindInfo, title, cleanText, hint,
                                  settings.ToastsDuringGame);
        }
    }
}
