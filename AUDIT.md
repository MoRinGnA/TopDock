# TopDock 감사 목록 (2026-10-13)

"완성 전에 또 고칠 것"을 고르기 위한 목록. 코드를 전부 읽고(앱 9.6k줄 + 로직/테스트),
성능 항목은 화면 밖 하네스로 재서 수치를 붙였다. **아직 코드는 고치지 않았다.**

- 기준선: `dotnet build -c Release` 경고 0·오류 0, `dotnet test tests/TopDock.Tests/TopDock.Tests.csproj`
  **137개 통과**(exit 0). 둘 다 이 감사 시점에 다시 돌린 값이다.
- 측정 하네스: `%TEMP%\ringprobe` (VolumeRing 렌더 비용), 이전 턴의 `audioprobe`(CPU), `lightprobe`(빛 위상).
- 작업 트리는 지금 커밋 안 된 변경 6개(빔 속도·Still 얼굴) 위에 있다.

---

## 진행 상황 (2026-10-13 갱신)

위 1~11은 이제 작업 트리에서 고쳐졌고, 같은 작업에서 12~18을 새로 찾아 고쳤다.
기준선은 그대로: `dotnet build -c Release` 경고 0·오류 0, 테스트 **137개 통과**
(다중 모니터 작업에서 10개, 핫키 회귀 테스트에서 9개가 늘어 지금은 **156개**).

### 1~11 처리 결과

- **1** 스티키 알림 — 턴의 `finally`에서 `ClearTurnNotices()`(Assistant+Tool)를 부르고, 생성 쪽에도
  "턴이 살아 있을 때만"(`_assistantBusy`) 조건을 넣었다.
- **2** 볼륨 링 할당 — `BorderBeam.PenFor`와 같은 양자화 펜 캐시 + `StreamGeometry` 한 줄기
  (`ringprobe`로 전후를 쟀다).
- **3** COM 폴링 60ms → 250ms(2.1ms/s). 즉각성은 콜백이 책임지므로 거래는 그대로.
- **4** `ThinkingOrb` 791줄 삭제 — 쓰이던 `OrbKind`만 `Controls/OrbKind.cs`로 옮겼다.
- **5** 종료 정리 — 인박스 핫키 해제, 타이머·트레이 아이콘·도구 HttpClient 정리,
  닫히는 중의 배경 이벤트는 `RunOnUi`가 버린다. 전역 핸들러는 로그만 남기고 모달창은 DEBUG에서만.
- **6** 루트 `dotnet test` — csproj 타깃이 "테스트가 없다"고 소리 내어 막는다.
- **7·8** `AssistantTools`·`AssistantService`·`LyricsService`의 HttpClient/CTS 정리(캐시 읽기도 비동기로).
- **9** 죽은 `Assistant_NotchClick` 제거.
- **10** `SetBars`가 바늘이 안 보이면 조기 반환(30fps 무효화 중단).
- **11** 아래 12에서 원인을 규명해 고쳤다.

### 이번에 새로 고친 것 (12~18)

- **12. 대기 비용의 정체는 '무한 반복 연출'이었다.** `frameprobe`로 실제 합성 프레임을 세어 보니,
  무한 반복 애니메이션(눈의 호흡·충전 맥동·AI 상태 점)이 기본값으로 **화면 주사율(초당 약 88장)** 만큼
  돌며, 화면 전체를 덮는 창을 그만큼 다시 합성하고 있었다. 빛의 시계를 세우면 245 → 56 ms/s,
  호흡·맥동을 끄면 245 → 71 ms/s. 그래서 반복 연출에만 20fps를 걸었다(`Tokens.IdlePulseFps`) —
  1.35초 동안 3.5% 움직이는 연출이라 20장이면 매끄럽다. 결과: **245 → 73~143 ms/s**(같은 하네스,
  기계가 조용할 때 73, 바쁠 때 143), 실측 프레임 수는 그대로다.
  (참고: 창 크기를 700×400으로 줄여도 비용은 거의 그대로였다 — 면적이 아니라 '몇 장을 그리는가'였다.)
- **13. 설정을 적용하면 Ctrl+Shift+V가 죽었다.** `ApplySettings`가 클립보드 핫키를 내리고 비서 핫키만
  다시 걸어서, 한 번 적용하면 복사 기록 단축기가 영영 안 들었다. 이제 셋을 함께 내리고 함께 건다
  (`RegisterGlobalHotkeys` — 시작 경로와 설정 경로가 같은 함수를 쓴다). 검증: 실행 중 세 핫키가 모두
  TopDock 소유임을 확인(다른 프로세스가 `RegisterHotKey`에 실패). — 이후 이 검증은 손이 아니라
  테스트로 굳혔다(아래 20).
- **14. 오디오 COM 콜백이 아직 동기 `Dispatcher.Invoke`**였다 — 종료 중에 도착하면 예외 → 모달창.
  `RunOnUi`로 바꿨다.
- **15. 정지 시 바늘 초기화가 죽은 호출**이었다 — `BarsVisible = false` 뒤의 `SetBars(0,0,0)`은
  조기 반환에 먹혀 아무 일도 하지 않았다. `ResetBars()`를 따로 두고 그걸 부른다.
- **16. README가 없는 기능을 광고**했다 — 삭제된 Thinking Orb(9종) 설명을 현재의 얼굴(눈·표정)로 바꾸고,
  최근 완료에 이번 수정들을 반영.
- **17. `Services/ImplementationFinished.txt`** — 구현 진행 표시용 임시 파일(UTF-16, 2줄). 지웠다.
- **18. 트레이 아이콘이 윈도우 기본 아이콘**이었다 — 노치 실루엣을 그린 `Assets/topdock.ico`(16~128px,
  DIB 엔트리)를 만들어 실행 파일 아이콘과 트레이 아이콘이 같은 그림을 쓰게 했다. 검증: 빌드된 exe에서
  아이콘을 추출해 픽셀 확인(투명 모서리·`#0C0C0E` 알약·`#F0F3F8` 눈).
  (System.Drawing은 PNG로 압축된 ICO 엔트리를 잘못 읽어 색이 깨진다 — DIB로 써야 한다.)

### 이번 작업 — 다중 모니터 대응 (19)

- **19. 창이 주 화면 하나에 못 박혀 있었다.** `UpdatePosition`이 `Left = 0` + `SystemParameters.PrimaryScreenWidth`로
  주 화면을 덮고 있었다. 그 둘은 주 모니터가 가상 화면의 (0,0)에 있을 때만 맞는 값이라, 주 모니터를 오른쪽이나
  아래에 두고 쓰는 배치에서는 노치가 엉뚱한 화면에 앉았다 — "어느 화면에 앉을지 고르는 것"과는 별개의 버그였다.
  이제 고른 모니터의 실제 사각형(장치 픽셀)을 `SetWindowPos`로 그대로 씌우고, DIP로 적힌 상단 여백만 그
  모니터의 배율로 환산한다(창이 덮는 좌표 공간과 모니터 목록의 좌표 공간이 같아 배율이 섞여도 어긋나지 않는다).
- **어느 화면에 앉을지는 순수 로직**(`TopDock.Logic/MonitorLayout.cs`)이 정한다 — 설정이 고른 모니터가 붙어
  있으면 그것, 없으면 주 모니터, 주 모니터 표시가 없는 배치(원격 세션 등)에서는 첫 번째. 고른 모니터가
  사라졌으면 주 화면으로 물러서고 로그에 한 줄 남긴다. 선택 규칙은 합성 배치(주 모니터가 오른쪽·왼쪽 음수
  좌표·세로 쌓기·사라진 모니터·모니터 없음·표기 흔들림)로 테스트가 잠근다(10개).
- **설정 → 적용 → 실제 재배치까지 한 경로**를 쓴다. 설정 창의 '표시할 모니터' 목록은 지금 붙어 있는 화면으로
  채워지고(`주 모니터 (자동)` + 디스플레이별 한 줄), 저장된 모니터가 없으면 `주 모니터 (자동)`을 보여 준다.
- **디스플레이 변경을 따라간다** — `WM_DISPLAYCHANGE`에서 자리를 다시 계산한다(모니터 추가·제거·해상도·배율).
  복사 기록 패널의 좌우 경계도 주 화면 폭이 아니라 창 폭(= 노치가 앉은 모니터)을 쓴다.

검증(2026-10-13, 이 개발 PC = 단일 2880×1800 @175%, 앱의 DPI 인식 수준은 SYSTEM_AWARE — 실행 중인 프로세스에
`GetProcessDpiAwareness`로 확인):

- **창이 모니터를 정확히 덮는가** — 실행 중 창의 `GetWindowRect`를 읽는 하네스(`%TEMP%\monprobe\dockprobe.ps1`)로
  세 경우를 실측: `primary`·`\\.\DISPLAY1`·`\\.\DISPLAY99`(없는 모니터) 모두 창 사각형 = 모니터 사각형 + 상단 여백.
- **기본값에서 자리가 바뀌지 않았는가** — 임시 로그로 예전 식의 값을 함께 찍어 비교: `legacy dip=1645.714 × scale 1.75
  = 2880.0`(= 지금 창 폭), 여백 `4 dip × 1.75 = 7`장치 px(= 지금 위쪽 오프셋). 즉 단일 모니터에서는 예전과 같은 자리다.
- **없는 모니터를 지정하면** `[WARN] Dock monitor '...' is not connected - using \\.\DISPLAY1` 한 줄만 남기고
  제자리에 앉았다(창은 여전히 모니터를 정확히 덮었다).
- **설정 창의 목록·저장 경로** — 빌드된 `TopDock.dll`을 참조하는 화면 밖 하네스(`%TEMP%\uiprobe`)로 확인:
  목록이 `[0] '주 모니터 (자동)'` + `[1] '디스플레이 1 · 2880×1800  (주 모니터)'`로 채워지고, 1번을 골라
  적용하면 `\\.\DISPLAY1`이, 0번(자동)을 고르면 `primary`가 디스크의 설정 파일에 저장됐다(다시 읽어 확인).
- **하지 못한 것** — 이 기계에는 모니터가 하나뿐이라 실제 다중 모니터 배치(다른 모니터에 앉기·배율이 다른
  모니터로 이동·실제로 모니터를 뽑기)는 관측하지 못했다. 그 부분은 합성 배치 테스트와 좌표 공간 일치로만
  뒷받침된다. 또 이 세션에서는 화면 캡처가 살아 있지 않아(`CopyFromScreen`이 정지 프레임을 돌려주고,
  `PrintWindow`는 뒤 화면을 돌려준다 — 자홍색 폼으로 직접 확인) 노치를 픽셀로 검증하지 않았다.
- 앱은 PER_MONITOR_AWARE가 아니라 SYSTEM_AWARE다(이번 작업 이전부터 그랬다). 그래서 배율이 다른 두 번째
  모니터에서는 Windows가 창을 비트맵 확대한다(그 화면에서 흐릿해진다). **위치와 크기는** 두 좌표가 같은 공간에서
  오므로 맞지만, 그 모니터에서 선명하게 그리려면 per-monitor 인식이 별도로 필요하다 — 이번 작업의 범위 밖.

### 핫키 회귀 테스트 (20)

- **20. 13번은 '고쳐 놓았다'로는 부족했다 — 적용할 때마다 자동으로 확인해야 한다.** 그 회귀는 화면에
  아무 흔적을 남기지 않아서(단축키가 조용히 안 들릴 뿐), 다시 깨져도 코드를 읽는 사람만 알 수 있었다.
  이제 두 겹으로 막는다:
  - **앱** — `RegisterGlobalHotkeys`가 '이번에 각 자리가 실제로 걸렸는가'(OS가 준 답)를 돌려주고,
    `ApplySettings`가 시작 시점의 것과 견주어 죽은 자리를 `[ERROR] Global hotkey lost after settings apply`로
    남긴다. 판단은 UI 밖 순수 규칙 `TopDock.Logic/HotkeyPlan.cs`가 한다 — 창과 테스트가 같은 규칙을 쓴다.
  - **테스트** — `tests/TopDock.Tests/HotkeyRegressionTests.cs`가 진짜 앱을 띄우고(`--selftest-hotkeys`,
    프로토콜은 `MainWindow.SelfTest.cs`) 설정 적용을 한 번 돌린 뒤, **다른 프로세스에서** `RegisterHotKey`를
    시도해 적용 전/뒤의 생존을 판정한다(같은 조합이 잡히면 아무도 — 우리 앱도 — 그걸 쥐고 있지 않다).
    판정을 앱이 아니라 밖에서 하는 이유는, 앱이 자기 핫키를 스스로 채점하면 "살아 있다"의 근거가 앱 자신이
    되기 때문이다.
- **검증(2026-10-13, 이 개발 PC)** — 정상 상태에서 새 테스트를 포함해 **156개 통과**(exit 0),
  `dotnet build -c Release` 경고 0·오류 0. 그리고 **테스트가 진짜로 회귀를 잡는지 확인**했다:
  적용 경로에 옛 버그(셋을 내리고 비서만 다시 걸기)를 일부러 되살리니
  `적용 전: Ctrl+Shift+V=살아 있음 … 적용 뒤: Ctrl+Shift+V=죽음 → lost after apply: Clipboard`로
  **실패**했다(테스트가 공허하지 않다는 증거). 같은 상태에서 앱 로그에도
  `[ERROR] Global hotkey lost after settings apply — lost after apply: Clipboard`가 남는 것을 확인했고,
  되돌린 뒤에는 그 줄이 없다(거짓 경보 없음). 앱을 두 번 되살려 검증한 뒤 원래 코드로 되돌렸다.
- **남은 한계** — 이 테스트는 Windows + 대화형 데스크톱을 요구하고, **실행 중인 TopDock이 있으면 실패한다**
  (둘이 같은 전역 핫키를 쥘 수 없고, 그때는 '조용한 등록 실패'와 회귀를 구별할 수 없다 — 그래서 통과로
  넘기지 않고 원인을 말하며 멈춘다). 또 테스트가 띄운 앱은 사용자의 실제 설정 파일과 로그를 그대로 쓰므로
  (설정을 새로 만들 뿐 바꾸지는 않는다), 그 실행 동안 노치가 잠깐 화면에 뜨고 로그에 한 인스턴스가 남는다.

### 아직 남은 것

- **집중 시간 위젯은 만들지 않기로 했다**(2026-10-13 결정) — 로드맵에서 내렸다. 남은 것은 아래 둘뿐이다.
- **per-monitor DPI** — 창이 SYSTEM_AWARE라 배율이 다른 모니터에서는 위치·크기만 맞고 그림이 비트맵 확대로 흐려진다(README '여러 모니터'에 적어 두었다).
- 12번은 '몇 장을 그리는가'를 줄인 것이라, 남은 70~140 ms/s는 같은 창에서 한 장 그리는 값이다
  (창을 숨기면 27~95 ms/s). 더 줄이려면 노치를 별도 창으로 떼는 구조 변경이 필요하다.

---

## P1 — 지금 고칠 값어치가 있는 것

### 1. 도구/생각 알림이 턴이 끝나도 걷히지 않아 노치가 붙잡힌다

**증상.** 비서가 도는 중에 Esc(또는 ✕)로 화면을 닫으면, 그때 실행 중이던 도구가 끝나면서
노치에 `AI · 생각 중…`(또는 `실행 · …`)이 **스티키**로 뜬다. 요청은 이미 취소됐고 이 알림을
걷을 코드는 어디에도 없어서, 노치는 그 카드에 붙잡힌 채 남는다. 알림이 떠 있는 동안
`DockSignals.NoticeActive`가 계속 참이라 마우스를 올리면 알림 카드가 펼쳐지고, 대기·미디어
화면으로 돌아가지 못한다. 사용자가 직접 걷을 방법도 없다(노치 클릭은 인박스/기록만 연다).

**근거.**
- 스티키 생성: `MainWindow.Notifications.cs:387` `ShowThinkingNotice()`, `:394` `ShowToolNotice()`
  — 둘 다 `duration: null`(스티키)로 띄운다.
- 생성 지점: `MainWindow.xaml.cs:208`, `:218` — 도구 시작/끝 이벤트 핸들러가
  `Dispatcher.BeginInvoke`로 **나중에** 실행된다.
- 걷는 곳: `OpenAssistant()`(`MainWindow.Assistant.cs:46`)와 취소 경로의
  `ClearAssistantNotice()`(`MainWindow.Assistant.cs:175`)뿐. **정상 종료·오류 경로에는 없다.**
- 도구는 대부분 CancellationToken을 보지 않는다(`AssistantTools`: `get_weather`만 확인) —
  취소해도 도구는 계속 돌다가 `ToolFinished`를 쏜다.

**고치는 방향.** 턴이 끝나는 자리(finally)에서 그 턴이 띄운 알림을 함께 걷는다.
`ClearAssistantNotice()`는 `NoticeSource.Assistant`만 보므로, 도구가 띄운 것까지 걷는
`ClearTurnNotices()`(Assistant + Tool)를 하나 두고 `SendAssistantMessageAsync`의 finally에서 부른다.
`ShowToolNotice`/`ShowThinkingNotice`는 `_assistantBusy`가 아니라 **화면 모양**만 보고
판단하고 있으니, 생성 쪽에도 "턴이 살아 있을 때만"이라는 조건을 같이 넣는 편이 안전하다.

**크기.** 작음(핸들러 한 곳 + 새 메서드 하나).

### 2. 볼륨 링이 프레임마다 108 KB를 새로 할당한다 (측정)

**증상.** 재생 중 30fps로 도는 `VolumeRing`이 **렌더 1회당 107,906 B 할당 · 0.39 ms**를 쓴다.
24×24 픽셀짜리 계기 하나가 30fps에서 **3.24 MB/s 쓰레기**와 **11.7 ms/s CPU**를 만든다
(호만 그릴 때도 107,688 B — 비용은 전부 호를 긋는 루프다).

**측정 방법.** `%TEMP%\ringprobe` — 앱의 `VolumeRing`을 그대로 화면 밖 창에 올리고,
`SetBars → 렌더 패스`를 300번 돌리며 `OnRender` 안에서 `GC.GetAllocatedBytesForCurrentThread()`와
`Stopwatch`를 쟀다(워밍업 5회, `-c Release`). 원시 출력:

```
렌더 횟수        : 300
1회당 할당       : 107,906 B
1회당 시간       : 0.392 ms
30fps 환산 할당  : 3.24 MB/s
30fps 환산 시간  : 11.75 ms/s
  그중 호만      : 107,688 B, 0.605 ms
```

**근거(코드).** `Controls/VolumeRing.xaml.cs:207` `MakePen`이 **호출마다** 새
`SolidColorBrush` + `Pen`을 만들어 동결한다. `DrawArc`는 세그먼트(≈2.4°)마다 두 번
(`:152` 블룸, `:156` 코어) 부른다 — 359° 기준 세그먼트 약 150개 × 2 = **프레임당 펜 300개**.

**대조.** 같은 저장소의 `BorderBeam.PenFor`(`Controls/BorderBeam.cs:304`)는
`(알파 6bit, 색 5bit×3, 굵기 0.25px)` 양자화 키로 펜을 **캐시**한다. 두 컨트롤이 각자 다른
방식을 쓰고 있어서, 한쪽에서 배운 교훈이 다른 쪽에 없다.

**고치는 방향.** `BorderBeam`처럼 굵기·알파를 양자화한 펜 캐시를 두거나, `DrawArc`를
`StreamGeometry` 한 줄기로 만들어 펜 하나로 긋는다(같은 문제를 `BorderBeam`이 이미 그렇게 풀었다 —
`StrokeRingRun`, `Controls/BorderBeam.cs:472`). 부수 효과로 조각 사이 이음매(둥근 캡이 겹쳐 보이는 자리)도 사라진다.

**크기.** 중간(렌더 함수 재작성) / 작음(펜 캐시만).

---

## P2 — 값어치는 있지만 급하지 않음

### 3. `AudioService`의 60ms COM 폴링 — 9.4 ms/s

`Services/AudioService.cs:118` — `GetMasterVolumeLevelScalar` + `GetMute`를 초당 16.7회.
이전 턴에서 **9.4 ms/s**(2.1 ms/s @240ms)로 측정됐다. 이미 `IAudioEndpointVolumeCallback`으로
볼륨 변경을 받고 있어서 이 폴링은 **보험**이다(콜백을 놓친 장치 교체 감지용).
줄이면 볼륨 HUD가 최대 주기만큼 늦게 뜨는 거래가 생겨 결정이 필요했던 항목 — 그대로 남아 있다.

### 4. `ThinkingOrb` 791줄이 죽은 코드다

`Controls/ThinkingOrb.xaml(.cs)`(785 + 6줄)는 어디서도 인스턴스화되지 않는다(`grep` 결과 자기
파일뿐). 살아 쓰는 것은 같은 파일에 있는 `enum OrbKind`(`:13`)뿐이다 — 얼굴이 `AiFace`로
바뀌면서 포팅한 60fps 오브 렌더러가 통째로 남았다.
**고치는 방향.** `OrbKind`를 자기 파일로 옮기고 두 파일을 지운다(또는 그대로 둘 이유가 있다면
주석으로 남긴다). 지우면 앱 코드(11,193줄)가 약 7% 줄고 "왜 두 개의 얼굴 엔진이 있지"라는 질문이 사라진다.

### 5. 앱을 닫을 때 정리 누락 + 종료 중 예외가 모달 오류창으로 뜬다

- `MainWindow.xaml.cs:455` — `HOTKEY_ID_ASSISTANT`, `HOTKEY_ID_CLIPBOARD`만 해제하고
  **`HOTKEY_ID_INBOX`(Ctrl+Shift+N)는 해제하지 않는다.**
- 같은 `MainWindow_Closed`(:441)에서 `_notificationTimer`, `_clipboardToastTimer`,
  `_assistantDoneTimer`, `_chargingBeamTimer`, 트레이 아이콘(`_trayIcon`)은 정리하지 않는다
  (디스패처가 죽으면 타이머는 같이 죽지만, 트레이 아이콘은 명시적 `Dispose`가 없다).
- 배경 이벤트의 `Dispatcher.Invoke`(Media/Notification/Battery/AudioService 콜백 → UI)는
  **디스패처가 종료된  뒤** 불리면 예외를 던진다. 그 예외는 `App.xaml.cs:16`(UI)·`:25`(백그라운드)의 전역 핸들러로 가서
  **모달 오류창**을 띄우고, 그 뒤 앱이 죽는다 — 즉 "닫을 때 가끔 오류창이 뜨는" 형태.
**고치는 방향.** 종료 플래그를 두고 종료 중에는 `InvokeAsync`로 넘기며, 종료 시
Assistant/Tools/HttpClient까지 `Dispose`한다. 전역 핸들러는 조용히 로그로 남기고
(사용자에게 보여줄 오류는 비서 화면이 이미 한다) 모달창은 디버그 빌드에서만 띄운다.

### 6. 루트에서 `dotnet test`는 테스트 0개를 돌리고 성공을 보고한다

저장소에 `.sln`이 없고 루트에 `TopDock.csproj`(테스트 없음)가 있어서, 루트 `dotnet test -c Release`는
`exit 0`과 함께 **아무 테스트도 실행하지 않는다**(이번 감사에서 실제로 그렇게 나왔다 —
`dotnet test tests/TopDock.Tests/TopDock.Tests.csproj`로 돌려야 137개가 돈다).
CI는 경로를 명시해 두었으니(`.github/workflows/build.yml`) CI는 안전하고, 사람이 속는 함정이다.
**고치는 방향.** `.sln`을 두거나 루트에 `Directory.Build.props`로 테스트 진입점을 고정한다.

---

## P3 — 위생 (틈날 때)

7. **HttpClient 미해제 — `AssistantTools`가 `WeatherService`·`YouTubeSearchService`를 소유하고
   `Dispose`가 없다**(`Services/Assistant/AssistantTools.cs:23-24`). `MainWindow_Closed`도
   `_assistant`/`_assistantTools`를 정리하지 않아 `AiClientService`의 `HttpClient`가 남는다.
8. **`LyricsService._currentCts`** — 매 조회마다 새 CTS를 만들며 이전 것을 취소만 하고
   **Dispose하지 않는다**(`Services/LyricsService.cs:92-94`). 그리고 캐시 조회
   (`:378 File.ReadAllText`)는 **UI 스레드에서 동기로** 돈다(첫 `await` 앞).
9. **`Assistant_NotchClick`**(`MainWindow.Assistant.cs:271`) — 본문이 빈 채 XAML에 연결되지도 않은
   죽은 핸들러. 주석만 남아 있다.
10. **바늘이 안 보일 때도 30fps로 링을 다시 그린다** — `VolumeRing.BarsVisible = false`인데도
    `SetBars`가 `InvalidateVisual`을 부른다(`MainWindow.Views.cs:518-522`). 상태 점(배터리·AI)이
    자리를 쓰는 동안에도 렌더가 돈다. 위 2번을 고치면 이 비용도 함께 사라진다.
11. **앱 정지 비용의 미설명분(약 130 ms/s)** — 이전 턴의 실패 항목. 빛을 15fps로 내려
    135.3 → 33.3 ms/s를 확인했지만, 앱 전체 정지는 162~270 ms/s로 흔들려 식별된 항목으로
    설명되지 않는 부분이 남았다(매니지드 프로파일은 핫스팟을 못 찾음, 최소화 A/B는 무효).
    이번 감사에서 원인 후보가 하나 늘었다: **재생 중이 아닐 때도 도는 링 렌더**(위 10번).

---

## 이번 감사에서 확인한 "이상 없음"

- 빌드 경고 0·오류 0, 테스트 137개 통과(마지막 편집 이후 상태 그대로).
- 배터리·미디어·알림·클립보드 서비스의 COM/이벤트 등록과 해제 짝은 맞다(`AudioService.ReleaseDevice`,
  `MediaService.Dispose`, `AudioCaptureService.CaptureSession`의 finally).
- 로직 계층(`TopDock.Logic`)은 WPF를 참조하지 않고, 도크 상태·빔 속도·미터·인박스가 테스트로 잠겨 있다.
- `MainWindow.xaml`에 효과(`DropShadowEffect`·`BlurEffect`)나 캐시 모드 남용은 없다(OpacityMask 1곳뿐).

## 참고: 이 감사의 한계

- 측정하지 않고 코드에서 센 수치(펜 개수 등)는 "코드에서 센 값"으로 표기했다.
- `MainWindow.xaml`(845줄)은 전수 정독이 아니라 구조·효과·이름을 훑는 방식으로 확인했다.
- `ThinkingOrb`의 렌더 품질/정확성은 죽은 코드라 검증하지 않았다.
