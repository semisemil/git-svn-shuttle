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
