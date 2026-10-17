using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Playnite.SDK;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // ProfileEditorDialog — modifier UN profil (nom, sources, destinations) à la manette.
    //
    // PÉDAGOGIE :
    //   • Les modifications s'appliquent tout de suite à l'objet Profile des réglages EN COURS D'ÉDITION
    //     (PluginSettings après BeginEdit). Rien n'est écrit sur le disque ici : c'est « Enregistrer », dans la
    //     fenêtre des réglages, qui déclenche EndEdit (donc la sauvegarde). Si l'utilisateur annule les réglages,
    //     CancelEdit restaure tout : rien n'est perdu.
    //   • Retirer une source ou une destination de la liste ne supprime AUCUN fichier : c'est seulement un
    //     réglage. L'archive source reste intacte.
    //   • Les vérifications (chemins invalides, source imbriquée dans une destination, doublons…) sont faites
    //     par PluginSettings.VerifySettings au moment d'enregistrer, comme dans l'écran classique.
    // ======================================================================
    public partial class ProfileEditorDialog : ConsoleWindow
    {
        private readonly PluginSettings _s;
        private readonly Profile _p;
        private readonly IPlayniteAPI _api;
        private readonly Action _touch;                       // prévient la fenêtre des réglages : « il y a des modifications »
        private readonly List<UIElement> _rows = new List<UIElement>();

        /// <summary>Vrai si l'utilisateur a supprimé ce profil (la fenêtre des réglages se rafraîchit).</summary>
        public bool Deleted { get; private set; }

        public ProfileEditorDialog(PluginSettings settings, Profile profile, IPlayniteAPI api, Action touch)
        {
            InitializeComponent();
            _s = settings;
            _p = profile;
            _api = api;
            _touch = touch ?? (() => { });

            if (_p.Sources == null) _p.Sources = new List<string>();
            if (_p.Destinations == null) _p.Destinations = new List<DestinationEntry>();

            // Taille fixe (le contenu défile) : jamais plus grand que l'écran.
            var area = SystemParameters.WorkArea;
            SizeToContent = SizeToContent.Manual;
            Width = Math.Min(1048, area.Width);
            Height = Math.Min(900, area.Height);

            Build();
        }

        // ------------------------------------------------------------------
        // Construction du contenu
        // ------------------------------------------------------------------
        private void Add(UIElement row)
        {
            Host.Children.Add(row);
            _rows.Add(row);
        }

        private void Build()
        {
            Host.Children.Clear();
            _rows.Clear();

            TitleText.Text = "« " + (_p.Name ?? "") + " »";
            bool active = _p.Name == _s.ActiveProfileName;

            // --- Nom ---
            Host.Children.Add(ConsoleRows.Section(this, "NOM"));
            Add(ConsoleRows.Row(this, "Nom du profil", _p.Name, Rename));
            if (active)
                Host.Children.Add(ConsoleRows.Note(this, "Ce profil est le profil actif : il alimente la bibliothèque."));

            // --- Sources ---
            Host.Children.Add(ConsoleRows.Section(this, "SOURCES DE CE PROFIL"));
            Host.Children.Add(ConsoleRows.Note(this,
                "Dossiers contenant un sous-dossier par jeu. Vous pouvez en déclarer plusieurs (plusieurs disques, un NAS…) : " +
                "tous les jeux sont réunis dans la bibliothèque. L'ordre compte : si le même jeu existe dans deux sources, " +
                "la PREMIÈRE gagne. Une source inaccessible (disque débranché) est signalée par une notification, " +
                "sans bloquer les autres."));
            for (int i = 0; i < _p.Sources.Count; i++)
            {
                int index = i;
                string path = string.IsNullOrWhiteSpace(_p.Sources[i]) ? "(vide)" : _p.Sources[i];
                Add(ConsoleRows.Row(this, (i + 1) + ".   " + path,
                    (i == 0 && _p.Sources.Count > 1) ? "prioritaire" : null,
                    () => SourceMenu(index)));
            }
            Add(ConsoleRows.Row(this, "+   Ajouter une source…", null, AddSource));

            // --- Destinations ---
            Host.Children.Add(ConsoleRows.Section(this, "DESTINATIONS DE CE PROFIL"));
            Host.Children.Add(ConsoleRows.Note(this,
                "Dossiers où les jeux sont installés (un sous-dossier par jeu). La PREMIÈRE destination est celle par défaut. " +
                "Avec plusieurs destinations, la fenêtre d'installation vous laisse choisir, et le clic droit sur un jeu non " +
                "installé permet de lui affecter une destination à l'avance (utile pour un vieux jeu qui exige le disque " +
                "Windows). Le nom est facultatif : il sert seulement à vous y retrouver."));
            for (int i = 0; i < _p.Destinations.Count; i++)
            {
                int index = i;
                var d = _p.Destinations[i];
                string label = d == null ? "(vide)" : d.GetLabel();
                if (string.IsNullOrWhiteSpace(label)) label = "(vide)";
                Add(ConsoleRows.Row(this, (i + 1) + ".   " + label,
                    i == 0 ? "par défaut" : null,
                    () => DestinationMenu(index)));
            }
            Add(ConsoleRows.Row(this, "+   Ajouter une destination…", null, AddDestination));

            // --- Actions sur le profil ---
            Host.Children.Add(ConsoleRows.Section(this, "PROFIL"));
            if (!active)
                Add(ConsoleRows.Row(this, "Définir comme profil actif", null, MakeActive));
            Add(ConsoleRows.Row(this, "Supprimer ce profil…", null, DeleteProfile));
            Host.Children.Add(ConsoleRows.Note(this,
                "Supprimer un profil ne touche ni vos archives ni vos jeux installés : seule sa configuration disparaît."));
        }

        // Après une modification : réglages « modifiés », anciens champs remis à jour, écran reconstruit.
        private void Changed()
        {
            _p.RefreshLegacyFields();
            _touch();
            Rebuild();
        }

        // Reconstruit l'écran en gardant le focus sur la même position de ligne.
        private void Rebuild()
        {
            int idx = _rows.IndexOf(CurrentFocus as UIElement);
            Build();
            if (idx < 0 || _rows.Count == 0) return;
            int target = Math.Min(idx, _rows.Count - 1);
            // Priorité « Loaded » : on attend que les nouvelles lignes soient en place.
            Dispatcher.BeginInvoke(new Action(() => FocusRow(_rows[Math.Min(target, _rows.Count - 1)])),
                                   DispatcherPriority.Loaded);
        }

        private static void Swap<T>(List<T> list, int a, int b)
        {
            T tmp = list[a];
            list[a] = list[b];
            list[b] = tmp;
        }

        // ------------------------------------------------------------------
        // Nom
        // ------------------------------------------------------------------
        /// <summary>Vrai si un AUTRE profil porte déjà ce nom (casse ignorée).</summary>
        internal static bool NameTaken(PluginSettings s, string name, Profile except)
        {
            string wanted = (name ?? "").Trim();
            foreach (var q in s.Profiles)
            {
                if (ReferenceEquals(q, except)) continue;
                if (string.Equals((q.Name ?? "").Trim(), wanted, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private void Rename()
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, this, "Nom du profil", "Nom du profil :", _p.Name, out text)) return;

            if (text.Length == 0)
            {
                ConsoleDialogs.Info(this, "Nom vide", "Le nom du profil ne peut pas être vide.");
                return;
            }
            if (NameTaken(_s, text, _p))
            {
                ConsoleDialogs.Info(this, "Nom déjà utilisé", "Un autre profil s'appelle déjà « " + text + " ».");
                return;
            }

            // Si c'est le profil actif, le « profil actif » suit le nouveau nom (sinon il pointerait vers un nom disparu).
            bool wasActive = _p.Name == _s.ActiveProfileName;
            _p.Name = text;
            if (wasActive) _s.ActiveProfileName = text;
            Changed();
        }

        // ------------------------------------------------------------------
        // Sources
        // ------------------------------------------------------------------
        private void AddSource()
        {
            string path = ConsoleDialogs.PickFolder(_api, this, "Ajouter une source", null);
            if (path == null) return;
            if (_p.Sources.Any(x => ArchiveScanner.SamePath(x, path)))
            {
                ConsoleDialogs.Info(this, "Déjà dans la liste", "Ce dossier est déjà une source de ce profil.");
                return;
            }
            _p.Sources.Add(path);
            Changed();
        }

        private void SourceMenu(int i)
        {
            if (i < 0 || i >= _p.Sources.Count) return;

            var choices = new List<MenuChoice>();
            choices.Add(new MenuChoice("Changer le dossier", null, "change"));
            if (i > 0)
                choices.Add(new MenuChoice("Monter", "Plus prioritaire : gagne si le même jeu existe plus bas.", "up"));
            if (i < _p.Sources.Count - 1)
                choices.Add(new MenuChoice("Descendre", "Moins prioritaire.", "down"));
            choices.Add(new MenuChoice("Retirer de la liste",
                "Ne supprime aucun fichier : ce dossier n'est simplement plus utilisé par ce profil.", "remove"));

            var choice = ConsoleDialogs.Choose(this, "Source", _p.Sources[i], null, choices, 0);
            if (choice == null) return;

            switch ((string)choice.Tag)
            {
                case "change":
                    string path = ConsoleDialogs.PickFolder(_api, this, "Changer la source", _p.Sources[i]);
                    if (path == null) return;
                    int self = i;
                    if (_p.Sources.Where((x, k) => k != self).Any(x => ArchiveScanner.SamePath(x, path)))
                    {
                        ConsoleDialogs.Info(this, "Déjà dans la liste", "Ce dossier est déjà une source de ce profil.");
                        return;
                    }
                    _p.Sources[i] = path;
                    break;
                case "up": Swap(_p.Sources, i, i - 1); break;
                case "down": Swap(_p.Sources, i, i + 1); break;
                case "remove": _p.Sources.RemoveAt(i); break;
                default: return;
            }
            Changed();
        }

        // ------------------------------------------------------------------
        // Destinations
        // ------------------------------------------------------------------
        private void AddDestination()
        {
            string path = ConsoleDialogs.PickFolder(_api, this, "Ajouter une destination", null);
            if (path == null) return;
            if (_p.Destinations.Any(x => x != null && ArchiveScanner.SamePath(x.FolderPath, path)))
            {
                ConsoleDialogs.Info(this, "Déjà dans la liste", "Ce dossier est déjà une destination de ce profil.");
                return;
            }
            _p.Destinations.Add(new DestinationEntry { Name = "", FolderPath = path });
            Changed();
        }

        private void DestinationMenu(int i)
        {
            if (i < 0 || i >= _p.Destinations.Count) return;
            var dest = _p.Destinations[i];
            if (dest == null) return;

            var choices = new List<MenuChoice>();
            choices.Add(new MenuChoice("Changer le dossier", null, "change"));
            choices.Add(new MenuChoice("Donner un nom", "Facultatif : sert seulement à s'y retrouver dans les listes.", "rename"));
            if (i > 0)
                choices.Add(new MenuChoice("Monter", i == 1 ? "Deviendrait la destination par défaut." : null, "up"));
            if (i < _p.Destinations.Count - 1)
                choices.Add(new MenuChoice("Descendre", i == 0 ? "La suivante deviendrait la destination par défaut." : null, "down"));
            choices.Add(new MenuChoice("Retirer de la liste",
                "Ne supprime aucun fichier : ce dossier n'est simplement plus une destination de ce profil.", "remove"));

            var choice = ConsoleDialogs.Choose(this, "Destination", dest.GetLabel(), null, choices, 0);
            if (choice == null) return;

            switch ((string)choice.Tag)
            {
                case "change":
                    string path = ConsoleDialogs.PickFolder(_api, this, "Changer la destination", dest.FolderPath);
                    if (path == null) return;
                    int self = i;
                    if (_p.Destinations.Where((x, k) => k != self).Any(x => x != null && ArchiveScanner.SamePath(x.FolderPath, path)))
                    {
                        ConsoleDialogs.Info(this, "Déjà dans la liste", "Ce dossier est déjà une destination de ce profil.");
                        return;
                    }
                    dest.FolderPath = path;
                    break;
                case "rename":
                    string name;
                    if (!ConsoleDialogs.AskText(_api, this, "Nom de la destination",
                            "Nom facultatif (laissez vide pour n'avoir aucun nom) :", dest.Name, out name))
                        return;
                    dest.Name = name;
                    break;
                case "up": Swap(_p.Destinations, i, i - 1); break;
                case "down": Swap(_p.Destinations, i, i + 1); break;
                case "remove": _p.Destinations.RemoveAt(i); break;
                default: return;
            }
            Changed();
        }

        // ------------------------------------------------------------------
        // Actions sur le profil
        // ------------------------------------------------------------------
        private void MakeActive()
        {
            _s.ActiveProfileName = _p.Name;
            Changed();
        }

        private void DeleteProfile()
        {
            if (!ConsoleDialogs.Ask(this, "Supprimer ce profil ?",
                    "Le profil « " + _p.Name + " » sera retiré des réglages (une fois enregistrés). " +
                    "Vos dossiers d'archives et vos jeux installés ne sont PAS touchés.",
                    "Oui, supprimer", "Non, le garder"))
                return;

            bool wasActive = _p.Name == _s.ActiveProfileName;
            _s.Profiles.Remove(_p);
            if (wasActive)
                _s.ActiveProfileName = _s.Profiles.Count > 0 ? _s.Profiles[0].Name : "";

            Deleted = true;
            _touch();
            CloseDialog(true);
        }

        // ------------------------------------------------------------------
        // Manette / clavier (navigation de base : haut/bas parcourent les lignes puis « Terminé »)
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            return _rows.Count > 0 ? _rows[0] : (UIElement)BtnDone;
        }

        protected override IList<UIElement> NavigationRows
        {
            get { return _rows; }
        }

        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnDone }; }
        }

        protected override void FocusRow(UIElement row)
        {
            base.FocusRow(row);
            // Première ligne : on remonte tout en haut pour qu'on voie aussi le titre « NOM ».
            if (_rows.Count > 0 && ReferenceEquals(row, _rows[0])) Scroll.ScrollToTop();
        }

        private void BtnDone_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(true);
        }
    }
}
