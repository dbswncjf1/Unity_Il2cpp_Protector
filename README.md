# AegisGuard 0.3.0

Unity **6000.3.13f1**, **Windows x64 / Android IL2CPP**용 소스 포함 UPM 패키지입니다. 명시적으로 선택한 함수명·문자열·관리 어셈블리 내장 리소스와 별도로 등록한 데이터/AssetBundle을 보호합니다. 기존 프로젝트의 모든 코드를 자동으로 바꾸는 제품이 아니며, 보호 대상 지정과 로딩 코드 변경이 필요합니다.

**프로젝트별 IL2CPP 런타임 소스 복사본**의 메타데이터 로더를 수정합니다. `global-metadata.dat` 전체를 XChaCha20-Poly1305로 인증 암호화하고 네이티브 초기화 중에 복호화합니다. 보호 대상으로 등록한 에셋은 암호화 번들로만 배포되도록 사전 의존성 검사와 실제 player archive 검사를 수행합니다.

0.3은 실제 런타임 공격 시험에서 확인된 키 노출을 줄입니다. 키 조각을 문자열 메타데이터에 넣지 않고, 리소스 로더의 장기 키 보관을 없애며, 각 동기 Read가 끝나면 임시 키와 평문 캐시를 지웁니다. 네이티브 메타데이터는 전용 읽기 전용 메모리에 보관하고 Android에서는 커널 코어 덤프 제외를 시도합니다. **프로세스 메모리 읽기·내부 후킹·배포 바이너리에서의 키 회수 자체를 차단하지는 않습니다.** 실제 재현 결과는 `VALIDATION.md`, 엔진 수정 기록은 `ENGINE_CHANGES.md`에 있습니다.

## 제공하는 기능

| 대상 | 처리 |
| --- | --- |
| 함수명 | `[GuardRename]` private 메서드를 빌드마다 임의 이름으로 변경하고 IL 호출 참조 수정 |
| IL2CPP 메타데이터 | 네이티브 로더 수정 + 전체 파일 인증 암호화, 평문 메타데이터 fallback 거부 |
| 문자열 | `[GuardStrings]` 메서드의 `ldstr`를 AES-256-CBC + HMAC-SHA256 envelope로 교체 |
| 어셈블리 내장 데이터 | 설정에 지정한 `EmbeddedResource` 암호화, `ProtectedResources.Open`으로 접근 |
| JSON/바이너리 | `GuardSource/` 파일을 인증된 암호화 컨테이너로 패킹 |
| Unity 리소스 | 명시적 LZ4 AssetBundle 생성 → 암호화 → 추가 StreamingAssets 경로로 배포 |
| 런타임 | 요청된 64 KiB 조각만 인증 후 복호화, 기본 로더는 각 Read 종료 시 키·평문 캐시 지우기 |
| 빌드 검사 | IL2CPP 릴리스 빌드, 보호 콜백 확인, 씬·Resources·Preloaded Assets 중복 참조 검사 |

## 보장 범위

**클라이언트에서 사용되는 데이터를 런타임에 절대 추출하지 못하게 만들 수는 없습니다.** 오프라인 실행에는 클라이언트가 복호화 키를 재구성해야 합니다. 이 패키지의 키 분할은 분석 비용을 높이는 난독화이며 보안 저장소가 아닙니다. 프로세스를 통제하는 공격자는 키·복호화 함수·Unity 오브젝트·GPU 데이터를 후킹하거나 덤프할 수 있습니다. 키를 회수하면 암호문 위조도 가능합니다.

이 구현은 일반적인 파일 추출, 문자열 검색, 함수명 기반 분석과 통째로 평문 번들을 올리는 로딩 경로의 노출을 줄입니다. 메타데이터의 저장 형식과 IL2CPP 네이티브 로더를 함께 변경하지만, OS 메모리 덤프 차단·안티디버깅·커널 드라이버·Frida 탐지는 제공하지 않습니다. 실행 중 IL2CPP 메타데이터, C# 문자열과 Unity 네이티브 오브젝트에는 평문이 남습니다. 버퍼 지우기는 GC·암호화 제공자·OS가 만든 복사본까지 지운다는 보장이 아닙니다.

0.2에서 회수한 메타데이터의 문자열 두 개만으로 리소스 키와 보호 에셋까지 복구됐습니다. 0.3은 이 문자열 경로를 제거하지만 키 재구성용 정수 상수는 네이티브 코드에 포함됩니다. 복호화 호출을 관찰하는 공격자는 사용 순간의 키를 얻을 수 있습니다. 키 지우기는 이미 공격자가 복사한 키를 무효화하지 않습니다.

서버 비밀키·결제/보상 검증·권한 판정은 클라이언트에 넣지 마세요. 이 패키지는 서버의 권한 검증을 대체하지 않습니다. 독립적인 암호 검토를 거친 상용 보안 제품이 아니라 프로젝트에 맞게 검증하고 확장할 수 있는 초기 구현입니다.

## GitHub에서 설치

Unity Package Manager의 **Install package from git URL**에 다음 URL을 입력합니다.

```text
https://github.com/dbswncjf1/Unity_Il2cpp_Protector.git#v0.3.0
```

저장소 루트가 UPM 패키지입니다. Windows Editor의 Unity 6000.3.13f1, 해당 플랫폼의 IL2CPP 빌드 모듈, .NET 8 SDK가 필요합니다. 아래 설정 후 **Tools > AegisGuard > Build Windows x64 / Build Android**를 사용합니다. 일반 Build 버튼은 보호 빌드를 대신하지 않습니다.

## 엔진 수정이 적용되는 순서

1. 설치된 Unity의 IL2CPP를 프로젝트 `Library/AegisGuardEngine/6000.3.13f1`에 물리 복사합니다. 검증된 캐시는 재사용합니다.
2. 이 복사본의 메타데이터 로더에 패치를 적용하고 빌드별 키를 생성합니다.
3. 빌드 프로세스의 `UNITY_IL2CPP_PATH`를 복사본으로 지정하여 선택한 플랫폼의 네이티브 코드를 컴파일합니다.
4. Windows는 player build 후 metadata를 암호화하고 배포 ZIP을 만듭니다. Android는 Gradle 패키징·서명 전에 metadata를 암호화합니다.
5. 완료·예외 정리에서 환경 변수를 복원하고 생성 키 헤더를 지웁니다. 강제 프로세스 종료 시 정리 코드 실행은 보장되지 않습니다.

Windows와 Android는 각 플랫폼으로 전환해 별도로 빌드합니다. 프로젝트별 엔진 캐시는 플랫폼 간 재사용되며, 같은 프로젝트에서의 동시 보호 빌드는 지원하지 않습니다. 전체 Unity Editor를 별도로 빌드하거나 배포하는 방식이 아닙니다. 원본 Unity 엔진·컴파일러·생성 키는 이 저장소에 포함하지 않습니다.

이 저장소는 소스와 검증 요약을 제공합니다. 기기 식별자·개인 경로가 담긴 원본 테스트 로그와 빌드별 보고서는 게시하지 않습니다. 원래의 로컬 검증 자료는 별도로 보존되어 있으며 [검증 요약](VALIDATION.md)과 [엔진 변경 기록](ENGINE_CHANGES.md)에 범위와 한계를 설명합니다. 리플렉션 제한은 0.3에 포함되지 않습니다.

## 설치

1. 패키지 폴더를 프로젝트의 `Packages/com.yun.aegisguard`로 복사하거나 Package Manager → **Install package from disk**에서 `package.json` 선택.
2. **Windows Editor**, Unity 6000.3.13f1과 Windows IL2CPP / Android Build Support, .NET 8 SDK 이상 준비. Cecil 의존성 `com.unity.nuget.mono-cecil:1.11.6`은 UPM에서 설치됩니다. 이 버전의 네이티브 도구 배치는 Windows Editor용입니다.
3. `Tools > AegisGuard > Create Configuration` 실행.
4. 생성된 프로젝트의 `ProjectSettings/AegisGuard.json` 수정. `Samples~/Basic/AegisGuard.example.json` 참고.
5. Build Profiles에서 활성 플랫폼을 Windows 또는 Android로 전환하고 빌드 씬 등록.
6. `Tools > AegisGuard > Build Windows x64` 또는 `Build Android` 실행.

0.1 설정을 이어 쓰면 `"nativeMetadataProtection": true`를 명시적으로 추가하는 것이 좋습니다. 누락된 경우에도 true로 적용합니다. false는 비교/개발용 호환 모드이며 IL2CPP 메타데이터 암호화를 하지 않습니다.

설정이 활성화되어 있으면 일반 Build 버튼은 실수로 비보호 빌드를 배포하지 않도록 중단됩니다. 설정의 `enabled:false`는 일반 Unity 빌드를 다시 허용하지만, 암호화 로더를 사용하는 코드는 키가 없어 동작하지 않습니다. 보호 로더는 Editor Play Mode용 키를 생성하지 않으므로 **Player 빌드에서 검증**하세요.

asmdef를 사용하는 게임 어셈블리는 `AegisGuard.Runtime`을 참조하고 `assemblies` 배열에 정확한 이름을 추가합니다. `.dll` 접미사는 제외합니다. 해당 어셈블리에 `[GuardRename]`/`[GuardStrings]`가 없으면 자동으로 모든 메서드를 변경하지 않습니다.

## 함수와 문자열 지정

```csharp
using AegisGuard;

[GuardRename, GuardStrings]
private static string BalanceTableId() => "balance";
```

`[GuardRename]`은 직접 호출하는 private 메서드만 지원합니다. public/internal API, virtual, override, 생성자, 프로퍼티 accessor, P/Invoke, 제네릭 메서드/제네릭 선언 타입, 콜백 특성이 붙은 메서드는 거부합니다. 이름이 `On`으로 시작하거나 알려진 Unity 이벤트 이름인 경우도 거부합니다. 타입명·필드명·직렬화 멤버명은 유지합니다.

리플렉션·`SendMessage`·`Invoke`·UnityEvent·AnimationEvent·네이티브에서 이름으로 호출하는 함수에는 `[GuardRename]`을 붙이지 마세요. 일부 문자열 참조는 자동 탐지하지만 외부 설정/문자열 조합까지 탐지하지 못합니다. `[GuardKeep]`을 메서드나 선언 타입에 붙이면 두 변환 모두 제외합니다. `link.xml`은 스트리핑 보존 규칙이며 난독화 제외 규칙이 아닙니다. 이름 기반 `link.xml` 대상도 `[GuardKeep]`으로 보존하세요.

`[GuardStrings]`는 지정된 동기 메서드 본문 안의 비어 있지 않은 문자열 리터럴만 바꿉니다. async/iterator 메서드는 거부하므로 위 예처럼 동기 helper로 분리합니다. 람다/로컬 함수·생성된 다른 메서드에는 자동 전파되지 않습니다. const 필드 메타데이터·특성 인수·직렬화 데이터·숫자/RVA 배열·타입명에는 적용하지 않습니다. 동일 문자열이 다른 곳에 있으면 그 복사본은 남습니다. 문자열 interning/참조 동일성에 의존하는 코드는 대상에서 제외하세요. 호출 때마다 복호화하므로 매 프레임의 대량 루프에는 사용하지 마세요.

## 데이터와 AssetBundle

원본 JSON/바이너리를 프로젝트 루트의 `GuardSource/`에 둡니다. Unity `Assets/` 밖이므로 원본이 플레이어에 자동 포함되지 않습니다. 설정의 `dataFiles`에 ID와 경로를 추가합니다. 원본이 들어갈 별도 복사본을 `Resources`, StreamingAssets, 씬 등에 남기지 마세요.

```csharp
var store = new GuardStore();
yield return store.Initialize();
yield return store.PrepareData("balance");
using (var stream = store.OpenData("balance"))
using (var reader = new System.IO.StreamReader(stream))
{
    var json = reader.ReadToEnd(); // 사용 후 앱에서 오래 보관하지 않기
}

yield return store.LoadBundle("characters");
var prefab = store.LoadAsset<UnityEngine.GameObject>("characters", "hero");
// 사용 종료 후:
store.Dispose(); // 로드한 오브젝트도 Unload(true)로 해제됨
```

모든 `GuardStore` 호출은 Unity 메인 스레드에서 실행하고 한 store의 번들 로딩은 순차 실행합니다. store는 로드된 오브젝트 사용 기간 동안 유지합니다. Dispose 전에 진행 중인 코루틴을 종료하고 사용하는 인스턴스도 정리하세요. `OpenData`로 받은 스트림은 호출자가 Dispose해야 합니다. 오류는 예외로 전달되며 평문 경로로 fallback하지 않습니다. 중첩 코루틴 예외를 상위 코드에서 일괄 처리하려면 `Tests~/UnityFixture/PlayerProbe.cs`의 iterator 실행 예제를 참고하세요.

번들은 LZ4로 만들고 의존 번들을 먼저 로딩합니다. 실제 mount와 `LoadAsset`은 동기식이므로 큰 번들은 로딩 화면에서 사용하세요. 암호화 원본을 persistentDataPath에 복사하므로 디스크 공간이 추가로 필요합니다. Android APK 내부 경로는 `UnityWebRequest` + `DownloadHandlerFile`로 **암호문만** 복사합니다. 캐시는 빌드 키의 해시별로 나뉘며 자동 삭제하지 않습니다. 사용 중이지 않은 이전 빌드 캐시는 앱의 업데이트 정책에 맞춰 정리하세요. 손상된 현재 캐시는 해당 파일을 삭제한 뒤 다시 준비하면 원본에서 복구됩니다.

0.3 기본 로더는 `ProtectedReadStream(Stream, Func<byte[]>, context)`를 사용합니다. 공급자는 호출마다 새로 소유권을 넘길 32바이트 배열을 반환해야 하며, 스트림이 이를 지웁니다. 키를 오래 보관하는 클로저를 사용하지 마세요. 각 Read에서 키를 다시 파생하고 평문 캐시를 지우므로 작은 반복 읽기의 CPU·할당 비용은 늘어납니다. 기존 `byte[]` 생성자는 호환성을 위해 유지하며, 그 모드의 파생 키·캐시는 Dispose까지 남고 호출자의 원본 키는 지우지 않습니다. 반환된 데이터와 Unity 오브젝트의 수명은 호출자가 관리합니다.

보호 리소스를 씬/Resources/Preloaded Assets에서 직접 참조하면 중복 평문 포함을 막기 위해 빌드를 거부합니다. 검사 대상에는 의존 에셋도 포함되므로 공유 Material/Texture가 발견될 수 있습니다. Addressables, 커스텀 빌드 플러그인, 원본 파일의 다른 이름 복사본까지 검사하지는 않습니다. Addressables provider와 씬 번들 로더는 이 버전에 포함되지 않으며 기존 Addressables 번들을 자동 암호화하지 않습니다. 보호할 콘텐츠는 이 패키지의 명시적 번들 목록과 로더로 옮겨야 합니다.

## 어셈블리 내장 리소스

설정 예: `"embeddedResources": ["Game.Logic/Game.Logic.Table.bin"]`. 어셈블리는 `assemblies`에도 포함되어야 합니다. `ProtectedResources.Open(typeof(YourType).Assembly, "Game.Logic.Table.bin")`이 인증된 스트림을 반환합니다. 기존 `GetManifestResourceStream`/`ResourceManager`는 암호문을 받으므로 로딩 코드를 변경해야 합니다. 리소스 이름 자체는 유지됩니다. 서명된 DLL은 재서명 통합 없이 바꾸지 않도록 거부합니다. 외부 SDK/엔진 DLL은 선택하지 마세요.

## 빌드와 배포

### 네이티브 메타데이터 보호

첫 빌드에서 설치된 `Editor/Data/il2cpp`를 프로젝트 `Library/AegisGuardEngine/6000.3.13f1`로 물리 복사하고, 해당 Unity 버전의 개발 도구 디렉터리 구조를 구성합니다. 빌드 프로세스 범위에서만 `UNITY_IL2CPP_PATH`를 지정하고 완료 후 이전 값으로 복원합니다. 최초 준비에는 수백 MB 이상의 디스크와 시간이 필요합니다. 다른 프로젝트와 엔진 설치본의 소스는 건드리지 않습니다.

원본 `MetadataLoader.cpp`의 SHA-256이 검증된 값과 다르면 패치를 중단합니다. Windows에서는 플레이어 빌드 후 배포용 metadata를 암호화하고, Android에서는 Gradle 생성 후 패키징·서명 전에 암호화합니다. 산출물 안에서 암호화 헤더와 커스텀 네이티브 로더 표식을 확인하지 못하면 빌드 성공으로 처리하지 않습니다. Android의 각 포함 ABI에 로더 표식을 검사합니다.

잘못된 메타데이터는 관리 코드 실행 전에 로그를 남기고 네이티브 종료 코드 86으로 종료합니다. 평문 파일 fallback과 인증 실패 후 계속 실행은 허용하지 않습니다.

메타데이터 키는 매 빌드 생성하며 관리 어셈블리 키와 분리합니다. 키는 stdin으로 로컬 패킹 도구에 전달하며 명령행이나 보고서에 기록하지 않습니다. 네이티브 소스에 잠시 생성되는 키 헤더는 종료 시 빌드 불가 표식으로 덮어씁니다. 컴파일 중간물과 네이티브 바이너리에서의 키 회수 가능성은 남으므로 Library/심볼/소스 백업을 배포하지 마세요. 원문 metadata도 빌드 중간물에는 존재합니다.

네이티브 복호화는 [Monocypher 4.0.2](https://github.com/LoupVaillant/Monocypher/tree/4.0.2)를 사용하고, 로컬 .NET 8 패킹 도구와 상호 동작합니다. 태그 확인 실패·잘못된 길이·평문 입력은 로더가 거부합니다. 인증된 평문은 디스크 임시 파일 없이 네이티브 메모리에 보관되고 IL2CPP가 종료할 때 해제됩니다. 따라서 정적 도구 차단 결과를 런타임 메모리 덤프 불가능으로 해석해서는 안 됩니다.

0.3은 Windows `VirtualAlloc` / Android `mmap`으로 분리한 메모리를 인증 후 읽기 전용으로 바꿉니다. `VirtualLock`/`mlock`은 페이징 노출을 줄이기 위한 최선형 처리이며 실패할 수 있습니다. Android `MADV_DONTDUMP`는 커널 코어 덤프 대상에서 제외하는 설정입니다. 어느 것도 권한 있는 메모리 읽기나 앱 내부 후킹을 막지 않습니다. 실행 시 `AEGIS_NATIVE_MEMORY`가 실제 적용 상태를 기록합니다. 네이티브 키 조각은 사용 후 지우며 OS 프로세스당 메타데이터 초기화는 한 번만 지원합니다. 같은 프로세스에서 IL2CPP 종료 후 재초기화하는 호스트에는 적합하지 않습니다.

빌드마다 `GuardBuildReports/<build-id>/engine-modifications.json` 및 `MetadataLoader.patch`를 생성합니다. 원본/수정 파일 해시, 적용 경로, 보조 파일, 검사 대상 산출물과 키 헤더 정리 상태를 기록하고 키 바이트는 기록하지 않습니다. 이 개발용 기록은 게임 배포물에 포함하지 마세요.

### 보호 에셋 누출 검사

기존 씬/Resources/Preloaded Assets 검사에 더해, 완성된 `BuildReport.packedAssets`에서 보호 의존 에셋이 평문 player archive로 들어갔는지 검사합니다. `GuardSource` 데이터와 바이트가 완전히 같은 StreamingAssets 복사본도 이름과 무관하게 거부합니다. 형식을 바꾼 복사본, 임의 타사 빌드 플러그인의 별도 출력, 이미지를 재인코딩한 복사본까지 탐지하는 DLP는 아닙니다.

게임의 보호 대상 텍스처/메시/프리팹/데이터를 모두 `bundles`/`dataFiles`에 등록하고 이 로더로 접근해야 합니다. Unity 기본 리소스와 의도적으로 평문으로 남긴 씬/에셋은 표준 도구가 읽을 수 있습니다. 보호 대상을 일반 씬에 직접 연결하는 대신 시작 씬에서 암호화 번들을 로딩하세요.

빌드는 새 랜덤 키로 실행되며 IL2CPP 변환 전의 **중간 player DLL만** 수정합니다. Editor DLL과 원본 C#을 바꾸지 않습니다. clean build를 사용하므로 증분 빌드보다 느립니다. AssetBundle manifest를 player build에 전달해 번들에서 필요한 타입의 스트리핑을 방지합니다. 추가 리플렉션/AOT 타입 보존은 게임 프로젝트가 관리해야 합니다.

`GuardBuildReports/<build-id>/`에 변경 개수와 함수명 매핑을 기록합니다. **이 폴더와 Library, 원본 소스, `*_BackUpThisFolder_ButDontShipItWithYourGame`, PDB, symbols.zip은 배포에서 제외**하세요. Unity가 생성하는 심볼과 백업 폴더를 이 패키지가 임의로 삭제하지 않습니다. 원본·중간 파일은 빌드 머신에서 존재하므로 CI 접근 통제도 별도로 필요합니다. 자체 임시 번들/키 버퍼는 finally에서 정리하지만 Unity 캐시와 강제 종료 시 잔여 파일까지 완전 삭제하지 않습니다.

Windows에는 `Game.protected.zip` 형태의 배포용 ZIP도 자동 생성합니다. **빌드 상위 폴더 전체가 아니라 이 ZIP을 배포**하세요. 실행 파일, 루트 DLL, CrashHandler, `<이름>_Data`, D3D12를 포함하고 소스 백업과 디버그 심볼을 제외합니다. 비표준 런처나 별도 루트 데이터 폴더를 쓰는 프로젝트는 `GuardDistribution.cs`의 포함 목록을 검토하고 추가해야 합니다. Android는 최종 APK/AAB만 배포하고 별도 symbols.zip은 제외합니다.

Android는 Player Settings의 keystore/SDK/최소 API/아키텍처를 사용합니다. 출력 확장자가 `.aab`이면 App Bundle, `.apk`이면 APK로 빌드합니다. 키스토어는 패키지가 생성하거나 변경하지 않습니다. 빌드 중 IL2CPP 설정을 적용하고 끝나면 원래 설정을 복원합니다. Android Gradle export 모드는 이 빌드 경로에서 사용하지 않습니다.

CI 예시(PowerShell):

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Unity.exe' `
  -batchmode -nographics -quit -projectPath 'D:\Game' -buildTarget Win64 `
  -executeMethod AegisGuard.Editor.GuardBuild.Windows -logFile 'D:\Logs\guard-windows.log'

& 'C:\Program Files\Unity\Hub\Editor\6000.3.13f1\Editor\Unity.exe' `
  -batchmode -nographics -quit -projectPath 'D:\Game' -buildTarget Android `
  -executeMethod AegisGuard.Editor.GuardBuild.Android -logFile 'D:\Logs\guard-android.log'
```

## AI/자동화 도구 안내문

빌드의 `StreamingAssets/AegisGuard/CONTENT_AUTHORIZATION_NOTICE.txt`에 한·영 권한 안내가 포함됩니다. 무단 추출·외부 전송·비공개 콘텐츠의 무단 AI 학습을 거절하도록 요청하며, 허가된 점검과 법적으로 허용된 이용은 구분합니다. 이 파일은 출처·권한 표시일 뿐이며 AI가 반드시 읽거나 따르도록 강제하지 못합니다. 패키지 자체의 MIT 라이선스나 타사 라이선스를 변경하지 않습니다.

## 테스트 재현

`.NET 8+`에서 `dotnet run --project "Tests~/Core/CoreTests.csproj" -c Release`로 순수 암호화/스트림 검사를 실행합니다. Unity 통합 검사는 `Tests~/Run-UnityValidation.ps1`을 참고하세요. 테스트는 새 프로젝트를 생성하며 사용자 프로젝트를 수정하지 않습니다. 검증 결과와 검증하지 못한 항목은 동봉한 `VALIDATION.md`에 기록합니다.

USB 디버깅을 승인한 실기기에서는 `Tests~/Run-AndroidDeviceTest.ps1 -Device <adb-serial> -Apk <fixture-apk> -LogDirectory <new-log-directory>`로 fixture를 설치·실행할 수 있습니다. 지정한 테스트 앱 UID의 로그만 수집하고 시스템 로그를 지우지 않습니다. 테스트 앱은 기기에 남으며 필요 없을 때 제거할 수 있습니다.

## 구현 근거

- [Unity 6000.3 IPostBuildPlayerScriptDLLs](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Build.IPostBuildPlayerScriptDLLs.html): IL2CPP 변환 전 player assembly 처리 지점.
- [BuildPlayerContext.AddAdditionalPathToStreamingAssets](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Build.BuildPlayerContext.AddAdditionalPathToStreamingAssets.html): 원본 Assets를 변경하지 않고 암호화 산출물 포함.
- [AssetBundle.LoadFromStream](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/AssetBundle.LoadFromStream.html): seek 가능한 스트림, EOF 처리, 네이티브 스레드 접근, 번들 unload까지 스트림 수명 유지.
- [Application.streamingAssetsPath](https://docs.unity3d.com/6000.3/Documentation/ScriptReference/Application-streamingAssetsPath.html): Android 패키지 내부 경로 접근.
- [Unity linker and managed stripping](https://docs.unity.com/en-us/engine/6000.3/manual/programming-environment/script-compilation/managed-code-stripping/unity-linker): 이름 보존과 스트리핑 보존을 구분해야 하는 이유.
