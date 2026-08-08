using System;
using System.Text.RegularExpressions;

namespace GitSvnShuttle.Core;

public static class SensitiveTextRedactor
{
    private static readonly Regex UrlUserInfo = new Regex(
        @"(?<scheme>\b[a-z][a-z0-9+.-]*://)(?<userinfo>[^/@\s]+)@",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SecretAssignment = new Regex(
        @"(?<prefix>(?:^|[?&;\s])(?:password|passwd|pwd|token|access_token|secret|authorization)\s*[=:]\s*)(?<value>[^\s&;]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex AuthorizationHeader = new Regex(
        @"^(?<prefix>\s*authorization\s*:\s*).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static readonly Regex SecretArgument = new Regex(
        @"(?<prefix>--(?:password|passwd|token|access-token|secret)(?:=|\s+))(?<value>\S+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var redacted = AuthorizationHeader.Replace(value, "${prefix}***");
        redacted = UrlUserInfo.Replace(redacted, "${scheme}***@");
        redacted = SecretAssignment.Replace(redacted, "${prefix}***");
        return SecretArgument.Replace(redacted, "${prefix}***");
    }
}
