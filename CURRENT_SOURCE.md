# Sunrise File Manager v1.8.2 R03 — 정식 배포 기준

- 공식 제품 버전: v1.8.2 (2026-11-18 GitHub 정식 릴리즈 확인).
- 개발·유지보수 기준: 사용자 확정 v1.8.2 R03. `v1.8.2 R00`은 최초 버전 승격의 역사적 기준이다.
- 최종 원본 소스 ZIP: `Sunrise_File_Manager_v1.8.2_R03.zip`, 536개 항목, 1,597,479 bytes, SHA-256 `a21dd93568c566b4eb0c1553856bd28b0a164ea59d283c38c131a6eeaa8d774b`.
- 공식 배포 자산: [Sunrise_File_Manager_1.8.2_Setup.exe](https://github.com/Peter-msk/Sunrise-File-Manager/releases/tag/v1.8.2), 52,665,816 bytes, GitHub 공개 SHA-256 `90253f8e4d0e4dec8aee5a49cc44ff3d83e07f14a6c87e6840b25526d2a6c421`. `latest.json`과 공개 자산의 크기·해시 일치 확인.
- 원본 소스 ZIP과 설치파일은 서로 다른 산출물이다. 사용자 배포 결정 및 공개 자산 확인을 기록하며, Authenticode·Windows 기능별 실기 및 ZIP에서 설치파일이 생성됐다는 재현 증거를 독립 검증한 것으로 간주하지 않는다.
- GitHub에는 R03 제품 소스·빌드 도구·관련 공개 문서를 동기화한다. 전체 검증 자료 및 역사 자료는 원본 ZIP을 기준으로 한다. 기존 공개 설치 자산, 태그, 자동 업데이트 메타데이터 변경 없음.
- 과거 R00~R03의 후보 상태/미완료 검증 보고서는 이력이다. 현행 배포 상태는 이 문서를 우선한다. 당시 검증 결과를 새 Windows PASS로 변경하지 않는다.

## 유지보수
- `SDK_DIAGNOSTICS.cmd` 또는 `BUILD.cmd --check --no-pause` 후 `BUILD.cmd --release --no-pause`. .NET 10 SDK 및 기존 서명 정책 유지.
- 변경영향 검사와 실제 Windows 실행·설치·복구 증거 중심으로 작업하며 무관한 이전 PASS 검사 중복 실행을 피한다.
- EIASS는 로컬 NTFS/ReFS의 누락 폴더만 생성. NAS/SMB 및 원격 클라우드 원본 제거의 보호 조건은 유지한다.
