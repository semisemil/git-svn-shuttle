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
    private readonly List<GitSvnPublishSnapshot> preparedPublishSnapshots = new List<GitSvnPublishSnapshot>();
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
    private bool isPublishConfirmationOpen;
    private bool disposed;
    private bool refreshSolutionAfterBusy;
    private CancellationTokenSource? operationCancellation;

    public GitSvnShuttleViewModel(GitSvnShuttlePackage package, Func<string?> selectGitExecutable)
    {
        this.package = package ?? throw new ArgumentNullException(nameof(package));
        this.selectGitExecutable = selectGitExecutable ?? throw new ArgumentNullException(nameof(selectGitExecutable));
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
    public AsyncCommand ToggleAllSelectionCommand { get; }
    public AsyncCommand ClearSelectionCommand { get; }
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
        package.SolutionContextChanged -= OnSolutionContextChanged;
        changeMonitor.Dispose();
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
        ClearPublishOutcomes();
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
        ClearPublishOutcomes();
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
        ClearPublishOutcomes();
        OperationResult? result = null;
        await RunBusyAsync(async () =>
        {
            StatusText = "SVN 변경 가져오기 실행 중: " + repositoryPath;
            result = await WorkspaceService.RebaseAsync(repositoryPath, OperationToken);
            await LogAsync(result);
        });

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        if (result != null)
        {
            StatusText = result.Succeeded ? "SVN 변경 가져오기 완료" : SensitiveTextRedactor.Redact(result.Message);
        }
    }

    private async Task RunRebaseAllAsync()
    {
        ClearPublishOutcomes();
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

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        var failure = results.FirstOrDefault(result => !result.Succeeded);
        StatusText = failure == null
            ? "모든 저장소의 SVN 변경을 가져왔습니다."
            : SensitiveTextRedactor.Redact(failure.Message) + " 이후 저장소는 실행하지 않았습니다.";
    }

    private async Task ContinueRebaseOneAsync(string repositoryPath)
    {
        ClearPublishOutcomes();
        OperationResult? result = null;
        await RunBusyAsync(async () =>
        {
            StatusText = "rebase를 계속하는 중입니다: " + repositoryPath;
            result = await WorkspaceService.ContinueRebaseAsync(repositoryPath, OperationToken);
            await LogAsync(result);
        });

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        if (result != null)
        {
            StatusText = SensitiveTextRedactor.Redact(result.Message);
        }
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

        ClearPublishOutcomes();
        OperationResult? result = null;
        await RunBusyAsync(async () =>
        {
            StatusText = "rebase를 중단하는 중입니다: " + repositoryPath;
            result = await WorkspaceService.AbortRebaseAsync(repositoryPath, confirmed: true, OperationToken);
            await LogAsync(result);
        });

        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        if (result != null)
        {
            StatusText = SensitiveTextRedactor.Redact(result.Message);
        }
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
        ClearPublishOutcomes();
        await RunBusyAsync(async () =>
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
                repositoryState.SetOutcomes(preparationResult.Outcomes);
                ApplyPublishOutcomesToRows();
                await LogAsync(preparation.Outcome);
                StatusText = BuildPublishSummary(preparationResult);
                ClosePublishConfirmation();
                return;
            }

            preparedPublishSnapshots.AddRange(preparation.Snapshots);

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

        PublishBatchResult? batchResult = null;
        await RunBusyAsync(async () =>
        {
            StatusText = "확인한 상태와 현재 저장소 상태를 비교하는 중입니다.";
            batchResult = await WorkspaceService.DcommitPreparedBatchAsync(
                snapshots,
                new Progress<PublishProgress>(OnPublishProgress),
                OperationToken);
            foreach (var outcome in batchResult.Outcomes)
            {
                await LogPublishOutcomeAsync(outcome);
            }
        });

        if (batchResult == null)
        {
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

    private void OnPublishProgress(PublishProgress progress)
    {
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

    private static string BuildPublishSummary(PublishBatchResult result)
    {
        var succeeded = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Succeeded);
        var failed = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Failed);
        var cancelled = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.Cancelled);
        var notRun = result.Outcomes.Count(outcome => outcome.Kind == PublishOutcomeKind.NotRun);
        var parts = new List<string>();
        if (succeeded > 0) parts.Add("성공 " + succeeded + "개");
        if (failed > 0) parts.Add("실패 " + failed + "개");
        if (cancelled > 0) parts.Add("취소됨 " + cancelled + "개");
        if (notRun > 0) parts.Add("실행 안 함 " + notRun + "개");

        var problem = result.Outcomes.FirstOrDefault(outcome =>
            outcome.Kind == PublishOutcomeKind.Failed || outcome.Kind == PublishOutcomeKind.Cancelled);
        var summary = "게시 결과 · " + string.Join(" · ", parts);
        return problem == null ? summary : summary + " · " + SensitiveTextRedactor.Redact(problem.Message);
    }

    private void ClearPublishOutcomes()
    {
        repositoryState.ClearOutcomes();
        ApplyPublishOutcomesToRows();
    }

    private void ApplyPublishOutcomesToRows()
    {
        foreach (var repository in Repositories)
        {
            repository.ApplyPublishOutcome(repositoryState.GetOutcome(repository.Path));
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
            repository.ApplyPublishOutcome(null);
        }

        NotifyRepositorySummaryChanged();
    }

    private async Task LogAsync(OperationResult result)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + (result.Succeeded ? "OK" : "FAIL") + "] " + result.RepositoryPath + Environment.NewLine +
            SensitiveTextRedactor.Redact(result.Message) + Environment.NewLine + Environment.NewLine);
    }

    private async Task LogPublishOutcomeAsync(PublishRepositoryOutcome outcome)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var label = outcome.Kind switch
        {
            PublishOutcomeKind.Succeeded => "SUCCESS",
            PublishOutcomeKind.Failed => "FAIL",
            PublishOutcomeKind.Cancelled => "CANCELLED",
            PublishOutcomeKind.NotRun => "NOT RUN",
            _ => "RESULT",
        };
        pane?.OutputStringThreadSafe(
            "[" + label + "] " + outcome.RepositoryPath + Environment.NewLine +
            SensitiveTextRedactor.Redact(outcome.Message) + Environment.NewLine + Environment.NewLine);
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

internal sealed class RepositoryViewModel : INotifyPropertyChanged
{
    private bool isExpanded;
    private bool isProblemExpanded;
    private bool isSelected;
    private PublishRepositoryOutcome? publishOutcome;
    private readonly Action<bool> selectionChanged;
    private readonly Action<bool> expansionChanged;

    public RepositoryViewModel(
        GitSvnRepository repository,
        Func<Task> rebase,
        Func<Task> dcommit,
        Func<Task> continueRebase,
        Func<Task> abortRebase,
        Action<bool> selectionChanged,
        Action<bool> expansionChanged,
        bool initiallySelected,
        bool initiallyExpanded,
        PublishRepositoryOutcome? initialPublishOutcome)
    {
        this.selectionChanged = selectionChanged ?? throw new ArgumentNullException(nameof(selectionChanged));
        this.expansionChanged = expansionChanged ?? throw new ArgumentNullException(nameof(expansionChanged));
        Name = repository.Name;
        Path = repository.Path;
        PendingCommits = repository.PendingCommits;
        SvnBaseline = repository.SvnBaseline;
        IsReady = repository.IsReady;
        Problem = repository.Problem ?? string.Empty;
        IsRebaseInProgress = repository.IsRebaseInProgress;
        ConflictedFiles = repository.ConflictedFiles;
        CanContinueRebase = repository.CanContinueRebase;
        IsExternalLink = repository.IsExternalLink;
        LinkedProjectPath = repository.LinkedProjectPath ?? string.Empty;
        SvnTargets = repository.SvnTargets;
        isSelected = initiallySelected && repository.IsReady && repository.PendingCommits.Count > 0;
        isExpanded = initiallyExpanded && repository.PendingCommits.Count > 0;
        publishOutcome = initialPublishOutcome;
        RebaseCommand = new AsyncCommand(rebase, () => repository.IsReady);
        DcommitCommand = new AsyncCommand(dcommit, () => repository.IsReady && repository.PendingCommits.Count > 0);
        ContinueRebaseCommand = new AsyncCommand(
            continueRebase,
            () => repository.IsRebaseInProgress && repository.CanContinueRebase);
        AbortRebaseCommand = new AsyncCommand(abortRebase, () => repository.IsRebaseInProgress);
    }

    public string Name { get; }
    public string Path { get; }
    public IReadOnlyList<GitSvnCommit> PendingCommits { get; }
    public GitSvnCommit? SvnBaseline { get; }
    public bool IsReady { get; }
    public bool CanSelect => IsReady && PendingCommits.Count > 0;
    public string Problem { get; }
    public string DisplayProblem => Problem;
    public bool IsRebaseInProgress { get; }
    public IReadOnlyList<string> ConflictedFiles { get; }
    public bool CanContinueRebase { get; }
    public bool IsExternalLink { get; }
    public string LinkedProjectPath { get; }
    public IReadOnlyList<string> SvnTargets { get; }
    public string PathText => IsExternalLink ? "실제 작업 경로: " + Path : Path;
    public string LinkedProjectPathText => "로드된 프로젝트: " + LinkedProjectPath;
    public string CompactPathText => IsExternalLink
        ? "실제: " + Path + "  ·  로드됨: " + LinkedProjectPath
        : Path;
    public string SvnTargetText => SvnTargets.Count switch
    {
        0 => "대상 확인 불가",
        1 => SvnTargets[0],
        _ => SvnTargets[0] + " 외 " + (SvnTargets.Count - 1) + "개",
    };
    public string SvnTargetTooltip => SvnTargets.Count == 0
        ? "SVN 대상 확인 불가"
        : string.Join(Environment.NewLine, SvnTargets);
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            var acceptedValue = CanSelect && value;
            if (isSelected == acceptedValue)
            {
                return;
            }

            isSelected = acceptedValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            selectionChanged(isSelected);
        }
    }

    public string SelectionAutomationName => CanSelect
        ? Name + " 저장소 게시 선택"
        : Name + " 저장소 게시 선택 불가";

    public bool CanExpand => PendingCommits.Count > 0;

    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            var acceptedValue = CanExpand && value;
            if (isExpanded == acceptedValue)
            {
                return;
            }

            isExpanded = acceptedValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommitDetailsVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommitDetailsAutomationName)));
            expansionChanged(isExpanded);
        }
    }

    public Visibility CommitDetailsVisibility => IsExpanded ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandToggleVisibility => CanExpand ? Visibility.Visible : Visibility.Hidden;
    public string CommitDetailsAutomationName =>
        Name + " 게시 대기 커밋 " + (IsExpanded ? "접기" : "펼치기");
    public Visibility ProblemVisibility =>
        string.IsNullOrWhiteSpace(DisplayProblem) ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReadyStatusVisibility =>
        string.IsNullOrWhiteSpace(DisplayProblem) ? Visibility.Visible : Visibility.Collapsed;
    public Visibility PendingCommitsVisibility => PendingCommits.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility NoPendingCommitsVisibility => PendingCommits.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility BaselineVisibility => SvnBaseline == null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility RebaseRecoveryVisibility => IsRebaseInProgress ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ConflictFilesVisibility => ConflictedFiles.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ExternalLinkVisibility => IsExternalLink ? Visibility.Visible : Visibility.Collapsed;
    public string ProblemLabel => IsRebaseInProgress
        ? ConflictedFiles.Count > 0 ? "rebase 충돌" : "rebase 진행 중"
        : Problem == "커밋되지 않은 변경이 있습니다."
            ? "커밋되지 않은 변경"
            : "작업 필요";
    public string StatusText => !string.IsNullOrWhiteSpace(DisplayProblem)
        ? ProblemLabel
        : PendingCommits.Count == 0 ? "게시 대기 없음" : "게시 가능";
    public string PendingCountText => PendingCommits.Count + "개";
    public string RebaseAutomationName => Name + " 저장소에서 SVN 변경 받기";
    public string DcommitAutomationName => Name + " 저장소의 로컬 커밋을 SVN에 게시";
    public Visibility PublishOutcomeVisibility =>
        publishOutcome == null ? Visibility.Collapsed : Visibility.Visible;
    public string PublishOutcomeText => publishOutcome?.Kind switch
    {
        PublishOutcomeKind.Succeeded => "성공",
        PublishOutcomeKind.Failed => "실패",
        PublishOutcomeKind.Cancelled => "취소됨",
        PublishOutcomeKind.NotRun => "실행 안 함",
        _ => string.Empty,
    };
    public string PublishOutcomeMessage => publishOutcome?.Message ?? string.Empty;
    public string PublishOutcomeAutomationName => string.IsNullOrWhiteSpace(PublishOutcomeText)
        ? Name + " 저장소 게시 결과 없음"
        : Name + " 저장소 마지막 게시 결과 " + PublishOutcomeText + ". " + PublishOutcomeMessage;

    public bool IsProblemExpanded
    {
        get => isProblemExpanded;
        set
        {
            var acceptedValue = !string.IsNullOrWhiteSpace(DisplayProblem) && value;
            if (isProblemExpanded == acceptedValue)
            {
                return;
            }

            isProblemExpanded = acceptedValue;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsProblemExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProblemDetailsVisibility)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProblemAutomationName)));
        }
    }

    public Visibility ProblemDetailsVisibility =>
        IsProblemExpanded ? Visibility.Visible : Visibility.Collapsed;
    public string ProblemAutomationName =>
        Name + " 저장소 작업 필요 상태 " + (IsProblemExpanded ? "접기" : "보기");
    public AsyncCommand RebaseCommand { get; }
    public AsyncCommand DcommitCommand { get; }
    public AsyncCommand ContinueRebaseCommand { get; }
    public AsyncCommand AbortRebaseCommand { get; }

    public void ApplyPublishOutcome(PublishRepositoryOutcome? outcome)
    {
        if (ReferenceEquals(publishOutcome, outcome))
        {
            return;
        }

        publishOutcome = outcome;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishOutcomeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishOutcomeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishOutcomeMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishOutcomeAutomationName)));
    }
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
