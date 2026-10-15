using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ArchiveStore
{
    // ======================================================================
    // ConsoleRows — fabrique de « lignes » pour les fenêtres de réglages au style console.
    //
    // PÉDAGOGIE : les fenêtres de réglages (ConsoleSettingsWindow, ProfileEditorDialog) construisent leur
    // contenu EN CODE : une catégorie = une pile de lignes. Plutôt que de répéter vingt fois la même
    // construction (grille, couleurs, styles…), on la range ici, une seule fois.
    //
    // Les styles (RowButton, GroupStyle, NoteStyle…) viennent de ConsoleStyle.xaml. On les retrouve avec
    // FindResource sur la fenêtre « hôte » (celle qui a fusionné ConsoleStyle.xaml dans ses ressources).
    // ======================================================================
    internal static class ConsoleRows
    {
        // Va chercher un style (ou une brosse) dans les ressources de la fenêtre hôte.
        private static Style StyleOf(FrameworkElement host, string key)
        {
            return host.FindResource(key) as Style;
        }

        private static Brush BrushOf(FrameworkElement host, string key)
        {
            return host.FindResource(key) as Brush;
        }

        /// <summary>Titre de groupe (« SOURCES », « NOTIFICATIONS »…).</summary>
        public static TextBlock Section(FrameworkElement host, string text)
        {
            var t = new TextBlock { Text = text, Style = StyleOf(host, "GroupStyle") };
            t.Margin = new Thickness(4, 22, 0, 8);
            return t;
        }

        /// <summary>Texte explicatif (petit, grisé, à la ligne).</summary>
        public static TextBlock Note(FrameworkElement host, string text)
        {
            var t = new TextBlock { Text = text, Style = StyleOf(host, "NoteStyle") };
            t.FontSize = 14;
            t.Margin = new Thickness(4, 2, 4, 12);
            return t;
        }

        /// <summary>Texte explicatif décalé à droite : placé juste sous une case à cocher.</summary>
        public static TextBlock CheckNote(FrameworkElement host, string text)
        {
            var t = new TextBlock { Text = text, Style = StyleOf(host, "NoteStyle") };
            t.FontSize = 13.5;
            t.Margin = new Thickness(52, 0, 8, 14);
            return t;
        }

        /// <summary>Mise en garde orange.</summary>
        public static TextBlock Warn(FrameworkElement host, string text)
        {
            var t = new TextBlock { Text = text, Style = StyleOf(host, "WarnOrangeStyle") };
            t.Margin = new Thickness(4, 2, 4, 12);
            return t;
        }

        /// <summary>
        /// Une ligne cliquable : le libellé à gauche, la valeur actuelle (facultative) à droite, en bleu.
        /// onClick est appelé au clic de la souris ET au bouton A de la manette (la fenêtre « clique » la ligne).
        /// </summary>
        public static Button Row(FrameworkElement host, string label, string value, Action onClick)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new TextBlock
            {
                Text = label ?? "",
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            grid.Children.Add(left);

            if (!string.IsNullOrEmpty(value))
            {
                var right = new TextBlock
                {
                    Text = value,
                    Foreground = BrushOf(host, "AccentBrush"),
                    MaxWidth = 440,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(20, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = value
                };
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);
            }

            var b = new Button { Content = grid, Style = StyleOf(host, "RowButton") };
            if (onClick != null) b.Click += (s, e) => onClick();
            return b;
        }

        /// <summary>
        /// Une case à cocher. « changed » reçoit la nouvelle valeur. On écoute Checked/Unchecked (et non Click) :
        /// ainsi le changement est aussi signalé quand la FENÊTRE coche la case pour la manette (IsChecked = ...).
        /// </summary>
        public static CheckBox Check(FrameworkElement host, string text, bool value, Action<bool> changed)
        {
            var cb = new CheckBox
            {
                Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                IsChecked = value // AVANT d'attacher les gestionnaires : sinon ils se déclencheraient pour rien
            };
            if (changed != null)
            {
                cb.Checked += (s, e) => changed(true);
                cb.Unchecked += (s, e) => changed(false);
            }
            return cb;
        }
    }
}
