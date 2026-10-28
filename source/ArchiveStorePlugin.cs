using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Playnite.SDK;
using Playnite.SDK.Events;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // UiHelper — "ramène" du code sur le thread UI de WPF.
    //
    // PÉDAGOGIE — Dispatcher :
    // Une fenêtre WPF n'est utilisable QUE depuis le thread UI. Notre
    // installation tourne en arrière-plan (Task.Run) pour ne pas figer
    // Playnite. Chaque fois qu'elle doit ouvrir/modifier une fenêtre ou
    // toucher la base Playnite, elle passe par Dispatcher.Invoke : le code
    // est exécuté sur le thread UI et l'appelant ATTEND qu'il ait fini
    // (indispensable pour récupérer le choix de l'utilisateur dans un dialogue).
    // Si on est déjà sur le thread UI, Invoke exécute simplement le code.
    // ======================================================================
    internal static class UiHelper
    {
        public static T OnUi<T>(Func<T> func)
            => Application.Current.Dispatcher.Invoke(func);

        public static void OnUi(Action action)
            => Application.Current.Dispatcher.Invoke(action);
    }

    // ======================================================================
    // InstalledSizeHelper — la taille d'un jeu INSTALLÉ.
    //
    // Playnite affiche "Taille d'installation" (InstallSize). Tant qu'un jeu n'est pas installé, on y met la taille de son
    // dossier source (l'installeur). Une fois installé, la vraie taille est celle du dossier installé, qui peut être plus
    // petite ou plus grande que l'installeur : on la mesure donc à la fin de l'installation (et d'un ajout de DLC / patch),
    // et le calcul en arrière-plan ne remet plus la taille de l'installeur sur un jeu installé.
    // ======================================================================
    internal static class InstalledSizeHelper
    {
        private static readonly HashSet<string> Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object Gate = new object();

        /// <summary>Vrai si la taille installée de ce jeu a déjà été mesurée pendant cette session de Playnite.</summary>
        public static bool IsDone(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return false;
            lock (Gate) return Done.Contains(gameId);
        }

        public static void MarkDone(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (Gate) Done.Add(gameId);
        }

        /// <summary>Le jeu n'est plus installé : au prochain mesure, on repart de zéro.</summary>
        public static void Forget(string gameId)
        {
            if (string.IsNullOrEmpty(gameId)) return;
            lock (Gate) Done.Remove(gameId);
        }

        /// <summary>Mesure le dossier installé (à appeler hors du thread UI) puis écrit la taille dans Playnite. Ne lève jamais d'exception.</summary>
        public static void Refresh(IPlayniteAPI api, Guid databaseId, string gameId, string installDir)
        {
            try
            {
                if (api == null || string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return;
                long bytes = ArchiveScanner.GetDirectorySize(installDir);
                MarkDone(gameId);
                if (bytes <= 0) return;

                ulong size = (ulong)bytes;
                UiHelper.OnUi(() =>
                {
                    var game = api.Database.Games.Get(databaseId);
                    if (game == null || game.InstallSize == size) return;
                    game.InstallSize = size;
                    api.Database.Games.Update(game);
                });
            }
            catch (Exception ex)
            {
                LogManager.GetLogger().Warn(ex, "ArchiveStore : taille du jeu installé non mise à jour.");
            }
        }
    }

    // ======================================================================
    // GameActionHelper — création / mise à jour de l'action "Jouer".
    //
    // ⚠️ IsPlayAction = true est INDISPENSABLE : sans lui, Playnite considère
    // l'action comme une simple action annexe et n'affiche PAS le bouton
    // natif "Jouer".
    // ======================================================================
    internal static class GameActionHelper
    {
        public static GameAction MakePlayAction(string exePath)
        {
            return new GameAction
            {
                Type = GameActionType.File,
                Path = exePath,
                WorkingDir = Path.GetDirectoryName(exePath),
                Name = "Jouer",
                IsPlayAction = true
            };
        }

        /// <summary>Remplace l'action de lancement du jeu (UI thread requis).</summary>
        public static void SetPlayAction(IPlayniteAPI api, Guid gameId, string exePath)
        {
            var g = api.Database.Games.Get(gameId);
            if (g == null) return;

            // On conserve les actions annexes éventuelles, on remplace seulement le "Jouer".
            var others = (g.GameActions ?? new ObservableCollection<GameAction>())
                .Where(a => !a.IsPlayAction);
            var actions = new ObservableCollection<GameAction>(others);
            actions.Insert(0, MakePlayAction(exePath));
            g.GameActions = actions;
            api.Database.Games.Update(g);
        }

        /// <summary>Retire l'action de lancement (UI thread requis).</summary>
        public static void RemovePlayAction(IPlayniteAPI api, Guid gameId)
        {
            var g = api.Database.Games.Get(gameId);
            if (g?.GameActions == null) return;
            g.GameActions = new ObservableCollection<GameAction>(
                g.GameActions.Where(a => !a.IsPlayAction));
            api.Database.Games.Update(g);
        }
    }

    // ======================================================================
    // ArchiveLog — journal de l'extension.
    //
    // Deux destinations :
    //   1. le journal de Playnite (playnite.log), via LogManager ;
    //   2. un petit fichier "archivestore.log" dans le dossier de données du plugin
    //      (Plugin.GetPluginUserDataPath() — il suit Playnite portable), une ligne
    //      par événement avec la date et l'heure. Plus lisible pour comprendre
    //      pourquoi un DLC ou un patch n'a pas pu s'installer.
    //
    // RÈGLES :
    //  - Écrire dans le journal ne doit JAMAIS faire échouer une installation :
    //    toute erreur d'écriture est avalée (try/catch).
    //  - Plusieurs threads peuvent écrire en même temps : un verrou (lock) sérialise
    //    les écritures, sinon deux lignes pourraient se mélanger ou le fichier être
    //    verrouillé par l'une pendant que l'autre essaie d'écrire.
    //  - Taille limitée : au-delà de ~1 Mo, le fichier devient archivestore.log.old
    //    (l'ancien .old est remplacé). Il ne grossit donc jamais sans fin.
    // ======================================================================
    internal static class ArchiveLog
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private static readonly object _lock = new object();
        private const long MaxBytes = 1024 * 1024;
        private static string _filePath; // null = pas de fichier (seul playnite.log est utilisé)
        private static string _folder;   // dossier de données du plugin (null = indisponible)

        public static void Init(string folder)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folder)) return;
                Directory.CreateDirectory(folder);
                _folder = folder;
                _filePath = Path.Combine(folder, "archivestore.log");
            }
            catch (Exception ex)
            {
                _filePath = null;
                _folder = null;
                logger.Warn(ex, "ArchiveStore : fichier journal indisponible (playnite.log reste utilisé).");
            }
        }

        // OUVRIR LE JOURNAL (bouton des réglages).
        // Ouvre l'Explorateur Windows sur le dossier de données du plugin. Si archivestore.log existe,
        // l'option /select le met en surbrillance (« explorer.exe /select,"chemin" » : option de
        // l'Explorateur Windows). Sinon on ouvre simplement le dossier.
        // Retourne faux, avec un message en français, si le dossier est indisponible ou si l'ouverture échoue :
        // l'appelant affiche alors ce message. Ne lève jamais d'exception.
        public static bool OpenLogFolder(out string error)
        {
            error = null;
            try
            {
                if (_folder == null)
                {
                    error = "Le dossier du journal est indisponible : seul le journal de Playnite (playnite.log) est utilisé.";
                    return false;
                }

                if (_filePath != null && File.Exists(_filePath))
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _filePath + "\"");
                else
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + _folder + "\"");
                return true;
            }
            catch (Exception ex)
            {
                error = "Impossible d'ouvrir le dossier du journal : " + ex.Message;
                return false;
            }
        }

        // PÉDAGOGIE — journal propre à Inno Setup.
        // Inno Setup sait écrire son PROPRE journal détaillé (étape par étape) si on lui passe
        // l'argument /LOG="chemin" (documenté dans l'aide d'Inno Setup : si le fichier existe
        // déjà, il est remplacé). On l'ajoute ici, AU LANCEMENT SEULEMENT : il n'apparaît pas
        // dans les réglages et ne les modifie pas. Le fichier est rangé à côté de
        // archivestore.log, dans le dossier de données du plugin.
        // Retourne les arguments inchangés si le dossier est indisponible ou si l'utilisateur
        // a déjà mis son propre /LOG dans ses arguments. "logFile" reçoit le chemin choisi
        // (ou null si rien n'a été ajouté), pour pouvoir l'écrire dans archivestore.log.
        public static string WithInnoLog(string arguments, string label, out string logFile)
        {
            logFile = null;
            try
            {
                if (_folder == null) return arguments;
                if (!string.IsNullOrEmpty(arguments) &&
                    System.Text.RegularExpressions.Regex.IsMatch(arguments, @"(^|\s)/LOG(=|\s|$)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return arguments;

                string name = "inno_" + label;
                foreach (var c in Path.GetInvalidFileNameChars())
                    name = name.Replace(c, '_');
                if (name.Length > 100) name = name.Substring(0, 100);

                logFile = Path.Combine(_folder, name + ".log");
                return arguments + " /LOG=\"" + logFile + "\"";
            }
            catch
            {
                logFile = null;
                return arguments;
            }
        }

        public static void Info(string message) { logger.Info("ArchiveStore : " + message); Append("INFO ", message); }
        public static void Warn(string message) { logger.Warn("ArchiveStore : " + message); Append("WARN ", message); }
        public static void Error(string message) { logger.Error("ArchiveStore : " + message); Append("ERREUR", message); }

        private static void Append(string level, string message)
        {
            lock (_lock)
            {
                try
                {
                    if (_filePath == null) return;

                    var info = new FileInfo(_filePath);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        var old = _filePath + ".old";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(_filePath, old);
                    }

                    var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + message + Environment.NewLine;
                    File.AppendAllText(_filePath, line, Encoding.UTF8);
                }
                catch
                {
                    // Volontairement muet : le journal ne doit jamais casser une installation.
                }
            }
        }
    }

    public class ArchiveStorePlugin : LibraryPlugin
    {
        private const string PluginGuid = "7660910e-8cb7-429d-adf7-03edd5b9ee89";

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly IPlayniteAPI _api;
        private readonly PluginSettings _settings;

        public ArchiveStorePlugin(IPlayniteAPI api) : base(api)
        {
            _api = api;

            // ⚠️ Playnite ne recharge pas nos réglages tout seul : on les lit
            // explicitement. LoadPluginSettings renvoie null s'il n'existe pas
            // encore de fichier (tout premier lancement).
            PluginSettings loaded = null;
            try { loaded = LoadPluginSettings<PluginSettings>(); }
            catch (Exception ex) { logger.Error(ex, "ArchiveStore : lecture des réglages impossible."); }

            _settings = loaded ?? PluginSettings.CreateDefault();
            _settings.Normalize();
            _settings.SetSaveCallback(() => SavePluginSettings(_settings));

            // Dossier de données du plugin (Plugin.GetPluginUserDataPath) : on y range
            // archivestore.log. Un try/catch interne à Init évite tout plantage ici.
            try { ArchiveLog.Init(GetPluginUserDataPath()); }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : dossier de données du plugin introuvable."); }

            // Sans HasSettings = true, Playnite n'affiche pas l'écran de paramètres.
            Properties = new LibraryPluginProperties { HasSettings = true };

            // MANETTE, STYLE, TOASTS : boîte à outils « console » partagée (dossier ArgentToolkit), branchée sur
            // notre journal. Les fenêtres « console » (InstallDialog, ProgressWindow…) reçoivent les boutons de la
            // manette par l'intermédiaire de GamepadHelper (voir ArgentToolkit\src\ConsoleWindow.cs).
            Toolkit.Initialize(api, ArchiveLog.Info, ArchiveLog.Warn);

            // COMPATIBLES ARGENT : avec le thème Argent, nos réglages sont dans sa fenêtre Personnalisation (voir ArgentSettings.cs).
            new ArgentSettings(_settings, api, PluginGuid).Register();
            // Avec Argent (Plein écran) : les actions du menu du jeu vont sur la fiche du jeu et dans le menu Options de la bibliothèque.
            ArgentBridge.RegisterGameActions(PluginGuid, "ArchiveStore", GameActionsForArgent);
        }

        // ------------------------------------------------------------------
        // MANETTE — Playnite nous prévient à chaque bouton pressé ou relâché.
        //   OnControllerButtonStateChanged        : mode PLEIN ÉCRAN.
        //   OnDesktopControllerButtonStateChanged : mode BUREAU (il faut activer la manette dans les
        //                                           réglages d'entrée du mode Bureau de Playnite).
        // Nous ne faisons que TRANSMETTRE à GamepadHelper, qui livre l'ordre à la fenêtre ArchiveStore
        // active (s'il y en a une) et ignore tout le reste.
        // ------------------------------------------------------------------
        public override void OnControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            GamepadHelper.OnButton(args.Button, args.State, false);
        }

        public override void OnDesktopControllerButtonStateChanged(OnControllerButtonStateChangedArgs args)
        {
            GamepadHelper.OnButton(args.Button, args.State, true);
        }

        public override Guid Id => Guid.Parse(PluginGuid);
        public override string Name => "ArchiveStore";

        // Icône de la bibliothèque : icon.png placé à côté de la DLL (si présent).
        public override string LibraryIcon
        {
            get
            {
                try
                {
                    var dir = Path.GetDirectoryName(typeof(ArchiveStorePlugin).Assembly.Location);
                    var icon = Path.Combine(dir, "icon.png");
                    return File.Exists(icon) ? icon : null;
                }
                catch { return null; }
            }
        }

        public override ISettings GetSettings(bool firstRunSettings) => _settings;

        public override UserControl GetSettingsView(bool firstRunView)
            => new SettingsView(_settings, _api);

        // ==================================================================
        // SCÉNARIO 1 — SCAN SOURCE → BIBLIOTHÈQUE
        // ==================================================================
        public override IEnumerable<GameMetadata> GetGames(LibraryGetGamesArgs args)
        {
            var profile = _settings.GetActiveProfile();
            if (profile == null)
            {
                logger.Warn("ArchiveStore : aucun profil actif.");
                _api.Notifications.Add(new NotificationMessage(
                    "ArchiveStore_NoProfile",
                    "ArchiveStore : aucun profil actif. Créez un profil dans les paramètres de l'extension.",
                    NotificationType.Info));
                return Enumerable.Empty<GameMetadata>();
            }

            var sources = profile.GetSources();
            if (sources.Count == 0)
            {
                string noSourceMsg = $"ArchiveStore : le profil '{profile.Name}' n'a aucune source configurée.";
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_SourceInjoignable", noSourceMsg, NotificationType.Error));
                ToastHelper.ShowError(_api, _settings, noSourceMsg); // toast en plus, si la case « Erreurs et blocages » est cochée
                return Enumerable.Empty<GameMetadata>();
            }

            var destinationRoots = profile.GetDestinations().Select(d => d.FolderPath).ToList();

            var list = new List<GameMetadata>();
            try
            {
                // PLUSIEURS SOURCES : le scanner les lit dans l'ordre et fusionne les jeux (la première
                // source gagne en cas de doublon). Il note aussi les sources inaccessibles.
                // La destination PRÉVUE de chaque jeu (choix de l'utilisateur, sinon la première) est
                // testée en premier pour savoir si le jeu est installé.
                var report = ArchiveScanner.ScanSources(
                    sources, destinationRoots,
                    id => { bool fb; return _settings.ResolveDestinationRoot(profile, id, out fb); },
                    _settings.ExcludedExePatterns, args.CancelToken);

                foreach (var dup in report.IgnoredDuplicates)
                    ArchiveLog.Warn("Doublon ignoré : " + dup);

                if (report.UnreachableSources.Count > 0)
                {
                    string names = string.Join(" ; ", report.UnreachableSources.Select(x => "'" + x + "'"));
                    ArchiveLog.Warn($"Source(s) inaccessible(s) : {names}.");

                    // Case « Source ou destination absente » décochée : le journal garde la trace, mais aucune notification.
                    if (!_settings.IsNotifyEnabled(NotifyCategory.MissingPaths))
                    {
                        try { _api.Notifications.Remove("ArchiveStore_SourceInjoignable"); }
                        catch (Exception ex) { logger.Warn(ex, "ArchiveStore : retrait de l'alerte de source impossible."); }
                    }
                    else
                    {
                        string unreachableMsg =
                            (report.UnreachableSources.Count == 1
                                ? $"ArchiveStore : la source {names} est inaccessible."
                                : $"ArchiveStore : les sources {names} sont inaccessibles.") +
                            " Vérifiez que le disque (ou le NAS) est connecté. " +
                            "Les jeux de cette source restent dans la bibliothèque mais ne peuvent pas être installés pour l'instant.";
                        _api.Notifications.Add(new NotificationMessage("ArchiveStore_SourceInjoignable", unreachableMsg, NotificationType.Error));
                        // Notification gouvernée par la case « Source ou destination absente » : son toast aussi.
                        ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.MissingPaths, unreachableMsg);
                    }
                }
                else
                {
                    // Tout est de nouveau accessible : on retire l'ancienne alerte.
                    try { _api.Notifications.Remove("ArchiveStore_SourceInjoignable"); }
                    catch (Exception ex) { logger.Warn(ex, "ArchiveStore : retrait de l'alerte de source impossible."); }
                }

                // DOUBLONS : un même nom de jeu dans plusieurs sources. La première source est utilisée ;
                // on prévient l'utilisateur (le détail est dans le journal).
                NotifyDuplicates(report.Duplicates);

                // DESTINATIONS ABSENTES (disque externe débranché…) : simple information, rien n'est bloqué.
                NotifyUnreachableDestinations(profile);

                // Aucune source lisible : on ne renvoie rien (Playnite laisse alors les jeux tels quels).
                if (report.ReachableCount == 0)
                    return Enumerable.Empty<GameMetadata>();

                var scans = report.Results;

                foreach (var scan in scans)
                {
                    args.CancelToken.ThrowIfCancellationRequested();

                    var meta = new GameMetadata
                    {
                        // GameId = nom du DOSSIER : identifiant stable, jamais nettoyé.
                        GameId = scan.FolderName,
                        // Name = nom AFFICHÉ (et utilisé par Playnite pour chercher la jaquette
                        // en ligne) : nettoyé si l'option est active.
                        Name = _settings.CleanGameNames
                            ? ArchiveScanner.CleanGameName(scan.FolderName)
                            : scan.FolderName,
                        IsInstalled = scan.IsInstalled,
                        InstallDirectory = scan.IsInstalled ? scan.InstallDirectory : null,

                        // Plateforme "PC (Windows)" : sans elle, la recherche en ligne de
                        // jaquettes est moins précise (le jeu n'a aucune plateforme).
                        // ("pc_windows" = identifiant standard de Playnite pour cette plateforme.)
                        Platforms = new HashSet<MetadataProperty>
                        {
                            new MetadataSpecProperty("pc_windows")
                        }
                    };

                    // Taille déjà connue (cache) → affichée dès l'import. Sinon elle sera
                    // calculée en arrière-plan (voir OnLibraryUpdated / RunSizeWorker).
                    try
                    {
                        var ticks = Directory.GetLastWriteTimeUtc(scan.FullSourcePath).Ticks;
                        var cached = _settings.GetCachedSize(scan.FolderName, ticks);
                        if (cached.HasValue && cached.Value > 0)
                            meta.InstallSize = (ulong)cached.Value;
                    }
                    catch { /* taille facultative : jamais bloquant */ }

                    // JAQUETTES / ICÔNES AUTOMATIQUES : si le dossier du jeu contient
                    // icon.png, cover.jpg, background.jpg… (voir ArchiveScanner.FindArtwork),
                    // on les donne à Playnite. MetadataFile(chemin local) : Playnite COPIE
                    // l'image dans sa propre base → l'archive n'est que LUE, jamais modifiée.
                    ApplyArtwork(meta, scan.Artwork);

                    if (scan.IsInstalled && !string.IsNullOrEmpty(scan.MainExePath))
                    {
                        // EXE MÉMORISÉ : le choix de l'utilisateur l'emporte sur l'heuristique
                        // "le plus gros .exe" (sinon chaque scan écraserait son choix).
                        var exe = _settings.ResolveRememberedExe(scan.FolderName, scan.InstallDirectory)
                                  ?? scan.MainExePath;

                        meta.GameActions = new List<GameAction>
                        {
                            GameActionHelper.MakePlayAction(exe)
                        };
                    }

                    list.Add(meta);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : erreur de scan.");
                string scanErrorMsg = $"ArchiveStore : erreur — {ex.Message}";
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_ScanError", scanErrorMsg, NotificationType.Error));
                ToastHelper.ShowError(_api, _settings, scanErrorMsg);
            }

            return list;
        }

        // ------------------------------------------------------------------
        // NOTIFICATIONS DE LA MISE À JOUR DE LA BIBLIOTHÈQUE (thread d'arrière-plan, comme GetGames)
        // ------------------------------------------------------------------

        // Notification de doublons. S'il n'y en a plus, l'ancienne notification est retirée.
        private void NotifyDuplicates(List<ArchiveScanner.DuplicateGame> duplicates)
        {
            const string id = "ArchiveStore_Doublons";
            try
            {
                // Case « Doublons entre sources » décochée : on retire une éventuelle ancienne notification
                // (le détail reste dans archivestore.log, écrit plus haut).
                if (duplicates == null || duplicates.Count == 0 || !_settings.IsNotifyEnabled(NotifyCategory.Duplicates))
                {
                    _api.Notifications.Remove(id);
                    return;
                }

                // Un même jeu peut être présent dans trois sources : on regroupe par nom.
                var groups = duplicates.GroupBy(d => d.FolderName, StringComparer.OrdinalIgnoreCase).ToList();
                string message;
                if (groups.Count == 1)
                {
                    var g = groups[0];
                    var others = string.Join(" et ", g.Select(d => d.IgnoredSource).Distinct(StringComparer.OrdinalIgnoreCase));
                    message =
                        $"ArchiveStore : doublon détecté — « {g.Key} » existe dans {g.First().KeptSource} et dans {others}. " +
                        $"La première source ({g.First().KeptSource}) est utilisée ; l'autre copie est ignorée.";
                }
                else
                {
                    const int maxShown = 3;
                    var names = groups.Take(maxShown).Select(g => "« " + g.Key + " »");
                    message =
                        $"ArchiveStore : {groups.Count} doublons détectés — {string.Join(", ", names)}" +
                        (groups.Count > maxShown ? ", …" : "") +
                        " existent dans plusieurs sources. Pour chacun, la première source est utilisée ; " +
                        "les autres copies sont ignorées (détails dans archivestore.log).";
                }

                _api.Notifications.Add(new NotificationMessage(id, message, NotificationType.Info));
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.Duplicates, message);
            }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : notification des doublons impossible."); }
        }

        // Notification des destinations dont le disque est absent. Elle disparaît quand tout est de nouveau accessible.
        private void NotifyUnreachableDestinations(Profile profile)
        {
            const string id = "ArchiveStore_DestinationInjoignable";
            try
            {
                var missing = profile.GetDestinations()
                    .Where(d => !ArchiveScanner.IsDriveReachable(d.FolderPath))
                    .ToList();

                if (missing.Count == 0)
                {
                    _api.Notifications.Remove(id);
                    return;
                }

                string names = string.Join(", ", missing.Select(d => "« " + d.GetLabel() + " »"));
                ArchiveLog.Warn($"Destination(s) inaccessible(s) : {names}.");

                // Case « Source ou destination absente » décochée : journal seulement.
                if (!_settings.IsNotifyEnabled(NotifyCategory.MissingPaths))
                {
                    _api.Notifications.Remove(id);
                    return;
                }

                string missingDestMsg =
                    (missing.Count == 1
                        ? $"ArchiveStore : la destination {names} n'est pas accessible."
                        : $"ArchiveStore : les destinations {names} ne sont pas accessibles.") +
                    " Reconnectez le disque, ou choisissez une autre destination dans les réglages de l'extension. " +
                    "Rien n'est bloqué : les autres destinations restent utilisables.";
                _api.Notifications.Add(new NotificationMessage(id, missingDestMsg, NotificationType.Info));
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.MissingPaths, missingDestMsg);
            }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : notification des destinations absentes impossible."); }
        }

        // ==================================================================
        // TAILLES DES JEUX EN ARRIÈRE-PLAN
        // ==================================================================
        // Playnite possède un champ natif "Taille d'installation" (InstallSize),
        // visible sans fenêtre de notre part. On le remplit pour chaque jeu de la
        // bibliothèque, SANS ralentir l'import : le calcul (parcours de tous les
        // fichiers) se fait après, dans un thread à part.
        //
        // PÉDAGOGIE — Interlocked : si deux mises à jour de bibliothèque se
        // suivent, on évite de lancer deux calculs en même temps. CompareExchange
        // passe le drapeau de 0 à 1 de façon ATOMIQUE (aucun autre thread ne peut
        // s'intercaler) et nous dit si on était bien le premier.
        private int _sizeWorkerRunning;

        public override void OnLibraryUpdated(OnLibraryUpdatedEventArgs args)
        {
            // Vérification des nouveaux DLC et patchs (notification), en arrière-plan, indépendante du calcul des tailles.
            StartExtrasWatch();

            if (Interlocked.CompareExchange(ref _sizeWorkerRunning, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try { RunSizeWorker(); }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : calcul des tailles interrompu."); }
                finally { Interlocked.Exchange(ref _sizeWorkerRunning, 0); }
            });
        }

        private void RunSizeWorker()
        {
            var profile = _settings.GetActiveProfile();
            if (profile == null) return;

            // PLUSIEURS SOURCES : le dossier d'un jeu est cherché dans chacune, dans l'ordre.
            var sourceRoots = profile.GetSources();
            if (sourceRoots.Count == 0) return;

            // La base Playnite se lit depuis le thread UI : on en tire une copie simple
            // (identifiants + taille actuelle) puis on travaille hors UI.
            var games = UiHelper.OnUi(() => _api.Database.Games
                .Where(g => g.PluginId == Id && !string.IsNullOrEmpty(g.GameId))
                .Select(g => new { g.Id, g.GameId, g.InstallSize, g.IsInstalled, g.InstallDirectory })
                .ToList());

            int computed = 0;
            foreach (var g in games)
            {
                // JEU INSTALLÉ : la taille est celle du dossier INSTALLÉ (pas celle de l'installeur). Mesurée une fois par
                // session de Playnite (et à la fin de chaque installation) : c'est ce qui corrige aussi les jeux déjà installés.
                bool installed = g.IsInstalled && !string.IsNullOrEmpty(g.InstallDirectory) && Directory.Exists(g.InstallDirectory);
                if (installed)
                {
                    if (InstalledSizeHelper.IsDone(g.GameId)) continue;
                    InstalledSizeHelper.Refresh(_api, g.Id, g.GameId, g.InstallDirectory);
                    continue;
                }
                InstalledSizeHelper.Forget(g.GameId);

                var dir = ArchiveScanner.FindGameSourceDir(sourceRoots, g.GameId);
                if (dir == null) continue; // jeu absent, ou source débranchée : on ne touche à rien

                var ticks = Directory.GetLastWriteTimeUtc(dir).Ticks;
                long bytes;
                var cached = _settings.GetCachedSize(g.GameId, ticks);
                if (cached.HasValue)
                {
                    bytes = cached.Value;
                }
                else
                {
                    bytes = ArchiveScanner.GetDirectorySize(dir); // long : c'est pourquoi on est en arrière-plan
                    _settings.SetCachedSize(g.GameId, ticks, bytes);
                    computed++;
                }

                if (bytes <= 0 || g.InstallSize == (ulong)bytes) continue;

                var gameId = g.Id;
                var size = (ulong)bytes;
                UiHelper.OnUi(() =>
                {
                    var game = _api.Database.Games.Get(gameId);
                    if (game == null) return;
                    if (game.IsInstalled) return; // installé entre-temps : sa vraie taille est celle du dossier installé
                    game.InstallSize = size;
                    _api.Database.Games.Update(game);
                });
            }

            // Une seule écriture disque pour tout le lot.
            if (computed > 0)
            {
                try { _settings.Save(); }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : sauvegarde du cache des tailles impossible."); }
            }
        }

        // ==================================================================
        // NOUVEAUX DLC ET PATCHS — notification par jeu
        // ==================================================================
        // À chaque mise à jour de la bibliothèque, on regarde, dans le dossier SOURCE de chaque jeu installé,
        // s'il y a des DLC ou patchs qu'on n'avait jamais vus. Si oui : UNE notification pour ce jeu. Un clic
        // ouvre la fenêtre « DLC et patchs » (où l'on coche ce qu'on veut), jamais une installation directe.
        //
        // RÈGLES (pour ne jamais crier au loup) :
        //   • seuls les jeux dont on connaît les extras installés sont surveillés (PluginSettings.HasExtrasMemory) ;
        //   • un jeu copié tel quel (jeu portable) n'est jamais surveillé : sa « taille installée » n'est mémorisée
        //     que pour les jeux installés par un installeur reconnu, c'est notre repère ;
        //   • la PREMIÈRE vérification d'un jeu ne notifie rien : elle note seulement ce qui existe déjà
        //     (PluginSettings.SeenExtras). Ensuite, chaque fichier n'est signalé QU'UNE FOIS, même si on ne
        //     l'installe pas : la notification ne revient donc pas en boucle ;
        //   • un extra déjà installé n'est jamais signalé ;
        //   • source débranchée = on saute le jeu, sans rien noter ni dire (la case « Source ou destination
        //     absente » s'en charge ailleurs).
        // La source n'est que LUE : rien n'y est modifié.
        private int _extrasWatchRunning;

        private void StartExtrasWatch()
        {
            // Case « Nouveaux DLC et patchs » décochée : on ne parcourt même pas les dossiers.
            if (!_settings.NotifyNewExtras) return;
            if (Interlocked.CompareExchange(ref _extrasWatchRunning, 1, 0) != 0) return;

            Task.Run(() =>
            {
                try { RunExtrasWatch(); }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : vérification des nouveaux DLC et patchs interrompue."); }
                finally { Interlocked.Exchange(ref _extrasWatchRunning, 0); }
            });
        }

        private void RunExtrasWatch()
        {
            var profile = _settings.GetActiveProfile();
            if (profile == null) return;

            var sourceRoots = profile.GetSources();
            if (sourceRoots.Count == 0) return;

            // La base Playnite se lit depuis le thread UI : copie simple, puis on travaille hors UI.
            var games = UiHelper.OnUi(() => _api.Database.Games
                .Where(g => g.PluginId == Id && g.IsInstalled && !string.IsNullOrEmpty(g.GameId))
                .Select(g => new { g.Id, g.GameId, g.Name })
                .ToList());

            bool changed = false;
            foreach (var g in games)
            {
                if (!_settings.NotifyNewExtras) break; // décochée pendant la vérification

                if (!_settings.HasExtrasMemory(g.GameId)) continue;
                bool alreadyWatched = _settings.HasSeenExtras(g.GameId);
                if (!alreadyWatched && _settings.GetInstalledSize(g.GameId) == null) continue; // jeu portable ou installé à la main

                var dir = ArchiveScanner.FindGameSourceDir(sourceRoots, g.GameId);
                if (dir == null) continue; // jeu absent, ou source débranchée

                var found = ArchiveScanner.FindExtraInstallers(dir, _settings.ExcludedExePatterns,
                    _settings.ExtraInstallerWords, _settings.InstallerWordsAnywhere);
                var allKeys = found.Installers.Concat(found.Manual).Select(x => x.Key).ToList();

                // Première vérification de ce jeu : on note l'existant, sans rien dire.
                if (!alreadyWatched)
                {
                    _settings.MarkExtrasSeen(g.GameId, allKeys);
                    changed = true;
                    continue;
                }

                var installed = _settings.GetInstalledExtras(g.GameId);
                var seen = _settings.GetSeenExtras(g.GameId);

                // Seuls les installeurs RECONNUS comptent pour la notification (les autres se lancent à la main).
                var fresh = found.Installers
                    .Where(x => !installed.Contains(x.Key) && !seen.Contains(x.Key))
                    .ToList();

                // Tout ce qui est vu est noté : la notification ne reviendra pas pour ces fichiers.
                if (allKeys.Any(k => !seen.Contains(k)))
                {
                    _settings.MarkExtrasSeen(g.GameId, allKeys);
                    changed = true;
                }

                if (fresh.Count > 0)
                {
                    ArchiveLog.Info($"Nouveaux extras détectés pour '{g.Name}' ({g.GameId}) : " +
                                    string.Join(", ", fresh.Select(x => x.Key)) + ".");
                    PublishNewExtrasNotification(g.Id, g.Name, fresh.Count(x => x.IsDlc), fresh.Count(x => !x.IsDlc));
                }
            }

            // Une seule écriture disque pour tout le lot (sur le thread UI, comme les autres sauvegardes).
            if (changed)
            {
                try { UiHelper.OnUi(() => _settings.Save()); }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : sauvegarde des extras vus impossible."); }
            }
        }

        // « 2 nouveaux DLC et 1 nouveau patch », « 1 nouveau DLC », « 3 nouveaux patchs »…
        private static string DescribeNewExtras(int dlcCount, int patchCount)
        {
            var parts = new List<string>();
            if (dlcCount > 0) parts.Add(dlcCount + (dlcCount > 1 ? " nouveaux DLC" : " nouveau DLC"));
            if (patchCount > 0) parts.Add(patchCount + (patchCount > 1 ? " nouveaux patchs" : " nouveau patch"));
            return string.Join(" et ", parts);
        }

        // Une notification par jeu. Le clic ouvre la fenêtre « DLC et patchs » de CE jeu.
        private void PublishNewExtrasNotification(Guid gameGuid, string gameName, int dlcCount, int patchCount)
        {
            string id = "ArchiveStore_NewExtras_" + gameGuid;
            string text = $"ArchiveStore : {DescribeNewExtras(dlcCount, patchCount)} pour '{gameName}'. " +
                          "Cliquez pour choisir ce que vous voulez installer.";

            // Exécuté par Playnite au clic, sur le thread UI. Le jeu est relu à ce moment-là (il a pu changer).
            Action onClick = () =>
            {
                try
                {
                    _api.Notifications.Remove(id);
                    var game = _api.Database.Games.Get(gameGuid);
                    if (game == null) return;
                    new ArchiveInstallController(game, _settings, _api).InstallExtrasFromNotification();
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "ArchiveStore : ouverture de la fenêtre DLC et patchs depuis la notification impossible.");
                }
            };

            // La collection de notifications appartient au thread UI : BeginInvoke (comme Notify).
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _api.Notifications.Remove(id);
                _api.Notifications.Add(new NotificationMessage(id, text, NotificationType.Info, onClick));

                // Toast en plus (si la case est cochée). Il ne se clique pas : il renvoie à la notification de Playnite.
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.NewExtras,
                    $"{DescribeNewExtras(dlcCount, patchCount)} pour '{gameName}'.",
                    "Cliquez sur la notification de Playnite pour choisir ce que vous voulez installer.");
            }));
        }

        // Copie les images trouvées vers les propriétés GameMetadata correspondantes.
        // Internal static : réutilisé par le fournisseur de métadonnées ci-dessous.
        internal static void ApplyArtwork(GameMetadata meta, ArtworkFiles art)
        {
            if (meta == null || art == null) return;
            if (art.IconPath != null) meta.Icon = new MetadataFile(art.IconPath);
            if (art.CoverPath != null) meta.CoverImage = new MetadataFile(art.CoverPath);
            if (art.BackgroundPath != null) meta.BackgroundImage = new MetadataFile(art.BackgroundPath);
        }

        // ==================================================================
        // MÉTADONNÉES À LA DEMANDE (jaquettes / icônes)
        // ==================================================================
        // GetGames ne fournit les images qu'à l'IMPORT d'un jeu. Pour les jeux déjà
        // présents (ou pour appliquer des images ajoutées plus tard), Playnite
        // interroge ce fournisseur quand l'utilisateur lance « Télécharger les
        // métadonnées » en choisissant la source de cette bibliothèque.
        public override LibraryMetadataProvider GetMetadataDownloader()
            => new ArchiveMetadataProvider(_settings);

        // ==================================================================
        // SCÉNARIO 2 — INSTALLATION
        // ==================================================================
        public override IEnumerable<InstallController> GetInstallActions(GetInstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new ArchiveInstallController(args.Game, _settings, _api);
        }

        // ==================================================================
        // SCÉNARIO 3 — DÉSINSTALLATION
        // ==================================================================
        public override IEnumerable<UninstallController> GetUninstallActions(GetUninstallActionsArgs args)
        {
            if (args.Game.PluginId != Id) yield break;
            yield return new ArchiveUninstallController(args.Game, _settings, _api);
        }

        // ==================================================================
        // SCÉNARIO 4 — ROBUSTESSE AU LANCEMENT
        // ==================================================================
        // Appelé par Playnite juste AVANT de lancer un jeu. Si l'exécutable
        // lié a disparu, on prévient clairement (au lieu de laisser une erreur
        // technique) et on annule le lancement.
        public override void OnGameStarting(OnGameStartingEventArgs args)
        {
            try
            {
                if (args.Game == null || args.Game.PluginId != Id) return;

                var action = args.SourceAction;
                if (action == null || action.Type != GameActionType.File) return;

                var exe = action.Path;
                // Chemin contenant une variable Playnite ({InstallDir}…) : on ne sait pas le vérifier ici.
                if (string.IsNullOrEmpty(exe) || exe.Contains("{")) return;

                if (!File.Exists(exe))
                {
                    // EXÉCUTABLE QUI A CHANGÉ : avant de déclarer le jeu cassé, on cherche dans son dossier
                    // d'installation. Si on a trouvé (ou fait choisir) un nouvel exécutable, le bouton « Jouer » est
                    // déjà mis à jour. Ce lancement-ci est annulé, car le SDK ne permet pas de changer la
                    // cible d'un lancement en cours : l'utilisateur reclique sur Jouer (la notification le dit).
                    if (TryRecoverMissingExe(args.Game, exe))
                    {
                        args.CancelStartup = true;
                        return;
                    }

                    string exeMissingMsg =
                        $"ArchiveStore : l'exécutable de '{args.Game.Name}' est introuvable ({exe}). " +
                        "Clic droit sur le jeu → « Réassocier l'exécutable (ArchiveStore) ».";
                    _api.Notifications.Add(new NotificationMessage("ArchiveStore_ExeMissing_" + args.Game.Id, exeMissingMsg, NotificationType.Error));
                    ToastHelper.ShowError(_api, _settings, exeMissingMsg);
                    args.CancelStartup = true;
                }
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : vérification de l'exécutable impossible.");
            }
        }

        // Cherche un exécutable de remplacement pour un jeu dont l'exécutable lié a disparu
        // (jeu mis à jour, exécutable renommé…).
        //   • UN SEUL candidat dans le dossier d'installation : on le prend (comme la détection automatique,
        //     ce choix n'est pas mémorisé) ;
        //   • PLUSIEURS candidats : on ne devine pas, la fenêtre de choix de l'exécutable s'ouvre (le choix de
        //     l'utilisateur, lui, est mémorisé comme avec « Réassocier l'exécutable ») ;
        //   • AUCUN candidat, dossier absent, choix annulé ou erreur : on renvoie faux et l'appelant garde
        //     son message d'erreur habituel.
        // Retourne vrai SEULEMENT si le bouton « Jouer » a été mis à jour (la notification est alors déjà envoyée).
        // Cette notification n'est liée à aucune case des réglages : elle explique pourquoi le lancement a été
        // annulé, elle s'affiche donc toujours. Ne lève jamais d'exception.
        private bool TryRecoverMissingExe(Game game, string oldExe)
        {
            try
            {
                string installDir = game.InstallDirectory;
                if (string.IsNullOrEmpty(installDir) || !Directory.Exists(installDir)) return false;

                var candidates = ArchiveScanner.FindGameExes(installDir, _settings.ExcludedExePatterns);
                if (candidates == null || candidates.Count == 0) return false;

                string chosen;
                bool userChoice = false;
                if (candidates.Count == 1)
                {
                    chosen = candidates[0];
                }
                else
                {
                    // Plusieurs possibilités : fenêtre de choix (thread UI), même style et même manette que partout.
                    chosen = UiHelper.OnUi<string>(() =>
                    {
                        var sel = new ExeSelectorDialog(installDir, _settings.ExcludedExePatterns)
                        { Owner = _api.Dialogs.GetCurrentAppWindow() };
                        return sel.ShowDialog() == true ? sel.SelectedExePath : null;
                    });

                    userChoice = !string.IsNullOrEmpty(chosen);
                    if (!userChoice)
                    {
                        ArchiveLog.Info($"Exécutable de '{game.Name}' introuvable ({oldExe}) : choix d'un remplaçant annulé.");
                        return false;
                    }
                }

                ArchiveLog.Info($"Exécutable de '{game.Name}' introuvable ({oldExe}) : remplacé par '{chosen}'" +
                                (userChoice ? " (choix de l'utilisateur)." : " (seul candidat trouvé)."));

                // Base Playnite + notification : thread UI.
                UiHelper.OnUi(() =>
                {
                    GameActionHelper.SetPlayAction(_api, game.Id, chosen);

                    // Seul un choix explicite est mémorisé. Un échec d'écriture des réglages ne doit pas
                    // défaire le changement du bouton « Jouer », déjà fait.
                    if (userChoice)
                    {
                        try { _settings.RememberExe(game.GameId, installDir, chosen); }
                        catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mémorisation de l'exécutable impossible."); }
                    }

                    var id = "ArchiveStore_ExeChanged_" + game.Id;
                    _api.Notifications.Remove(id);
                    _api.Notifications.Add(new NotificationMessage(
                        id,
                        $"ArchiveStore : l'exécutable de « {game.Name} » a changé : " +
                        $"{Path.GetFileName(oldExe)} → {Path.GetFileName(chosen)}. Cliquez de nouveau sur Jouer.",
                        NotificationType.Info));
                });
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : recherche d'un exécutable de remplacement impossible.");
                return false;
            }
        }

        // ==================================================================
        // SCÉNARIO 4 — MENU CONTEXTUEL (Bureau : clic droit ; Plein écran : sous le jeu → Extensions)
        // ==================================================================
        // Avec Argent en Plein écran (HostsGameActions) : ces actions sont sur la fiche du jeu et dans le menu Options de la
        // bibliothèque (voir GameActionsForArgent), plus sous « Extensions ». Mode Bureau : rien ne change.
        public override IEnumerable<GameMenuItem> GetGameMenuItems(GetGameMenuItemsArgs args)
        {
            if (ArgentBridge.HostsGameActions(_api)) yield break;

            foreach (var action in GameActions(args.Games))
            {
                var run = action.Value;
                yield return new GameMenuItem
                {
                    Description = action.Key + " (ArchiveStore)",
                    Action = _ => run()
                };
            }
        }

        // Les actions du menu du jeu (texte, action), pour les jeux choisis. Les mêmes pour Playnite et pour Argent.
        private List<KeyValuePair<string, Action>> GameActions(IEnumerable<Game> games)
        {
            var actions = new List<KeyValuePair<string, Action>>();
            var chosen = (games ?? Enumerable.Empty<Game>()).Where(g => g != null).ToList();

            // PLUSIEURS DESTINATIONS — jeux ArchiveStore PAS ENCORE installés : on propose de leur choisir à
            // l'avance une destination (ex. un vieux jeu sur le disque Windows). Le menu ne lit RIEN sur le
            // disque : il s'affiche à chaque clic droit. Il n'apparaît que si le profil actif a au moins
            // deux destinations (avec une seule, il n'y a rien à choisir).
            var activeProfile = _settings.GetActiveProfile();
            if (activeProfile != null && activeProfile.GetDestinations().Count > 1)
            {
                var notInstalled = chosen
                    .Where(g => g.PluginId == Id && !g.IsInstalled && !string.IsNullOrEmpty(g.GameId))
                    .ToList();

                if (notInstalled.Count > 0)
                    actions.Add(new KeyValuePair<string, Action>("Choisir la destination d'installation", () => ChooseDestination(notInstalled)));
            }

            var targets = chosen
                .Where(g => g.PluginId == Id
                         && g.IsInstalled
                         && !string.IsNullOrEmpty(g.InstallDirectory)
                         && Directory.Exists(g.InstallDirectory))
                .ToList();

            if (targets.Count == 0) return actions;

            actions.Add(new KeyValuePair<string, Action>("Réassocier l'exécutable", () =>
            {
                foreach (var game in targets)
                    ReassociateExe(game);
            }));

            // DLC et patchs après coup : un seul jeu à la fois (une fenêtre de choix par jeu, ce serait
            // confus avec plusieurs jeux sélectionnés). La recherche se fait AU CLIC, pas ici : le menu
            // s'affiche à chaque clic droit, il ne doit rien lire sur le disque.
            if (targets.Count == 1)
            {
                var target = targets[0];
                actions.Add(new KeyValuePair<string, Action>("Installer les DLC et patchs",
                    () => new ArchiveInstallController(target, _settings, _api).InstallExtrasFromMenu()));
            }
            return actions;
        }

        // Pour Argent : les actions d'UN jeu (fiche du jeu, menu Options de la bibliothèque).
        private IList<object> GameActionsForArgent(Guid gameId)
        {
            var lines = new List<object>();
            var game = _api.Database.Games.Get(gameId);
            if (game == null || game.PluginId != Id) return lines;
            foreach (var action in GameActions(new[] { game }))
                lines.Add(ArgentBridge.Action(action.Key, action.Value));
            return lines;
        }

        // Appelé depuis un menu : on est sur le thread UI.
        // Affecte une destination à l'avance à un ou plusieurs jeux pas encore installés.
        // Rien n'est installé ni créé ici : on mémorise seulement le choix (PluginSettings.ChosenDestinations).
        private void ChooseDestination(List<Game> games)
        {
            var profile = _settings.GetActiveProfile();
            if (profile == null || games == null || games.Count == 0) return;

            var destinations = profile.GetDestinations();
            if (destinations.Count < 2) return;

            // Pas de calcul d'espace libre ici (on est sur le thread UI : un disque réseau lent le figerait).
            var choices = DestinationChoice.FromEntries(destinations, false);

            // Destination actuelle de chaque jeu : si elle est la même pour tous, on la présélectionne.
            string common = null;
            bool same = true;
            foreach (var g in games)
            {
                bool unused;
                string root = _settings.ResolveDestinationRoot(profile, g.GameId, out unused);
                if (common == null) common = root;
                else if (!ArchiveScanner.SamePath(common, root)) { same = false; break; }
            }

            var dlg = new DestinationPickerDialog(games.Select(g => g.Name).ToList(), choices, same ? common : null)
            { Owner = _api.Dialogs.GetCurrentAppWindow() };
            if (dlg.ShowDialog() != true || string.IsNullOrEmpty(dlg.SelectedRoot)) return;

            // Choix identique à la destination par défaut : l'entrée mémorisée est simplement retirée.
            _settings.SetChosenDestinations(games.Select(g => g.GameId), dlg.SelectedRoot, destinations[0].FolderPath);

            string label = choices.First(c => ArchiveScanner.SamePath(c.FolderPath, dlg.SelectedRoot)).Label;
            ArchiveLog.Info($"Destination choisie pour {games.Count} jeu(x) ({string.Join(", ", games.Select(g => g.GameId))}) : '{dlg.SelectedRoot}'.");
            string destChosenMsg =
                (games.Count > 1
                    ? $"ArchiveStore : {games.Count} jeux seront installés dans « {label} »."
                    : $"ArchiveStore : '{games[0].Name}' sera installé dans « {label} ».") +
                " Rien n'est encore installé : cliquez sur Installer quand vous voulez.";
            // Réponse : fenêtre (par défaut) ou notification et toast (réglage « Réponses à tes actions »).
            ActionReply.Show(_settings, ReplyCategory.Destination, "Destination mémorisée", destChosenMsg, false, () =>
            {
                if (!_settings.IsNotifyEnabled(NotifyCategory.MenuConfirmations)) return;
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_DestChosen", destChosenMsg, NotificationType.Info));
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.MenuConfirmations, destChosenMsg);
            });
        }

        // Appelé depuis un menu : on est sur le thread UI.
        private void ReassociateExe(Game game)
        {
            // Dossier sans aucun exécutable de jeu : la fenêtre serait une liste
            // vide sans issue, on explique plutôt le problème.
            if (ArchiveScanner.FindGameExes(game.InstallDirectory, _settings.ExcludedExePatterns).Count == 0)
            {
                // Réponse : fenêtre (par défaut) ou notification d'erreur et toast (réglage « Réponses à tes actions »).
                string noExeMsg = $"ArchiveStore : aucun exécutable de jeu trouvé dans « {game.InstallDirectory} ».";
                ActionReply.Show(_settings, ReplyCategory.Reassociate, "Aucun exécutable", noExeMsg, true, () =>
                {
                    _api.Notifications.Add(new NotificationMessage("ArchiveStore_ReassocNone_" + game.Id, noExeMsg, NotificationType.Error));
                    ToastHelper.ShowError(_api, _settings, noExeMsg);
                });
                return;
            }

            // On pré-sélectionne l'exe actuellement mémorisé (s'il y en a un), pas le plus gros.
            var current = _settings.ResolveRememberedExe(game.GameId, game.InstallDirectory);

            var sel = new ExeSelectorDialog(game.InstallDirectory, _settings.ExcludedExePatterns, current)
            { Owner = _api.Dialogs.GetCurrentAppWindow() };

            if (sel.ShowDialog() != true || string.IsNullOrEmpty(sel.SelectedExePath))
                return;

            GameActionHelper.SetPlayAction(_api, game.Id, sel.SelectedExePath);

            // MÉMORISATION : ce choix survivra aux scans, redémarrages et réinstallations.
            // Un échec d'écriture des réglages ne doit pas annuler le changement déjà fait.
            try { _settings.RememberExe(game.GameId, game.InstallDirectory, sel.SelectedExePath); }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mémorisation de l'exécutable impossible."); }

            string reassocMsg = $"ArchiveStore : '{game.Name}' est maintenant lancé via {Path.GetFileName(sel.SelectedExePath)}.";
            ActionReply.Show(_settings, ReplyCategory.Reassociate, "Exécutable réassocié", reassocMsg, false, () =>
            {
                if (!_settings.IsNotifyEnabled(NotifyCategory.MenuConfirmations)) return;
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_Reassoc_" + game.Id, reassocMsg, NotificationType.Info));
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.MenuConfirmations, reassocMsg);
            });
        }

        // ==================================================================
        // MENU PRINCIPAL — bascule de profil (Desktop uniquement, limite de Playnite)
        // ==================================================================
        public override IEnumerable<MainMenuItem> GetMainMenuItems(GetMainMenuItemsArgs args)
        {
            // Réglages « manette » : une entrée directement sous « Extensions » (MenuSection "@"), en mode Bureau
            // comme en mode Plein écran. L'écran de réglages classique (mode Bureau) reste disponible.
            // Avec le thème Argent (Compatibles Argent) : pas d'entrée, les réglages sont dans Personnalisation d'Argent.
            if (!ArgentBridge.IsHosted(_api))
            {
                yield return new MainMenuItem
                {
                    Description = "ArchiveStore — Réglages",
                    MenuSection = "@",
                    Action = _ => OpenConsoleSettings()
                };
            }

            const string section = "Library|ArchiveStore";
            if (_settings.Profiles != null && _settings.Profiles.Count > 1)
            {
                foreach (var p in _settings.Profiles)
                {
                    bool active = p.Name == _settings.ActiveProfileName;
                    var name = p.Name;
                    yield return new MainMenuItem
                    {
                        Description = (active ? "● " : "   ") + name,
                        MenuSection = section + "|Basculer le profil",
                        Action = _ =>
                        {
                            _settings.ActiveProfileName = name;
                            _settings.Save(); // sans ça, le choix serait perdu au redémarrage
                            string switchMsg =
                                $"ArchiveStore : profil '{name}' activé. " +
                                "Lancez une mise à jour de la bibliothèque Playnite pour rescanner.";
                            ActionReply.Show(_settings, ReplyCategory.Profile, "Profil activé", switchMsg, false, () =>
                            {
                                if (!_settings.IsNotifyEnabled(NotifyCategory.MenuConfirmations)) return;
                                _api.Notifications.Add(new NotificationMessage("ArchiveStore_ProfileSwitch", switchMsg, NotificationType.Info));
                                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.MenuConfirmations, switchMsg);
                            });
                        }
                    };
                }
            }
        }

        // ==================================================================
        // RÉGLAGES « MANETTE » (fenêtre console) — ouverte par le menu Extensions
        //
        // PÉDAGOGIE : même cycle que l'écran de réglages classique (ISettings) :
        //   BeginEdit()  → copie de sécurité des réglages ;
        //   la fenêtre modifie les réglages EN DIRECT ;
        //   « Enregistrer » → EndEdit() (nettoyage + écriture sur le disque) ; sinon CancelEdit() (retour en arrière).
        // Aucune exception ne doit remonter vers Playnite : on journalise et on annule proprement.
        // ==================================================================
        private void OpenConsoleSettings()
        {
            try
            {
                _settings.BeginEdit();

                var dialog = new ConsoleSettingsWindow(_settings, _api);
                try { dialog.Owner = _api.Dialogs.GetCurrentAppWindow(); }
                catch (Exception) { /* pas de fenêtre propriétaire : la fenêtre se centre sur l'écran */ }

                if (dialog.ShowDialog() == true)
                    _settings.EndEdit();
                else
                    _settings.CancelEdit();
            }
            catch (Exception ex)
            {
                try { _settings.CancelEdit(); } catch (Exception) { }
                ArchiveLog.Error("Réglages manette : " + ex);
                _api.Dialogs.ShowErrorMessage(
                    "La fenêtre de réglages n'a pas pu s'ouvrir : " + ex.Message +
                    "\n\nLes réglages n'ont pas été modifiés. Détails dans archivestore.log.",
                    "ArchiveStore");
            }
        }
    }

    // ======================================================================
    // ArchiveMetadataProvider — fournit icône / jaquette / fond d'un jeu à la
    // demande, à partir des images placées dans son dossier d'archive.
    //
    // PÉDAGOGIE : Playnite appelle GetMetadata(game) pour chaque jeu concerné.
    // On retrouve le dossier source du jeu (profil actif + GameId), on y
    // cherche les images, et on les renvoie dans un GameMetadata. Les champs
    // laissés à null ne modifient rien dans Playnite.
    // ======================================================================
    public class ArchiveMetadataProvider : LibraryMetadataProvider
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly PluginSettings _settings;

        public ArchiveMetadataProvider(PluginSettings settings)
        {
            _settings = settings;
        }

        public override GameMetadata GetMetadata(Game game)
        {
            // On renvoie un GameMetadata VIDE (et non null) quand il n'y a rien à
            // apporter : plus prudent, Playnite n'a alors aucun cas "null" à gérer.
            var result = new GameMetadata();
            try
            {
                var profile = _settings.GetActiveProfile();
                if (profile == null || game == null || string.IsNullOrEmpty(game.GameId))
                    return result;

                // PLUSIEURS SOURCES : le dossier du jeu est cherché dans chacune, dans l'ordre.
                var dir = ArchiveScanner.FindGameSourceDir(profile.GetSources(), game.GameId);
                if (dir == null) return result;

                ArchiveStorePlugin.ApplyArtwork(result, ArchiveScanner.FindArtwork(dir));
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : recherche des images impossible.");
            }
            return result;
        }

        // Rien à libérer : le fournisseur ne garde aucune ressource ouverte.
        public override void Dispose() { }
    }

    // ======================================================================
    // ArchiveInstallController — signature officielle : void Install(InstallActionArgs)
    //
    // PÉDAGOGIE — comment Playnite pilote une installation :
    // 1. Playnite appelle Install() SUR LE THREAD UI et marque le jeu
    //    "en cours d'installation".
    // 2. Le jeu reste dans cet état tant que le contrôleur n'a pas signalé
    //    la fin avec InvokeOnInstalled(...) (succès) ou
    //    InvokeOnInstallationCancelled(...) (annulation / échec).
    // 3. Si Install() LÈVE une exception, Playnite l'affiche et remet l'état.
    //
    // Notre travail est long (copie de dizaines de Go) : Install() fait donc
    // seulement les vérifications rapides, lance le travail en arrière-plan
    // (Task.Run) puis REND LA MAIN tout de suite. Le thread UI reste libre
    // → les fenêtres (progression, dialogues) restent fluides.
    // ======================================================================
    public partial class ArchiveInstallController : InstallController
    {
        // FailedKept = l'installeur a échoué MAIS on garde le dossier de destination tel quel
        // (pas de nettoyage), pour pouvoir constater ce qu'il contient. Voir RunSilentInstaller.
        // Abandoned = installation MANUELLE (cas ambigu) refusée, échouée ou introuvable : on s'arrête
        // SANS RIEN SUPPRIMER (on ne sait pas ce que l'installeur a écrit), voir RunManualInstaller.
        private enum StepResult { Ok, Cancelled, Failed, FailedKept, Abandoned }

        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI _api;

        // Identifiant de la notification de progression (une seule par jeu).
        private string ProgressNotificationId => "ArchiveStore_Progress_" + Game.Id;

        // Vrai si Playnite est en mode Plein écran (lu sur le thread UI au début de Install, puis réutilisé
        // par le thread d'arrière-plan : une simple valeur, écrite avant le démarrage de la tâche).
        private volatile bool _isFullscreen;

        // Vrai si le dossier de destination EXISTAIT DÉJÀ (et n'était pas vide) quand l'utilisateur a choisi
        // « Écraser » ou « Utiliser tel quel ». Dans ce cas le plugin ne supprime RIEN, jamais : ni nettoyage
        // en cas d'échec, ni suppression « du dossier partiel ». Écrit avant l'étape C, lu par la copie.
        private volatile bool _destinationExisting;

        public ArchiveInstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            _settings = settings;
            _api = api;
        }

        public override void Install(InstallActionArgs args)
        {
            // --- Vérifications rapides, sur le thread UI. Une exception ici est
            //     affichée par Playnite et l'état du jeu est remis proprement. ---
            var profile = _settings.GetActiveProfile();
            if (profile == null)
                throw new Exception("ArchiveStore : aucun profil actif configuré.");
            var sources = profile.GetSources();
            var destinations = profile.GetDestinations();
            if (sources.Count == 0 || destinations.Count == 0)
                throw new Exception($"ArchiveStore : le profil '{profile.Name}' n'a pas de Source/Destination configurées.");

            // PLUSIEURS SOURCES : on cherche le dossier du jeu dans chacune, dans l'ordre.
            var sourceGameDir = ArchiveScanner.FindGameSourceDir(sources, Game.GameId);
            if (sourceGameDir == null)
                throw new Exception(
                    $"ArchiveStore : le dossier source de '{Game.Name}' ({Game.GameId}) est introuvable dans le profil '{profile.Name}'. " +
                    "Une source est peut-être inaccessible (disque ou NAS débranché ou éteint). Sources du profil : " +
                    string.Join(" ; ", sources) + ".");

            // PLUSIEURS DESTINATIONS : celle choisie pour ce jeu (menu clic droit ou fenêtre d'installation),
            // sinon la première (par défaut). En mode sans fenêtre, c'est elle qui est utilisée telle quelle.
            bool destinationFallback;
            string destRoot = _settings.ResolveDestinationRoot(profile, Game.GameId, out destinationFallback);
            if (destinationFallback)
            {
                ArchiveLog.Warn($"La destination choisie pour '{Game.Name}' ne fait plus partie du profil '{profile.Name}' : " +
                                $"destination par défaut utilisée ('{destRoot}').");
                Notify("ArchiveStore_DestFallback",
                    $"ArchiveStore : la destination choisie pour '{Game.Name}' n'existe plus dans le profil '{profile.Name}'. " +
                    $"La destination par défaut est utilisée : {destRoot}.",
                    NotificationType.Info);
            }

            var destDir = Path.Combine(destRoot, Game.GameId);

            // (La vérification « destination déjà peuplée » se fait dans RunWorkflow, APRÈS le choix éventuel
            // d'une autre destination dans la fenêtre d'installation.)

            // MODE « CONSOLE » : les fenêtres (confirmation, progression, choix…) s'affichent dans les DEUX
            // modes, Bureau comme Plein écran (elles se commandent à la manette), si l'option
            // « Installation directe » est décochée. Option cochée : aucune fenêtre, tout est automatique.
            // On lit le mode ICI (thread UI) puis on le transmet au thread d'arrière-plan.
            bool interactive = !_settings.DirectInstall;

            // Le mode Plein écran sert seulement à AVERTIR, dans la fenêtre d'installation, qu'un installeur
            // non reconnu s'ouvre avec sa propre fenêtre (non commandable à la manette).
            _isFullscreen = _api.ApplicationInfo.Mode == ApplicationMode.Fullscreen;

            // canAsk = fenêtres POSSIBLES, même avec « Installation directe » : le CAS AMBIGU (installeur non
            // reconnu) s'en sert pour poser sa question, et les erreurs de destination pour s'afficher en
            // fenêtre en plus de la notification. Vrai dans les deux modes.
            bool canAsk = true;

            Task.Run(() => RunWorkflow(sourceGameDir, destDir, destinations, interactive, canAsk));
        }

        public override void Dispose() { }

        // ------------------------------------------------------------------
        // Déroulé complet A → B → C → D, sur un thread d'arrière-plan.
        // interactive = true  : fenêtres (confirmation, progression, choix de l'exe).
        // interactive = false : aucune fenêtre ; progression par NOTIFICATIONS,
        //                       mode et exécutable détectés automatiquement.
        // ------------------------------------------------------------------
        //
        // destinations : toutes les destinations du profil (la première est celle par défaut). destDir est la
        // destination PRÉVUE ; l'utilisateur peut en choisir une autre dans la fenêtre d'installation
        // (destDir est alors remplacé). Tant que destinationOwned est faux, la destination n'a pas été
        // vérifiée vide : AUCUN nettoyage n'est permis (on ne supprimerait pas un dossier déjà peuplé).
        private void RunWorkflow(string sourceGameDir, string destDir, List<DestinationEntry> destinations, bool interactive, bool canAsk)
        {
            bool destinationOwned = false;
            try
            {
                // Destination déjà peuplée ? Si une fenêtre peut proposer une autre destination, on attend
                // ce choix ; sinon on s'arrête tout de suite, avant toute analyse.
                bool windowMayChoose = interactive && destinations.Count > 1;

                // Disque de destination absent (débranché…) : si aucune fenêtre ne peut proposer une autre
                // destination, on s'arrête tout de suite. Sinon la fenêtre signale la destination absente
                // et grise Installer ; la vérification définitive est faite plus bas.
                if (!windowMayChoose && !ArchiveScanner.IsDriveReachable(destDir))
                {
                    FailDestinationUnreachable(destDir, destinations, canAsk);
                    return;
                }

                // Destination déjà peuplée. SANS fenêtre (« Installation directe »), on s'arrête tout de suite :
                // on n'écrase JAMAIS automatiquement. AVEC fenêtres, la question (Utiliser tel quel / Écraser /
                // Annuler) est posée plus bas, une fois la méthode (installeur ou copie) et la destination
                // définitive connues : ses avertissements en dépendent.
                if (!interactive && ArchiveScanner.HasForeignContent(destDir, _settings.SaveFolderNames))
                {
                    FailDestinationPopulated(destDir, destinations.Count > 1, canAsk);
                    return;
                }

                // Dossier du jeu rappelé dans les avertissements des extras lancés à la main.
                _destDirHint = destDir;

                // ÉTAPE A : analyse (lecture de fichiers → hors thread UI)
                var scan = new ScanResult
                {
                    FolderName = Game.GameId,
                    FullSourcePath = sourceGameDir,
                    DestinationDirectory = destDir
                };

                // DLC et patchs repérés dès l'analyse (installeur reconnu seulement) : les fenêtres
                // peuvent ainsi montrer la taille du jeu SEUL et celle des extras.
                ExtraScanResult extrasScan = null;
                long extrasBytes = 0;     // taille des dossiers de DLC et patchs (archives)
                long fullSourceSize = 0;  // taille de TOUT le dossier source (jeu + extras)

                ProgressWindow analysis = null;
                if (interactive)
                    analysis = OpenProgress(0, "Analyse du jeu…", "Recherche d'un installeur…", false, null);
                else
                    ShowProgress($"Analyse de '{Game.Name}'…", null);

                try
                {
                    // Les sous-dossiers d'extras (DLC, patchs, updates) sont ignorés : un installeur
                    // de DLC ne doit pas être pris pour l'installeur du jeu de base.
                    // Mots d'installeur de l'utilisateur et option « n'importe où dans le nom » : voir la
                    // détection du cas ambigu (ArchiveScanner.DetectInstaller).
                    ArchiveScanner.DetectInstaller(sourceGameDir, _settings.ExcludedExePatterns, scan,
                        _settings.ExtraInstallerWords, _settings.InstallerWordsAnywhere);

                    // TAILLE DU JEU + ESPACE LIBRE. La taille vient du cache si elle a déjà
                    // été calculée en arrière-plan (instantané), sinon on la calcule maintenant.
                    if (interactive) SetProgressStatus(analysis, "Calcul de la taille du jeu…");
                    scan.SourceSizeBytes = GetGameSize(sourceGameDir);
                    scan.FreeSpaceBytes = ArchiveScanner.GetFreeSpace(destDir);
                    fullSourceSize = scan.SourceSizeBytes;

                    // DLC ET PATCHS : seulement avec un installeur reconnu. Un jeu portable est copié
                    // EN ENTIER (extras compris), sans aucune détection.
                    if (IsRecognizedInstaller(scan.DetectedType))
                    {
                        if (interactive) SetProgressStatus(analysis, "Recherche des DLC et patchs…");
                        extrasScan = FindExtrasWithSize(sourceGameDir, out extrasBytes);

                        // La taille « du jeu » affichée exclut désormais les extras (le dossier source
                        // les contient, mais ils se comptent à part). Si l'utilisateur choisit finalement
                        // de COPIER le dossier, on remet la taille complète (voir plus bas).
                        if (extrasBytes > 0)
                            scan.SourceSizeBytes = Math.Max(0, fullSourceSize - extrasBytes);
                    }
                }
                finally { CloseProgress(analysis); } // sans effet si analysis est null

                // CAS AMBIGU : aucun installeur reconnu par signature, mais un fichier ressemble à un
                // installeur (setup, install… ou un mot ajouté dans les paramètres).
                bool ambiguous = scan.DetectedType == InstallerType.Unknown;

                // La fenêtre d'installation s'ouvre en mode « interactif », ET aussi pour le cas ambigu, même
                // avec « Installation directe » (seule exception à « aucune fenêtre »), dans les deux modes :
                // on NE COPIE JAMAIS À L'AVEUGLE un fichier qui ressemble à un installeur (copié comme un jeu,
                // il ferait lancer l'installation par le bouton Jouer).
                bool showInstallDialog = interactive || (ambiguous && canAsk);

                // Notification « Analyse… » (mode sans fenêtre) : elle ne doit pas rester pendant la question.
                if (showInstallDialog && !interactive) RemoveProgress();

                // ÉTAPE B : choix de la méthode
                InstallDialog.Mode mode;
                if (showInstallDialog)
                {
                    // PLUSIEURS DESTINATIONS : la fenêtre propose une liste (avec l'espace libre de chaque
                    // disque, calculé ICI en arrière-plan). Avec une seule destination : pas de liste.
                    List<DestinationChoice> choices = destinations.Count > 1
                        ? DestinationChoice.FromEntries(destinations)
                        : null;
                    string pickedRoot = null;

                    // Dialogue de confirmation (fenêtre → via le Dispatcher)
                    var chosen = UiHelper.OnUi<InstallDialog.Mode?>(() =>
                    {
                        var dlg = new InstallDialog(scan, _settings, choices, _isFullscreen)
                        { Owner = _api.Dialogs.GetCurrentAppWindow() };
                        if (dlg.ShowDialog() != true) return null;
                        pickedRoot = dlg.SelectedDestinationRoot;
                        return (InstallDialog.Mode?)dlg.SelectedMode;
                    });

                    if (chosen == null)
                    {
                        // Rien n'a été créé : pas de nettoyage à faire.
                        ArchiveLog.Info($"Installation de '{Game.Name}' ({Game.GameId}) annulée dans la fenêtre de confirmation.");
                        ReportCancelled();
                        return;
                    }
                    mode = chosen.Value;

                    // Destination choisie dans la fenêtre : elle remplace la destination prévue, et on la
                    // MÉMORISE pour ce jeu (si elle est différente de la destination par défaut du profil).
                    if (choices != null && !string.IsNullOrEmpty(pickedRoot))
                    {
                        var pickedDir = Path.Combine(pickedRoot, Game.GameId);
                        if (!ArchiveScanner.SamePath(pickedDir, destDir))
                        {
                            ArchiveLog.Info($"Destination de '{Game.Name}' choisie dans la fenêtre : '{pickedDir}' (prévue : '{destDir}').");
                            destDir = pickedDir;
                            scan.DestinationDirectory = destDir;
                            scan.FreeSpaceBytes = ArchiveScanner.GetFreeSpace(destDir);
                            _destDirHint = destDir;
                        }
                        try
                        {
                            string defaultRoot = destinations[0].FolderPath;
                            UiHelper.OnUi(() => _settings.SetChosenDestinations(new[] { Game.GameId }, pickedRoot, defaultRoot));
                        }
                        catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mémorisation de la destination impossible."); }
                    }
                }
                else
                {
                    // Détection automatique : installeur reconnu → silencieux, sinon copie.
                    // (Le cas ambigu ne peut pas arriver ici : il passe toujours par la fenêtre ci-dessus.)
                    mode = (scan.DetectedType == InstallerType.InnoSetup ||
                            scan.DetectedType == InstallerType.Nullsoft ||
                            scan.DetectedType == InstallerType.Msi)
                        ? InstallDialog.Mode.Installer
                        : InstallDialog.Mode.Copy;
                }

                // DESTINATION DÉFINITIVE. Un dossier VIDE (les dossiers de sauvegarde conservés lors d'une
                // désinstallation précédente ne comptent pas) est « à nous » : un nettoyage est permis en cas
                // d'échec. Un dossier DÉJÀ PEUPLÉ ne l'est jamais : on demande à l'utilisateur, et quoi qu'il
                // réponde on ne supprimera rien dedans.
                if (!ArchiveScanner.IsDriveReachable(destDir))
                {
                    FailDestinationUnreachable(destDir, destinations, canAsk);
                    return;
                }

                bool useAsIs = false;
                bool existingFolder = ArchiveScanner.HasForeignContent(destDir, _settings.SaveFolderNames);
                if (existingFolder)
                {
                    // Sans fenêtre : arrêt (déjà fait plus haut ; ceci protège aussi tout autre chemin d'arrivée).
                    if (!interactive)
                    {
                        FailDestinationPopulated(destDir, destinations.Count > 1, canAsk);
                        return;
                    }

                    var existingChoice = AskExistingFolder(destDir, mode, ambiguous && mode == InstallDialog.Mode.Installer);
                    if (existingChoice == ExistingFolderChoice.Cancel)
                    {
                        // Rien n'a été créé ni modifié : pas de nettoyage.
                        ArchiveLog.Info($"Installation de '{Game.Name}' annulée : le dossier '{destDir}' existe déjà et l'utilisateur a choisi « Annuler ». Rien n'est modifié ni supprimé.");
                        ReportCancelled();
                        return;
                    }

                    useAsIs = existingChoice == ExistingFolderChoice.UseAsIs;
                    ArchiveLog.Info($"Dossier existant '{destDir}' pour '{Game.Name}' : l'utilisateur a choisi " +
                                    (useAsIs ? "« Utiliser ce dossier tel quel » (rien n'est copié ni supprimé)."
                                             : "« Écraser » (installation par-dessus : même nom = remplacé, le reste est conservé, rien n'est supprimé)."));
                }

                // Ces deux valeurs gouvernent TOUS les nettoyages plus bas : voir CleanupIfOwned.
                _destinationExisting = existingFolder;
                destinationOwned = !existingFolder;

                // INSTALLATION MANUELLE : cas ambigu ET choix « Installer » (installeur lancé avec sa fenêtre).
                // (Jamais avec « tel quel » : aucun installeur n'est lancé.)
                bool manual = !useAsIs && ambiguous && mode == InstallDialog.Mode.Installer;

                ArchiveLog.Info($"Installation de '{Game.Name}' ({Game.GameId}) : méthode {(useAsIs ? "aucune (dossier existant utilisé tel quel)" : mode.ToString())}, type détecté {scan.DetectedType}" +
                                (string.IsNullOrEmpty(scan.DetectedInstallerPath) ? "" : $", installeur '{scan.DetectedInstallerPath}'") +
                                $", destination '{destDir}'.");

                // Copie (jeu portable) : tout le dossier est copié, extras compris. On remet donc la
                // taille COMPLÈTE (la fenêtre de confirmation affichait celle du jeu seul).
                if (mode != InstallDialog.Mode.Installer)
                    scan.SourceSizeBytes = fullSourceSize;

                // DLC ET PATCHS : question AVANT d'installer le jeu, pour que l'utilisateur choisisse
                // « avec ou sans » avant les longues minutes d'installation.
                // (Pas après une installation MANUELLE : aucun extra proposé ; le clic droit
                // « Installer les DLC et patchs » reste disponible ensuite.)
                ExtrasPlan extrasPlan = null;
                if (!useAsIs && mode == InstallDialog.Mode.Installer && !manual)
                {
                    bool extrasCancelled;
                    extrasPlan = PlanExtras(extrasScan, extrasBytes, scan, interactive, false, out extrasCancelled);
                    if (extrasCancelled)
                    {
                        // Rien n'a été créé : pas de nettoyage à faire.
                        ArchiveLog.Info($"Installation de '{Game.Name}' annulée à la question sur les DLC et patchs.");
                        ReportCancelled();
                        return;
                    }
                }

                // Vérification de l'espace libre (re-calculé : en mode fenêtre, le dialogue est
                // resté ouvert un moment). Rien n'est encore créé : on annule sans nettoyage.
                var freeNow = ArchiveScanner.GetFreeSpace(destDir);

                // Place nécessaire = le jeu, plus ses extras SI on va les installer.
                // « Tel quel » : rien n'est copié, donc aucune place nécessaire.
                long gameNeed = useAsIs ? 0 : scan.SourceSizeBytes;
                long extrasNeed = (extrasPlan != null && extrasPlan.Install) ? extrasPlan.ExtrasBytes : 0;

                // Le jeu rentre mais pas jeu + extras : on installe le jeu SEUL et on ignore les extras
                // (rien n'est supprimé, la source reste intacte), en le disant clairement.
                if (extrasNeed > 0 &&
                    !ArchiveScanner.HasEnoughSpace(gameNeed + extrasNeed, freeNow) &&
                    ArchiveScanner.HasEnoughSpace(gameNeed, freeNow))
                {
                    extrasPlan.Install = false;
                    extrasPlan.Summary = $" {extrasPlan.What} non installé(s) : espace insuffisant.";
                    ArchiveLog.Warn($"Extras de '{Game.Name}' ignorés : espace insuffisant pour le jeu et ses extras " +
                                    $"({DescribeSpace(gameNeed + extrasNeed, freeNow)}). Le jeu seul est installé.");
                    Notify("ArchiveStore_ExtrasNoSpace",
                        $"ArchiveStore : pas assez de stockage pour '{Game.Name}' et ses extras. " +
                        $"{DescribeSpace(gameNeed + extrasNeed, freeNow)} " +
                        $"Le jeu sera installé, mais {extrasPlan.What} {(extrasPlan.Selected.Count > 1 ? "sont ignorés" : "est ignoré")}.",
                        NotificationType.Info);
                    extrasNeed = 0;
                }

                long totalNeed = gameNeed + extrasNeed;
                if (!useAsIs && !ArchiveScanner.HasEnoughSpace(totalNeed, freeNow))
                {
                    ArchiveLog.Warn($"Installation de '{Game.Name}' annulée : espace insuffisant. " + DescribeSpace(totalNeed, freeNow));
                    Notify("ArchiveStore_NoSpace",
                        $"ArchiveStore : espace insuffisant pour '{Game.Name}'. " +
                        DescribeSpace(totalNeed, freeNow) +
                        $" Il manque environ {ArchiveScanner.FormatSize(ArchiveScanner.GetMissingSpace(totalNeed, freeNow))}. " +
                        "Libérez de la place puis réessayez.",
                        NotificationType.Error);
                    ReportCancelled();
                    return;
                }

                // En mode sans fenêtre, c'est l'UNIQUE endroit où l'on voit la taille et
                // l'espace libre avant que l'installation ne démarre : on l'écrit clairement.
                if (!interactive && !manual)
                    Notify("ArchiveStore_InstallInfo",
                        $"ArchiveStore : installation de '{Game.Name}'. " + DescribeSpace(totalNeed, freeNow),
                        NotificationType.Info);

                // ÉTAPE C : exécution
                //   manuelle (cas ambigu, « Installer ») : installeur lancé avec sa fenêtre, voir
                //   ArchiveInstallController.Manual.cs ; silencieuse : installeur reconnu ; sinon copie.
                // La veille de Windows est bloquée pendant toute cette étape (la plus longue), puis libérée
                // par le using, même en cas d'erreur ou d'annulation (voir SleepGuard).
                // « Tel quel » : on saute toute l'étape (rien n'est copié ni lancé).
                StepResult step = StepResult.Ok;
                if (!useAsIs)
                {
                    using (new SleepGuard("installation de '" + Game.Name + "'"))
                    {
                        if (manual)
                            step = RunManualInstaller(scan, destDir);
                        else if (mode == InstallDialog.Mode.Installer)
                            step = RunSilentInstaller(scan, destDir, interactive);
                        else
                            step = RunCopyInstall(sourceGameDir, destDir, scan.SourceSizeBytes, interactive);
                    }
                }

                if (step != StepResult.Ok)
                {
                    if (step == StepResult.Abandoned)
                    {
                        // Installation manuelle non réussie : on s'arrête SANS RIEN SUPPRIMER (on ne sait pas ce
                        // que l'installeur a écrit). Les messages ont déjà été donnés par RunManualInstaller.
                        ArchiveLog.Warn($"Installation manuelle de '{Game.Name}' abandonnée : aucune suppression, le jeu reste « à installer ».");
                        ReportCancelled();
                        return;
                    }

                    if (step == StepResult.FailedKept)
                    {
                        // Cas particulier : on ne supprime JAMAIS la destination sans le dire.
                        //   Mode avec fenêtres : on DEMANDE (avec le chemin exact sous les yeux).
                        //   Mode sans fenêtre         : on garde le dossier et la notification l'indique.
                        // Jamais pour un dossier qui existait déjà : on n'y supprime rien (voir CleanupIfOwned).
                        bool deletePartial = false;
                        if (interactive && destinationOwned)
                        {
                            deletePartial = UiHelper.OnUi<bool>(() =>
                                _api.Dialogs.ShowMessage(
                                    $"L'installation de '{Game.Name}' a échoué (détails : archivestore.log).\n\n" +
                                    $"Un dossier partiel existe peut-être encore :\n{destDir}\n\nLe supprimer maintenant ?",
                                    "ArchiveStore",
                                    MessageBoxButton.YesNo,
                                    MessageBoxImage.Question) == MessageBoxResult.Yes);
                        }

                        if (deletePartial)
                        {
                            ArchiveLog.Warn($"Installation de '{Game.Name}' non terminée : suppression du dossier '{destDir}' demandée par l'utilisateur.");
                            CleanupPartial(destDir);
                        }
                        else
                        {
                            ArchiveLog.Warn($"Installation de '{Game.Name}' non terminée (résultat : {step}) : " +
                                            $"dossier de destination '{destDir}' CONSERVÉ (pas de nettoyage), pour vérification.");
                        }
                    }
                    else
                    {
                        if (destinationOwned)
                            ArchiveLog.Warn($"Installation de '{Game.Name}' non terminée (résultat : {step}) : nettoyage du dossier de destination '{destDir}'.");
                        CleanupIfOwned(destDir, destinationOwned);
                    }
                    if (step == StepResult.Cancelled)
                        Notify("ArchiveStore_InstallCancelled", $"ArchiveStore : installation de '{Game.Name}' annulée.", NotificationType.Info);
                    ReportCancelled();
                    return;
                }

                // ÉTAPE D : finalisation — choix de l'exécutable principal
                var exes = ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns);
                if (exes.Count == 0)
                {
                    ArchiveLog.Error($"Installation de '{Game.Name}' : aucun exécutable de jeu trouvé dans '{destDir}' après l'installation.");
                    Notify("ArchiveStore_Err",
                        $"ArchiveStore : aucun exécutable de jeu trouvé dans '{destDir}' après l'installation. " +
                        "(L'installeur a peut-être ignoré le dossier imposé.) Installation annulée." +
                        (destinationOwned ? "" : " Le dossier existait déjà : rien n'y a été supprimé."),
                        NotificationType.Error);
                    CleanupIfOwned(destDir, destinationOwned);
                    ReportCancelled();
                    return;
                }

                // DLC et patchs : seulement si le jeu a été installé par un installeur (un jeu
                // portable est copié EN ENTIER, extras compris, sans aucune détection), et
                // seulement APRÈS avoir vérifié que le jeu de base est bien là (inutile de
                // lancer des extras sur une installation ratée). Lancés avant le choix de
                // l'exécutable : un patch peut remplacer l'exe, on relit donc la liste ensuite.
                // ProcessExtras ne lève jamais d'exception, et un échec des extras ne
                // supprime PAS le jeu de base, qui est installé et utilisable.
                string extrasSummary = "";

                // MÉMOIRE DES EXTRAS : la destination vient d'être créée à neuf, donc plus aucun extra
                // n'est installé (même si une ancienne installation en avait). On repart de zéro, en
                // marquant que la mémoire est ACTIVE pour ce jeu (voir PluginSettings.ExtrasMemoryMarker) ;
                // chaque extra installé ci-dessous sera ensuite noté. try/catch : une erreur de sauvegarde
                // des réglages ne doit pas gâcher l'installation qui vient de réussir.
                //
                // DOSSIER PRÉEXISTANT (« Écraser » / « tel quel ») : on ne sait PAS quels extras s'y trouvent déjà.
                // On efface donc toute ancienne mémoire SANS poser le marqueur : le plugin sait alors qu'il ne peut
                // pas connaître les extras installés et prévient l'utilisateur (comportement des jeux anciens).
                try
                {
                    if (destinationOwned) UiHelper.OnUi(() => _settings.ResetInstalledExtras(Game.GameId));
                    else UiHelper.OnUi(() => _settings.ForgetInstalledExtras(Game.GameId));
                }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : réinitialisation de la mémoire des extras impossible."); }

                if (!useAsIs && mode == InstallDialog.Mode.Installer && !manual)
                {
                    // La taille du jeu de base (avant les extras) sert à estimer l'avancement
                    // de la prochaine installation de ce jeu.
                    RememberInstalledSize(destDir);

                    // Les DLC et patchs présents dans la source à cet instant sont « déjà vus » (installés ou
                    // refusés) : seuls ceux qui apparaîtront plus tard déclencheront la notification de nouveautés.
                    MarkExtrasSeen(extrasScan);

                    extrasSummary = ProcessExtras(extrasPlan, destDir, interactive);

                    var exesAfterExtras = ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns);
                    if (exesAfterExtras.Count > 0) exes = exesAfterExtras;
                }

                // EXE MÉMORISÉ : si l'utilisateur a déjà choisi l'exe de ce jeu lors d'une
                // installation précédente (ou via « Réassocier »), on le retrouve et on ne
                // pose AUCUNE question. Résolution = chemin relatif + vérification que le
                // fichier existe toujours dans la nouvelle installation.
                var selectedExe = _settings.ResolveRememberedExe(Game.GameId, destDir);
                bool userChoice = false; // vrai seulement si l'utilisateur a choisi explicitement

                if (selectedExe != null)
                {
                    Notify("ArchiveStore_ExeRemembered",
                        $"ArchiveStore : exécutable mémorisé retrouvé pour '{Game.Name}' : {Path.GetFileName(selectedExe)}.",
                        NotificationType.Info);
                }
                else if (interactive || manual)
                {
                    // Installation MANUELLE (l'utilisateur est devant l'écran) : la fenêtre de
                    // choix de l'exécutable s'ouvre aussi avec « Installation directe », comme annoncé
                    // dans la question « terminée et réussie ? ».
                    selectedExe = UiHelper.OnUi<string>(() =>
                    {
                        var sel = new ExeSelectorDialog(destDir, _settings.ExcludedExePatterns)
                        { Owner = _api.Dialogs.GetCurrentAppWindow() };
                        return sel.ShowDialog() == true ? sel.SelectedExePath : null;
                    });
                    userChoice = !string.IsNullOrEmpty(selectedExe);
                }

                if (string.IsNullOrEmpty(selectedExe))
                {
                    // Détection automatique (mode sans fenêtre, ou sélection fermée sans choix) :
                    // le meilleur candidat selon l'heuristique du scanner. Ce choix automatique
                    // n'est PAS mémorisé : « Réassocier » (ou une meilleure détection plus tard)
                    // reste possible, et une réinstallation refera la même détection.
                    selectedExe = ArchiveScanner.PickBestExe(exes);
                    Notify("ArchiveStore_ExeAuto",
                        $"ArchiveStore : exécutable détecté automatiquement : {Path.GetFileName(selectedExe)}. " +
                        "(Clic droit sur le jeu → « Réassocier l'exécutable (ArchiveStore) » pour en changer.)",
                        NotificationType.Info);
                }

                // Mise à jour de la base Playnite + signal de fin : thread UI.
                UiHelper.OnUi(() =>
                {
                    GameActionHelper.SetPlayAction(_api, Game.Id, selectedExe);

                    // Mémorisation du choix explicite. try/catch OBLIGATOIRE : une erreur
                    // d'écriture des réglages ne doit surtout pas empêcher InvokeOnInstalled
                    // (le jeu resterait bloqué « en cours d'installation »), ni déclencher
                    // le nettoyage de l'installation qui vient de réussir.
                    if (userChoice)
                    {
                        try { _settings.RememberExe(Game.GameId, destDir, selectedExe); }
                        catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mémorisation de l'exécutable impossible."); }
                    }

                    InvokeOnInstalled(new GameInstalledEventArgs(
                        new GameInstallationData { InstallDirectory = destDir }));
                });

                // La taille affichée par Playnite devient celle du jeu INSTALLÉ (et non celle de l'installeur) : mesurée en
                // arrière-plan pour ne pas retarder la fin de l'installation.
                {
                    var sizeDbId = Game.Id;
                    var sizeGameId = Game.GameId;
                    var sizeDir = destDir;
                    Task.Run(() => InstalledSizeHelper.Refresh(_api, sizeDbId, sizeGameId, sizeDir));
                }

                string doneText = useAsIs
                    ? $"'{Game.Name}' : le dossier existant a été utilisé tel quel (rien n'a été copié ni supprimé)."
                    : $"'{Game.Name}' installé avec succès.{extrasSummary}";
                ArchiveLog.Info(doneText);
                Notify("ArchiveStore_InstallDone", doneText, NotificationType.Info);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : installation échouée.");
                ArchiveLog.Error($"Installation de '{Game.Name}' échouée : {ex.Message}");
                Notify("ArchiveStore_Err", $"ArchiveStore : échec de l'installation de '{Game.Name}' — {ex.Message}",
                    NotificationType.Error);
                // Nettoyage SEULEMENT si la destination a été vérifiée vide avant le début (voir plus haut).
                if (destinationOwned) CleanupPartial(destDir);
                ReportCancelled();
            }
            finally
            {
                // Quoi qu'il arrive (succès, échec, annulation), la notification de
                // progression ne doit pas rester affichée.
                RemoveProgress();
            }
        }

        // Écrit dans le journal l'état des disques (espace libre du disque des fichiers temporaires
        // de Windows et du disque de destination) et, si demandé, un inventaire de la destination :
        // taille totale et exécutables trouvés. Ne lève jamais d'exception.
        // Lecture seule : rien n'est modifié.
        private void LogDiskState(string moment, string destDir, bool withInventory)
        {
            try
            {
                string tempDir = Path.GetTempPath();
                ArchiveLog.Info($"État des disques {moment} : " +
                                $"libre sur le disque temporaire ('{tempDir}') = {DescribeFree(ArchiveScanner.GetFreeSpace(tempDir))}, " +
                                $"libre sur la destination = {DescribeFree(ArchiveScanner.GetFreeSpace(destDir))}.");

                if (!withInventory) return;

                if (!Directory.Exists(destDir))
                {
                    ArchiveLog.Warn($"Inventaire {moment} : le dossier '{destDir}' n'existe pas.");
                    return;
                }

                long size = ArchiveScanner.GetDirectorySize(destDir);
                var exes = ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns);
                var shown = exes.Take(5)
                                .Select(f => f.StartsWith(destDir, StringComparison.OrdinalIgnoreCase)
                                    ? f.Substring(destDir.Length).TrimStart('\\', '/')
                                    : f);
                ArchiveLog.Info($"Inventaire {moment} de '{destDir}' : taille {ArchiveScanner.FormatSize(size)}, " +
                                $"{exes.Count} exécutable(s) de jeu" +
                                (exes.Count > 0 ? " : " + string.Join(", ", shown) + (exes.Count > 5 ? ", …" : "") : "") + ".");
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Inventaire {moment} impossible : {ex.Message}");
            }
        }

        private static string DescribeFree(long free)
            => free >= 0 ? ArchiveScanner.FormatSize(free) : "indisponible";

        // Vrai si un installeur Inno Setup a TERMINÉ son travail puis planté en se fermant.
        // Conditions (toutes nécessaires) :
        //   1. "code" est un code de plantage Windows (NTSTATUS d'erreur : 0xC0000000 et au-dessus,
        //      comme 0xC000041D = « exception non gérée dans une fonction de rappel ») ;
        //   2. la fin du journal d'Inno Setup (créé par le plugin) contient
        //      « Installation process succeeded », ligne écrite après TOUS les fichiers.
        // "reason" explique le refus. Lecture seule, ne lève jamais d'exception.
        private static bool IsInnoCrashAtExit(int code, string innoLogPath, out string reason)
        {
            reason = null;

            if (unchecked((uint)code) < 0xC0000000u)
            {
                reason = $"le code {code} n'est pas un code de plantage Windows";
                return false;
            }

            if (string.IsNullOrEmpty(innoLogPath) || !File.Exists(innoLogPath))
            {
                reason = "journal Inno Setup introuvable";
                return false;
            }

            try
            {
                // FileShare.ReadWrite : au cas où un autre programme garderait le fichier ouvert.
                using (var fs = new FileStream(innoLogPath, FileMode.Open, FileAccess.Read,
                                               FileShare.ReadWrite | FileShare.Delete))
                {
                    // On ne lit que la FIN du fichier (le journal fait plusieurs Mo).
                    int tail = (int)Math.Min(fs.Length, 256 * 1024);
                    fs.Seek(-tail, SeekOrigin.End);

                    var buffer = new byte[tail];
                    int total = 0;
                    while (total < tail)
                    {
                        int n = fs.Read(buffer, total, tail - total);
                        if (n <= 0) break;
                        total += n;
                    }

                    string text = Encoding.UTF8.GetString(buffer, 0, total);
                    if (text.IndexOf("Installation process succeeded", StringComparison.Ordinal) < 0)
                    {
                        reason = "le journal Inno Setup ne contient pas « Installation process succeeded »";
                        return false;
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                reason = "lecture du journal Inno Setup impossible : " + ex.Message;
                return false;
            }
        }

        // « 2 min 05 s », « 1 h 12 min », « 8 s » : durée lisible pour le journal.
        private static string DescribeDuration(TimeSpan t)
        {
            if (t.TotalHours >= 1) return $"{(int)t.TotalHours} h {t.Minutes:00} min";
            if (t.TotalMinutes >= 1) return $"{t.Minutes} min {t.Seconds:00} s";
            return $"{(int)t.TotalSeconds} s";
        }

        // Taille du jeu : cache si dossier inchangé, sinon calcul (puis mise en cache).
        private long GetGameSize(string sourceGameDir)
        {
            long ticks = Directory.GetLastWriteTimeUtc(sourceGameDir).Ticks;
            var cached = _settings.GetCachedSize(Game.GameId, ticks);
            if (cached.HasValue) return cached.Value;

            long size = ArchiveScanner.GetDirectorySize(sourceGameDir);
            _settings.SetCachedSize(Game.GameId, ticks, size); // sauvegardé plus tard avec les autres réglages
            return size;
        }

        // Phrase « taille + espace libre » (notifications).
        private static string DescribeSpace(long size, long free)
        {
            var sizeText = size > 0 ? ArchiveScanner.FormatSize(size) : "taille indéterminée";
            var freeText = free >= 0
                ? ArchiveScanner.FormatSize(free) + " libres sur le disque de destination"
                : "espace libre indisponible";
            return $"Taille : {sizeText} — {freeText}.";
        }

        // ------------------------------------------------------------------
        // Exécution d'un installeur en mode TOTALEMENT silencieux.
        // ------------------------------------------------------------------
        private StepResult RunSilentInstaller(ScanResult scan, string destDir, bool interactive)
        {
            var a = _settings.SilentArguments ?? new SilentArgs();
            string file, arguments;

            switch (scan.DetectedType)
            {
                case InstallerType.InnoSetup:
                    file = scan.DetectedInstallerPath;
                    bool scopeCancelled;
                    string scopeArg = ResolveInnoScopeArg(interactive, out scopeCancelled);
                    if (scopeCancelled) return StepResult.Cancelled;
                    arguments = $"{a.InnoArgs}{scopeArg} /DIR=\"{destDir}\"";
                    break;
                case InstallerType.Nullsoft:
                    file = scan.DetectedInstallerPath;
                    // NSIS : /D= doit être le DERNIER argument, SANS guillemets.
                    arguments = $"{a.NsisArgs} /D={destDir}";
                    break;
                case InstallerType.Msi:
                    file = "msiexec.exe";
                    arguments = $"/i \"{scan.DetectedInstallerPath}\" {a.MsiArgs} TARGETDIR=\"{destDir}\"";
                    break;
                case InstallerType.Unknown:
                    ArchiveLog.Error($"Jeu de base '{Game.Name}' : type d'installeur indéterminé, exécution silencieuse impossible.");
                    Notify("ArchiveStore_Err",
                        "ArchiveStore : type d'installeur indéterminé, impossible de l'exécuter en silence. " +
                        "Utilisez « Copier le dossier » ou adaptez l'installeur (Inno Setup/NSIS/MSI).",
                        NotificationType.Error);
                    return StepResult.Failed;
                default:
                    ArchiveLog.Error($"Jeu de base '{Game.Name}' : aucun installeur à exécuter dans le dossier.");
                    Notify("ArchiveStore_Err",
                        "ArchiveStore : aucun installeur à exécuter dans ce dossier.",
                        NotificationType.Error);
                    return StepResult.Failed;
            }

            // Installeur Inno : on demande aussi son journal détaillé (voir ArchiveLog.WithInnoLog).
            string innoLog = null; // chemin du journal d'Inno Setup (null = pas de journal)
            if (scan.DetectedType == InstallerType.InnoSetup)
            {
                arguments = ArchiveLog.WithInnoLog(arguments, $"{Game.GameId}_jeu", out innoLog);
                if (innoLog != null) ArchiveLog.Info($"Journal Inno Setup du jeu de base : {innoLog}");
            }

            ArchiveLog.Info($"Jeu de base '{Game.Name}' — commande : {file} {arguments}");
            ArchiveLog.Info($"Dossier de travail : '{Path.GetDirectoryName(scan.DetectedInstallerPath) ?? ""}'.");

            ProgressWindow win = null;
            if (interactive)
                win = OpenProgress(0, "Installation en cours…", "Exécution de l'installeur silencieux…", false, null, false, true);
            else
                ShowProgress($"Installation de '{Game.Name}' en cours…", null);

            CancellationTokenSource monitorCts = null;
            Task monitorTask = null;

            try
            {
                Directory.CreateDirectory(destDir);

                // PAS de redirection de la sortie : lire stdout/stderr APRÈS
                // WaitForExit provoque un blocage (deadlock) dès que le tampon se remplit.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(scan.DetectedInstallerPath) ?? ""
                };

                LogDiskState("avant l'installeur", destDir, false);

                // SUIVI EN TEMPS RÉEL : en tâche de fond, on mesure ce que l'installeur écrit dans la
                // destination (voir StartInstallMonitor). La taille finale attendue vient d'une
                // installation précédente de CE jeu (0 = inconnue : pas de pourcentage).
                long baseline = ArchiveScanner.GetDirectorySize(destDir);
                long expectedTotal = _settings.GetInstalledSize(Game.GameId) ?? 0;
                monitorCts = new CancellationTokenSource();
                monitorTask = StartInstallMonitor(destDir, baseline, expectedTotal, win, monitorCts.Token);

                var chrono = System.Diagnostics.Stopwatch.StartNew();
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    ArchiveLog.Info($"Installeur lancé (processus {proc.Id}).");
                    proc.WaitForExit();
                    int code = proc.ExitCode;
                    ArchiveLog.Info($"Installeur terminé : code de retour {code} " +
                                    $"(0x{code:X8}), durée {DescribeDuration(chrono.Elapsed)}.");

                    // 3010 = succès MSI mais "redémarrage conseillé" : le jeu est installé.
                    bool abnormal = code != 0 && code != 3010;

                    // Inventaire complet seulement en cas de code anormal (il peut prendre quelques
                    // secondes sur un gros jeu) ; l'espace libre, lui, est toujours noté.
                    LogDiskState("après l'installeur", destDir, abnormal);

                    // PÉDAGOGIE — TOLÉRANCE D'UN PLANTAGE « À LA FERMETURE ».
                    // Certains installeurs GOG (Inno Setup) installent TOUT correctement puis plantent
                    // en se fermant (code 0xC000041D observé avec Cyberpunk 2077). On ne tolère que si
                    // TROIS preuves sont réunies : (1) code de plantage Windows, (2) le journal d'Inno
                    // contient « Installation process succeeded » (écrit seulement APRÈS tous les
                    // fichiers), (3) un exécutable de jeu existe dans la destination.
                    if (abnormal && scan.DetectedType == InstallerType.InnoSetup)
                    {
                        string why;
                        if (IsInnoCrashAtExit(code, innoLog, out why))
                        {
                            int exeCount = ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns).Count;
                            if (exeCount > 0)
                            {
                                ArchiveLog.Warn($"Jeu de base '{Game.Name}' : l'installeur a planté à la fermeture (code {code}) " +
                                                $"MAIS son journal indique une installation réussie et {exeCount} exécutable(s) de jeu sont présents : " +
                                                "installation considérée comme réussie.");
                                Notify("ArchiveStore_InstallerCrash",
                                    $"ArchiveStore : l'installeur de '{Game.Name}' a planté à la fermeture (code {code}), " +
                                    "mais l'installation est terminée. Vérifiez que le jeu se lance. Détails : archivestore.log.",
                                    NotificationType.Info);
                                return StepResult.Ok;
                            }
                            ArchiveLog.Warn($"Jeu de base '{Game.Name}' : tolérance refusée, aucun exécutable de jeu trouvé dans '{destDir}'.");
                        }
                        else
                        {
                            ArchiveLog.Warn($"Jeu de base '{Game.Name}' : tolérance refusée ({why}).");
                        }
                    }

                    if (abnormal)
                    {
                        ArchiveLog.Error($"Jeu de base '{Game.Name}' : échec, code de retour {code}. " +
                                         "Voir aussi le journal Inno Setup indiqué plus haut (fichiers inno_*.log).");

                        // PÉDAGOGIE — pourquoi garder le dossier ? Un installeur Inno Setup peut
                        // planter À LA FIN, alors que les fichiers du jeu sont déjà copiés. Avant,
                        // on supprimait tout dans ce cas, sans pouvoir vérifier. Pour Inno Setup,
                        // on CONSERVE désormais la destination (FailedKept) : l'utilisateur peut
                        // l'ouvrir, et l'inventaire ci-dessus dit ce qu'elle contient.
                        if (scan.DetectedType == InstallerType.InnoSetup)
                        {
                            // En mode avec fenêtres, une boîte de dialogue demandera ensuite s'il
                            // faut supprimer le dossier : la notification ne dit donc rien de définitif.
                            Notify("ArchiveStore_Err",
                                interactive
                                    ? $"ArchiveStore : l'installeur a retourné le code {code}. Détails : archivestore.log."
                                    : $"ArchiveStore : l'installeur a retourné le code {code}. Le dossier '{destDir}' " +
                                      "a été CONSERVÉ (non supprimé) pour vérification. Détails : archivestore.log.",
                                NotificationType.Error);
                            return StepResult.FailedKept;
                        }

                        Notify("ArchiveStore_Err",
                            $"ArchiveStore : l'installeur a retourné le code {code}. Détails : archivestore.log.",
                            NotificationType.Error);
                        return StepResult.Failed;
                    }
                }
                return StepResult.Ok;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                // 740 = ERROR_ELEVATION_REQUIRED : l'installeur exige les droits admin,
                // impossible sans fenêtre UAC (donc pas "silencieux").
                ArchiveLog.Error($"Jeu de base '{Game.Name}' : droits administrateur exigés (code 740).");
                Notify("ArchiveStore_Err",
                    "ArchiveStore : cet installeur exige les droits administrateur. Dans votre script Inno Setup, " +
                    "mettez PrivilegesRequired=lowest (ou lancez Playnite en administrateur).",
                    NotificationType.Error);
                return StepResult.Failed;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : échec installeur.");
                ArchiveLog.Error($"Jeu de base '{Game.Name}' : exception pendant l'installeur : {ex.GetType().Name} — {ex.Message}");
                Notify("ArchiveStore_Err", $"ArchiveStore : échec installeur — {ex.Message}", NotificationType.Error);
                return StepResult.Failed;
            }
            finally
            {
                StopInstallMonitor(monitorCts, monitorTask); // AVANT de fermer la fenêtre
                CloseProgress(win);
            }
        }

        // ------------------------------------------------------------------
        // SUIVI D'UN INSTALLEUR EXTERNE
        //
        // PÉDAGOGIE — pourquoi mesurer le dossier ? Un installeur (Inno Setup, GOG…) ne
        // communique pas son avancement au programme qui le lance. Mais il ÉCRIT des fichiers
        // dans la destination : en additionnant leur taille à intervalle régulier (réglage, moins ce qui
        // s'y trouvait déjà au départ), on sait ce qui a été écrit en temps réel.
        // Tourne dans sa propre tâche (Task.Run) : l'interface n'est jamais bloquée, et chaque
        // mise à jour de la fenêtre passe par BeginInvoke (règle d'or des threads WPF).
        //   win != null : fenêtre de progression mise à jour.
        //   win == null : (mode sans fenêtre) notification mise à jour au même rythme.
        // Ne lève jamais d'exception vers l'appelant.
        // ------------------------------------------------------------------
        private Task StartInstallMonitor(string destDir, long baseline, long expectedTotal,
                                         ProgressWindow win, CancellationToken token)
        {
            // Rythme choisi dans les réglages (2 à 10 s), pour la fenêtre ET pour la notification.
            // SÉCURITÉ : on attend au moins 3 fois la durée du dernier parcours. Sur un très gros jeu,
            // ou un disque lent, le suivi se ralentit tout seul au lieu de gêner l'installeur.
            int seconds = Math.Max(PluginSettings.MinProgressIntervalSeconds,
                          Math.Min(PluginSettings.MaxProgressIntervalSeconds, _settings.ProgressIntervalSeconds));
            int minWaitMs = seconds * 1000;

            return Task.Run(async () =>
            {
                try
                {
                    var chrono = System.Diagnostics.Stopwatch.StartNew();
                    double lastPassMs = 0;

                    // Affichage IMMÉDIAT (avant la première mesure, qui n'arrive qu'après le délai choisi) :
                    // la fenêtre dit tout de suite que ça travaille, au lieu de rester vide.
                    if (win != null)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            win.SetInstallProgress(0, expectedTotal, "Temps écoulé : 0 s")));
                    }

                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay((int)Math.Max(minWaitMs, lastPassMs * 3), token); // lève OperationCanceledException à l'arrêt

                        var pass = System.Diagnostics.Stopwatch.StartNew();
                        long written = Math.Max(0, ArchiveScanner.GetDirectorySize(destDir, token) - baseline);
                        lastPassMs = pass.Elapsed.TotalMilliseconds;

                        string elapsed = DescribeDuration(chrono.Elapsed);

                        if (win != null && !win.InBackground)
                        {
                            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                                win.SetInstallProgress(written, expectedTotal, "Temps écoulé : " + elapsed)));
                        }
                        else
                        {
                            // Même notification pour le jeu et pour les extras (l'étape en plus).
                            string step = _stepLabel;
                            string who = string.IsNullOrEmpty(step)
                                ? $"Installation de '{Game.Name}'"
                                : $"Installation de '{Game.Name}' : {step}";

                            if (written <= 0)
                            {
                                // Rien de nouveau mesuré (début, ou fichiers remplacés à l'identique) : on n'écrit
                                // pas « 0 o écrits », ce serait trompeur. Voir ProgressWindow.SetInstallProgress.
                                ShowProgress($"{who} en cours… ({elapsed})", null);
                            }
                            else
                            {
                                string estimate = expectedTotal > 0
                                    ? $"≈ {Math.Min(99L, written * 100 / expectedTotal)} % — " : "";
                                ShowProgress($"{who} — {estimate}{ArchiveScanner.FormatSize(written)} écrits ({elapsed})", null);
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Arrêt normal : l'installeur est terminé.
                }
                catch (Exception ex)
                {
                    logger.Warn(ex, "ArchiveStore : suivi de l'installation interrompu.");
                }
            });
        }

        // Arrête le suivi (et attend brièvement qu'il se termine). Ne lève jamais d'exception.
        private static void StopInstallMonitor(CancellationTokenSource cts, Task task)
        {
            if (cts == null) return;
            try
            {
                cts.Cancel();
                if (task != null) task.Wait(3000);
            }
            catch
            {
                // Volontairement muet : le suivi ne doit jamais casser une installation.
            }
            finally
            {
                cts.Dispose();
            }
        }

        // Mémorise la taille installée du jeu (pour estimer l'avancement la prochaine fois).
        // Ne lève jamais d'exception.
        private void RememberInstalledSize(string destDir)
        {
            try
            {
                long size = ArchiveScanner.GetDirectorySize(destDir);
                if (size <= 0) return;

                _settings.SetInstalledSize(Game.GameId, size);
                UiHelper.OnUi(() => _settings.Save());
                ArchiveLog.Info($"Taille installée de '{Game.Name}' mémorisée : {ArchiveScanner.FormatSize(size)} " +
                                "(sert à estimer l'avancement des prochaines installations).");
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Taille installée de '{Game.Name}' non mémorisée : {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Copie récursive.
        //   interactive : fenêtre de progression avec bouton Annuler.
        //   sinon       : notification de progression mise à jour toutes les ~3 s ;
        //                 un CLIC sur la notification propose d'annuler.
        // "total" = taille déjà calculée à l'étape A (évite un second parcours du dossier).
        // L'espace libre a, lui aussi, déjà été vérifié juste avant l'appel.
        // ------------------------------------------------------------------
        private StepResult RunCopyInstall(string source, string dest, long total, bool interactive)
        {
            var cts = new CancellationTokenSource();
            ProgressWindow win = null;

            if (interactive)
                win = OpenProgress(total, "Copie en cours…", "Préparation…", true, () => cts.Cancel(), _destinationExisting, true);
            else
                ShowProgress($"Copie de '{Game.Name}'… 0 % (cliquer ici pour annuler)", () => AskCancel(cts));

            // Mémoire de la dernière notification publiée (anti-inondation).
            var lastNotice = DateTime.UtcNow;
            var lastPercent = 0;

            try
            {
                // Le rappel s'exécute sur le thread qui copie : on le renvoie
                // vers le thread UI via BeginInvoke (asynchrone → la copie n'attend pas l'écran).
                var progress = new InlineProgress<CopyProgress>(p =>
                {
                    if (win != null && !win.InBackground)
                    {
                        Application.Current.Dispatcher.BeginInvoke(new Action(() =>
                            win.UpdateProgress(p.CurrentFile, p.TotalBytesCopied)));
                        return;
                    }

                    // Mode sans fenêtre : on ne republie la notification que si le pourcentage
                    // a changé ET que 3 secondes se sont écoulées (sinon on noierait l'interface).
                    if (total <= 0) return;
                    int percent = (int)(Math.Min(p.TotalBytesCopied, total) * 100 / total);
                    var now = DateTime.UtcNow;
                    if (percent != lastPercent && (now - lastNotice).TotalSeconds >= 3)
                    {
                        lastPercent = percent;
                        lastNotice = now;
                        ShowProgress($"Copie de '{Game.Name}'… {percent} % (cliquer ici pour annuler)",
                                     () => AskCancel(cts));
                    }
                });

                ArchiveLog.Info($"Copie de '{Game.Name}' : '{source}' vers '{dest}' ({ArchiveScanner.FormatSize(total)}).");
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                ArchiveScanner.CopyDirectory(source, dest, progress, cts.Token);
                ArchiveLog.Info($"Copie de '{Game.Name}' terminée en {DescribeDuration(chrono.Elapsed)}.");
                return StepResult.Ok;
            }
            catch (OperationCanceledException)
            {
                ArchiveLog.Warn($"Copie de '{Game.Name}' annulée par l'utilisateur.");
                return StepResult.Cancelled;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : échec copie.");
                ArchiveLog.Error($"Copie de '{Game.Name}' : échec : {ex.GetType().Name} — {ex.Message}");
                Notify("ArchiveStore_Err", $"ArchiveStore : échec copie — {ex.Message}", NotificationType.Error);
                return StepResult.Failed;
            }
            finally
            {
                CloseProgress(win);
                cts.Dispose();
            }
        }

        // Clic sur la notification de progression : demande de confirmation
        // (boîte de dialogue NATIVE de Playnite, utilisable à la manette) puis annulation.
        private void AskCancel(CancellationTokenSource cts)
        {
            try
            {
                string detail = _destinationExisting
                    ? "La copie s'arrêtera. Rien ne sera supprimé : le dossier existait déjà."
                    : "Les fichiers déjà copiés seront supprimés.";
                var r = _api.Dialogs.ShowMessage(
                    $"Annuler l'installation de '{Game.Name}' ?\n\n{detail}",
                    "ArchiveStore",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);
                if (r == MessageBoxResult.Yes) cts.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // La copie vient de se terminer : il n'y a plus rien à annuler.
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : annulation depuis la notification impossible.");
            }
        }

        // ------------------------------------------------------------------
        // Outils
        // ------------------------------------------------------------------
        // keepFilesOnCancel : dossier préexistant (« Écraser ») → les textes d'annulation précisent que rien n'est supprimé.
        // allowBackground : la touche Y masque la fenêtre (installation / copie / extras) ; l'installation continue
        // et le suivi passe par les notifications. Le choix vaut pour toute l'installation du jeu.
        private volatile bool _backgroundRequested;

        // ownedByMain : la fenêtre appartient à la fenêtre principale de Playnite plutôt qu'à la « fenêtre actuelle » (qui peut
        // être un menu en train de se fermer : à la fermeture de la nôtre, Windows rendrait alors la main ailleurs).
        private ProgressWindow OpenProgress(long total, string title, string status, bool allowCancel, Action onCancel,
                                            bool keepFilesOnCancel = false, bool allowBackground = false, bool ownedByMain = false)
        {
            return UiHelper.OnUi<ProgressWindow>(() =>
            {
                var w = new ProgressWindow(total, title, allowCancel, allowBackground);
                ConsoleDialogs.SetOwner(w, ownedByMain ? (ActionReply.PlayniteMain() ?? _api.Dialogs.GetCurrentAppWindow())
                                                       : _api.Dialogs.GetCurrentAppWindow());
                if (keepFilesOnCancel) w.KeepFilesOnCancel();
                w.SetStatus(status);
                if (onCancel != null)
                    w.CancelRequested += (s, e) => onCancel();
                if (allowBackground)
                {
                    w.BackgroundRequested += (s, e) =>
                    {
                        _backgroundRequested = true;
                        ArchiveLog.Info($"Installation de '{Game.Name}' : poursuite en arrière-plan demandée (touche Y).");
                        ShowProgress($"Installation de '{Game.Name}' en arrière-plan…", null);
                    };
                    if (_backgroundRequested)
                    {
                        w.StartInBackground(); // jamais affichée
                        return w;
                    }
                }
                w.Show();
                return w;
            });
        }

        // Change le texte d'une fenêtre de progression depuis un thread d'arrière-plan.
        private static void SetProgressStatus(ProgressWindow w, string text)
        {
            if (w == null) return;
            try { UiHelper.OnUi(() => w.SetStatus(text)); }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mise à jour du statut impossible."); }
        }

        // Change la barre « par étapes » d'une fenêtre de progression depuis un thread d'arrière-plan.
        private static void SetProgressSteps(ProgressWindow w, int done, int total, string text)
        {
            if (w == null) return;
            try { UiHelper.OnUi(() => w.SetSteps(done, total, text)); }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : mise à jour de la progression impossible."); }
        }

        private static void CloseProgress(ProgressWindow w)
        {
            if (w == null) return;
            try
            {
                UiHelper.OnUi(() => { w.AllowClose(); w.Close(); });
            }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : fermeture de la fenêtre de progression."); }
        }

        // ------------------------------------------------------------------
        // PROGRESSION PAR NOTIFICATION (mode sans fenêtre : « Installation directe » cochée)
        //
        // PÉDAGOGIE : on remplace la notification (Remove puis Add) à chaque mise à
        // jour. Tout passe par BeginInvoke (file d'attente du thread UI, ordre
        // respecté) : ainsi la suppression finale arrive TOUJOURS après la dernière
        // mise à jour, et aucune notification fantôme ne reste affichée.
        // ------------------------------------------------------------------
        //
        // Case « Infos pendant l'installation » décochée : plus de notification d'avancement, SAUF celle d'une copie
        // (onActivate != null) : un clic dessus est le seul moyen d'annuler une copie sans fenêtre.
        private void ShowProgress(string text, Action onActivate)
        {
            if (onActivate == null && !_settings.IsNotifyEnabled(NotifyCategory.InstallInfo)) return;

            var id = ProgressNotificationId;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _api.Notifications.Remove(id);
                _api.Notifications.Add(onActivate == null
                    ? new NotificationMessage(id, text, NotificationType.Info)
                    : new NotificationMessage(id, text, NotificationType.Info, onActivate));
            }));
        }

        private void RemoveProgress()
        {
            var id = ProgressNotificationId;
            Application.Current.Dispatcher.BeginInvoke(new Action(() => _api.Notifications.Remove(id)));
        }

        // Signale à Playnite que l'installation est terminée SANS succès
        // (sinon le jeu resterait bloqué à l'état "Installation en cours").
        private void ReportCancelled()
        {
            try
            {
                UiHelper.OnUi(() => InvokeOnInstallationCancelled(new GameInstallationCancelledEventArgs()));
            }
            catch (Exception ex) { logger.Error(ex, "ArchiveStore : signal d'annulation impossible."); }
        }

        // Retire ce que l'installation a laissé dans la destination (copie
        // partielle…). Triple garde-fou : IsSafeToDelete vérifie que le dossier
        // est bien DANS une destination connue et JAMAIS dans/autour d'une source.
        // On passe TOUTES les sources et destinations de TOUS les profils : même si l'utilisateur
        // a changé de profil pendant l'installation, aucune archive ne peut être touchée.
        private void CleanupPartial(string destDir)
        {
            try
            {
                if (!Directory.Exists(destDir)) return;
                if (!ArchiveScanner.IsSafeToDelete(destDir, _settings.GetAllDestinationRoots(), _settings.GetAllSourceRoots())) return;
                ArchiveScanner.DeleteDirectoryWithRetry(destDir, _settings.SaveFolderNames);
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : nettoyage de la destination impossible.");
            }
        }

        // Nettoyage après un échec, SEULEMENT si le dossier a été créé par cette installation (« à nous »).
        // Un dossier qui existait déjà (choix « Écraser ») n'est JAMAIS touché : on ne sait pas ce qui lui
        // appartient (mods, fichiers de l'utilisateur…). Ne lève jamais d'exception.
        private void CleanupIfOwned(string destDir, bool destinationOwned)
        {
            if (!destinationOwned)
            {
                ArchiveLog.Info($"Aucun nettoyage : le dossier '{destDir}' existait déjà avant l'installation, rien n'y est supprimé.");
                return;
            }
            CleanupPartial(destDir);
        }

        // Le dossier de destination existe déjà et n'est pas vide : fenêtre à trois choix (Utiliser tel quel /
        // Écraser / Annuler). Appelée sur le thread d'arrière-plan : la recherche d'un exécutable (lecture du
        // disque, parfois d'un NAS) se fait ICI, hors du thread UI ; seule la fenêtre passe par le Dispatcher.
        // Ne supprime rien, ne modifie rien.
        private ExistingFolderChoice AskExistingFolder(string destDir, InstallDialog.Mode mode, bool unrecognizedInstaller)
        {
            bool hasExe = false;
            try
            {
                hasExe = ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns).Count > 0;
            }
            catch (Exception ex)
            {
                // En cas d'erreur de lecture, on considère qu'il n'y a pas d'exécutable : « tel quel » reste grisé (prudent).
                ArchiveLog.Warn($"Dossier existant '{destDir}' : recherche d'un exécutable impossible : {ex.Message}");
            }

            ArchiveLog.Info($"Dossier de destination existant : '{destDir}' (exécutable de jeu trouvé : {(hasExe ? "oui" : "non")}). Question posée à l'utilisateur.");

            // Notification « Analyse… » (si elle existe) : elle ne doit pas rester pendant la question.
            RemoveProgress();

            return UiHelper.OnUi<ExistingFolderChoice>(() =>
            {
                var dlg = new ExistingFolderDialog(Game.GameId, destDir, hasExe,
                                                   mode == InstallDialog.Mode.Installer, unrecognizedInstaller, _isFullscreen)
                { Owner = _api.Dialogs.GetCurrentAppWindow() };
                dlg.ShowDialog();
                return dlg.Choice;
            });
        }

        // Le disque de la destination est absent : on n'installe RIEN, on ne crée RIEN, on ne supprime RIEN.
        // Une fenêtre d'erreur s'ajoute à la notification.
        private void FailDestinationUnreachable(string destDir, List<DestinationEntry> destinations, bool canAsk)
        {
            // Nom lisible de la destination : celle dont le dossier du jeu est un sous-dossier.
            string parent = Path.GetDirectoryName(destDir) ?? destDir;
            var entry = destinations.FirstOrDefault(d => ArchiveScanner.SamePath(d.FolderPath, parent));
            string label = entry != null ? entry.GetLabel() : parent;

            string hint = destinations.Count > 1
                ? " Reconnectez-la ou choisissez-en une autre (clic droit sur le jeu → « Choisir la destination d'installation (ArchiveStore) »)."
                : " Reconnectez-la, ou changez la destination dans les réglages de l'extension.";
            string message =
                $"ArchiveStore : impossible d'installer « {Game.Name} » — la destination « {label} » n'est pas accessible (disque ou réseau débranché ?)." +
                hint + " Rien n'a été installé ni supprimé.";

            ArchiveLog.Warn($"Installation de '{Game.Name}' ({Game.GameId}) refusée : destination '{label}' inaccessible. Rien n'est installé ni supprimé.");
            Notify("ArchiveStore_DestUnreachable", message, NotificationType.Error);
            if (canAsk)
                UiHelper.OnUi(() => { _api.Dialogs.ShowErrorMessage(message, "ArchiveStore"); });
            ReportCancelled();
        }

        // La destination choisie contient déjà des fichiers ET aucune fenêtre ne peut poser la question
        // (« Installation directe ») : on n'installe RIEN, on ne supprime RIEN, on n'écrase JAMAIS tout seul.
        // Le message indique les deux issues : décocher « Installation directe » (la fenêtre propose alors
        // « Utiliser tel quel » ou « Écraser »), ou choisir une autre destination. Une fenêtre d'erreur s'ajoute
        // à la notification (l'ancienne erreur de Playnite).
        private void FailDestinationPopulated(string destDir, bool otherDestinations, bool canAsk)
        {
            string otherDestinationHint = otherDestinations
                ? " Vous pouvez aussi choisir une autre destination (clic droit sur le jeu → « Choisir la destination d'installation (ArchiveStore) »)."
                : "";
            string message =
                $"ArchiveStore : le dossier « {destDir} » contient déjà des fichiers. Rien n'a été installé ni supprimé. " +
                "Pour utiliser ce dossier tel quel ou installer par-dessus, décochez « Installation directe » " +
                "dans les réglages de l'extension, puis relancez l'installation : une fenêtre vous laissera choisir." +
                otherDestinationHint;

            ArchiveLog.Warn($"Installation de '{Game.Name}' ({Game.GameId}) refusée : destination '{destDir}' déjà peuplée, aucune fenêtre possible (Installation directe). Rien n'est installé ni supprimé.");
            Notify("ArchiveStore_DestPopulated", message, NotificationType.Error);
            if (canAsk)
                UiHelper.OnUi(() => { _api.Dialogs.ShowErrorMessage(message, "ArchiveStore"); });
            ReportCancelled();
        }

        // Notification « classique » (message final). L'identifiant est suffixé par le
        // jeu : deux jeux ne s'écrasent pas, et on retire l'éventuelle ancienne
        // notification du même jeu avant d'ajouter (le SDK ne précise pas ce qui se
        // passe quand on ajoute deux fois le même identifiant).
        // BeginInvoke : la collection de notifications appartient au thread UI.
        //
        // FILTRE : une catégorie décochée dans les réglages (groupe « Notifications ») supprime ses notifications.
        // Les ERREURS ne sont jamais filtrées (voir PluginSettings.IsNotificationAllowed).
        private void Notify(string id, string message, NotificationType type)
        {
            if (!_settings.IsNotificationAllowed(id, type)) return;

            var fullId = id + "_" + Game.Id;
            Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                _api.Notifications.Remove(fullId);
                _api.Notifications.Add(new NotificationMessage(fullId, message, type));
            }));

            // TOAST EN PLUS (point central : tous les messages d'installation, d'extras et d'erreur passent ici).
            // ToastHelper vérifie la case de la catégorie (ou « Erreurs et blocages ») et ne fait rien sinon.
            ToastHelper.ShowForNotification(_api, _settings, id, type, message);
        }
    }

    // ======================================================================
    // ArchiveUninstallController — signature officielle : void Uninstall(UninstallActionArgs)
    //
    // Ici on utilise ActivateGlobalProgress : Playnite affiche SON dialogue
    // d'attente (bloquant, mais l'interface reste "vivante") pendant que notre
    // code tourne en arrière-plan. Si on LÈVE une exception après, Playnite
    // l'affiche et remet l'état du jeu tout seul.
    // ======================================================================
    public class ArchiveUninstallController : UninstallController
    {
        private static readonly ILogger logger = LogManager.GetLogger();
        private readonly PluginSettings _settings;
        private readonly IPlayniteAPI _api;

        public ArchiveUninstallController(Game game, PluginSettings settings, IPlayniteAPI api)
            : base(game)
        {
            _settings = settings;
            _api = api;
        }

        public override void Uninstall(UninstallActionArgs args)
        {
            // Dossier à retirer : celui enregistré par Playnite, sinon déduit du profil actif.
            string destDir = Game.InstallDirectory;
            if (string.IsNullOrEmpty(destDir))
            {
                // PLUSIEURS DESTINATIONS : on cherche le jeu dans chacune (la destination choisie d'abord).
                var profile = _settings.GetActiveProfile();
                if (profile != null)
                {
                    bool fallbackUnused;
                    string planned = _settings.ResolveDestinationRoot(profile, Game.GameId, out fallbackUnused);
                    var roots = new List<string>();
                    if (!string.IsNullOrWhiteSpace(planned)) roots.Add(planned);
                    foreach (var d in profile.GetDestinations())
                        if (!roots.Any(r => ArchiveScanner.SamePath(r, d.FolderPath))) roots.Add(d.FolderPath);

                    foreach (var root in roots)
                    {
                        var candidate = Path.Combine(root, Game.GameId);
                        if (Directory.Exists(candidate)) { destDir = candidate; break; }
                    }
                    if (string.IsNullOrEmpty(destDir) && roots.Count > 0)
                        destDir = Path.Combine(roots[0], Game.GameId);
                }
            }
            if (string.IsNullOrEmpty(destDir))
                throw new Exception("ArchiveStore : dossier d'installation inconnu (aucun profil actif).");

            // TOUTES les destinations et sources de TOUS les profils (garde-fou : voir IsSafeToDelete).
            var destRoots = _settings.GetAllDestinationRoots();
            var sourceRoots = _settings.GetAllSourceRoots();

            // DERNIER RAMPART : on ne supprime JAMAIS hors des destinations
            // configurées, ni dans/autour d'une archive source.
            if (Directory.Exists(destDir) && !ArchiveScanner.IsSafeToDelete(destDir, destRoots, sourceRoots))
                throw new Exception(
                    $"ArchiveStore : suppression refusée pour '{destDir}' : ce dossier n'est pas " +
                    "strictement à l'intérieur d'une Destination configurée (ou touche une Source).");

            // CONFIRMATION (toujours demandée, même avec « Installation directe ») : le chemin EXACT qui va être
            // supprimé est sous les yeux de l'utilisateur. Essentiel pour un dossier repris « tel quel » ou
            // « écrasé » : il peut contenir des fichiers qui ne viennent pas de l'archive (mods, scripts…).
            // PÉDAGOGIE : le SDK n'a pas d'événement « désinstallation annulée » ; pour annuler, on LÈVE une
            // exception (voir plus haut) : Playnite l'affiche et remet l'état du jeu.
            // Dossier absent : il n'y a rien à supprimer, on ne pose pas de question inutile.
            if (Directory.Exists(destDir))
            {
                var saveNames = (_settings.SaveFolderNames ?? new List<string>())
                    .Where(n => !string.IsNullOrWhiteSpace(n)).ToList();
                string savesLine = saveNames.Count > 0
                    ? "\n\nLes dossiers de sauvegarde (" + string.Join(", ", saveNames) + ") sont conservés s'ils existent."
                    : "";

                bool confirmed = UiHelper.OnUi<bool>(() => ConsoleConfirmDialog.Ask(
                    _api.Dialogs.GetCurrentAppWindow(),
                    "Désinstaller « " + Game.Name + " » ?",
                    "Ce dossier va être supprimé AVEC TOUT SON CONTENU, y compris les fichiers que vous y avez " +
                    "ajoutés vous-même (mods, scripts…) :\n\n" + destDir + savesLine +
                    "\n\nL'archive source n'est pas touchée.",
                    "Oui, désinstaller",
                    "Non, garder le jeu"));

                if (!confirmed)
                {
                    ArchiveLog.Info($"Désinstallation de '{Game.Name}' refusée par l'utilisateur : rien n'a été supprimé ('{destDir}').");
                    throw new Exception("Désinstallation annulée : rien n'a été supprimé.");
                }

                ArchiveLog.Info($"Désinstallation de '{Game.Name}' confirmée par l'utilisateur : suppression de '{destDir}'.");
            }

            Exception error = null;
            bool savesKept = false;

            _api.Dialogs.ActivateGlobalProgress(a =>
            {
                try
                {
                    if (!Directory.Exists(destDir)) return;

                    // 1. Désinstalleur en silence s'il existe ET si on sait lui parler.
                    a.Text = $"Désinstallation de {Game.Name}…";
                    RunUninstallerIfAny(destDir);

                    // 2. Suppression directe (avec réessais), saves protégées.
                    a.Text = $"Suppression des fichiers de {Game.Name}…";
                    savesKept = !ArchiveScanner.DeleteDirectoryWithRetry(destDir, _settings.SaveFolderNames);
                }
                catch (Exception ex)
                {
                    logger.Error(ex, "ArchiveStore : désinstallation échouée.");
                    error = ex;
                }
            }, new GlobalProgressOptions($"Désinstallation de {Game.Name}…", false) { IsIndeterminate = true });

            if (error != null)
                throw new Exception($"ArchiveStore : échec de la désinstallation — {error.Message}", error);

            // Nettoyage du statut Playnite : retrait de l'action "Jouer"
            // (IsInstalled=false et InstallDirectory vidé sont faits par Playnite
            // lorsqu'on signale la fin avec InvokeOnUninstalled).
            UiHelper.OnUi(() =>
            {
                GameActionHelper.RemovePlayAction(_api, Game.Id);

                // Le jeu n'est plus installé : ses DLC et patchs non plus. On efface leur mémoire (sinon
                // une réinstallation du jeu croirait encore ces extras installés).
                try { _settings.ForgetInstalledExtras(Game.GameId); }
                catch (Exception ex) { logger.Warn(ex, "ArchiveStore : oubli des extras mémorisés impossible."); }

                InvokeOnUninstalled();
            });

            if (_settings.IsNotifyEnabled(NotifyCategory.Summaries))
            {
                string uninstallMsg =
                    $"'{Game.Name}' désinstallé. L'archive source est intacte." +
                    (savesKept ? " Les dossiers de sauvegarde ont été conservés." : "");
                _api.Notifications.Add(new NotificationMessage("ArchiveStore_UninstallDone", uninstallMsg, NotificationType.Info));
                ToastHelper.ShowForCategory(_api, _settings, NotifyCategory.Summaries, uninstallMsg);
            }
        }

        // Exécute unins*.exe avec les bons arguments selon son type. Tout échec
        // est NON bloquant : la suppression directe prend le relais.
        private void RunUninstallerIfAny(string destDir)
        {
            try
            {
                var unins = ArchiveScanner.FindUninstaller(destDir);
                if (string.IsNullOrEmpty(unins)) return;

                var a = _settings.SilentArguments ?? new SilentArgs();
                string arguments;
                switch (ArchiveScanner.DetectUninstallerType(unins))
                {
                    case InstallerType.InnoSetup:
                        arguments = a.InnoArgs;
                        break;
                    case InstallerType.Nullsoft:
                        // "_?=dossier" : le désinstalleur NSIS s'exécute sur place et
                        // on peut ATTENDRE sa fin (sinon il se copie dans %TEMP% et
                        // rend la main aussitôt). Doit être le dernier argument.
                        arguments = $"{a.NsisArgs} _?={destDir}";
                        break;
                    default:
                        logger.Info("ArchiveStore : désinstalleur de type inconnu, suppression directe.");
                        return;
                }

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = unins,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(unins) ?? ""
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    // Plafond de 5 minutes : un désinstalleur qui reste bloqué
                    // ne doit pas figer indéfiniment la désinstallation.
                    if (!p.WaitForExit(5 * 60 * 1000))
                    {
                        logger.Warn("ArchiveStore : désinstalleur trop long, suppression directe.");
                        try { p.Kill(); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Warn(ex, "ArchiveStore : désinstalleur en échec (non bloquant).");
            }
        }

        public override void Dispose() { }
    }
}
