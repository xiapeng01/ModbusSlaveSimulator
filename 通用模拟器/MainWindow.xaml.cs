using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Shell;

namespace 通用模拟器
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        #region 最大化不遮挡任务栏（限制到显示器工作区）

        private const int WM_GETMINMAXINFO = 0x0024;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int left;
            public int top;
            public int right;
            public int bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ((HwndSource)PresentationSource.FromVisual(this)).AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_GETMINMAXINFO)
            {
                var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
                IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (monitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                    {
                        // 最大化时使用当前显示器的工作区（排除任务栏），支持多显示器
                        mmi.ptMaxPosition.x = Math.Abs(info.rcWork.left - info.rcMonitor.left);
                        mmi.ptMaxPosition.y = Math.Abs(info.rcWork.top - info.rcMonitor.top);
                        mmi.ptMaxSize.x = Math.Abs(info.rcWork.right - info.rcWork.left);
                        mmi.ptMaxSize.y = Math.Abs(info.rcWork.bottom - info.rcWork.top);
                        Marshal.StructureToPtr(mmi, lParam, true);
                        handled = true;
                    }
                }
            }
            return IntPtr.Zero;
        }

        #endregion

        protected override void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);
            if (DataContext is MainWindowViewModel vm)
            {
                vm.Dispose();      // 先停止并释放网络资源
                vm.SaveSetting();  // 再保存配置（此时 CanEdit 已恢复）
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Maximize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            var chrome = WindowChrome.GetWindowChrome(this);
            if (chrome is null) return;

            if (WindowState == WindowState.Maximized)
            {
                RootBorder.Margin = new Thickness(7);
                chrome.ResizeBorderThickness = new Thickness(0);
            }
            else
            {
                RootBorder.Margin = new Thickness(0);
                chrome.ResizeBorderThickness = new Thickness(6);
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {

        }

        // 串口下拉展开时重新枚举，支持运行期热插拔
        private void PortName_DropDownOpened(object sender, EventArgs e)
        {
            if (sender is System.Windows.Controls.ComboBox { DataContext: NetworkConfigModel network })
            {
                network.RefreshPortNames();
            }
        }

        private void lvStation_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if(sender is ListView lw)
            {
                var item = lw.SelectedItem as StationInformationModel;
                if(item is not null)
                {
                    var window = new Window1(item);
                    window.Owner = Application.Current.MainWindow;
                    window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
                    window.ShowDialog();
                }
            }
        }
    }
}
