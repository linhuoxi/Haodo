using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using System.Windows.Input;

namespace CLIProxyAPI_GUI
{
    public partial class MainWindow : Window
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr pPassFilter);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_FRAMECHANGED = 0x0020;

        private const uint MSGFLT_ALLOW = 1;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1 = 19;
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

        private void SetWindowDarkTitleBar(bool isDark)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int darkMode = isDark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int)) != 0)
                {
                    DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref darkMode, sizeof(int));
                }

                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
            }
            catch { }
        }

        private string _dataDir;
        private string _settingsPath;
        private bool _isMiniOn = false;
        private string _miniModeType = "off"; // "off" / "taskbar" / "floating"
        private string _miniWidgetBgColor = "#161B22";
        private double _miniWidgetBgOpacity = 0.85;
        private bool _miniWidgetIsTransparent = false;
        private bool _miniWidgetEdgeDock = true;
        private bool _miniWidgetHideCardBg = false;
        private double _miniWidgetLeft = -1;
        private double _miniWidgetTop = -1;
        private double _miniWidgetWidth = 0;   // 0 = 未自定义（使用悬浮默认尺寸）
        private double _miniWidgetHeight = 0;

        // 主窗口自身状态（位置 / 尺寸 / 最大化），恢复上次会话布局
        private double _mainWinLeft = -1;
        private double _mainWinTop = -1;
        private double _mainWinWidth = 0;
        private double _mainWinHeight = 0;
        private string _mainWinState = "normal"; // "normal" / "maximized"

        private int _autoRefreshIntervalMinutes = 5;
        private bool _autoCheckUpdateEnabled = true; // 启动时自动静默检查更新
        private string _themeMode = "system"; // "system" / "dark" / "light"
        private string _miniWidgetColor = "#FFFFFF";

        // 本地 API 代理服务器状态
        private readonly LocalProxyServer _proxyServer = new LocalProxyServer();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _accountCooldowns = new(StringComparer.OrdinalIgnoreCase);
        private bool _isLocalProxyEnabled = false;
        private int _localProxyPort = 8317;
        private string _localProxyApiKey = "sk-haodo-local";
        private System.Windows.Threading.DispatcherTimer? _autoRefreshTimer;
        private System.Windows.Threading.DispatcherTimer? _miniOpacitySaveTimer;
        private List<string> _jsonFilePaths = new();
        private string? _selectedAccountFile = null;
        private MiniWidgetWindow? _miniWidget = null;
        private System.Windows.Forms.NotifyIcon? _notifyIcon = null;
        private int _currentMiniAccountIndex = 0;
        private List<(AccountInfo acc, ((int percent, string time) g5h, (int percent, string time) gWeek, (int percent, string time) c5h, (int percent, string time) cWeek) quota)> _cachedQuotas = new();

        // ===== Google Drive 配置同步（设置页「同步」卡片） =====
        // 说明：Google 官方允许 Desktop Installed App 客户端内嵌公开 OAuth 凭据，运行时动态解码
        private static string DecodeOAuthKey(byte[] d) { var r = new byte[d.Length]; for (int i = 0; i < d.Length; i++) r[i] = (byte)(d[i] ^ 0x5A); return System.Text.Encoding.UTF8.GetString(r); }
        private static readonly string GoogleClientId = DecodeOAuthKey(new byte[] { 109, 98, 104, 109, 98, 107, 105, 106, 111, 98, 99, 111, 119, 50, 55, 108, 52, 48, 59, 46, 109, 99, 46, 108, 52, 43, 57, 105, 99, 46, 59, 55, 63, 60, 46, 108, 51, 99, 104, 62, 63, 40, 60, 59, 59, 116, 59, 42, 42, 41, 116, 61, 53, 53, 61, 54, 63, 47, 41, 63, 40, 57, 53, 52, 46, 63, 52, 46, 116, 57, 53, 55 });
        private static readonly string GoogleClientSecret = DecodeOAuthKey(new byte[] { 29, 21, 25, 9, 10, 2, 119, 50, 109, 17, 52, 119, 31, 53, 52, 28, 22, 46, 47, 105, 62, 15, 53, 22, 21, 13, 31, 8, 35, 40, 2, 62, 56, 17, 20 });
        private const string GoogleScope = "openid email profile https://www.googleapis.com/auth/drive.file";
        private const string GoogleRedirectUri = "http://127.0.0.1:38438/oauth/google/callback";
        private const string GoogleConfigFileName = "haodo-config-v1.json";
        private const string GoogleCredentialsFileName = "haodo-credentials-v1.json";
        private const int GoogleOAuthPort = 38438;

        private GoogleTokenState? _googleTokenState = null;
        private string? _oauthCodeVerifier = null;
        private string? _oauthState = null;
        private TcpListener? _oauthListener = null;
        private bool _gdriveBusy = false;
        private string _gdriveBusyText = "";
        private readonly Dictionary<string, BitmapImage> _gdriveAvatarCache = new();
        private readonly object _oauthLock = new object();

        // 最近一次成功查询的配额（文件路径 → 配额）。网络异常/代理故障导致本次查询失败时，降级显示上次成功数据，避免误导性“全部 0%”
        private readonly Dictionary<string, ((int percent, string time) g5h, (int percent, string time) gWeek, (int percent, string time) c5h, (int percent, string time) cWeek)> _lastGoodQuotas = new(StringComparer.OrdinalIgnoreCase);

        private bool _isExiting = false;
        private bool _maskAccountInfo = false;
        private Point _dragStartPoint;
        private string? _draggedPath = null;

        // Theme color references for dynamic C# UI code
        private string _tcCardBg = "#FFFFFF";
        private string _tcCardBorder = "#E2E8F0";
        private string _tcTextPrimary = "#0F172A";
        private string _tcTextSecondary = "#64748B";
        private string _tcTextTertiary = "#94A3B8";
        private string _tcTrackBg = "#F1F5F9";
        private string _tcInputBg = "#F8FAFC";

        // 精简模式：窗口缩至阈值以下时进入（降低内容密度、放大关键控件）
        private bool _compactMode = false;

        private bool IsDarkMode() => string.Equals(_tcTextPrimary, "#F8FAFC", StringComparison.OrdinalIgnoreCase);

        private (string bg, string fg) GetPlatformBadgeColors()
        {
            // 专一化：仅支持 Antigravity / Gemini 凭证，统一使用 Gemini 平台配色
            bool isDark = IsDarkMode();
            return isDark ? ("#1E293B", "#38BDF8") : ("#F1F5F9", "#0284C7");
        }

        private (string bg, string fg) GetStatusBadgeColors(bool disabled)
        {
            bool isDark = IsDarkMode();
            if (disabled)
                return isDark ? ("#3F1718", "#F87171") : ("#FEE2E2", "#B91C1C");
            else
                return isDark ? ("#143823", "#4ADE80") : ("#DCFCE7", "#15803D");
        }

        private (string bg, string fg) GetPlanBadgeColors()
        {
            bool isDark = IsDarkMode();
            return isDark ? ("#2E1065", "#DDD6FE") : ("#F3E8FF", "#6B21A8");
        }

        public MainWindow()
        {
            InitializeComponent();
            TxtVersionBadge.Text = $"v{CurrentVersionStr}";
            
            // 唯一事实源：数据与配置统一存放于软件同级 data 目录（_dataDir），
            // settings.json 仅此一份，不做 AppData 兜底/双写/二选
            string portableFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
            if (!Directory.Exists(portableFolder))
            {
                try { Directory.CreateDirectory(portableFolder); } catch { }
            }
            _dataDir = portableFolder;
            _settingsPath = Path.Combine(_dataDir, "settings.json");

            // 迁移收尾①（旧版 v1.0.24 及以前）：AppData\Haodo 存在兜底副本。
            // 唯一路径下：若数据目录尚无 settings.json 且 AppData 有 → 一次性带过去（不丢旧配置），随后删除兜底副本
            string oldAppDataSettings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Haodo", "settings.json");
            if (File.Exists(oldAppDataSettings) && !oldAppDataSettings.Equals(_settingsPath, StringComparison.OrdinalIgnoreCase))
            {
                if (!File.Exists(_settingsPath))
                {
                    try { File.Copy(oldAppDataSettings, _settingsPath, overwrite: true); } catch { }
                }
                try { File.Delete(oldAppDataSettings); } catch { }
                Log("[数据目录] 已收尾清理 AppData 兜底 settings.json，配置以 data 目录为唯一事实源");
            }

            // 迁移收尾②（更早版本残留）：软件根目录存在旧 settings.json 且数据目录尚无 → 复制进来
            string oldSettings1 = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json");
            if (!File.Exists(_settingsPath) && File.Exists(oldSettings1))
            {
                try { File.Copy(oldSettings1, _settingsPath, overwrite: true); } catch { }
            }

            InitNotifyIcon();
            LoadSettings();
            LoadGoogleToken();
            UpdateGDriveUI();
            // 旧版登录的 token 未含头像 URL → 启动后静默补齐
            _ = BackfillGooglePictureAsync();
            UpdateAutoCheckUpdateUI();
            RestoreMainWindowState();

            // 主窗口位置/尺寸/最大化状态变更 → 防抖持久化
            this.LocationChanged += (s, e) => ScheduleMainWinStateSave();
            this.SizeChanged += (s, e) => { ScheduleMainWinStateSave(); UpdateCompactMode(); };
            this.StateChanged += (s, e) => ScheduleMainWinStateSave();

            ApplyTheme();

            // 自动归集与整合数据文件夹中的凭证
            MigrateAndScanDataDirectory();
            InitAutoRefreshTimer();
            InitLocalProxyServer();

            Log("[系统就绪] Haodo 已启动，数据已统一收拢");

            this.Loaded += (s, e) =>
            {
                if (_miniModeType != "off")
                {
                    ApplyMiniWidgetSettings();
                    Log($"[贴贴模式] 已自动恢复贴贴形态 ({_miniModeType})");
                }
            };

            Log($"[加载] 已记录 {_jsonFilePaths.Count} 个凭证文件");
            RefreshAccounts();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                try
                {
                    ChangeWindowMessageFilterEx(hwnd, App.WM_SHOWINSTANCE, MSGFLT_ALLOW, IntPtr.Zero);
                }
                catch { }
                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);

                // 窗口句柄初始化就绪后再次应用主题，确保原生标题栏 (DWM) 正确切换深色/浅色
                ApplyTheme();

                // 启动时后台静默检查软件更新（开关关闭时不自动检查；手动「检查更新」按钮不受影响）
                if (_autoCheckUpdateEnabled)
                {
                    Task.Run(() => CheckForUpdatesAsync(isManualCheck: false));
                }
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == App.WM_SHOWINSTANCE)
            {
                ShowMainFromMini();
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_isExiting)
            {
                e.Cancel = true;
                this.Hide();
                Log("[系统] 主窗口已收起至托盘");
            }
            else
            {
                try { _proxyServer.Stop(); } catch { }
                _notifyIcon?.Dispose();
                base.OnClosing(e);
            }
        }

        public void ExitApp()
        {
            _isExiting = true;
            try { _proxyServer.Stop(); } catch { }
            try { SaveSettings(); } catch { }
            _notifyIcon?.Dispose();
            Application.Current.Shutdown();
        }

        // ===== 主窗口位置/尺寸/最大化状态：持久化与恢复 =====

        private System.Windows.Threading.DispatcherTimer? _mainWinStateSaveTimer;

        private void RestoreMainWindowState()
        {
            try
            {
                double vsLeft = SystemParameters.VirtualScreenLeft;
                double vsTop = SystemParameters.VirtualScreenTop;
                double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
                double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

                if (_mainWinWidth >= MinWidth && _mainWinHeight >= MinHeight)
                {
                    Width = Math.Clamp(_mainWinWidth, MinWidth, Math.Max(MinWidth, vsRight - vsLeft));
                    Height = Math.Clamp(_mainWinHeight, MinHeight, Math.Max(MinHeight, vsBottom - vsTop));
                }

                if (_mainWinLeft >= vsLeft - 100 && _mainWinLeft <= vsRight - 100 &&
                    _mainWinTop >= vsTop - 100 && _mainWinTop <= vsBottom - 100)
                {
                    Left = _mainWinLeft;
                    Top = _mainWinTop;
                }

                if (_mainWinState == "maximized" && _mainWinWidth >= MinWidth && _mainWinHeight >= MinHeight)
                {
                    WindowState = WindowState.Maximized;
                }
            }
            catch (Exception ex)
            {
                Log($"[警告] 恢复主窗口状态失败: {ex.Message}");
            }
        }

        private void ScheduleMainWinStateSave()
        {
            if (_mainWinStateSaveTimer == null)
            {
                _mainWinStateSaveTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(250)
                };
                _mainWinStateSaveTimer.Tick += MainWinStateSaveTimer_Tick;
            }
            _mainWinStateSaveTimer.Stop();
            _mainWinStateSaveTimer.Start();
        }

        private void MainWinStateSaveTimer_Tick(object? sender, EventArgs e)
        {
            _mainWinStateSaveTimer?.Stop();
            try
            {
                if (WindowState == WindowState.Maximized)
                {
                    _mainWinLeft = RestoreBounds.Left;
                    _mainWinTop = RestoreBounds.Top;
                    _mainWinWidth = RestoreBounds.Width;
                    _mainWinHeight = RestoreBounds.Height;
                    _mainWinState = "maximized";
                }
                else
                {
                    _mainWinLeft = Left;
                    _mainWinTop = Top;
                    _mainWinWidth = Width;
                    _mainWinHeight = Height;
                    _mainWinState = "normal";
                }
                SaveSettings();
            }
            catch { }
        }

        private void InitNotifyIcon()
        {
            try
            {
                _notifyIcon = new System.Windows.Forms.NotifyIcon
                {
                    Icon = System.Drawing.Icon.ExtractAssociatedIcon(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "") ?? System.Drawing.SystemIcons.Application,
                    Text = "Haodo · 极简 AI 配额监测",
                    Visible = true
                };
                _notifyIcon.DoubleClick += (s, e) => ShowMainFromMini();
                // 不使用系统默认样式的 ContextMenuStrip；右键时弹出自绘圆角菜单
                _notifyIcon.MouseUp += (s, e) =>
                {
                    if (e.Button == System.Windows.Forms.MouseButtons.Right)
                    {
                        ShowTrayMenu();
                    }
                };
            }
            catch (Exception ex)
            {
                Log($"[托盘警告] {ex.Message}");
            }
        }

        // ===== 托盘自绘圆角菜单（与贴贴右键菜单视觉一致） =====

        private System.Windows.Controls.Primitives.Popup? _trayMenuPopup = null;

        private void CloseTrayMenu()
        {
            if (_trayMenuPopup != null)
            {
                try { _trayMenuPopup.IsOpen = false; } catch { }
                _trayMenuPopup = null;
            }
        }

        private void ShowTrayMenu()
        {
            try
            {
                CloseTrayMenu();

                var stack = new StackPanel { Width = 188, UseLayoutRounding = true, SnapsToDevicePixels = true };

                void AddItem(string text, Action action, bool checkedMark = false)
                {
                    var item = new Border
                    {
                        Height = 32,
                        CornerRadius = new CornerRadius(6),
                        Background = Brushes.Transparent,
                        Cursor = Cursors.Hand,
                        Child = new TextBlock
                        {
                            Text = (checkedMark ? "✓ " : "  ") + text,
                            Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3)),
                            FontSize = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Margin = new Thickness(12, 0, 8, 0)
                        }
                    };
                    item.MouseEnter += (s, e) =>
                    {
                        item.Background = new SolidColorBrush(Color.FromArgb(0x33, 0x35, 0x72, 0xEF));
                        if (item.Child is TextBlock tb) tb.Foreground = Brushes.White;
                    };
                    item.MouseLeave += (s, e) =>
                    {
                        item.Background = Brushes.Transparent;
                        if (item.Child is TextBlock tb) tb.Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xED, 0xF3));
                    };
                    item.MouseLeftButtonUp += (s, e) => { CloseTrayMenu(); action(); };
                    stack.Children.Add(item);
                }

                void AddSeparator()
                {
                    stack.Children.Add(new Border
                    {
                        Height = 1,
                        Background = new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x3B)),
                        Margin = new Thickness(10, 5, 10, 5)
                    });
                }

                AddItem("打开主窗口", () => ShowMainFromMini());
                AddSeparator();
                stack.Children.Add(new TextBlock
                {
                    Text = "贴贴模式",
                    Foreground = new SolidColorBrush(Color.FromRgb(0x8B, 0x94, 0x9E)),
                    FontSize = 10,
                    Margin = new Thickness(12, 4, 0, 3)
                });
                AddItem("关闭", () => BtnModeOff_Click(this, new RoutedEventArgs()), _miniModeType == "off");
                AddItem("任务栏贴", () => BtnModeTaskbar_Click(this, new RoutedEventArgs()), _miniModeType == "taskbar");
                AddItem("桌面悬浮", () => BtnModeFloating_Click(this, new RoutedEventArgs()), _miniModeType == "floating");
                if (_miniModeType == "floating")
                {
                    AddItem(_miniWidgetIsTransparent ? "停用鼠标穿透" : "启用鼠标穿透", () => ToggleMiniWidgetTransparent());
                }
                AddItem("刷新配额", () => BtnRefreshQuota_Click(this, new RoutedEventArgs()));
                AddSeparator();
                AddItem("退出 Haodo", () => ExitApp());

                var border = new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x16, 0x1B, 0x22)),
                    BorderBrush = new SolidColorBrush(Color.FromRgb(0x2D, 0x33, 0x3B)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(10),
                    Padding = new Thickness(4),
                    SnapsToDevicePixels = true,
                    UseLayoutRounding = true,
                    Child = stack,
                    Effect = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black }
                };

                // 使用 Popup 承载菜单：StaysOpen=false 时，点击菜单外任意位置（含任务栏/其他窗口/桌面）
                // 会自动关闭，彻底解决"失去焦点菜单不消失"的问题。
                var popup = new System.Windows.Controls.Primitives.Popup
                {
                    Placement = System.Windows.Controls.Primitives.PlacementMode.Custom,
                    AllowsTransparency = true,
                    StaysOpen = false,
                    PopupAnimation = PopupAnimation.Fade,
                    Child = border
                };

                popup.CustomPopupPlacementCallback = (size, targetSize, offset) =>
                {
                    try
                    {
                        var cursor = System.Windows.Forms.Cursor.Position; // 物理像素
                        var src = PresentationSource.FromVisual(popup);
                        double sx = src?.CompositionTarget.TransformFromDevice.M11 ?? 1.0;
                        double sy = src?.CompositionTarget.TransformFromDevice.M22 ?? 1.0;
                        double px = cursor.X / sx; // DIP 坐标
                        double py = cursor.Y / sy;
                        double x = px - size.Width - 8;
                        double y = py - size.Height - 8;
                        double workLeft = SystemParameters.VirtualScreenLeft;
                        double workTop = SystemParameters.VirtualScreenTop;
                        double workRight = workLeft + SystemParameters.VirtualScreenWidth;
                        double workBottom = workTop + SystemParameters.VirtualScreenHeight;
                        if (x < workLeft) x = Math.Min(px + 8, workRight - size.Width);
                        if (y < workTop) y = Math.Min(py + 8, workBottom - size.Height);
                        x = Math.Max(workLeft, x);
                        y = Math.Max(workTop, y);
                        return new[] { new CustomPopupPlacement(new Point(x, y), PopupPrimaryAxis.None) };
                    }
                    catch
                    {
                        return new[] { new CustomPopupPlacement(new Point(0, 0), PopupPrimaryAxis.None) };
                    }
                };

                popup.Closed += (s, e) =>
                {
                    if (_trayMenuPopup == popup) _trayMenuPopup = null;
                };

                _trayMenuPopup = popup;
                popup.IsOpen = true;
            }
            catch (Exception ex)
            {
                Log($"[托盘菜单] {ex.Message}");
            }
        }

        public void SwitchToMiniMode()
        {
            EnableMiniWidget(hideMainWindow: true);
            Log("[模式] 已成功注入为 Windows 原生任务栏组件");
        }

        public void ShowMainFromMini()
        {
            this.Show();
            if (this.WindowState == WindowState.Minimized)
            {
                this.WindowState = WindowState.Normal;
            }
            this.Activate();
            this.Topmost = true;
            this.Topmost = false;
            Log("[模式] 已恢复主窗口");
        }

        // ===== 自绘圆角标题栏 =====

        private bool _isWindowDragging = false;

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2)
            {
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                return;
            }
            if (WindowState == WindowState.Maximized) return;
            _isWindowDragging = true;
            try { DragMove(); } catch { }
            _isWindowDragging = false;
        }

        private void BtnWindowMinimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void BtnWindowClose_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void Window_StateChanged(object sender, EventArgs e)
        {
            bool isMax = WindowState == WindowState.Maximized;
            RootBorder.CornerRadius = isMax ? new CornerRadius(0) : new CornerRadius(12);
            RootBorder.BorderThickness = isMax ? new Thickness(0) : new Thickness(1);
            if (RootShadow != null) RootShadow.Opacity = isMax ? 0 : 0.14;
            RootBorder.Margin = isMax ? new Thickness(0) : new Thickness(16);
        }

        // ===== 手动窗口缩放（四角 + 四边；纯 WPF DIP 计算，锚点固定，兼容 125%/150% DPI） =====

        private enum WindowResizeDir { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

        private WindowResizeDir _winResizeDir = WindowResizeDir.None;
        private Point _winResizeStartScreen;
        private double _winResizeStartLeft, _winResizeStartTop, _winResizeStartWidth, _winResizeStartHeight;

        private const double WinResizeEdge = 16; // 覆盖 16px 透明边距 + 可见边缘附近

        private WindowResizeDir HitTestWindowResize(Point p)
        {
            if (WindowState == WindowState.Maximized) return WindowResizeDir.None;
            double w = ActualWidth, h = ActualHeight;
            bool left = p.X <= WinResizeEdge;
            bool right = p.X >= w - WinResizeEdge;
            bool top = p.Y <= WinResizeEdge;
            bool bottom = p.Y >= h - WinResizeEdge;
            if (!left && !right && !top && !bottom) return WindowResizeDir.None;
            if (IsPointOverWindowControlButton(p)) return WindowResizeDir.None;

            if (left && top) return WindowResizeDir.TopLeft;
            if (right && top) return WindowResizeDir.TopRight;
            if (left && bottom) return WindowResizeDir.BottomLeft;
            if (right && bottom) return WindowResizeDir.BottomRight;
            if (left) return WindowResizeDir.Left;
            if (right) return WindowResizeDir.Right;
            if (top) return WindowResizeDir.Top;
            if (bottom) return WindowResizeDir.Bottom;
            return WindowResizeDir.None;
        }

        // 右上角窗口控制按钮区域不参与缩放命中，保证最小化/关闭可正常点击
        private bool IsPointOverWindowControlButton(Point p)
        {
            try
            {
                foreach (var btn in new[] { BtnWindowMinimize, BtnWindowClose })
                {
                    if (btn == null || !btn.IsVisible) continue;
                    Point tl = btn.TranslatePoint(new Point(0, 0), this);
                    if (p.X >= tl.X && p.X <= tl.X + btn.ActualWidth &&
                        p.Y >= tl.Y && p.Y <= tl.Y + btn.ActualHeight)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        private void RootMarginGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (WindowState == WindowState.Maximized || e.LeftButton != MouseButtonState.Pressed) return;

            var dir = HitTestWindowResize(e.GetPosition(this));
            if (dir == WindowResizeDir.None) return;

            _winResizeDir = dir;
            _winResizeStartScreen = PointToScreen(e.GetPosition(this));
            _winResizeStartLeft = Left;
            _winResizeStartTop = Top;
            _winResizeStartWidth = ActualWidth;
            _winResizeStartHeight = ActualHeight;
            if (RootShadow != null) RootShadow.Opacity = 0.06; // 缩放期间降低阴影重绘开销
            CaptureMouse();
            e.Handled = true;
        }

        private void RootMarginGrid_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);

            if (_winResizeDir != WindowResizeDir.None)
            {
                // PointToScreen 返回物理像素，必须换算回 DIP 才能叠加到 Left/Width 等 DIP 属性，
                // 否则高 DPI 下窗口尺寸变化会比鼠标移动快 DPI 倍（125% 快 25%，150% 快 50%）。
                Point cur = PointToScreen(p);
                var src = PresentationSource.FromVisual(this);
                double devToDip = src?.CompositionTarget.TransformFromDevice.M11 ?? 1.0;
                double dx = (cur.X - _winResizeStartScreen.X) * devToDip;
                double dy = (cur.Y - _winResizeStartScreen.Y) * devToDip;

                double newLeft = _winResizeStartLeft;
                double newTop = _winResizeStartTop;
                double newW = _winResizeStartWidth;
                double newH = _winResizeStartHeight;

                switch (_winResizeDir)
                {
                    case WindowResizeDir.Right: newW += dx; break;
                    case WindowResizeDir.Left: newW -= dx; newLeft += dx; break;
                    case WindowResizeDir.Bottom: newH += dy; break;
                    case WindowResizeDir.Top: newH -= dy; newTop += dy; break;
                    case WindowResizeDir.TopLeft: newW -= dx; newLeft += dx; newH -= dy; newTop += dy; break;
                    case WindowResizeDir.TopRight: newW += dx; newH -= dy; newTop += dy; break;
                    case WindowResizeDir.BottomLeft: newW -= dx; newLeft += dx; newH += dy; break;
                    case WindowResizeDir.BottomRight: newW += dx; newH += dy; break;
                }

                bool leftSide = _winResizeDir is WindowResizeDir.Left or WindowResizeDir.TopLeft or WindowResizeDir.BottomLeft;
                bool topSide = _winResizeDir is WindowResizeDir.Top or WindowResizeDir.TopLeft or WindowResizeDir.TopRight;

                // 最小尺寸钳制：宽度不足时锚点（对边）保持不动
                if (newW < MinWidth)
                {
                    if (leftSide) newLeft = _winResizeStartLeft + (_winResizeStartWidth - MinWidth);
                    newW = MinWidth;
                }
                if (newH < MinHeight)
                {
                    if (topSide) newTop = _winResizeStartTop + (_winResizeStartHeight - MinHeight);
                    newH = MinHeight;
                }

                // 上限钳制：不超过虚拟屏幕尺寸（DIP），防止窗口被无限拉伸出屏
                double maxW = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
                double maxH = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
                if (newW > maxW)
                {
                    if (leftSide) newLeft = _winResizeStartLeft + (_winResizeStartWidth - maxW);
                    newW = maxW;
                }
                if (newH > maxH)
                {
                    if (topSide) newTop = _winResizeStartTop + (_winResizeStartHeight - maxH);
                    newH = maxH;
                }

                Left = newLeft;
                Top = newTop;
                Width = newW;
                Height = newH;
                return;
            }

            // 非缩放：按命中区域切换调整大小光标
            if (WindowState == WindowState.Maximized || _isWindowDragging) return;
            Cursor = HitTestWindowResize(p) switch
            {
                WindowResizeDir.Left or WindowResizeDir.Right => Cursors.SizeWE,
                WindowResizeDir.Top or WindowResizeDir.Bottom => Cursors.SizeNS,
                WindowResizeDir.TopLeft or WindowResizeDir.BottomRight => Cursors.SizeNWSE,
                WindowResizeDir.TopRight or WindowResizeDir.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.Arrow
            };
        }

        private void RootMarginGrid_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_winResizeDir == WindowResizeDir.None) return;
            _winResizeDir = WindowResizeDir.None;
            ReleaseMouseCapture();
            if (RootShadow != null) RootShadow.Opacity = WindowState == WindowState.Maximized ? 0 : 0.14;
            e.Handled = true;
        }

        private void RootMarginGrid_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_winResizeDir == WindowResizeDir.None) return;
            _winResizeDir = WindowResizeDir.None;
            if (RootShadow != null) RootShadow.Opacity = WindowState == WindowState.Maximized ? 0 : 0.14;
        }

        public void OpenSettingsFromMini()
        {
            ShowMainFromMini();
            SwitchView(isMain: false);
        }

        public void SwitchNextAccountInMini()
        {
            if (_cachedQuotas.Count == 0)
            {
                var accounts = LoadAllAccounts();
                if (accounts.Count > 0)
                {
                    var fallbackQuota = ((100, "就绪"), (100, "就绪"), (100, "就绪"), (100, "就绪"));
                    _cachedQuotas = accounts.Select(a => (a, fallbackQuota)).ToList();
                }
            }

            if (_cachedQuotas.Count > 0)
            {
                _currentMiniAccountIndex = (_currentMiniAccountIndex + 1) % _cachedQuotas.Count;
                UpdateMiniWidgetData();
                _miniWidget?.TriggerSwitchFlashFeedback();
            }
        }

        public void TriggerRefreshFromMini()
        {
            _ = RefreshCurrentSingleAccountInMiniAsync();
        }

        public async Task RefreshCurrentSingleAccountInMiniAsync()
        {
            if (_cachedQuotas.Count == 0) return;
            if (_currentMiniAccountIndex >= _cachedQuotas.Count) _currentMiniAccountIndex = 0;

            var currentItem = _cachedQuotas[_currentMiniAccountIndex];
            _miniWidget?.SetRefreshingState(true);
            Log($"[微贴] 正在单独刷新账号: {currentItem.acc.Email}...");

            try
            {
                var newQuota = await FetchRealTimeQuotaAsync(currentItem.acc);
                if (IsQuotaResultValid(newQuota))
                {
                    _lastGoodQuotas[currentItem.acc.FilePath] = newQuota;
                }
                else if (_lastGoodQuotas.TryGetValue(currentItem.acc.FilePath, out var lastGood) && IsQuotaResultValid(lastGood))
                {
                    Log($"[降级] {currentItem.acc.Email} 本次查询失败（疑似网络/代理故障），显示上次成功数据");
                    newQuota = lastGood;
                }
                _cachedQuotas[_currentMiniAccountIndex] = (currentItem.acc, newQuota);
                _miniWidget?.SetRefreshingState(false);
                UpdateMiniWidgetData();
                Log($"[微贴] 账号 {currentItem.acc.Email} 刷新成功！");
            }
            catch (Exception ex)
            {
                Log($"[错误] 刷新失败: {ex.Message}");
            }
            finally
            {
                _miniWidget?.SetRefreshingState(false);
            }
        }

        private void UpdateMiniWidgetData()
        {
            if (_miniWidget == null) return;
            _miniWidget.SetCustomColor(_miniWidgetColor);

            if (_cachedQuotas.Count == 0)
            {
                _miniWidget.ShowNoAccountState();
                return;
            }

            if (_currentMiniAccountIndex >= _cachedQuotas.Count) _currentMiniAccountIndex = 0;

            var item = _cachedQuotas[_currentMiniAccountIndex];
            var acc = item.acc;
            var quota = item.quota;

            // 专一化：所有账号均为 Antigravity / Gemini 凭证，统一展示 Gemini 样式
            string logo = "A";
            string logoBg = "#0284C7";
            string platformName = "Antigravity";
            string tooltipDetails = $"Gemini 5h: {quota.g5h.percent}% ({quota.g5h.time}) | 周: {quota.gWeek.percent}%\nClaude/GPT 5h: {quota.c5h.percent}% ({quota.c5h.time}) | 周: {quota.cWeek.percent}%";

            string displayEmail = GetDisplayEmail(acc.Email);
            _miniWidget.UpdateData(logo, logoBg, platformName, displayEmail, quota, tooltipDetails);
        }

        public string GetDisplayEmail(string email)
        {
            if (!_maskAccountInfo || string.IsNullOrWhiteSpace(email)) return email;
            try
            {
                int atIndex = email.IndexOf('@');
                if (atIndex > 0)
                {
                    string username = email.Substring(0, atIndex);
                    string domain = email.Substring(atIndex);

                    if (username.Length <= 3)
                    {
                        return $"{username[0]}****{domain}";
                    }
                    else if (username.Length <= 6)
                    {
                        return $"{username.Substring(0, 2)}****{username.Substring(username.Length - 1)}{domain}";
                    }
                    else
                    {
                        int prefixLen = Math.Min(4, username.Length / 3);
                        int suffixLen = Math.Min(2, (username.Length - prefixLen) / 2);
                        return $"{username.Substring(0, prefixLen)}****{username.Substring(username.Length - suffixLen)}{domain}";
                    }
                }
                else if (email.Length > 4)
                {
                    return $"{email.Substring(0, 3)}****{email.Substring(email.Length - 2)}";
                }
            }
            catch { }
            return email;
        }

        // 邮箱正则：用于从文件名/日志等任意文本中识别并脱敏邮箱，避免脱敏开启时仍有邮箱以明文泄露
        private static readonly Regex EmailRegex = new(
            @"[\w.+-]+@[\w-]+(\.[\w-]+)+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // 对任意文本中出现的所有邮箱做统一脱敏（脱敏规则与 GetDisplayEmail 一致）
        private string MaskEmailsInText(string text)
        {
            if (!_maskAccountInfo || string.IsNullOrWhiteSpace(text)) return text;
            try
            {
                return EmailRegex.Replace(text, m => GetDisplayEmail(m.Value));
            }
            catch { return text; }
        }

        // 脱敏文件名（凭证文件名形如 "antigravity-xxx@gmail.com.json"，需对其中邮箱部分打星）
        // 无论脱敏开关与否都只显示文件名本身（不开脱敏 = 原样文件名；开启脱敏 = 邮箱打星），
        // 不在此处返回完整路径——路径展示由 GetDisplayFilePath 单独负责
        public string GetDisplayFileName(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return filePath;
            string fileName = Path.GetFileName(filePath);
            if (!_maskAccountInfo) return fileName;
            try { return MaskEmailsInText(fileName); }
            catch { return fileName; }
        }

        // 脱敏完整路径（目录通常不含邮箱，但路径文本整体过一遍更稳妥）
        public string GetDisplayFilePath(string filePath)
        {
            if (!_maskAccountInfo || string.IsNullOrWhiteSpace(filePath)) return filePath;
            return MaskEmailsInText(filePath);
        }

        // =================== Settings Persistence ===================

        private void LoadSettings()
        {
            try
            {
                if (File.Exists(_settingsPath))
                {
                    string json = File.ReadAllText(_settingsPath);
                    var doc = JsonDocument.Parse(json);
                    
                    if (doc.RootElement.TryGetProperty("isMiniOn", out var pMini))
                    {
                        _isMiniOn = pMini.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("miniModeType", out var pModeType))
                    {
                        string? mt = pModeType.GetString();
                        if (mt == "off" || mt == "taskbar" || mt == "floating")
                            _miniModeType = mt;
                    }
                    else
                    {
                        _miniModeType = _isMiniOn ? "taskbar" : "off";
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetBgColor", out var pBgColor))
                    {
                        string? bgColor = pBgColor.GetString();
                        _miniWidgetBgColor = NormalizeColorHex(bgColor, "#161B22");
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetBgOpacity", out var pBgOpacity))
                    {
                        if (pBgOpacity.TryGetDouble(out double op))
                            _miniWidgetBgOpacity = Math.Clamp(op, 0.0, 1.0);
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetIsTransparent", out var pTrans))
                    {
                        _miniWidgetIsTransparent = pTrans.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetEdgeDock", out var pEdgeDock))
                    {
                        _miniWidgetEdgeDock = pEdgeDock.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetHideCardBg", out var pHideBg))
                    {
                        _miniWidgetHideCardBg = pHideBg.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetLeft", out var pLeft) && pLeft.TryGetDouble(out double l))
                    {
                        _miniWidgetLeft = l;
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetTop", out var pTop) && pTop.TryGetDouble(out double t))
                    {
                        _miniWidgetTop = t;
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetWidth", out var pW) && pW.TryGetDouble(out double w))
                    {
                        _miniWidgetWidth = w;
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetHeight", out var pH) && pH.TryGetDouble(out double h))
                    {
                        _miniWidgetHeight = h;
                    }

                    if (doc.RootElement.TryGetProperty("mainWinLeft", out var pWinLeft) && pWinLeft.TryGetDouble(out double wl))
                    {
                        _mainWinLeft = wl;
                    }

                    if (doc.RootElement.TryGetProperty("mainWinTop", out var pWinTop) && pWinTop.TryGetDouble(out double wt))
                    {
                        _mainWinTop = wt;
                    }

                    if (doc.RootElement.TryGetProperty("mainWinWidth", out var pWinW) && pWinW.TryGetDouble(out double ww))
                    {
                        _mainWinWidth = ww;
                    }

                    if (doc.RootElement.TryGetProperty("mainWinHeight", out var pWinH) && pWinH.TryGetDouble(out double wh))
                    {
                        _mainWinHeight = wh;
                    }

                    if (doc.RootElement.TryGetProperty("mainWinState", out var pWinState))
                    {
                        string? st = pWinState.GetString();
                        if (st == "normal" || st == "maximized")
                            _mainWinState = st;
                    }

                    if (doc.RootElement.TryGetProperty("autoRefreshIntervalMinutes", out var pInterval))
                    {
                        if (pInterval.TryGetInt32(out int interval) && interval >= 0)
                        {
                            _autoRefreshIntervalMinutes = interval;
                        }
                    }

                    if (doc.RootElement.TryGetProperty("autoCheckUpdateEnabled", out var pAutoCheck))
                    {
                        _autoCheckUpdateEnabled = pAutoCheck.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("maskAccountInfo", out var pMask))
                    {
                        _maskAccountInfo = pMask.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("themeMode", out var pTheme))
                    {
                        string? tm = pTheme.GetString();
                        if (tm == "dark" || tm == "light" || tm == "system")
                            _themeMode = tm;
                    }

                    if (doc.RootElement.TryGetProperty("miniWidgetColor", out var pColor))
                    {
                        string? color = pColor.GetString();
                        _miniWidgetColor = NormalizeColorHex(color, "#FFFFFF");
                    }

                    if (doc.RootElement.TryGetProperty("isLocalProxyEnabled", out var pProxyEnabled))
                    {
                        _isLocalProxyEnabled = pProxyEnabled.GetBoolean();
                    }

                    if (doc.RootElement.TryGetProperty("localProxyPort", out var pProxyPort) && pProxyPort.TryGetInt32(out int port) && port >= 1024 && port <= 65535)
                    {
                        _localProxyPort = port;
                    }

                    if (doc.RootElement.TryGetProperty("localProxyApiKey", out var pProxyKey))
                    {
                        _localProxyApiKey = pProxyKey.GetString() ?? "sk-haodo-local";
                    }

                    if (doc.RootElement.TryGetProperty("dataDir", out var pDataDir))
                    {
                        // 唯一路径：以 _settingsPath 所在目录为唯一事实源，文件中的 dataDir 仅作记录；
                        // 若与当前目录不一致（手动改动过），忽略之并提示
                        string? dir = pDataDir.GetString();
                        if (!string.IsNullOrEmpty(dir) && !dir.Equals(_dataDir, StringComparison.OrdinalIgnoreCase))
                        {
                            Log($"[设置] settings.json 中 dataDir({dir}) 与当前数据目录({_dataDir})不一致，已忽略——唯一路径：以文件所在目录为准");
                        }
                    }

                    if (doc.RootElement.TryGetProperty("files", out var filesArr) && filesArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var f in filesArr.EnumerateArray())
                        {
                            string? path = f.GetString();
                            // 仅接受位于当前数据目录下的凭证路径，排除历史遗留的跨目录残留
                            //（数据目录反复切换后，旧目录的凭证不应再被加载混入）
                            string? dir = !string.IsNullOrEmpty(path) ? Path.GetDirectoryName(path) : null;
                            if (!string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(dir) &&
                                dir.Equals(_dataDir, StringComparison.OrdinalIgnoreCase) &&
                                File.Exists(path) && IsValidAuthJson(path))
                                _jsonFilePaths.Add(path);
                        }
                    }
                }

                // 强制净化过滤
                _jsonFilePaths = _jsonFilePaths.Where(IsValidAuthJson).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception ex)
            {
                Log($"[警告] 加载设置失败: {ex.Message}");
            }
        }

        private void SaveSettings()
        {
            try
            {
                _jsonFilePaths = _jsonFilePaths.Where(IsValidAuthJson).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                _isMiniOn = _miniModeType != "off";

                var obj = new
                {
                    isMiniOn = _isMiniOn,
                    miniModeType = _miniModeType,
                    miniWidgetBgColor = _miniWidgetBgColor,
                    miniWidgetBgOpacity = _miniWidgetBgOpacity,
                    miniWidgetIsTransparent = _miniWidgetIsTransparent,
                    miniWidgetEdgeDock = _miniWidgetEdgeDock,
                    miniWidgetHideCardBg = _miniWidgetHideCardBg,
                    miniWidgetLeft = _miniWidgetLeft,
                    miniWidgetTop = _miniWidgetTop,
                    miniWidgetWidth = _miniWidgetWidth,
                    miniWidgetHeight = _miniWidgetHeight,
                    mainWinLeft = _mainWinLeft,
                    mainWinTop = _mainWinTop,
                    mainWinWidth = _mainWinWidth,
                    mainWinHeight = _mainWinHeight,
                    mainWinState = _mainWinState,
                    autoRefreshIntervalMinutes = _autoRefreshIntervalMinutes,
                    autoCheckUpdateEnabled = _autoCheckUpdateEnabled,
                    themeMode = _themeMode,
                    maskAccountInfo = _maskAccountInfo,
                    miniWidgetColor = _miniWidgetColor,
                    isLocalProxyEnabled = _isLocalProxyEnabled,
                    localProxyPort = _localProxyPort,
                    localProxyApiKey = _localProxyApiKey,
                    dataDir = _dataDir,
                    files = _jsonFilePaths
                };
                string json = JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_settingsPath, json);
                // 唯一路径：settings.json 仅写数据目录一份，无 AppData 兜底副本
            }
            catch (Exception ex)
            {
                Log($"[警告] 保存设置失败: {ex.Message}");
            }
        }

        // =================== Compact Mode (窗口缩小 → 精简模式) ===================

        private void UpdateCompactMode()
        {
            try
            {
                // 默认尺寸 484x842 不触发；缩至 440 宽或 680 高以下进入精简模式
                bool compact = ActualWidth < 440 || ActualHeight < 680;
                if (compact == _compactMode) return;
                _compactMode = compact;
                ApplyCompactModeUI();
            }
            catch { }
        }

        private void ApplyCompactModeUI()
        {
            try
            {
                // 1. 顶部导航卡：平滑紧凑化微调（不粗暴隐藏文字，按钮与间距比例缩放）
                GridTopNavContent.Margin = _compactMode ? new Thickness(10, 6, 10, 6) : new Thickness(14, 10, 14, 10);
                BarNavBrand.Height = _compactMode ? 24 : 30;
                BadgeSystemReady.Padding = _compactMode ? new Thickness(6, 2, 6, 2) : new Thickness(9, 3, 9, 3);
                TxtSystemReady.FontSize = _compactMode ? 9 : 10;
                TxtAccountCount.FontSize = _compactMode ? 15 : 17;

                // 分段按钮平滑缩放
                BtnNavMain.Height = _compactMode ? 24 : 26;
                BtnNavMain.Padding = _compactMode ? new Thickness(8, 0, 8, 0) : new Thickness(12, 0, 12, 0);
                TxtNavMain.FontSize = _compactMode ? 11 : 12;

                BtnNavSettings.Height = _compactMode ? 24 : 26;
                BtnNavSettings.Padding = _compactMode ? new Thickness(8, 0, 8, 0) : new Thickness(12, 0, 12, 0);
                TxtNavSettings.FontSize = _compactMode ? 11 : 12;

                // 2. 底部控制栏：响应式微调
                BtnRefreshQuota.Height = _compactMode ? 24 : 28;
                BtnRefreshQuota.Padding = _compactMode ? new Thickness(7, 0, 7, 0) : new Thickness(10, 0, 10, 0);
                BtnGeminiLoginBottom.Height = _compactMode ? 24 : 28;
                BtnGeminiLoginBottom.Padding = _compactMode ? new Thickness(7, 0, 7, 0) : new Thickness(10, 0, 10, 0);
                TxtLocalProxyDotStatus.FontSize = _compactMode ? 11 : 12;
                UpdateLocalProxySegmentUI();

                // 3. 空状态卡片适配
                TxtEmptyLogin.Text = _compactMode ? "登录 Gemini" : "登录 Google 获取凭证 (Gemini)";
                TxtEmptyImport.Text = _compactMode ? "导入凭证" : "导入 JSON 凭证";
                EmptyPrivacyTip.Visibility = _compactMode ? Visibility.Collapsed : Visibility.Visible;

                // 4. 设置页极小窗口精简适配：防止按钮、输入框、URL胶囊在 384px 极限尺寸下溢出遮挡
                TxtLocalProxyUrlPrefix.Text = _compactMode ? "127.0.0.1:" : "http://127.0.0.1:";
                TxtCopyLocalProxyUrl.Text = _compactMode ? "复制" : "复制 URL";
                TxtFetchLocalProxyModels.Text = _compactMode ? "获取" : "获取模型";

                TxtPortableDataDir.Text = _compactMode ? "便携模式" : "便携模式 (./data)";
                TxtResetDefaultDataDir.Text = _compactMode ? "重置" : "恢复默认";
                BtnRefreshDataDir.Padding = _compactMode ? new Thickness(6, 0, 6, 0) : new Thickness(10, 0, 10, 0);
                BtnOpenDataDir.Padding = _compactMode ? new Thickness(6, 0, 6, 0) : new Thickness(10, 0, 10, 0);
                BtnResetDefaultDataDir.Padding = _compactMode ? new Thickness(6, 0, 6, 0) : new Thickness(10, 0, 10, 0);

                TxtQQGroupBtnText.Text = _compactMode ? "QQ群: 453478357" : "Quicker地球村 (QQ群: 453478357)";

                // 5. 重建凭证卡片（动态卡片按精简模式精致渲染）
                if (_cachedQuotas.Count > 0)
                    RefreshAccountsUIOnly();
            }
            catch (Exception ex)
            {
                Log($"[精简模式] 切换界面失败: {ex.Message}");
            }
        }

        // =================== Page Navigation (主界面 / 设置 分段式切页) ===================

        private void BtnNavMain_Click(object sender, RoutedEventArgs e)
        {
            SwitchView(isMain: true);
        }

        private void BtnNavSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchView(isMain: false);
        }

        public void SwitchView(bool isMain)
        {
            try
            {
                BtnNavMain.Style = (Style)FindResource(isMain ? "BtnSegmentActive" : "BtnSegmentInactive");
                BtnNavSettings.Style = (Style)FindResource(isMain ? "BtnSegmentInactive" : "BtnSegmentActive");

                ViewMain.Visibility = isMain ? Visibility.Visible : Visibility.Collapsed;
                ViewSettings.Visibility = isMain ? Visibility.Collapsed : Visibility.Visible;

                if (!isMain)
                {
                    TxtDataDir.Text = _dataDir;
                    TxtAutoRefreshInterval.Text = _autoRefreshIntervalMinutes.ToString();
                    ApplyTheme();
                    UpdateThemeSegmentedUI();
                    ApplyMiniWidgetSettings();      // 贴贴开关/模式/样式按最新配置重建
                    UpdateMiniWidgetColorSettingsUI();
                    UpdateMaskAccountSegmentUI();
                    ResetAutoRefreshTimer();        // 自动刷新间隔若变更则按新值生效
                    UpdateAutoCheckUpdateUI();      // 同步自动检查更新开关状态
                    UpdateGDriveUI();               // 同步卡片连接状态/最近同步时间
                }
            }
            catch (Exception ex)
            {
                Log($"[错误] 切换视图失败: {ex.Message}");
            }
        }

        private void BtnRefreshDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadSettings();
                TxtDataDir.Text = _dataDir;
                TxtAutoRefreshInterval.Text = _autoRefreshIntervalMinutes.ToString();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                ApplyMiniWidgetSettings();      // 贴贴开关/模式/样式按最新配置重建
                UpdateMiniWidgetColorSettingsUI();
                UpdateMaskAccountSegmentUI();
                ResetAutoRefreshTimer();        // 自动刷新间隔若变更则按新值生效

                // 2. 归集扫描凭证 + 净化持久化（把最新配置与扫描结果写回 settings.json）
                MigrateAndScanDataDirectory();
                RenderSettingsFilesList();
                RefreshAccounts();
                UpdateDataDirStatUI();
                Log($"[数据目录] 已重载最新配置并重新扫描 ({_jsonFilePaths.Count} 个凭证)");
                ShowCustomModal("刷新成功", $"已重载最新配置并重新扫描数据目录：\n\n{_dataDir}\n\n共载入 {_jsonFilePaths.Count} 个有效凭证。", "✓");
            }
            catch (Exception ex)
            {
                Log($"[错误] 刷新数据目录失败: {ex.Message}");
            }
        }

        private void BtnBrowseDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "选择凭证与数据的存放目录",
                    SelectedPath = Directory.Exists(_dataDir) ? _dataDir : AppDomain.CurrentDomain.BaseDirectory,
                    ShowNewFolderButton = true
                };
                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dlg.SelectedPath))
                {
                    TxtDataDir.Text = dlg.SelectedPath;
                    if (!TryChangeDataDirectory(dlg.SelectedPath))
                    {
                        TxtDataDir.Text = _dataDir;
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[错误] 更改数据存放位置失败: {ex.Message}");
            }
        }

        private void BtnResetDefaultDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // 唯一路径：默认数据目录 = 软件同级 data 文件夹
                string defaultFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!Directory.Exists(defaultFolder))
                {
                    try { Directory.CreateDirectory(defaultFolder); } catch { }
                }

                if (!TryChangeDataDirectory(defaultFolder))
                {
                    TxtDataDir.Text = _dataDir;
                }
            }
            catch (Exception ex)
            {
                Log($"[错误] 恢复默认数据位置失败: {ex.Message}");
            }
        }

        private void BtnSetPortableDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string portableFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (!TryChangeDataDirectory(portableFolder))
                {
                    TxtDataDir.Text = _dataDir;
                }
            }
            catch (Exception ex)
            {
                Log($"[错误] 切换至便携目录失败: {ex.Message}");
            }
        }

        private void UpdateDataDirStatUI()
        {
            try
            {
                int validCreds = CountValidCredsIn(_dataDir);
                bool hasSettings = File.Exists(_settingsPath);
                // 唯一路径：默认数据目录 = 软件同级 data 文件夹
                string defaultFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                bool isDefault = string.Equals(_dataDir, defaultFolder, StringComparison.OrdinalIgnoreCase);

                string statText = $"📁 存有 {validCreds} 个凭证文件" + (hasSettings ? " | 已生成配置文件 settings.json" : "");
                if (TxtDataDirStat != null)
                {
                    TxtDataDirStat.Text = statText;
                }

                if (BtnResetDefaultDataDir != null)
                {
                    BtnResetDefaultDataDir.IsEnabled = !isDefault;
                    BtnResetDefaultDataDir.Opacity = isDefault ? 0.5 : 1.0;
                }
            }
            catch { }
        }

        // 统一的数据目录切换入口：
        // 1) 校验目标目录可创建、可写；2) 若旧目录存在凭证，弹「迁移 / 仅切换 / 取消」三选一；
        // 3) 切换成功后若新目录无凭证，弹出空目录引导提示。
        // 返回 false 表示未切换（用户取消或失败），调用方应还原输入框显示。
        private bool TryChangeDataDirectory(string newDir)
        {
            try
            {
                newDir = newDir.Trim();
                if (string.IsNullOrEmpty(newDir)) return false;

                try { newDir = Path.GetFullPath(newDir); } catch { }

                if (!Directory.Exists(newDir))
                {
                    try { Directory.CreateDirectory(newDir); } catch { }
                }
                if (!Directory.Exists(newDir))
                {
                    Log($"[错误] 无法创建目标目录: {newDir}");
                    ShowCustomModal("更改数据目录", $"无法创建目标目录：\n\n{newDir}", "");
                    return false;
                }

                // 可写性校验（防止只读盘/权限问题）
                try
                {
                    string testFile = Path.Combine(newDir, ".write_test_tmp");
                    File.WriteAllText(testFile, "1");
                    File.Delete(testFile);
                }
                catch
                {
                    Log($"[错误] 目标目录不可写: {newDir}");
                    ShowCustomModal("更改数据目录", $"目标目录不可写，无法作为数据目录：\n\n{newDir}", "");
                    return false;
                }

                if (newDir.Equals(_dataDir, StringComparison.OrdinalIgnoreCase))
                {
                    return true; // 路径未变化
                }

                // 统计旧目录中有效凭证（排除 settings.json 本身）
                List<string> oldCreds = new();
                try
                {
                    if (Directory.Exists(_dataDir))
                    {
                        foreach (var f in Directory.GetFiles(_dataDir, "*.json", SearchOption.TopDirectoryOnly))
                        {
                            if (f.Equals(_settingsPath, StringComparison.OrdinalIgnoreCase)) continue;
                            if (IsValidAuthJson(f)) oldCreds.Add(f);
                        }
                    }
                }
                catch { }

                // 行为由唯一事实决定，不弹选择窗：
                // 新目录是空文件夹（允许目录中仅有 settings.json——它是软件自身配置不算用户数据，
                //   若把它算作"有内容"则切到仅含配置的空目录永远无法触发迁移）→ 把旧凭证带过去（零冲突，正是选空目录的本意）
                // 新目录已有其他内容   → 只切换，旧凭证留在原地（用那边的数据）
                bool migrate = oldCreds.Count > 0 && IsDirDataEmpty(newDir);
                ApplyDataDirectorySwitch(newDir, oldCreds, migrate);
                return true;
            }
            catch (Exception ex)
            {
                Log($"[错误] 更改数据目录失败: {ex.Message}");
                ShowCustomModal("更改数据目录", $"无法更改数据目录：{ex.Message}", "");
                return false;
            }
        }

        // 目录是否无数据（顶层除 settings.json 外无任何文件；settings.json 是软件自身配置
        // 不算用户数据，不能据此判定"有内容"）
        private static bool IsDirDataEmpty(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return false;
                foreach (var f in Directory.GetFiles(dir))
                {
                    if (Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase)) continue;
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        // 统计目录中有效凭证数（排除 settings.json）
        private int CountValidCredsIn(string dir)
        {
            int n = 0;
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (var f in Directory.GetFiles(dir, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        if (Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase)) continue;
                        if (IsValidAuthJson(f)) n++;
                    }
                }
            }
            catch { }
            return n;
        }

        // 执行数据目录切换：可选迁移凭证（Move，同名跳过防覆盖），切换后归集扫描并刷新，末尾统一弹结果告知
        private void ApplyDataDirectorySwitch(string newDir, List<string> oldCreds, bool migrate)
        {
            int moved = 0, skipped = 0;
            try
            {
                if (migrate)
                {
                    foreach (var f in oldCreds)
                    {
                        try
                        {
                            string target = Path.Combine(newDir, Path.GetFileName(f));
                            if (File.Exists(target)) { skipped++; continue; }
                            File.Move(f, target);
                            moved++;
                        }
                        catch { skipped++; }
                    }

                    // 迁移 settings.json 配置文件本身（确保用户的全套偏好设置完整平滑带至新目录）
                    string oldSettingsFile = _settingsPath;
                    string newSettingsFile = Path.Combine(newDir, "settings.json");
                    if (File.Exists(oldSettingsFile) && !File.Exists(newSettingsFile))
                    {
                        try
                        {
                            File.Copy(oldSettingsFile, newSettingsFile, overwrite: false);
                            Log("[数据目录] 已平滑迁移配置文件 settings.json 至新目录");
                        }
                        catch { }
                    }

                    // 唯一路径：迁移成功后删除旧目录残留的 settings.json——配置已复制到新目录，
                    // 旧副本不再被软件引用，删除以免磁盘上出现"第二份配置"造成混淆（无配置丢失风险）
                    string? oldDirName = Path.GetDirectoryName(oldSettingsFile);
                    string? newDirName = Path.GetDirectoryName(newSettingsFile);
                    if (!string.IsNullOrEmpty(oldDirName) && !string.IsNullOrEmpty(newDirName) &&
                        !oldDirName.Equals(newDirName, StringComparison.OrdinalIgnoreCase))
                    {
                        try { File.Delete(oldSettingsFile); } catch { }
                    }

                    Log(moved > 0
                        ? $"[数据目录] 已迁移 {moved} 个凭证至新目录" + (skipped > 0 ? $"，跳过 {skipped} 个（同名或失败）" : "")
                        : $"[数据目录] 凭证未迁移（全部跳过 {skipped} 个：目标已存在或失败）");
                }

                _dataDir = newDir;
                _settingsPath = Path.Combine(_dataDir, "settings.json");
                TxtDataDir.Text = _dataDir;
                Log($"[设置] 数据存放位置已更新至: {_dataDir}");

                // 先归集扫描出净化后的凭证列表，再持久化，确保 settings.json 的 files 字段
                // 不含旧目录残留路径（此前 SaveSettings 在归集前调用，会先写入旧目录路径）
                MigrateAndScanDataDirectory();
                SaveSettings();
                RenderSettingsFilesList();
                RefreshAccounts();

                // 切换完成后的新目录凭证数（用于结果告知）
                int newCreds = CountValidCredsIn(_dataDir);

                // 结果告知弹窗：只告知结果，不再引导选择
                if (migrate && moved > 0)
                {
                    ShowCustomModal("更改数据目录",
                        $"已切换数据目录，并将 {moved} 个凭证迁移至新目录。" +
                        (skipped > 0 ? $"\n\n跳过 {skipped} 个（同名或失败）。" : "") +
                        $"\n\n新数据目录：\n{_dataDir}", "✓");
                }
                else if (migrate && moved == 0)
                {
                    ShowCustomModal("更改数据目录",
                        $"已切换数据目录，但凭证未能迁移（{skipped} 个全部跳过）。\n\n旧凭证仍保留在原数据目录中。\n\n新数据目录：\n{_dataDir}", "⚠");
                }
                else if (newCreds > 0)
                {
                    ShowCustomModal("更改数据目录",
                        $"已切换数据目录。\n\n新目录已有 {newCreds} 个凭证，" +
                        (oldCreds.Count > 0 ? $"旧目录的 {oldCreds.Count} 个凭证保留在原位置。" : "可直接使用。") +
                        $"\n\n新数据目录：\n{_dataDir}", "✓");
                }
                else
                {
                    ShowCustomModal("更改数据目录",
                        $"已切换数据目录。\n\n新目录暂无凭证，可点击主界面「导入凭证」或使用「Gemini 登录」添加。\n\n新数据目录：\n{_dataDir}", "✓");
                }
            }
            catch (Exception ex)
            {
                Log($"[错误] 应用数据目录变更失败: {ex.Message}");
                ShowCustomModal("更改数据目录", $"切换失败：{ex.Message}", "");
            }
        }

        private void BtnResetInitialData_Click(object sender, RoutedEventArgs e)
        {
            ShowConfirmModal(
                "恢复出厂设置",
                "将清空所有已导入的凭证文件与全部本地设置（贴贴配置、主题、代理等），恢复到最初安装时的干净状态。\n\n此操作不可撤销，确定要继续吗？",
                DoResetInitialData);
        }

        // 恢复初始数据：清空所有凭证文件与本地设置（保留当前数据目录路径本身）
        private void DoResetInitialData()
        {
            try
            {
                // 1. 删除所有已登记的凭证文件
                foreach (var path in _jsonFilePaths.ToList())
                {
                    try { if (File.Exists(path)) File.Delete(path); } catch { }
                }
                _jsonFilePaths.Clear();

                // 2. 清理数据目录中残留的 json 凭证文件（排除配置文件本身）
                try
                {
                    if (Directory.Exists(_dataDir))
                    {
                        foreach (var f in Directory.GetFiles(_dataDir, "*.json", SearchOption.TopDirectoryOnly))
                        {
                            if (f.Equals(_settingsPath, StringComparison.OrdinalIgnoreCase)) continue;
                            try { File.Delete(f); } catch { }
                        }
                    }
                }
                catch { }

                // 3. 重置全部内存设置为初始默认值，并覆盖写入配置文件
                ResetSettingsToDefaults();
                SaveSettings();

                // 4. 刷新界面与运行时状态
                TxtDataDir.Text = _dataDir;
                TxtAutoRefreshInterval.Text = _autoRefreshIntervalMinutes.ToString();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                UpdateSettingsSegmentedSwitchUI();
                UpdateAutoCheckUpdateUI();
                UpdateMaskAccountSegmentUI();
                UpdateMiniWidgetColorSettingsUI();
                RenderSettingsFilesList();
                RefreshAccounts();

                // 5. 重建贴贴窗口（若当前处于开启状态）
                if (_miniModeType != "off")
                {
                    try { ApplyMiniWidgetSettings(); } catch { }
                }

                Log("[设置] 已恢复初始数据：所有凭证与本地设置已清空");
                ShowCustomModal("恢复完成", "已恢复到最初安装时的干净状态。\n\n所有凭证文件与本地设置均已清空。", "✓");
            }
            catch (Exception ex)
            {
                Log($"[错误] 恢复初始数据失败: {ex.Message}");
            }
        }

        private void ResetSettingsToDefaults()
        {
            _miniModeType = "off";
            _isMiniOn = false;
            _miniWidgetBgColor = "#161B22";
            _miniWidgetBgOpacity = 0.85;
            _miniWidgetIsTransparent = false;
            _miniWidgetEdgeDock = true;
            _miniWidgetHideCardBg = false;
            _miniWidgetLeft = -1;
            _miniWidgetTop = -1;
            _miniWidgetWidth = 0;
            _miniWidgetHeight = 0;
            _mainWinLeft = -1;
            _mainWinTop = -1;
            _mainWinWidth = 0;
            _mainWinHeight = 0;
            _mainWinState = "normal";
            _autoRefreshIntervalMinutes = 5;
            _autoCheckUpdateEnabled = true;
            _themeMode = "system";
            _maskAccountInfo = false;
            _miniWidgetColor = "#FFFFFF";
        }

        private void MigrateAndScanDataDirectory()
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                // 1. 自动将软件根目录及旧 data 目录下散落的 json 凭证归集移入最新 _dataDir 文件夹
                List<string> oldPathsToMigrate = new();
                if (Directory.Exists(AppDomain.CurrentDomain.BaseDirectory))
                    oldPathsToMigrate.AddRange(Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "*.json", SearchOption.TopDirectoryOnly));
                
                string oldDataDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data");
                if (Directory.Exists(oldDataDir) && !oldDataDir.Equals(_dataDir, StringComparison.OrdinalIgnoreCase))
                    oldPathsToMigrate.AddRange(Directory.GetFiles(oldDataDir, "*.json", SearchOption.TopDirectoryOnly));

                foreach (var oldPath in oldPathsToMigrate)
                {
                    if (!IsValidAuthJson(oldPath))
                        continue;

                    string fileName = Path.GetFileName(oldPath);
                    string newPath = Path.Combine(_dataDir, fileName);
                    try
                    {
                        if (!File.Exists(newPath))
                        {
                            File.Copy(oldPath, newPath, overwrite: true);
                            Log($"[自动归集] 凭证已平滑迁移至数据目录: {fileName}");
                        }
                    }
                    catch { }
                }

                // 2. 自动检索 _dataDir 目录下的所有有效凭证 JSON 文件
                var dataJsonFiles = Directory.GetFiles(_dataDir, "*.json", SearchOption.TopDirectoryOnly);
                int added = 0;
                foreach (var file in dataJsonFiles)
                {
                    if (!IsValidAuthJson(file))
                        continue;

                    if (!_jsonFilePaths.Contains(file, StringComparer.OrdinalIgnoreCase))
                    {
                        _jsonFilePaths.Add(file);
                        added++;
                    }
                }

                // 彻底清除非凭证 JSON、配置 JSON 以及不存在的遗留路径
                // 同时仅保留位于当前数据目录下的凭证路径：切换数据目录后，旧目录的凭证路径
                // 不再残留混入列表（旧文件仍留在原目录，只是不再被本软件加载/刷新/代理使用）
                _jsonFilePaths = _jsonFilePaths
                    .Where(IsValidAuthJson)
                    .Where(p => Path.GetDirectoryName(p)?.Equals(_dataDir, StringComparison.OrdinalIgnoreCase) == true)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                // 移除失效的路径记录
                _jsonFilePaths.RemoveAll(p => !File.Exists(p));

                SaveSettings();
                if (added > 0)
                {
                    Log($"[数据目录] 自动归集并载入了 {added} 个凭证文件");
                }
            }
            catch (Exception ex)
            {
                Log($"[警告] 归集数据目录失败: {ex.Message}");
            }
        }

        private void BtnThemeSystem_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _themeMode = "system";
                SaveSettings();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                Log("[主题] 已切换为跟随系统");
            }
            catch (Exception ex) { Log($"[主题错误] {ex.Message}"); }
        }

        private void BtnThemeDark_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _themeMode = "dark";
                SaveSettings();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                Log("[主题] 已开启深色模式");
            }
            catch (Exception ex) { Log($"[主题错误] {ex.Message}"); }
        }

        private void BtnThemeLight_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _themeMode = "light";
                SaveSettings();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                Log("[主题] 已开启浅色模式");
            }
            catch (Exception ex) { Log($"[主题错误] {ex.Message}"); }
        }

        private void UpdateThemeSegmentedUI()
        {
            try
            {
                BtnThemeSystem.ClearValue(Button.BackgroundProperty);
                BtnThemeSystem.ClearValue(Button.ForegroundProperty);
                BtnThemeSystem.ClearValue(Button.BorderThicknessProperty);
                BtnThemeDark.ClearValue(Button.BackgroundProperty);
                BtnThemeDark.ClearValue(Button.ForegroundProperty);
                BtnThemeDark.ClearValue(Button.BorderThicknessProperty);
                BtnThemeLight.ClearValue(Button.BackgroundProperty);
                BtnThemeLight.ClearValue(Button.ForegroundProperty);
                BtnThemeLight.ClearValue(Button.BorderThicknessProperty);

                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");

                BtnThemeSystem.Style = _themeMode == "system" ? active : inactive;
                BtnThemeDark.Style = _themeMode == "dark" ? active : inactive;
                BtnThemeLight.Style = _themeMode == "light" ? active : inactive;
            }
            catch { }
        }

        private bool DetectSystemIsDark()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                if (key != null)
                {
                    var val = key.GetValue("AppsUseLightTheme");
                    if (val is int intVal)
                        return intVal == 0; // 0 = dark, 1 = light
                }
            }
            catch { }
            return false;
        }

        private void ApplyTheme()
        {
            try
            {
                bool isDark;
                if (_themeMode == "dark")
                    isDark = true;
                else if (_themeMode == "light")
                    isDark = false;
                else
                    isDark = DetectSystemIsDark();

                if (isDark)
                {
                    // 按照用户指定的精准深色组合配色表：#11151C, #3572EF, #161B22, #2D3748
                    SetBrush("ThemeWindowBg", "#11151C");
                    SetBrush("ThemeCardBg", "#161B22");
                    SetBrush("ThemeCardBorder", "#2D3748");
                    SetBrush("ThemePrimary", "#2563EB");
                    SetBrush("ThemeTextPrimary", "#F8FAFC");
                    SetBrush("ThemeTextSecondary", "#94A3B8");
                    SetBrush("ThemeTextTertiary", "#718096");
                    SetBrush("ThemeSubtleBg", "#1E293B");
                    SetBrush("ThemeInputBg", "#11151C");
                    SetBrush("ThemeInputBorder", "#2D3748");
                    SetBrush("ThemeBtnHeaderBg", "#161B22");
                    SetBrush("ThemeBtnHeaderFg", "#E2E8F0");
                    SetBrush("ThemeBtnHeaderBorder", "#2D3748");
                    SetBrush("ThemeSegmentInactiveFg", "#A0AEC0");
                    SetBrush("ThemeSegmentBg", "#2D3748");
                    SetBrush("ThemeScrollThumb", "#2D3748");
                    SetBrush("ThemeOverlayBg", "#B3000000");

                    _tcCardBg = "#161B22";
                    _tcCardBorder = "#2D3748";
                    _tcTextPrimary = "#F8FAFC";
                    _tcTextSecondary = "#94A3B8";
                    _tcTextTertiary = "#718096";
                    _tcTrackBg = "#11151C";
                    _tcInputBg = "#11151C";
                }
                else
                {
                    // 浅色白天模式
                    SetBrush("ThemeWindowBg", "#FAFAFA");
                    SetBrush("ThemeCardBg", "#FFFFFF");
                    SetBrush("ThemeCardBorder", "#E2E8F0");
                    SetBrush("ThemePrimary", "#2563EB");
                    SetBrush("ThemeTextPrimary", "#0F172A");
                    SetBrush("ThemeTextSecondary", "#64748B");
                    SetBrush("ThemeTextTertiary", "#94A3B8");
                    SetBrush("ThemeSubtleBg", "#F1F5F9");
                    SetBrush("ThemeInputBg", "#F8FAFC");
                    SetBrush("ThemeInputBorder", "#CBD5E1");
                    SetBrush("ThemeBtnHeaderBg", "#FFFFFF");
                    SetBrush("ThemeBtnHeaderFg", "#475569");
                    SetBrush("ThemeBtnHeaderBorder", "#CBD5E1");
                    SetBrush("ThemeSegmentInactiveFg", "#64748B");
                    SetBrush("ThemeSegmentBg", "#F1F5F9");
                    SetBrush("ThemeScrollThumb", "#CBD5E1");
                    SetBrush("ThemeOverlayBg", "#80000000");

                    _tcCardBg = "#FFFFFF";
                    _tcCardBorder = "#E2E8F0";
                    _tcTextPrimary = "#0F172A";
                    _tcTextSecondary = "#64748B";
                    _tcTextTertiary = "#94A3B8";
                    _tcTrackBg = "#F1F5F9";
                    _tcInputBg = "#F8FAFC";
            }

            SetWindowDarkTitleBar(isDark);
            RenderSettingsFilesList();
            RefreshAccountsUIOnly();
            if (IsLoaded)
            {
                UpdateMiniWidgetColorSettingsUI();
            }
        }
        catch (Exception ex)
        {
            Log($"[主题应用错误] {ex.Message}");
        }
        }

        private void SetBrush(string key, string hex)
        {
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(hex);
                var brush = new SolidColorBrush(color);
                brush.Freeze(); // 冻结以获得安全跨线程与无故障更新
                Resources[key] = brush;
            }
            catch { }
        }

        // =================== Mini Widget Custom Color & Color Picker Handlers ===================

        private static string NormalizeColorHex(string? value, string fallback)
        {
            if (string.IsNullOrWhiteSpace(value)) return fallback;

            string candidate = value.Trim();
            if (!candidate.StartsWith("#", StringComparison.Ordinal)) candidate = "#" + candidate;
            try
            {
                Color color = (Color)ColorConverter.ConvertFromString(candidate);
                return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            }
            catch
            {
                return fallback;
            }
        }

        private void UpdateMiniWidgetColorSettingsUI()
        {
            _miniWidgetColor = NormalizeColorHex(_miniWidgetColor, "#FFFFFF");
            _miniWidgetBgColor = NormalizeColorHex(_miniWidgetBgColor, "#161B22");

            TxtMiniWidgetColorDisplay.Text = _miniWidgetColor;
            TxtMiniWidgetBgColorDisplay.Text = _miniWidgetBgColor;
            BorderCurrentColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_miniWidgetColor));
            BorderCurrentBgColorPreview.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_miniWidgetBgColor));

            UpdatePresetSelection(PanelAccentPresets, _miniWidgetColor);
            UpdatePresetSelection(PanelBackgroundPresets, _miniWidgetBgColor);

            try
            {
                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");
                BtnEdgeDockOn.Style = _miniWidgetEdgeDock ? active : inactive;
                BtnEdgeDockOff.Style = _miniWidgetEdgeDock ? inactive : active;
                BtnHideCardBgOn.Style = _miniWidgetHideCardBg ? active : inactive;
                BtnHideCardBgOff.Style = _miniWidgetHideCardBg ? inactive : active;
            }
            catch { }
        }

        private void BtnHideCardBgOn_Click(object sender, RoutedEventArgs e)
        {
            SetHideCardBg(true);
        }

        private void BtnHideCardBgOff_Click(object sender, RoutedEventArgs e)
        {
            SetHideCardBg(false);
        }

        private void SetHideCardBg(bool enabled)
        {
            try
            {
                _miniWidgetHideCardBg = enabled;
                SaveSettings();
                UpdateMiniWidgetColorSettingsUI();
                ApplyCurrentMiniWidgetStyle();
            }
            catch (Exception ex)
            {
                Log($"[错误] 设置隐藏圆角矩形失败: {ex.Message}");
            }
        }

        private void BtnEdgeDockOn_Click(object sender, RoutedEventArgs e)
        {
            SetEdgeDockEnabled(true);
        }

        private void BtnEdgeDockOff_Click(object sender, RoutedEventArgs e)
        {
            SetEdgeDockEnabled(false);
        }

        private void SetEdgeDockEnabled(bool enabled)
        {
            try
            {
                _miniWidgetEdgeDock = enabled;
                SaveSettings();
                UpdateMiniWidgetColorSettingsUI();
                ApplyCurrentMiniWidgetStyle();
                Log($"[悬浮贴贴] 贴边自动收纳: {(enabled ? "已开启" : "已关闭")}");
            }
            catch { }
        }

        private void UpdatePresetSelection(Panel panel, string selectedHex)
        {
            var selectedBrush = (Brush)FindResource("ThemePrimary");
            var normalBrush = (Brush)FindResource("ThemeCardBorder");
            foreach (UIElement child in panel.Children)
            {
                if (child is not Border swatch) continue;
                bool selected = string.Equals(swatch.Tag as string, selectedHex, StringComparison.OrdinalIgnoreCase);
                swatch.BorderBrush = selected ? selectedBrush : normalBrush;
                swatch.BorderThickness = selected ? new Thickness(3) : new Thickness(1);
                swatch.Padding = selected ? new Thickness(2) : new Thickness(0);
            }
        }

        private void ApplyMiniWidgetColor(string hex)
        {
            try
            {
                _miniWidgetColor = NormalizeColorHex(hex, _miniWidgetColor);
                UpdateMiniWidgetColorSettingsUI();

                SaveSettings();
                _miniWidget?.SetCustomColor(_miniWidgetColor);
                ApplyCurrentMiniWidgetStyle();
                UpdateMiniWidgetData();

                Log($"[微贴主题] 微贴显示颜色已更新为: {_miniWidgetColor}");
            }
            catch
            {
                TxtMiniWidgetColorDisplay.Text = _miniWidgetColor;
            }
        }

        private void BtnColorPreset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string hex)
            {
                ApplyMiniWidgetColor(hex);
            }
        }

        private void BtnOpenColorPicker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new System.Windows.Forms.ColorDialog();
                dlg.FullOpen = true;
                if (!string.IsNullOrEmpty(_miniWidgetColor))
                {
                    try
                    {
                        var wpfColor = (Color)ColorConverter.ConvertFromString(_miniWidgetColor);
                        dlg.Color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
                    }
                    catch { }
                }

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dlg.Color;
                    string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    ApplyMiniWidgetColor(hex);
                }
            }
            catch (Exception ex)
            {
                Log($"[取色器错误] {ex.Message}");
            }
        }

        private bool _isUpdatingUI = false;
        private string _lastActiveModeType = "";

        public void ApplyMiniWidgetSettings()
        {
            try
            {
                UpdateSettingsSegmentedSwitchUI();

                if (_miniModeType == "off")
                {
                    DestroyMiniWidgetWindow();
                    _lastActiveModeType = "off";
                    return;
                }

                if (_miniWidget == null || _lastActiveModeType != _miniModeType)
                {
                    DestroyMiniWidgetWindow();
                    _miniWidget = new MiniWidgetWindow(this);
                    _lastActiveModeType = _miniModeType;
                }

                // 在创建原生句柄前先写入目标模式，避免浮动窗口 Loaded 时被默认嵌入任务栏。
                ApplyCurrentMiniWidgetStyle();

                if (!_miniWidget.IsVisible)
                {
                    _miniWidget.Show();
                }

                _miniWidget.Visibility = Visibility.Visible;
                ApplyCurrentMiniWidgetStyle();
                UpdateMiniWidgetData();
            }
            catch (Exception ex)
            {
                Log($"[贴贴模式应用错误] {ex.Message}");
                DestroyMiniWidgetWindow();
                _lastActiveModeType = "";
            }
        }

        private void ApplyCurrentMiniWidgetStyle()
        {
            _miniWidget?.ApplyModeAndStyles(
                _miniModeType,
                _miniWidgetColor,
                _miniWidgetBgColor,
                _miniWidgetBgOpacity,
                _miniWidgetIsTransparent,
                _miniWidgetLeft,
                _miniWidgetTop,
                _miniWidgetEdgeDock,
                _miniWidgetWidth,
                _miniWidgetHeight,
                _miniWidgetHideCardBg
            );
        }

        private void DestroyMiniWidgetWindow()
        {
            if (_miniWidget == null) return;
            try { _miniWidget.CloseWindow(); } catch { }
            _miniWidget = null;
        }

        public void SetMiniWidgetMode(string modeType)
        {
            if (modeType != "off" && modeType != "taskbar" && modeType != "floating")
            {
                modeType = "off";
            }

            _miniModeType = modeType;
            _isMiniOn = modeType != "off";
            SaveSettings();
            ApplyMiniWidgetSettings();
        }

        public void EnableMiniWidget(bool hideMainWindow = false)
        {
            SetMiniWidgetMode(_miniModeType == "off" ? "taskbar" : _miniModeType);
            if (hideMainWindow)
            {
                Hide();
            }
        }

        public void DisableMiniWidget()
        {
            SetMiniWidgetMode("off");
        }

        private void BtnModeOff_Click(object sender, RoutedEventArgs e)
        {
            SetMiniWidgetMode("off");
            Log("[设置] 已关闭贴贴");
        }

        private void BtnModeTaskbar_Click(object sender, RoutedEventArgs e)
        {
            SetMiniWidgetMode("taskbar");
            Log("[设置] 已开启任务栏贴贴模式");
        }

        private void BtnModeFloating_Click(object sender, RoutedEventArgs e)
        {
            SetMiniWidgetMode("floating");
            Log("[设置] 已开启桌面悬浮贴贴模式");
        }

        private void UpdateSettingsSegmentedSwitchUI()
        {
            _isUpdatingUI = true;
            try
            {
                BtnModeOff.ClearValue(Button.BackgroundProperty);
                BtnModeOff.ClearValue(Button.ForegroundProperty);
                BtnModeOff.ClearValue(Button.BorderThicknessProperty);

                BtnModeTaskbar.ClearValue(Button.BackgroundProperty);
                BtnModeTaskbar.ClearValue(Button.ForegroundProperty);
                BtnModeTaskbar.ClearValue(Button.BorderThicknessProperty);

                BtnModeFloating.ClearValue(Button.BackgroundProperty);
                BtnModeFloating.ClearValue(Button.ForegroundProperty);
                BtnModeFloating.ClearValue(Button.BorderThicknessProperty);

                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");

                BtnModeOff.Style = _miniModeType == "off" ? active : inactive;
                BtnModeTaskbar.Style = _miniModeType == "taskbar" ? active : inactive;
                BtnModeFloating.Style = _miniModeType == "floating" ? active : inactive;

                PanelFloatingSettings.Visibility = _miniModeType == "floating" ? Visibility.Visible : Visibility.Collapsed;

                UpdateMiniWidgetColorSettingsUI();

                SliderBgOpacity.Value = Math.Round(_miniWidgetBgOpacity * 100);
                TxtBgOpacityVal.Text = $"{Math.Round(_miniWidgetBgOpacity * 100):0}%";
            }
            finally
            {
                _isUpdatingUI = false;
            }
        }

        private void BtnBgColorPreset_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement el && el.Tag is string hex)
            {
                ApplyMiniWidgetBgColor(hex);
            }
        }

        private void ToggleSettingsSection_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button btn) return;

                // 内嵌按钮（如本地代理头部的 运行/停止）的 Click 会冒泡到此头部按钮，
                // 从事件源向上查找：若先遇到其他 Button，说明点的是内嵌按钮，不触发折叠
                if (e.OriginalSource is DependencyObject src)
                {
                    for (DependencyObject? node = src; node != null; node = VisualTreeHelper.GetParent(node))
                    {
                        if (node is Button b)
                        {
                            if (b != btn) return;
                            break;
                        }
                    }
                }

                var parts = (btn.Tag as string)?.Split('|');
                if (parts == null || parts.Length != 2) return;

                if (FindName(parts[0]) is UIElement content && FindName(parts[1]) is TextBlock arrow)
                {
                    bool isOpen = content.Visibility == Visibility.Visible;
                    content.Visibility = isOpen ? Visibility.Collapsed : Visibility.Visible;
                    arrow.Text = isOpen ? "▶" : "▼";
                    // 展开/收起后同步卡片高亮描边（同主界面凭证卡片 hover 同款）
                    SetSettingsCardHighlight(btn, !isOpen);
                }
            }
            catch { }
        }

        private void SetSettingsCardHighlight(Button header, bool open)
        {
            try
            {
                // 从头部按钮沿视觉树向上找卡片容器 Border（VergeCard）
                for (DependencyObject? node = VisualTreeHelper.GetParent(header); node != null; node = VisualTreeHelper.GetParent(node))
                {
                    if (node is Border bd)
                    {
                        if (open)
                            bd.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x25, 0x63, 0xEB)); // #2563EB 45%
                        else
                            bd.ClearValue(Border.BorderBrushProperty); // 清除本地值，恢复 VergeCard 样式的 DynamicResource（跟随主题）
                        return;
                    }
                }
            }
            catch { }
        }

        private void SyncAllSettingsCardHighlights()
        {
            try
            {
                foreach (var btn in FindVisualChildren<Button>(ViewSettings))
                {
                    if (btn.Tag is not string tag) continue;
                    var parts = tag.Split('|');
                    if (parts.Length != 2) continue;
                    if (FindName(parts[0]) is UIElement content)
                        SetSettingsCardHighlight(btn, content.Visibility == Visibility.Visible);
                }
            }
            catch { }
        }

        private void BtnOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            SwitchView(isMain: false);
        }

        private void TxtDataDir_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyTxtDataDirChange();
        }

        private void TxtDataDir_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyTxtDataDirChange();
            }
        }

        private void ApplyTxtDataDirChange()
        {
            string inputPath = TxtDataDir.Text.Trim();
            if (string.IsNullOrEmpty(inputPath))
            {
                TxtDataDir.Text = _dataDir;
                return;
            }

            if (!TryChangeDataDirectory(inputPath))
            {
                TxtDataDir.Text = _dataDir;
            }
        }

        private void TxtAutoRefreshInterval_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyAutoRefreshIntervalChange();
        }

        private void TxtAutoRefreshInterval_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyAutoRefreshIntervalChange();
            }
        }

        private void BtnSaveAutoRefresh_Click(object sender, RoutedEventArgs e)
        {
            ApplyAutoRefreshIntervalChange();
        }

        private void ApplyAutoRefreshIntervalChange()
        {
            if (int.TryParse(TxtAutoRefreshInterval.Text.Trim(), out int minutes) && minutes >= 0)
            {
                _autoRefreshIntervalMinutes = minutes;
                SaveSettings();
                ResetAutoRefreshTimer();
                if (minutes > 0)
                {
                    Log($"[定时刷新] 已更新为每 {minutes} 分钟自动刷新一次配额");
                }
                else
                {
                    Log("[定时刷新] 已关闭后台自动刷新");
                }
            }
            else
            {
                TxtAutoRefreshInterval.Text = _autoRefreshIntervalMinutes.ToString();
                Log("[警告] 刷新间隔请输入大于等于 0 的整数");
            }
        }

        private void InitAutoRefreshTimer()
        {
            try
            {
                _autoRefreshTimer = new System.Windows.Threading.DispatcherTimer();
                _autoRefreshTimer.Tick += (s, e) =>
                {
                    if (_jsonFilePaths.Count > 0)
                    {
                        Log($"[定时刷新] 触发自动刷新配额 ({_autoRefreshIntervalMinutes} 分钟间隔)...");
                        RefreshAccounts();
                    }
                };
                ResetAutoRefreshTimer();
            }
            catch { }
        }

        private void ResetAutoRefreshTimer()
        {
            try
            {
                _autoRefreshTimer?.Stop();
                if (_autoRefreshIntervalMinutes > 0)
                {
                    _autoRefreshTimer!.Interval = TimeSpan.FromMinutes(_autoRefreshIntervalMinutes);
                    _autoRefreshTimer.Start();
                }
            }
            catch { }
        }

        private void BtnOpenDataDir_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }
                System.Diagnostics.Process.Start("explorer.exe", _dataDir);
            }
            catch (Exception ex)
            {
                Log($"[错误] 打开数据目录失败: {ex.Message}");
            }
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t) yield return t;
                foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
            }
        }

        private void UpdateAutoCheckUpdateUI()
        {
            try
            {
                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");
                BtnAutoCheckOn.Style = _autoCheckUpdateEnabled ? active : inactive;
                BtnAutoCheckOff.Style = _autoCheckUpdateEnabled ? inactive : active;
            }
            catch { }
        }

        private void BtnAutoCheckOn_Click(object sender, RoutedEventArgs e)
        {
            _autoCheckUpdateEnabled = true;
            UpdateAutoCheckUpdateUI();
            SaveSettings();
            Log("[设置] 已开启启动时自动检查更新");
        }

        private void BtnAutoCheckOff_Click(object sender, RoutedEventArgs e)
        {
            _autoCheckUpdateEnabled = false;
            UpdateAutoCheckUpdateUI();
            SaveSettings();
            Log("[设置] 已关闭启动时自动检查更新");
        }

        // =================== Google Drive 配置同步（设置页「同步」卡片） ===================

        // ---- DPAPI：当前用户级加解密（P/Invoke crypt32.dll，零 NuGet 依赖） ----

        [StructLayout(LayoutKind.Sequential)]
        private struct DATA_BLOB
        {
            public int cbData;
            public IntPtr pbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(ref DATA_BLOB pDataIn, string szDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, out DATA_BLOB pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        private static string? ProtectString(string plain)
        {
            try
            {
                if (string.IsNullOrEmpty(plain)) return null;
                byte[] data = Encoding.UTF8.GetBytes(plain);
                DATA_BLOB input = new DATA_BLOB { cbData = data.Length, pbData = Marshal.AllocHGlobal(data.Length) };
                try
                {
                    Marshal.Copy(data, 0, input.pbData, data.Length);
                    if (CryptProtectData(ref input, "Haodo Google Token", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DATA_BLOB output))
                    {
                        try
                        {
                            byte[] enc = new byte[output.cbData];
                            Marshal.Copy(output.pbData, enc, 0, output.cbData);
                            return Convert.ToBase64String(enc);
                        }
                        finally { LocalFree(output.pbData); }
                    }
                }
                finally { Marshal.FreeHGlobal(input.pbData); }
            }
            catch { }
            return null;
        }

        private static string? UnprotectString(string? protectedBase64)
        {
            try
            {
                if (string.IsNullOrEmpty(protectedBase64)) return null;
                byte[] enc = Convert.FromBase64String(protectedBase64);
                DATA_BLOB input = new DATA_BLOB { cbData = enc.Length, pbData = Marshal.AllocHGlobal(enc.Length) };
                try
                {
                    Marshal.Copy(enc, 0, input.pbData, enc.Length);
                    if (CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, out DATA_BLOB output))
                    {
                        try
                        {
                            byte[] plain = new byte[output.cbData];
                            Marshal.Copy(output.pbData, plain, 0, output.cbData);
                            return Encoding.UTF8.GetString(plain);
                        }
                        finally { LocalFree(output.pbData); }
                    }
                }
                finally { Marshal.FreeHGlobal(input.pbData); }
            }
            catch { }
            return null;
        }

        // ---- Google 登录令牌持久化（data/google-token.json，DPAPI 加密敏感字段） ----

        private string GoogleTokenFilePath => Path.Combine(_dataDir, "google-token.json");

        private void LoadGoogleToken()
        {
            try
            {
                _googleTokenState = null;
                string path = GoogleTokenFilePath;
                if (!File.Exists(path)) return;
                string json = File.ReadAllText(path);
                var st = JsonSerializer.Deserialize<GoogleTokenState>(json);
                if (st == null || string.IsNullOrEmpty(st.RefreshTokenProtected))
                {
                    _googleTokenState = null;
                    return;
                }
                // 校验 DPAPI 可解密（如系统用户环境变更则视为未登录）
                if (string.IsNullOrEmpty(UnprotectString(st.RefreshTokenProtected)))
                {
                    _googleTokenState = null;
                    return;
                }
                _googleTokenState = st;
            }
            catch
            {
                _googleTokenState = null;
            }
        }

        /// <summary>存量 token 补齐：旧版本登录时未保存头像 URL，启动后用 access_token 调一次 userinfo 补回并落盘</summary>
        private async Task BackfillGooglePictureAsync()
        {
            if (_googleTokenState == null || !string.IsNullOrEmpty(_googleTokenState.PictureUrl)) return;
            try
            {
                var (ok, at, _) = await EnsureGoogleAccessTokenAsync();
                if (!ok || string.IsNullOrEmpty(at)) return;
                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                using var ureq = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
                ureq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", at);
                var uresp = await hc.SendAsync(ureq);
                if (!uresp.IsSuccessStatusCode) return;
                using var udoc = JsonDocument.Parse(await uresp.Content.ReadAsStringAsync());
                if (!udoc.RootElement.TryGetProperty("picture", out var pic)) return;
                string? url = pic.GetString();
                if (string.IsNullOrEmpty(url)) return;
                _googleTokenState!.PictureUrl = url;
                SaveGoogleToken();
                Log("[同步] 已补齐 Google 头像 URL");
                Dispatcher.Invoke(() => UpdateGDriveAvatar());
            }
            catch { }
        }

        private void SaveGoogleToken()
        {
            try
            {
                if (_googleTokenState == null)
                {
                    if (File.Exists(GoogleTokenFilePath)) File.Delete(GoogleTokenFilePath);
                    return;
                }
                File.WriteAllText(GoogleTokenFilePath, JsonSerializer.Serialize(_googleTokenState, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex)
            {
                Log($"[同步] 保存 Google 登录凭证失败: {ex.Message}");
            }
        }

        private void ClearGoogleToken()
        {
            _googleTokenState = null;
            try { if (File.Exists(GoogleTokenFilePath)) File.Delete(GoogleTokenFilePath); } catch { }
        }

        private static string GenerateRandomBase64Url(int byteCount)
        {
            byte[] buf = RandomNumberGenerator.GetBytes(byteCount);
            return Convert.ToBase64String(buf).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---- OAuth 登录（PKCE + 本地回环回调） ----

        private void BtnGoogleLogin_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_oauthListener != null)
                {
                    ShowCustomModal("登录 Google", "正在等待浏览器授权完成，请勿重复点击。");
                    return;
                }
                if (!StartOAuthListener()) return;

                string verifier = GenerateRandomBase64Url(48);
                string challenge;
                using (var sha = SHA256.Create())
                {
                    challenge = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(verifier)))
                        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
                }
                string state = GenerateRandomBase64Url(24);
                _oauthCodeVerifier = verifier;
                _oauthState = state;

                string url = $"https://accounts.google.com/o/oauth2/v2/auth?client_id={GoogleClientId}" +
                             $"&redirect_uri={Uri.EscapeDataString(GoogleRedirectUri)}" +
                             $"&response_type=code&scope={Uri.EscapeDataString(GoogleScope)}" +
                             $"&access_type=offline&prompt=consent" +
                             $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                Log("[同步] 已打开 Google 授权页，等待回调完成");

                // 10 分钟无回调则自动关闭监听（用户放弃登录）
                Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromMinutes(10));
                    lock (_oauthLock)
                    {
                        if (_oauthListener != null)
                        {
                            try { _oauthListener.Stop(); } catch { }
                            _oauthListener = null;
                            _oauthState = null;
                            _oauthCodeVerifier = null;
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                Log($"[同步] 启动 Google 登录失败: {ex.Message}");
                ShowCustomModal("登录 Google", $"无法打开授权流程：{ex.Message}");
            }
        }

        private bool StartOAuthListener()
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, GoogleOAuthPort);
                listener.Start();
                _oauthListener = listener;
                Task.Run(() => OAuthListenerLoop(listener));
                return true;
            }
            catch (Exception ex)
            {
                Log($"[同步] 回调监听端口 {GoogleOAuthPort} 启动失败: {ex.Message}");
                ShowCustomModal("登录 Google",
                    $"无法监听本地回调端口 {GoogleOAuthPort}（可能已被其他程序占用，如辉夜姬画布）。\n请关闭占用该端口的程序后重试。");
                return false;
            }
        }

        private void OAuthListenerLoop(TcpListener listener)
        {
            try
            {
                while (true)
                {
                    TcpClient client;
                    try { client = listener.AcceptTcpClient(); }
                    catch { break; } // 监听已停止
                    using (client)
                    {
                        if (HandleOAuthHttpRequest(client)) break;
                    }
                }
            }
            catch { }
            finally
            {
                try { listener.Stop(); } catch { }
                lock (_oauthLock)
                {
                    if (_oauthListener == listener) _oauthListener = null;
                }
            }
        }

        /// <summary>处理单次本地 HTTP 请求；返回 true 表示回调已完成（关闭监听）</summary>
        private bool HandleOAuthHttpRequest(TcpClient client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var sb = new StringBuilder();
                var buf = new byte[1];
                while (sb.Length < 8192 && stream.Read(buf, 0, 1) > 0)
                {
                    sb.Append((char)buf[0]);
                    string head = sb.ToString();
                    if (head.EndsWith("\r\n\r\n") || head.EndsWith("\n\n")) break;
                }
                string request = sb.ToString();
                string firstLine = request.Split('\n')[0].Trim();

                if (!firstLine.StartsWith("GET /oauth/google/callback", StringComparison.OrdinalIgnoreCase))
                {
                    // 其他路径（浏览器预取等）→ 直接空响应
                    WriteSimpleHtmlResponse(stream, 204, "", "");
                    return false;
                }

                // 解析 query：GET /oauth/google/callback?code=...&state=... HTTP/1.1
                int qIdx = firstLine.IndexOf('?');
                int spIdx = firstLine.IndexOf(' ', qIdx > 0 ? qIdx : 0);
                string query = qIdx > 0 ? firstLine.Substring(qIdx + 1, (spIdx > qIdx ? spIdx : firstLine.Length) - qIdx - 1) : "";
                var qs = ParseQueryString(query);
                string? code = qs.TryGetValue("code", out var c) ? c : null;
                string? state = qs.TryGetValue("state", out var s) ? s : null;
                bool hasError = qs.ContainsKey("error");

                bool stateOk = !string.IsNullOrEmpty(state) && _oauthState != null && state == _oauthState;
                if (hasError || string.IsNullOrEmpty(code) || !stateOk)
                {
                    WriteSimpleHtmlResponse(stream, 200, "登录失败", stateOk
                        ? "Google 授权被取消或失败，请重试。"
                        : "授权回调校验失败（state 不匹配），请重试。");
                    lock (_oauthLock) { _oauthState = null; _oauthCodeVerifier = null; }
                    return true;
                }

                // 换取令牌（同步等待，超时 30s）
                string? userEmail = null;
                string? userPicture = null;
                string? accessToken = null;
                string? refreshToken = null;
                long expiresAt = 0;
                try
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                    var form = new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["code"] = code,
                        ["client_id"] = GoogleClientId,
                        ["client_secret"] = GoogleClientSecret,
                        ["redirect_uri"] = GoogleRedirectUri,
                        ["grant_type"] = "authorization_code",
                        ["code_verifier"] = _oauthCodeVerifier ?? ""
                    });
                    var resp = hc.PostAsync("https://oauth2.googleapis.com/token", form).GetAwaiter().GetResult();
                    string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode)
                    {
                        using var doc = JsonDocument.Parse(body);
                        accessToken = doc.RootElement.TryGetProperty("access_token", out var at) ? at.GetString() : null;
                        refreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
                        long expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out long secs) ? secs : 3600;
                        expiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
                        try
                        {
                            using var ureq = new HttpRequestMessage(HttpMethod.Get, "https://openidconnect.googleapis.com/v1/userinfo");
                            if (accessToken != null)
                                ureq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                            var uresp = hc.SendAsync(ureq).GetAwaiter().GetResult();
                            if (uresp.IsSuccessStatusCode)
                            {
                                using var udoc = JsonDocument.Parse(uresp.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                                if (udoc.RootElement.TryGetProperty("email", out var em)) userEmail = em.GetString();
                                if (udoc.RootElement.TryGetProperty("picture", out var pic)) userPicture = pic.GetString();
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        Log($"[同步] 换取令牌失败: HTTP {(int)resp.StatusCode} {body}");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[同步] 换取令牌异常: {ex.Message}");
                }

                if (string.IsNullOrEmpty(refreshToken) || string.IsNullOrEmpty(accessToken))
                {
                    WriteSimpleHtmlResponse(stream, 200, "登录失败", "获取 Google 访问令牌失败，请重试。");
                    lock (_oauthLock) { _oauthState = null; _oauthCodeVerifier = null; }
                    return true;
                }

                var st = new GoogleTokenState
                {
                    UserEmail = string.IsNullOrEmpty(userEmail) ? "Google 用户" : userEmail,
                    PictureUrl = string.IsNullOrEmpty(userPicture) ? null : userPicture,
                    RefreshTokenProtected = ProtectString(refreshToken),
                    AccessTokenProtected = ProtectString(accessToken),
                    ExpiresAtUnixMs = expiresAt,
                    FileId = null,
                    CredentialsFileId = null,
                    LastSyncUnixMs = 0
                };
                _googleTokenState = st;
                SaveGoogleToken();
                lock (_oauthLock) { _oauthState = null; _oauthCodeVerifier = null; }

                WriteSimpleHtmlResponse(stream, 200, "登录成功", "Google Drive 登录成功，可以关闭此页面并返回 Haodo。");
                Log($"[同步] Google 登录成功: {st.UserEmail}");
                Dispatcher.Invoke(UpdateGDriveUI);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(query)) return dict;
            foreach (var pair in query.Split('&'))
            {
                if (string.IsNullOrEmpty(pair)) continue;
                int eq = pair.IndexOf('=');
                string k = UrlDecodeSimple(eq >= 0 ? pair.Substring(0, eq) : pair);
                string v = eq >= 0 ? UrlDecodeSimple(pair.Substring(eq + 1)) : "";
                if (!dict.ContainsKey(k)) dict[k] = v;
            }
            return dict;
        }

        private static string UrlDecodeSimple(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            var result = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '+') result.Append(' ');
                else if (c == '%' && i + 2 < s.Length && Uri.IsHexDigit(s[i + 1]) && Uri.IsHexDigit(s[i + 2]))
                {
                    result.Append((char)Convert.ToInt32(s.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else result.Append(c);
            }
            return result.ToString();
        }

        private static void WriteSimpleHtmlResponse(NetworkStream stream, int statusCode, string title, string message)
        {
            try
            {
                string statusText = statusCode == 204 ? "No Content" : "OK";
                string bodyHtml = "";
                if (statusCode != 204)
                {
                    bodyHtml =
                        "<!DOCTYPE html><html lang=\"zh\"><head><meta charset=\"utf-8\"><title>" + title + "</title></head>" +
                        "<body style=\"font-family:'Microsoft YaHei',sans-serif;background:#f5f6f8;display:flex;align-items:center;justify-content:center;height:100vh;margin:0;\">" +
                        "<div style=\"background:#fff;border-radius:12px;padding:36px 44px;text-align:center;box-shadow:0 8px 30px rgba(0,0,0,.08);\">" +
                        "<h2 style=\"margin:0 0 10px;color:#111;\">" + title + "</h2>" +
                        "<p style=\"margin:0;color:#666;font-size:14px;\">" + message + "</p></div></body></html>";
                }
                string respStr = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                                 "Content-Type: text/html; charset=utf-8\r\n" +
                                 $"Content-Length: {Encoding.UTF8.GetByteCount(bodyHtml)}\r\n" +
                                 "Connection: close\r\n\r\n" + bodyHtml;
                byte[] bytes = Encoding.UTF8.GetBytes(respStr);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch { }
        }

        // ---- 令牌续期 ----

        private async Task<(bool ok, string? accessToken, bool authFailed)> EnsureGoogleAccessTokenAsync()
        {
            if (_googleTokenState == null) return (false, null, false);
            try
            {
                string? at = UnprotectString(_googleTokenState.AccessTokenProtected);
                long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                if (!string.IsNullOrEmpty(at) && _googleTokenState.ExpiresAtUnixMs > now + 120_000)
                    return (true, at, false);

                string? rt = UnprotectString(_googleTokenState.RefreshTokenProtected);
                if (string.IsNullOrEmpty(rt))
                {
                    ClearGoogleToken();
                    return (false, null, true);
                }

                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
                var form = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["refresh_token"] = rt,
                    ["client_id"] = GoogleClientId,
                    ["client_secret"] = GoogleClientSecret,
                    ["grant_type"] = "refresh_token"
                });
                var resp = await hc.PostAsync("https://oauth2.googleapis.com/token", form);
                string body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    Log($"[同步] access_token 刷新失败: HTTP {(int)resp.StatusCode}");
                    if (body.Contains("invalid_grant") || body.Contains("invalid_client"))
                    {
                        ClearGoogleToken();
                        return (false, null, true);
                    }
                    return (false, null, false);
                }

                using var doc = JsonDocument.Parse(body);
                string? newAt = doc.RootElement.TryGetProperty("access_token", out var a) ? a.GetString() : null;
                long expiresIn = doc.RootElement.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out long secs) ? secs : 3600;
                if (string.IsNullOrEmpty(newAt)) return (false, null, false);

                _googleTokenState.AccessTokenProtected = ProtectString(newAt);
                _googleTokenState.ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
                SaveGoogleToken();
                return (true, newAt, false);
            }
            catch (Exception ex)
            {
                Log($"[同步] 刷新令牌异常: {ex.Message}");
                return (false, null, false);
            }
        }

        // ---- Drive 文件操作（固定单文件覆盖式） ----

        private async Task<(bool ok, string? fileId, bool authFailed, string? error)> FindGoogleFileAsync(HttpClient hc, string accessToken, string fileName)
        {
            try
            {
                string url = "https://www.googleapis.com/drive/v3/files?" +
                             $"q={Uri.EscapeDataString($"name='{fileName}' and trashed=false")}&spaces=drive&fields=files(id,name)&pageSize=1";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await hc.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    return (false, null, true, body);
                if (!resp.IsSuccessStatusCode) return (false, null, false, $"HTTP {(int)resp.StatusCode}");
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("files", out var files) && files.GetArrayLength() > 0 && files[0].TryGetProperty("id", out var id))
                    return (true, id.GetString(), false, null);
                return (true, null, false, null);
            }
            catch (Exception ex)
            {
                return (false, null, false, ex.Message);
            }
        }

        private async Task<(bool ok, string? fileId, bool authFailed, string? error)> UploadGoogleFileAsync(HttpClient hc, string accessToken, string fileName, string json, string? existingFileId)
        {
            try
            {
                if (!string.IsNullOrEmpty(existingFileId))
                {
                    string url = $"https://www.googleapis.com/upload/drive/v3/files/{existingFileId}?uploadType=media";
                    using var req = new HttpRequestMessage(new HttpMethod("PATCH"), url);
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                    req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                    var resp = await hc.SendAsync(req);
                    string body = await resp.Content.ReadAsStringAsync();
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        return (false, null, true, body);
                    if (!resp.IsSuccessStatusCode) return (false, null, false, $"HTTP {(int)resp.StatusCode}");
                    return (true, existingFileId, false, null);
                }

                // 新建：multipart/related（元数据 + 内容）
                string boundary = "HaodoBoundary" + Guid.NewGuid().ToString("N");
                using var ms = new MemoryStream();
                byte[] meta = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: application/json; charset=UTF-8\r\n\r\n" +
                                                     $"{{\"name\":\"{fileName}\",\"mimeType\":\"application/json\"}}\r\n");
                ms.Write(meta, 0, meta.Length);
                byte[] body2 = Encoding.UTF8.GetBytes($"--{boundary}\r\nContent-Type: application/json\r\n\r\n{json}\r\n--{boundary}--\r\n");
                ms.Write(body2, 0, body2.Length);
                ms.Position = 0;

                using var createReq = new HttpRequestMessage(HttpMethod.Post, "https://www.googleapis.com/upload/drive/v3/files?uploadType=multipart");
                createReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var content = new StreamContent(ms);
                content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/related; boundary={boundary}");
                createReq.Content = content;
                var createResp = await hc.SendAsync(createReq);
                string respBody = await createResp.Content.ReadAsStringAsync();
                if (createResp.StatusCode == HttpStatusCode.Unauthorized || createResp.StatusCode == HttpStatusCode.Forbidden)
                    return (false, null, true, respBody);
                if (!createResp.IsSuccessStatusCode) return (false, null, false, $"HTTP {(int)createResp.StatusCode}");
                using var doc = JsonDocument.Parse(respBody);
                if (doc.RootElement.TryGetProperty("id", out var id)) return (true, id.GetString(), false, null);
                return (false, null, false, "未返回文件 ID");
            }
            catch (Exception ex)
            {
                return (false, null, false, ex.Message);
            }
        }

        private async Task<(bool ok, string? json, bool authFailed, string? error)> DownloadGoogleFileAsync(HttpClient hc, string accessToken, string fileId)
        {
            try
            {
                string url = $"https://www.googleapis.com/drive/v3/files/{fileId}?alt=media";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", accessToken);
                var resp = await hc.SendAsync(req);
                string body = await resp.Content.ReadAsStringAsync();
                if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                    return (false, null, true, body);
                if (!resp.IsSuccessStatusCode) return (false, null, false, $"HTTP {(int)resp.StatusCode}");
                return (true, body, false, null);
            }
            catch (Exception ex)
            {
                return (false, null, false, ex.Message);
            }
        }

        // ---- 同步内容：偏好配置 + 凭证打包 ----

        private Dictionary<string, object?> CollectSyncConfig()
        {
            return new Dictionary<string, object?>
            {
                ["app"] = "haodo",
                ["configVersion"] = 1,
                ["themeMode"] = _themeMode,
                ["maskAccountInfo"] = _maskAccountInfo,
                ["autoRefreshIntervalMinutes"] = _autoRefreshIntervalMinutes,
                ["autoCheckUpdateEnabled"] = _autoCheckUpdateEnabled,
                ["miniModeType"] = _miniModeType,
                ["miniWidgetBgColor"] = _miniWidgetBgColor,
                ["miniWidgetBgOpacity"] = _miniWidgetBgOpacity,
                ["miniWidgetIsTransparent"] = _miniWidgetIsTransparent,
                ["miniWidgetEdgeDock"] = _miniWidgetEdgeDock,
                ["miniWidgetHideCardBg"] = _miniWidgetHideCardBg,
                ["miniWidgetColor"] = _miniWidgetColor
            };
        }

        private string CollectCredentialsJson()
        {
            var list = new List<object>();
            var files = _jsonFilePaths.Where(IsValidAuthJson).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var file in files)
            {
                try
                {
                    if (!File.Exists(file)) continue;
                    string name = Path.GetFileName(file);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    string content = File.ReadAllText(file);
                    list.Add(new { name, content });
                }
                catch { }
            }
            return JsonSerializer.Serialize(
                new { version = 1, exportedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), credentials = list },
                new JsonSerializerOptions { WriteIndented = true });
        }

        private bool ApplySyncConfig(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var r = doc.RootElement;
                if (r.TryGetProperty("themeMode", out var p0))
                {
                    string? v = p0.GetString();
                    if (v == "system" || v == "dark" || v == "light") _themeMode = v;
                }
                if (r.TryGetProperty("maskAccountInfo", out var p1)) _maskAccountInfo = p1.GetBoolean();
                if (r.TryGetProperty("autoRefreshIntervalMinutes", out var p2) && p2.TryGetInt32(out int v2))
                    _autoRefreshIntervalMinutes = Math.Clamp(v2, 1, 1440);
                if (r.TryGetProperty("autoCheckUpdateEnabled", out var p3)) _autoCheckUpdateEnabled = p3.GetBoolean();
                if (r.TryGetProperty("miniModeType", out var p4))
                {
                    string? v = p4.GetString();
                    if (v == "off" || v == "taskbar" || v == "floating") _miniModeType = v;
                }
                if (r.TryGetProperty("miniWidgetBgColor", out var p5))
                {
                    string? v = p5.GetString();
                    _miniWidgetBgColor = NormalizeColorHex(v, _miniWidgetBgColor);
                }
                if (r.TryGetProperty("miniWidgetBgOpacity", out var p6) && p6.TryGetDouble(out double v6))
                    _miniWidgetBgOpacity = Math.Clamp(v6, 0.0, 1.0);
                if (r.TryGetProperty("miniWidgetIsTransparent", out var p7)) _miniWidgetIsTransparent = p7.GetBoolean();
                if (r.TryGetProperty("miniWidgetEdgeDock", out var p8)) _miniWidgetEdgeDock = p8.GetBoolean();
                if (r.TryGetProperty("miniWidgetHideCardBg", out var p9)) _miniWidgetHideCardBg = p9.GetBoolean();
                if (r.TryGetProperty("miniWidgetColor", out var p10))
                {
                    string? v = p10.GetString();
                    _miniWidgetColor = NormalizeColorHex(v, _miniWidgetColor);
                }

                SaveSettings();
                ApplyTheme();
                UpdateThemeSegmentedUI();
                UpdateMaskAccountSegmentUI();
                ResetAutoRefreshTimer();
                UpdateAutoCheckUpdateUI();
                ApplyMiniWidgetSettings();
                UpdateMiniWidgetColorSettingsUI();
                Log($"[同步] 已从云端恢复偏好配置");
                return true;
            }
            catch (Exception ex)
            {
                Log($"[同步] 恢复配置失败: {ex.Message}");
                return false;
            }
        }

        private bool ApplySyncCredentials(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("credentials", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return false;

                int applied = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    string? name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                    string? content = item.TryGetProperty("content", out var c) ? c.GetString() : null;
                    if (string.IsNullOrEmpty(name) || content == null) continue;
                    // 防目录穿越：仅接受纯文件名
                    string safeName = Path.GetFileName(name);
                    if (safeName != name || string.IsNullOrWhiteSpace(safeName)) continue;
                    string target = Path.Combine(_dataDir, safeName);
                    File.WriteAllText(target, content);
                    if (!_jsonFilePaths.Contains(target, StringComparer.OrdinalIgnoreCase) && IsValidAuthJson(target))
                    {
                        _jsonFilePaths.Add(target);
                    }
                    applied++;
                }
                SaveSettings();
                RenderSettingsFilesList();
                RefreshAccounts();
                Log($"[同步] 已从云端恢复 {applied} 个凭证文件");
                return applied > 0;
            }
            catch (Exception ex)
            {
                Log($"[同步] 恢复凭证失败: {ex.Message}");
                return false;
            }
        }

        // ---- 上传 / 下载 / 断开 ----

        private async void BtnGDriveUpload_Click(object sender, RoutedEventArgs e)
        {
            if (_gdriveBusy) return;
            if (_googleTokenState == null) { ShowCustomModal("上传到云端", "请先登录 Google Drive。"); return; }
            _gdriveBusy = true;
            SetGDriveBusyText("正在上传偏好配置…");
            UpdateGDriveUI();
            try
            {
                var (ok, at, authFailed) = await EnsureGoogleAccessTokenAsync();
                if (!ok) { ShowAuthOrError(authFailed, "无法获取 Google 访问令牌，请稍后重试。"); return; }

                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                string configJson = JsonSerializer.Serialize(CollectSyncConfig(), new JsonSerializerOptions { WriteIndented = true });
                string credJson = CollectCredentialsJson();

                // 1) 偏好配置
                string? configId = _googleTokenState?.FileId;
                if (string.IsNullOrEmpty(configId))
                {
                    var (fok, fid, fauth, ferr) = await FindGoogleFileAsync(hc, at!, GoogleConfigFileName);
                    if (fauth) { HandleAuthFailure(); return; }
                    if (!fok) { ShowCustomModal("上传到云端", $"查询云端配置失败：{ferr}"); return; }
                    configId = fid;
                }
                var (uok, newConfigId, uauth, uerr) = await UploadGoogleFileAsync(hc, at!, GoogleConfigFileName, configJson, configId);
                if (uauth) { HandleAuthFailure(); return; }
                if (!uok) { ShowCustomModal("上传到云端", $"偏好配置上传失败：{uerr}"); return; }

                // 2) 凭证打包
                SetGDriveBusyText("正在上传凭证…");
                string? credId = _googleTokenState?.CredentialsFileId;
                if (string.IsNullOrEmpty(credId))
                {
                    var (fok, fid, fauth, ferr) = await FindGoogleFileAsync(hc, at!, GoogleCredentialsFileName);
                    if (fauth) { HandleAuthFailure(); return; }
                    if (!fok) { ShowCustomModal("上传到云端", $"查询云端凭证失败：{ferr}"); return; }
                    credId = fid;
                }
                var (cok, newCredId, cauth, cerr) = await UploadGoogleFileAsync(hc, at!, GoogleCredentialsFileName, credJson, credId);
                if (cauth) { HandleAuthFailure(); return; }
                if (!cok) { ShowCustomModal("上传到云端", $"凭证上传失败：{cerr}"); return; }

                if (_googleTokenState != null)
                {
                    _googleTokenState.FileId = newConfigId;
                    _googleTokenState.CredentialsFileId = newCredId;
                    _googleTokenState.LastSyncUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    SaveGoogleToken();
                }
                ShowCustomModal("上传到云端", "偏好配置与凭证已上传至 Google Drive。");
                Log("[同步] 配置与凭证已上传到云端");
            }
            catch (Exception ex)
            {
                Log($"[同步] 上传异常: {ex.Message}");
                ShowCustomModal("上传到云端", $"上传异常：{ex.Message}");
            }
            finally
            {
                _gdriveBusy = false;
                UpdateGDriveUI();
            }
        }

        private async void BtnGDriveDownload_Click(object sender, RoutedEventArgs e)
        {
            if (_gdriveBusy) return;
            if (_googleTokenState == null) { ShowCustomModal("从云端恢复", "请先登录 Google Drive。"); return; }
            _gdriveBusy = true;
            SetGDriveBusyText("正在下载偏好配置…");
            UpdateGDriveUI();
            try
            {
                var (ok, at, authFailed) = await EnsureGoogleAccessTokenAsync();
                if (!ok) { ShowAuthOrError(authFailed, "无法获取 Google 访问令牌，请稍后重试。"); return; }

                using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };

                // 1) 偏好配置
                string? configId = _googleTokenState?.FileId;
                if (string.IsNullOrEmpty(configId))
                {
                    var (fok, fid, fauth, ferr) = await FindGoogleFileAsync(hc, at!, GoogleConfigFileName);
                    if (fauth) { HandleAuthFailure(); return; }
                    if (!fok) { ShowCustomModal("从云端恢复", $"查询云端配置失败：{ferr}"); return; }
                    configId = fid;
                }
                if (!string.IsNullOrEmpty(configId))
                {
                    var (dok, configJson, dauth, derr) = await DownloadGoogleFileAsync(hc, at!, configId);
                    if (dauth) { HandleAuthFailure(); return; }
                    if (!dok) { ShowCustomModal("从云端恢复", $"配置下载失败：{derr}"); return; }
                    if (configJson != null) ApplySyncConfig(configJson);
                }

                // 2) 凭证打包
                SetGDriveBusyText("正在下载凭证…");
                string? credId = _googleTokenState?.CredentialsFileId;
                if (string.IsNullOrEmpty(credId))
                {
                    var (fok, fid, fauth, ferr) = await FindGoogleFileAsync(hc, at!, GoogleCredentialsFileName);
                    if (fauth) { HandleAuthFailure(); return; }
                    if (!fok) { ShowCustomModal("从云端恢复", $"查询云端凭证失败：{ferr}"); return; }
                    credId = fid;
                }
                if (!string.IsNullOrEmpty(credId))
                {
                    var (dok, credJson, dauth, derr) = await DownloadGoogleFileAsync(hc, at!, credId);
                    if (dauth) { HandleAuthFailure(); return; }
                    if (!dok) { ShowCustomModal("从云端恢复", $"凭证下载失败：{derr}"); return; }
                    if (credJson != null) ApplySyncCredentials(credJson);
                }

                if (_googleTokenState != null)
                {
                    _googleTokenState.FileId = configId;
                    _googleTokenState.CredentialsFileId = credId;
                    _googleTokenState.LastSyncUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    SaveGoogleToken();
                }
                ShowCustomModal("从云端恢复", "已从云端恢复配置与凭证并应用到本地。");
                Log("[同步] 已从云端恢复配置与凭证");
            }
            catch (Exception ex)
            {
                Log($"[同步] 恢复异常: {ex.Message}");
                ShowCustomModal("从云端恢复", $"恢复异常：{ex.Message}");
            }
            finally
            {
                _gdriveBusy = false;
                UpdateGDriveUI();
            }
        }

        private async void BtnGDriveLogout_Click(object sender, RoutedEventArgs e)
        {
            if (_googleTokenState == null) return;
            try
            {
                string? rt = UnprotectString(_googleTokenState.RefreshTokenProtected);
                if (!string.IsNullOrEmpty(rt))
                {
                    using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                    var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = rt });
                    await hc.PostAsync("https://oauth2.googleapis.com/revoke", form);
                }
            }
            catch { }
            ClearGoogleToken();
            UpdateGDriveUI();
            Log("[同步] 已断开 Google Drive 连接");
        }

        private void HandleAuthFailure()
        {
            ClearGoogleToken();
            UpdateGDriveUI();
            ShowCustomModal("Google Drive", "Google 授权已失效，请重新登录。");
        }

        private void ShowAuthOrError(bool authFailed, string msg)
        {
            if (authFailed) HandleAuthFailure();
            else ShowCustomModal("Google Drive", msg);
        }

        private void SetGDriveBusyText(string text)
        {
            _gdriveBusyText = text;
            try { if (TxtGDriveBusyText != null) TxtGDriveBusyText.Text = text; } catch { }
        }

        private void UpdateGDriveUI()
        {
            try
            {
                bool connected = _googleTokenState != null;
                TxtGDriveStatusBadge.Text = connected ? "已连接" : "未连接";
                TxtGDriveStatusBadge.Foreground = connected
                    ? (Brush)FindResource("ThemePrimary")
                    : (Brush)FindResource("ThemeTextTertiary");

                if (SettingsSyncLoginRow != null)
                    SettingsSyncLoginRow.Visibility = connected ? Visibility.Collapsed : Visibility.Visible;
                if (SettingsSyncConnectedRow != null)
                    SettingsSyncConnectedRow.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;

                if (connected && _googleTokenState != null)
                {
                    TxtGDriveUserEmail.Text = GetDisplayEmail(_googleTokenState.UserEmail);
                    TxtGDriveLastSync.Text = _googleTokenState.LastSyncUnixMs > 0
                        ? $"上次同步: {DateTimeOffset.FromUnixTimeMilliseconds(_googleTokenState.LastSyncUnixMs).ToLocalTime():yyyy-MM-dd HH:mm}"
                        : "尚未同步";
                }

                UpdateGDriveAvatar();

                if (GDriveBusyPanel != null)
                    GDriveBusyPanel.Visibility = _gdriveBusy ? Visibility.Visible : Visibility.Collapsed;
                if (TxtGDriveBusyText != null && !string.IsNullOrEmpty(_gdriveBusyText))
                    TxtGDriveBusyText.Text = _gdriveBusyText;

                bool busy = _gdriveBusy;
                if (BtnGoogleLogin != null) BtnGoogleLogin.IsEnabled = !busy;
                if (BtnGDriveUpload != null) BtnGDriveUpload.IsEnabled = connected && !busy;
                if (BtnGDriveDownload != null) BtnGDriveDownload.IsEnabled = connected && !busy;
                if (BtnGDriveLogout != null) BtnGDriveLogout.IsEnabled = connected && !busy;
            }
            catch { }
        }

        /// <summary>圆形头像：有 Google 头像 URL 时异步加载（缓存），失败/无 URL 时显示邮箱首字母兜底</summary>
        private void UpdateGDriveAvatar()
        {
            try
            {
                bool connected = _googleTokenState != null;
                string letter = "G";
                if (connected && !string.IsNullOrEmpty(_googleTokenState?.UserEmail))
                {
                    char first = _googleTokenState!.UserEmail.Trim().FirstOrDefault(char.IsLetter);
                    if (first != '\0') letter = char.ToUpperInvariant(first).ToString();
                }
                if (GDriveAvatarFallback != null) GDriveAvatarFallback.Text = letter;

                string? pic = _googleTokenState?.PictureUrl;
                if (!connected || string.IsNullOrEmpty(pic))
                {
                    if (GDriveAvatarImage != null)
                    {
                        GDriveAvatarImage.Source = null;
                        GDriveAvatarImage.Visibility = Visibility.Collapsed;
                    }
                    if (GDriveAvatarFallback != null)
                        GDriveAvatarFallback.Visibility = Visibility.Visible;
                    return;
                }

                if (_gdriveAvatarCache.TryGetValue(pic, out var cached))
                {
                    GDriveAvatarImage.Source = cached;
                    GDriveAvatarImage.Visibility = Visibility.Visible;
                    if (GDriveAvatarFallback != null)
                        GDriveAvatarFallback.Visibility = Visibility.Collapsed;
                    return;
                }

                string url = pic;
                Task.Run(async () =>
                {
                    try
                    {
                        using var hc = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                        byte[] bytes = await hc.GetByteArrayAsync(url);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(bytes);
                        bmp.EndInit();
                        bmp.Freeze();
                        lock (_gdriveAvatarCache) { _gdriveAvatarCache[url] = bmp; }
                        Dispatcher.Invoke(() =>
                        {
                            try
                            {
                                if (GDriveAvatarImage != null && _googleTokenState?.PictureUrl == url)
                                {
                                    GDriveAvatarImage.Source = bmp;
                                    GDriveAvatarImage.Visibility = Visibility.Visible;
                                }
                                if (GDriveAvatarFallback != null)
                                    GDriveAvatarFallback.Visibility = Visibility.Collapsed;
                            }
                            catch { }
                        });
                    }
                    catch { } // 头像下载失败：保留首字母兜底
                });
            }
            catch { }
        }

        private void ApplyMiniWidgetBgColor(string hex)
        {
            try
            {
                _miniWidgetBgColor = NormalizeColorHex(hex, _miniWidgetBgColor);
                UpdateMiniWidgetColorSettingsUI();
                SaveSettings();
                ApplyMiniWidgetSettings();
                Log($"[悬浮贴贴] 背景颜色更新为: {_miniWidgetBgColor}");
            }
            catch { }
        }

        private void BtnOpenBgColorPicker_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                using var dlg = new System.Windows.Forms.ColorDialog();
                dlg.FullOpen = true;
                if (!string.IsNullOrEmpty(_miniWidgetBgColor))
                {
                    try
                    {
                        var wpfColor = (Color)ColorConverter.ConvertFromString(_miniWidgetBgColor);
                        dlg.Color = System.Drawing.Color.FromArgb(wpfColor.A, wpfColor.R, wpfColor.G, wpfColor.B);
                    }
                    catch { }
                }

                if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    var c = dlg.Color;
                    string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
                    ApplyMiniWidgetBgColor(hex);
                }
            }
            catch (Exception ex)
            {
                Log($"[取色器错误] {ex.Message}");
            }
        }

        private void SliderBgOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_isUpdatingUI || SliderBgOpacity == null || TxtBgOpacityVal == null) return;

            double opacity = Math.Clamp(SliderBgOpacity.Value / 100.0, 0.0, 1.0);
            _miniWidgetBgOpacity = opacity;
            TxtBgOpacityVal.Text = $"{Math.Round(opacity * 100):0}%";

            // 拖动时只更新现有卡片背景，不触发窗口重排、Win32 定位或磁盘写入。
            _miniWidget?.PreviewBackground(_miniWidgetBgColor, opacity);

            _miniOpacitySaveTimer ??= new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _miniOpacitySaveTimer.Stop();
            _miniOpacitySaveTimer.Tick -= MiniOpacitySaveTimer_Tick;
            _miniOpacitySaveTimer.Tick += MiniOpacitySaveTimer_Tick;
            _miniOpacitySaveTimer.Start();
        }

        private void MiniOpacitySaveTimer_Tick(object? sender, EventArgs e)
        {
            _miniOpacitySaveTimer?.Stop();
            SaveSettings();
        }

        public void ToggleMiniWidgetTransparent()
        {
            _miniWidgetIsTransparent = !_miniWidgetIsTransparent;
            SaveSettings();
            // 轻量切换：只更新 WS_EX_TRANSPARENT/右键钩子/光标状态，不触发 ApplyModeAndStyles
            // 的整套重定位+背景重建，避免分层窗口在切换瞬间闪烁。
            _miniWidget?.SetTransparentMode(_miniWidgetIsTransparent);
            Log($"[悬浮贴贴] {(_miniWidgetIsTransparent ? "已启用左键穿透并固定位置" : "已停用鼠标穿透，可拖动位置")}");
        }

        public void SaveMiniWidgetLocation(double left, double top)
        {
            _miniWidgetLeft = left;
            _miniWidgetTop = top;
            SaveSettings();
        }

        public void SaveMiniWidgetSize(double width, double height)
        {
            _miniWidgetWidth = width;
            _miniWidgetHeight = height;
            SaveSettings();
        }

        private void BtnMaskAccountOff_Click(object sender, RoutedEventArgs e)
        {
            SetMaskAccountInfo(false);
        }

        private void BtnMaskAccountOn_Click(object sender, RoutedEventArgs e)
        {
            SetMaskAccountInfo(true);
        }

        private void SetMaskAccountInfo(bool enabled)
        {
            try
            {
                _maskAccountInfo = enabled;
                SaveSettings();
                UpdateMaskAccountSegmentUI();
                RefreshAccountsUIOnly();
                RenderSettingsFilesList(); // 设置页凭证列表中的文件名/路径也需即时刷新脱敏
                UpdateGDriveUI(); // Google 同步卡片邮箱也需即时刷新脱敏
                UpdateMiniWidgetData();
                Log($"[设置] 隐私脱敏: {(enabled ? "已开启" : "已关闭")}");
            }
            catch { }
        }

        private void UpdateMaskAccountSegmentUI()
        {
            try
            {
                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");
                BtnMaskAccountOff.Style = !_maskAccountInfo ? active : inactive;
                BtnMaskAccountOn.Style = _maskAccountInfo ? active : inactive;
            }
            catch { }
        }

        // =================== 本地 API 代理服务器 (Local Proxy Server) ===================

        private void InitLocalProxyServer()
        {
            _proxyServer.LogCallback = Log;
            _proxyServer.GetGeminiTokenAsync = GetValidGeminiTokenForProxyAsync;
            _proxyServer.OnAccountRateLimited = (email, duration) => MarkAccountCooling(email, duration);
            _proxyServer.Port = _localProxyPort;
            _proxyServer.ApiKey = _localProxyApiKey;

            TxtLocalProxyPort.Text = _localProxyPort.ToString();
            TxtLocalProxyApiKey.Text = _localProxyApiKey;

            UpdateLocalProxySegmentUI();

            if (_isLocalProxyEnabled)
            {
                StartLocalProxyServer();
            }
        }

        private bool StartLocalProxyServer()
        {
            _proxyServer.Port = _localProxyPort;
            _proxyServer.ApiKey = _localProxyApiKey;
            bool ok = _proxyServer.Start();
            UpdateLocalProxySegmentUI();
            return ok;
        }

        private void StopLocalProxyServer()
        {
            _proxyServer.Stop();
            UpdateLocalProxySegmentUI();
        }

        private void UpdateLocalProxySegmentUI()
        {
            try
            {
                bool running = _proxyServer.IsRunning;
                var active = (Style)FindResource("BtnSegmentActive");
                var inactive = (Style)FindResource("BtnSegmentInactive");
                BtnLocalProxyOn.Style = running ? active : inactive;
                BtnLocalProxyOff.Style = running ? inactive : active;
                TxtLocalProxyStatusBadge.Text = running ? "运行中" : "已停止";
                TxtLocalProxyStatusBadge.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#16A34A" : "#94A3B8"));
                DotLocalProxyStatus.Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(running ? "#4ADE80" : "#94A3B8"));
                TxtLocalProxyDotStatus.Text = running
                    ? (_compactMode ? "代理运行中" : "HTTP 代理运行中")
                    : (_compactMode ? "代理未开启" : "HTTP 代理未开启");
                // 停止时禁用模型测试相关控件
                CmbLocalProxyModel.IsEnabled = running;
                BtnFetchLocalProxyModels.IsEnabled = running;
                BtnTestLocalProxyModel.IsEnabled = running;
            }
            catch { }
        }

        public void MarkAccountCooling(string email, TimeSpan duration)
        {
            if (string.IsNullOrWhiteSpace(email)) return;
            var until = DateTimeOffset.UtcNow.Add(duration);
            string durationText = duration >= TimeSpan.FromMinutes(1)
                ? $"{duration.TotalMinutes:0.#} 分钟"
                : $"{duration.TotalSeconds:0.#} 秒";
            _accountCooldowns[email] = until;
            Log($"[本地代理 冷却] 账号 {email} 已标记进入冷却期 ({durationText})，预计 {until.ToLocalTime():HH:mm:ss} 解冻");
        }

        public bool IsAccountInCooldown(string email, out DateTimeOffset cooldownUntil)
        {
            if (!string.IsNullOrWhiteSpace(email) && _accountCooldowns.TryGetValue(email, out cooldownUntil))
            {
                if (DateTimeOffset.UtcNow < cooldownUntil)
                {
                    return true;
                }
                _accountCooldowns.TryRemove(email, out _);
            }
            cooldownUntil = DateTimeOffset.MinValue;
            return false;
        }

        private int _localProxyAccountIndex = 0;

        private async Task<(string accessToken, string email, string projectId)?> GetValidGeminiTokenForProxyAsync(IReadOnlySet<string>? excludeEmails)
        {
            // 专一化：LoadAllAccounts 仅载入有效的 Antigravity/Gemini 凭证（禁用凭证已被跳过）
            var allAccounts = LoadAllAccounts();
            if (allAccounts.Count == 0) return null;

            // 过滤：排除当前已尝试过的账号 (excludeEmails) 和处于 429 冷却期中的账号
            var availableAccounts = allAccounts.Where(acc =>
            {
                if (excludeEmails != null && excludeEmails.Contains(acc.Email)) return false;
                if (IsAccountInCooldown(acc.Email, out _)) return false;
                return true;
            }).ToList();

            if (availableAccounts.Count == 0)
            {
                // 无直接可用账号时，检查是否全在冷却中，便于输出详细诊断日志
                var coolingAccounts = allAccounts.Where(acc => IsAccountInCooldown(acc.Email, out _)).ToList();
                if (coolingAccounts.Count > 0 && (excludeEmails == null || excludeEmails.Count == 0))
                {
                    Log($"[本地代理] 当前所有有效账号 ({coolingAccounts.Count} 个) 均处于 429 冷却期中，暂无可调度的可用账号");
                }
                return null;
            }

            // 轮询（Round-Robin）选号，在可用账号池中均摊负载
            int startIdx = Math.Abs(System.Threading.Interlocked.Increment(ref _localProxyAccountIndex)) % availableAccounts.Count;
            
            // 遍历候选账号，直到找到成功获取/刷新 Token 的账号
            for (int i = 0; i < availableAccounts.Count; i++)
            {
                var acc = availableAccounts[(startIdx + i) % availableAccounts.Count];
                try
                {
                    if (!File.Exists(acc.FilePath)) continue;
                    string authJsonContent = File.ReadAllText(acc.FilePath);
                    string accessToken = "";
                    string refreshToken = "";
                    string projectId = "";

                    using (var doc = JsonDocument.Parse(authJsonContent))
                    {
                        var root = doc.RootElement;
                        if (root.TryGetProperty("access_token", out var pTok)) accessToken = pTok.GetString() ?? "";
                        if (root.TryGetProperty("refresh_token", out var pRef)) refreshToken = pRef.GetString() ?? "";
                        if (root.TryGetProperty("project_id", out var pProj)) projectId = pProj.GetString() ?? "";
                    }

                    if (!string.IsNullOrEmpty(refreshToken))
                    {
                        bool isExpired = IsTokenExpired(authJsonContent, acc.Expired);
                        if (isExpired || string.IsNullOrEmpty(accessToken))
                        {
                            Log($"[本地代理] 账号 {acc.Email} Token 已过期，正在自动刷新...");
                            string? newToken = await RefreshAntigravityTokenAsync(refreshToken);
                            if (!string.IsNullOrEmpty(newToken))
                            {
                                accessToken = newToken;
                                UpdateJsonToken(acc.FilePath, newToken);
                                Log($"[本地代理] 账号 {acc.Email} Token 自动刷新成功！");
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(accessToken)) continue;

                    return (accessToken, acc.Email, projectId);
                }
                catch (Exception ex)
                {
                    Log($"[本地代理] 获取凭证异常 ({acc.Email}): {ex.Message}");
                }
            }

            return null;
        }

        private void BtnLocalProxyOff_Click(object sender, RoutedEventArgs e)
        {
            _isLocalProxyEnabled = false;
            StopLocalProxyServer();
            SaveSettings();
            Log("[设置] 本地 API 代理: 已关闭");
        }

        private void BtnLocalProxyOn_Click(object sender, RoutedEventArgs e)
        {
            _isLocalProxyEnabled = true;
            if (StartLocalProxyServer())
            {
                SaveSettings();
                Log("[设置] 本地 API 代理: 已开启");
            }
            else
            {
                _isLocalProxyEnabled = false;
                UpdateLocalProxySegmentUI();
                ShowCustomModal("启动失败", $"无法启动本地代理端口 {_localProxyPort}，请检查端口是否被占用。", "");
            }
        }

        private void TxtLocalProxyPort_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyLocalProxyPortChange();
        }

        private void TxtLocalProxyPort_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyLocalProxyPortChange();
                System.Windows.Input.Keyboard.ClearFocus();
            }
        }

        private void TxtLocalProxyPort_TextChanged(object sender, TextChangedEventArgs e)
        {
            // 端口输入时实时同步内存值，供复制/测试等使用完整 URL（http://127.0.0.1:{port}/v1）
            string p = TxtLocalProxyPort.Text.Trim();
            if (int.TryParse(p, out int port) && port >= 1 && port <= 65535)
            {
                _localProxyPort = port;
                _proxyServer.Port = port;
            }
        }

        private void ApplyLocalProxyPortChange()
        {
            if (int.TryParse(TxtLocalProxyPort.Text.Trim(), out int p) && p >= 1024 && p <= 65535)
            {
                if (p != _localProxyPort)
                {
                    _localProxyPort = p;
                    SaveSettings();
                    Log($"[设置] 本地代理端口已变更至: {_localProxyPort}");
                    if (_proxyServer.IsRunning)
                    {
                        StopLocalProxyServer();
                        StartLocalProxyServer();
                    }
                    else
                    {
                        UpdateLocalProxySegmentUI();
                    }
                }
            }
            else
            {
                TxtLocalProxyPort.Text = _localProxyPort.ToString();
            }
        }

        private void TxtLocalProxyApiKey_LostFocus(object sender, RoutedEventArgs e)
        {
            ApplyLocalProxyApiKeyChange();
        }

        private void TxtLocalProxyApiKey_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                ApplyLocalProxyApiKeyChange();
                System.Windows.Input.Keyboard.ClearFocus();
            }
        }

        private void ApplyLocalProxyApiKeyChange()
        {
            string key = TxtLocalProxyApiKey.Text.Trim();
            if (key != _localProxyApiKey)
            {
                _localProxyApiKey = key;
                _proxyServer.ApiKey = _localProxyApiKey;
                SaveSettings();
                Log($"[设置] 本地代理 API Key 已更新");
            }
        }

        private void BtnGenLocalProxyKey_Click(object sender, RoutedEventArgs e)
        {
            // sk-haodo- 前缀 + 32 位 hex（41 字符），与 OpenAI 风格 key 一致
            string newKey = "sk-haodo-" + Guid.NewGuid().ToString("N");
            TxtLocalProxyApiKey.Text = newKey;
            ApplyLocalProxyApiKeyChange();
        }

        private void BtnCopyLocalProxyKey_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtLocalProxyApiKey.Text);
                Log("[设置] API Key 已复制到剪贴板");
            }
            catch { }
        }

        private void BtnCopyLocalProxyUrl_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(GetLocalProxyUrl());
                Log("[设置] 接口地址已复制到剪贴板");
            }
            catch { }
        }

        private string GetLocalProxyUrl()
        {
            return $"http://127.0.0.1:{_localProxyPort}/v1";
        }

        // 主界面右下角 HTTP 代理状态灯：点击跳转设置页并展开本地代理卡片（若已收起）
        private void DotLocalProxyStatus_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            SwitchView(false);
            if (SettingsLocalProxyContent.Visibility != Visibility.Visible)
            {
                SettingsLocalProxyContent.Visibility = Visibility.Visible;
                TxtArrowLocalProxy.Text = "▼";
            }
        }

        // 更新模型测试状态行：颜色 0xRRGGBB 十六进制字符串
        private void SetLocalProxyTestStatus(string text, string colorHex)
        {
            TxtLocalProxyTestStatus.Text = text;
            TxtLocalProxyTestStatus.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex));
        }

        // 获取模型：通过本地代理 /v1/models 拉取可用模型列表填充下拉框
        private async void BtnFetchLocalProxyModels_Click(object sender, RoutedEventArgs e)
        {
            if (!_proxyServer.IsRunning)
            {
                SetLocalProxyTestStatus("代理未运行", "#EF4444");
                ShowCustomModal("获取模型", "本地代理服务未运行，请先开启服务。", "");
                return;
            }
            try
            {
                BtnFetchLocalProxyModels.IsEnabled = false;
                SetLocalProxyTestStatus("正在获取模型列表...", "#94A3B8");

                // 同编号（前缀）的模型放一起：按前缀字母序分组，组内数字版本升序、latest 最后
                string GroupKey(string id)
                {
                    int idx = id.LastIndexOf('-');
                    if (idx > 0)
                    {
                        string suffix = id.Substring(idx + 1);
                        if (suffix.Length > 0 && (suffix == "latest" || suffix.All(char.IsDigit)))
                            return id.Substring(0, idx);
                    }
                    return id;
                }
                (int Group, int Seq) VersionKey(string id)
                {
                    int idx = id.LastIndexOf('-');
                    if (idx > 0)
                    {
                        string suffix = id.Substring(idx + 1);
                        if (suffix == "latest") return (2, 0);
                        if (suffix.Length > 0 && suffix.All(char.IsDigit)) return (1, int.Parse(suffix));
                    }
                    return (0, 0);
                }

                var sw = Stopwatch.StartNew();
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var req = new HttpRequestMessage(HttpMethod.Get, GetLocalProxyUrl() + "/models");
                if (!string.IsNullOrEmpty(_localProxyApiKey))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _localProxyApiKey);
                var resp = await client.SendAsync(req);
                sw.Stop();
                string raw = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                {
                    SetLocalProxyTestStatus($"HTTP {(int)resp.StatusCode} | {sw.ElapsedMilliseconds}ms", "#EF4444");
                    TxtLocalProxyTestResult.Text = TruncateText(raw, 500);
                    Log($"[本地代理] 获取模型失败: HTTP {(int)resp.StatusCode} {TruncateText(raw, 300)}");
                    return;
                }
                using var doc = JsonDocument.Parse(raw);
                var ids = doc.RootElement.TryGetProperty("data", out var data)
                    ? data.EnumerateArray()
                        .Where(m => m.TryGetProperty("id", out var pId) && !string.IsNullOrEmpty(pId.GetString()))
                        .Select(m => m.GetProperty("id").GetString()!)
                        .Distinct()
                        .OrderBy(GroupKey).ThenBy(VersionKey)
                        .ToList()
                    : new List<string>();
                CmbLocalProxyModel.Items.Clear();
                foreach (var id in ids) CmbLocalProxyModel.Items.Add(id);
                if (ids.Count > 0)
                {
                    CmbLocalProxyModel.SelectedIndex = 0;
                    SetLocalProxyTestStatus($"200 OK | {sw.ElapsedMilliseconds}ms | 共 {ids.Count} 个模型", "#16A34A");
                    TxtLocalProxyTestResult.Text = $"已获取 {ids.Count} 个模型，选择或输入模型后点击「测试」。";
                }
                else
                {
                    SetLocalProxyTestStatus($"200 OK | {sw.ElapsedMilliseconds}ms | 未返回模型", "#F59E0B");
                    TxtLocalProxyTestResult.Text = "代理响应正常，但未返回任何模型。可手动输入模型名后直接测试。";
                }
                Log($"[本地代理] 获取到 {ids.Count} 个可用模型");
            }
            catch (Exception ex)
            {
                SetLocalProxyTestStatus("请求异常", "#EF4444");
                TxtLocalProxyTestResult.Text = ex.Message;
                Log($"[本地代理] 获取模型异常: {ex.Message}");
            }
            finally
            {
                BtnFetchLocalProxyModels.IsEnabled = true;
            }
        }

        // 测试模型：向选中模型发送一段自我介绍请求以验证连通性
        private async void BtnTestLocalProxyModel_Click(object sender, RoutedEventArgs e)
        {
            string model = (CmbLocalProxyModel.Text ?? "").Trim();
            if (string.IsNullOrEmpty(model))
            {
                SetLocalProxyTestStatus("未选择模型", "#EF4444");
                TxtLocalProxyTestResult.Text = "点击「获取模型」选择模型，或直接在输入框键入模型名。";
                return;
            }
            if (!_proxyServer.IsRunning)
            {
                SetLocalProxyTestStatus("代理未运行", "#EF4444");
                TxtLocalProxyTestResult.Text = "请先开启本地 API 代理服务。";
                return;
            }
            try
            {
                BtnTestLocalProxyModel.IsEnabled = false;
                SetLocalProxyTestStatus($"正在请求 {model} ...", "#94A3B8");
                TxtLocalProxyTestResult.Text = "";
                var sw = Stopwatch.StartNew();
                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
                var req = new HttpRequestMessage(HttpMethod.Post, GetLocalProxyUrl() + "/chat/completions");
                if (!string.IsNullOrEmpty(_localProxyApiKey))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _localProxyApiKey);
                var body = new
                {
                    model,
                    messages = new[]
                    {
                        new { role = "user", content = "Please introduce yourself in one sentence. / 请用一句话介绍你自己。" }
                    },
                    stream = false
                };
                req.Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json");
                var resp = await client.SendAsync(req);
                string raw = await resp.Content.ReadAsStringAsync();
                sw.Stop();
                if (!resp.IsSuccessStatusCode)
                {
                    SetLocalProxyTestStatus($"HTTP {(int)resp.StatusCode} | {sw.ElapsedMilliseconds}ms", "#EF4444");
                    TxtLocalProxyTestResult.Text = string.IsNullOrWhiteSpace(raw) ? "代理返回了空响应体。" : TruncateText(raw, 500);
                    Log($"[本地代理] 模型 {model} 测试失败: HTTP {(int)resp.StatusCode}");
                    return;
                }
                using var doc = JsonDocument.Parse(raw);
                string reply = "";
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                    choices[0].TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var content))
                {
                    reply = content.GetString() ?? "";
                }
                SetLocalProxyTestStatus($"200 OK | {sw.ElapsedMilliseconds}ms", "#16A34A");
                TxtLocalProxyTestResult.Text = string.IsNullOrWhiteSpace(reply) ? "(模型无回复内容)" : reply;
                Log($"[本地代理] 模型 {model} 测试成功，返回 {reply.Length} 字符");
            }
            catch (Exception ex)
            {
                SetLocalProxyTestStatus("请求异常", "#EF4444");
                TxtLocalProxyTestResult.Text = ex.Message;
                Log($"[本地代理] 测试模型异常: {ex.Message}");
            }
            finally
            {
                BtnTestLocalProxyModel.IsEnabled = true;
            }
        }

        private static string TruncateText(string text, int maxLen)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= maxLen ? text : text.Substring(0, maxLen) + "...";
        }

        private static bool IsJsonFileDisabled(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                string content = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(content);
                if (doc.RootElement.TryGetProperty("disabled", out var pDis))
                {
                    return pDis.GetBoolean();
                }
            }
            catch { }
            return false;
        }

        private static void SetJsonFileDisabled(string filePath, bool disabled)
        {
            try
            {
                if (!File.Exists(filePath)) return;
                string content = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var dict = new Dictionary<string, object>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "disabled")
                        continue;
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString()!;
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out long lv)) dict[prop.Name] = lv;
                        else dict[prop.Name] = prop.Value.GetDouble();
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        dict[prop.Name] = prop.Value.GetBoolean();
                    else
                        dict[prop.Name] = prop.Value.GetRawText();
                }
                dict["disabled"] = disabled;

                string newJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(filePath, newJson);
            }
            catch { }
        }

        private void RenderSettingsFilesList()
        {
            UpdateDataDirStatUI();
            SettingsFilesContainer.Children.Clear();
            TxtSettingsFileCount.Text = $"凭证文件 ({_jsonFilePaths.Count})";

            if (_jsonFilePaths.Count == 0)
            {
                SettingsFilesContainer.Children.Add(new TextBlock
                {
                    Text = "暂无凭证文件", 
                    FontSize = 12,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#94A3B8")),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 16, 0, 0)
                });
                return;
            }

            for (int index = 0; index < _jsonFilePaths.Count; index++)
            {
                string path = _jsonFilePaths[index];
                bool isDisabled = IsJsonFileDisabled(path);

                Border itemBorder = new Border
                {
                    Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcInputBg)),
                    BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder)),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(10, 8, 10, 8),
                    Margin = new Thickness(0, 0, 0, 8),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    ToolTip = $"路径: {path}\n拖拽最左侧 ≡ 拖手可调整上下显示顺序"
                };

                // 支持拖拽 Drop
                itemBorder.AllowDrop = true;
                string dropTargetPath = path;

                itemBorder.DragOver += (s, ev) =>
                {
                    if (ev.Data.GetDataPresent("JsonFilePath"))
                    {
                        ev.Effects = DragDropEffects.Move;
                        ev.Handled = true;
                    }
                };

                itemBorder.Drop += (s, ev) =>
                {
                    if (ev.Data.GetDataPresent("JsonFilePath") && ev.Data.GetData("JsonFilePath") is string sourcePath)
                    {
                        if (!string.Equals(sourcePath, dropTargetPath, StringComparison.OrdinalIgnoreCase))
                        {
                            int oldIdx = _jsonFilePaths.FindIndex(p => string.Equals(p, sourcePath, StringComparison.OrdinalIgnoreCase));
                            int newIdx = _jsonFilePaths.FindIndex(p => string.Equals(p, dropTargetPath, StringComparison.OrdinalIgnoreCase));

                            if (oldIdx >= 0 && newIdx >= 0)
                            {
                                _jsonFilePaths.RemoveAt(oldIdx);
                                _jsonFilePaths.Insert(newIdx, sourcePath);
                                SaveSettings();
                                RenderSettingsFilesList();
                                ReorderAccountCards();
                                Log($"[设置] 已拖拽调整凭证顺序: {Path.GetFileName(sourcePath)} -> 位置 {newIdx + 1}");
                            }
                        }
                    }
                };

                Grid outerGrid = new Grid();
                outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                outerGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                // Row 0: 拖手 + 文件路径 + 删除按钮
                Grid row0 = new Grid();
                row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                row0.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row0.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                // 最左侧拖手 Grip (≡)
                Border dragGrip = new Border
                {
                    Width = 20, Height = 28,
                    Background = Brushes.Transparent,
                    Cursor = System.Windows.Input.Cursors.SizeAll,
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = "按住左键拖拽调整上下顺序",
                    Tag = path
                };
                dragGrip.Child = new TextBlock
                {
                    Text = "≡",
                    FontSize = 16,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextSecondary)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                string dragSourcePath = path;
                dragGrip.PreviewMouseLeftButtonDown += (s, ev) =>
                {
                    _dragStartPoint = ev.GetPosition(null);
                    _draggedPath = dragSourcePath;
                };

                dragGrip.MouseMove += (s, ev) =>
                {
                    if (ev.LeftButton != MouseButtonState.Pressed || string.IsNullOrEmpty(_draggedPath)) return;

                    Point currentPoint = ev.GetPosition(null);
                    Vector diff = _dragStartPoint - currentPoint;

                    if (Math.Abs(diff.X) > 4 || Math.Abs(diff.Y) > 4)
                    {
                        var dataObj = new DataObject("JsonFilePath", _draggedPath);
                        DragDrop.DoDragDrop(dragGrip, dataObj, DragDropEffects.Move);
                        _draggedPath = null;
                    }
                };

                Grid.SetColumn(dragGrip, 0);
                row0.Children.Add(dragGrip);

                // 中间路径文件名
                StackPanel textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
                textStack.Children.Add(new TextBlock
                {
                    Text = GetDisplayFileName(path),
                    FontSize = 12,
                    FontWeight = FontWeights.Bold,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextPrimary))
                });
                textStack.Children.Add(new TextBlock
                {
                    Text = GetDisplayFilePath(path),
                    FontSize = 10,
                    Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextSecondary)),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(textStack, 1);
                row0.Children.Add(textStack);

                // 右侧删除按钮
                Button delBtn = new Button
                {
                    Content = "删除",
                    Style = (Style)FindResource("BtnHeader"),
                    Width = 42, Height = 24, MinHeight = 24, MaxHeight = 24,
                    Padding = new Thickness(0),
                    FontSize = 10,
                    ToolTip = "彻底移除并删除该凭证 JSON 文件"
                };
                string currentPath = path;
                delBtn.Click += (s, ev) =>
                {
                    try
                    {
                        _jsonFilePaths.RemoveAll(p => string.Equals(p, currentPath, StringComparison.OrdinalIgnoreCase));
                        if (File.Exists(currentPath))
                        {
                            try { File.Delete(currentPath); } catch { }
                        }

                        SaveSettings();
                        RenderSettingsFilesList();
                        RefreshAccounts();
                        Log($"[设置] 已彻底删除凭证文件: {Path.GetFileName(currentPath)}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[错误] 删除文件失败: {ex.Message}");
                    }
                };
                Grid.SetColumn(delBtn, 2);
                row0.Children.Add(delBtn);
                outerGrid.Children.Add(row0);

                // Row 1: 启用跟停用 分段式开关
                Border switchContainer = new Border
                {
                    Background = (Brush)FindResource("ThemeSegmentBg"),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(2),
                    Margin = new Thickness(0, 8, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                Grid switchGrid = new Grid();
                switchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                switchGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var activeStyle = (Style)FindResource("BtnSegmentActive");
                var inactiveStyle = (Style)FindResource("BtnSegmentInactive");

                Button btnEnable = new Button
                {
                    Content = "启用",
                    Style = !isDisabled ? activeStyle : inactiveStyle,
                    Height = 24, MinHeight = 24, MaxHeight = 24,
                    FontSize = 10, Margin = new Thickness(0, 0, 1, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnEnable.Click += (s, ev) =>
                {
                    SetJsonFileDisabled(currentPath, false);
                    RenderSettingsFilesList();
                    RefreshAccounts();
                    Log($"[设置] 已为 {Path.GetFileName(currentPath)} 设置为 [启用]");
                };
                Grid.SetColumn(btnEnable, 0);
                switchGrid.Children.Add(btnEnable);

                Button btnDisable = new Button
                {
                    Content = "停用",
                    Style = isDisabled ? activeStyle : inactiveStyle,
                    Height = 24, MinHeight = 24, MaxHeight = 24,
                    FontSize = 10, Margin = new Thickness(1, 0, 0, 0),
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                btnDisable.Click += (s, ev) =>
                {
                    SetJsonFileDisabled(currentPath, true);
                    RenderSettingsFilesList();
                    RefreshAccounts();
                    Log($"[设置] 已为 {Path.GetFileName(currentPath)} 设置为 [停用]");
                };
                Grid.SetColumn(btnDisable, 1);
                switchGrid.Children.Add(btnDisable);

                switchContainer.Child = switchGrid;
                Grid.SetRow(switchContainer, 1);
                outerGrid.Children.Add(switchContainer);

                itemBorder.Child = outerGrid;
                SettingsFilesContainer.Children.Add(itemBorder);
            }
        }

        private void BtnToggleLog_Click(object sender, RoutedEventArgs e)
        {
            if (LogPanelContainer.Visibility == Visibility.Collapsed)
            {
                LogPanelContainer.Visibility = Visibility.Visible;
                TxtLogArrow.Text = "▲";
                TxtLogToggleTitle.Text = "运行日志";
            }
            else
            {
                LogPanelContainer.Visibility = Visibility.Collapsed;
                TxtLogArrow.Text = "▼";
                TxtLogToggleTitle.Text = "运行日志";
            }
        }

        // =================== Account Loading & Token Refresh ===================

        private record AccountInfo(string FilePath, string Email, string ProjectId, bool Disabled, string Expired, long FileSize, string ModTime, string AccountType, string PlanType);

        private List<AccountInfo> LoadAllAccounts()
        {
            var accounts = new List<AccountInfo>();
            // 自动去重并过滤失效路径
            _jsonFilePaths = _jsonFilePaths.Distinct(StringComparer.OrdinalIgnoreCase).Where(File.Exists).ToList();

            foreach (var filePath in _jsonFilePaths)
            {
                try
                {
                    if (!File.Exists(filePath)) continue;
                    string content = File.ReadAllText(filePath);
                    using var doc = JsonDocument.Parse(content);
                    var root = doc.RootElement;

                    string email = root.TryGetProperty("email", out var pE) ? pE.GetString() ?? Path.GetFileName(filePath) : Path.GetFileName(filePath);
                    string projectId = root.TryGetProperty("project_id", out var pP) ? pP.GetString() ?? "" : "";
                    bool disabled = root.TryGetProperty("disabled", out var pD) && pD.GetBoolean();
                    string expired = root.TryGetProperty("expired", out var pExp) ? pExp.GetString() ?? "" : "";
                    string accType = root.TryGetProperty("type", out var pT) ? pT.GetString() ?? "unknown" : "unknown";

                    // 设置中「停用」的凭证仅保留文件，不载入主界面，也不参与任何配额刷新与本地代理取号
                    if (disabled) continue;

                    string planType = "Standard";
                    if (root.TryGetProperty("id_token", out var pIdTok))
                    {
                        string idTokStr = pIdTok.GetString() ?? "";
                        if (!string.IsNullOrEmpty(idTokStr))
                        {
                            planType = ExtractPlanFromJwt(idTokStr);
                        }
                    }

                    var fi = new FileInfo(filePath);
                    accounts.Add(new AccountInfo(filePath, email, projectId, disabled, expired, fi.Length, fi.LastWriteTime.ToString("yyyy/M/d HH:mm:ss"), accType, planType));
                }
                catch (Exception ex)
                {
                    Log($"[错误] 解析文件失败 {Path.GetFileName(filePath)}: {ex.Message}");
                }
            }
            return accounts;
        }

        private static string ExtractPlanFromJwt(string jwt)
        {
            try
            {
                var parts = jwt.Split('.');
                if (parts.Length >= 2)
                {
                    string payload = parts[1];
                    // Pad base64
                    switch (payload.Length % 4)
                    {
                        case 2: payload += "=="; break;
                        case 3: payload += "="; break;
                    }
                    byte[] bytes = Convert.FromBase64String(payload);
                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.TryGetProperty("https://api.openai.com/auth", out var authObj))
                    {
                        if (authObj.TryGetProperty("chatgpt_plan_type", out var pPlan))
                        {
                            string plan = pPlan.GetString() ?? "";
                            if (!string.IsNullOrEmpty(plan))
                                return System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(plan);
                        }
                    }
                }
            }
            catch { }
            return "Standard";
        }

        // =================== Quota Fetching (Antigravity / Gemini) ===================

        private async Task<((int percent, string time) g5h, (int percent, string time) gWeek, (int percent, string time) c5h, (int percent, string time) cWeek)> FetchRealTimeQuotaAsync(AccountInfo acc)
        {
            var fallback = ((0, "无数据"), (0, "无数据"), (0, "无数据"), (0, "无数据"));
            try
            {
                if (!File.Exists(acc.FilePath)) return fallback;

                string authJsonContent = File.ReadAllText(acc.FilePath);
                string accessToken = "";
                string refreshToken = "";
                string projectId = "";

                using (var doc = JsonDocument.Parse(authJsonContent))
                {
                    var root = doc.RootElement;
                    if (root.TryGetProperty("access_token", out var pTok)) accessToken = pTok.GetString() ?? "";
                    if (root.TryGetProperty("refresh_token", out var pRef)) refreshToken = pRef.GetString() ?? "";
                    if (root.TryGetProperty("project_id", out var pProj)) projectId = pProj.GetString() ?? "";
                }

                // 1. 优先尝试使用当前现有的 access_token 直接查询配额（优先连通，避免本地时间误判导致频繁失败）
                if (!string.IsNullOrEmpty(accessToken))
                {
                    var directQuota = await FetchAntigravityQuotaAsync(accessToken, projectId);
                    if (IsQuotaResultValid(directQuota))
                    {
                        return directQuota;
                    }
                }

                // 2. 直查未查到有效数据（Token 已过期或无效），若有 refresh_token 则自动向服务器刷新
                if (!string.IsNullOrEmpty(refreshToken))
                {
                    Log($"[Antigravity] Token 已失效，正在自动向服务器刷新...");
                    string? newToken = await RefreshAntigravityTokenAsync(refreshToken);
                    if (!string.IsNullOrEmpty(newToken))
                    {
                        accessToken = newToken;
                        UpdateJsonToken(acc.FilePath, newToken);
                        Log($"[Antigravity] Token 自动刷新成功，正在重新查询真实配额...");
                        var refreshedQuota = await FetchAntigravityQuotaAsync(accessToken, projectId);
                        if (IsQuotaResultValid(refreshedQuota))
                        {
                            return refreshedQuota;
                        }
                    }
                    else
                    {
                        Log($"[Antigravity] ⚠️ Token 自动刷新失败，请检查网络连接/代理状态，或重新登录账号。");
                    }
                }

                if (string.IsNullOrEmpty(accessToken))
                {
                    Log($"[警告] {acc.Email} 缺少有效的 access_token");
                }

                return fallback;
            }
            catch (HttpRequestException ex)
            {
                // 网络层失败（代理未开 / 代理节点不可用 / 域名无法解析 / TLS 握手失败）
                Log($"[配额错误] {acc.Email} ⚠️ 网络连接失败: {ex.Message}（请检查代理是否开启、代理节点是否可用）");
                return fallback;
            }
            catch (TaskCanceledException)
            {
                Log($"[配额错误] {acc.Email} ⚠️ 网络请求超时（请检查代理节点是否可用）");
                return fallback;
            }
            catch (Exception ex)
            {
                Log($"[配额错误] {acc.Email}: {ex.Message}");
                return fallback;
            }
        }



        private static bool IsQuotaResultValid(((int percent, string time) g5h, (int percent, string time) gWeek, (int percent, string time) c5h, (int percent, string time) cWeek) res)
        {
            return !string.Equals(res.g5h.time, "无数据", StringComparison.Ordinal) ||
                   !string.Equals(res.c5h.time, "无数据", StringComparison.Ordinal) ||
                   res.g5h.percent > 0 || res.c5h.percent > 0 || res.gWeek.percent > 0 || res.cWeek.percent > 0;
        }

        private static bool IsTokenExpired(string jsonContent, string expiredStr)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonContent);
                var root = doc.RootElement;
                if (root.TryGetProperty("timestamp", out var pTs) && root.TryGetProperty("expires_in", out var pExp))
                {
                    long ts = pTs.GetInt64();
                    int expIn = pExp.GetInt32();
                    long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (ts > 0 && nowMs > (ts + (long)expIn * 1000 - 60000))
                        return true;
                }
                if (!string.IsNullOrEmpty(expiredStr) && DateTime.TryParse(expiredStr, out var expDt))
                {
                    if (DateTime.UtcNow > expDt.ToUniversalTime().AddMinutes(-1))
                        return true;
                }
            }
            catch { }
            return false;
        }

        private async Task<string?> RefreshAntigravityTokenAsync(string refreshToken)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GeminiClientID,
                    ["client_secret"] = GeminiClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                });

                var resp = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("access_token", out var pTok))
                        return pTok.GetString();
                }
            }
            catch (Exception ex) { Log($"[Antigravity Refresh] {ex.Message}"); }
            return null;
        }

        private async Task<((int percent, string time) g5h, (int percent, string time) gWeek, (int percent, string time) c5h, (int percent, string time) cWeek)> FetchAntigravityQuotaAsync(string accessToken, string projectId)
        {
            var fallback = ((0, "无数据"), (0, "无数据"), (0, "无数据"), (0, "无数据"));
            if (string.IsNullOrEmpty(projectId)) return fallback;

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var req = new HttpRequestMessage(HttpMethod.Post, LocalProxyServer.GoogleCloudCodeBaseUrl + "/v1internal:retrieveUserQuotaSummary");
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            req.Headers.TryAddWithoutValidation("User-Agent", LocalProxyServer.AntigravityUserAgent);
            string body = JsonSerializer.Serialize(new { project = projectId });
            req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");

            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
            {
                string errRaw = await resp.Content.ReadAsStringAsync();
                Log($"[Antigravity 错误] HTTP {(int)resp.StatusCode}: {TruncateText(errRaw, 150)}");
                return fallback;
            }

            string respJson = await resp.Content.ReadAsStringAsync();
            using var quotaDoc = JsonDocument.Parse(respJson);

            (int percent, string time) g5h = (0, "无数据");
            (int percent, string time) gWeek = (0, "无数据");
            (int percent, string time) c5h = (0, "无数据");
            (int percent, string time) cWeek = (0, "无数据");

            if (quotaDoc.RootElement.TryGetProperty("groups", out var groupsArr) && groupsArr.ValueKind == JsonValueKind.Array)
            {
                int groupIndex = 0;
                foreach (var g in groupsArr.EnumerateArray())
                {
                    string gName = g.TryGetProperty("displayName", out var pDN) ? pDN.GetString() ?? "" : "";
                    bool isGeminiGroup = gName.Contains("gemini", StringComparison.OrdinalIgnoreCase) ||
                                         gName.Contains("google", StringComparison.OrdinalIgnoreCase) ||
                                         gName.Contains("antigravity", StringComparison.OrdinalIgnoreCase) ||
                                         (groupIndex == 0 && !gName.Contains("claude", StringComparison.OrdinalIgnoreCase) && !gName.Contains("gpt", StringComparison.OrdinalIgnoreCase));

                    if (g.TryGetProperty("buckets", out var bucketsArr) && bucketsArr.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var b in bucketsArr.EnumerateArray())
                        {
                            string bId = b.TryGetProperty("bucketId", out var pBId) ? pBId.GetString() ?? "" : "";
                            double remFrac = b.TryGetProperty("remainingFraction", out var pFrac) ? pFrac.GetDouble() : 0.0;
                            int remPercent = (int)Math.Round(remFrac * 100.0);
                            string resetTimeStr = b.TryGetProperty("resetTime", out var pReset) ? pReset.GetString() ?? "" : "";
                            string formattedTime = FormatResetTime(resetTimeStr);

                            bool is5h = bId.Contains("5h", StringComparison.OrdinalIgnoreCase) ||
                                        bId.Contains("hour", StringComparison.OrdinalIgnoreCase) ||
                                        bId.Contains("primary", StringComparison.OrdinalIgnoreCase) ||
                                        bId.Contains("short", StringComparison.OrdinalIgnoreCase);

                            bool isWeekly = bId.Contains("week", StringComparison.OrdinalIgnoreCase) ||
                                           bId.Contains("7d", StringComparison.OrdinalIgnoreCase) ||
                                           bId.Contains("secondary", StringComparison.OrdinalIgnoreCase) ||
                                           bId.Contains("long", StringComparison.OrdinalIgnoreCase);

                            if (isGeminiGroup || bId.Contains("gemini", StringComparison.OrdinalIgnoreCase))
                            {
                                if (is5h) g5h = (remPercent, formattedTime);
                                else if (isWeekly) gWeek = (remPercent, formattedTime);
                                else if (g5h.percent == 0 && string.Equals(g5h.time, "无数据", StringComparison.Ordinal)) g5h = (remPercent, formattedTime);
                                else if (gWeek.percent == 0 && string.Equals(gWeek.time, "无数据", StringComparison.Ordinal)) gWeek = (remPercent, formattedTime);
                            }
                            else
                            {
                                if (is5h) c5h = (remPercent, formattedTime);
                                else if (isWeekly) cWeek = (remPercent, formattedTime);
                                else if (c5h.percent == 0 && string.Equals(c5h.time, "无数据", StringComparison.Ordinal)) c5h = (remPercent, formattedTime);
                                else if (cWeek.percent == 0 && string.Equals(cWeek.time, "无数据", StringComparison.Ordinal)) cWeek = (remPercent, formattedTime);
                            }
                        }
                    }
                    groupIndex++;
                }
            }
            else
            {
                // HTTP 200 但响应里没有预期的 groups 结构 → 端点/响应结构可能已变化，输出片段便于排查
                Log($"[Antigravity 诊断] HTTP 200 但响应中未找到 groups 字段，响应片段: {TruncateText(respJson, 300)}");
            }

            return (g5h, gWeek, c5h, cWeek);
        }

        private static string FormatResetTime(string resetTimeUtcStr)
        {
            if (string.IsNullOrEmpty(resetTimeUtcStr)) return "无数据";
            if (DateTime.TryParse(resetTimeUtcStr, out DateTime resetDt))
            {
                TimeSpan diff = resetDt.ToUniversalTime() - DateTime.UtcNow;
                if (diff.TotalSeconds <= 0) return "已刷新";
                if (diff.TotalDays >= 1)
                    return $"{(int)diff.TotalDays} 天 {diff.Hours} 小时 后刷新";
                else
                    return $"{(int)diff.TotalHours} 小时 {diff.Minutes} 分钟 后刷新";
            }
            return "计算中";
        }

        private async Task<string?> RefreshAccessTokenAsync(string refreshToken)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = GeminiClientID,
                    ["client_secret"] = GeminiClientSecret,
                    ["refresh_token"] = refreshToken,
                    ["grant_type"] = "refresh_token"
                });

                var resp = await client.PostAsync("https://oauth2.googleapis.com/token", content);
                if (resp.IsSuccessStatusCode)
                {
                    string json = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("access_token", out var pTok))
                        return pTok.GetString();
                }
                else
                {
                    string err = await resp.Content.ReadAsStringAsync();
                    Log($"[Token 刷新] HTTP {(int)resp.StatusCode}: {err.Substring(0, Math.Min(err.Length, 100))}");
                }
            }
            catch (Exception ex)
            {
                Log($"[Token 刷新错误] {ex.Message}");
            }
            return null;
        }

        private void UpdateJsonToken(string jsonFilePath, string newAccessToken)
        {
            try
            {
                string content = File.ReadAllText(jsonFilePath);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                var dict = new Dictionary<string, object>();
                foreach (var prop in root.EnumerateObject())
                {
                    if (prop.Name == "access_token")
                        dict[prop.Name] = newAccessToken;
                    else if (prop.Name == "timestamp")
                        dict[prop.Name] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    else if (prop.Name == "expires_in")
                        dict[prop.Name] = 3599; // Google OAuth 固定 1 小时，归一化防止陈旧值
                    else if (prop.Name == "expired")
                        dict[prop.Name] = DateTimeOffset.UtcNow.AddSeconds(3599).ToString("yyyy-MM-ddTHH:mm:ssZ");
                    else if (prop.Value.ValueKind == JsonValueKind.String)
                        dict[prop.Name] = prop.Value.GetString()!;
                    else if (prop.Value.ValueKind == JsonValueKind.Number)
                    {
                        if (prop.Value.TryGetInt64(out long lv)) dict[prop.Name] = lv;
                        else dict[prop.Name] = prop.Value.GetDouble();
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.True || prop.Value.ValueKind == JsonValueKind.False)
                        dict[prop.Name] = prop.Value.GetBoolean();
                    else
                        dict[prop.Name] = prop.Value.GetRawText();
                }

                string newJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = false });
                File.WriteAllText(jsonFilePath, newJson);
            }
            catch (Exception ex)
            {
                Log($"[警告] 更新 JSON 文件失败: {ex.Message}");
            }
        }

        // =================== UI Rendering ===================

        // 设置中调整凭证上下顺序后，仅按新顺序重排主界面卡片，不重新查询配额
        private void ReorderAccountCards()
        {
            try
            {
                var orderMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < _jsonFilePaths.Count; i++)
                {
                    orderMap[_jsonFilePaths[i]] = i;
                }

                _cachedQuotas = _cachedQuotas
                    .OrderBy(item => orderMap.TryGetValue(item.acc.FilePath, out int idx) ? idx : int.MaxValue)
                    .ToList();

                RefreshAccountsUIOnly();
                UpdateMiniWidgetData();
            }
            catch (Exception ex)
            {
                Log($"[错误] 重排凭证卡片失败: {ex.Message}");
            }
        }

        private void RefreshAccountsUIOnly()
        {
            try
            {
                QuotaAccountsContainer.Children.Clear();
                if (_cachedQuotas.Count == 0) return;

                foreach (var item in _cachedQuotas)
                {
                    var acc = item.acc;
                    var quota = item.quota;

                    Border accountCard = new Border
                    {
                        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBg)),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(10),
                        Padding = _compactMode ? new Thickness(12, 10, 12, 10) : new Thickness(14, 12, 14, 12),
                        Margin = new Thickness(0, 0, 0, 10),
                        HorizontalAlignment = HorizontalAlignment.Stretch,
                        Tag = acc.FilePath,
                        Effect = new DropShadowEffect
                        {
                            BlurRadius = 14,
                            ShadowDepth = 2,
                            Direction = 270,
                            Color = Color.FromRgb(0, 0, 0),
                            Opacity = 0.06
                        }
                    };
                    AttachCardHoverFeedback(accountCard);

                    StackPanel cardStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

                    // Header
                    Grid headerGrid = new Grid();

                    string platformName = "Antigravity";

                    StackPanel titleStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                    StackPanel badgePanel = new StackPanel { Orientation = Orientation.Horizontal };

                    var platColors = GetPlatformBadgeColors();
                    AddBadge(badgePanel, platformName, platColors.bg, platColors.fg);

                    var statusColors = GetStatusBadgeColors(acc.Disabled);
                    AddBadge(badgePanel, acc.Disabled ? "已禁用" : "启用", statusColors.bg, statusColors.fg);

                    if (!string.IsNullOrEmpty(acc.PlanType) && !acc.PlanType.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                    {
                        var planColors = GetPlanBadgeColors();
                        string planLabel = _compactMode ? acc.PlanType : $"套餐: {acc.PlanType}";
                        AddBadge(badgePanel, planLabel, planColors.bg, planColors.fg);
                    }
                    titleStack.Children.Add(badgePanel);
                    titleStack.Children.Add(new TextBlock
                    {
                        Text = GetDisplayEmail(acc.Email), FontSize = _compactMode ? 13 : 13, FontWeight = FontWeights.Bold,
                        FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                        Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextPrimary)),
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        Margin = new Thickness(0, 4, 0, 0)
                    });
                    headerGrid.Children.Add(titleStack);
                    cardStack.Children.Add(headerGrid);

                    // Meta info（正常模式完整展示，精简模式低密度极简单行印记）
                    if (!_compactMode)
                    {
                        cardStack.Children.Add(new TextBlock
                        {
                            Text = $"文件: {GetDisplayFileName(acc.FilePath)}  大小: {acc.FileSize} B  修改: {acc.ModTime}",
                            FontSize = 10, FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextTertiary)),
                            Margin = new Thickness(0, 4, 0, 10)
                        });
                    }
                    else
                    {
                        cardStack.Children.Add(new TextBlock
                        {
                            Text = $"📄 {GetDisplayFileName(acc.FilePath)} · {acc.FileSize}B",
                            FontSize = 9.5, FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextTertiary)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 3, 0, 6)
                        });
                    }

                    // Quota Sections（专一化：所有账号均为 Gemini/Antigravity 凭证，展示双模型分组）
                    cardStack.Children.Add(CreateGroupSection("Gemini 模型分组", quota.g5h, quota.gWeek));
                    cardStack.Children.Add(new Border
                    {
                        BorderThickness = new Thickness(0, 1, 0, 0),
                        BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder)),
                        Margin = new Thickness(0, 10, 0, 10)
                    });
                    cardStack.Children.Add(CreateGroupSection("Claude 和 GPT 模型分组", quota.c5h, quota.cWeek));

                    accountCard.Child = cardStack;
                    QuotaAccountsContainer.Children.Add(accountCard);
                }
            }
            catch { }
        }

        // 质感：账号卡片 hover 时边框泛主题蓝、阴影加深（移出恢复）
        private void AttachCardHoverFeedback(Border card)
        {
            try
            {
                card.MouseEnter += (s, e) =>
                {
                    if (s is Border bd)
                    {
                        bd.BorderBrush = new SolidColorBrush(Color.FromArgb(0x73, 0x25, 0x63, 0xEB)); // #2563EB 45%
                        if (bd.Effect is DropShadowEffect dse) dse.Opacity = 0.12;
                    }
                };
                card.MouseLeave += (s, e) =>
                {
                    if (s is Border bd)
                    {
                        bd.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder));
                        if (bd.Effect is DropShadowEffect dse) dse.Opacity = 0.06;
                    }
                };
            }
            catch { }
        }

        private async void RefreshAccounts()
        {
            try
            {
                BtnRefreshQuota.IsEnabled = false;
                TxtRefreshBtn.Text = "⏳ 查询中...";
                QuotaAccountsContainer.Children.Clear();

                var accounts = LoadAllAccounts();
                TxtAccountCount.Text = accounts.Count.ToString();
                _cachedQuotas.Clear();

                if (accounts.Count == 0)
                {
                    EmptyStateContainer.Visibility = Visibility.Visible;
                    QuotaAccountsContainer.Visibility = Visibility.Collapsed;
                    UpdateMiniWidgetData();
                    return;
                }

                EmptyStateContainer.Visibility = Visibility.Collapsed;
                QuotaAccountsContainer.Visibility = Visibility.Visible;

                Log("==========================================");
                Log($">>> 开始查询 {accounts.Count} 个账号配额...");

                foreach (var acc in accounts)
                {
                    try
                    {
                        var quota = await FetchRealTimeQuotaAsync(acc);
                        if (IsQuotaResultValid(quota))
                        {
                            // 查询成功：更新“上次成功”缓存（用于网络/代理故障时的降级展示）
                            _lastGoodQuotas[acc.FilePath] = quota;
                        }
                        else if (_lastGoodQuotas.TryGetValue(acc.FilePath, out var lastGood) && IsQuotaResultValid(lastGood))
                        {
                            // 本次查询失败（网络/代理故障等）：降级显示上次成功数据，避免误导性“全部 0%”
                            Log($"[降级] {acc.Email} 本次查询失败（疑似网络/代理故障），显示上次成功数据: 5h {lastGood.g5h.percent}% | 周 {lastGood.gWeek.percent}%");
                            quota = lastGood;
                        }
                        _cachedQuotas.Add((acc, quota));

                        Border accountCard = new Border
                        {
                            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBg)),
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder)),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(10),
                            Padding = _compactMode ? new Thickness(12, 10, 12, 10) : new Thickness(14, 12, 14, 12),
                            Margin = new Thickness(0, 0, 0, 10),
                            HorizontalAlignment = HorizontalAlignment.Stretch,
                            Tag = acc.FilePath,
                            Effect = new DropShadowEffect
                            {
                                BlurRadius = 14,
                                ShadowDepth = 2,
                                Direction = 270,
                                Color = Color.FromRgb(0, 0, 0),
                                Opacity = 0.06
                            }
                        };
                        AttachCardHoverFeedback(accountCard);

                        StackPanel cardStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };

                        // Header
                        Grid headerGrid = new Grid();

                        string platformName = "Antigravity";

                        StackPanel titleStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
                        StackPanel badgePanel = new StackPanel { Orientation = Orientation.Horizontal };

                        var platColors = GetPlatformBadgeColors();
                        AddBadge(badgePanel, platformName, platColors.bg, platColors.fg);

                        var statusColors = GetStatusBadgeColors(acc.Disabled);
                        AddBadge(badgePanel, acc.Disabled ? "已禁用" : "启用", statusColors.bg, statusColors.fg);

                        if (!string.IsNullOrEmpty(acc.PlanType) && !acc.PlanType.Equals("Standard", StringComparison.OrdinalIgnoreCase))
                        {
                            var planColors = GetPlanBadgeColors();
                            string planLabel = _compactMode ? acc.PlanType : $"套餐: {acc.PlanType}";
                            AddBadge(badgePanel, planLabel, planColors.bg, planColors.fg);
                        }
                        titleStack.Children.Add(badgePanel);
                        titleStack.Children.Add(new TextBlock
                        {
                            Text = GetDisplayEmail(acc.Email), FontSize = _compactMode ? 13 : 13, FontWeight = FontWeights.Bold,
                            FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextPrimary)),
                            TextTrimming = TextTrimming.CharacterEllipsis,
                            Margin = new Thickness(0, 4, 0, 0)
                        });
                        headerGrid.Children.Add(titleStack);
                        cardStack.Children.Add(headerGrid);

                        // Meta info（正常模式完整展示，精简模式低密度极简单行印记）
                        if (!_compactMode)
                        {
                            cardStack.Children.Add(new TextBlock
                            {
                                Text = $"文件: {GetDisplayFileName(acc.FilePath)}  大小: {acc.FileSize} B  修改: {acc.ModTime}",
                                FontSize = 10, FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextTertiary)),
                                Margin = new Thickness(0, 4, 0, 10)
                            });
                        }
                        else
                        {
                            cardStack.Children.Add(new TextBlock
                            {
                                Text = $"📄 {GetDisplayFileName(acc.FilePath)} · {acc.FileSize}B",
                                FontSize = 9.5, FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextTertiary)),
                                TextTrimming = TextTrimming.CharacterEllipsis,
                                Margin = new Thickness(0, 3, 0, 6)
                            });
                        }

                        // Quota Sections（专一化：所有账号均为 Gemini/Antigravity 凭证，展示双模型分组）
                        cardStack.Children.Add(CreateGroupSection("Gemini 模型分组", quota.g5h, quota.gWeek));
                        cardStack.Children.Add(new Border
                        {
                            BorderThickness = new Thickness(0, 1, 0, 0),
                            BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcCardBorder)),
                            Margin = new Thickness(0, 10, 0, 10)
                        });
                        cardStack.Children.Add(CreateGroupSection("Claude 和 GPT 模型分组", quota.c5h, quota.cWeek));

                        accountCard.Child = cardStack;
                        QuotaAccountsContainer.Children.Add(accountCard);

                        Log($"[配额] {acc.Email} ({platformName}) — 5h: {quota.c5h.percent}% | 周: {quota.cWeek.percent}%");
                    }
                    catch (Exception ex)
                    {
                        Log($"[错误] 渲染账号卡片失败: {ex.Message}");
                    }
                }

                Log($"[完成] 配额查询结束。");
                UpdateMiniWidgetData();
            }
            catch (Exception ex)
            {
                Log($"[错误] RefreshAccounts: {ex.Message}");
            }
            finally
            {
                BtnRefreshQuota.IsEnabled = true;
                TxtRefreshBtn.Text = "🔄 刷新配额";
            }
        }

        private void BtnSwitchToMini_Click(object sender, RoutedEventArgs e)
        {
            SwitchToMiniMode();
        }

        // =================== UI Helper Methods ===================

        private StackPanel CreateGroupSection(string groupTitle, (int percent, string time) fiveHourQuota, (int percent, string time) weekQuota)
        {
            StackPanel groupStack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
            groupStack.Children.Add(new TextBlock
            {
                Text = groupTitle, FontSize = _compactMode ? 12 : 13, FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextPrimary)),
                Margin = _compactMode ? new Thickness(0, 2, 0, 4) : new Thickness(0, 2, 0, 8)
            });

            // 警示色阶：5h 紧张橙 / 用尽红；周 紧张红 / 用尽深红（保持两种效果区分）
            string bar1Color = fiveHourQuota.percent <= 0 ? "#EF4444" : fiveHourQuota.percent < 50 ? "#F97316" : "#2563EB";
            string bar2Color = weekQuota.percent <= 0 ? "#DC2626" : weekQuota.percent < 50 ? "#EF4444" : "#3B82F6";

            // 5h quota bar
            string label1 = _compactMode ? "5h 限额" : "5 小时限额";
            string label2 = _compactMode ? "周限额" : "周限额";
            groupStack.Children.Add(CreateQuotaBar(label1, fiveHourQuota.percent, fiveHourQuota.time, bar1Color));
            // Weekly quota bar
            groupStack.Children.Add(CreateQuotaBar(label2, weekQuota.percent, weekQuota.time, bar2Color, isMarginTop: true));

            return groupStack;
        }

        private StackPanel CreateQuotaBar(string label, int percent, string refreshTime, string barColor, bool isMarginTop = false)
        {
            Thickness cardMargin = isMarginTop
                ? (_compactMode ? new Thickness(0, 6, 0, 0) : new Thickness(0, 10, 0, 0))
                : new Thickness(0);
            StackPanel card = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = cardMargin };

            Grid line1 = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(0, 0, 0, 4) };
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line1.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            StackPanel labelPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            labelPanel.Children.Add(new TextBlock
            {
                Text = label, FontSize = _compactMode ? 11.5 : 12,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextSecondary)),
                VerticalAlignment = VerticalAlignment.Center
            });
            labelPanel.Children.Add(new TextBlock
            {
                Text = $" 剩余 {percent}%", FontSize = _compactMode ? 13 : 12, FontWeight = FontWeights.Bold,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(percent < 50 ? barColor : _tcTextPrimary)),
                Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            Grid.SetColumn(labelPanel, 0);
            line1.Children.Add(labelPanel);

            TextBlock txtTime = new TextBlock
            {
                Text = refreshTime, FontSize = _compactMode ? 10 : 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTextTertiary)),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(txtTime, 1);
            line1.Children.Add(txtTime);
            card.Children.Add(line1);

            // Progress bar（精简模式：进度条 8px 饱满胶囊，小窗下格外精致清晰）
            double barHeight = _compactMode ? 8 : 6;
            Border trackBorder = new Border
            {
                Height = barHeight, CornerRadius = new CornerRadius(barHeight / 2),
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(_tcTrackBg)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(10, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Stretch, SnapsToDevicePixels = true
            };
            Grid fillGrid = new Grid();
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Clamp(percent, 0, 100), GridUnitType.Star) });
            fillGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0, 100 - percent), GridUnitType.Star) });
            // 渐变填充（同色系由深到浅，质感更润）
            string barColorLight = barColor switch
            {
                "#2563EB" => "#60A5FA",
                "#3B82F6" => "#93C5FD",
                "#EF4444" => "#F87171",
                "#F97316" => "#FB923C",
                "#DC2626" => "#EF4444",
                _ => barColor
            };
            var barBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 0)
            };
            barBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(barColor), 0));
            barBrush.GradientStops.Add(new GradientStop((Color)ColorConverter.ConvertFromString(barColorLight), 1));

            Border fillBorder = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(0.5, barHeight / 2 - 0.5)),
                Background = barBrush,
                SnapsToDevicePixels = true
            };
            Grid.SetColumn(fillBorder, 0);
            fillGrid.Children.Add(fillBorder);
            trackBorder.Child = fillGrid;
            card.Children.Add(trackBorder);

            return card;
        }

        private void AddBadge(StackPanel parent, string text, string bgColor, string fgColor)
        {
            Border b = new Border
            {
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(bgColor)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(32, 0, 0, 0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 6, 0)
            };
            b.Child = new TextBlock
            {
                Text = text, FontSize = _compactMode ? 11 : 10, FontWeight = FontWeights.SemiBold,
                FontFamily = new FontFamily("Cascadia Code, Consolas, Segoe UI"),
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fgColor))
            };
            parent.Children.Add(b);
        }

        // =================== Event Handlers ===================

        private static bool IsValidAuthJson(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

                string fileName = Path.GetFileName(filePath);
                if (fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase)) return false;

                string content = File.ReadAllText(filePath);
                if (string.IsNullOrWhiteSpace(content)) return false;

                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return false;

                // 严密排除软件自身的 settings.json 配置文件及非凭证属性特征
                if (root.TryGetProperty("json_file_paths", out _) ||
                    root.TryGetProperty("auto_refresh_interval", out _) ||
                    root.TryGetProperty("autoRefreshIntervalMinutes", out _) ||
                    root.TryGetProperty("themeMode", out _) ||
                    root.TryGetProperty("isMiniOn", out _) ||
                    root.TryGetProperty("dataDir", out _))
                {
                    return false;
                }

                // 核心凭证字段校验 (符合 CLIProxyAPI / Haodo 凭证规范)
                bool hasRefreshToken = root.TryGetProperty("refresh_token", out var pRef) && pRef.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pRef.GetString());
                bool hasAccessToken = root.TryGetProperty("access_token", out var pAcc) && pAcc.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pAcc.GetString());
                bool hasProjectId = root.TryGetProperty("project_id", out var pProj) && pProj.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pProj.GetString());
                bool hasType = root.TryGetProperty("type", out var pType) && pType.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(pType.GetString());
                bool hasClientId = root.TryGetProperty("client_id", out _);
                bool hasIdToken = root.TryGetProperty("id_token", out _);

                // 专一化：仅接受 Antigravity / Gemini 凭证；其余平台凭证（Claude/Codex/Grok/Kimi 等）一律跳过载入
                if (hasType)
                {
                    string credType = pType.GetString() ?? "";
                    if (!credType.Equals("antigravity", StringComparison.OrdinalIgnoreCase) &&
                        !credType.Equals("gemini", StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }

                return hasRefreshToken || hasAccessToken || hasProjectId || hasType || hasClientId || hasIdToken;
            }
            catch
            {
                return false;
            }
        }

        private void BtnAddFiles_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择 JSON 凭证文件",
                Filter = "JSON 凭证文件 (*.json)|*.json|所有文件 (*.*)|*.*",
                InitialDirectory = Directory.Exists(_dataDir) ? _dataDir : AppDomain.CurrentDomain.BaseDirectory,
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
            {
                int added = 0;
                List<string> invalidFiles = new();
                List<string> duplicateFiles = new();

                if (!Directory.Exists(_dataDir))
                {
                    try { Directory.CreateDirectory(_dataDir); } catch { }
                }

                foreach (var file in dlg.FileNames)
                {
                    string fileName = Path.GetFileName(file);
                    if (fileName.Equals("settings.json", StringComparison.OrdinalIgnoreCase) || !IsValidAuthJson(file))
                    {
                        invalidFiles.Add(fileName);
                        continue;
                    }

                    string targetPath = file;
                    string sourceDir = Path.GetDirectoryName(file) ?? "";
                    // 如果选中的凭证不在当前数据存放位置中，自动安全复制至数据目录
                    if (!sourceDir.Equals(_dataDir, StringComparison.OrdinalIgnoreCase))
                    {
                        targetPath = Path.Combine(_dataDir, fileName);
                        try
                        {
                            if (!File.Exists(targetPath))
                            {
                                File.Copy(file, targetPath, overwrite: true);
                                Log($"[自动归集] 已将外部凭证复制存入数据目录: {fileName}");
                            }
                        }
                        catch { }
                    }

                    if (!_jsonFilePaths.Contains(targetPath, StringComparer.OrdinalIgnoreCase))
                    {
                        _jsonFilePaths.Add(targetPath);
                        added++;
                    }
                    else
                    {
                        duplicateFiles.Add(fileName);
                    }
                }

                if (added > 0)
                {
                    SaveSettings();
                    RenderSettingsFilesList();
                    RefreshAccounts();
                    Log($"[导入成功] 成功添加并集中管理了 {added} 个凭证文件");
                }

                // 如果存在选择错误的非凭证文件 (如 settings.json 或无效格式)，通过符合软件 UI 风格的自定弹窗提示用户
                if (invalidFiles.Count > 0)
                {
                    string invalidListStr = string.Join("\n• ", invalidFiles);
                    Log($"[拦截警告] 用户尝试导入非凭证文件: {string.Join(", ", invalidFiles)}");
                    ShowCustomModal(
                        "导入提示 - 文件格式不匹配",
                        $"以下文件不是有效的账号凭证文件，已被自动拒绝导入：\n\n• {invalidListStr}\n\n格式说明：\n1. settings.json 为软件自身的配置文件，不是账号凭证。\n2. 请选择有效的凭证 JSON 文件（包含 refresh_token / access_token 等核心属性）。",
                        "");
                }
                else if (added == 0 && duplicateFiles.Count > 0)
                {
                    ShowCustomModal("提示", "所选凭证文件已全部存在于列表中，无需重复添加。", "");
                }
            }
        }

        private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAccountFile != null && File.Exists(_selectedAccountFile))
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{_selectedAccountFile}\"");
            }
            else if (_jsonFilePaths.Count > 0)
            {
                string? dir = Path.GetDirectoryName(_jsonFilePaths[0]);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start("explorer.exe", dir);
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", AppDomain.CurrentDomain.BaseDirectory);
            }
        }

        private void BtnRefreshQuota_Click(object sender, RoutedEventArgs e)
        {
            Log("正在刷新配额...");
            RefreshAccounts();
        }

        private void BtnRemoveAccount_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAccountFile == null)
            {
                MessageBox.Show("请先点击选中一个账号卡片，再执行移除。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show($"确定要从列表中移除该账号文件吗？\n\n{GetDisplayFileName(_selectedAccountFile)}\n\n（不会删除文件本身）", "确认移除", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _jsonFilePaths.RemoveAll(p => string.Equals(p, _selectedAccountFile, StringComparison.OrdinalIgnoreCase));
                Log($"[移除] {Path.GetFileName(_selectedAccountFile)}");
                _selectedAccountFile = null;
                SaveSettings();
                RefreshAccounts();
            }
        }

        // =================== Gemini OAuth Login ===================

        // 说明：Google 官方允许 Desktop Installed App 客户端内嵌公开 OAuth 凭据，运行时动态解码
        private static readonly string GeminiClientID = DecodeOAuthKey(new byte[] { 107, 106, 109, 107, 106, 106, 108, 106, 108, 106, 111, 99, 107, 119, 46, 55, 50, 41, 41, 51, 52, 104, 50, 104, 107, 54, 57, 40, 63, 104, 105, 111, 44, 46, 53, 54, 53, 48, 50, 110, 61, 110, 106, 105, 63, 42, 116, 59, 42, 42, 41, 116, 61, 53, 53, 61, 54, 63, 47, 41, 63, 40, 57, 53, 52, 46, 63, 52, 46, 116, 57, 53, 55 });
        private static readonly string GeminiClientSecret = DecodeOAuthKey(new byte[] { 29, 21, 25, 9, 10, 2, 119, 17, 111, 98, 28, 13, 8, 110, 98, 108, 22, 62, 22, 16, 107, 55, 22, 24, 98, 41, 2, 25, 110, 32, 108, 43, 30, 27, 60 });
        private static readonly string[] GeminiScopes = new[]
        {
            "https://www.googleapis.com/auth/cloud-platform",
            "https://www.googleapis.com/auth/userinfo.email",
            "https://www.googleapis.com/auth/userinfo.profile",
            "https://www.googleapis.com/auth/cclog",
            "https://www.googleapis.com/auth/experimentsandconfigs"
        };

        private async void BtnGeminiLogin_Click(object sender, RoutedEventArgs e)
        {
            await StartGeminiOAuthLoginAsync();
        }

        private async Task StartGeminiOAuthLoginAsync()
        {
            try
            {
                int port = 51121;
                HttpListener? listener = null;

                for (int p = 51121; p <= 51130; p++)
                {
                    try
                    {
                        var l = new HttpListener();
                        l.Prefixes.Add($"http://localhost:{p}/oauth-callback/");
                        l.Start();
                        listener = l;
                        port = p;
                        break;
                    }
                    catch { }
                }

                if (listener == null)
                {
                    ShowCustomModal("登录失败", "无法启动本地 OAuth 接收端口 (51121-51130 已被占用)。", "");
                    return;
                }

                string redirectUri = $"http://localhost:{port}/oauth-callback";
                string state = Guid.NewGuid().ToString("N");
                string scopesStr = Uri.EscapeDataString(string.Join(" ", GeminiScopes));
                string authUrl = $"https://accounts.google.com/o/oauth2/v2/auth?access_type=offline&client_id={GeminiClientID}&prompt=consent&redirect_uri={Uri.EscapeDataString(redirectUri)}&response_type=code&scope={scopesStr}&state={state}";

                Log($"[Gemini OAuth] 正在自动打开浏览器登录 Google 账号 (监听端口: {port})...");

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = authUrl,
                        UseShellExecute = true
                    });
                }
                catch
                {
                    Log($"[Gemini OAuth] 自动打开浏览器失败，请手动打开 URL: {authUrl}");
                }

                var contextTask = listener.GetContextAsync();
                var timeoutTask = Task.Delay(TimeSpan.FromMinutes(3));
                var completedTask = await Task.WhenAny(contextTask, timeoutTask);

                if (completedTask != contextTask)
                {
                    try { listener.Stop(); } catch { }
                    Log("[Gemini OAuth] 登录授权超时 (3 分钟内未在浏览器完成登录)。");
                    ShowCustomModal("提示", "Google 登录授权超时，请重试。", "");
                    return;
                }

                var context = await contextTask;
                var request = context.Request;
                var response = context.Response;

                string code = request.QueryString["code"] ?? "";
                string err = request.QueryString["error"] ?? "";

                string htmlResponse = "";
                if (!string.IsNullOrEmpty(code) && string.IsNullOrEmpty(err))
                {
                    htmlResponse = "<html><head><meta charset='utf-8'><title>登录成功</title></head><body style='font-family:-apple-system,BlinkMacSystemFont,\"Segoe UI\",sans-serif;text-align:center;padding:50px;background:#0F172A;color:#F8FAFC;'><div style='max-width:400px;margin:0 auto;background:#161B22;padding:30px;border-radius:12px;border:1px solid #2D3748;'><h1 style='color:#4ADE80;margin-bottom:10px;'>🎉 Google 登录成功！</h1><p style='color:#94A3B8;font-size:14px;line-height:1.6;'>Haodo 已成功接收授权 Code。<br>凭证文件已自动创建并存入数据目录，您可以关闭此浏览器页面。</p></div></body></html>";
                }
                else
                {
                    htmlResponse = $"<html><head><meta charset='utf-8'><title>登录失败</title></head><body style='font-family:-apple-system,BlinkMacSystemFont,\"Segoe UI\",sans-serif;text-align:center;padding:50px;background:#0F172A;color:#F8FAFC;'><div style='max-width:400px;margin:0 auto;background:#161B22;padding:30px;border-radius:12px;border:1px solid #2D3748;'><h1 style='color:#EF4444;margin-bottom:10px;'>❌ 授权失败</h1><p style='color:#94A3B8;font-size:14px;'>{err}</p></div></body></html>";
                }

                byte[] buffer = System.Text.Encoding.UTF8.GetBytes(htmlResponse);
                response.ContentLength64 = buffer.Length;
                response.ContentType = "text/html; charset=utf-8";
                await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
                response.OutputStream.Close();
                try { listener.Stop(); } catch { }

                if (!string.IsNullOrEmpty(err) || string.IsNullOrEmpty(code))
                {
                    Log($"[Gemini OAuth 错误] 用户取消或授权失败: {err}");
                    return;
                }

                Log("[Gemini OAuth] 正在向 Google 换取 Token 凭证...");

                using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var tokenReqContent = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["code"] = code,
                    ["client_id"] = GeminiClientID,
                    ["client_secret"] = GeminiClientSecret,
                    ["redirect_uri"] = redirectUri,
                    ["grant_type"] = "authorization_code"
                });

                var tokenResp = await httpClient.PostAsync("https://oauth2.googleapis.com/token", tokenReqContent);
                if (!tokenResp.IsSuccessStatusCode)
                {
                    string errBody = await tokenResp.Content.ReadAsStringAsync();
                    Log($"[Gemini OAuth 错误] Token 换取失败: {errBody}");
                    ShowCustomModal("错误", $"Token 换取失败: {errBody}", "");
                    return;
                }

                string tokenJson = await tokenResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(tokenJson);
                var root = doc.RootElement;

                string accessToken = root.GetProperty("access_token").GetString() ?? "";
                string refreshToken = root.TryGetProperty("refresh_token", out var pRef) ? pRef.GetString() ?? "" : "";
                long expiresIn = root.TryGetProperty("expires_in", out var pExp) ? pExp.GetInt64() : 3599;

                if (string.IsNullOrEmpty(accessToken))
                {
                    Log("[Gemini OAuth 错误] 未获取到有效 access_token");
                    return;
                }

                Log("[Gemini OAuth] 正在获取 Google 账号 Email 信息...");
                string email = "";
                try
                {
                    var userReq = new HttpRequestMessage(HttpMethod.Get, "https://www.googleapis.com/oauth2/v2/userinfo?alt=json");
                    userReq.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                    var userResp = await httpClient.SendAsync(userReq);
                    if (userResp.IsSuccessStatusCode)
                    {
                        string uJson = await userResp.Content.ReadAsStringAsync();
                        using var uDoc = JsonDocument.Parse(uJson);
                        if (uDoc.RootElement.TryGetProperty("email", out var pEmail))
                            email = pEmail.GetString() ?? "";
                    }
                }
                catch { }

                if (string.IsNullOrEmpty(email))
                {
                    email = "antigravity_user";
                }

                Log("[Gemini OAuth] 正在查询 GCP Project ID...");
                string projectId = await FetchAntigravityProjectIDAsync(httpClient, accessToken);

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                string expiredStr = DateTime.UtcNow.AddSeconds(expiresIn).ToString("yyyy-MM-ddTHH:mm:ssZ");

                var credDict = new Dictionary<string, object>
                {
                    ["type"] = "antigravity",
                    ["access_token"] = accessToken,
                    ["refresh_token"] = refreshToken,
                    ["expires_in"] = expiresIn,
                    ["timestamp"] = nowMs,
                    ["expired"] = expiredStr,
                    ["email"] = email
                };
                if (!string.IsNullOrEmpty(projectId))
                {
                    credDict["project_id"] = projectId;
                }

                if (!Directory.Exists(_dataDir))
                {
                    Directory.CreateDirectory(_dataDir);
                }

                string safeEmail = string.Concat(email.Split(Path.GetInvalidFileNameChars()));
                string fileName = $"antigravity-{safeEmail}.json";
                string filePath = Path.Combine(_dataDir, fileName);

                string jsonContent = JsonSerializer.Serialize(credDict, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, jsonContent);

                if (!_jsonFilePaths.Contains(filePath, StringComparer.OrdinalIgnoreCase))
                {
                    _jsonFilePaths.Add(filePath);
                }

                SaveSettings();
                RenderSettingsFilesList();
                RefreshAccounts();

                Log($"[Gemini OAuth 成功] 已保存凭证至 {fileName} (Project: {projectId})");
                ShowCustomModal("Gemini OAuth 登录成功", $"登录成功！凭证已自动建立并管理：\n\n• 文件名: {fileName}\n• 账号: {email}\n• Project: {projectId}", "");
            }
            catch (Exception ex)
            {
                Log($"[Gemini OAuth 异常] {ex.Message}");
                ShowCustomModal("登录失败", $"OAuth 登录失败: {ex.Message}", "");
            }
        }

        private async Task<string> FetchAntigravityProjectIDAsync(HttpClient httpClient, string accessToken)
        {
            try
            {
                var req = new HttpRequestMessage(HttpMethod.Post, LocalProxyServer.GoogleCloudCodeBaseUrl + "/v1internal:loadCodeAssist");
                req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
                req.Headers.TryAddWithoutValidation("Content-Type", "application/json");
                req.Headers.TryAddWithoutValidation("User-Agent", LocalProxyServer.AntigravityUserAgent);
                req.Content = new StringContent("{\"metadata\":{\"ideType\":\"ANTIGRAVITY\"}}", System.Text.Encoding.UTF8, "application/json");

                var resp = await httpClient.SendAsync(req);
                if (resp.IsSuccessStatusCode)
                {
                    string respJson = await resp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(respJson);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("cloudaicompanionProject", out var pCompanion))
                    {
                        if (pCompanion.ValueKind == JsonValueKind.String)
                            return pCompanion.GetString() ?? "";
                        else if (pCompanion.ValueKind == JsonValueKind.Object && pCompanion.TryGetProperty("id", out var pId))
                            return pId.GetString() ?? "";
                    }
                    if (root.TryGetProperty("projectId", out var pProjId))
                    {
                        return pProjId.GetString() ?? "";
                    }
                }
            }
            catch { }

            return "";
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e) => TxtLog.Clear();

        private void BtnCopyLog_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(TxtLog.Text);
                ShowCustomModal("提示", "日志已成功复制到剪贴板。", "📋");
            }
            catch (Exception ex) { Log($"[错误] 复制失败: {ex.Message}"); }
        }

        // =================== Custom UI Modal Dialog ===================

        private Action? _modalConfirmAction = null;

        private void SyncRootShadowWithOverlay()
        {
            // 弹窗/遮罩层显示期间禁用窗口根阴影：半透明遮罩会衬托出重影且阴影溢出窗口边缘
            if (RootShadow == null) return;
            bool overlayShown = (GridUpdateOverlay != null && GridUpdateOverlay.Visibility == Visibility.Visible) ||
                                (GridCustomModalOverlay != null && GridCustomModalOverlay.Visibility == Visibility.Visible);
            RootShadow.Opacity = overlayShown ? 0 : (WindowState == WindowState.Maximized ? 0 : 0.14);
        }

        private void ShowCustomModal(string title, string message, string icon = "")
        {
            Dispatcher.Invoke(() =>
            {
                TxtModalTitle.Text = title;
                TxtModalMessage.Text = message;
                BtnModalOk.Visibility = Visibility.Visible;
                BtnModalOk.Margin = new Thickness(0);
                Grid.SetColumn(BtnModalOk, 0);
                Grid.SetColumnSpan(BtnModalOk, 3);
                BtnModalCancel.Visibility = Visibility.Collapsed;
                BtnModalConfirm.Visibility = Visibility.Collapsed;
                _modalConfirmAction = null;
                GridCustomModalOverlay.Visibility = Visibility.Visible;
                SyncRootShadowWithOverlay();
            });
        }

        private void ShowConfirmModal(string title, string message, Action onConfirm)
        {
            Dispatcher.Invoke(() =>
            {
                TxtModalTitle.Text = title;
                TxtModalMessage.Text = message;
                BtnModalOk.Visibility = Visibility.Collapsed;
                BtnModalCancel.Visibility = Visibility.Visible;
                BtnModalCancel.Margin = new Thickness(0, 0, 8, 0);
                BtnModalConfirm.Visibility = Visibility.Visible;
                BtnModalConfirm.Margin = new Thickness(0);
                _modalConfirmAction = onConfirm;
                GridCustomModalOverlay.Visibility = Visibility.Visible;
                SyncRootShadowWithOverlay();
            });
        }

        private void BtnModalConfirm_Click(object sender, RoutedEventArgs e)
        {
            GridCustomModalOverlay.Visibility = Visibility.Collapsed;
            SyncRootShadowWithOverlay();
            var action = _modalConfirmAction;
            _modalConfirmAction = null;
            try { action?.Invoke(); }
            catch (Exception ex) { Log($"[错误] 操作执行失败: {ex.Message}"); }
        }

        private void BtnModalOk_Click(object sender, RoutedEventArgs e)
        {
            GridCustomModalOverlay.Visibility = Visibility.Collapsed;
            SyncRootShadowWithOverlay();
            _modalConfirmAction = null;
        }

        private void BtnCloseCustomModal_Click(object sender, RoutedEventArgs e)
        {
            GridCustomModalOverlay.Visibility = Visibility.Collapsed;
            SyncRootShadowWithOverlay();
            _modalConfirmAction = null;
        }

        // =================== Software Auto Update & Version Checking ===================

        public static readonly string CurrentVersionStr =
            System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";
        public static readonly int CurrentBuildNum = GetCurrentBuildNumber();
        public const string VersionJsonUrl = "https://yusnip.top/version.json";

        private static int GetCurrentBuildNumber()
        {
            var parts = CurrentVersionStr.Split('.');
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], out int major) ||
                !int.TryParse(parts[1], out int minor) ||
                !int.TryParse(parts[2], out int patch))
            {
                return 0;
            }

            return major * 100 + minor * 10 + patch;
        }

        public class UpdateInfo
        {
            public string name { get; set; } = "";
            public string version { get; set; } = "";
            public int build { get; set; } = 0;
            public string download_url { get; set; } = "";
            public string updated_at { get; set; } = "";
            public string changelog { get; set; } = "";
            public bool force_update { get; set; } = false;
        }

        private UpdateInfo? _latestUpdateInfo = null;

        private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            TxtCheckUpdateBtn.Text = "检查中...";
            await CheckForUpdatesAsync(isManualCheck: true);
            TxtCheckUpdateBtn.Text = "检查更新";
        }

        // 关于卡片：QQ 群交流按钮 → 复制群号 + 唤起 QQ（失败时提示已复制）
        private void BtnQQGroup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText("453478357");
                Process.Start(new ProcessStartInfo("tencent://groupchat/?uin=453478357") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log($"[关于] 打开 QQ 群链接失败: {ex.Message}");
                ShowCustomModal("提示", "已复制 QQ 群号：453478357\n请在 QQ 中搜索并加入该群。", "✓");
            }
        }

        private async Task CheckForUpdatesAsync(bool isManualCheck)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
                var resp = await client.GetAsync(VersionJsonUrl);
                if (!resp.IsSuccessStatusCode)
                {
                    if (isManualCheck)
                    {
                        ShowCustomModal("检查更新失败", $"无法连接版本服务器 (HTTP {(int)resp.StatusCode})。");
                    }
                    return;
                }

                string json = await resp.Content.ReadAsStringAsync();
                var info = JsonSerializer.Deserialize<UpdateInfo>(json);

                if (info == null)
                {
                    if (isManualCheck) ShowCustomModal("检查更新失败", "解析版本数据失败。");
                    return;
                }

                if (info.build > CurrentBuildNum)
                {
                    _latestUpdateInfo = info;
                    Log($"[发现新版本] v{info.version} (Build {info.build}) - {info.changelog}");

                    Dispatcher.Invoke(() =>
                    {
                        TxtUpdateModalTitle.Text = $"发现新版本 v{info.version}";
                        TxtUpdateDate.Text = string.IsNullOrWhiteSpace(info.updated_at) ? "最新版本" : info.updated_at;
                        string changelog = info.changelog?.Trim() ?? "";
                        if (string.IsNullOrWhiteSpace(changelog) || 
                            changelog.Contains("软件版本更新至") || 
                            changelog.Contains("更新至") ||
                            !changelog.Contains("1."))
                        {
                            changelog = "1. 修复已知问题。\n2. 增加了一些新的特性。";
                        }
                        TxtUpdateChangelog.Text = changelog;

                        TxtUpdateProgress.Visibility = Visibility.Collapsed;
                        GridUpdateButtons.IsEnabled = true;
                        GridUpdateOverlay.Visibility = Visibility.Visible;
                        SyncRootShadowWithOverlay();
                    });
                }
                else
                {
                    Log($"[更新检查] 当前已是最新版本 v{CurrentVersionStr} (Build {CurrentBuildNum})");
                    if (isManualCheck)
                    {
                        ShowCustomModal("已是最新版本", $"当前版本 v{CurrentVersionStr} (Build {CurrentBuildNum}) 已是最新版本。");
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"[检查更新异常] {ex.Message}");
                if (isManualCheck)
                {
                    ShowCustomModal("检查更新失败", $"网络异常或无法连接更新服务器：{ex.Message}");
                }
            }
        }

        private void BtnCancelUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdateInfo != null && _latestUpdateInfo.force_update)
            {
                ShowCustomModal("提示", "本次更新包含紧急功能修正，为强制更新。");
                return;
            }
            GridUpdateOverlay.Visibility = Visibility.Collapsed;
            SyncRootShadowWithOverlay();
        }

        private async void BtnConfirmUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (_latestUpdateInfo == null || string.IsNullOrWhiteSpace(_latestUpdateInfo.download_url))
            {
                ShowCustomModal("更新失败", "下载链接无效。");
                return;
            }

            try
            {
                GridUpdateButtons.IsEnabled = false;
                TxtUpdateProgress.Visibility = Visibility.Visible;
                TxtUpdateProgress.Text = "⏳ 正在下载最新版本...";

                string downloadUrl = _latestUpdateInfo.download_url;
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName
                                 ?? Path.Combine(baseDir, "Haodo.exe");
                string newExePath = exePath + ".new";

                using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
                using var resp = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);

                if (!resp.IsSuccessStatusCode)
                {
                    TxtUpdateProgress.Visibility = Visibility.Collapsed;
                    GridUpdateButtons.IsEnabled = true;
                    ShowCustomModal("下载失败", $"服务器返回 HTTP {(int)resp.StatusCode}");
                    return;
                }

                long totalBytes = resp.Content.Headers.ContentLength ?? -1;
                using (var stream = await resp.Content.ReadAsStreamAsync())
                using (var fs = new FileStream(newExePath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[8192];
                    long totalRead = 0;
                    int read;

                    while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fs.WriteAsync(buffer, 0, read);
                        totalRead += read;

                        if (totalBytes > 0)
                        {
                            int pct = (int)((totalRead * 100) / totalBytes);
                            TxtUpdateProgress.Text = $"⏳ 正在下载最新版本 ({pct}%)...";
                        }
                        else
                        {
                            TxtUpdateProgress.Text = $"⏳ 正在下载最新版本 ({(totalRead / 1024)} KB)...";
                        }
                    }
                }

                TxtUpdateProgress.Text = "下载完成，正在重启并自动应用更新...";
                Log("[软件更新] 下载完成，即将重启替代文件...");

                // 使用 PowerShell 隐藏后台无窗口执行无缝覆盖替换与自动重新启动（无临时脚本文件、无 CMD 终端弹出）
                string psScript = $"Start-Sleep -Seconds 1; while (Test-Path '{newExePath}') {{ try {{ Move-Item -Path '{newExePath}' -Destination '{exePath}' -Force -ErrorAction Stop }} catch {{ Start-Sleep -Seconds 1 }} }}; Start-Process -FilePath '{exePath}'";

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"{psScript}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                };
                System.Diagnostics.Process.Start(psi);

                // 退出当前应用，释放文件锁
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                TxtUpdateProgress.Visibility = Visibility.Collapsed;
                GridUpdateButtons.IsEnabled = true;
                Log($"[软件更新异常] {ex.Message}");
                ShowCustomModal("更新失败", $"下载或覆盖过程发生异常: {ex.Message}");
            }
        }

        // =================== Log ===================

        public void LogMiniWidgetDiagnostic(string message)
        {
            try { Log($"[贴贴诊断] {message}"); } catch { }
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "HaodoMiniWidgetDiagnostics.log");
                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch { }
        }

        private void Log(string message)
        {
            try
            {
                // 隐私脱敏开启时，日志中出现的任何邮箱统一打星（含刷新日志/错误日志中的账号邮箱）
                if (_maskAccountInfo)
                {
                    message = MaskEmailsInText(message);
                }

                Dispatcher.Invoke(() =>
                {
                    string time = DateTime.Now.ToString("HH:mm:ss");
                    TxtLog.AppendText($"[{time}] {message}\n");
                    LogScrollViewer.ScrollToEnd();
                });
            }
            catch
            {
                // 窗口关闭/退出期间 Dispatcher 可能已不可用；日志仅为调试辅助，静默失败即可。
                // 严禁异常冒泡：LogMiniWidgetDiagnostic 会被 Win32 WndProc 调用，异常会进入窗口过程。
            }
        }
    }

    /// <summary>Google Drive 登录令牌状态（敏感字段以 DPAPI Base64 存储于 data/google-token.json）</summary>
    public class GoogleTokenState
    {
        public string UserEmail { get; set; } = "";
        public string? PictureUrl { get; set; }
        public string? RefreshTokenProtected { get; set; }
        public string? AccessTokenProtected { get; set; }
        public long ExpiresAtUnixMs { get; set; }
        public string? FileId { get; set; }
        public string? CredentialsFileId { get; set; }
        public long LastSyncUnixMs { get; set; }
    }
}
