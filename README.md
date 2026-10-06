<div align="center">

# 🎩 TopDock

**Windows 화면 상단에 떠 있는 다이나믹 아일랜드**

미디어 · 실시간 가사 · AI 비서 · 클립보드 · 알림 —<br/>
당신의 노트북 상단, 하나의 검은 알약 안에.

[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows11&logoColor=white)](https://github.com/MoRinGnA/TopDock)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-5C2D91)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-FF9F0A)](https://github.com/MoRinGnA/TopDock/pulls)

<sub>NuGet 의존성 <b>0개</b> · 순수 WPF · .NET 10</sub>

</div>

---

> [!TIP]
> **한 줄 요약** — 화면 맨 위 검은 알약 하나가, 필요할 때만 스르륵 펼쳐집니다.
> 마우스를 올리면 열리고, 치우면 닫힙니다. iPhone의 다이나믹 아일랜드처럼.

## 📖 목차

- [왜 필요한가요?](#-왜-필요한가요)
- [미리보기](#-미리보기)
- [기능](#-기능)
- [단축키](#️-단축키)
- [시작하기](#-시작하기)
- [설정](#️-설정)
- [기술 스택](#-기술-스택)
- [로드맵](#️-로드맵)
- [기여](#-기여)
- [라이선스](#-라이선스)

---

## 🤔 왜 필요한가요?

노트북으로 작업하다 보면 이런 순간이 반복됩니다.

| 궁금한 것 | 예전엔 | 이제는 |
|---|---|---|
| 지금 무슨 노래지? | 플레이어 창 찾기 | 시선만 위로 |
| 아까 복사한 계좌번호가? | `Win`+`V` 뒤지기 | 시선만 위로 |
| 캡처한 거 다시 붙여넣기 | 캡처 도구 다시 열기 | 시선만 위로 |
| 배터리 얼마나 남았지? | 알림 센터 열기 | 시선만 위로 |

**시선만 위로 옮기면 답이 나오게.** 그게 TopDock의 전부입니다.

---

## 👀 미리보기

<div align="center">

```text
            
           YouTube

   41:00 ────────●───────── 19:33

          ⏮      ⏸      ⏭

   · 복사하면 테두리 빛이 한 번 밝아집니다 ·
   · 음악이 재생되면 테두리가 앨범색으로 물듭니다 ·
```

<sub>마우스를 올리면 펼쳐지고, 치우면 접힙니다.</sub>

</div>

---

## ✨ 기능

### 🎵 미디어 컨트롤 & 실시간 가사

- **모든 플레이어 지원** — 브라우저 YouTube, Spotify, PotPlayer 등 SMTC를 보고하는 앱이라면 무엇이든
- 앨범아트 · 진행바 · 재생/일시정지/이전/다음 컨트롤
- **싱크 가사 자동 검색** ([LRCLIB](https://lrclib.net/)) — 지금 재생 구간 가사가 노치에 흐릅니다
- 영상과 음원 길이가 달라도 **오프셋 자동 보정**으로 싱크 유지
- 한 번 본 가사는 **디스크에 영구 캐시** — 오프라인에서도 즉시 표시
- 재생 중엔 노치 테두리의 빛이 **앨범 지배색**으로 물듭니다

### 🤖 AI 비서

> [!NOTE]
> 노치 오른쪽의 AI 아이콘을 누르거나 **<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Space</kbd>** 로 부릅니다.
> 생각하는 동안 **Thinking Orb**가 상태를 보여줍니다.
> 지시를 보내면 화면은 스스로 접히고 진행 상태가 노치 알림으로 이어집니다 — 실패했을 때만 자동으로 다시 펼쳐집니다.

명령이 아니라 **행동**까지 합니다 — 모델이 도구를 직접 호출합니다.

| 도구 | 하는 일 |
|---|---|
| `media_play_pause` / `media_next` / `media_previous` | 재생 제어 — "다음 곡" |
| `media_seek` | 재생 위치 앞뒤 이동 — "30초 앞으로" |
| `set_volume` | 시스템 볼륨 설정 — "볼륨 30으로" |
| `open_app` | 허용 목록의 앱 실행 — "메모장 열어줘" |
| `open_url` | 브라우저로 URL 열기 |
| `play_youtube` | 유튜브 검색 후 첫 영상 재생 |

- **키 없이 바로 사용** — 기본 프로바이더 LLM7(분당 30회 무료)
- 프로바이더 선택: LLM7 · Google Gemini · OpenRouter · Upstage Solar · **Ollama(로컬)**
- **실행 허용 목록** — 설정에 적힌 앱만 실행합니다. 한글 별칭(`메모장`·`계산기`·`크롬`)은 자동 번역
- 프롬프트 인젝션 방어 — 이름 그대로 실행하지 않고 항상 허용 목록으로 검증
- **Thinking Orb 9종** — 작업(궤도) · 검색(구체) · 추론(큐브) · 청취(파동) · 연결(네트워크) · 직조(꼬임) · 작곡(리본) · 대기(링) · 변형(모프)

### 📋 클립보드 히스토리

- 복사하면 노치 안쪽에 작은 배지가 잠깐 스며들고 **테두리 빛이 한 번 밝아집니다** (떠 있는 창 없음)
- **<kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd>** 또는 **노치 클릭**으로 최근 **5개 목록**이 노치 아래에 내려옵니다
- 텍스트 · **Win+Shift+S 캡처 이미지(썸네일)** · **탐색기에서 복사한 파일**까지 한 목록에서 처리
- 항목을 클릭하면 **클립보드에 올림과 동시에 직전 창에 바로 붙여넣습니다** (Ctrl+V 주입)
- **<kbd>↑</kbd><kbd>↓</kbd>** 이동 · **<kbd>Enter</kbd>** 붙여넣기 · **<kbd>Delete</kbd>** 삭제 · **<kbd>Esc</kbd>** 닫기 — 마우스 없이도 조작
- 행의 **✕**로 개별 삭제, 헤더 우측 휴지통으로 **전체 삭제**
- 같은 내용을 여러 번 복사/재캡처해도 **중복 없이 한 장만** 유지

### 🔔 노치 알림

노치는 같은 통로 하나로 모든 일을 알립니다.

- **지시하면 노치가 스스로 접혀서 일합니다** — 비서 화면이 저절로 닫히고, 노치가 진행 상태를 대신 보여줍니다
- **진행 중엔 계속 떠 있습니다** — "AI · 생각 중…" · "실행 · 볼륨 조절 중…"이 끝날 때까지 자리를 지킵니다 (깜빡였다 사라지지 않음)
- **완료되면 딱 한 번** — "AI · 완료"가 잠깐 떴다가 사라집니다
- **실패했을 때만 화면으로** — 응답을 못 받으면 비서 화면이 다시 펼쳐져 오류를 바로 보여줍니다
- 진행 중에 핫키를 다시 누르면 화면을 펼쳐 상태를 볼 수 있고, 거기서 **Esc**로 취소합니다
- **Windows 알림** — 앱 이름과 제목 (파랑 벨)
- **복사** — 화면을 가로채지 않고 조용한 배지 + 테두리 빛 한 번
- 알림이 뜨는 순간 노치 테두리가 한 번 밝아집니다

### 🔋 시스템 인디케이터

- 노치 우측 **상태 점** — 충전 중 = 초록(은은한 맥동), 저전력 = 빨강
- 노치 위에서 **마우스 휠만으로 볼륨 조절** + 실시간 볼륨 HUD
- Windows 알림을 노치 위에서 접수
- 미디어 재생 중 **이퀄라이저 바**가 같은 곡의 색으로 살아 움직임

### ✨ 살아있는 가장자리 (Living Edge)

노치 테두리는 장식이 아니라 **상태를 말하는 언어**입니다.

- **색** — 평소엔 중립 화이트, 음악이 재생되면 그 곡의 색
- **운동** — 대기(느리게) · 사고(크게) · 스트리밍(얇게) · 재생(크게 빠르게)
- **순간** — 복사 같은 이벤트에 테두리가 한 번 밝아짐
- 링 세그먼트마다 알파를 직접 계산해, 펼침/접힘 모프 중에도 빛이 튀지 않습니다

---

## ⌨️ 단축키

| 하고 싶은 것 | 방법 |
|---|---|
| 노치 펼치기 / 접기 | 노치에 **마우스 올리기 / 치우기** |
| AI 비서 부르기 | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Space</kbd> |
| 클립보드 히스토리 | <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>V</kbd> 또는 노치 클릭 |
| 볼륨 조절 | 노치 위에서 **마우스 휠** |
| 붙여넣기 / 삭제 / 닫기 | <kbd>Enter</kbd> / <kbd>Delete</kbd> / <kbd>Esc</kbd> |
| 설정 열기 | 트레이 아이콘 우클릭 → **설정** |
| 종료 | 트레이 아이콘 우클릭 → **종료** |

> [!WARNING]
> 붙여넣기를 거부하는 앱에서는 클립보드에만 올라갑니다. 이때는 <kbd>Ctrl</kbd>+<kbd>V</kbd>를 직접 눌러 주세요.

---

## 🚀 시작하기

**요구 사항** — Windows 10 (19041+) 이상, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
git clone https://github.com/MoRinGnA/TopDock.git
cd TopDock
dotnet build -c Release
```

빌드 결과물:

```
bin\Release\net10.0-windows10.0.19041.0\TopDock.exe
```

바로 실행해도 되고, 시작 프로그램에 등록해 상시 사용해도 됩니다.

---

## ⚙️ 설정

트레이 아이콘 우클릭 → **설정**.

| 항목 | 설명 |
|---|---|
| 알림 표시 | Windows 알림을 노치에 표시 |
| 복사 피드백 | 복사할 때 노치 안쪽 배지 표시 |
| 노치 상단 여백 | 0~40px |
| AI 비서 | 사용 on/off · 프로바이더 · API 키 · 모델 · 실행 허용 앱 |
| YouTube API 키 | (선택) 있으면 영상 검색이 더 정확해짐 |

설정은 `%APPDATA%\TopDock\config.json`에 저장됩니다.

<details>
<summary><b>🔌 AI 프로바이더 자세히 보기</b></summary>

<br/>

| 프로바이더 | 키 | 비고 |
|---|---|---|
| **LLM7** | 불필요 | 기본값. 익명 키로 분당 30회 |
| Google Gemini | 필요 | 무료 티어 (기본 모델 `gemini-3.5-flash`) |
| OpenRouter | 필요 | 무료 모델 사용 가능 |
| Upstage Solar | 필요 | 유료 |
| Ollama | 불필요 | 내 PC에서 로컬 실행 |

모델 칸을 비워두면 각 프로바이더의 기본 모델을 사용합니다.
`models/` 접두어를 붙여 넣어도 자동으로 보정됩니다.

</details>

---

## 🧩 기술 스택

| 영역 | 사용 기술 |
|---|---|
| UI | WPF (.NET 10) + 커스텀 애니메이션 |
| 글꼴 | [Pretendard](https://github.com/orioncactus/pretendard) (SIL OFL 1.1, 리소스 번들) |
| 미디어 감지 | Windows SMTC (`GlobalSystemMediaTransportControlsSession`) |
| 볼륨 | Core Audio COM (`IAudioEndpointVolume`) |
| 배터리 | `SystemInformation.PowerStatus` |
| 가사 | [LRCLIB](https://lrclib.net/) API + LRC 파서 |
| AI | OpenAI 호환 SSE 스트리밍 + function calling |
| videoId 검색 | YouTube Data API v3 / HTML 폴백 |

---

## 🗺️ 로드맵

### 최근 완료

- [x] 진행바 드래그 탐색 — 클릭·드래그로 즉시 이동 (AI `media_seek` 도구와 같은 엔진)
- [x] AI 작업 백그라운드 추적 — 지시하면 노치가 접히고, 사고·도구 실행·완료를 노치 알림으로 추적 (실패만 화면으로)
- [x] AI 탭 오브 중심 몰입형 재설계
- [x] Pretendard 폰트 번들

### 다음

- [ ] 알림 인박스 — 놓친 알림을 노치에서 다시 보기
- [ ] 집중 시간 위젯 & 휴식 알림
- [ ] 다중 모니터 대응
- [ ] 전용 트레이 아이콘

---

## 🤝 기여

이슈로 아이디어를 남기거나 PR을 보내주세요. 스크린샷·데모 GIF 기여도 환영합니다.

---

## 📄 라이선스

앱 코드는 아직 별도의 라이선스를 명시하지 않았습니다.

번들된 **Pretendard** 글꼴은 [SIL Open Font License 1.1](Fonts/LICENSE.txt)을 따릅니다
(Copyright 2021 Kil Hyung-jin, orioncactus/pretendard).

---

<div align="center">

**TopDock** — by [MoRinGnA](https://github.com/MoRinGnA)

<sub>시선을 조금만 위로 올려보세요. 거기에 다 있습니다. ✨</sub>

</div>
