# 엔진 수정 기록 — AegisGuard 0.3.0

검증일: 2026-11-15 (Asia/Seoul). 대상: Unity 6000.3.13f1, Windows x64 / Android ARM64.

## 수정 위치와 적용 방법

설치된 `C:/Program Files/Unity/Hub/Editor/6000.3.13f1/Editor/Data/il2cpp`를 프로젝트의 `Library/AegisGuardEngine/6000.3.13f1`에 물리 복사한 뒤 **이 복사본**을 수정합니다. 빌드 프로세스에서만 `UNITY_IL2CPP_PATH`를 지정하고 종료 시 원래 환경 변수로 복원합니다. Unity Editor/UnityPlayer 바이너리 자체를 패치한 작업은 아닙니다.

이번 실제 적용 프로젝트는 다음 두 개입니다.

- Windows: `<Windows validation project>`
- Android: `<Android validation project>`

두 프로젝트에 있는 동일한 상대 경로의 파일이 아래와 같이 바뀝니다.

| 파일 | 변경 내용 |
| --- | --- |
| `libil2cpp/vm/MetadataLoader.cpp` | `AegisMetadata.h` 포함, 파일 길이 제한, 인증 복호화 후 원래 암호문 매핑 해제, 실패 시 종료 코드 86, unload 시 전용 메모리 정리 호출 |
| `libil2cpp/vm/AegisMetadata.h` | 추가한 보호 로더. XChaCha20-Poly1305 인증, 평문 metadata v39 검사, 전용 VM 할당·읽기 전용 전환·키 지우기·메모리 해제 |
| `libil2cpp/vm/monocypher.c`, `.h` | Monocypher 4.0.2 원본 추가. 암호 함수 구현이며 자체 변경 없음 |
| `libil2cpp/vm/AegisBuildKey.h` | 빌드별 랜덤 키의 두 조각을 임시 생성. 0.3에서는 변경 가능한 배열이고 사용 직후 지움. 빌드 종료 시 헤더 전체를 `#error` 문으로 덮어씀 |
| `libil2cpp/vm/GlobalMetadata.cpp` | 수정하지 않음. Unity가 테이블 내부 포인터를 유지하므로 평문 metadata는 실행 중 필요함 |

`Editor/NativeDistribution.cs`가 Unity 개발 도구 디렉터리 배치를 준비하고, `Editor/GuardNativeProtection.cs`가 원본 해시 검사·패치·키 생성·패커 실행·산출물 확인·정리를 담당합니다. Windows metadata는 player build 후 암호화하고, Android metadata는 `IPostGenerateGradleAndroidProject`에서 Gradle 패키징·서명 전에 암호화합니다.

## 0.2에서 0.3으로 바뀐 메모리 처리

0.2는 `malloc` 영역에 평문 전체를 보관했습니다. 0.3은 Windows `VirtualAlloc`, Android `mmap`으로 메타데이터만의 영역을 만듭니다. 인증에 성공하면 `VirtualProtect(PAGE_READONLY)` / `mprotect(PROT_READ)`로 바꾸고, 읽기 전용 전환에 실패하면 초기화를 중단합니다.

페이징 노출을 줄이기 위한 `VirtualLock`/`mlock`과 Android `MADV_DONTDUMP`는 최선형 처리입니다. 실행 상태를 `AEGIS_NATIVE_MEMORY`에 남깁니다. Windows 최종 실행에서는 잠금 플래그가 false였고, Android 실기기에서는 `locked=1`, `dump_excluded=1`, `smaps`의 `lo dd`를 확인했습니다. 읽기 전용은 읽기를 금지하지 않으며, `MADV_DONTDUMP`는 커널 코어 덤프만 대상으로 합니다.

네이티브 키 조각은 volatile 읽기로 재구성 후 즉시 지우고, 재구성한 키도 인증 복호화 직후 지웁니다. Windows 실행 메모리에서 두 조각이 모두 0인지 직접 확인했습니다. 원본 DLL/SO의 파일 바이트에는 키 조각이 여전히 있어 별도로 회수할 수 있습니다.

닫을 때는 영역을 쓰기 가능 상태로 전환하여 지우고 해제합니다. 한 OS 프로세스에서 한 번만 초기화하며, 종료 후 같은 프로세스의 재초기화는 거부합니다. 이 수명 제약은 Unity를 임베드하는 사용자 정의 호스트에서 별도 검토해야 합니다.

## 생성된 변경 증거

빌드마다 `GuardBuildReports/<build-id>/engine-modifications.json`과 `MetadataLoader.patch`가 생성됩니다. 이번 검증의 원본 JSON·patch는 로컬 검증 자료에 보관했습니다. 개인 경로를 포함한 원본 보고서와 Unity 소스 문맥을 포함한 생성 patch는 GitHub 소스 저장소에 넣지 않습니다. 빌드하면 사용자의 프로젝트에서 다시 생성됩니다.

| 항목 | 값 |
| --- | --- |
| Windows build ID | `6735f7ae15374cd6a99cb86c26bd16e5` |
| Android build ID | `f1cadfe0a2374e83a1db4cf94086f3d1` |
| 설치된 MetadataLoader 전후 SHA-256 | `851dc9ba32dbfad0dd7eb9c5a57aac4d30cfe3b948b5c7b42ffc149eebb95bec` |
| 수정된 MetadataLoader SHA-256, 두 플랫폼 동일 | `60cdb814202af0f537e7705dd9f098334b7eb74b29ad480f08e24345d5b92ac5` |
| GlobalMetadata 원본·복사본 SHA-256 | `e9f6e045750c88045024de76e4c766336dc73d6661562a61b5b96d061ee0c075` |
| AegisMetadata.h SHA-256 | `ab6291cc33bdb845280e58088e38d4211ed8613e900eec222024ddd61f062890` |

각 패치의 4개 hunk를 원본에 적용하면 실제 수정 파일을 재구성하는지 검증했습니다. 생성 키 헤더는 값 대신 생성 당시 해시와 정리 후 해시만 기록합니다. 출력 해시의 대상 파일은 `outputHashPath`에 명시합니다. 설치된 Unity 파일과 `GlobalMetadata.cpp`의 해시는 모두 유지됐습니다.

이 기록과 빌드 중간물·심볼·키 헤더를 게임 배포물에 넣지 마세요. 패키지에 동봉한 기록은 개발자용입니다. 엔진을 원래대로 사용하려면 보호 빌드를 사용하지 않는 별도 검증 빌드를 만들면 됩니다. 설치본을 되돌리는 패치는 필요하지 않습니다.

## 보호 범위

실제 검사에서 0.3의 읽기 전용 metadata도 Windows 외부 읽기와 Android 앱 내부 읽기로 회수됐습니다. 헤더를 지우거나 서명을 바꿔 스캐너 실패를 보호 성공으로 처리하지 않았습니다. 수정된 엔진은 메모리 수명을 개선하지만 런타임 추출을 불가능하게 하지 않습니다. 세부 결과와 미실시 항목은 `VALIDATION.md`에 있습니다.
