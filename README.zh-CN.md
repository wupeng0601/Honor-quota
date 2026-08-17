# Honor Quota

Honor Quota 是一个轻量级 Windows 托盘面板，把 Codex、OpenCode Go 和 DeepSeek 的用量状态集中展示在本机。它适合希望快速查看额度、又不想把 API 密钥交给第三方服务的用户。

[English README](README.md) · [版本下载](https://github.com/wupeng0601/Honor-quota/releases) · [OpenCode Go 官方文档](https://opencode.ai/docs/zh-cn/go)

## 项目简介

- 展示 Codex 用量、OpenCode Go 的 5 小时/每周/每月窗口，以及 DeepSeek 余额。
- 从本机 Codex 安装或环境变量读取凭据，不把密钥提交到仓库。
- 根据 OpenCode Go 官方文档和实时 `/models` 接口自动更新模型目录及用量规则。
- 可选择哪些模型显示在估算区。
- 可直接拖动模型卡片调整展示顺序，并在本地保存。
- 支持手动修改单个模型的典型请求估算，官方目录刷新不会覆盖手动覆盖值。
- 使用本机 WebView2 界面，不依赖 Honor Quota 云端后台。

## 工作方式

桌面程序完全在本机运行，刷新流程如下：

1. `honor_quota_cli.py` 读取本机 Codex 凭据和已配置的环境变量。
2. CLI 从相应官方接口请求各服务的状态。
3. 托盘程序在本机渲染结果，并把缓存/历史写在程序目录中。
4. OpenCode Go 的模型规则单独从官方文档和实时模型目录同步。

OpenCode Go 数据来源：

- 官方文档：<https://opencode.ai/docs/zh-cn/go>
- 实时模型目录：<https://opencode.ai/zen/go/v1/models>

如果实时目录已经出现新模型、但官方还没有公布典型用量规则，程序会将其标记为“待配置”，不会自行猜一个错误的额度。

## 运行要求

- Windows 10 或 Windows 11，x64。
- Microsoft Edge WebView2 Runtime（Evergreen）。Windows 11 通常已自带；Windows 10 可从 [Microsoft WebView2 页面](https://developer.microsoft.com/microsoft-edge/webview2/) 安装。
- Python 3.10 或更高版本，并且在 `PATH` 中；也可以用 `PYTHON` 环境变量指定 Python 路径。面板使用内置的 `honor_quota_cli.py` 刷新服务状态。
- 已登录的本机 Codex 环境。
- OpenCode Go 和 DeepSeek 需要按下面的本地登录/缓存或环境变量方式配置。

## 安装发布包

1. 从 [Releases](https://github.com/wupeng0601/Honor-quota/releases) 下载最新的 `HonorQuota-*-win-x64.zip`。
2. 解压到普通可写目录；也可以直接在解压目录运行 `install-honor-quota.ps1`。
3. 如果 PowerShell 阻止本地脚本，运行：

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-honor-quota.ps1
   ```

4. 从开始菜单快捷方式启动 `HonorQuota.exe`，或运行 `start-honor-quota.ps1`。
5. 左键点击托盘图标刷新并打开面板；右键可以进入设置、登录、开机启动和模型管理。

安装方式是按用户安装的便携式安装，不需要管理员权限，默认安装到 `%LOCALAPPDATA%\HonorQuota`。升级时会保留本地模型选择、拖拽顺序和缓存。

## 第一次配置

### Codex

CLI 会查找 `%CODEX_HOME%\auth.json`；如果没有设置 `CODEX_HOME`，则使用标准的 `~\.codex\auth.json`。Honor Quota 不要求你把 Codex token 粘贴进程序。

### OpenCode Go

从托盘菜单选择 `OpenCode Go 登录/检查`，建立本机 WebView 会话。程序也可能直接使用已有本地缓存。API key 可以用于模型访问，但不一定能提供订阅用量；能请求模型目录，不代表一定能读取额度。

### DeepSeek

如果希望查询 DeepSeek 余额 API，设置以下任意一个环境变量：

```text
DEEPSEEK_API_KEY
DEEPSEEK_KEY
```

OpenCode Go API/模型访问支持：

```text
OPENCODE_GO_API_KEY
OPENCODE_API_KEY
```

环境变量只在本机读取，不会写入仓库，也不会出现在诊断信息中。

## 模型规则和拖拽排序

从托盘菜单打开 `OpenCode Go 模型与用量规则...`：

- 勾选或取消模型，控制主面板的估算区。
- 只有在确实有依据时才手动修改 5 小时、每周或每月典型请求数。
- 点击 `刷新官方目录`，拉取最新官方文档和实时模型 ID。
- 没有官方估算的模型会保持“待配置”，手动填写后才会参与估算。
- 在主展示面板中，直接拖动模型卡片即可调整顺序；顺序会写入 `opencode_go_models.json`。

顶部三张摘要卡默认优先展示周额度；5 小时和每月窗口仍在下面的详细区域中完整展示。

## 本地文件和隐私

以下文件属于运行时状态，故意不放入公开仓库：

- `opencode_go_cache.json`
- `honor_quota_cli_cache.json`
- `usage_history.json`
- `honor-quota-app.log`
- `opencode_go_models.json`（个人模型选择和拖拽顺序）

不要把这些文件直接上传到 Issue 或公开发送；如果用于排障，应先删除账号、workspace、余额和时间信息。

## 从源码构建

源码是一个便于维护的 .NET Framework WinForms/WebView2 单文件应用。PowerShell 构建脚本会下载固定版本的 Microsoft WebView2 NuGet 包，并调用系统 C# 编译器：

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

构建结果写入 `build\HonorQuota`。脚本需要 `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319` 下的 .NET Framework 4.x 开发者编译器。

主要源码在 [`src/HonorQuotaApp.cs`](src/HonorQuotaApp.cs)，构建和安装脚本在 [`scripts/`](scripts/)。

## 常见问题

### 托盘图标没有用量

先看 `honor-quota-app.log`，再直接运行 CLI：

```powershell
python .\honor_quota_cli.py --pretty --fast
```

程序会分别报告各个 provider 的失败；某一个服务失败不代表全部服务都不可用。

### WebView2 初始化失败

安装或修复 Microsoft Edge WebView2 Evergreen Runtime，然后重启 Honor Quota。

### OpenCode Go 有模型但没有用量

模型目录和订阅用量是两个不同数据源。请从托盘菜单完成 OpenCode Go 本地登录，并等待缓存刷新。

### 程序无法写入设置

请安装到默认的用户目录或其他可写目录。不要直接把便携目录放到 `C:\Program Files`，除非你明确处理了写权限。

## 许可证和第三方声明

Honor Quota 源码采用 MIT License。程序同时使用 Microsoft WebView2 程序集和运行时组件，这些组件仍受 Microsoft 条款约束。服务名称、接口和 logo 归各自权利人所有。详见 [LICENSE](LICENSE) 和 [SECURITY.md](SECURITY.md)。

本项目是独立的本地工具，与 OpenAI、OpenCode、DeepSeek、Microsoft 或 HONOR 没有隶属关系。
