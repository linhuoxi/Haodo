# BalanceViewer (Haodo) 智能体软件维护文档

> **文档定位**：本文件是给**后来者 AI 与开发者**的完整软件说明书与维护手册。任何 AI 或人类开发者拿到本文件 + 源码，即可完全理解本软件的结构、机制、构建方式与维护流程。
>
> **当前版本**：v1.0.17（见项目根目录 `版本号.txt`）
> **目标平台**：Windows x64（原生 Windows，不使用 WSL）
> **技术栈**：.NET 10、WPF、WinForms（NotifyIcon）、HttpListener、SSE 流式转发
> **最后更新**：2026-08-07
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
- [9. 更新记录规范（强制要求）](#9-更新记录规范强制要求)
- [10. 历史变更记录](#10-历史变更记录)
- [11. 已知边界与未来改进](#11-已知边界与未来改进)
- [12. 安全红线](#12-安全红线)

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
| 改完代码必须做什么 | [第 9 章](#9-更新记录规范强制要求)（**写更新记录，否则违规**） |
| 哪些事绝对不能做 | [第 12 章](#12-安全红线) |

**三条最重要的常识**：

1. **本软件 = 配额查看器 + 本地 Gemini API 代理，两个功能共享同一个 GUI 进程**。代理功能是后期加入的，旧文档（`docs/MAINTENANCE.md`）只描述配额查看器，已过时，以本文件为准。
2. **代理的后端是 Google Antigravity 私有上游**（`cloudcode-pa.googleapis.com`），不是公开的 `generativelanguage.googleapis.com`。它有一堆私有行为（按 UA 放行模型、schema 严格校验 400、空响应等），[第 5 章](#5-核心机制深度解析) 的所有"坑"都是真实验证过的。
3. **每次修改代码后必须在 [第 9 章](#9-更新记录规范强制要求) 追加记录**，这是用户明确要求的项目规范。

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
├── Haodo.exe                  ← 发布产物（框架依赖单文件 + Obfuscar 混淆版，约 900KB），随版本更新
├── 版本号.txt                 ← 版本号唯一来源（当前 1.0.17；兼容旧名 VERSION.txt）
├── 仅编译.bat                 ← 本地编译入口（调 scripts\build_only.ps1）
├── 编译并发布.bat             ← 编译+云端发布入口（调 scripts\build.ps1）
├── 一键更新发布.bat           ← 仅发布现有 Haodo.exe（调 scripts\deploy.ps1）
├── 维护文档\
│   └── MAINTENANCE.md         ← 本文档（唯一维护记录归档处）
├── docs\
│   ├── MAINTENANCE.md         ← 旧版维护指南（仅覆盖配额查看器，已过时，保留作历史）
│   └── AI平台OAuth额度查询机制深度分析报告.md ← CLIProxyAPI 配额机制研究报告（背景资料）
├── scripts\
│   ├── build.ps1              ← 编译 + Obfuscar 混淆 + 修复资源段 + 重打包，随后调用 deploy.ps1
│   ├── build_only.ps1         ← 仅编译混淆，覆盖根目录 Haodo.exe（详见 7.2）
│   ├── fix_obfuscated_rsrc.py ← 修复 Obfuscar 3.0 混淆后 Win32 资源段 RVA 缺陷（详见 7.4）
│   ├── deploy.ps1             ← 上传服务器 + 更新 version.json（含内嵌 SSH 私钥，勿外泄）
│   └── schema_regression_check.cs ← Schema 白名单回归验证脚本（反射调用真实代码）
├── tools\
│   └── Obfuscar\              ← Obfuscar 3.0 GlobalTool 解包产物（GlobalTools.dll，混淆器本体）
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
| `Haodo.exe` | 编译产物，发布到服务器给用户下载 | 由构建脚本生成；**不要**手动编辑；提交前确认是最新构建 |
| `版本号.txt` | 版本唯一来源，三段式如 `1.0.17` | 升级版本只改这个文件；csproj 构建时自动读取并生成 `Version`/`AssemblyVersion`/`FileVersion`/`ProductVersion` |
| `仅编译.bat` | `powershell scripts\build_only.ps1` | 日常自测用 |
| `编译并发布.bat` | `powershell scripts\build.ps1` | 上线用 |
| `一键更新发布.bat` | `powershell scripts\deploy.ps1` | 只发 exe 不改源码时用 |

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
- 对象：只保留白名单键；`properties` 的**键是用户自定义属性名（任意）必须全部保留**，仅递归清洗其值；`additionalProperties` 仅保留布尔值（对象形式如 `{"type":"string"}` 不支持，丢弃）；清洗后空对象补 `"type":"object"`（避免上游拒绝空 schema）；
- 数组：逐项递归；
- 标量：原样保留。

**被移除的字段（探测确证上游不支持）**：`$schema`、`$id`、`$defs`、`definitions`、`propertyNames`、`patternProperties`、`const`、`$ref`、`$comment`、`enumDescriptions`、`enumTitles`、`prefill`、`deprecated`、`title`、`default`、`examples`、`uniqueItems`、`multipleOf`、`exclusiveMinimum`、`exclusiveMaximum`、`allOf`、`anyOf`、`oneOf`、`not`、`contains`、`minContains`、`maxContains`、`dependencies`、`propertyOrdering`、`readOnly`、`writeOnly` 等。

**验证方式**：改白名单后运行回归脚本 `scripts/schema_regression_check.cs`（需先编译 src 生成 Haodo.dll；脚本通过反射调用真实编译产物中的私有 `SanitizeGeminiSchema`，用覆盖全部攻击面的合成 schema 断言：400 源字段必须清除、通用字段（bool additionalProperties/nullable/format/enum/minimum 等）必须保留、属性名恰好叫 `propertyNames` 的键必须保留、空对象补 type）。期望输出：`PASS: 白名单收敛验证通过`。

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
autoRefreshInterval, isLocalProxyEnabled, localProxyPort(默认8317), localProxyApiKey(默认"sk-haodo-local"),
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

### 7.2 三个构建入口

| 入口 | 脚本 | 行为 |
|---|---|---|
| `仅编译.bat` | `scripts\build_only.ps1` | ① 读版本号 → ② 强杀运行中的 Haodo.exe → ③ `dotnet publish -c Release -r win-x64 --no-self-contained`（首次全量编译，产出干净中间程序集；**框架依赖**，单文件约 900KB，目标机器需安装 .NET 10 Desktop Runtime）→ ④ 用 `tools\Obfuscar\GlobalTools.dll` 混淆中间 `Haodo.dll` → ⑤ 用 `scripts\fix_obfuscated_rsrc.py` 修复混淆产物的 Win32 资源段 RVA 缺陷 → ⑥ 用修复后的混淆 dll 覆盖 obj 中间产物 → ⑦ `dotnet publish --no-build` 重新打包单文件（不重新编译，防止混淆被冲掉）→ ⑧ 复制 `Haodo.exe` 到根目录 → ⑨ 删根目录 `Haodo.pdb` + 清 `%TEMP%\HaodoBuild` |
| `编译并发布.bat` | `scripts\build.ps1` | 同上的 ①-⑨，随后调用 `deploy.ps1` |
| `一键更新发布.bat` | `scripts\deploy.ps1` | 不编译，直接用根目录现有 `Haodo.exe` 发布 |

### 7.3 云端发布（deploy.ps1）

1. 读版本号 → 自动算 Build 号（版本三段式解析失败时拉线上 version.json 的 build+1）；
2. 用**脚本内嵌的 ED25519 SSH 私钥**（写入 `%TEMP%\haodo_deploy_id_ed25519`，并设置 ACL 仅当前用户）连接 `ai-ops@39.106.203.8`；
3. `scp` 上传 `Haodo.exe` → 远程 `sudo mv` 到 `/var/www/website1/Haodo.exe`，`chmod 644` + `chown www-data:www-data`；
4. 生成 `version.json`（name/version/build/download_url=`https://yusnip.top/Haodo.exe`/updated_at/changelog/force_update）→ base64 传输 → `sudo tee /var/www/website1/version.json`；
5. 公网校验 `Invoke-RestMethod https://yusnip.top/version.json`。

**发布前置检查**（来自旧指南，仍然有效）：
1. `版本号.txt` 是目标版本；
2. .NET 10 SDK 可用；
3. 先 `仅编译.bat` 本地验收；
4. 根目录存在最新 `Haodo.exe`，不存在 `Haodo.pdb`；源码目录无 `src\bin`/`src\obj`；
5. `%TEMP%\HaodoBuild` 已清理。

### 7.4 Obfuscar 混淆集成（v1.0.27 起）

**为什么引入**：用户要求发布物经受混淆保护，选用免费开源混淆器 **Obfuscar 3.0**（GlobalTool 解包产物，本地部署于 `tools\Obfuscar\GlobalTools.dll`，离线可用）。

**混淆配置要点**（构建脚本在 `%TEMP%\HaodoBuild\obfuscar.xml` 动态生成）：
- `HidePrivateApi=true` + `KeepPublicApi=true` + `HideStrings=true`（字符串加密隐藏）+ `AnalyzeXaml=true`（XAML 引用扫描，避免误重命名被 BAML 引用的成员）+ `UseUnicodeNames=true`（Unicode 重命名，反编译更难看）+ `SuppressIldasm=true`（禁用 ILDASM 反汇编）；
- **`SkipType` 保留两类反射依赖类型，混淆后必须保留原名**：
  - `CLIProxyAPI_GUI.LocalProxyServer` —— 由 UserControl 在 XAML 解析时经反射创建，改名后反射找不到类型（运行期 XAML 崩溃）；
  - `CLIProxyAPI_GUI.DpiBootstrap` —— `[ModuleInitializer]` 入口程序集静态构造引用，混淆重写后启动即崩溃。

**构建流程**：publish 首次全量编译 → 用 Obfuscar 混淆 **obj 中间产物**（`%TEMP%\HaodoBuild\artifacts\obj\CLIProxyAPI_GUI\release_win-x64\Haodo.dll`）→ 修复资源段 → 覆盖 obj dll → `dotnet publish --no-build` 重打包单文件。**不能**直接混淆最终 exe（单文件是自解压 bundle，混淆需在托管程序集层面做）。

### 7.5 混淆后 Win32 资源段 RVA 修复机制（关键）

**缺陷背景**：Obfuscar 3.0 重写 PE 时把 PE32+ (x64) 改写为 PE32 格式，在扩展 `.text` 段的同时把 `.rsrc` 段整体后移（本工程实测：基址 `0x7C000 → 0x8A000`，**DELTA = +0xE000**），但**资源树 9 个叶子节点的 `OffsetToData`（绝对 RVA）未同步更新**——数据仍指向旧地址。单文件重打包时 SDK 的 `CreateAppHost` 任务读取该资源段做版本/图标合并，越界抛 `ArgumentOutOfRangeException` 崩溃（`dotnet publish --no-build` 报错），同时混淆 dll 的图标/版本信息也丢失。

**修复方案**（`scripts/fix_obfuscated_rsrc.py`）：
1. 输入三个文件：原始干净 dll（obj 中间产物）、混淆 dll（Obfuscar 输出）、输出路径；
2. 分别解析两个 PE 的节表，定位 `.rsrc` 段的 `VirtualAddress`（RVA）；
3. 计算 `DELTA = 混淆版 .rsrc RVA - 原版 .rsrc RVA`（本工程为 `+0xE000`；脚本自动计算，不硬编码）；
4. 遍历混淆 dll 的资源树（IMAGE_RESOURCE_DIRECTORY / IMAGE_RESOURCE_DATA_ENTRY），把**所有叶子节点**的 `OffsetToData` 加上 DELTA；
5. 写回时自动内置校验：重解析输出文件，确认叶子条目数一致、每个 `OffsetToData` 都落在 `.rsrc` 段内，否则报错退出（防误修）；
6. 输出修复版 dll（构建脚本用它覆盖 obj 中间产物后再 `--no-build` 重打包）。

**维护注意**：
- 若 Obfuscar 版本升级导致 DELTA 变化，脚本自动适应（不硬编码）；若报"校验失败"，多半是 Obfuscar 输出布局变化，先手工比对节表；
- 本机需可用 `python`（PATH 中）；构建脚本在修复步骤前检查工具与脚本存在性；
- 修复前后的 dll 均可正常加载运行（修复只影响资源目录的 RVA 指向），修复是幂等的前置步骤。

---

## 8. 维护指南与故障排查

### 8.1 日常维护任务速查

| 任务 | 步骤 |
|---|---|
| 改一个 UI 文案 | `MainWindow.xaml` / 对应 cs 里字符串 → 编译 → 回归 → 写记录 |
| 新增设置项 | 字段 + `LoadSettings`/`SaveSettings` 各加一段 + XAML 控件 + UI 刷新方法 → 同上 |
| 上游新增模型 | 改 `ModelAliasMap`/`KnownModelKeys`/`defaultModels` 三处（4.10/5.3） |
| 客户端报 schema 400 | 见 8.2 第 2 条 |
| 升级版本 | 改 `版本号.txt` → `编译并发布.bat` |
| 只重发 exe | `一键更新发布.bat` |
| 验证 schema 清洗 | 跑 `scripts/schema_regression_check.cs`（需先编译出 Haodo.dll） |
| 排查代理问题 | 打开软件设置页日志区（`TxtLog`，所有代理请求/错误都有 `[本地代理 API]` 前缀日志） |

### 8.2 故障排查手册

| 症状 | 原因 | 处理 |
|---|---|---|
| `400 Unknown name "propertyNames" ... Cannot find field` | schema 含上游不支持字段 | 白名单清洗（5.5）已覆盖；若出现新字段名，把它加入被移除集合并跑回归 |
| `404 Requested entity was not found` | 模型名不是上游合法键 | 检查 `MapModel` 与 `KnownModelKeys`；`GetModelRetryCandidate` 自动兜底一次 |
| 上游 404 但代码没重试 | 模型不是 gemini- 前缀 | `GetModelRetryCandidate` 只处理 gemini- 系列 |
| 空响应（无文本无工具） | 上游对"系统提示强调工具+工具定义"返回空 | 自动移除 tools 重试（5.6）；日志可见 `[chat流]/[Responses流] ... 移除 tools 后重试` |
| 请求返回 503 `No valid logged-in Gemini account` | 无有效账号 / Token 刷新失败 | 检查 Haodo 是否登录账号、凭证文件是否过期 |
| 401 `Invalid API Key` | 客户端 Key 与设置不一致 | 检查 `Authorization` 头与设置页 Key（默认 `sk-haodo-local`） |
| 端口占用启动失败 | 其他程序占用 8317 | 设置页改端口（1024-65535） |
| `dotnet publish` 报版本错误 | `版本号.txt` 缺失/非法 | 检查根目录文件 |
| `dotnet publish --no-build` 报 `CreateAppHost` / `ArgumentOutOfRangeException` | 混淆 dll 的 `.rsrc` 段 RVA 未同步（Obfuscar 3.0 缺陷） | 重跑构建脚本（自动走 7.5 修复）；手工排查：`python scripts\fix_obfuscated_rsrc.py <原dll> <混淆dll> <输出dll>` 后覆盖 obj 再打包 |
| 混淆版启动即崩 / XAML 加载异常 | `SkipType` 遗漏反射依赖类型 | 确认混淆配置保留 `LocalProxyServer` 与 `DpiBootstrap`（7.4） |
| 覆盖 Haodo.exe 失败 | 进程还在运行 | 脚本会自动强杀；手动时先结束进程 |
| 客户端探测 `/v1/embeddings` | 无真实嵌入能力 | 代理返回占位假数据（设计如此） |
| `/v1beta/models/*` 请求异常 | 原生转发到公开 Gemini API | 检查路径大小写与账号权限 |

### 8.3 回归验证

1. 编译：`仅编译.bat`（或 `dotnet publish` 到临时目录）；
2. Schema 回归：`scripts/schema_regression_check.cs` → 期望 `PASS: 白名单收敛验证通过`；
3. 手工端到端（需要已登录账号）：启动软件 → 开启本地代理 → 用 curl/客户端打 `http://127.0.0.1:8317/v1/chat/completions`（流式与非流式、带工具与不带工具各测一遍）与 `/v1/responses`；
4. 检查设置页日志无异常，配额卡片刷新正常。

---

## 9. 更新记录规范（强制要求）

> **用户明确要求：每次对本软件进行更新、维护、修复后，都必须在本章追加记录。这是项目强制规范，任何 AI 或开发者都不许跳过。**

### 9.1 规则

1. 每次**代码改动、配置改动、文档改动、构建发布**后，立即在 [10. 历史变更记录](#10-历史变更记录) **顶部**插入一条记录；
2. 记录必须包含：日期、软件版本、变更类型、变更内容、涉及文件、验证方式；
3. 禁止只写"修复了一些问题"这类模糊描述——要写清楚改了什么机制、为什么；
4. 与版本发布无关的中间过程（如探索性排查）可以合并到该版本的最终记录中；
5. 更新记录是给后来 AI 定位问题的最重要线索，**宁可啰嗦不可缺失**。

### 9.2 模板

```markdown
### YYYY-MM-DD — vX.Y.Z（变更类型：修复 / 新功能 / 重构 / 配置 / 文档）

**变更内容**：
- （做了什么，为什么）

**涉及文件**：
- `路径/文件`：改动说明

**验证方式**：
- （编译结果、回归脚本输出、端到端测试步骤与结果）
```

### 9.3 更新记录书写示例

```markdown
### 2026-08-07 — v1.0.17（变更类型：修复）

**变更内容**：
- 将 GeminiSchemaAllowedKeys 从"探测模式临时全保留"宽名单收敛为最终 18 字段窄名单，
  修复白名单未真正收敛、NarraFork 场景（schema 含 propertyNames）仍会 400 的隐患。

**涉及文件**：
- `src/GeminiProtocolTranslator.cs`：GeminiSchemaAllowedKeys 收敛 + 注释更新
- `scripts/schema_regression_check.cs`：新增回归脚本（反射调用真实代码）

**验证方式**：
- dotnet build 0 错误 0 警告
- schema_regression_check.cs 输出 PASS: 白名单收敛验证通过
```

---

## 10. 历史变更记录

### 2026-08-29 — v1.0.35（变更类型：修复，429 误报根因修复：上游端点切换 daily 通道 + 429 分类分档冷却 + 动态 UA；流式响应 0 字节修复）

**背景**：用户通过 Haodo 本地代理调用模型（如 `gemini-3.6-flash-high`）时返回 `HTTP 429 RESOURCE_EXHAUSTED`，报错"All Gemini accounts in Haodo have exhausted their quota"。但已确认所有账号额度充足，且官方 CPA（CLIProxyAPI）反代同账号一切正常。根因调研：**Haodo 将模型请求硬编码发往 prod 端点 `cloudcode-pa.googleapis.com`，而官方 CPA 默认使用 daily 端点 `daily-cloudcode-pa.googleapis.com`，两条通道的配额与限流独立计量**——prod 通道配额已被其他客户端耗尽，与账号本身余额无关。此外 Haodo 的 429 处理过于粗放（任何 429 一律 30 分钟全局冷却），且 UA 版本硬编码 2.9.1 未跟进上游更新。

端点修复后用户实测 agent（opencode）调用 `/v1/responses` 流式接口报 `stream_closed_before_response_completed`：客户端收到 200 + `text/event-stream` + `content-length: 0` 的空响应体。经本地复现与诊断宿主直连上游验证（上游 SSE 返回正常），定位为 **HttpListener 流式响应写出层缺陷**。

**变更内容**：
1. **上游端点切换（429 根因修复）**：`LocalProxyServer` 新增常量 `GoogleCloudCodeBaseUrl = "https://daily-cloudcode-pa.googleapis.com"`，将 4 处模型请求（`generateContent` / `streamGenerateContent` ×2 组）、`fetchAvailableModels`（GeminiProtocolTranslator）、`retrieveUserQuotaSummary` 与 `loadCodeAssist`（MainWindow）全部切换到 daily 通道，与官方 CPA `antigravityBaseURLDaily` 默认值对齐；
2. **流式响应 0 字节修复（stream_closed 根因修复）**：`HttpListenerSyncStream.SyncNow()` 原逻辑对未设置 `ContentLength64` 的响应既不设长度也不启用 chunked，http.sys 按 `Content-Length: 0` 处理，首个 SSE 事件写入即抛 `Bytes to be written to the stream exceed the Content-Length bytes size specified`，被 catch 吞掉后客户端只能收到 200 + event-stream + 0 字节空响应体（agent 报 stream_closed_before_response_completed）。修复：`ContentLength64 < 0` 时自动 `_response.SendChunked = true` 启用 chunked 传输编码。该缺陷影响所有流式路径（/v1/responses 与 /v1/chat/completions 流式此前均无法正常工作，非流式因显式设置 ContentLength64 不受影响）；
3. **流式错误路径悬挂响应修复**：两个流式处理器的上游错误路径原为 `if (attempt == 0)` 才写错误并关流，空响应移除 tools 重试后（attempt=1）失败时会留下既不写内容也不关闭的悬挂响应。修复：通过 `HttpListenerSyncStream.HeadersSynced` 判断是否已写出字节，未写出则回传上游错误 JSON，已写出则直接关流，两种情况均保证调用 `CloseAsync()`；
4. **429 分类分档冷却（对齐 CPA antigravity_executor 决策逻辑）**：新增 `ClassifyRateLimitCooldown()`，递归解析 429 响应体中的 `retryDelay`（含 google.rpc.RetryInfo）与 ErrorInfo `reason`，按 CPA 相同阈值分档：
   - `retryDelay < 3s` → 瞬时软限流，同账号立即重试（每请求限 1 次，防止死循环）；
   - `3s ≤ retryDelay < 5min` → 短时限流，按 retryDelay 冷却（10s~5min 钳制）后换号故障转移；
   - `retryDelay ≥ 5min` → 配额耗尽，长冷却（5min~60min 钳制）；
   - 无 retryDelay 时按 reason 辅助判断（SOFT→立即重试 / RATE_LIMIT→60s），兜底 30 分钟；
5. **故障转移主循环重构**：各上游处理方法返回值从 `Task<bool>`（是否限流）改为 `Task<TimeSpan?>`（null=完成 / 非null=建议冷却时长），`TriggerAccountCooldown()` 替换为 `ApplyAccountRateLimit()`（含同账号瞬时重试与分类日志）；
6. **动态 User-Agent（对齐 CPA internal/misc/antigravity_version.go）**：UA 从硬编码 `antigravity/hub/2.9.1` 改为运行时动态拼装；本地代理启动时开启后台刷新循环（每 3 小时拉取 Antigravity Hub 更新 manifest `latest-arm64-mac.yml`，缓存有效期 6 小时，请求头伪装 `electron-builder`），解析 YAML `version` 字段并校验 x.y.z 纯数字格式，失败回退 2.9.1；版本变化输出 `[UA 版本]` 日志（实测已拉取到 2.11.0）；
7. **冷却日志优化**：`MarkAccountCooling` 冷却时长显示从固定"分钟"改为自适应（秒/分钟），短时限流不再显示为"0 分钟"。

**涉及文件**：
- `src/LocalProxyServer.cs`：端点常量 + 4 处 URL 切换；`HttpListenerSyncStream.SyncNow` 未知长度响应启用 chunked（流式 0 字节根因修复）；`HttpListenerSyncStream` 新增 `HeadersSynced` 属性；两处流式错误路径改为按写出状态回传错误并确保关流；`AntigravityUserAgent` const → 动态属性；新增 manifest 刷新循环（`EnsureAntigravityVersionRefreshLoop` / `AntigravityVersionRefreshLoopAsync` / `ParseManifestVersion`）；新增 429 分类（`ClassifyRateLimitCooldown` / `FindRetryDelayRecursive` / `FindErrorInfoReasonRecursive` / `TryParseGoogleDuration` / `ClampCooldown`）与 `ApplyAccountRateLimit`；5 个处理方法签名 `Task<bool>` → `Task<TimeSpan?>`
- `src/GeminiProtocolTranslator.cs`：`fetchAvailableModels` 请求 URL 切换 daily 通道
- `src/MainWindow.xaml.cs`：`retrieveUserQuotaSummary` / `loadCodeAssist` URL 切换 daily 通道；`MarkAccountCooling` 日志时长自适应
- `版本号.txt`：1.0.33 → 1.0.35

**验证方式**：
- `dotnet build -c Release`：0 错误 0 警告；
- 诊断宿主（临时控制台项目复用 LocalProxyServer/GeminiProtocolTranslator 真实源码 + 真实凭证）端到端实测：
  - 直连 daily 上游 `streamGenerateContent`：200 + 正常 SSE（确认上游与请求体构造无问题）；
  - 修复前 `/v1/responses` 流式：200 + event-stream + 0 字节（复现 agent 报错），服务端日志 `Responses 流式处理异常: Bytes to be written to the stream exceed the Content-Length bytes size specified`；
  - 修复后 `/v1/responses` 流式：200 + 完整 SSE 事件序列（response.created → output_item.added → content_part.added → output_text.delta → … → completed）；
  - 修复后 `/v1/chat/completions` 流式：200 + 正常 chunk + `[DONE]`；
  - 修复后 `/v1/responses` 非流式：200 + 正常 JSON；
  - 动态 UA：启动时自动拉取 manifest 并升级到 2.11.0。

---

### 2026-08-09 — v1.0.34（变更类型：升级与架构对齐，跟进 CLIProxyAPI 最新反代架构：429 自动故障转移冷却轮换 + UA 2.9.1 + 3.7 模型映射与纯上游真实动态模型解析）

**背景**：用户在通过 Haodo 本地代理服务（OpenAI 兼容端点）将 Gemini 转发给第三方 Agent（如 Claude Code / Roo Code 等）时，偶发遭遇 Google Cloud Code PA（Antigravity）私有上游返回 `HTTP 429 RESOURCE_EXHAUSTED` 报错。全面调研 CLIProxyAPI（CPA）最新提交（v7.2.140 ~ v7.2.145），对齐 CPA 最新反代规范与多账号故障转移机制；同时按用户要求彻底移除 `/v1/models` 中硬编码伪造模型的行为，实现 100% 纯上游动态解析。

**变更内容**：
1. **GeminiProtocolTranslator 协议转换层升级**：
   - **模型映射补全**：新增 `gemini-3.7-flash`、`gemini-3.7-flash-thinking`、`gemini-3.7-pro`、`gemini-3.7-pro-thinking` 等 3.7 官方与思维模型映射；
   - **Antigravity 请求体规范化**：构建外层包裹 `project`、`requestType: "agent"`（或 `image_gen`）、动态生成 `requestId: "agent-..."` / `sessionId: "-..."` 与 `userAgent: "antigravity/hub/2.9.1 darwin/arm64"`；
   - **前置用户轮次保证**：当对话历史第一条为非 `user` 角色（如 `system` 或 `model`）时，自动补充空 `user` 轮次，避免 Google Cloud Code PA 上游拒绝；
   - **参数与 Schema 清洗**：支持 `max_completion_tokens` 解析；补全 array 类型缺 `items` 时的默认 schema `{ type: "string" }`；
   - **纯上游真实模型获取**：彻底移除 `defaultModels` 硬编码数组与所有虚假伪模型；严格按当前已登录账号真实请求 `fetchAvailableModels` 动态解析上游返回的 `models` 字典与 `webSearchModelIds` 列表；若上游未返回则如实返回空列表，所见即所得。
2. **LocalProxyServer 故障转移与传输升级**：
   - **UA 全量升级**：统一使用 `antigravity/hub/2.9.1 darwin/arm64`（>= 2.9.0 避免被识别为旧版客户端）；
   - **多账号 429 故障转移（Failover）重试循环**：所有代理请求流（流式 SSE、非流式、原生 Forward）遇到上游 429 时，触发 `OnAccountRateLimited` 回调将当前账号标记冷却，并在当前可用账号池内无缝切换下一个未冷却账号重试（最多重试 3 轮），实现客户端零感知的透明高可用；
   - **异常响应保护**：若账号池耗尽仍 429，向客户端回传标准化 JSON 错误响应，避免连接悬挂。
3. **MainWindow 账号冷却管理与智能调度**：
   - **并发冷却字典**：引入 `_accountCooldowns`（并发线程安全），触发 429 自动冷却 30 分钟；
   - **排除与过滤调度**：`GetValidGeminiTokenForProxyAsync` 支持 `IReadOnlySet<string>? excludeEmails` 过滤，自动跳过重试已尝试账号及处于冷却期的账号，并在可用池中轮询（Round-Robin）均摊负载；
   - **查询与换 Token UA 统一**：`FetchAntigravityQuotaAsync` 及 `FetchAntigravityProjectIDAsync` 均统一采用 `LocalProxyServer.AntigravityUserAgent`。

**涉及文件**：
- `src/GeminiProtocolTranslator.cs`：3.7 模型映射、Antigravity 请求体规范化、前置轮次补齐、Schema 修复、纯上游真实动态模型解析
- `src/LocalProxyServer.cs`：UA 2.9.1 升级、多账号 429 自动故障转移重试循环、429 冷却回调触发
- `src/MainWindow.xaml.cs`：账号冷却字典、智能排除与轮询调度、UA 统一
- `维护文档/MAINTENANCE.md`：本记录

**验证方式**：
- `dotnet build src/CLIProxyAPI_GUI.csproj -c Debug` 0 错误 0 警告
- `dotnet build src/CLIProxyAPI_GUI.csproj -c Release` 0 错误 0 警告

### 2026-08-09 — v1.0.31（变更类型：新功能，Google Drive 配置同步「同步」卡片）

**背景**：用户要求为 Haodo 增加跨设备配置同步：将**偏好配置与 Gemini 凭证文件**备份至用户自己的 Google Drive 私有空间，全手动上传/下载。参考辉夜姬画布（同开发者）的成熟 OAuth 模式实现，但 Haodo 为纯 WPF 应用、无 Web 前端，回调监听采用 `TcpListener` 回环端口（无需 urlacl 权限），DPAPI 采用 P/Invoke `crypt32.dll`（零 NuGet 依赖）。

**变更内容**：
1. **设置页「同步」折叠卡片**（「凭证文件」与「关于」卡片之间）：
   - XAML：`SettingsCardHeader` + `SettingsSyncContent` 折叠区；头部状态徽章 `TxtGDriveStatusBadge`（未连接灰 / 已连接蓝）；内容区含登录区（`SettingsSyncLoginRow`）与已连接区（`SettingsSyncConnectedRow`：邮箱、上次同步时间、上传到云端 / 从云端恢复 / 断开连接）；
   - 说明文字明示：偏好配置与 Gemini 凭证文件备份至用户的 Google Drive 私有空间（`drive.file` scope，仅本人可见）。
2. **OAuth 登录**（PKCE S256 + state 校验）：
   - 复用辉夜姬同开发者 OAuth 凭据（ClientID/ClientSecret 硬编码，桌面应用标准做法）；
   - 授权 URL 打开默认浏览器；回调监听 `TcpListener(127.0.0.1:38438)` 后台线程，仅处理 `GET /oauth/google/callback`，其余路径 204 空响应；10 分钟无回调自动关闭监听；
   - 回调后换取 token（`authorization_code` + `code_verifier`），拉取 `userinfo` 邮箱，响应 HTML 成功页，`Dispatcher.Invoke` 刷新 UI；
   - 端口被占用（如辉夜姬画布同时运行）→ `ShowCustomModal` 明确提示。
3. **令牌持久化**（`data/google-token.json`）：
   - `GoogleTokenState`：UserEmail / RefreshTokenProtected / AccessTokenProtected / ExpiresAtUnixMs / FileId / CredentialsFileId / LastSyncUnixMs；
   - refresh/access token 以 DPAPI（P/Invoke `CryptProtectData`/`CryptUnprotectData`，CurrentUser 级）加密后 Base64 落盘；加载时校验 DPAPI 可解密，用户环境变更则视为未登录；
   - `EnsureGoogleAccessTokenAsync()`：access_token 过期前 2 分钟自动用 refresh_token 续期；`invalid_grant`/`invalid_client` → 清除本地登录态。
4. **云端文件**（固定单文件覆盖式，最后写入者胜）：
   - `haodo-config-v1.json`：偏好配置（themeMode / maskAccountInfo / autoRefreshIntervalMinutes / autoCheckUpdateEnabled / miniModeType / miniWidgetBgColor / miniWidgetBgOpacity / miniWidgetIsTransparent / miniWidgetEdgeDock / miniWidgetHideCardBg / miniWidgetColor）；
   - `haodo-credentials-v1.json`：凭证打包 `{"version":1,"exportedAt":...,"credentials":[{"name","content"}]}`（name 仅纯文件名）；
   - 上传：已有 FileId → `PATCH uploadType=media`；无 → `multipart/related` 创建；下载：`GET alt=media`；两文件 FileId 分别缓存于 `FileId` / `CredentialsFileId`；
   - 明确**不同步**（机器相关/敏感/冗余）：窗口与迷你窗几何（mainWin*/miniWidget Left/Top/Width/Height）、`dataDir`、`files`（本地凭证路径）、`localProxy*`、`isMiniOn`（由 miniModeType 推导）。
5. **应用恢复**：
   - `ApplySyncConfig`：校验回写字段 → `SaveSettings()` → `ApplyTheme` / `UpdateThemeSegmentedUI` / `UpdateMaskAccountSegmentUI` / `ResetAutoRefreshTimer` / `UpdateAutoCheckUpdateUI` / `ApplyMiniWidgetSettings` / `UpdateMiniWidgetColorSettingsUI`；
   - `ApplySyncCredentials`：防目录穿越（仅接受 `Path.GetFileName` 纯文件名）→ 写入 `_dataDir` → `IsValidAuthJson` 通过则加入 `_jsonFilePaths` → `SaveSettings` + `RenderSettingsFilesList` + `RefreshAccounts`。
6. **UI 状态刷新**：`UpdateGDriveUI()`（构造后、切设置页、登录/登出/上传/下载完成后调用）；`_gdriveBusy` 防重入，busy 期间禁用操作按钮；`BtnGDriveLogout_Click` 先 revoke 再清 token 文件。

**涉及文件**：
- `src/MainWindow.xaml`：「同步」折叠卡片（含状态徽章、登录/上传/恢复/断开按钮）
- `src/MainWindow.xaml.cs`：Google Drive 同步完整逻辑（OAuth PKCE、DPAPI、TcpListener 回调、Drive 文件操作、配置/凭证收集与应用、UI 刷新）、`GoogleTokenState` 类、using 补充
- `版本号.txt`：1.0.31
- `维护文档/MAINTENANCE.md`：本记录

**验证方式**：
- `dotnet build` 0 错误 0 警告（Release / win-x64）；
- `scripts/build_only.ps1` 完整打包覆盖根目录 `Haodo.exe`；
- 运行实测：设置页「同步」卡片折叠/展开正常；未登录仅显示「登录 Google」；本地模拟回调 `http://127.0.0.1:38438/oauth/google/callback?state=...&code=...` 验证监听器响应与失败路径（state 不匹配/缺少 code 均返回失败页）；未登录点上传/恢复 → 提示「请先登录 Google Drive」；
- 真实 Google 授权环（浏览器登录 → 回调 → 上传 → 恢复）需用户在真机验证，涉及用户账号操作。

**已知边界**：
- 38438 端口与辉夜姬画布同端口，两者同时运行时登录回调监听会失败（UI 已明确提示）；
- 凭证以明文 JSON 存于用户自己的 Google Drive 私有空间（等同「网盘存 key」信任级别），未做二次加密（跨设备解密需机器无关密钥，v1 不做）；
- 覆盖式同步不合并冲突，最后写入者胜。

### 2026-08-08 — v1.0.50（变更类型：新功能，自动检查更新分段开关 + 修复 OneDrive 同步导致的源文件错位损坏）

**背景**：用户要求为启动时静默检查更新增加可配置开关（默认开启），关闭后启动不再请求 `version.json`；配置持久化于 `settings.json`。实施期间发现 `MainWindow.xaml.cs` 因 OneDrive 多端同步错位而残缺（缺失 14 个方法与 `FindVisualChildren`、`SwitchView` 括号损坏、`BtnRefreshDataDir_Click` 定义丢失），导致编译失败且 XAML 事件绑定悬空，一并修复。

**变更内容**：
1. **自动检查更新开关**（设置页「关于」卡片内，与主题/掩码同款分段样式）：
   - XAML：新增 `BtnAutoCheckOn`（开启）/ `BtnAutoCheckOff`（关闭）分段按钮；
   - C#：新增 `_autoCheckUpdateEnabled` 字段（默认 `true`）；`LoadSettings` 读取、`SaveSettings` 写入 `autoCheckUpdateEnabled` 键；`OnSourceInitialized` 中启动静默检查增加条件 `if (_autoCheckUpdateEnabled) Task.Run(() => CheckForUpdatesAsync(isManualCheck: false));`；
   - `UpdateAutoCheckUpdateUI()` 统一同步开关 UI 状态（构造后、切设置页、恢复初始数据后均调用）；两个 Click Handler 修改状态并立即 `SaveSettings`；
   - 重置流程接入：`ResetSettingsToDefaults()` 恢复 `_autoCheckUpdateEnabled = true`，恢复初始数据界面同步刷新；
   - 关于卡片内手动「检查更新」按钮走 `isManualCheck: true` 路径，**不受开关影响**。
2. **源文件错位损坏修复**：
   - 现象：`MainWindow.xaml.cs` 缺失 `ToggleSettingsSection_Click`、`InitAutoRefreshTimer`、`ResetAutoRefreshTimer`、`BtnOpenDataDir_Click`、`BtnSaveAutoRefresh_Click`、`TxtDataDir_LostFocus/KeyDown`、`TxtAutoRefreshInterval_LostFocus/KeyDown`、`ApplyTxtDataDirChange`、`ApplyAutoRefreshIntervalChange`、`BtnOpenSettings_Click`、`SetSettingsCardHighlight`、`SyncAllSettingsCardHighlights` 共 14 个方法及 `FindVisualChildren` 辅助方法；`SwitchView` 的 `try` 缺闭合括号；XAML 引用的 `BtnRefreshDataDir_Click` 定义丢失（运行时会 XamlParse 崩溃）；
   - 修复：从 NarraFork 文件快照（`narrator_file_snapshots`，13:37Z 完整版 5102 行）提取缺失方法合并回当前版本，修复 `SwitchView` 括号结构并补回 `BtnRefreshDataDir_Click`（含重载配置+重扫+弹窗完整逻辑），恢复后与当前 XAML/`MiniWidgetWindow.cs` 签名（6 参数 `UpdateData`）对齐。

**涉及文件**：
- `src/MainWindow.xaml`：关于卡片新增自动检查更新分段开关
- `src/MainWindow.xaml.cs`：开关字段/持久化/启动条件/UI 同步/重置接入 + 缺失方法恢复
- `维护文档/MAINTENANCE.md`：本记录

**验证方式**：
- `dotnet build` 0 错误 0 警告（Release / win-x64）；
- 隔离目录实测首次运行：自动生成 `data/settings.json` 且 `autoCheckUpdateEnabled: true`（默认开启持久化）；GUI 正常启动、主界面与设置页导航切换正常（UIAutomation 验证控件树完整，无 XamlParse 崩溃）；
- 注意：OneDrive 同步（`D:\OneDrive\开发\Haodo`）曾造成多端版本互相覆盖，涉及本文件的大改建议先本地构建验证再同步。


### 2026-08-08 — v1.0.50-2（变更类型：修复，弹窗遮罩超出窗口容器范围）

**背景**：用户反馈更新弹窗的遮罩"太大、不正常，超出软件窗口的容器本身"。排查确认根因：`GridCustomModalOverlay` / `GridUpdateOverlay` 两个弹窗层原本是 **`RootMarginGrid` 的直接子元素**（位于 `RootBorder` 之外），遮罩因此覆盖**整个窗口**（含 `RootBorder` 外圈的 16px 透明边距），视觉上比内容容器四周大一圈，且直角矩形把 `RootBorder` 的 12px 圆角盖平。

**变更内容**（`src/MainWindow.xaml`）：
1. 将 `GridCustomModalOverlay`（自定义弹窗）与 `GridUpdateOverlay`（更新弹窗）**移入 `RootBorder` 内容 Grid 内**（作为其直接子元素，排在视图宿主之后），并加 `Grid.RowSpan="2"` 覆盖标题栏 + 内容区两行；
2. 遮罩由 Grid 直角背景改为**内嵌圆角 Border**（`CornerRadius="12"` 与 `RootBorder` 一致，`Background=ThemeOverlayBg`），Grid 本身 `Background="Transparent"`——遮罩不再直角盖住容器圆角；
3. 弹窗卡片 Border 保持原样，位于圆角遮罩之上。

**验证方式**：
- `dotnet build` 0 错误 0 警告（Release / win-x64），SAX 校验 overlay 父级为 `RootBorder` 内容 Grid（456 行开）；
- **必须用 `scripts/build_only.ps1` 重新打包根目录 `Haodo.exe` 才能生效**（仅 `dotnet build` 只更新 bin 目录，用户运行的根目录单文件发布物仍是旧版——此前用户反馈"没修复成功"即因根目录 `Haodo.exe`（23:22）早于修复后源码（23:31），本次 23:34 混淆打包后实测通过）；
- 实测（UIAutomation + PIL 像素扫描）：展开设置页「关于」卡片 → 点击「检查更新」→ 弹窗（"已是最新版本"+ 我知道了）正常出现；水平/垂直中线亮度跳变点均在窗口 16px 边距之后（x≈17、y≈17）；弹窗打开 vs 关闭时边距区颜色几乎不变（65,64,69 ↔ 64,63,68）、内容区正常压暗（165→199）——遮罩严格限制在 `RootBorder` 圆角容器内，不覆盖窗口透明边距。


### 2026-08-08 — v1.0.27（变更类型：构建体系，接入 Obfuscar 免费混淆 + 修复混淆后 Win32 资源段 RVA 缺陷）

**背景**：用户要求发布物经受混淆保护。选用免费开源混淆器 **Obfuscar 3.0**（GlobalTool 解包产物部署于 `tools\Obfuscar`），并打通「编译 → 混淆 obj 中间产物 → 修复资源段 → `--no-build` 重打单文件」全自动化链路；期间定位并修复 Obfuscar 3.0 重写 PE 导致 `.rsrc` 段 RVA 脱节的打包崩溃缺陷。

**变更内容**：
1. **Obfuscar 集成**（`scripts/build_only.ps1`、`scripts/build.ps1`）：
   - 构建脚本在首次 publish 后，对 obj 中间 `Haodo.dll` 执行 Obfuscar 混淆（配置：`HidePrivateApi` + `KeepPublicApi` + `HideStrings` + `AnalyzeXaml` + `UseUnicodeNames` + `SkipGenerated` + `SkipSpecialName` + `RegenerateDebugInfo` + `SuppressIldasm`）；
   - **`SkipType` 保留两类反射依赖**：`CLIProxyAPI_GUI.LocalProxyServer`（XAML 反射创建）与 `CLIProxyAPI_GUI.DpiBootstrap`（`[ModuleInitializer]`，混淆后启动即崩）；
   - 混淆后以 `--no-build` 重新打包单文件（防止重新编译冲掉混淆），publish 保持 `--no-self-contained` **框架依赖**模式（与 csproj 一致，单文件约 900KB；目标机器需安装 .NET 10 Desktop Runtime），并显式传入 `DebugType=None` / `DebugSymbols=false`。
2. **`.rsrc` 资源段 RVA 修复**（`scripts/fix_obfuscated_rsrc.py` 新建）：
   - 根因：Obfuscar 3.0 将 PE32+ (x64) 改写为 PE32 时 `.rsrc` 段整体后移（`0x7C000 → 0x8A000`，DELTA=+0xE000），但资源树 9 个叶子节点 `OffsetToData` 绝对 RVA 未同步，导致 SDK `CreateAppHost` 单文件打包任务 `ArgumentOutOfRangeException` 崩溃；
   - 脚本对比原版/混淆版节表自动计算 DELTA，重定位全部叶子 RVA，并内置自校验（叶子数一致 + 每个 RVA 落回 `.rsrc` 段内），输出修复版 dll 覆盖 obj 后重打包。
3. **构建脚本健壮性**：publish 参数统一集中定义复用（第二次追加 `--no-build`）；混淆/修复步骤前后均有产物存在性检查与失败提示；两脚本均以 UTF-8 BOM 保存确保 PowerShell 5.1 中文兼容。

**涉及文件**：
- `scripts/build_only.ps1`、`scripts/build.ps1`：全流程改造（9 步）
- `scripts/fix_obfuscated_rsrc.py`：新建资源段修复脚本
- `tools/Obfuscar/`：内置混淆器工具包（GlobalTools.dll）
- `维护文档/MAINTENANCE.md`：第 3/7/8/10 章同步更新

**验证方式**：
- `build_only.ps1` 完整跑通：混淆后 `Haodo.dll` 671,744 字节 → 修复脚本输出「已重定位叶子数据条目: 9 个 / 校验: 全部通过」→ `--no-build` 重打包成功（无 CreateAppHost 报错）→ 根目录 `Haodo.exe` 955,543 字节（框架依赖单文件，约 900KB）；
- 混淆版单文件实测启动：进程存活 20 秒+，窗口正常创建，`.NET Runtime`/WER 事件日志无崩溃记录；
- 混淆与修复只影响托管 dll（611KB → 671KB，+60KB），**单文件体积差异来自发布模式**：框架依赖 ~900KB（本方案，目标机器需 .NET 10 Desktop Runtime）vs 自包含 ~165MB（免装运行时）。

### 2026-08-08 — v1.0.27（变更类型：重构，专一轻量化——彻底移除 Claude/Codex/Grok/Kimi 非 Gemini 平台支持）

**背景**：用户要求将 BalanceViewer (Haodo) 重构为专一轻量的软件——只服务 Antigravity/Gemini 凭证，删光其他平台的凭证载入、额度查询、Token 刷新与 UI 徽章逻辑。

**变更内容**：
1. **凭证载入硬过滤**（`src/MainWindow.xaml.cs` `IsValidAuthJson`）：JSON 凭证若含 `type` 字段且非 `antigravity`/`gemini`（claude/codex/grok/xai/kimi 等）直接返回 false 跳过载入；`LoadAllAccounts` 不再遇到非 Gemini 平台。
2. **删除平台分派与远端调用**：删除 `QueryQuotaByProviderAsync`（Claude 真实请求 + Codex/Grok/Kimi 100% 占位）、`FetchClaudeQuotaAsync`（`api.anthropic.com/api/oauth/usage`）、`RefreshTokenByProviderAsync`/`RefreshClaudeTokenAsync`/`RefreshCodexTokenAsync`；`FetchRealTimeQuotaAsync` 直接调用 `FetchAntigravityQuotaAsync`，刷新统一走 `RefreshAntigravityTokenAsync`（`oauth2.googleapis.com/token`）。
3. **简化 UI 徽章与图标**：`GetPlatformBadgeColors` 去除参数与 claude/codex/grok/kimi 配色分支（统一 Gemini 蓝）；主界面两处卡片渲染（platformName 分支 + Quota Sections 分支）收敛为「Gemini 模型分组 + Claude/GPT 模型分组」双段展示（后者为 Antigravity 接口返回的真实模型分组）；空状态文案改为「Gemini / Antigravity」。
4. **贴贴窗口简化**（`src/MiniWidgetWindow.xaml.cs`）：删除 `accountType` 参数的 Claude/Codex/Grok/Kimi 图标分支与「状态/订阅」假数据行，恒为 Antigravity 双行布局（Google 图标 + OpenAI 图标）；`UpdateData` 签名移除 `accountType` 参数；XAML 删除无用的 `IconClaude` 资源。
5. **代理取号简化**（`GetValidGeminiTokenForProxyAsync`）：所有载入账号均为 Gemini 账号且已排除禁用，删除二次平台过滤。
6. 日志前缀统一为 `[Antigravity]`；保留 GeminiProtocolTranslator / LocalProxyServer 的 OpenAI API 兼容层（把 OpenAI 格式请求翻译为 Gemini 请求，属核心功能）。

**涉及文件**：
- `src/MainWindow.xaml.cs`（5108 → 4892 行）：凭证过滤、配额/刷新精简、卡片渲染、代理取号、徽章配色
- `src/MiniWidgetWindow.xaml.cs`（1991 → 1912 行）：图标分支删除、`UpdateData` 签名简化
- `src/MiniWidgetWindow.xaml`（269 → 268 行）：删除 `IconClaude` 资源
- `src/MainWindow.xaml`：空状态文案更新（行数不变）

**验证方式**：
- `dotnet build` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`（v1.0.27）；
- 逻辑走查：`data/` 下两个 `type=antigravity` 凭证正常载入；`type=claude/codex` 的凭证 JSON 被 `IsValidAuthJson` 拒绝；主界面卡片仅展示 Gemini/Claude-GPT 双模型分组；贴贴窗口恒为 Antigravity 双行样式。

### 2026-08-08 — v1.0.26（变更类型：修复/补强，二次运行 exe 唤醒面板的启动竞态——信号落在主窗口创建前会丢失）

**背景**：用户要求「运行 exe 时，只要当前进程存在就直接打开面板」。单实例唤醒机制本就存在（Mutex + EventWaitHandle + 广播消息），但存在一个竞态缺陷：主实例刚启动、主窗口尚未创建完成时，若第二个实例的唤醒信号恰好到达，`Current.MainWindow is MainWindow mw` 判空失败 → 信号静默丢失，表现为"运行了 exe 没反应"。

**变更内容**（`src/App.xaml.cs` 唤醒监听回调）：
1. 主窗口已创建 → 立即 `ShowMainFromMini()`（Show + Activate + 置顶闪烁，面板必现）；
2. 主窗口尚未创建（启动竞态窗口期）→ 用 `DispatcherTimer` 延迟 800ms 重试一次，兜底唤醒；
3. 原有双通道（EventWaitHandle 主 + PostMessage 广播备）与第二实例 `Environment.Exit(0)` 逻辑保持不变。

**涉及文件**：
- `src/App.xaml.cs`：唤醒监听回调竞态兜底

**验证方式**：
- `dotnet build` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`（v1.0.26）；
- 逻辑走查：窗口在托盘（Hide）时二次运行 exe → 面板弹回前台；窗口在贴贴模式下二次运行 exe → 主面板弹出；启动瞬间立刻二次运行 exe → 800ms 后仍弹出面板。

### 2026-08-08 — v1.0.25（变更类型：重构，数据目录唯一路径——移除 AppData 兜底/双写，data 目录为唯一事实源）

**背景**：用户明确要求「以当前 data 路径为唯一路径，不做二选」。旧机制：默认 `%AppData%\Haodo` + SaveSettings 双写兜底 + 启动时靠兜底版 `dataDir` 字段重定向。改为：软件同级 `data\` 为唯一事实源，配置仅此一份。

**变更内容**：
1. 构造函数：默认 `_dataDir = BaseDirectory\data`、`_settingsPath = data\settings.json`；新增一次性迁移收尾——旧版 `%AppData%\Haodo\settings.json` 存在时：数据目录尚无配置则复制过去（不丢旧配置），随后删除兜底副本；更早版本的根目录 `settings.json` 残留同理复制进 `data\`；
2. `LoadSettings`：删除 `dataDir` 字段重定向逻辑（不再二选）——`dataDir` 仅作记录，与当前目录不一致时忽略并 Log 提示；`_dataDir` 恒等于 `_settingsPath` 所在目录；
3. `SaveSettings`：删除 AppData 双写块，只写数据目录一份；
4. `BtnResetDefaultDataDir_Click` / `UpdateDataDirStatUI`：默认目录判定从 AppData 改为 `BaseDirectory\data`；XAML 对应 ToolTip 改为「恢复为默认 data 路径」；
5. `ApplyDataDirectorySwitch`：迁移成功后旧目录 `settings.json` 一律删除（原仅非 AppData 目录才删），杜绝磁盘出现第二份配置；
6. 相关注释同步更新（`IsDirDataEmpty` / `TryChangeDataDirectory` 不再引用双写前提）。

**涉及文件**：
- `src/MainWindow.xaml.cs`：构造函数、`LoadSettings`、`SaveSettings`、`BtnResetDefaultDataDir_Click`、`UpdateDataDirStatUI`、`ApplyDataDirectorySwitch`、注释
- `src/MainWindow.xaml`：恢复默认按钮 ToolTip
- `版本号.txt`：1.0.24 → 1.0.25

**验证方式**：
- `dotnet build` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`（v1.0.25）；
- 逻辑走查：启动 → 读 `data\settings.json`（无 AppData 介入）；手动改配置 → 刷新/重启均以 data 版为准；AppData 残留副本启动时被收尾删除；切目录后旧目录配置副本被清理，新目录唯一。

### 2026-08-08 — v1.0.24（变更类型：新功能，数据目录「刷新」按钮升级：重新载入最新 settings.json 配置后扫描）

**背景**：此前「刷新」只重新扫描凭证文件，用户手动编辑数据目录下 `settings.json`（主题、自动刷新间隔、贴贴开关、代理等）后无法一键应用。本次让刷新按钮先重载磁盘上的最新配置，再归集扫描凭证。

**变更内容**（`src/MainWindow.xaml.cs` `BtnRefreshDataDir_Click`）：
1. 新增第 1 步「重载配置」：`LoadSettings()` 从 `_settingsPath` 重新读取全部配置；若文件内 `dataDir` 已变更且目录存在，`_dataDir`/`_settingsPath` 自动重定向；
2. 重载后同步 UI 与运行时：`TxtDataDir`/`TxtAutoRefreshInterval` 文本、`ApplyTheme` + `UpdateThemeSegmentedUI`（主题）、`ApplyMiniWidgetSettings`（贴贴开关/模式/样式重建）、`UpdateMiniWidgetColorSettingsUI`、`UpdateMaskAccountSegmentUI`、`ResetAutoRefreshTimer`（间隔按新值生效）；
3. 第 2 步保持原逻辑：`MigrateAndScanDataDirectory` 归集扫描凭证并 `SaveSettings` 持久化（净化后的凭证列表写回配置文件，实现「文件 ↔ 内存 ↔ 磁盘」闭环）；
4. 成功弹窗文案更新为「已重载最新配置并重新扫描数据目录」。

**安全兜底**（沿用既有逻辑）：settings.json 损坏 → `LoadSettings` catch 并保持内存现状；`dataDir` 指向不存在目录 → 不重定向；`files` 中失效/非凭证路径 → 过滤。

**涉及文件**：
- `src/MainWindow.xaml.cs`：`BtnRefreshDataDir_Click` 重写

**验证方式**：
- `dotnet build` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`；
- 逻辑走查：手动改 `settings.json` 的 `themeMode`/`autoRefreshIntervalMinutes`/`miniModeType` → 点刷新 → 主题/计时器/贴贴立即按新值生效，凭证列表重新扫描，配置文件被净化重写。

### 2026-08-08 — v1.0.23（变更类型：优化，设置页展开卡片新增主界面凭证卡片同款高亮描边）

**背景**：用户希望设置页展开的卡片有更明确的视觉反馈，且与主界面凭证卡片的 hover 高亮保持统一质感（主界面 hover 为边框泛主题蓝 `#2563EB` 45%）。

**变更内容**（`src/MainWindow.xaml.cs`）：
1. `ToggleSettingsSection_Click` 展开/收起后调用新增 `SetSettingsCardHighlight(btn, newOpen)`：从头部按钮沿视觉树向上定位卡片容器 Border（`VergeCard`）——
   - 展开 → `BorderBrush = Color.FromArgb(0x73, 0x25, 0x63, 0xEB)`（`#2563EB` 45%，与 `AttachCardHoverFeedback` hover 完全同款）；
   - 收起 → `ClearValue(Border.BorderBrushProperty)` 清除本地值，恢复 `VergeCard` 样式的 `DynamicResource ThemeCardBorder`（主题切换自动跟随）。
2. 新增 `SyncAllSettingsCardHighlights()`：遍历 `ViewSettings` 视觉树中 Tag 为「内容面板名|箭头文本块名」格式的头部按钮（配合新增通用辅助 `FindVisualChildren<T>`），按内容面板当前 Visibility 同步各卡片描边；在 `SwitchView` 进入设置页时调用，保证初始默认展开/收起状态下的描边一致。
3. 无 XAML 改动——卡片 Border 无 x:Name 也能通过视觉树定位。

**涉及文件**：
- `src/MainWindow.xaml.cs`：`ToggleSettingsSection_Click` 追加高亮同步；新增 `SetSettingsCardHighlight` / `SyncAllSettingsCardHighlights` / `FindVisualChildren<T>`；`SwitchView` 进入设置页时同步一次

**验证方式**：
- `dotnet build` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`；
- 逻辑走查：展开任一设置卡片 → 卡片描边变主题蓝；收起 → 恢复主题边框；主题切换后收起卡片边框颜色正确跟随；进入设置页时初始展开的卡片（如有）直接带高亮。

### 2026-08-08 — v1.0.23（变更类型：修复，凭证文件列表「显示文件名」逻辑修复——不再误显示完整路径）

**背景**：用户反馈设置页「凭证文件」列表同一列表里有的条目显示账号名、有的显示完整路径。定位根因：`GetDisplayFileName` 的两个分支都错误返回了完整路径——①脱敏（账号打码）关闭时直接 `return filePath`；②脱敏开启但文件名不含邮箱（如 `claude-1.json`、`codex-pro.json`）时打码无变化，也 `return filePath`。只有文件名含邮箱（`antigravity-xxx@gmail.com.json`）时才返回（打星后的）文件名。

**变更内容**（`src/MainWindow.xaml.cs` `GetDisplayFileName`）：
1. 统一只返回文件名（`Path.GetFileName`）：
   - 脱敏关闭 → 原样文件名；
   - 脱敏开启 → `MaskEmailsInText` 打星（无邮箱则文件名不变）；
2. 不再在任何分支返回完整路径——路径展示仍由 `GetDisplayFilePath`（列表第二行小字）负责；
3. 该方法的其他调用点（主界面卡片 meta 行「📄 文件 · 大小」、移除确认弹窗）同步受益，显示统一为文件名。

**涉及文件**：
- `src/MainWindow.xaml.cs`：`GetDisplayFileName` 逻辑重写

**验证方式**：
- `dotnet build -c Release` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`；
- 逻辑走查：脱敏关/开 × 文件名含邮箱/不含邮箱 四种组合均只显示文件名（邮箱按 `GetDisplayEmail` 规则打星），不再出现路径。

### 2026-08-08 — v1.0.49（变更类型：修复，数据目录反复横跳导致凭证混乱与"导不过来"的根因修复）

**背景**：用户反馈在「恢复默认 ↔ 便携模式 ↔ 自定义目录」间反复切换时：①数据经常导不过来（切过去看不到原目录凭证）；②明明已是便携模式，软件读取的仍是默认目录（AppData）里的信息。经源码走查定位三个根因。

**根因**：
1. **旧目录凭证路径从不清理（核心）**：`_jsonFilePaths`（内存列表 + settings.json `files` 字段）在切换目录时只增不减——`MigrateAndScanDataDirectory` 只做"把程序根目录/data 凭证复制进新目录 + 把新目录凭证加进列表"，旧目录凭证文件仍在磁盘且有效，`IsValidAuthJson`/`File.Exists` 过滤都清不掉 → 主界面、配额刷新、本地代理继续使用旧目录凭证；重启后 `LoadSettings` 加载 `files` 时也无目录归属检查 → 跨目录混合列表永久化；
2. **「恢复默认」永不迁移凭证**：`SaveSettings` 双写机制保证 `%AppData%\Haodo` 恒有 settings.json → 恢复默认时目标目录被 `IsDirEmpty` 判为"非空" → `migrate=false` → 便携目录凭证永不 File.Move 迁移，仅靠归集 Copy 兜底（同名跳过、不删源）→ 同名凭证两目录各一份，切回时列表重复；
3. **旧目录 settings.json 残留**：迁移配置的"删除旧目录副本"逻辑包在 `if (File.Exists(old) && !File.Exists(new))` 内，恢复默认时新目录已有 settings.json → 旧副本不删除，内容仍指向旧 dataDir。

**变更内容**（`src/MainWindow.xaml.cs`）：
1. `LoadSettings`：加载 `files` 数组仅接受「路径位于当前 `_dataDir` 下」的凭证（忽略大小写），排除历史跨目录残留；
2. `MigrateAndScanDataDirectory`：净化过滤追加「仅保留当前 `_dataDir` 下路径」条件（同时覆盖启动与切换两条路径）；安全性由导入逻辑保证（`BtnAddFiles_Click` 一律把外部凭证复制进数据目录，正常凭证必然位于数据目录内，不会误删）；
3. `TryChangeDataDirectory`：迁移判定由 `IsDirEmpty`（有任何文件即非空）改为 `IsDirDataEmpty`（除 settings.json 外无其他文件）——settings.json 是软件配置不算用户数据，且双写机制使 AppData 目录恒有它，不能据此判定"有内容"；同时保留"目标目录有其他文件（如系统目录）不迁移"的防误搬防护；
4. `ApplyDataDirectorySwitch`：「删除旧目录 settings.json」移出 `if (File.Exists(old) && !File.Exists(new))` 块（旧目录非默认 AppData 即删除残留副本，后续 SaveSettings 会向新目录写入全量配置，无丢失风险）；
5. `ApplyDataDirectorySwitch`：`SaveSettings()` 移到 `MigrateAndScanDataDirectory()` 之后——此前归集前保存会把旧目录路径先写进 `files` 字段，改为归集净化后再持久化，磁盘配置与内存列表一致。

**涉及文件**：
- `src/MainWindow.xaml.cs`：5 处逻辑修改（LoadSettings / MigrateAndScanDataDirectory / TryChangeDataDirectory / ApplyDataDirectorySwitch）
- `维护文档/MAINTENANCE.md`：本次记录

**验证方式**：
- `dotnet build -c Release` 0 错误 0 警告；`build_only.ps1` 覆盖根目录 `Haodo.exe`；
- 场景走查（逻辑推演）：a) 便携→恢复默认：凭证 Move 迁移 + 旧 settings.json 清除 + 列表只剩 AppData 凭证；b) 恢复默认→便携（便携已空）：凭证 Move 回 + 配置复制 + 双写 dataDir 正确；c) 自定义 A→空 B：迁移；d) 自定义 A→有文件 B：只切换、列表只剩 B 凭证；e) 重启后 `files` 仅加载当前目录凭证；f) 代理取号仅见当前目录账号；
- grep 确认 `IsDirEmpty` 无残留（全部替换为 `IsDirDataEmpty`）。

### 2026-08-08 — v1.0.48（变更类型：重构，凭证文件「开启查看/关闭查看」改名「启用/停用」）

**背景**：用户指出「开启查看/关闭查看」的"查看"二字具有误导性——该开关实际是账号的启用/停用开关（停用的凭证仅保留文件，不载入主界面、不参与配额刷新、不参与本地代理取号），并非仅控制界面查看。

**变更内容**（`src/MainWindow.xaml.cs` 设置页凭证文件列表）：
1. 分段按钮文案：「开启查看」→「启用」、「关闭查看」→「停用」；
2. 日志文案同步：`[设置] 已为 xxx 设置为 [启用]` / `[设置] 已为 xxx 设置为 [停用]`；
3. 注释同步：`LoadAllAccounts` 中停用凭证的跳过注释补全"不参与配额刷新与本地代理取号"；
4. 功能逻辑零改动：仍为 `SetJsonFileDisabled` 写 `disabled` 字段（启用=false / 停用=true）。

**涉及文件**：
- `src/MainWindow.xaml.cs`：6 处文案/注释替换（3169/3188/3199/3206/3217/3272 行）

**验证方式**：
- `dotnet build -c Release` 0 错误 0 警告；
- grep 确认源码与文档无「开启查看/关闭查看」残留。

### 2026-08-07 — v1.0.22（变更类型：重构与修复，数据目录 UI 重构与主配置文件持久化修复）

**背景**：深入分析用户修改数据目录的实际动机（网盘/多设备同步、绿色便携、备份迁移），对数据目录 UI 布局与重置交互进行系统性重构；同时修复选择/更改目录后系统主配置文件未同步保存导致重启后复原默认目录的 Bug。

**变更内容**：
1. **「数据目录」卡片 UI & 交互重构**（`src/MainWindow.xaml` + `src/MainWindow.xaml.cs`）：
   - 新增 **数据概况统计条**（`TxtDataDirStat`）：实时展示当前目录下凭证文件数量与 `settings.json` 生成状态；
   - 增加 **「便携模式 (./data)」快捷按钮**（`BtnSetPortableDataDir_Click`）：支持一键将数据存放位置切换至程序运行根目录下的 `data` 文件夹（方便 U 盘或绿色随身版使用）；
   - **路径输入框内嵌 📂 浏览图标**：将选择目录按钮收纳到路径输入框右侧内部（`📂` 图标），视觉更美观紧凑；
   - **新增「刷新」按钮**（`BtnRefreshDataDir_Click`）：原选择按钮位置替换为「刷新」，支持用户手动往目录复制粘贴凭证文件后一键扫描重新加载数据。
   - **「本地 API 代理」恢复默认折叠**：与项目其他设置卡片保持一致的初始收起/折叠交互（点击头部展开），主界面点击代理状态灯时依然可跳转并自动展开。
2. **「重置与危险操作」卡片彻底独立**（`src/MainWindow.xaml`）：
   - 将原位于数据目录区的 `恢复初始数据` 彻底剥离，在设置页最底部新增独立的 **「重置软件」** 卡片（避免用户把重置设置误以为是清除目录路径）；
   - 按钮文案更新为 **「恢复出厂设置」**，二次确认弹窗同步对齐文案。
3. **数据目录主配置文件双向同步持久化修复与全量迁移**（`src/MainWindow.xaml.cs`）：
   - **配置文件跟随迁移**：迁移数据目录时，除了移动凭证 JSON 外，将 `settings.json` 一并打包迁移至新目录，保证用户所有个性化设置（贴贴、代理、主题等 22 项习惯）100% 完整带走；非默认旧目录清理残留 `settings.json`；
   - `SaveSettings()`：切换数据目录后除了向新目录写入 `settings.json` 外，同步写入系统标准 AppData 主配置文件 `%AppData%\Haodo\settings.json`，确保软件重启后 `LoadSettings()` 能准确读取到用户设定的自定义路径；
   - `LoadSettings()`：解析 `dataDir` 成功后同步更新内存中的 `_settingsPath`；
   - `BtnBrowseDataDir_Click`：选择目录后先更新 UI 输入框，防范失焦竞争。
4. **严谨极简主义 UI 视觉与版式重构**（`src/MainWindow.xaml` + `src/MainWindow.xaml.cs`）：
   - **摒弃所有蓝紫 AI 渐变与俗套彩光**：全面转向黑白冷灰中性极简单色系统（`#2563EB` 纯蓝点缀）；
   - **完全无多余 Icon 纯粹界面**：清理全部非必要 Emoji 与图形符号，仅保留折叠箭头与路径 `📂`；
   - **空状态与顶栏胶囊重构**：顶部引入 `SYSTEM READY` 极简冷调标牌，`EmptyStateContainer` 重构为带有 `NO CREDENTIALS FOUND` 的深层沉降插槽样式，使得有无凭证状态下视效均呈现质感飞跃；
   - **全量 DropShadow 悬浮阴影**：为所有卡片注入 `BlurRadius=10, Opacity=0.04` 的悬浮轻盈质感；
   - **代码/数据字体对齐**：配额百分比、倒计时时间、凭证 Badge 强化使用 `Cascadia Code / Consolas` 字体，主次对比分明，建立严谨专业的桌面软件视觉标准。

**验证方式**：
- `dotnet build` 编译 0 错误 0 警告；
- 界面控件与事件链路审查闭合：路径编辑、选择/打开/恢复默认、便携模式快捷设置、出厂重置确认与重启持久化链路完整。

### 2026-08-08 — v1.0.23（变更类型：优化，界面质感精修，不动布局结构）

**背景**：用户明确要求「在现有基础上不要做大改动，主要优化界面质感」。本次全部为细节层质感精修，布局结构、控件位置、功能逻辑零改动。

**变更内容**：
1. **阴影层次精修**（`src/MainWindow.xaml`）：
   - 窗口投影：`Blur 18→26 / Depth 2→3 / Opacity 0.28→0.16`，由浓影改为大范围淡柔影，更现代轻盈；
   - `VergeCard` 卡片阴影：`Blur 10→14 / Depth 1→2 / Opacity 0.04→0.06`，悬浮感清晰可见；
   - 账号卡片阴影同步加强（`MainWindow.xaml.cs` 两处渲染点）。
2. **按钮 hover 质感重构**（`MainWindow.xaml` `BaseBtn` 模板）：由「整体透明度变化（hover 0.88）」改为「叠加半透明深色层（hover `#0A000000` / pressed `#18000000`）」——蓝底按钮 hover 更饱满、白底按钮 hover 微灰、文字不发虚，深浅主题通用。
3. **输入框聚焦光晕**（`MainWindow.xaml` TextBox 模板）：聚焦时边框变主题蓝 + 8px `#2563EB` 蓝色微光晕（`Opacity 0.28`），输入态更明确精致。
4. **标题栏底部分隔线**：1px 主题边框色（70% 透明度）置于标题栏底部，强化「标题栏 / 内容区」层级。
5. **滚动条 hover 反馈**：悬停滑块由 `ThemeScrollThumb` 加深为 `#94A3B8`。
6. **配额进度条渐变填充**（`MainWindow.xaml.cs` `CreateQuotaBar`）：纯色填充改为同色系线性渐变（`#2563EB→#60A5FA`、`#3B82F6→#93C5FD`、`#EF4444→#F87171`），数据条观感更润。
7. **徽章细边框**（`AddBadge`）：彩色徽章加 1px 半透明黑描边（`#20000000`），精致度提升。
8. **账号卡片 hover 交互**（新增 `AttachCardHoverFeedback`，挂接两处卡片渲染）：悬停时边框泛主题蓝（`#2563EB` 45%）、阴影加深至 `0.12`，移出自动恢复；删除、刷新等事件链路不受影响。

**验证方式**：
- `dotnet build` Release 0 错误 0 警告；`scripts/build_only.ps1` 覆盖根目录 `Haodo.exe` 成功；
- 冒烟测试：启动后进程 `Responding=True`，已清理；
- 深浅色主题：所有新增颜色走 `DynamicResource` 主题键 / 半透明黑叠加 / 主题蓝点缀，两模式均自适应。

### 2026-08-08 — v1.0.24（变更类型：修复与优化，窗口阴影裁切修复 + 质感精修续）

**背景**：用户反馈「窗口阴影似乎被容器遮挡，边缘不柔和」。排查确认根因：`RootBorder` 透明边距仅 12px，而阴影 `Blur 26` 的扩散范围超出窗口边界，阴影在窗口边缘被硬性裁切出方形硬边；同时发现代码中三处阴影透明度硬编码 `0.28` 与 XAML 新值 `0.16` 不一致，拖动/最大化切换后阴影会突然变浓。

**变更内容**：
1. **窗口阴影裁切修复**（`src/MainWindow.xaml` + `src/MainWindow.xaml.cs`）：
   - `RootBorder` 透明边距 `12 → 16`（容器给阴影留足扩散空间）；
   - `RootShadow` 参数收敛：`Blur 26→18 / Depth 3→2 / Opacity 0.16→0.14`，阴影在 16px 边距内自然衰减至不可见，不再出现硬截断；
   - 统一全部阴影透明度硬编码：`Window_StateChanged` / `PreviewMouseLeftButtonUp` / `LostMouseCapture` 三处 `0.28 → 0.14`（与 XAML 一致），消除拖动、最大化后阴影突变；
   - `WinResizeEdge 14 → 16`（手动缩放热区跟随新透明边距，注释同步）。
2. **质感继续优化**（`src/MainWindow.xaml` + `src/MainWindow.xaml.cs`）：
   - 主按钮 `BtnPrimary` 由纯色改为**垂直微渐变**（`#2F6FF0 → #2563EB → #1D4ED8`），蓝更饱满（深浅主题统一蓝色系，`StaticResource` 固定）；
   - 配额进度条轨道加 1px 极淡内描边（`#0A000000`），细节更精致；
   - 彩色徽章圆角 `4 → 5`。

**验证方式**：
- `scripts/build_only.ps1` 编译成功，`Haodo.exe` 覆盖根目录；冒烟测试启动 `Responding=True`；
- `grep RootShadow.Opacity` 确认无 `0.28` 残留，阴影参数全局统一；
- 拖动窗口、最大化/还原后阴影无突变，边缘柔和无裁切硬边。

### 2026-08-08 — v1.0.33（变更类型：UI 布局优化，重置功能嵌入「关于」卡片底部）

**背景**：根据用户反馈，取消独立的「重置软件」卡片，将其功能与警示按钮直接嵌入到「关于」卡片的内部底部（位于「检查更新」按钮下方）。

**变更内容**：
1. **删除独立卡片**（`src/MainWindow.xaml`）：彻底删除了原独立的 `VergeCard` 重置软件块；
2. **嵌入关于卡片底部**：在「检查更新」按钮下方通过分隔线连接，内嵌危险警示区 `重置软件` 说明文案与 `[恢复出厂设置]` 淡红按钮，排版更加紧凑高级、利落省空间。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；展开设置 → 关于，底部直观展示 `恢复出厂设置` 功能按钮，交互响应完好。

### 2026-08-08 — v1.0.47（变更类型：修复 / 诊断增强，实测定位“配额刷新仍全部0%”根因 = 代理出站节点故障，并新增网络降级与分类诊断）

**背景**：用户反馈 v1.0.46 修复后“刷新配额依然是全部 0%”。本次不凭猜测，用真实网络实测定位根因。

**根因（实测证据）**：
- 本机系统代理 `127.0.0.1:7897` 存活，但**出站节点不可用**：经代理实测国内直连规则（`www.baidu.com`）HTTP 200 正常；而所有需要节点出站的域名（`oauth2.googleapis.com`、`cloudcode-pa.googleapis.com`、`www.google.com`、`api.github.com`、`example.com`）**全部 TLS 握手失败**（curl `HTTP 000 / SSL connect error`，HTTP 请求返回 502）。
- 用与程序完全相同的 .NET HttpClient 代码路径编写独立 C# 探针复现：`retrieveUserQuotaSummary`、`loadCodeAssist`、`oauth2 token 刷新` **三个端点全部 `SSL connection could not be established`**。
- 凭证时间戳佐证：`antigravity-*.json` 的 `timestamp=2026-08-08T06:32:57Z`（= 本地 14:32:57）说明 14:32 时网络正常、token 刷新成功并写入文件；此后代理节点故障，配额查询失败 → 显示 0%。
- **结论：配额显示 0% 的直接原因是代理出站节点故障，不是配额端点或凭证逻辑问题**。此前代码在网络异常时静默返回 `(0, "无数据")` 且日志只有英文异常（如 `The SSL connection could not be established`），用户无法判断根因。

**变更内容**（`src/MainWindow.xaml.cs`）：
1. **网络错误分类诊断日志**：`FetchRealTimeQuotaAsync` 区分 `HttpRequestException`（输出「⚠️ 网络连接失败…请检查代理是否开启、代理节点是否可用」）与 `TaskCanceledException`（超时提示），不再笼统混为一条英文异常；
2. **上次成功配额降级**：新增 `_lastGoodQuotas`（文件路径→最近成功配额）字典。主界面 `RefreshAccounts` 与微贴单独刷新两条路径中，本次查询失败时自动降级显示上次成功数据并打 `[降级]` 日志，**避免网络故障时出现误导性的“全部 0%”**；查询成功时实时更新缓存；
3. **`UpdateJsonToken` 字段一致性**：刷新回写时同步归一化 `expires_in=3599` 并重算 `expired`（此前只更新 `access_token`/`timestamp`，`expired` 残留旧值，可能导致其他消费方误判）；
4. **端点响应诊断**：`retrieveUserQuotaSummary` 返回 HTTP 200 但缺少 `groups` 结构时输出 `[Antigravity 诊断]` 响应片段（300 字符），方便日后端点结构变化时快速定位。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；代理节点恢复后重新打开程序，配额应正常加载；若代理仍故障，运行日志会明确提示「⚠️ 网络连接失败…请检查代理节点」，且刷新失败时显示上次成功数据而非全 0。

### 2026-08-08 — v1.0.46（变更类型：修复 / 优化，重构 Token 现存直查优先与容错刷新机制，彻底修复配额变0隐患）

**背景**：排查用户反馈“配额不加载全部为0”问题。原机制在本地时间判定 token 到了过期时间后，会直接弃用现有 access_token 并强行触发网络刷新；当网络由于未挂代理/连接超时无法连上 Google OAuth 刷新服务器时，刷新失败返回 null，导致原本依然有效的 access_token 被直接作废、配额卡片全部降级为 0% "无数据"。

**变更内容**（`src/MainWindow.xaml.cs`）：
- **现存 Token 直查优先**：改变“本地先判过期 -> 刷新失败 -> 全清为0”的缺陷逻辑，改为优先使用现有 `access_token` 向服务器打配额接口；只要 Token 在服务端依然有效，秒级返回真实准确配额，绝不因网络连不上 OAuth 刷新服务器而误清零；
- **精准触发刷新与重试**：仅当直查失败（或 Token 确实被服务端拒绝）时，才触发 Token 自动刷新，刷新成功后自动使用新 Token 重试查询并更新本地文件；
- **详细诊断日志告警**：在 `FetchAntigravityQuotaAsync` 中补充 HTTP 错误码与详细响应截取，在运行日志中清晰呈现错误根因（如 `[Antigravity 错误] HTTP 401 / ⚠️ Token 刷新失败，请检查网络连接或代理`）。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；现有凭证直查优先连通，即使网络暂时无法连接 OAuth 刷新点，有效 Token 配额依然正常加载展示。

### 2026-08-08 — v1.0.45（变更类型：UI 优化，彻底清除滚动边缘阴影，回归极简纯粹圆角容器结构）

**背景**：按用户明确要求，彻底移除滚动阴影，保持凭证卡片容器最纯粹干净的视觉呈现。

**变更内容**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
- **移除阴影控件**：彻底清理 `ScrollTopShadow` 与 `ScrollBottomShadow` 这两个渐变 Border；
- **清理后端逻辑**：删除 `MainQuotaScrollViewer_ScrollChanged` 事件处理器及 `ApplyTheme()` 中阴影颜色设置逻辑；
- **保留圆角矩形槽**：凭证卡片列表完整保留外层 `CornerRadius="10"`、`ThemeSubtleBg` 的【圆角矩形容器底板】，在 8px 内边距视口内部流畅滚动，极致干练、利落纯净。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；卡片容器彻底清除阴影盖板，视口干净利落。

### 2026-08-08 — v1.0.44（变更类型：UI 优化，升级阴影扩散范围至 24px 广域 4-Stop 抛物线超柔无痕渐变）

**背景**：针对用户反馈“狭窄 10px 导致阴影边缘生硬”的问题，重构阴影物理扩散距离与渐变衰减曲线。

**变更内容**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
- **广域扩散 (24px Dispersal)**：将阴影 Border 的高度从 10px 扩大至 **24px**（扩散空间扩展 2.4 倍），彻底消除了原先窄阴影带形成的生硬切线；
- **4-Stop 抛物线衰减曲线**：渐变采用 4 级抛物线衰减（Offset 0.0 -> 0.25 -> 0.60 -> 1.0），从容器边缘向内层如烟雾般无痕散开；
- **临界距离扩增**：将 C# 动态淡入淡出临界触发距离扩大为 `32px`，配合 Smoothstep 缓动公式，带来极致丝滑自然的光影效果。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；滚动列表时，阴影呈广域大弧度超柔沉降，无任何窄边或生硬切线。

### 2026-08-08 — v1.0.43（变更类型：UI 优化，重构滚动动态阴影为 Smoothstep S型缓动与深浅主题自适应光晕色调）

**背景**：纠正此前写死硬编码黑色 `#30000000` 导致界面脏粗、以及线性跳变导致淡出过于生硬的重大缺陷。

**变更内容**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
- **色调自适应与去除脏灰**：彻底废弃写死黑色！在 `ApplyTheme()` 中根据深浅主题动态更新基色——浅色模式采用极淡 slate 灰蓝 `#140F172A`（不透明度约 8%），深色模式采用深夜暗沉色 `#4805080E`，与容器背景无缝渐变融合，极具高级感；
- **Smoothstep S型缓动算法**：在 `MainQuotaScrollViewer_ScrollChanged` 中采用 `t * t * (3 - 2 * t)` 缓动公式，过渡行程由 8px 扩展至 24px；阴影淡入淡出如同微光顺滑泛起，彻底消除了生硬跳变与突兀感。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；滑动鼠标时，阴影在容器边缘极度自然顺滑地泛起与消隐，深浅模式下色彩和谐高级。

### 2026-08-08 — v1.0.42（变更类型：UI 优化，隔离动态阴影至容器内边距槽，实现 0% 卡片遮挡）

**背景**：纠正阴影遮盖卡片头部的缺陷。将动态滚动过渡阴影精准限制在容器 Border 自身的 8px 内边距槽内。

**变更内容**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
- **物理隔离**：设置 `ScrollViewer` 的 Margin 为 `8,8,8,8`，卡片滚动视口严格限定在内边距 8px 之内；
- **阴影精准约束**：顶部/底部动态阴影 Border 高度限制为 `8px`（`Margin="1,1,1,0"` / `Margin="1,0,1,1"`），与容器边框圆角完美贴合；
- 阴影在滚动时 100% 作用于【容器 Border 槽位】本身，0% 侵入/遮盖凭证卡片，卡片头部保持绝对清晰干净。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；滚动页面时，动态阴影在容器顶/底槽内平滑淡入淡出，绝对不压在卡片文字或头部上。

### 2026-08-08 — v1.0.41（变更类型：UI 优化，实现容器边缘 Scroll-linked 动态滚动过渡阴影）

**背景**：纠正此前静态盖板阴影缺陷，按用户精准诉求实现“在容器上随滚动状态动态触发过渡”的边缘沉浸式阴影。

**变更内容**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
- **清除静态脏污杂项**：彻底删除了此前静态常驻在视口内外的黑色盖板与外框投影，容器恢复极简干净底板；
- **动态滚动阴影层**：在圆角矩形容器内侧边缘放置 `ScrollTopShadow` 与 `ScrollBottomShadow` 渐变 Border（初始 `Opacity="0"`）；
- **C# 滚动状态事件联动**（`MainQuotaScrollViewer_ScrollChanged`）：
  - 当列表在最顶端（`VerticalOffset == 0`）时，顶部阴影完全不显示（`Opacity = 0`），容器极其干净；
  - 当列表向下滚动（`VerticalOffset > 0`）时，顶部阴影随滚动量平滑淡入呈现，产生卡片卷入顶部的立体过渡感；
  - 当下方还有未滚出的卡片（`remaining > 1`）时，底部阴影柔和呈现；滚到底部时自动淡出消失。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；不滚动或无滚动条时容器彻底纯净无阴影，向下滚动时顶部/底部阴影根据滚动状态平滑过渡显示。

### 2026-08-08 — v1.0.40（变更类型：UI 优化，修正卡片容器阴影为外侧浮立 DropShadow 投影）

**背景**：纠正此前在容器内部粗暴叠加黑色渐变层导致界面污脏的缺陷，重构为符合现代设计规范的容器外侧卡片悬浮投影。

**变更内容**（`src/MainWindow.xaml`）：
- 移除内部叠加的静态黑色渐变层；
- 为凭证卡片圆角矩形容器 Border 独立配置精致的 `DropShadowEffect` 悬浮外阴影（`BlurRadius="14"`，`Opacity="0.08"`，`Direction="270"`）；
- 容器内部恢复为极简干净视口，卡片在带有外侧浮立投影的圆角矩形底板内部流畅滚动，画面清爽、层次高雅。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；容器呈现在窗口上方浮立的现代高级质感，内部视口干净清爽。

### 2026-08-08 — v1.0.39（变更类型：UI 优化，圆角矩形容器内嵌上下立体滚动氛围阴影）

**背景**：按用户要求，在凭证卡片圆角矩形容器内部，为滚动的上下边缘增加立体氛围内阴影遮罩。

**变更内容**（`src/MainWindow.xaml`）：
- 在圆角矩形容器 `Border` 内部的 `Grid` 中，在 `ScrollViewer` 视口上方与下方各叠加了一层 14px 高度的渐变阴影遮罩 `Border`（`Top Inner Scroll Shadow` & `Bottom Inner Scroll Shadow`）；
- 使用渐变色 `LinearGradientBrush`（`#24000000` -> `#00000000`），产生细腻柔和的边缘滚进/滚出深度阴影效果；
- 阴影层设置 `IsHitTestVisible="False"`，保证不阻挡任何鼠标拖拽、点击与滚动交互。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；卡片在圆角矩形槽内滚动时，上下边缘呈现极具层次感的立体深浅内阴影。

### 2026-08-08 — v1.0.38（变更类型：UI 优化，凭证卡片列表专属沉浸式圆角矩形容器底板重构）

**背景**：澄清用户意图：此前凭证卡片裸露漫浮在窗口背景上，滚动时上下边缘无明确视口收纳框架。用户要求为卡片区域设计专属的【圆角矩形容器】。

**变更内容**（`src/MainWindow.xaml`）：
- 为凭证卡片列表专门设计了外层 **【圆角矩形容器 Border】**（`CornerRadius="10"`、背景 `ThemeSubtleBg`、1px 细边框 `ThemeCardBorder`）；
- 将 `ScrollViewer` 完整包裹在圆角矩形容器内部，卡片列表在容器框内平滑滚动；
- 四周预留 `Padding="8,8,8,0"` 呼吸空间，形成高端软件常见的“沉浸式圆角矩形槽装载卡片”设计语言，彻底消除了裸露漫浮与生硬断开感。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；凭证卡片列表整齐收纳在圆角矩形槽内部，滚动边界自然清晰、视觉质感显著提升。

### 2026-08-08 — v1.0.37（变更类型：UI 优化，滚动视口引入上下边缘 Alpha 渐变羽化遮罩，消除硬切断感）

**背景**：用户指出滚动卡片列表时，卡片在视口顶端和底端边缘直接被“刀切”硬切断，视觉上不够高级平滑，存在生硬的切断感。

**变更内容**（`src/MainWindow.xaml`）：
- 为主界面 `ViewMain` 的凭证滚动容器（`Grid`）与设置页 `ViewSettings` 滚动容器（`StackPanel`）引入 `OpacityMask` 垂直 Alpha 渐变遮罩（`LinearGradientBrush`）；
- 上下边缘（0~3% 与 97~100%）呈现平滑的 Alpha 渐隐/羽化效果；中间 94% 区域保持 100% 不透明高清晰度；
- 当凭证卡片滚入或滚出视口上下边缘时，以高级的渐隐淡出（Smooth Gradient Fade）效果消失，彻底消除锯齿和硬切断感，与底部分割线及顶部导航卡片完美衔接。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；滚动凭证卡片与设置卡片，上下边缘以极佳质感羽化渐隐，无任何生硬切断边。

### 2026-08-08 — v1.0.36（变更类型：UI 优化，运行日志独立分割隔离方案定稿）

**背景**：按用户确认，采用“独立分隔方案”，将上部的凭证列表滚动视口与下部的日志及控制区域通过物理分割线彻底隔开。

**变更内容**（`src/MainWindow.xaml`）：
- 恢复上部 `ScrollViewer` 为独立滚动视口，仅装载账号凭证卡片列表 `QuotaAccountsContainer` 与空状态引导卡片 `EmptyStateContainer`；
- 在凭证滚动视口下方新增精致 **1px 物理分界线**（`ThemeCardBorder`），为上部凭证列表向下滚动时提供精准的视觉边界与平滑裁切视口；
- 下部固定区域承载独立的运行日志 `VergeCard` 卡片以及底部的快捷控制按钮与 `代理状态灯`，上下边界分明利落。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；凭证卡片上下滚动时在底部分割线处平滑裁切，不遮挡、不透叠底部的运行日志面板。

### 2026-08-08 — v1.0.35（变更类型：UI 优化，运行日志卡片移入 ScrollViewer 滚动列表实现连贯平滑体验）

**背景**：用户反馈当账号凭证较多时在列表中滚动，底部的运行日志卡片若固死在窗口底部，凭证卡片向下滚动时会从日志卡片后方切过，产生明显的视觉割裂感。

**变更内容**（`src/MainWindow.xaml`）：
- 将「运行日志卡片」整体从窗口底部固定栏移入 `ScrollViewer` 内部，与账号凭证卡片 `QuotaAccountsContainer` 成为同级别的平级列表项；
- 滚动凭证卡片列表时，运行日志卡片作为最后一项随内容自然平滑向上/向下滚动，彻底解决固定底部带来的视觉穿透与割裂感；
- 窗口最下方的吸底容器仅保留一行 28px 高度的轻量控制底栏（`刷新配额` / `登录 Gemini` 与 `代理状态灯`）。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；多账号下滚动页面内容，运行日志卡片在列表末尾自然平滑随动，无任何割裂感。

### 2026-08-08 — v1.0.34（变更类型：UI 优化，运行日志独立 VergeCard 卡片化重构）

**背景**：澄清用户意图：此前运行日志仅作为底层纯色块放在窗口底部，没有外层 `VergeCard` 卡片容器，视觉上直接与窗口背景融为一体。

**变更内容**（`src/MainWindow.xaml`）：
- 将运行日志收纳模块整体包装为标准的 `Border Style="{StaticResource VergeCard}"` 卡片容器；
- 继承项目统一的卡片背景（`ThemeCardBg`）、1px 精致边框（`ThemeCardBorder`）与微浮悬浮阴影（`DropShadowEffect`）；
- 未展开时呈现为优雅干净的单行精致卡片，点击展开后日志面板（`LogPanelContainer`）在卡片内部向下进行平滑扩展，与其他功能卡片视觉风格完全一致；
- 微调底部控制按钮与代理状态灯的边距（`Margin="0,2,0,0"`），使整体垂直布局比例更加协调。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；运行日志模块呈现为独立标准的 VergeCard 卡片，收起与展开均具有极佳质感。

### 2026-08-08 — v1.0.33（变更类型：UI 优化，顶部品牌导航栏上边距调整与标题栏对等隔离）

**背景**：用户反馈顶部导航栏（包含 `SYSTEM READY` / `账号统计` / `主界面` / `设置`）上方紧贴标题栏底部分隔线、无留白空隙。

**变更内容**（`src/MainWindow.xaml`）：
- 将 `MAIN APP CONTENT` 容器 Grid 的 `Margin` 从 `14,0,14,14` 调整为 `14,14,14,14`；
- 使顶部导航卡片上方与标题栏底部分隔线之间增加 14px 留白空隙，实现上下左右四边边距完全对称对等。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；界面顶部留白与其他三边完全对称一致。

### 2026-08-08 — v1.0.32（变更类型：UI 优化，全界面设置页与小窗口精简模式深度自适应）

**背景**：用户反馈窗口缩至极小尺寸（如 384px 宽）时，设置页部分控件（Base URL 胶囊、数据目录按钮组、模型测试按钮等）若不进行响应式精简容易挤压溢出。

**变更内容**：
1. **文案更正**：设置「关于」架构描述更新为 `自绘无边框界面，Gemini Antigravity 配额实时监测与本地代理集成。`；
2. **设置页全维度精简模式 (Compact Mode) 响应式适配**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
   - **本地 API 代理**：Base URL 胶囊前缀在小窗下由 `http://127.0.0.1:` 精简为 `127.0.0.1:`；`复制 URL` 按钮精简为 `复制`；`获取模型` 按钮精简为 `获取`；
   - **数据目录卡片**：`便携模式 (./data)` 按钮精简为 `便携模式`；`恢复默认` 按钮精简为 `重置`；`刷新` / `打开` / `重置` 按钮内边距收紧至 `6px`，给左侧路径输入框留出充足可读宽度；
   - **关于卡片**：QQ 群胶囊按钮在小窗下精简为 `QQ群: 453478357`，防止推挤变形。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；在 384px 极窄尺寸下展开设置页所有卡片，各按钮与输入框布局完好、绝对无任何溢出遮挡。

### 2026-08-08 — v1.0.31（变更类型：UI 优化，撤销设置页模式绑定，重构主界面精致响应式精简模式）

**背景**：澄清用户意图：设置页保持全尺寸精致展现，无需配合精简模式缩减内容；精简模式核心聚焦在主界面窗口缩小下的**低密度、高辨识度与极佳质感**。

**变更内容**：
1. **撤销设置页精简模式控制**：从 `ApplyCompactModeUI` 中完全移去关于设置页 `TxtAboutBrandDesc`、`TxtTechDesc`、`TxtQQGroupBtnText`、`AboutBrandBanner` 的隐藏与缩减控制，设置页恢复独立完整展现；
2. **主界面精简模式 (Compact Mode) 重构**（`src/MainWindow.xaml` 与 `src/MainWindow.xaml.cs`）：
   - **顶部导航卡**：不粗暴隐去文字！改为微缩字号（`9px` / `11px`）与紧凑 Padding（`8px`），分段按钮保持图标+文字完整呈现；
   - **凭证卡片 Header**：邮箱增加 `CharacterEllipsis` 剪裁防撑开；套餐徽章在精简模式下智能展示短标签 `Pro`；
   - **凭证卡片 Meta 印记**：不再粗暴全删，改为单行极简高质感印记 `📄 claude-1.json · 256B`（`9.5px`），不占高度但保留文件身份；
   - **配额进度条（核心控件放大）**：配额进度条高度提升至 `8px` 饱满胶囊（`4px` 圆角），剩余百分比升至 `13px` 粗体高亮，标签精简为 `5h 限额` / `周限额`；
   - **底部控制栏**：代理状态灯文本精简为 `代理运行中` / `代理未开启`，按钮内边距与高度比例收紧，保证 384px 宽下绝对不拥挤冲突。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；展开设置页不受精简模式影响，缩窄主界面展示精致低密度卡片与放大版 8px 饱满胶囊进度条。

### 2026-08-08 — v1.0.30（变更类型：UI 优化，设置「关于」卡片高颜值重构与精简模式深度适配）

**背景**：用户反馈「关于」卡片排版过于平淡，且需要深度适配缩小窗口时的精简模式（Compact Mode）。

**变更内容**：
1. **关于卡片重构**（`src/MainWindow.xaml`）：
   - **品牌 Header Banner**：新增浅背景卡片 + 蓝底发光 `H` Logo 徽章 + `Haodo Pro Quota` 标记；
   - **作者与交流区**：精致信息条，右侧配有专属 **QQ群交流胶囊按钮**（`Quicker地球村 (QQ群: 453478357)`，带 Icon 与 Hover 态）；
   - **技术架构模块**：标签云 `[.NET 10]` `[WPF]` `[SingleFile]` `[Encrypted Storage]` + 描述；
2. **精简模式深度适配**（`src/MainWindow.xaml.cs` `ApplyCompactModeUI`）：
   - 小窗口下自动隐去 Banner 描述 `TxtAboutBrandDesc` 与架构长句 `TxtTechDesc`；
   - QQ群胶囊按钮文本动态压缩为 `QQ群: 453478357`，收紧边距，确保小窗展开时完全不拥挤、高质感呈现。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；展开设置 → 关于，深浅主题及窗口缩放均完美呈现。

### 2026-08-08 — v1.0.29（变更类型：功能，设置「关于」卡片增强）

**背景**：用户希望在设置页「关于」中增加软件信息（作者/开发维护/技术架构），参考其他软件格式，并给 QQ 群号加超链接。

**变更内容**（`src/MainWindow.xaml` 设置页「关于」卡片 + `src/MainWindow.xaml.cs`）：
1. **关于软件信息区**：卡片展开内容新增「关于软件」小标题 + 三行信息（标签列 76px + 值列）：
   - 作者：北京林或西
   - 开发维护：Quicker地球村（QQ群：453478357）—— 群号部分为 Hyperlink 样式（品牌蓝 + 下划线 + ToolTip）
   - 技术架构：基于 .NET 10 + WPF 自绘无边框界面，单文件发布；多平台 AI 账号配额实时监测，凭证本地加密存储
2. **QQ 群超链接**（`LinkQQGroup_RequestNavigate`）：点击先复制群号到剪贴板（兜底保障），再通过 `tencent://groupchat/?uin=453478357` 协议唤起 QQ；协议打开失败时弹窗提示已复制群号，请用户手动搜索加入。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；展开设置 → 关于可见信息三行，点击 QQ 群号复制群号并尝试唤起 QQ。

### 2026-08-08 — v1.0.28（变更类型：功能，窗口精简模式 Compact Mode）

**背景**：用户希望保持主界面整体布局不变的前提下放宽窗口最小缩放限制；窗口缩至更小尺寸时自动进入「精简模式」——内容密度降低、仅保留核心信息、进度条等关键控件按比例放大，小窗下依然美观清晰。

**变更内容**：
1. **窗口约束放宽**（`MainWindow.xaml`）：`MinWidth 444→384`、`MinHeight 704→560`；
2. **精简模式状态机**（`MainWindow.xaml.cs`）：
   - 字段 `_compactMode`；`UpdateCompactMode()` 挂在现有 `SizeChanged` 上，窗口宽 < 440 或高 < 680 时进入（默认 484×842 不触发），变化才处理防抖；
   - `ApplyCompactModeUI()`：切换静态元素 + 重建凭证卡片（走 `RefreshAccountsUIOnly` 缓存）；
3. **静态元素精简**：SYSTEM READY 文字、「个账号」标签、导航按钮文字（主界面/设置）隐藏（仅留白点/数字/图标，ToolTip 兜底）；底部代理状态灯仅留状态点；空状态按钮文字简化（「登录 Google 获取凭证 (Gemini)」→「登录 Gemini」、「导入 JSON 凭证」→「导入凭证」）、隐私提示槽隐藏；
4. **动态卡片精简**（两处卡片构建 + `CreateGroupSection` + `CreateQuotaBar` + `AddBadge`）：meta 行（文件/大小/修改时间）与套餐徽章省略；进度条高度 6→10 胶囊化（圆角随高度）；「剩余 X%」12→14、邮箱 13→14、组标题 13→14、徽章 10→11；卡片 Padding 14,12→12,10。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；窗口缩至 384×560 主界面自动精简、恢复 484×842 完整模式回归，深浅主题均正常。

### 2026-08-08 — v1.0.26（变更类型：UI 优化，顶部导航栏品牌感重构）

**背景**：用户希望顶部导航排（SYSTEM READY / 账号统计 / 主界面 / 设置）更有品牌感；初版加入的 3px 顶部渐变条被用户否决，改为左侧品牌侧标方案。

**变更内容**（`src/MainWindow.xaml` 顶部导航卡 + `src/MainWindow.xaml.cs` 账号计数更新）：
1. **品牌侧标**：导航卡内容左侧新增 4px 宽、30px 高、圆角 2 的品牌蓝（`#2563EB`）竖条，作为卡片品牌锚点；
2. **就绪徽章实心化**：`SYSTEM READY` 徽章改为实心品牌蓝底（`ThemePrimary`）+ 白字 + 白色发光点（7px `#FFFFFF` + 同色光晕），品牌识别最强；圆角 6、内边距 9,3；
3. **账号计数拆分**：`共 2 个账号` 改为大号品牌色数字（Cascadia Code 17px `#2563EB`）+ 小号「个账号」标签（11px 次级文字），中间加竖向分隔线；C# 更新逻辑改为 `TxtAccountCount.Text = accounts.Count.ToString()`（原 `TxtAccountSummary` 移除）；
4. **分段导航带图标**：主界面 = Segoe MDL2 Assets `E80F`（Home），设置 = `E713`（齿轮），图标颜色随按钮 active/inactive 前景色自动切换（白/灰）；按钮宽度从固定 70 改为内容自适应（Padding 12）。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；深浅主题下侧标与徽章为品牌蓝固定色，两模式协调。

### 2026-08-08 — v1.0.25（变更类型：优化，配额进度条分级警示色阶）

**背景**：用户希望配额快用完时进度条呈现橙色/红色警示，且 5h 与周两种进度条在警示态下也保持两种区分效果（此前仅 `percent==0` 时两者统一变红）。

**变更内容**（`src/MainWindow.xaml.cs` `CreateGroupSection` / `CreateQuotaBar`）：
1. **分级警示色阶**（阈值 `<30%` 为紧张、`<=0%` 为用尽）：
   - 5h 限额：充足 `#2563EB` 蓝 → 紧张 `#F97316` 橙 → 用尽 `#EF4444` 红；
   - 周限额：充足 `#3B82F6` 浅蓝 → 紧张 `#EF4444` 红 → 用尽 `#DC2626` 深红；
   - 紧张/用尽时进度条渐变填充映射同步补齐（橙 `#FB923C`、深红 `#EF4444`）。
2. 「剩余 X%」文字颜色跟随警示色：`percent < 30` 时文字使用与进度条相同的橙/红色，不再仅 0% 才变红。

**验证方式**：`build_only.ps1` 编译成功覆盖根目录 `Haodo.exe`；冒烟测试 `Responding=True`；深浅主题下警示色为固定色阶，两模式一致。

### 2026-08-07 — v1.0.21（变更类型：重构，数据目录管理零选择题化）

**背景**：用户反馈切换数据目录交互"诡异"——多层意图猜测（自动迁移/三选一/空目录引导）叠加，行为不可预期，要求按最合理方案整体重构。

**设计**：行为由唯一事实决定——**新目录是否为空文件夹**（顶层无任何文件），全程不弹选择窗；弹窗只承担「结果告知」职责。

**变更内容**（`MainWindow.xaml.cs`）：
1. `TryChangeDataDirectory`：删除三选一弹窗分支与"目标有无凭证"猜测逻辑，改为 `migrate = oldCreds.Count > 0 && IsDirEmpty(newDir)`（新增 `IsDirEmpty`：顶层无任何文件才算空文件夹——防止凭证被搬进有文件的系统目录，也保证"选空目录=数据跟过去"）→ 直接同步执行切换；
2. `ApplyDataDirectorySwitch`：删除 `notifyMigrated` 参数、中途弹窗与"空目录引导"弹窗；切换完成后末尾统一弹结果告知（四种文案：已迁移 N 个 / 迁移失败保留原地 / 新目录已有 M 个凭证旧目录保留 / 新目录暂无凭证可导入或登录）；
3. 弹窗系统清理：删除 `ShowThreeChoiceModal` 与 `_modalOkAction` 字段，`BtnModalOk_Click` 简化为仅关闭；`ShowCustomModal`/`ShowConfirmModal` 不变；
4. "恢复初始数据"二次确认保留；`MigrateAndScanDataDirectory` 启动归集兼容保留；三个入口（文本框/浏览/恢复默认）继续统一走 `TryChangeDataDirectory`，返回 false 仅表示失败（不再有"弹窗期间暂未切换"的中间态，输入框闪烁问题消除）。

**验证方式**：dotnet build Debug 0 错误 0 警告；grep 确认 `_modalOkAction`/`ShowThreeChoiceModal`/`notifyMigrated` 无残留；启动 Haodo.exe 存活 Responding=True，进程已清理；四场景矩阵走查：空文件夹→迁移、有文件→仅切换、无凭证→直接切、不可写→报错。

### 2026-08-07 — v1.0.20（变更类型：调整，数据目录切换按用户意图推断）

**背景**：用户指出：选一个空目录时，若旧目录本来有凭证和数据，大概率是想迁移数据过去——交互应猜测用户意图，而不是一律弹三选一。

**变更内容**（`MainWindow.xaml.cs`）：
1. `TryChangeDataDirectory` 增加目标目录凭证统计（新增 `CountValidCredsIn`，排除 settings.json）；
2. 意图分场景：
   - **选空目录 + 旧目录有凭证** → 不弹窗，直接自动迁移（空目录无覆盖风险），完成后提示「已将 N 个凭证自动迁移至新目录」（`ApplyDataDirectorySwitch` 新增 `notifyMigrated` 参数）；
   - **目标目录已有数据 + 旧目录有凭证** → 保留三选一（迁移/仅切换/取消），文案补充「目标目录已有 N 个凭证，迁移时将跳过同名文件」；
   - **旧目录无凭证** → 直接切换（不变）。
3. 空目录引导弹窗保留（仅「仅切换」且新目录无凭证时出现）。

**验证方式**：dotnet build Debug 0 错误 0 警告；启动 Haodo.exe 存活 Responding=True，进程已清理；逻辑走查三分支闭合。

### 2026-08-07 — v1.0.19（变更类型：调整，本地代理卡片头部布局定稿 + 模型分组排序）

**背景**：用户要求代理卡片折叠/展开交互与其他设置卡片完全一致（整行可点击 + 右侧箭头），但头部布局特殊：标题 | 状态徽章 | 运行 | 停止 | 箭头。

**变更内容**（`MainWindow.xaml` + `MainWindow.xaml.cs`）：
1. 头部为整行 `SettingsCardHeader` Button（同其他卡片，模板自带底部分隔线），内部 Grid 4 列：标题靠左（Margin 14,11,0,11 同款）→ 弹性空白（点击同样折叠）→ 徽章+运行+停止整组靠右（`TxtLocalProxyStatusBadge`/`BtnLocalProxyOn`/`BtnLocalProxyOff`，运行/停止为内嵌按钮不冒泡触发折叠）→ 箭头最右（`TxtArrowLocalProxy`，Margin 0,0,14,0 与其他卡片完全对齐）；`Tag="SettingsLocalProxyContent|TxtArrowLocalProxy"` 折叠逻辑与其他卡片共用 `ToggleSettingsSection_Click`；
2. 内容区仅剩描述/连接配置/模型测试（下拉 | 获取模型 | 测试，3 列）；
3. 获取模型排序（`MainWindow.xaml.cs` BtnFetchLocalProxyModels_Click）：按前缀（去 `-数字`/`-latest` 版本后缀）字母序分组，组内无后缀 → 数字版本升序 → latest 最后，并 `Distinct()` 去重；
4. 内嵌按钮防折叠（`MainWindow.xaml.cs` ToggleSettingsSection_Click）：WPF 中 Button 的 Click 是 bubbling 路由事件，内嵌「运行/停止」按钮点击会冒泡触发外层头部按钮的折叠——在折叠处理器开头从 `e.OriginalSource` 沿视觉树向上查找，若先遇到非 sender 的 Button 则直接 return（不折叠）；点击标题/空白/箭头仍正常折叠。

**验证方式**：dotnet build Debug 0 错误 0 警告；Python 模拟算法输出分组正确；启动 Haodo.exe 存活 Responding=True，进程已清理。

### 2026-08-07 — v1.0.18（变更类型：新功能，本地代理卡片恢复展开/收起）

**背景**：用户询问本地 API 代理卡片无展开收起操作。此前按设计稿常驻展开，现恢复与其他设置卡片一致的头部折叠交互，但默认仍展开。

**变更内容**（`MainWindow.xaml` + `MainWindow.xaml.cs`）：
1. 头部标题改为可点击（`ToggleSettingsSection_Click` + `Tag="SettingsLocalProxyContent|TxtArrowLocalProxy"`），标题右侧箭头 ▶/▼ 与其他卡片一致；状态徽章与分段按钮保持常驻可见（收起的只是内容区）；
2. 内容区包入 `StackPanel x:Name="SettingsLocalProxyContent"`，初始 `Visibility="Visible"`（默认展开）；
3. 主界面代理状态灯点击跳转时，若卡片已收起则自动展开。

**验证方式**：dotnet build Debug 0 错误 0 警告；启动 Haodo.exe（PID 45540）存活 Responding=True，进程已清理。

### 2026-08-07 — v1.0.18（变更类型：新功能，数据目录切换交互增强）

**背景**：用户反馈修改数据目录时凭证不会自动迁移、新用户选择空目录无引导，交互偏弱。确认现状后用户选择「弹窗三选一 + 空目录引导」方案。

**变更内容**：
1. **`MainWindow.xaml`** 弹窗按钮区由 2 列改为 3 列等宽（`BtnModalCancel`/`BtnModalConfirm`/`BtnModalOk`），`BtnModalOk` 点击事件由关闭改为 `BtnModalOk_Click`（可携带动作）；
2. **`MainWindow.xaml.cs`**：
   - 新增 `ShowThreeChoiceModal`（取消/确认/确定 三按钮 + 各自回调）与 `_modalOkAction` 字段，`ShowCustomModal`/`ShowConfirmModal` 同步维护列位置与边距；
   - 新增统一入口 `TryChangeDataDirectory`：路径规范化 → 目录创建 → 可写性校验（写临时文件）→ 同路径短路 → 统计旧目录有效凭证（排除 settings.json）→ 有凭证时弹「迁移到新目录（N 个）/ 仅切换目录 / 取消」三选一；
   - 新增 `ApplyDataDirectorySwitch`：迁移用 `File.Move`（同名跳过防覆盖、失败计数并记日志）→ 切换 `_dataDir`/`_settingsPath` 并保存 → 归集扫描 + 刷新列表/账号 → 新目录无凭证时弹出空目录引导（导入凭证 / Gemini 登录 / 重新选择目录）；
   - 三个入口（文本框输入 `ApplyTxtDataDirChange`、浏览选择 `BtnBrowseDataDir_Click`、恢复默认 `BtnResetDefaultDataDir_Click`）全部改为调用 `TryChangeDataDirectory`，取消或失败时还原输入框显示。

**验证方式**：
- dotnet build Debug 0 错误 0 警告；
- 启动 Haodo.exe（PID 44024）存活 Responding=True，弹窗新模板无 XAML 运行时错误，事件日志无崩溃；测试进程已清理；
- 逻辑走查：三入口 → 三选一 → 迁移(Move+防覆盖)/仅切换/取消 → 空目录引导，路径均闭合。

### 2026-08-07 — v1.0.17（变更类型：重构，本地代理卡片文案改名）

**变更内容**（`MainWindow.xaml` 本地代理卡片）：「本地接口」标签改为「Base URL」；其复制按钮「复制」改为「复制 URL」。

**验证方式**：dotnet build Debug 0 错误 0 警告；启动 Haodo.exe（PID 18204）存活 Responding=True，进程已清理。

### 2026-08-07 — v1.0.17（变更类型：重构，端口输入框收窄）

**变更内容**（`MainWindow.xaml` 本地代理卡片）：URL 胶囊内端口 TextBox 列宽 52→42，仅容纳 4 位端口数字，胶囊更紧凑。

**验证方式**：dotnet build Debug 0 错误 0 警告；启动 Haodo.exe（PID 56468）存活 Responding=True，进程已清理。

### 2026-08-07 — v1.0.17（变更类型：重构，本地代理卡片按钮文字与 URL 胶囊）

**背景**：用户要求：①「运行中」按钮改「运行」；② URL 整体呈现为「圆角矩形套圆角矩形」的地址胶囊，端口输入框内嵌其中。

**变更内容**（`MainWindow.xaml` 本地 API 代理卡片）：
1. `BtnLocalProxyOn` 文本「运行中」→「运行」；
2. 本地接口行改为**地址胶囊**：外层 `ThemeSubtleBg` 圆角 8 矩形（高 32）包住 `http://127.0.0.1:` + 端口 TextBox（高 24、圆角 6 内嵌）+ `/v1`，胶囊随行宽弹性伸展；
3. 复制按钮宽度 = 上方「随机+复制」宽度总和：借助 `Grid.IsSharedSizeScope` + `SharedSizeGroup="ProxyKeyBtn"`，复制按钮跨两共享列（ColumnSpan=2 + Stretch）精确对齐。

**验证方式**：
- dotnet build Debug 0 错误 0 警告；
- 启动 Haodo.exe（PID 15120）存活 15 秒 Responding=True，无 XAML 运行时错误；测试进程已清理。

### 2026-08-07 — v1.0.17（变更类型：重构，本地 API 代理卡片去符号 + 分组布局简化）

**背景**：用户对上一版卡片提出 7 点调整：去全部 icon/符号、端口框紧凑、随机/复制用文字、不喜欢 Border 悬浮标题分组、按钮改「测试」、获取模型移入行内下拉右侧。

**变更内容**：
1. **`MainWindow.xaml`** 本地代理卡片：
   - 移除卡片内全部 emoji/符号（🚀⚙️🧪ⓘ↻⎘⚡🟢🔴⏹⚪✓✖⏳等）：标题「本地 API 代理」、徽章纯文字「运行中/已停止」（颜色区分）、分段按钮「运行中/停止」、按钮「随机」「复制」「获取模型」「测试」；
   - 弃用「Border 大块 + 悬浮标题」分组，改为应用既有风格：`SemiBold` 小标题（连接配置/模型测试）+ 1px 分隔线 + 行式布局；
   - 端口 TextBox 紧凑化：列宽 72→52、Padding 6,4→4,2、Height 32→30、数字居中，与 http://127.0.0.1: /v1 贴合；
   - 模型行布局改为 `[下拉框] [获取模型] [测试]`（获取模型在测试左边、下拉右边）。
2. **`MainWindow.xaml.cs`**：徽章文本、状态行文本（SetLocalProxyTestStatus 全部调用点）同步去符号，如「200 OK | 123ms」「HTTP 500 | 12ms」「正在请求 xxx ...」。

**验证方式**：
- dotnet build Debug 0 错误 0 警告；
- 启动 Haodo.exe（PID 1124）存活 16 秒 Responding=True，窗口可视树正常实例化（无 XAML 运行时错误）；测试进程已清理。

### 2026-08-07 — v1.0.17（变更类型：重构，本地 API 代理卡片按设计稿整体改版）

**背景**：按设计稿重构「本地 API 代理」卡片，卡片改为常驻展开（不再折叠），头部集成状态徽章与分段启停按钮，配置与测试分两个带标题的圆角分组，模型选择改为可编辑 ComboBox。

**变更内容**：
1. **`MainWindow.xaml`** 卡片整体重写：
   - 移除折叠按钮，头部常驻：标题 + `TxtLocalProxyStatusBadge` 状态徽章 + `BtnLocalProxyOn`/`BtnLocalProxyOff` 分段切换按钮（🟢 运行中 / ⏹ 停止，当前状态高亮）；
   - 「⚙️ 连接配置」分组：API Key 行（输入框 + `↻` 随机 + `⎘` 复制）与本地接口行（`ⓘ http://127.0.0.1:[端口]/v1` + `⎘` 复制），端口 TextBox 内嵌 URL 中间；
   - 「🧪 模型测试」分组：`CmbLocalProxyModel` 改为 `IsEditable="True"` + `BtnFetchLocalProxyModels`（↻ 获取模型）+ `BtnTestLocalProxyModel`（⚡ 测试当前模型）；结果区改为 Border 包裹的**状态行 + 内容**两段式（`TxtLocalProxyTestStatus` / `TxtLocalProxyTestResult`）；
   - 样式区：`ComboRound` 替换为 `ComboRoundEditable`（含 `PART_EditableTextBox` 模板 + 空值占位符「请选择或输入模型...」）；
2. **`MainWindow.xaml.cs`**：
   - `TxtLocalProxyPort_TextChanged` 实时同步端口到内存（`_localProxyPort`/`_proxyServer.Port`），失焦/回车时保存并重启服务；
   - `UpdateLocalProxySegmentUI` 改为驱动分段按钮样式（BtnSegmentActive/Inactive），并**停止时禁用模型测试控件**；
   - 获取/测试模型改用 `Stopwatch` 精确计时，成功显示 `✓ 200 OK | Nms`，失败显示 `✖ HTTP 状态码 | Nms` 并附错误内容，弃用弹窗提示；
   - 移除旧 `BtnLocalProxyToggle`、`TxtLocalProxyUrl`、`SettingsLocalProxyContent`/`TxtArrowLocalProxy` 相关代码，新增 `GetLocalProxyUrl()` 统一拼接完整地址。

**验证方式**：
- dotnet build Debug 0 错误 0 警告；
- 端口输入实时联动复制地址、启停分段高亮与模型控件禁用逻辑经代码审查确认。

### 2026-08-07 — v1.0.17（变更类型：新功能 / 重构，API Key 加长 + 本地代理卡片 UI 精简）

**背景**：用户反馈随机 API Key 过短（20 字符）、本地代理卡片信息层级过多不够直观。

**变更内容**：
1. **`MainWindow.xaml.cs`**：`BtnGenLocalProxyKey_Click` 随机 Key 格式由 `sk-haodo-`+12 位 hex
   加长为 `sk-haodo-`+32 位 hex（41 字符，与 OpenAI 风格一致）；
2. **`MainWindow.xaml`** 本地代理卡片展开区重构为三个视觉区块（分隔线 1px 分区）：
   - 区块一「服务开关」：分段按钮整行展示，去掉「服务状态」冗余标签（状态由标题行徽章体现）；
   - 区块二「连接信息」：API Key 一排 + 端口/Base URL 一排；Base URL 由只读输入框改为
     **静态胶囊**（`ThemeSubtleBg` 圆角 Border + TextBlock，`TextTrimming` 溢出省略 + ToolTip），
     视觉上不再是可编辑输入框，更轻量；「复制 URL」按钮统一为「复制」；
   - 区块三「模型测试」：获取模型 + 下拉 + 测试模型一行，结果框高度 96→84；
   - 说明文案缩短为一行；
   - `TxtLocalProxyUrl` 由 TextBox 改为 TextBlock（仅 `.Text` 赋值，代码无兼容问题）。

**验证**：编译 0 错误 0 警告；部署后应用正常启动、代理 8317 自启；回归 3 项通过
（`/v1/models` 200 + 30 模型、chat 200、无 Key 401）；测试进程已清理。

### 2026-08-07 — v1.0.17（变更类型：重构，统一 UI 圆角）

**背景**：用户要求界面不出现直角、统一圆角。排查发现全部卡片/按钮/分段控件已有圆角，
唯一直角来源是 TextBox 与 ComboBox（WPF 这两个控件**没有 `CornerRadius` 属性**，系统默认模板为直角）。

**变更内容**（`MainWindow.xaml` 文件头样式区）：
1. 新增**隐式 TextBox 样式**（TargetType TextBox 无 Key，作用于全部 7 个 TextBox）：
   自定义 ControlTemplate（圆角 Border R=6 + `PART_ContentHost`），统一
   `ThemeInputBg`/`ThemeInputBorder`/`ThemeTextPrimary`/Padding 8,4/FontSize 12，
   并带交互反馈：hover 边框变 `ThemeTextTertiary`、聚焦变 `ThemePrimary`；
2. 新增 **`ComboRound` ComboBox 样式**：完整圆角模板（R=6 编辑区 + 透明 ToggleButton 展开层 +
   主题配色圆角下拉面板 R=6 + 三角箭头 Path），应用于 `CmbLocalProxyModel`；
3. `TxtLog`（日志框）显式 `Padding="0"` 覆盖隐式样式，保持原排版（透明无边框不受圆角影响）。

**验证**：编译 0 错误 0 警告；部署后应用正常启动（模板加载无异常）、代理 8317 自启；
快速回归 3 项通过（`/v1/models` 200 + 30 模型、`/v1/chat/completions` 测试对话 200、
无 Key 401）；测试进程已清理。

### 2026-08-07 — v1.0.17（变更类型：新功能 / 重构，本地代理 UI 增强）

**背景**：HTTPS 移除后，设置页本地代理卡片布局与主界面缺少直观的代理运行状态反馈；同时用户希望直接从界面获取模型列表并验证模型连通性。

**变更内容**：
1. **`MainWindow.xaml`**：
   - 「本地 API 代理」卡片移至设置页**最顶部**（外观卡片之前）；
   - API Key **独占一排**（输入框 + 「随机」「复制」纯文字按钮，移除 🎲/📋 图标）；
   - 端口输入框与 API Base URL **同排显示**（端口 + URL + 「复制 URL」）；
   - 新增「模型工具」区块：`获取模型` 按钮 + 模型下拉框（`CmbLocalProxyModel`）+ `测试模型` 按钮 + 结果只读文本框（`TxtLocalProxyTestResult`）；
   - 主界面右下角新增 HTTP 代理状态灯（`DotLocalProxyStatus` 圆点 + `TxtLocalProxyDotStatus` 文字），点击跳转设置页并展开本地代理卡片；
2. **`MainWindow.xaml.cs`**：
   - `UpdateLocalProxySegmentUI()` 同步刷新状态灯（运行中 #4ADE80 / 未开启 #94A3B8）；
   - 新增 `BtnLocalProxyFetchModels_Click`：经本地代理 `GET /v1/models` 拉取模型列表填充下拉框（带 Bearer 鉴权）；
   - 新增 `BtnLocalProxyTestModel_Click`：向选中模型 `POST /v1/chat/completions` 发送测试文本
     `Please introduce yourself in one sentence. / 请用一句话介绍你自己。`，回复展示到结果框；
   - 新增 `DotLocalProxyStatus_Click`（状态灯点击跳设置）与 `TruncateText` 工具方法。

**验证**：编译 0 错误 0 警告；部署后 HTTP 8317 六项回归全部通过（含 `GET /v1/models` 返回 30 个模型、`POST /v1/chat/completions` 测试模型返回正常自我介绍回复）；8318 无监听；测试进程已清理。

### 2026-08-07 — v1.0.17（变更类型：废弃/回滚，移除 HTTPS 监听全部实现）

**背景**：OpenCode Desktop 等客户端的 TLS 栈不读取 Windows 证书库
（报 `unable to verify the first certificate`），自签名证书方案无法被此类客户端
信任，该功能失去价值，用户决定放弃。

**变更内容**：
1. **`LocalProxyServer.cs`**：移除 `TcpListener`/`SslStream` 自托管 HTTPS 监听
   （`StartHttpsListener`/`HttpsAcceptLoopAsync`/`HandleHttpsClientAsync`/
   `ProcessHttpsRequestAsync`）、证书管理全套（`EnsureHttpsCertificate`/
   `TryLoadPfx`/`ReplaceCertificateInMy`/`EnsureRootTrusted`/`ExportCertPem`）
   及 HTTPS 专属流类（`TlsResponseStream`/`LimitedReadStream`/`ChunkedReadStream`/
   `StreamReadExtensions`）；`HttpsEnabled`/`HttpsPort` 属性删除；
   **保留** HTTP/HTTPS 共用的请求抽象层（`ProxyRequest`/`ProxyResponse`/
   `HttpListenerSyncStream`）与五个业务 handler（纯 HTTP 路径无回归）；
2. **`MainWindow.xaml(.cs)`**：移除 HTTPS 配置区（开关分段按钮、端口输入、
   HTTPS Base URL 复制框）与全部事件处理器、状态徽章 https 部分；
   `settings.json` 不再读写 `localProxyHttpsEnabled`/`localProxyHttpsPort`
   （旧配置残留字段被忽略，向后兼容）；
3. **本机清理**：删除 `%LocalAppData%\Haodo\haodo-proxy.pfx` 与
   `haodo-proxy-ca.pem`，并从 `CurrentUser\My`、`CurrentUser\Root`
   移除 `CN=Haodo Local Proxy` 证书（确认两库均为 0 残留）。

**验证**：编译 0 错误 0 警告；HTTP 8317 六项回归（chat 流式/非流式 +
responses 两轮工具调用）全部通过；HTTPS 8318 端口不再监听（curl 000 符合预期）。

### 2026-08-07 — v1.0.17（变更类型：新功能，本地代理 HTTPS 监听支持）

**背景**：OpenCode Desktop 等 AI 客户端仅接受 `https://` 形式的 Base URL，
而本地代理此前只有 HTTP 监听（8317）。

**变更内容**：
1. **`LocalProxyServer.cs` 增加 HTTPS 监听**：
   - 新增 `HttpsEnabled` / `HttpsPort`（默认 8318）属性，`Start()` 在 HTTP 前缀之外
     追加 `https://127.0.0.1:{HttpsPort}/` 前缀（基于 HttpListener 原生 https 前缀方案）；
   - `EnsureHttpsCertificate()`：三级证书获取——复用 `CurrentUser\My` 现有同名证书 →
     从 `%LocalAppData%\Haodo\haodo-proxy.pfx` 恢复 → 生成新自签证书（RSA 2048、
     SAN: localhost+127.0.0.1、10 年），并安装至 `CurrentUser\My` 与信任根 `CurrentUser\Root`，
     导出 pfx 持久化（重启不重新生成、指纹不漂移）；
   - `EnsureSslCertBinding()`：`netsh http show sslcert` 核对绑定与指纹；未绑定/指纹不符时
     `runas` 提权执行 `netsh http add sslcert ipport=127.0.0.1:{port} certhash=... appid={A5F3B2C4-9D1E-4B7A-8C6F-2E1D5A9B3C77}`
     （首次弹一次 UAC，绑定系统级持久生效）；绑定失败不阻塞 HTTP 启动；
   - 证书写入 `CurrentUser\Root`（HKCU 无需管理员），使用系统信任库的客户端
     （Chrome/Edge/curl schannel/Python ssl 默认上下文）可直接信任；
2. **`MainWindow.xaml(.cs)` 设置页新增 HTTPS 配置区**：
   - HTTPS 开关分段按钮（关闭 HTTPS / 开启 HTTPS）、HTTPS 端口输入框（默认 8318，
     LostFocus/Enter 生效并持久化）、HTTPS Base URL 只读框与「复制 URL」按钮；
   - 状态徽章在运行中同时显示 HTTP 与 HTTPS 地址；
3. **settings.json 新增字段**：`localProxyHttpsEnabled`（bool）、`localProxyHttpsPort`（int，
   默认 8318），与旧配置向后兼容（字段缺失时按默认值处理，不影响旧配置）。

**验证**：HTTP 端口 8317 六项回归（chat 流式/非流式 + responses 两轮工具调用，
含 `thought_signature` 透传）全部通过；HTTPS 端口 8318 端到端两轮工具调用验证通过
（证书信任链依赖 CurrentUser\Root，绑定依赖 netsh sslcert）。

### 2026-08-07 — v1.0.17（变更类型：重构/修复，HTTPS 零提权化 + 私钥持久化修复）

**背景**：`netsh http add sslcert` 仅接受 `LocalMachine\My` 存储且必须 UAC 提权，
无法实现「开开关即用」的免管理员 HTTPS 监听；且初次实现采用 HttpListener 原生
https 前缀方案，运行依赖 HTTP.sys + 系统级 sslcert 绑定。

**变更内容**：
1. **自托管 TLS 监听取代 HttpListener https 前缀**（`LocalProxyServer.cs`）：
   - 新增 `TcpListener` + `SslStream` 监听循环（`HttpsAcceptLoopAsync`），
     `SslServerAuthenticationOptions` 启用 TLS 1.2/1.3，握手后按 HTTP/1.1 语义
     解析请求头（`Content-Length` 边界），完全绕开 HTTP.sys 与 sslcert 绑定，
     全程纯用户态、零提权、零 UAC；
   - 新增请求/响应抽象层：`ProxyRequest` / `ProxyResponse` 通用接口 +
     `HttpListenerSyncStream`（HTTP）与 `TlsResponseStream`（HTTPS）适配器，
     五个业务 handler（chat 流式/非流式、responses、native gemini 转发）通过
     统一抽象复用，HTTP 与 HTTPS 行为完全一致；
   - `TlsResponseStream` 在首次写入前延迟提交 HTTP 响应头（含 `Access-Control-*`
     与业务头部），完成后触发 `SendFileAsync` 刷出响应体，连接默认 `Connection: close`；
2. **证书私钥持久化修复**：`CertificateRequest.CreateSelfSigned()` 生成的私钥是
   **临时密钥集**（EphemeralKeySet），直接 `store.Add(cert)` 只持久化公钥部分，
   导致 `CurrentUser\My` 中的证书 `HasPrivateKey=False`、`SslStream` 握手被对端中止。
   修复：生成后先导出 pfx（临时私钥此时可用），再从 pfx 以
   `Exportable | PersistKeySet` 重载获取带持久私钥的证书对象再安装；
   对历史遗留的无钥证书，优先从 pfx 恢复同指纹带钥版本（指纹不变，
   现有客户端无需重新信任）；
   `ReplaceCertificateInMy` 先按指纹无条件移除旧副本再添加（`X509Store.Contains`
   按 issuer+serial 判等、无法区分私钥有无，直接 Contains 判断会漏替换）。

**验证**：重启进程后证书直接复用、`HasPrivateKey=True`、指纹不变
（A1E4A97360BDBA966F9923E02AB9FA8C918043D8）；HTTPS 8318 六项回归
（chat 流式/非流式 + responses 两轮工具调用）全部通过；HTTP 8317 六项回归
继续通过；curl/python/浏览器（信任 CurrentUser\Root）均无需额外参数直连成功。

### 2026-08-07 — v1.0.17（变更类型：修复，NarraFork 第二轮工具调用 400 根因修复）

**背景**：NarraFork 会话「打招呼与问候」中，能发起首轮工具调用，但工具结果回传
（第二轮请求）时上游报 `400 Request contains an invalid argument`，错误消息指明
functionCall 缺失 `thought_signature`。

**根因**：
1. Google Antigravity 上游（gemini-3.6-flash-high 等）强制要求历史消息中的 functionCall
   附带 `thought_signature`（**part 级字段**，JSON 字段名为 camelCase `thoughtSignature`，
   由官方 SDK 类型定义确认）；且服务端校验签名真实性（伪造签名报 `Corrupted thought signature`）；
2. 代理响应侧把上游 functionCall 转为 OpenAI tool_calls / Responses function_call 时丢失签名
   （call_id 用随机值生成），客户端回传时无签名 → 400；
3. 请求侧缺陷：Chat 格式 tool 消息无 name 字段时整个结果被丢弃（历史里无 functionResponse）；
   Responses 格式 input 数组中的顶层 function_call / function_call_output item（无 content 键）
   被 ParseOpenAIMessageParts 静默忽略；顶层 function_call 无 role 时默认映射为 user 角色
   （Gemini 要求 functionCall 必须处于 model 角色）——历史工具交互逻辑全部丢失。

**变更内容**：
1. **thought_signature 全链路透传**：
   - 响应侧：上游 part 的 thoughtSignature（兼容读取 thought_signature 变体）经 `BuildCallId`
     编码为 `call_ts_` + base64url(签名) 放入 OpenAI `tool_calls[].id` / Responses
     `function_call.call_id`（chat/responses 非流式 + 两个流式路径全部覆盖）；
   - 请求侧：`TryExtractThoughtSignature` 从客户端回传的 call_id 还原签名，写回 Gemini
     functionCall part 的 thoughtSignature 兄弟字段；普通客户端自建 id（无 `call_ts_` 前缀）
     不解析、不加字段，兼容无需签名的上游；
2. **Chat 历史转换修复**：
   - assistant.tool_calls 转换时附带签名（JsonObject 条件字段，无签名时不发该键）；
   - tool 消息缺少 name 时：先用同请求内 tool_call_id → 函数名映射还原，再兜底 call_id，
     不再静默丢弃工具结果（此前 `!string.IsNullOrEmpty(toolName)` 检查会直接跳过）；
3. **Responses 历史转换修复**：
   - ParseOpenAIMessageParts 支持无 content 的顶层 function_call / function_call_output item；
   - 顶层 function_call item 自动映射为 model 角色（此前默认 user 会被上游拒绝）；
   - function_call_output 的 name 兜底链：item.name → call_id → "tool_result"；
   - content 数组内同类分支抽取复用 AddFunctionCallPart / AddFunctionCallOutputPart；
4. **环境恢复**：settings.json 的 isLocalProxyEnabled 恢复为 true（20:01 重启时被重置为 false，
   导致代理不启动）；files 恢复指向账号文件（data 目录下的 antigravity 凭证，凭证本身未变动）。

**涉及文件**：
- `src/GeminiProtocolTranslator.cs`：新增 GetThoughtSignature / BuildCallId /
  TryExtractThoughtSignature / AddFunctionCallPart / AddFunctionCallOutputPart；
  双向 functionCall 转换改造（Chat 与 Responses 请求侧 + 响应侧）
- `src/LocalProxyServer.cs`：chat/responses 流式 fnCalls 元组携带签名 + callId 生成改造
- `维护文档/MAINTENANCE.md`：本次记录 + 第 11 章已知边界更新

**验证方式**（真实账号端到端，代理 8317 端口，模型 gemini-3.6-flash-high）：
- chat 流式两轮：第一轮拿到 `call_ts_` 编码 id；第二轮回传后 200，模型引用工具结果；
- chat 非流式两轮：200，模型正常回答；
- responses 非流式两轮（input 含完整历史 user → function_call → function_call_output → user）：
  200，模型引用工具结果；
- responses 流式两轮：200，事件序列完整（created → output_item.added → arguments.delta →
  output_text.delta → completed）；
- 反证：伪造签名回传报 `Corrupted thought signature`（证明签名真实性由上游校验，
  透传必须是真签名）；
- 共 6/6 用例通过（临时测试脚本 `%TEMP%\bv_probe\test_roundtrip.py`，按需可迁入 scripts/）。

### 2026-08-07 — v1.0.17（变更类型：清理 / 文档）

**变更内容**：
- 经用户确认，删除根目录 Schema 探测残留文件 `test_schema_probe.json` / `test_schema_probe_resp.json`。
  两文件为上游 Schema 字段探测实验的原始请求/响应样本（探测结果已固化为
  `GeminiSchemaAllowedKeys` 白名单 + 回归脚本断言 + 本文档 5.5 节，删除零信息损失）。
- 同步删除文档第 3 章目录图与第 4.1 表格中对该两文件的描述。

**涉及文件**：
- 项目根目录：`test_schema_probe.json`、`test_schema_probe_resp.json`（删除）
- `维护文档/MAINTENANCE.md`：同步移除相关描述

**验证方式**：
- `ls` 确认根目录已无两个探测文件；
- 文档第 3/4 章不再引用已删除文件；5.5 节结论性描述不受影响。

### 2026-08-07 — v1.0.17（变更类型：文档迁移 / 整理）

**变更内容**：
- 将 MAINTENANCE.md 从项目根目录移入独立文件夹 `维护文档/`，与源码、脚本、发布产物分离；
- 按用户要求清除文档中所有本机相关表述（如"项目位于 OneDrive 同步目录"）；全文只保留相对路径
  （一律相对项目根）、系统环境变量（`%AppData%`、`%TEMP%`）与网络地址，文档可随项目整体迁移而不失效；
- 文件头新增"路径约定"说明，目录结构图根节点中性化为"项目根目录"。

**涉及文件**：
- `维护文档/MAINTENANCE.md`：整体迁移 + 路径表述规范化

**验证方式**：
- grep 全文确认不存在盘符绝对路径（`E:\` / `C:\` / `D:\`）与 OneDrive 字样；
- 迁移后重新校验章节结构完整（12 章）。

### 2026-08-07 — v1.0.17（变更类型：文档）

**变更内容**：
- 在根目录新建本维护文档 MAINTENANCE.md，完整覆盖：软件概述、目录结构、逐文件详解、
  核心机制（代理链路/模型映射/schema 白名单/流式转换/凭证管理/配额/OAuth/贴贴/更新）、
  构建发布流程、维护与排障手册、更新记录规范。
- 记录此前已完成的 NarraFork 适配成果（Responses 流式工具调用、propertyNames 400 修复等）。

**涉及文件**：
- `MAINTENANCE.md`：新建
- `docs/MAINTENANCE.md`：保留为旧版历史，不改动

**验证方式**：
- 文档内容与源码逐文件核对（行号/方法名/常量均来自实际代码）。

### 2026-08-07 — v1.0.17（变更类型：修复）

**变更内容**：
- 将 `GeminiSchemaAllowedKeys` 从"探测模式: 临时全保留"宽名单收敛为最终窄名单（18 个字段），
  修复白名单未真正收敛、NarraFork 场景（工具 schema 含 propertyNames）仍会触发上游 400 的隐患。
- 新增 schema 回归验证脚本，用反射调用真实编译产物中私有 SanitizeGeminiSchema 进行攻击面断言。

**涉及文件**：
- `src/GeminiProtocolTranslator.cs`：白名单收敛 + 注释更新
- `scripts/schema_regression_check.cs`：新建回归脚本

**验证方式**：
- `dotnet build src\CLIProxyAPI_GUI.csproj`：0 错误 0 警告
- 回归脚本输出：`PASS: 白名单收敛验证通过`（27 项断言：400 源字段清除、通用字段保留、
  属性名恰为 propertyNames 的键保留、空对象补 type）
- 与 CLIProxyAPI（Go）gemini_schema.go 的黑名单处理做交叉印证，字段交集一致。

### 2026-08-05~06 — v1.0.13~v1.0.16（变更类型：新功能 + 修复，此前各版本）

**变更内容**（由旧版 `docs/MAINTENANCE.md` 与代码注释归纳）：
- 新增本地 Gemini API 代理（LocalProxyServer + GeminiProtocolTranslator），支持
  /v1/chat/completions、/v1/responses、/v1/completions、/v1/models、/v1/embeddings(占位)、
  /v1beta/models/* 原生转发；流式 SSE 与工具调用（chat + Responses 双协议）。
- NarraFork Responses API 流式工具调用适配：function_call.added/arguments.delta/done/
  completed 事件序列、空响应移除 tools 重试、多账号轮询、Token 自动刷新。
- propertyNames 400 修复：schema 白名单递归清洗（探测确认 examples/const/$ref/propertyNames/
  对象形式 additionalProperties 等均不被上游接受）。
- 模型映射体系：ModelAliasMap/KnownModelKeys/404 候选重试/UA 伪装 antigravity/1.11.3。
- 配额查看器与贴贴小部件在此前版本完成（详见 docs/MAINTENANCE.md 旧文档）。

**涉及文件**：
- `src/LocalProxyServer.cs`、`src/GeminiProtocolTranslator.cs`、`src/MainWindow.xaml(.cs)`、
  `src/MiniWidgetWindow.xaml(.cs)`、`src/CLIProxyAPI_GUI.csproj`、`scripts/*.ps1`

**验证方式**：
- 各版本发布前经 `仅编译.bat` 验收；NarraFork 端到端回归通过；
- 上游探测记录：`test_schema_probe.json` / `test_schema_probe_resp.json`。

---

## 11. 已知边界与未来改进

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
   `NODE_TLS_REJECT_UNAUTHORIZED=0` 绕过（代码与证书残留均已清理，详见第 10 章）；
8. **更新服务器/发布服务器**为固定 IP（39.106.203.8）与域名（yusnip.top），迁移需同步改 `deploy.ps1`、`VersionJsonUrl`、`MainWindow.xaml.cs` 的更新逻辑。

### 未来改进方向

- schema 约束字段迁移到 description（对齐 CLIProxyAPI 的 moveConstraintsToDescription）；
- union 展平（flatten anyOf/oneOf）；
- 响应 usage token 统计（当前 Responses 流式 completed 中 token 传 0）；
- 代理支持多 Key 映射（当前仅单一 ApiKey）；
- 端到端自动化测试（当前 schema 回归是单元级，上游链路依赖真实账号）。

---

## 12. 安全红线

1. **绝不把 OAuth 凭证、access_token、refresh_token、用户邮箱写入文档或提交到版本库**（旧指南同样强调）；
2. **`scripts/deploy.ps1` 内嵌服务器 SSH 私钥**——该文件禁止外发、禁止复制到公开仓库；如泄露立即更换服务器密钥；
3. `%AppData%\Haodo` 下的凭证文件是敏感数据，排障时如需贴日志请先脱敏（邮箱打星，参考 `GetDisplayEmail`）；
4. 不要在文档中记录真实 API Key 以外的密钥（`GeminiClientID` 是公开 OAuth 客户端 ID，位置在 `MainWindow.xaml.cs` 常量，无需也不应改动）；
5. 修改鉴权逻辑时保持默认 Key 可被用户在设置页修改的行为，不要把 Key 硬编码写死；
6. 代理的 `ApiKey` 仅保护本机回环端口，**不要**把端口暴露到公网（`HttpListener` 前缀为 127.0.0.1/localhost，勿改为 `+` 或 `*`）。

---

*本文件由 AI 维护助手在 2026-08-07 基于源码逐文件核对生成；此后任何代码变更必须同步更新本文件（至少更新第 10 章记录）。*
