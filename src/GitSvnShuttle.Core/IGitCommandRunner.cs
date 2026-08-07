using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public interface IGitCommandRunner
{
    Task<GitCommandResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}
