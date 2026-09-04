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
using static GitSvnShuttle.Vsix.OperationOutcomeBuilder;
using static GitSvnShuttle.Vsix.OperationPresentation;

namespace GitSvnShuttle.Vsix;

internal enum RepositoryRefreshReason
{
    Manual,
    Automatic,
    Internal,
    ContextReset,
}

internal sealed class GitSvnShuttleViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly GitSvnShuttlePackage package;
    private readonly GitSvnRuntimeDetector runtimeDetector;
    private readonly GitRuntimePreference runtimePreference;
    private readonly Func<string?> selectGitExecutable;
    private readonly RepositoryChangeMonitor changeMonitor;
    private readonly PublishConfirmationViewModel publishConfirmation = new PublishConfirmationViewModel();
    private readonly WorkspaceOutputLogger outputLogger;
    private readonly RepositorySessionState repositoryState = new RepositorySessionState();
    private GitSvnWorkspaceService? service;
    private string currentSolutionDirectory = string.Empty;
    private string statusText = "솔루션의 Git-SVN 저장소를 찾는 중입니다.";
    private string runtimeTitle = "Git-SVN 확인 중";
    private string runtimeMessage = "사용 가능한 Git-SVN 실행 환경을 확인하고 있습니다.";
    private string runtimePath = string.Empty;
    private string runtimeVersion = string.Empty;
    private bool isBusy;
    private bool isRuntimeReady;
    private bool isRuntimePanelOpen = true;
    private bool disposed;
    private bool refreshSolutionAfterBusy;
    private CancellationTokenSource? operationCancellation;
    private PublishProgress? activePublishProgress;

    public GitSvnShuttleViewModel(GitSvnShuttlePackage package, Func<string?> selectGitExecutable)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
        this.selectGitExecutable = selectGitExecutable ?? throw new ArgumentNullException(nameof(selectGitExecutable));
        outputLogger = new WorkspaceOutputLogger(package);
        publishConfirmation.PropertyChanged += OnPublishConfirmationChanged;
        runtimeDetector = new GitSvnRuntimeDetector();
        runtimePreference = new GitRuntimePreference();
        changeMonitor = new RepositoryChangeMonitor(OnRepositoryChangedAsync);
        package.SolutionContextChanged += OnSolutionContextChanged;

        InitializeCommand = new AsyncCommand(InitializeAsync, () => !IsBusy);
        RefreshCommand = new AsyncCommand(ManualRefreshAsync, () => !IsBusy && IsRuntimeReady);
        ToggleRuntimeSettingsCommand = new AsyncCommand(ToggleRuntimeSettingsAsync, () => !IsBusy);
        ChooseGitExecutableCommand = new AsyncCommand(ChooseGitExecutableAsync, () => !IsBusy);
        AutoDetectRuntimeCommand = new AsyncCommand(AutoDetectRuntimeAsync, () => !IsBusy);
        RecheckRuntimeCommand = new AsyncCommand(RecheckRuntimeAsync, () => !IsBusy);
        ResetRuntimeCommand = new AsyncCommand(ResetRuntimeAsync, () => !IsBusy);
        RebaseAllCommand = new AsyncCommand(RunRebaseAllAsync, CanRunAll);
        DcommitAllCommand = new AsyncCommand(ShowSelectedPublishAsync, CanRequestSelectedPublish);
        ToggleAllSelectionCommand = new AsyncCommand(ToggleAllSelectionAsync, CanChangeSelection);
        ClearSelectionCommand = new AsyncCommand(ClearSelectionAsync, () => !IsBusy && SelectedRepositoryCount > 0);
        ConfirmPublishCommand = new AsyncCommand(ConfirmPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
        CancelPublishCommand = new AsyncCommand(CancelPublishAsync, () => IsPublishConfirmationOpen && !IsBusy);
        CancelOperationCommand = new AsyncCommand(CancelOperationAsync, () => IsBusy && operationCancellation != null);
        ShowLogCommand = new AsyncCommand(ShowLogAsync);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<RepositoryViewModel> Repositories { get; } = new ObservableCollection<RepositoryViewModel>();
    public ObservableCollection<PublishCommitViewModel> PendingPublishItems => publishConfirmation.PendingPublishItems;

    public AsyncCommand InitializeCommand { get; }
    public AsyncCommand RefreshCommand { get; }
    public AsyncCommand ToggleRuntimeSettingsCommand { get; }
    public AsyncCommand ChooseGitExecutableCommand { get; }
    public AsyncCommand AutoDetectRuntimeCommand { get; }
    public AsyncCommand RecheckRuntimeCommand { get; }
    public AsyncCommand ResetRuntimeCommand { get; }
    public AsyncCommand RebaseAllCommand { get; }
    public AsyncCommand DcommitAllCommand { get; }
    public AsyncCommand ToggleAllSelectionCommand { get; }
    public AsyncCommand ClearSelectionCommand { get; }
    public AsyncCommand ConfirmPublishCommand { get; }
    public AsyncCommand CancelPublishCommand { get; }
    public AsyncCommand CancelOperationCommand { get; }
    public AsyncCommand ShowLogCommand { get; }

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

    public bool IsPublishConfirmationOpen => publishConfirmation.IsPublishConfirmationOpen;

    public Visibility PublishConfirmationVisibility =>
        IsPublishConfirmationOpen ? Visibility.Visible : Visibility.Collapsed;

    public Visibility EmptyStateVisibility =>
        IsRuntimeReady && Repositories.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    public int RepositoryCount => Repositories.Count;

    public int TotalPendingCommits => Repositories.Sum(repository => repository.PendingCommits.Count);

    public int SelectedRepositoryCount => repositoryState.SelectedCount;

    public bool? AllSelectionState => repositoryState.GetAllSelectionState(
        Repositories.Count(repository => repository.CanSelect));

    public string SelectionSummary => SelectedRepositoryCount == 0
        ? "게시할 저장소를 선택하세요."
        : SelectedRepositoryCount + "개 저장소 선택됨";

    public string PublishSelectionTooltip => SelectedRepositoryCount == 0
        ? "게시할 저장소를 먼저 선택하세요."
        : "선택한 저장소 " + SelectedRepositoryCount + "개를 확인한 뒤 SVN에 게시";

    public string PublishSelectionAutomationName => SelectedRepositoryCount == 0
        ? "선택한 저장소 없음, SVN 게시 비활성"
        : "선택한 저장소 " + SelectedRepositoryCount + "개를 SVN에 게시";

    public string TopPublishSelectionActionName =>
        "상단 도구 모음: " + PublishSelectionAutomationName;

    public string SummaryPublishSelectionActionName =>
        "선택 요약: " + PublishSelectionAutomationName;

    public Visibility SelectionBadgeVisibility =>
        SelectedRepositoryCount == 0 ? Visibility.Collapsed : Visibility.Visible;

    public string PublishAllLabel => TotalPendingCommits == 0
        ? "게시할 커밋 없음"
        : TotalPendingCommits + "개 커밋 SVN에 게시";

    public string PublishConfirmationSubtitle => publishConfirmation.PublishConfirmationSubtitle;
    public string PublishTargetSummary => publishConfirmation.PublishTargetSummary;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        operationCancellation?.Cancel();
        package.SolutionContextChanged -= OnSolutionContextChanged;
        changeMonitor.Dispose();
        publishConfirmation.PropertyChanged -= OnPublishConfirmationChanged;
    }

    private bool CanRunAll() => !IsBusy && IsRuntimeReady && Repositories.Count > 0;

    private bool CanRequestSelectedPublish() =>
        !IsBusy && IsRuntimeReady && SelectedRepositoryCount > 0;

    private bool CanChangeSelection() =>
        !IsBusy && Repositories.Any(repository => repository.CanSelect);

    private async Task InitializeAsync()
    {
        var configuredPath = runtimePreference.GetSelectedPath();
        var ready = await DiagnoseRuntimeAsync(configuredPath, persistOnSuccess: false);
        if (ready)
        {
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
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
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        }
    }

    private async Task AutoDetectRuntimeAsync()
    {
        var ready = await DiagnoseRuntimeAsync(null, persistOnSuccess: true);
        if (ready)
        {
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
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
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        }
    }

    private async Task ResetRuntimeAsync()
    {
        runtimePreference.Reset();
        var ready = await DiagnoseRuntimeAsync(null, persistOnSuccess: false);
        if (ready)
        {
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        }
    }

    private async Task<bool> DiagnoseRuntimeAsync(string? path, bool persistOnSuccess)
    {
        ClearOperationOutcomes();
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
        var runtimeChanged = !string.Equals(
            RuntimePath,
            diagnostic.ExecutablePath,
            StringComparison.OrdinalIgnoreCase);
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

        if (runtimeChanged || !diagnostic.IsReady)
        {
            ResetRepositorySession();
        }

        if (!diagnostic.IsReady)
        {
            Repositories.Clear();
            changeMonitor.Configure(string.Empty, Array.Empty<GitSvnRepository>());
            NotifyRepositorySummaryChanged();
        }
    }

    private Task ManualRefreshAsync()
    {
        ClearOperationOutcomes();
        return RefreshRepositoriesAsync(RepositoryRefreshReason.Manual);
    }

    private async Task RefreshRepositoriesAsync(RepositoryRefreshReason reason)
    {
        if (disposed || service == null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var confirmationInvalidated =
                reason == RepositoryRefreshReason.Automatic && IsPublishConfirmationOpen;
            ClosePublishConfirmation();
            var solutionDirectory = await package.GetSolutionDirectoryAsync(OperationToken);
            if (solutionDirectory == null)
            {
                ResetRepositorySession();
                Repositories.Clear();
                changeMonitor.Configure(string.Empty, Array.Empty<GitSvnRepository>());
                StatusText = "먼저 솔루션을 여세요.";
                NotifyRepositorySummaryChanged();
                return;
            }

            var solutionChanged = !string.Equals(
                currentSolutionDirectory,
                solutionDirectory,
                StringComparison.OrdinalIgnoreCase);
            if (reason == RepositoryRefreshReason.ContextReset || solutionChanged)
            {
                repositoryState.Reset();
            }

            currentSolutionDirectory = solutionDirectory;

            StatusText = "Git-SVN 저장소를 찾는 중입니다.";
            var projectPaths = await package.GetLoadedProjectPathsAsync(OperationToken);
            var repositories = await WorkspaceService.DiscoverAsync(
                solutionDirectory,
                projectPaths,
                OperationToken);
            repositoryState.Reconcile(repositories.Select(repository =>
                new RepositoryAvailability(
                    repository.Path,
                    repository.IsReady && repository.PendingCommits.Count > 0,
                    repository.PendingCommits.Count > 0)));
            Repositories.Clear();
            foreach (var repository in repositories)
            {
                Repositories.Add(new RepositoryViewModel(
                    repository,
                    () => RunRebaseOneAsync(repository.Path),
                    () => ShowPublishOneAsync(repository.Path),
                    () => ContinueRebaseOneAsync(repository.Path),
                    () => AbortRebaseOneAsync(repository.Path),
                    isSelected => OnRepositorySelectionChanged(repository.Path, isSelected),
                    isExpanded => repositoryState.SetExpanded(
                        repository.Path,
                        isExpanded,
                        repository.PendingCommits.Count > 0),
                    repositoryState.IsSelected(repository.Path),
                    repositoryState.IsExpanded(repository.Path),
                    repositoryState.GetOutcome(repository.Path)));
            }

            changeMonitor.Configure(solutionDirectory, repositories);
            NotifyRepositorySummaryChanged();
            StatusText = confirmationInvalidated
                ? "저장소 변경을 감지해 게시 확인을 닫고 준비 상태를 폐기했습니다."
                : repositories.Count == 0
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

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Automatic);
    }

    private void OnSolutionContextChanged(object? sender, SolutionContextChangedEventArgs eventArgs)
    {
        if (disposed)
        {
            return;
        }

        operationCancellation?.Cancel();
        ResetRepositorySession();
        Repositories.Clear();
        changeMonitor.Configure(string.Empty, Array.Empty<GitSvnRepository>());
        NotifyRepositorySummaryChanged();
        StatusText = eventArgs.IsOpen
            ? "새 솔루션의 Git-SVN 저장소를 찾는 중입니다."
            : "먼저 솔루션을 여세요.";

        if (!eventArgs.IsOpen || !IsRuntimeReady)
        {
            return;
        }

        if (IsBusy)
        {
            refreshSolutionAfterBusy = true;
            return;
        }

        QueueSolutionRefresh();
    }

    private void QueueSolutionRefresh()
    {
#pragma warning disable VSSDK007 // Solution events are synchronous; FileAndForget observes refresh failures.
        ThreadHelper.JoinableTaskFactory.RunAsync(async () =>
        {
            await RefreshRepositoriesAsync(RepositoryRefreshReason.ContextReset);
        }).FileAndForget("GitSvnShuttle/SolutionContextChanged");
#pragma warning restore VSSDK007
    }

    private async Task RunRebaseOneAsync(string repositoryPath)
    {
        await RunSingleRebaseOperationAsync(
            repositoryPath,
            "SVN 변경 가져오기 실행 중: ",
            () => WorkspaceService.RebaseAsync(repositoryPath, OperationToken));
    }

    private async Task RunRebaseAllAsync()
    {
        ClearOperationOutcomes();
        var paths = Repositories.Select(repository => repository.Path).ToArray();
        if (paths.Length == 0)
        {
            return;
        }

        IReadOnlyList<OperationResult> results = Array.Empty<OperationResult>();
        var execution = await RunBusyAsync(async () =>
        {
            StatusText = "모든 저장소에서 순서대로 SVN 변경을 가져옵니다.";
            results = await WorkspaceService.RebaseAllAsync(paths, OperationToken);
        });

        var outcomes = BuildRebaseOutcomes(paths, results, execution);
        repositoryState.SetOutcomes(outcomes);
        foreach (var outcome in outcomes)
        {
            await outputLogger.LogOperationOutcomeAsync(outcome);
        }

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        StatusText = BuildRebaseSummary(outcomes);
    }

    private async Task ContinueRebaseOneAsync(string repositoryPath)
    {
        await RunSingleRebaseOperationAsync(
            repositoryPath,
            "rebase를 계속하는 중입니다: ",
            () => WorkspaceService.ContinueRebaseAsync(repositoryPath, OperationToken));
    }

    private async Task AbortRebaseOneAsync(string repositoryPath)
    {
        var confirmed = MessageBox.Show(
            "진행 중인 rebase를 중단하고 시작 전 상태로 되돌리시겠습니까?",
            "rebase 중단 확인",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed)
        {
            StatusText = "rebase 중단을 취소했습니다.";
            return;
        }

        await RunSingleRebaseOperationAsync(
            repositoryPath,
            "rebase를 중단하는 중입니다: ",
            () => WorkspaceService.AbortRebaseAsync(repositoryPath, confirmed: true, OperationToken));
    }

    private async Task RunSingleRebaseOperationAsync(
        string repositoryPath,
        string progressPrefix,
        Func<Task<OperationResult>> operation)
    {
        ClearOperationOutcomes();
        OperationResult? result = null;
        var execution = await RunBusyAsync(async () =>
        {
            StatusText = progressPrefix + repositoryPath;
            result = await operation();
        });

        var outcome = result == null
            ? OutcomeFromBusyExecution(repositoryPath, RepositoryOperationKind.Rebase, execution)
            : new RepositoryOperationOutcome(
                repositoryPath,
                RepositoryOperationKind.Rebase,
                result.Succeeded
                    ? RepositoryOperationOutcomeKind.Succeeded
                    : RepositoryOperationOutcomeKind.Failed,
                result.Message);
        repositoryState.SetOutcome(outcome);
        await outputLogger.LogOperationOutcomeAsync(outcome);
        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        StatusText = BuildOperationStatus(outcome);
    }

    private async Task ShowSelectedPublishAsync()
    {
        var selectedRepositories = repositoryState.SelectedPaths
            .Select(path => Repositories.FirstOrDefault(repository =>
                string.Equals(repository.Path, path, StringComparison.OrdinalIgnoreCase)))
            .Where(repository => repository?.CanSelect == true)
            .Cast<RepositoryViewModel>()
            .ToArray();
        await PreparePublishAsync(selectedRepositories);
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
        var orderedRepositories = repositories.ToArray();
        ClearOperationOutcomes();
        activePublishProgress = null;
        var execution = await RunBusyAsync(async () =>
        {
            ClosePublishConfirmation();
            StatusText = "게시할 커밋과 SVN 대상을 확인하는 중입니다.";

            var preparation = await WorkspaceService.PrepareDcommitAllAsync(
                orderedRepositories.Select(repository => repository.Path).ToArray(),
                OperationToken,
                new Progress<PublishProgress>(OnPublishProgress));
            if (!preparation.Succeeded)
            {
                var safeMessage = SensitiveTextRedactor.Redact(preparation.Outcome.Message);
                var preparationResult = PublishBatchResult.FromPreparationFailure(
                    orderedRepositories
                        .Select(repository => new PublishRepositoryTarget(repository.Name, repository.Path))
                        .ToArray(),
                    preparation.Outcome.RepositoryPath,
                    safeMessage);
                repositoryState.ApplyPublishResult(preparationResult);
                ApplyOperationOutcomesToRows();
                await outputLogger.LogAsync(preparation.Outcome);
                StatusText = BuildPublishSummary(preparationResult);
                ClosePublishConfirmation();
                return;
            }

            publishConfirmation.Prepare(preparation.Snapshots);
            StatusText = "확인한 상태가 바뀌면 게시를 자동으로 중단합니다.";
        });

        if (execution.Kind != BusyExecutionKind.Completed)
        {
            var outcomes = BuildInterruptedOutcomes(
                orderedRepositories.Select(repository => repository.Path).ToArray(),
                RepositoryOperationKind.Dcommit,
                execution,
                activePublishProgress?.RepositoryIndex ?? 0);
            repositoryState.SetOutcomes(outcomes);
            ApplyOperationOutcomesToRows();
            StatusText = BuildOperationSummary("게시 준비 결과", outcomes);
        }
    }

    private async Task ConfirmPublishAsync()
    {
        var snapshots = publishConfirmation.TakeSnapshots();
        if (snapshots.Length == 0)
        {
            return;
        }

        PublishBatchResult? batchResult = null;
        activePublishProgress = null;
        var execution = await RunBusyAsync(async () =>
        {
            StatusText = "확인한 상태와 현재 저장소 상태를 비교하는 중입니다.";
            batchResult = await WorkspaceService.DcommitPreparedBatchAsync(
                snapshots,
                new Progress<PublishProgress>(OnPublishProgress),
                OperationToken);
            foreach (var outcome in batchResult.Outcomes)
            {
                await outputLogger.LogPublishOutcomeAsync(outcome);
            }
        });

        if (batchResult == null)
        {
            var outcomes = BuildInterruptedOutcomes(
                snapshots.Select(snapshot => snapshot.RepositoryPath).ToArray(),
                RepositoryOperationKind.Dcommit,
                execution,
                activePublishProgress?.RepositoryIndex ?? 0);
            repositoryState.SetOutcomes(outcomes);
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
            StatusText = BuildOperationSummary("게시 결과", outcomes);
            return;
        }

        repositoryState.ApplyPublishResult(batchResult);
        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        StatusText = BuildPublishSummary(batchResult);
    }

    private Task CancelPublishAsync()
    {
        ClosePublishConfirmation();
        return Task.CompletedTask;
    }

    private Task ToggleAllSelectionAsync()
    {
        if (AllSelectionState == true)
        {
            repositoryState.ClearSelection();
            foreach (var repository in Repositories)
            {
                repository.IsSelected = false;
            }
        }
        else
        {
            repositoryState.SelectAll(Repositories
                .Where(repository => repository.CanSelect)
                .Select(repository => repository.Path));
            foreach (var repository in Repositories)
            {
                repository.IsSelected = repository.CanSelect;
            }
        }

        NotifySelectionChanged();
        return Task.CompletedTask;
    }

    private Task ClearSelectionAsync()
    {
        repositoryState.ClearSelection();
        foreach (var repository in Repositories)
        {
            repository.IsSelected = false;
        }

        NotifySelectionChanged();
        return Task.CompletedTask;
    }

    private void OnRepositorySelectionChanged(string repositoryPath, bool isSelected)
    {
        var repository = Repositories.FirstOrDefault(item =>
            string.Equals(item.Path, repositoryPath, StringComparison.OrdinalIgnoreCase));
        if (repository == null)
        {
            return;
        }

        if (repositoryState.SetSelected(repositoryPath, isSelected, repository.CanSelect))
        {
            NotifySelectionChanged();
        }
    }

    private Task CancelOperationAsync()
    {
        operationCancellation?.Cancel();
        StatusText = "작업 취소를 요청했습니다.";
        return Task.CompletedTask;
    }

    private async Task ShowLogAsync()
    {
        if (!await package.ShowOutputPaneAsync(CancellationToken.None))
        {
            StatusText = "Git-SVN Shuttle 로그 창을 열지 못했습니다.";
        }
    }

    private void ClosePublishConfirmation() => publishConfirmation.Close();

    private void OnPublishConfirmationChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        OnPropertyChanged(eventArgs.PropertyName);
        if (eventArgs.PropertyName == nameof(IsPublishConfirmationOpen))
        {
            OnPropertyChanged(nameof(PublishConfirmationVisibility));
            ConfirmPublishCommand.RaiseCanExecuteChanged();
            CancelPublishCommand.RaiseCanExecuteChanged();
        }
    }

    private void OnPublishProgress(PublishProgress progress)
    {
        activePublishProgress = progress;
        var repositoryName = Repositories.FirstOrDefault(repository =>
            string.Equals(repository.Path, progress.RepositoryPath, StringComparison.OrdinalIgnoreCase))?.Name
            ?? progress.RepositoryName;
        var phase = progress.Phase switch
        {
            PublishProgressPhase.Preparing => "게시 준비",
            PublishProgressPhase.Revalidating => "상태 재검증",
            PublishProgressPhase.Publishing => "SVN 게시",
            _ => "게시 작업",
        };
        StatusText = phase + " 중 (" + (progress.RepositoryIndex + 1) + "/" + progress.RepositoryCount + "): " +
                     repositoryName;
    }

    private void ClearOperationOutcomes()
    {
        repositoryState.ClearOutcomes();
        ApplyOperationOutcomesToRows();
    }

    private void ApplyOperationOutcomesToRows()
    {
        foreach (var repository in Repositories)
        {
            repository.ApplyOperationOutcome(repositoryState.GetOutcome(repository.Path));
        }
    }

    private void ResetRepositorySession()
    {
        ClosePublishConfirmation();
        repositoryState.Reset();
        currentSolutionDirectory = string.Empty;
        foreach (var repository in Repositories)
        {
            repository.IsSelected = false;
            repository.IsExpanded = false;
            repository.ApplyOperationOutcome(null);
        }

        NotifyRepositorySummaryChanged();
    }

    private async Task<BusyExecutionResult> RunBusyAsync(Func<Task> action)
    {
        if (IsBusy || disposed)
        {
            return BusyExecutionResult.Skipped();
        }

        using (var cancellation = new CancellationTokenSource())
        try
        {
            operationCancellation = cancellation;
            IsBusy = true;
            CancelOperationCommand.RaiseCanExecuteChanged();
            await action();
            return BusyExecutionResult.Completed();
        }
        catch (OperationCanceledException)
        {
            StatusText = "작업을 취소했습니다.";
            return BusyExecutionResult.Cancelled();
        }
        catch (Exception exception)
        {
            var safeMessage = SensitiveTextRedactor.Redact(exception.Message);
            StatusText = "실행 실패: " + safeMessage;
            await outputLogger.LogFailureAsync(safeMessage);
            return BusyExecutionResult.Failed("실행 실패: " + safeMessage);
        }
        finally
        {
            operationCancellation = null;
            IsBusy = false;
            if (refreshSolutionAfterBusy && IsRuntimeReady && !disposed)
            {
                refreshSolutionAfterBusy = false;
                QueueSolutionRefresh();
            }
        }
    }

    private void NotifyRepositorySummaryChanged()
    {
        OnPropertyChanged(nameof(RepositoryCount));
        OnPropertyChanged(nameof(TotalPendingCommits));
        OnPropertyChanged(nameof(PublishAllLabel));
        OnPropertyChanged(nameof(EmptyStateVisibility));
        NotifySelectionChanged();
    }

    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedRepositoryCount));
        OnPropertyChanged(nameof(AllSelectionState));
        OnPropertyChanged(nameof(SelectionSummary));
        OnPropertyChanged(nameof(PublishSelectionTooltip));
        OnPropertyChanged(nameof(PublishSelectionAutomationName));
        OnPropertyChanged(nameof(TopPublishSelectionActionName));
        OnPropertyChanged(nameof(SummaryPublishSelectionActionName));
        OnPropertyChanged(nameof(SelectionBadgeVisibility));
        DcommitAllCommand.RaiseCanExecuteChanged();
        ToggleAllSelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
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
        ToggleAllSelectionCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
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
