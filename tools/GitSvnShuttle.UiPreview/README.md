# Git-SVN Shuttle UI Preview

Visual Studio를 실행하지 않고 실제 `GitSvnShuttleControl` XAML을 목 데이터로 확인하는 WPF 호스트입니다.

```powershell
dotnet run --project tools\GitSvnShuttle.UiPreview\GitSvnShuttle.UiPreview.csproj
```

창 위쪽에서 밝은/어두운 테마와 420px/1280px 폭을 전환할 수 있습니다. 제품 화면을 복제하지 않고 VSIX 프로젝트의 컨트롤을 직접 참조하므로 프리뷰와 실제 XAML이 따로 어긋나지 않습니다.

PNG 렌더링:

```powershell
tools\GitSvnShuttle.UiPreview\bin\Debug\net472\GitSvnShuttle.UiPreview.exe `
  --render output\qa\ui-redesign\preview.png --theme dark --width 1280 --height 760
```

SVN 관리 화면은 `--scenario branch`로 렌더링합니다.
`multiple`, `busy`, `error`, `timeout`, `partial`, `advanced`, `connection`으로 각 상태를 확인할 수 있습니다.
프리뷰는 가상 주소와 예시 경로만 사용하며 Git이나 SVN 명령을 실행하지 않습니다.

```powershell
tools\GitSvnShuttle.UiPreview\bin\Debug\net472\GitSvnShuttle.UiPreview.exe `
  --render output\qa\svn-management\branch.png --scenario branch `
  --theme light --width 420 --height 820 --verify-management
```

`--verify-management`는 실제 WPF 입력 컨트롤에서 값 변경, 고급 설정, 후보 선택과 조회 중 입력 잠금을 확인합니다.
검증 결과는 PNG 옆의 `.verified.txt`에 저장합니다.
WPF 바인딩 진단은 `.bindings.txt`에 저장하며, 실패 시 `.failed.txt`를 남기고 종료 코드 1을 반환합니다.
