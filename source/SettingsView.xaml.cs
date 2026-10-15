using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;

namespace ArchiveStore
{
    // Une ligne de la grille des sources. Une DataGrid ne sait pas modifier directement une liste de
    // chaînes (elle a besoin d'un objet avec une PROPRIÉTÉ à lier) : on enveloppe chaque chemin.
    public class SourceRow : INotifyPropertyChanged
    {
        private string _folder = "";

        public string Folder
        {
            get => _folder;
            set { _folder = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Folder))); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }

    public partial class SettingsView : UserControl
    {
        private readonly PluginSettings _settings;
        private readonly ObservableCollection<Profile> _profiles;
        private readonly IPlayniteAPI _api;

        // Vrai pendant qu'on modifie la ComboBox par code : évite que
        // SelectionChanged ne réécrive ActiveProfileName au mauvais moment.
        private bool _updatingCombo;

        // Éditeurs de SOURCES et de DESTINATIONS du profil sélectionné dans la grille des profils.
        // _current = le profil en cours d'édition ; _loadingEditors = vrai pendant qu'on remplit les
        // grilles par code (aucune recopie vers le profil à ce moment-là).
        private Profile _current;
        private bool _loadingEditors;
        private readonly ObservableCollection<SourceRow> _sourceRows = new ObservableCollection<SourceRow>();
        private readonly ObservableCollection<DestinationEntry> _destinationRows = new ObservableCollection<DestinationEntry>();

        public SettingsView(PluginSettings settings, IPlayniteAPI api)
        {
            InitializeComponent();
            _settings = settings;
            _api = api;

            if (_settings.Profiles == null)
                _settings.Profiles = new List<Profile>();

            // Anciens réglages (une seule source / destination) : on remplit les listes à partir des anciens
            // champs. Sans effet si c'est déjà fait (la méthode ne touche pas des listes non vides).
            foreach (var p in _settings.Profiles) p.MigrateLegacyFields();

            _profiles = new ObservableCollection<Profile>(_settings.Profiles);
            ProfilesGrid.ItemsSource = _profiles;

            SourcesGrid.ItemsSource = _sourceRows;
            DestinationsGrid.ItemsSource = _destinationRows;
            _sourceRows.CollectionChanged += Rows_CollectionChanged;
            _destinationRows.CollectionChanged += Rows_CollectionChanged;

            // On écoute le renommage de CHAQUE profil (voir Profile_PropertyChanged).
            foreach (var p in _profiles)
                p.PropertyChanged += Profile_PropertyChanged;

            _profiles.CollectionChanged += (s, e) => _settings.Profiles = _profiles.ToList();

            InitProfileCombo();

            // Profil affiché dans les éditeurs au départ : le profil actif, sinon le premier.
            ProfilesGrid.SelectedItem = _profiles.FirstOrDefault(p => p.Name == _settings.ActiveProfileName)
                                        ?? _profiles.FirstOrDefault();
            LoadEditors(ProfilesGrid.SelectedItem as Profile);

            var a = _settings.SilentArguments ?? new SilentArgs();
            TxtInno.Text = a.InnoArgs;
            TxtNsis.Text = a.NsisArgs;
            TxtMsi.Text = a.MsiArgs;
            TxtExclusions.Text = string.Join("\n", _settings.ExcludedExePatterns ?? new List<string>());
            TxtSaves.Text = string.Join("\n", _settings.SaveFolderNames ?? new List<string>());
            TxtInstallerWords.Text = string.Join("\n", _settings.ExtraInstallerWords ?? new List<string>());

            // Cases à cocher : valeur initiale AVANT d'attacher les gestionnaires
            // (sinon l'affectation de IsChecked les déclencherait pour rien).
            ChkDirectInstall.IsChecked = _settings.DirectInstall;
            ChkCleanNames.IsChecked = _settings.CleanGameNames;
            ChkConfirmExtras.IsChecked = _settings.ConfirmExtras;
            ChkWordsAnywhere.IsChecked = _settings.InstallerWordsAnywhere;

            // Cases « Notifications » (les erreurs, elles, ne se désactivent jamais).
            ChkNotifyNewExtras.IsChecked = _settings.NotifyNewExtras;
            ChkNotifyMissingPaths.IsChecked = _settings.NotifyMissingPaths;
            ChkNotifyDuplicates.IsChecked = _settings.NotifyDuplicates;
            ChkNotifySummaries.IsChecked = _settings.NotifySummaries;
            ChkNotifyInstallInfo.IsChecked = _settings.NotifyInstallInfo;
            ChkNotifyMenu.IsChecked = _settings.NotifyMenuConfirmations;

            // Cases « Toasts » (en plus des notifications) : valeurs initiales AVANT les gestionnaires.
            ChkToastNewExtras.IsChecked = _settings.ToastNewExtras;
            ChkToastMissingPaths.IsChecked = _settings.ToastMissingPaths;
            ChkToastDuplicates.IsChecked = _settings.ToastDuplicates;
            ChkToastSummaries.IsChecked = _settings.ToastSummaries;
            ChkToastInstallInfo.IsChecked = _settings.ToastInstallInfo;
            ChkToastMenu.IsChecked = _settings.ToastMenuConfirmations;
            ChkToastErrors.IsChecked = _settings.ToastErrors;
            ChkToastsDuringGame.IsChecked = _settings.ToastsDuringGame;
            RbToastQueue.IsChecked = _settings.ToastMode != ToastStackMode.Stacked;
            RbToastStack.IsChecked = _settings.ToastMode == ToastStackMode.Stacked;

            // Curseur « rythme de la progression » : valeur initiale AVANT d'attacher le gestionnaire.
            SldProgressInterval.Value = _settings.ProgressIntervalSeconds;
            TxtProgressInterval.Text = ProgressIntervalLabel(_settings.ProgressIntervalSeconds);

            // « Demander confirmation » n'a de sens que si une fenêtre est permise.
            UpdateConfirmExtrasState();

            ChkDirectInstall.Checked += (s, e) => { _settings.DirectInstall = true; UpdateConfirmExtrasState(); };
            ChkDirectInstall.Unchecked += (s, e) => { _settings.DirectInstall = false; UpdateConfirmExtrasState(); };
            ChkCleanNames.Checked += (s, e) => _settings.CleanGameNames = true;
            ChkCleanNames.Unchecked += (s, e) => _settings.CleanGameNames = false;
            ChkConfirmExtras.Checked += (s, e) => _settings.ConfirmExtras = true;
            ChkConfirmExtras.Unchecked += (s, e) => _settings.ConfirmExtras = false;
            ChkWordsAnywhere.Checked += (s, e) => _settings.InstallerWordsAnywhere = true;
            ChkWordsAnywhere.Unchecked += (s, e) => _settings.InstallerWordsAnywhere = false;

            ChkNotifyNewExtras.Checked += (s, e) => { _settings.NotifyNewExtras = true; UpdateToastStates(); };
            ChkNotifyNewExtras.Unchecked += (s, e) => { _settings.NotifyNewExtras = false; UpdateToastStates(); };
            ChkNotifyMissingPaths.Checked += (s, e) => { _settings.NotifyMissingPaths = true; UpdateToastStates(); };
            ChkNotifyMissingPaths.Unchecked += (s, e) => { _settings.NotifyMissingPaths = false; UpdateToastStates(); };
            ChkNotifyDuplicates.Checked += (s, e) => { _settings.NotifyDuplicates = true; UpdateToastStates(); };
            ChkNotifyDuplicates.Unchecked += (s, e) => { _settings.NotifyDuplicates = false; UpdateToastStates(); };
            ChkNotifySummaries.Checked += (s, e) => { _settings.NotifySummaries = true; UpdateToastStates(); };
            ChkNotifySummaries.Unchecked += (s, e) => { _settings.NotifySummaries = false; UpdateToastStates(); };
            ChkNotifyInstallInfo.Checked += (s, e) => { _settings.NotifyInstallInfo = true; UpdateToastStates(); };
            ChkNotifyInstallInfo.Unchecked += (s, e) => { _settings.NotifyInstallInfo = false; UpdateToastStates(); };
            ChkNotifyMenu.Checked += (s, e) => { _settings.NotifyMenuConfirmations = true; UpdateToastStates(); };
            ChkNotifyMenu.Unchecked += (s, e) => { _settings.NotifyMenuConfirmations = false; UpdateToastStates(); };

            ChkToastNewExtras.Checked += (s, e) => _settings.ToastNewExtras = true;
            ChkToastNewExtras.Unchecked += (s, e) => _settings.ToastNewExtras = false;
            ChkToastMissingPaths.Checked += (s, e) => _settings.ToastMissingPaths = true;
            ChkToastMissingPaths.Unchecked += (s, e) => _settings.ToastMissingPaths = false;
            ChkToastDuplicates.Checked += (s, e) => _settings.ToastDuplicates = true;
            ChkToastDuplicates.Unchecked += (s, e) => _settings.ToastDuplicates = false;
            ChkToastSummaries.Checked += (s, e) => _settings.ToastSummaries = true;
            ChkToastSummaries.Unchecked += (s, e) => _settings.ToastSummaries = false;
            ChkToastInstallInfo.Checked += (s, e) => _settings.ToastInstallInfo = true;
            ChkToastInstallInfo.Unchecked += (s, e) => _settings.ToastInstallInfo = false;
            ChkToastMenu.Checked += (s, e) => _settings.ToastMenuConfirmations = true;
            ChkToastMenu.Unchecked += (s, e) => _settings.ToastMenuConfirmations = false;
            ChkToastErrors.Checked += (s, e) => _settings.ToastErrors = true;
            ChkToastErrors.Unchecked += (s, e) => _settings.ToastErrors = false;
            ChkToastsDuringGame.Checked += (s, e) => _settings.ToastsDuringGame = true;
            ChkToastsDuringGame.Unchecked += (s, e) => _settings.ToastsDuringGame = false;
            RbToastQueue.Checked += (s, e) => _settings.ToastMode = ToastStackMode.OneAfterAnother;
            RbToastStack.Checked += (s, e) => _settings.ToastMode = ToastStackMode.Stacked;

            // Une case « toast » n'a de sens que si la notification correspondante est cochée.
            UpdateToastStates();

            SldProgressInterval.ValueChanged += (s, e) =>
            {
                int seconds = (int)Math.Round(SldProgressInterval.Value);
                _settings.ProgressIntervalSeconds = seconds;
                TxtProgressInterval.Text = ProgressIntervalLabel(seconds);
            };

            TxtInno.TextChanged += (s, e) => EnsureArgs().InnoArgs = TxtInno.Text;
            TxtNsis.TextChanged += (s, e) => EnsureArgs().NsisArgs = TxtNsis.Text;
            TxtMsi.TextChanged += (s, e) => EnsureArgs().MsiArgs = TxtMsi.Text;
            TxtExclusions.TextChanged += (s, e) =>
                _settings.ExcludedExePatterns = LinesToList(TxtExclusions.Text);
            TxtSaves.TextChanged += (s, e) =>
                _settings.SaveFolderNames = LinesToList(TxtSaves.Text);
            TxtInstallerWords.TextChanged += (s, e) =>
                _settings.ExtraInstallerWords = LinesToList(TxtInstallerWords.Text);
        }

        // « toutes les 5 secondes » / « toutes les secondes » (jamais 1 ici : le minimum est 2).
        private static string ProgressIntervalLabel(int seconds)
            => $"toutes les {seconds} secondes";

        // PÉDAGOGIE — IsEnabled : une case désactivée ("grisée") ne réagit plus aux clics.
        // Avec « Installation directe » cochée, aucune fenêtre ne s'ouvre : la question
        // « confirmer avant d'installer les DLC ? » n'a donc pas de sens. On grise la case
        // (IsEnabled) et on baisse son Opacity, car notre Foreground fixé à la main
        // ne s'estompe pas tout seul quand la case est désactivée.
        private void UpdateConfirmExtrasState()
        {
            bool windowsAllowed = !_settings.DirectInstall;
            ChkConfirmExtras.IsEnabled = windowsAllowed;
            ChkConfirmExtras.Opacity = windowsAllowed ? 1.0 : 0.5;
        }

        // PÉDAGOGIE — un toast s'AJOUTE à une notification : si la notification d'une catégorie est décochée,
        // sa case « toast » est grisée (IsEnabled = faux, et Opacity baissée car notre Foreground fixé à la main ne
        // s'estompe pas tout seul). La valeur de la case n'est PAS modifiée : elle redevient active si on recoche
        // la notification. « Erreurs et blocages » n'a pas de case « notification » : toujours active.
        private void UpdateToastStates()
        {
            SetEnabled(ChkToastNewExtras, _settings.NotifyNewExtras);
            SetEnabled(ChkToastMissingPaths, _settings.NotifyMissingPaths);
            SetEnabled(ChkToastDuplicates, _settings.NotifyDuplicates);
            SetEnabled(ChkToastSummaries, _settings.NotifySummaries);
            SetEnabled(ChkToastInstallInfo, _settings.NotifyInstallInfo);
            SetEnabled(ChkToastMenu, _settings.NotifyMenuConfirmations);
        }

        private static void SetEnabled(CheckBox box, bool enabled)
        {
            box.IsEnabled = enabled;
            box.Opacity = enabled ? 1.0 : 0.5;
        }

        // Bouton « Afficher des toasts de test » : utilise les réglages actuels (même non encore enregistrés).
        private void BtnTestToast_Click(object sender, RoutedEventArgs e)
        {
            ToastHelper.ShowTest(_api, _settings);
        }

        private SilentArgs EnsureArgs()
        {
            if (_settings.SilentArguments == null)
                _settings.SilentArguments = new SilentArgs();
            return _settings.SilentArguments;
        }

        private static List<string> LinesToList(string text)
            => text.Split('\n')
                   .Select(l => l.Trim())
                   .Where(l => l.Length > 0)
                   .ToList();

        // ==================================================================
        // PROFILS
        // ==================================================================
        private void BtnAjouterProfil_Click(object sender, RoutedEventArgs e)
        {
            var nouveau = new Profile
            {
                Name = UniqueProfileName()
            };
            nouveau.PropertyChanged += Profile_PropertyChanged;
            _profiles.Add(nouveau);
            ProfilesGrid.SelectedItem = nouveau;

            // Premier profil créé : il devient automatiquement le profil actif.
            if (ComboActif.SelectedItem == null)
                SelectActive(nouveau);
        }

        private void BtnSupprimerProfil_Click(object sender, RoutedEventArgs e)
        {
            if (!(ProfilesGrid.SelectedItem is Profile p)) return;

            p.PropertyChanged -= Profile_PropertyChanged;
            _profiles.Remove(p);
            if (ReferenceEquals(_current, p))
                LoadEditors(ProfilesGrid.SelectedItem as Profile); // sélection suivante, ou éditeurs vides

            // Si on vient de supprimer le profil actif, on bascule sur le premier restant.
            if (!_profiles.Any(x => x.Name == _settings.ActiveProfileName))
                SelectActive(_profiles.FirstOrDefault());
        }

        // "Nouveau profil", "Nouveau profil 2"… : les noms doivent être uniques.
        private string UniqueProfileName()
        {
            const string baseName = "Nouveau profil";
            var name = baseName;
            int i = 2;
            while (_profiles.Any(p => p.Name == name))
                name = baseName + " " + i++;
            return name;
        }

        // Quand l'utilisateur RENOMME le profil actif dans la grille, on suit le
        // nouveau nom : sinon ActiveProfileName pointerait vers un nom disparu
        // et VerifySettings refuserait la validation.
        private void Profile_PropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(Profile.Name) &&
                sender is Profile p &&
                ReferenceEquals(p, ComboActif.SelectedItem))
            {
                _settings.ActiveProfileName = p.Name;
            }

            // Les titres des éditeurs affichent le nom du profil en cours d'édition.
            if (e.PropertyName == nameof(Profile.Name) && ReferenceEquals(sender, _current))
            {
                TxtSourcesTitle.Text = "SOURCES DU PROFIL « " + _current.Name + " »";
                TxtDestinationsTitle.Text = "DESTINATIONS DU PROFIL « " + _current.Name + " »";
            }
        }

        // ==================================================================
        // Sélecteurs de dossiers — API Playnite : IDialogsFactory.SelectFolder.
        // Vérifié dans la doc du SDK : deux surcharges existent,
        //   string SelectFolder()
        //   string SelectFolder(string initialDir)   ← le paramètre est le
        //   DOSSIER DE DÉPART (pas un titre !). Retourne une chaîne vide si
        //   l'utilisateur annule.
        // ==================================================================
        // ==================================================================
        // ÉDITEURS DE SOURCES ET DE DESTINATIONS (profil sélectionné)
        //
        // PÉDAGOGIE : les grilles travaillent sur des collections « observables » (_sourceRows,
        // _destinationRows). À chaque changement (ligne ajoutée, supprimée, déplacée, chemin modifié),
        // SyncCurrentProfile() recopie ces lignes dans le profil. Le profil garde ainsi toujours
        // l'état affiché à l'écran ; si l'utilisateur annule les réglages, Playnite restaure la copie
        // faite par BeginEdit (voir PluginSettings.CancelEdit).
        // ==================================================================
        private void ProfilesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            LoadEditors(ProfilesGrid.SelectedItem as Profile);
        }

        // Affiche les sources et destinations du profil donné (null = aucun profil sélectionné).
        private void LoadEditors(Profile profile)
        {
            _loadingEditors = true;
            try
            {
                _current = profile;

                foreach (var r in _sourceRows) r.PropertyChanged -= Row_PropertyChanged;
                foreach (var d in _destinationRows) d.PropertyChanged -= Row_PropertyChanged;
                _sourceRows.Clear();
                _destinationRows.Clear();

                EditorsPanel.IsEnabled = profile != null;
                EditorsPanel.Opacity = profile != null ? 1.0 : 0.5;

                if (profile == null)
                {
                    TxtSourcesTitle.Text = "SOURCES DU PROFIL";
                    TxtDestinationsTitle.Text = "DESTINATIONS DU PROFIL";
                    return;
                }

                TxtSourcesTitle.Text = "SOURCES DU PROFIL « " + profile.Name + " »";
                TxtDestinationsTitle.Text = "DESTINATIONS DU PROFIL « " + profile.Name + " »";

                foreach (var path in profile.GetSources())
                {
                    var row = new SourceRow { Folder = path };
                    row.PropertyChanged += Row_PropertyChanged;
                    _sourceRows.Add(row);
                }
                foreach (var dest in profile.GetDestinations())
                {
                    dest.PropertyChanged += Row_PropertyChanged;
                    _destinationRows.Add(dest);
                }
            }
            finally
            {
                _loadingEditors = false;
            }

            // Le profil prend l'état affiché (même contenu, mais instances de destinations unifiées).
            SyncCurrentProfile();
        }

        private void Row_PropertyChanged(object sender, PropertyChangedEventArgs e) => SyncCurrentProfile();

        private void Rows_CollectionChanged(object sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
            => SyncCurrentProfile();

        // Recopie les lignes affichées dans le profil en cours d'édition.
        // Les listes sont copiées TELLES QUELLES (lignes vides et doublons compris) : c'est VerifySettings
        // qui les signale à l'utilisateur ; Normalize les nettoie à la validation.
        private void SyncCurrentProfile()
        {
            if (_loadingEditors || _current == null) return;

            _current.Sources = _sourceRows.Select(r => r.Folder ?? "").ToList();
            _current.Destinations = _destinationRows.ToList();
            _current.RefreshLegacyFields();
        }

        // --- Sources ---
        // Bouton « Ouvrir le journal » : ouvre le dossier d'archivestore.log (voir ArchiveLog.OpenLogFolder).
        // Une erreur est expliquée dans une fenêtre, jamais d'exception vers Playnite.
        private void BtnOpenLog_Click(object sender, RoutedEventArgs e)
        {
            string error;
            if (!ArchiveLog.OpenLogFolder(out error))
                _api.Dialogs.ShowErrorMessage(error, "ArchiveStore");
        }

        private void BtnAddSource_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var path = PickFolder(null);
            if (string.IsNullOrEmpty(path)) return;

            var row = new SourceRow { Folder = path };
            row.PropertyChanged += Row_PropertyChanged;
            _sourceRows.Add(row);
            SourcesGrid.SelectedItem = row;
        }

        private void BtnRemoveSource_Click(object sender, RoutedEventArgs e)
        {
            var row = SourcesGrid.SelectedItem as SourceRow;
            if (row == null) return;
            row.PropertyChanged -= Row_PropertyChanged;
            _sourceRows.Remove(row);
        }

        private void BtnSourceUp_Click(object sender, RoutedEventArgs e) => MoveRow(_sourceRows, SourcesGrid, -1);
        private void BtnSourceDown_Click(object sender, RoutedEventArgs e) => MoveRow(_sourceRows, SourcesGrid, +1);

        private void BtnBrowseSourceRow_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is SourceRow row)) return;
            var path = PickFolder(row.Folder);
            if (!string.IsNullOrEmpty(path)) row.Folder = path;
        }

        // --- Destinations ---
        private void BtnAddDestination_Click(object sender, RoutedEventArgs e)
        {
            if (_current == null) return;
            var path = PickFolder(null);
            if (string.IsNullOrEmpty(path)) return;

            var dest = new DestinationEntry { Name = "", FolderPath = path };
            dest.PropertyChanged += Row_PropertyChanged;
            _destinationRows.Add(dest);
            DestinationsGrid.SelectedItem = dest;
        }

        private void BtnRemoveDestination_Click(object sender, RoutedEventArgs e)
        {
            var dest = DestinationsGrid.SelectedItem as DestinationEntry;
            if (dest == null) return;
            dest.PropertyChanged -= Row_PropertyChanged;
            _destinationRows.Remove(dest);
        }

        private void BtnDestinationUp_Click(object sender, RoutedEventArgs e) => MoveRow(_destinationRows, DestinationsGrid, -1);
        private void BtnDestinationDown_Click(object sender, RoutedEventArgs e) => MoveRow(_destinationRows, DestinationsGrid, +1);

        private void BtnBrowseDestinationRow_Click(object sender, RoutedEventArgs e)
        {
            if (!(sender is FrameworkElement fe) || !(fe.DataContext is DestinationEntry dest)) return;
            var path = PickFolder(dest.FolderPath);
            if (!string.IsNullOrEmpty(path)) dest.FolderPath = path;
        }

        // Monte (-1) ou descend (+1) la ligne sélectionnée d'une grille.
        private static void MoveRow<T>(ObservableCollection<T> rows, DataGrid grid, int delta) where T : class
        {
            var item = grid.SelectedItem as T;
            if (item == null) return;
            int from = rows.IndexOf(item);
            int to = from + delta;
            if (from < 0 || to < 0 || to >= rows.Count) return;
            rows.Move(from, to);
            grid.SelectedItem = item;
        }

        // Méthode isolée : UN seul endroit pour le sélecteur de dossier.
        private string PickFolder(string current)
        {
            // On ouvre le sélecteur directement sur le chemin déjà saisi, s'il existe.
            if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
                return _api.Dialogs.SelectFolder(current);

            return _api.Dialogs.SelectFolder();
        }

        // ==================================================================
        // PROFIL ACTIF
        // ==================================================================
        // La ComboBox est liée directement aux objets Profile (et non plus à
        // une liste de noms) : DisplayMemberPath="Name" affiche le nom, et se
        // met à jour tout seul si le profil est renommé (INotifyPropertyChanged).
        private void InitProfileCombo()
        {
            ComboActif.DisplayMemberPath = nameof(Profile.Name);
            ComboActif.ItemsSource = _profiles;

            var active = _profiles.FirstOrDefault(p => p.Name == _settings.ActiveProfileName)
                         ?? _profiles.FirstOrDefault();
            SelectActive(active);

            // Le gestionnaire n'est attaché QU'APRÈS la sélection initiale.
            ComboActif.SelectionChanged += ComboActif_SelectionChanged;
        }

        private void SelectActive(Profile profile)
        {
            _updatingCombo = true;
            try
            {
                ComboActif.SelectedItem = profile;
                _settings.ActiveProfileName = profile?.Name ?? "";
            }
            finally
            {
                _updatingCombo = false;
            }
        }

        private void ComboActif_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_updatingCombo) return;
            if (ComboActif.SelectedItem is Profile sel)
                _settings.ActiveProfileName = sel.Name;
        }
    }
}
