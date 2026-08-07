using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GitSvnShuttle.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace GitSvnShuttle.Vsix;

internal sealed class GitSvnShuttleViewModel : INotifyPropertyChanged
{
    private readonly GitSvnShuttlePackage package;
    private readonly GitSvnWorkspaceService service;
    private string statusText = "솔루션의 Git-SVN 저장소를 찾는 중입니다.";
    private bool isBusy;
    private bool isPublishConfirmationOpen;
    private bool publishAllRequested;
    private string? publishRepositoryPath;

    public GitSvnShuttleViewModel(GitSvnShuttlePackage package)
    {
        this.package = package;
        var gitPath = Environment.GetEnvironmentVariable("GIT_SVN_SHUTTLE_GIT");
        service = new GitSvnWorkspaceService(new ProcessGitCommandRunner(
            string.IsNullOrWhiteSpace(gitPath) ? "git" : gitPath));

        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        RebaseAllCommand = new AsyncCommand(() => RunAllAsync(isDcommit: false), CanRunAll);
        DcommitAllCommand = new AsyncCommand(ShowPublishAllAsync, CanRequestPublishAll);
        ConfirmPublishCommand = new AsyncCommand(ConfirmPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
        CancelPublishCommand = new AsyncCommand(CancelPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RepositoryViewModel> Repositories { get; } = new ObservableCollection<RepositoryViewModel>();
    public ObservableCollection<PublishCommitViewModel> PendingPublishItems { get; } = new ObservableCollection<PublishCommitViewModel>();

    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand RebaseAllCommand { get; }
    public AsyncCommand DcommitAllCommand { get; }
    public AsyncCommand ConfirmPublishCommand { get; }
    public AsyncCommand CancelPublishCommand { get; }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(BusyVisibility));
                RefreshCommands();
            }
        }
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public bool IsPublishConfirmationOpen
    {
        get => isPublishConfirmationOpen;
        private set
        {
            if (SetProperty(ref isPublishConfirmationOpen, value))
            {
                OnPropertyChanged(nameof(PublishConfirmationVisibility));
                ConfirmPublishCommand.RaiseCanExecuteChanged();
                CancelPublishCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public Visibility PublishConfirmationVisibility =>
        IsPublishConfirmationOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility => Repositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public int RepositoryCount => Repositories.Count;

    public int TotalPendingCommits => Repositories.Sum(repository => repository.PendingCommits.Count);

    public string PublishAllLabel => TotalPendingCommits == 0
        ? "게시할 커밋 없음"
        : TotalPendingCommits + "개 커밋 SVN에 게시";

    public string PublishConfirmationSubtitle =>
        "커밋 " + PendingPublishItems.Count + "개를 아래 순서대로 게시합니다.";

    private bool CanRunAll() => !IsBusy && Repositories.Count > 0;

    private bool CanRequestPublishAll() =>
        !IsBusy && Repositories.Any(repository => repository.PendingCommits.Count > 0);

    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            ClosePublishConfirmation();
            var solutionDirectory = await package.GetSolutionDirectoryAsync(CancellationToken.None);
            if (solutionDirectory == null)
            {
                Repositories.Clear();
                StatusText = "먼저 솔루션을 여세요.";
                return;
            }

            StatusText = "Git-SVN 저장소를 찾는 중입니다.";
            var repositories = await service.DiscoverAsync(solutionDirectory, CancellationToken.None);
            Repositories.Clear();
            foreach (var repository in repositories)
            {
                Repositories.Add(new RepositoryViewModel(
                    repository,
                    () => RunOneAsync(repository.Path, isDcommit: false),
                    () => ShowPublishOneAsync(repository.Path)));
            }

            OnPropertyChanged(nameof(RepositoryCount));
            OnPropertyChanged(nameof(TotalPendingCommits));
            OnPropertyChanged(nameof(PublishAllLabel));
            OnPropertyChanged(nameof(EmptyStateVisibility));

            StatusText = repositories.Count == 0
                ? "Git-SVN 저장소를 찾지 못했습니다. git svn 런타임과 svn-remote 설정을 확인하세요."
                : repositories.Count + "개 저장소 검사 완료 · 게시할 커밋 " + TotalPendingCommits + "개";
            RefreshCommands();
        });
    }

    private async Task RunOneAsync(string repositoryPath, bool isDcommit)
    {
        await RunBusyAsync(async () =>
        {
            var action = isDcommit ? "SVN에 게시" : "SVN 변경 가져오기";
            StatusText = action + " 실행 중: " + repositoryPath;
            var result = isDcommit
                ? await service.DcommitAsync(repositoryPath, CancellationToken.None)
                : await service.RebaseAsync(repositoryPath, CancellationToken.None);
            await LogAsync(result);
            StatusText = result.Succeeded ? action + " 완료" : result.Message;
        });

        await RefreshAsync();
    }

    private Task ShowPublishAllAsync()
    {
        publishAllRequested = true;
        publishRepositoryPath = null;
        PopulatePublishItems(Repositories.Where(repository => repository.PendingCommits.Count > 0));
        IsPublishConfirmationOpen = PendingPublishItems.Count > 0;
        return Task.CompletedTask;
    }

    private Task ShowPublishOneAsync(string repositoryPath)
    {
        var repository = Repositories.FirstOrDefault(item =>
            string.Equals(item.Path, repositoryPath, StringComparison.OrdinalIgnoreCase));
        if (repository == null || repository.PendingCommits.Count == 0)
        {
            return Task.CompletedTask;
        }

        publishAllRequested = false;
        publishRepositoryPath = repositoryPath;
        PopulatePublishItems(new[] { repository });
        IsPublishConfirmationOpen = true;
        return Task.CompletedTask;
    }

    private async Task ConfirmPublishAsync()
    {
        var runAll = publishAllRequested;
        var repositoryPath = publishRepositoryPath;
        ClosePublishConfirmation();

        if (runAll)
        {
            await RunAllAsync(isDcommit: true);
        }
        else if (repositoryPath != null)
        {
            await RunOneAsync(repositoryPath, isDcommit: true);
        }
    }

    private Task CancelPublishAsync()
    {
        ClosePublishConfirmation();
        return Task.CompletedTask;
    }

    private void PopulatePublishItems(IEnumerable<RepositoryViewModel> repositories)
    {
        PendingPublishItems.Clear();
        foreach (var repository in repositories)
        {
            foreach (var commit in repository.PendingCommits)
            {
                PendingPublishItems.Add(new PublishCommitViewModel(
                    repository.Name,
                    commit.Subject,
                    commit.ShortHash));
            }
        }

        OnPropertyChanged(nameof(PublishConfirmationSubtitle));
    }

    private void ClosePublishConfirmation()
    {
        IsPublishConfirmationOpen = false;
        PendingPublishItems.Clear();
        publishAllRequested = false;
        publishRepositoryPath = null;
        OnPropertyChanged(nameof(PublishConfirmationSubtitle));
    }

    private async Task RunAllAsync(bool isDcommit)
    {
        await RunBusyAsync(async () =>
        {
            var paths = Repositories
                .Where(repository => !isDcommit || repository.PendingCommits.Count > 0)
                .Select(repository => repository.Path)
                .ToArray();

            if (paths.Length == 0)
            {
                StatusText = "SVN에 게시할 로컬 커밋이 없습니다.";
                return;
            }
            StatusText = isDcommit
                ? "모든 저장소를 검사한 뒤 순서대로 게시합니다."
                : "모든 저장소에서 순서대로 SVN 변경을 가져옵니다.";

            var results = isDcommit
                ? await service.DcommitAllAsync(paths, CancellationToken.None)
                : await service.RebaseAllAsync(paths, CancellationToken.None);

            foreach (var result in results)
            {
                await LogAsync(result);
            }

            var failure = results.FirstOrDefault(result => !result.Succeeded);
            StatusText = failure == null
                ? "모든 저장소 작업이 완료되었습니다."
                : failure.Message + " 이후 저장소는 실행하지 않았습니다.";
        });

        await RefreshAsync();
    }

    private async Task LogAsync(OperationResult result)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + (result.Succeeded ? "OK" : "FAIL") + "] " + result.RepositoryPath + Environment.NewLine +
            result.Message + Environment.NewLine + Environment.NewLine);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        try
        {
            await action();
        }
        catch (Exception exception)
        {
            StatusText = "실행 실패: " + exception.Message;
            var pane = await package.GetOutputPaneAsync(CancellationToken.None);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.OutputStringThreadSafe(exception + Environment.NewLine);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void RefreshCommands()
    {
        RefreshCommand.RaiseCanExecuteChanged();
        RebaseAllCommand.RaiseCanExecuteChanged();
        DcommitAllCommand.RaiseCanExecuteChanged();
        ConfirmPublishCommand.RaiseCanExecuteChanged();
        CancelPublishCommand.RaiseCanExecuteChanged();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

internal sealed class RepositoryViewModel : INotifyPropertyChanged
{
    private bool isExpanded = true;

    public RepositoryViewModel(GitSvnRepository repository, Func<Task> rebase, Func<Task> dcommit)
    {
        Name = repository.Name;
        Path = repository.Path;
        PendingCommits = repository.PendingCommits;
        SvnBaseline = repository.SvnBaseline;
        IsReady = repository.IsReady;
        Problem = repository.Problem ?? string.Empty;
        RebaseCommand = new AsyncCommand(rebase, () => repository.IsReady);
        DcommitCommand = new AsyncCommand(dcommit, () => repository.IsReady && repository.PendingCommits.Count > 0);
    }

    public string Name { get; }
    public string Path { get; }
    public IReadOnlyList<GitSvnCommit> PendingCommits { get; }
    public GitSvnCommit? SvnBaseline { get; }
    public bool IsReady { get; }
    public string Problem { get; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            if (isExpanded == value)
            {
                return;
            }

            isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ExpandedVisibility)));
        }
    }

    public Visibility ExpandedVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProblemVisibility => string.IsNullOrWhiteSpace(Problem) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PendingCommitsVisibility => PendingCommits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoPendingCommitsVisibility => PendingCommits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BaselineVisibility => SvnBaseline == null ? Visibility.Collapsed : Visibility.Visible;
    public string ProblemLabel => Problem == "커밋되지 않은 변경이 있습니다."
        ? "커밋되지 않은 변경"
        : "작업 필요";
    public AsyncCommand RebaseCommand { get; }
    public AsyncCommand DcommitCommand { get; }
}

internal sealed class PublishCommitViewModel
{
    public PublishCommitViewModel(string repositoryName, string subject, string shortHash)
    {
        RepositoryName = repositoryName;
        Subject = subject;
        ShortHash = shortHash;
    }

    public string RepositoryName { get; }
    public string Subject { get; }
    public string ShortHash { get; }
}
