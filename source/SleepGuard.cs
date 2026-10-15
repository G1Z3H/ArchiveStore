using System;
using System.Runtime.InteropServices;

namespace ArchiveStore
{
    // ======================================================================
    // SleepGuard — empêche Windows de se mettre en veille PENDANT une longue opération
    // (installation, copie, installation des DLC et patchs).
    //
    // Utilisation (le bloc using libère la demande à la fin, même en cas d'erreur ou d'annulation) :
    //
    //     using (new SleepGuard("installation de 'Mon jeu'"))
    //     {
    //         ... longue opération ...
    //     }
    //
    // PÉDAGOGIE — SetThreadExecutionState (fonction de Windows, documentée par Microsoft) :
    //   • ES_SYSTEM_REQUIRED  : remet à zéro le minuteur d'inactivité du système, qui n'a donc plus
    //                           de raison de passer en veille ;
    //   • ES_CONTINUOUS       : l'état reste en vigueur jusqu'au prochain appel qui utilise ES_CONTINUOUS
    //                           sans autre drapeau. C'est ce second appel que fait Dispose().
    //   • Les drapeaux décrivent les besoins « du thread » : on APPELLE donc Dispose sur le MÊME thread que
    //     le constructeur. C'est le cas ici, car le using entoure du code synchrone (sans await).
    //
    // CE QUE ÇA NE FAIT PAS (précisé par la documentation Microsoft) :
    //   • ça n'empêche pas l'utilisateur de mettre le PC en veille (fermeture du capot, bouton
    //     d'alimentation) : seule la veille automatique par inactivité est bloquée ;
    //   • ça ne garde pas l'écran allumé (on n'utilise pas ES_DISPLAY_REQUIRED) : l'écran peut s'éteindre.
    //
    // Cette classe ne lève JAMAIS d'exception : au pire, la veille n'est pas bloquée (c'est écrit dans le
    // journal) et l'opération continue normalement.
    // ======================================================================
    internal sealed class SleepGuard : IDisposable
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint SetThreadExecutionState(uint esFlags);

        private const uint ES_CONTINUOUS = 0x80000000;
        private const uint ES_SYSTEM_REQUIRED = 0x00000001;

        private readonly string _reason;
        private readonly int _threadId;
        private bool _active;

        public SleepGuard(string reason)
        {
            _reason = reason ?? "";
            _threadId = Environment.CurrentManagedThreadId;

            try
            {
                // Le retour (état précédent) n'est pas utilisé comme preuve de réussite : la documentation
                // dit seulement qu'un échec renvoie NULL, et un état précédent peut aussi valoir 0.
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                _active = true;
                ArchiveLog.Info("Mise en veille de Windows bloquée pendant : " + _reason + ".");
            }
            catch (Exception ex)
            {
                _active = false;
                ArchiveLog.Warn("Impossible de bloquer la mise en veille (" + _reason + ") : " + ex.Message);
            }
        }

        public void Dispose()
        {
            if (!_active) return;
            _active = false;

            try
            {
                if (Environment.CurrentManagedThreadId != _threadId)
                    ArchiveLog.Warn("Veille : libération demandée depuis un autre thread que celui qui l'a bloquée (" + _reason + ").");

                SetThreadExecutionState(ES_CONTINUOUS); // plus aucun besoin : Windows peut de nouveau s'endormir
                ArchiveLog.Info("Mise en veille de Windows de nouveau autorisée (fin de : " + _reason + ").");
            }
            catch (Exception ex)
            {
                ArchiveLog.Warn("Libération de la veille impossible (" + _reason + ") : " + ex.Message);
            }
        }
    }
}
