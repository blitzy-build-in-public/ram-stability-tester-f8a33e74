using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using RamQuick.App.Services;
using Xunit;

namespace RamQuick.App.Tests;

/// <summary>
/// Tests for the question dialog of <see cref="MessageBoxUserPrompt"/>: its title and message, its two named buttons
/// with their access keys and focus highlight, No as the default, cancel and initially focused button, the buttons as
/// its only tab stops, its system fonts and colours, and its placement with and without an owner; how each way of
/// ending it maps to the answer, Esc and Close included; the STA thread it is built on when the caller is not on one;
/// and the argument guards.
/// </summary>
/// <remarks>
/// Each test builds its windows on an STA thread of its own and shuts that thread's dispatcher down at the end. The
/// tests run on an interactive desktop that other processes share, so a shown window is placed off every screen, is
/// never activated and has no taskbar button, and a shown dialog is answered by work posted to its own dispatcher. A
/// watchdog closes a dialog its answer did not end, and every wait is bounded, so a failing test can neither hang the
/// run nor leave a window behind.
/// </remarks>
public sealed class MessageBoxUserPromptTests
{
    private const string Title = UiText.ElevationPromptTitle;
    private const string Message = UiText.ElevationPromptMessage;

    // Far enough beyond the top-left corner of the virtual screen that no part of a dialog shows.
    private const double OffScreenDistance = 10_000;

    // A WPF dispatcher that never drains would otherwise hang the test run instead of failing it.
    private static readonly TimeSpan _staThreadTimeout = TimeSpan.FromMinutes(1);

    // How long a shown dialog may stay open before the watchdog closes it and fails the test.
    private static readonly TimeSpan _answerTimeout = TimeSpan.FromSeconds(15);

    /// <summary>A way the user can end the question dialog.</summary>
    public enum DialogEnding
    {
        /// <summary>The Yes button is pressed, through its automation Invoke pattern.</summary>
        YesButton,

        /// <summary>The Yes button's access key, Y, is pressed.</summary>
        YesAccessKey,

        /// <summary>The No button is pressed, through its automation Invoke pattern.</summary>
        NoButton,

        /// <summary>The No button's access key, N, is pressed.</summary>
        NoAccessKey,

        /// <summary>Esc is pressed.</summary>
        Escape,

        /// <summary>Enter is pressed while no button has been chosen, which activates the default button.</summary>
        Enter,

        /// <summary>The window's Close system command runs, as the title-bar Close and Alt+F4 run it.</summary>
        CloseCommand,

        /// <summary>The window is closed by <see cref="Window.Close"/>.</summary>
        Close,
    }

    /// <summary>
    /// Builds the dialog and checks its structure: the title and the wrapping message verbatim, both also its
    /// automation names; exactly two buttons, Yes then No, with their labels, access keys and automation names, each
    /// wearing the system highlight border while it has keyboard focus and exposing no automation element named with
    /// the access-key underscore, which a screen reader would read out; No alone the default and the cancel button,
    /// and the dialog's focused element; the two buttons its only focusable elements, in tab order within a window
    /// whose Tab navigation cycles; a fixed-size window sized to its content with a standard caption, which UI
    /// Automation exposes as a dialog before it is shown; and system fonts and colours as resource references.
    /// </summary>
    [Fact]
    public void CreateQuestionDialog_Structure_HasMessageAndYesNoButtonsWithNoAsDefaultCancelAndFocus() =>
        RunOnStaThread(() =>
        {
            Window dialog = MessageBoxUserPrompt.CreateQuestionDialog(Title, Message, owner: null);
            try
            {
                Assert.Equal(Title, dialog.Title);
                Assert.Equal(Title, AutomationProperties.GetName(dialog));
                Assert.Equal(ResizeMode.NoResize, dialog.ResizeMode);
                Assert.Equal(SizeToContent.WidthAndHeight, dialog.SizeToContent);
                Assert.Equal(WindowStyle.SingleBorderWindow, dialog.WindowStyle);
                Assert.Equal(KeyboardNavigationMode.Cycle, KeyboardNavigation.GetTabNavigation(dialog));
                Assert.True(AutomationProperties.GetIsDialog(dialog), "The dialog must be marked as a dialog.");
                Assert.True(
                    UIElementAutomationPeer.CreatePeerForElement(dialog).IsDialog(),
                    "UI Automation must expose the dialog as a dialog, so a screen reader reads its question.");

                List<DependencyObject> elements = LogicalDescendants(dialog).ToList();

                TextBlock messageText = Assert.Single(elements.OfType<TextBlock>(), text => text.Text == Message);
                Assert.Equal(TextWrapping.Wrap, messageText.TextWrapping);
                Assert.True(double.IsFinite(messageText.MaxWidth), "The message must wrap at a finite width.");
                Assert.Equal(Message, AutomationProperties.GetName(messageText));

                Button[] buttons = elements.OfType<Button>().ToArray();
                Assert.Equal(2, buttons.Length);
                Button yes = buttons[0];
                Button no = buttons[1];
                AssertButton(yes, UiText.PromptYesLabel, 'Y', UiText.PromptYesAutomationName);
                AssertButton(no, UiText.PromptNoLabel, 'N', UiText.PromptNoAutomationName);
                Assert.False(yes.IsDefault);
                Assert.False(yes.IsCancel);
                Assert.True(no.IsDefault);
                Assert.True(no.IsCancel);
                Assert.Same(no, FocusManager.GetFocusedElement(dialog));

                // Measured first, so each button's template has built the access-key text a peer could expose; a peer
                // caches its children on the first query.
                UIElement content = Assert.IsAssignableFrom<UIElement>(dialog.Content);
                content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                foreach (Button button in buttons)
                {
                    Assert.DoesNotContain(
                        AutomationDescendants(UIElementAutomationPeer.CreatePeerForElement(button))
                            .Select(peer => peer.GetName()),
                        name => name.Contains('_', StringComparison.Ordinal));
                }

                Assert.Equal<UIElement>(buttons, elements.OfType<UIElement>().Where(element => element.Focusable));
                Assert.True(yes.TabIndex < no.TabIndex, "Tab order must follow the buttons' order, Yes then No.");

                AssertSystemResource(dialog, Control.FontFamilyProperty, SystemFonts.MessageFontFamily);
                AssertSystemResource(dialog, Control.FontSizeProperty, SystemFonts.MessageFontSize);
                AssertSystemResource(dialog, Control.FontWeightProperty, SystemFonts.MessageFontWeight);
                AssertSystemResource(dialog, Control.FontStyleProperty, SystemFonts.MessageFontStyle);
                AssertSystemBrush(messageText, TextBlock.ForegroundProperty, SystemColors.WindowTextColor);

                Border[] areas = elements.OfType<Border>().ToArray();
                Assert.Equal(2, areas.Length);
                Border messageArea = Assert.Single(areas, area => ReferenceEquals(area.Child, messageText));
                Border footer = Assert.Single(areas, area => !ReferenceEquals(area, messageArea));
                AssertSystemBrush(messageArea, Border.BackgroundProperty, SystemColors.WindowColor);
                AssertSystemBrush(footer, Border.BackgroundProperty, SystemColors.ControlColor);
                Assert.Contains(yes, LogicalDescendants(footer));
                Assert.Contains(no, LogicalDescendants(footer));
            }
            finally
            {
                dialog.Close();
            }
        });

    /// <summary>
    /// Builds the dialog with and without an owner: with one it is owned by it, opens centred on it and has no taskbar
    /// button; without one it opens centred on the screen with a taskbar button of its own.
    /// </summary>
    /// <param name="hasOwner">Whether a shown window owns the dialog.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CreateQuestionDialog_Owner_CentresOnOwnerOrScreen(bool hasOwner) =>
        RunOnStaThread(() =>
        {
            Window? owner = null;
            Window? dialog = null;
            try
            {
                if (hasOwner)
                {
                    owner = new Window { Width = 100, Height = 100 };
                    PlaceOffScreen(owner);
                    owner.Show();
                }

                dialog = MessageBoxUserPrompt.CreateQuestionDialog(Title, Message, owner);

                Assert.Same(owner, dialog.Owner);
                Assert.Equal(
                    hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                    dialog.WindowStartupLocation);
                Assert.Equal(!hasOwner, dialog.ShowInTaskbar);
            }
            finally
            {
                dialog?.Close();
                owner?.Close();
            }
        });

    /// <summary>
    /// Shows the dialog modally and ends it one way: only Yes, by its button or its access key, answers Yes; No, its
    /// access key, Esc, Enter on the default button, the Close system command and closing the window all answer No.
    /// </summary>
    /// <param name="ending">How the dialog is ended.</param>
    /// <param name="expected">The answer that ending must give.</param>
    [Theory]
    [InlineData(DialogEnding.YesButton, PromptResult.Yes)]
    [InlineData(DialogEnding.YesAccessKey, PromptResult.Yes)]
    [InlineData(DialogEnding.NoButton, PromptResult.No)]
    [InlineData(DialogEnding.NoAccessKey, PromptResult.No)]
    [InlineData(DialogEnding.Escape, PromptResult.No)]
    [InlineData(DialogEnding.Enter, PromptResult.No)]
    [InlineData(DialogEnding.CloseCommand, PromptResult.No)]
    [InlineData(DialogEnding.Close, PromptResult.No)]
    public void ShowQuestionDialog_Ended_ReturnsAnswerOfThatEnding(DialogEnding ending, PromptResult expected) =>
        RunOnStaThread(() =>
        {
            Window dialog = MessageBoxUserPrompt.CreateQuestionDialog(Title, Message, owner: null);
            PlaceOffScreen(dialog);
            bool closed = false;
            dialog.Closed += (_, _) => closed = true;

            ExceptionDispatchInfo? endingFailure = null;
            dialog.Loaded += (_, _) =>
            {
                // Posted, so the dialog ends from inside its own modal loop, as it does when the user answers.
                _ = dialog.Dispatcher.InvokeAsync(
                    () =>
                    {
                        try
                        {
                            End(dialog, ending);
                        }
                        catch (Exception exception)
                        {
                            endingFailure = ExceptionDispatchInfo.Capture(exception);
                            CloseIfOpen(dialog, closed);
                        }
                    },
                    DispatcherPriority.Input);
            };

            bool timedOut = false;
            var watchdog = new DispatcherTimer(
                _answerTimeout,
                DispatcherPriority.Send,
                (_, _) =>
                {
                    timedOut = !closed;
                    CloseIfOpen(dialog, closed);
                },
                dialog.Dispatcher);

            PromptResult answer;
            try
            {
                answer = MessageBoxUserPrompt.ShowQuestionDialog(dialog);
            }
            finally
            {
                watchdog.Stop();
                CloseIfOpen(dialog, closed);
            }

            endingFailure?.Throw();
            Assert.False(timedOut, $"{ending} did not end the dialog within {_answerTimeout}.");
            Assert.Equal(expected, answer);
            Assert.False(dialog.IsVisible);
        });

    /// <summary>
    /// Runs work through the STA helper the question takes when no WPF application exists, from a caller of each
    /// apartment: an STA caller runs it inline on its own thread, and an MTA caller has it run on another thread, which
    /// is STA, so the dialog can be built there. Either way the caller receives the work's result.
    /// </summary>
    /// <param name="callerApartment">The apartment of the calling thread.</param>
    [Theory]
    [InlineData(ApartmentState.STA)]
    [InlineData(ApartmentState.MTA)]
    public void OnStaThread_CallerApartment_RunsWorkOnStaThreadAndReturnsItsResult(ApartmentState callerApartment) =>
        RunOnThread(callerApartment, () =>
        {
            int callerThreadId = Environment.CurrentManagedThreadId;

            (ApartmentState apartment, int threadId, string title) = MessageBoxUserPrompt.OnStaThread(() =>
            {
                Window dialog = MessageBoxUserPrompt.CreateQuestionDialog(Title, Message, owner: null);
                try
                {
                    return (
                        Thread.CurrentThread.GetApartmentState(),
                        Environment.CurrentManagedThreadId,
                        dialog.Title);
                }
                finally
                {
                    dialog.Close();
                }
            });

            Assert.Equal(ApartmentState.STA, apartment);
            Assert.Equal(callerApartment == ApartmentState.STA, threadId == callerThreadId);
            Assert.Equal(Title, title);
        });

    /// <summary>
    /// Runs throwing work through the STA helper from an MTA caller and checks that the caller receives the same
    /// exception, so a failure on the short-lived STA thread is never lost.
    /// </summary>
    [Fact]
    public void OnStaThread_WorkThrowsOnOtherThread_RethrowsToCaller() =>
        RunOnThread(ApartmentState.MTA, () =>
        {
            var thrown = new InvalidOperationException("Thrown on the STA thread.");

            InvalidOperationException caught = Assert.Throws<InvalidOperationException>(
                () => MessageBoxUserPrompt.OnStaThread<int>(() => throw thrown));

            Assert.Same(thrown, caught);
        });

    /// <summary>
    /// Asks with a <see langword="null"/> title or message and checks that the argument is rejected by name before
    /// any dialog is built.
    /// </summary>
    /// <param name="title">The title passed.</param>
    /// <param name="message">The message passed.</param>
    /// <param name="parameterName">The name of the rejected parameter.</param>
    [Theory]
    [InlineData(null, Message, "title")]
    [InlineData(Title, null, "message")]
    public void Ask_NullArgument_ThrowsArgumentNullException(string? title, string? message, string parameterName)
    {
        var prompt = new MessageBoxUserPrompt();

        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(() => prompt.Ask(title!, message!));

        Assert.Equal(parameterName, exception.ParamName);
    }

    /// <summary>
    /// Builds the dialog with a <see langword="null"/> title or message and checks that the argument is rejected by
    /// name.
    /// </summary>
    /// <param name="title">The title passed.</param>
    /// <param name="message">The message passed.</param>
    /// <param name="parameterName">The name of the rejected parameter.</param>
    [Theory]
    [InlineData(null, Message, "title")]
    [InlineData(Title, null, "message")]
    public void CreateQuestionDialog_NullArgument_ThrowsArgumentNullException(
        string? title,
        string? message,
        string parameterName)
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => MessageBoxUserPrompt.CreateQuestionDialog(title!, message!, owner: null));

        Assert.Equal(parameterName, exception.ParamName);
    }

    /// <summary>Shows a <see langword="null"/> dialog and checks that the argument is rejected by name.</summary>
    [Fact]
    public void ShowQuestionDialog_NullDialog_ThrowsArgumentNullException()
    {
        ArgumentNullException exception = Assert.Throws<ArgumentNullException>(
            () => MessageBoxUserPrompt.ShowQuestionDialog(null!));

        Assert.Equal("dialog", exception.ParamName);
    }

    // Runs inside the shown dialog's modal loop. The keys go through the access-key manager in the dialog's own
    // presentation source, the path a key press takes when the dialog has the keyboard focus.
    private static void End(Window dialog, DialogEnding ending)
    {
        Button[] buttons = LogicalDescendants(dialog).OfType<Button>().ToArray();
        Button yes = buttons[0];
        Button no = buttons[1];
        PresentationSource source = Assert.IsAssignableFrom<PresentationSource>(PresentationSource.FromVisual(dialog));
        switch (ending)
        {
            case DialogEnding.YesButton:
                Invoke(yes);
                break;
            case DialogEnding.YesAccessKey:
                PressKey(source, "Y", ending);
                break;
            case DialogEnding.NoButton:
                Invoke(no);
                break;
            case DialogEnding.NoAccessKey:
                PressKey(source, "N", ending);
                break;
            case DialogEnding.Escape:
                PressKey(source, "\u001B", ending);
                break;
            case DialogEnding.Enter:
                PressKey(source, "\r", ending);
                break;
            case DialogEnding.CloseCommand:
                SystemCommands.CloseWindow(dialog);
                break;
            case DialogEnding.Close:
                dialog.Close();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ending), ending, "Unknown dialog ending.");
        }
    }

    // ProcessKey reports whether further elements share the key, so false means the key reached exactly one
    // element or none; the dialog's answer, or the watchdog, then tells which.
    private static void PressKey(PresentationSource source, string key, DialogEnding ending) =>
        Assert.False(AccessKeyManager.ProcessKey(source, key, false), $"More than one element answers {ending}.");

    private static void Invoke(Button button)
    {
        AutomationPeer peer = UIElementAutomationPeer.CreatePeerForElement(button);
        IInvokeProvider invoke = Assert.IsAssignableFrom<IInvokeProvider>(peer.GetPattern(PatternInterface.Invoke));
        invoke.Invoke();
    }

    // A window that has closed cannot be closed again, so every cleanup path checks first.
    private static void CloseIfOpen(Window window, bool closed)
    {
        if (!closed)
        {
            window.Close();
        }
    }

    // The focused button must wear the system highlight border, the visible mark of the button Enter presses.
    private static void AssertButton(Button button, string label, char accessKey, string automationName)
    {
        AccessText text = Assert.IsType<AccessText>(button.Content);
        Assert.Equal(label, text.Text);
        Assert.Equal(accessKey, char.ToUpperInvariant(text.AccessKey));
        Assert.Equal(automationName, AutomationProperties.GetName(button));

        Style style = Assert.IsType<Style>(button.Style);
        Trigger focused = Assert.Single(
            style.Triggers.OfType<Trigger>(),
            trigger => trigger.Property == UIElement.IsKeyboardFocusedProperty && Equals(trigger.Value, true));
        Setter border = Assert.Single(
            focused.Setters.OfType<Setter>(),
            setter => setter.Property == Control.BorderBrushProperty);
        DynamicResourceExtension brush = Assert.IsType<DynamicResourceExtension>(border.Value);
        Assert.Equal(SystemColors.HighlightBrushKey, brush.ResourceKey);
    }

    // A resource reference, rather than a value copied once, is what lets a theme change, high contrast included,
    // restyle a dialog that is already open.
    private static void AssertSystemResource(DependencyObject element, DependencyProperty property, object expected)
    {
        Assert.True(
            DependencyPropertyHelper.GetValueSource(element, property).IsExpression,
            $"{property.Name} must be a resource reference.");
        Assert.Equal(expected, element.GetValue(property));
    }

    private static void AssertSystemBrush(DependencyObject element, DependencyProperty property, Color expected)
    {
        Assert.True(
            DependencyPropertyHelper.GetValueSource(element, property).IsExpression,
            $"{property.Name} must be a resource reference.");
        SolidColorBrush brush = Assert.IsAssignableFrom<SolidColorBrush>(element.GetValue(property));
        Assert.Equal(expected, brush.Color);
    }

    // The peers below a peer, as UI Automation's raw view walks them.
    private static IEnumerable<AutomationPeer> AutomationDescendants(AutomationPeer parent)
    {
        foreach (AutomationPeer child in parent.GetChildren() ?? [])
        {
            yield return child;
            foreach (AutomationPeer descendant in AutomationDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    private static IEnumerable<DependencyObject> LogicalDescendants(DependencyObject parent)
    {
        foreach (DependencyObject child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            yield return child;
            foreach (DependencyObject descendant in LogicalDescendants(child))
            {
                yield return descendant;
            }
        }
    }

    // Off every screen, never activated and without a taskbar button, so a shown window neither takes the shared
    // desktop's focus nor appears on it.
    private static void PlaceOffScreen(Window window)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = SystemParameters.VirtualScreenLeft - OffScreenDistance;
        window.Top = SystemParameters.VirtualScreenTop - OffScreenDistance;
        window.ShowActivated = false;
        window.ShowInTaskbar = false;
    }

    // xUnit v3 runs tests on thread-pool threads, but a WPF window needs an STA thread with its own dispatcher.
    private static void RunOnStaThread(Action body) => RunOnThread(ApartmentState.STA, body);

    // Runs the body on a new thread of the given apartment and shuts down any dispatcher it created.
    private static void RunOnThread(ApartmentState apartment, Action body)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(apartment);
        thread.Start();
        Assert.True(thread.Join(_staThreadTimeout), $"The {apartment} test thread did not finish in time.");
        failure?.Throw();
    }
}
