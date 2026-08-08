using System;
using System.IO;

namespace GitSvnShuttle.Vsix;

internal sealed class GitRuntimePreference
{
    internal const string EnvironmentVariableName = "GIT_SVN_SHUTTLE_GIT";

    public string? GetSelectedPath() =>
        Environment.GetEnvironmentVariable(EnvironmentVariableName, EnvironmentVariableTarget.Process);

    public void Save(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
        {
            throw new ArgumentException("Git 실행 파일은 절대 경로여야 합니다.", nameof(executablePath));
        }

        var absolutePath = Path.GetFullPath(executablePath);
        Environment.SetEnvironmentVariable(EnvironmentVariableName, absolutePath, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(EnvironmentVariableName, absolutePath, EnvironmentVariableTarget.Process);
    }

    public void Reset()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariableName, null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(EnvironmentVariableName, null, EnvironmentVariableTarget.Process);
    }
}
