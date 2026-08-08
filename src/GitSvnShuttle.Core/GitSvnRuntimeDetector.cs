using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace GitSvnShuttle.Core;

public enum GitSvnRuntimeStatus
{
    Ready,
    GitNotFound,
    GitSvnNotAvailable,
    Invalid,
}

public sealed class GitSvnRuntimeDiagnostic
{
    public GitSvnRuntimeDiagnostic(
        GitSvnRuntimeStatus status,
        string executablePath,
        string gitVersion,
        string gitSvnVersion,
        string message)
    {
        Status = status;
        ExecutablePath = executablePath;
        GitVersion = gitVersion;
        GitSvnVersion = gitSvnVersion;
        Message = message;
    }

    public GitSvnRuntimeStatus Status { get; }
    public string ExecutablePath { get; }
    public string GitVersion { get; }
    public string GitSvnVersion { get; }
    public string Message { get; }
    public bool IsReady => Status == GitSvnRuntimeStatus.Ready;
}

public sealed class GitSvnRuntimeDetector
{
    private readonly Func<string, string> executableResolver;
    private readonly Func<string, IGitCommandRunner> runnerFactory;
    private readonly Func<IReadOnlyList<string>> candidateProvider;
    private readonly string probeDirectory;

    public GitSvnRuntimeDetector()
        : this(
            ProcessGitCommandRunner.ResolveExecutablePath,
            path => new ProcessGitCommandRunner(path),
            () => ProcessGitCommandRunner.FindCandidateExecutablePaths(),
            GetProbeDirectory())
    {
    }

    public GitSvnRuntimeDetector(
        Func<string, string> executableResolver,
        Func<string, IGitCommandRunner> runnerFactory,
        Func<IReadOnlyList<string>> candidateProvider,
        string probeDirectory)
    {
        this.executableResolver = executableResolver ?? throw new ArgumentNullException(nameof(executableResolver));
        this.runnerFactory = runnerFactory ?? throw new ArgumentNullException(nameof(runnerFactory));
        this.candidateProvider = candidateProvider ?? throw new ArgumentNullException(nameof(candidateProvider));
        this.probeDirectory = Directory.Exists(probeDirectory)
            ? probeDirectory
            : throw new DirectoryNotFoundException("Runtime probe directory does not exist: " + probeDirectory);
    }

    public async Task<GitSvnRuntimeDiagnostic> DiagnoseAsync(
        string? configuredPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return await AutoDetectAsync(cancellationToken).ConfigureAwait(false);
        }

        return await ProbeAsync(configuredPath!, cancellationToken).ConfigureAwait(false);
    }

    public async Task<GitSvnRuntimeDiagnostic> AutoDetectAsync(CancellationToken cancellationToken)
    {
        GitSvnRuntimeDiagnostic? gitOnly = null;
        foreach (var candidate in candidateProvider().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var diagnostic = await ProbeAsync(candidate, cancellationToken).ConfigureAwait(false);
            if (diagnostic.IsReady)
            {
                return diagnostic;
            }

            if (gitOnly == null && diagnostic.Status == GitSvnRuntimeStatus.GitSvnNotAvailable)
            {
                gitOnly = diagnostic;
            }
        }

        return gitOnly ?? new GitSvnRuntimeDiagnostic(
            GitSvnRuntimeStatus.GitNotFound,
            string.Empty,
            string.Empty,
            string.Empty,
            "Git 실행 파일을 찾지 못했습니다. Git-SVN이 포함된 git.exe를 선택하세요.");
    }

    public async Task<GitSvnRuntimeDiagnostic> ProbeAsync(
        string executablePath,
        CancellationToken cancellationToken)
    {
        string resolvedPath;
        try
        {
            resolvedPath = executableResolver(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException ||
            exception is FileNotFoundException ||
            exception is NotSupportedException ||
            exception is PathTooLongException)
        {
            return new GitSvnRuntimeDiagnostic(
                GitSvnRuntimeStatus.GitNotFound,
                executablePath ?? string.Empty,
                string.Empty,
                string.Empty,
                "선택한 Git 실행 파일을 찾을 수 없습니다.");
        }

        try
        {
            var runner = runnerFactory(resolvedPath);
            var gitResult = await runner.RunAsync(
                probeDirectory,
                new[] { "--version" },
                cancellationToken).ConfigureAwait(false);
            var gitVersion = FirstLine(gitResult.CombinedOutput);
            if (!gitResult.Succeeded)
            {
                return new GitSvnRuntimeDiagnostic(
                    GitSvnRuntimeStatus.Invalid,
                    resolvedPath,
                    gitVersion,
                    string.Empty,
                    "선택한 파일을 Git 실행 파일로 사용할 수 없습니다.");
            }

            var gitSvnResult = await runner.RunAsync(
                probeDirectory,
                new[] { "svn", "--version" },
                cancellationToken).ConfigureAwait(false);
            var gitSvnVersion = FirstLine(gitSvnResult.CombinedOutput);
            if (!gitSvnResult.Succeeded)
            {
                return new GitSvnRuntimeDiagnostic(
                    GitSvnRuntimeStatus.GitSvnNotAvailable,
                    resolvedPath,
                    gitVersion,
                    string.Empty,
                    "Git은 발견했지만 git svn을 사용할 수 없습니다. Git-SVN이 포함된 다른 git.exe를 선택하세요.");
            }

            return new GitSvnRuntimeDiagnostic(
                GitSvnRuntimeStatus.Ready,
                resolvedPath,
                gitVersion,
                gitSvnVersion,
                "Git-SVN 런타임을 사용할 수 있습니다.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new GitSvnRuntimeDiagnostic(
                GitSvnRuntimeStatus.Invalid,
                resolvedPath,
                string.Empty,
                string.Empty,
                "Git-SVN 런타임 확인에 실패했습니다: " + SensitiveTextRedactor.Redact(exception.Message));
        }
    }

    private static string FirstLine(string value) =>
        (value ?? string.Empty)
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim() ?? string.Empty;

    private static string GetProbeDirectory()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(userProfile) ? userProfile : AppDomain.CurrentDomain.BaseDirectory;
    }
}
