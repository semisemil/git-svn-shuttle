using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal sealed class PublishConfirmationViewModel : INotifyPropertyChanged
{
    private GitSvnPublishSnapshot[] snapshots = Array.Empty<GitSvnPublishSnapshot>();
    private bool isOpen;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<PublishCommitViewModel> PendingPublishItems { get; } =
        new ObservableCollection<PublishCommitViewModel>();

    public bool IsPublishConfirmationOpen => isOpen;
    public string PublishConfirmationSubtitle =>
        "커밋 " + PendingPublishItems.Count + "개를 아래 순서대로 게시합니다.";

    public string PublishTargetSummary
    {
        get
        {
            var targets = snapshots.SelectMany(snapshot => snapshot.SvnTargets)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return targets.Length == 0 ? "SVN 대상 확인 불가" : string.Join("  ·  ", targets);
        }
    }

    public void Prepare(IEnumerable<GitSvnPublishSnapshot> preparedSnapshots)
    {
        snapshots = preparedSnapshots.ToArray();
        PendingPublishItems.Clear();
        foreach (var snapshot in snapshots)
        {
            foreach (var commit in snapshot.PendingCommits)
            {
                PendingPublishItems.Add(new PublishCommitViewModel(
                    snapshot.RepositoryName, commit.Subject, commit.ShortHash));
            }
        }

        NotifySummaryChanged();
        SetOpen(PendingPublishItems.Count > 0);
    }

    public async Task<bool> IsCurrentAsync(
        Func<GitSvnPublishSnapshot, CancellationToken, Task<OperationResult>> validate,
        CancellationToken cancellationToken)
    {
        if (!isOpen)
        {
            return false;
        }

        var expected = snapshots;
        foreach (var snapshot in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await validate(snapshot, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Succeeded || !isOpen || !ReferenceEquals(expected, snapshots))
            {
                return false;
            }
        }

        return true;
    }

    public GitSvnPublishSnapshot[] TakeSnapshots()
    {
        var confirmed = snapshots;
        Close();
        return confirmed;
    }

    public void Close()
    {
        SetOpen(false);
        PendingPublishItems.Clear();
        snapshots = Array.Empty<GitSvnPublishSnapshot>();
        NotifySummaryChanged();
    }

    private void SetOpen(bool value)
    {
        if (isOpen == value)
        {
            return;
        }

        isOpen = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsPublishConfirmationOpen)));
    }

    private void NotifySummaryChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishConfirmationSubtitle)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PublishTargetSummary)));
    }
}
