using System;
using System.Collections.Generic;
using System.Linq;

namespace GitSvnShuttle.Vsix;

internal static class SvnManagementInput
{
    // An exact URL clone has an empty fetch path. A visible '.' preserves that
    // entry when the multiline editor removes blank lines on save.
    internal static string FormatTrunkPaths(IEnumerable<string> paths) =>
        string.Join(Environment.NewLine, paths.Select(path => path.Length == 0 ? "." : path));

    internal static string[] SplitTrackingPaths(string text) => text
        .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(path => path.Trim())
        .Where(path => path.Length > 0)
        .ToArray();

    internal static string ResolveBranchPath(string baseUrl, string path)
    {
        var value = path.Trim();
        if (value.Length == 0 || value.Any(char.IsControl) || value.Contains("\\"))
            throw new ArgumentException("기준 SVN 경로와 생성 위치를 올바르게 입력하세요.");

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            if (value.IndexOf("://", StringComparison.Ordinal) < 0 || !IsSvnUrl(absolute) || HasParentTraversal(value))
                throw new ArgumentException("기준 SVN 경로와 생성 위치에는 전체 SVN URL 또는 기준 URL 상대 경로를 입력하세요.");
            return absolute.AbsoluteUri;
        }

        if (baseUrl.IndexOf("://", StringComparison.Ordinal) < 0 ||
            !Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var root) || !IsSvnUrl(root) || HasParentTraversal(baseUrl))
            throw new ArgumentException("상대 경로를 사용하려면 화면의 SVN 기준 URL을 먼저 지정하세요.");
        if (value == ".") return root.AbsoluteUri;

        var segments = value.TrimEnd('/').Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (segments.Any(segment => segment.Length == 0 || segment == "." || segment == ".." ||
                                    segment.IndexOfAny(new[] { '/', '\\' }) >= 0 || segment.Any(char.IsControl)))
            throw new ArgumentException("SVN 상대 경로에는 빈 경로, '.', '..' 또는 인코딩된 구분자를 사용할 수 없습니다. 기준 URL 자체는 '.'으로 지정하세요.");

        var prefix = root.AbsoluteUri.EndsWith("/", StringComparison.Ordinal) ? root.AbsoluteUri : root.AbsoluteUri + "/";
        return prefix + string.Join("/", segments.Select(Uri.EscapeDataString));
    }

    private static bool IsSvnUrl(Uri uri) =>
        new[] { "http", "https", "svn", "svn+ssh", "file" }.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase) &&
        string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private static bool HasParentTraversal(string path) => path.Split('/')
        .Select(Uri.UnescapeDataString).Any(segment => segment == ".." || segment == ".");
}
