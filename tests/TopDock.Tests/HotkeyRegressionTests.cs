using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TopDock.Logic;
using Xunit.Abstractions;

namespace TopDock.Tests
{
    /// <summary>
    /// 설정을 적용한 뒤에도 전역 핫키가 살아 있는지 <b>진짜 앱을 띄워</b> 확인한다.
    ///
    /// 왜 화면 밖 테스트로는 안 되는가. 핫키는 창(WPF)의 HWND에만 걸린다 — 창을 만들 수 없는
    /// 순수 로직 프로젝트에서는 부를 길이 없다. 그래서 앱에게 통로를 하나 열어 두고
    /// (<c>--selftest-hotkeys</c>: 창이 서면 READY를 알리고, 표준 입력으로 온 'apply'마다
    /// 설정 창의 '적용'이 부르는 바로 그 함수를 돈다), 하네스는 앱을 띄운 채로
    /// <b>자기 프로세스에서</b> RegisterHotKey를 시도해 본다.
    ///
    /// 판정이 왜 밖에서 이뤄지는가. 같은 세션에서 그 조합을 잡을 수 있다면 아무도 — 우리 앱도 —
    /// 그걸 쥐고 있지 않다는 뜻이다. 앱이 자기 등록 성공 여부를 스스로 보고하면 근거가 앱 자신이
    /// 되어 버리므로, 이 테스트는 OS에게 직접 묻는다. 적용 전에도, 적용 뒤에도.
    ///
    /// 이 테스트가 잡는 것: 적용이 핫키 하나를 조용히 죽여 놓는 회귀(13번). 화면에는 아무 흔적이
    /// 없어서 사용자가 한참 뒤에야 알아차리던 종류다.
    ///
    /// 요구 사항: Windows + 대화형 데스크톱, 그리고 <b>실행 중인 TopDock이 없어야 한다</b>
    /// (둘은 같은 전역 핫키를 쥘 수 없다 — 그때는 원인을 정확히 말하고 실패한다).
    /// 앱 실행 파일이 필요하므로 돌리기 전에 앱을 빌드한다(<c>dotnet build</c>) — CI도 그 순서다.
    /// </summary>
    public class HotkeyRegressionTests
    {
        private readonly ITestOutputHelper _output;

        public HotkeyRegressionTests(ITestOutputHelper output) => _output = output;

        /// <summary>앱이 뜨고 창이 HWND를 얻기까지 기다리는 시간(콜드 스타트 포함).</summary>
        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

        /// <summary>apply/quit 명령 한 번에 돌아와야 하는 시간.</summary>
        private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);

        [Fact]
        public void HotkeysStayAliveAfterSettingsApply()
        {
            Assert.True(OperatingSystem.IsWindows(),
                "전역 핫키는 Windows에서만 검증된다 — 이 플랫폼에서는 검증할 수 없으므로 통과로 위장하지 않는다.");

            Process[] running = Process.GetProcessesByName("TopDock");
            try
            {
                Assert.True(running.Length == 0,
                    "이미 실행 중인 TopDock이 있다 — 그 인스턴스가 전역 핫키를 쥐고 있어서 새 인스턴스는 " +
                    "하나도 걸지 못한다(그리고 이 테스트는 그 '조용한 실패'와 회귀를 구별할 수 없다). " +
                    "TopDock을 완전히 닫고 다시 돌려라.");
            }
            finally
            {
                foreach (Process p in running) p.Dispose();
            }

            string exe = ResolveAppExe();

            // 앱이 보는 것과 같은 설정 파일에서 같은 값을 읽는다 — 비서 자리는 설정으로 꺼질 수 있다.
            bool assistantEnabled = ReadAssistantEnabled();
            IReadOnlyList<HotkeySlot> desired = HotkeyPlan.Desired(assistantEnabled);
            _output.WriteLine($"앱: {exe}");
            _output.WriteLine($"비서 핫키: {(assistantEnabled ? "켜짐" : "꺼짐")} — 확인할 자리: {string.Join(", ", desired)}");

            using var app = new SelfTestApp(exe);

            var before = new Dictionary<HotkeySlot, bool>();
            var after = new Dictionary<HotkeySlot, bool>();

            // 1) 창이 서고 시작 핫키가 걸릴 때까지
            Assert.True(app.Expect("READY", StartupTimeout),
                $"앱이 READY를 알리지 않았다(시작 실패 또는 크래시).\n{app.Diagnostics()}");

            foreach (HotkeySlot slot in desired) before[slot] = IsHeldBySomeoneElse(slot);

            // 적용 전에 안 걸려 있으면 이 테스트는 아무것도 증명할 수 없다 — 조용히 넘어가지 않는다.
            Assert.True(before.Values.All(alive => alive),
                "적용 전에 이미 안 걸려 있는 전역 핫키가 있다: " +
                $"{string.Join(", ", before.Where(p => !p.Value).Select(p => $"{p.Key} ({DescribeCombo(p.Key)})"))}. " +
                "다른 앱이 그 조합을 쥐고 있거나(그러면 앱 로그에 'already taken by another app'이 남는다), " +
                "핫키 조합이 바뀌어 이 테스트의 표가 낡았거나, 대화형 데스크톱이 아니다.");

            // 2) 설정 창의 '적용'이 부르는 그 경로를 돈다
            app.Send("apply");
            Assert.True(app.Expect("APPLIED", CommandTimeout),
                $"ApplySettings가 돌아오지 않았다.\n{app.Diagnostics()}");

            foreach (HotkeySlot slot in desired) after[slot] = IsHeldBySomeoneElse(slot);

            // 3) 판정 — 적용 전에 살아 있던 자리가 적용 뒤에도 살아 있어야 한다
            HotkeyAudit audit = HotkeyPlan.Audit(before, after, assistantEnabled);
            _output.WriteLine($"적용 전: {Format(before)}");
            _output.WriteLine($"적용 뒤: {Format(after)}");

            Assert.True(audit.Ok,
                $"설정 적용이 전역 핫키를 죽였다 — {audit.Describe()}. " +
                "RegisterGlobalHotkeys가 함께 내린 것을 함께 올리는지 보라(내린 채 두면 그 단축기는 영영 듣지 않는다).");

            // 4) 깔끔하게 닫혔는가 — 여기서 실패하면 앱이 아직 무언가 잡고 있다
            app.Send("quit");
            Assert.True(app.WaitForExit(CommandTimeout), $"앱이 종료되지 않았다.\n{app.Diagnostics()}");
            Assert.Equal(0, app.ExitCode);
        }

        /// <summary>
        /// 지금 이 조합을 다른 누군가(우리 앱 포함)가 쥐고 있는가.
        ///
        /// 핫키 표는 세션 전역이라, 자기 프로세스에서 같은 조합을 걸어 보면 알 수 있다: 성공하면
        /// 아무도 안 쥐고 있다는 뜻이므로 <b>즉시 풀어 놓는다</b>(빌린 것을 그대로 돌려준다).
        /// 실패하면 이미 누가 쥐고 있고, 그 오류는 반드시 ERROR_HOTKEY_ALREADY_REGISTERED여야 한다 —
        /// 다른 오류(권한 등)를 '쥐고 있음'으로 읽으면 거짓 통과가 된다.
        /// </summary>
        private static bool IsHeldBySomeoneElse(HotkeySlot slot)
        {
            const int ProbeId = 0x0F00D;   // 이 프로세스 안에서만 쓰는 임시 아이디
            (uint modifiers, uint key) = Combo(slot);

            if (RegisterHotKey(IntPtr.Zero, ProbeId, modifiers, key))
            {
                UnregisterHotKey(IntPtr.Zero, ProbeId);
                return false;
            }

            int error = Marshal.GetLastWin32Error();
            Assert.True(error == ErrorHotkeyAlreadyRegistered,
                $"{slot} 조합을 확인하려던 RegisterHotKey가 예상 밖의 오류로 실패했다: {error}");
            return true;
        }

        // 앱이 거는 조합과 같은 값이어야 한다(MainWindow의 HOTKEY_ID_* / RegisterHotKey 호출).
        // 조합을 바꾸면 여기도 바꾼다 — 어긋나면 '적용 전에 이미 안 걸려 있다'로 시끄럽게 실패한다.
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_NOREPEAT = 0x4000;
        private const int ErrorHotkeyAlreadyRegistered = 1409;

        private static (uint Modifiers, uint Key) Combo(HotkeySlot slot) => slot switch
        {
            HotkeySlot.Clipboard => (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x56),   // Ctrl+Shift+V
            HotkeySlot.Assistant => (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x20),   // Ctrl+Shift+Space
            HotkeySlot.Inbox => (MOD_CONTROL | MOD_SHIFT | MOD_NOREPEAT, 0x4E),       // Ctrl+Shift+N
            _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, "조합을 모르는 자리다"),
        };

        private static string DescribeCombo(HotkeySlot slot) => slot switch
        {
            HotkeySlot.Clipboard => "Ctrl+Shift+V",
            HotkeySlot.Assistant => "Ctrl+Shift+Space",
            HotkeySlot.Inbox => "Ctrl+Shift+N",
            _ => slot.ToString(),
        };

        private static string Format(IReadOnlyDictionary<HotkeySlot, bool> map)
            => string.Join(", ", map.Select(p => $"{DescribeCombo(p.Key)}={(p.Value ? "살아 있음" : "죽음")}"));

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        /// <summary>앱이 보는 것과 같은 설정 파일(%APPDATA%\TopDock\config.json)에서 비서 스위치를 읽는다.</summary>
        private static bool ReadAssistantEnabled()
        {
            try
            {
                string path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TopDock", "config.json");
                if (!File.Exists(path)) return true;   // 설정 파일이 없으면 앱과 같은 기본값

                using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
                return !doc.RootElement.TryGetProperty("assistantEnabled", out JsonElement value)
                    || value.ValueKind != JsonValueKind.False;
            }
            catch
            {
                return true;   // 못 읽으면 기본값 — 어차피 앱도 기본값으로 시작한다
            }
        }

        /// <summary>
        /// 검증할 앱 실행 파일. 저장소 루트의 bin 아래에서 찾되, 지금 테스트가 빌드된 구성(Debug/Release)을
        /// 먼저 본다. 그리고 <b>소스보다 오래된 실행 파일은 쓰지 않는다</b> — 어제 빌드해 둔 앱을 상대로
        /// 초록불을 보는 것이 이 테스트에서 가장 나쁜 거짓말이다.
        /// (환경 변수 TOPDOCK_APP_EXE로 직접 지정할 수도 있다.)
        /// </summary>
        private string ResolveAppExe()
        {
            string root = FindRepoRoot();
            string? fix = Environment.GetEnvironmentVariable("TOPDOCK_APP_EXE");
            if (!string.IsNullOrWhiteSpace(fix))
            {
                Assert.True(File.Exists(fix), $"TOPDOCK_APP_EXE가 가리키는 파일이 없다: {fix}");
                return fix;
            }

            string binDir = Path.Combine(root, "bin");
            Assert.True(Directory.Exists(binDir),
                $"앱 실행 파일을 찾지 못했다({binDir}가 없다). 먼저 앱을 빌드하라: dotnet build");

            DateTime newestSource = NewestSourceTime(root);
            List<(string Path, DateTime Time)> found = Directory
                .EnumerateFiles(binDir, "TopDock.exe", SearchOption.AllDirectories)
                .Select(path => (Path: path, Time: File.GetLastWriteTimeUtc(path)))
                .ToList();

            Assert.True(found.Count > 0,
                $"앱 실행 파일을 찾지 못했다({binDir} 아래에 TopDock.exe가 없다). 먼저 앱을 빌드하라: dotnet build");

            string preferred = PreferredConfiguration();
            List<(string Path, DateTime Time)> fresh = found.Where(f => f.Time >= newestSource).ToList();

            // 1순위: 소스보다 새것 중 지금 구성 2순위: 소스보다 새것 아무거나
            // 3순위: 지금 구성의 낡은 것 4순위: 그냥 가장 최근 것 — 뒤의 둘은 아래에서 실패한다
            (string Path, DateTime Time) pick =
                fresh.Where(f => IsInConfiguration(f.Path, preferred)).OrderByDescending(f => f.Time).FirstOrDefault();
            if (pick.Path == null) pick = fresh.OrderByDescending(f => f.Time).FirstOrDefault();
            if (pick.Path == null)
                pick = found.Where(f => IsInConfiguration(f.Path, preferred)).OrderByDescending(f => f.Time).FirstOrDefault();
            if (pick.Path == null) pick = found.OrderByDescending(f => f.Time).First();

            Assert.True(pick.Time >= newestSource,
                $"앱 실행 파일이 소스보다 오래됐다({pick.Path}, {pick.Time:u} < 소스 {newestSource:u}). " +
                "낡은 앱을 상대로 통과해 봐야 뜻이 없으니 다시 빌드하라: dotnet build");

            return pick.Path;
        }

        /// <summary>테스트 자신이 빌드된 구성과 같은 구성의 앱을 먼저 본다.</summary>
        private static string PreferredConfiguration()
        {
#if DEBUG
            return "Debug";
#else
            return "Release";
#endif
        }

        private static bool IsInConfiguration(string exePath, string configuration)
            => exePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                      .Any(part => part.Equals(configuration, StringComparison.OrdinalIgnoreCase));

        /// <summary>앱을 이루는 파일 중 가장 최근에 손댄 것 — 실행 파일이 이보다 새야 한다.</summary>
        private static DateTime NewestSourceTime(string root)
        {
            DateTime newest = DateTime.MinValue;
            foreach (string pattern in new[] { "*.cs", "*.xaml", "*.csproj" })
            {
                foreach (string file in Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
                {
                    if (IsBuildOutput(root, file)) continue;
                    DateTime time = File.GetLastWriteTimeUtc(file);
                    if (time > newest) newest = time;
                }
            }
            return newest;
        }

        /// <summary>빌드 산출물과 다른 프로젝트의 소스는 '앱 소스'가 아니다.</summary>
        private static bool IsBuildOutput(string root, string file)
        {
            string[] parts = Path.GetRelativePath(root, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            return parts.Any(part =>
                part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || part.Equals("obj", StringComparison.OrdinalIgnoreCase)
                || part.Equals("tests", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>테스트 출력 폴더에서 위로 올라가 앱 프로젝트(TopDock.csproj)를 찾는다.</summary>
        private static string FindRepoRoot()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TopDock.csproj"))) return dir.FullName;
                dir = dir.Parent;
            }
            throw new InvalidOperationException(
                $"저장소 루트를 찾지 못했다(TopDock.csproj가 위쪽에 없다). 시작 위치: {AppContext.BaseDirectory}");
        }

        /// <summary>
        /// 회귀 하네스가 조종하는 앱 한 벌 — 표준 입출력으로 줄 단위 대화를 나눈다
        /// (앱 쪽 프로토콜은 MainWindow.SelfTest.cs).
        /// </summary>
        private sealed class SelfTestApp : IDisposable
        {
            private readonly Process _process;
            private readonly BlockingCollection<string> _lines = new();
            private readonly StringBuilder _stderr = new();

            public SelfTestApp(string exe)
            {
                var startInfo = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                startInfo.ArgumentList.Add("--selftest-hotkeys");

                _process = Process.Start(startInfo)
                    ?? throw new InvalidOperationException($"앱을 시작하지 못했다: {exe}");

                // 줄을 읽는 일은 전용 스레드가 맡는다 — 한 줄씩 시간 제한을 두고 받으려면
                // 읽기가 테스트 스레드를 붙잡고 있으면 안 된다.
                var reader = new Thread(() =>
                {
                    try
                    {
                        string? line;
                        while ((line = _process.StandardOutput.ReadLine()) != null) _lines.Add(line);
                    }
                    catch (InvalidOperationException) { /* 프로세스가 죽어 파이프가 닫혔다 */ }
                    finally { _lines.CompleteAdding(); }
                })
                { IsBackground = true };
                reader.Start();

                var errorReader = new Thread(() =>
                {
                    try
                    {
                        string? line;
                        while ((line = _process.StandardError.ReadLine()) != null)
                        {
                            lock (_stderr) _stderr.AppendLine(line);
                        }
                    }
                    catch (InvalidOperationException) { }
                })
                { IsBackground = true };
                errorReader.Start();
            }

            public int ExitCode => _process.ExitCode;

            public void Send(string command)
            {
                _process.StandardInput.WriteLine(command);
                _process.StandardInput.Flush();
            }

            /// <summary>다음 줄이 정확히 <paramref name="expected"/>인가. 시간이 지나면 false.</summary>
            public bool Expect(string expected, TimeSpan timeout)
                => _lines.TryTake(out string? line, timeout) && line == expected;

            public bool WaitForExit(TimeSpan timeout) => _process.WaitForExit((int)timeout.TotalMilliseconds);

            public string Diagnostics()
            {
                string stderr;
                lock (_stderr) stderr = _stderr.ToString();

                string stdout = string.Join(Environment.NewLine, _lines.ToArray());
                return $"exit code={(_process.HasExited ? _process.ExitCode.ToString() : "아직 실행 중")}\n" +
                       $"stdout:\n{stdout}\nstderr:\n{stderr}";
            }

            public void Dispose()
            {
                try
                {
                    if (!_process.HasExited)
                    {
                        Send("quit");
                        if (!_process.WaitForExit(5000)) _process.Kill(entireProcessTree: true);
                    }
                }
                catch { /* 이미 죽었다면 정리할 것이 없다 */ }
                finally
                {
                    _process.Dispose();
                    _lines.Dispose();
                }
            }
        }
    }
}
