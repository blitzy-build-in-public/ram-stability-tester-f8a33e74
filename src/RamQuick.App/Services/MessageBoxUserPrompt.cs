using System.Windows;
using System.Windows.Threading;

namespace RamQuick.App.Services;

/// <summary>
/// The production <see cref="IUserPrompt"/>: shows each question or message in a modal WPF
/// <see cref="MessageBox"/>, with the caller's title and text verbatim.
/// </summary>
/// <remarks>
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
/// so the caller still receives a modal answer.
/// </para>
/// <para>
/// The class holds no state, so one instance serves the whole application.
/// </para>
/// </remarks>
public sealed class MessageBoxUserPrompt : IUserPrompt
{
    /// <summary>
    /// Initializes a new instance of the <see cref="MessageBoxUserPrompt"/> class.
    /// </summary>
    public MessageBoxUserPrompt()
    {
        // Stateless: every call resolves the dispatcher and the owner window afresh, because the
        // main window does not exist yet when the application host shows its startup messages.
    }

    /// <summary>
    /// Asks a modal Yes/No question with a question icon, and blocks until the user answers it.
    /// </summary>
    /// <remarks>
    /// No is the default button, so an accidental Enter never accepts the offer, which for the
    /// elevation question would start a UAC restart. Only Yes returns
    /// <see cref="PromptResult.Yes"/>; every other way the dialog ends returns
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

        MessageBoxResult answer = OnUiThread(
            () => Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No));
        return answer == MessageBoxResult.Yes ? PromptResult.Yes : PromptResult.No;
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
}
