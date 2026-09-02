# AI 平台 OAuth 额度查询机制深度分析报告

> **报告版本**: v1.0  
> **分析日期**: 2026年8月  
> **核心仓库**: [CLIProxyAPI](https://github.com/router-for-me/CLIProxyAPI)  
> **报告范围**: CLIProxyAPI + Anthropic Claude / OpenAI Codex / Google Antigravity / xAI Grok / Kimi  
> **项目应用**: 本报告是 Haodo 配额查询实现的背景研究材料；当前软件源码位于 `src`，发布产物为根目录 `Haodo.exe`，运行配置默认保存在 `%AppData%\Haodo`。  

---

## 目录

1. [Executive Summary](#1-executive-summary)
2. [CLIProxyAPI 项目架构概述](#2-cliproxyapi-项目架构概述)
3. [Anthropic Claude — OAuth 额度查询](#3-anthropic-claude--oauth-额度查询)
4. [OpenAI Codex — OAuth 额度查询](#4-openai-codex--oauth-额度查询)
5. [Google Antigravity — OAuth 额度查询](#5-google-antigravity--oauth-额度查询)
6. [xAI Grok — OAuth 额度查询](#6-xai-grok--oauth-额度查询)
7. [Kimi — OAuth / API Key 额度查询](#7-kimi--oauth--api-key-额度查询)
8. [跨平台对比总结](#8-跨平台对比总结)
9. [附录：CLIProxyAPI 核心源码解析](#9-附录cliproxyapi-核心源码解析)

---

## 1. Executive Summary

本报告深入分析了以 CLIProxyAPI 为代表的 AI CLI 代理工具如何通过 OAuth 认证获取各大平台的账号剩余额度。核心发现如下：

| 平台 | 额度查询 API | 限额类型 | 认证方式 |
|------|-------------|---------|---------|
| **Anthropic Claude** | `GET api.anthropic.com/api/oauth/usage` | 5小时滚动窗口 + 7天周限额 | OAuth Bearer Token + `claude-code` UA |
| **OpenAI Codex** | Codex Usage Dashboard (内部端点) | 5小时滚动窗口 + 周限额 + 额外额度 | OAuth Access Token |
| **Google Antigravity** | `POST cloudcode-pa.googleapis.com/v1internal:loadCodeAssist` | 5小时刷新配额 + 周限额 | Google OAuth 2.0 + Project ID |
| **xAI Grok** | 无公开 OAuth 额度端点 | 按订阅等级 RPS/TPM 限制 | OAuth Token (SuperGrok) |
| **Kimi** | 平台 API Key 机制 | API 计量额度 | OAuth 或 API Key |

**为什么额度分为「5小时限额」和「周限额」？**

这是一个**双层限额设计 (Dual-Layer Rate Limiting)**：
- **5小时滚动窗口 (Rolling Window)**：短期防滥用机制，限制在任意连续5小时内可消耗的 token/工作量。它是一个**滑动窗口**，不是固定时间段。每当窗口开始时间点之后的5小时到期后，额度自动恢复。
- **周限额 (Weekly Quota)**：长期资源分配机制，限制一周内（通常为7天自然周）的总消耗量。即使5小时窗口恢复，如果周限额已耗尽，仍然无法使用。

这种双层设计兼顾了：
1. **防止突发滥用**（5小时窗口阻止短时间内过度消耗）
2. **控制总资源成本**（周限额控制整体运营成本）
3. **公平分配**（确保所有用户都有机会使用服务）

---

## 2. CLIProxyAPI 项目架构概述

### 2.1 项目简介

CLIProxyAPI 是一个用 **Go 语言**编写的代理服务器，将 Antigravity (Google)、ChatGPT Codex (OpenAI)、Claude Code (Anthropic)、Grok Build (xAI) 等 CLI 工具的 OAuth 认证包装为兼容 OpenAI/Gemini/Claude/Codex 的 API 接口。

### 2.2 目录结构

```
CLIProxyAPI/
├── internal/
│   ├── auth/                    # 各平台 OAuth 认证实现
│   │   ├── antigravity/         # Google Antigravity OAuth
│   │   │   ├── auth.go          # OAuth 流程 + loadCodeAssist + onboardUser
│   │   │   └── constants.go     # Client ID, Secret, Endpoints
│   │   ├── claude/              # Anthropic Claude OAuth
│   │   │   ├── anthropic_auth.go # OAuth 流程 + 额度查询
│   │   │   ├── oauth_server.go   # 本地 OAuth 回调服务器
│   │   │   └── token.go          # Token 存储结构
│   │   ├── codex/               # OpenAI Codex OAuth
│   │   │   ├── openai_auth.go    # OAuth 流程 + Token 刷新
│   │   │   ├── oauth_server.go   # 本地 OAuth 回调服务器
│   │   │   ├── token.go          # Token 存储结构
│   │   │   └── openai.go         # 数据类型定义
│   │   ├── xai/                 # xAI Grok OAuth
│   │   └── kimi/                # Kimi OAuth
│   ├── client/                  # API 客户端封装
│   ├── credentialweight/        # 凭证权重管理（用于负载均衡）
│   └── access/                  # API Key 访问控制
├── sdk/
│   ├── cliproxy/                # 核心服务
│   │   ├── service_auth.go      # 认证管理 + 模型注册
│   │   ├── service_home.go      # 管理 API 端点
│   │   └── service_models.go    # 模型注册表
│   ├── auth/                    # SDK 认证接口
│   └── access/                  # SDK 访问控制
└── config.example.yaml          # 配置示例
```

### 2.3 支持的 OAuth Provider

```go
// sdk/cliproxy/service_auth.go
func newDefaultAuthManager() *sdkAuth.Manager {
    return sdkAuth.NewManager(
        sdkAuth.GetTokenStore(),
        sdkAuth.NewCodexAuthenticator(),   // OpenAI Codex
        sdkAuth.NewClaudeAuthenticator(),  // Anthropic Claude
        sdkAuth.NewXAIAuthenticator(),     // xAI Grok
    )
}
```

---

## 3. Anthropic Claude — OAuth 额度查询

### 3.1 OAuth 认证流程

Claude Code 的 OAuth 2.0 流程（源码：`internal/auth/claude/anthropic_auth.go`）：

```
用户浏览器 → Anthropic OAuth 授权页面
    ↓ 用户授权
回调 → http://localhost:{port}/auth/callback?code=xxx
    ↓
CLIProxyAPI → POST Anthropic Token Endpoint
    ← access_token + refresh_token + id_token
    ↓
存储到 auths/ 目录的 JSON 文件
```

**关键常量**（从源码推断）：
- **授权 URL**: Anthropic OAuth 授权端点
- **Token URL**: Anthropic Token 端点
- **PKCE**: 使用 S256 方法（与 OpenAI 类似）
- **回调端口**: 本地端口（配置化）

### 3.2 额度查询 API

**这是最关键的发现 — Anthropic 提供了一个公开的 OAuth 额度查询端点。**

#### 端点

```
GET https://api.anthropic.com/api/oauth/usage
```

#### 必需请求头

```http
Authorization: Bearer <oauth_access_token>
anthropic-beta: oauth-2025-04-20
User-Agent: claude-code/<version>
Content-Type: application/json
```

> **⚠️ 关键细节**: `User-Agent: claude-code/<version>` 头是**必需的**。不带此头会触发一个极其严格的限速桶，持续返回 429 错误。带上正确的 User-Agent 后，限速大幅放宽（安全间隔约 180 秒）。

#### 响应格式

```json
{
  "five_hour": {
    "utilization": 33.0,
    "resets_at": "2026-04-11T07:00:00.528743+00:00"
  },
  "seven_day": {
    "utilization": 13.0,
    "resets_at": "2026-04-17T00:59:59.951713+00:00"
  },
  "seven_day_opus": null,
  "seven_day_sonnet": {
    "utilization": 1.0,
    "resets_at": "2026-04-16T03:00:00.951719+00:00"
  },
  "extra_usage": {
    "is_enabled": false,
    "monthly_limit": null,
    "used_credits": null,
    "utilization": null
  }
}
```

#### 字段详解

| 字段 | 类型 | 说明 |
|------|------|------|
| `five_hour.utilization` | float | 5小时滚动窗口的使用百分比 (0-100)，0表示无活跃窗口 |
| `five_hour.resets_at` | string (ISO 8601) | 5小时窗口重置时间 (UTC) |
| `seven_day.utilization` | float | 7天周限额的总使用百分比 |
| `seven_day.resets_at` | string (ISO 8601) | 周限额重置时间 |
| `seven_day_opus` | object\|null | Opus 模型专属周限额（null 表示未使用过） |
| `seven_day_sonnet` | object\|null | Sonnet 模型专属周限额 |
| `extra_usage` | object | 额外使用量（Max 计划的付费超额） |

### 3.3 5小时限额 vs 周限额

- **5小时窗口 (`five_hour`)**: 这是一个**滑动窗口 (Sliding Window)**。Anthropic 在用户第一次使用时开启一个5小时的计时窗口。在此窗口内，用户可以消耗一定量的 token（具体额度取决于订阅等级：Pro/Max）。窗口到期后，如果周限额未耗尽，新的5小时窗口会自动开启。
- **7天周限额 (`seven_day`)**: 一周内的总消耗量上限。通常在自然周的固定时间重置。**即使5小时窗口恢复，周限额耗尽后仍然无法使用。**
- **模型专属限额**: Opus 和 Sonnet 可能有独立的周限额。

### 3.4 限额等级

| 计划 | 5小时窗口 | 周限额 | 额外使用 |
|------|----------|--------|---------|
| **Claude Pro** ($20/月) | 标准 | 标准 | 不支持 |
| **Claude Max** ($100/$200/月) | 更高 | 更高 | 支持（按量计费） |
| **Team** | 标准 | 按席分配 | 可配置 |

> **注意**: 2026年5月，Anthropic 宣布将 5 小时配额翻倍，并取消了高峰时段限制。

### 3.5 Token 生命周期

- Access Token 有效期约 **60 分钟**
- Claude Code 运行时自动使用 refresh token 刷新
- Token 存储位置：
  - **macOS**: Keychain (`security find-generic-password -s "Claude Code-credentials" -w`)
  - **Linux/Windows**: `~/.claude/.credentials.json`
- 刷新端点：Anthropic Token Endpoint (POST, grant_type=refresh_token)

### 3.6 CLIProxyAPI 中的实现

CLIProxyAPI 的管理面板通过 **API 调用代理** 获取 Claude 额度：

1. 从存储的 auth 文件读取 OAuth access_token
2. 调用 `GET api.anthropic.com/api/oauth/usage`（附带正确的 User-Agent）
3. 解析响应中的 `five_hour` 和 `seven_day` 字段
4. 在管理面板中展示 5小时和周限额的进度条
5. 当 utilization 接近 100% 时触发冷却/切换到下一个凭证

---

## 4. OpenAI Codex — OAuth 额度查询

### 4.1 OAuth 认证流程

OpenAI Codex 的 OAuth 2.0 + PKCE 流程（源码：`internal/auth/codex/openai_auth.go`）：

#### 关键常量

```go
const (
    AuthURL             = "https://auth.openai.com/oauth/authorize"
    TokenURL            = "https://auth.openai.com/oauth/token"
    ClientID            = "app_EMoamEEZ73f0CkXaXp7hrann"
    RedirectURI         = "http://localhost:1455/auth/callback"
    codexRefreshTimeout = 30 * time.Second
)
```

> **关键发现**: Client ID 为 `app_EMoamEEZ73f0CkXaXp7hrann`，这是 OpenAI 官方 Codex CLI 使用的 Client ID。CLIProxyAPI 直接复用了这个 Client ID。

#### 授权 URL 参数

```go
params := url.Values{
    "client_id":                  {ClientID},
    "response_type":              {"code"},
    "redirect_uri":               {RedirectURI},
    "scope":                      {"openid email profile offline_access"},
    "state":                      {state},
    "code_challenge":             {pkceCodes.CodeChallenge},
    "code_challenge_method":      {"S256"},
    "prompt":                     {"login"},
    "id_token_add_organizations": {"true"},
    "codex_cli_simplified_flow":  {"true"},
}
```

#### Token 交换

```go
// POST https://auth.openai.com/oauth/token
data := url.Values{
    "grant_type":    {"authorization_code"},
    "client_id":     {ClientID},
    "code":          {code},
    "redirect_uri":  {RedirectURI},
    "code_verifier": {pkceCodes.CodeVerifier},
}
```

响应包含：`access_token`, `refresh_token`, `id_token`, `expires_in`

#### Token 刷新

```go
// POST https://auth.openai.com/oauth/token
data := url.Values{
    "client_id":     {ClientID},
    "grant_type":    {"refresh_token"},
    "refresh_token": {refreshToken},
    "scope":         {"openid profile email"},
}
```

- 使用 `singleflight` 模式防止并发刷新
- 非可重试错误：`refresh_token_reused`（token 被复用，说明已被轮换）
- 指数退避重试机制

#### ID Token 解析

从 `id_token` (JWT) 中提取：
- **Account ID**: `claims.GetAccountID()`
- **Email**: `claims.GetUserEmail()` / `claims.Email`

### 4.2 额度查询机制

OpenAI Codex **没有提供公开的 REST API 端点**来查询 OAuth 订阅的额度。其额度查询通过以下方式实现：

#### 方式一：Codex Usage Dashboard（CLIProxyAPI 使用的方式）

CLIProxyAPI 通过代理 Codex CLI 的内部端点获取额度信息。具体端点未在开源代码中直接暴露（可能在 `sdk/cliproxy/auth` 或闭源模块中），但从项目文档和 issue 中可以确认：

> "Displays account availability, Plus-base capacity, 5-hour and weekly quota bars, plan weights, and restore forecasts through the Management API."

CLIProxyAPI 的管理面板展示了：
- 账户可用性 (availability)
- 5小时限额进度条
- 周限额进度条  
- 计划权重 (plan weights)
- 恢复预测 (restore forecasts)

#### 方式二：CLI /status 命令

在 Codex CLI 中运行 `/status` 命令可以查看当前会话状态，包括：
- 当前活跃的5小时窗口使用百分比
- 上下文使用量
- 可用的速率限制信息

#### 方式三：Codex Usage Dashboard（网页）

OpenAI 提供了一个网页版 Usage Dashboard，需要登录 ChatGPT 账户查看。

### 4.3 5小时限额 vs 周限额

#### 5小时滚动窗口

| 计划 | GPT-5.6 Terra | GPT-5.4 mini | 备注 |
|------|--------------|-------------|------|
| **Plus** ($20/月) | 20-110 条 | 60-350 条 | 含周限额 |
| **Pro 5x** ($200/月) | 100-550 条 | 300-1,750 条 | 更大范围 |
| **Pro 20x** ($200/月) | 400-2,200 条 | 1,200-7,000 条 | 仍受上下文/工具影响 |

> **注意**: 以上为本地消息数量范围，不是保证配额。实际消耗取决于模型、任务复杂度、上下文长度、推理、工具调用、缓存等因素。

#### 周限额

- 周限额在自然周固定时间重置
- **5小时窗口恢复 ≠ 周限额恢复**
- 两个看起来相似的 prompt 可能消耗不同量的额度
- Pro 5x 和 Pro 20x 的周限额也不同

#### Credits（额外额度）

- 购买的 Credits 可以在包含限额用完后继续使用
- **消耗顺序**: 先消耗包含的计划额度，再用 Credits
- Credits 按输入 token、缓存输入 token、输出 token 计量
- **ChatGPT Credits ≠ OpenAI Platform API 余额**
- API Key 走独立的计费体系，不影响 CLI 额度

### 4.4 六种限制类型总结

| 限制类型 | 表现 | 信息来源 | 解决方案 |
|----------|------|---------|----------|
| 5小时计划窗口 | Banner 百分比 / 重置时间 | Usage Dashboard | 等待重置 / 减少消耗 |
| 额外周限额 | 周上限 / 更晚的重置日期 | Dashboard | 等待该重置 |
| Credits 余额 | 余额显示 / 购买选项 | Dashboard | 使用 Credits |
| API 速率限制 | HTTP 429, RPM/TPM headers | Platform 限制页 | 降低吞吐量 |
| API 花费/配额 | 计费/预算/配额错误 | Platform 计费页 | 修复付款方/预算 |
| 本地上下文容量 | 上下文百分比 / 截断错误 | /status | 缩减文件/历史/工具 |

---

## 5. Google Antigravity — OAuth 额度查询

### 5.1 OAuth 认证流程

Google Antigravity 使用标准 Google OAuth 2.0 流程（源码：`internal/auth/antigravity/auth.go`）：

#### 关键常量

```go
const (
    ClientID     = "YOUR_GOOGLE_CLIENT_ID"
    ClientSecret = "YOUR_GOOGLE_CLIENT_SECRET"
    CallbackPort = 51121
)

var Scopes = []string{
    "https://www.googleapis.com/auth/cloud-platform",
    "https://www.googleapis.com/auth/userinfo.email",
    "https://www.googleapis.com/auth/userinfo.profile",
    "https://www.googleapis.com/auth/cclog",
    "https://www.googleapis.com/auth/experimentsandconfigs",
}

const (
    TokenEndpoint    = "https://oauth2.googleapis.com/token"
    AuthEndpoint     = "https://accounts.google.com/o/oauth2/v2/auth"
    UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo?alt=json"
)

const (
    APIEndpoint      = "https://cloudcode-pa.googleapis.com"
    DailyAPIEndpoint = "https://daily-cloudcode-pa.googleapis.com"
    APIVersion       = "v1internal"
)
```

#### 授权 URL

```go
params := url.Values{}
params.Set("access_type", "offline")      // 获取 refresh_token
params.Set("client_id", ClientID)
params.Set("prompt", "consent")
params.Set("redirect_uri", redirectURI)
params.Set("response_type", "code")
params.Set("scope", strings.Join(Scopes, " "))
params.Set("state", state)
```

#### Token 交换

```go
// POST https://oauth2.googleapis.com/token
data.Set("code", code)
data.Set("client_id", ClientID)
data.Set("client_secret", ClientSecret)
data.Set("redirect_uri", redirectURI)
data.Set("grant_type", "authorization_code")
```

### 5.2 额度查询机制

Google Antigravity **不提供类似 Anthropic 的单一「usage 端点」**。其额度管理更加分散和隐式：

#### loadCodeAssist — 初始化与配额发现

```
POST https://cloudcode-pa.googleapis.com/v1internal:loadCodeAssist
Authorization: Bearer <google_access_token>
Content-Type: application/json
User-Agent: <antigravity_user_agent>

{
  "metadata": {
    "ideType": "ANTIGRAVITY"
  }
}
```

响应中包含：
- `cloudaicompanionProject` / `projectId` — 项目 ID
- `allowedTiers` — 允许的套餐等级列表
- `currentTier` — 当前套餐

#### onboardUser — 新用户注册

```
POST https://daily-cloudcode-pa.googleapis.com/v1internal:onboardUser
Authorization: Bearer <google_access_token>
X-Goog-Api-Client: <specific_ua>
Content-Type: application/json

{
  "tier_id": "<tier_id>",
  "metadata": {
    "ide_type": "ANTIGRAVITY",
    "ide_version": "<version>",
    "ide_name": "antigravity"
  }
}
```

这是一个**长轮询操作**（最多5次尝试，每次30秒超时），返回 `{"done": true, "response": {"projectId": "..."}}`。

#### 额度等级

| 计划 | 5小时配额 | 周限额 | 模型可用性 |
|------|----------|--------|-----------|
| **Google AI Ultra** | 最高、最大方 | 最高周限额 | 所有 Gemini 模型 + 第三方模型 |
| **Google AI Pro** | 高、大方 | 较高周限额 | 大部分 Gemini 模型 |
| **免费用户** | 基础配额（每周刷新） | 每周限额 | 有限模型 |

> **重要**: Antigravity 的速率限制与 Agent 实际完成的工作量相关，而不是简单的请求次数。简单任务可以获得更多 prompt 响应，复杂任务则消耗更多配额。

#### 超额使用 (Overages)

- Pro 和 Ultra 用户可以购买 **AI Credits** 用于超出基础配额的使用
- AI Credits 是一次性或促销性质的

### 5.3 特殊的 User-Agent 要求

Antigravity 对 User-Agent 有严格要求，使用两种不同的 UA：

- **短 UA** (`shortUserAgent`): 用于常规 API 调用
- **节点 UA** (`nodeUserAgent`): 用于 onboardUser 等控制面操作

---

## 6. xAI Grok — OAuth 额度查询

### 6.1 OAuth 认证流程

xAI Grok 的 OAuth 流程（源码：`internal/auth/xai/` 目录）：

- 使用 SuperGrok / X (Twitter) 账户进行 OAuth
- 获得 OAuth Token 后可访问订阅支持的模型（包括 grok-build-0.1 和高级 Grok 4 变体）
- 支持订阅额度、Credits 和速率限制的消耗

### 6.2 额度/速率限制机制

xAI **没有提供公开的 OAuth 额度查询端点**。其限制通过以下方式实施：

#### API 速率限制

| 维度 | 说明 |
|------|------|
| **RPS** (Requests Per Second) | 每秒请求数限制 |
| **TPM** (Tokens Per Minute) | 每分钟 token 数限制 |

#### 订阅等级速率限制

| Tier | 月费 | 速率限制特性 |
|------|------|------------|
| **Tier 1** | $50 | 基础 RPS/TPM |
| **Tier 2** | $250 | 提高 RPS/TPM |
| **Enterprise** | 定制 | 自定义限制 |

#### SuperGrok 订阅

- 通过 X (Twitter) Premium+ 订阅获得
- OAuth Token 访问订阅支持的模型
- 限制较为宽松但不确定
- 限制会随时间变化，且对不同用户可能不同

### 6.3 限制特点

- **无公开 usage API**: 无法通过 API 查询剩余额度
- **限制不透明**: 限制会随机变化，用户反馈「惩罚付费用户」
- **社区建议**: xAI 应该将功能拆分为独立的订阅

---

## 7. Kimi — OAuth / API Key 额度查询

### 7.1 认证方式

Kimi（月之暗面）支持两种认证方式：

1. **OAuth 方式**: 通过 Kimi Code 订阅的 OAuth 认证
2. **API Key 方式**: 通过 Kimi Open Platform 获取的 API Key

### 7.2 额度机制

Kimi K3 是月之暗面最强大的模型，拥有 2.8 万亿参数，原生视觉能力，和 100 万 token 上下文窗口。

- **OAuth 方式**: 通过订阅计划获得使用额度
- **API Key 方式**: 通过平台计费，按 token 用量计费
- CLIProxyAPI 支持通过 OAuth 或兼容 API 接口连接 Kimi

---

## 8. 跨平台对比总结

### 8.1 额度查询 API 对比

| 特性 | Anthropic Claude | OpenAI Codex | Google Antigravity | xAI Grok | Kimi |
|------|:---:|:---:|:---:|:---:|:---:|
| **公开 Usage API** | ✅ | ❌ (内部) | ❌ (分散) | ❌ | ❌ |
| **API 端点** | `/api/oauth/usage` | Dashboard 内部 | `loadCodeAssist` | — | — |
| **5小时窗口** | ✅ `five_hour` | ✅ 滚动窗口 | ✅ 5小时刷新 | ❌ | ❌ |
| **周限额** | ✅ `seven_day` | ✅ 周上限 | ✅ 周限额 | ❌ (RPS/TPM) | ❌ |
| **模型专属限额** | ✅ Opus/Sonnet | ❌ | ❌ | ❌ | ❌ |
| **额外使用量** | ✅ `extra_usage` | ✅ Credits | ✅ AI Credits | ❌ | ❌ |
| **重置时间** | ✅ ISO 8601 | ✅ Dashboard | ❌ (隐式) | ❌ | ❌ |
| **UA 要求** | ✅ `claude-code/*` | ❌ | ✅ 特定 UA | ❌ | ❌ |

### 8.2 OAuth 认证对比

| 特性 | Anthropic Claude | OpenAI Codex | Google Antigravity | xAI Grok |
|------|:---:|:---:|:---:|:---:|
| **OAuth 版本** | 2.0 + PKCE | 2.0 + PKCE | 2.0 | 2.0 |
| **PKCE 方法** | S256 | S256 | N/A (client_secret) | — |
| **Client Secret** | 可能 | 不需要 | ✅ 需要 | — |
| **授权端点** | Anthropic | `auth.openai.com` | `accounts.google.com` | X/Twitter |
| **Token 端点** | Anthropic | `auth.openai.com` | `oauth2.googleapis.com` | xAI |
| **Access Token 寿命** | ~60 分钟 | 由 `expires_in` 决定 | Google 标准 (~1h) | — |
| **Offline Access** | ✅ | ✅ | ✅ (`access_type=offline`) | — |
| **ID Token 用途** | — | 提取 Account ID + Email | — | — |

### 8.3 限额设计哲学

| 平台 | 设计哲学 |
|------|---------|
| **Anthropic** | 透明的双层限额（5小时+7天），提供精确的 usage API，重视开发者体验 |
| **OpenAI** | 不透明的双层限额，官方 Client ID 泄露，无公开 usage API，通过 Dashboard 展示 |
| **Google** | 隐式限额，与 Agent 工作量关联而非请求次数，通过项目 ID 和 tier 管理 |
| **xAI** | 传统 API 速率限制 (RPS/TPM)，SuperGrok 订阅限制不透明且易变 |

### 8.4 CLIProxyAPI 的额度管理策略

CLIProxyAPI 作为多凭证代理，实现了以下策略：

1. **凭证权重 (Credential Weight)**: 每个凭证可配置权重，用于负载均衡
2. **冷却存储 (Cooldown State Store)**: 被限速的凭证进入冷却状态
   - 支持文件持久化 (`SaveCooldownStatus`)  
   - `TransientErrorCooldownSeconds` 配置瞬态错误冷却时间
3. **模型状态注册 (Model State Registration)**: 每个凭证的每个模型有独立状态
4. **调度器 (Scheduler)**: 基于凭证状态和权重选择最佳凭证
5. **重试配置 (Retry Config)**: 
   - `RequestRetry`: 请求重试次数
   - `MaxRetryInterval`: 最大重试间隔
   - `MaxRetryCredentials`: 最大重试凭证数
6. **会话粘性路由**: 支持 WebSocket 的会话粘性

---

## 9. 附录：CLIProxyAPI 核心源码解析

### 9.1 OpenAI Codex OAuth 认证核心代码

**文件**: `internal/auth/codex/openai_auth.go`

```go
// OAuth 配置常量
const (
    AuthURL             = "https://auth.openai.com/oauth/authorize"
    TokenURL            = "https://auth.openai.com/oauth/token"
    ClientID            = "app_EMoamEEZ73f0CkXaXp7hrann"
    RedirectURI         = "http://localhost:1455/auth/callback"
    codexRefreshTimeout = 30 * time.Second
)

// 生成授权 URL（含 PKCE）
func (o *CodexAuth) GenerateAuthURL(state string, pkceCodes *PKCECodes) (string, error) {
    params := url.Values{
        "client_id":                  {ClientID},
        "response_type":              {"code"},
        "redirect_uri":               {RedirectURI},
        "scope":                      {"openid email profile offline_access"},
        "state":                      {state},
        "code_challenge":             {pkceCodes.CodeChallenge},
        "code_challenge_method":      {"S256"},
        "prompt":                     {"login"},
        "id_token_add_organizations": {"true"},
        "codex_cli_simplified_flow":  {"true"},
    }
    return fmt.Sprintf("%s?%s", AuthURL, params.Encode()), nil
}

// Token 刷新（含 singleflight 防并发）
func (o *CodexAuth) RefreshTokens(ctx context.Context, refreshToken string) (*CodexTokenData, error) {
    result, err, _ := codexRefreshGroup.Do(refreshToken, func() (interface{}, error) {
        refreshCtx, cancelRefresh := context.WithTimeout(
            context.WithoutCancel(ctx), codexRefreshTimeout,
        )
        defer cancelRefresh()
        return o.refreshTokensSingleFlight(refreshCtx, refreshToken)
    })
    // ...
}
```

### 9.2 Google Antigravity OAuth 认证核心代码

**文件**: `internal/auth/antigravity/auth.go`

```go
// OAuth 端点配置
const (
    ClientID     = "YOUR_GOOGLE_CLIENT_ID"
    ClientSecret = "YOUR_GOOGLE_CLIENT_SECRET"
    TokenEndpoint    = "https://oauth2.googleapis.com/token"
    AuthEndpoint     = "https://accounts.google.com/o/oauth2/v2/auth"
    APIEndpoint      = "https://cloudcode-pa.googleapis.com"
    APIVersion       = "v1internal"
)

// 通过 loadCodeAssist 获取项目 ID
func (o *AntigravityAuth) FetchProjectID(ctx context.Context, accessToken string) (string, error) {
    endpointURL := fmt.Sprintf("%s/%s:loadCodeAssist", APIEndpoint, APIVersion)
    // POST 请求获取 projectId 和 allowedTiers
}

// 新用户注册（长轮询）
func (o *AntigravityAuth) OnboardUser(ctx context.Context, accessToken, tierID string) (string, error) {
    endpointURL := fmt.Sprintf("%s/%s:onboardUser", DailyAPIEndpoint, APIVersion)
    // 最多5次轮询，每次30秒超时
}
```

### 9.3 Anthropic Claude OAuth 额度查询（API 调用方式）

**端点**: `GET https://api.anthropic.com/api/oauth/usage`

```bash
curl -s https://api.anthropic.com/api/oauth/usage \\
  -H "Authorization: Bearer $OAUTH_TOKEN" \\
  -H "anthropic-beta: oauth-2025-04-20" \\
  -H "User-Agent: claude-code/1.0.0" \\
  -H "Content-Type: application/json"
```

**响应示例**:

```json
{
  "five_hour": {
    "utilization": 33.0,
    "resets_at": "2026-04-11T07:00:00.528743+00:00"
  },
  "seven_day": {
    "utilization": 13.0,
    "resets_at": "2026-04-17T00:59:59.951713+00:00"
  },
  "seven_day_opus": null,
  "seven_day_sonnet": {
    "utilization": 1.0,
    "resets_at": "2026-04-16T03:00:00.951719+00:00"
  },
  "extra_usage": {
    "is_enabled": false,
    "monthly_limit": null,
    "used_credits": null,
    "utilization": null
  }
}
```

---

## 结论

1. **Anthropic 是唯一提供公开、结构化 OAuth 额度查询 API 的平台**。`/api/oauth/usage` 端点返回精确的使用百分比和重置时间，是 CLIProxyAPI 管理面板展示 Claude 额度的数据来源。

2. **OpenAI 和 Google 的额度信息分散在多个内部端点中**，不对外公开。CLIProxyAPI 通过复用官方 CLI 的 Client ID 和端点来获取这些信息。

3. **5小时 + 周限额的双层设计**是 Anthropic 和 OpenAI 共同采用的策略，目的在于平衡用户体验（5小时窗口保证短期可用性）和成本控制（周限额控制长期资源消耗）。

4. **Google Antigravity 的额度与 Agent 工作量关联**，而非简单的请求计数，这是一种更精细但也更不透明的限额方式。

5. **xAI 的限制最为传统**，采用 RPS/TPM 的 API 速率限制，对 SuperGrok 订阅用户的不透明限制引起社区不满。

6. **CLIProxyAPI 的核心价值**在于：统一多平台 OAuth 认证 → 代理为标准 API 接口 → 智能凭证轮换（基于额度/权重/冷却状态）→ 提供管理面板可视化。

---

> **参考来源**:
> - [CLIProxyAPI GitHub 仓库](https://github.com/router-for-me/CLIProxyAPI)
> - [Anthropic OAuth Usage API (Issue #202)](https://github.com/Maciek-roboblog/Claude-Code-Usage-Monitor/issues/202)
> - [OpenAI Codex Usage Limits (LaoZhang Blog)](https://blog.laozhang.ai/en/posts/openai-codex-usage-limits)
> - [Google Antigravity Plans 文档](https://antigravity.google/docs/plans)
> - [xAI Rate Limits 文档](https://docs.x.ai/developers/rate-limits)
> - [CLIProxyAPI Issue #2529 (Quota Threshold)](https://github.com/router-for-me/CLIProxyAPI/issues/2529)
> - [CLIProxyAPI Issue #2599 (OAuth Metering)](https://github.com/router-for-me/CLIProxyAPI/issues/2599)
