using System.Windows;

namespace TopDock
{
    public partial class MainWindow : Window
    {
        /// <summary>전역 핫키 회귀 하네스(tests/TopDock.Tests)가 앱을 조종할 때 넘기는 인자.</summary>
        private const string HotkeySelfTestSwitch = "--selftest-hotkeys";

        /// <summary>
        /// 이 실행이 핫키 회귀 하네스가 부른 것인가. 명령줄은 여기서만 본다 — 보통 실행(창을 띄우고
        /// 노치로 사는 길)에는 아무 영향이 없다.
        /// </summary>
        private static bool IsHotkeySelfTestRun()
            => Environment.GetCommandLineArgs().Any(arg =>
                   string.Equals(arg, HotkeySelfTestSwitch, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// 핫키 회귀 하네스를 위해 잠깐 서 있는다 — 앱에게 "설정 적용을 대신 눌러 주고, 그동안
        /// 나를 살려 둬라"고 부탁하는 통로다.
        ///
        /// 핫키는 창(WPF) 안에서만 등록된다. 창을 만들 수 없는 화면 밖 테스트 프로젝트에서는 이 길을
        /// 부를 방법이 없어서, 앱을 그대로 띄운 뒤 <see cref="ApplySettings"/> — 설정 창의 '적용'이
        /// 부르는 바로 그 함수 — 를 밖에서 한 번 눌러 달라고 한다.
        ///
        /// 판정은 여기서 하지 않는다. 앱이 자기 핫키를 스스로 채점하면 "살아 있다"의 근거가 앱 자신이
        /// 되어 버린다. 살아 있는지는 하네스가 자기 프로세스에서 RegisterHotKey로 직접 물어본다
        /// (같은 세션에서 그 조합을 잡을 수 있으면 아무도 — 우리 앱도 — 그걸 쥐고 있지 않다는 뜻이다).
        ///
        /// 프로토콜(줄 단위, 부모가 리다이렉트한 표준 입출력 파이프):
        /// <code>
        ///   앱    → READY    창이 섰고 시작 핫키를 걸었다
        ///   하네스 → apply   ApplySettings() 한 번
        ///   앱    → APPLIED  끝났다
        ///   하네스 → quit    정상 종료(exit 0)
        /// </code>
        /// </summary>
        private void StartHotkeySelfTest()
        {
            Console.Out.WriteLine("READY");
            Console.Out.Flush();

            // 입력 읽기를 UI 스레드에서 하면 안 된다 — 붙잡고 있으면 창이 메시지를 못 돌려
            // 핫키는 물론 ApplySettings(UpdatePosition)도 멈춘다.
            _ = Task.Run(() =>
            {
                string? line;
                while ((line = Console.In.ReadLine()) != null)
                {
                    switch (line.Trim().ToLowerInvariant())
                    {
                        case "apply":
                            Dispatcher.Invoke(ApplySettings);
                            Console.Out.WriteLine("APPLIED");
                            Console.Out.Flush();
                            break;

                        case "quit":
                            Console.Out.WriteLine("BYE");
                            Console.Out.Flush();
                            Dispatcher.Invoke(() => Application.Current.Shutdown(0));
                            return;
                    }
                }

                // 입력이 닫혔다 = 하네스가 죽었거나 끝났는데 quit을 받지 못했다. 그대로 살아 있으면
                // 이 앱이 전역 핫키를 쥔 채 좀비로 남아, 다음 테스트는 물론 사용자의 진짜 TopDock까지
                // '이미 다른 앱이 쥐고 있다'며 조용히 등록에 실패한다. 부모가 사라졌으면 같이 사라진다.
                Dispatcher.Invoke(() => Application.Current.Shutdown(0));
            });
        }
    }
}
