using System.Windows;

namespace MujVideoEditor;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        // Popisky casoveho pravitka po 5 sekundach (zatim pevne na 2 minuty)
        TimeRuler.ItemsSource = Enumerable.Range(0, 25)
            .Select(i => TimeSpan.FromSeconds(i * 5).ToString(@"mm\:ss"));

        StateChanged += (_, _) => UpdateMaximizeState();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateMaximizeState()
    {
        bool maximized = WindowState == WindowState.Maximized;

        // Maximalizovane okno s vlastni listou presahuje okraje obrazovky, proto ho odsadime
        RootGrid.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "" : "";
        MaximizeButton.ToolTip = maximized ? "Obnovit" : "Maximalizovat";
    }
}
