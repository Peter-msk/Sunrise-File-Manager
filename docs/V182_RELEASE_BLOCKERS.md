# Sunrise File Manager v1.8.2 R00 — 출시에 필요한 미완료 게이트

| 영역 | 상태 | 최소 통과 조건 |
|---|---|---|
| 버전 계약 | STATIC검증 대상 | csproj/어셈블리/manifest/Control/XAML/installer/release notice 1.8.2 일치 |
| Windows 10/11 x64 .NET 10 Release WPF | PENDING | 새 소스의 실제 컴파일·실행 정상, 결함 수정 |
| EIASS 로컬 사업 폴더 | PENDING | NTFS/ReFS/ACL/링크/경합/기존파일/취소 안전성 실기 |
| EIASS/일반 PDF 이름 변경 | PENDING | 충돌/취소/복구/Undo/메모 및 원본 보존 실기 |
| 파일 이동·복사/다른 볼륨 | PENDING | 부분 실패/동시 수정/비재귀 원본 정리 실기 |
| 탐색/UI/UX | PENDING | 1·2·4분할, 최소창, DPI 100/125/150/175%, 2,205건 목록 및 성능 실측 |
| 클라우드/AI/Ollama | PENDING | 연결된 실서비스·사용자 승인 경계 및 실패 UX 실기; 무단 계정 변경 금지 |
| 설치/서명/업데이트 | PENDING | `Sunrise_File_Manager_1.8.2_Setup.exe` 빌드, Authenticode 확인, SHA-256, v1.8.0→v1.8.2 설치·설정 유지·복구 실기 |
| 정식 GitHub Release & update feed | BLOCKED | 실제 다운로드 자산과 공개 해시 확인 뒤에만 version 1.8.2 latest.json 공개 |

- 별도 회사 폴더 규칙은 요구하지 않는다. 기존 파일/설정/업데이트 공개 계약을 보존한다.
- NAS/SMB 운영 폴더 생성은 검증되지 않아 계속 차단된다.
- 기존 GitHub Actions 중단 상태를 유지하며 자동 빌드/배포를 실행하지 않는다.
- 이전 R05 Python/Windows 증거는 참고만 하고, 현 버전의 변경영향 검증과 Windows 실기 결과를 각각 별도로 기록한다.
