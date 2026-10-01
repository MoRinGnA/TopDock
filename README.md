<div align="center">

# TopDock

**Windows 화면 상단에 떠 있는 다이나믹 아일랜드**

시계 · 미디어 · 실시간 가사 · 클립보드 · 알림 · 배터리 —<br/>
당신의 노트북 상단, 하나의 검은 알약 안에.

[![Windows 10/11](https://img.shields.io/badge/Windows-10%20%2F%2011-0078D4?logo=windows11&logoColor=white)](https://github.com/MoRinGnA/TopDock)
[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![WPF](https://img.shields.io/badge/UI-WPF-5C2D91)](https://learn.microsoft.com/dotnet/desktop/wpf/)
[![PRs Welcome](https://img.shields.io/badge/PRs-welcome-FF9F0A)](https://github.com/MoRinGnA/TopDock/pulls)


</div>

---

## TopDock이 왜 필요한가요?

노트북으로 작업하다 보면 이런 순간이 반복됩니다.

> 지금 음악이 뭐지? → 미디어 플레이어 창 찾기 | 
> 아까 복사한 계좌번호가 뭐였지? → Win+V 뒤지기 | 
> 캡처한 거 다시 붙여넣고 싶은데 → 캡처 도구 다시 열기 |
> 배터리 얼마나 남았지? → 알림 센터 열기 

**TopDock은 이 모든 것을 시선만 위로 옮기면 답이 나오게 만듭니다.**
화면 최상단 중앙에 항상 떠 있는 작은 검은 알약이, 필요할 때만 스르륵 펼쳐집니다 —
iPhone의 다이나믹 아일랜드처럼.

---

## 주요 기능

### 미디어 컨트롤 & 실시간 가사
- **모든 플레이어 지원** — 브라우저 YouTube, Spotify, PotPlayer 등 SMTC를 보고하는 앱이라면 무엇이든
- 앨범아트, 진행바, 재생/일시정지/이전/다음 컨트롤
- **싱크 가사 자동 검색** (LRCLIB 연동) — 현재 재생 구간 가사가 노치에 흐릅니다
- 영상과 음원 길이가 달라도 **오프셋 자동 보정**으로 싱크 유지
- 한 번 본 가사는 **디스크에 영구 캐시** — 오프라인에서도 즉시 표시

### 클립보드 히스토리
- 복사하면 노치 안쪽에 작은 배지가 잠깐 스며들고 테두리 빛이 한 번 밝아집니다 (떠 있는 창 없음)
- **Ctrl+Shift+V** 또는 **노치 클릭**으로 **최근 5개 목록**이 노치 아래 중앙에 내려옵니다
- 텍스트 · **Win+Shift+S 캡처 이미지(썸네일)** · **탐색기에서 복사한 파일**까지 한 목록에서 처리
- 항목을 클릭하면 **클립보드에 올림과 동시에 직전 창에 바로 붙여넣습니다** (Ctrl+V 주입)
- **↑↓ 이동 / Enter 붙여넣기 / Delete 삭제 / Esc 닫기** — 마우스 없이도 사용
- 행의 **✕**로 개별 삭제, 헤더 우측 휴지통 버튼으로 **전체 삭제**
- 같은 내용을 여러 번 복사/재캡처해도 **중복 없이 한 장만** 유지
- "복사했다가 덮어쓴 그 순간"을 위한 기능

### 스폰서 구간 자동 스킵
- [SponsorBlock](https://sponsor.ajay.app/) 연동 — 스폰서·인트로·아웃트로 구간을 자동으로 건너뜁니다
- 확장 뷰 진행바에 **구간이 색상 마커로 표시**되어 한눈에 파악
- 카테고리별 on/off, YouTube Data API 키 입력 시 더 정확한 매칭

### 시스템 인디케이터
- 노치 우측 **상태 점**: 충전 중 = 초록(은은한 맥동), 저전력 = 빨강
- 노치 위에서 **마우스 휠만으로 볼륨 조절** + 실시간 볼륨 HUD
- Windows 알림을 노치 위에서 접수

---

## 사용 방법

| 하고 싶은 것 | 방법 |
|---|---|
| 노치 펼치기 / 접기 | 노치에 **마우스를 올리기 / 치우기** |
| 볼륨 조절 | 노치 위에서 **마우스 휠** |
| 음악 컨트롤 | 펼친 상태에서 **재생/이전/다음 버튼** |
| 현재 가사 보기 | 펼치면 미디어 정보 옆에 표시 |
| 복사한 내용 다시 쓰기 | **Ctrl+Shift+V** 또는 노치 클릭 → **항목 클릭** 시 직전 창에 붙여넣기 |
| 캡처한 이미지 다시 붙여넣기 | **썸네일 클릭** → 바로 붙여넣기 (실패 시 Ctrl+V) |
| 기록 지우기 | 목록 헤더의 **휴지통** = 전체 삭제, 행의 **✕** = 개별 삭제 |
| 스폰서 스킵 마커 보기 | 미디어 펼침 상태의 **진행바** 확인 |
| 설정 열기 | **트레이 아이콘 우클릭 → 설정** |
| 종료 | 트레이 아이콘 우클릭 → 종료 |

> **참고**: 확장 노치 우상단 클립보드 아이콘으로도 같은 히스토리 목록을 열 수 있습니다.
> 붙여넣기를 거부하는 앱에서는 클립보드에만 올라가므로 Ctrl+V를 직접 눌러 주세요.

---

## 설정

트레이 아이콘 우클릭 → **설정**에서 조절할 수 있습니다.

- 노치 상단 여백 (0~40px)
- 알림 / 클립보드 토스트 on/off
- 스폰서 스킵 카테고리별 토글 (intro / outro / intermission / music_offtopic)
- YouTube Data API 키 (선택 — 없으면 HTML 검색으로 대체)

설정은 `%APPDATA%\TopDock\config.json`에 저장됩니다.

---

## 빌드 방법

**요구 사항**: Windows 10 (19041+) 이상, [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```bash
git clone https://github.com/MoRinGnA/TopDock.git
cd TopDock
dotnet build -c Release
```

빌드 결과물:

```
bin\Release\net10.0-windows10.0.19041.0\TopDock.exe
```

바로 실행해도 되고, 원한다면 시작 프로그램에 등록해 상시 사용할 수 있습니다.

---

## 기술 스택

| 영역 | 사용 기술 |
|---|---|
| UI | WPF (.NET 10) + 커스텀 애니메이션 |
| 미디어 감지 | Windows SMTC (`GlobalSystemMediaTransportControlsSession`) |
| 볼륨 | Core Audio COM (`IAudioEndpointVolume`) |
| 배터리 | `SystemInformation.PowerStatus` |
| 가사 | [LRCLIB](https://lrclib.net/) API + LRC 파서 |
| 스폰서 스킵 | [SponsorBlock](https://sponsor.ajay.app/) API |
| videoId 검색 | YouTube Data API v3 / HTML 폴백 |

---

## 로드맵

- [ ] 알림 인박스 — 놓친 알림을 노치에서 다시 보기
- [ ] 집중 시간 위젯 & 휴식 알림
- [ ] 진행바 드래그 탐색
- [ ] 다중 모니터 대응
- [ ] 전용 트레이 아이콘

기여는 언제나 환영입니다! 이슈로 아이디어를 남기거나 PR을 보내주세요.

---

<div align="center">

**TopDock** — by [MoRinGnA](https://github.com/MoRinGnA)

</div>
