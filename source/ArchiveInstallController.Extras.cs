using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // ArchiveInstallController — partie « DLC ET PATCHS ».
    //
    // PÉDAGOGIE — « partial class » : une classe C# peut être écrite en PLUSIEURS fichiers, le
    // compilateur les assemble en une seule. Ici, tout ce qui concerne les DLC et les patchs
    // (détection, choix, installation, mémoire des extras déjà installés, menu clic droit) est
    // rangé dans ce fichier, pour alléger ArchiveStorePlugin.cs. Les deux fichiers partagent les
    // mêmes champs (_settings, _api, Game…) et les mêmes outils (Notify, OpenProgress…).
    // ======================================================================
    public partial class ArchiveInstallController
    {
        // ------------------------------------------------------------------
        // DLC ET PATCHS — détectés et choisis AVANT le jeu, installés APRÈS lui
        //
        // Deux temps :
        //   1. PlanExtras (AVANT l'installation du jeu) : on cherche, dans les sous-dossiers
        //      d'extras (nom contenant « DLC », « patch » ou « update » : voir
        //      ArchiveScanner.ClassifyExtraFolder), les installeurs RECONNUS, et on décide :
        //        interactive (« Installation directe » décochée) ET ConfirmExtras
        //          → fenêtre « DLC et patchs » : deux cases à cocher (DLC, patchs), avec la taille
        //            de chacune. Aucune case cochée = le jeu seul. On sait donc « quoi installer »
        //            AVANT les 15 minutes d'installation du jeu.
        //        sinon (aucune fenêtre possible, ou confirmation désactivée)
        //          → TOUT s'installe, et une notification annonce « installation automatique ».
        //   2. ProcessExtras (APRÈS le jeu) : on lance les installeurs choisis l'un après l'autre
        //      en silencieux, avec le dossier du jeu comme destination, DLC d'abord puis patchs.
        //
        // Le même PlanExtras / ProcessExtras sert au menu clic droit (jeu DÉJÀ installé : voir
        // InstallExtrasFromMenu plus bas), ce qui garantit un comportement identique.
        //
        // Au PREMIER échec, on arrête tout : la suite pourrait dépendre de l'étape qui
        // vient d'échouer (un patch après un DLC, par exemple). Le jeu de base, lui,
        // reste installé. Chaque étape est écrite dans le journal (ArchiveLog).
        //
        // MÉMOIRE : chaque extra installé avec succès est mémorisé (PluginSettings.InstalledExtras),
        // pour que le menu clic droit ne propose que ce qui reste à installer.
        // ------------------------------------------------------------------

        // Étape en cours (« DLC 2 sur 3 »), lue par le suivi de l'installation pour la notification.
        // null = on installe le jeu de base. volatile : écrit par un thread, lu par un autre.
        private volatile string _stepLabel;

        // Dossier d'installation du jeu, rappelé dans l'avertissement des extras lancés à la main
        // (« si l'installeur demande un dossier, choisissez celui du jeu »). Renseigné au début de
        // RunWorkflow et de RunExtrasFromMenu ; null = pas de rappel.
        private volatile string _destDirHint;

        // Les tailles installées des extras sont rangées dans le même dictionnaire que celles des jeux,
        // sous une clé « NomDuJeu::extras:empreinte » (le nom d'un dossier Windows ne peut pas contenir
        // « : »). L'empreinte dépend de la liste exacte d'extras installés : « 2 DLC » et « 2 DLC + 1 patch »
        // n'ont pas la même taille, on ne les mélange donc pas (voir ExtrasSizeKey).
        private const string ExtrasSizeKeySuffix = "::extras";

        // Vrai quand l'utilisateur vient de CLIQUER sur la notification « nouveaux DLC et patchs » : il demande
        // lui-même à choisir, donc la fenêtre « DLC et patchs » s'ouvre même si « Installation directe » est
        // cochée (ou la confirmation des DLC désactivée). Le reste des installations n'en dépend pas.
        // volatile : écrit sur le thread UI, lu par le thread d'arrière-plan.
        private volatile bool _forceExtrasWindow;

        // La fenêtre de choix des extras est possible si les fenêtres sont autorisées (interactive) ET que la
        // confirmation est activée, ou si l'utilisateur l'a demandée en cliquant sur la notification.
        private bool ExtrasWindowWanted(bool interactive)
            => interactive && (_settings.ConfirmExtras || _forceExtrasWindow);

        // Résultat de la détection + décision, transmis de PlanExtras à ProcessExtras.
        private sealed class ExtrasPlan
        {
            public ExtraScanResult Scan;   // installeurs reconnus / non reconnus (tout ce qui a été trouvé)
            public List<ExtraInstaller> Selected = new List<ExtraInstaller>(); // ceux qu'on va installer, dans l'ordre
            public bool Install;           // vrai = installer la sélection
            public string What;            // « 2 DLC et 1 patch » (la sélection)
            public long ExtrasBytes;       // taille des dossiers d'extras SÉLECTIONNÉS (archives)
            public string Summary = "";    // phrase pour la notification finale quand Install est faux
            public string SkippedNote = "";// phrase ajoutée à la notification finale : extras écartés par choix
            public bool MarkedOnly;        // vrai = l'utilisateur a seulement NOTÉ des extras comme déjà installés (rien à lancer)
        }

        // Décide quoi faire des extras déjà détectés (extras) et pose la question.
        // Retourne null s'il n'y a rien à faire.
        // cancelled = vrai si l'utilisateur a répondu « Annuler » : l'installation s'arrête.
        // gameAlreadyInstalled = vrai pour le menu clic droit (la fenêtre ne parle plus du jeu).
        // Ne lève JAMAIS d'exception (une erreur ici ne bloque pas le jeu de base).
        private ExtrasPlan PlanExtras(ExtraScanResult extras, long extrasBytes, ScanResult scan,
                                      bool interactive, bool gameAlreadyInstalled, out bool cancelled)
        {
            cancelled = false;
            try
            {
                if (extras == null) return null;
                if (extras.Installers.Count == 0 && extras.NotRecognized.Count == 0)
                    return null; // aucun extra : rien à dire, rien à écrire

                ArchiveLog.Info($"Extras de '{Game.Name}' : {extras.Installers.Count} installeur(s) reconnu(s), " +
                                $"{extras.NotRecognized.Count} fichier(s) non reconnu(s), " +
                                $"taille des dossiers d'extras {ArchiveScanner.FormatSize(extrasBytes)}.");
                foreach (var f in extras.NotRecognized)
                    ArchiveLog.Warn($"Extra de type non reconnu : '{f}' (jamais lancé automatiquement).");

                // La fenêtre de choix est possible seulement avec fenêtres (« Installation directe » décochée) ET confirmation
                // activée (ou clic sur la notification de nouveautés : voir _forceExtrasWindow). Elle seule peut
                // proposer les fichiers non reconnus (lancement manuel).
                bool showDialog = ExtrasWindowWanted(interactive);

                var plan = new ExtrasPlan { Scan = extras, ExtrasBytes = extrasBytes };
                string notRecognizedText = extras.NotRecognized.Count > 0
                    ? DescribeNotRecognized(extras.NotRecognized) : "";

                // Des .exe dans les dossiers d'extras, mais aucun installeur reconnu ET pas de fenêtre pour
                // proposer le lancement manuel : on le dit, et rien n'est lancé.
                if (extras.Installers.Count == 0 && !(showDialog && extras.Manual.Count > 0))
                {
                    Notify("ArchiveStore_ExtrasUnknown",
                        $"ArchiveStore : {notRecognizedText} dans les dossiers d'extras de '{Game.Name}'. " +
                        "Ce type d'installeur n'est pas reconnu : rien ne sera lancé.",
                        NotificationType.Info);
                    plan.Summary = $" Extras non lancés ({extras.NotRecognized.Count} fichier(s) non reconnu(s)).";
                    return plan;
                }

                var list = extras.Installers;
                // Les fichiers non reconnus ne sont proposés que dans la fenêtre de choix.
                var manualList = showDialog ? extras.Manual : new List<ExtraInstaller>();
                var shownList = list.Concat(manualList).ToList();   // tout ce que la fenêtre montre
                int dlcCount = shownList.Count(x => x.IsDlc);
                int patchCount = shownList.Count - dlcCount;
                string allWhat = DescribeExtras(dlcCount, patchCount);       // « 2 DLC et 1 patch »
                plan.What = allWhat;
                string detected = shownList.Count > 1 ? "détectés" : "détecté";

                if (showDialog)
                {
                    // Fenêtre « DLC et patchs » (même style que la fenêtre d'installation du jeu),
                    // ouverte depuis le thread UI : on attend la réponse (OnUi = Dispatcher.Invoke).
                    long gameBytes = gameAlreadyInstalled ? 0 : scan.SourceSizeBytes;   // jeu seul (extras exclus)
                    long free = ArchiveScanner.GetFreeSpace(scan.DestinationDirectory);
                    string headline = $"{allWhat} {detected}";

                    // Menu clic droit seulement : bouton « Déjà installés », et avertissement si ce jeu a été
                    // installé avant la mémoire des extras (aucune liste pour lui dans les réglages).
                    bool offerMark = gameAlreadyInstalled;
                    bool warnNoMemory = gameAlreadyInstalled && !_settings.HasExtrasMemory(Game.GameId);

                    var choice = ExtrasDialog.UserChoice.Cancel;
                    List<ExtraInstaller> picked = new List<ExtraInstaller>();
                    UiHelper.OnUi(() =>
                    {
                        var dlg = new ExtrasDialog(Game.Name, headline, !gameAlreadyInstalled, gameBytes,
                                                   extras, free, offerMark, warnNoMemory)
                        { Owner = _api.Dialogs.GetCurrentAppWindow() };
                        dlg.ShowDialog();
                        choice = dlg.Choice; // Cancel si la fenêtre est fermée sans réponse
                        picked = dlg.SelectedInstallers;
                    });

                    if (choice == ExtrasDialog.UserChoice.Cancel)
                    {
                        cancelled = true;
                        return null;
                    }

                    // « Déjà installés » : on NOTE les fichiers cochés comme installés, sans rien lancer.
                    if (choice == ExtrasDialog.UserChoice.MarkInstalled)
                    {
                        string markedWhat = DescribeExtras(picked.Count(x => x.IsDlc), picked.Count(x => !x.IsDlc));
                        var keys = picked.Select(x => x.Key).ToList();
                        int added = 0;
                        UiHelper.OnUi(() => { added = _settings.AddInstalledExtras(Game.GameId, keys); });

                        ArchiveLog.Info($"Extras de '{Game.Name}' : {markedWhat} noté(s) comme déjà installé(s) à la demande " +
                                        $"de l'utilisateur ({added} nouvelle(s) entrée(s) en mémoire). Rien n'a été lancé.");
                        plan.MarkedOnly = true;
                        plan.Summary = $" {markedWhat} noté(s) comme déjà installé(s) : ils ne seront plus proposés.";
                        return plan;
                    }

                    // Fichiers NON RECONNUS cochés : un seul avertissement pour tous, avant d'aller plus loin.
                    // « Non » = on ne lance pas ces fichiers (le reste de la sélection continue normalement).
                    var pickedManual = manualList.Where(x => picked.Contains(x)).ToList();
                    if (pickedManual.Count > 0 && !ConfirmManualRun(pickedManual))
                    {
                        ArchiveLog.Info($"Extras de '{Game.Name}' : lancement manuel de {pickedManual.Count} fichier(s) non reconnu(s) refusé.");
                        picked = picked.Where(x => !x.IsManual).ToList();
                        pickedManual.Clear();
                    }

                    // La sélection, dans l'ordre d'exécution : installeurs reconnus (DLC puis patchs), PUIS les
                    // fichiers lancés à la main (eux aussi DLC puis patchs : les listes d'origine sont déjà triées).
                    var selected = list.Where(x => picked.Contains(x)).Concat(pickedManual).ToList();
                    int skippedDlc = dlcCount - selected.Count(x => x.IsDlc);
                    int skippedPatches = patchCount - selected.Count(x => !x.IsDlc);

                    if (selected.Count == 0)
                    {
                        // Aucune case cochée : le jeu seul.
                        ArchiveLog.Info($"Extras de '{Game.Name}' : installation refusée par l'utilisateur (jeu seul).");
                        Notify("ArchiveStore_ExtrasSkipped",
                            $"ArchiveStore : {allWhat} ne sera pas installé(s), à votre demande.",
                            NotificationType.Info);
                        plan.Summary = $" {allWhat} non installé(s).";
                        return plan;
                    }

                    plan.Selected = selected;
                    plan.What = DescribeExtras(selected.Count(x => x.IsDlc), selected.Count(x => !x.IsDlc));
                    plan.ExtrasBytes = extras.BytesFor(selected);

                    if (skippedDlc + skippedPatches > 0)
                    {
                        string skippedWhat = DescribeExtras(skippedDlc, skippedPatches);
                        ArchiveLog.Info($"Extras de '{Game.Name}' : {skippedWhat} écarté(s) par l'utilisateur.");
                        Notify("ArchiveStore_ExtrasSkipped",
                            $"ArchiveStore : {skippedWhat} ne sera pas installé(s), à votre demande.",
                            NotificationType.Info);
                        plan.SkippedNote = $" {skippedWhat} non installé(s).";
                    }

                    ArchiveLog.Info($"Extras de '{Game.Name}' : l'utilisateur a choisi {plan.What}.");
                }
                else
                {
                    // Sans fenêtre (ou confirmation désactivée) : TOUT s'installe.
                    plan.Selected = new List<ExtraInstaller>(list);
                    Notify("ArchiveStore_ExtrasFound",
                        $"ArchiveStore : {allWhat} {detected} ({ArchiveScanner.FormatSize(extrasBytes)}) : " +
                        (gameAlreadyInstalled
                            ? "installation automatique."
                            : "installation automatique après le jeu."),
                        NotificationType.Info);
                }

                plan.Install = true;
                return plan;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : préparation des extras impossible.");
                ArchiveLog.Error($"Extras de '{Game.Name}' : préparation impossible : {ex.Message}");
                Notify("ArchiveStore_ExtrasFail",
                    gameAlreadyInstalled
                        ? $"ArchiveStore : préparation des DLC et patchs de '{Game.Name}' impossible ({ex.Message}). " +
                          "Détails : archivestore.log."
                        : $"ArchiveStore : préparation des DLC et patchs de '{Game.Name}' impossible ({ex.Message}). " +
                          "Le jeu sera installé sans eux. Détails : archivestore.log.",
                    NotificationType.Error);
                return null;
            }
        }

        // Vrai pour un installeur qu'on sait lancer en silence (Inno Setup, NSIS, MSI).
        private static bool IsRecognizedInstaller(InstallerType type)
            => type == InstallerType.InnoSetup || type == InstallerType.Nullsoft || type == InstallerType.Msi;

        // Cherche les DLC et patchs de la source et calcule la taille de leurs dossiers
        // (chaque sous-dossier d'extras n'est compté qu'une fois : FolderBytes, puis DlcBytes et
        // PatchBytes). "bytes" = taille de TOUS les dossiers d'extras trouvés.
        // Ne lève jamais d'exception : en cas d'erreur, on renvoie null et on l'écrit dans le journal.
        private ExtraScanResult FindExtrasWithSize(string sourceGameDir, out long bytes)
        {
            bytes = 0;
            try
            {
                var extras = ArchiveScanner.FindExtraInstallers(sourceGameDir, _settings.ExcludedExePatterns,
                    _settings.ExtraInstallerWords, _settings.InstallerWordsAnywhere);

                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var x in extras.Installers.Concat(extras.Manual))
                    if (!string.IsNullOrEmpty(x.FolderName))
                        folders.Add(x.FolderName);

                foreach (var name in folders)
                    extras.FolderBytes[name] = ArchiveScanner.GetDirectorySize(Path.Combine(sourceGameDir, name));

                bytes = SumExtraBytes(extras);
                return extras;
            }
            catch (Exception ex)
            {
                bytes = 0;
                logger.Error(ex, "ArchiveStore : détection des extras impossible.");
                ArchiveLog.Error($"Extras de '{Game.Name}' : détection impossible : {ex.Message}");
                return null;
            }
        }

        // Recalcule DlcBytes et PatchBytes d'un résultat de recherche à partir de SES installeurs et
        // des tailles de dossiers déjà mesurées (FolderBytes). Un dossier n'est compté qu'une fois,
        // même s'il contient plusieurs installeurs. Retourne le total. Aucun accès disque.
        private static long SumExtraBytes(ExtraScanResult extras)
        {
            long dlc = 0, patch = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var x in extras.Installers.Concat(extras.Manual))
            {
                if (string.IsNullOrEmpty(x.FolderName) || !seen.Add(x.FolderName)) continue;
                long size;
                if (!extras.FolderBytes.TryGetValue(x.FolderName, out size)) continue;
                if (x.IsDlc) dlc += size; else patch += size;
            }
            extras.DlcBytes = dlc;
            extras.PatchBytes = patch;
            return dlc + patch;
        }

        // Installe les extras décidés par PlanExtras, l'un après l'autre.
        // Retourne une phrase à ajouter à la notification finale (peut être vide).
        // Ne lève JAMAIS d'exception : une erreur ici ne doit pas faire supprimer le
        // jeu qui vient d'être installé correctement.
        //
        // Suivi : exactement comme pour le jeu de base (StartInstallMonitor) : Go écrits et
        // temps écoulé, dans la fenêtre de progression (mode avec fenêtres) ou dans la
        // notification (mode sans fenêtre), avec « ≈ x % » quand la taille des extras est connue.
        // Enveloppe : bloque la veille de Windows pendant l'installation des extras (appelée depuis l'installation
        // du jeu ET depuis le menu « Installer les DLC et patchs »), puis la libère, même en cas d'erreur.
        // Rien à bloquer quand il n'y a rien à installer.
        private string ProcessExtras(ExtrasPlan plan, string destDir, bool interactive)
        {
            if (plan == null || !plan.Install) return ProcessExtrasCore(plan, destDir, interactive);

            using (new SleepGuard("installation des DLC et patchs de '" + Game.Name + "'"))
            {
                return ProcessExtrasCore(plan, destDir, interactive);
            }
        }

        private string ProcessExtrasCore(ExtrasPlan plan, string destDir, bool interactive)
        {
            if (plan == null) return "";
            if (!plan.Install) return plan.Summary;

            ProgressWindow win = null;
            CancellationTokenSource monitorCts = null;
            Task monitorTask = null;

            try
            {
                // Les extras choisis, dans l'ordre d'exécution (DLC puis patchs).
                var list = plan.Selected;

                // Taille que CETTE sélection d'extras a occupée la dernière fois (0 = inconnue).
                string sizeKey = ExtrasSizeKey(list);
                long expectedExtras = _settings.GetInstalledSize(sizeKey) ?? 0;

                if (interactive)
                    win = OpenProgress(0, "Installation des DLC et patchs…", "Préparation…", false, null, false, true);

                long baseline = ArchiveScanner.GetDirectorySize(destDir);
                monitorCts = new CancellationTokenSource();
                monitorTask = StartInstallMonitor(destDir, baseline, expectedExtras, win, monitorCts.Token);

                var extras = plan.Scan;
                string what = plan.What;

                // Fichiers non reconnus : ceux lancés à la main (cochés par l'utilisateur) ne sont pas « non lancés ».
                var manualPaths = new HashSet<string>(
                    list.Where(x => x.IsManual).Select(x => x.Path), StringComparer.OrdinalIgnoreCase);
                var notLaunched = extras.NotRecognized.Where(f => !manualPaths.Contains(f)).ToList();
                string notRecognizedText = notLaunched.Count > 0 ? DescribeNotRecognized(notLaunched) : "";

                // --- Installation, l'un après l'autre, arrêt au premier échec ---
                for (int i = 0; i < list.Count; i++)
                {
                    var extra = list[i];
                    string label = extra.IsDlc ? "DLC" : "patch";
                    string fileName = Path.GetFileName(extra.Path);
                    if (extra.IsManual) label += " (à la main)";

                    // Étape en cours : lue par le suivi pour les notifications (mode sans fenêtre).
                    _stepLabel = $"{label} {i + 1} sur {list.Count}";

                    if (win != null && !win.InBackground)
                    {
                        SetProgressStatus(win, extra.IsManual
                            ? $"{_stepLabel} : {fileName} — une fenêtre d'installation s'ouvre, suivez ses instructions"
                            : $"{_stepLabel} : {fileName}");
                        // Sans taille connue, la barre avance au nombre d'extras terminés ; avec une
                        // taille connue, c'est le pourcentage estimé (SetInstallProgress) qui la pilote.
                        if (expectedExtras <= 0)
                            SetProgressSteps(win, i, list.Count, null); // null : garde la ligne « Go écrits »
                    }
                    else
                    {
                        ShowProgress($"Installation de '{Game.Name}' : {_stepLabel} ({fileName})…", null);
                    }
                    ArchiveLog.Info(extra.IsCopy
                        ? $"Extra {i + 1}/{list.Count} ({label}, copie de fichiers) : copie du dossier '{extra.Path}' dans '{destDir}'."
                        : $"Extra {i + 1}/{list.Count} ({label}) : lancement de '{extra.Path}' (type {extra.Type}).");

                    string error;
                    bool launched = extra.IsCopy
                        ? RunCopyExtra(extra, destDir, out error)
                        : extra.IsManual
                            ? RunManualExtra(extra, interactive, out error)
                            : RunExtraInstaller(extra, destDir, interactive, out error);
                    if (!launched)
                    {
                        var notRun = list.Skip(i + 1).Select(x => Path.GetFileName(x.Path)).ToList();
                        ArchiveLog.Error($"Extra {i + 1}/{list.Count} '{fileName}' : ÉCHEC ({error}). Arrêt. " +
                                         (notRun.Count == 0
                                             ? "Aucun extra restant."
                                             : "Non lancés : " + string.Join(", ", notRun) + "."));

                        Notify("ArchiveStore_ExtrasFail",
                            $"ArchiveStore : échec de l'installation du {label} '{fileName}' pour '{Game.Name}' ({error}). " +
                            (notRun.Count == 0
                                ? "Aucun autre extra n'était prévu. "
                                : $"Arrêt : {notRun.Count} extra(s) non lancé(s). ") +
                            "Le jeu de base reste installé. Détails : archivestore.log.",
                            NotificationType.Error);

                        return $" Extras interrompus : échec de '{fileName}'.";
                    }

                    ArchiveLog.Info(extra.IsCopy
                        ? $"Extra {i + 1}/{list.Count} '{fileName}' : copié."
                        : $"Extra {i + 1}/{list.Count} '{fileName}' : installé.");

                    // Mémorisé tout de suite : si un extra suivant échoue, celui-ci reste noté « installé ».
                    RememberInstalledExtra(extra);
                }

                // Tous les extras sont installés : on retient leur taille pour estimer la prochaine fois.
                RememberExtrasSize(destDir, baseline, sizeKey);

                string done = $" {what} installé(s).{plan.SkippedNote}";
                if (notLaunched.Count > 0)
                {
                    Notify("ArchiveStore_ExtrasUnknown",
                        $"ArchiveStore : {notRecognizedText} dans les dossiers d'extras de '{Game.Name}' : " +
                        "type d'installeur non reconnu, non lancé(s).",
                        NotificationType.Info);
                    done += $" {notLaunched.Count} fichier(s) non reconnu(s), non lancé(s).";
                }
                return done;
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : erreur pendant l'installation des extras.");
                ArchiveLog.Error($"Extras de '{Game.Name}' : erreur inattendue : {ex.Message}");
                Notify("ArchiveStore_ExtrasFail",
                    $"ArchiveStore : erreur pendant l'installation des DLC et patchs de '{Game.Name}' ({ex.Message}). " +
                    "Le jeu de base reste installé. Détails : archivestore.log.",
                    NotificationType.Error);
                return " Extras interrompus (erreur).";
            }
            finally
            {
                _stepLabel = null;
                StopInstallMonitor(monitorCts, monitorTask); // AVANT de fermer la fenêtre
                CloseProgress(win); // sans effet si win est null
            }
        }

        // Clé de mémoire de la taille installée d'une SÉLECTION d'extras : « NomDuJeu::extras:1a2b3c4d ».
        // L'empreinte est calculée sur la liste triée des identifiants d'installeurs.
        private string ExtrasSizeKey(List<ExtraInstaller> selected)
        {
            var keys = selected.Select(x => (x.Key ?? x.Path ?? "").ToLowerInvariant())
                               .OrderBy(k => k, StringComparer.Ordinal);
            return Game.GameId + ExtrasSizeKeySuffix + ":" + StableHash(string.Join("|", keys)).ToString("x8");
        }

        // Petite empreinte STABLE d'un texte (FNV-1a, 32 bits). On n'utilise pas string.GetHashCode() :
        // sa valeur n'est pas garantie d'un lancement à l'autre, alors qu'ici elle doit toujours
        // donner le même résultat pour retrouver la taille mémorisée.
        private static uint StableHash(string text)
        {
            unchecked
            {
                uint hash = 2166136261;
                foreach (char c in text)
                {
                    hash ^= (uint)c;
                    hash *= 16777619;
                }
                return hash;
            }
        }

        // Note qu'un extra est installé (mémoire par jeu, voir PluginSettings.InstalledExtras).
        // Passe par le thread UI (la sauvegarde des réglages s'y fait). Ne lève jamais d'exception.
        private void RememberInstalledExtra(ExtraInstaller extra)
        {
            try
            {
                string gameId = Game.GameId;
                string key = extra.Key;
                UiHelper.OnUi(() => _settings.AddInstalledExtra(gameId, key));
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Extra '{Path.GetFileName(extra.Path)}' installé mais non mémorisé : {ex.Message}");
            }
        }

        // Note tous les extras trouvés dans la source comme « vus » (voir PluginSettings.SeenExtras) : la notification
        // « nouveaux DLC et patchs » ne concernera que ceux qui apparaîtront ENSUITE. Passe par le thread UI
        // (sauvegarde des réglages). Ne lève jamais d'exception.
        private void MarkExtrasSeen(ExtraScanResult found)
        {
            try
            {
                if (found == null) return;
                string gameId = Game.GameId;
                var keys = found.Installers.Concat(found.Manual).Select(x => x.Key).ToList();
                UiHelper.OnUi(() =>
                {
                    _settings.MarkExtrasSeen(gameId, keys);
                    _settings.Save();
                });
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Extras de '{Game.Name}' : mémorisation des extras vus impossible : {ex.Message}");
            }
        }

        // Un seul avertissement pour TOUS les fichiers non reconnus cochés. Retourne vrai si l'utilisateur
        // accepte de les lancer. Boîte de dialogue native de Playnite (Oui / Non), via le thread UI.
        private bool ConfirmManualRun(List<ExtraInstaller> files)
        {
            string names = string.Join("\n", files.Take(8).Select(x => "  • " + (x.Key ?? Path.GetFileName(x.Path))))
                           + (files.Count > 8 ? "\n  • …" : "");
            string text =
                (files.Count == 1
                    ? "Ce fichier n'est pas d'un type d'installeur connu (ni Inno Setup, ni NSIS, ni MSI) :\n\n"
                    : "Ces fichiers ne sont pas d'un type d'installeur connu (ni Inno Setup, ni NSIS, ni MSI) :\n\n") +
                names +
                "\n\nIls seront lancés tels quels, SANS installation silencieuse : une fenêtre s'ouvrira " +
                "et vous devrez les installer vous-même.\n\n" +
                (string.IsNullOrEmpty(_destDirHint)
                    ? "Si l'installeur demande un dossier, choisissez celui du jeu.\n\n"
                    : "Si l'installeur demande un dossier, choisissez le dossier du jeu :\n" + _destDirHint + "\n\n") +
                "Voulez-vous quand même les exécuter ?";

            return UiHelper.OnUi<bool>(() =>
                _api.Dialogs.ShowMessage(text, "ArchiveStore", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                == MessageBoxResult.Yes);
        }

        // Lance UN fichier d'extra NON RECONNU, avec sa propre fenêtre (aucun argument silencieux : on
        // n'en connaît pas), et attend qu'il se ferme. On ne peut pas lire son résultat : à sa fermeture,
        // on DEMANDE à l'utilisateur si l'installation s'est bien passée (Oui = noté comme installé,
        // Non = reproposé plus tard, et la suite des extras s'arrête). Avec fenêtres seulement.
        // Retourne vrai si l'utilisateur confirme que tout s'est bien passé. Ne lève pas d'exception.
        private bool RunManualExtra(ExtraInstaller extra, bool interactive, out string error)
        {
            error = null;
            if (!interactive)
            {
                // Garde-fou : sans fenêtre (installation directe), on ne lance JAMAIS un inconnu.
                error = "lancement manuel impossible sans fenêtre";
                return false;
            }

            try
            {
                ArchiveLog.Info($"Lancement manuel (type non reconnu) : '{extra.Path}'. Aucun argument silencieux.");

                // UseShellExecute = true : Windows gère lui-même l'éventuelle demande de droits administrateur.
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = extra.Path,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(extra.Path) ?? ""
                };

                var chrono = System.Diagnostics.Stopwatch.StartNew();
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc != null) proc.WaitForExit();
                }
                ArchiveLog.Info($"Lancement manuel de '{Path.GetFileName(extra.Path)}' terminé (durée {DescribeDuration(chrono.Elapsed)}).");
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }

            string fileName = Path.GetFileName(extra.Path);
            // Même question « Attendez la fin » que pour le jeu de base (texte commun : voir Manual.cs).
            bool ok = AskInstallDone(
                fileName,
                "Il sera noté comme installé et ne sera plus proposé.",
                "seulement si elle a échoué ou planté. Il sera de nouveau proposé, et les extras suivants ne seront pas lancés.");

            ArchiveLog.Info($"Lancement manuel de '{fileName}' : réponse de l'utilisateur = {(ok ? "installé" : "non installé")}.");
            if (!ok) error = "l'utilisateur indique que l'installation n'a pas abouti";
            return ok;
        }

        // « EXTRA EN FICHIERS » : copie le contenu d'un dossier de DLC ou de patchs (sans installeur) dans le
        // dossier du jeu, avec la même arborescence.
        //   • les fichiers du même nom sont REMPLACÉS, les autres sont ajoutés ;
        //   • RIEN n'est supprimé (ni dans le jeu, ni ailleurs) ;
        //   • la SOURCE n'est jamais modifiée (lecture seule) ;
        //   • les jonctions ne sont pas suivies (voir ArchiveScanner.CopyDirectory).
        // Garde-fou : on refuse si la source et la destination s'imbriquent (PathsOverlap), pour ne jamais copier
        // une archive sur elle-même. Retourne vrai si tout est copié ; sinon faux, avec une explication dans
        // « error ». Ne lève pas d'exception.
        private bool RunCopyExtra(ExtraInstaller extra, string destDir, out string error)
        {
            error = null;
            try
            {
                if (string.IsNullOrEmpty(extra.Path) || !Directory.Exists(extra.Path))
                {
                    error = "dossier source introuvable";
                    return false;
                }
                if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
                {
                    error = "dossier d'installation du jeu introuvable";
                    return false;
                }
                if (ArchiveScanner.PathsOverlap(extra.Path, destDir))
                {
                    error = "la source et le dossier du jeu s'imbriquent : copie refusée par sécurité";
                    return false;
                }

                var chrono = System.Diagnostics.Stopwatch.StartNew();
                ArchiveScanner.CopyDirectory(extra.Path, destDir, null, CancellationToken.None);
                ArchiveLog.Info($"Copie de '{extra.FolderName}' terminée (durée {DescribeDuration(chrono.Elapsed)}).");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // ------------------------------------------------------------------
        // PORTÉE D'INSTALLATION (Inno Setup) : « utilisateur actuel » ou « tous les utilisateurs ».
        // Selon le réglage : option fixe (/CURRENTUSER ou /ALLUSERS), ou question posée UNE SEULE fois par
        // installation (le jeu de base et ses extras partagent la réponse). Sans fenêtre (installation
        // directe) et réglage « demander » : aucune option, l'installeur fait ce que son script prévoit.
        // Renvoie le texte à ajouter aux arguments (avec espace devant, ou vide).
        // ------------------------------------------------------------------
        private string _innoScopeArg; // null = pas encore décidé

        private string ResolveInnoScopeArg(bool interactive, out bool cancelled)
        {
            cancelled = false;
            if (_innoScopeArg != null) return _innoScopeArg;

            switch (_settings.InstallScope)
            {
                case InstallScope.CurrentUser:
                    _innoScopeArg = " /CURRENTUSER";
                    break;
                case InstallScope.AllUsers:
                    _innoScopeArg = " /ALLUSERS";
                    break;
                default:
                    if (!interactive)
                    {
                        _innoScopeArg = "";
                        break;
                    }
                    var choices = new List<MenuChoice>
                    {
                        new MenuChoice("Utilisateur actuel", "Sans demande UAC (droits administrateur).", " /CURRENTUSER"),
                        new MenuChoice("Tous les utilisateurs", "L'UAC peut s'afficher.", " /ALLUSERS")
                    };
                    var choice = UiHelper.OnUi<MenuChoice>(() =>
                        ConsoleDialogs.Choose(null, "Installation", "Pour qui installer ce jeu ?",
                            "« " + Game.Name + " » : installer pour l'utilisateur actuel seulement, ou pour tous les utilisateurs ?",
                            choices, 0));
                    if (choice == null)
                    {
                        cancelled = true;
                        ArchiveLog.Info($"Installation de '{Game.Name}' : choix « pour qui installer » annulé par l'utilisateur.");
                        return "";
                    }
                    _innoScopeArg = (string)choice.Tag;
                    break;
            }
            ArchiveLog.Info($"Portée d'installation Inno Setup : '{(_innoScopeArg.Length == 0 ? "(aucune option)" : _innoScopeArg.Trim())}'.");
            return _innoScopeArg;
        }

        // Lance UN installeur d'extra en silencieux et attend sa fin.
        // Retourne true si le code de retour est 0 (ou 3010 = succès MSI, redémarrage conseillé).
        // Sinon false, avec une explication dans "error". Ne lève pas d'exception.
        private bool RunExtraInstaller(ExtraInstaller extra, string destDir, bool interactive, out string error)
        {
            error = null;
            var a = _settings.SilentArguments ?? new SilentArgs();
            string file, arguments;

            // Mêmes arguments que pour le jeu de base (voir RunSilentInstaller).
            switch (extra.Type)
            {
                case InstallerType.InnoSetup:
                    file = extra.Path;
                    bool scopeCancelled;
                    string scopeArg = ResolveInnoScopeArg(interactive, out scopeCancelled);
                    if (scopeCancelled)
                    {
                        error = "choix « pour qui installer » annulé";
                        return false;
                    }
                    arguments = $"{a.InnoArgs}{scopeArg} /DIR=\"{destDir}\"";
                    break;
                case InstallerType.Nullsoft:
                    file = extra.Path;
                    // NSIS : /D= doit être le DERNIER argument, SANS guillemets.
                    arguments = $"{a.NsisArgs} /D={destDir}";
                    break;
                case InstallerType.Msi:
                    file = "msiexec.exe";
                    arguments = $"/i \"{extra.Path}\" {a.MsiArgs} TARGETDIR=\"{destDir}\"";
                    break;
                default:
                    error = "type d'installeur non pris en charge";
                    return false;
            }

            // Installeur Inno : on demande aussi son journal détaillé (voir ArchiveLog.WithInnoLog).
            string innoLog = null;
            if (extra.Type == InstallerType.InnoSetup)
            {
                arguments = ArchiveLog.WithInnoLog(arguments,
                    $"{Game.GameId}_extra_{extra.FolderName}_{Path.GetFileNameWithoutExtension(extra.Path)}",
                    out innoLog);
                if (innoLog != null) ArchiveLog.Info($"Journal Inno Setup de cet extra : {innoLog}");
            }

            ArchiveLog.Info($"Commande : {file} {arguments}");

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = file,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(extra.Path) ?? ""
                };

                // Pas de redirection de sortie (risque de blocage), comme pour le jeu de base.
                var chrono = System.Diagnostics.Stopwatch.StartNew();
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    proc.WaitForExit();
                    int code = proc.ExitCode;
                    ArchiveLog.Info($"Code de retour : {code} (durée {DescribeDuration(chrono.Elapsed)}).");

                    if (code != 0 && code != 3010)
                    {
                        // Même tolérance que pour le jeu de base (voir RunSilentInstaller) : plantage
                        // Windows à la fermeture + journal d'Inno indiquant une installation réussie.
                        // (Pas de test d'exécutable ici : un DLC n'en apporte pas forcément un.)
                        string why;
                        if (extra.Type == InstallerType.InnoSetup && IsInnoCrashAtExit(code, innoLog, out why))
                        {
                            ArchiveLog.Warn($"Extra '{Path.GetFileName(extra.Path)}' : plantage de l'installeur à la fermeture (code {code}) " +
                                            "mais son journal indique une installation réussie : considéré comme installé.");
                            Notify("ArchiveStore_ExtraCrash",
                                $"ArchiveStore : l'installeur de '{Path.GetFileName(extra.Path)}' a planté à la fermeture " +
                                $"(code {code}), mais l'installation est terminée.",
                                NotificationType.Info);
                            return true;
                        }

                        error = $"code de retour {code}";
                        return false;
                    }
                }
                return true;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 740)
            {
                // 740 = ERROR_ELEVATION_REQUIRED : droits administrateur exigés.
                error = "droits administrateur exigés (code 740)";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // « 2 DLC et 1 patch », « 3 patchs », « 1 DLC »…
        private static string DescribeExtras(int dlcCount, int patchCount)
        {
            var parts = new List<string>();
            if (dlcCount > 0) parts.Add(dlcCount + " DLC");
            if (patchCount > 0) parts.Add(patchCount + (patchCount > 1 ? " patchs" : " patch"));
            return string.Join(" et ", parts);
        }

        // « 1 fichier non reconnu (a.exe) » / « 3 fichiers non reconnus (a.exe, b.exe, c.exe) ».
        private static string DescribeNotRecognized(List<string> files)
        {
            var names = files.Take(3).Select(Path.GetFileName).ToList();
            string list = string.Join(", ", names) + (files.Count > 3 ? ", …" : "");
            return files.Count == 1
                ? $"1 fichier non reconnu ({list})"
                : $"{files.Count} fichiers non reconnus ({list})";
        }

        // Mémorise la taille réellement écrite par les extras (différence de taille de la
        // destination avant/après). Ne lève jamais d'exception.
        private void RememberExtrasSize(string destDir, long baseline, string sizeKey)
        {
            try
            {
                long delta = ArchiveScanner.GetDirectorySize(destDir) - baseline;
                if (delta <= 0) return;

                _settings.SetInstalledSize(sizeKey, delta);
                UiHelper.OnUi(() => _settings.Save());
                ArchiveLog.Info($"Taille installée des extras de '{Game.Name}' mémorisée : {ArchiveScanner.FormatSize(delta)}.");
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Taille installée des extras de '{Game.Name}' non mémorisée : {ex.Message}");
            }
        }

        // ==================================================================
        // MENU CLIC DROIT — « Installer les DLC et patchs » sur un jeu DÉJÀ installé
        // (Bureau et Plein écran : en Plein écran, le menu est dans « Extensions » sous le jeu)
        //
        // Même moteur que l'installation du jeu : PlanExtras (fenêtre à cases à cocher, ou
        // installation automatique) puis ProcessExtras. Ce qui change :
        //   - la recherche se fait AU CLIC, dans le dossier SOURCE du jeu : un DLC ou un patch ajouté
        //     à la source après l'installation du jeu est donc bien trouvé ;
        //   - on retire de la liste les extras DÉJÀ installés (mémoire par jeu) : jamais de doublon ;
        //   - s'il n'y a rien à installer, une petite notification le dit (ici seulement : à
        //     l'installation d'un jeu, l'absence d'extras ne dit rien) ;
        //   - pas de jeu à sauver : si l'espace manque, on n'installe rien et on le dit.
        // Réglage « Installation directe » : aucune fenêtre, tout ce qui reste s'installe, avec une
        // notification de progression (comme pour l'installation d'un jeu).
        // La source n'est JAMAIS modifiée ; rien n'est supprimé.
        // ==================================================================

        // Garde-fou : un seul traitement d'extras à la fois pour un même jeu (double clic, etc.).
        private static readonly HashSet<Guid> _extrasBusy = new HashSet<Guid>();

        // Point d'entrée du CLIC sur la notification « nouveaux DLC et patchs » (thread UI). Même déroulé que le
        // menu clic droit, mais la fenêtre de choix s'ouvre toujours : on n'installe jamais sur un simple clic.
        public void InstallExtrasFromNotification()
        {
            _forceExtrasWindow = true;
            InstallExtrasFromMenu();
        }

        // Point d'entrée du menu (thread UI) : vérifications rapides, puis travail en arrière-plan.
        public void InstallExtrasFromMenu()
        {
            var profile = _settings.GetActiveProfile();
            var sources = profile?.GetSources();
            if (profile == null || sources == null || sources.Count == 0)
            {
                Notify("ArchiveStore_ExtrasMenu",
                    "ArchiveStore : aucun profil actif avec une Source configurée : impossible de chercher les DLC et patchs.",
                    NotificationType.Error);
                return;
            }

            string destDir = Game.InstallDirectory;
            if (string.IsNullOrEmpty(destDir) || !Directory.Exists(destDir))
            {
                Notify("ArchiveStore_ExtrasMenu",
                    $"ArchiveStore : le dossier d'installation de '{Game.Name}' est introuvable.",
                    NotificationType.Error);
                return;
            }

            // PLUSIEURS SOURCES : le dossier du jeu est cherché dans chacune, dans l'ordre.
            string sourceGameDir = ArchiveScanner.FindGameSourceDir(sources, Game.GameId);
            if (sourceGameDir == null)
            {
                Notify("ArchiveStore_ExtrasMenu",
                    $"ArchiveStore : dossier source de '{Game.Name}' introuvable dans le profil '{profile.Name}'. " +
                    "Une source est peut-être inaccessible (disque ou NAS débranché ou éteint).",
                    NotificationType.Error);
                return;
            }

            lock (_extrasBusy)
            {
                if (!_extrasBusy.Add(Game.Id))
                {
                    Notify("ArchiveStore_ExtrasMenu",
                        $"ArchiveStore : une installation de DLC et patchs est déjà en cours pour '{Game.Name}'.",
                        NotificationType.Info);
                    return;
                }
            }

            // Fenêtres (dans les deux modes, Bureau et Plein écran) seulement si l'option « Installation
            // directe » est décochée, ou si l'utilisateur arrive par la notification « nouveaux DLC et patchs »
            // (il a cliqué pour choisir : la fenêtre est justifiée). On lit le réglage ICI (thread UI).
            bool interactive = !_settings.DirectInstall || _forceExtrasWindow;

            Task.Run(() => RunExtrasFromMenu(sourceGameDir, destDir, interactive));
        }

        // Déroulé du menu, sur un thread d'arrière-plan. Ne laisse jamais une exception s'échapper.
        private void RunExtrasFromMenu(string sourceGameDir, string destDir, bool interactive)
        {
            ProgressWindow analysis = null;
            try
            {
                _destDirHint = destDir; // rappelé dans l'avertissement des extras lancés à la main
                ArchiveLog.Info($"Menu « Installer les DLC et patchs » pour '{Game.Name}' ({Game.GameId}) : " +
                                $"source '{sourceGameDir}', destination '{destDir}'.");

                var scan = new ScanResult
                {
                    FolderName = Game.GameId,
                    FullSourcePath = sourceGameDir,
                    DestinationDirectory = destDir
                };

                // --- Recherche (lecture de fichiers : hors thread UI) ---
                if (interactive)
                    analysis = OpenProgress(0, "Recherche des DLC et patchs…", "Analyse du dossier source…", false, null);
                else
                    ShowProgress($"Recherche des DLC et patchs de '{Game.Name}'…", null);

                ExtraScanResult found = null;
                long foundBytes = 0;
                try
                {
                    // Comme à l'installation : seuls les jeux installés par un installeur reconnu ont des
                    // extras à lancer. Un jeu portable est copié EN ENTIER, extras compris.
                    ArchiveScanner.DetectInstaller(sourceGameDir, _settings.ExcludedExePatterns, scan,
                        _settings.ExtraInstallerWords, _settings.InstallerWordsAnywhere);

                    // Un jeu installé À LA MAIN (cas ambigu : installeur non reconnu, lancé avec sa fenêtre)
                    // a lui aussi des extras possibles : « Indéterminé » ne bloque donc pas le menu. Seul un
                    // dossier SANS aucun installeur (vrai jeu portable) n'en a pas.
                    if (IsRecognizedInstaller(scan.DetectedType) || scan.DetectedType == InstallerType.Unknown)
                        found = FindExtrasWithSize(sourceGameDir, out foundBytes);
                }
                finally
                {
                    CloseProgress(analysis);
                    analysis = null;
                    RemoveProgress();
                }

                if (!IsRecognizedInstaller(scan.DetectedType) && scan.DetectedType != InstallerType.Unknown)
                {
                    ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' : jeu portable (type détecté {scan.DetectedType}), rien à installer.");
                    Notify("ArchiveStore_ExtrasMenuNone",
                        $"ArchiveStore : '{Game.Name}' a été copié tel quel (jeu portable) : ses DLC et patchs, " +
                        "s'il y en a, sont déjà inclus dans la copie.",
                        NotificationType.Info);
                    return;
                }

                if (found == null)
                {
                    Notify("ArchiveStore_ExtrasMenu",
                        $"ArchiveStore : recherche des DLC et patchs de '{Game.Name}' impossible. Détails : archivestore.log.",
                        NotificationType.Error);
                    return;
                }

                // Tout ce que la fenêtre va montrer est désormais « vu » : la notification de nouveautés
                // ne le signalera pas (même si on décide de ne rien installer).
                MarkExtrasSeen(found);

                if (found.Installers.Count == 0 && found.Manual.Count == 0)
                {
                    ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' : aucun dossier de DLC ou de patchs (nom commençant par « __ »), ou ils sont vides.");
                    Notify("ArchiveStore_ExtrasMenuNone",
                        $"ArchiveStore : aucun dossier de DLC ou de patchs trouvé pour '{Game.Name}'. " +
                        "Leur nom doit commencer par deux underscores et contenir DLC, patch ou update (par exemple « __DLC » ou « __Patch »).",
                        NotificationType.Info);
                    return;
                }

                // Les fichiers NON RECONNUS ne se lancent qu'à la main, depuis la fenêtre de choix.
                bool windowPossible = ExtrasWindowWanted(interactive);

                if (found.Installers.Count == 0 && !windowPossible)
                {
                    ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' : seulement des fichiers non reconnus, et pas de fenêtre de choix.");
                    Notify("ArchiveStore_ExtrasMenuNone",
                        $"ArchiveStore : dossier de DLC ou de patchs trouvé pour '{Game.Name}', mais {DescribeNotRecognized(found.NotRecognized)} : " +
                        "type d'installeur non reconnu. Ils ne se lancent qu'à la main, depuis la fenêtre de choix " +
                        "(« Installation directe » décochée et confirmation activée).",
                        NotificationType.Info);
                    return;
                }

                // --- On retire ce qui est déjà installé ---
                var alreadyInstalled = _settings.GetInstalledExtras(Game.GameId);
                var pending = found.Installers.Where(x => !alreadyInstalled.Contains(x.Key)).ToList();
                var pendingManual = windowPossible
                    ? found.Manual.Where(x => !alreadyInstalled.Contains(x.Key)).ToList()
                    : new List<ExtraInstaller>();

                if (pending.Count == 0 && pendingManual.Count == 0)
                {
                    ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' : les {found.Installers.Count + found.Manual.Count} extra(s) trouvé(s) sont déjà installés.");
                    Notify("ArchiveStore_ExtrasMenuNone",
                        $"ArchiveStore : tous les DLC et patchs trouvés pour '{Game.Name}' sont déjà installés.",
                        NotificationType.Info);
                    return;
                }

                // Résultat « filtré » : seulement ce qui reste à installer, avec ses propres tailles.
                var pendingScan = new ExtraScanResult
                {
                    Installers = pending,
                    Manual = pendingManual,
                    NotRecognized = found.NotRecognized,
                    FolderBytes = found.FolderBytes
                };
                long pendingBytes = SumExtraBytes(pendingScan);

                // --- Choix (fenêtre à cases à cocher, ou tout en automatique) ---
                bool cancelled;
                var plan = PlanExtras(pendingScan, pendingBytes, scan, interactive, true, out cancelled);
                if (cancelled)
                {
                    ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' : annulé dans la fenêtre de choix.");
                    return;
                }
                if (plan != null && plan.MarkedOnly)
                {
                    // « Déjà installés » : seulement noté en mémoire, rien n'a été lancé.
                    Notify("ArchiveStore_ExtrasMarked", $"'{Game.Name}' :{plan.Summary}", NotificationType.Info);
                    return;
                }
                if (plan == null || !plan.Install) return; // erreur ou rien à faire : déjà signalé

                // --- Espace libre (recalculé : la fenêtre est restée ouverte un moment) ---
                long free = ArchiveScanner.GetFreeSpace(destDir);
                if (!ArchiveScanner.HasEnoughSpace(plan.ExtrasBytes, free))
                {
                    ArchiveLog.Warn($"Menu DLC et patchs de '{Game.Name}' : espace insuffisant. " + DescribeSpace(plan.ExtrasBytes, free));
                    Notify("ArchiveStore_ExtrasNoSpace",
                        $"ArchiveStore : pas assez de stockage pour installer {plan.What} sur '{Game.Name}'. " +
                        DescribeSpace(plan.ExtrasBytes, free) +
                        $" Il manque environ {ArchiveScanner.FormatSize(ArchiveScanner.GetMissingSpace(plan.ExtrasBytes, free))}. " +
                        "Rien n'a été installé.",
                        NotificationType.Error);
                    return;
                }

                // --- Installation ---
                string summary = ProcessExtras(plan, destDir, interactive);

                // Des DLC / patchs ont changé la taille du jeu installé : on la remet à jour dans Playnite.
                {
                    var sizeDbId = Game.Id;
                    var sizeGameId = Game.GameId;
                    var sizeDir = destDir;
                    Task.Run(() => InstalledSizeHelper.Refresh(_api, sizeDbId, sizeGameId, sizeDir));
                }

                ArchiveLog.Info($"Menu DLC et patchs de '{Game.Name}' terminé :{summary}");
                Notify("ArchiveStore_ExtrasMenuDone", $"'{Game.Name}' :{summary}", NotificationType.Info);
            }
            catch (Exception ex)
            {
                logger.Error(ex, "ArchiveStore : installation des DLC et patchs depuis le menu échouée.");
                ArchiveLog.Error($"Menu DLC et patchs de '{Game.Name}' : erreur inattendue : {ex.Message}");
                Notify("ArchiveStore_ExtrasFail",
                    $"ArchiveStore : erreur pendant l'installation des DLC et patchs de '{Game.Name}' ({ex.Message}). " +
                    "Détails : archivestore.log.",
                    NotificationType.Error);
            }
            finally
            {
                CloseProgress(analysis); // sans effet si analysis est null
                RemoveProgress();
                lock (_extrasBusy) { _extrasBusy.Remove(Game.Id); }
            }
        }
    }
}
