namespace RamQuick.App.Services;

/// <summary>
/// The user's answer to a question asked through <see cref="IUserPrompt.Ask"/>.
/// </summary>
/// <remarks>
/// Only an explicit acceptance is <see cref="Yes"/>. Every other way the dialog can end is
/// <see cref="No"/>, so a dismissed question never triggers the action it offered, such as a
/// restart through UAC.
/// </remarks>
public enum PromptResult
{
    /// <summary>
    /// The user accepted the question, for example by pressing Yes.
    /// </summary>
    Yes = 0,

    /// <summary>
    /// The user declined the question, or closed the dialog without accepting it.
    /// </summary>
    No = 1,
}

/// <summary>
/// Shows the user a modal Yes/No question or a modal informational message. This is the seam
/// between the application's services and the dialog technology, so the elevation offer and the
/// startup messages can be exercised in tests without WPF.
/// </summary>
/// <remarks>
/// <para>
/// The production implementation, <c>MessageBoxUserPrompt</c>, asks questions in a modal WPF
/// dialog that answers No to Esc and Close, and shows messages in WPF message boxes; tests
/// substitute <c>FakeUserPrompt</c>, which records each call and returns scripted answers.
/// </para>
/// <para>
/// Both members are modal: they block the calling thread until the user dismisses the dialog.
/// Implementations must be callable from any thread. The production implementation marshals
/// the dialog synchronously to the UI dispatcher when the caller is on another thread.
/// </para>
/// <para>
/// Callers pass finished English text, normally from <c>UiText</c>. Implementations show the
/// title and message verbatim and add no wording of their own.
/// </para>
/// </remarks>
public interface IUserPrompt
{
    /// <summary>
    /// Asks the user a modal Yes/No question and blocks until it is answered or dismissed.
    /// </summary>
    /// <remarks>
    /// <c>RunController</c> uses it for the elevation offer, at launch and again when Start is
    /// pressed while the process is not elevated: "RAMQuick needs Administrator rights to lock
    /// memory for testing and to read temperature sensors. Restart as Administrator?"
    /// </remarks>
    /// <param name="title">The dialog title, shown verbatim.</param>
    /// <param name="message">The question, shown verbatim.</param>
    /// <returns>
    /// <see cref="PromptResult.Yes"/> when the user accepts; otherwise
    /// <see cref="PromptResult.No"/>, including when the dialog is closed without an answer.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    PromptResult Ask(string title, string message);

    /// <summary>
    /// Shows the user a modal informational message with a single OK button and blocks until
    /// it is dismissed.
    /// </summary>
    /// <remarks>
    /// The application host uses it for messages shown before, or without, the main window:
    /// the unsupported-platform message, "RAMQuick is already running" for a second instance,
    /// and the damaged-installation message when the engine library is missing or its version
    /// does not match.
    /// </remarks>
    /// <param name="title">The dialog title, shown verbatim.</param>
    /// <param name="message">The message, shown verbatim.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="title"/> or <paramref name="message"/> is <see langword="null"/>.
    /// </exception>
    void Inform(string title, string message);
}
