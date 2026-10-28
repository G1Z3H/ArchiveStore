using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Playnite.SDK;

namespace ArchiveStore
{
    // ======================================================================
    // DestinationEntry — UNE destination d'installation d'un profil.
    //
    // Un profil peut avoir PLUSIEURS destinations (ex. « Disque rapide » = D:\Jeux,
    // « Vieux jeux » = C:\Jeux anciens, certains vieux jeux exigeant le disque Windows).
    // Le nom est facultatif : il sert seulement à s'y retrouver dans les listes.
    // INotifyPropertyChanged : la grille des réglages se met à jour toute seule.
    // ======================================================================
    [Serializable]
    public class DestinationEntry : INotifyPropertyChanged
    {
        private string _name = "";
        private string _folderPath = "";

        /// <summary>Nom facultatif (ex. « Vieux jeux »).</summary>
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        /// <summary>Dossier local où les jeux sont installés (un sous-dossier par jeu).</summary>
        public string FolderPath
        {
            get => _folderPath;
            set { _folderPath = value; OnPropertyChanged(nameof(FolderPath)); }
        }

        /// <summary>« Nom (chemin) » si un nom existe, sinon le chemin seul. Sert aux listes déroulantes.</summary>
        public string GetLabel()
        {
            string path = (FolderPath ?? "").Trim();
            string name = (Name ?? "").Trim();
            return name.Length > 0 ? name + " (" + path + ")" : path;
        }

        public DestinationEntry Copy() => new DestinationEntry { Name = Name, FolderPath = FolderPath };

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ======================================================================
    // Profile — un ensemble NOMMÉ de SOURCES (dossiers d'archives) et de DESTINATIONS.
    // INotifyPropertyChanged permet aux contrôles de la fenêtre de settings
    // de se mettre à jour automatiquement quand on modifie une propriété
    // (mécanisme de "data binding" WPF).
    //
    // PÉDAGOGIE — compatibilité avec les anciens réglages :
    // Avant, un profil n'avait qu'UNE source (SourcePath) et UNE destination
    // (DestinationPath). Ces deux champs EXISTENT TOUJOURS : ils recopient le PREMIER élément
    // des nouvelles listes (SyncLegacyFields). Ainsi :
    //   • un ancien fichier de réglages est lu sans rien perdre (la liste vide est remplie
    //     à partir de l'ancien champ — voir MigrateLegacyFields) ;
    //   • un retour à une ancienne version du plugin retrouve encore ses chemins.
    // Les méthodes GetSources()/GetDestinations() sont des MÉTHODES (et non des propriétés) pour
    // ne pas être écrites dans le fichier JSON.
    // ======================================================================
    [Serializable]
    public class Profile : INotifyPropertyChanged
    {
        private string _name = "";
        private string _sourcePath = "";
        private string _destinationPath = "";

        /// <summary>Nom affiché dans le sélecteur de profil.</summary>
        public string Name
        {
            get => _name;
            set { _name = value; OnPropertyChanged(nameof(Name)); }
        }

        /// <summary>
        /// ANCIEN champ, conservé : recopie de la PREMIÈRE source (voir SyncLegacyFields).
        /// À ne plus utiliser dans le code : passer par GetSources().
        /// </summary>
        public string SourcePath
        {
            get => _sourcePath;
            set { _sourcePath = value; OnPropertyChanged(nameof(SourcePath)); }
        }

        /// <summary>
        /// ANCIEN champ, conservé : recopie du chemin de la PREMIÈRE destination.
        /// À ne plus utiliser dans le code : passer par GetDestinations().
        /// </summary>
        public string DestinationPath
        {
            get => _destinationPath;
            set { _destinationPath = value; OnPropertyChanged(nameof(DestinationPath)); }
        }

        /// <summary>
        /// Dossiers d'archives (un sous-dossier par jeu), dans l'ordre de PRIORITÉ : si le même nom de
        /// jeu existe dans deux sources, la première gagne. Vide par défaut (piège des collections).
        /// </summary>
        public List<string> Sources { get; set; } = new List<string>();

        /// <summary>
        /// Destinations d'installation. La PREMIÈRE est la destination par défaut. Vide par défaut
        /// (piège des collections : le JSON ajouterait ses éléments à une liste déjà remplie).
        /// </summary>
        public List<DestinationEntry> Destinations { get; set; } = new List<DestinationEntry>();

        /// <summary>
        /// Migration d'un ancien fichier : si les nouvelles listes sont vides mais que les anciens champs
        /// sont remplis, on en fait le premier élément. À appeler APRÈS le chargement du JSON.
        /// </summary>
        public void MigrateLegacyFields()
        {
            if ((Sources == null || Sources.Count == 0) && !string.IsNullOrWhiteSpace(SourcePath))
                Sources = new List<string> { SourcePath.Trim() };
            if ((Destinations == null || Destinations.Count == 0) && !string.IsNullOrWhiteSpace(DestinationPath))
                Destinations = new List<DestinationEntry> { new DestinationEntry { Name = "", FolderPath = DestinationPath.Trim() } };
        }

        /// <summary>
        /// Nettoie les listes (null, vides, doublons de chemin) puis recopie le premier élément dans les
        /// anciens champs. N'effectue PAS de migration (sinon on ressusciterait une source qu'on vient de
        /// supprimer) : la migration est faite une fois, au chargement (MigrateLegacyFields).
        /// </summary>
        public void SyncLegacyFields()
        {
            var srcs = new List<string>();
            foreach (var s in Sources ?? new List<string>())
            {
                var t = (s ?? "").Trim();
                if (t.Length > 0 && !srcs.Contains(t, StringComparer.OrdinalIgnoreCase)) srcs.Add(t);
            }
            Sources = srcs;

            var dsts = new List<DestinationEntry>();
            foreach (var d in Destinations ?? new List<DestinationEntry>())
            {
                if (d == null) continue;
                var path = (d.FolderPath ?? "").Trim();
                if (path.Length == 0) continue;
                if (dsts.Any(x => string.Equals(x.FolderPath, path, StringComparison.OrdinalIgnoreCase))) continue;
                dsts.Add(new DestinationEntry { Name = (d.Name ?? "").Trim(), FolderPath = path });
            }
            Destinations = dsts;

            SourcePath = srcs.Count > 0 ? srcs[0] : "";
            DestinationPath = dsts.Count > 0 ? dsts[0].FolderPath : "";
        }

        /// <summary>
        /// Recopie le premier chemin NON VIDE des listes dans les anciens champs, SANS nettoyer les listes
        /// (l'écran de réglages s'en sert pendant l'édition : un doublon ou une ligne vide doit rester visible
        /// pour que la validation puisse le signaler). Liste vide → ancien champ vidé.
        /// </summary>
        public void RefreshLegacyFields()
        {
            string src = (Sources ?? new List<string>())
                .Select(x => (x ?? "").Trim()).FirstOrDefault(x => x.Length > 0);
            string dst = (Destinations ?? new List<DestinationEntry>())
                .Where(x => x != null).Select(x => (x.FolderPath ?? "").Trim()).FirstOrDefault(x => x.Length > 0);
            SourcePath = src ?? "";
            DestinationPath = dst ?? "";
        }

        /// <summary>
        /// Sources utilisables (non vides), dans l'ordre. Retombe sur l'ancien champ si la liste est vide
        /// et que la migration n'a pas encore eu lieu. Jamais null.
        /// </summary>
        public List<string> GetSources()
        {
            var list = new List<string>();
            if (Sources != null)
                foreach (var s in Sources)
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim());
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(SourcePath))
                list.Add(SourcePath.Trim());
            return list;
        }

        /// <summary>
        /// Destinations utilisables (chemin non vide), dans l'ordre : la première est la destination par
        /// défaut. Retombe sur l'ancien champ si la liste est vide. Jamais null.
        /// </summary>
        public List<DestinationEntry> GetDestinations()
        {
            var list = new List<DestinationEntry>();
            if (Destinations != null)
                foreach (var d in Destinations)
                    if (d != null && !string.IsNullOrWhiteSpace(d.FolderPath))
                        list.Add(new DestinationEntry { Name = (d.Name ?? "").Trim(), FolderPath = d.FolderPath.Trim() });
            if (list.Count == 0 && !string.IsNullOrWhiteSpace(DestinationPath))
                list.Add(new DestinationEntry { Name = "", FolderPath = DestinationPath.Trim() });
            return list;
        }

        /// <summary>Copie profonde (BeginEdit / CancelEdit).</summary>
        public Profile Copy()
        {
            return new Profile
            {
                Name = Name,
                SourcePath = SourcePath,
                DestinationPath = DestinationPath,
                Sources = new List<string>(Sources ?? new List<string>()),
                Destinations = (Destinations ?? new List<DestinationEntry>())
                    .Where(d => d != null).Select(d => d.Copy()).ToList()
            };
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ======================================================================
    // InstallScope — pour qui installer les jeux (installeurs Inno Setup).
    //   Ask         : on demande à chaque installation (comportement par défaut).
    //   CurrentUser : toujours « utilisateur actuel » (/CURRENTUSER) : pas de demande UAC.
    //   AllUsers    : toujours « tous les utilisateurs » (/ALLUSERS) : l'UAC peut apparaître.
    // Un installeur Inno sans choix possible (script sans PrivilegesRequiredOverridesAllowed)
    // ignore simplement ces options et fait ce que son script prévoit.
    // ======================================================================
    public enum InstallScope { Ask = 0, CurrentUser = 1, AllUsers = 2 }

    // ======================================================================
    // SilentArgs — arguments d'installation silencieuse par type d'installeur.
    // Valeurs par défaut = standards industriels. L'utilisateur peut les
    // personnaliser dans l'UI.
    // ======================================================================
    [Serializable]
    public class SilentArgs
    {
        /// <summary>Inno Setup. /VERYSILENT = aucune UI, /SUPPRESSMSGBOXES = pas de popup.</summary>
        public string InnoArgs { get; set; } = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART";

        /// <summary>NSIS. /S = mode silencieux.</summary>
        public string NsisArgs { get; set; } = "/S";

        /// <summary>MSI. /qn = sans interface, /norestart = pas de redémarrage.</summary>
        public string MsiArgs { get; set; } = "/qn /norestart";
    }

    // ======================================================================
    // SizeEntry — taille d'un dossier de jeu mémorisée (cache).
    //
    // Calculer la taille d'un jeu = parcourir TOUS ses fichiers : long sur un
    // gros disque ou un partage réseau. On ne le fait qu'une fois, puis on
    // réutilise le résultat tant que le dossier n'a pas changé.
    // ======================================================================
    [Serializable]
    public class SizeEntry
    {
        /// <summary>Taille totale en octets.</summary>
        public long Bytes { get; set; }

        /// <summary>
        /// Date de dernière modification du dossier du jeu (en "ticks") au moment
        /// du calcul. Si elle change (fichier ajouté/supprimé à la racine), le
        /// cache est considéré périmé et la taille est recalculée.
        /// </summary>
        public long FolderTicks { get; set; }
    }


    // ======================================================================
    // NotifyCategory — les six catégories de notifications que l'utilisateur peut désactiver
    // (réglages → groupe « Notifications »).
    //
    // RÈGLE ABSOLUE : les ERREURS et les blocages (NotificationType.Error : échec d'installation,
    // espace insuffisant, destination absente ou déjà peuplée, exécutable introuvable…) ne
    // font partie d'AUCUNE catégorie et s'affichent TOUJOURS. Voir PluginSettings.IsNotificationAllowed.
    // ======================================================================
    public enum NotifyCategory
    {
        /// <summary>Nouveaux DLC et patchs détectés dans le dossier source d'un jeu installé.</summary>
        NewExtras,
        /// <summary>Source ou destination absente, à la mise à jour de la bibliothèque.</summary>
        MissingPaths,
        /// <summary>Même dossier de jeu présent dans plusieurs sources.</summary>
        Duplicates,
        /// <summary>Résumés de fin : jeu installé, désinstallé, installation annulée, DLC et patchs terminés.</summary>
        Summaries,
        /// <summary>Précisions pendant l'installation : exécutable choisi ou mémorisé, destination de secours, avancement.</summary>
        InstallInfo,
        /// <summary>Confirmations des menus : profil activé, destination mémorisée, exécutable réassocié.</summary>
        MenuConfirmations
    }

    /// <summary>
    /// « Réponses à tes actions » : les quatre actions de menu dont la réponse peut s'afficher en FENÊTRE (par défaut) ou en
    /// notification et toast (voir ActionReply).
    /// </summary>
    public enum ReplyCategory
    {
        /// <summary>Menu du jeu « Installer les DLC et patchs » : rien à installer, déjà installés, blocages…</summary>
        Extras,
        /// <summary>Menu du jeu « Choisir la destination d'installation » : destination mémorisée.</summary>
        Destination,
        /// <summary>Menu du jeu « Réassocier l'exécutable » : exécutable réassocié (ou introuvable).</summary>
        Reassociate,
        /// <summary>Menu Extensions « Basculer le profil » : profil activé.</summary>
        Profile
    }

    // ======================================================================
    // PluginSettings — modèle persisté en JSON par Playnite.
    //
    // CONCEPT PÉDAGOGIQUE — ISettings :
    // Playnite pilote le cycle d'édition :
    //   BeginEdit()   → appelé à l'ouverture de l'écran de settings.
    //                   On clone pour pouvoir annuler.
    //   CancelEdit()  → l'utilisateur annule : on restaure le clone.
    //   VerifySettings() → appelé avant EndEdit : on valide la cohérence.
    //                      Retourne false = on bloque la validation et on
    //                      affiche les erreurs à l'utilisateur.
    //   EndEdit()     → l'utilisateur valide (OK) : ON SAUVEGARDE.
    //
    // ⚠️ Playnite ne sauvegarde RIEN tout seul : c'est NOUS qui devons appeler
    // SavePluginSettings (méthode du plugin) dans EndEdit. Le modèle n'a pas
    // de référence vers le plugin : celui-ci lui confie un "rappel de
    // sauvegarde" (SetSaveCallback). Ce champ est PRIVÉ → jamais écrit dans le
    // JSON (seules les propriétés publiques le sont).
    // ======================================================================
    [Serializable]
    public class PluginSettings : ISettings
    {
        /// <summary>Liste des profils configurés.</summary>
        public List<Profile> Profiles { get; set; } = new List<Profile>();

        /// <summary>Nom du profil actif (celui qui alimente la bibliothèque).</summary>
        public string ActiveProfileName { get; set; } = "";

        /// <summary>Arguments silencieux personnalisables.</summary>
        public SilentArgs SilentArguments { get; set; } = new SilentArgs();

        /// <summary>
        /// Préfixes de noms .exe à EXCLURE (utilitaires, pas des jeux).
        /// Comparaison insensible à la casse, sur le DÉBUT du nom.
        /// Volontairement VIDE ici : les valeurs par défaut sont appliquées une
        /// seule fois, au premier lancement (CreateDefault). Sinon, à chaque
        /// chargement du JSON, la désérialisation AJOUTERAIT les valeurs
        /// sauvegardées aux valeurs par défaut (doublons, et impossible de
        /// supprimer un défaut).
        /// </summary>
        public List<string> ExcludedExePatterns { get; set; } = new List<string>();

        /// <summary>
        /// Noms de sous-dossiers de sauvegarde à CONSERVER à la désinstallation.
        /// Vide = tout supprimer. Exemple : "saves", "SaveData", "My Games".
        /// </summary>
        public List<string> SaveFolderNames { get; set; } = new List<string>();

        /// <summary>
        /// Exécutable principal choisi par l'utilisateur pour chaque jeu.
        /// Clé = nom du dossier du jeu (GameId). Valeur = chemin RELATIF au
        /// dossier d'installation (ex. "bin\jeu.exe").
        ///
        /// Pourquoi mémoriser ? Sans ça, à chaque mise à jour de la bibliothèque
        /// le plugin redevine "le plus gros .exe" : le choix de l'utilisateur
        /// serait écrasé, et une réinstallation redemanderait tout.
        ///
        /// Ce dictionnaire n'est PAS modifié par l'écran de settings : il n'est
        /// donc ni cloné ni restauré par BeginEdit/CancelEdit (une annulation
        /// des réglages ne doit pas "oublier" un exe choisi entre-temps).
        /// </summary>
        public Dictionary<string, string> RememberedExes { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Vrai (par défaut) = « console » : appuyer sur Installer installe tout de suite,
        /// sans fenêtre de confirmation ni choix d'exécutable (détection automatique).
        /// Faux = les fenêtres de confirmation s'affichent, en mode Bureau COMME en mode Plein écran
        /// (elles se commandent à la manette). Cette case décide seule : le mode de Playnite n'y change rien.
        /// (Valeur par défaut simple : pas le piège des collections, voir CreateDefault.)
        /// </summary>
        public bool DirectInstall { get; set; } = true;

        /// <summary>Pour qui installer les jeux Inno Setup (voir InstallScope).</summary>
        public InstallScope InstallScope { get; set; } = InstallScope.Ask;

        /// <summary>
        /// Vrai (par défaut) = le nom affiché est « nettoyé » pour aider Playnite à
        /// retrouver le jeu en ligne (jaquette, description…) : "Jeu_v1.2.3 [GOG]" → "Jeu".
        /// Le DOSSIER n'est jamais renommé : seul le nom affiché change.
        /// </summary>
        public bool CleanGameNames { get; set; } = true;

        /// <summary>
        /// Vrai (par défaut) = quand les fenêtres sont autorisées (« Installation directe » décochée),
        /// une fenêtre demande confirmation avant d'installer les DLC et patchs détectés. Faux =
        /// installation automatique, sans demander. Sans effet si « Installation directe » est
        /// cochée : sans fenêtre, les extras s'installent toujours automatiquement.
        /// </summary>
        public bool ConfirmExtras { get; set; } = true;

        /// <summary>
        /// Rythme de mise à jour de la progression d'une installation (en secondes, de 2 à 10) :
        /// fenêtre de progression ET notifications (installation directe, sans fenêtre), pour le jeu
        /// comme pour ses DLC et patchs. Un petit chiffre = affichage plus vivant, mais le plugin
        /// parcourt plus souvent les fichiers de la destination ; un grand chiffre ménage les
        /// machines lentes. (Valeur simple : pas le piège des collections, voir CreateDefault.)
        /// </summary>
        public int ProgressIntervalSeconds { get; set; } = 5;

        public const int MinProgressIntervalSeconds = 2;
        public const int MaxProgressIntervalSeconds = 10;

        /// <summary>
        /// Mots d'installeur SUPPLÉMENTAIRES (un par entrée), en plus de « setup » et « install » qui
        /// restent écrits dans le code. Un fichier .exe dont le nom correspond à l'un de ces mots, alors
        /// qu'AUCUN installeur n'est reconnu par signature dans le dossier du jeu, déclenche le CAS AMBIGU
        /// (fenêtre « Installer / Copier / Annuler », en Bureau comme en Plein écran). Ils ne remplacent JAMAIS une
        /// signature reconnue (Inno Setup, NSIS, MSI). Vide par défaut (collection vide : pas de piège de
        /// doublons à la désérialisation, voir CreateDefault).
        /// </summary>
        public List<string> ExtraInstallerWords { get; set; } = new List<string>();

        /// <summary>
        /// Faux (par défaut) = seul le DÉBUT du nom du fichier compte (« setup.exe », « install_jeu.exe »).
        /// Vrai = le mot peut se trouver N'IMPORTE OÙ dans le nom (« jeu_setup.exe »). S'applique à tous les
        /// mots : « setup », « install » et ceux ajoutés.
        /// </summary>
        public bool InstallerWordsAnywhere { get; set; } = false;

        // ------------------------------------------------------------------
        // NOTIFICATIONS DÉSACTIVABLES (une case à cocher par catégorie, voir NotifyCategory)
        //
        // Toutes cochées par défaut = le comportement d'avant : rien ne disparaît tant qu'on ne décoche pas.
        // Un ancien fichier de réglages n'a pas ces champs : la désérialisation garde alors la valeur par
        // défaut ci-dessous (vrai). Les erreurs et blocages ne dépendent d'AUCUNE de ces cases.
        // (Valeurs simples : pas le piège des collections, voir CreateDefault.)
        // ------------------------------------------------------------------

        /// <summary>Prévenir quand de nouveaux DLC ou patchs apparaissent dans le dossier d'un jeu installé.</summary>
        public bool NotifyNewExtras { get; set; } = true;

        /// <summary>Prévenir, à la mise à jour de la bibliothèque, qu'une source ou une destination est inaccessible.</summary>
        public bool NotifyMissingPaths { get; set; } = true;

        /// <summary>Prévenir quand le même dossier de jeu existe dans plusieurs sources.</summary>
        public bool NotifyDuplicates { get; set; } = true;

        /// <summary>Afficher les résumés de fin (jeu installé, désinstallé, installation annulée, DLC et patchs terminés).</summary>
        public bool NotifySummaries { get; set; } = true;

        /// <summary>Afficher les précisions pendant l'installation (exécutable, destination de secours, avancement).</summary>
        public bool NotifyInstallInfo { get; set; } = true;

        /// <summary>Afficher les confirmations des menus (profil activé, destination mémorisée, exécutable réassocié).</summary>
        public bool NotifyMenuConfirmations { get; set; } = true;

        // ------------------------------------------------------------------
        // RÉPONSES À TES ACTIONS (une par catégorie, voir ReplyCategory et ActionReply)
        //
        // Vrai (par défaut) = une FENÊTRE avec « OK » s'affiche devant toi. Faux = notification et toast, comme avant
        // (les cases « Confirmations de menu » s'appliquent alors). Un ancien fichier de réglages n'a pas ces champs :
        // la désérialisation garde la valeur par défaut ci-dessous (fenêtre).
        // ------------------------------------------------------------------

        /// <summary>« Installer les DLC et patchs » : réponse en fenêtre.</summary>
        public bool ReplyWindowExtras { get; set; } = true;

        /// <summary>« Choisir la destination d'installation » : réponse en fenêtre.</summary>
        public bool ReplyWindowDestination { get; set; } = true;

        /// <summary>« Réassocier l'exécutable » : réponse en fenêtre.</summary>
        public bool ReplyWindowReassociate { get; set; } = true;

        /// <summary>« Basculer le profil » : réponse en fenêtre.</summary>
        public bool ReplyWindowProfile { get; set; } = true;

        /// <summary>Vrai si la réponse de cette catégorie s'affiche en fenêtre.</summary>
        public bool IsReplyWindow(ReplyCategory category)
        {
            switch (category)
            {
                case ReplyCategory.Extras: return ReplyWindowExtras;
                case ReplyCategory.Destination: return ReplyWindowDestination;
                case ReplyCategory.Reassociate: return ReplyWindowReassociate;
                case ReplyCategory.Profile: return ReplyWindowProfile;
                default: return true;
            }
        }

        /// <summary>Vrai si au moins une catégorie répond en notification et toast (les cases « Confirmations de menu » servent alors).</summary>
        public bool AnyReplyAsNotification()
        {
            return !ReplyWindowExtras || !ReplyWindowDestination || !ReplyWindowReassociate || !ReplyWindowProfile;
        }

        // ------------------------------------------------------------------
        // TOASTS (petites cartes au premier plan, en plus des notifications — voir ToastHelper)
        //
        // TOUT EST COCHÉ PAR DÉFAUT (décision de l'utilisateur) : les toasts existent pour être vus ; ceux que cela
        // dérange les décochent. Un toast ne s'ajoute QU'À une notification affichée : si la notification d'une
        // catégorie est décochée, son toast n'apparaît pas non plus. Les erreurs sont toujours notifiées ;
        // ToastErrors ajoute un toast.
        // Un ancien fichier de réglages n'a pas ces champs : la désérialisation garde les valeurs par défaut ci-dessous
        // (donc cochées : les toasts apparaissent dès la mise à jour, sans rien changer aux notifications).
        // ------------------------------------------------------------------

        /// <summary>Toast pour les nouveaux DLC et patchs.</summary>
        public bool ToastNewExtras { get; set; } = true;

        /// <summary>Toast pour une source ou une destination absente.</summary>
        public bool ToastMissingPaths { get; set; } = true;

        /// <summary>Toast pour les doublons entre sources.</summary>
        public bool ToastDuplicates { get; set; } = true;

        /// <summary>Toast pour les résumés de fin (jeu installé, désinstallé, annulé, DLC et patchs terminés).</summary>
        public bool ToastSummaries { get; set; } = true;

        /// <summary>Toast pour les précisions pendant l'installation (exécutable choisi, destination de secours…).</summary>
        public bool ToastInstallInfo { get; set; } = true;

        /// <summary>Toast pour les confirmations des menus.</summary>
        public bool ToastMenuConfirmations { get; set; } = true;

        /// <summary>Toast pour les erreurs et blocages (la notification, elle, existe toujours).</summary>
        public bool ToastErrors { get; set; } = true;

        /// <summary>
        /// Vrai (par défaut) : les toasts s'affichent aussi PENDANT une partie (sans jamais prendre le focus).
        /// Faux : seulement si Playnite est au premier plan et qu'aucun jeu n'est en cours de lancement ou d'exécution.
        /// </summary>
        public bool ToastsDuringGame { get; set; } = true;

        /// <summary>
        /// Cache des tailles de jeux (clé = nom du dossier). Remplacé en entier à
        /// chaque écriture (voir SetCachedSize), comme RememberedExes.
        /// </summary>
        public Dictionary<string, SizeEntry> SizeCache { get; set; }
            = new Dictionary<string, SizeEntry>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Taille INSTALLÉE de chaque jeu (en octets), mémorisée après une installation
        /// réussie par installeur. Clé = nom du dossier du jeu (GameId).
        /// Pourquoi ? Un installeur ne dit pas combien d'espace il va écrire : à la prochaine
        /// installation du même jeu, cette taille sert à ESTIMER l'avancement (« ≈ 45 % »).
        /// Remplacé en entier à chaque écriture (voir SetInstalledSize), comme RememberedExes.
        /// Non cloné par BeginEdit/CancelEdit : l'écran de réglages ne le modifie pas.
        /// </summary>
        public Dictionary<string, long> InstalledSizes { get; set; }
            = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// DLC et patchs DÉJÀ INSTALLÉS pour chaque jeu. Clé = nom du dossier du jeu (GameId) ;
        /// valeur = liste d'identifiants d'installeurs (chemin RELATIF dans le dossier source du jeu,
        /// ex. « DLC\setup_dlc1.exe »). Sert au menu clic droit « Installer les DLC et patchs » : il ne
        /// propose que ce qui n'est pas encore installé, donc jamais de doublon.
        /// Remplacé en entier à chaque écriture (copie puis remplacement, comme RememberedExes).
        /// Non cloné par BeginEdit/CancelEdit : l'écran de réglages ne le modifie pas.
        /// </summary>
        public Dictionary<string, List<string>> InstalledExtras { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Destination CHOISIE pour chaque jeu (installé ou non). Clé = nom du dossier du jeu (GameId) ;
        /// valeur = chemin de la destination (la racine, pas le dossier du jeu). Absent = destination par
        /// défaut du profil actif (la première). Sert à « pré-affecter » un vieux jeu au disque Windows
        /// avant de l'installer, et à se souvenir du choix fait dans la fenêtre d'installation.
        /// Remplacé en entier à chaque écriture (copie puis remplacement, comme RememberedExes).
        /// Non cloné par BeginEdit/CancelEdit : l'écran de réglages ne le modifie pas.
        /// </summary>
        public Dictionary<string, string> ChosenDestinations { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// DLC et patchs DÉJÀ VUS pour chaque jeu installé (qu'ils aient été installés ou non). Clé = nom du
        /// dossier du jeu (GameId) ; valeur = identifiants d'installeurs (chemin relatif dans le dossier
        /// source, comme InstalledExtras), précédés du marqueur « * » = « la surveillance est active pour ce
        /// jeu ». Sert à la notification « nouveaux DLC et patchs » : un fichier n'y figure qu'UNE fois, donc
        /// la notification n'apparaît qu'UNE fois par nouveau fichier, pas à chaque mise à jour de la
        /// bibliothèque. Remplacé en entier à chaque écriture (copie puis remplacement, comme RememberedExes).
        /// Non cloné par BeginEdit/CancelEdit : l'écran de réglages ne le modifie pas.
        /// </summary>
        public Dictionary<string, List<string>> SeenExtras { get; set; }
            = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        // Verrou d'écriture sur RememberedExes, SizeCache, InstalledSizes, InstalledExtras, ChosenDestinations et SeenExtras.
        [NonSerialized]
        private readonly object _exeLock = new object();

        // --- Clone interne pour gérer l'annulation (CancelEdit) ---
        [NonSerialized]
        private PluginSettings _clone;

        // --- Rappel de sauvegarde fourni par le plugin ---
        [NonSerialized]
        private Action _saveCallback;

        /// <summary>Réglages du tout premier lancement (aucun fichier JSON encore).</summary>
        public static PluginSettings CreateDefault()
        {
            return new PluginSettings
            {
                ExcludedExePatterns = new List<string>
                {
                    "unins", "uninstall", "vc_redist", "vcredist", "dxsetup",
                    "crashhandler", "crashreporter"
                }
            };
        }

        /// <summary>Le plugin branche ici sa méthode SavePluginSettings.</summary>
        public void SetSaveCallback(Action save) { _saveCallback = save; }

        /// <summary>Sauvegarde immédiate (ex. bascule de profil depuis le menu).</summary>
        public void Save() { _saveCallback?.Invoke(); }

        /// <summary>
        /// Nettoie ce qui vient d'être chargé du JSON : valeurs nulles,
        /// doublons, entrées vides.
        /// </summary>
        public void Normalize()
        {
            Profiles = (Profiles ?? new List<Profile>()).Where(p => p != null).ToList();
            ActiveProfileName = ActiveProfileName ?? "";

            // Migration des anciens réglages (une seule source / destination) vers les listes, puis
            // nettoyage et recopie du premier élément dans les anciens champs.
            foreach (var profile in Profiles)
            {
                profile.MigrateLegacyFields();
                profile.SyncLegacyFields();
            }
            SilentArguments = SilentArguments ?? new SilentArgs();
            ExcludedExePatterns = CleanList(ExcludedExePatterns);
            SaveFolderNames = CleanList(SaveFolderNames);
            ExtraInstallerWords = CleanList(ExtraInstallerWords);

            // Rythme de progression : on le garde entre 2 et 10 secondes (valeur absente ou
            // modifiée à la main dans le fichier JSON → ramenée dans la plage).
            ProgressIntervalSeconds = Math.Max(MinProgressIntervalSeconds,
                                      Math.Min(MaxProgressIntervalSeconds, ProgressIntervalSeconds));

            // Exes mémorisés : on retire les entrées vides et on garantit une
            // comparaison insensible à la casse (Windows ne distingue pas
            // "Jeu" de "jeu"), même si le JSON a recréé le dictionnaire autrement.
            var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (RememberedExes != null)
            {
                foreach (var kv in RememberedExes)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        clean[kv.Key] = kv.Value;
            }
            RememberedExes = clean;

            var cleanSizes = new Dictionary<string, SizeEntry>(StringComparer.OrdinalIgnoreCase);
            if (SizeCache != null)
            {
                foreach (var kv in SizeCache)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null && kv.Value.Bytes >= 0)
                        cleanSizes[kv.Key] = kv.Value;
            }
            SizeCache = cleanSizes;

            var cleanInstalled = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (InstalledSizes != null)
            {
                foreach (var kv in InstalledSizes)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value > 0)
                        cleanInstalled[kv.Key] = kv.Value;
            }
            InstalledSizes = cleanInstalled;

            var cleanExtras = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (InstalledExtras != null)
            {
                foreach (var kv in InstalledExtras)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var list = CleanList(kv.Value);
                    if (list.Count > 0) cleanExtras[kv.Key] = list;
                }
            }
            InstalledExtras = cleanExtras;

            var cleanChosen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (ChosenDestinations != null)
            {
                foreach (var kv in ChosenDestinations)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        cleanChosen[kv.Key] = kv.Value.Trim();
            }
            ChosenDestinations = cleanChosen;

            var cleanSeen = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (SeenExtras != null)
            {
                foreach (var kv in SeenExtras)
                {
                    if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                    var list = CleanList(kv.Value);
                    if (list.Count > 0) cleanSeen[kv.Key] = list;
                }
            }
            SeenExtras = cleanSeen;
        }

        private static List<string> CleanList(List<string> source)
            => (source ?? new List<string>())
                .Select(s => (s ?? "").Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // ==================================================================
        // Cycle d'édition ISettings
        // ==================================================================
        public void BeginEdit()
        {
            // On mémorise l'état actuel pour pouvoir revenir dessus si annulation.
            _clone = Clone();
        }

        public void CancelEdit()
        {
            // L'utilisateur a annulé : on restaure l'état d'avant BeginEdit.
            if (_clone != null)
            {
                Profiles = _clone.Profiles;
                ActiveProfileName = _clone.ActiveProfileName;
                SilentArguments = _clone.SilentArguments;
                ExcludedExePatterns = _clone.ExcludedExePatterns;
                SaveFolderNames = _clone.SaveFolderNames;
                DirectInstall = _clone.DirectInstall;
                InstallScope = _clone.InstallScope;
                CleanGameNames = _clone.CleanGameNames;
                ConfirmExtras = _clone.ConfirmExtras;
                ProgressIntervalSeconds = _clone.ProgressIntervalSeconds;
                ExtraInstallerWords = _clone.ExtraInstallerWords;
                InstallerWordsAnywhere = _clone.InstallerWordsAnywhere;
                NotifyNewExtras = _clone.NotifyNewExtras;
                NotifyMissingPaths = _clone.NotifyMissingPaths;
                NotifyDuplicates = _clone.NotifyDuplicates;
                NotifySummaries = _clone.NotifySummaries;
                NotifyInstallInfo = _clone.NotifyInstallInfo;
                NotifyMenuConfirmations = _clone.NotifyMenuConfirmations;
                ReplyWindowExtras = _clone.ReplyWindowExtras;
                ReplyWindowDestination = _clone.ReplyWindowDestination;
                ReplyWindowReassociate = _clone.ReplyWindowReassociate;
                ReplyWindowProfile = _clone.ReplyWindowProfile;
                ToastNewExtras = _clone.ToastNewExtras;
                ToastMissingPaths = _clone.ToastMissingPaths;
                ToastDuplicates = _clone.ToastDuplicates;
                ToastSummaries = _clone.ToastSummaries;
                ToastInstallInfo = _clone.ToastInstallInfo;
                ToastMenuConfirmations = _clone.ToastMenuConfirmations;
                ToastErrors = _clone.ToastErrors;
                ToastsDuringGame = _clone.ToastsDuringGame;
                _clone = null;
            }
        }

        public void EndEdit()
        {
            // Validation réussie : on n'a plus besoin du clone.
            _clone = null;

            Normalize();

            // Aucun profil actif choisi alors qu'il existe des profils : on prend le premier.
            if (string.IsNullOrEmpty(ActiveProfileName) && Profiles.Count > 0)
                ActiveProfileName = Profiles[0].Name;

            // C'est ICI que la configuration est réellement écrite sur le disque.
            Save();
        }

        public bool VerifySettings(out List<string> errors)
        {
            errors = new List<string>();

            // Aucun profil = autorisé (premier lancement) : le plugin signale
            // simplement "aucun profil actif" lors du scan.
            var profiles = Profiles ?? new List<Profile>();

            foreach (var p in profiles)
            {
                var label = string.IsNullOrWhiteSpace(p.Name) ? "(sans nom)" : p.Name;

                if (string.IsNullOrWhiteSpace(p.Name))
                    errors.Add("Chaque profil doit avoir un nom.");

                var sources = p.GetSources();
                var destinations = p.GetDestinations();

                if (sources.Count == 0)
                    errors.Add($"Profil '{label}' : au moins une source est requise.");
                if (destinations.Count == 0)
                    errors.Add($"Profil '{label}' : au moins une destination est requise.");

                // Chemins valides ? (on garde les chemins normalisés pour les comparaisons qui suivent)
                var okSources = new List<string>();
                foreach (var src in sources)
                {
                    if (!ArchiveScanner.TryNormalizePath(src, out var ns))
                        errors.Add($"Profil '{label}' : le chemin source « {src} » est invalide.");
                    else if (okSources.Contains(ns, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"Profil '{label}' : la source « {src} » est déclarée plusieurs fois.");
                    else
                        okSources.Add(ns);
                }

                var okDestinations = new List<string>();
                foreach (var dst in destinations)
                {
                    if (!ArchiveScanner.TryNormalizePath(dst.FolderPath, out var nd))
                        errors.Add($"Profil '{label}' : le chemin de destination « {dst.FolderPath} » est invalide.");
                    else if (okDestinations.Contains(nd, StringComparer.OrdinalIgnoreCase))
                        errors.Add($"Profil '{label}' : la destination « {dst.FolderPath} » est déclarée plusieurs fois.");
                    else
                        okDestinations.Add(nd);
                }

                // Deux destinations imbriquées (D:\Jeux et D:\Jeux\Anciens) se marcheraient dessus :
                // un jeu nommé « Anciens » serait installé DANS l'autre destination.
                for (int i = 0; i < okDestinations.Count; i++)
                    for (int j = i + 1; j < okDestinations.Count; j++)
                        if (!okDestinations[i].Equals(okDestinations[j], StringComparison.OrdinalIgnoreCase)
                            && ArchiveScanner.PathsOverlap(okDestinations[i], okDestinations[j]))
                            errors.Add($"Profil '{label}' : les destinations « {okDestinations[i]} » et « {okDestinations[j]} » ne doivent pas être imbriquées.");

                // Protection de l'archive : AUCUNE source ne doit être identique à une destination
                // ni s'imbriquer avec elle (une désinstallation pourrait alors supprimer l'archive).
                foreach (var ns in okSources)
                    foreach (var nd in okDestinations)
                        if (ArchiveScanner.PathsOverlap(ns, nd))
                            errors.Add($"Profil '{label}' : la source « {ns} » et la destination « {nd} » ne doivent pas être identiques ni imbriquées.");
            }

            // Noms uniques (le profil actif est retrouvé par son nom).
            var duplicates = profiles
                .Where(p => !string.IsNullOrWhiteSpace(p.Name))
                .GroupBy(p => p.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key);
            foreach (var d in duplicates)
                errors.Add($"Le nom de profil '{d}' est utilisé plusieurs fois.");

            // Le profil actif doit exister dans la liste.
            if (!string.IsNullOrEmpty(ActiveProfileName) &&
                !profiles.Any(x => x.Name == ActiveProfileName))
            {
                errors.Add("Le profil actif sélectionné n'existe pas dans la liste.");
            }

            // Sans argument silencieux, l'installeur afficherait son assistant.
            var a = SilentArguments ?? new SilentArgs();
            if (string.IsNullOrWhiteSpace(a.InnoArgs))
                errors.Add("Les arguments silencieux Inno Setup sont requis.");
            if (string.IsNullOrWhiteSpace(a.NsisArgs))
                errors.Add("Les arguments silencieux NSIS sont requis.");
            if (string.IsNullOrWhiteSpace(a.MsiArgs))
                errors.Add("Les arguments silencieux MSI sont requis.");

            return errors.Count == 0;
        }

        // ==================================================================
        // Helpers métier
        // ==================================================================

        /// <summary>Retourne le profil actif, ou null si non configuré.</summary>
        public Profile GetActiveProfile()
        {
            if (string.IsNullOrEmpty(ActiveProfileName) || Profiles == null)
                return null;
            return Profiles.Find(p => p.Name == ActiveProfileName);
        }

        // ------------------------------------------------------------------
        // SOURCES ET DESTINATIONS (tous profils confondus) — GARDE-FOUS
        // ------------------------------------------------------------------

        /// <summary>
        /// TOUTES les sources de TOUS les profils (pas seulement le profil actif). Sert de garde-fou avant
        /// une suppression : même si l'utilisateur change de profil, aucune archive ne doit être touchée.
        /// </summary>
        public List<string> GetAllSourceRoots()
        {
            var result = new List<string>();
            foreach (var p in Profiles ?? new List<Profile>())
                foreach (var s in p.GetSources())
                    if (!result.Contains(s, StringComparer.OrdinalIgnoreCase)) result.Add(s);
            return result;
        }

        /// <summary>TOUTES les destinations de TOUS les profils (chemins). Voir GetAllSourceRoots.</summary>
        public List<string> GetAllDestinationRoots()
        {
            var result = new List<string>();
            foreach (var p in Profiles ?? new List<Profile>())
                foreach (var d in p.GetDestinations())
                    if (!result.Contains(d.FolderPath, StringComparer.OrdinalIgnoreCase)) result.Add(d.FolderPath);
            return result;
        }

        // ------------------------------------------------------------------
        // DESTINATION CHOISIE PAR JEU
        // ------------------------------------------------------------------

        /// <summary>Destination mémorisée pour ce jeu, ou null (= destination par défaut du profil).</summary>
        public string GetChosenDestination(string gameId)
        {
            var map = ChosenDestinations; // lecture sans verrou (copie puis remplacement à l'écriture)
            string value;
            if (string.IsNullOrEmpty(gameId) || map == null || !map.TryGetValue(gameId, out value)
                || string.IsNullOrWhiteSpace(value))
                return null;
            return value;
        }

        /// <summary>
        /// Mémorise la destination de PLUSIEURS jeux d'un coup, avec UNE seule sauvegarde. Si elle est
        /// identique à la destination par défaut (defaultRoot), l'entrée est simplement retirée : « pas de
        /// choix particulier » = « destination par défaut », sans rien mémoriser pour rien.
        /// </summary>
        public void SetChosenDestinations(IEnumerable<string> gameIds, string destinationRoot, string defaultRoot)
        {
            if (gameIds == null) return;
            bool toDefault = string.IsNullOrWhiteSpace(destinationRoot)
                             || ArchiveScanner.SamePath(destinationRoot, defaultRoot);
            lock (_exeLock)
            {
                var copy = new Dictionary<string, string>(
                    ChosenDestinations ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                foreach (var id in gameIds)
                {
                    if (string.IsNullOrEmpty(id)) continue;
                    if (toDefault) copy.Remove(id);
                    else copy[id] = destinationRoot.Trim();
                }
                ChosenDestinations = copy;
            }
            Save();
        }

        /// <summary>
        /// Racine de destination à utiliser pour ce jeu dans ce profil :
        ///   • la destination choisie, si elle fait toujours partie des destinations du profil ;
        ///   • sinon la PREMIÈRE destination (par défaut). « fallback » vaut alors vrai si un choix
        ///     existait mais n'est plus déclaré (destination supprimée des réglages) : l'appelant prévient.
        /// Retourne null si le profil n'a aucune destination.
        /// </summary>
        public string ResolveDestinationRoot(Profile profile, string gameId, out bool fallback)
        {
            fallback = false;
            if (profile == null) return null;
            var dests = profile.GetDestinations();
            if (dests.Count == 0) return null;

            string chosen = GetChosenDestination(gameId);
            if (chosen == null) return dests[0].FolderPath;

            foreach (var d in dests)
                if (ArchiveScanner.SamePath(d.FolderPath, chosen)) return d.FolderPath;

            fallback = true;
            return dests[0].FolderPath;
        }

        // ------------------------------------------------------------------
        // EXE MÉMORISÉ
        // ------------------------------------------------------------------

        /// <summary>
        /// Retourne le chemin COMPLET de l'exe mémorisé pour ce jeu, ou null si
        /// rien n'est mémorisé / si le fichier n'existe plus / si le chemin
        /// mémorisé sortirait du dossier d'installation (voir TryResolveUnder).
        /// </summary>
        public string ResolveRememberedExe(string gameId, string installDir)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrEmpty(installDir)) return null;

            // Lecture SANS verrou : RememberExe remplace le dictionnaire entier
            // (copie puis affectation de la référence), jamais de modification
            // en place → on lit toujours un dictionnaire cohérent.
            var map = RememberedExes;
            string relative;
            if (map == null || !map.TryGetValue(gameId, out relative)) return null;

            string full;
            if (!ArchiveScanner.TryResolveUnder(installDir, relative, out full)) return null;

            return File.Exists(full) ? full : null;
        }

        /// <summary>
        /// Mémorise l'exe choisi pour ce jeu et sauvegarde les réglages.
        /// Retourne false si l'exe n'est pas dans le dossier d'installation.
        ///
        /// PÉDAGOGIE — "copie puis remplace" : plusieurs threads peuvent lire le
        /// dictionnaire (scan de la bibliothèque) pendant qu'on écrit. Modifier un
        /// Dictionary pendant qu'un autre thread le parcourt provoque une
        /// exception. On modifie donc une COPIE, puis on remplace la référence
        /// (opération atomique). Le verrou n'empêche que deux écritures simultanées.
        /// </summary>
        public bool RememberExe(string gameId, string installDir, string exePath)
        {
            if (string.IsNullOrEmpty(gameId)) return false;

            string relative;
            if (!ArchiveScanner.TryGetRelativePath(installDir, exePath, out relative)) return false;

            lock (_exeLock)
            {
                var copy = new Dictionary<string, string>(
                    RememberedExes ?? new Dictionary<string, string>(),
                    StringComparer.OrdinalIgnoreCase);
                copy[gameId] = relative;
                RememberedExes = copy;
            }

            Save();
            return true;
        }

        // ------------------------------------------------------------------
        // CACHE DES TAILLES
        // ------------------------------------------------------------------

        /// <summary>
        /// Taille mémorisée du jeu, ou null si inconnue / périmée (le dossier a changé
        /// depuis : "folderTicks" ne correspond plus).
        /// </summary>
        public long? GetCachedSize(string gameId, long folderTicks)
        {
            var map = SizeCache; // lecture sans verrou (copie puis remplacement à l'écriture)
            SizeEntry entry;
            if (string.IsNullOrEmpty(gameId) || map == null || !map.TryGetValue(gameId, out entry))
                return null;
            return entry.FolderTicks == folderTicks ? entry.Bytes : (long?)null;
        }

        /// <summary>
        /// Mémorise une taille. NE SAUVEGARDE PAS (on peut en enregistrer des dizaines
        /// d'affilée) : appelez Save() ensuite, une seule fois.
        /// </summary>
        public void SetCachedSize(string gameId, long folderTicks, long bytes)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, SizeEntry>(
                    SizeCache ?? new Dictionary<string, SizeEntry>(),
                    StringComparer.OrdinalIgnoreCase);
                copy[gameId] = new SizeEntry { Bytes = bytes, FolderTicks = folderTicks };
                SizeCache = copy;
            }
        }

        // ------------------------------------------------------------------
        // TAILLES INSTALLÉES (estimation de l'avancement d'un installeur)
        // ------------------------------------------------------------------

        /// <summary>Taille installée mémorisée pour ce jeu, ou null si on ne la connaît pas.</summary>
        public long? GetInstalledSize(string gameId)
        {
            var map = InstalledSizes; // lecture sans verrou (copie puis remplacement à l'écriture)
            long bytes;
            if (string.IsNullOrEmpty(gameId) || map == null || !map.TryGetValue(gameId, out bytes) || bytes <= 0)
                return null;
            return bytes;
        }

        /// <summary>
        /// Mémorise la taille installée d'un jeu. NE SAUVEGARDE PAS : appelez Save() ensuite.
        /// </summary>
        public void SetInstalledSize(string gameId, long bytes)
        {
            if (string.IsNullOrEmpty(gameId) || bytes <= 0) return;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, long>(
                    InstalledSizes ?? new Dictionary<string, long>(),
                    StringComparer.OrdinalIgnoreCase);
                copy[gameId] = bytes;
                InstalledSizes = copy;
            }
        }

        // ------------------------------------------------------------------
        // DLC ET PATCHS DÉJÀ INSTALLÉS (mémoire par jeu)
        // ------------------------------------------------------------------

        /// <summary>
        /// Identifiants des extras déjà installés pour ce jeu (copie : sans risque si le
        /// plugin écrit pendant qu'on la parcourt). Jamais null.
        /// </summary>
        public HashSet<string> GetInstalledExtras(string gameId)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = InstalledExtras; // lecture sans verrou (copie puis remplacement à l'écriture)
            List<string> list;
            if (string.IsNullOrEmpty(gameId) || map == null || !map.TryGetValue(gameId, out list) || list == null)
                return result;
            foreach (var x in list) result.Add(x);
            return result;
        }

        /// <summary>
        /// Marqueur placé en tête de la liste d'un jeu installé AVEC cette version : il signifie « la mémoire
        /// des extras est active pour ce jeu » (même si aucun extra n'est installé). Un jeu installé avant
        /// n'a pas de liste du tout : le plugin sait alors qu'il ne peut pas savoir quels extras sont installés
        /// et prévient l'utilisateur. Le caractère « * » est interdit dans un nom de fichier Windows : le
        /// marqueur ne peut donc jamais être confondu avec un vrai installeur.
        /// </summary>
        public const string ExtrasMemoryMarker = "*";

        /// <summary>Vrai si la mémoire des extras existe pour ce jeu (installé avec cette version, ou extras déjà notés).</summary>
        public bool HasExtrasMemory(string gameId)
        {
            var map = InstalledExtras; // lecture sans verrou (copie puis remplacement à l'écriture)
            List<string> list;
            return !string.IsNullOrEmpty(gameId) && map != null
                   && map.TryGetValue(gameId, out list) && list != null && list.Count > 0;
        }

        /// <summary>
        /// Repart de zéro pour ce jeu : la mémoire ne contient plus que le marqueur (aucun extra installé).
        /// À appeler quand le jeu vient d'être installé à neuf. Sauvegarde.
        /// </summary>
        public void ResetInstalledExtras(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, List<string>>(
                    InstalledExtras ?? new Dictionary<string, List<string>>(),
                    StringComparer.OrdinalIgnoreCase);
                copy[gameId] = new List<string> { ExtrasMemoryMarker };
                InstalledExtras = copy;
            }
            Save();
        }

        /// <summary>
        /// Note PLUSIEURS extras d'un coup comme installés (bouton « Déjà installés »), avec UNE seule
        /// sauvegarde. Retourne le nombre d'extras réellement ajoutés.
        /// </summary>
        public int AddInstalledExtras(string gameId, IEnumerable<string> extraKeys)
        {
            if (string.IsNullOrEmpty(gameId) || extraKeys == null) return 0;
            int added = 0;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, List<string>>(
                    InstalledExtras ?? new Dictionary<string, List<string>>(),
                    StringComparer.OrdinalIgnoreCase);
                var list = new List<string>(copy.ContainsKey(gameId) && copy[gameId] != null
                    ? copy[gameId] : new List<string>());
                // Premier ajout pour un jeu ancien : on pose aussi le marqueur (mémoire désormais active).
                if (list.Count == 0) list.Add(ExtrasMemoryMarker);
                foreach (var key in extraKeys)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;
                    if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                    list.Add(key);
                    added++;
                }
                copy[gameId] = list;
                InstalledExtras = copy;
            }
            Save();
            return added;
        }

        /// <summary>
        /// Ajoute un extra à la liste des installés de ce jeu. SAUVEGARDE tout de suite : si un
        /// extra suivant échoue, ceux déjà installés restent bien mémorisés.
        /// </summary>
        public void AddInstalledExtra(string gameId, string extraKey)
        {
            if (string.IsNullOrEmpty(gameId) || string.IsNullOrWhiteSpace(extraKey)) return;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, List<string>>(
                    InstalledExtras ?? new Dictionary<string, List<string>>(),
                    StringComparer.OrdinalIgnoreCase);
                var list = new List<string>(copy.ContainsKey(gameId) && copy[gameId] != null
                    ? copy[gameId] : new List<string>());
                if (!list.Contains(extraKey, StringComparer.OrdinalIgnoreCase)) list.Add(extraKey);
                copy[gameId] = list;
                InstalledExtras = copy;
            }
            Save();
        }

        /// <summary>
        /// Efface la mémoire des extras d'un jeu (nouvelle installation du jeu, ou désinstallation :
        /// la destination repart de zéro, donc plus aucun extra n'est installé). Sauvegarde.
        /// </summary>
        public void ForgetInstalledExtras(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            bool changed = false;
            lock (_exeLock)
            {
                if (InstalledExtras != null && InstalledExtras.ContainsKey(gameId))
                {
                    var copy = new Dictionary<string, List<string>>(InstalledExtras, StringComparer.OrdinalIgnoreCase);
                    copy.Remove(gameId);
                    InstalledExtras = copy;
                    changed = true;
                }

                // Les extras « déjà vus » (notification des nouveaux DLC et patchs) repartent de zéro eux aussi.
                if (SeenExtras != null && SeenExtras.ContainsKey(gameId))
                {
                    var seenCopy = new Dictionary<string, List<string>>(SeenExtras, StringComparer.OrdinalIgnoreCase);
                    seenCopy.Remove(gameId);
                    SeenExtras = seenCopy;
                    changed = true;
                }
            }
            if (changed) Save();
        }

        // ------------------------------------------------------------------
        // NOTIFICATIONS : QUI A LE DROIT DE S'AFFICHER ?
        // ------------------------------------------------------------------

        /// <summary>Vrai si la catégorie de notifications est cochée dans les réglages.</summary>
        public bool IsNotifyEnabled(NotifyCategory category)
        {
            switch (category)
            {
                case NotifyCategory.NewExtras: return NotifyNewExtras;
                case NotifyCategory.MissingPaths: return NotifyMissingPaths;
                case NotifyCategory.Duplicates: return NotifyDuplicates;
                case NotifyCategory.Summaries: return NotifySummaries;
                case NotifyCategory.InstallInfo: return NotifyInstallInfo;
                case NotifyCategory.MenuConfirmations: return NotifyMenuConfirmations;
                default: return true;
            }
        }

        // Catégorie de chaque notification émise par Notify() (identifiant SANS le suffixe du jeu).
        // Une notification qui n'est pas dans cette table s'affiche TOUJOURS (blocages, avertissements importants :
        // espace insuffisant pour les extras, types d'installeur non reconnus, plantage d'un installeur,
        // erreurs du menu « DLC et patchs »…).
        // PÉDAGOGIE — static readonly : une seule table pour toute l'application, jamais modifiée après sa création.
        private static readonly Dictionary<string, NotifyCategory> NotificationCategories =
            new Dictionary<string, NotifyCategory>(StringComparer.Ordinal)
            {
                // Résumés de fin
                { "ArchiveStore_InstallDone", NotifyCategory.Summaries },
                { "ArchiveStore_InstallCancelled", NotifyCategory.Summaries },
                { "ArchiveStore_ExtrasMenuDone", NotifyCategory.Summaries },
                { "ArchiveStore_ExtrasMarked", NotifyCategory.Summaries },
                { "ArchiveStore_ExtrasSkipped", NotifyCategory.Summaries },
                // Précisions pendant l'installation
                { "ArchiveStore_ExeRemembered", NotifyCategory.InstallInfo },
                { "ArchiveStore_ExeAuto", NotifyCategory.InstallInfo },
                { "ArchiveStore_DestFallback", NotifyCategory.InstallInfo },
                { "ArchiveStore_InstallInfo", NotifyCategory.InstallInfo },
                { "ArchiveStore_ExtrasFound", NotifyCategory.InstallInfo },
                // Réponses « rien à installer » du menu clic droit « Installer les DLC et patchs » : ce sont des
                // confirmations d'une action de menu (case « Confirmations de menu », et son toast). Les ERREURS du
                // même menu gardent l'identifiant « ArchiveStore_ExtrasMenu » : sans catégorie, elles s'affichent toujours.
                { "ArchiveStore_ExtrasMenuNone", NotifyCategory.MenuConfirmations }
            };

        /// <summary>
        /// Vrai si une notification peut s'afficher. LES ERREURS (NotificationType.Error) S'AFFICHENT TOUJOURS,
        /// quelle que soit la case décochée. Pour les autres, on regarde la catégorie de l'identifiant ;
        /// un identifiant inconnu s'affiche (dans le doute, on prévient).
        /// </summary>
        public bool IsNotificationAllowed(string baseId, NotificationType type)
        {
            if (type == NotificationType.Error) return true;

            NotifyCategory category;
            if (baseId != null && NotificationCategories.TryGetValue(baseId, out category))
                return IsNotifyEnabled(category);
            return true;
        }

        // ------------------------------------------------------------------
        // TOASTS : QUI A LE DROIT DE S'AFFICHER ? (voir ToastHelper)
        // ------------------------------------------------------------------

        /// <summary>Vrai si la case « toast » de cette catégorie est cochée.</summary>
        public bool IsToastEnabled(NotifyCategory category)
        {
            switch (category)
            {
                case NotifyCategory.NewExtras: return ToastNewExtras;
                case NotifyCategory.MissingPaths: return ToastMissingPaths;
                case NotifyCategory.Duplicates: return ToastDuplicates;
                case NotifyCategory.Summaries: return ToastSummaries;
                case NotifyCategory.InstallInfo: return ToastInstallInfo;
                case NotifyCategory.MenuConfirmations: return ToastMenuConfirmations;
                default: return false;
            }
        }

        /// <summary>Catégorie d'une notification émise par Notify() (identifiant SANS suffixe), ou null si elle n'en a pas.</summary>
        public NotifyCategory? GetNotificationCategory(string baseId)
        {
            NotifyCategory category;
            if (baseId != null && NotificationCategories.TryGetValue(baseId, out category))
                return category;
            return null;
        }

        /// <summary>
        /// Vrai si une notification de Notify() peut aussi faire un toast. Une notification d'une catégorie suit la
        /// case « toast » de sa catégorie ; une notification sans catégorie (message secondaire) ne fait JAMAIS
        /// de toast, SAUF une erreur, qui suit la case « Erreurs et blocages ».
        /// (L'appelant ne crée un toast que si la notification elle-même est autorisée : un toast s'AJOUTE.)
        /// </summary>
        public bool IsToastAllowed(string baseId, NotificationType type)
        {
            var category = GetNotificationCategory(baseId);
            if (category.HasValue) return IsToastEnabled(category.Value);
            return type == NotificationType.Error && ToastErrors;
        }

        // ------------------------------------------------------------------
        // DLC ET PATCHS DÉJÀ VUS (notification « nouveaux DLC et patchs »)
        // ------------------------------------------------------------------

        /// <summary>Vrai si la surveillance des extras a déjà été initialisée pour ce jeu.</summary>
        public bool HasSeenExtras(string gameId)
        {
            var map = SeenExtras; // lecture sans verrou (copie puis remplacement à l'écriture)
            List<string> list;
            return !string.IsNullOrEmpty(gameId) && map != null
                   && map.TryGetValue(gameId, out list) && list != null && list.Count > 0;
        }

        /// <summary>Identifiants des extras déjà vus pour ce jeu (copie). Jamais null.</summary>
        public HashSet<string> GetSeenExtras(string gameId)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var map = SeenExtras;
            List<string> list;
            if (string.IsNullOrEmpty(gameId) || map == null || !map.TryGetValue(gameId, out list) || list == null)
                return result;
            foreach (var x in list) result.Add(x);
            return result;
        }

        /// <summary>
        /// Note ces extras comme « vus » pour ce jeu (et pose le marqueur « * » : surveillance active).
        /// NE SAUVEGARDE PAS : appelez Save() ensuite, une seule fois pour tout un lot de jeux.
        /// </summary>
        public void MarkExtrasSeen(string gameId, IEnumerable<string> extraKeys)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (_exeLock)
            {
                var copy = new Dictionary<string, List<string>>(
                    SeenExtras ?? new Dictionary<string, List<string>>(),
                    StringComparer.OrdinalIgnoreCase);
                var list = new List<string>(copy.ContainsKey(gameId) && copy[gameId] != null
                    ? copy[gameId] : new List<string>());
                if (!list.Contains(ExtrasMemoryMarker)) list.Insert(0, ExtrasMemoryMarker);
                if (extraKeys != null)
                {
                    foreach (var key in extraKeys)
                    {
                        if (string.IsNullOrWhiteSpace(key)) continue;
                        if (list.Contains(key, StringComparer.OrdinalIgnoreCase)) continue;
                        list.Add(key);
                    }
                }
                copy[gameId] = list;
                SeenExtras = copy;
            }
        }

        /// <summary>Copie profonde (indispensable pour BeginEdit/CancelEdit).</summary>
        private PluginSettings Clone()
        {
            return new PluginSettings
            {
                Profiles = CloneProfiles(Profiles),
                ActiveProfileName = ActiveProfileName,
                SilentArguments = new SilentArgs
                {
                    InnoArgs = SilentArguments?.InnoArgs,
                    NsisArgs = SilentArguments?.NsisArgs,
                    MsiArgs = SilentArguments?.MsiArgs
                },
                ExcludedExePatterns = new List<string>(ExcludedExePatterns ?? new List<string>()),
                SaveFolderNames = new List<string>(SaveFolderNames ?? new List<string>()),
                DirectInstall = DirectInstall,
                InstallScope = InstallScope,
                CleanGameNames = CleanGameNames,
                ConfirmExtras = ConfirmExtras,
                ProgressIntervalSeconds = ProgressIntervalSeconds,
                ExtraInstallerWords = new List<string>(ExtraInstallerWords ?? new List<string>()),
                InstallerWordsAnywhere = InstallerWordsAnywhere,
                NotifyNewExtras = NotifyNewExtras,
                NotifyMissingPaths = NotifyMissingPaths,
                NotifyDuplicates = NotifyDuplicates,
                NotifySummaries = NotifySummaries,
                NotifyInstallInfo = NotifyInstallInfo,
                NotifyMenuConfirmations = NotifyMenuConfirmations,
                ReplyWindowExtras = ReplyWindowExtras,
                ReplyWindowDestination = ReplyWindowDestination,
                ReplyWindowReassociate = ReplyWindowReassociate,
                ReplyWindowProfile = ReplyWindowProfile,
                ToastNewExtras = ToastNewExtras,
                ToastMissingPaths = ToastMissingPaths,
                ToastDuplicates = ToastDuplicates,
                ToastSummaries = ToastSummaries,
                ToastInstallInfo = ToastInstallInfo,
                ToastMenuConfirmations = ToastMenuConfirmations,
                ToastErrors = ToastErrors,
                ToastsDuringGame = ToastsDuringGame
            };
        }

        // Copie PROFONDE des profils : on recrée chaque Profile, sinon le clone
        // partagerait les mêmes instances que l'original et CancelEdit ne
        // restaurerait rien (piège classique des collections de référence).
        private static List<Profile> CloneProfiles(List<Profile> source)
        {
            var list = new List<Profile>();
            if (source == null) return list;
            foreach (var p in source)
                if (p != null) list.Add(p.Copy());
            return list;
        }
    }
}
