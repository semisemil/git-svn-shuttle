# Git-SVN Shuttle

Visual Studio의 Git 작업 흐름과 기존 SVN 서버 사이를 연결하는 가벼운 Git-SVN 도구 창입니다.

![Git-SVN Shuttle overview](images/git-svn-shuttle-overview.png)

## 한눈에 확인하는 게시 범위

- 파란색 위쪽 화살표 행: `git svn dcommit`으로 SVN에 게시될 로컬 커밋
- SVN 기준 행: 이미 SVN과 동일한 마지막 커밋
- 저장소 헤더의 경고: 커밋되지 않은 변경이나 해결해야 할 작업 상태
- 저장소별 실행과 여러 저장소 순차 실행 지원

## SVN 변경 받기

아래쪽 화살표는 해당 저장소에서 `git svn rebase`를 실행합니다. 작업 트리가 깨끗하지 않거나 브랜치가 분리된 경우에는 실행하지 않습니다.

## 확인 후 SVN에 게시

위쪽 화살표를 누르면 게시 대상 커밋과 SVN 대상을 먼저 보여줍니다. 확인 시점의 HEAD, 커밋 목록, SVN 기준점과 설정을 기록하고 실행 직전에 다시 검증합니다. 상태가 달라졌다면 게시를 중단하고 새 확인을 요구합니다.

## Git-SVN 실행 환경 설정

도구 창을 열 때 `git --version`과 `git svn --version`을 확인합니다. Git-SVN을 찾지 못하면 다음 동작을 바로 사용할 수 있습니다.

- `git.exe` 직접 선택
- PATH, Git for Windows, MSYS2 일반 설치 위치 자동 검색
- 현재 경로 재검사
- 사용자 지정 경로 초기화

## 보안과 리소스 사용

- junction 및 reparse-point 하위 탐색 차단
- 확인된 HEAD를 고정하고 dcommit 직전 상태 재검증
- Git 프로세스 시간 제한, 취소, 출력 크기 제한
- 자격 증명 형태 로그와 SVN URL 사용자 정보 제거
- 주기적 Git 폴링 대신 debounced 파일 시스템 알림 사용
- 게시자 서버, 원격 분석 또는 제3자 분석 서비스 없음

## 요구 사항

- Visual Studio 2022 또는 Visual Studio 2026, x64
- .NET Framework 4.7.2 이상
- `git svn`을 사용할 수 있는 Git 실행 환경
- 이미 clone/init이 완료된 Git-SVN 작업 복사본

Git-SVN Shuttle은 `git svn clone`이나 SVN에서 Git으로의 일회성 마이그레이션을 수행하지 않습니다.

## Privacy

Git-SVN Shuttle does not send telemetry, repository contents, credentials, or personal data to the publisher. It only invokes the selected local Git-SVN runtime, which communicates with the SVN endpoints configured by the user.

[Source code](https://github.com/semisemil/git-svn-shuttle) · [Privacy notice](https://github.com/semisemil/git-svn-shuttle/blob/main/PRIVACY.md) · [MIT license](https://github.com/semisemil/git-svn-shuttle/blob/main/LICENSE.txt)
