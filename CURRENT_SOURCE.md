# Sunrise File Manager — v1.2.0 소스 기준본

- 현재 개발 기준: **v1.2.0 R00**, 부모: v1.1.0 R23.
- 공식 배포: 기존 GitHub 릴리스 v1.1.0은 유지. v1.2.0 Setup은 Windows 서명 빌드 및 실기 검증 전에는 미발행.
- 핵심 변경: 업데이트 다운로드 및 SHA256 확인 뒤, 본체 프로세스/파일 작업의 정상 종료를 기다린 후 설치 실행.
- 중앙제어: Sunrise_Control/control.txt (실행·점검·차단·업데이트 정책).
- 제품 공지·업데이트: Sunrise-File-Manager 제품 저장소의 notice.txt / latest.json.
- 인수인계 및 검증: docs/V120_SUNRISE_GROUP_AUDIT_HANDOVER.md.
- 빌드 진입: BUILD_ON_WINDOWS_COMPANY.cmd (공식), BUILD_DEV_EXE.cmd (개발용).
- 그룹 표준에 따라 소스 정적 사전 검증을 빌드 중 반복하지 않으며, 필수 Windows 컴파일·서명·Setup 검증은 유지.
- Windows 동적 검증 완료 전에는 상용 배포 승인/업데이트 성공을 주장하지 않음.
