# 유지보수 리팩토링

## 목표와 작업 기준

전체 제품 코드와 테스트·실행 도구의 연결을 확인하고, 책임이 집중된 구현을 분리한다. 작업 시작 시의 미커밋 변경을 포함한 동작, 공개 API, Git 명령 순서, 게시 전 재검증, 순차 실행과 실패 시 중단을 유지한다. UI 문구와 바인딩도 유지한다. 버그가 없음을 보장하는 대신 변경 경로의 회귀 검증을 남긴다.

기준 검증: Core 테스트 46개 통과. 작업 시작 전에 수정된 18개 파일은 이번 리팩토링의 원본으로 취급한다.

## 현재 구조

책임 소유자: GitSvnWorkspaceService가 탐색, Git 상태 조회, rebase, 게시 준비·재검증·실행을 모두 담당한다. ProcessGitCommandRunner는 실행 파일 탐색도 담당한다. GitSvnShuttleViewModel은 화면 조정 외에 행 모델, 게시 확인 데이터, 결과 분류·표현을 포함한다.

호출 경로: 화면 → 저장소 서비스 → 명령 실행기. 런타임 탐지 → 명령 실행기의 정적 경로 탐색. 행 표시와 로그에 동일한 결과 문구가 중복된다.

의존 방향: UI가 Core를 사용한다. 서비스 내부의 모든 기능은 같은 필드와 비공개 메서드에 접근한다.

상태 소유자: 저장소 선택·펼침·결과는 RepositorySessionState, 게시 확인 목록·열림 상태는 화면 모델, 프로세스 수명은 실행기가 소유한다.

## 의도한 구조

책임 소유자: 공개 저장소 서비스는 진입점으로 유지하고 탐색, 저장소 조회·사전 검사, rebase, 게시 검증, 게시 흐름을 각각 별도 컴포넌트로 위임한다. 실행 파일 탐색은 별도 resolver로 이동한다. 화면의 게시 확인 데이터, 작업 결과 계산·문구, 행 모델을 분리한다.

호출 경로: 화면 → 기존 공개 서비스 → 기능별 컴포넌트 → 저장소 조회 또는 명령 실행기. 게시 경로는 공통 검증기를 거친다. 런타임 탐지와 명령 실행기는 같은 실행 파일 resolver를 사용한다.

의존 방향: UI → Core를 유지한다. 하위 컴포넌트는 공개 서비스나 UI를 참조하지 않는다. 테스트에서 UI 독립 로직을 직접 실행할 수 있게 한다.

상태 소유자: 기존 RepositorySessionState와 프로세스 수명 소유권은 유지한다. 게시 확인 스냅샷과 표시 목록은 전용 확인 모델이 함께 관리한다. 결과 계산은 상태 없는 전용 컴포넌트로 이동한다.

## 단계와 구조 조건

1. 기준 테스트와 전체 소스의 책임·호출 관계를 확인한다.
2. Core 책임을 분리하고 공개 경로의 기존 테스트를 통과시킨다.
3. 화면의 상태·결과 책임을 분리하고 직접 실행하는 회귀 테스트를 추가한다.
4. 이전 결합 제거, 의존 방향, 테스트, Release VSIX와 미리보기 빌드를 확인하고 결과를 기록한다.

## 검증 계획과 한계

검색으로 기존 서비스의 구현·직접 Git 호출과 실행기의 경로 탐색 구현이 제거됐는지 확인한다. 새 호출 경로는 공개 서비스 테스트와 새 책임별 테스트로 확인한다. 게시의 전체 사전 검사, 상태 변경 거부, 취소·실패 결과, 탐색 경계, UTF-8와 프로세스 취소를 검증한다. UI 소스 위치에 의존하는 기존 검사는 새 소유자에 맞추되 행동 검증을 추가한다.

실제 Visual Studio 호스트와 SVN 왕복 검증은 실행 환경 및 사용 가능한 격리된 테스트 fixture를 확인한 뒤 검증 가능 여부를 기록한다. 기존 로컬 SVN fixture를 임의로 초기화하지 않는다. 새로운 제품 동작이나 오류 정책을 도입할 필요가 생기면 구조 변경과 구분한다.

## 검증 결과

2026-09-05 기준, 위 네 단계의 구조 변경과 검증을 완료했다. 기존 변경과 Core 분리의 체크포인트는 `d65acb9`에 커밋했다.

### 확인한 구조 변경

| 영역 | 이전 소유자 | 현재 소유자와 호출 관계 |
| --- | --- | --- |
| 탐색·조회 | GitSvnWorkspaceService | 서비스 → GitSvnRepositoryDiscovery → GitRepositoryReader. 파일 탐색은 Discovery, Git 상태·커밋 조회와 공통 사전 검사는 Reader가 담당한다. |
| rebase·게시 | GitSvnWorkspaceService | 서비스 → GitSvnRebaseWorkflow 또는 GitSvnPublishWorkflow. 게시 준비·재검증은 GitSvnPublishValidator를 거치며 SVN 설정 해석은 SvnConfiguration을 사용한다. |
| 실행 파일 경로 | ProcessGitCommandRunner | GitSvnRuntimeDetector와 ProcessGitCommandRunner가 GitExecutableResolver를 함께 사용한다. 프로세스 실행·종료는 기존 실행기가 계속 담당한다. |
| 게시 확인 상태 | GitSvnShuttleViewModel | PublishConfirmationViewModel이 스냅샷과 표시 목록·열림 상태를 소유한다. 화면은 Prepare, Close, TakeSnapshots를 호출하고 속성 알림을 기존 바인딩 이름으로 전달한다. |
| 작업 결과·행·로그 | GitSvnShuttleViewModel | OperationOutcomeBuilder가 결과를 계산하고 OperationPresentation이 공통 문구를 제공한다. RepositoryViewModel은 행 표시, WorkspaceOutputLogger는 Output 출력을 담당한다. |

GitSvnWorkspaceService는 1,232줄에서 95줄, GitSvnShuttleViewModel은 1,416줄에서 948줄, ProcessGitCommandRunner는 343줄에서 233줄이 됐다. 파일 크기뿐 아니라 실제 호출과 책임 소유자가 변경됐다. 화면 모델에는 명령 조정, 런타임 설정 표시, 솔루션 이벤트 대응이 남아 있다.

검색에서 공개 서비스의 직접 Git 실행·파일 탐색·스냅샷 생성 구현이 제거됐음을 확인했다. Core 하위 컴포넌트는 공개 서비스와 VSIX를 참조하지 않는다. 실행 파일 탐색 구현은 resolver에만 있으며 탐지기와 실행기가 모두 사용한다. 화면의 `preparedPublishSnapshots`, 결과 계산 구현, 행 클래스와 중복 결과 라벨 구현도 제거됐다.

공개 서비스의 비동기 메서드 14개는 매개변수·기본값·반환형을 유지했다. OperationOutcomeBuilder, BusyExecutionResult, PublishCommitViewModel은 이동 전 코드와 비교해 접근 제한자 변경 외 로직이 같음을 확인했다.

### 상태·데이터 계약 보완

README의 불변 게시 스냅샷 계약과 달리 GitSvnPublishSnapshot이 입력 컬렉션을 그대로 참조하던 결함을 재현했다. 입력 목록을 바꾸거나 반환된 목록을 가변 인터페이스로 변환하면 확인 데이터가 바뀌었다. 재현 테스트 2개의 수정 전 실패를 확인한 뒤, 생성자에서 컬렉션을 복사하고 읽기 전용으로 노출하도록 수정했다. 커밋과 SVN 대상의 순서는 유지한다.

선택·펼침·작업 결과의 소유권은 RepositorySessionState에 유지했다. 게시 확인은 전용 모델이 한 번 소비한 뒤 폐기한다. 새 테스트는 원본 목록 변경의 격리, 확인 폐기 후 재준비, 빈 확인, 표시 순서와 속성 알림을 직접 실행해 확인한다.

### 실행한 검사

| 검사 | 결과와 검증 범위 |
| --- | --- |
| `dotnet test tests/GitSvnShuttle.Core.Tests/GitSvnShuttle.Core.Tests.csproj --no-restore --nologo` | 75개 통과, 실패·건너뜀 0개. 기준 46개에 회귀 검증 29개 추가. 공개 진입점별 dry-run 중 상태 변경, 전체 사전 검사 실패 시 게시 차단, rebase 순서·중단·복구 조건, 결과 분류, 스냅샷 불변성 등을 포함한다. |
| `dotnet build git-svn-shuttle.sln -c Release --no-restore --nologo` | 전체 솔루션 빌드 성공, 경고·오류 0개. VSIX, Core, 테스트, WPF 미리보기 산출물 생성. |
| `test-env/setup.ps1 -WorkspaceRoot <새 임시 경로>` 후 `test-env/smoke.ps1 -WorkspaceRoot <같은 경로>` | 실제 제품 서비스와 실행기를 사용해 중첩 저장소 탐색, 두 저장소의 rebase·dcommit, 브랜치 갱신, git-svn-id와 SVN 기준 일치, 게시 대기 0개 확인. 테스트 서버와 생성한 임시 fixture는 정리했다. |
| `tests/verify-acp-isolation.ps1` | .NET Framework 프로세스의 ACP 949·1252에서 동일한 한글 작성자·커밋 제목 확인. `ACP_ISOLATION=PASS`. |
| 구조 검색·UI 렌더링·`git diff --check` | 이전 결합 제거와 의존 방향 확인. 실제 XAML을 사용하는 목 데이터 미리보기의 1280px 어두운 화면과 420px 밝은 화면 PNG를 생성하고 시각 확인. 공백 오류 없음. |

UI 독립 로직 테스트는 제품의 C# 소스 파일을 테스트 프로젝트에 연결해 컴파일한다. Visual Studio 호스트가 필요 없는 상태·결과 계산을 직접 검증하며, WPF 연결 자체를 실행하는 테스트와는 구분한다.

NuGet 온라인 복원은 제한 환경의 인증서 오류로 실패했으나 로컬 패키지 캐시를 지정해 복원했다. SVN fixture 구성의 임시 경로 접근 실패와 ACP 검사의 Path/PATH 중복 실패는 제한 환경 밖에서 재실행해 통과했다. 이를 제품 설정 변경으로 우회하지 않았다.

### 미검증 항목과 남은 구조 작업

이 계획에 남은 구조 변경은 없다. 실제 Visual Studio에 설치한 뒤 클릭하는 전체 사용자 흐름, 실제 서버 인증 및 운영 저장소는 검증하지 않았다. 미리보기는 목 데이터를 사용하므로 솔루션 전환·파일 감시·비동기 버튼 상호작용까지 검증한 것으로 해석하지 않는다. 테스트 통과는 해당 경로의 근거이며 모든 버그의 부재를 의미하지 않는다.
