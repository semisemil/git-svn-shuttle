using System;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal sealed class SvnBranchForm : INotifyPropertyChanged
{
    private string baseUrl = string.Empty;
    private string sourcePath = string.Empty;
    private string destinationPath = string.Empty;
    private string name = string.Empty;
    private string revision = "HEAD";
    private string? message;
    private string? localName;
    private bool switchAfterCreate = true;
    private bool sourceEdited;
    private bool destinationEdited;
    private string blockedDestination = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string[] SourceChoices { get; private set; } = Array.Empty<string>();
    public string[] DestinationChoices { get; private set; } = Array.Empty<string>();
    public string SourcePath { get => sourcePath; set { if (sourcePath == value) return; sourcePath = value; sourceEdited = true; Notify(); } }
    public string DestinationPath { get => destinationPath; set { if (destinationPath == value) return; destinationPath = value; destinationEdited = true; Notify(); } }
    public string Name { get => name; set { name = value; Notify(); } }
    public string Revision { get => revision; set { revision = value; Notify(); } }
    public string Message
    {
        get => message ?? "브랜치 생성: " + Name.Trim();
        set { message = string.IsNullOrWhiteSpace(value) || value == "브랜치 생성: " + Name.Trim() ? null : value; Notify(); }
    }
    public string LocalName
    {
        get => localName ?? SuggestedLocalName();
        set { localName = string.IsNullOrWhiteSpace(value) || value == SuggestedLocalName() ? null : value; Notify(); }
    }
    public bool SwitchAfterCreate { get => switchAfterCreate; set { switchAfterCreate = value; Notify(); } }
    public string RevisionSummary => Revision.Trim() == "HEAD" ? "서버 최신 버전" : "리비전 " + Revision.Trim();
    public string SwitchSummary => SwitchAfterCreate ? "생성 후 이 브랜치로 전환합니다." : "현재 브랜치와 작업 파일을 그대로 둡니다.";
    public string SourceUrl => SensitiveTextRedactor.Redact(ResolveForDisplay(SourcePath));
    public string DestinationUrl
    {
        get
        {
            var parent = ResolveForDisplay(DestinationPath);
            return parent.Length == 0 || Name.Trim().Length == 0
                ? string.Empty : SensitiveTextRedactor.Redact(parent.TrimEnd('/') + "/" + Uri.EscapeDataString(Name.Trim()));
        }
    }

    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SourcePath)) return "복사할 원본을 선택하거나 SVN 경로를 입력하세요.";
            if (SourceUrl.Length == 0) return "원본 경로를 확인하세요. 연결 설정의 기준 URL에 상대적인 경로나 전체 SVN URL을 사용할 수 있습니다.";
            if (string.IsNullOrWhiteSpace(Name)) return "새 브랜치 이름을 입력하세요. 예: fix-login";
            if (Name.Trim() == "." || Name.Trim() == ".." || Name.Any(c => c == '/' || c == '\\' || char.IsControl(c)))
                return "브랜치 이름에는 폴더 구분자나 줄바꿈을 사용할 수 없습니다. 폴더는 저장 위치에서 지정하세요.";
            if (string.IsNullOrWhiteSpace(DestinationPath)) return "저장 위치를 선택하거나 SVN 폴더 경로를 입력하세요. 예: branches/ProjectA";
            if (ResolveForDisplay(DestinationPath).Length == 0) return "저장 위치를 확인하세요. 연결 설정의 기준 URL에 상대적인 경로나 전체 SVN URL을 사용할 수 있습니다.";
            if (DestinationUrl == blockedDestination) return "이 주소는 다시 생성하지 마세요. 다른 브랜치를 만들려면 새 이름을 입력하세요.";
            if (Revision.Trim() != "HEAD" && (!long.TryParse(Revision.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 0))
                return "고급 설정에서 기준 버전을 HEAD 또는 0 이상의 리비전 번호로 입력하세요.";
            if (string.IsNullOrWhiteSpace(LocalName)) return "고급 설정에서 로컬 Git 브랜치 이름을 입력하세요.";
            return string.Empty;
        }
    }

    public bool IsReady => ValidationMessage.Length == 0;

    public void ApplyTracking(GitSvnTrackingPaths paths, string? currentSvnUrl)
    {
        // Keep edited paths on a refresh. Resolve them before changing the base URL.
        var previousSource = ResolveForDisplay(SourcePath);
        var previousDestination = ResolveForDisplay(DestinationPath);
        var changedBase = baseUrl.Length > 0 && baseUrl != paths.BaseUrl;
        baseUrl = paths.BaseUrl;
        var sources = paths.TrunkPaths.Select(path => path.Length == 0 ? "." : path.Replace("%", "%25")).Distinct().ToList();
        var currentSource = string.IsNullOrWhiteSpace(currentSvnUrl) ? null
            : sources.FirstOrDefault(path => SensitiveTextRedactor.Redact(ResolveForDisplay(path)) == currentSvnUrl!.TrimEnd('/')) ?? currentSvnUrl!;
        if (currentSource != null && !sources.Contains(currentSource)) sources.Add(currentSource);
        SourceChoices = sources.ToArray();
        DestinationChoices = paths.BranchesPaths.Select(BranchContainer).Where(path => path != null)
            .Cast<string>().Select(path => path.Replace("%", "%25")).Distinct().ToArray();
        if (changedBase)
        {
            sourcePath = previousSource;
            destinationPath = previousDestination;
        }
        if (!sourceEdited || string.IsNullOrWhiteSpace(SourcePath))
        {
            sourcePath = currentSource ?? (SourceChoices.Length == 1 ? SourceChoices[0] : string.Empty);
        }
        if (!destinationEdited || string.IsNullOrWhiteSpace(DestinationPath))
            destinationPath = DestinationChoices.Length == 1 ? DestinationChoices[0] : string.Empty;
        Notify();
    }

    public void ChangeBaseUrl(string value)
    {
        baseUrl = value;
        Notify();
    }

    public GitSvnBranchRequest CreateRequest()
    {
        if (!IsReady) throw new ArgumentException(ValidationMessage);
        return new GitSvnBranchRequest(
            SvnManagementInput.ResolveBranchPath(baseUrl, SourcePath), Revision.Trim(),
            SvnManagementInput.ResolveBranchPath(baseUrl, DestinationPath), Name.Trim(),
            Message.Trim(), LocalName.Trim(), SwitchAfterCreate);
    }

    public void BlockDuplicateCreation()
    {
        blockedDestination = DestinationUrl;
        Notify();
    }

    private static string? BranchContainer(string path)
    {
        var segments = path.TrimEnd('/').Split('/');
        if (segments.Take(segments.Length - 1).Any(segment => segment.Contains("*"))) return null;
        return segments[segments.Length - 1].Contains("*")
            ? segments.Length == 1 ? "." : string.Join("/", segments.Take(segments.Length - 1))
            : path;
    }

    private string ResolveForDisplay(string path)
    {
        try { return SvnManagementInput.ResolveBranchPath(baseUrl, path).TrimEnd('/'); }
        catch (ArgumentException) { return string.Empty; }
    }

    private string SuggestedLocalName()
    {
        var suggested = Regex.Replace(Name.Trim(), @"[^\p{L}\p{N}_.-]+", "-").Trim('.', '-');
        while (suggested.Contains("..")) suggested = suggested.Replace("..", "-");
        if (suggested.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) suggested += "-work";
        return suggested;
    }

    private void Notify()
    {
        // Name changes also update automatic names, messages, URLs, and validation.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
