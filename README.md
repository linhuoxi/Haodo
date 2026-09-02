# Haodo

<p align="center">
  <strong>优雅、轻量的多账号 AI 配额监控与本地 API 代理工具</strong>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10.0_WPF-512BD4?logo=dotnet" alt=".NET 10.0" />
  <img src="https://img.shields.io/badge/Platform-Windows_x64-0078D6?logo=windows" alt="Platform" />
  <img src="https://img.shields.io/badge/License-MIT-green.svg" alt="License" />
</p>

---

## 📖 简介

**Haodo** 是一款专为 AI 开发者和重度用户打造的 Windows 桌面工具。它集成了 **多平台 AI 账号额度实时监控** 与 **本地 OpenAI 协议兼容代理网关**，并提供高颜值的桌面贴贴小部件（Mini Widget）。

全部数据仅保存在本地设备，凭证安全加密，纯本地运行，隐私无忧。

---

## ✨ 核心特性

- 📊 **多账号配额实时监控**
  - 支持 Google Antigravity / Gemini 等多账号管理；
  - 精确展示 5 小时滚动窗口与 7 天周限额百分比、重置倒计时；
  - 智能分级警示色阶（充足 / 紧张 / 用尽）；
  - 支持窗口精简模式（Compact Mode），缩窄窗口自动进入高辨识度低密度卡片。

- ⚡ **本地 OpenAI 兼容 API 代理**
  - 本地回环监听（`http://127.0.0.1:8317/v1`），支持自定义 API Key；
  - 完美将 OpenAI `/v1/chat/completions`、`/v1/responses` 协议双向转换为 Gemini 上游格式；
  - 全链路支持**流式输出 (SSE)** 与 **工具调用 (Function Call / Tool Calls)**（含 `thought_signature` 透传）；
  - 严谨的 JSON Schema 白名单清洗，彻底避免上游 400 参数校验异常；
  - 内置多模型别名智能路由映射。

- 🎈 **桌面贴贴小部件 (Mini Widget)**
  - 支持「任务栏嵌入」与「桌面悬浮贴边」双模式；
  - 实时紧凑显示当前账号配额剩余，支持快捷切换账号与点击刷新；
  - 支持自定义背景色、不透明度及贴边停靠。

- 🎨 **现代化精致 UI**
  - 基于 WPF 自绘无边框流畅交互，原生适配 Windows 11 云母与圆角美学；
  - 完美支持深色 / 浅色模式，跟随系统主题自适应切换。

---

## 🛠️ 构建与运行

### 环境要求

- **操作系统**: Windows 10 (1809+) / Windows 11 (x64)
- **开发环境**: [.NET 10.0 Desktop SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- **可选依赖**: Python 3.x（仅用于使用混淆工具时的 Win32 资源修复）

### 快速编译

1. **方式一：使用一键编译脚本**
   双击运行项目根目录下的：
   ```cmd
   仅编译.bat
   ```
   脚本将自动调用 .NET 发布引擎生成单文件，并输出至根目录。

2. **方式二：使用 .NET CLI 命令行**
   ```bash
   dotnet build src/CLIProxyAPI_GUI.csproj -c Release
   ```
   或直接生成单文件：
   ```bash
   dotnet publish src/CLIProxyAPI_GUI.csproj -c Release -r win-x64 --no-self-contained -p:PublishSingleFile=true
   ```

---

## 📁 目录结构

```text
Haodo/
├── src/                      # C# WPF 完整源码与工程文件
│   ├── CLIProxyAPI_GUI.csproj# 项目工程文件 (.NET 10 Windows)
│   ├── MainWindow.xaml(.cs)  # 主窗口 UI 与业务调度
│   ├── MiniWidgetWindow.xaml # 悬浮贴贴小部件 UI
│   ├── LocalProxyServer.cs   # 本地 API 代理 HTTP 服务
│   └── GeminiProtocolTranslator.cs # OpenAI <-> Gemini 协议双向转换器
├── scripts/                  # 本地构建与辅助脚本
│   ├── build_only.ps1        # 本地单文件编译与混淆打包
│   ├── fix_obfuscated_rsrc.py# 资源段修复工具
│   └── schema_regression_check.cs # Schema 白名单回归测试
├── tools/                    # 构建辅助工具
├── 维护文档/                 # 架构详解与维护文档
├── 仅编译.bat                # 一键编译批处理入口
├── 版本号.txt                # 版本号定义文件
├── .gitignore                # Git 忽略规则 (已严格排除敏感配置与私有脚本)
└── README.md
```

---

## 🔒 安全与隐私声明

- 本项目所有网络请求均直接与官方 AI 平台 API 通信，本地代理仅监听 `127.0.0.1` 本机回环地址；
- 账号凭证及配置文件均以加密/本地形式保存在用户本机 `%AppData%\Haodo`（或便携模式 `./data`）下，绝不上报任何私有数据。

---

## 📄 开源许可证

本项目采用 [MIT License](LICENSE) 开源协议。
