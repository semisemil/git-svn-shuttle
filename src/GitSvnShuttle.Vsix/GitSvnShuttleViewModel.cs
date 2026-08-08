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

internal sealed class GitSvnShuttleViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GitSvnShuttlePackage package;
    private readonly GitSvnRuntimeDetector runtimeDetector;
    private readonly GitRuntimePreference runtimePreference;
    private readonly Func<string?> selectGitExecutable;
    private readonly RepositoryChangeMonitor changeMonitor;
    private readonly List<GitSvnPublishSnapshot> preparedPublishSnapshots = new List<GitSvnPublishSnapshot>();
    private GitSvnWorkspaceService? service;
    private string statusText = "솔루션의 Git-SVN 저장소를 찾는 중입니다.";
    private string runtimeTitle = "Git-SVN 확인 중";
    private string runtimeMessage = "사용 가능한 Git-SVN 실행 환경을 확인하고 있습니다.";
    private string runtimePath = string.Empty;
    private string runtimeVersion = string.Empty;
    private bool isBusy;
    private bool isRuntimeReady;
    private bool isRuntimePanelOpen = true;
    private bool isPublishConfirmationOpen;
    private bool disposed;
    private CancellationTokenSource? operationCancellation;

    public GitSvnShuttleViewModel(GitSvnShuttlePackage package, Func<string?> selectGitExecutable)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
        this.selectGitExecutable = selectGitExecutable ?? throw new ArgumentNullException(nameof(selectGitExecutable));
        runtimeDetector = new GitSvnRuntimeDetector();
        runtimePreference = new GitRuntimePreference();
        changeMonitor = new RepositoryChangeMonitor(OnRepositoryChangedAsync);

        InitializeCommand = new AsyncCommand(InitializeAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy && IsRuntimeReady);
        ToggleRuntimeSettingsCommand = new AsyncCommand(ToggleRuntimeSettingsAsync, () => !IsBusy);
        ChooseGitExecutableCommand = new AsyncCommand(ChooseGitExecutableAsync, () => !IsBusy);
        AutoDetectRuntimeCommand = new AsyncCommand(AutoDetectRuntimeAsync, () => !IsBusy);
        RecheckRuntimeCommand = new AsyncCommand(RecheckRuntimeAsync, () => !IsBusy);
        ResetRuntimeCommand = new AsyncCommand(ResetRuntimeAsync, () => !IsBusy);
        RebaseAllCommand = new AsyncCommand(RunRebaseAllAsync, CanRunAll);
        DcommitAllCommand = new AsyncCommand(ShowPublishAllAsync, CanRequestPublishAll);
        ConfirmPublishCommand = new AsyncCommand(ConfirmPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
        CancelPublishCommand = new AsyncCommand(CancelPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
        CancelOperationCommand = new AsyncCommand(CancelOperationAsync, () => IsBusy && operationCancellation != null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RepositoryViewModel> Repositories { get; } = new ObservableCollection<RepositoryViewModel>();
    public ObservableCollection<PublishCommitViewModel> PendingPublishItems { get; } = new ObservableCollection<PublishCommitViewModel>();

    public AsyncCommand InitializeCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ToggleRuntimeSettingsCommand { get; }
    public AsyncCommand ChooseGitExecutableCommand { get; }
    public AsyncCommand AutoDetectRuntimeCommand { get; }
    public AsyncCommand RecheckRuntimeCommand { get; }
    public AsyncCommand ResetRuntimeCommand { get; }
    public AsyncCommand RebaseAllCommand { get; }
    public AsyncCommand DcommitAllCommand { get; }
    public AsyncCommand ConfirmPublishCommand { get; }
    public AsyncCommand CancelPublishCommand { get; }
    public AsyncCommand CancelOperationCommand { get; }

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
                OnPropertyChanged(nameof(CancelOperationVisibility));
                RefreshCommands();
            }
        }
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelOperationVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRuntimeReady
    {
        get => isRuntimeReady;
        private set
        {
            if (SetProperty(ref isRuntimeReady, value))
            {
                OnPropertyChanged(nameof(RuntimeReadyVisibility));
                OnPropertyChanged(nameof(RuntimeWarningVisibility));
                OnPropertyChanged(nameof(WorkspaceVisibility));
                OnPropertyChanged(nameof(EmptyStateVisibility));
                OnPropertyChanged(nameof(RuntimePanelVisibility));
                RefreshCommands();
            }
        }
    }

    public bool IsRuntimePanelOpen
    {
        get => isRuntimePanelOpen;
        private set
        {
            if (SetProperty(ref isRuntimePanelOpen, value))
            {
                OnPropertyChanged(nameof(RuntimePanelVisibility));
            }
        }
    }

    public string RuntimeTitle
    {
        get => runtimeTitle;
        private set => SetProperty(ref runtimeTitle, value);
    }

    public string RuntimeMessage
    {
        get => runtimeMessage;
        private set => SetProperty(ref runtimeMessage, value);
    }

    public string RuntimePath
    {
        get => runtimePath;
        private set
        {
            if (SetProperty(ref runtimePath, value))
            {
                OnPropertyChanged(nameof(RuntimePathVisibility));
            }
        }
    }

    public string RuntimeVersion
    {
        get => runtimeVersion;
        private set => SetProperty(ref runtimeVersion, value);
    }

    public Visibility RuntimePanelVisibility =>
        IsRuntimePanelOpen || !IsRuntimeReady ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeReadyVisibility => IsRuntimeReady ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RuntimeWarningVisibility => IsRuntimeReady ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RuntimePathVisibility =>
        string.IsNullOrWhiteSpace(RuntimePath) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WorkspaceVisibility => IsRuntimeReady ? Visibility.Visible : Visibility.Collapsed;

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

    public Visibility EmptyStateVisibility =>
        IsRuntimeReady && Repositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public int RepositoryCount => Repositories.Count;

    public int TotalPendingCommits => Repositories.Sum(repository => repository.PendingCommits.Count);

    public string PublishAllLabel => TotalPendingCommits == 0
        ? "게시할 커밋 없음"
        : TotalPendingCommits + "개 커밋 SVN에 게시";

    public string PublishConfirmationSubtitle =>
        "커밋 " + PendingPublishItems.Count + "개를 아래 순서대로 게시합니다.";

    public string PublishTargetSummary
    {
        get
        {
            var targets = preparedPublishSnapshots
                .SelectMany(snapshot => snapshot.SvnTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return targets.Length == 0
                ? "SVN 대상 확인 불가"
                : string.Join("  ·  ", targets);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        operationCancellation?.Cancel();
        changeMonitor.Dispose();
    }

    private bool CanRunAll() => !IsBusy && IsRuntimeReady && Repositories.Count > 0;

    private bool CanRequestPublishAll() =>
        !IsBusy && IsRuntimeReady && Repositories.Any(repository => repository.PendingCommits.Count > 0);

    private async Task InitializeAsync()
    {
        var configuredPath = runtimePreference.GetSelectedPath();
        var ready = await DiagnoseRuntimeAsync(configuredPath, persistOnSuccess: false);
        if (ready)
        {
            await RefreshAsync();
        }
    }

    private Task ToggleRuntimeSettingsAsync()
    {
        if (IsRuntimeReady)
        {
            IsRuntimePanelOpen = !IsRuntimePanelOpen;
        }

        return Task.CompletedTask;
    }

    private async Task ChooseGitExecutableAsync()
    {
        var selectedPath = selectGitExecutable();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        var ready = await DiagnoseRuntimeAsync(selectedPath, persistOnSuccess: true);
        if (ready)
        {
            await RefreshAsync();
        }
    }

    private async Task AutoDetectRuntimeAsync()
    {
        var ready = await DiagnoseRuntimeAsync(null, persistOnSuccess: true);
        if (ready)
        {
            await RefreshAsync();
        }
    }

    private async Task RecheckRuntimeAsync()
    {
        var path = string.IsNullOrWhiteSpace(RuntimePath)
            ? runtimePreference.GetSelectedPath()
            : RuntimePath;
        var ready = await DiagnoseRuntimeAsync(path, persistOnSuccess: false);
        if (ready)
        {
            await RefreshAsync();
        }
    }

    private async Task ResetRuntimeAsync()
    {
        runtimePreference.Reset();
        var ready = await DiagnoseRuntimeAsync(null, persistOnSuccess: false);
        if (ready)
        {
            await RefreshAsync();
        }
    }

    private async Task<bool> DiagnoseRuntimeAsync(string? path, bool persistOnSuccess)
    {
        GitSvnRuntimeDiagnostic? diagnostic = null;
        await RunBusyAsync(async () =>
        {
            ClosePublishConfirmation();
            StatusText = "Git-SVN 실행 환경을 확인하는 중입니다.";
            diagnostic = await runtimeDetector.DiagnoseAsync(path, OperationToken);
            ApplyRuntimeDiagnostic(diagnostic);
            if (diagnostic.IsReady && persistOnSuccess)
            {
                runtimePreference.Save(diagnostic.ExecutablePath);
            }
        });

        return diagnostic?.IsReady == true;
    }

    private void ApplyRuntimeDiagnostic(GitSvnRuntimeDiagnostic diagnostic)
    {
        RuntimePath = diagnostic.ExecutablePath;
        RuntimeVersion = string.IsNullOrWhiteSpace(diagnostic.GitSvnVersion)
            ? diagnostic.GitVersion
            : diagnostic.GitSvnVersion;
        RuntimeMessage = diagnostic.Message;
        RuntimeTitle = diagnostic.Status switch
        {
            GitSvnRuntimeStatus.Ready => "Git-SVN 준비됨",
            GitSvnRuntimeStatus.GitSvnNotAvailable => "Git-SVN 구성 요소 없음",
            GitSvnRuntimeStatus.GitNotFound => "Git 실행 파일 필요",
            _ => "Git-SVN 확인 실패",
        };

        service = diagnostic.IsReady
            ? new GitSvnWorkspaceService(new ProcessGitCommandRunner(diagnostic.ExecutablePath))
            : null;
        IsRuntimeReady = diagnostic.IsReady;
        IsRuntimePanelOpen = !diagnostic.IsReady;
        StatusText = diagnostic.Message;

        if (!diagnostic.IsReady)
        {
            Repositories.Clear();
            changeMonitor.Configure(string.Empty, Array.Empty<GitSvnRepository>());
            NotifyRepositorySummaryChanged();
        }
    }

    private async Task RefreshAsync()
    {
        if (disposed || service == null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            ClosePublishConfirmation();
            var solutionDirectory = await package.GetSolutionDirectoryAsync(OperationToken);
            if (solutionDirectory == null)
            {
                Repositories.Clear();
                changeMonitor.Configure(string.Empty, Array.Empty<GitSvnRepository>());
                StatusText = "먼저 솔루션을 여세요.";
                NotifyRepositorySummaryChanged();
                return;
            }

            StatusText = "Git-SVN 저장소를 찾는 중입니다.";
            var repositories = await WorkspaceService.DiscoverAsync(solutionDirectory, OperationToken);
            Repositories.Clear();
            foreach (var repository in repositories)
            {
                Repositories.Add(new RepositoryViewModel(
                    repository,
                    () => RunRebaseOneAsync(repository.Path),
                    () => ShowPublishOneAsync(repository.Path)));
            }

            changeMonitor.Configure(solutionDirectory, repositories);
            NotifyRepositorySummaryChanged();
            StatusText = repositories.Count == 0
                ? "Git-SVN 저장소를 찾지 못했습니다. git svn 런타임과 svn-remote 설정을 확인하세요."
                : repositories.Count + "개 저장소 검사 완료 · 게시할 커밋 " + TotalPendingCommits + "개";
            RefreshCommands();
        });
    }

    private async Task OnRepositoryChangedAsync()
    {
        if (disposed || IsBusy)
        {
            return;
        }

        await RefreshAsync();
    }

    private async Task RunRebaseOneAsync(string repositoryPath)
    {
        OperationResult? result = null;
        await RunBusyAsync(async () =>
        {
            StatusText = "SVN 변경 가져오기 실행 중: " + repositoryPath;
            result = await WorkspaceService.RebaseAsync(repositoryPath, OperationToken);
            await LogAsync(result);
        });

        await RefreshAsync();
        if (result != null)
        {
            StatusText = result.Succeeded ? "SVN 변경 가져오기 완료" : SensitiveTextRedactor.Redact(result.Message);
        }
    }

    private async Task RunRebaseAllAsync()
    {
        IReadOnlyList<OperationResult> results = Array.Empty<OperationResult>();
        await RunBusyAsync(async () =>
        {
            var paths = Repositories.Select(repository => repository.Path).ToArray();
            if (paths.Length == 0)
            {
                return;
            }

            StatusText = "모든 저장소에서 순서대로 SVN 변경을 가져옵니다.";
            results = await WorkspaceService.RebaseAllAsync(paths, OperationToken);
            foreach (var result in results)
            {
                await LogAsync(result);
            }
        });

        await RefreshAsync();
        var failure = results.FirstOrDefault(result => !result.Succeeded);
        StatusText = failure == null
            ? "모든 저장소의 SVN 변경을 가져왔습니다."
            : SensitiveTextRedactor.Redact(failure.Message) + " 이후 저장소는 실행하지 않았습니다.";
    }

    private async Task ShowPublishAllAsync()
    {
        await PreparePublishAsync(Repositories.Where(repository => repository.PendingCommits.Count > 0));
    }

    private async Task ShowPublishOneAsync(string repositoryPath)
    {
        var repository = Repositories.FirstOrDefault(item =>
            string.Equals(item.Path, repositoryPath, StringComparison.OrdinalIgnoreCase));
        if (repository == null || repository.PendingCommits.Count == 0)
        {
            return;
        }

        await PreparePublishAsync(new[] { repository });
    }

    private async Task PreparePublishAsync(IEnumerable<RepositoryViewModel> repositories)
    {
        await RunBusyAsync(async () =>
        {
            ClosePublishConfirmation();
            StatusText = "게시할 커밋과 SVN 대상을 확인하는 중입니다.";

            foreach (var repository in repositories)
            {
                var preparation = await WorkspaceService.PrepareDcommitAsync(repository.Path, OperationToken);
                if (!preparation.Succeeded)
                {
                    await LogAsync(preparation.Outcome);
                    StatusText = SensitiveTextRedactor.Redact(preparation.Outcome.Message);
                    ClosePublishConfirmation();
                    return;
                }

                preparedPublishSnapshots.Add(preparation.Snapshot!);
            }

            PopulatePublishItems(preparedPublishSnapshots);
            IsPublishConfirmationOpen = PendingPublishItems.Count > 0;
            StatusText = "확인한 상태가 바뀌면 게시를 자동으로 중단합니다.";
        });
    }

    private async Task ConfirmPublishAsync()
    {
        var snapshots = preparedPublishSnapshots.ToArray();
        ClosePublishConfirmation();
        if (snapshots.Length == 0)
        {
            return;
        }

        IReadOnlyList<OperationResult> results = Array.Empty<OperationResult>();
        await RunBusyAsync(async () =>
        {
            StatusText = "확인한 상태와 현재 저장소 상태를 비교하는 중입니다.";
            results = snapshots.Length == 1
                ? new[] { await WorkspaceService.DcommitPreparedAsync(snapshots[0], OperationToken) }
                : await WorkspaceService.DcommitPreparedAllAsync(snapshots, OperationToken);

            foreach (var result in results)
            {
                await LogAsync(result);
            }
        });

        await RefreshAsync();
        var failure = results.FirstOrDefault(result => !result.Succeeded);
        StatusText = failure == null
            ? "확인한 커밋을 SVN에 게시했습니다."
            : SensitiveTextRedactor.Redact(failure.Message);
    }

    private Task CancelPublishAsync()
    {
        ClosePublishConfirmation();
        return Task.CompletedTask;
    }

    private Task CancelOperationAsync()
    {
        operationCancellation?.Cancel();
        StatusText = "작업 취소를 요청했습니다.";
        return Task.CompletedTask;
    }

    private void PopulatePublishItems(IEnumerable<GitSvnPublishSnapshot> snapshots)
    {
        PendingPublishItems.Clear();
        foreach (var snapshot in snapshots)
        {
            foreach (var commit in snapshot.PendingCommits)
            {
                PendingPublishItems.Add(new PublishCommitViewModel(
                    snapshot.RepositoryName,
                    commit.Subject,
                    commit.ShortHash));
            }
        }

        OnPropertyChanged(nameof(PublishConfirmationSubtitle));
        OnPropertyChanged(nameof(PublishTargetSummary));
    }

    private void ClosePublishConfirmation()
    {
        IsPublishConfirmationOpen = false;
        PendingPublishItems.Clear();
        preparedPublishSnapshots.Clear();
        OnPropertyChanged(nameof(PublishConfirmationSubtitle));
        OnPropertyChanged(nameof(PublishTargetSummary));
    }

    private async Task LogAsync(OperationResult result)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + (result.Succeeded ? "OK" : "FAIL") + "] " + result.RepositoryPath + Environment.NewLine +
            SensitiveTextRedactor.Redact(result.Message) + Environment.NewLine + Environment.NewLine);
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy || disposed)
        {
            return;
        }

        using (var cancellation = new CancellationTokenSource())
        try
        {
            operationCancellation = cancellation;
            IsBusy = true;
            CancelOperationCommand.RaiseCanExecuteChanged();
            await action();
        }
        catch (OperationCanceledException)
        {
            StatusText = "작업을 취소했습니다.";
        }
        catch (Exception exception)
        {
            var safeMessage = SensitiveTextRedactor.Redact(exception.Message);
            StatusText = "실행 실패: " + safeMessage;
            var pane = await package.GetOutputPaneAsync(CancellationToken.None);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            pane?.OutputStringThreadSafe("Git-SVN Shuttle 실행 실패: " + safeMessage + Environment.NewLine);
        }
        finally
        {
            operationCancellation = null;
            IsBusy = false;
        }
    }

    private void NotifyRepositorySummaryChanged()
    {
        OnPropertyChanged(nameof(RepositoryCount));
        OnPropertyChanged(nameof(TotalPendingCommits));
        OnPropertyChanged(nameof(PublishAllLabel));
        OnPropertyChanged(nameof(EmptyStateVisibility));
    }

    private void RefreshCommands()
    {
        InitializeCommand.RaiseCanExecuteChanged();
        RefreshCommand.RaiseCanExecuteChanged();
        ToggleRuntimeSettingsCommand.RaiseCanExecuteChanged();
        ChooseGitExecutableCommand.RaiseCanExecuteChanged();
        AutoDetectRuntimeCommand.RaiseCanExecuteChanged();
        RecheckRuntimeCommand.RaiseCanExecuteChanged();
        ResetRuntimeCommand.RaiseCanExecuteChanged();
        RebaseAllCommand.RaiseCanExecuteChanged();
        DcommitAllCommand.RaiseCanExecuteChanged();
        ConfirmPublishCommand.RaiseCanExecuteChanged();
        CancelPublishCommand.RaiseCanExecuteChanged();
        CancelOperationCommand.RaiseCanExecuteChanged();
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

    private CancellationToken OperationToken =>
        operationCancellation?.Token ?? CancellationToken.None;

    private GitSvnWorkspaceService WorkspaceService =>
        service ?? throw new InvalidOperationException("Git-SVN 런타임이 준비되지 않았습니다.");
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
