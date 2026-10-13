# BalanceViewer (Haodo) 智能体软件维护文档

> **文档定位**：本文件是给**后来者 AI 与开发者**的完整软件说明书与维护手册。任何 AI 或人类开发者拿到本文件 + 源码，即可完全理解本软件的结构、机制、构建方式与维护流程。
>
> **当前版本**：v1.0.50（见项目根目录 `版本号.txt`）
> **目标平台**：Windows x64（原生 Windows，不使用 WSL）
> **技术栈**：.NET 10、WPF、WinForms（NotifyIcon）、HttpListener、SSE 流式转发
> **最后更新**：2026-10-13
>
> **路径约定**：本文档存放于项目根目录的 `维护文档/` 子目录。文中所有路径（`src/`、`scripts/`、`docs/` 等）
> 一律相对**项目根目录**；只使用相对路径、系统环境变量（`%AppData%`、`%TEMP%` 等）或网络地址，
> **不含任何本机绝对路径**，文档可随项目整体迁移而不失效。

---

## 目录

- [0. 给 AI 读者的快速导航](#0-给-ai-读者的快速导航)
- [1. 软件概述](#1-软件概述)
- [2. 技术栈与环境要求](#2-技术栈与环境要求)
- [3. 目录结构总览](#3-目录结构总览)
- [4. 逐文件详解](#4-逐文件详解)
- [5. 核心机制深度解析](#5-核心机制深度解析)
- [6. 配置与数据存储](#6-配置与数据存储)
- [7. 构建与发布](#7-构建与发布)
- [8. 维护指南与故障排查](#8-维护指南与故障排查)
- [9. 已知边界与未来改进](#9-已知边界与未来改进)
- [10. 安全红线](#10-安全红线)

---

## 0. 给 AI 读者的快速导航

如果你是一个刚接触本项目的 AI，请按以下顺序阅读，10 分钟内可建立完整认知：

| 目的 | 看哪里 |
|---|---|
| 软件是干什么的 | [第 1 章](#1-软件概述) |
| 数据在软件里怎么流动 | [1.3 数据流](#13-数据流) |
| 每个文件干什么 | [第 4 章](#4-逐文件详解) |
| 代理怎么转换协议 | [第 5 章](#5-核心机制深度解析)（尤其 5.1–5.8） |
| 改代码后怎么编译发布 | [第 7 章](#7-构建与发布) |
| 出问题怎么排查 | [第 8 章](#8-维护指南与故障排查) |
| 哪些事绝对不能做 | [第 10 章](#10-安全红线) |

**两条最重要的常识**：

1. **本软件 = 配额查看器 + 本地 Gemini API 代理，两个功能共享同一个 GUI 进程**。代理功能是后期加入的，旧文档（`docs/MAINTENANCE.md`）只描述配额查看器，已过时，以本文件为准。
2. **代理的后端是 Google Antigravity 私有上游**（`cloudcode-pa.googleapis.com`），不是公开的 `generativelanguage.googleapis.com`。它有一堆私有行为（按 UA 放行模型、schema 严格校验 400、空响应等），[第 5 章](#5-核心机制深度解析) 的所有"坑"都是真实验证过的。

---

## 1. 软件概述

### 1.1 是什么

**BalanceViewer**（程序文件名/进程名 **Haodo**，项目文件 `CLIProxyAPI_GUI.csproj`）是一个 Windows 桌面应用，集成了两大功能：

**功能 A：AI 账号配额查看器**
- 导入 **Antigravity / Gemini** 平台的 OAuth 凭证 JSON 文件（v1.0.27 起专一化，非 Gemini 平台凭证一律拒绝载入），在桌面卡片上实时展示"5 小时滚动额度 + 周额度"百分比与重置时间（Gemini 模型分组 + Claude/GPT 模型分组）。
- 提供任务栏贴贴（嵌入任务栏）与桌面悬浮卡片两种迷你展示形态，支持拖拽、缩放、边缘停靠收纳、鼠标穿透、背景透明度、强调色定制。
- 支持内置 Google OAuth 登录（浏览器授权 + 本地回调），自动生成 Antigravity 凭证文件。
- 支持凭证 Token 过期自动刷新、多账号轮询、自动刷新定时器。

**功能 B：本地 Gemini API 代理服务器（后期核心功能）**
- 在本机 `http://127.0.0.1:8317`（默认端口）启动一个 OpenAI 兼容的 HTTP 服务。
- 将 OpenAI 格式的请求（`/v1/chat/completions`、`/v1/responses`、`/v1/completions`）转换为 Google Antigravity 上游的 `generateContent` 请求，使用**用户已登录的 Gemini 账号 Token** 调用上游，再把响应转回 OpenAI 格式（含流式 SSE、工具调用）。
- 让任何 OpenAI 客户端（NarraFork、AI SDK、各种 Chat 客户端）都能直接使用 Gemini 模型（gemini-3.6-flash 系列等）。

**为什么要做代理**：Antigravity 上游是私有端点，官方只允许特定客户端（UA 识别）访问；通过本地代理可以把"用户在 Haodo 里登录的账号"转化为通用 OpenAI API 供第三方工具使用，且自动处理 Token 刷新、模型名映射、协议差异。

### 1.2 架构总览

```text
┌─────────────────────────────────────────────────────────────┐
│                      Haodo.exe (单一进程)                     │
│                                                             │
│  ┌───────────────────┐      ┌──────────────────────────┐   │
│  │  MainWindow (WPF) │      │  MiniWidgetWindow (WPF)  │   │
│  │  · 账号管理/配额    │◄────►│  · 任务栏贴贴/桌面悬浮卡片  │   │
│  │  · 设置/主题/更新   │      │  · 拖拽/缩放/停靠/穿透     │   │
│  │  · 代理开关与配置   │      └──────────────────────────┘   │
│  └─────────┬─────────┘                                       │
│            │ 回调注入 (LogCallback / GetGeminiTokenAsync)     │
│  ┌─────────▼─────────┐      ┌──────────────────────────┐   │
│  │ LocalProxyServer  │      │ GeminiProtocolTranslator │   │
│  │ (HttpListener)    │─────►│ (纯静态协议转换，无状态)    │   │
│  └─────────┬─────────┘      └──────────────────────────┘   │
│            │ 读软件同级 data\ 目录凭证 JSON + Token 自动刷新  │
└────────────┼────────────────────────────────────────────────┘
             │ HTTPS (Bearer Token, UA=antigravity/1.11.3)
             ▼
   Google Antigravity 上游 (cloudcode-pa.googleapis.com)
   · /v1internal:generateContent           (非流式)
   · /v1internal:streamGenerateContent?alt=sse (流式)
   · /v1internal:fetchAvailableModels      (模型列表)
```

### 1.3 数据流

**配额查看流程**：
```text
MainWindow.RefreshAccounts()
  → 扫描数据目录 *.json 凭证文件（排除 settings.json）
  → 校验格式 (IsValidAuthJson，type 非 antigravity/gemini 直接跳过)
  → 直查 FetchAntigravityQuotaAsync（access_token 现存优先）
  → Token 过期则 RefreshAntigravityTokenAsync 自动刷新并回写文件
  → 更新 UI 卡片 + 通知 MiniWidgetWindow 同步显示
```

**代理请求流程**（以 `/v1/responses` 流式为例）：
```text
第三方客户端 ──POST /v1/responses (stream:true)──► LocalProxyServer.ProcessRequestAsync
  → 鉴权（Bearer API Key）
  → GetGeminiTokenAsync() 取账号 Token（轮询多个账号，过期自动刷新）
  → GeminiProtocolTranslator.ConvertOpenAIToGeminiRequest()
  │    · MapModel() 模型名 → 上游合法键
  │    · messages/input → Gemini contents（system → systemInstruction）
  │    · tool 消息 → functionResponse；assistant.tool_calls → functionCall
  │    · tools → functionDeclarations（含 Schema 白名单清洗）
  │    · 图片 → inlineData(base64)
  │    · temperature/top_p/max_tokens → generationConfig
  → HandleResponsesStreamAsync()
  │    · POST 上游 :streamGenerateContent?alt=sse（UA=antigravity/1.11.3）
  │    · 404 时 GetModelRetryCandidate 换同系列候选键重发一次
  │    · 逐行读 SSE，text → response.output_text.delta，functionCall → function_call.* 事件
  │    · 空响应（无文本无函数调用）→ RemoveToolsFromGeminiBody 移除 tools 重试一次
  │    · 结束发 response.completed（含 function_call 输出项）
  └── SSE 流返回客户端
```

---

## 2. 技术栈与环境要求

| 项 | 要求 |
|---|---|
| .NET SDK | **.NET 10 SDK**（编译需要，csproj 的 TargetFramework 为 `net10.0-windows`） |
| 运行时 | 目标电脑需安装 **.NET Desktop Runtime 10**（发布为框架依赖，非自包含） |
| 框架 | WPF（`UseWPF=true`）+ WinForms（`UseWindowsForms=true`，用于 NotifyIcon） |
| 语言 | C#（Nullable enable） |
| 系统 | Windows x64；manifest 开启 longPathAware；Per-Monitor DPI v2 |
| 构建工具 | `dotnet publish`（脚本封装） |
| 其他 | `scp.exe`/`ssh.exe`（发布脚本上传服务器用，Windows 10+ 自带） |

**注意**：项目可能位于任何目录（包括云同步目录），构建脚本特意把所有中间产物输出到系统临时目录 `%TEMP%\HaodoBuild`（`--artifacts-path`），避免在源码目录生成 `bin`/`obj` 污染同步。**不要**手动在 `src` 里跑裸 `dotnet build`（会生成 bin/obj）；要编译就用项目根目录的 `.bat` 或 `dotnet publish` 加 `--artifacts-path`。

---

## 3. 目录结构总览

```text
项目根目录\
├── Haodo.exe                  ← 发布产物（框架依赖单文件轻量版，约 930KB，开源无混淆），随版本更新
├── 版本号.txt                 ← 版本号唯一来源（当前 1.0.38；兼容旧名 VERSION.txt）
├── 仅编译.bat                 ← 本地一键编译入口（调 scripts\build_only.ps1）
├── 维护文档\
│   └── MAINTENANCE.md         ← 本文档（唯一维护记录归档处）
├── docs\
│   ├── MAINTENANCE.md         ← 旧版维护指南（仅覆盖配额查看器，已过时，保留作历史）
│   └── AI平台OAuth额度查询机制深度分析报告.md ← CLIProxyAPI 配额机制研究报告（背景资料）
├── scripts\
│   ├── build_only.ps1         ← 轻量单文件编译打包，覆盖根目录 Haodo.exe（详见 7.2）
│   └── schema_regression_check.cs ← Schema 白名单回归验证脚本（反射调用真实代码）
├── src\
│   ├── CLIProxyAPI_GUI.csproj ← 工程文件（版本读取、发布配置）
│   ├── App.xaml / App.xaml.cs ← 应用入口、DPI、单实例
│   ├── AssemblyInfo.cs        ← WPF ThemeInfo
│   ├── app.manifest           ← longPathAware 清单
│   ├── app.ico                ← 图标
│   ├── MainWindow.xaml(.cs)   ← 主窗口：界面(1321行) + 逻辑(4892行)
│   ├── MiniWidgetWindow.xaml(.cs) ← 贴贴/悬浮卡片：界面(268行) + 逻辑(1912行)
│   ├── LocalProxyServer.cs    ← 本地代理服务器（930 行）
│   └── GeminiProtocolTranslator.cs ← 协议转换器（1218 行）
└── .narrafork\ .workbuddy\    ← AI 工具工作元数据，保留但非发布内容
```

---

## 4. 逐文件详解

### 4.1 根目录文件

| 文件 | 作用 | 维护注意 |
|---|---|---|
| `Haodo.exe` | 编译产物，框架依赖轻量单文件 | 由构建脚本生成；**不要**手动编辑；提交前确认是最新构建 |
| `版本号.txt` | 版本唯一来源，三段式如 `1.0.38` | 升级版本只改这个文件；csproj 构建时自动读取并生成 `Version`/`AssemblyVersion`/`FileVersion`/`ProductVersion` |
| `仅编译.bat` | `powershell scripts\build_only.ps1` | 一键本地编译打包使用 |

### 4.2 `src/CLIProxyAPI_GUI.csproj`

```xml
<OutputType>WinExe</OutputType>
<TargetFramework>net10.0-windows</TargetFramework>
<UseWPF>true</UseWPF> <UseWindowsForms>true</UseWindowsForms>
<AssemblyName>Haodo</AssemblyName>
<ApplicationIcon>app.ico</ApplicationIcon>
<ApplicationManifest>app.manifest</ApplicationManifest>
<PublishSingleFile>true</PublishSingleFile>
<SelfContained>false</SelfContained>
<RuntimeIdentifier>win-x64</RuntimeIdentifier>
```

版本读取逻辑（**理解这里才能改版本**）：
- 优先读根目录 `版本号.txt`，不存在则读 `VERSION.txt`；
- `RawVersion` 去掉前导 `v`/空格，`Version` 再正则清理非法字符；
- `AssemblyVersion`/`FileVersion` = `$(Version).0`；
- `ValidateVersionFile` Target 在 `BeforeBuild/BeforeRebuild/BeforePublish` 校验文件存在且版本非空，否则编译直接报错。

### 4.3 `src/App.xaml` / `App.xaml.cs` — 入口（138 行）

- `DpiBootstrap`：`[ModuleInitializer]` 在程序集加载时设置进程为 **Per-Monitor DPI Aware V2**（先试 `SetProcessDpiAwarenessContext(-4)`，失败回退 `SetProcessDpiAwareness(2)`）。
- `App : Application`：**单实例控制**：
  - 命名互斥体 `Local\Haodo_SingleInstance_Mutex_V1`；已存在实例时：
    1. `EventWaitHandle`（`Local\Haodo_SingleInstance_Event_V1`）通知旧实例 `ShowMainFromMini()` 唤醒主窗口；
    2. 广播自定义 Windows 消息 `WM_SHOW_BALANCE_VIEWER_SINGLE_INSTANCE` 作为备用；
    3. 新进程 `Environment.Exit(0)` 干净退出。
  - 后台 `Task.Run` 循环等待唤醒事件 → Dispatcher 调 `MainWindow.ShowMainFromMini()`。
- 维护注意：新增唤醒需求时保持"Event + 广播"双通道，不要只留一种。

### 4.4 `src/AssemblyInfo.cs`、`app.manifest`、`app.ico`

- AssemblyInfo：WPF `ThemeInfo`（主题资源在 SourceAssembly）。
- app.manifest：仅 `longPathAware`（长路径支持）。
- app.ico：应用图标。

### 4.5 `src/MainWindow.xaml`（1321 行）— 主窗口界面

- 无边框透明窗口（`WindowStyle=None AllowsTransparency=True`），圆角 12，DropShadow。
- **自定义控件样式**（文件头）：滑块 Track/Thumb、滚动条、卡片 Border 模板；统一字体 `Segoe UI Variable Text, Microsoft YaHei UI`。
- 主结构：`RootMarginGrid`（拖拽移动窗口）→ `TitleBar`（标题 + 最小化/关闭）→ 顶栏（账号汇总 + 主界面/设置导航）→ `ViewMain`（账号卡片容器 `QuotaAccountsContainer` + 空状态 + 右下角 HTTP 代理状态灯 `DotLocalProxyStatus`）→ `ViewSettings`（设置页：**本地 API 代理卡片（置顶，含模型工具）**、外观、数据目录、自动刷新、主题、贴贴样式、凭证文件列表、日志）→ 更新弹窗 Overlay。
- 关键命名元素：`TxtAccountSummary`、`QuotaAccountsContainer`、`EmptyStateContainer`、`BtnNavMain/Settings`、`TxtLocalProxyPort/ApiKey/Url/StatusBadge`、`BtnLocalProxyOn/Off`、`BtnLocalProxyFetchModels/TestModel`、`CmbLocalProxyModel`、`TxtLocalProxyTestResult`、`TxtLog`、更新弹窗控件（`TxtUpdateModalTitle/TxtUpdateChangelog/GridUpdateOverlay/...`）。
- 主题用 `DynamicResource ThemeXxx` 键，由代码切换（见 4.6 的 ApplyTheme）。

### 4.6 `src/MainWindow.xaml.cs`（4892 行）— 主窗口逻辑

**模块划分（按行号）**：

| 区域 | 行号范围 | 内容 |
|---|---|---|
| 窗口基础 | 22–337 | 深色标题栏 P/Invoke、窗口状态保存/恢复、启动恢复 |
| 托盘 | 344–513 | `InitNotifyIcon`：WinForms NotifyIcon，右键菜单（主界面/贴贴/退出） |
| 贴贴控制 | 518–898 | `SwitchToMiniMode`/`ShowMainFromMini`/`OpenSettingsFromMini`/`SwitchNextAccountInMini`/`RefreshCurrentSingleAccountInMiniAsync`/`UpdateMiniWidgetData`；显示脱敏工具 `GetDisplayEmail/FileName/FilePath`（**邮箱打星脱敏**，`EmailRegex`） |
| 配置 | 904–1190 | `LoadSettings()`/`SaveSettings()`：见[第 6 章](#6-配置与数据存储) |
| 视图/设置页 | 1198–2000 | 主/设置页切换、展开卡片高亮（`ToggleSettingsSection_Click`/`SetSettingsCardHighlight`）、数据目录修改、自动刷新定时器、重置初始数据、数据目录迁移扫描 |
| 主题 | 2007–2550 | 系统/深色/浅色三态、`ApplyTheme()` 设置 `ThemeXxx` 动态资源、`NormalizeColorHex` |
| 贴贴样式设置 | 2123–2550 | 隐藏卡片背景、边缘停靠开关、强调色预设/取色器、背景色/透明度、账号打码、模式切换 |
| **本地代理** | **2556–3030** | `InitLocalProxyServer`/`Start/StopLocalProxyServer`/`UpdateLocalProxySegmentUI`/`GetValidGeminiTokenForProxyAsync`（凭证获取+过期刷新+**账号轮询** `_localProxyAccountIndex`）/端口/Key 修改/生成复制 Key 与 URL |
| 凭证文件管理 | 3031–3292 | `IsJsonFileDisabled`/`SetJsonFileDisabled`（写 `disabled` 标记字段）、`RenderSettingsFilesList` |
| 配额获取 | 3373–3840 | `FetchRealTimeQuotaAsync`/`FetchAntigravityQuotaAsync`（`cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`）/`RefreshAntigravityTokenAsync`（`oauth2.googleapis.com/token`）/`UpdateJsonToken` 回写（**专一化后仅保留 Antigravity/Gemini 路径**） |
| 账号刷新 UI | 3844–4000 | `RefreshAccounts`（异步全量刷新） |
| 文件导入/移除 | 4138–4330 | `IsValidAuthJson`（**硬过滤：`type` 非 antigravity/gemini 的凭证一律跳过**）、`BtnAddFiles_Click`（文件对话框+格式校验+拒绝 settings.json）、`BtnOpenFolder_Click`、`BtnRefreshQuota_Click`、`BtnRemoveAccount_Click` |
| **Gemini OAuth 登录** | **4335–4560** | `StartGeminiOAuthLoginAsync`：本地回调端口 51121–51130 循环尝试、Google 授权 URL（含内置 `GeminiClientID` 常量与 `GeminiScopes`）、3 分钟超时、回调换 Token（`oauth2.googleapis.com/token`）→ 拉 userinfo → 写 `antigravity-{email}.json` 凭证、`FetchAntigravityProjectIDAsync`（`loadCodeAssist` 拿 project_id） |
| 日志/弹窗 | 4568–4690 | `ShowCustomModal`/`ShowConfirmModal`、`Log`（带时间戳写 `TxtLog` + 转发小部件诊断） |
| **在线更新** | **4694–4892** | `CurrentVersionStr`/`CurrentBuildNum`（`major*100+minor*10+patch`）、`VersionJsonUrl = https://yusnip.top/version.json`、`CheckForUpdatesAsync`（build 号比较）、下载安装流程 `BtnConfirmUpdate_Click`（下载 exe → 杀进程 → 覆盖 → 重启） |

**AccountInfo record（3293 行）**：`(FilePath, Email, ProjectId, Disabled, Expired, FileSize, ModTime, AccountType, PlanType)` —— 凭证卡片的数据模型。

### 4.7 `src/MiniWidgetWindow.xaml`（268 行）— 贴贴界面

- 资源区：托盘右键菜单样式（深色 `#161B22` 底）、`MenuItem` 模板。
- 主结构：`MainContainer`（拖拽/缩放事件）→ `BorderFloatingCard`（悬浮卡片，`244×92` 逻辑像素级布局）→ 内容元素：`HeaderPanel`（账号头=专用拖拽区）、`LogoBadge`（Logo/平台图标）、`TxtPlatform`/`TxtEmail`、`QuotaBadge`（配额徽标）、`TxtQuotaPercent` 等。
- 事件：`MainContainer_PreviewMouseLeftButtonDown/Up/MouseMove`（拖拽+八方向缩放）、`HeaderPanel_MouseMove`（悬停展开）、`AccountArea_MouseLeftButtonDown`（底部配额区单击切换账号）、`ContextMenu_Opened`、`MenuToggleMode_Click`（任务栏↔悬浮切换）、`MenuTransparent_Click`（鼠标穿透）、`MenuExit_Click`。

### 4.8 `src/MiniWidgetWindow.xaml.cs`（1912 行）— 贴贴逻辑

**三态**：`off` / `taskbar`（任务栏嵌入）/ `floating`（桌面悬浮）。切换形态会**销毁旧原生窗口按目标模式重建**（避免任务栏父子窗口样式残留）。

关键机制：
- **任务栏嵌入**（`EmbedIntoTaskbar`）：`FindWindow("Shell_TrayWnd")` → `SetParent` → `SetWindowLong` 去 `WS_POPUP`/`WS_EX_APPWINDOW` 加 `WS_CHILD` → 按任务栏实际高度压缩；`DetachFromTaskbar` 还原。隐藏自 Alt-Tab（`HideFromAltTab`）。
- **边缘停靠收纳**（`_edgeDockEnabled`/`_dockEdge`/`EvaluateDockOnPosition`）：四边吸附（物理像素基准），收纳后露出 `_dockPeek=24` 把手；`GetDockGeometry` 处理多屏与任务栏占用边（`DetectTaskbarOccupiedEdges`）；`SlideTo` + `EaseInOutCubic` 动画；`_dockHitMargin=8` 命中边距。
- **鼠标穿透**（`SetMouseClickThrough`/`SetTransparentMode`）：置 `WS_EX_TRANSPARENT` + 低层鼠标钩子（`WH_MOUSE_LL=14`）拦截右键（`WM_RBUTTONDOWN/UP`）唤出菜单；穿透时自动固定位置。
- **强调色**：`ColorizeBitmap` 给 OpenAI Logo 着色 + `_tintedOpenAiCache` 缓存；`RelativeLuminance`/`ContrastRatio`/`GetReadableForegroundHex` 自动算可读前景色；背景透明度只写画刷 Alpha 不影响文字。
- **交互**：底部配额区单击切账号、双击刷新、`TriggerSwitchFlashFeedback` 切换闪烁；拖拽（HeaderPanel 为专用拖拽区）+ 八方向缩放（`HitTestMiniResize`）。
- **刷新状态**：`SetRefreshingState`、`UpdateData(...)` 由主窗口推送数据。

### 4.9 `src/LocalProxyServer.cs`（约 1060 行）— 本地代理服务器

**公共接口**：
```csharp
public int Port { get; set; } = 8317;              // HTTP 监听端口
public string ApiKey { get; set; } = "";
public bool IsRunning;                              // HttpListener 监听中
public Action<string>? LogCallback;                       // 日志回调 → MainWindow.Log
public Func<Task<(string accessToken, string email, string projectId)?>>? GetGeminiTokenAsync; // 凭证回调
```

**方法一览**：

| 方法 | 作用 |
|---|---|
| `Start()`/`Stop()` | `HttpListener` 绑定 `http://127.0.0.1:{Port}/`、`http://localhost:{Port}/` 并启动接受循环 |
| `ListenLoopAsync` | HTTP 接受连接循环，每连接 `Task.Run(ProcessRequestAsync)` |
| `ProcessRequestAsync` | 包 `HttpListenerContext` 为 `ProxyRequest`/`HttpListenerSyncStream`，转发 `ProcessRequestCoreAsync` |
| `ProcessRequestCoreAsync` | 主路由分发（见下） |
| `HandleResponsesNonStreamAsync` | responses 非流式：上游 `generateContent` → `ConvertGeminiToResponsesResponse` |
| `HandleResponsesStreamAsync` | responses 流式：上游 `streamGenerateContent?alt=sse` → SSE 事件流（created→item_added→part_added→delta/function_call.*→completed） |
| `HandleNonStreamChatCompletionAsync` | chat 非流式 → `ConvertGeminiToOpenAIResponse` |
| `HandleStreamChatCompletionAsync` | chat 流式 → `BuildOpenAISseChunk`/`BuildOpenAIToolCallSseChunk` → `[DONE]` |
| `GetModelRetryCandidate` | 上游 404 时返回同系列候选键（见 5.3） |
| `ReplaceGeminiBodyModel` | 流式重试时只改请求体 model 字段（手写 Utf8JsonWriter 重写） |
| `RemoveToolsFromGeminiBody` | 空响应重试时移除 `request.tools`/`toolConfig` |
| `HandleNativeGeminiForwardAsync` | `/v1beta/models/*` 原生转发 |
| 其余 | JSON 404（`"Path not found"`） |

**鉴权**：`ApiKey` 非空时校验 `Authorization: Bearer <key>` 或 `?key=`/`?api_key=`；失败 401 `Invalid API Key`。默认 Key 是 `sk-haodo-local`（可在设置里改）。

**通用处理**：每个需要上游的请求先 `GetGeminiTokenAsync()` 取 Token；无有效账号 → 503 `"No valid logged-in Gemini account found in Haodo"`。

### 4.10 `src/GeminiProtocolTranslator.cs`（1371 行）— 协议转换器

纯静态类，无状态。方法清单：

| 方法 | 作用 |
|---|---|
| `MapModel(string)` | 模型名 → 上游合法键（见 5.3） |
| `FetchOpenAIModelListJsonAsync` | 调 `v1internal:fetchAvailableModels`（POST，body `{"project":...}`），解析 `models`（**注意上游返回的是对象字典而非数组**，旧实现按数组解析导致列表从未生效——已修）与 `webSearchModelIds`，并合并内置默认模型列表 |
| `ConvertOpenAIToGeminiRequest` | OpenAI 请求 → Gemini 请求体（见 5.4） |
| `SanitizeGeminiSchema`（私有） | **Schema 白名单递归清洗**（见 5.5，重点维护区） |
| `ParseOpenAIMessageParts` | content 数组 → Gemini parts（text/function_call/function_call_output/image_url→inlineData） |
| `ConvertGeminiToOpenAIResponse` | Gemini 非流式响应 → `chat.completion` |
| `BuildOpenAISseChunk` / `BuildOpenAIToolCallSseChunk` | chat 流式 chunk 构造 |
| `ConvertGeminiToResponsesResponse` | Gemini → Responses API JSON 对象 |
| `BuildResponsesSse*` 系列（11 个） | Responses 流式 SSE 事件构造：created / item_added / part_added / delta / function_call.added / function_call.arguments.delta / function_call.done / completed / incomplete |
| `MapFinishReason` | Gemini finishReason → OpenAI 约定值（如 MAX_TOKENS→length 等） |

---

## 5. 核心机制深度解析

### 5.1 上游端点与 UA 伪装（重要）

- 非流式：`POST https://cloudcode-pa.googleapis.com/v1internal:generateContent`
- 流式：`POST https://cloudcode-pa.googleapis.com/v1internal:streamGenerateContent?alt=sse`
- 模型列表：`POST https://cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels`
- 配额：`POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist`
- **必须设置 `User-Agent: antigravity/1.11.3`**（`LocalProxyServer.cs` 与 `GeminiProtocolTranslator.cs` 中硬编码）。上游按 UA 识别 Antigravity 客户端并放行 gemini-3.5/3.6 系列模型；其他 UA 会得到 404。
- 鉴权：`Authorization: Bearer <Google OAuth access_token>`（来自 Haodo 登录的账号凭证）。

### 5.2 流式 SSE 结构（上游 → 代理）

上游 `?alt=sse` 返回 `data: {...}\n\n` 行；每条 JSON 可能是 `{candidates:[...]}` 或**外层包一层 `response` 对象**（`HandleResponsesStreamAsync`/`HandleStreamChatCompletionAsync` 都有 `root.response → targetRoot` 的兼容处理，两种都要支持）。
candidate 内容：`candidates[0].content.parts[]`，part 可能为 `{text}` 或 `{functionCall:{name,args}}`（args 可能是字符串或对象）。

### 5.3 模型映射（MapModel + 404 重试）

**规则链（按顺序）**：
1. 空/缺省 → `gemini-3.5-flash-low`；
2. 带 `:` 前缀（如 `测试:gemini-3.6-flash-high`）→ 剥离冒号前部分；
3. 第三方别名：`gpt-4*`/`claude-3-5*`/`claude-sonnet*` → `gemini-3.1-pro-high`；`gpt-3*`/`claude-haiku*` → `gemini-3.5-flash-low`；
4. `KnownModelKeys` 命中 → 原样返回（不归一化，保护 `tiered` 等合法变体）；
5. `ModelAliasMap` 精确映射（如 `gemini-3.6-flash`→`gemini-3.6-flash-high`、`gemini-flash-latest`→`gemini-3.6-flash-high`）；
6. `gemini-` 前缀兜底按系列归一（3.6/3.5/3-flash/3-pro/3.1-flash 分支）；
7. 其余（2.x 及更早、claude 原生名）原样传递。

**404 重试**：上游返回 404 时 `GetModelRetryCandidate` 对 3.x 系列给出同系列候选键（如未知 3.6 变体 → `gemini-3.6-flash-high`）；已是合法键返回 null。重试时 `ReplaceGeminiBodyModel` 只替换请求体 `model` 字段。

**维护注意**：上游模型键会随服务端更新而增减；新增模型时同步改 `ModelAliasMap`、`KnownModelKeys`、`FetchOpenAIModelListJsonAsync` 的 `defaultModels` 三处。`KnownModelKeys` 含 `gemini-3.6-flash-tiered`、`gemini-3-flash-agent`、`gemini-pro-agent`、`tab_flash_lite_preview` 等私有键。

### 5.4 OpenAI → Gemini 请求转换（ConvertOpenAIToGeminiRequest）

| OpenAI 概念 | Gemini 输出 |
|---|---|
| `messages[]` (chat) | `contents[]` |
| `system` 消息 | `systemInstruction.parts`（合并所有 system 消息） |
| `assistant` 消息 + `tool_calls` | `role:"model"`，parts 加 `functionCall{name,args}` |
| `tool` 消息 | `role:"user"` + `functionResponse{name,response:{result}}` |
| `input[]`/`instructions`/`prompt` (Responses/Completions) | 同上兼容（instructions→systemInstruction，input 数组逐项转 parts，`prompt`→user text） |
| `content` 数组元素 | 支持 `text`/`input_text`/`output_text`（→text）、`function_call`（→functionCall）、`function_call_output`（→functionResponse，**注意 name 用了 call_id**）、`image_url`/`input_image`（base64 data URL → `inlineData{mimeType,data}`，正则 `^data:(image\/...);base64,(.+)`） |
| `tools[]` | `functionDeclarations`，兼容两种格式：`{type:"function",function:{...}}`（Chat）与扁平 `{type:"function",name,...}`（AI SDK Responses） |
| `temperature`/`top_p`/`max_tokens` | `generationConfig.temperature/topP/maxOutputTokens` |
| `stream` | 布尔透出，决定走流式/非流式分支 |

### 5.5 Schema 白名单清洗（重点维护区，近期刚修复）

**问题背景**：AI SDK / OpenAI 客户端生成的 JSON Schema 常含 `propertyNames`、`const`、`$ref`、`examples` 等字段；Antigravity 上游 proto 严格校验，出现未知字段直接 400：`Invalid JSON payload received. Unknown name "propertyNames" at '...parameters.properties[1].value': Cannot find field.`（NarraFork 场景实测错误）。

**解决方案**（`GeminiProtocolTranslator.cs` 508 行起）：

```csharp
private static readonly HashSet<string> GeminiSchemaAllowedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
{
    "type", "format", "description", "nullable", "enum", "items", "properties", "required",
    "minItems", "maxItems", "minLength", "maxLength", "minimum", "maximum",
    "minProperties", "maxProperties", "pattern", "additionalProperties"
};
```

**`SanitizeGeminiSchema(JsonElement)` 递归规则**：
- `type` 数组：Gemini/Antigravity proto 的 `type` 是标量枚举，不能接收 JSON Schema 联合类型数组。`["string","null"]` 转为 `type:"string", nullable:true`，`null` 的顺序不影响转换；嵌套 `properties` / `items` 同样处理。多个非 null 类型近似取第一个，仅 null 或空数组用 `string` 兜底；这不是完整联合类型语义。`null` 分支优先于输入中的 `nullable:false`。类型归一化后仍执行缺失 array items 的补齐逻辑；
- 对象：只保留白名单键；`properties` 的**键是用户自定义属性名（任意）必须全部保留**，仅递归清洗其值；`additionalProperties` 仅保留布尔值（对象形式如 `{"type":"string"}` 不支持，丢弃）；清洗后空对象补 `"type":"object"`（避免上游拒绝空 schema）；
- 数组：逐项递归；
- 标量：原样保留。

**被移除的字段（探测确证上游不支持）**：`$schema`、`$id`、`$defs`、`definitions`、`propertyNames`、`patternProperties`、`const`、`$ref`、`$comment`、`enumDescriptions`、`enumTitles`、`prefill`、`deprecated`、`title`、`default`、`examples`、`uniqueItems`、`multipleOf`、`exclusiveMinimum`、`exclusiveMaximum`、`allOf`、`anyOf`、`oneOf`、`not`、`contains`、`minContains`、`maxContains`、`dependencies`、`propertyOrdering`、`readOnly`、`writeOnly` 等。

**验证方式**：在项目根目录运行 `dotnet run --project scripts/schema_regression_check.csproj -c Release --artifacts-path obj/quicker-check -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true`。测试项目直接引用主工程，自动编译当前源码，无需查找旧 DLL；产物留在被忽略的根目录 `obj/` 下。脚本通过反射调用实际转换器的私有 `SanitizeGeminiSchema`，验证未知字段移除、通用字段保留、用户属性名保留、空对象补 type；另调用公开请求转换入口，覆盖 Chat Completions / Responses 的流式及非流式工具定义、可空类型数组、嵌套 items、缺失 items、与 schema 关键字同名的用户属性。期望输出：`PASS: 白名单收敛验证通过`。测试不联网、不读取凭证。

**对照参考**：成熟 Go 网关 CLIProxyAPI 的 `internal/util/gemini_schema.go` 用黑名单 + 约束迁移到 description 的方式处理同一批字段（含同样的 propertyNames 400 历史修复）。本项目的白名单方案与它在字段交集上互相印证。

### 5.6 空响应重试（chat 与 responses 流式都有）

上游对"**系统提示强调工具 + 工具定义**"的请求可能返回空内容（有 candidates 但无文本无 functionCall）。两处流式处理器检测到 `chunksWithCandidates>0 && chunksWithText==0 && chunksWithFunctionCall==0` 时：移除请求体 `request.tools`/`toolConfig` 重试一次（`RemoveToolsFromGeminiBody`），让模型转为纯文本回答。统计日志：`[chat流] 上游 chunk 总数: N, 含文本: N, 含函数调用: N`。

### 5.7 Responses 流式事件序列（给客户端）

首个有效内容到达后才发送起始事件（避免空响应重试时客户端收到多余事件）：
```
response.created → response.item_added → response.output_text.delta* →
（工具调用时）response.function_call.added → response.function_call.arguments.delta
→ response.function_call.done →（最后）response.completed（extra_output_items 含 function_call 项）
```
异常时发 `response.incomplete`。chat 流式则发 `chat.completion.chunk`（delta.content / delta.tool_calls）→ `data: [DONE]`。

### 5.8 凭证管理（GetValidGeminiTokenForProxyAsync）

- **专一化**：软件仅支持 Antigravity / Gemini 凭证。`IsValidAuthJson` 在载入时硬过滤——凭证 JSON 若含 `type` 字段且非 `antigravity`/`gemini`（如 claude/codex/grok/kimi），直接跳过载入；`LoadAllAccounts` 返回的账号全部为 Gemini 账号且已排除禁用项，**无需二次平台过滤**；
- **多账号轮询**：`Interlocked.Increment(ref _localProxyAccountIndex) % count`，多个账号分摊请求；
- 读取凭证 JSON 的 `access_token`/`refresh_token`/`project_id`；
- Token 过期判断 `IsTokenExpired`（解析凭证内 `expired` 字段/时间戳），过期或缺失则 `RefreshAntigravityTokenAsync` 统一走 `oauth2.googleapis.com/token` 刷新（Google OAuth），成功后 `UpdateJsonToken` 回写文件；
- 拿不到 Token → 返回 null → 代理 503。

### 5.9 配额查询（功能 A 核心）

- Antigravity：`POST cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary`（body `{project: projectId}`，UA `antigravity`），解析 `groups` 数组按 displayName 分为「Gemini 模型分组」与「Claude/GPT 模型分组」，各自给出 5 小时/周额度百分比与重置时间（`FetchAntigravityQuotaAsync`）；Claude/GPT 分组为 Antigravity 接口返回的真实模型分组数据，非独立平台支持；
- **时间口径**：上游 `buckets[].resetTime` 给的是「重置的绝对时刻」，其中**满额冷态桶的那条是随查询浮动的假倒计时**（见 5.15），其余桶为真实到期时刻；界面显示的「X 小时 X 分 X 秒 后刷新」是 `FormatResetTime` 用该时刻减本机 UTC 算出的差值，1 秒一跳由本地 `_countdownTimer` 驱动，期间不再请求上游。故「重置在哪一刻」可信、「还剩多久」依赖 Windows 时钟准确；两次真查之间（`autoRefreshIntervalMinutes`，默认 5 分钟）百分比为旧值，网络故障时还会降级显示 `_lastGoodQuotas` 的上次成功数据；
- `5h`/`周` 两个标签与「Gemini 组 / Claude 组」归属都是按 `bucketId`、`displayName` 关键词匹配的启发式结果（上游未提供稳定枚举），Google 改命名会错标，但其中的百分比与 resetTime 仍是上游原值；
- 已移除 Claude（`api.anthropic.com/api/oauth/usage`）、Codex/Grok/Kimi 等非 Gemini 平台的额度查询与占位 stub；
- 详细背景见 `docs/AI平台OAuth额度查询机制深度分析报告.md`。

### 5.10 Gemini OAuth 登录（内置登录）

1. 本地 `HttpListener` 尝试端口 **51121–51130**，绑定 `/oauth-callback/`；
2. 拼 Google 授权 URL：`access_type=offline&prompt=consent&response_type=code&scope=<GeminiScopes>&state=<随机>`，用内置 `GeminiClientID`（`MainWindow.xaml.cs` 4319 行常量）与 `redirect_uri=http://localhost:{port}/oauth-callback`；
3. `Process.Start(UseShellExecute)` 打开浏览器；3 分钟超时；
4. 回调拿到 `code` → `POST oauth2.googleapis.com/token` 换 access/refresh token → `GET www.googleapis.com/oauth2/v2/userinfo` 拿邮箱 → 写凭证 `antigravity-{safeEmail}.json` 到数据目录 → `loadCodeAssist` 拿 project_id 存入凭证；
5. 回调页返回内嵌 HTML（登录成功/失败页面）。

### 5.11 本地代理在 GUI 中的生命周期

- 启动时 `InitLocalProxyServer`：绑定 Log 回调与凭证回调，设置 Port/ApiKey；若设置 `_isLocalProxyEnabled` 为 true 则自动 `StartLocalProxyServer`；
- 设置页开关、端口（1024–65535 校验）、API Key 修改即时生效（运行中则重启服务）；
- 设置持久化在 `settings.json`。

### 5.12 贴贴/悬浮小部件要点

- 三种模式 `off/taskbar/floating`，切换时销毁重建窗口；
- 任务栏模式：`SetParent` 到 `Shell_TrayWnd`，去 `WS_POPUP` 加 `WS_CHILD`，按任务栏高度压缩，`HideFromAltTab`；
- 悬浮模式：`244×92` 逻辑像素布局，边缘停靠 + 收纳把手（24px）+ 展开动画；鼠标穿透（`WS_EX_TRANSPARENT` + 低层钩子保右键菜单）；
- 顶部账号头为拖拽区；底部配额区单击切账号、双击刷新；强调色自动对比度适配；
- 数据由主窗口 `UpdateMiniWidgetData` 推送。

### 5.13 单实例与唤醒

`App.xaml.cs`：`Local\Haodo_SingleInstance_Mutex_V1` 互斥锁 + `Local\Haodo_SingleInstance_Event_V1` 事件。非首个实例：`EventWaitHandle.Set()`（主通道）+ `PostMessage(HWND_BROADCAST, WM_SHOWINSTANCE)`（备用）→ `Environment.Exit(0)`。主实例收到信号 → `ShowMainFromMini()`（Show + Activate + 置顶闪烁）把面板带到前台；若信号落在启动竞态窗口期（主窗口未创建完成），延迟 800ms 重试一次，保证「运行 exe → 面板必现」。`ShowMainFromMini()` 是贴贴模式下从托盘/二次启动唤醒主窗口的统一入口。

### 5.14 在线更新

- `CurrentBuildNum = major*100+minor*10+patch`（如 1.0.17 → 117）；
- 检查 `https://yusnip.top/version.json`，`build > CurrentBuildNum` 时弹更新窗（含 changelog 兜底文案）；
- 确认后下载 `download_url` 的 Haodo.exe → 结束自身进程树 → 覆盖根目录（实际部署路径为服务器 `https://yusnip.top/Haodo.exe`）→ 重新启动。

### 5.15 定时预热（冷态唤醒，判定一律以上游为准）

- **目的**：账号处于「5h 满额 + 上游无活跃窗口」的冷态时，发一次最小 `generateContent`（文本 `hi`、`maxOutputTokens=1`）把 Google 的滑动窗口**启动**起来，使窗口从此刻开始计时、若干小时后自然刷新到满额，用户真正要用时额度已就绪。候选模型轮退见 `GeminiWarmupCandidates`/`ClaudeWarmupCandidates`；
- **握手模型的硬约束（改名单前必读）**：`GeminiWarmupCandidates` **只能用 pro 档**（现名单 `gemini-3.7-pro-low` / `gemini-3.7-pro-high` / `gemini-3.1-pro-high`，取值均出自 `GeminiProtocolTranslator` 的映射表）。flash 档请求不计入 `gemini-5h` 桶，用它握手等于什么都没做（证据见下文「同日第三轮实测」）。候选轮退逻辑在 `WarmupAccountGroupDirectAsync`：某候选返回 404/400 时自动试下一个；
- **上游行为实测结论（2026-09-06 由 `[上游原始]` 诊断日志定案，判据的地基）**：
  - `retrieveUserQuotaSummary` 对**满额冷态桶**（`gemini-5h` / `3p-5h` 剩余 100%）返回的 `resetTime` 是「查询时刻 + 精确 300.0 分钟」的**浮动假倒计时**——每次查询都往后挪，它不代表任何真实窗口。识别特征就是那个「整 5 小时到秒」的取值；
  - 对**有活跃窗口的桶**（如当时 79% / 84% 的桶）返回的是钉死不动的固定到期时刻；
  - **同日第二轮实测（反向确认，判据成立的关键证据）**：三个账号的 `3p-5h` 都是满额 100%，而各自 `resetTime` 在 11:44:29→11:47:29 四路查询（跨 3 分钟）里完全不变（如 `08:34:36Z` 重复四次），只有「剩余分钟」随时间正常递减（290.1→289.1→288.1→287.1）——即 `drift≈0` → 判为「上游固定窗口倒计时中」→ 不握手，**符合预期**。这证明满额桶并非一律浮动，「有活跃窗口」与「冷态无窗口」在上游是两种可分辨的读数；
  - **同日第三轮实测（客户日志，暴露出「判定正确但动作无效」的第三种坑）**：某账号 `gemini-5h` 整轮日志里恒为 100% 且 `resetTime` 持续浮动 → 判定链每次都正确判「该唤醒」，而旧名单（flash 档）握手 **75 次全部 HTTP 200**，上游状态**一丝不变**。结论：**握手请求返回 200 ≠ 进了这个桶**，判定条件因此永久成立、每两轮就重发一次真实请求。同账号的 `3p-5h`（Claude 分组，名单本就是 opus/sonnet）同期 74 次判为「固定窗口倒计时中」、0 次握手 → 反证判定链本身没问题，错在发的模型；
  - 推论：**单条 `resetTime` 没有信息量**，「冷态」与「窗口刚启动且接近满额」两种状态读出来可能一模一样。分辨它们只能看这条值**随不随查询时刻移动**，所以软件不再本地推算任何窗口长度（5 小时兜底已彻底删除）；
  - 附带结论：账号若长期被本地代理驱动，其窗口会一直处于「已激活」态，预热因此**几乎不会触发**——这是正确行为，不是漏握手。要看预热真握手，需让某账号彻底闲置到其 5h 窗口跑完、`percent` 回到 100% 且 `resetTime` 变回「查询时刻 + 整 5 小时」。
- **触发**：`UpdateActiveCountdowns` 内 `_countdownTickCounter` 累计到 `WarmupScanIntervalTicks`(60 秒) 巡检一次，仅 `IsWarmupActive()` 为真时执行。该处**只做廉价预筛**「任一 5h 分组 `percent >= 100`」，绝不在此调用 `NeedsWarmup`（见下条副作用红线）；
- **判定链 `NeedsWarmup`（七步，全部取值来自上游）**：
  1. `percent < 100`（使用中）→ 跳过，绝不干扰正常使用；顺带 `NoteUpstreamChanged` 清零退避（桶开始消耗说明冷态已解除）；
  2. 上游没给 `resetTime`（或该分组「无数据」）→ 跳过，**拿不到真实依据就不发握手请求**；
  3. 采样不足 → 只登记基准不判定：首轮无基准、间隔 < `WarmupMinSampleGapSeconds`(40 秒)、基准超 `WarmupMaxSampleGapMinutes`(30 分钟，软件闲置后基准失效需重取)。**该下限必须明显低于 60 秒的巡检周期**：两者贴平时时钟一抖整轮比对就作废，日志表现为反复「样本间隔仅 34/51/59 秒」且判定来回晃（曾把两个值都写成 60 秒踩过这个坑）；
  4. **核心判据（两点漂移）**：`drift = 本次 resetTime − 上次 resetTime`。`resetTime` 仍在未来且 `drift < 采样间隔的一半` → 上游有真实窗口在倒计时 → 跳过（同样 `NoteUpstreamChanged` 清零退避）；`drift` 接近采样间隔（跟着查询时刻往后挪）或到期时刻已经过去 → 认定无活跃窗口 → 进入唤醒流程。阈值取半间隔是因为浮动时 `drift≈间隔`、钉住时 `drift≈0`（仅秒级取整噪声），两侧都有量级余量；**不要改成固定秒数下限**，曾写成 `max(60, 间隔一半)` 时巡检间隔恰好 60 秒会卡在临界线上，上游一次取整就把冷态误判成"倒计时中"而永不唤醒；
  5. 距上次握手 < `WarmupDebounceSeconds`(90 秒) → 防抖，等上游 summary 同步；
  6. **未见效退避闸门**：退避截止时刻 `BackoffUntilUtc` 尚未到期 → 跳过并打「已唤醒 N 次而上游状态未变，退避中 (HH:mm 前不再握手)」。存在的理由就是第三轮实测那种「判定永远成立 + 握手永远 200」的死循环：只限制**我们自己发请求的频率**，不宣称任何窗口长度，因此不违反「以上游为准」；退避期间第 4 步的 `TakeSample` 照常执行，样本不会断档，退避一到就能立即比对；
  7. 满额 + 上游无活跃窗口 + 过完上述所有闸门 → 定向握手。**计数在发请求之前**：`WakeAttempts++`，若此前已有过尝试则立刻按 `IneffectiveWakeBackoffMinutes` 设下一次退避截止（第 1 次无效后 5 分钟、第 2 次后 30 分钟、此后每轮 180 分钟），日志原因后缀「第 N 次尝试」。放在前面是为了握手失败（候选全 404）时也照样退避。
- **副作用红线**：`NeedsWarmup` 判定即采样（内部 `TakeSample` 刷新基准），因此**一轮真查里每个分组只能调用它一次，且必须喂 `FetchRealTimeQuotaAsync` 的新鲜读数**。任何拿缓存快照去调它的写法都会污染样本对，把真实比对带偏——这是旧版「缓存预判 + 上游改判」两次调用被拆掉的根因；
- **握手前必做上游复核**：`_cachedQuotas` 最长滞后一个自动刷新周期（默认 5 分钟），拿旧快照拍板会把「其实还在倒计时」与「其实早已重置」都判反。故流程是「缓存只预筛 → 真查一次 → 每组判定一次」；真查失败（网络/代理故障）时**保守不握手**；
- **握手成功后的处理**：`RecordWarmupHandshake` 记下握手时刻（仅供第 5 步防抖）并**清空采样基准**。必须清空：握手前那条浮动值与握手后的真实到期时刻几乎重合（都约等于「现在 + 5h」），留着旧基准会把真窗口误判成浮动值而反复握手烧额度。清空后由握手后的两两采样重新识别——此时上游回的是钉死时刻，`drift≈0` → 判为「上游固定窗口倒计时中」，不再握手；
- **退避只能由上游清零**：`RecordWarmupHandshake` **不碰** `WakeAttempts`/`BackoffUntilUtc`。把「我们发出了 200 的握手」当成「唤醒已生效」正是那 75 次无效握手的成因；唯一合法的清零入口是上游读数本身变了（第 1 步开始消耗、或第 4 步出现钉死窗口），即 `NoteUpstreamChanged`；
- **持久化**：记录写在 `settings.json` 的 `warmupRecords`，键为 `凭证绝对路径:分组`，值为 `lastWarmupUtc`/`observedResetUtc`/`observedAtUtc`/`wakeAttempts`/`backoffUntilUtc`；**本轮只要发出过握手请求就 `SaveSettings`**（不只是成功时，否则候选全 404 的账号重启后计数归零、继续每两轮重发）。加载时过期退避连同计数直接丢弃、采样基准超 7 天也丢弃；落盘前剪掉凭证已删除，以及握手、采样基准、退避三者皆陈旧的记录。**不参与 Drive 云同步**（`CollectSyncConfig` 不含它）——键含本机绝对路径，跨机同步会误压制唤醒；
- **诊断手段**：`FetchAntigravityQuotaAsync` 的 bucket 循环会打 `[上游原始] …resetTime=… (剩余 X 分钟) → 分组槽位`，用于核对上游到底给了什么、以及 `5h`/`weekly` 关键词归类是否猜对；排查预热不触发时先看这几行两次读数的分钟数是否在移动。
- **旧实现缺陷（2026-09-06 改掉）**：曾以「上次握手时刻 + 固定 5 小时」作为唯一闸门，上游实际窗口更短时会错压数小时不再唤醒；且该字典纯内存，重启即归零。现**软件自身不再推算任何窗口长度**，全部判据来自上游返回值及其观测时刻。
- **同日第二处缺陷（客户日志暴露，已改）**：判定链换成上游驱动后，握手名单里仍是 flash 档模型 —— 结果同一账号 75 次握手全部返回 HTTP 200，`gemini-5h` 却全程 100% + `resetTime` 恒定浮动，等于白白发真实请求。根因是**旧流程只以「200」自我确认，从不回看上游有没有因此变化**。现两处收口：名单换成 pro 档（见本节开头那条硬约束），并加第 6/7 步的「未见效退避」计数。

---

## 6. 配置与数据存储

**数据目录（唯一事实源）**：软件同级 `data\` 文件夹（`_dataDir = BaseDirectory\data`，v1.0.25 起为唯一路径；旧版 `%AppData%\Haodo` 兜底机制已移除）。
- 配置：`settings.json`（仅此一份，无兜底副本、无双写）
- 凭证：`*.json`（如 `antigravity-xxx@gmail.com.json`），文件名含邮箱；每个凭证含 `access_token`/`refresh_token`/`project_id`/`expired` 等字段；禁用标记写在文件内（`IsJsonFileDisabled`/`SetJsonFileDisabled` 读写 `disabled` 字段）。**v1.0.27 起专一化**：`IsValidAuthJson` 硬过滤凭证 `type` 字段，仅 `antigravity`/`gemini` 类型载入，其余平台（claude/codex/grok/kimi 等）一律跳过。

**唯一路径语义**：`_settingsPath` 所在目录即 `_dataDir`，二者恒绑定；启动/换目录/刷新均以此为准，不做 AppData 二选。切换数据目录时配置随迁（旧目录 `settings.json` 复制到新目录后删除残留）。

**迁移收尾**（v1.0.25 构造函数内一次性逻辑）：若旧版残留 `%AppData%\Haodo\settings.json`——数据目录尚无配置则复制过去（不丢旧配置），随后删除兜底副本；更早版本的软件根目录 `settings.json` 残留同理复制进 `data\`。

**settings.json 主要字段**（LoadSettings 逐字段读取，缺失用默认值）：

```text
isMiniOn, miniModeType("off"/"taskbar"/"floating"), miniWidgetBgColor, miniWidgetBgOpacity,
miniWidgetIsTransparent, miniWidgetEdgeDock, miniWidgetHideCardBg, miniWidgetLeft/Top/Width/Height,
mainWinLeft/Top/Width/Height, mainWinState, theme(系统/深色/浅色), dataDir(仅记录，读取时忽略不一致),
autoRefreshInterval, autoCheckUpdateEnabled(自动检查更新), autoStartEnabled(开机自启动),
warmupEnabled(定时预热开关), warmupRecords(预热判定记录，键"凭证绝对路径:分组"，含上游采样基准与未见效退避计数，仅本机不同步),
isLocalProxyEnabled, localProxyPort(默认8317), localProxyApiKey(默认"sk-haodo-local"),
maskAccountInfo(账号打码), 以及贴贴强调色/背景色等
```

**注意**：`settings.json` 会被凭证导入对话框主动排除（`IsValidAuthJson` 与文件列表渲染都跳过它），不要把配置当凭证导入。

**刷新按钮**（v1.0.24 起）：先 `LoadSettings` 重载磁盘最新配置（手动编辑也生效），再归集扫描凭证并 `SaveSettings` 写回——文件 ↔ 内存 ↔ 界面三者一致。

---

## 7. 构建与发布

### 7.1 版本管理

1. 改版本 → 只编辑根目录 `版本号.txt`（如 `1.0.17`），**不要**在 csproj 或代码里改版本字符串（程序集版本与"关于"徽标都从它动态生成）；
2. csproj `ValidateVersionFile` 会在构建前校验文件存在且内容合法，否则报错；
3. Build 号 `major*100+minor*10+patch` 用于在线更新比较。

### 7.2 本地构建入口

| 入口 | 脚本 | 行为 |
|---|---|---|
| `仅编译.bat` | `scripts\build_only.ps1` | ① 读版本号 → ② 强杀运行中的 Haodo.exe → ③ `dotnet publish -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false`（**框架依赖极轻单文件**，仅约 940KB，开源无混淆，目标机器需安装 .NET 10 Desktop Runtime）→ ④ 复制 `Haodo.exe` 到根目录 → ⑤ 清理临时构建目录与符号 |
| `编译并发布.bat` | `scripts\build.ps1` | ① 执行上述编译流程生成单文件 `Haodo.exe` → ② 自动联动 `scripts\deploy.ps1` 上传服务器并发布 `version.json` |
| `一键更新发布.bat` | `scripts\deploy.ps1` | 直接将根目录下已有的 `Haodo.exe` 上传服务器并更新线上 `version.json` |

### 7.3 在线更新机制说明

客户端内置在线更新检查逻辑（请求 `https://yusnip.top/version.json` 获取最新发布版本号与下载地址）：
1. 读线上 `version.json`，比较 `build` 号与当前本地版本；
2. 若存在新版本且非静默模式，提示用户下载更新 `https://yusnip.top/Haodo.exe`；
3. 私有运维发布脚本（原 `deploy.ps1` / `build.ps1`）属于服务端运维工具，已从开源仓库中剔除。

**本地构建前置检查**：
1. `版本号.txt` 是目标版本；
2. .NET 10 SDK 可用；
3. 运行 `仅编译.bat` 验收；
4. 根目录生成最新 `Haodo.exe`，不存在 `Haodo.pdb`；源码目录无 `src\bin`/`src\obj`；
5. `%TEMP%\HaodoBuild` 已自动清理。

### 7.4 单文件轻量打包体系（v1.0.38 起全开源无混淆）

**为什么移除混淆**：
1. **拥抱开源与透明度**：项目全面开源，代码与程序集透明可见，方便社区审计与贡献，无需再引入商业/代码保护混淆；
2. **极简构建工具链**：移除了 Obfuscar 3.0 工具包依赖与 Python 资源段修复脚本（`fix_obfuscated_rsrc.py`），用户只需具备标准的 `.NET 10 SDK` 即可一键无痛编译；
3. **原生极致体积与可靠性**：直接利用 .NET SDK 官方 `PublishSingleFile=true` 单文件打包，去除调试符号后最终 `Haodo.exe` 仅约 **930 KB**（比原混淆版的 1005 KB 更小更轻量），完全避免了混淆器修改 PE 头导致的各类潜在崩溃与 Win32 资源段脱节缺陷。

**打包参数配置**：
```bash
dotnet publish src/CLIProxyAPI_GUI.csproj \
  -c Release \
  -r win-x64 \
  --no-self-contained \
  -p:PublishSingleFile=true \
  -p:DebugType=None \
  -p:DebugSymbols=false \
  -o %TEMP%\HaodoBuild\publish
```
- `--no-self-contained`：采用框架依赖模式，单文件体积保持在 1MB 以内（目标机器需安装 .NET 10 Desktop Runtime）；
- `PublishSingleFile=true`：将托管 DLL、静态资源及 AppHost 启动器打成单个便携式 `Haodo.exe`；
- `DebugType=None` + `DebugSymbols=false`：剥离 PDB 调试符号，进一步压缩体积并保护二进制整洁。

---

## 8. 维护指南与故障排查

### 8.1 日常维护任务速查

| 任务 | 步骤 |
|---|---|
| 改一个 UI 文案 | `MainWindow.xaml` / 对应 cs 里字符串 → 编译 → 回归 → 写记录 |
| 新增设置项 | 字段 + `LoadSettings`/`SaveSettings` 各加一段 + XAML 控件 + UI 刷新方法 → 同上 |
| 上游新增模型 | 改 `ModelAliasMap`/`KnownModelKeys`/`defaultModels` 三处（4.10/5.3） |
| 客户端报 schema 400 | 见 8.2 第 2 条 |
| 升级版本 | 改 `版本号.txt` → `仅编译.bat` |
| 验证 schema 清洗 | 跑 `scripts/schema_regression_check.cs`（需先编译出 Haodo.dll） |
| 排查代理问题 | 打开软件设置页日志区（`TxtLog`，所有代理请求/错误都有 `[本地代理 API]` 前缀日志） |

### 8.2 故障排查手册

| 症状 | 原因 | 处理 |
|---|---|---|
| `400 Unknown name "propertyNames" ... Cannot find field` | schema 含上游不支持字段 | 白名单清洗（5.5）已覆盖；若出现新字段名，把它加入被移除集合并跑回归 |
| `400 Unknown name "type" ... Proto field is not repeating, cannot start list` | 工具参数的 `type` 是数组（常见于可空联合类型），但上游要求单个枚举 | v1.0.50 起递归归一化 type 数组并保留 nullability（见 5.5）；更新后重启 Haodo 再试 |
| `404 Requested entity was not found` | 模型名不是上游合法键 | 检查 `MapModel` 与 `KnownModelKeys`；`GetModelRetryCandidate` 自动兜底一次 |
| 上游 404 但代码没重试 | 模型不是 gemini- 前缀 | `GetModelRetryCandidate` 只处理 gemini- 系列 |
| 空响应（无文本无工具） | 上游对"系统提示强调工具+工具定义"返回空 | 自动移除 tools 重试（5.6）；日志可见 `[chat流]/[Responses流] ... 移除 tools 后重试` |
| 请求返回 503 `No valid logged-in Gemini account` | 无有效账号 / Token 刷新失败 | 检查 Haodo 是否登录账号、凭证文件是否过期 |
| 401 `Invalid API Key` | 客户端 Key 与设置不一致 | 检查 `Authorization` 头与设置页 Key（默认 `sk-haodo-local`） |
| 端口占用启动失败 | 其他程序占用 8317 | 设置页改端口（1024-65535） |
| `dotnet publish` 报版本错误 | `版本号.txt` 缺失/非法 | 检查根目录文件 |
| 覆盖 Haodo.exe 失败 | 进程还在运行 | 脚本会自动强杀；手动时先结束进程 |
| 客户端探测 `/v1/embeddings` | 无真实嵌入能力 | 代理返回占位假数据（设计如此） |
| `/v1beta/models/*` 请求异常 | 原生转发到公开 Gemini API | 检查路径大小写与账号权限 |

### 8.3 回归验证

1. 编译：`仅编译.bat`（或 `dotnet publish` 到临时目录）；
2. Schema 回归：`scripts/schema_regression_check.cs` → 期望 `PASS: 白名单收敛验证通过`；
3. 手工端到端（需要已登录账号）：启动软件 → 开启本地代理 → 用 curl/客户端打 `http://127.0.0.1:8317/v1/chat/completions`（流式与非流式、带工具与不带工具各测一遍）与 `/v1/responses`；
4. 检查设置页日志无异常，配额卡片刷新正常。

---

## 9. 已知边界与未来改进

### 已知边界（设计如此，勿当 bug 修）

1. **`/v1/embeddings` 返回占位假数据**——本代理无真实嵌入能力，仅防客户端探测报错；
2. **`function_call_output` 的 `name` 兜底链为 `item.name → call_id → "tool_result"`**——
   真实客户端（NarraFork）回传的 function_call_output 通常只有 call_id，转换时以 call_id 作为
   name；该行为已被上游实测接受（2026-08-07 两轮 responses 端到端验证通过）；
3. **Responses 历史消息的 function_call_output 无法还原真实函数名**（协议里只有 call_id，
   且客户端回传时不带 name）——见 `AddFunctionCallOutputPart` 兜底链；如需精确匹配工具名，
   可扩展为按同请求 function_call 的 call_id → name 映射还原；
4. **schema 清洗是"丢弃"而非"语义迁移"**——`examples`/`pattern`/`format` 等直接删除，模型少一点提示；CLIProxyAPI 会把这些并入 description（更精细，非必须对齐）；
5. **`anyOf`/`oneOf` 直接丢弃**（不 400，但工具参数类型信息可能退化）；
6. **代理只监听 127.0.0.1/localhost**，不暴露局域网（安全设计）；
7. **HTTPS 方案已废弃（2026-08-07 回滚）**：自签名证书不被不读 Windows 证书库的
   客户端（OpenCode Desktop 等 OpenSSL/Node 系）信任，无法实用化。客户端需
   https 地址时可填 `http://127.0.0.1:8317`（多数本地模型客户端支持）；
   若客户端强制 https，可用 `NODE_EXTRA_CA_CERTS` 指向 CA pem 或
   `NODE_TLS_REJECT_UNAUTHORIZED=0` 绕过（代码与证书残留均已清理）；
8. **更新服务器**为固定域名（yusnip.top），迁移需同步改 `VersionJsonUrl` 与 `MainWindow.xaml.cs` 的更新逻辑；
9. **满额冷态桶的上游 `resetTime` 是「查询时刻 + 5h」的浮动假倒计时**（2026-09-06 实测，见 5.15）——单条读数无法区分「冷态无窗口」与「窗口刚启动且近满额」，只能靠两次采样的漂移识别。代价是唤醒前需攒够两个样本（约 2 分钟），**这是设计如此**：不要改回单轮判定，也不要恢复任何本地时长推算（旧的 5 小时乐观兜底已删除）；
10. **预热握手返回 HTTP 200 ≠ 请求进了那个配额桶**（2026-09-06 客户日志实测，见 5.15 第三轮）——flash 档请求不计入 `gemini-5h`，用它唤醒会「永远成功、永远无效」。现两道防线：Gemini 分组只走 pro 档候选，以及「同一冷态下上游状态仍未变化就按 5→30→180 分钟退避」的计数闸门。

### 未来改进方向

- schema 约束字段迁移到 description（对齐 CLIProxyAPI 的 moveConstraintsToDescription）；
- union 展平（flatten anyOf/oneOf）；
- 响应 usage token 统计（当前 Responses 流式 completed 中 token 传 0）；
- 代理支持多 Key 映射（当前仅单一 ApiKey）；
- 端到端自动化测试（当前 schema 回归是单元级，上游链路依赖真实账号）。

---

## 10. 安全红线

1. **绝不把 OAuth 凭证、access_token、refresh_token、用户邮箱写入文档或提交到版本库**（旧指南同样强调）；
2. **私有部署运维脚本已彻底从源码仓库隔离**——包含服务器权限的运维脚本禁止上传公开仓库；
3. `%AppData%\Haodo` 下的凭证文件是敏感数据，排障时如需贴日志请先脱敏（邮箱打星，参考 `GetDisplayEmail`）；
4. 不要在文档中记录真实 API Key 以外的密钥（`GeminiClientID` 是公开 OAuth 客户端 ID，位置在 `MainWindow.xaml.cs` 常量，无需也不应改动）；
5. 修改鉴权逻辑时保持默认 Key 可被用户在设置页修改的行为，不要把 Key 硬编码写死；
6. 代理的 `ApiKey` 仅保护本机回环端口，**不要**把端口暴露到公网（`HttpListener` 前缀为 127.0.0.1/localhost，勿改为 `+` 或 `*`）。

---

*本文件由 AI 维护助手在 2026-08-07 基于源码逐文件核对生成；此后任何代码变更必须同步更新本文件。*

### 2026-10-13 · v1.0.50：Quicker agent 工具 Schema 兼容修复

- 来源核对：Haodo 使用参考 CLIProxyAPI 的 C# 原生转换器，并未嵌入 Go 内核或通过包管理引用其版本。上游最新发布为 [v8.0.27](https://github.com/router-for-me/CLIProxyAPI/releases/tag/v8.0.27)（查询日期 2026-10-13），不能直接替换为 Haodo 内核；本次仅移植与报错有关的类型数组处理，未声称完整同步该版本。
- 同类上游记录：[Issue #1513](https://github.com/router-for-me/CLIProxyAPI/issues/1513)、[已合并 PR #1511](https://github.com/router-for-me/CLIProxyAPI/pull/1511)，以及当前 `internal/util/gemini_schema.go` 的 schema 归一化流程。
- 本地合成请求在修复前复现 type 数组透传、nullable 丢失以及 array items 补齐失效；修复后原白名单用例和四种请求组合全部通过。
- 尚未取得 Quicker 的完整原始请求，也未调用真实上游验证；本地回归确认的是协议转换缺陷已修正，最终需要在 Quicker 中重新运行原 agent 验证。
