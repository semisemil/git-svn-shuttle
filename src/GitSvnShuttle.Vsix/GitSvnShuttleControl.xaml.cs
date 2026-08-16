using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace GitSvnShuttle.Vsix;

public partial class GitSvnShuttleControl : UserControl, System.IDisposable
{
    private const double CompactLayoutThreshold = 720;

    public static readonly System.Windows.DependencyProperty IsCompactLayoutProperty =
        System.Windows.DependencyProperty.Register(
            nameof(IsCompactLayout),
            typeof(bool),
            typeof(GitSvnShuttleControl),
            new System.Windows.PropertyMetadata(true));

    public GitSvnShuttleControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnSizeChanged;
    }

    public bool IsCompactLayout
    {
        get => (bool)GetValue(IsCompactLayoutProperty);
        private set => SetValue(IsCompactLayoutProperty, value);
    }

    private void OnLoaded(object sender, System.Windows.RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            if (GitSvnShuttlePackage.Instance is GitSvnShuttlePackage package)
            {
                var viewModel = new GitSvnShuttleViewModel(package, SelectGitExecutable);
                DataContext = viewModel;
                viewModel.InitializeCommand.Execute(null!);
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

    private static string? SelectGitExecutable()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Git-SVN이 포함된 Git 실행 파일 선택",
            Filter = "Git 실행 파일 (git.exe)|git.exe|실행 파일 (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
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

    private void OnSizeChanged(object sender, System.Windows.SizeChangedEventArgs e) =>
        IsCompactLayout = e.NewSize.Width < CompactLayoutThreshold;

    private void OnRepositorySummaryMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not System.Windows.FrameworkElement row ||
            row.DataContext is not RepositoryViewModel repository ||
            !repository.CanExpand ||
            IsInsideButton(e.OriginalSource as System.Windows.DependencyObject, row))
        {
            return;
        }

        repository.IsExpanded = !repository.IsExpanded;
        e.Handled = true;
    }

    private static bool IsInsideButton(
        System.Windows.DependencyObject? source,
        System.Windows.DependencyObject row)
    {
        var current = source;
        while (current != null && !ReferenceEquals(current, row))
        {
            if (current is ButtonBase)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
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

    public void Dispose()
    {
        SizeChanged -= OnSizeChanged;
        if (DataContext is System.IDisposable disposable)
        {
            disposable.Dispose();
        }

        DataContext = null;
    }
}
