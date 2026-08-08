# Git-SVN Shuttle 개인정보 안내

Git-SVN Shuttle은 게시자에게 원격 분석, 사용 통계, 저장소 내용, 자격 증명 또는 개인 데이터를 전송하지 않습니다.

확장은 사용자가 선택한 로컬 `git.exe`를 실행하고, 해당 Git-SVN 런타임에 구성된 SVN 서버와 통신합니다. 따라서 네트워크 통신과 인증은 사용자의 Git-SVN 및 SVN 설정에 따라 해당 SVN 서버와 직접 이루어집니다.

선택한 Git 실행 파일 경로는 현재 Windows 사용자 환경 변수 `GIT_SVN_SHUTTLE_GIT`에 저장됩니다. Visual Studio Output 로그에는 명령 결과가 표시될 수 있지만 자격 증명 형태의 값과 URL 사용자 정보는 제거됩니다.

Git-SVN Shuttle은 자체 서버나 제3자 분석 서비스에 연결하지 않습니다.

---

# Git-SVN Shuttle privacy notice

Git-SVN Shuttle does not send telemetry, usage analytics, repository contents, credentials, or personal data to the publisher.

The extension launches the local `git.exe` selected by the user. That Git-SVN runtime communicates directly with the SVN endpoints configured by the user. Network access and authentication therefore occur between the user's Git-SVN/SVN runtime and those configured SVN servers.

The selected Git executable path is stored in the current Windows user's `GIT_SVN_SHUTTLE_GIT` environment variable. Command results can appear in the Visual Studio Output window, but credential-shaped values and URL user information are redacted.

Git-SVN Shuttle does not connect to a publisher-operated server or third-party analytics service.
