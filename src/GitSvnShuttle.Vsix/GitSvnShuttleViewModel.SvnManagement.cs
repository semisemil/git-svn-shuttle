using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
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
    private string actualDcommitTarget = "아직 확인하지 않았습니다.";
    private string dcommitTargetDetails = string.Empty;
    private string dcommitTargetGuidance = string.Empty;
    private string managementProgressText = string.Empty;
    private int managementTabIndex;
    private int trackingLayoutIndex;
    private bool isTrackingAdvancedExpanded;
    private bool changingTrackingLayout;
    private SvnManagementFeedback? managementFeedback;
    private GitSvnTrackingPaths? loadedTrackingPaths;
    private SvnBranchForm branchForm = new SvnBranchForm();

    public AsyncCommand NewSvnConnectionCommand { get; private set; } = null!;
    public AsyncCommand CloseSvnManagementCommand { get; private set; } = null!;
    public AsyncCommand LoadTrackingPathsCommand { get; private set; } = null!;
    public AsyncCommand SaveTrackingPathsCommand { get; private set; } = null!;
    public AsyncCommand CreateSvnBranchCommand { get; private set; } = null!;
    public AsyncCommand RefreshDcommitTargetCommand { get; private set; } = null!;

    public Visibility SvnManagementVisibility => isSvnManagementOpen ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ManagementFeedbackVisibility => ManagementFeedback == null ? Visibility.Collapsed : Visibility.Visible;
    public bool CanEditSvnManagement => !IsBusy;
    public SvnBranchForm BranchForm => branchForm;
    public string ManagementRepositoryPath
    {
        get => managementRepositoryPath;
        set
        {
            if (!SetProperty(ref managementRepositoryPath, value)) return;
            loadedTrackingPaths = null;
            ResetBranchForm();
            ActualDcommitTarget = "폴더의 연결 설정을 조회하세요.";
            DcommitTargetGuidance = "다른 폴더를 입력했다면 ‘설정 다시 조회’를 눌러 해당 폴더의 SVN 연결을 읽으세요.";
            DcommitTargetDetails = string.Empty;
            ManagementFeedback = null;
            RefreshSvnManagementCommands();
        }
    }
    public string TrackingBaseUrl
    {
        get => trackingBaseUrl;
        set
        {
            if (!SetProperty(ref trackingBaseUrl, value)) return;
            BranchForm.ChangeBaseUrl(value);
            SaveTrackingPathsCommand?.RaiseCanExecuteChanged();
        }
    }
    public string TrunkPathsText { get => trunkPathsText; set { if (SetProperty(ref trunkPathsText, value)) UseCustomTrackingLayout(); } }
    public string BranchesPathsText { get => branchesPathsText; set { if (SetProperty(ref branchesPathsText, value)) UseCustomTrackingLayout(); } }
    public string TagsPathsText { get => tagsPathsText; set { if (SetProperty(ref tagsPathsText, value)) UseCustomTrackingLayout(); } }
    public string RetainedTrackingPathsText { get => retainedTrackingPathsText; private set => SetProperty(ref retainedTrackingPathsText, value); }
    public string ActualDcommitTarget { get => actualDcommitTarget; private set => SetProperty(ref actualDcommitTarget, value); }
    public string DcommitTargetDetails { get => dcommitTargetDetails; private set => SetProperty(ref dcommitTargetDetails, value); }
    public string DcommitTargetGuidance { get => dcommitTargetGuidance; private set => SetProperty(ref dcommitTargetGuidance, value); }
    public string ManagementProgressText { get => managementProgressText; private set => SetProperty(ref managementProgressText, value); }
    public SvnManagementFeedback? ManagementFeedback
    {
        get => managementFeedback;
        private set { SetProperty(ref managementFeedback, value); OnPropertyChanged(nameof(ManagementFeedbackVisibility)); }
    }
    public int ManagementTabIndex { get => managementTabIndex; set => SetProperty(ref managementTabIndex, value); }
    public bool IsTrackingAdvancedExpanded { get => isTrackingAdvancedExpanded; set => SetProperty(ref isTrackingAdvancedExpanded, value); }
    public int TrackingLayoutIndex
    {
        get => trackingLayoutIndex;
        set
        {
            if (!SetProperty(ref trackingLayoutIndex, value)) return;
            changingTrackingLayout = true;
            try
            {
                if (value == 0)
                {
                    TrunkPathsText = "trunk";
                    BranchesPathsText = "branches";
                    TagsPathsText = "tags";
                }
                else if (value == 1)
                {
                    TrunkPathsText = ".";
                    BranchesPathsText = string.Empty;
                    TagsPathsText = string.Empty;
                }
                else IsTrackingAdvancedExpanded = true;
            }
            finally { changingTrackingLayout = false; }
        }
    }

    private void UseCustomTrackingLayout()
    {
        if (changingTrackingLayout) return;
        trackingLayoutIndex = 2;
        OnPropertyChanged(nameof(TrackingLayoutIndex));
    }

    private void InitializeSvnManagementCommands()
    {
        branchForm.PropertyChanged += OnBranchFormChanged;
        NewSvnConnectionCommand = new AsyncCommand(OpenNewSvnConnectionAsync, () => !IsBusy && IsRuntimeReady);
        CloseSvnManagementCommand = new AsyncCommand(() => { CloseSvnManagement(); return Task.CompletedTask; }, () => isSvnManagementOpen);
        LoadTrackingPathsCommand = new AsyncCommand(LoadTrackingPathsAsync, () => !IsBusy && IsRuntimeReady && !string.IsNullOrWhiteSpace(ManagementRepositoryPath));
        SaveTrackingPathsCommand = new AsyncCommand(SaveTrackingPathsAsync, () => !IsBusy && IsRuntimeReady && !string.IsNullOrWhiteSpace(ManagementRepositoryPath) && !string.IsNullOrWhiteSpace(TrackingBaseUrl));
        CreateSvnBranchCommand = new AsyncCommand(CreateSvnBranchAsync, () => !IsBusy && IsRuntimeReady && !string.IsNullOrWhiteSpace(ManagementRepositoryPath) && BranchForm.IsReady);
        RefreshDcommitTargetCommand = new AsyncCommand(RefreshDcommitTargetAsync, () => !IsBusy && IsRuntimeReady && !string.IsNullOrWhiteSpace(ManagementRepositoryPath));
    }

    private Task OpenNewSvnConnectionAsync()
    {
        ShowSvnManagementPanel(currentSolutionDirectory);
        TrackingBaseUrl = string.Empty;
        TrunkPathsText = "trunk";
        BranchesPathsText = "branches";
        TagsPathsText = "tags";
        trackingLayoutIndex = 0;
        OnPropertyChanged(nameof(TrackingLayoutIndex));
        ManagementTabIndex = 1;
        ManagementFeedback = new SvnManagementFeedback("SVN 주소를 입력해 연결하세요.",
            "로컬 폴더와 SVN 주소를 확인하고 가져올 경로 구조를 선택하세요. 기존 연결이 있는 폴더라면 ‘설정 다시 조회’를 누르세요.");
        return Task.CompletedTask;
    }

    private async Task OpenSvnManagementAsync(string repositoryPath)
    {
        if (IsBusy) return;
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
        loadedTrackingPaths = null;
        ResetBranchForm();
        ActualDcommitTarget = "아직 확인하지 않았습니다.";
        DcommitTargetDetails = string.Empty;
        DcommitTargetGuidance = string.Empty;
        ManagementFeedback = null;
        RetainedTrackingPathsText = string.Empty;
        ManagementTabIndex = 0;
        IsTrackingAdvancedExpanded = false;
        isSvnManagementOpen = true;
        OnPropertyChanged(nameof(SvnManagementVisibility));
        CloseSvnManagementCommand.RaiseCanExecuteChanged();
    }

    private void ResetBranchForm()
    {
        branchForm.PropertyChanged -= OnBranchFormChanged;
        branchForm = new SvnBranchForm();
        branchForm.ChangeBaseUrl(TrackingBaseUrl);
        branchForm.PropertyChanged += OnBranchFormChanged;
        OnPropertyChanged(nameof(BranchForm));
    }

    private void OnBranchFormChanged(object sender, PropertyChangedEventArgs e) => CreateSvnBranchCommand?.RaiseCanExecuteChanged();

    private void CloseSvnManagement()
    {
        isSvnManagementOpen = false;
        OnPropertyChanged(nameof(SvnManagementVisibility));
        CloseSvnManagementCommand?.RaiseCanExecuteChanged();
    }

    private void BeginSvnManagementOperation(string progress)
    {
        ManagementFeedback = null;
        ManagementProgressText = progress;
    }

    private async Task LoadTrackingPathsAsync()
    {
        BeginSvnManagementOperation("로컬 폴더의 SVN 연결 설정을 읽는 중입니다.");
        var execution = await RunBusyAsync(async () =>
        {
            var paths = await WorkspaceService.GetTrackingPathsAsync(ManagementRepositoryPath.Trim(), OperationToken);
            ApplyTrackingPaths(paths);
            await LoadDcommitTargetAsync();
            if (paths.BaseUrl.Length == 0)
            {
                ManagementTabIndex = 1;
                ManagementFeedback = new SvnManagementFeedback("이 폴더에 SVN 연결 설정이 없습니다.",
                    "SVN 주소와 경로 구조를 입력한 뒤 ‘SVN 이력 가져오기’를 누르세요.");
            }
        });
        ShowSvnManagementFailure(execution);
    }

    private void ApplyTrackingPaths(GitSvnTrackingPaths paths)
    {
        loadedTrackingPaths = paths;
        BranchForm.ApplyTracking(paths, null);
        TrackingBaseUrl = paths.BaseUrl;
        TrunkPathsText = SvnManagementInput.FormatTrunkPaths(paths.TrunkPaths);
        BranchesPathsText = string.Join(Environment.NewLine, paths.BranchesPaths);
        TagsPathsText = string.Join(Environment.NewLine, paths.TagsPaths);
        RetainedTrackingPathsText = string.Join(Environment.NewLine, paths.RetainedPaths);
        trackingLayoutIndex = paths.TrunkPaths.SequenceEqual(new[] { string.Empty }) && paths.BranchesPaths.Count == 0 && paths.TagsPaths.Count == 0 ? 1
            : paths.TrunkPaths.SequenceEqual(new[] { "trunk" }) && paths.BranchesPaths.SequenceEqual(new[] { "branches/*" }) && paths.TagsPaths.SequenceEqual(new[] { "tags/*" }) ? 0 : 2;
        OnPropertyChanged(nameof(TrackingLayoutIndex));
    }

    private async Task SaveTrackingPathsAsync()
    {
        ClosePublishConfirmation();
        BeginSvnManagementOperation("연결 설정을 저장하고 SVN 이력을 가져오는 중입니다. 이력이 많으면 오래 걸릴 수 있습니다.");
        var execution = await RunBusyAsync(async () =>
        {
            var paths = new GitSvnTrackingPaths(TrackingBaseUrl.Trim(), SvnManagementInput.SplitTrackingPaths(TrunkPathsText),
                SvnManagementInput.SplitTrackingPaths(BranchesPathsText), SvnManagementInput.SplitTrackingPaths(TagsPathsText));
            var result = await WorkspaceService.ConfigureTrackingPathsAsync(ManagementRepositoryPath.Trim(), paths, OperationToken);
            await outputLogger.LogAsync(result);
            if (!result.Succeeded)
            {
                ManagementFeedback = SvnManagementFeedback.Failure(result.Message);
                return;
            }
            ApplyTrackingPaths(await WorkspaceService.GetTrackingPathsAsync(ManagementRepositoryPath.Trim(), OperationToken));
            await LoadDcommitTargetAsync();
            if (ManagementFeedback == null)
                ManagementFeedback = new SvnManagementFeedback("SVN 이력을 가져왔습니다.",
                    "새 작업을 시작하려면 ‘새 브랜치’에서 원본과 이름을 확인하세요. 기존 로컬 브랜치의 전송 대상은 유지됩니다.", result.Message);
            ManagementTabIndex = 0;
        });
        ShowSvnManagementFailure(execution);
        await RefreshAfterSvnManagementAsync(execution);
    }

    private async Task CreateSvnBranchAsync()
    {
        ClosePublishConfirmation();
        var request = BranchForm.CreateRequest();
        BeginSvnManagementOperation("SVN 서버에 브랜치를 만들고 이력을 가져와 로컬 Git 브랜치를 연결하는 중입니다.");
        var execution = await RunBusyAsync(async () =>
        {
            var result = await WorkspaceService.CreateSvnBranchAsync(ManagementRepositoryPath.Trim(), request, OperationToken);
            await outputLogger.LogAsync(result);
            ManagementFeedback = result.Succeeded
                ? new SvnManagementFeedback("브랜치를 만들고 로컬 Git 브랜치를 연결했습니다.",
                    request.SwitchToBranch ? "이 브랜치에서 작업하고 Visual Studio에서 커밋한 뒤 SVN에 게시하세요." : "작업을 시작할 때 로컬 Git 브랜치 ‘" + request.LocalBranchName + "’로 전환하세요.", result.Message)
                : SvnManagementFeedback.Failure(result.Message, creatingBranch: true);
            if (result.Succeeded || ManagementFeedback.ServerCopyMayHaveCompleted) BranchForm.BlockDuplicateCreation();
            if (result.Succeeded) await LoadDcommitTargetAsync();
        });
        ShowSvnManagementFailure(execution);
        await RefreshAfterSvnManagementAsync(execution);
    }

    private async Task RefreshDcommitTargetAsync()
    {
        BeginSvnManagementOperation("현재 Git 브랜치의 SVN 연결을 확인하는 중입니다.");
        var execution = await RunBusyAsync(LoadDcommitTargetAsync);
        ShowSvnManagementFailure(execution);
    }

    private async Task LoadDcommitTargetAsync()
    {
        ManagementProgressText = "현재 Git 브랜치의 SVN 연결을 확인하는 중입니다. 최대 20초 동안 기다립니다.";
        ActualDcommitTarget = "확인 중입니다.";
        DcommitTargetDetails = string.Empty;
        DcommitTargetGuidance = string.Empty;
        using (var query = CancellationTokenSource.CreateLinkedTokenSource(OperationToken))
        {
            query.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                var target = await WorkspaceService.ResolveDcommitTargetAsync(ManagementRepositoryPath.Trim(), query.Token);
                if (target.IsResolved)
                {
                    ActualDcommitTarget = SensitiveTextRedactor.Redact(target.Url);
                    DcommitTargetDetails = "원본 SVN 이력: " + SensitiveTextRedactor.Redact(target.SvnUrl) + Environment.NewLine +
                        "추적 ref: " + target.RemoteRef + Environment.NewLine + "리비전: " + target.Revision;
                    DcommitTargetGuidance = "현재 Git 브랜치의 커밋은 이 주소로 전송됩니다. 브랜치나 설정을 바꿨다면 다시 확인하세요.";
                }
                else
                {
                    var feedback = SvnManagementFeedback.ConnectionFailure(target.Problem ?? "SVN 연결을 확인하지 못했습니다.");
                    ActualDcommitTarget = feedback.Title;
                    DcommitTargetGuidance = feedback.NextAction;
                    DcommitTargetDetails = feedback.Details;
                    if (ManagementFeedback == null) ManagementFeedback = feedback;
                }
                if (loadedTrackingPaths != null) BranchForm.ApplyTracking(loadedTrackingPaths, target.SvnUrl);
            }
            catch (OperationCanceledException) when (!OperationToken.IsCancellationRequested)
            {
                var feedback = SvnManagementFeedback.ConnectionFailure("SVN 연결 조회 시간이 20초를 초과했습니다.");
                ActualDcommitTarget = feedback.Title;
                DcommitTargetGuidance = feedback.NextAction;
                DcommitTargetDetails = feedback.Details;
                if (ManagementFeedback == null) ManagementFeedback = feedback;
            }
            catch (OperationCanceledException)
            {
                ActualDcommitTarget = "연결 확인을 취소했습니다.";
                DcommitTargetGuidance = "‘연결 다시 확인’을 누르면 다시 조회합니다.";
                if (ManagementFeedback == null) throw;
            }
            catch (Exception exception)
            {
                var feedback = SvnManagementFeedback.ConnectionFailure(exception.Message);
                ActualDcommitTarget = feedback.Title;
                DcommitTargetGuidance = feedback.NextAction;
                DcommitTargetDetails = feedback.Details;
                if (ManagementFeedback == null) ManagementFeedback = feedback;
            }
        }
    }

    private async Task RefreshAfterSvnManagementAsync(BusyExecutionResult execution)
    {
        if (execution.Kind == BusyExecutionKind.Skipped) return;
        ManagementProgressText = "저장소 상태를 새로 확인하는 중입니다.";
        ClearOperationOutcomes();
        await RefreshRepositoriesAsync(RepositoryRefreshReason.Internal);
    }

    private void ShowSvnManagementFailure(BusyExecutionResult execution)
    {
        if (execution.Kind == BusyExecutionKind.Failed)
            ManagementFeedback = SvnManagementFeedback.Failure(execution.Message);
        else if (execution.Kind == BusyExecutionKind.Cancelled)
            ManagementFeedback = new SvnManagementFeedback("작업을 취소했습니다.",
                "입력값은 유지됩니다. 조회를 취소했다면 ‘설정 다시 조회’ 또는 ‘연결 다시 확인’을 누르세요.");
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
