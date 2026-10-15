using System;
using System.IO;
using System.Linq;
using System.Windows;
using Playnite.SDK;
using Playnite.SDK.Models;
using Playnite.SDK.Plugins;

namespace ArchiveStore
{
    // ======================================================================
    // ArchiveInstallController — partie « INSTALLATION MANUELLE » (livraison B).
    //
    // CONTEXTE : quand aucun installeur n'est reconnu par signature (ni Inno Setup, ni NSIS, ni MSI)
    // mais qu'un fichier du dossier ressemble à un installeur (« setup.exe », « install… », ou un mot
    // ajouté dans les paramètres), c'est le CAS AMBIGU. En Bureau comme en Plein écran, l'utilisateur choisit dans la
    // fenêtre d'installation : Installer, Copier ou Annuler (en Plein écran, la fenêtre prévient que l'installeur a
    // sa propre fenêtre, non commandable à la manette). Ce fichier gère le choix « Installer » :
    // l'installeur est lancé AVEC SA FENÊTRE (aucune option silencieuse : on n'en connaît pas), et
    // c'est L'UTILISATEUR qui y choisit le dossier d'installation.
    //
    // PÉDAGOGIE — on ne peut ni imposer le dossier ni savoir si l'installation a réussi. Le plugin :
    //   1. avertit AVANT le lancement et rappelle le chemin EXACT à choisir (Destination\NomDuDossier) ;
    //   2. attend la fin du programme lancé (WaitForExit) — en arrière-plan, Playnite ne se fige pas ;
    //   3. DEMANDE ensuite si l'installation est terminée et réussie (un installeur peut en lancer un
    //      autre puis se fermer : seul l'utilisateur sait quand c'est vraiment fini) ;
    //   4. VÉRIFIE que le jeu est bien dans Destination\NomDuDossier avec un exécutable de jeu.
    // Quoi qu'il arrive, RIEN n'est supprimé : on ne sait pas ce que l'installeur a écrit.
    // ======================================================================
    public partial class ArchiveInstallController
    {
        // Titre de la question posée après un installeur lancé à la main (jeu de base ET extras).
        private const string DoneQuestionTitle = "ArchiveStore — ⚠ Attendez la fin de l'installation";

        // ------------------------------------------------------------------
        // Texte COMMUN de la question « terminée et réussie ? », pour le jeu de base et pour les
        // DLC/patchs lancés à la main (même avertissement, même ton).
        //   subject : ce qui a été lancé (nom du jeu, ou nom du fichier d'extra)
        //   yesNote : phrase ajoutée après « Oui » (ex. « Vous pourrez alors choisir l'exécutable. »)
        //   noText  : suite de « Non : » (ce qui se passe si l'installation a échoué)
        // ------------------------------------------------------------------
        private static string BuildDoneQuestion(string subject, string yesNote, string noText)
        {
            return
                "Installation de « " + subject + " ».\n\n" +
                "⚠ Ne répondez pas trop vite !\n\n" +
                "Attendez que l'installation soit TERMINÉE et que la fenêtre de l'installeur soit fermée, " +
                "même si cela semble long ou bloqué. Cette fenêtre reste ouverte : prenez votre temps.\n\n" +
                "• Oui : l'installation est terminée et a réussi." + (string.IsNullOrEmpty(yesNote) ? "" : " " + yesNote) + "\n" +
                "• Non : " + noText + "\n\n" +
                "L'installation est-elle terminée et réussie ?";
        }

        // Pose la question (Oui / Non) via la boîte de dialogue native de Playnite, sur le thread UI.
        private bool AskInstallDone(string subject, string yesNote, string noText)
        {
            string text = BuildDoneQuestion(subject, yesNote, noText);
            return UiHelper.OnUi<bool>(() =>
                _api.Dialogs.ShowMessage(text, DoneQuestionTitle, MessageBoxButton.YesNo, MessageBoxImage.Warning)
                == MessageBoxResult.Yes);
        }

        // ------------------------------------------------------------------
        // Installation MANUELLE du jeu de base (cas ambigu, choix « Installer »).
        // Appelée seulement quand l'utilisateur a choisi « Installer » dans la fenêtre du cas ambigu (l'appelant
        // le garantit). Ne supprime JAMAIS rien.
        //   Ok        : le jeu est bien dans destDir, avec au moins un exécutable de jeu.
        //   Abandoned : refus au lancement, échec, réponse « Non » ou jeu introuvable (voir RunWorkflow :
        //               le jeu reste « à installer », rien n'est supprimé).
        // ------------------------------------------------------------------
        private StepResult RunManualInstaller(ScanResult scan, string destDir)
        {
            string installer = scan.DetectedInstallerPath;
            string installerName = Path.GetFileName(installer ?? "");

            if (string.IsNullOrEmpty(installer) || !File.Exists(installer))
            {
                ArchiveLog.Error($"Installation manuelle de '{Game.Name}' impossible : installeur introuvable ('{installer}').");
                Notify("ArchiveStore_Err",
                    $"ArchiveStore : installeur introuvable pour '{Game.Name}'.",
                    NotificationType.Error);
                return StepResult.Abandoned;
            }

            ArchiveLog.Info($"Installation MANUELLE de '{Game.Name}' : installeur '{installer}' (type non reconnu), " +
                            $"dossier attendu '{destDir}'.");

            // La notification « Analyse… » (mode sans fenêtre) ne doit pas rester affichée pendant l'attente.
            RemoveProgress();

            // 1. Avertissement AVANT le lancement, avec le chemin exact à choisir dans l'installeur.
            string warning =
                "⚠ Cet installeur n'est pas reconnu : « " + installerName + " » va s'ouvrir avec sa propre fenêtre.\n\n" +
                "Quand il vous demande où installer, choisissez EXACTEMENT ce dossier, celui du profil " +
                "ArchiveStore actif :\n\n" +
                destDir + "\n\n" +
                "Sinon, ArchiveStore ne retrouvera pas le jeu et il restera « à installer ».\n\n" +
                "Lancer l'installeur maintenant ?";

            bool go = UiHelper.OnUi<bool>(() =>
                _api.Dialogs.ShowMessage(warning, "ArchiveStore — Installation manuelle",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
            if (!go)
            {
                ArchiveLog.Info($"Installation manuelle de '{Game.Name}' : lancement refusé par l'utilisateur.");
                Notify("ArchiveStore_InstallCancelled",
                    $"ArchiveStore : installation de '{Game.Name}' annulée. Rien n'a été installé ni supprimé.",
                    NotificationType.Info);
                return StepResult.Abandoned;
            }

            // 2. Lancement (UseShellExecute = true : Windows gère lui-même l'éventuelle demande de droits
            //    administrateur) et attente de la fin du programme lancé, en arrière-plan.
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = installer,
                    UseShellExecute = true,
                    WorkingDirectory = Path.GetDirectoryName(installer) ?? ""
                };

                var chrono = System.Diagnostics.Stopwatch.StartNew();
                using (var proc = System.Diagnostics.Process.Start(psi))
                {
                    if (proc != null)
                    {
                        proc.WaitForExit();
                        // Le code de retour n'est PAS utilisé pour décider (seul l'utilisateur sait) :
                        // on l'écrit seulement dans le journal.
                        int code = 0;
                        try { code = proc.ExitCode; } catch { }
                        ArchiveLog.Info($"Installeur manuel '{installerName}' fermé (code {code}, durée {DescribeDuration(chrono.Elapsed)}).");
                    }
                }
            }
            catch (Exception ex)
            {
                ArchiveLog.Error($"Installation manuelle de '{Game.Name}' : lancement impossible : {ex.Message}");
                Notify("ArchiveStore_Err",
                    $"ArchiveStore : impossible de lancer l'installeur de '{Game.Name}' — {ex.Message}",
                    NotificationType.Error);
                return StepResult.Abandoned;
            }

            // 3. La question « terminée et réussie ? » (texte « Attendez la fin »).
            bool done = AskInstallDone(
                Game.Name,
                "Vous pourrez alors choisir l'exécutable du jeu.",
                "seulement si elle a échoué ou planté (le jeu n'est pas installé).");
            ArchiveLog.Info($"Installation manuelle de '{Game.Name}' : réponse de l'utilisateur = {(done ? "terminée et réussie" : "échec ou non terminée")}.");

            if (!done)
            {
                ArchiveLog.Warn($"Installation manuelle de '{Game.Name}' non réussie selon l'utilisateur : le jeu n'est pas déclaré installé, " +
                                $"RIEN n'est supprimé (dossier attendu '{destDir}').");
                Notify("ArchiveStore_InstallFailedManual",
                    $"ArchiveStore : installation de '{Game.Name}' non réussie. Le jeu reste « à installer » et rien n'a été supprimé : " +
                    $"si l'installeur a laissé des fichiers, vérifiez ou supprimez vous-même le dossier '{destDir}'.",
                    NotificationType.Info);
                return StepResult.Abandoned;
            }

            // 4. Vérification : le jeu est-il bien là où le plugin le cherche ?
            bool found = false;
            try
            {
                found = Directory.Exists(destDir)
                        && ArchiveScanner.FindGameExes(destDir, _settings.ExcludedExePatterns).Count > 0;
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn($"Installation manuelle de '{Game.Name}' : vérification de '{destDir}' impossible : {ex.Message}");
            }

            if (!found)
            {
                ArchiveLog.Warn($"Installation manuelle de '{Game.Name}' : aucun exécutable de jeu trouvé dans '{destDir}'. " +
                                "Le jeu reste « à installer », rien n'est supprimé.");
                Notify("ArchiveStore_InstallNotFound",
                    $"ArchiveStore : installation de '{Game.Name}' non reconnue à l'endroit attendu ({destDir}). Le jeu reste « à installer ».",
                    NotificationType.Error);

                string notFound =
                    "L'installation n'a pas été trouvée à l'endroit attendu :\n\n" + destDir + "\n\n" +
                    "Aucun exécutable de jeu n'y a été trouvé. L'installeur a peut-être installé le jeu dans un autre dossier.\n\n" +
                    "Le jeu reste « à installer ». RIEN n'a été supprimé. Vous pouvez recommencer en choisissant exactement le dossier indiqué ci-dessus.";
                UiHelper.OnUi(() =>
                {
                    _api.Dialogs.ShowMessage(notFound, "ArchiveStore — Installation non trouvée",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                });
                return StepResult.Abandoned;
            }

            ArchiveLog.Info($"Installation manuelle de '{Game.Name}' : jeu trouvé dans '{destDir}'.");
            return StepResult.Ok;
        }
    }
}
