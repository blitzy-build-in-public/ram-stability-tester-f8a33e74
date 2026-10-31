using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace RamQuick.App.Services;

/// <summary>
/// The production <see cref="IUserPrompt"/>: asks each question in a small modal WPF dialog with
/// Yes and No buttons, and shows each informational message in a modal WPF
/// <see cref="MessageBox"/>, with the caller's title and text verbatim.
/// </summary>
/// <remarks>
/// <para>
/// A question is not a Yes/No message box because a Windows Yes/No message box has no cancel
/// action: it ignores Esc and disables the title-bar Close, so it cannot end as No the way the
/// <see cref="IUserPrompt.Ask"/> contract describes. The question dialog answers No to Esc, the
/// title-bar Close and Alt+F4. Its fonts and colours are the system's, applied as resource
/// references, so high-contrast themes apply. An informational message keeps the message box,
/// whose single OK button already closes on Esc.
/// </para>
/// <para>
/// Once the application's main window is shown and loaded, it owns the dialog, which is then
/// modal to that window and stays above it: the elevation offer at launch and on Start. Before
/// then the dialog has no owner. That covers the messages <c>App.OnStartup</c> shows before the
/// window exists: the unsupported platform, a second instance, and a damaged installation.
/// </para>
/// <para>
/// Both members block until the user dismisses the dialog, and both can be called from any
/// thread. On the UI thread, or when no WPF <see cref="Application"/> exists, the dialog is shown
/// directly. From any other thread it is shown synchronously on the application's dispatcher,
/// so the caller still receives a modal answer. Without an <see cref="Application"/>, a question
/// asked from a thread that is not single-threaded apartment (STA) is shown on a short-lived STA
/// thread that the call waits for, because a WPF window can be created only on an STA thread.
/// </para>
/// <para>
/// The class holds no state, so one instance serves the whole application.
/// </para>
/// </remarks>
public sealed class MessageBoxUserPrompt : IUserPrompt
{
    // The question wraps at about a Windows message box's text width, so a long question reads as a
    // block rather than one wide line.
    private const double MessageMaxWidth = 400;

    // Button size and spacing close to a Windows message box's, in DIPs.
    private const double ButtonMinWidth = 88;
    private const double ButtonMinHeight = 26;
    private const double ButtonSpacing = 8;

    private static readonly Thickness _messagePadding = new(24, 20, 24, 20);
    private static readonly Thickness _footerPadding = new(12);
    private static readonly Thickness _buttonPadding = new(12, 2, 12, 2);

    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxUserPrompt"/> class.
    /// </summary>
    public MessageBoxUserPrompt()
    {
        // Stateless: every call resolves the dispatcher and the owner window afresh, because the
        // main window does not exist yet when the application host shows its startup messages.
    }

    /// <summary>
    /// Asks a modal Yes/No question, and blocks until the user answers or dismisses it.
    /// </summary>
    /// <remarks>
    /// No is the default button and has the initial keyboard focus, so an accidental Enter never
    /// accepts the offer, which for the elevation question would start a UAC restart. No is also
    /// the cancel button, so Esc answers No, and so do the title-bar Close and Alt+F4. Y and N are
    /// the buttons' access keys, and Tab and Shift+Tab move between the two buttons. Only Yes
    /// returns <see cref="PromptResult.Yes"/>; every other way the dialog ends returns
    /// <see cref="PromptResult.No"/>.
    /// </remarks>
    /// <param name="title">The dialog title, shown verbatim.</param>
    /// <param name="message">The question, shown verbatim.</param>
    /// <returns>
    /// <see cref="PromptResult.Yes"/> when the user presses Yes; otherwise
    /// <see cref="PromptResult.No"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public PromptResult Ask(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        return OnUiThread(
            () => OnStaThread(() => ShowQuestionDialog(CreateQuestionDialog(title, message, FindOwner()))));
    }

    /// <summary>
    /// Shows a modal informational message with an information icon and a single OK button, and
    /// blocks until the user dismisses it.
    /// </summary>
    /// <param name="title">The dialog title, shown verbatim.</param>
    /// <param name="message">The message, shown verbatim.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    public void Inform(string title, string message)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        _ = OnUiThread(
            () => Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK));
    }

    /// <summary>
    /// Builds, without showing it, the dialog <see cref="Ask"/> shows: the question in a
    /// window-coloured area above a control-coloured footer that holds the Yes and No buttons,
    /// right-aligned.
    /// </summary>
    /// <remarks>
    /// Must be called on an STA thread, which then owns the dialog. Yes sets
    /// <see cref="Window.DialogResult"/> to <see langword="true"/>, so the dialog must be shown
    /// with <see cref="Window.ShowDialog"/>, as <see cref="ShowQuestionDialog"/> does. No is the
    /// default and the cancel button and holds the dialog's logical focus, which becomes keyboard
    /// focus when the dialog is activated. The two buttons are its only tab stops.
    /// </remarks>
    /// <param name="title">The dialog title, shown verbatim.</param>
    /// <param name="message">The question, shown verbatim.</param>
    /// <param name="owner">
    /// A shown window of the calling thread that owns the dialog, which then opens centred on it
    /// and has no taskbar button; or <see langword="null"/> for a dialog of its own, centred on
    /// the screen with a taskbar button.
    /// </param>
    /// <returns>The dialog, ready to show.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    internal static Window CreateQuestionDialog(string title, string message, Window? owner)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(message);

        var messageText = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = MessageMaxWidth,
        };
        messageText.SetResourceReference(TextBlock.ForegroundProperty, SystemColors.WindowTextBrushKey);
        AutomationProperties.SetName(messageText, message);

        var messageArea = new Border { Padding = _messagePadding, Child = messageText };
        messageArea.SetResourceReference(Border.BackgroundProperty, SystemColors.WindowBrushKey);

        Style buttonStyle = CreateButtonStyle();
        Button yesButton = CreateButton(
            UiText.PromptYesLabel, UiText.PromptYesAutomationName, tabIndex: 0, buttonStyle);
        Button noButton = CreateButton(
            UiText.PromptNoLabel, UiText.PromptNoAutomationName, tabIndex: 1, buttonStyle);
        noButton.Margin = new Thickness(ButtonSpacing, 0, 0, 0);

        // Default, so Enter answers No until the user moves focus to Yes; cancel, so Esc answers No.
        // A cancel button without a command closes a modal dialog with DialogResult false itself.
        noButton.IsDefault = true;
        noButton.IsCancel = true;

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { yesButton, noButton },
        };
        var footer = new Border { Padding = _footerPadding, Child = buttons };
        footer.SetResourceReference(Border.BackgroundProperty, SystemColors.ControlBrushKey);
        DockPanel.SetDock(footer, Dock.Bottom);

        var dialog = new Window
        {
            Title = title,
            Content = new DockPanel { Children = { footer, messageArea } },
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
            ShowInTaskbar = owner is null,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,

            // The caption keeps its Close button, which answers No like Esc.
            WindowStyle = WindowStyle.SingleBorderWindow,
            UseLayoutRounding = true,
        };
        dialog.SetResourceReference(Control.FontFamilyProperty, SystemFonts.MessageFontFamilyKey);
        dialog.SetResourceReference(Control.FontSizeProperty, SystemFonts.MessageFontSizeKey);
        dialog.SetResourceReference(Control.FontStyleProperty, SystemFonts.MessageFontStyleKey);
        dialog.SetResourceReference(Control.FontWeightProperty, SystemFonts.MessageFontWeightKey);
        AutomationProperties.SetName(dialog, title);

        // A dialog to UI Automation, as a Windows message box is, so a screen reader reads the
        // question when it opens. WPF infers this only while ShowDialog shows a window, so the mark
        // makes it part of the dialog however it is shown.
        AutomationProperties.SetIsDialog(dialog, true);

        KeyboardNavigation.SetTabNavigation(dialog, KeyboardNavigationMode.Cycle);
        FocusManager.SetFocusedElement(dialog, noButton);

        yesButton.Click += (_, _) => dialog.DialogResult = true;
        return dialog;
    }

    /// <summary>
    /// Shows a dialog built by <see cref="CreateQuestionDialog"/> modally, and maps how it ended to
    /// the answer.
    /// </summary>
    /// <remarks>Must be called on the thread that built the dialog.</remarks>
    /// <param name="dialog">The question dialog, not yet shown.</param>
    /// <returns>
    /// <see cref="PromptResult.Yes"/> when Yes closed the dialog; otherwise
    /// <see cref="PromptResult.No"/>.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="dialog"/> is <see langword="null"/>.
    /// </exception>
    internal static PromptResult ShowQuestionDialog(Window dialog)
    {
        ArgumentNullException.ThrowIfNull(dialog);

        // Only Yes sets true. No, Esc and Enter on No set false; the title-bar Close and Alt+F4
        // set no result, which ShowDialog reports as false or null, never true.
        return dialog.ShowDialog() == true ? PromptResult.Yes : PromptResult.No;
    }

    // Runs on the UI thread, or on the only thread there is when no Application exists, because
    // the owner lookup reads Window properties that are bound to the dispatcher's thread.
    private static MessageBoxResult Show(
        string message,
        string title,
        MessageBoxButton buttons,
        MessageBoxImage icon,
        MessageBoxResult defaultResult)
    {
        Window? owner = FindOwner();
        return owner is null
            ? MessageBox.Show(message, title, buttons, icon, defaultResult)
            : MessageBox.Show(owner, message, title, buttons, icon, defaultResult);
    }

    // A window that is not yet visible and loaded has no usable handle to own a dialog, so the
    // dialog then stands alone rather than attaching to a window the user cannot see.
    private static Window? FindOwner()
    {
        Window? mainWindow = Application.Current?.MainWindow;
        return mainWindow is { IsVisible: true, IsLoaded: true } ? mainWindow : null;
    }

    // Invoke, never BeginInvoke: callers wait for the user's answer, so the dialog must have closed
    // before this returns.
    private static T OnUiThread<T>(Func<T> action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        return dispatcher is null || dispatcher.CheckAccess() ? action() : dispatcher.Invoke(action);
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a single-threaded apartment (STA) thread and returns its
    /// result: inline when the calling thread is STA, otherwise on a short-lived STA thread whose
    /// end the call waits for.
    /// </summary>
    /// <remarks>
    /// A WPF window can be created only on an STA thread. The UI thread always is one; without an
    /// Application, a caller on another thread gets the short-lived thread, so the answer is still
    /// modal. That thread's dispatcher is shut down before the thread ends, which releases the
    /// hidden window WPF creates for it. An exception from <paramref name="action"/> is rethrown
    /// on the calling thread with its original stack trace.
    /// </remarks>
    /// <typeparam name="T">The result type.</typeparam>
    /// <param name="action">The work, which may create and show WPF windows.</param>
    /// <returns>The result of <paramref name="action"/>.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="action"/> is <see langword="null"/>.
    /// </exception>
    internal static T OnStaThread<T>(Func<T> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            return action();
        }

        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.CurrentDispatcher.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
            Name = nameof(MessageBoxUserPrompt),
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
        return result;
    }

    private static Button CreateButton(string label, string automationName, int tabIndex, Style style)
    {
        var button = new DialogButton
        {
            // An AccessText, rather than a string the template converts, so the access key is the
            // label's own and does not depend on the button's template.
            Content = new AccessText { Text = label },
            MinWidth = ButtonMinWidth,
            MinHeight = ButtonMinHeight,
            Padding = _buttonPadding,
            TabIndex = tabIndex,
            Style = style,
        };
        AutomationProperties.SetName(button, automationName);
        return button;
    }

    // The focused button wears the highlight border Windows gives a dialog's default button, so
    // the dialog opens showing which button Enter presses, and the border follows Tab. WPF draws
    // its own default-button border only while focus is on something other than a button, and
    // hides the focus rectangle until the keyboard is used, so without this No would open with no
    // visible mark. The brush is a resource reference, so high-contrast themes apply.
    private static Style CreateButtonStyle()
    {
        var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
        focused.Setters.Add(new Setter(
            Control.BorderBrushProperty, new DynamicResourceExtension(SystemColors.HighlightBrushKey)));

        var style = new Style(typeof(Button));
        style.Triggers.Add(focused);
        return style;
    }

    // A button that UI Automation sees as a Windows dialog's button does: a named button with no
    // children. A WPF button otherwise exposes the text its label draws, named with the access-key
    // underscore ("_Yes"), which a screen reader reads out while browsing the dialog, whether the
    // label is a string or an AccessText. Its name still comes from AutomationProperties.Name, and
    // its access key from the label.
    private sealed class DialogButton : Button
    {
        protected override AutomationPeer OnCreateAutomationPeer() =>
            new DialogButtonAutomationPeer(this);
    }

    private sealed class DialogButtonAutomationPeer : ButtonAutomationPeer
    {
        public DialogButtonAutomationPeer(Button owner)
            : base(owner)
        {
        }

        protected override List<AutomationPeer>? GetChildrenCore() => null;
    }
}
