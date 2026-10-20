# Sunrise File Manager R19 R17 — Sunrise Capture 실제 소스 기반 릴리스 정비

## 참조 원본(직접 확인)
- Google Drive `1. Sunrise Capture/Sunrise_Capture_v4.2.0_FINAL.zip`
- ZIP 내부 `03_Source/BUILD.cmd`, `03_Source/build_scripts/build_pipeline.py`, `03_Source/build_scripts/build_artifact_contract.json`
- Capture 릴리스 흐름: `pyinstaller`(서명 포함) → `installer_build`(Setup 서명·해시) → `release_metadata` → `security_trust_package` → required artifacts 검증.
- Capture는 `stage_release_metadata`에서 설치파일을 기준으로 `latest.json`, `notice.txt`, `release_info.json`, 제품 매니페스트, 배포 가이드, 서명 검증 보고서를 동일 배포 출력 폴더에 생성함.
- Capture `CERT_SUBJECT_NAME`은 File Manager의 기존 고정 인증서와 다름. `_extract_sha1_thumbprint()`는 첫 발견 인증서를 선택할 수 있어 동명 인증서가 많은 PC에서는 그대로 이식하지 않음.

## R17 변경
1. File Manager의 `.NET 10` 네이티브 메타데이터 생성기는 Capture의 결과물 계약을 File Manager용으로 재현: `latest.json`, `notice.txt`, `release_info.json`, `sunrise_file_manager_product_manifest.json`, `UPLOAD_TO_GITHUB.txt`, `SIGNATURE_VERIFY_RESULT.txt`.
2. 생성 전 `installer/RELEASE_VERSION.txt` 버전, 실제 Setup 이름·크기, SignTool 검사 로그 존재, 프로젝트 signer SHA1/subject 검사. Setup의 실 SHA-256 계산. 인증서 비밀키/토큰 사용 없음.
3. `BUILD_RELEASE_INSTALLER.cmd`는 서명된 Setup 검증 뒤 ReleaseMetadata 실행하고, **별도** `RELEASE_ARTIFACT_VERIFY` 단계에서 배포 파일 6개의 존재/0바이트 및 제품 루트 두 파일과 바이트 동일성 검사. 실패 시 릴리스 실패(기존 Setup 복구 경로 유지).
4. 설치파일 다운로드 URL은 실제 공개 배포 위치가 승인되기 전까지 비워두어, 로컬 빌드 성공을 인터넷 공개로 오인하지 않음.
5. 제품 메타데이터는 `Sunrise-File-Manager` 소유. 중앙 `Sunrise_Control/control.txt`는 실행·점검·업데이트 정책만 관리. 공개 Control 저장소에 제품 공지/릴리스 복제하지 않음.
6. 일반 PC는 현재 비공개 GitHub 원본 raw URL을 익명 조회할 수 없음. 공개 제품별 읽기 엔드포인트 설정 전까지 원격 공지·업데이트 기능은 보장되지 않음.

## 변경하지 않은 영역
- WPF 파일탐색기/AI/메모/미리보기·UI, 앱 인증/데이터, .NET 게시, 자체서명 인증서 고정·SignTool 호출·Inno Setup 보안 정책.
- 사용자 빌드 진입점 2개: 공식 `BUILD_ON_WINDOWS_COMPANY.cmd` 및 미서명 개발용 `BUILD_DEV_EXE.cmd`.

## 검증 게이트
- 소스 계약/pytest, JSON·원본 manifests, CRLF CMD, ZIP CRC·SHA-256.
- Windows .NET 10 컴파일, SignTool 실서명, Setup 설치·업데이트·삭제 및 네트워크 접근은 **PENDING**. 서명 정책의 WinTrust 실패 허용(자체서명) 문제는 기존 빌드 계약 범위이며 R17에서 우회하지 않음.
