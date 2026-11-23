using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace 通用模拟器
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : Application
    {
        public App()
        {
            // UI 线程未处理异常
            DispatcherUnhandledException += App_DispatcherUnhandledException;
            // 后台 Task 中未被观察的异常（不处理会在 GC 时终止进程）
            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                AppLogger.Exception("未观察的后台任务异常", e.Exception, "TaskScheduler.UnobservedTaskException");
                e.SetObserved();
            };
            // 非 UI 线程兜底
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                AppLogger.Error($"未处理的非 UI 线程异常：{e.ExceptionObject}", "AppDomain.UnhandledException");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
            AppLogger.Info($"应用启动，日志文件：{AppLogger.CurrentLogFile}", "App.OnStartup");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLogger.Info("应用退出", "App.OnExit");
            AppLogger.Shutdown();
            base.OnExit(e);
        }

        void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            AppLogger.Exception("UI 线程未处理异常", e.Exception, nameof(App_DispatcherUnhandledException));
            MessageBox.Show(e.Exception.Message, "未处理的异常", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;// 记录后继续运行，避免直接崩溃
        }
    }

}
