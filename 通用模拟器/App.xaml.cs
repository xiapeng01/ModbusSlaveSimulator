using System;
using System.Configuration;
using System.Data;
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
                Debug.WriteLine($"[UnobservedTask] {e.Exception}");
                e.SetObserved();
            };
            // 非 UI 线程兜底
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
                Debug.WriteLine($"[Unhandled] {e.ExceptionObject}");
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            ShutdownMode = ShutdownMode.OnMainWindowClose;
        }

        void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            Debug.WriteLine($"[Dispatcher] {e.Exception}");
            MessageBox.Show(e.Exception.Message, "未处理的异常", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;// 记录后继续运行，避免直接崩溃
        }
    }

}
