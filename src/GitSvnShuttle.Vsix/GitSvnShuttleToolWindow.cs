using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace GitSvnShuttle.Vsix;

[Guid("E79DF599-BF62-4B15-9C8A-CFCB1EB7CBFD")]
public sealed class GitSvnShuttleToolWindow : ToolWindowPane
{
    public GitSvnShuttleToolWindow() : base(null)
    {
        Caption = "Git-SVN Shuttle";
        Content = new GitSvnShuttleControl();
    }
}
