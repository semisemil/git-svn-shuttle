using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace GitSvnShuttle.UiPreview;

internal static class PreviewData
{
    public static PreviewViewModel Create() => new PreviewViewModel
    {
        Repositories = new ObservableCollection<PreviewRepository>
        {
            new PreviewRepository
            {
                Name = "ShuttleDemo",
                PathText = @"C:\work\ShuttleDemo",
                CompactPathText = @"C:\work\ShuttleDemo",
                SvnTargetText = "svn://dev-svn/main/trunk",
                SvnTargetTooltip = "svn://dev-svn/main/trunk",
                StatusText = "게시 가능",
                PendingCountText = "3개",
                IsSelected = true,
                IsExpanded = true,
                CommitDetailsVisibility = Visibility.Visible,
                PendingCommits = new ObservableCollection<PreviewCommit>
                {
                    new PreviewCommit("8b21d9a", "인증 토큰 갱신 순서 수정", "semi", "오늘 14:32"),
                    new PreviewCommit("37c02bf", "게시 전 상태 검증 추가", "semi", "오늘 15:08"),
                    new PreviewCommit("c913fc0", "로그 메시지 정리", "semi", "오늘 15:41"),
                },
            },
            new PreviewRepository
            {
                Name = "Shared.Common",
                PathText = @"실제 작업 경로: C:\work\externals\Shared.Common",
                CompactPathText = @"실제: C:\work\externals\Shared.Common  ·  로드됨: C:\work\ShuttleDemo\Common",
                LinkedProjectPathText = @"로드된 프로젝트: C:\work\ShuttleDemo\Common",
                SvnTargetText = "svn://dev-svn/shared/trunk",
                SvnTargetTooltip = "svn://dev-svn/shared/trunk",
                StatusText = "게시 가능",
                PendingCountText = "2개",
                IsSelected = true,
                ExternalLinkVisibility = Visibility.Visible,
                PendingCommits = new ObservableCollection<PreviewCommit>
                {
                    new PreviewCommit("1fe0ac3", "공통 직렬화 포맷 보정", "semi", "어제 18:10"),
                    new PreviewCommit("8e612dd", "nullable 경고 제거", "semi", "오늘 09:20"),
                },
            },
            new PreviewRepository
            {
                Name = "Build.Tools",
                PathText = @"D:\linked\Build.Tools",
                CompactPathText = @"D:\linked\Build.Tools",
                SvnTargetText = "svn://dev-svn/tools/trunk",
                SvnTargetTooltip = "svn://dev-svn/tools/trunk",
                StatusText = "게시 대기 없음",
                PendingCountText = "0개",
                CanSelect = false,
                ReadyStatusVisibility = Visibility.Visible,
                PendingCommitsVisibility = Visibility.Collapsed,
                NoPendingCommitsVisibility = Visibility.Visible,
            },
            new PreviewRepository
            {
                Name = "Legacy.Adapter",
                PathText = @"C:\work\Legacy.Adapter",
                CompactPathText = @"C:\work\Legacy.Adapter",
                SvnTargetText = "svn://dev-svn/legacy/trunk",
                SvnTargetTooltip = "svn://dev-svn/legacy/trunk",
                StatusText = "작업 필요",
                PendingCountText = "1개",
                CanSelect = false,
                DisplayProblem = "커밋되지 않은 변경이 있습니다",
                ProblemVisibility = Visibility.Visible,
                ReadyStatusVisibility = Visibility.Collapsed,
            },
        },
    };
}

internal sealed class PreviewViewModel
{
    private static readonly ICommand Command = new PreviewCommand();

    public ObservableCollection<PreviewRepository> Repositories { get; set; } = new ObservableCollection<PreviewRepository>();
    public int RepositoryCount => Repositories.Count;
    public int TotalPendingCommits => 6;
    public int SelectedRepositoryCount => 2;
    public bool? AllSelectionState => null;
    public string SelectionSummary => "2개 저장소 선택됨";
    public string StatusText => "4개 저장소 검사 완료 · 게시할 커밋 6개";
    public string TopPublishSelectionActionName => "선택한 저장소 2개를 SVN에 게시";
    public string SummaryPublishSelectionActionName => TopPublishSelectionActionName;
    public string PublishSelectionTooltip => TopPublishSelectionActionName;
    public string PublishSelectionAutomationName => TopPublishSelectionActionName;
    public Visibility SelectionBadgeVisibility => Visibility.Visible;
    public Visibility RuntimePanelVisibility => Visibility.Collapsed;
    public Visibility RuntimeReadyVisibility => Visibility.Visible;
    public Visibility RuntimeWarningVisibility => Visibility.Collapsed;
    public Visibility RuntimePathVisibility => Visibility.Visible;
    public Visibility WorkspaceVisibility => Visibility.Visible;
    public Visibility EmptyStateVisibility => Visibility.Collapsed;
    public Visibility BusyVisibility => Visibility.Collapsed;
    public Visibility CancelOperationVisibility => Visibility.Collapsed;
    public Visibility PublishConfirmationVisibility => Visibility.Collapsed;
    public string RuntimeTitle => "Git for Windows";
    public string RuntimeMessage => "Git-SVN 실행 환경 준비됨";
    public string RuntimePath => @"C:\Program Files\Git\cmd\git.exe";
    public string RuntimeVersion => "git version 2.51.0.windows.1";
    public ICommand RefreshCommand => Command;
    public ICommand ToggleRuntimeSettingsCommand => Command;
    public ICommand ChooseGitExecutableCommand => Command;
    public ICommand AutoDetectRuntimeCommand => Command;
    public ICommand RecheckRuntimeCommand => Command;
    public ICommand ResetRuntimeCommand => Command;
    public ICommand RebaseAllCommand => Command;
    public ICommand DcommitAllCommand => Command;
    public ICommand ToggleAllSelectionCommand => Command;
    public ICommand ClearSelectionCommand => Command;
    public ICommand ConfirmPublishCommand => Command;
    public ICommand CancelPublishCommand => Command;
    public ICommand CancelOperationCommand => Command;
}

internal sealed class PreviewRepository : INotifyPropertyChanged
{
    private static readonly ICommand Command = new PreviewCommand();
    private bool isSelected;
    private bool isExpanded;

    public string Name { get; set; } = string.Empty;
    public string PathText { get; set; } = string.Empty;
    public string CompactPathText { get; set; } = string.Empty;
    public string LinkedProjectPathText { get; set; } = string.Empty;
    public string SvnTargetText { get; set; } = string.Empty;
    public string SvnTargetTooltip { get; set; } = string.Empty;
    public string StatusText { get; set; } = string.Empty;
    public string PendingCountText { get; set; } = string.Empty;
    public bool CanSelect { get; set; } = true;
    public bool IsSelected
    {
        get => isSelected;
        set { isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }
    public bool IsExpanded
    {
        get => isExpanded;
        set
        {
            isExpanded = value;
            CommitDetailsVisibility = value ? Visibility.Visible : Visibility.Collapsed;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CommitDetailsVisibility)));
        }
    }
    public bool IsProblemExpanded { get; set; }
    public string DisplayProblem { get; set; } = string.Empty;
    public string SelectionAutomationName => Name + " 저장소 게시 선택";
    public string CommitDetailsAutomationName => Name + " 게시 대기 커밋 펼치기";
    public string RebaseAutomationName => Name + " 저장소에서 SVN 변경 받기";
    public string DcommitAutomationName => Name + " 저장소를 SVN에 게시";
    public string ProblemAutomationName => Name + " 작업 필요 상태 보기";
    public string PublishOutcomeAutomationName => Name + " 게시 결과 없음";
    public string PublishOutcomeText { get; set; } = string.Empty;
    public string PublishOutcomeMessage { get; set; } = string.Empty;
    public Visibility CommitDetailsVisibility { get; set; } = Visibility.Collapsed;
    public Visibility ExpandToggleVisibility => PendingCommits.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    public Visibility ProblemVisibility { get; set; } = Visibility.Collapsed;
    public Visibility ReadyStatusVisibility { get; set; } = Visibility.Visible;
    public Visibility PendingCommitsVisibility { get; set; } = Visibility.Visible;
    public Visibility NoPendingCommitsVisibility { get; set; } = Visibility.Collapsed;
    public Visibility BaselineVisibility => Visibility.Collapsed;
    public Visibility RebaseRecoveryVisibility => Visibility.Collapsed;
    public Visibility ConflictFilesVisibility => Visibility.Collapsed;
    public Visibility ExternalLinkVisibility { get; set; } = Visibility.Collapsed;
    public Visibility PublishOutcomeVisibility => Visibility.Collapsed;
    public Visibility ProblemDetailsVisibility => Visibility.Collapsed;
    public ObservableCollection<string> ConflictedFiles { get; } = new ObservableCollection<string>();
    public ObservableCollection<PreviewCommit> PendingCommits { get; set; } = new ObservableCollection<PreviewCommit>();
    public ICommand RebaseCommand => Command;
    public ICommand DcommitCommand => Command;
    public ICommand ContinueRebaseCommand => Command;
    public ICommand AbortRebaseCommand => Command;
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class PreviewCommit
{
    public PreviewCommit(string shortHash, string subject, string author, string date)
    {
        ShortHash = shortHash;
        Subject = subject;
        Author = author;
        Date = date;
    }

    public string ShortHash { get; }
    public string Subject { get; }
    public string Author { get; }
    public string Date { get; }
}

internal sealed class PreviewCommand : ICommand
{
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) { }
    public event EventHandler? CanExecuteChanged { add { } remove { } }
}
