using System.Windows;
using System.Windows.Controls;

namespace ArchiveStore
{
    // ======================================================================
    // ConsoleHeader — l'en-tête commun des fenêtres : logo + « ARCHIVESTORE · SECTION » + nom du jeu.
    // ======================================================================
    public partial class ConsoleHeader : UserControl
    {
        public ConsoleHeader()
        {
            InitializeComponent();
            Section = "";
        }

        /// <summary>Nom de la section (ex. « Installation ») : affiché en capitales après « ARCHIVESTORE ».</summary>
        public string Section
        {
            get { return SectionText.Text; }
            set
            {
                string s = (value ?? "").Trim();
                SectionText.Text = s.Length == 0
                    ? "ARCHIVESTORE"
                    : "ARCHIVESTORE  ·  " + s.ToUpperInvariant();
            }
        }

        /// <summary>Nom du jeu concerné (vide = ligne masquée).</summary>
        public string Game
        {
            get { return GameText.Text; }
            set
            {
                string g = (value ?? "").Trim();
                GameText.Text = g;
                GameText.Visibility = g.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
        }
    }
}
