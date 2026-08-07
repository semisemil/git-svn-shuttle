using System.Windows.Controls;
using System.Windows.Input;

namespace GitSvnShuttle.Vsix;

public partial class GitSvnShuttleControl : UserControl
{
    public GitSvnShuttleControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            if (GitSvnShuttlePackage.Instance is GitSvnShuttlePackage package)
            {
                var viewModel = new GitSvnShuttleViewModel(package);
                DataContext = viewModel;
                viewModel.RefreshCommand.Execute(null!);
            }
        }
        catch (System.Exception exception)
        {
            DataContext = null;
            Content = new TextBlock
            {
                Margin = new System.Windows.Thickness(18),
                Text = "Git-SVN Shuttle 화면을 열지 못했습니다.\n\n" + exception.Message,
                TextWrapping = System.Windows.TextWrapping.Wrap,
            };
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not GitSvnShuttleViewModel viewModel)
        {
            return;
        }

        if (viewModel.CancelPublishCommand.CanExecute(null!))
        {
            viewModel.CancelPublishCommand.Execute(null!);
            e.Handled = true;
        }
    }

    private void OnPublishOverlayMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, PublishOverlay) ||
            DataContext is not GitSvnShuttleViewModel viewModel ||
            !viewModel.CancelPublishCommand.CanExecute(null!))
        {
            return;
        }

        viewModel.CancelPublishCommand.Execute(null!);
        e.Handled = true;
    }
}
