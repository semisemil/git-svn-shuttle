using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

/// <summary>Runs the native SVN client for exact server-side URL/revision copies.</summary>
public interface ISvnCommandRunner
{
    Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
