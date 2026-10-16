using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public sealed class ProcessSvnCommandRunner : ISvnCommandRunner
{
    private readonly string? executablePath;

    public ProcessSvnCommandRunner(string? executablePath = null) => this.executablePath = executablePath;

    public Task<GitCommandResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var configured = executablePath ?? Environment.GetEnvironmentVariable("GIT_SVN_SHUTTLE_SVN");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            ProcessGitCommandRunner configuredRunner;
            try { configuredRunner = new ProcessGitCommandRunner(configured!, TimeSpan.FromMinutes(30)); }
            catch (FileNotFoundException exception)
            {
                throw new FileNotFoundException("SVN 명령줄 클라이언트를 찾지 못했습니다. GIT_SVN_SHUTTLE_SVN에 올바른 svn 실행 파일의 절대 경로를 지정하세요.", configured, exception);
            }
            return configuredRunner.RunAsync(workingDirectory, arguments, cancellationToken);
        }

        var filename = Path.DirectorySeparatorChar == '\\' ? "svn.exe" : "svn";
        var candidates = GitExecutableResolver.FindCandidateExecutablePaths(filename);
        if (candidates.Count == 0)
        {
            throw new FileNotFoundException("정확한 SVN URL/리비전으로 브랜치를 만들려면 SVN 명령줄 클라이언트가 필요합니다. PATH 또는 GIT_SVN_SHUTTLE_SVN에 svn 실행 파일을 지정하세요.");
        }

        return new ProcessGitCommandRunner(candidates[0], TimeSpan.FromMinutes(30)).RunAsync(workingDirectory, arguments, cancellationToken);
    }
}
