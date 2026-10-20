# Sunrise File Manager R19 R13 — GitHub Capture 대조 및 단계 가시화 보완

## 확인한 Capture 기준

- GitHub 저장소 `Peter-msk/Sunrise_capture`, 최신 공개 릴리스 `v4.2.0` (2026-09-30): https://github.com/Peter-msk/Sunrise_capture/releases/tag/v4.2.0
- `v4.2.0` GitHub 태그의 source zip/tree 목록: `README.md`, `latest.json`, `notice.txt`. Release Asset은 `SunriseCapture_Setup.exe` 하나. 따라서 실제 `03_Source/build_scripts/build_pipeline.py` 전체를 이 GitHub 저장소에서 가져올 수 없었음.
- 연결된 `Sunrise_Capture_v4.2.0_작업인수인계서_FINAL.md`에 기록된 성공 경로: `SunriseCapture_Build.exe → cmd.exe → BUILD.cmd → CPython 3.12 x64 → build_scripts/build_pipeline.py → PyInstaller / SignTool / certreq+certutil / Inno Setup`. R06의 PyInstaller 본체·업데이터 SignTool 서명 Windows 실기 성공과 SHA1 파서 수정 확인. 다만 R07 후속 설치형 전단계 및 설치 스모크는 PENDING.
- File Manager는 .NET 10 WPF이므로 Python/PyInstaller 코드를 가정해서 대체하지 않음. 확인된 **빌드→서명→설치형 패키징 단계 분리**, CMD 진입점, OS 네이티브 서명 도구 활용 원칙만 적용.

## R12에서 사용자가 보게 된 현상

R12 `BUILD_RELEASE_INSTALLER.cmd`는 SignTool 위치를 출력한 직후 하위 명령을 `> R15_payload_build.log 2>&1`로 실행하였다. 이 때문에 본체 `.NET`, 인증서, 서명 진행이 전부 화면에서 사라졌다. 주어진 콘솔 로그만으로는 **정지인지 장시간 처리 중인지 확정할 수 없다**. R12 해당 구간의 `R14_BUILD_RESULT.txt`, `R14_cert_provision.log`, `R14_wpf_publish.log`가 없어 근본적인 외부 도구 정지 원인을 추정하지 않는다.

## R13 수정

1. `BUILD_RELEASE_INSTALLER.cmd`의 하위 OneDir 전체 출력 리디렉션 제거. `R15_payload_build.log`는 시작/성공/실패 요약으로 보존하고 상세 정보는 R14 개별 로그 및 콘솔로 노출.
2. `BUILD_RELEASE_ONEDIR.cmd`를 **dotnet publish → PIN 인증서 재사용 또는 최초 생성/복구 → SignTool sign/verify** 순서로 변경. 인증서 작업 이전에 WPF 빌드 상태 확인 가능. 최종 Setup은 서명 검증 전 절대 커밋하지 않음.
3. `dotnet publish -v:minimal`로 실시간 콘솔 출력을 유지하면서 MSBuild 기본 파일 로거 `-flp:logfile=...;verbosity=normal`로 상세 기록 보존.
4. `R13_CURRENT_STAGE.txt`에 현재 단계 출력 (`WPF_PUBLISH`, `SIGNING_PREFLIGHT`, `AUTHENTICODE_SIGN`, `SIGNATURE_CHECK`, `INSTALLER_COMPILE`, `INSTALLER_VERIFY`, `COMMIT`). 장애 발생 시 `FAILED_...` 상태 남김.
5. 도구 사전검사의 R12에 존재하지 않는 `CHECK_SIGNING_CERT.cmd` 실행 안내 삭제.
6. 인증서 PIN, 새 서명 신원 생성 안전정책, 인증서 선택 기준, SHA1 검증, EXE/DLL/Setup 서명 및 기존 배포본 보호는 변경하지 않음.

## 공식 빌드 실행 및 장애 진단

```cmd
BUILD_ON_WINDOWS_COMPANY.cmd
```

빌드 진행 중 별도의 CMD에서 파일 확인:

```cmd
type build-logs\R13_CURRENT_STAGE.txt
type build-logs\R14_BUILD_RESULT.txt
type build-logs\R15_INSTALLER_BUILD_RESULT.txt
```

- `WPF_PUBLISH`: NuGet Restore 또는 WPF 컴파일 진행 상태. `R14_wpf_publish.log` 확인
- `SIGNING_PREFLIGHT`: 인증서 재사용/생성/검증. `R14_cert_provision_console.log`, `R14_cert_provision.log` 확인
- `AUTHENTICODE_SIGN`: SignTool EXE/DLL 또는 RFC 3161 타임스탬프 서버. `R14_sign_exe.log`, `R14_sign_dll.log` 확인
- `INSTALLER_COMPILE`: Inno Setup 및 언인스톨러 서명. `R15_inno_compile.log` 확인

기존 인증서/개인키 삭제 금지. 회사 보안정책·공식 서명 검증은 유지. 프로젝트 UI 및 문서 fixture는 열거나 수정하지 않음.

## 검증 상태

정적 회귀 및 소스 무결성 검증과 Windows 실기 검증을 구분함. 현재 환경에서는 Windows CMD 실제 실행, 서명, V3, 설치/제거는 미검증. R13이 **빌드 정지를 확실히 해결했다고 보장하지 않음**. 대신 숨겨진 단계 및 첫 실패 원인을 확인할 수 있도록 수정.
