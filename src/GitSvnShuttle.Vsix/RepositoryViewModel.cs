using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal sealed class RepositoryViewModel : INotifyPropertyChanged
{
    private bool isExpanded;
    private bool isProblemExpanded;
    private bool isSelected;
    private RepositoryOperationOutcome? operationOutcome;
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
        RepositoryOperationOutcome? initialOperationOutcome)
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
        operationOutcome = initialOperationOutcome;
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
    public Visibility OperationOutcomeVisibility =>
        operationOutcome == null ? Visibility.Collapsed : Visibility.Visible;
    public string OperationOutcomeText => operationOutcome == null
        ? string.Empty
        : OperationPresentation.OperationName(operationOutcome.Operation) + " · " + OperationPresentation.OutcomeName(operationOutcome.Kind);
    public string OperationOutcomeMessage => operationOutcome?.Message ?? string.Empty;
    public string OperationOutcomeAutomationName => string.IsNullOrWhiteSpace(OperationOutcomeText)
        ? Name + " 저장소 작업 결과 없음"
        : Name + " 저장소 마지막 작업 결과 " + OperationOutcomeText + ". " + OperationOutcomeMessage;

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

    public void ApplyOperationOutcome(RepositoryOperationOutcome? outcome)
    {
        if (ReferenceEquals(operationOutcome, outcome))
        {
            return;
        }

        operationOutcome = outcome;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OperationOutcomeVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OperationOutcomeText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OperationOutcomeMessage)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OperationOutcomeAutomationName)));
    }
}
