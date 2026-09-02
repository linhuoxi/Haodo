using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace CLIProxyAPI_GUI
{
    internal static class DpiBootstrap
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiContext);

        [DllImport("shcore.dll")]
        private static extern int SetProcessDpiAwareness(ProcessDpiAwareness value);

        private enum ProcessDpiAwareness
        {
            ProcessDpiUnaware = 0,
            ProcessSystemDpiAware = 1,
            ProcessPerMonitorDpiAware = 2
        }

        private static readonly IntPtr DpiAwarenessContextPerMonitorAwareV2 = new(-4);

        // 注意：不再使用 [ModuleInitializer]（混淆器 3.0 beta 对模块初始化器调用点存在重命名缺陷，
        // 会生成无法解析的 <Module>.cctor 导致启动即崩），改由 App 静态构造函数在创建窗口前调用。
        internal static void Initialize()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2)) return;
            }
            catch (EntryPointNotFoundException) { }
            catch (DllNotFoundException) { }

            try
            {
                SetProcessDpiAwareness(ProcessDpiAwareness.ProcessPerMonitorDpiAware);
            }
            catch { }
        }
    }

    public partial class App : Application
    {
        static App()
        {
            // 在创建任何窗口前完成 DPI 感知初始化（替代 [ModuleInitializer]，
            // 避免混淆器对模块初始化器的重命名缺陷导致启动崩溃）
            DpiBootstrap.Initialize();
        }

        private static Mutex? _mutex;
        private static EventWaitHandle? _eventWaitHandle;
        private const string MutexName = "Local\\Haodo_SingleInstance_Mutex_V1";
        private const string EventName = "Local\\Haodo_SingleInstance_Event_V1";

        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        public static readonly uint WM_SHOWINSTANCE = RegisterWindowMessage("WM_SHOW_BALANCE_VIEWER_SINGLE_INSTANCE");
        private const int HWND_BROADCAST = 0xffff;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            try
            {
                _mutex = new Mutex(true, MutexName, out createdNew);
            }
            catch
            {
                createdNew = true;
            }

            if (!createdNew)
            {
                // 1. 通知已有进程唤醒并显示主窗口 (EventWaitHandle 机制，不受 UIPI 限制)
                try
                {
                    using var eventHandle = EventWaitHandle.OpenExisting(EventName);
                    eventHandle.Set();
                }
                catch { }

                // 2. 广播 Windows 消息作为备用机制
                PostMessage((IntPtr)HWND_BROADCAST, WM_SHOWINSTANCE, IntPtr.Zero, IntPtr.Zero);

                // 3. 强制干净退出多余进程，防止僵尸进程驻留
                Environment.Exit(0);
                return;
            }

            // 监听唤醒事件
            try
            {
                _eventWaitHandle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
                Task.Run(() =>
                {
                    while (_eventWaitHandle != null)
                    {
                        if (_eventWaitHandle.WaitOne())
                        {
                            Current?.Dispatcher.Invoke(() =>
                            {
                                // 主窗口已创建 → 立即唤醒到前台
                                if (Current.MainWindow is MainWindow mw)
                                {
                                    mw.ShowMainFromMini();
                                    return;
                                }
                                // 启动竞态窗口期（主窗口尚未创建完成）：延迟重试一次，确保"运行 exe → 面板必现"
                                var retry = new System.Windows.Threading.DispatcherTimer
                                {
                                    Interval = TimeSpan.FromMilliseconds(800)
                                };
                                retry.Tick += (s, e) =>
                                {
                                    retry.Stop();
                                    (Current?.MainWindow as MainWindow)?.ShowMainFromMini();
                                };
                                retry.Start();
                            });
                        }
                    }
                });
            }
            catch { }

            base.OnStartup(e);
            MainWindow window = new MainWindow();
            window.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (_mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch { }
                _mutex.Dispose();
            }
            try
            {
                _eventWaitHandle?.Dispose();
            }
            catch { }
            base.OnExit(e);
        }
    }
}
