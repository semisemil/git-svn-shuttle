using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace GitSvnShuttle.Vsix;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[InstalledProductRegistration("Git-SVN Shuttle", "Git-SVN rebase and dcommit for Visual Studio", "0.3.0")]
[ProvideMenuResource("GitSvnShuttle.CTMENU", 1)]
[ProvideToolWindow(typeof(GitSvnShuttleToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
[Guid(PackageGuidString)]
public sealed class GitSvnShuttlePackage : AsyncPackage
{
    public const string PackageGuidString = "4879B52A-7FA8-4D1A-8EAE-E96D2940C02E";
    private static readonly Guid CommandSet = new Guid("46F6ACDB-AD32-49D8-B38A-EDDFB249B83C");
    private const int ShowCommandId = 0x0100;

    internal static GitSvnShuttlePackage? Instance { get; private set; }

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        commandService?.AddCommand(new MenuCommand(ShowToolWindow, new CommandID(CommandSet, ShowCommandId)));
    }

    private void ShowToolWindow(object sender, EventArgs e)
    {
        JoinableTaskFactory.RunAsync(async delegate
        {
            var window = await ShowToolWindowAsync(typeof(GitSvnShuttleToolWindow), 0, true, DisposalToken);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("Git-SVN Shuttle tool window could not be created.");
            }
        }).FileAndForget("GitSvnShuttle/ShowToolWindow");
    }

    internal async Task<string?> GetSolutionDirectoryAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        if (solution == null)
        {
            return null;
        }

        ErrorHandler.ThrowOnFailure(solution.GetSolutionInfo(out var directory, out _, out _));
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    internal async Task<IVsOutputWindowPane?> GetOutputPaneAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var outputWindow = await GetServiceAsync(typeof(SVsOutputWindow)) as IVsOutputWindow;
        if (outputWindow == null)
        {
            return null;
        }

        var paneGuid = new Guid("8052D829-5FC5-4FC8-8789-6484BD746BD4");
        outputWindow.CreatePane(ref paneGuid, "Git-SVN Shuttle", 1, 1);
        outputWindow.GetPane(ref paneGuid, out var pane);
        return pane;
    }
}
