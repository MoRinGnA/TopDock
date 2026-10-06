using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TopDock.Models;
using TopDock.Services;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        // 클립보드 히스토리 한 항목 — 텍스트 / 이미지 / 파일 중 하나 (최근 5개, 최신이 앞)
        private sealed class ClipboardItem
        {
            public ClipboardItem(string text, BitmapSource? image, string[]? files = null)
            {
                Text = text;
                Image = image;
                Files = files;
            }

            public string Text { get; }
            public BitmapSource? Image { get; }
            public string[]? Files { get; }
            public BitmapSource? Thumbnail { get; set; }   // 목록 미리보기용 축소본 (지연 생성)

            public bool IsImage => Image != null;
            public bool IsFiles => Files is { Length: > 0 };
        }

        private const int HistoryLimit = 5;

        private readonly List<ClipboardItem> _clipboardHistory = new();
        private DispatcherTimer? _clipboardToastTimer;

        // 붙여넣기 대상: 복사가 일어난 순간의 전경 창
        private IntPtr _pasteTargetHwnd;
        private bool _pasteInFlight;

        // 앱이 스스로 클립보드에 쓴 내용의 지문 — 이벤트가 안 오더라도 다음 정상 복사를 삼키지 않게 한다
        private string? _selfCopySignature;
        private DateTime _selfCopyAt = DateTime.MinValue;

        /// <summary>
        /// 클립보드가 바뀔 때마다 불린다. 읽기는 한 번만 하고(다른 앱과의 잠금 경쟁 최소화)
        /// 우리 앱이 쓴 내용이면 무시한 뒤, 표시는 UI 쪽으로 넘긴다.
        /// </summary>
        private void HandleClipboardUpdate()
        {
            ClipboardItem? item = ReadClipboard();
            if (item == null) return;

            // 방금 앱이 스스로 쓴 내용(항목 클릭 → 재복사)은 새 항목으로 잡지 않는다
            if (IsSelfCopy(item)) return;

            // 붙여넣기 대상은 '복사가 일어난 순간'의 전경 창 — 노치가 아니라 사용자가 쓰던 앱이어야 한다
            IntPtr foreground = GetForegroundWindow();
            if (foreground != IntPtr.Zero && foreground != new WindowInteropHelper(this).Handle)
                _pasteTargetHwnd = foreground;

            Dispatcher.InvokeAsync(() => ShowClipboardToast(item));
        }

        /// <summary>
        /// 클립보드를 한 번만 열이 이미지 / 파일 / 텍스트를 읽는다.
        /// (기존에는 Contains→Get을 형식마다 반복해 OpenClipboard를 4번 시도했다)
        /// </summary>
        private static ClipboardItem? ReadClipboard()
        {
            // 다른 앱이 클립보드를 쥐고 있으면 열기가 실패한다 — 짧게 재시도 (최대 ~75ms)
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    System.Windows.IDataObject? data = Clipboard.GetDataObject();
                    if (data == null) return null;

                    // Win+Shift+S 캡처 등 이미지가 본체인 경우 → 이미지 우선 (기존 동작 유지)
                    if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource image)
                    {
                        image.Freeze();   // 해시·썸네일 계산 전에 동결해야 안전하다
                        return new ClipboardItem(string.Empty, image);
                    }

                    // 탐색기 파일 복사(CF_HDROP) — 그동안 조용히 무시되던 형식
                    if (data.GetDataPresent(DataFormats.FileDrop)
                        && data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
                    {
                        return new ClipboardItem(string.Empty, null, files);
                    }

                    // 빈 문자열/공백만 있는 복사는 히스토리에서도 제외
                    if (data.GetDataPresent(DataFormats.UnicodeText)
                        && data.GetData(DataFormats.UnicodeText) is string text && !string.IsNullOrWhiteSpace(text))
                    {
                        return new ClipboardItem(text, null);
                    }

                    return null;
                }
                catch (COMException)
                {
                    Thread.Sleep(25);   // 클립보드 잠금 경쟁
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        /// <summary>내용 지문 — 우리가 쓴 클립보드를 되읽지 않기 위한 비교용. 계산 불가면 null.</summary>
        private static string? Signature(ClipboardItem item)
        {
            if (item.IsImage && item.Image != null)
            {
                byte[] hash = ComputeImageHash(item.Image);
                return hash.Length == 0 ? null : "i:" + Convert.ToBase64String(hash);
            }
            if (item.IsFiles) return "f:" + string.Join('\n', item.Files!);
            return "t:" + item.Text;
        }

        private bool IsSelfCopy(ClipboardItem item)
        {
            if (_selfCopySignature == null) return false;
            if (DateTime.UtcNow - _selfCopyAt > TimeSpan.FromSeconds(2)) return false;
            try { return Signature(item) == _selfCopySignature; }
            catch { return false; }
        }

        private void ShowClipboardToast(ClipboardItem item)
        {
            // 히스토리는 토스트 설정과 무관하게 항상 수집한다
            AddToHistory(item);

            if (!ConfigService.Current.ShowClipboardToast) return;
            // 복사는 노치 안에서만 조용히 알린다
            // 기록패널이 열려있으면 목록만 갱신한다
            if (_clipboardHistoryOpen)
            {
                BuildClipboardHistoryRows();
                return;
            }

            ShowClipboardFeedback();
        }

        private void AddToHistory(ClipboardItem item)
        {
            // 중복은 새로 쌓지 않고 맨 위로 올린다 (이미지는 픽셀 해시 비교 — 캡처를 여러 번 떠도 1장만 유지)
            if (item.IsImage && item.Image != null)
            {
                byte[] newHash = ComputeImageHash(item.Image);
                _clipboardHistory.RemoveAll(i =>
                {
                    if (!i.IsImage || i.Image == null) return false;
                    return HashEquals(ComputeImageHash(i.Image), newHash);
                });
            }
            else if (item.IsFiles)
            {
                string key = string.Join('\n', item.Files!);
                _clipboardHistory.RemoveAll(i => i.IsFiles && string.Join('\n', i.Files!) == key);
            }
            else
            {
                _clipboardHistory.RemoveAll(i => !i.IsImage && !i.IsFiles && i.Text == item.Text);
            }

            _clipboardHistory.Insert(0, item);
            if (_clipboardHistory.Count > HistoryLimit) _clipboardHistory.RemoveAt(HistoryLimit);
            _clipboardSelectedIndex = -1;
        }

        private static string ClipboardRowLabel(ClipboardItem item)
        {
            if (item.IsImage) return $"캡처 {item.Image!.PixelWidth}×{item.Image.PixelHeight}";
            if (item.IsFiles)
            {
                string? name = SafeFileName(item.Files![0]);
                return item.Files.Length == 1 ? name : $"{name} 외 {item.Files.Length - 1}개 파일";
            }
            return Collapse(item.Text, 120);
        }

        private static string SafeFileName(string? path)
        {
            if (string.IsNullOrEmpty(path)) return "파일";
            string name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        /// <summary>공백(개행·탭 포함)을 단일 공백으로 접고 max자에서 자른다.</summary>
        private static string Collapse(string text, int max)
        {
            const int ScanLimit = 512;   // 아주 큰 텍스트는 앞부분만 본다
            int consumed = Math.Min(text.Length, ScanLimit);
            var sb = new StringBuilder(consumed);
            bool pendingSpace = false;
            for (int i = 0; i < consumed; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { pendingSpace = sb.Length > 0; continue; }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }

            bool truncated = text.Length > consumed;
            string collapsed = sb.ToString();
            if (collapsed.Length > max)
            {
                collapsed = collapsed[..max];
                truncated = true;
            }
            return truncated ? collapsed + "…" : collapsed;
        }

        /// <summary>목록 미리보기용 축소본. 원본은 재복사 정확도를 위해 그대로 보관한다.</summary>
        private static BitmapSource? ThumbnailFor(ClipboardItem item)
        {
            if (item.Thumbnail != null || item.Image == null) return item.Thumbnail;

            const int MaxSide = 96;
            try
            {
                var image = item.Image;
                double scale = Math.Min(1.0, (double)MaxSide / Math.Max(image.PixelWidth, image.PixelHeight));
                BitmapSource thumb = scale >= 1.0
                    ? image
                    : new TransformedBitmap(image, new ScaleTransform(scale, scale));
                thumb.Freeze();
                item.Thumbnail = thumb;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Clipboard thumbnail failed: {ex.Message}");
            }
            return item.Thumbnail;
        }

        /// <summary>항목을 클립보드에 올린다. 다른 앱이 쥐고 있으면 짧게 재시도.</summary>
        private bool CopyToClipboard(ClipboardItem item)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    if (item.IsImage && item.Image != null)
                    {
                        Clipboard.SetImage(item.Image);
                    }
                    else if (item.IsFiles)
                    {
                        var list = new StringCollection();
                        list.AddRange(item.Files!);
                        Clipboard.SetFileDropList(list);
                    }
                    else if (!string.IsNullOrWhiteSpace(item.Text))
                    {
                        Clipboard.SetText(item.Text);
                    }
                    else
                    {
                        return false;
                    }

                    _selfCopySignature = Signature(item);
                    _selfCopyAt = DateTime.UtcNow;
                    return true;
                }
                catch (COMException)
                {
                    Thread.Sleep(25);
                }
                catch
                {
                    return false;
                }
            }
            Log.Warn("Clipboard copy failed: clipboard locked by another app");
            return false;
        }

        /// <summary>항목 사용: 클립보드에 올리고 → 최근 순서 맨 앞으로 → 직전 창에 붙여넣기.</summary>
        private void UseClipboardItem(ClipboardItem item)
        {
            if (!CopyToClipboard(item)) return;

            // 재사용한 항목도 최근 순서 맨 앞으로 (다시 쓰기 쉬운 위치로)
            _clipboardHistory.Remove(item);
            _clipboardHistory.Insert(0, item);
            _clipboardSelectedIndex = -1;

            HideClipboardHistoryPanel();
            PasteIntoPasteTarget();
        }

        /// <summary>직전 창으로 포커스를 돌린 뒤 Ctrl+V를 주입한다. 실패하면 복사만 남긴다.</summary>
        private async void PasteIntoPasteTarget()
        {
            IntPtr target = _pasteTargetHwnd;
            if (_pasteInFlight || target == IntPtr.Zero || !IsWindow(target))
            {
                // 붙여넣을 창을 모를 때(복사 순간에 전경 창이 없었을 때) — 복사만 해 둔다
                Log.Info("Clipboard paste: no target window, kept clipboard only");
                return;
            }

            _pasteInFlight = true;
            try
            {
                // 전경 창 전환은 비동기라 실제로 넘어갈 때까지 짧게 기다린다 (최대 ~300ms)
                for (int i = 0; i < 12 && GetForegroundWindow() != target; i++)
                {
                    SetForegroundWindow(target);
                    await Task.Delay(25);
                }

                if (GetForegroundWindow() != target)
                {
                    // 정책상 포커스를 되찾지 못한 경우 — 복사는 이미 됐으므로 사용자가 직접 Ctrl+V 하면 된다
                    Log.Info("Clipboard paste: target refused focus, kept clipboard only");
                    return;
                }

                await Task.Delay(60);   // 활성화 직후 앱이 키 입력을 받을 준비를 할 여유
                SendCtrlV();
                Log.Info("Clipboard paste: Ctrl+V sent");
            }
            catch (Exception ex)
            {
                Log.Warn($"Clipboard paste failed: {ex.Message}");
            }
            finally
            {
                _pasteInFlight = false;
            }
        }

        private static void SendCtrlV()
        {
            var inputs = new[]
            {
                KeyInput(VK_CONTROL, down: true),
                KeyInput(VK_V, down: true),
                KeyInput(VK_V, down: false),
                KeyInput(VK_CONTROL, down: false),
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        }

        private static INPUT KeyInput(ushort vk, bool down) => new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = down ? 0u : KEYEVENTF_KEYUP } },
        };

        // 이미지 저해상도 평균 해시 (aHash): 크기/포맷이 달라도 같은 그림이면 같은 해시
        private static byte[] ComputeImageHash(BitmapSource image)
        {
            const int Size = 8;

            // 비정상 이미지 방어
            if (image.PixelWidth <= 0 || image.PixelHeight <= 0)
                return Array.Empty<byte>();

            var scaled = new TransformedBitmap(image, new ScaleTransform(
                (double)Size / image.PixelWidth, (double)Size / image.PixelHeight));
            var converted = new FormatConvertedBitmap(scaled, PixelFormats.Gray8, null, 0);

            // Gray8 = 픽셀당 1바이트 → stride = 폭 (바이트 단위)
            int width = converted.PixelWidth;   // 8
            int height = converted.PixelHeight; // 8
            int stride = width;                 // Gray8: 1 byte per pixel
            byte[] pixels = new byte[height * stride];
            converted.CopyPixels(pixels, stride, 0);

            // 평균 밝기보다 크면 1, 작으면 0 → 64비트 해시
            long sum = 0;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                    sum += pixels[y * stride + x];
            double avg = (double)sum / (width * height);

            byte[] hash = new byte[8];
            int bit = 0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (pixels[y * stride + x] > avg)
                        hash[bit / 8] |= (byte)(1 << (bit % 8));
                    bit++;
                }
            }
            return hash;
        }

        private static bool HashEquals(byte[] a, byte[] b)
        {
            if (a.Length == 0 || b.Length == 0) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++)
            {
                int x = a[i] ^ b[i];
                while (x != 0) { diff += x & 1; x >>= 1; } // Hamming distance
            }
            return diff <= 2; // 약간의 압축/리샘플 차이는 허용
        }

        private void ClipboardToastTimer_Tick(object? sender, EventArgs e)
        {
            _clipboardToastTimer!.Stop();
            HideClipboardFeedback();
        }

        // ── 복사 피드백: 아일랜드 안쪽 배지 + 가장자리 빛 펄스 ──

        /// <summary>복사 직후의 조용한 알림 — 떠 있는 창을 뛰우지 않고 노치 안에서만 알린다.</summary>
        private void ShowClipboardFeedback()
        {
            ClipboardFeedbackChip.Visibility = Visibility.Visible;
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, null);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 4 };
            var fadeIn = new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(130))) { EasingFunction = ease };
            var popIn = new DoubleAnimation(0.65, 1, new Duration(TimeSpan.FromMilliseconds(240))) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fadeIn, 60);
            Timeline.SetDesiredFrameRate(popIn, 60);
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleXProperty, popIn);
            ClipboardFeedbackScale.BeginAnimation(ScaleTransform.ScaleYProperty, popIn);

            // 가장자리 빛 펄스 — 복사된 것을 "빛으로" 알린다
            FlashNotchEdge();

            // 재시작 가능한 일회성 타이머 — 연속 복사 시 경쟁 상태 없이 사라지는 시점만 미룬다
            if (_clipboardToastTimer == null)
            {
                _clipboardToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1300) };
                _clipboardToastTimer.Tick += ClipboardToastTimer_Tick;
            }
            _clipboardToastTimer.Stop();
            _clipboardToastTimer.Start();
        }

        private void HideClipboardFeedback()
        {
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 3 };
            var fade = new DoubleAnimation(0, new Duration(TimeSpan.FromMilliseconds(220))) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(fade, 60);
            fade.Completed += (s, e) =>
            {
                // 그사이 또 복사됐으면 그대로 둔다
                if (_clipboardToastTimer == null || !_clipboardToastTimer.IsEnabled)
                    ClipboardFeedbackChip.Visibility = Visibility.Collapsed;
            };
            ClipboardFeedbackChip.BeginAnimation(UIElement.OpacityProperty, fade);
        }

        // 복사 알림 · 기록패널 (Ctrl+Shift+V)

        private bool _clipboardHistoryOpen;

        // 키보드(↑↓/Enter/Delete)로 고른 항목 — -1이면 선택 없음
        private int _clipboardSelectedIndex = -1;

        /// <summary>기록 패널 열기/닫기 — 단축키와 노치 클릭이 공유하는 입구.</summary>
        private void ToggleClipboardHistory()
        {
            if (_clipboardHistoryOpen)
            {
                HideClipboardHistoryPanel();
            }
            else if (_clipboardHistory.Count > 0)
            {
                ShowClipboardHistoryPanel();
            }
        }

        /// <summary>기록 패널을 연다 — 노치 아래 중앙에 내려온다.</summary>
        /// <summary>노치 본체 클릭 → 기록 열기/닫기. 확장 뷰의 버튼들은 자기 클릭을 먼저 처리한다.</summary>
        private void Notch_Click(object sender, MouseButtonEventArgs e)
        {
            if (_currentViewMode == ViewMode.Assistant) return;   // 대화 중엔 입력을 가로채지 않는다
            if (_clipboardHistory.Count == 0) return;

            ToggleClipboardHistory();
            e.Handled = true;
        }

        private void ShowClipboardHistoryPanel()
        {
            BuildClipboardHistoryRows();
            _clipboardHistoryOpen = true;

            _clipboardToastTimer?.Stop();

            ClipboardHistoryPanel.Visibility = Visibility.Visible;
            // 먼저 배치해야 패널 실제 크기가 나온다 — 그 전에 계산하면 좌상단(0,0)에 붙는다
            ClipboardHistoryPanel.UpdateLayout();
            RepositionClipboardHistoryPanel();
            var ease = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 };
            var dur = new Duration(TimeSpan.FromMilliseconds(280));
            var opacity = new DoubleAnimation(0, 1, dur) { EasingFunction = ease };
            var slide = new DoubleAnimation(-8, 0, dur) { EasingFunction = ease };
            Timeline.SetDesiredFrameRate(opacity, 60);
            Timeline.SetDesiredFrameRate(slide, 60);
            ClipboardHistoryPanel.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardHistoryTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        private void HideClipboardHistoryPanel()
        {
            if (!_clipboardHistoryOpen) return;
            _clipboardHistoryOpen = false;

            var dur = new Duration(TimeSpan.FromMilliseconds(200));
            var opacity = new DoubleAnimation(0, dur);
            opacity.Completed += (s, e) =>
            {
                if (!_clipboardHistoryOpen) ClipboardHistoryPanel.Visibility = Visibility.Collapsed;
            };
            ClipboardHistoryPanel.BeginAnimation(UIElement.OpacityProperty, opacity);
            ClipboardHistoryTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(-8, dur));
        }

        private void BuildClipboardHistoryRows()
        {
            ClipboardHistoryRows.Children.Clear();
            int count = Math.Min(_clipboardHistory.Count, HistoryLimit);
            for (int i = 0; i < count; i++)
            {
                ClipboardHistoryRows.Children.Add(BuildClipboardHistoryRow(_clipboardHistory[i], i));
            }
        }

        private Border BuildClipboardHistoryRow(ClipboardItem item, int index)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // 이미지 항목은 썸네일로 — 캡처가 여러 장이어도 구분된다
            BitmapSource? thumb = ThumbnailFor(item);
            if (thumb != null)
            {
                var preview = new Image
                {
                    Source = thumb,
                    Height = 30,
                    MaxWidth = 48,
                    Stretch = Stretch.Uniform,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 9, 0),
                    IsHitTestVisible = false,
                };
                RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);
                Grid.SetColumn(preview, 0);
                grid.Children.Add(preview);
            }

            var text = new TextBlock
            {
                Text = ClipboardRowLabel(item),
                Foreground = new SolidColorBrush(Color.FromArgb(0xE5, 0xFF, 0xFF, 0xFF)),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            // 개별 삭제 — 호버할 때만 보이는 최소 표시
            var remove = new TextBlock
            {
                Text = "✕",
                FontSize = 10,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 2, 0),
                Cursor = Cursors.Hand,
                Opacity = 0,
                ToolTip = "이 항목 삭제",
            };
            remove.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;   // 행 클릭(붙여넣기)으로 번지지 않게
                RemoveFromHistory(item);
            };
            Grid.SetColumn(remove, 2);
            grid.Children.Add(remove);

            var row = new Border
            {
                Background = index == _clipboardSelectedIndex ? SelectedRowBrush : Brushes.Transparent,
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 1, 0, 1),
                Cursor = Cursors.Hand,
                Tag = item,
                Child = grid,
            };
            row.MouseLeftButtonUp += ClipboardHistoryRow_Click;
            row.MouseEnter += (s, e) =>
            {
                remove.Opacity = 1;
                row.Background = HoverRowBrush;
            };
            row.MouseLeave += (s, e) =>
            {
                remove.Opacity = 0;
                row.Background = index == _clipboardSelectedIndex ? SelectedRowBrush : Brushes.Transparent;
            };
            return row;
        }

        private static readonly Brush HoverRowBrush = new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF));
        private static readonly Brush SelectedRowBrush = new SolidColorBrush(Color.FromArgb(0x33, 0x0A, 0x84, 0xFF));

        private void ClipboardHistoryRow_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.Tag is ClipboardItem item)
            {
                UseClipboardItem(item);
            }
        }

        private void RemoveFromHistory(ClipboardItem item)
        {
            _clipboardHistory.Remove(item);
            _clipboardSelectedIndex = -1;

            if (_clipboardHistory.Count == 0)
            {
                HideClipboardHistoryPanel();
                return;
            }
            BuildClipboardHistoryRows();
        }

        private void RemoveSelectedFromHistory()
        {
            if (_clipboardSelectedIndex < 0 || _clipboardSelectedIndex >= _clipboardHistory.Count) return;
            RemoveFromHistory(_clipboardHistory[_clipboardSelectedIndex]);
        }

        private void ClipboardClearButton_Click(object sender, RoutedEventArgs e)
        {
            _clipboardHistory.Clear();
            _clipboardSelectedIndex = -1;
            HideClipboardHistoryPanel();
        }

        private void MoveClipboardSelection(int delta)
        {
            if (_clipboardHistory.Count == 0) return;
            int current = _clipboardSelectedIndex < 0 ? (delta > 0 ? -1 : _clipboardHistory.Count) : _clipboardSelectedIndex;
            _clipboardSelectedIndex = Math.Clamp(current + delta, 0, _clipboardHistory.Count - 1);
            BuildClipboardHistoryRows();
        }

        private void ActivateClipboardSelection()
        {
            if (_clipboardSelectedIndex < 0 || _clipboardSelectedIndex >= _clipboardHistory.Count) return;
            UseClipboardItem(_clipboardHistory[_clipboardSelectedIndex]);
        }


        /// <summary>기록 패널을 노치 아래 중앙에 내린다.</summary>
        private void RepositionClipboardHistoryPanel()
        {
            if (ClipboardHistoryPanel.Visibility != Visibility.Visible) return;
            try
            {
                double scaleX = VisualTreeHelper.GetDpi(this).DpiScaleX;
                double scaleY = VisualTreeHelper.GetDpi(this).DpiScaleY;

                // 물리 크기 정규화 — 어느 배율에서도 같은 크기로 보이게
                double panelScale = scaleY > 0 ? Math.Min(1.75 / scaleY, 2.2) : 1.0;
                ClipboardHistoryPanel.LayoutTransform = new ScaleTransform(panelScale, panelScale);
                double panelWidth = ClipboardHistoryPanel.ActualWidth * panelScale;

                Point notchBottom = NotchBorder.PointToScreen(new Point(NotchBorder.ActualWidth / 2, NotchBorder.ActualHeight));
                Point origin = RootGrid.PointToScreen(new Point(0, 0));
                double left = (notchBottom.X - origin.X) / scaleX - panelWidth / 2;

                // 화면 밖으로 밀려나지 않게 좌우를 눌러 둔다
                double screenWidth = SystemParameters.PrimaryScreenWidth;
                left = Math.Clamp(left, 8, Math.Max(8, screenWidth - panelWidth - 8));

                Canvas.SetLeft(ClipboardHistoryPanel, left);
                Canvas.SetTop(ClipboardHistoryPanel, (notchBottom.Y - origin.Y) / scaleY + 10);

                Log.Info("Clipboard panel layout: "
                    + $"scale={scaleX:F2} panelScale={panelScale:F2} "
                    + $"panel={ClipboardHistoryPanel.ActualWidth:F0}x{ClipboardHistoryPanel.ActualHeight:F0} "
                    + $"notch={notchBottom.X:F0},{notchBottom.Y:F0} origin={origin.X:F0},{origin.Y:F0} "
                    + $"left={Canvas.GetLeft(ClipboardHistoryPanel):F0} top={Canvas.GetTop(ClipboardHistoryPanel):F0}");
            }
            catch
            {
                Canvas.SetLeft(ClipboardHistoryPanel, ActualWidth / 2 - ClipboardHistoryPanel.ActualWidth / 2);
                Canvas.SetTop(ClipboardHistoryPanel, 60);
            }
        }

    }
}
