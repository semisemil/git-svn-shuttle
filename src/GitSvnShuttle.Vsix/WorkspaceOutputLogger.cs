using System;
using System.Threading;
using System.Threading.Tasks;
using GitSvnShuttle.Core;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using static GitSvnShuttle.Vsix.OperationPresentation;

namespace GitSvnShuttle.Vsix;

internal sealed class WorkspaceOutputLogger
{
    private readonly GitSvnShuttlePackage package;

    internal WorkspaceOutputLogger(GitSvnShuttlePackage package)
    {
        this.package = package;
    }

    internal async Task LogAsync(OperationResult result)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" +
            (result.Succeeded ? "OK" : "FAIL") + "] " + result.RepositoryPath + Environment.NewLine +
            SensitiveTextRedactor.Redact(result.Message) + Environment.NewLine + Environment.NewLine);
    }

    internal async Task LogOperationOutcomeAsync(RepositoryOperationOutcome outcome)
    {
        IVsOutputWindowPane? pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" +
            OutcomeName(outcome.Kind) + "] " + OperationName(outcome.Operation) + " · " +
            outcome.RepositoryPath + Environment.NewLine + outcome.Message + Environment.NewLine + Environment.NewLine);
    }

    internal async Task LogPublishOutcomeAsync(PublishRepositoryOutcome outcome)
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
            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + label + "] " +
            outcome.RepositoryPath + Environment.NewLine +
            SensitiveTextRedactor.Redact(outcome.Message) + Environment.NewLine + Environment.NewLine);
    }

    internal async Task LogFailureAsync(string safeMessage)
    {
        var pane = await package.GetOutputPaneAsync(CancellationToken.None);
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        pane?.OutputStringThreadSafe(
            "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] Git-SVN Shuttle 실행 실패: " +
            safeMessage + Environment.NewLine);
    }
}
