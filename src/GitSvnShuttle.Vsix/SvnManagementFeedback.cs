using System;
using GitSvnShuttle.Core;

namespace GitSvnShuttle.Vsix;

internal sealed class SvnManagementFeedback
{
    public SvnManagementFeedback(string title, string nextAction, string details = "", bool serverCopyMayHaveCompleted = false)
    {
        Title = title;
        NextAction = nextAction;
        Details = SensitiveTextRedactor.Redact(details);
        ServerCopyMayHaveCompleted = serverCopyMayHaveCompleted;
    }

    public string Title { get; }
    public string NextAction { get; }
    public string Details { get; }
    public bool HasDetails => Details.Length > 0;
    public bool CanRetryQuery { get; private set; }
    public bool ServerCopyMayHaveCompleted { get; }

    public static SvnManagementFeedback ConnectionFailure(string message)
    {
        var feedback = Failure(message);
        feedback.CanRetryQuery = true;
        return feedback;
    }

    public static SvnManagementFeedback Failure(string message, bool creatingBranch = false)
    {
        if (message.Contains("SVN 브랜치는 이미 생성되었습니다"))
            return new SvnManagementFeedback("서버 브랜치는 생성됐지만 로컬 연결을 마치지 못했습니다.",
                "같은 브랜치를 다시 만들지 마세요. 결과 상세의 생성 주소를 확인하고, 연결 설정에서 해당 경로의 SVN 이력을 가져온 뒤 로컬 Git 브랜치를 연결하세요.", message, serverCopyMayHaveCompleted: true);
        if (creatingBranch && (message.Contains("생성 여부를 확인") || message.Contains("완료 여부를 확인")))
            return new SvnManagementFeedback("서버에서 브랜치가 생성됐는지 확인해야 합니다.",
                "SVN 서버에서 결과 상세의 생성 주소를 먼저 확인하세요. 이미 있으면 다시 생성하지 말고 SVN 이력 가져오기와 로컬 연결을 계속하세요.", message, serverCopyMayHaveCompleted: true);
        if (message.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 || message.Contains("time limit") || message.Contains("시간") && message.Contains("초과"))
            return new SvnManagementFeedback("응답을 기다리는 시간이 초과됐습니다.",
                "SVN 서버와 네트워크 연결을 확인하세요. 인증이 필요한 경우 사용 중인 Git-SVN 실행 환경에서 인증을 완료한 뒤 다시 조회하세요.", message);
        if (message.IndexOf("authentication", StringComparison.OrdinalIgnoreCase) >= 0 || message.Contains("인증") || message.Contains("E170001"))
            return new SvnManagementFeedback("SVN 인증 정보를 확인해야 합니다.",
                "사용 중인 Git-SVN 실행 환경과 SVN 명령줄 클라이언트에서 해당 서버에 인증한 뒤 다시 실행하세요. 서버 주소와 저장소 접근 권한도 확인하세요.", message);
        if (message.Contains("추적 이력") || message.Contains("upstream SVN"))
            return new SvnManagementFeedback("현재 Git 브랜치의 SVN 연결을 확인하지 못했습니다.",
                "SVN 이력에 연결된 로컬 Git 브랜치로 전환한 뒤 ‘연결 다시 확인’을 누르세요. 처음 연결한 폴더라면 ‘새 브랜치’에서 원본을 선택해 작업 브랜치를 만들 수 있습니다.", message);
        if (message.Contains("커밋되지 않은") || message.Contains("uncommitted") || message.Contains("working tree"))
            return new SvnManagementFeedback("현재 작업 상태에서는 브랜치를 전환할 수 없습니다.",
                "변경을 커밋하거나 스태시한 뒤 다시 실행하세요. 현재 파일을 그대로 두려면 고급 설정의 ‘생성 후 이 브랜치로 전환’을 끄세요.", message);
        if (message.Contains("같은 이름의 로컬 브랜치") || message.Contains("로컬 브랜치 이름이 올바르지"))
            return new SvnManagementFeedback("로컬 Git 브랜치 이름을 바꿔야 합니다.",
                "고급 설정에서 사용할 수 있는 다른 로컬 Git 브랜치 이름을 입력하세요. 기존 브랜치의 커밋은 유지됩니다.", message);
        return new SvnManagementFeedback("작업을 완료하지 못했습니다.",
            "결과 상세에서 경로와 실패 원인을 확인하세요. 입력값을 고친 뒤 다시 실행하고, 명령 출력을 더 확인하려면 ‘로그 보기’를 누르세요.", message);
    }
}
