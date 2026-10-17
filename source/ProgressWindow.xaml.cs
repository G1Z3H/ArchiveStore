using System;
using System.ComponentModel;
using System.Windows;
using ArgentToolkit;

namespace ArchiveStore
{
    // ======================================================================
    // Code-behind de la fenêtre de progression (ÉTAPE C).
    //
    // RÔLE : afficher l'avancement d'une copie récursive (jeux portables),
    // ou une simple barre "en mouvement" (analyse, installeur silencieux).
    //
    // ⚠️ RÈGLE D'OR DES THREADS WPF (à ne JAMAIS oublier) :
    // Une fenêtre WPF appartient au "thread UI". On ne peut la lire/modifier
    // QUE depuis ce thread. Or la copie tourne en arrière-plan (Task.Run).
    // Le plugin ne touche donc PAS cette fenêtre directement : il appelle
    // UpdateProgress()/SetStatus()/Close() VIA le Dispatcher, qui rapatrie
    // l'exécution sur le thread UI. Ce fichier, lui, peut manipuler ses
    // propres contrôles librement : il EST déjà sur le thread UI.
    //
    // MANETTE / CLAVIER : B (ou Échap) propose d'annuler, avec une confirmation (Non par défaut).
    // A ne fait rien ici : une pression distraite ne doit rien déclencher pendant une copie.
    // ======================================================================
    public partial class ProgressWindow : ConsoleWindow
    {
        // Taille totale attendue (0 = inconnue → barre indéterminée).
        private readonly long _totalSize;
        private readonly bool _allowCancel;

        private bool _allowClose;       // vrai uniquement quand le PLUGIN ferme la fenêtre
        private bool _cancelRequested;
        private bool _asking;           // la question « Annuler ? » est affichée

        // Texte de la question « Annuler l'installation ? ». Par défaut, annuler SUPPRIME ce qui a déjà été copié
        // (dossier créé par l'installation). Si le dossier existait déjà (choix « Écraser »), rien n'est supprimé :
        // voir KeepFilesOnCancel.
        private string _cancelDetail = "Les fichiers déjà copiés seront supprimés.";

        /// <summary>Déclenché quand l'utilisateur confirme l'annulation.</summary>
        public event EventHandler CancelRequested;

        public ProgressWindow(long totalSize, string title, bool allowCancel)
        {
            InitializeComponent();
            _totalSize = totalSize;
            _allowCancel = allowCancel;

            TitleText.Text = title;

            // Pied de fenêtre (touche B + bouton) et rappel : seulement si on peut annuler.
            FooterPanel.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            NoteText.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;
            BtnCancel.Visibility = allowCancel ? Visibility.Visible : Visibility.Collapsed;

            // Si on connaît la taille totale, on passe la barre en mode
            // "déterminée" (avec %) ; sinon elle reste "en mouvement".
            if (_totalSize > 0)
            {
                Progress.IsIndeterminate = false;
                Progress.Maximum = _totalSize;
                Progress.Minimum = 0;
            }

            Closing += OnWindowClosing;
        }

        // ------------------------------------------------------------------
        // Dossier de destination PRÉEXISTANT (choix « Écraser ») : annuler arrête la copie mais ne supprime RIEN
        // (le plugin ne vide jamais un dossier qui existait avant lui). On corrige donc les deux textes qui
        // annonçaient une suppression. Appelé sur le thread UI, avant l'affichage de la fenêtre.
        // ------------------------------------------------------------------
        public void KeepFilesOnCancel()
        {
            _cancelDetail = "La copie s'arrêtera. Rien ne sera supprimé : le dossier existait déjà.";
            NoteText.Text = "Annuler arrête la copie sans rien supprimer (le dossier existait déjà).";
        }

        // ------------------------------------------------------------------
        // Appelé par le plugin (via Dispatcher) pour changer le texte d'état.
        // ------------------------------------------------------------------
        public void SetStatus(string text)
        {
            FileText.Text = text ?? "";
        }

        // ------------------------------------------------------------------
        // Appelé par le plugin (via Dispatcher) pendant la copie.
        // bytesCopied = total CUMULÉ depuis le début.
        // ------------------------------------------------------------------
        public void UpdateProgress(string currentFile, long bytesCopied)
        {
            // Le rapport final n'a pas de nom de fichier : on garde le dernier affiché.
            if (!string.IsNullOrEmpty(currentFile))
                FileText.Text = currentFile;

            if (_totalSize > 0)
            {
                var shown = Math.Min(bytesCopied, _totalSize);
                int percent = (int)(shown * 100 / _totalSize);
                SizeText.Text = $"{FormatSize(shown)} / {FormatSize(_totalSize)}";
                PercentText.Text = $"{percent} %";
                Progress.Value = shown;
            }
            else
            {
                SizeText.Text = FormatSize(bytesCopied);
            }
        }

        // ------------------------------------------------------------------
        // Installeur EXTERNE (Inno Setup…) : il ne dit pas où il en est. Le plugin mesure donc
        // ce qui a été écrit dans le dossier de destination ("writtenBytes").
        //   expectedTotal > 0 : taille finale ESTIMÉE (mémorisée d'une installation précédente)
        //                       → barre à pourcentage, affichée avec « ≈ » (c'est une estimation) ;
        //                       plafonnée à 99 % tant que l'installeur n'a pas fini.
        //   expectedTotal = 0 : taille inconnue → on affiche seulement ce qui est écrit, la barre
        //                       reste « en mouvement ».
        // Appelé via le Dispatcher (thread UI), comme les autres méthodes de cette fenêtre.
        // ------------------------------------------------------------------
        public void SetInstallProgress(long writtenBytes, long expectedTotal, string elapsedText)
        {
            string elapsed = string.IsNullOrEmpty(elapsedText) ? "" : "  —  " + elapsedText;

            // Rien de NOUVEAU mesuré dans la destination : on n'écrit surtout pas « 0 o écrits », ce
            // serait faux et trompeur. Cas typiques : le début d'une installation, ou un installeur
            // qui REMPLACE des fichiers de même taille (la taille du dossier ne change pas alors que
            // des fichiers sont bien réécrits). On dit simplement que ça travaille.
            if (writtenBytes <= 0)
            {
                SizeText.Text = "Installation en cours…" + elapsed;
                return;
            }

            if (expectedTotal > 0)
            {
                int percent = (int)Math.Min(99, writtenBytes * 100 / expectedTotal);
                Progress.IsIndeterminate = false;
                Progress.Minimum = 0;
                Progress.Maximum = 100;
                Progress.Value = percent;
                PercentText.Text = $"≈ {percent} %";
                SizeText.Text = $"{FormatSize(writtenBytes)} écrits sur environ {FormatSize(expectedTotal)}{elapsed}";
            }
            else
            {
                SizeText.Text = $"{FormatSize(writtenBytes)} écrits{elapsed}";
            }
        }

        // ------------------------------------------------------------------
        // Suite d'étapes (DLC et patchs) : barre à pourcentage sur le NOMBRE d'étapes terminées.
        // La ligne de détail peut rester pilotée par SetInstallProgress (Go écrits, temps écoulé).
        // ------------------------------------------------------------------
        public void SetSteps(int done, int total, string text)
        {
            if (total > 0)
            {
                int shown = Math.Max(0, Math.Min(done, total));
                Progress.IsIndeterminate = false;
                Progress.Minimum = 0;
                Progress.Maximum = total;
                Progress.Value = shown;
                PercentText.Text = $"{shown * 100 / total} %";
            }
            // text = null : on ne touche pas à la ligne de détail (ex. « 12,3 Go écrits — Temps écoulé… »).
            if (text != null) SizeText.Text = text;
        }

        // ------------------------------------------------------------------
        // Le plugin appelle AllowClose() juste avant Close() : sans ça, la
        // fermeture (Alt+F4) est traitée comme une demande d'annulation,
        // pas comme une fermeture.
        // ------------------------------------------------------------------
        public void AllowClose()
        {
            _allowClose = true;
        }

        private void OnWindowClosing(object sender, CancelEventArgs e)
        {
            if (_allowClose) return;

            // Fermeture demandée par l'utilisateur : on ne ferme pas, on propose d'annuler.
            // BeginInvoke : la question s'affiche APRÈS la fin de cet événement de fermeture
            // (on évite d'ouvrir une fenêtre au beau milieu d'une fermeture).
            e.Cancel = true;
            if (_allowCancel) Dispatcher.BeginInvoke(new Action(AskCancel));
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------

        // Bouton A / Entrée : volontairement sans effet (voir l'en-tête du fichier).
        protected override void OnConfirm()
        {
        }

        // Bouton B / Échap : proposer d'annuler la copie.
        protected override void OnCancelPressed()
        {
            AskCancel();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            AskCancel();
        }

        private void AskCancel()
        {
            if (_cancelRequested || !_allowCancel || _asking) return;

            bool yes;
            _asking = true;
            try
            {
                yes = ConsoleConfirmDialog.Ask(
                    this,
                    "Annuler l'installation ?",
                    _cancelDetail,
                    "Oui, annuler",
                    "Non, continuer");
            }
            finally
            {
                _asking = false;
            }

            if (!yes) return;

            _cancelRequested = true;
            BtnCancel.IsEnabled = false;
            TitleText.Text = "Annulation en cours…";
            CancelRequested?.Invoke(this, EventArgs.Empty);
        }

        // ------------------------------------------------------------------
        // Formatage humain d'une taille en octets → Go / Mo / Ko.
        // Méthode static : elle n'a pas besoin de l'état de la fenêtre.
        // ------------------------------------------------------------------
        private static string FormatSize(long bytes)
        {
            if (bytes >= 1_000_000_000L)
                return $"{bytes / 1_000_000_000.0:F1} Go";
            if (bytes >= 1_000_000L)
                return $"{bytes / 1_000_000.0:F0} Mo";
            if (bytes >= 1_000L)
                return $"{bytes / 1_000.0:F0} Ko";
            return $"{bytes} o";
        }
    }
}
