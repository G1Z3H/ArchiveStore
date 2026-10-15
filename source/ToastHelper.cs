using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;

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
    // PLUSIEURS TOASTS À LA FOIS (réglage ToastMode) :
    //   • « L'un après l'autre » (défaut) : un seul visible ; les suivants attendent leur tour (file d'attente) ;
    //   • « Superposés » : jusqu'à MaxStacked visibles, empilés vers le haut depuis le coin inférieur droit ;
    //     au-delà, les suivants attendent qu'une place se libère.
    // La file d'attente est plafonnée (MaxPending) : en cas d'inondation, les toasts en trop sont abandonnés
    // (et notés dans le journal) plutôt que de défiler pendant des minutes. Aucune information n'est perdue :
    // la notification de Playnite existe toujours.
    //
    // PÉDAGOGIE — Visible et Pending ne sont lus et modifiés que sur le thread UI (BeginInvoke) : pas besoin de verrou.
    // ======================================================================
    internal static class ToastHelper
    {
        private const int MaxStacked = 4;     // toasts visibles en même temps en mode « Superposés »
        private const int MaxPending = 20;    // toasts en attente au maximum
        private const int MaxMessageChars = 240; // un message plus long est coupé (le détail reste dans la notification)
        private const double StackGap = -6;   // chevauchement léger des marges transparentes : cartes bien rapprochées

        private sealed class PendingToast
        {
            public string Title;
            public string Message;
            public string Hint;
        }

        private static readonly List<ToastWindow> Visible = new List<ToastWindow>(); // du plus ancien (en bas) au plus récent
        private static readonly Queue<PendingToast> Pending = new Queue<PendingToast>();
        private static ToastStackMode _mode = ToastStackMode.OneAfterAnother;        // dernier réglage lu

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
            Enqueue(api, settings, TitleFor(settings.GetNotificationCategory(baseId)), message, hint);
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
            Enqueue(api, settings, TitleFor(null), message, null);
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
        // Mise en file d'attente et affichage (thread UI)
        // ------------------------------------------------------------------
        private static void Enqueue(IPlayniteAPI api, PluginSettings settings, string title, string message, string hint)
        {
            // Réglages lus ICI (valeurs simples) puis transmises au thread UI.
            bool duringGame = settings.ToastsDuringGame;
            ToastStackMode mode = settings.ToastMode;
            string cleanText = CleanMessage(message);
            if (cleanText.Length == 0) return;

            var app = Application.Current;
            if (app == null) return;

            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    _mode = mode;

                    // « Y compris pendant une partie » décoché : le toast n'apparaît que si Playnite est au premier
                    // plan ET qu'aucun jeu n'est en cours de lancement ou d'exécution.
                    if (!duringGame && !MayShowOutsideGame(api, title))
                        return;

                    if (Pending.Count >= MaxPending)
                    {
                        ArchiveLog.Warn("Toast abandonné (trop de toasts en attente) : " + title);
                        return;
                    }

                    Pending.Enqueue(new PendingToast { Title = title, Message = cleanText, Hint = hint });
                    ShowPending();
                }
                catch (Exception ex)
                {
                    ArchiveLog.Warn("Toast impossible : " + ex.Message);
                }
            }));
        }

        // Vrai si Playnite est au premier plan et qu'aucun jeu ne tourne. Thread UI.
        private static bool MayShowOutsideGame(IPlayniteAPI api, string title)
        {
            var app = Application.Current;
            if (app == null) return false;

            bool inFront = false;
            foreach (Window w in app.Windows)
            {
                if (w is ToastWindow) continue;
                if (w.IsActive) { inFront = true; break; }
            }
            if (!inFront)
            {
                ArchiveLog.Info("Toast non affiché (Playnite n'est pas au premier plan et « pendant une partie » est décoché) : " + title);
                return false;
            }

            if (api != null && api.Database.Games.Any(g => g.IsRunning || g.IsLaunching))
            {
                ArchiveLog.Info("Toast non affiché (un jeu est en cours et « pendant une partie » est décoché) : " + title);
                return false;
            }
            return true;
        }

        // Nombre de toasts visibles autorisés en même temps selon la disposition choisie.
        private static int Capacity()
        {
            return _mode == ToastStackMode.Stacked ? MaxStacked : 1;
        }

        // Affiche autant de toasts en attente que la place le permet. Thread UI.
        private static void ShowPending()
        {
            while (Pending.Count > 0 && Visible.Count < Capacity())
            {
                var next = Pending.Dequeue();
                ToastWindow win = null;
                try
                {
                    win = new ToastWindow(next.Title, next.Message, next.Hint);
                    Visible.Add(win);

                    // Quand un toast se ferme : on le retire, on redescend les autres, on affiche le suivant.
                    win.Closed += (s, e) =>
                    {
                        Visible.Remove((ToastWindow)s);
                        Relayout();
                        ShowPending();
                    };
                    win.Show();
                }
                catch (Exception ex)
                {
                    if (win != null) Visible.Remove(win);
                    ArchiveLog.Warn("Toast impossible : " + ex.Message);
                }
            }
        }

        /// <summary>
        /// Replace tous les toasts visibles : le plus ancien en bas, les suivants empilés au-dessus.
        /// Appelé par chaque toast quand sa hauteur est connue, et à chaque fermeture. Thread UI.
        /// </summary>
        public static void Relayout()
        {
            double offset = 0;
            foreach (var w in Visible.ToList())
            {
                if (!w.IsLoaded || w.ActualHeight <= 0) continue; // sa hauteur n'est pas encore connue
                w.PlaceAt(offset);
                offset += w.ActualHeight + StackGap;
            }
        }
    }
}
