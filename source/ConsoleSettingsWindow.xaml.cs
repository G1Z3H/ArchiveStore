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
    // ConsoleSettingsWindow — les réglages d'ArchiveStore, utilisables à la MANETTE (mode Plein écran).
    //
    // POURQUOI ? Playnite n'affiche l'écran de réglages d'un plugin (GetSettingsView) qu'en mode Bureau.
    // Cette fenêtre est une SECONDE façon de modifier EXACTEMENT les mêmes réglages : elle est ouverte par
    // le menu Extensions → « ArchiveStore — Réglages », en mode Bureau comme en mode Plein écran. L'écran
    // classique (mode Bureau) reste inchangé et reste disponible.
    //
    // DISPOSITION : deux colonnes. À gauche les catégories, à droite les réglages de la catégorie choisie.
    //   • haut/bas dans la colonne de gauche : change de catégorie (l'aperçu suit) ;
    //   • droite (ou A) : passe aux réglages ; gauche : revient aux catégories ;
    //   • A : coche une case / ouvre le réglage (menu, clavier à l'écran, navigateur de dossiers) ;
    //   • Y : enregistrer ; B : annuler (une confirmation est demandée s'il y a des modifications).
    //
    // PÉDAGOGIE — comment les réglages sont modifiés :
    //   Le plugin appelle BeginEdit() AVANT d'ouvrir cette fenêtre (on garde une copie des réglages).
    //   Les réglages (PluginSettings) sont ensuite modifiés EN DIRECT. Si on enregistre, le plugin appelle
    //   EndEdit() (nettoyage + écriture sur le disque) ; sinon CancelEdit() restaure la copie.
    //   « Enregistrer » vérifie d'abord les réglages (VerifySettings) : en cas d'erreur, la fenêtre reste
    //   ouverte et explique le problème, exactement comme l'écran classique.
    //
    // Le contenu de la colonne de droite est CONSTRUIT EN CODE (voir ConsoleRows) : une catégorie = une pile
    // de lignes. Après un changement qui modifie l'affichage, on reconstruit la catégorie (Rebuild).
    // ======================================================================
    public partial class ConsoleSettingsWindow : ConsoleWindow
    {
        private readonly PluginSettings _s;
        private readonly IPlayniteAPI _api;

        private readonly List<Button> _nav = new List<Button>();       // boutons de la colonne de gauche
        private readonly List<UIElement> _rows = new List<UIElement>(); // lignes utilisables de la catégorie affichée
        private int _cat;                                                // catégorie affichée
        private bool _dirty;                                             // des réglages ont été touchés

        private static readonly string[] CategoryNames =
        {
            "Profils",
            "Comportement",
            "Notifications",
            "Toasts",
            "DLC et patchs",
            "Installeurs non reconnus",
            "Installation silencieuse",
            "Exclusions et sauvegardes",
            "Journal"
        };

        private static readonly string[] CategoryTitles =
        {
            "Profils",
            "Comportement",
            "Notifications",
            "Toasts",
            "DLC et patchs",
            "Installeurs non reconnus (cas ambigu)",
            "Arguments d'installation silencieuse",
            "Exclusions et sauvegardes",
            "Journal"
        };

        public ConsoleSettingsWindow(PluginSettings settings, IPlayniteAPI api)
        {
            InitializeComponent();
            _s = settings;
            _api = api;

            EnsureSettings();

            // Taille fixe (les réglages défilent à l'intérieur) : jamais plus grande que l'écran.
            var area = SystemParameters.WorkArea;
            SizeToContent = SizeToContent.Manual;
            Width = Math.Min(1228, area.Width);
            Height = Math.Min(940, area.Height);

            for (int i = 0; i < CategoryNames.Length; i++)
            {
                int index = i;
                var b = new Button { Content = CategoryNames[i], Style = (Style)FindResource("NavButton") };
                b.Click += (s, e) => ShowCategory(index);
                _nav.Add(b);
                NavPanel.Children.Add(b);
            }

            ShowCategory(0);
        }

        // Les réglages chargés d'un très ancien fichier peuvent contenir des listes vides (null) : on les répare ici
        // pour que le reste de la fenêtre n'ait jamais à s'en soucier.
        private void EnsureSettings()
        {
            if (_s.Profiles == null) _s.Profiles = new List<Profile>();
            foreach (var p in _s.Profiles) p.MigrateLegacyFields();
            if (_s.SilentArguments == null) _s.SilentArguments = new SilentArgs();
            if (_s.ExcludedExePatterns == null) _s.ExcludedExePatterns = new List<string>();
            if (_s.SaveFolderNames == null) _s.SaveFolderNames = new List<string>();
            if (_s.ExtraInstallerWords == null) _s.ExtraInstallerWords = new List<string>();
        }

        private void Touch()
        {
            _dirty = true;
        }

        // ------------------------------------------------------------------
        // Affichage d'une catégorie
        // ------------------------------------------------------------------
        private void ShowCategory(int index)
        {
            _cat = index;
            for (int k = 0; k < _nav.Count; k++) _nav[k].Tag = (k == index) ? "sel" : null;

            CategoryTitle.Text = CategoryTitles[index];
            ContentHost.Children.Clear();
            _rows.Clear();

            switch (index)
            {
                case 0: BuildProfiles(); break;
                case 1: BuildBehavior(); break;
                case 2: BuildNotifications(); break;
                case 3: BuildToasts(); break;
                case 4: BuildExtras(); break;
                case 5: BuildUnknownInstallers(); break;
                case 6: BuildSilent(); break;
                case 7: BuildLists(); break;
                default: BuildLog(); break;
            }

            ContentScroll.ScrollToTop();
        }

        // Reconstruit la catégorie affichée en gardant la position de défilement et la ligne qui avait le focus.
        private void Rebuild()
        {
            int idx = _rows.IndexOf(CurrentFocus as UIElement);
            double offset = ContentScroll.VerticalOffset;
            ShowCategory(_cat);
            if (idx < 0) return;

            // Priorité « Loaded » : on attend que les nouvelles lignes soient en place.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ContentScroll.ScrollToVerticalOffset(offset);
                if (_rows.Count > 0) FocusRow(_rows[Math.Min(idx, _rows.Count - 1)]);
            }), DispatcherPriority.Loaded);
        }

        // --- petites aides de construction ---
        private void AddText(UIElement element)
        {
            ContentHost.Children.Add(element);
        }

        private void AddRow(Button row)
        {
            ContentHost.Children.Add(row);
            _rows.Add(row);
        }

        // Case à cocher : le réglage est modifié tout de suite ; « note » (facultative) est affichée dessous.
        private void AddCheck(string text, bool value, Action<bool> setter, string note, bool enabled)
        {
            var cb = ConsoleRows.Check(this, text, value, v => { setter(v); Touch(); });
            cb.IsEnabled = enabled;
            ContentHost.Children.Add(cb);
            _rows.Add(cb);
            if (!string.IsNullOrEmpty(note))
                ContentHost.Children.Add(ConsoleRows.CheckNote(this, note));
        }

        private void AddCheck(string text, bool value, Action<bool> setter, string note)
        {
            AddCheck(text, value, setter, note, true);
        }

        private static string Plural(int n, string singular)
        {
            return n + " " + singular + (n > 1 ? "s" : "");
        }

        // ------------------------------------------------------------------
        // Catégorie 1 — PROFILS
        // ------------------------------------------------------------------
        private void BuildProfiles()
        {
            AddText(ConsoleRows.Note(this,
                "Un profil regroupe une ou plusieurs SOURCES (dossiers d'archives) et une ou plusieurs DESTINATIONS " +
                "(dossiers d'installation). Choisissez un profil pour modifier son nom, ses sources et ses destinations."));

            AddText(ConsoleRows.Section(this, "PROFIL ACTIF"));
            var active = _s.GetActiveProfile();
            var activeRow = ConsoleRows.Row(this, "Profil utilisé pour alimenter la bibliothèque",
                active != null ? active.Name : "(aucun)", ChooseActiveProfile);
            activeRow.IsEnabled = _s.Profiles.Count > 0;
            AddRow(activeRow);

            AddText(ConsoleRows.Section(this, "PROFILS"));
            foreach (var p in _s.Profiles)
            {
                var profile = p;
                bool isActive = profile.Name == _s.ActiveProfileName;
                string label = (isActive ? "●  " : "    ") + (string.IsNullOrWhiteSpace(profile.Name) ? "(sans nom)" : profile.Name);
                AddRow(ConsoleRows.Row(this, label, ProfileSummary(profile), () => OpenEditor(profile)));
            }
            AddRow(ConsoleRows.Row(this, "+   Nouveau profil…", null, CreateProfile));

            if (_s.Profiles.Count == 0)
                AddText(ConsoleRows.Note(this,
                    "Aucun profil pour l'instant : créez-en un, puis ajoutez-y au moins une source et une destination."));
        }

        private static string ProfileSummary(Profile p)
        {
            return Plural(p.GetSources().Count, "source") + " · " + Plural(p.GetDestinations().Count, "destination");
        }

        private void ChooseActiveProfile()
        {
            var choices = new List<MenuChoice>();
            int initial = 0;
            for (int i = 0; i < _s.Profiles.Count; i++)
            {
                var p = _s.Profiles[i];
                if (p.Name == _s.ActiveProfileName) initial = i;
                choices.Add(new MenuChoice(string.IsNullOrWhiteSpace(p.Name) ? "(sans nom)" : p.Name, ProfileSummary(p), p));
            }

            var choice = ConsoleDialogs.Choose(this, "Profil actif", "Profil actif",
                "Le profil actif alimente la bibliothèque Playnite.", choices, initial);
            if (choice == null) return;

            _s.ActiveProfileName = ((Profile)choice.Tag).Name;
            Touch();
            Rebuild();
        }

        private void OpenEditor(Profile profile)
        {
            var dialog = new ProfileEditorDialog(_s, profile, _api, Touch);
            ConsoleDialogs.SetOwner(dialog, this);
            dialog.ShowDialog();
            Rebuild();
        }

        // « Nouveau profil 2 », « Nouveau profil 3 »… : les noms doivent être uniques.
        private string UniqueProfileName()
        {
            const string baseName = "Nouveau profil";
            string name = baseName;
            int i = 2;
            while (ProfileEditorDialog.NameTaken(_s, name, null))
                name = baseName + " " + i++;
            return name;
        }

        private void CreateProfile()
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, this, "Nouveau profil", "Nom du nouveau profil :", UniqueProfileName(), out text))
                return;

            if (text.Length == 0)
            {
                ConsoleDialogs.Info(this, "Nom vide", "Le nom du profil ne peut pas être vide.");
                return;
            }
            if (ProfileEditorDialog.NameTaken(_s, text, null))
            {
                ConsoleDialogs.Info(this, "Nom déjà utilisé", "Un profil s'appelle déjà « " + text + " ».");
                return;
            }

            var profile = new Profile { Name = text };
            _s.Profiles.Add(profile);

            // Premier profil créé (ou profil actif disparu) : il devient le profil actif, comme dans l'écran classique.
            if (_s.GetActiveProfile() == null) _s.ActiveProfileName = text;

            Touch();
            // On enchaîne directement sur l'éditeur : il reste à ajouter au moins une source et une destination.
            OpenEditor(profile);
        }

        // ------------------------------------------------------------------
        // Catégorie 2 — COMPORTEMENT
        // ------------------------------------------------------------------
        private void BuildBehavior()
        {
            AddCheck("Installation directe : aucune fenêtre, tout s'installe automatiquement",
                _s.DirectInstall, v => _s.DirectInstall = v,
                "Un clic sur Installer lance tout : le jeu est installé dans la destination par défaut du profil actif " +
                "(ou celle choisie à l'avance par clic droit), avec le mode et l'exécutable détectés automatiquement, " +
                "puis tous les DLC et patchs reconnus trouvés dans le dossier du jeu. Les fichiers non reconnus ne sont " +
                "jamais lancés. Décochez pour choisir à chaque fois dans des fenêtres (destination, DLC et patchs), " +
                "en mode Bureau comme en mode Plein écran.");

            AddCheck("Nettoyer les noms de jeux (aide Playnite à retrouver jaquettes et descriptions)",
                _s.CleanGameNames, v => _s.CleanGameNames = v,
                "Ex. « Jeu_v1.2.3 [GOG] » s'affiche « Jeu ». Les dossiers ne sont jamais renommés. " +
                "S'applique aux nouveaux jeux détectés.");

            AddText(ConsoleRows.Section(this, "PROGRESSION D'UNE INSTALLATION"));
            AddRow(ConsoleRows.Row(this, "Mise à jour de la progression",
                "toutes les " + _s.ProgressIntervalSeconds + " secondes", ChooseProgressInterval));
            AddText(ConsoleRows.Note(this,
                "Valable pour la fenêtre de progression ET pour les notifications (installation directe), pour le jeu " +
                "comme pour ses DLC et patchs. Petit chiffre = affichage plus vivant ; grand chiffre = plus doux pour " +
                "une machine ou un disque lent."));
        }

        private void ChooseProgressInterval()
        {
            var choices = new List<MenuChoice>();
            for (int n = PluginSettings.MinProgressIntervalSeconds; n <= PluginSettings.MaxProgressIntervalSeconds; n++)
            {
                string detail = null;
                if (n == PluginSettings.MinProgressIntervalSeconds) detail = "Affichage plus vivant";
                else if (n == PluginSettings.MaxProgressIntervalSeconds) detail = "Plus doux pour une machine ou un disque lent";
                choices.Add(new MenuChoice("Toutes les " + n + " secondes", detail, n));
            }

            int initial = Math.Max(0, _s.ProgressIntervalSeconds - PluginSettings.MinProgressIntervalSeconds);
            var choice = ConsoleDialogs.Choose(this, "Comportement", "Mise à jour de la progression",
                "À quel rythme la progression d'une installation est-elle mise à jour ?", choices, initial);
            if (choice == null) return;

            _s.ProgressIntervalSeconds = (int)choice.Tag;
            Touch();
            Rebuild();
        }

        // ------------------------------------------------------------------
        // Catégorie 3 — NOTIFICATIONS
        // ------------------------------------------------------------------
        private void BuildNotifications()
        {
            AddText(ConsoleRows.Note(this,
                "Choisissez les notifications que vous voulez voir. Les erreurs et les blocages (échec d'installation, " +
                "espace insuffisant, exécutable introuvable…) s'affichent toujours : elles ne peuvent pas être désactivées."));

            AddCheck("Nouveaux DLC et patchs", _s.NotifyNewExtras, v => _s.NotifyNewExtras = v,
                "Prévient quand des DLC ou des patchs apparaissent dans le dossier d'un jeu installé. Cliquer sur la " +
                "notification ouvre la fenêtre où vous choisissez ce que vous voulez installer. La vérification se fait " +
                "à chaque mise à jour de la bibliothèque.");

            AddCheck("Source ou destination absente", _s.NotifyMissingPaths, v => _s.NotifyMissingPaths = v,
                "Prévient à la mise à jour de la bibliothèque quand un disque ou un partage réseau n'est pas accessible. " +
                "Décochez si vous débranchez volontairement un disque.");

            AddCheck("Doublons entre sources", _s.NotifyDuplicates, v => _s.NotifyDuplicates = v,
                "Prévient quand le même dossier de jeu existe dans plusieurs sources (la première source est utilisée).");

            AddCheck("Résumés de fin", _s.NotifySummaries, v => _s.NotifySummaries = v,
                "Confirme la fin d'une opération : jeu installé, désinstallé, installation annulée, DLC et patchs terminés.");

            AddCheck("Infos pendant l'installation", _s.NotifyInstallInfo, v => _s.NotifyInstallInfo = v,
                "Donne des précisions pendant l'installation : exécutable choisi automatiquement ou mémorisé, destination " +
                "de secours utilisée, avancement d'une installation directe. L'avancement d'une copie, qui permet de " +
                "l'annuler d'un clic, reste toujours affiché.");

            AddCheck("Confirmations de menu", _s.NotifyMenuConfirmations, v => _s.NotifyMenuConfirmations = v,
                "Confirme vos actions dans les menus : profil activé, destination mémorisée, exécutable réassocié.");
        }

        // ------------------------------------------------------------------
        // Catégorie 4 — TOASTS
        // ------------------------------------------------------------------
        private void BuildToasts()
        {
            AddText(ConsoleRows.Note(this,
                "Un toast est une petite carte qui s'affiche en bas à droite de l'écran, au premier plan, sans jamais " +
                "prendre le focus : elle apparaît même par-dessus un jeu sans le gêner, puis disparaît toute seule après " +
                "quelques secondes. Elle s'ajoute à la notification de Playnite (elle ne la remplace pas) et ne se clique " +
                "pas : pour agir, ouvrez la notification. Un toast n'apparaît que si la notification correspondante est " +
                "cochée (catégorie « Notifications »)."));

            AddText(ConsoleRows.Section(this, "AFFICHER AUSSI EN TOAST"));

            // Un toast s'AJOUTE à une notification : si la notification est décochée, la case « toast » est grisée.
            // Sa valeur n'est PAS modifiée : elle redevient active si on recoche la notification.
            AddCheck("Nouveaux DLC et patchs", _s.ToastNewExtras, v => _s.ToastNewExtras = v, null, _s.NotifyNewExtras);
            AddCheck("Source ou destination absente", _s.ToastMissingPaths, v => _s.ToastMissingPaths = v, null, _s.NotifyMissingPaths);
            AddCheck("Doublons entre sources", _s.ToastDuplicates, v => _s.ToastDuplicates = v, null, _s.NotifyDuplicates);
            AddCheck("Résumés de fin", _s.ToastSummaries, v => _s.ToastSummaries = v, null, _s.NotifySummaries);
            AddCheck("Infos pendant l'installation", _s.ToastInstallInfo, v => _s.ToastInstallInfo = v, null, _s.NotifyInstallInfo);
            AddCheck("Confirmations de menu", _s.ToastMenuConfirmations, v => _s.ToastMenuConfirmations = v, null, _s.NotifyMenuConfirmations);
            AddCheck("Erreurs et blocages", _s.ToastErrors, v => _s.ToastErrors = v,
                "Les erreurs restent toujours en notification : cette case ajoute seulement un toast.");

            AddText(ConsoleRows.Section(this, "AFFICHAGE"));
            AddCheck("Y compris pendant une partie", _s.ToastsDuringGame, v => _s.ToastsDuringGame = v,
                "Cochée : les toasts s'affichent aussi par-dessus un jeu en cours, sans lui prendre le clavier ni la " +
                "manette. Décochée : un toast n'apparaît que si Playnite est au premier plan et qu'aucun jeu n'est en " +
                "cours de lancement ou d'exécution.");

            AddText(ConsoleRows.Section(this, "ESSAI"));
            AddRow(ConsoleRows.Row(this, "Afficher des toasts de test", null, () => ToastHelper.ShowTest(_api, _s)));
            AddText(ConsoleRows.Note(this,
                "Montre trois toasts avec vos réglages actuels (« pendant une partie »), sans tenir " +
                "compte des cases ci-dessus."));
        }

        // ------------------------------------------------------------------
        // Catégorie 5 — DLC ET PATCHS
        // ------------------------------------------------------------------
        private void BuildExtras()
        {
            // Grisée quand « Installation directe » est cochée : sans fenêtre, la question n'a pas de sens.
            AddCheck("Demander confirmation avant d'installer les DLC et patchs",
                _s.ConfirmExtras, v => _s.ConfirmExtras = v,
                "Grisée quand « Installation directe » est cochée (catégorie « Comportement ») : sans fenêtre, les DLC " +
                "et patchs trouvés s'installent toujours automatiquement, après le jeu.",
                !_s.DirectInstall);

            AddText(ConsoleRows.Note(this,
                "Comment ils sont repérés : dans le dossier d'un jeu, un sous-dossier dont le nom commence par DEUX " +
                "UNDERSCORES (__) et contient « DLC » est un dossier de DLC (par exemple « __DLC » ou « __DLC - Pack HD ») ; " +
                "un sous-dossier qui commence par « __ » et contient « patch » (patch, patchs, patches…) ou « update » " +
                "est un dossier de patchs (par exemple « __Patch_01 »). Les deux underscores sont obligatoires et ne se " +
                "désactivent pas : un dossier « DLC » ou « Patch » sans eux est un dossier du jeu comme un autre, et " +
                "n'est jamais touché. Les dossiers d'extras sont ignorés pour trouver l'installeur du jeu. Les " +
                "installeurs reconnus qu'ils contiennent sont lancés en silencieux après le jeu (DLC d'abord, puis " +
                "patchs) ; dès qu'un échoue, tout s'arrête. Un jeu portable est copié en entier, DLC et patchs compris. " +
                "Un sous-dossier de DLC ou de patchs qui ne contient aucun installeur (ni reconnu, ni nommé « setup » ou « install »…) est une copie de fichiers : son contenu est copié dans le dossier du jeu avec la même arborescence, en remplaçant les fichiers du même nom ; rien n'est supprimé et la source n'est jamais modifiée. " +
                "Un fichier d'un type non reconnu (ni Inno Setup, ni NSIS, ni MSI) n'est jamais lancé tout seul : il est " +
                "proposé dans la fenêtre de choix (décoché), après un avertissement, et s'ouvre avec sa propre fenêtre " +
                "d'installation ; à sa fermeture, le plugin vous demande si l'installation s'est bien passée."));

            AddText(ConsoleRows.Note(this,
                "Dans la fenêtre de confirmation, une case par fichier (DLC, puis patchs) permet de choisir ce qui " +
                "s'installe. Sur un jeu déjà installé : clic droit (en mode Plein écran : menu du jeu → Extensions) → " +
                "« Installer les DLC et patchs (ArchiveStore) » ; seuls les extras pas encore installés sont proposés. " +
                "Quand de nouveaux DLC ou patchs apparaissent dans le dossier d'un jeu installé, une notification vous " +
                "le dit (catégorie « Notifications »). Pour un jeu installé avant cette version, le plugin ne sait pas " +
                "quels extras sont déjà installés et les reproposera : cochez-les dans cette fenêtre et choisissez « Déjà " +
                "installés » pour les noter sans rien réinstaller. Détails dans le fichier archivestore.log."));
        }

        // ------------------------------------------------------------------
        // Catégorie 6 — INSTALLEURS NON RECONNUS
        // ------------------------------------------------------------------
        private void BuildUnknownInstallers()
        {
            AddText(ConsoleRows.Note(this,
                "Quand aucun installeur n'est reconnu dans le dossier d'un jeu (ni Inno Setup, ni NSIS, ni MSI), un " +
                "fichier dont le nom commence par « setup » ou « install » rend la situation ambiguë : une fenêtre vous " +
                "demande d'installer, de copier ou d'annuler, en mode Bureau comme en mode Plein écran (même avec " +
                "« Installation directe »). En mode Plein écran, la fenêtre prévient que l'installeur a sa propre " +
                "fenêtre, qui ne se commande pas à la manette : gardez un clavier et une souris à portée de main. Un " +
                "installeur reconnu reste toujours prioritaire : ces mots ne servent qu'à déclencher ce cas ambigu."));

            AddText(ConsoleRows.Section(this, "MOTS D'INSTALLEUR SUPPLÉMENTAIRES"));
            AddText(ConsoleRows.Note(this, "En plus de « setup » et « install »."));
            AddStringList(_s.ExtraInstallerWords, "Ajouter un mot…", "Mot d'installeur", "Mot d'installeur supplémentaire :");

            AddText(ConsoleRows.Section(this, "OÙ CHERCHER CES MOTS"));
            AddCheck("Chercher ces mots n'importe où dans le nom du fichier",
                _s.InstallerWordsAnywhere, v => _s.InstallerWordsAnywhere = v,
                "Décochée (par défaut) : seul le DÉBUT du nom compte (« setup.exe », « install_jeu.exe »). Cochée : le " +
                "mot peut être n'importe où (« jeu_setup.exe »). S'applique à tous les mots : « setup », « install » et " +
                "ceux ajoutés.");

            AddText(ConsoleRows.Warn(this,
                "⚠ Ces mots sont distincts de la liste d'exclusions (catégorie « Exclusions et sauvegardes »), qui CACHE " +
                "des fichiers. Si un jeu portable contient un « setup.exe » qui n'est qu'un outil de réglages et " +
                "déclenche ce cas à tort, ajoutez son nom à la liste d'exclusions : il sera ignoré et le jeu copié sans " +
                "question."));
        }

        // ------------------------------------------------------------------
        // Catégorie 7 — ARGUMENTS D'INSTALLATION SILENCIEUSE
        // ------------------------------------------------------------------
        private void BuildSilent()
        {
            AddText(ConsoleRows.Section(this, "PORTÉE D'INSTALLATION (INNO SETUP)"));
            AddRow(ConsoleRows.Row(this, "Installer pour", DescribeScope(_s.InstallScope), ChooseScope));
            AddText(ConsoleRows.Note(this,
                "« Utilisateur actuel » évite la demande UAC. « Tous les utilisateurs » peut la déclencher. " +
                "« Demander à chaque installation » pose la question avant chaque jeu (sans effet en installation directe : " +
                "l'installeur fait alors ce que son script prévoit). Un installeur sans ce choix ignore ce réglage."));

            AddText(ConsoleRows.Section(this, "ARGUMENTS"));
            AddText(ConsoleRows.Note(this,
                "Arguments donnés aux installeurs reconnus pour qu'ils s'installent sans afficher leur assistant. " +
                "Ils ne peuvent pas être vides."));

            var a = _s.SilentArguments;

            AddRow(ConsoleRows.Row(this, "Inno Setup", a.InnoArgs, () => EditSilent("Inno Setup", a.InnoArgs, v => a.InnoArgs = v)));
            AddText(ConsoleRows.Note(this, "Ex. /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"));

            AddRow(ConsoleRows.Row(this, "NSIS", a.NsisArgs, () => EditSilent("NSIS", a.NsisArgs, v => a.NsisArgs = v)));
            AddText(ConsoleRows.Note(this, "Ex. /S"));

            AddRow(ConsoleRows.Row(this, "MSI", a.MsiArgs, () => EditSilent("MSI", a.MsiArgs, v => a.MsiArgs = v)));
            AddText(ConsoleRows.Note(this, "Ex. /qn /norestart — appliqué via msiexec."));
        }

        private static string DescribeScope(InstallScope scope)
        {
            switch (scope)
            {
                case InstallScope.CurrentUser: return "Toujours utilisateur actuel";
                case InstallScope.AllUsers: return "Toujours tous les utilisateurs";
                default: return "Demander à chaque installation";
            }
        }

        private void ChooseScope()
        {
            var choices = new List<MenuChoice>
            {
                new MenuChoice("Demander à chaque installation", "Une question avant chaque jeu.", InstallScope.Ask),
                new MenuChoice("Toujours utilisateur actuel", "Pas de demande UAC.", InstallScope.CurrentUser),
                new MenuChoice("Toujours tous les utilisateurs", "L'UAC peut s'afficher.", InstallScope.AllUsers)
            };
            int initial = (int)_s.InstallScope;
            var choice = ConsoleDialogs.Choose(this, "Portée d'installation", "Installer pour",
                "Pour les installeurs Inno Setup qui proposent le choix.", choices, initial);
            if (choice == null) return;

            _s.InstallScope = (InstallScope)choice.Tag;
            Touch();
            Rebuild();
        }

        private void EditSilent(string kind, string current, Action<string> setter)
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, this, "Arguments " + kind,
                    "Arguments d'installation silencieuse (" + kind + ") :", current, out text))
                return;

            if (text.Length == 0)
            {
                ConsoleDialogs.Info(this, "Arguments requis",
                    "Sans argument silencieux, l'installeur afficherait son assistant : ils ne peuvent pas être vides.");
                return;
            }

            setter(text);
            Touch();
            Rebuild();
        }

        // ------------------------------------------------------------------
        // Catégorie 8 — EXCLUSIONS ET SAUVEGARDES
        // ------------------------------------------------------------------
        private void BuildLists()
        {
            AddText(ConsoleRows.Section(this, "EXCLUSIONS D'EXÉCUTABLES"));
            AddText(ConsoleRows.Note(this, "Préfixes de noms .exe ignorés (non considérés comme des jeux)."));
            AddStringList(_s.ExcludedExePatterns, "Ajouter un préfixe…", "Préfixe d'exécutable", "Préfixe de nom .exe à ignorer :");

            AddText(ConsoleRows.Section(this, "DOSSIERS DE SAUVEGARDE À CONSERVER"));
            AddText(ConsoleRows.Note(this,
                "Sous-dossiers non supprimés lors d'une désinstallation (vide = tout supprimer)."));
            AddStringList(_s.SaveFolderNames, "Ajouter un dossier…", "Dossier de sauvegarde", "Nom du sous-dossier à conserver :");
        }

        // Liste de textes modifiable : une ligne par élément (A → modifier / supprimer) et une ligne « Ajouter ».
        // La liste est modifiée EN PLACE ; les doublons éventuels sont nettoyés à l'enregistrement (Normalize).
        private void AddStringList(List<string> list, string addLabel, string title, string prompt)
        {
            if (list.Count == 0)
                AddText(ConsoleRows.Note(this, "(liste vide)"));

            for (int i = 0; i < list.Count; i++)
            {
                int index = i;
                AddRow(ConsoleRows.Row(this, list[i], null, () => EditListItem(list, index, title, prompt)));
            }
            AddRow(ConsoleRows.Row(this, "+   " + addLabel, null, () => AddListItem(list, title, prompt)));
        }

        private void AddListItem(List<string> list, string title, string prompt)
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, this, title, prompt, "", out text)) return;
            if (text.Length == 0) return;

            if (list.Any(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase)))
            {
                ConsoleDialogs.Info(this, "Déjà dans la liste", "« " + text + " » est déjà dans la liste.");
                return;
            }

            list.Add(text);
            Touch();
            Rebuild();
        }

        private void EditListItem(List<string> list, int index, string title, string prompt)
        {
            if (index < 0 || index >= list.Count) return;

            var choices = new List<MenuChoice>
            {
                new MenuChoice("Modifier", null, "edit"),
                new MenuChoice("Supprimer de la liste", null, "delete")
            };
            var choice = ConsoleDialogs.Choose(this, title, list[index], null, choices, 0);
            if (choice == null) return;

            if ((string)choice.Tag == "edit")
            {
                string text;
                if (!ConsoleDialogs.AskText(_api, this, title, prompt, list[index], out text)) return;
                if (text.Length == 0) return;
                list[index] = text;
            }
            else
            {
                list.RemoveAt(index);
            }

            Touch();
            Rebuild();
        }

        // ------------------------------------------------------------------
        // Catégorie 9 — JOURNAL
        // ------------------------------------------------------------------
        private void BuildLog()
        {
            AddRow(ConsoleRows.Row(this, "Ouvrir le journal", null, OpenLog));
            AddText(ConsoleRows.Note(this,
                "Ouvre le dossier où se trouve archivestore.log, le journal détaillé de l'extension (à joindre en cas de problème)."));
        }

        private void OpenLog()
        {
            string error;
            if (!ArchiveLog.OpenLogFolder(out error))
                ConsoleDialogs.Info(this, "Journal", error);
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            return _nav.Count > 0 ? (UIElement)_nav[0] : BtnSave;
        }

        // Lignes de la colonne de droite que l'on peut vraiment atteindre (pas les cases grisées).
        private List<UIElement> UsableRows()
        {
            var list = new List<UIElement>();
            foreach (var r in _rows)
                if (r.IsEnabled && r.IsVisible && r.Focusable) list.Add(r);
            return list;
        }

        private void FocusNav(int index)
        {
            if (index < 0 || index >= _nav.Count) return;
            if (index != _cat) ShowCategory(index); // l'aperçu à droite suit le focus
            _nav[index].Focus();
        }

        private void FocusFirstContent()
        {
            var rows = UsableRows();
            if (rows.Count > 0) FocusRow(rows[0]);
        }

        protected override void FocusRow(UIElement row)
        {
            base.FocusRow(row);
            // Première ligne : on remonte tout en haut pour qu'on voie aussi le titre du premier groupe.
            if (_rows.Count > 0 && ReferenceEquals(row, _rows[0])) ContentScroll.ScrollToTop();
        }

        // Trois zones : les catégories (gauche), les réglages (droite), le pied de fenêtre (Annuler / Enregistrer).
        protected override bool OnNavigate(GamepadAction action)
        {
            var cur = CurrentFocus as UIElement;
            var curButton = cur as Button;
            int navIndex = curButton == null ? -1 : _nav.IndexOf(curButton);
            bool inFooter = ReferenceEquals(cur, BtnCancel) || ReferenceEquals(cur, BtnSave);
            var rows = UsableRows();

            switch (action)
            {
                case GamepadAction.Up:
                    if (navIndex >= 0)
                    {
                        if (navIndex > 0) FocusNav(navIndex - 1);
                    }
                    else if (inFooter)
                    {
                        if (rows.Count > 0) FocusRow(rows[rows.Count - 1]);
                        else FocusNav(_cat);
                    }
                    else
                    {
                        int i = rows.IndexOf(cur);
                        if (i > 0) FocusRow(rows[i - 1]);
                        else if (i < 0 && rows.Count > 0) FocusRow(rows[0]);
                    }
                    return true;

                case GamepadAction.Down:
                    if (navIndex >= 0)
                    {
                        if (navIndex < _nav.Count - 1) FocusNav(navIndex + 1);
                    }
                    else if (!inFooter)
                    {
                        int i = rows.IndexOf(cur);
                        if (i < 0) { if (rows.Count > 0) FocusRow(rows[0]); }
                        else if (i < rows.Count - 1) FocusRow(rows[i + 1]);
                        else BtnSave.Focus(); // après la dernière ligne : bouton « Enregistrer »
                    }
                    return true;

                case GamepadAction.Left:
                    if (navIndex >= 0) return true;
                    if (ReferenceEquals(cur, BtnSave)) BtnCancel.Focus();
                    else FocusNav(_cat); // revient aux catégories
                    return true;

                case GamepadAction.Right:
                    if (navIndex >= 0) FocusFirstContent();
                    else if (ReferenceEquals(cur, BtnCancel)) BtnSave.Focus();
                    return true;
            }
            return false;
        }

        // A : coche la case, ou clique la ligne / le bouton qui a le focus.
        protected override void OnConfirm()
        {
            var cur = CurrentFocus as UIElement;

            var box = cur as CheckBox;
            if (box != null)
            {
                box.IsChecked = !(box.IsChecked == true);
                return;
            }

            var button = cur as Button;
            int navIndex = button == null ? -1 : _nav.IndexOf(button);
            if (navIndex >= 0)
            {
                if (navIndex != _cat) ShowCategory(navIndex);
                FocusFirstContent();
                return;
            }

            ClickFocusedButton();
        }

        protected override void OnActionY()
        {
            Save();
        }

        protected override void OnCancelPressed()
        {
            TryCancel();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            TryCancel();
        }

        private void BtnSave_Click(object sender, RoutedEventArgs e)
        {
            Save();
        }

        // Annuler : s'il y a des modifications, on demande confirmation (le bouton « Non » a le focus).
        private void TryCancel()
        {
            if (_dirty && !ConsoleDialogs.Ask(this, "Abandonner les modifications ?",
                    "Les réglages modifiés dans cette fenêtre ne seront pas enregistrés.",
                    "Oui, abandonner", "Non, continuer"))
                return;

            CloseDialog(false);
        }

        // Enregistrer : on vérifie d'abord les réglages (VerifySettings). En cas d'erreur, la fenêtre reste ouverte.
        private void Save()
        {
            List<string> errors;
            if (!_s.VerifySettings(out errors))
            {
                const int maxShown = 8;
                string text = string.Join("\n", errors.Take(maxShown).Select(x => "• " + x));
                if (errors.Count > maxShown)
                    text += "\n… et " + (errors.Count - maxShown) + " autre(s)";

                ConsoleDialogs.Info(this, "Réglages non enregistrés", text);
                return;
            }

            CloseDialog(true); // le plugin appelle alors EndEdit() : nettoyage et sauvegarde sur le disque
        }
    }
}
