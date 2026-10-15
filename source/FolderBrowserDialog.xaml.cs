using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Playnite.SDK;

namespace ArchiveStore
{
    // ======================================================================
    // FolderBrowserItem — une ligne du navigateur de dossiers.
    // ======================================================================
    public enum FolderItemKind
    {
        Choose,   // « ✓ Choisir ce dossier »
        Parent,   // « ↑ Dossier parent » (ou « Liste des disques » depuis la racine d'un disque)
        Folder,   // un sous-dossier
        Drive,    // un disque
        Info      // simple information (« Chargement… », « Aucun sous-dossier », erreur) : non cliquable
    }

    public class FolderBrowserItem
    {
        public FolderItemKind Kind { get; set; }
        public string Label { get; set; }
        public string Detail { get; set; }

        /// <summary>Chemin visé par la ligne (null pour « Parent » depuis la racine = liste des disques).</summary>
        public string Path { get; set; }

        public Visibility DetailVisibility
        {
            get { return string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible; }
        }
    }

    // ======================================================================
    // FolderBrowserDialog — choisir un dossier à la manette.
    //
    // PRINCIPE : on part de la liste des disques, on descend de dossier en dossier (A), on remonte (gauche),
    // et on valide avec la ligne « ✓ Choisir ce dossier » (A).
    //
    // UNE AUTRE FAÇON DE CHOISIR, dans la même fenêtre :
    //   Y = « Boîte Windows » : le sélecteur de dossier de Windows (IDialogsFactory.SelectFolder),
    //                           pratique avec une souris, et seul moyen d'atteindre un partage réseau
    //                           non relié à une lettre de lecteur.
    //   (Le clavier à l'écran de Playnite n'a pas la touche « \ » : la saisie d'un chemin a donc été retirée.)
    //
    // POURQUOI DE L'ASYNCHRONE ? Lister un disque réseau lent ou débranché peut bloquer plusieurs secondes.
    // Le listing se fait donc dans une tâche de fond (Task.Run) ; la fenêtre affiche « Chargement… » et reste
    // utilisable. Un numéro de demande (_loadId) permet d'ignorer une réponse devenue inutile
    // (l'utilisateur a déjà changé de dossier). Rien n'est jamais créé, modifié ou supprimé ici : lecture seule.
    // ======================================================================
    public partial class FolderBrowserDialog : ConsoleWindow
    {
        private readonly IPlayniteAPI _api;
        private string _currentPath;     // null = liste des disques
        private int _loadId;
        private bool _closed;

        /// <summary>Dossier choisi (valide si ShowDialog() a renvoyé vrai).</summary>
        public string SelectedPath { get; private set; }

        public FolderBrowserDialog(IPlayniteAPI api, string title, string startPath)
        {
            InitializeComponent();
            _api = api;

            TitleText.Text = string.IsNullOrEmpty(title) ? "Choisir un dossier" : title;
            HelpText.Text = "Gauche/droite : dossier parent / ouvrir le dossier sélectionné. " +
                            "Y : sélecteur de dossier Windows. " +
                            "Rien n'est créé ni modifié ici : on ne fait que choisir.";

            Closed += (s, e) => _closed = true;

            Navigate(startPath, null, true);
        }

        // ------------------------------------------------------------------
        // Chargement d'un dossier (tâche de fond) puis affichage
        // ------------------------------------------------------------------
        private class ListResult
        {
            public string Path;                                   // dossier réellement lu (null = disques)
            public List<FolderBrowserItem> Items = new List<FolderBrowserItem>();
            public string Error;
        }

        // selectPath : chemin à re-sélectionner après le chargement (ex. le dossier d'où l'on vient).
        // firstRow   : vrai à l'ouverture de la fenêtre → on sélectionne la 1re ligne.
        private void Navigate(string path, string selectPath, bool firstRow)
        {
            int id = ++_loadId;
            PathText.Text = path == null ? "Ce PC — choisissez un disque" : path;
            SetItems(new List<FolderBrowserItem>
            {
                new FolderBrowserItem { Kind = FolderItemKind.Info, Label = "Chargement…" }
            }, -1);

            string requested = path;
            Task.Run(() => ReadFolder(requested)).ContinueWith(t =>
            {
                ListResult result = t.IsFaulted
                    ? new ListResult { Error = "Lecture impossible." }
                    : t.Result;
                try
                {
                    Dispatcher.BeginInvoke(new Action(() => Apply(id, result, selectPath, firstRow)));
                }
                catch (Exception) { /* application en cours de fermeture */ }
            });
        }

        // Tâche de fond : ne touche JAMAIS à l'interface. Lecture seule.
        private static ListResult ReadFolder(string requested)
        {
            var r = new ListResult();
            try
            {
                // Si le dossier demandé n'existe pas (ou plus), on remonte jusqu'à un dossier qui existe.
                string path = requested;
                while (!string.IsNullOrEmpty(path) && !Directory.Exists(path))
                {
                    string parent = null;
                    try { parent = Path.GetDirectoryName(path.TrimEnd('\\', '/')); }
                    catch (ArgumentException) { }
                    path = parent;
                }
                r.Path = string.IsNullOrEmpty(path) ? null : path;

                if (r.Path == null)
                {
                    // Liste des disques
                    foreach (var d in DriveInfo.GetDrives())
                    {
                        string detail;
                        try
                        {
                            if (d.IsReady)
                            {
                                string vol = d.VolumeLabel ?? "";
                                detail = (vol.Length > 0 ? vol + " — " : "")
                                       + ArchiveScanner.FormatSize(d.AvailableFreeSpace) + " libres sur "
                                       + ArchiveScanner.FormatSize(d.TotalSize);
                                if (d.DriveType == DriveType.Network) detail += " — réseau";
                                else if (d.DriveType == DriveType.Removable) detail += " — amovible";
                            }
                            else detail = "Non prêt";
                        }
                        catch (Exception) { detail = "Inaccessible"; }

                        r.Items.Add(new FolderBrowserItem
                        {
                            Kind = FolderItemKind.Drive,
                            Label = d.Name,
                            Detail = detail,
                            Path = d.Name
                        });
                    }
                    return r;
                }

                // Sous-dossiers (sans les dossiers « Système » comme $RECYCLE.BIN), triés par nom.
                var subs = new List<DirectoryInfo>();
                try
                {
                    foreach (var sub in new DirectoryInfo(r.Path).EnumerateDirectories())
                    {
                        try { if ((sub.Attributes & FileAttributes.System) != 0) continue; }
                        catch (Exception) { }
                        subs.Add(sub);
                    }
                }
                catch (UnauthorizedAccessException) { r.Error = "Accès refusé à ce dossier."; }
                catch (IOException ex) { r.Error = "Impossible de lire ce dossier : " + ex.Message; }

                subs.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
                foreach (var sub in subs)
                    r.Items.Add(new FolderBrowserItem { Kind = FolderItemKind.Folder, Label = sub.Name, Path = sub.FullName });
            }
            catch (Exception ex)
            {
                r.Error = "Lecture impossible : " + ex.Message;
            }
            return r;
        }

        // Thread de l'interface : construit les lignes et les affiche.
        private void Apply(int id, ListResult result, string selectPath, bool firstRow)
        {
            if (_closed || id != _loadId) return; // réponse périmée : l'utilisateur a déjà navigué ailleurs

            _currentPath = result.Path;
            PathText.Text = _currentPath == null ? "Ce PC — choisissez un disque" : _currentPath;

            var items = new List<FolderBrowserItem>();
            if (_currentPath != null)
            {
                items.Add(new FolderBrowserItem
                {
                    Kind = FolderItemKind.Choose,
                    Label = "✓  Choisir ce dossier",
                    Detail = _currentPath,
                    Path = _currentPath
                });

                string parent = ParentOf(_currentPath);
                items.Add(new FolderBrowserItem
                {
                    Kind = FolderItemKind.Parent,
                    Label = "↑  Dossier parent",
                    Detail = parent ?? "Liste des disques",
                    Path = parent
                });
            }

            items.AddRange(result.Items);

            if (!string.IsNullOrEmpty(result.Error))
                items.Add(new FolderBrowserItem { Kind = FolderItemKind.Info, Label = result.Error });
            else if (_currentPath != null && result.Items.Count == 0)
                items.Add(new FolderBrowserItem { Kind = FolderItemKind.Info, Label = "(aucun sous-dossier)" });
            else if (_currentPath == null && result.Items.Count == 0)
                items.Add(new FolderBrowserItem { Kind = FolderItemKind.Info, Label = "(aucun disque trouvé)" });

            // Ligne à sélectionner : celle d'où l'on vient, sinon (hors ouverture) le premier sous-dossier / disque.
            int select = 0;
            if (selectPath != null)
            {
                int k = items.FindIndex(it => it.Path != null && ArchiveScanner.SamePath(it.Path, selectPath)
                                              && (it.Kind == FolderItemKind.Folder || it.Kind == FolderItemKind.Drive));
                if (k >= 0) select = k;
            }
            else if (!firstRow)
            {
                int k = items.FindIndex(it => it.Kind == FolderItemKind.Folder || it.Kind == FolderItemKind.Drive);
                if (k >= 0) select = k;
            }

            SetItems(items, select);
        }

        private void SetItems(List<FolderBrowserItem> items, int select)
        {
            FolderList.ItemsSource = items;
            if (select >= 0 && select < items.Count)
            {
                FolderList.SelectedIndex = select;
                FolderList.ScrollIntoView(FolderList.SelectedItem);
            }
            // Le focus reste sur la liste (sauf si l'utilisateur est sur un bouton du pied de fenêtre).
            if (!(CurrentFocus is Button)) FolderList.Focus();
        }

        // Dossier parent d'un chemin ; null quand on est à la racine d'un disque (→ liste des disques).
        private static string ParentOf(string path)
        {
            try
            {
                string p = Path.GetDirectoryName(path.TrimEnd('\\', '/'));
                return string.IsNullOrEmpty(p) ? null : p;
            }
            catch (ArgumentException) { return null; }
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            return FolderList;
        }

        protected override IList<UIElement> FooterButtons
        {
            get { return new List<UIElement> { BtnCancel }; }
        }

        protected override bool OnNavigate(GamepadAction action)
        {
            bool inFooter = ReferenceEquals(CurrentFocus, BtnCancel);

            switch (action)
            {
                case GamepadAction.Up:
                    if (inFooter) { FolderList.Focus(); return true; }
                    MoveSelection(-1);
                    return true;

                case GamepadAction.Down:
                    if (inFooter) return true;
                    if (FolderList.SelectedIndex >= FolderList.Items.Count - 1 && FolderList.Items.Count > 0)
                    {
                        BtnCancel.Focus(); // après la dernière ligne : bouton « Annuler »
                        return true;
                    }
                    MoveSelection(+1);
                    return true;

                case GamepadAction.Left:
                    if (!inFooter) GoUp();
                    return true;

                case GamepadAction.Right:
                    if (!inFooter) EnterSelected();
                    return true;
            }
            return true;
        }

        private void MoveSelection(int delta)
        {
            int count = FolderList.Items.Count;
            if (count == 0) return;

            int index = FolderList.SelectedIndex < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Max(0, Math.Min(count - 1, FolderList.SelectedIndex + delta));

            FolderList.SelectedIndex = index;
            FolderList.ScrollIntoView(FolderList.SelectedItem);
            FolderList.Focus();
        }

        protected override void OnConfirm()
        {
            if (ReferenceEquals(CurrentFocus, BtnCancel)) { ClickButton(BtnCancel); return; }
            ActivateItem(FolderList.SelectedItem as FolderBrowserItem);
        }

        protected override void OnActionY() { WindowsBox(); }

        // ------------------------------------------------------------------
        // Actions
        // ------------------------------------------------------------------
        private void GoUp()
        {
            if (_currentPath == null) return; // déjà à la liste des disques
            Navigate(ParentOf(_currentPath), _currentPath, false);
        }

        private void EnterSelected()
        {
            var item = FolderList.SelectedItem as FolderBrowserItem;
            if (item == null) return;
            if (item.Kind == FolderItemKind.Folder || item.Kind == FolderItemKind.Drive)
                Navigate(item.Path, null, false);
        }

        private void ActivateItem(FolderBrowserItem item)
        {
            if (item == null) return;
            switch (item.Kind)
            {
                case FolderItemKind.Choose:
                    SelectedPath = item.Path;
                    CloseDialog(true);
                    break;
                case FolderItemKind.Parent:
                    Navigate(item.Path, _currentPath, false);
                    break;
                case FolderItemKind.Folder:
                case FolderItemKind.Drive:
                    Navigate(item.Path, null, false);
                    break;
                // Info : rien à faire
            }
        }

        // Un clic sur une ligne l'active (comme A).
        private void FolderList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var row = ItemsControl.ContainerFromElement(FolderList, e.OriginalSource as DependencyObject) as ListBoxItem;
            if (row == null) return;
            row.IsSelected = true;
            ActivateItem(row.Content as FolderBrowserItem);
        }

        private void HintY_Click(object sender, MouseButtonEventArgs e) { WindowsBox(); }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }

        // Y — « Boîte Windows » : IDialogsFactory.SelectFolder (renvoie une chaîne vide si on annule).
        private void WindowsBox()
        {
            string start = (_currentPath != null && ExistsQuick(_currentPath)) ? _currentPath : null;
            string picked;
            try
            {
                picked = ConsoleDialogs.RunOutside(this, () => start != null
                    ? _api.Dialogs.SelectFolder(start)
                    : _api.Dialogs.SelectFolder());
            }
            catch (Exception ex)
            {
                ConsoleDialogs.Info(this, "Boîte Windows indisponible", "Impossible d'ouvrir la boîte : " + ex.Message);
                return;
            }

            if (string.IsNullOrWhiteSpace(picked)) return; // annulé
            SelectedPath = picked.Trim();
            CloseDialog(true);
        }

        // Directory.Exists sur un partage réseau éteint peut bloquer longtemps : on attend au plus 4 secondes.
        // Au-delà, on considère le dossier comme inaccessible.
        private static bool ExistsQuick(string path)
        {
            try
            {
                var task = Task.Run(() => Directory.Exists(path));
                return task.Wait(4000) && task.Result;
            }
            catch (Exception)
            {
                return false;
            }
        }
    }
}
