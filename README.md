# Sunrise File Manager

Sunrise Group의 Windows 파일 탐색·정리·전송 및 다중 작업공간 관리 프로그램입니다.

## 프로그램 목적

로컬 PC, 네트워크 폴더와 선택적으로 연결한 클라우드 저장소의 파일을 한 작업공간에서 탐색·비교·검색·관리해 반복적인 파일 작업을 단순화합니다.

## 지원 운영체제

- Windows 10 / Windows 11 (64비트, x64)
- Windows 10은 빌드 19041 이상을 대상으로 합니다.

## 개발언어 및 주요 기술

- **개발언어:** C#
- **주요 기술:** .NET 10, WPF(XAML), Windows 데스크톱 API
- **배포 형태:** Windows 설치형 프로그램

## 주요 기능

- 다중 탭 및 1~4분할 파일 탐색 작업공간
- 파일·폴더 복사, 이동, 이름 변경, 삭제, 압축·압축 해제
- 휴지통 탐색과 파일 복원
- 파일명·확장자·크기·날짜 등의 조건 검색과 문서 본문 검색
- 파일 미리보기, 상세정보, 메모 및 폴더 비교
- 단방향 폴더 동기화와 파일명 일괄 변경 미리보기
- Google Drive·OneDrive의 선택적 계정 연결 및 지원 파일 작업
- 선택적으로 연결한 로컬 Ollama를 이용한 파일 검색·분류·요약 보조
- 전송 충돌·취소·실패 결과 안내, 화면 테마 및 접근성 설정

---

Copyright © 2026 Sunrise Group. All rights reserved.

## Sunrise Group 통합 경로

- 설치형의 표준 설치 경로는 `%ProgramFiles%\\Sunrise Group\\Sunrise File Manager`입니다. 실제 설치 경로는 **별도 설치 프로그램의 DefaultDirName에서도 반드시 동일하게 설정**해야 합니다(이 소스 저장소에는 설치 스크립트가 포함되지 않습니다).
- 설정, AI 작업 기록, 메모 무결성·복구 스냅샷, 공지·Control 캐시, 업데이트 임시 저장소는 `%LOCALAPPDATA%\\Sunrise Group\\Sunrise File Manager` 아래에서 분리합니다.
- 기존 `%LOCALAPPDATA%\\Sunrise\\FileManager` 데이터는 첫 실행 시 새 위치로 **복사**하되 원본을 삭제하거나 새 위치의 기존 파일을 덮어쓰지 않습니다. 실패하면 프로그램 시작을 중단하고 기존 사용자 데이터를 보호합니다. 진행 중이던 구형 업데이트 다운로드는 이전하지 않습니다.
- 사용자가 선택한 문서 경로와 문서 옆 `#memo` 폴더, 외부 Ollama 설치 위치는 변경하지 않습니다.
- Windows 설치형 Setup·업데이트·재설치·제거와 실제 데이터 복사는 별도의 Windows 통합 검증 대상입니다.
