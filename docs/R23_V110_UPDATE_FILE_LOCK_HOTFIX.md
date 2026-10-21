# R23 — 업데이트 다운로드 파일 잠금 오류 수정

## 실제 증상

기존 Sunrise File Manager 1.0.0에서 GitHub의 v1.1.0 릴리스가 인식되고 업데이트 버튼이 활성화되지만, 다운로드 뒤 `The process cannot access the file because it is being used by another process.` 메시지 발생.

## 원인

`SunriseReleaseService.DownloadAsync()`에서 `await using var file = new FileStream(... FileShare.None ...)`의 범위가 다운로드 반복문 전체이므로, `File.Move(temp, target, true)`를 호출할 때 동일 프로세스가 임시파일을 독점 잠근 상태였음. 네트워크·인증서·GitHub 버전 정보의 문제가 아님.

## 보완

`await using (var file = new FileStream(...)) { ... await file.FlushAsync(); }` 블록에서 다운로드를 마친 뒤 파일 핸들을 완전히 해제하고, `file_size_bytes`와 SHA-256 검증을 확인한 뒤에만 `File.Move` 실행.

- 사용자의 승인 없는 설치 실행 금지 유지.
- 다운로드 URL/리디렉션 허용 호스트/크기 상한/해시 검증 유지.
- Control 정책과 `latest.json`/`notice.txt`의 책임 분리 유지.
- 버전 1.1.0의 코드 수정 소스이며, 이미 게시된 v1.1.0 Setup 자산에는 이 코드가 자동으로 적용되지 않음.

## 배포 지침

**이미 설치된 1.0.0의 업데이트 처리 코드는 원격 메타데이터 변경만으로 교체할 수 없음.** 기존 사용자는 [공식 v1.1.0 설치파일](https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/v1.1.0)을 브라우저에서 직접 다운로드하고, File Manager 종료 후 설치해야 함. 향후 후속 버전 빌드 및 게시 시 이 소스 수정분을 반영해야 정상 앱 내 자동 다운로드 경로가 동작함.

## 검증

Linux 환경의 Python 소스 회귀/패키징 검사 및 ZIP 무결성만 검증. 실제 Windows 빌드·앱 내 1.1.x -> 다음 버전 갱신, 설치·롤백은 미검증.
