---
{
  "schema_version": 2,
  "id": "SPEC-0001",
  "title": "실사용 Git-SVN 복구 및 외부 링크 저장소 지원",
  "kind": "bug",
  "status": "completed",
  "revision": 1,
  "supersedes": [],
  "superseded_by": null,
  "related_issues": []
}
---

## Current

- 다른 PC에서 Git 커밋 작성자 또는 제목의 한글이 깨져 표시될 수 있다.
- `git svn rebase`가 충돌로 중단되면 저장소는 일반적인 작업 필요 상태로만 표시되고, 사용자가 충돌을 해결한 뒤 계속하거나 rebase를 중단할 수 없다.
- 솔루션에 정션으로 연결한 외부 Git 저장소의 하위 프로젝트는 저장소 루트에 직접 연결된 것이 아니면 Git-SVN 저장소로 발견되지 않는다.

## Contract

- REQ-001
  - Behavior: Git-SVN Shuttle은 PC의 기본 코드 페이지와 무관하게 Git이 반환한 한글 커밋 작성자와 제목을 원문대로 표시한다.
  - Done when: 한글 작성자와 한글 제목이 포함된 실제 Git 커밋을 서로 다른 Windows 기본 코드 페이지 조건에서도 동일한 문자열로 읽는다.

- REQ-002
  - Behavior: `git svn rebase`가 충돌로 중단되면 해당 저장소를 rebase 진행 중인 충돌 상태로 구분하고, 사용자가 해결해야 하는 충돌 파일을 표시한다.
  - Done when: rebase 메타데이터와 미해결 충돌이 있는 저장소에 일반적인 작업 필요 상태 대신 rebase 충돌 안내와 충돌 파일 목록이 표시된다.

- REQ-003
  - Behavior: rebase 충돌 상태에서는 사용자가 충돌을 해결하고 스테이징한 뒤 `git rebase --continue`로 계속하거나, 확인 후 `git rebase --abort`로 중단할 수 있다.
  - Done when: 미해결 충돌이 남아 있으면 계속할 수 없고, 모두 해결하여 스테이징하면 계속할 수 있으며, 중단은 사용자 확인 후에만 실행되고 각 명령 결과가 저장소 상태에 반영된다.

- REQ-004
  - Behavior: 솔루션에 로드된 프로젝트 경로가 정션을 통해 외부 Git 저장소의 하위 폴더를 가리키면, Git-SVN Shuttle은 정션의 실제 대상에서 Git 저장소 루트를 확인하고 `svn-remote.*`가 설정된 저장소를 외부 링크 저장소로 표시한다.
  - Done when: 저장소 루트가 아닌 하위 프로젝트 폴더를 가리키는 정션을 통해 로드된 프로젝트의 실제 Git-SVN 저장소 루트가 한 번만 발견되고, UI에서 외부 링크 저장소임과 실제 작업 대상 경로를 확인할 수 있다.

## Boundaries

- 임의의 정션 또는 재분석 지점 아래를 재귀 탐색하지 않는다.
- 외부 저장소 탐색은 Visual Studio 솔루션에 로드된 프로젝트가 가리키는 경로로 제한한다.
- Git-SVN 저장소가 아닌 외부 Git 저장소는 작업 목록에 포함하지 않는다.

## Preserve

- 기존 솔루션 루트 및 일반 중첩 Git-SVN 저장소 탐색을 유지한다.
- 기존 dirty tree, detached HEAD, merge/rebase 진행 상태, merge commit, 게시 대상 변경 및 `dcommit --dry-run` 보호를 유지한다.
- 일반 Git 커밋·diff·stage 작업은 Visual Studio의 기존 Git UI에 맡기고, `git svn clone`이나 저장소 마이그레이션을 추가하지 않는다.
- 여러 저장소의 rebase와 dcommit은 기존처럼 순차 실행하며 첫 실패 후 나머지 작업을 중단한다.

## Verification

- 실제 Git 프로세스를 사용해 한글 작성자와 제목의 UTF-8 왕복을 검증한다.
- rebase 충돌 감지, 미해결 상태의 계속 차단, 해결 후 계속, 확인된 중단 명령을 각각 검증한다.
- 저장소 내부 하위 프로젝트를 가리키는 정션 탐색과 임의 정션 비재귀 탐색을 함께 검증한다.
- Core 전체 테스트와 Release VSIX 빌드가 통과해야 한다.
