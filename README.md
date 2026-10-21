# Sunrise File Manager

**Sunrise Group**의 Windows 10/11 x64용 데스크톱 파일 관리자입니다.

## 주요 기능

- Windows 탐색기형 다중 탭 및 최대 4분할 파일 탐색
- 파일별 `#memo`, 속성 확인 및 미리보기
- 복사·이동·이름 변경·압축·해제 및 실수 방지 확인 흐름
- 라이트·다크 테마, 조절 가능한 전체 UI 배율과 반응형 메뉴
- Windows 기본 폴더 바로가기 및 탐색 탭 안의 휴지통 목록·복원
- 선택적으로 연결하는 로컬 Ollama AI 파일 검색·분류·요약

## 소스 버전과 공식 설치 버전

이 저장소의 애플리케이션 소스 기준은 **1.3.0**입니다. 공식 Windows 설치 패키지는 [Releases](https://github.com/Peter-msk/Sunrise-File-Manager/releases)에서 제공합니다. 소스 개발판과 공식 배포판 버전은 별도로 관리됩니다.

## 빌드 환경

Windows 10/11 x64, .NET 10 SDK와 Windows Desktop 개발 환경이 필요합니다. 애플리케이션 프로젝트는 `source/Sunrise.FileManager/Sunrise.FileManager.csproj`이며, 로컬 개발 빌드는 `dotnet build source/Sunrise.FileManager/Sunrise.FileManager.csproj -c Release`로 수행할 수 있습니다. 사내 공식 설치파일 제작 및 서명은 별도의 승인된 릴리스 환경에서 수행합니다.

## 라이선스

프로젝트 파일에 선언된 Apache-2.0 라이선스와 타사 구성요소의 라이선스가 적용됩니다.

© 2026 Sunrise Group.
