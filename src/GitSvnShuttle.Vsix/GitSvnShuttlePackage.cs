using System;
using System.Collections.Generic;
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
[InstalledProductRegistration("Git-SVN Shuttle", "Git-SVN rebase and dcommit for Visual Studio", "0.3.4")]
[ProvideMenuResource("GitSvnShuttle.CTMENU", 1)]
[ProvideToolWindow(typeof(GitSvnShuttleToolWindow), Style = VsDockStyle.Tabbed, Window = ToolWindowGuids80.SolutionExplorer)]
[Guid(PackageGuidString)]
public sealed class GitSvnShuttlePackage : AsyncPackage, IVsSolutionEvents
{
    public const string PackageGuidString = "4879B52A-7FA8-4D1A-8EAE-E96D2940C02E";
    private static readonly Guid CommandSet = new Guid("46F6ACDB-AD32-49D8-B38A-EDDFB249B83C");
    private const int ShowCommandId = 0x0100;
    private IVsSolution? advisedSolution;
    private uint solutionEventsCookie;

    internal static GitSvnShuttlePackage? Instance { get; private set; }
    internal event EventHandler<SolutionContextChangedEventArgs>? SolutionContextChanged;

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        Instance = this;
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var commandService = await GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
        commandService?.AddCommand(new MenuCommand(ShowToolWindow, new CommandID(CommandSet, ShowCommandId)));
        advisedSolution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        advisedSolution?.AdviseSolutionEvents(this, out solutionEventsCookie);
    }

    protected override void Dispose(bool disposing)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (disposing && advisedSolution != null && solutionEventsCookie != 0)
        {
            advisedSolution.UnadviseSolutionEvents(solutionEventsCookie);
            solutionEventsCookie = 0;
            advisedSolution = null;
        }

        base.Dispose(disposing);
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

    internal async Task<IReadOnlyList<string>> GetLoadedProjectPathsAsync(CancellationToken cancellationToken)
    {
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        var solution = await GetServiceAsync(typeof(SVsSolution)) as IVsSolution;
        if (solution == null)
        {
            return Array.Empty<string>();
        }

        var projectType = Guid.Empty;
        ErrorHandler.ThrowOnFailure(solution.GetProjectEnum(
            (uint)__VSENUMPROJFLAGS.EPF_LOADEDINSOLUTION,
            ref projectType,
            out var enumerator));
        if (enumerator == null)
        {
            return Array.Empty<string>();
        }

        var paths = new List<string>();
        var hierarchies = new IVsHierarchy[1];
        while (enumerator.Next(1, hierarchies, out var fetched) == VSConstants.S_OK && fetched == 1)
        {
            if (hierarchies[0] is IVsProject project &&
                ErrorHandler.Succeeded(project.GetMkDocument(VSConstants.VSITEMID_ROOT, out var projectPath)) &&
                !string.IsNullOrWhiteSpace(projectPath))
            {
                paths.Add(projectPath);
            }
        }

        return paths;
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

    int IVsSolutionEvents.OnAfterOpenProject(IVsHierarchy hierarchy, int added) => VSConstants.S_OK;

    int IVsSolutionEvents.OnQueryCloseProject(IVsHierarchy hierarchy, int removing, ref int cancel) =>
        VSConstants.S_OK;

    int IVsSolutionEvents.OnBeforeCloseProject(IVsHierarchy hierarchy, int removed) => VSConstants.S_OK;

    int IVsSolutionEvents.OnAfterLoadProject(IVsHierarchy stubHierarchy, IVsHierarchy realHierarchy) =>
        VSConstants.S_OK;

    int IVsSolutionEvents.OnQueryUnloadProject(IVsHierarchy realHierarchy, ref int cancel) => VSConstants.S_OK;

    int IVsSolutionEvents.OnBeforeUnloadProject(IVsHierarchy realHierarchy, IVsHierarchy stubHierarchy) =>
        VSConstants.S_OK;

    int IVsSolutionEvents.OnAfterOpenSolution(object reserved, int newSolution)
    {
        SolutionContextChanged?.Invoke(this, new SolutionContextChangedEventArgs(isOpen: true));
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnQueryCloseSolution(object reserved, ref int cancel) => VSConstants.S_OK;

    int IVsSolutionEvents.OnBeforeCloseSolution(object reserved)
    {
        SolutionContextChanged?.Invoke(this, new SolutionContextChangedEventArgs(isOpen: false));
        return VSConstants.S_OK;
    }

    int IVsSolutionEvents.OnAfterCloseSolution(object reserved) => VSConstants.S_OK;
}

internal sealed class SolutionContextChangedEventArgs : EventArgs
{
    public SolutionContextChangedEventArgs(bool isOpen) => IsOpen = isOpen;

    public bool IsOpen { get; }
}
