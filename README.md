# FileStar

Windows용 파일 탐색기 (WPF, .NET 8). 탭·트리·두 창 보기·폴더 비교, 파일별 메모와 태그, 이름·확장자·태그·메모 검색, 문서 미리보기, AI로 메모·태그 만들기.

## 설치

[Releases](https://github.com/sorrymommy/FileStar/releases/latest)에서 `FileStar-win-Setup.exe`를 받아 실행합니다. 관리자 권한과 .NET 설치는 필요 없습니다.
설치한 FileStar는 시작할 때 새 버전을 확인해 자동으로 업데이트합니다 (도움말 > 업데이트 확인).

설정·즐겨찾기·메모·태그는 `%AppData%\FileStar`에 저장되며, 업데이트나 제거 후에도 남습니다.

## 새 버전 배포

1. `FileStar/FileStar.csproj`의 `<Version>`을 올립니다 (예: `1.0.1`).
2. 커밋하고 같은 번호의 태그를 push합니다.

   ```
   git tag v1.0.1
   git push origin main v1.0.1
   ```

   GitHub Actions(`.github/workflows/release.yml`)가 빌드해서 Releases에 올립니다.

또는 이 PC에서 직접: `$env:GITHUB_TOKEN`(Contents 쓰기 권한 토큰)을 설정하고
`powershell -ExecutionPolicy Bypass -File release\release.ps1 -Publish`.
`-Publish` 없이 실행하면 `release\out`에 설치 파일만 만듭니다.

배포와 자동 업데이트는 [Velopack](https://velopack.io)을 사용합니다.

## 라이선스

[MIT](LICENSE)
