using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // ArgentSettings — « Compatibles Argent » : les réglages d'ArchiveStore DANS la fenêtre Personnalisation du thème Argent.
    //
    // Avec le thème Argent (et Argent Core), l'entrée « ArchiveStore — Réglages » du menu Extensions disparaît : les mêmes
    // réglages, rangés dans les mêmes 9 catégories que la fenêtre manette (ConsoleSettingsWindow), sont dans
    // Personnalisation → Compatibles Argent → ArchiveStore, au style d'Argent. Sans Argent (mode Bureau, autre thème,
    // Playnite de base), rien ne change : la fenêtre de réglages d'ArchiveStore reste là.
    //
    // ENREGISTREMENT : chaque changement est enregistré TOUT DE SUITE (comme les autres réglages d'Argent), avec le même
    // nettoyage que la fenêtre classique (EndEdit : Normalize, profil actif par défaut, sauvegarde).
    // PROFILS : l'éditeur de profil habituel s'ouvre (sources, destinations, dossiers). À sa fermeture, les réglages sont
    // vérifiés (VerifySettings, comme « Enregistrer » de la fenêtre classique) : s'il reste une erreur (source imbriquée
    // dans une destination, profil sans destination…), on propose de corriger ou d'abandonner les modifications de ce
    // profil. Rien d'invalide n'est donc jamais enregistré.
    // Textes, listes, choix d'un profil : les petites fenêtres habituelles (clavier, menu de choix) au style d'Argent.
    // Thread de l'interface.
    // ======================================================================
    internal sealed class ArgentSettings
    {
        private readonly PluginSettings _s;
        private readonly IPlayniteAPI _api;
        private readonly string _id;

        public ArgentSettings(PluginSettings settings, IPlayniteAPI api, string id)
        {
            _s = settings;
            _api = api;
            _id = id;
        }

        /// <summary>Décrit les réglages pour Argent (sans effet tant qu'Argent ne les affiche pas).</summary>
        public void Register()
        {
            ArgentBridge.Register(_id, "ArchiveStore", "ArchiveStore",
                "Installe les jeux de tes archives (disques, partages réseau) dans Playnite : profils, installation, notifications.",
                BuildRoot);
        }

        // ------------------------------------------------------------------
        // Première page : les 9 catégories (les mêmes que la fenêtre manette)
        // ------------------------------------------------------------------
        private IList<object> BuildRoot()
        {
            Ensure();
            return new List<object>
            {
                ArgentBridge.Page("Profils", "Profils",
                    "Un profil regroupe une ou plusieurs SOURCES (dossiers d'archives) et une ou plusieurs DESTINATIONS "
                    + "(dossiers d'installation).", BuildProfiles)
                    .WithValue(() => { var p = _s.GetActiveProfile(); return p != null ? NameOf(p) : "Aucun"; }),
                ArgentBridge.Page("Comportement", "Comportement", null, BuildBehavior),
                ArgentBridge.Page("Notifications", "Notifications",
                    "Choisissez les notifications que vous voulez voir. Les erreurs et les blocages (échec d'installation, "
                    + "espace insuffisant, exécutable introuvable…) s'affichent toujours : elles ne peuvent pas être désactivées.",
                    BuildNotifications),
                ArgentBridge.Page("Toasts", "Toasts", null, BuildToasts),
                ArgentBridge.Page("DLC et patchs", "DLC et patchs", null, BuildExtras),
                ArgentBridge.Page("Installeurs non reconnus", "Installeurs non reconnus (cas ambigu)", null, BuildUnknownInstallers),
                ArgentBridge.Page("Installation silencieuse", "Arguments d'installation silencieuse", null, BuildSilent),
                ArgentBridge.Page("Exclusions et sauvegardes", "Exclusions et sauvegardes", null, BuildLists),
                ArgentBridge.Page("Journal", "Journal", null, BuildLog)
            };
        }

        // ------------------------------------------------------------------
        // Profils
        // ------------------------------------------------------------------
        private IList<object> BuildProfiles()
        {
            Ensure();
            var rows = new List<object>
            {
                ArgentBridge.Section("PROFIL ACTIF"),
                ArgentBridge.Choice("Profil utilisé pour alimenter la bibliothèque", "Profil actif",
                    "Le profil actif alimente la bibliothèque Playnite.",
                    () => _s.Profiles.Select(p => NameOf(p)).ToList(),
                    () => _s.Profiles.FindIndex(p => p.Name == _s.ActiveProfileName),
                    i =>
                    {
                        if (i < 0 || i >= _s.Profiles.Count) return;
                        _s.ActiveProfileName = _s.Profiles[i].Name;
                        Commit("profil actif « " + _s.ActiveProfileName + " »");
                    })
                    .WithEnabled(() => _s.Profiles.Count > 0),
                ArgentBridge.Section("PROFILS")
            };

            foreach (var p in _s.Profiles)
            {
                var profile = p;
                bool isActive = profile.Name == _s.ActiveProfileName;
                rows.Add(ArgentBridge.Window((isActive ? "●  " : "") + NameOf(profile), () => EditProfile(profile))
                    .WithValue(() => Summary(profile)));
            }
            rows.Add(ArgentBridge.Window("+   Nouveau profil…", CreateProfile));

            rows.Add(ArgentBridge.Note(_s.Profiles.Count == 0
                ? "Aucun profil pour l'instant : créez-en un, puis ajoutez-y au moins une source et une destination."
                : "Choisissez un profil pour modifier son nom, ses sources et ses destinations."));
            return rows;
        }

        private static string NameOf(Profile p)
        {
            return string.IsNullOrWhiteSpace(p.Name) ? "(sans nom)" : p.Name;
        }

        private static string Summary(Profile p)
        {
            return Plural(p.GetSources().Count, "source") + " · " + Plural(p.GetDestinations().Count, "destination");
        }

        private static string Plural(int n, string singular)
        {
            return n + " " + singular + (n > 1 ? "s" : "");
        }

        private void EditProfile(Profile profile)
        {
            _s.BeginEdit();      // copie de sécurité : « Abandonner » revient exactement à l'état d'avant
            EditLoop(profile);
        }

        private void CreateProfile()
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, null, "Nouveau profil", "Nom du nouveau profil :", UniqueProfileName(), out text))
                return;
            if (text.Length == 0)
            {
                ConsoleDialogs.Info(null, "Nom vide", "Le nom du profil ne peut pas être vide.");
                return;
            }
            if (ProfileEditorDialog.NameTaken(_s, text, null))
            {
                ConsoleDialogs.Info(null, "Nom déjà utilisé", "Un profil s'appelle déjà « " + text + " ».");
                return;
            }

            _s.BeginEdit();
            var profile = new Profile { Name = text };
            _s.Profiles.Add(profile);
            if (_s.GetActiveProfile() == null) _s.ActiveProfileName = text;     // premier profil : il devient le profil actif
            EditLoop(profile);   // il reste à ajouter au moins une source et une destination
        }

        // L'éditeur s'ouvre ; à sa fermeture, on vérifie. Erreur : corriger (l'éditeur se rouvre) ou abandonner (CancelEdit).
        private void EditLoop(Profile profile)
        {
            bool finished = false;
            try
            {
                while (true)
                {
                    var dialog = new ProfileEditorDialog(_s, profile, _api, null);
                    ConsoleDialogs.SetOwner(dialog, OwnerWindow());
                    dialog.ShowDialog();

                    List<string> errors;
                    if (_s.VerifySettings(out errors))
                    {
                        _s.EndEdit();
                        finished = true;
                        ArchiveLog.Info("Réglages enregistrés (Argent) : profil « " + NameOf(profile) + " »"
                            + (dialog.Deleted ? " supprimé." : "."));
                        return;
                    }

                    const int maxShown = 6;
                    string text = string.Join("\n", errors.Take(maxShown).Select(x => "• " + x));
                    if (errors.Count > maxShown) text += "\n… et " + (errors.Count - maxShown) + " autre(s)";

                    bool canFix = _s.Profiles.Contains(profile);
                    if (!canFix)
                    {
                        ConsoleDialogs.Info(null, "Modifications abandonnées",
                            text + "\n\nLes réglages ne peuvent pas être enregistrés en l'état : rien n'a été modifié.");
                        _s.CancelEdit();
                        finished = true;
                        return;
                    }

                    if (!ConsoleDialogs.Ask(null, "Profil à corriger",
                            text + "\n\nCorriger maintenant ? Sinon, les modifications de ce profil sont abandonnées.",
                            "Corriger", "Abandonner"))
                    {
                        _s.CancelEdit();
                        finished = true;
                        ArchiveLog.Info("Modifications du profil « " + NameOf(profile) + " » abandonnées (Argent).");
                        return;
                    }
                }
            }
            finally
            {
                if (!finished)
                {
                    try { _s.CancelEdit(); } catch (Exception) { }
                }
            }
        }

        private string UniqueProfileName()
        {
            const string baseName = "Nouveau profil";
            string name = baseName;
            int i = 2;
            while (ProfileEditorDialog.NameTaken(_s, name, null))
                name = baseName + " " + i++;
            return name;
        }

        // ------------------------------------------------------------------
        // Comportement
        // ------------------------------------------------------------------
        private IList<object> BuildBehavior()
        {
            var rows = new List<object>
            {
                ArgentBridge.Switch("Installation directe : aucune fenêtre, tout s'installe automatiquement",
                    () => _s.DirectInstall, v => Set("installation directe", () => _s.DirectInstall = v)),
                ArgentBridge.Note("Un clic sur Installer lance tout : le jeu est installé dans la destination par défaut du profil actif "
                    + "(ou celle choisie à l'avance par clic droit), avec le mode et l'exécutable détectés automatiquement, puis tous "
                    + "les DLC et patchs reconnus trouvés dans le dossier du jeu. Les fichiers non reconnus ne sont jamais lancés. "
                    + "Désactivé : vous choisissez à chaque fois dans des fenêtres (destination, DLC et patchs)."),
                ArgentBridge.Switch("Nettoyer les noms de jeux (aide Playnite à retrouver jaquettes et descriptions)",
                    () => _s.CleanGameNames, v => Set("nettoyage des noms", () => _s.CleanGameNames = v)),
                ArgentBridge.Note("Ex. « Jeu_v1.2.3 [GOG] » s'affiche « Jeu ». Les dossiers ne sont jamais renommés. "
                    + "S'applique aux nouveaux jeux détectés."),
                ArgentBridge.Section("PROGRESSION D'UNE INSTALLATION")
            };

            var seconds = new List<int>();
            for (int n = PluginSettings.MinProgressIntervalSeconds; n <= PluginSettings.MaxProgressIntervalSeconds; n++) seconds.Add(n);
            rows.Add(ArgentBridge.Choice("Mise à jour de la progression", "Mise à jour de la progression",
                "À quel rythme la progression d'une installation est-elle mise à jour ?",
                () => seconds.Select(n => "Toutes les " + n + " secondes").ToList(),
                () => seconds.IndexOf(_s.ProgressIntervalSeconds),
                i => { if (i >= 0 && i < seconds.Count) Set("rythme de la progression", () => _s.ProgressIntervalSeconds = seconds[i]); }));
            rows.Add(ArgentBridge.Note("Valable pour la fenêtre de progression ET pour les notifications (installation directe), pour le jeu "
                + "comme pour ses DLC et patchs. Petit chiffre = affichage plus vivant ; grand chiffre = plus doux pour une machine "
                + "ou un disque lent."));
            return rows;
        }

        // ------------------------------------------------------------------
        // Notifications
        // ------------------------------------------------------------------
        private IList<object> BuildNotifications()
        {
            return new List<object>
            {
                ArgentBridge.Switch("Nouveaux DLC et patchs", () => _s.NotifyNewExtras, v => Set("notification DLC et patchs", () => _s.NotifyNewExtras = v)),
                ArgentBridge.Note("Prévient quand des DLC ou des patchs apparaissent dans le dossier d'un jeu installé. Cliquer sur la "
                    + "notification ouvre la fenêtre où vous choisissez ce que vous voulez installer. La vérification se fait à chaque "
                    + "mise à jour de la bibliothèque."),
                ArgentBridge.Switch("Source ou destination absente", () => _s.NotifyMissingPaths, v => Set("notification chemins absents", () => _s.NotifyMissingPaths = v)),
                ArgentBridge.Note("Prévient à la mise à jour de la bibliothèque quand un disque ou un partage réseau n'est pas accessible. "
                    + "Désactivez si vous débranchez volontairement un disque."),
                ArgentBridge.Switch("Doublons entre sources", () => _s.NotifyDuplicates, v => Set("notification doublons", () => _s.NotifyDuplicates = v)),
                ArgentBridge.Note("Prévient quand le même dossier de jeu existe dans plusieurs sources (la première source est utilisée)."),
                ArgentBridge.Switch("Résumés de fin", () => _s.NotifySummaries, v => Set("notification résumés", () => _s.NotifySummaries = v)),
                ArgentBridge.Note("Confirme la fin d'une opération : jeu installé, désinstallé, installation annulée, DLC et patchs terminés."),
                ArgentBridge.Switch("Infos pendant l'installation", () => _s.NotifyInstallInfo, v => Set("notification infos d'installation", () => _s.NotifyInstallInfo = v)),
                ArgentBridge.Note("Donne des précisions pendant l'installation : exécutable choisi automatiquement ou mémorisé, destination "
                    + "de secours utilisée, avancement d'une installation directe. L'avancement d'une copie, qui permet de l'annuler, "
                    + "reste toujours affiché."),
                ArgentBridge.Switch("Confirmations de menu", () => _s.NotifyMenuConfirmations, v => Set("notification confirmations", () => _s.NotifyMenuConfirmations = v)),
                ArgentBridge.Note("Confirme vos actions dans les menus : profil activé, destination mémorisée, exécutable réassocié.")
            };
        }

        // ------------------------------------------------------------------
        // Toasts
        // ------------------------------------------------------------------
        private IList<object> BuildToasts()
        {
            return new List<object>
            {
                ArgentBridge.Note("Un toast est une petite carte qui s'affiche en bas à droite de l'écran, au premier plan, sans jamais "
                    + "prendre le focus : elle apparaît même par-dessus un jeu sans le gêner, puis disparaît toute seule. Elle s'ajoute "
                    + "à la notification de Playnite (elle ne la remplace pas). Un toast n'apparaît que si la notification "
                    + "correspondante est activée (catégorie « Notifications »)."),
                ArgentBridge.Section("AFFICHER AUSSI EN TOAST"),
                ArgentBridge.Switch("Nouveaux DLC et patchs", () => _s.ToastNewExtras, v => Set("toast DLC et patchs", () => _s.ToastNewExtras = v))
                    .WithEnabled(() => _s.NotifyNewExtras),
                ArgentBridge.Switch("Source ou destination absente", () => _s.ToastMissingPaths, v => Set("toast chemins absents", () => _s.ToastMissingPaths = v))
                    .WithEnabled(() => _s.NotifyMissingPaths),
                ArgentBridge.Switch("Doublons entre sources", () => _s.ToastDuplicates, v => Set("toast doublons", () => _s.ToastDuplicates = v))
                    .WithEnabled(() => _s.NotifyDuplicates),
                ArgentBridge.Switch("Résumés de fin", () => _s.ToastSummaries, v => Set("toast résumés", () => _s.ToastSummaries = v))
                    .WithEnabled(() => _s.NotifySummaries),
                ArgentBridge.Switch("Infos pendant l'installation", () => _s.ToastInstallInfo, v => Set("toast infos d'installation", () => _s.ToastInstallInfo = v))
                    .WithEnabled(() => _s.NotifyInstallInfo),
                ArgentBridge.Switch("Confirmations de menu", () => _s.ToastMenuConfirmations, v => Set("toast confirmations", () => _s.ToastMenuConfirmations = v))
                    .WithEnabled(() => _s.NotifyMenuConfirmations),
                ArgentBridge.Switch("Erreurs et blocages", () => _s.ToastErrors, v => Set("toast erreurs", () => _s.ToastErrors = v)),
                ArgentBridge.Note("Les lignes grisées suivent leur notification : activez-la d'abord (catégorie « Notifications »). "
                    + "Les erreurs restent toujours en notification : cette ligne ajoute seulement un toast."),

                ArgentBridge.Section("AFFICHAGE"),
                ArgentBridge.Switch("Y compris pendant une partie", () => _s.ToastsDuringGame, v => Set("toasts pendant une partie", () => _s.ToastsDuringGame = v)),
                ArgentBridge.Note("Activé : les toasts s'affichent aussi par-dessus un jeu en cours, sans lui prendre le clavier ni la manette. "
                    + "Désactivé : un toast n'apparaît que si Playnite est au premier plan et qu'aucun jeu n'est en cours."),
                ArgentBridge.Note("Plusieurs toasts à la fois s'empilent (le plus ancien en haut), avec ceux des autres plugins Argent. "
                    + "Durée et nombre : Personnalisation d'Argent > Notifications (toasts)."),

                ArgentBridge.Section("ESSAI"),
                ArgentBridge.Action("Afficher des toasts de test", () => ToastHelper.ShowTest(_api, _s)),
                ArgentBridge.Note("Montre trois toasts avec vos réglages actuels (« pendant une partie »), sans tenir "
                    + "compte des lignes ci-dessus.")
            };
        }

        // ------------------------------------------------------------------
        // DLC et patchs
        // ------------------------------------------------------------------
        private IList<object> BuildExtras()
        {
            return new List<object>
            {
                ArgentBridge.Switch("Demander confirmation avant d'installer les DLC et patchs",
                    () => _s.ConfirmExtras, v => Set("confirmation des DLC et patchs", () => _s.ConfirmExtras = v))
                    .WithEnabled(() => !_s.DirectInstall),
                ArgentBridge.Note("Grisée quand « Installation directe » est activée (catégorie « Comportement ») : sans fenêtre, les DLC "
                    + "et patchs trouvés s'installent toujours automatiquement, après le jeu."),
                ArgentBridge.Note("Comment ils sont repérés : dans le dossier d'un jeu, un sous-dossier dont le nom commence par DEUX "
                    + "UNDERSCORES (__) et contient « DLC » est un dossier de DLC (par exemple « __DLC » ou « __DLC - Pack HD ») ; "
                    + "un sous-dossier qui commence par « __ » et contient « patch » (patch, patchs, patches…) ou « update » est un "
                    + "dossier de patchs (par exemple « __Patch_01 »). Les deux underscores sont obligatoires : un dossier « DLC » ou "
                    + "« Patch » sans eux est un dossier du jeu comme un autre, et n'est jamais touché. Les installeurs reconnus qu'ils "
                    + "contiennent sont lancés en silencieux après le jeu (DLC d'abord, puis patchs) ; dès qu'un échoue, tout s'arrête. "
                    + "Un jeu portable est copié en entier, DLC et patchs compris. Un sous-dossier sans installeur est une copie de "
                    + "fichiers : son contenu est copié dans le dossier du jeu (même arborescence, fichiers du même nom remplacés ; rien "
                    + "n'est supprimé et la source n'est jamais modifiée). Un fichier d'un type non reconnu (ni Inno Setup, ni NSIS, ni "
                    + "MSI) n'est jamais lancé tout seul : il est proposé dans la fenêtre de choix (décoché), après un avertissement."),
                ArgentBridge.Note("Sur un jeu déjà installé : menu du jeu → Extensions → « Installer les DLC et patchs (ArchiveStore) » ; "
                    + "seuls les extras pas encore installés sont proposés. Pour un jeu installé avant cette version, cochez les extras "
                    + "déjà présents et choisissez « Déjà installés » pour les noter sans rien réinstaller.")
            };
        }

        // ------------------------------------------------------------------
        // Installeurs non reconnus
        // ------------------------------------------------------------------
        private IList<object> BuildUnknownInstallers()
        {
            Ensure();
            var rows = new List<object>
            {
                ArgentBridge.Note("Quand aucun installeur n'est reconnu dans le dossier d'un jeu (ni Inno Setup, ni NSIS, ni MSI), un "
                    + "fichier dont le nom commence par « setup » ou « install » rend la situation ambiguë : une fenêtre vous demande "
                    + "d'installer, de copier ou d'annuler (même avec « Installation directe »). En mode Plein écran, l'installeur a sa "
                    + "propre fenêtre, qui ne se commande pas à la manette : gardez un clavier et une souris à portée de main."),
                ArgentBridge.Section("MOTS D'INSTALLEUR SUPPLÉMENTAIRES"),
                ArgentBridge.Note("En plus de « setup » et « install »."),
            };
            AddStringList(rows, _s.ExtraInstallerWords, "Ajouter un mot…", "Mot d'installeur", "Mot d'installeur supplémentaire :");
            rows.Add(ArgentBridge.Section("OÙ CHERCHER CES MOTS"));
            rows.Add(ArgentBridge.Switch("Chercher ces mots n'importe où dans le nom du fichier",
                () => _s.InstallerWordsAnywhere, v => Set("mots d'installeur partout", () => _s.InstallerWordsAnywhere = v)));
            rows.Add(ArgentBridge.Note("Désactivé (par défaut) : seul le DÉBUT du nom compte (« setup.exe », « install_jeu.exe »). Activé : "
                + "le mot peut être n'importe où (« jeu_setup.exe »). S'applique à tous les mots : « setup », « install » et ceux ajoutés."));
            rows.Add(ArgentBridge.Warn("⚠ Ces mots sont distincts de la liste d'exclusions (catégorie « Exclusions et sauvegardes »), qui "
                + "CACHE des fichiers. Si un jeu portable contient un « setup.exe » qui n'est qu'un outil de réglages et déclenche ce cas à "
                + "tort, ajoutez son nom à la liste d'exclusions : il sera ignoré et le jeu copié sans question."));
            return rows;
        }

        // ------------------------------------------------------------------
        // Installation silencieuse
        // ------------------------------------------------------------------
        private IList<object> BuildSilent()
        {
            Ensure();
            var scopes = new List<InstallScope> { InstallScope.Ask, InstallScope.CurrentUser, InstallScope.AllUsers };
            return new List<object>
            {
                ArgentBridge.Section("PORTÉE D'INSTALLATION (INNO SETUP)"),
                ArgentBridge.Choice("Installer pour", "Installer pour", "Pour les installeurs Inno Setup qui proposent le choix.",
                    () => new List<string> { "Demander à chaque installation", "Toujours utilisateur actuel", "Toujours tous les utilisateurs" },
                    () => scopes.IndexOf(_s.InstallScope),
                    i => { if (i >= 0 && i < scopes.Count) Set("portée d'installation", () => _s.InstallScope = scopes[i]); }),
                ArgentBridge.Note("« Utilisateur actuel » évite la demande UAC. « Tous les utilisateurs » peut la déclencher. « Demander à "
                    + "chaque installation » pose la question avant chaque jeu (sans effet en installation directe : l'installeur fait "
                    + "alors ce que son script prévoit). Un installeur sans ce choix ignore ce réglage."),

                ArgentBridge.Section("ARGUMENTS"),
                ArgentBridge.Note("Arguments donnés aux installeurs reconnus pour qu'ils s'installent sans afficher leur assistant. "
                    + "Ils ne peuvent pas être vides."),
                ArgentBridge.Window("Inno Setup", () => EditSilent("Inno Setup", _s.SilentArguments.InnoArgs, v => _s.SilentArguments.InnoArgs = v))
                    .WithValue(() => _s.SilentArguments.InnoArgs),
                ArgentBridge.Note("Ex. /VERYSILENT /SUPPRESSMSGBOXES /NORESTART"),
                ArgentBridge.Window("NSIS", () => EditSilent("NSIS", _s.SilentArguments.NsisArgs, v => _s.SilentArguments.NsisArgs = v))
                    .WithValue(() => _s.SilentArguments.NsisArgs),
                ArgentBridge.Note("Ex. /S"),
                ArgentBridge.Window("MSI", () => EditSilent("MSI", _s.SilentArguments.MsiArgs, v => _s.SilentArguments.MsiArgs = v))
                    .WithValue(() => _s.SilentArguments.MsiArgs),
                ArgentBridge.Note("Ex. /qn /norestart — appliqué via msiexec.")
            };
        }

        private void EditSilent(string kind, string current, Action<string> setter)
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, null, "Arguments " + kind,
                    "Arguments d'installation silencieuse (" + kind + ") :", current, out text))
                return;

            if (text.Length == 0)
            {
                ConsoleDialogs.Info(null, "Arguments requis",
                    "Sans argument silencieux, l'installeur afficherait son assistant : ils ne peuvent pas être vides.");
                return;
            }

            setter(text);
            Commit("arguments " + kind);
        }

        // ------------------------------------------------------------------
        // Exclusions et sauvegardes
        // ------------------------------------------------------------------
        private IList<object> BuildLists()
        {
            Ensure();
            var rows = new List<object>
            {
                ArgentBridge.Section("EXCLUSIONS D'EXÉCUTABLES"),
                ArgentBridge.Note("Préfixes de noms .exe ignorés (non considérés comme des jeux).")
            };
            AddStringList(rows, _s.ExcludedExePatterns, "Ajouter un préfixe…", "Préfixe d'exécutable", "Préfixe de nom .exe à ignorer :");
            rows.Add(ArgentBridge.Section("DOSSIERS DE SAUVEGARDE À CONSERVER"));
            rows.Add(ArgentBridge.Note("Sous-dossiers non supprimés lors d'une désinstallation (vide = tout supprimer)."));
            AddStringList(rows, _s.SaveFolderNames, "Ajouter un dossier…", "Dossier de sauvegarde", "Nom du sous-dossier à conserver :");
            return rows;
        }

        // Liste de textes : une ligne par élément (A → modifier / supprimer) et une ligne « Ajouter ».
        private void AddStringList(List<object> rows, List<string> list, string addLabel, string title, string prompt)
        {
            if (list.Count == 0) rows.Add(ArgentBridge.Note("(liste vide)"));
            for (int i = 0; i < list.Count; i++)
            {
                int index = i;
                rows.Add(ArgentBridge.Window(list[i], () => EditListItem(list, index, title, prompt)));
            }
            rows.Add(ArgentBridge.Window("+   " + addLabel, () => AddListItem(list, title, prompt)));
        }

        private void AddListItem(List<string> list, string title, string prompt)
        {
            string text;
            if (!ConsoleDialogs.AskText(_api, null, title, prompt, "", out text)) return;
            if (text.Length == 0) return;

            if (list.Any(x => string.Equals(x, text, StringComparison.OrdinalIgnoreCase)))
            {
                ConsoleDialogs.Info(null, "Déjà dans la liste", "« " + text + " » est déjà dans la liste.");
                return;
            }

            list.Add(text);
            Commit(title.ToLowerInvariant() + " ajouté : « " + text + " »");
        }

        private void EditListItem(List<string> list, int index, string title, string prompt)
        {
            if (index < 0 || index >= list.Count) return;

            var choices = new List<MenuChoice>
            {
                new MenuChoice("Modifier", null, "edit"),
                new MenuChoice("Supprimer de la liste", null, "delete")
            };
            var choice = ConsoleDialogs.Choose(null, title, list[index], null, choices, 0);
            if (choice == null) return;

            if ((string)choice.Tag == "edit")
            {
                string text;
                if (!ConsoleDialogs.AskText(_api, null, title, prompt, list[index], out text)) return;
                if (text.Length == 0) return;
                list[index] = text;
                Commit(title.ToLowerInvariant() + " modifié : « " + text + " »");
            }
            else
            {
                string removed = list[index];
                list.RemoveAt(index);
                Commit(title.ToLowerInvariant() + " retiré : « " + removed + " »");
            }
        }

        // ------------------------------------------------------------------
        // Journal
        // ------------------------------------------------------------------
        private IList<object> BuildLog()
        {
            return new List<object>
            {
                ArgentBridge.Action("Ouvrir le dossier du journal", OpenLog),
                ArgentBridge.Note("Ouvre le dossier où se trouve archivestore.log, le journal détaillé de l'extension (à joindre en cas "
                    + "de problème).")
            };
        }

        private void OpenLog()
        {
            string error;
            if (ArchiveLog.OpenLogFolder(out error)) return;
            ArchiveLog.Warn("Journal : " + error);
            try
            {
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_OpenLog", "ArchiveStore : " + error, NotificationType.Error));
            }
            catch (Exception) { /* rien de plus à faire : l'erreur est dans le journal */ }
        }

        // ------------------------------------------------------------------
        // Outils
        // ------------------------------------------------------------------

        // Un réglage simple change : on l'applique, puis on enregistre.
        private void Set(string what, Action change)
        {
            change();
            Commit(what);
        }

        // Enregistrement immédiat, avec le même nettoyage que « Enregistrer » de la fenêtre classique (EndEdit).
        private void Commit(string what)
        {
            _s.EndEdit();
            ArchiveLog.Info("Réglage enregistré (Argent) : " + what + ".");
        }

        // Réglages très anciens : listes absentes (null) réparées, comme le fait la fenêtre classique.
        private void Ensure()
        {
            if (_s.Profiles == null) _s.Profiles = new List<Profile>();
            foreach (var p in _s.Profiles) p.MigrateLegacyFields();
            if (_s.SilentArguments == null) _s.SilentArguments = new SilentArgs();
            if (_s.ExcludedExePatterns == null) _s.ExcludedExePatterns = new List<string>();
            if (_s.SaveFolderNames == null) _s.SaveFolderNames = new List<string>();
            if (_s.ExtraInstallerWords == null) _s.ExtraInstallerWords = new List<string>();
        }

        private Window OwnerWindow()
        {
            try { return _api.Dialogs.GetCurrentAppWindow(); }
            catch (Exception) { return null; }
        }
    }
}
