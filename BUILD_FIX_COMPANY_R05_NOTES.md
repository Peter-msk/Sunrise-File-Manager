# Sunrise File Manager 회사 전용 빌드 R05 — WPF CS0103 수정

## 기준본

`Sunrise_File_Manager_OpenSource_Full_COMPANY_BUILD_R04.zip`의 **전체 154개 원본 파일**을 보존한 상태에서 한 파일만 수정함.

## 원인

Windows 빌드 WPF_BUILD 단계에서 `AiCommandPanel.xaml.cs(64,9)`이 선언되지 않은 `SelectionSummary` 컨트롤을 사용해 CS0103 발생. XAML에는 같은 목적으로 `x:Name="ScopeLabel"`이 이미 선언되어 있음.

## 실제 수정

- `source/Sunrise.FileManager/Controls/AiCommandPanel.xaml.cs`:
  `SelectionSummary.Text` → `ScopeLabel.Text` (한 곳만 수정).
- `AiCommandPanel.xaml` 변경 없음. 기존 표시 위치, 텍스트, 기능 유지.
- 회사 보안 플래그, LogicTests 생략, 문서 Fixture 제외, 순수 CMD, Ollama 연동, 독립 미리보기 구조 변경 없음.
- 기존 C# 소스 수정 1개, 무결성 목록 갱신 1개 외 **원본 152개 파일의 바이트 동일성** 검증.

## 사용

1. R05 전체 ZIP을 **새 폴더**에 압축 해제.
2. `BUILD_ON_WINDOWS_COMPANY.cmd` 실행.
3. 오류가 남는다면 `build-logs/02_wpf_build.log`와 `build-logs/BUILD_RESULT.txt` 전달.

## 검증 범위

- XAML XML 파싱 / 생성 필드 ScopeLabel 존재 / `SelectionSummary.Text` 미사용: PASS.
- 모든 XAML 이벤트 핸들러 이름이 대응 C# partial 내에 선언됨: PASS (정적 확인).
- 회사 R04 원본 파일 보존 및 ZIP 무결성: PASS.
- Windows에서 실제 WPF 빌드: **이 환경에서 미실행**. 이 패키지는 Windows 빌드 성공 인증본이 아님.
