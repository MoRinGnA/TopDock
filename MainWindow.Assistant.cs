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

        /// <summary>도구 이름 → 얼굴의 표정 상태.</summary>
        private static Controls.OrbKind OrbForTool(string toolName) => toolName switch
        {
            "set_volume" or "media_play_pause" or "media_next" or "media_previous" or "media_seek"
                => Controls.OrbKind.Listening,
            "get_weather" or "play_youtube" => Controls.OrbKind.Searching,
            "open_url" => Controls.OrbKind.Connecting,
            "open_app" => Controls.OrbKind.Shaping,
            _ => Controls.OrbKind.Solving,
        };

        /// <summary>UI 스레드에서 얼굴 표정을 전환한다.</summary>
        private void PostAssistantFace(Controls.OrbKind kind)
            => Dispatcher.BeginInvoke(new Action(() => ShowAssistantFace(kind)));

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

            // 지시가 도는 중이면 진행 중 얼굴을, 아니면 기본 얼굴을 보여준다
            ShowAssistantFace(_assistantBusy
                ? (_assistantToolExecuting
                    ? Controls.OrbKind.Solving
                    : _assistantStreaming ? Controls.OrbKind.Composing : Controls.OrbKind.Working)
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
            _assistantToolExecuting = false;
            _assistantCts = new CancellationTokenSource();
            _assistantStreaming = false;
            bool finishedNormally = false;   // 완료 표시를 진행 표시가 있던 자리에서 하기 위한 표시

            AssistantInputBox.Clear();
            AssistantLatestResponseText.Text = string.Empty; // 최신 지시 결과만 보여준다
            ShowAssistantFace(Controls.OrbKind.Working);   // 사고 중

            // 지시를 보내면 패널은 접는다 — 작은 아일랜드에서 눈이 움직이고 옆에 상태 문구가 뜬다.
            // 답이 오면 결과에 따라 다시 펼치므로(아래) 입력칸은 잠기지 않는다.
            ShowAssistantIsland();

            UpdateAssistantContext();
            Log.Info($"Assistant query: {userMessage}");

            bool firstDeltaSeen = false;
            void OnFirstDelta()
            {
                if (firstDeltaSeen) return;
                firstDeltaSeen = true;
                _assistantStreaming = true;
                ShowAssistantFace(Controls.OrbKind.Composing); // 응답 스트리밍 중
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
                    // 완료 표시는 finally에서 한 번만 — 별도 알림 화면을 띄우지 않는다(피드백).
                    finishedNormally = true;
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
                _assistantToolExecuting = false;

                if (finishedNormally && _currentViewMode == ViewMode.AssistantCompact)
                {
                    // 완료는 진행 표시가 있던 그 자리에서 짧게 — 아일랜드는 타이머가 되돌린다
                    ShowAssistantStateDone();
                }
                else
                {
                    ShowAssistantFace(Controls.OrbKind.Breathing); // 기본 얼굴 복귀
                    CollapseAssistantIsland();                     // 끝난 작업의 아일랜드는 거둔다
                }
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
            // 네트워크 스레드 → UI 스레드: 최신 응답 흐름에 조각을 붙이고,
            // 같은 박자로 얼굴 입을 움직인다(발화 연동).
            Dispatcher.BeginInvoke(new Action(() =>
            {
                AssistantLatestResponseText.Inlines.Add(delta);
                AssistantScroll.ScrollToEnd();
                // 보이는 쪽이 어느 쪽이든 글자 흐름과 같은 박자로 눈이 움직인다
                AssistantFace.PulseSpeech();
                CompactAssistantFace.PulseSpeech();
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

        /// <summary>
        /// 지시가 도는 동안의 작은 아일랜드로 접는다. 아이들 아일랜드와 같은 자리·크기에 눈만 뜨고,
        /// 진행 상태는 오른쪽 끝 상태 점이 색으로 계속 갱신한다(ShowAssistantFace → ApplyAssistantIndicator).
        /// </summary>
        private void ShowAssistantIsland()
        {
            _assistantDoneTimer?.Stop();   // 앞선 완료 표시가 남아 있으면 물린다 — 새 작업을 가리면 안 된다
            _assistantDoneShowing = false;
            _isExpanded = false;           // 접힌 상태 — 마우스가 스쳐도 크기를 바꾸지 않는다
            SwitchViewMode(ViewMode.AssistantCompact);
        }

        // ── AI 상태 인디케이터 ──
        // AI 상태를 말하는 것은 점 하나다 — 배터리 점과 같은 자리(오른쪽 끝 12px), 같은 크기(12×12 후광 + 5×5 코어).
        // 색이 상태를, 숨쉬는 빛이 진행 중을, 고정된 초록이 완료를 말한다. 글씨는 쓰지 않는다.

        private static readonly Color AssistantThinkingColor = Color.FromRgb(0xB6, 0x9C, 0xFF);  // 생각
        private static readonly Color AssistantToolColor = Color.FromRgb(0xFF, 0xB8, 0x6B);      // 실행
        private static readonly Color AssistantReplyColor = Color.FromRgb(0x76, 0xD7, 0xFF);     // 응답
        private static readonly Color AssistantDoneColor = Color.FromRgb(0x30, 0xD1, 0x58);      // 완료

        private Controls.OrbKind? _assistantIndicatorKind;   // 마지막으로 표시한 단계
        private bool _assistantDoneShowing;                  // 완료 점이 떠 있는 동안
        private bool _assistantDotPulsing;
        private DispatcherTimer? _assistantDoneTimer;

        /// <summary>
        /// 상태 점을 현재 단계에 맞춘다. 비서 화면(확장/접힘)에서만 보이고,
        /// 대기 중이면 점을 두지 않는다 — 뜻 없는 회색 점은 어색하다(피드백).
        /// </summary>
        private void ApplyAssistantIndicator()
        {
            bool inAssistantView = _currentViewMode is ViewMode.Assistant or ViewMode.AssistantCompact;
            bool working = _assistantIndicatorKind is { } kind && kind != Controls.OrbKind.Breathing;

            if (!inAssistantView || (!working && !_assistantDoneShowing))
            {
                StopAssistantDotPulse();
                AssistantStatusChip.Visibility = Visibility.Collapsed;
                return;
            }

            Color accent = _assistantDoneShowing ? AssistantDoneColor : _assistantIndicatorKind switch
            {
                Controls.OrbKind.Working => AssistantThinkingColor,
                Controls.OrbKind.Composing => AssistantReplyColor,
                _ => AssistantToolColor,
            };

            var core = new SolidColorBrush(accent);
            core.Freeze();
            var glow = new RadialGradientBrush
            {
                Center = new Point(0.5, 0.5),
                GradientOrigin = new Point(0.5, 0.5),
                RadiusX = 0.5,
                RadiusY = 0.5,
            };
            glow.GradientStops.Add(new GradientStop(accent, 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1.0));
            glow.Freeze();

            AssistantStatusCore.Fill = core;
            AssistantStatusGlow.Fill = glow;
            AssistantStatusChip.Visibility = Visibility.Visible;

            // 진행 중에는 점이 은은하게 숨쉬고, 완료는 고정 — 끝났다는 신호가 흔들리면 거슬린다.
            if (_assistantDoneShowing) StopAssistantDotPulse();
            else StartAssistantDotPulse();
        }

        private void StartAssistantDotPulse()
        {
            if (_assistantDotPulsing) return;
            _assistantDotPulsing = true;
            var pulse = new DoubleAnimation(1.0, 0.45, new Duration(TimeSpan.FromMilliseconds(720)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            AssistantStatusChip.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        private void StopAssistantDotPulse()
        {
            if (!_assistantDotPulsing) return;
            _assistantDotPulsing = false;
            AssistantStatusChip.BeginAnimation(UIElement.OpacityProperty, null);
            AssistantStatusChip.Opacity = 1;
        }

        /// <summary>
        /// 완료는 진행 표시가 있던 그 자리에서 알린다 — 별도의 "완료" 화면을 띄우지 않는다(피드백).
        /// 잠시 뒤 아일랜드는 평소 모습으로 돌아간다.
        /// </summary>
        private void ShowAssistantStateDone()
        {
            _assistantIndicatorKind = Controls.OrbKind.Breathing;   // 진행 표시는 끝났다
            _assistantDoneShowing = true;
            ApplyAssistantIndicator();

            if (_assistantDoneTimer == null)
            {
                _assistantDoneTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1400) };
                _assistantDoneTimer.Tick += (_, _) => CollapseAssistantIsland();
            }
            _assistantDoneTimer.Stop();
            _assistantDoneTimer.Start();
        }

        /// <summary>아일랜드를 평소 모습으로 되돌린다 — 작업/완료 표시가 끝났을 때.</summary>
        private void CollapseAssistantIsland()
        {
            _assistantDoneTimer?.Stop();
            // 완료 표시가 떠 있는 1.4초 사이에 새 지시가 들어올 수 있다 — 돌고 있으면 거두지 않는다
            if (_assistantBusy) return;
            if (_currentViewMode != ViewMode.AssistantCompact) return;
            _assistantDoneShowing = false;   // 완료 점은 여기서 끝난다 (뷰 전환이 점도 함께 걷는다)
            _isExpanded = false;   // 접힌 상태로 돌아간다 (마우스가 올라가 있어도)
            SwitchViewMode(HasMedia ? ViewMode.MediaCompact : ViewMode.IdleCompact);
        }

        // ── AI 얼굴·상태 표시 전환 ──
        // 확장 화면과 작업 중 아일랜드의 표정을 한 번에 맞추고, 상태 점 색도 함께 맞춘다.
        private void ShowAssistantFace(Controls.OrbKind kind)
        {
            var faceState = kind switch
            {
                Controls.OrbKind.Breathing => Controls.AiFace.FaceState.Idle,
                Controls.OrbKind.Composing => Controls.AiFace.FaceState.Talking,
                Controls.OrbKind.Working => Controls.AiFace.FaceState.Thinking,
                _ => Controls.AiFace.FaceState.Alert,
            };
            AssistantFace.SetState(faceState);
            CompactAssistantFace.SetState(faceState);   // 어느 쪽이 보이든 표정이 어긋나지 않는다

            // 상태는 글씨가 아니라 점 하나가 색으로 말한다. 새 단계가 오면 완료 표시는 끝난 것.
            _assistantDoneShowing = false;
            _assistantIndicatorKind = kind;
            ApplyAssistantIndicator();

            // 흔들림·호흡 같은 반복 연출은 얼굴이 자기 상태와 보이는 여부를 보고 스스로 켜고 끈다.

            // 입력창의 beam은 응답 스트리밍 중에만 활성화한다.
            InputBeam.Active = kind == Controls.OrbKind.Composing;

            // 테두리 빛 상태는 기존 진행 단계에 맞춰 유지한다.
            bool central = kind is not (Controls.OrbKind.Breathing or Controls.OrbKind.Composing);
            _orbCentral = central;
            UpdateBeamState();
        }

    }
}
