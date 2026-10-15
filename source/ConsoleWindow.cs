using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Playnite.SDK;
using Playnite.SDK.Events;

namespace ArchiveStore
{
    // ======================================================================
    // Socle commun des fenêtres « console » d'ArchiveStore.
    //
    // CE FICHIER CONTIENT TROIS CHOSES :
    //   1. GamepadAction  : les « ordres » que la manette peut donner à une fenêtre ;
    //   2. GamepadHelper  : reçoit les boutons de la manette (depuis le plugin) et les
    //                       transmet à la fenêtre ACTIVE ;
    //   3. ConsoleWindow  : la classe de base de nos fenêtres (cadre arrondi sans barre
    //                       Windows, navigation au clavier ET à la manette).
    //
    // POURQUOI TOUT ÇA ? Playnite, en mode Plein écran, transforme la croix directionnelle
    // et le joystick en VRAIES touches du clavier (flèches), mais PAS les boutons A et B :
    // c'est pour cela que A/B ne faisaient rien dans nos fenêtres. Le plugin doit donc écouter
    // lui-même la manette (OnControllerButtonStateChanged) et piloter ses propres fenêtres.
    // ======================================================================

    /// <summary>Les ordres que la manette (ou le clavier) peut donner à une fenêtre.</summary>
    public enum GamepadAction
    {
        Up, Down, Left, Right,
        Confirm,   // bouton A (ou Entrée)
        Cancel,    // bouton B (ou Échap)
        X,         // bouton X
        Y          // bouton Y
    }

    // ----------------------------------------------------------------------
    // GamepadHelper — aiguilleur des boutons de manette.
    //
    // Le plugin (ArchiveStorePlugin) appelle OnButton() à chaque bouton de la manette.
    // On ne livre l'ordre qu'à la fenêtre qui est ACTIVE : si l'utilisateur est dans Playnite
    // (une copie tourne en arrière-plan), un appui sur B ne doit surtout pas annuler la copie.
    // ----------------------------------------------------------------------
    internal static class GamepadHelper
    {
        private static readonly ILogger logger = LogManager.GetLogger();

        private static IPlayniteAPI _api;

        // Fenêtres console actuellement ouvertes (modifié uniquement sur le thread UI).
        private static readonly List<ConsoleWindow> Open = new List<ConsoleWindow>();
        private static volatile int _openCount;

        public static void Initialize(IPlayniteAPI api)
        {
            _api = api;
        }

        public static void Register(ConsoleWindow window)
        {
            if (!Open.Contains(window)) Open.Add(window);
            _openCount = Open.Count;
        }

        public static void Unregister(ConsoleWindow window)
        {
            Open.Remove(window);
            _openCount = Open.Count;
        }

        /// <summary>
        /// Appelé par le plugin pour chaque changement d'état d'un bouton.
        /// desktopMode = vrai si l'événement vient du mode Bureau.
        /// </summary>
        public static void OnButton(ControllerInput button, ControllerInputState state, bool desktopMode)
        {
            // On n'agit qu'à l'APPUI (pas au relâchement), et seulement si une de nos fenêtres est ouverte.
            if (state != ControllerInputState.Pressed) return;
            if (_openCount == 0) return;

            GamepadAction? action = Translate(button, desktopMode);
            if (!action.HasValue) return;

            var app = Application.Current;
            if (app == null) return;

            GamepadAction captured = action.Value;
            // BeginInvoke : on ne bloque pas le thread de la manette, et on se retrouve sur le thread UI.
            app.Dispatcher.BeginInvoke(new Action(() => Deliver(captured)));
        }

        private static GamepadAction? Translate(ControllerInput button, bool desktopMode)
        {
            switch (button)
            {
                case ControllerInput.A:
                    return SwapConfirmCancel(desktopMode) ? GamepadAction.Cancel : GamepadAction.Confirm;
                case ControllerInput.B:
                    return SwapConfirmCancel(desktopMode) ? GamepadAction.Confirm : GamepadAction.Cancel;
                case ControllerInput.X:
                    return GamepadAction.X;
                case ControllerInput.Y:
                    return GamepadAction.Y;
            }

            // En mode Plein écran, la croix directionnelle et le joystick arrivent déjà comme de
            // vraies flèches du clavier (Playnite les simule) : on les traite dans PreviewKeyDown.
            // Les traiter AUSSI ici ferait avancer le curseur de DEUX crans à chaque appui.
            // En mode Bureau, Playnite ne simule rien : c'est à nous de gérer la croix.
            if (!desktopMode) return null;

            switch (button)
            {
                case ControllerInput.DPadUp:
                case ControllerInput.LeftStickUp:
                    return GamepadAction.Up;
                case ControllerInput.DPadDown:
                case ControllerInput.LeftStickDown:
                    return GamepadAction.Down;
                case ControllerInput.DPadLeft:
                case ControllerInput.LeftStickLeft:
                    return GamepadAction.Left;
                case ControllerInput.DPadRight:
                case ControllerInput.LeftStickRight:
                    return GamepadAction.Right;
            }

            return null;
        }

        // Réglage Playnite « inverser les boutons de confirmation et d'annulation » (manettes de type
        // Nintendo). Lu par RÉFLEXION : si ce réglage n'existe pas dans la version de Playnite, on
        // ignore simplement (A = valider, B = annuler). Seulement en Plein écran.
        private static bool SwapConfirmCancel(bool desktopMode)
        {
            if (desktopMode || _api == null) return false;
            try
            {
                var settingsProp = _api.GetType().GetProperty("ApplicationSettings");
                if (settingsProp == null) return false;
                var settings = settingsProp.GetValue(_api, null);
                if (settings == null) return false;

                var swapProp = settings.GetType().GetProperty("SwapConfirmCancelButtons");
                if (swapProp == null || swapProp.PropertyType != typeof(bool)) return false;
                return (bool)swapProp.GetValue(settings, null);
            }
            catch
            {
                return false;
            }
        }

        // Livre l'ordre à la fenêtre ACTIVE (thread UI).
        private static void Deliver(GamepadAction action)
        {
            ConsoleWindow target = null;
            for (int i = Open.Count - 1; i >= 0; i--)
            {
                if (Open[i].IsActive) { target = Open[i]; break; }
            }
            if (target == null) return;

            try { target.HandleGamepad(action); }
            catch (Exception ex) { logger.Warn(ex, "ArchiveStore : ordre de manette non traité."); }
        }
    }

    // ----------------------------------------------------------------------
    // ConsoleWindow — classe de base des fenêtres d'ArchiveStore.
    //
    // PÉDAGOGIE : toutes nos fenêtres « héritent » de cette classe (public partial class X : ConsoleWindow).
    // Elles reçoivent ainsi gratuitement le cadre, le déplacement à la souris et la navigation.
    // Une fenêtre fille personnalise le comportement en SURCHARGEANT (override) les méthodes
    // « protected virtual » ci-dessous.
    // ----------------------------------------------------------------------
    public class ConsoleWindow : Window
    {
        // Délai (en ms) pendant lequel une fenêtre qui vient de s'ouvrir ignore la manette :
        // l'appui sur A qui a OUVERT la fenêtre ne doit pas la valider tout de suite.
        private const int IgnorePadAfterOpenMs = 350;

        private DateTime _readyAtUtc = DateTime.MaxValue;
        private bool _closing;

        public ConsoleWindow()
        {
            // Fenêtre sans barre Windows : transparente, le cadre est dessiné par le modèle (ConsoleStyle.xaml).
            // ⚠️ AllowsTransparency doit être réglé AVANT l'affichage : le constructeur est le bon endroit.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            UseLayoutRounding = true;

            FontFamily = new FontFamily("Segoe UI");
            FontSize = 16;
            Foreground = new SolidColorBrush(Color.FromRgb(0xcd, 0xd6, 0xf4));

            // Une fenêtre ne doit jamais dépasser l'écran (la liste défile à l'intérieur si besoin).
            MaxHeight = SystemParameters.WorkArea.Height;

            Loaded += OnConsoleLoaded;
            ContentRendered += (s, e) => _readyAtUtc = DateTime.UtcNow;
            Closed += (s, e) => GamepadHelper.Unregister(this);
            PreviewKeyDown += OnConsolePreviewKeyDown;
            MouseLeftButtonDown += OnConsoleMouseDown;
        }

        // Appelé quand le XAML de la fenêtre fille est entièrement lu : les ressources (dont le style
        // du cadre) sont alors disponibles.
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            var style = TryFindResource("ConsoleWindowStyle") as Style;
            if (style != null) Style = style;
        }

        // ------------------------------------------------------------------
        // À SURCHARGER par les fenêtres filles
        // ------------------------------------------------------------------

        /// <summary>Élément qui reçoit le focus à l'ouverture (null = aucun).</summary>
        protected virtual UIElement InitialFocusElement() { return null; }

        /// <summary>Lignes parcourues avec haut/bas (cases, options…). null = aucune.</summary>
        protected virtual IList<UIElement> NavigationRows { get { return null; } }

        /// <summary>Boutons du pied de fenêtre, de gauche à droite (le dernier est le bouton principal). null = aucun.</summary>
        protected virtual IList<UIElement> FooterButtons { get { return null; } }

        /// <summary>Bouton A (ou Entrée). Par défaut : clique le bouton qui a le focus.</summary>
        protected virtual void OnConfirm() { ClickFocusedButton(); }

        /// <summary>Bouton B (ou Échap). Par défaut : ferme la fenêtre en « annulé ».</summary>
        protected virtual void OnCancelPressed() { CloseDialog(false); }

        /// <summary>Bouton X.</summary>
        protected virtual void OnActionX() { }

        /// <summary>Bouton Y.</summary>
        protected virtual void OnActionY() { }

        /// <summary>
        /// Flèches / croix directionnelle. Renvoie vrai si l'ordre a été traité.
        /// Par défaut : haut/bas parcourent NavigationRows puis les boutons du pied ; gauche/droite
        /// parcourent les boutons du pied.
        /// </summary>
        protected virtual bool OnNavigate(GamepadAction action)
        {
            var rows = Usable(NavigationRows);
            var footer = Usable(FooterButtons);
            var current = CurrentFocus as UIElement;
            bool inFooter = current != null && footer.Contains(current);

            switch (action)
            {
                case GamepadAction.Up:
                    if (inFooter)
                    {
                        // Du pied de fenêtre, on remonte à la dernière ligne.
                        if (rows.Count > 0) FocusRow(rows[rows.Count - 1]);
                        return true;
                    }
                    return StepRow(rows, current == null ? -1 : rows.IndexOf(current), -1);

                case GamepadAction.Down:
                    if (inFooter) return true;
                    int idx = current == null ? -1 : rows.IndexOf(current);
                    if (idx >= 0 && idx == rows.Count - 1)
                    {
                        // Après la dernière ligne : on passe au bouton principal du pied de fenêtre.
                        if (footer.Count > 0) footer[footer.Count - 1].Focus();
                        return true;
                    }
                    return StepRow(rows, idx, +1);

                case GamepadAction.Left:
                case GamepadAction.Right:
                    if (inFooter)
                    {
                        int k = footer.IndexOf(current) + (action == GamepadAction.Left ? -1 : 1);
                        k = Math.Max(0, Math.Min(footer.Count - 1, k));
                        footer[k].Focus();
                        return true;
                    }
                    return false;
            }
            return false;
        }

        /// <summary>Donne le focus à une ligne (surchargé si la ligne doit faire autre chose).</summary>
        protected virtual void FocusRow(UIElement row)
        {
            row.Focus();
            var fe = row as FrameworkElement;
            if (fe != null) fe.BringIntoView(); // fait défiler une liste si la ligne est hors de vue
        }

        // ------------------------------------------------------------------
        // Outils pour les fenêtres filles
        // ------------------------------------------------------------------

        /// <summary>Élément qui a le focus DANS cette fenêtre.</summary>
        protected IInputElement CurrentFocus
        {
            get { return FocusManager.GetFocusedElement(this); }
        }

        /// <summary>
        /// Clique le bouton qui a le focus (s'il est utilisable). Renvoie vrai si un clic a eu lieu.
        /// Seuls les vrais boutons (Button) sont concernés : les cases et options sont gérées par chaque fenêtre.
        /// </summary>
        protected bool ClickFocusedButton()
        {
            return ClickButton(CurrentFocus as Button);
        }

        /// <summary>Clique un bouton précis (s'il est utilisable). Renvoie vrai si un clic a eu lieu.</summary>
        protected static bool ClickButton(Button b)
        {
            if (b == null || !b.IsEnabled || !b.IsVisible) return false;
            b.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, b));
            return true;
        }

        /// <summary>
        /// Ferme la fenêtre en fixant son résultat, UNE seule fois (Entrée et un bouton peuvent
        /// arriver ensemble : le second appel est ignoré, sinon exception sur une fenêtre déjà fermée).
        /// Pour une fenêtre non modale (Show), DialogResult est interdit : on se contente de fermer.
        /// </summary>
        protected void CloseDialog(bool result)
        {
            if (_closing) return;
            _closing = true;
            try
            {
                DialogResult = result;
            }
            catch (InvalidOperationException)
            {
                try { Close(); }
                catch (InvalidOperationException) { /* déjà en cours de fermeture */ }
            }
        }

        // ------------------------------------------------------------------
        // Mécanique interne
        // ------------------------------------------------------------------

        private bool AcceptsGamepad
        {
            get { return (DateTime.UtcNow - _readyAtUtc).TotalMilliseconds >= IgnorePadAfterOpenMs; }
        }

        // Appelé par GamepadHelper (thread UI) pour la fenêtre active.
        internal void HandleGamepad(GamepadAction action)
        {
            if (_closing || !AcceptsGamepad) return;

            switch (action)
            {
                case GamepadAction.Up:
                case GamepadAction.Down:
                case GamepadAction.Left:
                case GamepadAction.Right:
                    OnNavigate(action);
                    break;
                case GamepadAction.Confirm:
                    OnConfirm();
                    break;
                case GamepadAction.Cancel:
                    OnCancelPressed();
                    break;
                case GamepadAction.X:
                    OnActionX();
                    break;
                case GamepadAction.Y:
                    OnActionY();
                    break;
            }
        }

        private void OnConsoleLoaded(object sender, RoutedEventArgs e)
        {
            GamepadHelper.Register(this);

            // Sans propriétaire (cas rare), WPF ne centre pas : on centre nous-mêmes sur l'écran.
            if (Owner == null)
            {
                var area = SystemParameters.WorkArea;
                Left = area.Left + Math.Max(0, (area.Width - ActualWidth) / 2);
                Top = area.Top + Math.Max(0, (area.Height - ActualHeight) / 2);
            }

            var first = InitialFocusElement();
            if (first != null) first.Focus();
        }

        // Clavier — et, en Plein écran, croix directionnelle (Playnite la transforme en flèches).
        private void OnConsolePreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Up: e.Handled = true; OnNavigate(GamepadAction.Up); return;
                case Key.Down: e.Handled = true; OnNavigate(GamepadAction.Down); return;
                case Key.Left: e.Handled = true; OnNavigate(GamepadAction.Left); return;
                case Key.Right: e.Handled = true; OnNavigate(GamepadAction.Right); return;

                case Key.Escape:
                    e.Handled = true;
                    if (!_closing) OnCancelPressed();
                    return;

                case Key.Enter:
                    // Sur un bouton, Entrée le clique déjà toute seule : on ne double pas.
                    if (Keyboard.FocusedElement is Button) return;
                    e.Handled = true;
                    if (!_closing) OnConfirm();
                    return;
            }
        }

        // Déplacer la fenêtre en la faisant glisser avec la souris (il n'y a plus de barre de titre).
        private void OnConsoleMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { /* bouton déjà relâché */ }
        }

        private static List<UIElement> Usable(IList<UIElement> source)
        {
            var list = new List<UIElement>();
            if (source == null) return list;
            foreach (var el in source)
            {
                if (el != null && el.IsEnabled && el.IsVisible && el.Focusable) list.Add(el);
            }
            return list;
        }

        private bool StepRow(List<UIElement> rows, int index, int delta)
        {
            if (rows.Count == 0) return true;
            int next = index < 0
                ? (delta > 0 ? 0 : rows.Count - 1)
                : Math.Max(0, Math.Min(rows.Count - 1, index + delta));
            FocusRow(rows[next]);
            return true;
        }
    }
}
