using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveStore
{
    // ======================================================================
    // PadHint — une astuce de touche (« A  Valider ») du pied de fenêtre.
    //
    // Pad   : "A", "B", "X", "Y" (pastilles de couleur), "D" (croix haut/bas), "H" (croix gauche/droite).
    // Label : le texte à côté. Modifiable en cours de route (ex. « Valider » devient « Cocher »
    //         selon l'élément qui a le focus).
    //
    // Ce sont de simples propriétés (pas de binding) : la fenêtre les règle dans le XAML
    // ou dans son code, et la pastille se redessine tout de suite.
    // ======================================================================
    public partial class PadHint : UserControl
    {
        private string _pad = "A";

        public PadHint()
        {
            InitializeComponent();
            ApplyPad();
        }

        /// <summary>Touche représentée : A, B, X, Y, D (haut/bas) ou H (gauche/droite).</summary>
        public string Pad
        {
            get { return _pad; }
            set { _pad = value ?? "A"; ApplyPad(); }
        }

        /// <summary>Texte affiché à droite de la pastille.</summary>
        public string Label
        {
            get { return LabelText.Text; }
            set { LabelText.Text = value ?? ""; }
        }

        private static Brush Rgb(byte r, byte g, byte b)
        {
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }

        private void ApplyPad()
        {
            var dark = Rgb(0x11, 0x11, 0x1b);
            var light = Rgb(0xcd, 0xd6, 0xf4);

            // Rond de 28 px pour les boutons de face ; plaque arrondie de 40 px pour la croix.
            Badge.Width = 28;
            Badge.CornerRadius = new CornerRadius(14);
            PadText.Foreground = dark;
            PadText.FontSize = 14;
            PadText.Text = _pad;

            switch (_pad)
            {
                case "A": Badge.Background = Rgb(0xa6, 0xe3, 0xa1); break; // vert
                case "B": Badge.Background = Rgb(0xf3, 0x8b, 0xa8); break; // rouge
                case "X": Badge.Background = Rgb(0x89, 0xb4, 0xfa); break; // bleu
                case "Y": Badge.Background = Rgb(0xf9, 0xe2, 0xaf); break; // jaune
                case "D":
                case "H":
                    Badge.Width = 40;
                    Badge.CornerRadius = new CornerRadius(8);
                    Badge.Background = Rgb(0x58, 0x5b, 0x70);
                    PadText.Foreground = light;
                    PadText.Text = _pad == "D" ? "▲▼" : "◄►";
                    PadText.FontSize = 12;
                    break;
                default:
                    Badge.Background = Rgb(0x58, 0x5b, 0x70);
                    PadText.Foreground = light;
                    break;
            }
        }
    }
}
