using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace ArchiveStore
{
    // ======================================================================
    // Code-behind du sélecteur d'exécutable (ÉTAPE D + réassociation).
    //
    // RÔLE : lister tous les .exe "de jeu" du dossier installé, triés par
    // taille décroissante, et laisser l'utilisateur (ou la manette) choisir
    // l'exécutable principal. La taille est une HEURISTIQUE : le vrai jeu
    // est presque toujours le plus gros .exe (vs lanceurs/outils légers).
    //
    // Le résultat est exposé via SelectedExePath, lu par le plugin après
    // ShowDialog() == true.
    //
    // MANETTE / CLAVIER (via ConsoleWindow) : haut/bas = changer de ligne, A = sélectionner, B = annuler.
    // ======================================================================
    public partial class ExeSelectorDialog : ConsoleWindow
    {
        /// <summary>Chemin complet de l'exe choisi (lu par le plugin).</summary>
        public string SelectedExePath { get; private set; }

        // preselectPath (optionnel) : exe à présélectionner (ex. l'exe actuellement
        // mémorisé lors d'une réassociation). Sans lui, on présélectionne le plus gros.
        public ExeSelectorDialog(string installDir, List<string> excludedPatterns, string preselectPath = null)
        {
            InitializeComponent();
            Header.Game = GameNameFromFolder(installDir);
            LoadExes(installDir, excludedPatterns, preselectPath);
        }

        // Nom du jeu pour l'en-tête : le dernier dossier du chemin d'installation, nettoyé.
        private static string GameNameFromFolder(string installDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(installDir)) return "";
                string last = Path.GetFileName(installDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return ArchiveScanner.CleanGameName(last);
            }
            catch
            {
                return "";
            }
        }

        // ------------------------------------------------------------------
        // Charge la liste des exécutables et la trie par taille décroissante.
        // ------------------------------------------------------------------
        private void LoadExes(string installDir, List<string> excludedPatterns, string preselectPath)
        {
            // On délègue la recherche + le filtrage des utilitaires au scanner
            // (logique centralisée, réutilisable ailleurs).
            var exes = ArchiveScanner.FindGameExes(installDir, excludedPatterns);

            var items = exes
                .Select(f => new ExeItem
                {
                    Path = f,
                    Name = System.IO.Path.GetFileName(f),
                    SizeBytes = SafeSize(f)
                })
                .OrderByDescending(x => x.SizeBytes)   // gros .exe en haut = jeu probable
                .ToList();

            // Le plus gros porte l'étiquette « RECOMMANDÉ ».
            if (items.Count > 0) items[0].IsRecommended = true;

            ExeList.ItemsSource = items;

            // Pré-sélection : l'exe demandé s'il est dans la liste (exe mémorisé),
            // sinon le plus gros : dans 90 % des cas c'est le bon choix,
            // l'utilisateur n'a plus qu'à valider (Entrée / A).
            if (items.Count > 0)
            {
                var wanted = string.IsNullOrEmpty(preselectPath)
                    ? null
                    : items.FirstOrDefault(i =>
                        string.Equals(i.Path, preselectPath, StringComparison.OrdinalIgnoreCase));

                ExeList.SelectedItem = wanted ?? items[0];

                // Fait défiler la liste jusqu'à l'élément présélectionné s'il est loin.
                ExeList.ScrollIntoView(ExeList.SelectedItem);
            }
        }

        // Taille fichier tolérante aux erreurs (fichier verrouillé/inaccessible
        // ne doit pas faire planter l'affichage de la liste).
        private static long SafeSize(string path)
        {
            try { return new FileInfo(path).Length; }
            catch { return 0; }
        }

        // ------------------------------------------------------------------
        // Manette / clavier
        // ------------------------------------------------------------------
        protected override UIElement InitialFocusElement()
        {
            // Focus initial sur la liste → navigation immédiate (essentiel à la manette / Plein écran).
            return ExeList;
        }

        // Haut/bas : on déplace NOUS-MÊMES la sélection (même comportement au clavier, en Plein écran
        // et à la croix directionnelle du mode Bureau). Gauche/droite : sans effet ici.
        protected override bool OnNavigate(GamepadAction action)
        {
            if (action == GamepadAction.Up) MoveSelection(-1);
            else if (action == GamepadAction.Down) MoveSelection(+1);
            return true;
        }

        private void MoveSelection(int delta)
        {
            int count = ExeList.Items.Count;
            if (count == 0) return;

            int index = ExeList.SelectedIndex < 0
                ? (delta > 0 ? 0 : count - 1)
                : Math.Max(0, Math.Min(count - 1, ExeList.SelectedIndex + delta));

            ExeList.SelectedIndex = index;
            ExeList.ScrollIntoView(ExeList.SelectedItem);
            ExeList.Focus();
        }

        // ------------------------------------------------------------------
        // Confirmation (plusieurs déclencheurs : bouton, double-clic, Entrée, bouton A).
        // CloseDialog() ignore tout appel après le premier : pas de double fermeture.
        // ------------------------------------------------------------------
        private void Confirm()
        {
            var item = ExeList.SelectedItem as ExeItem;
            if (item == null) return; // rien de sélectionné : l'utilisateur doit choisir

            SelectedExePath = item.Path;
            CloseDialog(true);
        }

        protected override void OnConfirm()
        {
            Confirm();
        }

        // Double-clic sur une ligne = confirmer directement.
        private void ExeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            Confirm();
        }

        private void BtnConfirm_Click(object sender, RoutedEventArgs e)
        {
            Confirm();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog(false);
        }
    }

    // ======================================================================
    // ExeItem — objet d'affichage d'une ligne de la ListBox.
    //
    // PÉDAGOGIE — DataTemplate :
    // Le XAML (ListBox.ItemTemplate) affiche {Binding Name}, {Binding SizeDisplay},
    // {Binding Path}. Ce sont les PROPRIÉTÉS publiques de cette classe que le
    // binding lit. "SizeDisplay" n'est pas une donnée brute mais une propriété
    // calculée qui formate la taille en Go/Mo/Ko pour l'humain.
    // ======================================================================
    public class ExeItem
    {
        /// <summary>Chemin complet de l'exécutable.</summary>
        public string Path { get; set; }

        /// <summary>Nom du fichier (ex: "game.exe").</summary>
        public string Name { get; set; }

        /// <summary>Taille brute en octets (sert au tri).</summary>
        public long SizeBytes { get; set; }

        /// <summary>Vrai pour le plus gros exécutable : la ligne porte l'étiquette « RECOMMANDÉ ».</summary>
        public bool IsRecommended { get; set; }

        /// <summary>
        /// Taille formatée pour l'affichage (Go / Mo / Ko).
        /// Propriété calculée : pas de champ, juste un get.
        /// </summary>
        public string SizeDisplay
        {
            get
            {
                if (SizeBytes >= 1_000_000_000L)
                    return $"{SizeBytes / 1_000_000_000.0:F1} Go";
                if (SizeBytes >= 1_000_000L)
                    return $"{SizeBytes / 1_000_000.0:F0} Mo";
                if (SizeBytes >= 1_000L)
                    return $"{SizeBytes / 1_000.0:F0} Ko";
                return "0 Ko";
            }
        }
    }
}
