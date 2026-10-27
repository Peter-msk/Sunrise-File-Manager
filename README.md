# Sunrise File Manager

**최신 설치 배포 기준: v1.7.0** · Windows 10/11 x64 · 설치형

Windows 파일과 폴더를 여러 작업공간에서 탐색·관리하고, 문서 검토와 반복적인 파일 정리 업무를 지원하는 Sunrise Group의 데스크톱 파일 관리자입니다.

## 1. 프로그램 개요
Sunrise File Manager는 Windows 탐색기에 익숙한 조작을 바탕으로 다중 탭, 최대 4분할 탐색, 파일 미리보기, 메모 및 업무용 파일 이름 정리 기능을 하나로 구성했습니다.

## 2. 개발 목적
- 여러 폴더와 드라이브 사이에서 반복하는 파일 작업을 줄입니다.
- 업무 문서를 미리보고 메모와 함께 관리할 수 있게 합니다.
- 환경영향평가 문서 등 대량 파일의 이름 변경 및 정리 편의성을 높입니다.
- Windows의 일반적인 파일 조작과 단축키 사용 경험을 유지합니다.

## 3. 주요 기능
- 다중 탭 및 1~4분할 파일 탐색
- 복사·이동·이름 변경·삭제·압축·압축 해제
- 파일 미리보기, 상세정보, 파일별 메모
- 이름·확장자·크기·날짜 조건 검색 및 지원 문서의 본문 검색
- EIASS 제출용 PDF 파일명 일괄 변경과 적용 전 미리보기
- Windows 휴지통 탐색·복원 및 선택 항목 삭제
- 목록 내보내기, 작업 화면과 사용자 보기 설정 복원
- 라이트·다크 테마, 인터페이스 배율 조절
- 선택적으로 연결하는 로컬 Ollama AI 파일 검색·분류·요약

v1.5.5에서는 우클릭 메뉴 구성과 좌측 드라이브 접기 동작을 개선하고, 휴지통의 Delete 키 조작을 추가했습니다.

## 4. 실행조건
| 항목 | 조건 |
| --- | --- |
| 운영체제 | Windows 10 또는 Windows 11 |
| 시스템 | 64비트(x64) |
| 배포 형식 | Windows 설치파일(Setup EXE) |
| 로컬 AI | Ollama 사용 시 별도 설치 및 모델 준비 필요; 일반 파일 관리에는 불필요 |
| 개발용 소스 빌드 | .NET 10 SDK 및 Windows Desktop 빌드 환경 필요 |

배포 설치파일 사용자는 일반적인 파일 관리 기능을 위해 .NET 개발용 SDK를 설치할 필요가 없습니다.

## 5. 설치 및 사용방법
1. [공식 Releases](https://github.com/Peter-msk/Sunrise-File-Manager/releases)에서 현재 버전 설치파일을 내려받습니다.
2. Setup을 실행해 설치한 다음 Sunrise File Manager를 시작합니다.
3. 왼쪽 탐색창에서 드라이브나 폴더를 선택합니다.
4. 탭 추가 또는 분할 기능으로 여러 작업 폴더를 나란히 엽니다.
5. 파일 선택 후 우클릭 메뉴, 상단 도구 또는 기본 키보드 단축키로 작업합니다.
6. 문서명 일괄 변경 시 대상과 변경 예정 이름을 미리 검토한 뒤 적용합니다.
7. 휴지통에서는 복원할 항목과 영구 삭제할 항목을 구분하여 작업합니다.

### 소스 개발 빌드
개발 환경에서는 다음 명령을 사용할 수 있습니다.

```powershell
dotnet build source/Sunrise.FileManager/Sunrise.FileManager.csproj -c Release
```

소스 프로젝트에 표기된 개발 버전과 공식 설치 배포 버전은 별도로 확인해야 하며, 위의 **v1.5.5는 배포 메타데이터 기준**입니다.

## 6. 최신 버전
- **현재 설치 배포 메타데이터 기준:** v1.5.5
- [v1.5.5 릴리즈](https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/v1.5.5)
- [전체 릴리즈](https://github.com/Peter-msk/Sunrise-File-Manager/releases)

---
Copyright © 2026 Sunrise Group. All rights reserved.
