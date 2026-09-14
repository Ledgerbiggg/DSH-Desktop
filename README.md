<p align="center">
  <img src="docs/screenshots/app-icon.png" alt="DSH-Desktop Logo" width="96" height="96" />
</p>

<h1 align="center">DSH-Desktop</h1>

<p align="center">
  <b>一个轻量、常驻托盘的 DeepSeek 本地桌面客户端</b><br/>
  内嵌 WebView2 加载本地 dsh（DeepSeek harness）服务，支持全局快捷键呼出/隐藏、
  托盘常驻、开机自启、自动更新，把网页版 DeepSeek 包装成顺手的原生应用。
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square" alt=".NET 9" />
  <img src="https://img.shields.io/badge/Platform-Windows-0078D4?style=flat-square" alt="Windows" />
  <img src="https://img.shields.io/badge/License-MIT-green?style=flat-square" alt="License" />
  <img src="https://img.shields.io/badge/Version-v0.1.0-blue?style=flat-square" alt="Version" />
</p>

> ⚠️ **使用前必读**：DSH-Desktop 仅是 DeepSeek 的桌面壳，**本机必须先装好官方运行环境（Node.js 20+ 与 DeepSeek Harness 全局包）** 才能正常工作。未安装时启动会弹窗引导，但程序无法使用。准备步骤见 [📋 本地环境准备](#本地环境准备)。

---

## 📑 目录

- [✨ 项目简介](#-项目简介)
- [🚀 核心功能](#-核心功能)
- [🖼️ 截图](#️-截图)
- [⌨️ 快捷键](#️-快捷键)
- [📋 本地环境准备](#本地环境准备)
- [📦 安装与运行](#-安装与运行)
- [🔧 从源码构建](#-从源码构建)
- [⚙️ 配置说明](#️-配置说明)
- [🧩 技术栈](#-技术栈)
- [🤝 贡献](#-贡献)
- [📄 许可证](#-许可证)

---

## ✨ 项目简介

**DSH-Desktop** 是一个基于 WPF（.NET 9）打造的 DeepSeek 本地桌面客户端。它的目标是：

- **用原生窗口承载 DeepSeek**：内嵌 WebView2 加载本地 `dsh`（DeepSeek harness）服务（默认 `http://127.0.0.1:3080`），并自动注入一次性 token 鉴权，避免手动处理登录态。
- **像原生应用一样顺手**：支持全局快捷键呼出/隐藏、托盘常驻、开机自启、窗口位置与尺寸记忆。
- **零残留，更省心**：启动时检测本地是否已安装 dsh，未安装则弹窗引导安装；退出时多层兜底清理后台服务进程，绝不残留占用 3080 端口。

> 本项目为个人开源项目，欢迎 Issue 与 PR。当前版本为首个预览版 `v0.1.0`。

---

## 🚀 核心功能

| 功能 | 说明 |
| --- | --- |
| 🌐 **WebView2 内嵌 DeepSeek** | 加载本地 dsh 服务，自动带上一次性 token，页面即开即用 |
| ⌨️ **全局快捷键呼出 / 隐藏** | 可在设置中录制的全局热键，一键唤起或收起主窗口 |
| 📌 **托盘常驻** | 关闭主窗口后最小化到系统托盘，单击托盘图标即唤起 |
| 🔁 **开机自启** | 设置中开启后写入注册表 Run 项，随系统启动 |
| 🎨 **主题跟随系统** | 支持「跟随系统 / 浅色 / 深色」，切换即时生效 |
| 🔄 **自动更新** | 启动时静默比对 GitHub 上的 `version.json`，发现新版本可一键下载安装（UAC 提权） |
| 🧩 **本地依赖检测** | 启动前检测 dsh 是否安装，未安装则弹窗指向安装教程，装好后点「重新进入」即可 |
| 🧹 **后台进程零残留** | Job Object + 进程退出兜底 + 启动扫残三层保障，任何退出方式都不留 dsh 后台进程 |

---

## 🖼️ 截图

> 📌 截图正在补充中，以下为各界面预览（图片位于 `docs/screenshots/`，请将对应截图放到该目录即可显示）。

### 1. 主界面（WebView2 加载本地 DeepSeek）

![主界面](docs/screenshots/main.png)

### 2. 设置窗口（主题 / 快捷键 / 开机自启）

![设置](docs/screenshots/Ah)

### 3. 托盘与唤起

![托盘](docs/screenshots/tray.png)

### 4. 未安装 dsh 引导弹窗

![未安装引导](docs/screenshots/install-guide.png)

---

## ⌨️ 快捷键

> 默认热键为**全局生效**的「呼出 / 隐藏主窗口」，可在「设置 → 快捷键」中重新录制。

| 操作 | 说明 |
| --- | --- |
| 唤起 / 隐藏主窗口 | 在「设置」中录制（默认未设置，需先录制） |
| 打开 / 关闭设置 | 点击标题栏设置按钮 |

---

## 📋 本地环境准备

> ⚠️ **DSH-Desktop 只是 DeepSeek 的桌面壳，自身不包含后端服务。** 使用前必须在本机装好官方运行环境，否则程序启动后会弹窗提示，但**无法正常使用**。

### 1. 安装 Node.js 20 及以上

- 前往 [Node.js 官网](https://nodejs.org/) 下载 **20.x 或更高版本** 的 LTS 安装包并安装。
- 安装完成后，打开 PowerShell 验证：

  ```powershell
  node -v   # 应输出 v20.x 或更高
  npm -v
  ```

- （可选）若需同时管理多个 Node 版本，可用 `nvm` / `fnm` 切换到 20+。

### 2. 安装官方 DeepSeek Harness 全局包

通过 npm 全局安装官方 `@deepseek-ai/dsh` 包（它提供 `dsh` 命令）：

```powershell
npm install -g @deepseek-ai/dsh
```

安装完成后验证（能正常输出本地服务地址即表示就绪）：

```powershell
dsh web --no-open
# 默认在 http://127.0.0.1:3080 启动本地服务
```

> 💡 若 `npm install -g` 因权限报错，请以管理员身份运行 PowerShell，或自行配置 npm 全局目录。
> 安装命令以 [官方文档 / 菜鸟教程](https://www.runoob.com/deepseek-harness/deepseek-harness-install.html) 为准；国内网络较慢时可先配置 npmmirror 镜像源。

### 3. 启动本程序

完成上述两步后，再按下方 [📦 安装与运行](#-安装与运行) 启动 DSH-Desktop。若启动仍提示未检测到 dsh，请确认上一步 `dsh web` 能在 PowerShell 中独立运行，并参考弹窗中的「打开安装教程」。

---

## 📦 安装与运行

> 本地环境准备（Node.js 20+ 与 DeepSeek Harness 全局包）见上方 [📋 本地环境准备](#本地环境准备)。未安装时启动会弹窗引导，但程序无法正常工作。

### 方式一：下载发布包（推荐）

1. 前往 [Releases](https://github.com/Ledgerbiggg/DSH-Desktop/releases) 页面下载最新 `Dsh-Setup-*.exe` 安装包。
2. 双击安装，安装完成后从开始菜单或桌面快捷方式启动 `Dsh`。
3. 首次运行若提示未检测到 dsh，按上方教程安装后再「重新进入」。

### 方式二：从源码运行（开发者）

见下方 [🔧 从源码构建](#-从源码构建)。

---

## 🔧 从源码构建

### 环境要求

- **Windows 10 / 11**
- **.NET 9 SDK**（<https://dotnet.microsoft.com/download>）
- Visual Studio 2022（含「桌面开发」工作负载）或 Rider
- 本地已安装 `dsh`（DeepSeek harness）

### 构建步骤

```bash
# 1. 克隆仓库
git clone https://github.com/Ledgerbiggg/DSH-Desktop.git
cd DSH-Desktop

# 2. 还原依赖并构建
dotnet restore
dotnet build Dsh/Dsh.csproj -c Release

# 3. 运行
dotnet run --project Dsh/Dsh.csproj -c Release
```

也可直接使用仓库根目录的 `Makefile` 提供的便捷命令：

```bash
make dev      # 杀进程 + 构建(Debug) + 运行
make watch    # 热部署：监听源码变化，自动重建并重启（按 Q 退出）
make build    # 构建解决方案
make dist     # 本地打包安装包（需 Inno Setup）
make release  # 云端出包：升版本 + 写 notes + 提交 + 推送
```

---

## ⚙️ 配置说明

所有配置保存在用户目录 `%APPDATA%\Dsh` 下：

- **`settings.json`**：通用设置（主题、开机自启、全局热键、窗口位置与尺寸、是否启动到托盘等）。

常用操作：

- **打开配置目录**：`make config`（或直接资源管理器访问 `%APPDATA%\Dsh`）
- **打开日志目录**：`make logs`

> 💡 首次运行会自动写入默认 `settings.json`；若配置缺失或字段不全，程序会用内置默认值兜底，不会因配置错误而崩溃。

---

## 🧩 技术栈

- **语言 / 框架**：C# / .NET 9、WPF
- **UI 组件**：[WPF-UI](https://github.com/lepoco/wpfui)（Fluent Design 风格）
- **WebView**：Microsoft WebView2（承载本地 DeepSeek 服务）
- **架构**：Prism + Unity（MVVM、依赖注入）
- **托盘**：`System.Windows.Forms.NotifyIcon`
- **快捷键**：基于 Win32 `RegisterHotKey` 的自研 `HotkeyManager`
- **本地服务**：`dsh`（DeepSeek harness），经 PowerShell 拉起并提取带 token 的地址

---

## 🤝 贡献

欢迎一切形式的贡献！

1. Fork 本仓库并创建你的特性分支 (`git checkout -b feature/xxx`)
2. 提交你的修改 (`git commit -m 'feat: 添加 xxx'`)
3. 推送到分支 (`git push origin feature/xxx`)
4. 打开一个 Pull Request

提交前请运行 `dotnet build` 或 `make build` 确保无错误，并遵循现有的代码风格（注释一律中文、解释「为什么」）。

---

## 📄 许可证

本项目基于 **MIT License** 开源。详见 [LICENSE](LICENSE) 文件。

---

<p align="center">
  Made with ❤️ by DSH-Desktop contributors
</p>
