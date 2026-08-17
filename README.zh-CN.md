# Honor Quota

Honor Quota 是一个 Windows 托盘额度面板，用来集中查看 Codex、OpenCode Go 和 DeepSeek。它把服务余额、OpenCode Go 额度窗口、模型估算和模型排序放在一个小巧的桌面工具里。

[English README](README.md) · [下载发布包](https://github.com/wupeng0601/Honor-quota/releases) · [OpenCode Go](https://opencode.ai/docs/zh-cn/go) · [DeepSeek API](https://api-docs.deepseek.com/zh-cn/api/get-user-balance/)

## 功能

- 查看 Codex 5 小时和每周用量。
- 查看 OpenCode Go 5 小时、每周和每月额度窗口。
- 通过 DeepSeek 官方余额接口查看余额。
- 自动刷新 OpenCode Go 模型目录。
- 选择模型、修改估算规则、拖动模型卡片排序。
- 按当前 Windows 用户安装，托盘菜单直接操作。

## 1. 下载和安装

1. 打开 [Releases](https://github.com/wupeng0601/Honor-quota/releases)，下载 `HonorQuota-0.0.1-win-x64.zip`。
2. 将 ZIP 解压到普通文件夹。
3. 运行 `install-honor-quota.ps1`。
4. 从开始菜单启动 **Honor Quota**，也可以运行 `start-honor-quota.ps1`。

安装器会检查 Microsoft Edge WebView2 Runtime，需要时提供官方 Evergreen Runtime 安装。程序默认安装到当前用户的 `%LOCALAPPDATA%\HonorQuota`。

![Honor Quota 主面板](assets/screenshots/dashboard-overview.png)

程序从托盘图标打开：左键点击图标会刷新并显示面板，右键点击图标可以打开设置。

## 2. 连接三个服务

### Codex

1. 先正常登录 Codex。
2. 打开 Honor Quota 托盘菜单。
3. 点击 `显示并刷新`。

Honor Quota 会读取当前 Codex 安装使用的本地账号。

### OpenCode Go

1. 右键点击 Honor Quota 托盘图标。
2. 点击 `OpenCode Go 登录/检查`。
3. 在 OpenCode Go 页面完成登录。
4. 关闭登录窗口，再点击 `显示并刷新`。

OpenCode Go 用量来自这套独立的 WebView2 登录会话。会话保存在 `%LOCALAPPDATA%\HonorQuota\WebView2`，最新页面数据会缓存到 `opencode_go_cache.json`。

### DeepSeek

1. 打开托盘菜单。
2. 点击 `DeepSeek API 配置...`。
3. 将 Key 粘贴到醒目的 **DeepSeek API Key** 输入框。
4. 点击 `验证并保存 Key`。
5. 余额请求成功后，主面板会自动刷新。

![DeepSeek API Key 配置窗口](assets/screenshots/deepseek-api-settings.png)

窗口支持输入 Key、临时显示 Key、查看本机配置状态，并提供 `清除本机 Key`。余额请求使用：

```text
GET https://api.deepseek.com/user/balance
Authorization: Bearer <你的 Key>
```

Key 会保存到当前 Windows 用户的 `DEEPSEEK_API_KEY` 环境变量。

## 3. 查看主面板

### 顶部摘要卡

- **Codex**：每周用量和 5 小时用量。
- **OpenCode Go**：每周剩余百分比、5 小时剩余百分比和每月剩余百分比。
- **DeepSeek**：官方 API 返回的余额和货币。

### OpenCode Go 额度窗口

- **5h 窗口**：短周期窗口和下次重置时间。
- **每周窗口**：每周额度和下次重置时间。
- **每月窗口**：每月额度和下次重置时间。

圆环和进度条表示剩余百分比，金额表示窗口剩余额度。

### 模型估算卡

每个已选模型会显示 5 小时、每周和每月窗口的典型请求估算。数值来自模型规则编辑器，可以按自己的使用情况调整。

## 4. 模型目录和用量规则

从托盘菜单打开 `OpenCode Go 模型与用量规则...`。

![OpenCode Go 模型与用量规则](assets/screenshots/model-rules-editor.png)

### 选择模型

- 按显示名称或 Model ID 搜索。
- 勾选模型，让它显示在主面板。
- 取消勾选即可隐藏模型。

### 刷新模型目录

点击 `刷新官方模型目录`，同步最新的 OpenCode Go 官方规则和实时模型目录。同步完成后，新模型会出现在编辑器中。

### 修改估算规则

1. 在窗口顶部设置 5 小时、每周和每月美元额度。
2. 修改各模型卡片中的典型请求估算。
3. 选择要展示在主面板的模型。
4. 点击 `保存并立即应用`。

### 拖动模型卡片

在主面板中拖动模型卡片，松开后即可保存新的展示顺序。顺序保存在 `opencode_go_models.json`。

## 5. 托盘菜单

| 菜单项 | 作用 |
| --- | --- |
| `显示并刷新` | 打开面板并刷新服务数据 |
| `静默刷新` | 在后台刷新服务数据 |
| `开机启动` | 开关当前用户的开机启动 |
| `OpenCode Go 登录/检查` | 打开 OpenCode Go WebView2 登录窗口 |
| `OpenCode Go 模型与用量规则...` | 管理模型、额度窗口和估算 |
| `DeepSeek API 配置...` | 输入、验证、保存或清除 DeepSeek Key |
| `打开程序目录` | 打开安装目录 |
| `退出` | 关闭 Honor Quota |

## 6. 本地文件

文件默认保存在安装目录：

| 文件 | 作用 |
| --- | --- |
| `opencode_go_models.json` | 模型选择、估算和拖动顺序 |
| `opencode_go_cache.json` | OpenCode Go 页面数据缓存 |
| `usage_history.json` | 本地用量历史 |
| `honor-quota-app.log` | 诊断日志 |
| `%LOCALAPPDATA%\HonorQuota\WebView2` | OpenCode Go 登录会话 |

## 7. 常见问题

| 情况 | 处理方式 |
| --- | --- |
| 启动后没有面板 | 打开系统托盘隐藏区域，点击 Honor Quota 图标。 |
| WebView2 初始化失败 | 安装或修复 [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)。 |
| Codex 没有数据 | 登录 Codex 后点击 `显示并刷新`。 |
| OpenCode Go 没有额度窗口 | 打开 `OpenCode Go 登录/检查` 重新登录，再刷新。 |
| DeepSeek Key 为空 | 打开 `DeepSeek API 配置...`，输入 Key 并点击 `验证并保存 Key`。 |
| DeepSeek 返回 401 | 在 [DeepSeek Platform](https://platform.deepseek.com/api-docs) 创建新的 Key。 |
| 找不到新模型 | 打开模型规则编辑器，点击 `刷新官方模型目录`。 |
| 模型顺序不对 | 直接拖动模型卡片重新排列。 |

## 8. 从源码构建

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

构建结果写入 `build\HonorQuota`，发布包写入 `dist\HonorQuota-0.0.1-win-x64.zip`。项目使用 .NET Framework WinForms 和 Microsoft WebView2。

## 许可证

MIT License，详见 [LICENSE](LICENSE)。
