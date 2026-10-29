using System.Windows;

namespace RamQuick.App;

/// <summary>
/// The single RAMQuick window. All behaviour is bound to <see cref="ViewModels.MainViewModel"/>; sizing is handled
/// by <see cref="Behaviors.WorkAreaSizing"/>.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// Creates the window from <c>MainWindow.xaml</c>. The caller supplies the <see cref="FrameworkElement.DataContext"/>
    /// and, for the application, routes closing to the run controller.
    /// </summary>
    public MainWindow()
    {
        InitializeComponent();
    }
}
