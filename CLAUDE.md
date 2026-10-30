# CLAUDE.md

FileStar는 Windows용 파일 탐색기입니다 (WPF, .NET 8, `net8.0-windows`). 탭·트리·두 창 보기·폴더 비교, 파일별 메모와 태그, 검색, 미리보기, AI로 메모·태그 만들기를 제공합니다. 배포와 자동 업데이트는 Velopack으로 합니다.

## 명령

```bash
dotnet build FileStar.sln
dotnet run --project FileStar/FileStar.csproj -- "C:\시작할\폴더"
dotnet test FileStar.Tests/FileStar.Tests.csproj
```

- VS Code(C# Dev Kit)가 열려 있으면 백그라운드 빌드가 `obj\Debug`의 생성 파일을 잡고 있어 "being used by another process"로 실패하거나, 오래된 `App.g.cs` 때문에 진입점이 두 개라는 오류가 날 수 있습니다. 그럴 때는 명령줄 빌드에 `-p:IntermediateOutputPath=obj\cli\ -p:OutputPath=bin\cli\`를 붙입니다.
- `FileStar.Tests`는 WPF 창을 전용 STA 스레드(`Support/WpfThread`)에서 실제로 띄워 바인딩 오류까지 검사합니다. 데이터는 임시 폴더만 쓰며 사용자 설정을 건드리지 않습니다. 앱의 static(CommandManager, 아이콘 캐시, 테스트용 WPF 스레드)을 같이 쓰므로 테스트는 병렬로 돌리지 않습니다.

- 개발 중에는 실제 설정을 건드리지 않도록 `FILESTAR_DATA_DIR`를 임시 폴더로 지정해서 실행합니다. 지정하지 않으면 `%AppData%\FileStar`를 씁니다.
- `FILESTAR_TRACE=1`이면 `%TEMP%\FileStar-trace.log`에 성능 로그(`Perf`)와 WPF 바인딩 오류, 처리되지 않은 예외를 남깁니다. XAML을 바꾼 뒤에는 이 로그에 `binding:` 줄이 없는지 봅니다.
- `FILESTAR_UPDATE_URL`은 로컬 Velopack 피드로 업데이트를 시험할 때만 씁니다.
- 설치 파일만 만들기: `powershell -File release\release.ps1` (결과는 `release\out`). 배포는 `FileStar.csproj`의 `<Version>`을 올리고 같은 번호의 `v*` 태그를 push하면 GitHub Actions(`.github/workflows/release.yml`)가 합니다. 태그와 `<Version>`이 다르면 빌드가 실패합니다.

## 구조 (MVVM)

```
FileStar/
  App.xaml.cs     조립 지점(composition root): 서비스를 한 번 만들고 MainWindow에 MainViewModel을 넘김
  Mvvm/           ObservableObject, RelayCommand, AsyncRelayCommand
  Models/         순수 데이터와 그 규칙 (FileNote, Favorite, SearchLocation, AppSettings …) — WPF 의존 없음
  Services/       인터페이스 + 구현 (INotesStore, IFavoritesStore, IFileOperations, IUndoHistory,
                  ISettingsStore, IRecentItems, IFileSystem, IDialogService, IWindowService, IShellService …)
  Ai/             AI 공급자(IAiProvider: Claude, OpenAI, Gemini, 사용자 서버), 프롬프트, 문서 읽기
  ViewModels/     화면 상태와 명령. 목록 행(FileItem)·트리 노드(FolderNode)·탭(BrowserTab)도 여기
  Views/          XAML 창과 code-behind, 변환기, IDialogService/IWindowService 구현
  Platform/       Windows 셸·COM·P/Invoke 래퍼 (ShellInfo, ShellContextMenu, PreviewHost, RecycleBin, ThemeManager …)
  Themes/         Light.xaml / Dark.xaml(색) + Controls.xaml(컨트롤 스타일)
```

의존 방향은 `Views → ViewModels → Services/Ai → Models`입니다. 역방향 참조는 만들지 않습니다.

## 규칙

- **ViewModel은 WPF 창을 직접 띄우지 않습니다.** `MessageBox`, `new XxxWindow()`, `Clipboard`를 ViewModel에서 쓰지 말고 `IDialogService`, `IWindowService`, `IFileOperations`, `IShellService`를 거칩니다. 그래야 단위 테스트에서 가짜 구현으로 바꿀 수 있습니다.
- **상태를 가진 static 클래스를 새로 만들지 않습니다.** 상태가 있으면 인터페이스와 구현을 만들어 `App.xaml.cs`에서 생성하고 생성자로 넘깁니다. 상태 없는 계산(`Locations`, `SizeFormat`, `FileNames`, `DiskPaths`, `SearchRunner`, `FolderCompare`)만 static으로 둡니다.
- **code-behind에는 화면 고유의 동작만 남깁니다.** 드래그 앤 드롭, 사각형 선택, 트리 스크롤 보정, 이름 바꾸기 상자 포커스, GridView 열 관리, 셸 컨텍스트 메뉴, 미리보기 렌더링이 해당합니다. 판단과 상태는 ViewModel로 보냅니다.
- **파일 변경은 반드시 `IFileOperations`를 거칩니다** (일괄 이름 바꾸기는 `BatchRenamer`, 압축은 `ZipTools`). 그래야 메모·태그가 파일을 따라가고(`INotesStore.Rename/CarryOver`) Ctrl+Z 기록(`IUndoHistory`)이 남습니다. 직접 `File.Move`/`Directory.Move`를 하면 메모가 떨어집니다.
- 폴더 감시(`IFolderWatchService`)가 알려 주는 이름 바꾸기는 FileStar가 한 것도 포함하므로 `INotesStore.FollowRename`으로만 반영합니다. `Rename`으로 다시 적용하면 맞바꾸기(a↔b) 같은 경우 메모가 되돌아갑니다.
- `INotesStore`는 UI 스레드 전용입니다. 백그라운드 작업에는 `Snapshot()`을 넘깁니다.
- 처리되지 않은 예외는 `App`이 기록하고 경고로 보여 준 뒤 계속 실행합니다. 예상되는 실패(이름 충돌, 잘못된 이름 등)는 그 자리에서 `IDialogService.Warn`으로 알맞은 문구를 보여 줍니다. 명령 재평가(CanExecute)처럼 자주 불리는 곳은 예외를 던지지 않게 합니다.
- 새 실행 취소 작업은 `UndoAction`을 상속해 `UndoAsync(UndoContext)`를 구현합니다.
- 새 AI 공급자는 `IAiProvider`를 구현해 `AiSummarizer`에 등록합니다. `switch (provider)`를 여기저기 추가하지 않습니다.
- 한 프로세스에 `MainWindow`는 하나입니다. "새 창"(Ctrl+N)은 새 프로세스를 띄웁니다.
- 클린코드를 작성한다.

## 코드 스타일

- 화면에 보이는 문자열은 한국어, 식별자와 주석은 영어입니다. 주석은 코드만으로 알 수 없는 이유와 제약만 씁니다.
- 기존 코드처럼 `Nullable` 켜짐, file-scoped namespace, 식 본문 멤버, `record`를 씁니다. 네임스페이스는 `GlobalUsings.cs`에 모여 있습니다.
- 색은 `Th.*` 리소스(`DynamicResource`)만 씁니다. 코드에서는 `SetResourceReference`나 `WithResource`를 써야 다크 모드 전환을 따라갑니다.
- 창 아이콘은 `Icon="pack://application:,,,/FileStar;component/FileStar.ico"`처럼 어셈블리 이름까지 쓴 pack URI로 씁니다. XAML이 `Views/` 안에 있어 상대 경로는 찾지 못하고, `/FileStar.ico`는 실행 진입점 어셈블리를 기준으로 해석되어 테스트에서 깨집니다.
- `App.Main`에서 `VelopackApp.Build().Run()`이 WPF보다 먼저 실행되어야 합니다 (`StartupObject`가 `FileStar.App`).
- 줄 끝은 git이 정규화합니다 (`core.autocrlf=true`).

## 확인

- 로직을 바꾸면 `FileStar.Tests`에 ViewModel/서비스 테스트를 추가하고 `dotnet test`로 확인합니다.
- 화면을 바꾸면 `FILESTAR_DATA_DIR`를 임시 폴더로 둔 채 실행해서 실제로 확인합니다. 시작, 파일 선택, 속성·미리보기 탭, 두 창 보기(Ctrl+Shift+D)를 봅니다.

• 컴플라이언스 기준 지정: SW 개발 보안 가이드(행정안전부/KISA 49개 항목), OWASP Top 10, CWE/SANS Top 25 등 준수해야 할 보안 표준을 명확히 지시합니다.
• 검사 범위 확정: 신규 개발 소스 코드, 외부 오픈소스 라이브러리 연동부, API 엔드포인트 등 검사 대상을 지정합니다.

## QA
- 코드 작성이 끝나면 별도의 프로세스가 10년이상의 시니어개발자로서 QA사항을 진행하고 보고한다.

## 시큐어코딩 확인
- 코드 작성이 끝나면 별도의 프로세스가 10년 이상의 보안 아키텍트로서 시큐어코딩 검사를 진행한다.
- 컴플라이선스 기준은 : SW 개발 보안 가이드(행정안전부/KISA 49개 항목), OWASP Top 10, CWE/SANS Top 25
