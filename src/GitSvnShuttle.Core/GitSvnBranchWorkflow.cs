using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using static GitSvnShuttle.Core.GitOperationResults;

namespace GitSvnShuttle.Core;

internal sealed class GitSvnBranchWorkflow
{
    private readonly IGitCommandRunner runner;
    private readonly ISvnCommandRunner svnRunner;
    private readonly GitRepositoryReader reader;

    internal GitSvnBranchWorkflow(IGitCommandRunner runner, ISvnCommandRunner svnRunner, GitRepositoryReader reader)
    { this.runner = runner; this.svnRunner = svnRunner; this.reader = reader; }

    internal async Task<OperationResult> CreateAsync(string repositoryPath, GitSvnBranchRequest request, CancellationToken cancellationToken)
    {
        if (request == null) throw new ArgumentNullException(nameof(request));
        var validation = ValidateRequest(request);
        if (validation != null) return BranchFailed(repositoryPath, validation);

        var configuration = await runner.RunAsync(repositoryPath,
            new[] { "config", "--get-regexp", "^(svn\\.|svn-remote\\.)" }, cancellationToken).ConfigureAwait(false);
        if (!configuration.Succeeded) return BranchFailed(repositoryPath, "SVN 추적 설정을 읽지 못했습니다.");
        var entries = GitSvnTargetResolver.ParseConfiguration(configuration.StandardOutput);
        if (entries.Any(entry => System.Text.RegularExpressions.Regex.IsMatch(entry.Key,
            @"^(svn\.|svn-remote\..*\.)(noMetadata|rewriteRoot|rewriteUUID|useSvmProps|useSvnsyncProps)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)))
            return BranchFailed(repositoryPath, "noMetadata, rewriteRoot/UUID, SVM/SVNSync 설정의 브랜치 생성은 지원하지 않습니다.");
        var source = ResolveUrl(request.SourcePath, entries);
        var parent = ResolveUrl(request.DestinationPath, entries);
        if (source == null || parent == null)
            return BranchFailed(repositoryPath, "기준/생성 위치는 전체 SVN URL로 지정하세요. 상대 경로는 추적 URL의 공통 기준을 확정할 수 있을 때만 지원합니다.");
        var destination = GitSvnTargetResolver.AppendUrlPath(parent, request.BranchName);

        var globalTarget = GitSvnTargetResolver.LastValue(entries, "svn.commiturl");
        if (!string.IsNullOrWhiteSpace(globalTarget) && !GitSvnTargetResolver.UrlEquals(globalTarget!, destination))
            return BranchFailed(repositoryPath, "svn.commiturl이 다른 경로를 강제로 지정합니다. 새 브랜치로 dcommit할 수 없으므로 생성하지 않았습니다.");

        var mappings = GitSvnTargetResolver.FindMappings(entries, destination);
        if (mappings.Count > 1) return BranchFailed(repositoryPath, "생성 위치가 여러 SVN 추적 경로와 겹칩니다. 추적 경로를 먼저 정리하세요.");
        var mapping = mappings.SingleOrDefault();
        if (mapping != null)
        {
            var configuredTarget = GitSvnTargetResolver.ResolveConfiguration(destination, configuration.StandardOutput);
            if (!configuredTarget.IsResolved || !GitSvnTargetResolver.UrlEquals(configuredTarget.Url!, SensitiveTextRedactor.Redact(destination)))
                return BranchFailed(repositoryPath, "생성 위치의 commiturl/pushurl 설정이 다른 경로를 가리킵니다. 전송 설정을 먼저 확인하세요.");
        }

        var localRef = "refs/heads/" + request.LocalBranchName;
        var validLocal = await runner.RunAsync(repositoryPath,
            new[] { "check-ref-format", "--branch", request.LocalBranchName }, cancellationToken).ConfigureAwait(false);
        if (!validLocal.Succeeded) return BranchFailed(repositoryPath, "로컬 브랜치 이름이 올바르지 않습니다.");
        var existingLocal = await runner.RunAsync(repositoryPath,
            new[] { "show-ref", "--verify", "--quiet", localRef }, cancellationToken).ConfigureAwait(false);
        if (existingLocal.ExitCode != 0 && existingLocal.ExitCode != 1)
            return BranchFailed(repositoryPath, "로컬 브랜치 존재 여부를 확인하지 못했습니다.");

        if (request.SwitchToBranch)
        {
            var preflight = await reader.PreflightAsync(repositoryPath, requirePendingCommits: false, cancellationToken).ConfigureAwait(false);
            if (!preflight.Succeeded) return preflight;
        }

        if (existingLocal.Succeeded)
        {
            // A name collision is not permission to move existing local commits.
            // An existing branch may only be switched to when its real SVN
            // history already belongs to the requested destination.
            var history = await runner.RunAsync(repositoryPath,
                new[] { "log", "--first-parent", "--grep=git-svn-id:", "--format=%B", "-1", localRef, "--" }, cancellationToken).ConfigureAwait(false);
            if (!history.Succeeded || !ContainsSvnUrl(history.StandardOutput, destination))
                return BranchFailed(repositoryPath, "같은 이름의 로컬 브랜치가 다른 SVN 이력을 가집니다. 기존 커밋 보존을 위해 새 로컬 이름을 지정하세요.");
        }

        var id = Guid.NewGuid().ToString("N");
        var remoteName = mapping?.RemoteName ?? "shuttle-branch-" + id;
        var remoteRef = mapping?.RemoteRef ?? "refs/remotes/shuttle/branch-" + id;
        var copyStarted = false;
        var copied = false;
        try
        {
            copyStarted = true;
            // git svn branch takes its copy revision from a Git commit's
            // metadata and ignores -r. Native svn copy preserves the exact
            // requested source path, peg revision, and operative revision.
            var copy = await svnRunner.RunAsync(repositoryPath, new[]
            {
                "copy", "--non-interactive", "--parents", "--revision", request.Revision,
                "--message", request.Message, "--", source + "@" + request.Revision, destination,
            }, cancellationToken).ConfigureAwait(false);
            if (!copy.Succeeded) return BranchFailed(repositoryPath, "SVN 브랜치를 만들지 못했습니다: " + copy.CombinedOutput);
            copied = true;

            if (mapping == null)
            {
                // A new exact mapping has its own revision map and never
                // retargets an existing git-svn ref or rewrites local history.
                var urlConfig = await runner.RunAsync(repositoryPath,
                    new[] { "config", "--local", "svn-remote." + remoteName + ".url", destination }, cancellationToken).ConfigureAwait(false);
                if (!urlConfig.Succeeded) return CreatedButFailed(repositoryPath, destination, "추적 URL 저장 실패: " + urlConfig.CombinedOutput);
                var fetchConfig = await runner.RunAsync(repositoryPath,
                    new[] { "config", "--local", "svn-remote." + remoteName + ".fetch", ":" + remoteRef }, cancellationToken).ConfigureAwait(false);
                if (!fetchConfig.Succeeded) return CreatedButFailed(repositoryPath, destination, "추적 경로 저장 실패: " + fetchConfig.CombinedOutput);
                var bindingConfig = await runner.RunAsync(repositoryPath,
                    new[] { "config", "--local", "svn-remote." + remoteName + ".shuttleBranch", "true" }, cancellationToken).ConfigureAwait(false);
                if (!bindingConfig.Succeeded) return CreatedButFailed(repositoryPath, destination, "브랜치 연결 설정 저장 실패: " + bindingConfig.CombinedOutput);
            }

            var fetch = await runner.RunAsync(repositoryPath, new[] { "svn", "fetch", "--no-checkout", remoteName }, cancellationToken).ConfigureAwait(false);
            if (!fetch.Succeeded) return CreatedButFailed(repositoryPath, destination, "fetch 실패: " + fetch.CombinedOutput);
            var imported = await runner.RunAsync(repositoryPath,
                new[] { "log", "--first-parent", "--grep=git-svn-id:", "--format=%B", "-1", remoteRef, "--" }, cancellationToken).ConfigureAwait(false);
            if (!imported.Succeeded || !ContainsSvnUrl(imported.StandardOutput, destination))
                return CreatedButFailed(repositoryPath, destination, "가져온 이력이 생성한 SVN 브랜치와 일치하지 않아 로컬 브랜치를 변경하지 않았습니다.");

            if (!existingLocal.Succeeded)
            {
                var create = await runner.RunAsync(repositoryPath,
                    new[] { "branch", "--no-track", request.LocalBranchName, remoteRef }, cancellationToken).ConfigureAwait(false);
                if (!create.Succeeded) return CreatedButFailed(repositoryPath, destination, "로컬 브랜치 생성 실패: " + create.CombinedOutput);
            }

            if (request.SwitchToBranch)
            {
                var checkout = await runner.RunAsync(repositoryPath, new[] { "checkout", request.LocalBranchName }, cancellationToken).ConfigureAwait(false);
                if (!checkout.Succeeded) return CreatedButFailed(repositoryPath, destination, "로컬 브랜치 전환 실패: " + checkout.CombinedOutput);
                var actualTarget = await new GitSvnTargetResolver(runner).ResolveAsync(repositoryPath, cancellationToken).ConfigureAwait(false);
                if (!actualTarget.IsResolved || !GitSvnTargetResolver.UrlEquals(actualTarget.Url!, SensitiveTextRedactor.Redact(destination)))
                    return CreatedButFailed(repositoryPath, destination, "로컬 브랜치는 연결되었지만 dcommit 대상을 검증하지 못했습니다. 게시 전에 전송 대상을 확인하세요.");
            }

            return new OperationResult(repositoryPath, true, "SVN 브랜치 생성 및 fetch 완료: " + SensitiveTextRedactor.Redact(destination) +
                "\n로컬 브랜치 " + request.LocalBranchName + (request.SwitchToBranch ? "로 전환했습니다." : "를 준비했습니다. 현재 작업 상태는 보존했습니다."));
        }
        catch (OperationCanceledException) when (copyStarted)
        {
            return copied
                ? CreatedButFailed(repositoryPath, destination, "후속 작업이 취소되었습니다. 서버 브랜치는 유지됩니다.")
                : BranchFailed(repositoryPath, "SVN copy가 취소되었습니다. 서버에서 " + destination + " 생성 여부를 확인한 뒤 재시도하세요.");
        }
        catch (Exception exception) when (exception is IOException || exception is InvalidOperationException ||
                                          exception is System.ComponentModel.Win32Exception || exception is TimeoutException)
        {
            if (copied) return CreatedButFailed(repositoryPath, destination, exception.Message);
            return BranchFailed(repositoryPath, exception is TimeoutException
                ? "SVN copy의 완료 여부를 확인하지 못했습니다. 서버에서 " + destination + " 생성 여부를 확인한 뒤 재시도하세요.\n" + exception.Message
                : exception.Message);
        }
    }

    private static string? ValidateRequest(GitSvnBranchRequest request)
    {
        if (new[] { request.SourcePath, request.Revision, request.DestinationPath, request.BranchName, request.Message, request.LocalBranchName }
            .Any(string.IsNullOrWhiteSpace)) return "기준 경로, 리비전, 생성 위치, SVN/로컬 브랜치 이름과 메시지를 지정하세요.";
        if (!request.Revision.Equals("HEAD", StringComparison.Ordinal) &&
            (!long.TryParse(request.Revision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0))
            return "SVN 리비전은 HEAD 또는 0 이상의 숫자여야 합니다.";
        if (request.BranchName == "." || request.BranchName == ".." || request.BranchName.IndexOfAny(new[] { '/', '\\', '\r', '\n', '\0' }) >= 0)
            return "SVN 브랜치 이름은 단일 경로 이름이어야 합니다. 중간 폴더는 생성 위치에 지정하세요.";
        if (request.SourcePath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || request.DestinationPath.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0)
            return "SVN 경로에 줄바꿈을 사용할 수 없습니다.";
        return null;
    }

    private static string? ResolveUrl(string value, IReadOnlyList<KeyValuePair<string, string>> entries)
    {
        if (GitSvnTargetResolver.IsSvnUrl(value)) return value.TrimEnd('/');
        if (Uri.TryCreate(value, UriKind.Absolute, out _) || value.Split('/').Any(part => part == ".." || part == ".")) return null;
        var roots = entries.Where(entry => entry.Key.StartsWith("svn-remote.", StringComparison.OrdinalIgnoreCase) &&
            entry.Key.EndsWith(".url", StringComparison.OrdinalIgnoreCase)).Select(entry => entry.Value.TrimEnd('/')).Distinct(StringComparer.Ordinal).ToArray();
        if (roots.Length == 0 || roots.Any(root => !GitSvnTargetResolver.IsSvnUrl(root))) return null;
        var baseUri = new Uri(roots[0]);
        if (roots.Any(root => !new Uri(root).GetLeftPart(UriPartial.Authority).Equals(baseUri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))) return null;
        var common = roots[0];
        while (roots.Any(root => !GitSvnTargetResolver.UrlEquals(root, common) && !root.StartsWith(common + "/", StringComparison.Ordinal)))
        {
            var slash = common.LastIndexOf('/');
            if (slash < baseUri.GetLeftPart(UriPartial.Authority).Length) return null;
            common = common.Substring(0, slash);
        }
        return GitSvnTargetResolver.AppendUrlPath(common, value);
    }

    private static bool ContainsSvnUrl(string body, string url) => body.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(line => Regex.Match(line, @"^git-svn-id: (.+)@\d+ [^\s]+\s*$"))
        .Any(match => match.Success && GitSvnTargetResolver.UrlEquals(Uri.UnescapeDataString(match.Groups[1].Value), Uri.UnescapeDataString(url)));

    private static OperationResult BranchFailed(string repositoryPath, string message) => Failed(repositoryPath, SensitiveTextRedactor.Redact(message));

    private static OperationResult CreatedButFailed(string repositoryPath, string destination, string problem) => BranchFailed(repositoryPath,
        "SVN 브랜치는 이미 생성되었습니다: " + SensitiveTextRedactor.Redact(destination) + "\n" + SensitiveTextRedactor.Redact(problem) +
        "\n서버 브랜치를 다시 생성하지 말고 fetch/로컬 연결을 계속하세요.");
}
