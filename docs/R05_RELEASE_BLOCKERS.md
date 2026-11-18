# v1.8.1 R05 — 정식 출시 보류 항목

| 게이트 | 현재 상태 | 미완료 사유 |
|---|---|---|
| .NET 10 / WPF Windows Release 컴파일 | PENDING | 이 작업 컨테이너에 dotnet 및 Windows WPF 런타임 없음 |
| R05 일반 일괄 이름 변경 Windows 원본·취소·Undo | PENDING | Native C# 시험 코드 생성, 실제 실행 전 |
| EIASS 운영용 폴더 생성·파일명 변경 실기 | PENDING | NTFS/ACL/reparse/취소·복구 Windows 검증 필요 |
| AI·클라우드 실계정·네트워크·Ollama | PENDING | 오프라인 정적 테스트로 실제 외부 서비스 완료 주장 불가 |
| 최소창/1·2·4분할/고DPI 한글 텍스트 시각 | PENDING | 실제 Windows 100/125/150/175% 캡처 필요 |
| v1.8.1 설치/서명/설정·업데이트 보존 | PENDING | 설치·서명 도구/공식 인증서 및 실사용 PC 검증 필요 |

- 기존 v1.8.0 Stable 및 GitHub main/`latest.json`/`notice.txt` 유지.
- 새 v1.8.1 PR은 Draft로 유지; 테스트 미완료를 정상 출시나 Windows PASS로 표시하지 않는다.
- NAS/SMB EIASS 운영 생성은 지원 대상으로 선언하지 않는다.
- 기존 PASS 항목은 코드 영향 범위가 같으면 재실행하지 않는다. 변경된 파일 작업/메뉴/레이아웃만 관련 회귀시험을 수행한다.
