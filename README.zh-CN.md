<div align="center">

<img src="Assets/agent-island-logo.png" alt="Agent Island logo" width="110">

# Agent Island

**AI 编程 Agent 的状态伴侣——Claude Code、Codex、Antigravity、Grok、Cursor。**

随时看清每个任务正在做什么。你可以离开终端，轮到你时 Agent Island 会叫你回来。本地优先、无需 Agent Island 账号、无产品遥测。

**[agent-island.dev](https://agent-island.dev/zh/)** · [English](README.md)

[![最新版本](https://img.shields.io/github/v/release/tristan666666/agent-island?style=flat-square&color=0969da)](https://github.com/tristan666666/agent-island/releases/latest)
[![下载量](https://img.shields.io/github/downloads/tristan666666/agent-island/total?style=flat-square&color=10b981)](https://github.com/tristan666666/agent-island/releases)
[![支持平台](https://img.shields.io/badge/%E5%B9%B3%E5%8F%B0-macOS%2013%2B%20%7C%20Windows%2010%2F11-24292f?style=flat-square)](#macos-和-windows)
[![构建状态](https://img.shields.io/github/actions/workflow/status/tristan666666/agent-island/macos-ci.yml?branch=main&style=flat-square&label=build)](https://github.com/tristan666666/agent-island/actions)
[![许可证](https://img.shields.io/github/license/tristan666666/agent-island?style=flat-square&color=8b5cf6)](LICENSE)

[![收录于 awesome-mac](https://img.shields.io/badge/listed%20in-awesome--mac-0969da?style=flat-square)](https://github.com/jaywcjlove/awesome-mac/blob/master/README.md#menu-bar-tools)
[![收录于 awesome-swift-macos-apps](https://img.shields.io/badge/listed%20in-awesome--swift--macOS-f97316?style=flat-square)](https://github.com/jaywcjlove/awesome-swift-macos-apps/blob/main/README.md#ai)
[![收录于 awesome-codex-cli](https://img.shields.io/badge/listed%20in-awesome--codex--cli-10b981?style=flat-square)](https://github.com/milisp/awesome-codex-cli)
[![收录于 awesome-coding-agents](https://img.shields.io/badge/listed%20in-awesome--coding--agents-7c3aed?style=flat-square)](https://github.com/kailiu42/awesome-coding-agents)
[![收录于 awesome-claude-code-and-skills](https://img.shields.io/badge/listed%20in-awesome--claude--code--and--skills-8b5cf6?style=flat-square)](https://github.com/GetBindu/awesome-claude-code-and-skills)
[![收录于 awesome-vibe-coding-resources](https://img.shields.io/badge/listed%20in-awesome--vibe--coding--resources-ec4899?style=flat-square)](https://github.com/acvnace/awesome-vibe-coding-resources#desktop-apps)
[![收录于 awesome-vibecoding](https://img.shields.io/badge/listed%20in-awesome--vibecoding-0ea5e9?style=flat-square)](https://github.com/roboco-io/awesome-vibecoding#projects-platforms--tools)
[![收录于中国独立开发者项目列表](https://img.shields.io/badge/listed%20in-%E4%B8%AD%E5%9B%BD%E7%8B%AC%E7%AB%8B%E5%BC%80%E5%8F%91%E8%80%85%E9%A1%B9%E7%9B%AE%E5%88%97%E8%A1%A8-c2410c?style=flat-square)](https://github.com/1c7/chinese-independent-developer/pull/1085/files)

<a href="https://www.producthunt.com/products/agent-island-2?embed=true&utm_source=badge-featured&utm_medium=badge&utm_campaign=badge-agent-island-2">
  <img src="https://api.producthunt.com/widgets/embed-image/v1/featured.svg?post_id=1175477&theme=light" alt="Agent Island - 实时状态与额度伴侣 | Product Hunt" width="250" height="54">
</a>

<!-- 1.7.1 发布影片已下架：它早于五家服务的岛和当前品牌。
     新的 Demo 循环（running -> your turn -> open session）拍好后放这里。 -->

<p>
  <a href="#快速开始"><strong>快速开始</strong></a> ·
  <a href="https://github.com/tristan666666/agent-island/releases/latest">下载</a> ·
  <a href="https://agent-island.dev/zh/">官网</a> ·
  <a href="docs/how-agent-island-detects-session-state.md">工作原理</a> ·
  <a href="CONTRIBUTING.md">参与贡献</a>
</p>

</div>

## 快速开始

选择你的平台，直接安装当前版本：

> `v2.1.2` **两个平台从同一个 tag 发布**——macOS 与 Windows 是同样的五家服务、同样的实时会话状态、同样的报告卡。

| 平台 | 推荐下载 | 系统要求 |
|---|---|---|
| macOS | [AgentIsland-2.1.2.dmg](https://github.com/tristan666666/agent-island/releases/download/v2.1.2/AgentIsland-2.1.2.dmg) | macOS 13+，Apple silicon 或 Intel |
| Windows | [AgentIsland-2.1.2-win-x64.zip](https://github.com/tristan666666/agent-island/releases/download/v2.1.2/AgentIsland-2.1.2-win-x64.zip) | Windows 10/11 x64 |

macOS：把 Agent Island 拖入 Applications。应用目前是 ad-hoc 签名而非 notarize，首次启动需要在 Finder 中右键应用并选择 **打开**。

Windows：解压下载包，运行 `AgentIsland.exe`。

> **macOS 首次打开**（仅手动下载）：Gatekeeper 会提示「Apple 无法验证 AgentIsland」——应用没有购买 Apple 开发者证书，使用 ad-hoc 签名，更新完整性由 Sparkle 自己的 EdDSA 签名保障。右键点按应用选「打开」，或到 系统设置 → 隐私与安全性 → 点「仍要打开」。用 Homebrew 安装则完全没有这一步。

<details>
<summary>包管理器与源码构建</summary>

Homebrew、WinGet 和 Scoop 的版本可能晚于 GitHub 最新 Release，安装前请先核对其版本号。

```sh
brew install tristan666666/tap/agentisland
```

```powershell
winget install TristanTang.AgentIsland
```

```powershell
scoop bucket add agent-island https://github.com/tristan666666/scoop-bucket
scoop install agent-island/agentisland
```

从源码构建 macOS 应用：

```sh
git clone https://github.com/tristan666666/agent-island.git
cd agent-island
./scripts/verify.sh
open build/AgentIsland.app
```

Windows 构建与测试文档正在 [issue #10](https://github.com/tristan666666/agent-island/issues/10) 中完善。

</details>

## 目录

- [功能](#功能)
  - [状态监视](#状态监视)
  - [用量](#用量)
  - [周报与月报卡片](#周报与月报卡片)
  - [轮到你闹钟](#轮到你闹钟)
  - [个性化](#个性化)
  - [macOS 和 Windows](#macos-和-windows)
- [社区](#社区)
- [工作原理](#工作原理)
- [为什么选 Agent Island](#为什么选-agent-island)
- [隐私与安全](#隐私与安全)
- [常见问题](#常见问题)
- [参与贡献](#参与贡献)
- [路线图与版本](#路线图与版本)
- [致谢与许可证](#致谢与许可证)

## 功能

### 状态监视

五家 Agent 的实时会话活动，收在一条紧凑的顶部条里——不用把任何会话切到前台就能看清状态：

<img src="Assets/agent-island-bar-working.png" alt="macOS 顶部条中正在运行的 Claude 会话状态" width="760">

<img src="Assets/agent-island-bar-alert.png" alt="同一顶部条的红色警示状态" width="760">

| 表现 | 含义 |
|---|---|
| Logo 旋转 | 有会话正在工作 |
| Logo 静止 | 当前没有会话在工作 |
| Logo 红色脉冲 | 会话遇到服务、登录、网络或限流错误，需要处理 |

### 用量

五家里任选两家上岛。实时会话状态五家全覆盖；被选中的每一家保留完整用量行，悬停看明细。本地没登录的服务不会占槽位，也不出现在列表里

成本、日历和报告由本机会话记录计算。各服务的额度与重置数据通过本地凭据存储访问服务方自有端点。

Codex 多账号的机器可以把每个登录存成命名档案，在服务菜单里随时切换——还可选开启自动切换：当前账号额度读到用尽时，依据真实用量轮转到下一个档案。

<img src="Assets/agent-island-usage-zh.png" alt="macOS 上的 Claude 和 Codex 用量页面" width="760">

只有一个订阅的机器会自动进入单订阅布局——面板一半是所属 logo 与名称，另一半是实时窗口：

<img src="Assets/agent-island-usage-solo-zh.png" alt="单订阅布局：一侧是 provider 徽标，另一侧是实时用量" width="760">

### 周报与月报卡片

在本机渲染的可分享卡片：本期 Token 总量与 ≈ API 费用换算（本地价目表估算，不是账单）、阵营对决（火花停在真实占比分割点）、7 天柱状图，以及收在前三名的模型环形图。两种卡片都能往回翻看过去的周与月，也支持从日历任选起始日期。复制或保存都由用户明确触发，Agent Island 不会代替你发布。

<table>
  <tr>
    <td align="center"><img src="Assets/report-weekly-en.webp" alt="英文周报卡（演示数据）——本周 Claude 夺冠" width="420"><br><sub>English · 本周 Claude 胜</sub></td>
    <td align="center"><img src="Assets/report-monthly-zh.webp" alt="中文月报卡（演示数据）——本月 Claude 夺冠" width="420"><br><sub>简体中文 · 本月 Claude 胜</sub></td>
  </tr>
</table>

### 轮到你闹钟

后台任务结束时，Agent Island 弹出闹钟窗口、发系统通知并播放提示音。多个完成的回合会排队而不是互相覆盖。如果你正盯着那个会话的终端，闹钟会先押着，等你切走且仍未回复时再弹。

<table>
  <tr>
    <td align="center"><img src="Assets/agent-island-turn-alarm-claude-zh.png" alt="会话完成后的轮到你提醒" width="420"></td>
    <td align="center"><img src="Assets/agent-island-turn-alarm-codex.png" alt="额度用完提醒，附重置时间" width="420"></td>
  </tr>
</table>

### 个性化

设置面板在 2.1.2 全部重写：黑白灰界面语言，颜色留给各家服务自己的标志。四种用量图表样式、已用/剩余切换，成本页可整个关掉。环境光可选**流光**（光晕与环绕流光，青、钴蓝、紫、银白）或全暗的**清爽**。

<img src="Assets/agent-island-settings-visual-zh.png" alt="设置：视觉模式与光效颜色选择" width="420">

### macOS 和 Windows

Agent Island 在两个平台上都是原生桌面应用，并提供英文和简体中文界面。

- **macOS 13+**：SwiftUI 通用应用，支持 Apple silicon 和 Intel，提供宽版与紧凑顶部条布局。
- **Windows 10/11 x64**：原生 WPF 应用，提供顶部条、可拖动悬浮组件和托盘入口。

从 2.1.2 起，两个平台从同一个 tag 发布，五家服务、实时会话状态、提醒、成本账本、报告卡完全一致。刻意保留的差异只有一个：Windows 多一个**悬浮条位置**设置，因为 Windows 显示器没有刘海可停靠。

<table>
  <tr>
    <td align="center"><img src="Assets/windows-island-usage.png" alt="Windows 上的灵动岛，显示 Claude 与 Codex 的实时额度窗口" width="420"><br><sub>Windows · 岛与实时用量</sub></td>
    <td align="center"><img src="Assets/windows-settings-providers.png" alt="Windows 设置页列出全部五家服务" width="420"><br><sub>Windows · 设置里的五家服务</sub></td>
  </tr>
</table>

<sub>Windows 截图取自 2.1.2 正式构建，使用演示数据</sub>

## 社区

优先扫群二维码直接进群；群码每 7 天轮换，过期时加作者微信（备注 Agent Island）拉你进群：

<table>
  <tr>
    <td align="center">
      <img src="Assets/wechat-group-qr.jpg" alt="Agent Island 微信交流群二维码" width="260"><br>
      <sub>微信交流群 — 扫码直接进</sub>
    </td>
    <td align="center">
      <img src="Assets/wechat-qr.jpg" alt="作者微信二维码；备注 Agent Island 拉群" width="260"><br>
      <sub>群码过期时加作者，备注 Agent Island</sub>
    </td>
  </tr>
</table>

也可以在 [Product Hunt](https://www.producthunt.com/products/agent-island-2) 查看 Agent Island。

## 工作原理

```mermaid
flowchart LR
    A[Claude 与 Codex 本地文件] --> B[本地解析器与状态机]
    B --> C[顶部条与提醒]
    B --> D[本地成本与报告页面]
    E[服务方自有用量端点] --> D
    F[兼容的 Antigravity / Grok / Cursor 登录] --> G[实时会话状态与完整用量行]
    E --> G
```

- **会话状态**只来自 Claude Code、Claude Desktop、Codex、Grok、Antigravity 和 Cursor 已经写入磁盘的记录与活动文件。本地文件事件和轮次标记驱动五家的工作中与轮到你状态，每一家都读自己的记录，不靠彼此推测。
- **用量与重置数据**来自各服务方自有的用量端点，并通过本地凭据存储访问——对每一家检测到兼容本地登录的服务都是如此
- **成本、模型和报告汇总**根据本机会话记录在本地计算。

实现说明（英文）：[How Agent Island detects Claude Code and Codex session state](docs/how-agent-island-detects-session-state.md)。

## 为什么选 Agent Island

运行耗时较长的 Claude Code 和 Codex 任务时，不应该一直把每个终端留在眼前。Agent Island 为这些会话提供持续可见的实时状态，在任务需要处理时提示你，并在下一步轮到你时把你叫回来——覆盖 Claude Code、Codex、Antigravity、Grok 与 Cursor
它适合这些开发者：

- 并行运行多个 Agent——Claude Code、Codex、Grok、Antigravity、Cursor——并把每一家的额度收进同一座小岛；
- 让长任务在后台运行；
- 希望查看状态、提醒、用量和可分享的战绩卡，又不想把会话数据交给另一个服务；
- 在乎桌面的样子——岛的光效、布局和卡片是按产品调的，不是调试悬浮窗。

与同类产品的对比：

| | Agent Island | [Vibe Island](https://vibeisland.app) | [CodexBar](https://github.com/steipete/CodexBar) | [ccusage](https://github.com/ccusage/ccusage) | [Claude Code Usage Monitor](https://github.com/Maciek-roboblog/Claude-Code-Usage-Monitor) | [CCSeva](https://github.com/Iamshankhadeep/ccseva) | [codex-island](https://github.com/ericjypark/codex-island) |
|---|---|---|---|---|---|---|---|
| 价格与源码 | 免费 · MIT | 一次性买断 · 闭源 | 免费 · MIT | 免费 · MIT | 免费 · MIT | 免费 · MIT | 免费 · MIT |
| 形态 | 菜单栏应用 | 刘海应用 | 菜单栏应用 | 命令行工具 | 终端仪表盘 | 菜单栏应用 | 菜单栏应用 |
| 平台 | macOS 13+ · Windows 10/11 | macOS 14+ | macOS 14+（CLI 版另支持 Linux） | 任何能跑 Node 的环境 | 任何能跑 Python 的环境 | macOS | macOS |
| 支持的 Agent | Claude Code · Codex · Grok · Antigravity · Cursor（全部实时会话） | Claude Code、Codex、Gemini CLI、Cursor 等十余家 | 59 家 provider（限额） | Claude Code（+ Codex） | Claude Code | Claude Code | Codex（+ Claude 用量） |
| 实时会话状态 | ✓ Claude · Codex · Grok · Antigravity · Cursor | ✓ | —（服务商故障徽章） | — | — | — | —（被动用量表） |
| 轮到你闹钟（弹窗+声音+队列） | ✓ | 完成提示，点击跳回 | — | — | — | — | — |
| 额度耗尽闹钟 | ✓ | — | — | — | 终端预警 | 70%/90% 阈值通知 | — |
| 刘海内权限审批 | — | ✓ | — | — | — | — | — |
| 用量、成本与重置 | 五家完整（用量窗口 · 成本 · 重置倒计时） | 用量窗口 | ✓（59 家限额+重置倒计时+开销） | ✓（本地成本报表） | ✓（实时+预测） | ✓（5 小时/周仪表+倒计时） | ✓（含 reset credits） |
| 周报/月报卡 | ✓ | — | — | — | — | — | — |

<sub>基于各产品 2026 年 7 月的公开资料整理，欢迎提 issue 指正。</sub>

## 隐私与安全

- 无需 Agent Island 账号。
- 会话数据不会上传到 Agent Island。
- 应用没有产品遥测。
- 用量与认证请求通过本地凭据存储直接发往服务方自有端点。
- 如果使用 Claude 重新认证，Agent Island 可能刷新并更新本地凭据存储中与 Claude Code 或 Claude Desktop 共用的凭据。
- macOS 更新在安装前由 Sparkle 使用 EdDSA 签名验证。

Agent Island 会读取实现这些页面所需的本地文件和凭据。与其他本地开发者工具一样，建议安装前检查源码与 Release 文件。

## 常见问题

<details>
<summary><strong>为什么 macOS 应用没有 notarize？</strong></summary>

项目目前没有使用付费 Apple Developer 账号。macOS 构建采用 ad-hoc 签名，因此首次启动需要在 Finder 中右键 Agent Island 并选择 **打开**。Sparkle 会在安装更新前独立验证更新签名。

</details>

<details>
<summary><strong>会话数据会离开我的电脑吗？</strong></summary>

会话状态、成本计算和报告都在本地生成。Agent Island 不上传会话数据，也不收集产品遥测。用量页面通过本地凭据存储访问服务方自有端点；Claude 重新认证可能刷新并更新其中与 Claude Code 或 Claude Desktop 共用的凭据。

</details>

<details>
<summary><strong>它和 codex-island 有什么区别？</strong></summary>

codex-island 是用量岛这个想法的起点，Agent Island 2026 年也从那里起步。今天交付的是另一个产品、另一套代码：五家服务的实时会话状态、轮到你与额度用完提醒、带可分享报告卡的本地成本账本，以及与 macOS 并行的原生 Windows 应用。

</details>

## 参与贡献

欢迎贡献 macOS、Windows、文档、测试和本地化。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 和 [Code of Conduct](CODE_OF_CONDUCT.md)。

当前适合首次参与的任务：

- [#10：编写 Windows 贡献者构建与测试流程](https://github.com/tristan666666/agent-island/issues/10)
- [#11：增加中英文本地化 key 一致性检查](https://github.com/tristan666666/agent-island/issues/11)
- [#15：增加已退役功能公开文案检查](https://github.com/tristan666666/agent-island/issues/15)

提交 macOS PR 前请运行 `./scripts/verify.sh`。Windows 改动由仓库中的 Windows CI 工作流检查。

## 路线图与版本

- [最新版本](https://github.com/tristan666666/agent-island/releases/latest)
- [路线图](docs/roadmap.md)
- [开放 Issues](https://github.com/tristan666666/agent-island/issues)

## 致谢与许可证

Agent Island 于 2026 年起步自 **Eric Park** 的 **[codex-island](https://github.com/ericjypark/codex-island)**——把用量岛放上刘海的最初想法来自那里。此后代码库已围绕五家服务的实时会话状态、轮到你与额度用完提醒、本地成本账本与报告卡，以及原生 Windows 应用重写。

项目采用 MIT 许可证，详见 [LICENSE](LICENSE)。
