using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        // ────────────────────────── AI 비서 ──────────────────────────

        /// <summary>도구 이름 → 어울리는 오브 디자인과 상태 문구.</summary>
        private static (Controls.OrbKind Kind, string Status) OrbForTool(string toolName) => toolName switch
        {
            "play_youtube" or "get_weather" => (Controls.OrbKind.Searching, "검색 중..."), // 외부 정보 조회
            "open_url" => (Controls.OrbKind.Connecting, "연결 중..."),        // 노드 네트워크
            "open_app" => (Controls.OrbKind.Shaping, "준비 중..."),           // 도형이 바뀌는 중
            "set_volume" => (Controls.OrbKind.Listening, "볼륨 조절 중..."),  // 구면을 타는 파동
            "media_play_pause" or "media_next" or "media_previous" or "media_seek"
                => (Controls.OrbKind.Listening, "음악 제어 중..."),
            _ => (Controls.OrbKind.Solving, "처리 중..."),                    // 레이어가 풀리는 큐브
        };

        /// <summary>비UI 스레드에서도 안전하게 오브 상태를 바꾼다.</summary>
        private void PostOrb(Controls.OrbKind kind, string? status = null)
            => Dispatcher.BeginInvoke(new Action(() => ShowConversationOrb(kind, status)));

        /// <summary>핫키/버튼으로 비서를 연다. 이미 열려 있으면 닫는다(토글).</summary>
        private void OpenAssistant()
        {
            if (!ConfigService.Current.AssistantEnabled)
            {
                Log.Info("Assistant hotkey ignored: disabled in settings");
                return;
            }
            if (_currentViewMode == ViewMode.Assistant)
            {
                // 다시 눌러 접기만 한다 — 요청은 계속 돌고, 취소는 Esc/✕가 맡는다
                CloseAssistant();
                return;
            }

            ClearNotchNotice();   // 떠 있던 알림을 걷고 비서 화면으로 (알림 타이머가 화면을 되돌리지 않게)
            _isExpanded = true;
            SwitchViewMode(ViewMode.Assistant);

            // 지시가 도는 중이면 진행 상태를, 아니면 대기 오브(호흡)를 보여준다
            ShowConversationOrb(_assistantBusy
                ? (_assistantStreaming ? Controls.OrbKind.Composing : Controls.OrbKind.Working)
                : Controls.OrbKind.Breathing);

            // 스위치 애니메이션 이후 포커스 (노치가 Topmost 투명 오버레이라 스스로 활성화 필요)
            ActivateSelfAndFocusInput();
        }

        private void CloseAssistant()
        {
            _isExpanded = false;
            AssistantInputBox.Clear();
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
        }

        private void ActivateSelfAndFocusInput()
        {
            try { Activate(); } catch { }
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AssistantInputBox.Focus();
                AssistantInputBox.CaretIndex = AssistantInputBox.Text.Length;
            }), System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>확장 크기에서 벗어나면 자동으로 닫히도록 하는 마우스 이탈 처리 포함.</summary>
        private void AssistantInput_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                CloseAssistant();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Enter && !_assistantBusy)
            {
                string text = AssistantInputBox.Text.Trim();
                if (text.Length > 0)
                {
                    _ = SendAssistantMessageAsync(text);
                }
                e.Handled = true;
            }
        }

        private async System.Threading.Tasks.Task SendAssistantMessageAsync(string userMessage)
        {
            if (_assistantBusy) return;
            _assistantBusy = true;
            _assistantCts = new CancellationTokenSource();
            _assistantStreaming = false;

            AssistantInputBox.Clear();
            AssistantLatestResponseText.Text = string.Empty; // 최신 지시 결과만 보여준다
            ShowConversationOrb(Controls.OrbKind.Working);   // 사고 중

            // 지시를 보낸 뒤 알약은 접는다 — 답변은 노치 알림으로 추적하고,
            // 다시 펼치면 그대로 볼 수 있다. 실패했을 때만 자동으로 다시 펼친다.
            CloseAssistant();
            ShowThinkingNotice();

            UpdateAssistantContext();
            Log.Info($"Assistant query: {userMessage}");

            bool firstDeltaSeen = false;
            void OnFirstDelta()
            {
                if (firstDeltaSeen) return;
                firstDeltaSeen = true;
                _assistantStreaming = true;
                ShowConversationOrb(Controls.OrbKind.Composing); // 응답 스트리밍 중
            }

            try
            {
                void DeltaProxy(string d)
                {
                    Dispatcher.BeginInvoke(new Action(OnFirstDelta));
                    Assistant_DeltaReceived(d);
                }

                string answer;
                _assistant.DeltaReceived += DeltaProxy;
                try
                {
                    answer = await _assistant.SendAsync(userMessage, _assistantCts.Token).ConfigureAwait(true);
                }
                finally
                {
                    _assistant.DeltaReceived -= DeltaProxy;
                }
                Log.Info($"Assistant answer length: {answer.Length}");
                // 빈 응답 판정은 반드시 완성된 문자열로 — UI 텍스트로 판정하면 델타가
                // BeginInvoke로 늦게 붙는 경쟁에서 "(빈 응답)"이 실제 답변 앞에 끼어든다 (실측 버그)
                if (string.IsNullOrWhiteSpace(answer))
                {
                    AssistantLatestResponseText.Text = "(빈 응답)";
                }

                bool keepAnswerOpen = _assistant.LastTurnRequestedInformation && !_assistant.LastTurnPerformedAction;
                if (keepAnswerOpen)
                {
                    // 정보 답변은 사용자가 바로 읽을 수 있도록 패널을 다시 펼쳐 둔다.
                    if (_currentViewMode != ViewMode.Assistant) OpenAssistant();
                    AssistantScroll.ScrollToEnd();
                }
                else
                {
                    // 실행 지시는 결과를 간단히 알리고 접힌 상태를 유지한다.
                    ShowDoneNotice(answer);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info("Assistant request cancelled");
                ClearAssistantNotice();
            }
            catch (AiException ex)
            {
                Log.Error("Assistant request failed", ex);
                AssistantLatestResponseText.Text = "⚠ " + ex.Message;
                RevealAssistantWithError();
            }
            catch (Exception ex)
            {
                Log.Error("Assistant unexpected error", ex);
                AssistantLatestResponseText.Text = "⚠ 알 수 없는 오류가 발생했습니다.";
                RevealAssistantWithError();
            }
            finally
            {
                _assistantBusy = false;
                _assistantStreaming = false;
                ShowConversationOrb(Controls.OrbKind.Breathing); // 대기 복귀
            }
        }

        /// <summary>실패는 놓치면 안 된다 — 비서 화면을 다시 펼쳐 오류를 보여준다.</summary>
        private void RevealAssistantWithError()
        {
            // 실패는 알림으로 흘리지 않고 화면으로 직접 가져온다 — 다시 시도하기도 쉽다
            Log.Info("Assistant error: revealing assistant view");
            if (_currentViewMode != ViewMode.Assistant) OpenAssistant();
        }

        private void Assistant_DeltaReceived(string delta)
        {
            // 네트워크 스레드 → UI 스레드: 최신 응답 흐름에 조각을 붙인다
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AssistantLatestResponseText.Inlines.Add(delta);
                AssistantScroll.ScrollToEnd();
            }));
        }

        private void UpdateAssistantContext()
        {
            string mediaText = string.Empty;
            string lyricText = string.Empty;
            var media = _mediaService.CurrentMedia;
            if (media != null && !string.IsNullOrWhiteSpace(media.Title) && media.Title != "재생 중인 미디어 없음")
            {
                mediaText = $"{media.Title}" + (string.IsNullOrWhiteSpace(media.Artist) ? "" : $" - {media.Artist}");

                // 현재 재생 위치의 가사 줄
                if (_syncedLyrics.Count > 0)
                {
                    var mediaPos = media.CurrentEstimatedPosition;
                    LyricLine? current = null;
                    foreach (LyricLine line in _syncedLyrics)
                    {
                        if (line.Time <= mediaPos) current = line; else break;
                    }
                    if (current != null && !string.IsNullOrWhiteSpace(current.Text))
                    {
                        lyricText = current.Text;
                    }
                }
            }

            string clipboardText = string.Empty;
            if (_clipboardHistory.Count > 0)
            {
                ClipboardItem last = _clipboardHistory[0];
                if (!last.IsImage) clipboardText = last.Text;
            }

            _assistant.UpdateContext(new AssistantContext(
                NowText: DateTime.Now.ToString("yyyy-MM-dd dddd HH:mm"),
                MediaText: mediaText,
                LyricText: lyricText,
                ClipboardText: clipboardText));
        }

        private void Assistant_NotchClick(object sender, MouseButtonEventArgs e)
        {
            // 1·2번 피드백: 노치 클릭 → AI 호출은 제거됐다. AI는 핫키(Ctrl+Shift+Space)로만 연다.
        }

        private void AssistantCloseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_assistantBusy)
            {
                // 생성 중이면 요청을 취소하고 창도 닫는다
                _assistantCts?.Cancel();
            }
            CloseAssistant();
        }

        private void ClearAssistantConversation()
        {
            AssistantLatestResponseText.Text = string.Empty;
            AssistantScroll.ScrollToHome();
        }

        // 상태 텍스트 광택 스윕: 밝은 빛이 글자 뒤를 왼→오른쪽으로 스치는 원작 디테일.
        // XAML EventTrigger는 TextBlock.Triggers에 못 쓰므로 코드에서 구동한다.
        private void StartStatusSheen()
        {
            if (StatusSheenBright == null) return;
            var anim = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = new Duration(TimeSpan.FromSeconds(2.4)),
                RepeatBehavior = RepeatBehavior.Forever,
            };
            StatusSheenBright.BeginAnimation(GradientStop.OffsetProperty, anim);
        }

        // ── Thinking Orbs 상태 전환 ──
        // 대기=Breathing(상단 소형) / 사고=Working(중앙 대형, 입력 불가 시각화) / 응답=Composing(상단 소형).
        // CenterOrb는 지시 직후 응답이 오기 전까지 중앙에서 크게 도는 전용 오브다.

        private void ShowConversationOrb(Controls.OrbKind kind, string? statusOverride = null)
        {
            // 오브 중심 몰입형 — 하나의 큰 오브가 항상 무대의 주인공이다.
            // (예전엔 대기·응답은 헤더의 작은 오브, 작업 중은 중앙 오브로 갈아탔다)
            CenterOrb.Kind = kind;
            CenterOrb.Visibility = Visibility.Visible;

            // border-beam: 입력창은 응답 스트리밍 중에만 광선이 흐른다.
            InputBeam.Active = kind == Controls.OrbKind.Composing;

            // 노치 테두리 = 살아있는 가장자리 — '작업 중'일 때만 크게 돈다.
            // 응답 텍스트는 비면 저절로 0 높이라 따로 숨길 필요가 없다.
            bool central = kind is not (Controls.OrbKind.Breathing or Controls.OrbKind.Composing);
            _orbCentral = central;
            UpdateBeamState();

            AssistantStatusText.Text = statusOverride ?? (kind switch
            {
                Controls.OrbKind.Working => "생각 중...",
                Controls.OrbKind.Composing => "응답 중...",
                Controls.OrbKind.Searching => "검색 중...",
                Controls.OrbKind.Solving => "처리 중...",
                Controls.OrbKind.Listening => "대화 중...",
                Controls.OrbKind.Connecting => "연결 중...",
                Controls.OrbKind.Weaving => "정리 중...",
                Controls.OrbKind.Shaping => "준비 중...",
                _ => "대기 중...",
            });
        }

    }
}
