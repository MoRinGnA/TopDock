using System;
using System.Configuration;
using System.Data;
using System.Windows;
using System.Windows.Threading;
using TopDock.Services;

namespace TopDock;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // UI 스레드에서 처리되지 않은 예외
        this.DispatcherUnhandledException += App_DispatcherUnhandledException;

        // UI 스레드가 아닌 곳(백그라운드 Task 등)에서 처리되지 않은 예외
        AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

        // async void 메서드 등에서 관찰되지 않은 Task 예외
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;
    }

    /// <summary>
    /// 예외를 보여 주는 자리는 비서 화면이지 모달창이 아니다.
    ///
    /// 예전에는 어떤 예외든 MessageBox로 띄웠다 — 배경 이벤트 하나만 어긋나도(예: 닫히는 중
    /// 도착한 COM 콜백) 화면 한가운데 모달창이 떠서 앱이 멈춘 것처럼 보였고, 사용자가
    /// 할 수 있는 일은 '확인'뿐이었다. 지금은 로그로 남기고 개발 빌드에서만 창을 띄운다.
    /// </summary>
    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled UI exception", e.Exception);
        ShowInDebug($"처리되지 않은 예외 발생:\n\n{e.Exception}");
        e.Handled = true; // 창을 살려 둔다 — 노치 앱은 스스로 사라지는 것보다 남는 편이 낫다
    }

    private void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Log.Error($"Fatal exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        ShowInDebug($"치명적 예외 발생:\n\n{e.ExceptionObject}");
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, System.Threading.Tasks.UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Background task exception", e.Exception);
        ShowInDebug($"백그라운드 작업 예외:\n\n{e.Exception}");
        e.SetObserved();
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void ShowInDebug(string message)
    {
        MessageBox.Show(message, "TopDock - 오류", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
