# Haodo 开发与维护指南

当前版本：v1.0.13  
目标平台：Windows x64  
技术栈：.NET 10、WPF、WinForms NotifyIcon

## 项目用途

Haodo 是一个 Windows 桌面 AI 账号配额查看器。程序读取用户导入的 OAuth 凭证 JSON，查询并展示各平台配额信息，并提供任务栏微贴和在线更新功能。

运行配置和凭证默认保存在：

```text
%AppData%\Haodo
```

项目目录中的 `data` 文件夹不再是运行必需项。

## 目录结构

整理后的项目只保留以下公开内容：

```text
BalanceViewer\
├── src\
│   ├── App.xaml
│   ├── App.xaml.cs
│   ├── AssemblyInfo.cs
│   ├── MainWindow.xaml
│   ├── MainWindow.xaml.cs
│   ├── MiniWidgetWindow.xaml
│   ├── MiniWidgetWindow.xaml.cs
│   ├── CLIProxyAPI_GUI.csproj
│   └── app.ico
├── scripts\
│   ├── build.ps1
│   ├── build_only.ps1
│   └── deploy.ps1
├── docs\
│   ├── MAINTENANCE.md
│   └── AI平台OAuth额度查询机制深度分析报告.md
├── Haodo.exe
├── 仅编译.bat
├── 编译并发布.bat
├── 一键更新发布.bat
└── 版本号.txt
```

`.workbuddy` 是项目工作元数据，不属于软件发布内容，但必须保留。

## 版本管理

`版本号.txt` 是当前版本来源；工程仍兼容旧的 `VERSION.txt`。内容使用三段式版本号，例如：

```text
1.0.13
```

`CLIProxyAPI_GUI.csproj` 在构建时直接读取该文件并设置：

- `Version`
- `AssemblyVersion`
- `FileVersion`
- `ProductVersion`

程序“关于软件”中的版本徽章及在线更新比较也从程序集版本动态获取，不需要手动修改源码中的版本字符串。

当前 Build 号计算规则：

```text
major * 100 + minor * 10 + patch
```

例如 `1.0.13` 对应 Build `113`。

## 编译与发布

根目录提供三个入口：

- `仅编译.bat`：本地编译并覆盖根目录 `Haodo.exe`，不上传服务器。
- `编译并发布.bat`：本地编译后继续执行服务器发布。
- `一键更新发布.bat`：仅发布当前根目录已有的 `Haodo.exe`。

本地构建脚本执行以下步骤：

1. 读取并校验 `版本号.txt`（兼容 `VERSION.txt`）。
2. 在编译前检查 `Haodo.exe` 是否正在运行；如果存在，则强制结束进程树，并再次确认进程已经退出。结束失败时立即停止发布。
3. 在 `%TEMP%\HaodoBuild` 使用 Release、win-x64、框架依赖单文件模式执行 `dotnet publish`。
4. 将最新 `Haodo.exe` 复制到项目根目录。
5. 删除根目录旧的 `Haodo.pdb`。
6. 删除 `%TEMP%\HaodoBuild` 临时构建目录；源码目录不会生成新的 `bin` 或 `obj` 文件。

构建命令使用 SDK 原生的 `--artifacts-path` 和 `-o`，将所有生成文件放在 `%TEMP%\HaodoBuild`，避免污染 OneDrive 中的源码目录。

发布模式为框架依赖单文件，因此目标电脑需要安装兼容的 .NET Desktop Runtime 10。

## 发布文件规则

根目录发布产物只保留：

```text
Haodo.exe
```

`Haodo.pdb` 是调试符号文件，不是软件运行必需文件。构建过程可以生成 PDB，但发布脚本不会把它保留在根目录，并会在成功发布后清理构建缓存。

## 核心源码

- `App.xaml.cs`：应用入口、单实例控制和跨进程唤醒。
- `MainWindow.xaml`：主窗口界面。
- `MainWindow.xaml.cs`：凭证管理、配额查询、配置、主题、OAuth 登录和在线更新。
- `MiniWidgetWindow.xaml`：任务栏贴贴与桌面悬浮卡片界面。
- `MiniWidgetWindow.xaml.cs`：三模式切换、任务栏嵌入、悬浮定位、DPI 适配、颜色与交互。
- `CLIProxyAPI_GUI.csproj`：.NET 工程、发布和版本配置。

## 文字渲染

主窗口统一使用 `Microsoft YaHei UI`、整数像素字号、`Display` 排版、ClearType 与固定 Hinting，并启用布局取整和像素对齐。透明贴贴窗口使用灰阶抗锯齿，避免透明表面上的 ClearType 彩边；进程保持 Per-Monitor DPI 感知，不额外执行手动缩放。

设置页采用“短标题 + 单句说明”的文案结构，术语统一为外观、数据目录、自动刷新、贴贴、凭证文件和关于。

## 贴贴模式

贴贴支持三种状态：`off`（关闭）、`taskbar`（任务栏）和 `floating`（桌面悬浮）。切换形态时会销毁旧原生窗口并按目标模式重建，避免任务栏父子窗口样式残留。

桌面悬浮卡片使用紧凑的 `244 × 92` 逻辑像素布局，并按系统 DPI 换算原生窗口尺寸；任务栏模式仍保持独立宽度，并依据实际任务栏高度压缩显示。顶部账号头是专用拖拽区，不切换账号；底部配额区单击切换账号、双击刷新，且不会触发拖拽。

背景不透明度只写入背景画刷 Alpha，不影响文字、图标和配额信息。滑块拖动期间只执行内存中的轻量预览，停止操作约 180 ms 后再持久化设置，不重复触发窗口布局和原生定位。

“信息强调色”由统一调色板应用到主 Logo、Logo 底板、平台名、账号文本、配额徽标、分隔线、刷新/空状态、两行平台图标、配额标签、轨道、填充、百分比和悬浮卡片边框。悬浮模式会根据背景色自动计算可读的实际显示色，但保留用户保存的原始颜色；任务栏模式直接使用用户强调色。设置恢复、预设色和取色器都统一规范为 `#RRGGBB`，预设列表会显示当前选中环。

鼠标穿透仅用于桌面悬浮模式：启用后左键穿透到底层窗口并自动固定贴贴位置，右键仍可打开贴贴菜单；不再提供独立的位置锁定选项。悬浮贴贴与托盘右键菜单均使用无图标的精简文案。

## 数据与配置

软件首次运行时会创建 `%AppData%\Haodo`。为兼容旧版本，程序仍会检查软件同目录下的 `settings.json` 或 `data\settings.json` 并尝试迁移，但项目和发布包不需要预置这些文件。

不要把 OAuth 凭证、Access Token、Refresh Token 或个人配置提交到项目目录或 Markdown 文档中。

## 发布前检查

1. 确认 `版本号.txt` 是目标版本。
2. 确认项目可使用 .NET 10 SDK 编译。
3. 运行 `仅编译.bat` 做本地验收；需要上线时再运行 `编译并发布.bat`。
4. 确认根目录存在最新 `Haodo.exe`。
5. 确认根目录不存在 `Haodo.pdb`，源码目录不存在 `src\bin` 和 `src\obj`。
6. 确认 `%TEMP%\HaodoBuild` 已由脚本清理。
