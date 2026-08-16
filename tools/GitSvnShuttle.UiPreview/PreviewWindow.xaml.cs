using System.Windows;
using GitSvnShuttle.Vsix;

namespace GitSvnShuttle.UiPreview;

public partial class PreviewWindow : Window
{
    private readonly GitSvnShuttleControl preview = new GitSvnShuttleControl
    {
        DataContext = PreviewData.Create(),
        PreserveDataContextOnLoad = true,
    };

    public PreviewWindow()
    {
        InitializeComponent();
        PreviewHost.Content = preview;
    }

    private void OnDarkClick(object sender, RoutedEventArgs e) => ThemePalette.Apply("dark");

    private void OnLightClick(object sender, RoutedEventArgs e) => ThemePalette.Apply("light");

    private void OnCompactClick(object sender, RoutedEventArgs e)
    {
        preview.Width = 420;
        Width = 470;
    }

    private void OnWideClick(object sender, RoutedEventArgs e)
    {
        preview.ClearValue(WidthProperty);
        Width = 1320;
    }
}
