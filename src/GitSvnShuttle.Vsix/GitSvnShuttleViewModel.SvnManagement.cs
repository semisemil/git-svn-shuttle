using System;
using System.Threading.Tasks;
using System.Windows;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal sealed partial class GitSvnShuttleViewModel
{
    private bool isSvnManagementOpen;
    private string managementRepositoryPath = string.Empty;
    private string trackingBaseUrl = string.Empty;
    private string trunkPathsText = "trunk";
    private string branchesPathsText = "branches";
    private string tagsPathsText = "tags";
    private string retainedTrackingPathsText = string.Empty;
    private string branchSourcePath = string.Empty;
    private string branchRevision = "HEAD";
    private string branchDestinationPath = string.Empty;
    private string svnBranchName = string.Empty;
    private string branchCommitMessage = string.Empty;
    private string localBranchName = string.Empty;
    private bool switchToCreatedBranch = true;
    private string actualDcommitTarget = "대상을 조회하세요.";
    private string managementStatus = string.Empty;

    public AsyncCommand NewSvnConnectionCommand { get; private set; } = null!;
    public AsyncCommand CloseSvnManagementCommand { get; private set; } = null!;
    public AsyncCommand LoadTrackingPathsCommand { get; private set; } = null!;
    public AsyncCommand SaveTrackingPathsCommand { get; private set; } = null!;
    public AsyncCommand CreateSvnBranchCommand { get; private set; } = null!;
    public AsyncCommand RefreshDcommitTargetCommand { get; private set; } = null!;

    public Visibility SvnManagementVisibility => isSvnManagementOpen ? Visibility.Visible : Visibility.Collapsed;
    public bool CanEditSvnManagement => !IsBusy;
    public string ManagementRepositoryPath
    {
        get => managementRepositoryPath;
        set
        {
            if (SetProperty(ref managementRepositoryPath, value))
            {
                ActualDcommitTarget = "대상을 조회하세요.";
            }
        }
    }
    public string TrackingBaseUrl { get => trackingBaseUrl; set => SetProperty(ref trackingBaseUrl, value); }
    public string TrunkPathsText { get => trunkPathsText; set => SetProperty(ref trunkPathsText, value); }
    public string BranchesPathsText { get => branchesPathsText; set => SetProperty(ref branchesPathsText, value); }
    public string TagsPathsText { get => tagsPathsText; set => SetProperty(ref tagsPathsText, value); }
    public string RetainedTrackingPathsText { get => retainedTrackingPathsText; private set => SetProperty(ref retainedTrackingPathsText, value); }
    public string BranchSourcePath { get => branchSourcePath; set => SetProperty(ref branchSourcePath, value); }
    public string BranchRevision { get => branchRevision; set => SetProperty(ref branchRevision, value); }
    public string BranchDestinationPath { get => branchDestinationPath; set => SetProperty(ref branchDestinationPath, value); }
    public string SvnBranchName { get => svnBranchName; set => SetProperty(ref svnBranchName, value); }
    public string BranchCommitMessage { get => branchCommitMessage; set => SetProperty(ref branchCommitMessage, value); }
    public string LocalBranchName { get => localBranchName; set => SetProperty(ref localBranchName, value); }
    public bool SwitchToCreatedBranch { get => switchToCreatedBranch; set => SetProperty(ref switchToCreatedBranch, value); }
    public string ActualDcommitTarget { get => actualDcommitTarget; private set => SetProperty(ref actualDcommitTarget, value); }
    public string ManagementStatus { get => managementStatus; private set => SetProperty(ref managementStatus, value); }

    private void InitializeSvnManagementCommands()
    {
        NewSvnConnectionCommand = new AsyncCommand(OpenNewSvnConnectionAsync, () => !IsBusy && IsRuntimeReady);
        CloseSvnManagementCommand = new AsyncCommand(() => { CloseSvnManagement(); return Task.CompletedTask; }, () => isSvnManagementOpen && !IsBusy);
        LoadTrackingPathsCommand = new AsyncCommand(LoadTrackingPathsAsync, () => !IsBusy && IsRuntimeReady);
        SaveTrackingPathsCommand = new AsyncCommand(SaveTrackingPathsAsync, () => !IsBusy && IsRuntimeReady);
        CreateSvnBranchCommand = new AsyncCommand(CreateSvnBranchAsync, () => !IsBusy && IsRuntimeReady);
        RefreshDcommitTargetCommand = new AsyncCommand(RefreshDcommitTargetAsync, () => !IsBusy && IsRuntimeReady);
    }

    private Task OpenNewSvnConnectionAsync()
    {
        ShowSvnManagementPanel(currentSolutionDirectory);
        TrackingBaseUrl = string.Empty;
        TrunkPathsText = "trunk";
        BranchesPathsText = "branches";
        TagsPathsText = "tags";
        ManagementStatus = "새 연결은 로컬 폴더와 SVN 기준 URL을 입력한 뒤 추적 경로를 적용하세요. 기존 Git 저장소도 연결할 수 있습니다.";
        return Task.CompletedTask;
    }

    private async Task OpenSvnManagementAsync(string repositoryPath)
    {
        if (IsBusy)
        {
            return;
        }

        ShowSvnManagementPanel(repositoryPath);
        TrackingBaseUrl = string.Empty;
        TrunkPathsText = string.Empty;
        BranchesPathsText = string.Empty;
        TagsPathsText = string.Empty;
        await LoadTrackingPathsAsync();
    }

    private void ShowSvnManagementPanel(string repositoryPath)
    {
        ClosePublishConfirmation();
        ManagementRepositoryPath = repositoryPath;
        BranchSourcePath = string.Empty;
        BranchRevision = "HEAD";
        BranchDestinationPath = string.Empty;
        SvnBranchName = string.Empty;
        BranchCommitMessage = string.Empty;
        LocalBranchName = string.Empty;
        SwitchToCreatedBranch = true;
        ActualDcommitTarget = "대상을 조회하세요.";
        ManagementStatus = string.Empty;
        RetainedTrackingPathsText = string.Empty;
        isSvnManagementOpen = true;
        OnPropertyChanged(nameof(SvnManagementVisibility));
        CloseSvnManagementCommand.RaiseCanExecuteChanged();
    }

    private void CloseSvnManagement()
    {
        isSvnManagementOpen = false;
        OnPropertyChanged(nameof(SvnManagementVisibility));
        CloseSvnManagementCommand?.RaiseCanExecuteChanged();
    }

    private async Task LoadTrackingPathsAsync()
    {
        var execution = await RunBusyAsync(async () =>
        {
            var paths = await WorkspaceService.GetTrackingPathsAsync(ManagementRepositoryPath.Trim(), OperationToken);
            TrackingBaseUrl = paths.BaseUrl;
            TrunkPathsText = SvnManagementInput.FormatTrunkPaths(paths.TrunkPaths);
            BranchesPathsText = string.Join(Environment.NewLine, paths.BranchesPaths);
            TagsPathsText = string.Join(Environment.NewLine, paths.TagsPaths);
            RetainedTrackingPathsText = string.Join(Environment.NewLine, paths.RetainedPaths);
            await LoadDcommitTargetAsync();
            ManagementStatus = "추적 경로를 조회했습니다. 이전 경로와 새 경로를 함께 유지하려면 각 경로를 한 줄씩 입력하세요.";
        });
        ShowSvnManagementFailure(execution);
    }

    private async Task SaveTrackingPathsAsync()
    {
        ClosePublishConfirmation();
        var execution = await RunBusyAsync(async () =>
        {
            var paths = new GitSvnTrackingPaths(
                TrackingBaseUrl.Trim(),
                SvnManagementInput.SplitTrackingPaths(TrunkPathsText),
                SvnManagementInput.SplitTrackingPaths(BranchesPathsText),
                SvnManagementInput.SplitTrackingPaths(TagsPathsText));
            var result = await WorkspaceService.ConfigureTrackingPathsAsync(ManagementRepositoryPath.Trim(), paths, OperationToken);
            await outputLogger.LogAsync(result);
            ManagementStatus = SensitiveTextRedactor.Redact(result.Message);
            if (result.Succeeded)
            {
                var applied = await WorkspaceService.GetTrackingPathsAsync(ManagementRepositoryPath.Trim(), OperationToken);
                RetainedTrackingPathsText = string.Join(Environment.NewLine, applied.RetainedPaths);
                await LoadDcommitTargetAsync();
            }
        });
        ShowSvnManagementFailure(execution);
        await RefreshAfterSvnManagementAsync(execution);
    }

    private async Task CreateSvnBranchAsync()
    {
        ClosePublishConfirmation();
        var execution = await RunBusyAsync(async () =>
        {
            var request = new GitSvnBranchRequest(
                SvnManagementInput.ResolveBranchPath(TrackingBaseUrl, BranchSourcePath),
                BranchRevision.Trim(),
                SvnManagementInput.ResolveBranchPath(TrackingBaseUrl, BranchDestinationPath),
                SvnBranchName.Trim(),
                BranchCommitMessage,
                LocalBranchName.Trim(),
                SwitchToCreatedBranch);
            var result = await WorkspaceService.CreateSvnBranchAsync(ManagementRepositoryPath.Trim(), request, OperationToken);
            await outputLogger.LogAsync(result);
            ManagementStatus = SensitiveTextRedactor.Redact(result.Message);
            await LoadDcommitTargetAsync();
        });
        ShowSvnManagementFailure(execution);
        await RefreshAfterSvnManagementAsync(execution);
    }

    private async Task RefreshDcommitTargetAsync()
    {
        var execution = await RunBusyAsync(LoadDcommitTargetAsync);
        ShowSvnManagementFailure(execution);
    }

    private async Task LoadDcommitTargetAsync()
    {
        var target = await WorkspaceService.ResolveDcommitTargetAsync(ManagementRepositoryPath.Trim(), OperationToken);
        ActualDcommitTarget = target.IsResolved
            ? SensitiveTextRedactor.Redact(target.Url) + Environment.NewLine +
              "SVN 이력: " + SensitiveTextRedactor.Redact(target.SvnUrl) + Environment.NewLine +
              "추적 ref: " + target.RemoteRef +
              (string.IsNullOrWhiteSpace(target.Revision) ? string.Empty : " · r" + target.Revision)
            : "대상 확인 불가: " + SensitiveTextRedactor.Redact(target.Problem);
    }

    private async Task RefreshAfterSvnManagementAsync(BusyExecutionResult execution)
    {
        if (execution.Kind != BusyExecutionKind.Skipped)
        {
            ClearOperationOutcomes();
            await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
        }
    }

    private void ShowSvnManagementFailure(BusyExecutionResult execution)
    {
        if (execution.Kind == BusyExecutionKind.Failed || execution.Kind == BusyExecutionKind.Cancelled)
        {
            ManagementStatus = execution.Message;
        }
    }

    private void RefreshSvnManagementCommands()
    {
        NewSvnConnectionCommand?.RaiseCanExecuteChanged();
        CloseSvnManagementCommand?.RaiseCanExecuteChanged();
        LoadTrackingPathsCommand?.RaiseCanExecuteChanged();
        SaveTrackingPathsCommand?.RaiseCanExecuteChanged();
        CreateSvnBranchCommand?.RaiseCanExecuteChanged();
        RefreshDcommitTargetCommand?.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(CanEditSvnManagement));
    }

}
