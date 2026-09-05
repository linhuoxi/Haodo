using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace CLIProxyAPI_GUI
{
    public partial class MiniWidgetWindow : Window
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll")]
        private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetProp(IntPtr hWnd, string lpString, IntPtr hData);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr RemoveProp(IntPtr hWnd, string lpString);

        /// <summary>窗口属性标记：置位表示鼠标穿透已启用（供外部诊断/验证识别穿透状态）。</summary>
        private const string PropTransparent = "HaodoTransparent";

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        private const int SW_SHOW = 5;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int SW_HIDE = 0;
        private const int WH_MOUSE_LL = 14;
        private const int WM_RBUTTONDOWN = 0x0204;
        private const int WM_RBUTTONUP = 0x0205;

        // ===== Win+D / 显示桌面 / 最小化等系统隐藏防护相关消息 =====
        private const int WM_SIZE = 0x0005;
        private const int WM_SHOWWINDOW = 0x0018;
        private const int WM_WINDOWPOSCHANGING = 0x0046;
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_MINIMIZE = 0xF020;
        private const int SIZE_MINIMIZED = 1;
        private const int SW_PARENTCLOSING = 0x0002;
        private const int SW_OTHERZOOM = 0x0004;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT
        {
            public POINT Pt;
            public uint MouseData;
            public uint Flags;
            public uint Time;
            public IntPtr ExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPOS
        {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        private const int GWL_STYLE = -16;
        private const int GWL_EXSTYLE = -20;
        private const int WS_CHILD = 0x40000000;
        private const int WS_POPUP = unchecked((int)0x80000000);
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WS_EX_APPWINDOW = 0x00040000;
        private const int WM_NCHITTEST = 0x0084;
        private const int HTTRANSPARENT = -2;

        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private const uint SWP_FRAMECHANGED = 0x0020;
        private const uint SWP_HIDEWINDOW = 0x0080;

        private readonly MainWindow _mainWin;
        private bool _isRefreshing = false;
        private const double FloatingWidth = 244;
        private const double FloatingHeight = 98;
        private const double TaskbarWidth = 264;
        private const double TaskbarPreferredHeight = 40;

        private string _customColorHex = "#FFFFFF";
        private string _effectiveColorHex = "#FFFFFF";

        private string _modeType = "taskbar"; // "taskbar" / "floating"
        private string _bgHex = "#161B22";
        private double _bgOpacity = 0.85;
        private bool _isTransparent = false;
        private IntPtr _rightClickHook = IntPtr.Zero;
        private LowLevelMouseProc? _rightClickHookProc;
        // 置位时放行窗口自身的隐藏/关闭流程，避免 WndProc 误拦截（OnClosing 的 Hide() / CloseWindow() 销毁）
        private bool _allowSystemHide = false;

        // ===== 靠近边缘自动收纳 =====
        private bool _edgeDockEnabled = true;
        // 隐藏悬浮卡片圆角背景框（背景透明 + 无边框），仅保留配额内容
        private bool _hideCardBg = false;
        private string _dockEdge = "none"; // none / left / top / right / bottom；四边均以屏幕物理分辨率边缘为基准
        private double _dockBaseLeft;
        private double _dockBaseTop;
        private bool _edgeExpanded = true;
        private bool _isEdgeAnimating = false;
        private bool _isDraggingWidget = false;
        private double _dockPeek = 24;        // 收纳后露出的把手宽度/高度(逻辑像素)
        private double _dockThreshold = 0.5;  // 仅容许 WPF/DPI 舍入误差；视觉上必须接触边缘，不接受可见缝隙
        private double _dockHitMargin = 8;    // 露出把手/展开卡片的命中边距(逻辑像素)；仅命中把手或卡片本身才展开，不再整条边触发
        private double _slideTargetLeft;
        private double _slideTargetTop;
        private double _slideStartLeft;
        private double _slideStartTop;
        private long _slideStartedAt;
        private const double SlideDurationMs = 240;
        private readonly System.Windows.Threading.DispatcherTimer _slideTimer =
            new() { Interval = TimeSpan.FromMilliseconds(16) };
        private readonly System.Windows.Threading.DispatcherTimer _edgeTimer =
            new() { Interval = TimeSpan.FromMilliseconds(90) };
        // 鼠标离开后延迟收纳(600ms)，期间重新进入则取消，避免来回晃动闪烁
        private readonly System.Windows.Threading.DispatcherTimer _retractDelayTimer =
            new() { Interval = TimeSpan.FromMilliseconds(600) };

        private const string OpenAiLogoBase64 = "iVBORw0KGgoAAAANSUhEUgAAADwAAAA8CAQAAACQ9RH5AAAACXBIWXMAAAsTAAALEwEAmpwYAAAE8WlUWHRYTUw6Y29tLmFkb2JlLnhtcAAAAAAAPD94cGFja2V0IGJlZ2luPSLvu78iIGlkPSJXNU0wTXBDZWhpSHpyZVN6TlRjemtjOWQiPz4gPHg6eG1wbWV0YSB4bWxuczp4PSJhZG9iZTpuczptZXRhLyIgeDp4bXB0az0iQWRvYmUgWE1QIENvcmUgMTAuMC1jMDAwIDI1LkcuZWY3MmU0ZSwgMjAyNS8wNi8yNy0xODo1NDowNSAgICAgICAgIj4gPHJkZjpSREYgeG1sbnM6cmRmPSJodHRwOi8vd3d3LnczLm9yZy8xOTk5LzAyLzIyLXJkZi1zeW50YXgtbnMjIj4gPHJkZjpEZXNjcmlwdGlvbiByZGY6YWJvdXQ9IiIgeG1sbnM6eG1wPSJodHRwOi8vbnMuYWRvYmUuY29tL3hhcC8xLjAvIiB4bWxuczpkYz0iaHR0cDovL3B1cmwub3JnL2RjL2VsZW1lbnRzLzEuMS8iIHhtbG5zOnBob3Rvc2hvcD0iaHR0cDovL25zLmFkb2JlLmNvbS9waG90b3Nob3AvMS4wLyIgeG1sbnM6eG1wTU09Imh0dHA6Ly9ucy5hZG9iZS5jb20veGFwLzEuMC9tbS8iIHhtbG5zOnN0RXZ0PSJodHRwOi8vbnMuYWRvYmUuY29tL3hhcC8xLjAvc1R5cGUvUmVzb3VyY2VFdmVudCMiIHhtcDpDcmVhdG9yVG9vbD0iQWRvYmUgUGhvdG9zaG9wIDI3LjMgKFdpbmRvd3MpIiB4bXA6Q3JlYXRlRGF0ZT0iMjAyNi0wOC0wNlQxNDozMDowNCswODowMCIgeG1wOk1vZGlmeURhdGU9IjIwMjYtMDgtMDZUMTQ6MzA6MjIrMDg6MDAiIHhtcDpNZXRhZGF0YURhdGU9IjIwMjYtMDgtMDZUMTQ6MzA6MjIrMDg6MDAiIGRjOmZvcm1hdD0iaW1hZ2UvcG5nIiBwaG90b3Nob3A6Q29sb3JNb2RlPSIxIiB4bXBNTTpJbnN0YW5jZUlEPSJ4bXAuaWlkOmM4YTYzNTQ4LTJlYmUtMWQ0OC1hNWEzLWRiMTEzNzI3NmUwNCIgeG1wTU06RG9jdW1lbnRJ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";
        private static System.Windows.Media.Imaging.BitmapImage? _rawOpenAiBitmap = null;
        private static readonly System.Collections.Generic.Dictionary<Color, ImageSource> _tintedOpenAiCache = new();

        private static ImageSource? GetOpenAiBitmapImage(Color tintColor)
        {
            if (_tintedOpenAiCache.TryGetValue(tintColor, out var cached)) return cached;

            try
            {
                if (_rawOpenAiBitmap == null)
                {
                    byte[] bytes = Convert.FromBase64String(OpenAiLogoBase64);
                    using var ms = new System.IO.MemoryStream(bytes);
                    var bitmap = new System.Windows.Media.Imaging.BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bitmap.StreamSource = ms;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _rawOpenAiBitmap = bitmap;
                }

                ImageSource tinted = ColorizeBitmap(_rawOpenAiBitmap, tintColor);
                _tintedOpenAiCache[tintColor] = tinted;
                return tinted;
            }
            catch
            {
                return _rawOpenAiBitmap;
            }
        }

        private static ImageSource ColorizeBitmap(System.Windows.Media.Imaging.BitmapImage original, Color tintColor)
        {
            try
            {
                int width = original.PixelWidth;
                int height = original.PixelHeight;
                int stride = width * 4;
                byte[] pixels = new byte[height * stride];

                var formatConverted = new System.Windows.Media.Imaging.FormatConvertedBitmap(original, PixelFormats.Bgra32, null, 0);
                formatConverted.CopyPixels(pixels, stride, 0);

                for (int i = 0; i < pixels.Length; i += 4)
                {
                    byte alpha = pixels[i + 3];
                    if (alpha > 0)
                    {
                        // 用 tintColor 的 RGB 替换原图 RGB，保留原始 Alpha 通道
                        pixels[i] = tintColor.B;     // Blue
                        pixels[i + 1] = tintColor.G; // Green
                        pixels[i + 2] = tintColor.R; // Red
                    }
                }

                var writeable = new System.Windows.Media.Imaging.WriteableBitmap(width, height, original.DpiX, original.DpiY, PixelFormats.Bgra32, null);
                writeable.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
                writeable.Freeze();
                return writeable;
            }
            catch
            {
                return original;
            }
        }

        public void SetCustomColor(string hexColor)
        {
            if (string.IsNullOrWhiteSpace(hexColor)) return;
            _customColorHex = hexColor;
            _effectiveColorHex = GetReadableForegroundHex(_customColorHex, _bgHex);
        }

        private static Color ParseColorOrDefault(string value, string fallback)
        {
            try { return (Color)ColorConverter.ConvertFromString(value); }
            catch { return (Color)ColorConverter.ConvertFromString(fallback); }
        }

        private static double RelativeLuminance(Color color)
        {
            static double Linearize(byte channel)
            {
                double value = channel / 255.0;
                return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
            }

            return 0.2126 * Linearize(color.R) + 0.7152 * Linearize(color.G) + 0.0722 * Linearize(color.B);
        }

        private static double ContrastRatio(Color first, Color second)
        {
            double lighter = Math.Max(RelativeLuminance(first), RelativeLuminance(second));
            double darker = Math.Min(RelativeLuminance(first), RelativeLuminance(second));
            return (lighter + 0.05) / (darker + 0.05);
        }

        private static string GetReadableForegroundHex(string requestedHex, string backgroundHex)
        {
            // 强调色必须尊重用户选择，不能因为背景对比度不足就把黑色替换成白色。
            // 背景透明度/边框负责提供层次，颜色本身始终保持原值。
            return requestedHex;
        }

        private static SolidColorBrush CreateBrush(Color color) => new(color);

        private static Color WithAlpha(Color color, byte alpha) =>
            Color.FromArgb(alpha, color.R, color.G, color.B);

        private void ApplyCustomColorUI()
        {
            try
            {
                _effectiveColorHex = _modeType == "floating"
                    ? GetReadableForegroundHex(_customColorHex, _bgHex)
                    : _customColorHex;

                Color accent = ParseColorOrDefault(_effectiveColorHex, "#FFFFFF");
                var accentBrush = CreateBrush(accent);
                var secondaryBrush = CreateBrush(WithAlpha(accent, 196));
                var trackBrush = CreateBrush(WithAlpha(accent, 176));
                var subtleBrush = CreateBrush(WithAlpha(accent, 28));
                var dividerBrush = CreateBrush(WithAlpha(accent, 48));

                PathMainLogo.Fill = accentBrush;
                PathRowIcon1.Fill = accentBrush;
                PathRowIcon2.Fill = accentBrush;

                // 右侧切换账号箭头同步使用强调色
                try
                {
                    if (BtnNextAccount.Template?.FindName("pathBtnIcon", BtnNextAccount) is Path pathBtnIcon)
                    {
                        pathBtnIcon.Fill = accentBrush;
                    }
                }
                catch { }

                var openAiImg = GetOpenAiBitmapImage(accent);
                if (openAiImg != null)
                {
                    if (ImgMainLogo.Visibility == Visibility.Visible) ImgMainLogo.Source = openAiImg;
                    if (ImgRowIcon1.Visibility == Visibility.Visible) ImgRowIcon1.Source = openAiImg;
                    if (ImgRowIcon2.Visibility == Visibility.Visible) ImgRowIcon2.Source = openAiImg;
                }

                TxtPlatform.Foreground = accentBrush;
                TxtEmail.Foreground = secondaryBrush;
                TxtQuotaBadge.Foreground = accentBrush;
                TxtRefreshing.Foreground = accentBrush;
                TxtNoAccount.Foreground = secondaryBrush;

                LogoBadge.Background = subtleBrush;
                LogoBadge.BorderBrush = CreateBrush(WithAlpha(accent, 70));
                QuotaBadge.Background = subtleBrush;
                HeaderDivider.Background = dividerBrush;

                ApplyBarPalette(TxtLabel1, TrackBg1, BarFill1, TxtVal1, accentBrush, trackBrush, subtleBrush);
                ApplyBarPalette(TxtLabel2, TrackBg2, BarFill2, TxtVal2, accentBrush, trackBrush, subtleBrush);
                ApplyBarPalette(TxtLabel3, TrackBg3, BarFill3, TxtVal3, accentBrush, trackBrush, subtleBrush);
                ApplyBarPalette(TxtLabel4, TrackBg4, BarFill4, TxtVal4, accentBrush, trackBrush, subtleBrush);

                if (_modeType == "floating")
                {
                    BorderFloatingCard.BorderBrush = CreateBrush(WithAlpha(accent, 112));
                }
            }
            catch { }
        }

        private static void ApplyBarPalette(
            TextBlock label,
            Border track,
            Border fill,
            TextBlock value,
            Brush accentBrush,
            Brush trackBrush,
            Brush subtleBrush)
        {
            label.Foreground = accentBrush;
            value.Foreground = accentBrush;
            fill.Background = accentBrush;
            track.Background = subtleBrush;
            track.BorderBrush = trackBrush;
            track.BorderThickness = new Thickness(1);
        }

        public void PreviewBackground(string bgHex, double opacity)
        {
            if (_modeType != "floating") return;

            Dispatcher.Invoke(() =>
            {
                _bgHex = string.IsNullOrWhiteSpace(bgHex) ? "#161B22" : bgHex;
                _bgOpacity = Math.Clamp(opacity, 0.0, 1.0);
                ApplyFloatingBackground();
                ApplyCustomColorUI();
            });
        }

        private void ApplyFloatingBackground()
        {
            if (_hideCardBg)
            {
                // 隐藏圆角矩形：背景透明 + 无边框，仅保留配额内容（交互不受影响）
                BorderFloatingCard.Background = Brushes.Transparent;
                BorderFloatingCard.BorderThickness = new Thickness(0);
                return;
            }
            Color background = ParseColorOrDefault(_bgHex, "#161B22");
            byte alpha = (byte)Math.Round(Math.Clamp(_bgOpacity, 0.0, 1.0) * 255);
            BorderFloatingCard.Background = CreateBrush(Color.FromArgb(alpha, background.R, background.G, background.B));
            BorderFloatingCard.Opacity = 1.0;
            BorderFloatingCard.BorderThickness = new Thickness(1);
        }

        public void ApplyModeAndStyles(string modeType, string colorHex, string bgHex, double opacity, bool isTransparent, double left = -1, double top = -1, bool edgeDockEnabled = true, double width = 0, double height = 0, bool hideCardBg = false)
        {
            _modeType = modeType;
            _customColorHex = string.IsNullOrWhiteSpace(colorHex) ? "#FFFFFF" : colorHex;
            _bgHex = string.IsNullOrWhiteSpace(bgHex) ? "#161B22" : bgHex;
            _bgOpacity = Math.Clamp(opacity, 0.0, 1.0);
            _isTransparent = isTransparent;
            _edgeDockEnabled = edgeDockEnabled;
            _hideCardBg = hideCardBg;

            Dispatcher.Invoke(() =>
            {
                    // 设置变更前先重置收纳/动画状态：若此刻正在运行收纳动画而随后要重新定位窗口，
                    // 残留动画会继续滑向旧目标导致窗口跳动。StopEdgeTimer 同时清除 _isEdgeAnimating。
                    StopEdgeTimer();

                    ApplyCustomColorUI();

                    IntPtr thisHwnd = new WindowInteropHelper(this).Handle;
                    if (thisHwnd != IntPtr.Zero)
                    {
                        ShowWindow(thisHwnd, SW_SHOW);
                    }

                    if (_modeType == "floating")
                    {
                        ConfigureFloatingLayout();

                        // 恢复用户自定义的悬浮尺寸（0 表示未自定义，使用默认值）
                        if (width > 0 && height > 0)
                        {
                            double maxW = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
                            double maxH = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
                            Width = Math.Clamp(width, MinWidth, maxW);
                            Height = Math.Clamp(height, MinHeight, maxH);
                        }

                        DetachFromTaskbar(left, top);

                        ApplyFloatingBackground();
                        ApplyCustomColorUI();

                        SetMouseClickThrough(_isTransparent);
                        HeaderPanel.Cursor = !_isTransparent ? Cursors.SizeAll : Cursors.Arrow;
                        PanelBars.Cursor = _isTransparent ? Cursors.Arrow : Cursors.SizeAll;

                        MenuTransparent.IsEnabled = true;
                        MenuTransparent.Header = _isTransparent ? "停用鼠标穿透" : "启用鼠标穿透";
                    }
                    else
                    {
                        SetMouseClickThrough(false);
                        ConfigureTaskbarLayout();
                        ApplyCustomColorUI();
                        EmbedIntoTaskbar();

                        BorderFloatingCard.Opacity = 1.0;
                        BorderFloatingCard.Background = Brushes.Transparent;
                        BorderFloatingCard.BorderThickness = new Thickness(0);
                        HeaderPanel.Cursor = Cursors.Arrow;
                        PanelBars.Cursor = Cursors.Hand;

                        MenuTransparent.IsEnabled = false;
                        MenuTransparent.Header = "鼠标穿透（仅桌面悬浮）";
                    }

                    EvaluateDockOnPosition();
                });
        }

        public void EnsureTopmost()
        {
            if (_modeType != "floating" || !IsVisible) return;
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Topmost = true;
                    SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                }
            }
            catch { }
        }

        private void ConfigureFloatingLayout()
        {
            Width = TaskbarWidth;
            Height = TaskbarPreferredHeight;
            BorderFloatingCard.Padding = new Thickness(6, 2, 6, 2);
            BorderFloatingCard.CornerRadius = new CornerRadius(6);
            ApplyContentScale(1.0);
        }

        private void ConfigureTaskbarLayout()
        {
            Width = TaskbarWidth;
            Height = TaskbarPreferredHeight;
            BorderFloatingCard.Padding = new Thickness(6, 2, 6, 2);
            BorderFloatingCard.CornerRadius = new CornerRadius(0);
            ApplyContentScale(1.0);
        }

        // ---- 布局级等比缩放（替代原 Viewbox 变换缩放）----
        // 原理：窗口尺寸变化时按比例重算 FontSize / 图标 / 间距 / 进度条等真实布局参数，
        // 文字始终以真实字体大小渲染，不经过 ScaleTransform 重采样，保证像素级清晰。
        private const double BaseContentWidth = 252;
        private const double BaseContentHeight = 36;
        private const double BaseCardPaddingH = 6;
        private const double BaseCardPaddingV = 2;
        private const double BaseIconColWidth = 17;
        private const double BaseIconSize = 13;
        private const double BaseFontSize = 10;
        private const double BaseBigFontSize = 12;
        private const double BaseTrackHeight = 7;
        private const double BaseTrackCorner = 3.5;
        private const double BaseTrackBorder = 1;
        private const double BaseTrackInner = 1.5;
        private const double BaseBarCorner = 2;
        private const double BaseItemGap = 8;
        private const double BaseLabelGap = 3;
        private const double BaseRowGap = 1;
        private const double BaseNextBtnW = 16;
        private const double BaseNextBtnH = 22;
        private const double BaseNextIconW = 6;
        private const double BaseNextIconH = 9;
        private const double BaseNextBtnCorner = 3;
        private const double BaseNextBtnMargin = 4;

        private void ApplyContentScale(double scale)
        {
            if (ContentGrid == null) return;
            scale = Math.Max(0.3, scale);
            double icon = BaseIconSize * scale;
            double labelGap = BaseLabelGap * scale;

            ColIcon1.Width = new GridLength(BaseIconColWidth * scale);
            ColIcon2.Width = new GridLength(BaseIconColWidth * scale);

            VboxIcon1.Width = icon; VboxIcon1.Height = icon;
            VboxIcon2.Width = icon; VboxIcon2.Height = icon;
            ImgRowIcon1.Width = icon; ImgRowIcon1.Height = icon;
            ImgRowIcon2.Width = icon; ImgRowIcon2.Height = icon;

            foreach (var tb in new[] { TxtLabel1, TxtLabel2, TxtLabel3, TxtLabel4,
                                       TxtVal1, TxtVal2, TxtVal3, TxtVal4 })
                tb.FontSize = BaseFontSize * scale;
            TxtRefreshing.FontSize = BaseBigFontSize * scale;
            TxtNoAccount.FontSize = BaseBigFontSize * scale;

            // Track 宽度不再固定：Grid 弹性列自动填满剩余空间（消除窗口宽于内容时的左右空白）
            foreach (var t in new[] { Track1, Track2, Track3, Track4 })
            {
                t.Height = BaseTrackHeight * scale;
            }
            var trackCorner = new CornerRadius(BaseTrackCorner * scale);
            var trackBorder = new Thickness(BaseTrackBorder * scale);
            var trackInner = new Thickness(BaseTrackInner * scale);
            var barCorner = new CornerRadius(BaseBarCorner * scale);
            foreach (var tb in new[] { TrackBg1, TrackBg2, TrackBg3, TrackBg4 })
            {
                tb.CornerRadius = trackCorner;
                tb.BorderThickness = trackBorder;
            }
            foreach (var g in new[] { TrackInner1, TrackInner2, TrackInner3, TrackInner4 })
                g.Margin = trackInner;
            foreach (var b in new[] { BarFill1, BarFill2, BarFill3, BarFill4 })
                b.CornerRadius = barCorner;

            var itemGap = new Thickness(0, 0, BaseItemGap * scale, 0);
            var labelRight = new Thickness(0, 0, labelGap, 0);
            var labelLeft = new Thickness(labelGap, 0, 0, 0);
            foreach (var s in new[] { BarItem1, BarItem2, BarItem3, BarItem4 }) s.Margin = itemGap;
            foreach (var tb in new[] { TxtLabel1, TxtLabel2, TxtLabel3, TxtLabel4 }) tb.Margin = labelRight;
            foreach (var tb in new[] { TxtVal1, TxtVal2, TxtVal3, TxtVal4 }) tb.Margin = labelLeft;
            RowBars1.Margin = new Thickness(0, 0, 0, BaseRowGap * scale);

            BtnNextAccount.Width = BaseNextBtnW * scale;
            BtnNextAccount.Height = BaseNextBtnH * scale;
            BtnNextAccount.Margin = new Thickness(BaseNextBtnMargin * scale, 0, 0, 0);
            if (BtnNextAccount.Template != null)
            {
                var iconPath = BtnNextAccount.Template.FindName("pathBtnIcon", BtnNextAccount) as System.Windows.Shapes.Path;
                if (iconPath != null) { iconPath.Width = BaseNextIconW * scale; iconPath.Height = BaseNextIconH * scale; }
                var btnBorder = BtnNextAccount.Template.FindName("border", BtnNextAccount) as Border;
                if (btnBorder != null) btnBorder.CornerRadius = new CornerRadius(BaseNextBtnCorner * scale);
            }
        }

        public void SetMouseClickThrough(bool isTransparent)
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                // 不再设置/清除 WS_EX_TRANSPARENT：整窗穿透改为按区域命中控制
                // （WndProc 的 WM_NCHITTEST：切换账号按钮区域返回 HTCLIENT 保持可交互，
                // 其余区域返回 HTTRANSPARENT 穿透到下层窗口）。
                // 此方法只负责穿透模式下右键钩子的启停，以及穿透状态窗口属性标记（供外部识别）。
                if (isTransparent && _modeType == "floating")
                {
                    SetProp(hwnd, PropTransparent, (IntPtr)1);
                    InstallRightClickHook();
                }
                else
                {
                    RemoveProp(hwnd, PropTransparent);
                    RemoveRightClickHook();
                }
            }
            catch (Exception ex)
            {
                RemoveRightClickHook();
                _mainWin.LogMiniWidgetDiagnostic($"穿透设置失败: {ex.Message}");
            }
        }

        // 轻量穿透切换：只切换 WS_EX_TRANSPARENT 与光标/菜单状态，不重定位、不重建背景。
        // 穿透切换若复用 ApplyModeAndStyles 的全套流程，会对分层窗口执行
        // ShowWindow/SetWindowPos(FRAMECHANGED)/背景重建等冗余操作，导致窗口闪烁。
        public void SetTransparentMode(bool isTransparent)
        {
            _isTransparent = isTransparent;
            Dispatcher.Invoke(() =>
            {
                if (_modeType != "floating") return;
                SetMouseClickThrough(_isTransparent);
                HeaderPanel.Cursor = !_isTransparent ? Cursors.SizeAll : Cursors.Arrow;
                PanelBars.Cursor = _isTransparent ? Cursors.Arrow : Cursors.SizeAll;
                MenuTransparent.Header = _isTransparent ? "停用鼠标穿透" : "启用鼠标穿透";
            });
        }

        private void InstallRightClickHook()
        {
            if (_rightClickHook != IntPtr.Zero) return;

            _rightClickHookProc = RightClickHookCallback;
            _rightClickHook = SetWindowsHookEx(WH_MOUSE_LL, _rightClickHookProc, IntPtr.Zero, 0);
            if (_rightClickHook == IntPtr.Zero)
            {
                _rightClickHookProc = null;
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法启用右键菜单监听");
            }

            _mainWin.LogMiniWidgetDiagnostic($"右键监听已启用: 0x{_rightClickHook.ToInt64():X}");
        }

        private void RemoveRightClickHook()
        {
            if (_rightClickHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_rightClickHook);
                _rightClickHook = IntPtr.Zero;
            }
            _rightClickHookProc = null;
        }

        private IntPtr RightClickHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            int message = wParam.ToInt32();
            if (nCode >= 0 && (message == WM_RBUTTONDOWN || message == WM_RBUTTONUP) &&
                _isTransparent && _modeType == "floating" && IsVisible)
            {
                MSLLHOOKSTRUCT mouseInfo = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect) &&
                    mouseInfo.Pt.X >= rect.Left && mouseInfo.Pt.X < rect.Right &&
                    mouseInfo.Pt.Y >= rect.Top && mouseInfo.Pt.Y < rect.Bottom)
                {
                    if (message == WM_RBUTTONDOWN)
                    {
                        _mainWin.LogMiniWidgetDiagnostic("已接收贴贴右键");
                        Dispatcher.BeginInvoke(() =>
                        {
                            if (ContextMenu == null) return;
                            ContextMenu.PlacementTarget = this;
                            ContextMenu.Placement = PlacementMode.MousePoint;
                            ContextMenu.IsOpen = true;
                            _mainWin.LogMiniWidgetDiagnostic($"右键菜单状态: {ContextMenu.IsOpen}");
                        });
                    }
                    return (IntPtr)1;
                }
            }

            return CallNextHookEx(_rightClickHook, nCode, wParam, lParam);
        }

        public void DetachFromTaskbar(double left = -1, double top = -1)
        {
            IntPtr thisHwnd = new WindowInteropHelper(this).Handle;
            if (thisHwnd == IntPtr.Zero) return;

            SetParent(thisHwnd, IntPtr.Zero);

            int style = GetWindowLong(thisHwnd, GWL_STYLE);
            style = (style & ~WS_CHILD) | WS_POPUP;
            SetWindowLong(thisHwnd, GWL_STYLE, style);

            Topmost = true;

            // 边界判定使用整个虚拟屏幕（含副屏），保证副屏上的位置也能被正确恢复；
            // -1 表示从未保存过位置，回退到主屏右下角默认位置
            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            double targetLeft = left;
            double targetTop = top;

            if (double.IsNaN(targetLeft) || double.IsInfinity(targetLeft) ||
                double.IsNaN(targetTop) || double.IsInfinity(targetTop))
            {
                // 从未保存过有效位置：回退到主屏右下角默认位置
                targetLeft = Math.Max(vsLeft, vsRight - Width - 24);
                targetTop = Math.Max(vsTop, vsBottom - Height - 24);
            }

            // 位置收敛只做“防丢失”保底：允许窗口部分甚至完全停在屏幕外（用户主动藏进屏幕边缘，
            // 例如藏进顶部后开启鼠标穿透，位置必须保持原地），但至少露出 24px 把手，
            // 避免窗口被完全拖出虚拟屏幕后无法用鼠标找回。
            const double peek = 24;
            double minLeft = vsLeft - Width + peek;
            double maxLeft = Math.Max(minLeft, vsRight - peek);
            double minTop = vsTop - Height + peek;
            double maxTop = Math.Max(minTop, vsBottom - peek);
            Left = Math.Clamp(targetLeft, minLeft, maxLeft);
            Top = Math.Clamp(targetTop, minTop, maxTop);

            double dpiScaleX = 1.0;
            double dpiScaleY = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget != null)
            {
                dpiScaleX = source.CompositionTarget.TransformToDevice.M11;
                dpiScaleY = source.CompositionTarget.TransformToDevice.M22;
            }

            if (!SetWindowPos(thisHwnd, (IntPtr)(-1),
                    (int)Math.Round(Left * dpiScaleX), (int)Math.Round(Top * dpiScaleY),
                    (int)Math.Round(Width * dpiScaleX), (int)Math.Round(Height * dpiScaleY),
                    SWP_SHOWWINDOW | SWP_FRAMECHANGED))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法显示悬浮贴贴窗口");
            }
            EnsureTopmost();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
                }
            }
            catch { }
        }

        // ===== Win+D / 显示桌面 / 任务栏点击等系统隐藏防护（仅桌面悬浮模式） =====
        // Windows 的“显示桌面”(Win+D / 任务栏右下角竖条) 会让顶层窗口最小化或隐藏，常见路径：
        //   1. SetWindowPos(SWP_HIDEWINDOW) 直接隐藏（Win10/11 ToggleDesktop 主路径，任务栏按钮消失）
        //   2. ShowWindow(SW_MINIMIZE) 直接最小化
        //   3. WM_SYSCOMMAND + SC_MINIMIZE（Aero Shake 甩动、任务栏右键最小化、Alt+Space 最小化）
        //   4. WM_SHOWWINDOW(0)（父窗口最小化 / 他窗最大化引发的隐藏）
        // 这里在窗口过程全链路拦截 + 最小化兜底恢复，确保悬浮贴贴始终停留在桌面上。
        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 任务栏贴是 Explorer 子窗口，不受显示桌面影响；主动隐藏/关闭时放行
            if (_modeType != "floating" || _allowSystemHide) return IntPtr.Zero;

            switch (msg)
            {
                case WM_NCHITTEST:
                    // 穿透模式下的按区域命中：切换账号按钮区域返回 HTCLIENT（按钮可点击），
                    // 其余区域返回 HTTRANSPARENT 穿透到下层窗口（等价于 WS_EX_TRANSPARENT 整窗穿透）。
                    if (_isTransparent && _modeType == "floating")
                    {
                        int hitX = unchecked((short)(lParam.ToInt64() & 0xFFFF));
                        int hitY = unchecked((short)((lParam.ToInt64() >> 16) & 0xFFFF));
                        try
                        {
                            if (BtnNextAccount != null && BtnNextAccount.IsVisible &&
                                BtnNextAccount.ActualWidth > 0)
                            {
                                // 按钮屏幕矩形（物理像素）：窗口内坐标 -> 屏幕坐标 -> 乘 DPI 缩放
                                Point btnScreen = PointToScreen(BtnNextAccount.TranslatePoint(new Point(0, 0), this));
                                var src = PresentationSource.FromVisual(this);
                                double dpiX = src?.CompositionTarget.TransformToDevice.M11 ?? 1.0;
                                double dpiY = src?.CompositionTarget.TransformToDevice.M22 ?? 1.0;
                                double btnRight = btnScreen.X + BtnNextAccount.ActualWidth * dpiX;
                                double btnBottom = btnScreen.Y + BtnNextAccount.ActualHeight * dpiY;
                                if (hitX >= btnScreen.X && hitX <= btnRight &&
                                    hitY >= btnScreen.Y && hitY <= btnBottom)
                                {
                                    // 按钮区域：显式返回 HTCLIENT，使鼠标消息派发给本窗口（WPF 收到后
                                    // 走正常输入命中测试，按钮保持可点击）；不能走 WPF 默认处理——
                                    // 分层窗口默认 WM_NCHITTEST 返回 HTTRANSPARENT，会导致按钮区也穿透。
                                    handled = true;
                                    return (IntPtr)1; // HTCLIENT
                                }
                            }
                        }
                        catch { }
                        handled = true;
                        return (IntPtr)(-2); // HTTRANSPARENT：穿透到下层窗口
                    }
                    break;

                case WM_SYSCOMMAND:
                    if ((wParam.ToInt32() & 0xFFF0) == SC_MINIMIZE)
                    {
                        handled = true;
                        _mainWin.LogMiniWidgetDiagnostic("拦截系统最小化指令 SC_MINIMIZE");
                        RestoreAfterSystemHideAttempt();
                    }
                    break;

                case WM_SHOWWINDOW:
                    // wParam==0 表示窗口被要求隐藏；仅拦父窗口关闭/他窗最大化等系统隐藏路径，
                    // 自身 OnClosing 的 ShowWindow(SW_HIDE)（lParam==0）交由 _allowSystemHide 放行
                    if (wParam.ToInt32() == 0)
                    {
                        int reason = lParam.ToInt32();
                        if (reason == SW_PARENTCLOSING || reason == SW_OTHERZOOM)
                        {
                            handled = true;
                            _mainWin.LogMiniWidgetDiagnostic($"拦截系统隐藏指令 WM_SHOWWINDOW (reason={reason})");
                            RestoreAfterSystemHideAttempt();
                        }
                    }
                    break;

                case WM_WINDOWPOSCHANGING:
                    // Win10/11 的“显示桌面”主要通过 SWP_HIDEWINDOW 隐藏窗口：清除标志使其保持可见
                    {
                        var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
                        if ((wp.flags & SWP_HIDEWINDOW) != 0)
                        {
                            wp.flags &= ~SWP_HIDEWINDOW;
                            Marshal.StructureToPtr(wp, lParam, false);
                            _mainWin.LogMiniWidgetDiagnostic("拦截系统隐藏指令 SWP_HIDEWINDOW");
                        }
                    }
                    break;

                case WM_SIZE:
                    // 兜底：若上述路径均未拦截成功导致窗口被最小化，立即恢复显示（不抢焦点）。
                    // 延迟到消息处理结束后再执行，避免在窗口过程内重入 ShowWindow/SetWindowPos；
                    // 同时同步 WPF WindowState，防止内部状态机停留在 Minimized 导致渲染异常。
                    if (wParam.ToInt32() == SIZE_MINIMIZED)
                    {
                        _mainWin.LogMiniWidgetDiagnostic("检测到窗口被最小化，立即恢复悬浮显示");
                        try
                        {
                            Dispatcher.BeginInvoke(new Action(() =>
                            {
                                IntPtr h2 = new WindowInteropHelper(this).Handle;
                                if (h2 == IntPtr.Zero) return;
                                ShowWindow(h2, SW_SHOWNOACTIVATE);
                                SetWindowPos(h2, (IntPtr)(-1), 0, 0, 0, 0,
                                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
                                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                            }));
                        }
                        catch { }
                    }
                    break;
            }
            return IntPtr.Zero;
        }

        private void RestoreAfterSystemHideAttempt()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                ShowWindow(hwnd, SW_SHOWNOACTIVATE);
                SetWindowPos(hwnd, (IntPtr)(-1), 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        public MiniWidgetWindow(MainWindow mainWin)
        {
            InitializeComponent();
            _mainWin = mainWin;
            _slideTimer.Tick += SlideTimer_Tick;
            _edgeTimer.Tick += EdgeTimer_Tick;
            _retractDelayTimer.Tick += RetractDelayTimer_Tick;
            // 窗口尺寸变化（缩放拖动/收纳动画）时按比例重算布局参数，文字保持真实渲染清晰
            this.SizeChanged += (s, e) =>
            {
                if (ContentGrid == null) return;
                double scale = Math.Min(
                    (ActualWidth - BaseCardPaddingH * 2) / BaseContentWidth,
                    (ActualHeight - BaseCardPaddingV * 2) / BaseContentHeight);
                ApplyContentScale(Math.Max(0.3, scale));
            };
            this.Activated += (s, e) => EnsureTopmost();
            this.Deactivated += (s, e) => EnsureTopmost();
            this.Loaded += (s, e) =>
            {
                HideFromAltTab();
                if (_modeType == "floating")
                {
                    // 传当前坐标重分离：只做“从任务栏解绑”，绝不重置用户已保存的位置
                    DetachFromTaskbar(this.Left, this.Top);
                }
                else
                {
                    EmbedIntoTaskbar();
                }
            };

            this.LocationChanged += (s, e) =>
            {
                // 贴边收纳状态下窗口大部分在屏幕外，任何微小移动都可能把屏幕外坐标写入设置，
                // 因此收纳期间不保存；展开/自由态才记录用户位置。
                if (_modeType == "floating" && !_isTransparent && this.IsVisible && !_isEdgeAnimating && _dockEdge == "none")
                {
                    _mainWin.SaveMiniWidgetLocation(this.Left, this.Top);
                }
            };
        }

        private void HideFromAltTab()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;

                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                exStyle |= WS_EX_TOOLWINDOW;    // 开启 ToolWindow 扩展属性 (AltTab 列表中彻底隐藏)
                exStyle &= ~WS_EX_APPWINDOW;   // 移除 AppWindow 扩展属性
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
            }
            catch { }
        }

        private bool _allowRealClose = false;

        public void CloseWindow()
        {
            _allowRealClose = true;
            _allowSystemHide = true; // 销毁流程放行，避免 WndProc 拦截关闭过程中的隐藏
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    SetMouseClickThrough(false);
                    SetParent(hwnd, IntPtr.Zero);
                    int style = GetWindowLong(hwnd, GWL_STYLE);
                    SetWindowLong(hwnd, GWL_STYLE, (style & ~WS_CHILD) | WS_POPUP);
                }
                Close();
            }
            catch { }
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_allowRealClose)
            {
                e.Cancel = true;
                _allowSystemHide = true; // 放行自身的 Hide()，防止 SWP_HIDEWINDOW 拦截导致无法收起
                this.Hide();
                _allowSystemHide = false;
                return;
            }

            RemoveRightClickHook();
            StopEdgeTimer();
            _slideTimer.Stop();
            base.OnClosing(e);
        }

        public void EmbedIntoTaskbar()
        {
            try
            {
                IntPtr thisHwnd = new WindowInteropHelper(this).Handle;
                if (thisHwnd == IntPtr.Zero) return;

                IntPtr taskbarHwnd = FindWindow("Shell_TrayWnd", null);
                if (taskbarHwnd != IntPtr.Zero && GetWindowRect(taskbarHwnd, out RECT taskbarRect))
                {
                    int taskbarWidth = taskbarRect.Right - taskbarRect.Left;
                    int taskbarHeight = taskbarRect.Bottom - taskbarRect.Top;

                    double dpiScaleX = 1.0;
                    double dpiScaleY = 1.0;
                    var source = PresentationSource.FromVisual(this);
                    if (source?.CompositionTarget != null)
                    {
                        dpiScaleX = source.CompositionTarget.TransformFromDevice.M11;
                        dpiScaleY = source.CompositionTarget.TransformFromDevice.M22;
                    }

                    int widgetWidthPx = Math.Min((int)Math.Round(Width / dpiScaleX), Math.Max(160, taskbarWidth / 3));
                    int preferredHeightPx = (int)Math.Round(TaskbarPreferredHeight / dpiScaleY);
                    int widgetHeightPx = Math.Max(24, Math.Min(preferredHeightPx, Math.Max(24, taskbarHeight - 4)));
                    Height = widgetHeightPx * dpiScaleY;

                    SetParent(thisHwnd, taskbarHwnd);

                    int style = GetWindowLong(thisHwnd, GWL_STYLE);
                    style = (style & ~WS_POPUP) | WS_CHILD;
                    SetWindowLong(thisHwnd, GWL_STYLE, style);

                    IntPtr trayNotifyHwnd = FindWindowEx(taskbarHwnd, IntPtr.Zero, "TrayNotifyWnd", null);
                    int clientX = taskbarWidth - widgetWidthPx - 180;

                    if (trayNotifyHwnd != IntPtr.Zero && GetWindowRect(trayNotifyHwnd, out RECT trayNotifyRect))
                    {
                        int trayLeftRelative = trayNotifyRect.Left - taskbarRect.Left;
                        if (trayLeftRelative > widgetWidthPx + 20)
                        {
                            clientX = trayLeftRelative - widgetWidthPx - 14;
                        }
                    }

                    int clientY = (taskbarHeight - widgetHeightPx) / 2;

                    if (!SetWindowPos(thisHwnd, IntPtr.Zero, clientX, clientY, widgetWidthPx, widgetHeightPx,
                            SWP_NOZORDER | SWP_SHOWWINDOW | SWP_FRAMECHANGED))
                    {
                        throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "无法定位任务栏贴贴");
                    }
                    return;
                }

                var workArea = SystemParameters.WorkArea;
                this.Left = workArea.Right - this.Width - 180;
                this.Top = workArea.Bottom - this.Height - 2;
            }
            catch
            {
                var workArea = SystemParameters.WorkArea;
                this.Left = workArea.Right - this.Width - 180;
                this.Top = workArea.Bottom - this.Height - 2;
            }
        }

        public void ShowNoAccountState()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    PanelBars.Visibility = Visibility.Collapsed;
                    PanelRefreshing.Visibility = Visibility.Collapsed;
                    PanelNoAccount.Visibility = Visibility.Visible;
                    this.ToolTip = "未检测到凭证数据 | 请打开主窗口导入账号 JSON 凭证";
                });
            }
            catch { }
        }

        public void UpdateData(
            string logoText,
            string logoBgHex,
            string platformName,
            string email,
            ((int percent, string time, DateTime? resetUtc) g5h, (int percent, string time, DateTime? resetUtc) gWeek, (int percent, string time, DateTime? resetUtc) c5h, (int percent, string time, DateTime? resetUtc) cWeek) quota,
            string tooltipDetails)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    PanelNoAccount.Visibility = Visibility.Collapsed;
                    PanelRefreshing.Visibility = Visibility.Collapsed;
                    PanelBars.Visibility = Visibility.Visible;

                    TxtLogo.Text = logoText;
                    TxtPlatform.Text = platformName;
                    TxtEmail.Text = email;

                    var geomGoogle = (Geometry)FindResource("IconGoogle");
                    var geomOpenAI = (Geometry)FindResource("IconOpenAI");
                    Color accentColor = ParseColorOrDefault(_effectiveColorHex, "#FFFFFF");
                    ImageSource? openAiImg = GetOpenAiBitmapImage(accentColor);

                    // 专一化：所有账号均为 Antigravity/Gemini 凭证，左侧 Badge 恒展示 Google 黑白透明 Icon
                    PathMainLogo.Visibility = Visibility.Visible;
                    ImgMainLogo.Visibility = Visibility.Collapsed;
                    PathMainLogo.Data = geomGoogle;

                    // Antigravity (Google): 第1排使用 Google 黑白透明 Icon，第2排使用 OpenAI 黑白透明 Icon
                    RowBars2.Visibility = Visibility.Visible;
                    RowBars1.Margin = new Thickness(0, 0, 0, 3);

                    PathRowIcon1.Visibility = Visibility.Visible;
                    ImgRowIcon1.Visibility = Visibility.Collapsed;
                    PathRowIcon1.Data = geomGoogle;
                    SetBarItem(TxtLabel1, ColFill1, ColBg1, BarFill1, TxtVal1, TrackBg1, "5h", quota.g5h.percent);
                    SetBarItem(TxtLabel2, ColFill2, ColBg2, BarFill2, TxtVal2, TrackBg2, "周", quota.gWeek.percent);

                    if (openAiImg != null)
                    {
                        ImgRowIcon2.Source = openAiImg;
                        ImgRowIcon2.Visibility = Visibility.Visible;
                        PathRowIcon2.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        PathRowIcon2.Visibility = Visibility.Visible;
                        ImgRowIcon2.Visibility = Visibility.Collapsed;
                        PathRowIcon2.Data = geomOpenAI;
                    }
                    SetBarItem(TxtLabel3, ColFill3, ColBg3, BarFill3, TxtVal3, TrackBg3, "5h", quota.c5h.percent);
                    SetBarItem(TxtLabel4, ColFill4, ColBg4, BarFill4, TxtVal4, TrackBg4, "周", quota.cWeek.percent);

                    ApplyCustomColorUI();
                    string tipText = $"账号: {email}\n平台: {platformName}\n{tooltipDetails}\n\n顶部拖动位置 | 下方单击切换账号 | 下方双击刷新";
                    if (this.ToolTip is ToolTip existingTip)
                    {
                        existingTip.Content = tipText;
                    }
                    else
                    {
                        this.ToolTip = new ToolTip { Content = tipText };
                    }
                });
            }
            catch { }
        }

        private void SetBarItem(
            TextBlock txtLabel,
            ColumnDefinition colFill,
            ColumnDefinition colBg,
            Border barFill,
            TextBlock txtVal,
            Border trackBg,
            string label,
            int percent)
        {
            txtLabel.Text = label;
            int clampedPercent = Math.Clamp(percent, 0, 100);
            colFill.Width = new GridLength(clampedPercent, GridUnitType.Star);
            colBg.Width = new GridLength(Math.Max(0, 100 - clampedPercent), GridUnitType.Star);
            txtVal.Text = $"{clampedPercent}%";

            try
            {
                Color accent = ParseColorOrDefault(_effectiveColorHex, "#FFFFFF");
                ApplyBarPalette(
                    txtLabel,
                    trackBg,
                    barFill,
                    txtVal,
                    CreateBrush(accent),
                    CreateBrush(WithAlpha(accent, 176)),
                    CreateBrush(WithAlpha(accent, 28)));
            }
            catch
            {
                var white = Colors.White;
                ApplyBarPalette(
                    txtLabel,
                    trackBg,
                    barFill,
                    txtVal,
                    CreateBrush(white),
                    CreateBrush(WithAlpha(white, 176)),
                    CreateBrush(WithAlpha(white, 28)));
            }
        }

        private System.Windows.Threading.DispatcherTimer? _flashTimer;

        public void TriggerSwitchFlashFeedback()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _flashTimer?.Stop();

                    // 1. 设置全控件亮蓝 #4DABF7 高亮点击视觉反馈
                    var flashBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#4DABF7"));
                    var flashSubtle = new SolidColorBrush(Color.FromArgb(80, 0x4D, 0xAB, 0xF7));

                    BarFill1.Background = flashBrush; TxtVal1.Foreground = flashBrush; TrackBg1.BorderBrush = flashBrush; TrackBg1.Background = flashSubtle;
                    BarFill2.Background = flashBrush; TxtVal2.Foreground = flashBrush; TrackBg2.BorderBrush = flashBrush; TrackBg2.Background = flashSubtle;
                    BarFill3.Background = flashBrush; TxtVal3.Foreground = flashBrush; TrackBg3.BorderBrush = flashBrush; TrackBg3.Background = flashSubtle;
                    BarFill4.Background = flashBrush; TxtVal4.Foreground = flashBrush; TrackBg4.BorderBrush = flashBrush; TrackBg4.Background = flashSubtle;

                    BtnNextAccount.Opacity = 0.5;

                    // 2. 260ms 后自动恢复用户当前选定的主题色彩
                    _flashTimer = new System.Windows.Threading.DispatcherTimer
                    {
                        Interval = TimeSpan.FromMilliseconds(260)
                    };
                    _flashTimer.Tick += (s, e) =>
                    {
                        _flashTimer.Stop();
                        RestoreCustomColor();
                    };
                    _flashTimer.Start();
                });
            }
            catch { }
        }

        private void RestoreCustomColor()
        {
            ApplyCustomColorUI();
            BtnNextAccount.Opacity = 1.0;
        }

        public void SetRefreshingState(bool isRefreshing)
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    _isRefreshing = isRefreshing;
                    if (isRefreshing)
                    {
                        PanelRefreshing.Visibility = Visibility.Visible;
                        PanelBars.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        PanelRefreshing.Visibility = Visibility.Collapsed;
                        PanelBars.Visibility = Visibility.Visible;
                    }
                });
            }
            catch { }
        }

        private Point _dragStartPoint;
        private Point _dragStartScreen;
        private double _dragStartWindowLeft, _dragStartWindowTop;
        private bool _isMouseDown = false;

        private void MainContainer_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is DependencyObject dep && IsDescendantOf(dep, BtnNextAccount))
            {
                // 在按下阶段直接切换账号并拦截事件：部分焦点/激活状态下 Button.Click 会丢失（表现为需点击两次），
                // 此处绕过 Click 依赖，确保每次点击都立即生效。
                _mainWin.SwitchNextAccountInMini();
                _mainWin.LogMiniWidgetDiagnostic("点击右箭头切换账号");
                e.Handled = true;
                return;
            }

            if (_modeType == "floating" && !_isTransparent && e.LeftButton == MouseButtonState.Pressed)
            {
                // 四角/四边缩放优先于整体拖动
                var resizeDir = HitTestMiniResize(e.GetPosition(this));
                if (resizeDir != MiniResizeDir.None)
                {
                    StartMiniResize(resizeDir, e);
                    e.Handled = true;
                    return;
                }

                _isMouseDown = true;
                _isDraggingWidget = true;
                StopEdgeTimer();
                _dragStartPoint = e.GetPosition(this);
                _dragStartScreen = PointToScreen(_dragStartPoint);
                _dragStartWindowLeft = Left;
                _dragStartWindowTop = Top;
                // 不再调用 DragMove()：它会进入系统移动循环（WM_NCLBUTTONDOWN + HTCAPTION），
                // 触发 Win11 顶部 Snap 最大化预览，NoResize 窗口拖到屏幕顶外侧被回弹到 Top=0，
                // 无法像截图贴图工具那样藏进顶部屏幕外。
                // 改为自定义拖动：MouseMove 中直接更新 Left/Top，绕过系统移动循环，可停在任何位置。
                MainContainer.CaptureMouse();
            }
        }

        // ===== 手动缩放（四角 + 四边；纯 WPF DIP 计算，锚点固定，兼容高 DPI） =====

        private enum MiniResizeDir { None, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

        private MiniResizeDir _miniResizeDir = MiniResizeDir.None;
        private Point _miniResizeStartScreen;
        private double _miniResizeStartLeft, _miniResizeStartTop, _miniResizeStartWidth, _miniResizeStartHeight;
        private const double MiniResizeEdge = 12; // 边缘缩放命中区宽度（逻辑像素），放大命中区便于拖拽缩小

        private MiniResizeDir HitTestMiniResize(Point p)
        {
            if (_modeType != "floating" || _isTransparent || _isEdgeAnimating) return MiniResizeDir.None;
            if (IsPointInsideButton(p)) return MiniResizeDir.None; // 右侧箭头按钮区域不参与缩放

            double w = ActualWidth, h = ActualHeight;
            bool left = p.X <= MiniResizeEdge;
            bool right = p.X >= w - MiniResizeEdge;
            bool top = p.Y <= MiniResizeEdge;
            bool bottom = p.Y >= h - MiniResizeEdge;
            if (!left && !right && !top && !bottom) return MiniResizeDir.None;

            if (left && top) return MiniResizeDir.TopLeft;
            if (right && top) return MiniResizeDir.TopRight;
            if (left && bottom) return MiniResizeDir.BottomLeft;
            if (right && bottom) return MiniResizeDir.BottomRight;
            if (left) return MiniResizeDir.Left;
            if (right) return MiniResizeDir.Right;
            if (top) return MiniResizeDir.Top;
            if (bottom) return MiniResizeDir.Bottom;
            return MiniResizeDir.None;
        }

        private void StartMiniResize(MiniResizeDir dir, MouseButtonEventArgs e)
        {
            _miniResizeDir = dir;
            _miniResizeStartScreen = PointToScreen(e.GetPosition(this));
            _miniResizeStartLeft = Left;
            _miniResizeStartTop = Top;
            _miniResizeStartWidth = ActualWidth;
            _miniResizeStartHeight = ActualHeight;
            StopEdgeTimer();
            MainContainer.CaptureMouse();
        }

        private void MainContainer_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);

            if (_miniResizeDir != MiniResizeDir.None)
            {
                // PointToScreen 返回物理像素，必须换算回 DIP 才能叠加到 Left/Width 等 DIP 属性，
                // 否则高 DPI 下窗口尺寸变化会比鼠标移动快 DPI 倍（125% 快 25%，150% 快 50%）。
                Point cur = PointToScreen(p);
                var src = PresentationSource.FromVisual(this);
                double devToDip = src?.CompositionTarget.TransformFromDevice.M11 ?? 1.0;
                double dx = (cur.X - _miniResizeStartScreen.X) * devToDip;
                double dy = (cur.Y - _miniResizeStartScreen.Y) * devToDip;

                double newLeft = _miniResizeStartLeft;
                double newTop = _miniResizeStartTop;
                double newW = _miniResizeStartWidth;
                double newH = _miniResizeStartHeight;

                switch (_miniResizeDir)
                {
                    case MiniResizeDir.Right: newW += dx; break;
                    case MiniResizeDir.Left: newW -= dx; newLeft += dx; break;
                    case MiniResizeDir.Bottom: newH += dy; break;
                    case MiniResizeDir.Top: newH -= dy; newTop += dy; break;
                    case MiniResizeDir.TopLeft: newW -= dx; newLeft += dx; newH -= dy; newTop += dy; break;
                    case MiniResizeDir.TopRight: newW += dx; newH -= dy; newTop += dy; break;
                    case MiniResizeDir.BottomLeft: newW -= dx; newLeft += dx; newH += dy; break;
                    case MiniResizeDir.BottomRight: newW += dx; newH += dy; break;
                }

                bool leftSide = _miniResizeDir is MiniResizeDir.Left or MiniResizeDir.TopLeft or MiniResizeDir.BottomLeft;
                bool topSide = _miniResizeDir is MiniResizeDir.Top or MiniResizeDir.TopLeft or MiniResizeDir.TopRight;

                // 最小尺寸钳制：宽度不足时锚点（对边）保持不动
                if (newW < MinWidth)
                {
                    if (leftSide) newLeft = _miniResizeStartLeft + (_miniResizeStartWidth - MinWidth);
                    newW = MinWidth;
                }
                if (newH < MinHeight)
                {
                    if (topSide) newTop = _miniResizeStartTop + (_miniResizeStartHeight - MinHeight);
                    newH = MinHeight;
                }

                // 上限：不超过虚拟屏幕尺寸（DIP）
                double maxW = Math.Max(MinWidth, SystemParameters.VirtualScreenWidth);
                double maxH = Math.Max(MinHeight, SystemParameters.VirtualScreenHeight);
                if (newW > maxW) newW = maxW;
                if (newH > maxH) newH = maxH;

                Left = newLeft;
                Top = newTop;
                Width = newW;
                Height = newH;
                return;
            }

            // 自定义拖动：用 PointToScreen 取鼠标真实屏幕位置（物理像素），
            // 与 Left/Top 的 DIP 单位换算后再叠加，避免窗口移动导致相对坐标负反馈（跟手只有一半）。
            // 不走系统移动循环，因此 Win11 顶部 Snap 不参与，可拖到屏幕外任意位置停住。
            if (_isDraggingWidget)
            {
                Point cur = PointToScreen(e.GetPosition(this));
                var src = PresentationSource.FromVisual(this);
                double devToDip = src?.CompositionTarget.TransformFromDevice.M11 ?? 1.0;
                Left = _dragStartWindowLeft + (cur.X - _dragStartScreen.X) * devToDip;
                Top = _dragStartWindowTop + (cur.Y - _dragStartScreen.Y) * devToDip;
                return;
            }

            // 非缩放：边缘显示调整大小光标，中央显示移动抓手（拖动中保持按下时的移动光标）。
            // 注意：不能依赖 _isMouseDown 做短路——旧 HeaderPanel 拖动路径已不执行，该标志可能残留 true，
            // 只需以 _isDraggingWidget 判断拖动中状态即可。
            if (_modeType != "floating" || _isTransparent || _isDraggingWidget) return;
            Cursor = HitTestMiniResize(p) switch
            {
                MiniResizeDir.Left or MiniResizeDir.Right => Cursors.SizeWE,
                MiniResizeDir.Top or MiniResizeDir.Bottom => Cursors.SizeNS,
                MiniResizeDir.TopLeft or MiniResizeDir.BottomRight => Cursors.SizeNWSE,
                MiniResizeDir.TopRight or MiniResizeDir.BottomLeft => Cursors.SizeNESW,
                _ => Cursors.SizeAll
            };
        }

        private void MainContainer_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_miniResizeDir != MiniResizeDir.None)
            {
                _miniResizeDir = MiniResizeDir.None;
                MainContainer.ReleaseMouseCapture();
                // 缩放结束：保存尺寸；重新评估边缘吸附（若仍贴边则按新尺寸重新收纳）
                _mainWin.SaveMiniWidgetSize(ActualWidth, ActualHeight);
                EvaluateDockOnPosition();
                e.Handled = true;
                return;
            }

            if (_isDraggingWidget)
            {
                _isDraggingWidget = false;
                _isMouseDown = false;
                // 先复位标志再释放捕获，避免 LostMouseCapture 里重复进入拖动结束分支
                MainContainer.ReleaseMouseCapture();
                EvaluateDockOnPosition();
                e.Handled = true;
            }
        }

        private void MainContainer_LostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_miniResizeDir != MiniResizeDir.None)
            {
                _miniResizeDir = MiniResizeDir.None;
                _mainWin.SaveMiniWidgetSize(ActualWidth, ActualHeight);
                EvaluateDockOnPosition();
                return;
            }

            if (_isDraggingWidget)
            {
                _isDraggingWidget = false;
                _isMouseDown = false;
                EvaluateDockOnPosition();
            }
        }

        private static bool IsDescendantOf(DependencyObject? element, DependencyObject parent)
        {
            while (element != null)
            {
                if (element == parent) return true;
                element = VisualTreeHelper.GetParent(element);
            }
            return false;
        }

        // 判断逻辑坐标点是否落在切换账号箭头按钮的可见区域内（用于排除边缘命中判定）
        private bool IsPointInsideButton(Point point)
        {
            try
            {
                if (BtnNextAccount == null || !BtnNextAccount.IsVisible) return false;
                Point topLeft = BtnNextAccount.TranslatePoint(new Point(0, 0), this);
                double right = topLeft.X + BtnNextAccount.ActualWidth;
                double bottom = topLeft.Y + BtnNextAccount.ActualHeight;
                return point.X >= topLeft.X && point.X <= right &&
                       point.Y >= topLeft.Y && point.Y <= bottom;
            }
            catch { return false; }
        }

        private void BtnNextAccount_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;
            _mainWin.SwitchNextAccountInMini();
            _mainWin.LogMiniWidgetDiagnostic("点击右箭头切换账号");
        }

        private void HeaderPanel_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isMouseDown || e.LeftButton != MouseButtonState.Pressed ||
                _modeType != "floating" || _isTransparent)
            {
                return;
            }

            Point currentPoint = e.GetPosition(this);
            Vector diff = _dragStartPoint - currentPoint;
            if (Math.Abs(diff.X) <= 4 && Math.Abs(diff.Y) <= 4) return;

            _isMouseDown = false;
            try { DragMove(); } catch { }
            _isDraggingWidget = false;
            EvaluateDockOnPosition();
        }

        private void HeaderPanel_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isMouseDown = false;
            e.Handled = true;
        }

        // ===== 靠近边缘自动收纳 =====
        private void StartEdgeTimer()
        {
            if (!_edgeTimer.IsEnabled) _edgeTimer.Start();
        }

        private void StopEdgeTimer()
        {
            if (_edgeTimer.IsEnabled) _edgeTimer.Stop();
            _retractDelayTimer.Stop();
            // 动画中途被打断（拖动/缩放/模式切换）时若残留 true，会导致：
            // 1. 缩放命中判定永久禁用；2. LocationChanged 位置保存被跳过。
            // 停止定时器时一并重置动画标记，确保状态一致。
            _isEdgeAnimating = false;
        }

        // 获取当前显示器的真实屏幕边界与工作区，并识别任务栏占用的边。
        // Screen 返回物理像素；通过 PointFromScreen 转为当前 WPF 窗口使用的逻辑坐标，兼容 Per-Monitor DPI。
        private (Rect screen, Rect work, bool allowLeft, bool allowTop, bool allowRight) GetDockGeometry()
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var monitor = System.Windows.Forms.Screen.FromHandle(hwnd);

            Rect ToLogical(System.Drawing.Rectangle r)
            {
                Point tl = PointFromScreen(new Point(r.Left, r.Top));
                Point br = PointFromScreen(new Point(r.Right, r.Bottom));
                return new Rect(Left + tl.X, Top + tl.Y, br.X - tl.X, br.Y - tl.Y);
            }

            Rect screen = ToLogical(monitor.Bounds);
            Rect work = ToLogical(monitor.WorkingArea);
            const double taskbarTolerance = 1.0;

            // 系统工作区已反映原生任务栏占用；但部分第三方任务栏外壳（MyDockFinder、StartAllBack 等）
            // 不更新系统工作区，会把任务栏所在边误判为“屏幕边缘”而触发贴边收纳（贴贴一放上去就被收走）。
            // 补充检测 Shell_TrayWnd / Shell_SecondaryTrayWnd 的实际占位矩形，两者结合判定。
            var (trayLeft, trayTop, trayRight) = DetectTaskbarOccupiedEdges(screen, taskbarTolerance);
            bool allowLeft = Math.Abs(work.Left - screen.Left) <= taskbarTolerance && !trayLeft;
            bool allowTop = Math.Abs(work.Top - screen.Top) <= taskbarTolerance && !trayTop;
            bool allowRight = Math.Abs(work.Right - screen.Right) <= taskbarTolerance && !trayRight;
            return (screen, work, allowLeft, allowTop, allowRight);
        }

        // 检测任务栏窗口实际占用了当前屏幕的哪些边（物理像素 → 逻辑坐标）。
        // 原生任务栏在工作区中已体现；此处主要兜底“不更新工作区”的第三方任务栏外壳。
        private (bool left, bool top, bool right) DetectTaskbarOccupiedEdges(Rect screen, double tolerance)
        {
            bool left = false, top = false, right = false;
            try
            {
                foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
                {
                    IntPtr tray = FindWindow(cls, null);
                    while (tray != IntPtr.Zero)
                    {
                        if (GetWindowRect(tray, out RECT r))
                        {
                            // PointFromScreen 返回相对窗口原点的逻辑偏移，叠加 Left/Top 还原绝对逻辑坐标
                            Point tl = PointFromScreen(new Point(r.Left, r.Top));
                            Point br = PointFromScreen(new Point(r.Right, r.Bottom));
                            double w = br.X - tl.X;
                            double h = br.Y - tl.Y;
                            if (w > 0 && h > 0)
                            {
                                double trayTop = Top + tl.Y;
                                double trayLeft = Left + tl.X;
                                double trayRight = Left + br.X;
                                // 任务栏贴住屏幕边且沿边几乎铺满（多屏拼接允许 60px 误差）
                                if (Math.Abs(trayTop - screen.Top) <= tolerance && w >= screen.Width - 60) top = true;
                                if (Math.Abs(trayLeft - screen.Left) <= tolerance && h >= screen.Height - 60) left = true;
                                if (Math.Abs(trayRight - screen.Right) <= tolerance && h >= screen.Height - 60) right = true;
                            }
                        }
                        tray = FindWindowEx(IntPtr.Zero, tray, cls, null);
                    }
                }
            }
            catch { }
            return (left, top, right);
        }

        // 根据用户手动拖动后的实际位置判断是否贴边；不主动吸附、不靠近触发。
        private void EvaluateDockOnPosition()
        {
            if (_modeType != "floating" || !IsVisible)
            {
                StopEdgeTimer();
                _dockEdge = "none";
                return;
            }

            // Topmost 保活：自由悬浮（未贴边）时 EdgeTimer 也需保持运行，
            // 周期性 EnsureTopmost 防止点击任务栏/其他置顶窗口后悬浮贴贴 Z 序被压低而盖住。
            if (!_edgeTimer.IsEnabled)
            {
                _edgeTimer.Start();
            }

            var geometry = GetDockGeometry();
            Rect screen = geometry.screen;

            // 严格手动贴边：只在用户释放后窗口已经接触左/右/上/下屏幕边缘时进入收纳。
            // 这里比较的是“到真实屏幕边缘的正向缝隙”：>阈值表示仍有缝，绝不收纳。
            double topGap = Top - screen.Top;
            double leftGap = Left - screen.Left;
            double rightGap = screen.Right - (Left + Width);
            double bottomGap = screen.Bottom - (Top + Height);

            string edge = "none";
            if (_edgeDockEnabled)
            {
                Rect work = geometry.work;
                bool clearOfTaskbarVertically = Top >= work.Top - _dockThreshold &&
                                                Top + Height <= work.Bottom + _dockThreshold;
                bool clearOfTaskbarHorizontally = Left >= work.Left - _dockThreshold &&
                                                  Left + Width <= work.Right + _dockThreshold;

                // 任务栏占用的侧边不允许收纳（避免藏到任务栏后面）；卡片若会压住任务栏也不允许。
                // 边缘判定统一以屏幕物理分辨率边界为准；底部候选只要求水平范围在工作区内，
                // 避免收纳后把手落到屏幕拼接缝隙处。
                var edges = new (string name, double gap, bool allowed)[]
                {
                    ("top", topGap, geometry.allowTop && clearOfTaskbarHorizontally),
                    ("left", leftGap, geometry.allowLeft && clearOfTaskbarVertically),
                    ("right", rightGap, geometry.allowRight && clearOfTaskbarVertically),
                    ("bottom", bottomGap, clearOfTaskbarHorizontally)
                };
                double bestGap = double.MaxValue;
                foreach (var (name, gap, allowed) in edges)
                {
                    if (allowed && gap <= _dockThreshold && gap < bestGap)
                    {
                        bestGap = gap;
                        edge = name;
                    }
                }
            }

            if (edge == "none")
            {
                if (_dockEdge != "none")
                {
                    // 退出收纳前先判断窗口是否正停在收纳位置（大部分在屏幕外）。
                    // 用户拖动离开时 Left/Top 已是新位置，不应弹回；仅设置变更（如关闭贴边收纳）
                    // 且窗口仍处于屏幕外收纳位时，才恢复到完整展开位置，避免贴贴"消失"。
                    var (retractedLeft, retractedTop) = RetractedPosition();
                    bool isStuckRetracted = Math.Abs(Left - retractedLeft) < 2 &&
                                            Math.Abs(Top - retractedTop) < 2;

                    _dockEdge = "none";
                    // 只停止延迟收纳并复位动画标记；EdgeTimer 保持运行以维持 Topmost 保活
                    _retractDelayTimer.Stop();
                    _isEdgeAnimating = false;
                    _edgeExpanded = true;

                    if (isStuckRetracted && IsVisible && _modeType == "floating")
                    {
                        Left = _dockBaseLeft;
                        Top = _dockBaseTop;
                    }
                }
                return;
            }

            // 用户拖入屏幕外的深度只用于确认“已接触边缘”，不作为展开位置。
            // 展开基准始终是完整卡片贴边的位置；沿边方向保留用户选择的位置并限制在工作区内。
            // 边缘统一以屏幕物理分辨率边界为准，任务栏只是屏幕上的普通区域，不参与边缘判定。
            Rect workArea = geometry.work;
            _dockBaseLeft = Math.Clamp(Left, workArea.Left, Math.Max(workArea.Left, workArea.Right - Width));
            _dockBaseTop = Math.Clamp(Top, workArea.Top, Math.Max(workArea.Top, workArea.Bottom - Height));
            switch (edge)
            {
                case "top": _dockBaseTop = screen.Top; break;
                case "left": _dockBaseLeft = screen.Left; break;
                case "right": _dockBaseLeft = screen.Right - Width; break;
                // 底部同样贴屏幕物理底边（分辨率边缘），任务栏/浮动 Dock 不改变基准
                case "bottom": _dockBaseTop = screen.Bottom - Height; break;
            }

            _dockEdge = edge;
            _edgeExpanded = true;
            // 持久化完整可见的展开位置，避免下次启动恢复到越界半身位置。
            _mainWin.SaveMiniWidgetLocation(_dockBaseLeft, _dockBaseTop);
            StartEdgeTimer();
            // 贴边后稍候再收纳；若鼠标仍停在卡片上则保持展开，离开后才收纳
            _retractDelayTimer.Stop();
            _retractDelayTimer.Start();
        }

        private (double L, double T) RetractedPosition()
        {
            double peek = _dockPeek;
            Rect screen = GetDockGeometry().screen;
            // 收纳位置相对真实屏幕边缘计算，确保无论用户刚好贴边还是稍微推入屏幕外，都固定露出 24px 把手。
            // 垂直/水平轴仍保留用户选择的位置。
            switch (_dockEdge)
            {
                case "top": return (_dockBaseLeft, screen.Top - (Height - peek));
                case "left": return (screen.Left - (Width - peek), _dockBaseTop);
                case "right": return (screen.Right - peek, _dockBaseTop);
                // 底部：把手露出在屏幕物理底边上方，窗口其余部分滑出屏幕外
                case "bottom": return (_dockBaseLeft, _dockBaseTop + Height - peek);
                default: return (_dockBaseLeft, _dockBaseTop);
            }
        }

        private void SlideTo(double targetLeft, double targetTop)
        {
            _slideTargetLeft = targetLeft;
            _slideTargetTop = targetTop;
            _slideStartLeft = Left;
            _slideStartTop = Top;
            _slideStartedAt = Environment.TickCount64;
            _isEdgeAnimating = true;
            if (!_slideTimer.IsEnabled) _slideTimer.Start();
        }

        private static double EaseInOutCubic(double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return t < 0.5
                ? 4.0 * t * t * t
                : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;
        }

        private void SlideTimer_Tick(object? sender, EventArgs e)
        {
            double progress = (Environment.TickCount64 - _slideStartedAt) / SlideDurationMs;
            if (progress >= 1.0)
            {
                Left = _slideTargetLeft;
                Top = _slideTargetTop;
                _slideTimer.Stop();
                _isEdgeAnimating = false;
                return;
            }

            double eased = EaseInOutCubic(progress);
            Left = _slideStartLeft + (_slideTargetLeft - _slideStartLeft) * eased;
            Top = _slideStartTop + (_slideTargetTop - _slideStartTop) * eased;
        }

        private void EdgeTimer_Tick(object? sender, EventArgs e)
        {
            // 1) Topmost 保活：点击任务栏/其他置顶窗口后，周期性把悬浮贴贴抬回最顶层（不抢焦点）
            if (_modeType == "floating" && IsVisible && !_isDraggingWidget)
            {
                EnsureTopmost();
            }

            // 2) 不可见/非悬浮/拖动中：无需保活与贴边逻辑，停止定时器
            if (!IsVisible || _modeType != "floating" || _isDraggingWidget)
            {
                StopEdgeTimer();
                return;
            }

            // 3) 自由悬浮（未贴边）：定时器保持运行，仅做 Topmost 保活
            if (_dockEdge == "none")
            {
                return;
            }

            Point c = GetCursorLogical();

            // 命中区：
            // 展开状态：鼠标停留在完整卡片上（含边距）保持展开，离开后延迟收纳；
            // 收纳状态：只有真正指向露出的把手（含边距）才展开——不碰本体（把手）绝不召唤，
            // 否则鼠标在卡片原本占据的位置划过就会误展开。
            Rect peekHit = InflateRect(PeekStripRect(), _dockHitMargin);
            bool wantExpanded = _edgeExpanded
                ? InflateRect(new Rect(_dockBaseLeft, _dockBaseTop, Width, Height), _dockHitMargin).Contains(c) || peekHit.Contains(c)
                : peekHit.Contains(c);

            if (wantExpanded)
            {
                // 仍在贴贴上：取消挂起的收纳，确保展开
                _retractDelayTimer.Stop();
                if (!_edgeExpanded)
                {
                    _edgeExpanded = true;
                    SlideTo(_dockBaseLeft, _dockBaseTop);
                }
            }
            else
            {
                // 已离开贴贴：启动延迟收纳(已在倒计时则不重复启动)
                if (_edgeExpanded && !_retractDelayTimer.IsEnabled)
                {
                    _retractDelayTimer.Start();
                }
            }
        }

        private void RetractDelayTimer_Tick(object? sender, EventArgs e)
        {
            _retractDelayTimer.Stop();
            if (_dockEdge == "none" || !IsVisible || _modeType != "floating" || _isDraggingWidget) return;

            // 倒计时结束再确认一次鼠标位置，避免倒计时期间鼠标又回到贴贴上仍被收纳
            Point c = GetCursorLogical();
            Rect expandedHit = InflateRect(new Rect(_dockBaseLeft, _dockBaseTop, Width, Height), _dockHitMargin);
            Rect peekHit = InflateRect(PeekStripRect(), _dockHitMargin);
            if (expandedHit.Contains(c) || peekHit.Contains(c))
            {
                return; // 又回来了，保持展开
            }

            _edgeExpanded = false;
            var (rl, rt) = RetractedPosition();
            SlideTo(rl, rt);
        }

        // 收纳状态下露在屏幕内的把手矩形(逻辑坐标)
        private Rect PeekStripRect()
        {
            double peek = _dockPeek;
            Rect screen = GetDockGeometry().screen;
            switch (_dockEdge)
            {
                case "top": return new Rect(_dockBaseLeft, screen.Top, Width, peek);
                case "left": return new Rect(screen.Left, _dockBaseTop, peek, Height);
                case "right": return new Rect(screen.Right - peek, _dockBaseTop, peek, Height);
                case "bottom": return new Rect(_dockBaseLeft, _dockBaseTop + Height - peek, Width, peek);
                default: return Rect.Empty;
            }
        }

        private static Rect InflateRect(Rect r, double m)
        {
            if (r.IsEmpty) return r;
            return new Rect(r.X - m, r.Y - m, r.Width + m * 2, r.Height + m * 2);
        }

        private Point GetCursorLogical()
        {
            POINT p;
            if (GetCursorPos(out p))
            {
                double sx = 1.0, sy = 1.0;
                var src = PresentationSource.FromVisual(this);
                if (src?.CompositionTarget != null)
                {
                    sx = src.CompositionTarget.TransformToDevice.M11;
                    sy = src.CompositionTarget.TransformToDevice.M22;
                }
                return new Point(p.X / sx, p.Y / sy);
            }
            return new Point(-10000, -10000);
        }

        private void AccountArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 单击悬浮卡片不再切换账号；仅响应双击刷新账号配额。
            e.Handled = true;
            if (e.ClickCount == 2)
            {
                _ = _mainWin.RefreshCurrentSingleAccountInMiniAsync();
            }
        }

        private void MenuTransparent_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.ToggleMiniWidgetTransparent();
        }

        // 右键菜单打开时同步「模式切换 / 鼠标穿透」项文案
        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            MenuToggleMode.Header = _modeType == "floating" ? "切换到任务栏贴" : "切换到桌面悬浮";
        }

        // 在任务栏贴 / 桌面悬浮之间直接切换
        private void MenuToggleMode_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.SetMiniWidgetMode(_modeType == "floating" ? "taskbar" : "floating");
        }

        private void MenuOpenMain_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.ShowMainFromMini();
        }

        private void MenuSwitchAccount_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.SwitchNextAccountInMini();
        }

        private void MenuRefresh_Click(object sender, RoutedEventArgs e)
        {
            _ = _mainWin.RefreshCurrentSingleAccountInMiniAsync();
        }

        private void MenuOpenSettings_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.OpenSettingsFromMini();
        }

        private void MenuExit_Click(object sender, RoutedEventArgs e)
        {
            _mainWin.ExitApp();
        }
    }
}
