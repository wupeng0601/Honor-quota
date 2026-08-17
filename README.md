# Honor Quota

Honor Quota is a Windows tray dashboard for Codex, OpenCode Go, and DeepSeek. It puts provider balances, OpenCode Go quota windows, model estimates, and model ordering in one compact panel.

[中文说明](README.zh-CN.md) · [Downloads](https://github.com/wupeng0601/Honor-quota/releases) · [OpenCode Go](https://opencode.ai/docs/zh-cn/go) · [DeepSeek API](https://api-docs.deepseek.com/api/get-user-balance/)

## Features

- Codex 5-hour and weekly usage.
- OpenCode Go 5-hour, weekly, and monthly windows.
- DeepSeek balance from the official balance API.
- Automatic OpenCode Go model catalog refresh.
- Model selection, estimate editing, and drag-and-drop ordering.
- Per-user Windows installation with a tray-first workflow.

## Current OpenCode Go reference

The bundled defaults and the in-app catalog sync follow the [current OpenCode Go page](https://opencode.ai/docs/zh-cn/go) (page update: 2026-08-16). Go has a **$12 / 5-hour**, **$30 / week**, and **$60 / month** usage window. The dollar windows are shared across models, so the practical request count changes with the selected model and its request pattern.

The currently documented catalog is: Grok 4.5; GLM-5.3, GLM-5.2, GLM-5.1; GPT 5.6 Luna; Kimi K3, Kimi K2.7 Code, Kimi K2.6; MiMo-V2.5, MiMo-V2.5-Pro; MiniMax M3, MiniMax M2.7; Qwen3.8 Max, Qwen3.7 Max, Qwen3.7 Plus, Qwen3.6 Plus; DeepSeek V4 Pro, DeepSeek V4 Flash; and Hy3.

On startup and when you click **刷新官方模型目录**, Honor Quota reads the official Go documentation for quota rules and also checks the live [`/models`](https://opencode.ai/zen/go/v1/models) directory. New live models are kept in the editor even if the documentation has not published a request estimate yet.

### Bundled typical-request estimates

| Model | 5 hours | Week | Month |
| --- | ---: | ---: | ---: |
| Grok 4.5 | 120 | 300 | 600 |
| GLM-5.3 | 220 | 540 | 1,080 |
| GLM-5.2 | 880 | 2,150 | 4,300 |
| GLM-5.1 | 880 | 2,150 | 4,300 |
| GPT 5.6 Luna | 2,050 | 5,100 | 10,250 |
| Kimi K3 | 110 | 250 | 490 |
| Kimi K2.7 Code | 1,350 | 3,380 | 6,750 |
| Kimi K2.6 | 1,150 | 2,880 | 5,750 |
| MiMo-V2.5 | 30,100 | 75,200 | 150,400 |
| MiMo-V2.5-Pro | 3,250 | 8,150 | 16,300 |
| MiniMax M3 | 3,200 | 8,000 | 16,000 |
| MiniMax M2.7 | 3,400 | 8,500 | 17,000 |
| Qwen3.8 Max | 160 | 400 | 810 |
| Qwen3.7 Max | 340 | 840 | 1,690 |
| Qwen3.7 Plus | 4,300 | 10,800 | 21,600 |
| Qwen3.6 Plus | 3,300 | 8,200 | 16,300 |
| DeepSeek V4 Pro | 1,050 | 2,600 | 5,200 |
| DeepSeek V4 Flash | 3,800 | 9,450 | 18,900 |
| Hy3 | 4,300 | 10,750 | 21,500 |

These are the official typical-request estimates, not a guarantee of how many requests your own prompts will use.

## 1. Download and install

1. Open the [Releases](https://github.com/wupeng0601/Honor-quota/releases) page and download `HonorQuota-0.0.1-win-x64.zip`.
2. Extract the ZIP to a normal folder.
3. Run `install-honor-quota.ps1`.
4. Start **Honor Quota** from the Start Menu, or run `start-honor-quota.ps1`.

The installer checks Microsoft Edge WebView2 Runtime and offers the official Evergreen Runtime installation when needed. The application is installed per user under `%LOCALAPPDATA%\HonorQuota`.

![Honor Quota dashboard](assets/screenshots/dashboard-overview.png)

The dashboard opens from the tray icon. Left-click the icon to refresh and show it; right-click the icon to open settings.

## 2. Connect the providers

### Codex

1. Sign in to Codex normally.
2. Open the Honor Quota tray menu.
3. Click `显示并刷新`.

Honor Quota reads the local Codex account used by the normal Codex installation.

### OpenCode Go

1. Right-click the Honor Quota tray icon.
2. Click `OpenCode Go 登录/检查`.
3. Sign in on the OpenCode Go page.
4. Close the login window and click `显示并刷新`.

OpenCode Go usage is read from this dedicated WebView2 login session, not from a Go API key stored by Honor Quota. The session is stored in `%LOCALAPPDATA%\HonorQuota\WebView2`; the latest dashboard values are cached in `opencode_go_cache.json`.

### DeepSeek

1. Open the tray menu.
2. Click `DeepSeek API 配置...`.
3. Paste the key into the large **DeepSeek API Key** field.
4. Click `验证并保存 Key`.
5. After the balance request succeeds, the dashboard refreshes automatically.

![DeepSeek API Key configuration](assets/screenshots/deepseek-api-settings.png)

The window accepts a key, lets you reveal it temporarily, shows the current local status, and provides `清除本机 Key`. The balance request uses:

```text
GET https://api.deepseek.com/user/balance
Authorization: Bearer <your-key>
```

The key is saved as the current Windows user's `DEEPSEEK_API_KEY` environment variable.

## 3. Read the dashboard

### Summary cards

- **Codex** — weekly usage and 5-hour usage.
- **OpenCode Go** — weekly remaining percentage, 5-hour remaining percentage, and monthly remaining percentage.
- **DeepSeek** — current balance and currency from the official API.

### OpenCode Go windows

- **5h window** — short rolling window and next reset time.
- **Weekly window** — weekly quota and next reset time.
- **Monthly window** — monthly quota and next reset time.

The ring and progress bar show remaining percentage. The dollar amount is the remaining window value.

### Estimate cards

Each selected model displays typical request estimates for the 5-hour, weekly, and monthly windows. These values come from the model rules editor and can be adjusted to match your own usage.

## 4. Model catalog and rules

Open `OpenCode Go 模型与用量规则...` from the tray menu.

![OpenCode Go model and usage rules](assets/screenshots/model-rules-editor.png)

### Select models

- Search by display name or Model ID.
- Check a model to show it on the dashboard.
- Uncheck a model to hide it.

### Refresh the catalog

Click `刷新官方模型目录` to update the current $12 / $30 / $60 windows, documented request estimates, and the live model directory. New live models appear in the editor even before an official estimate is available.

### Edit estimates

1. Set the 5-hour, weekly, and monthly dollar windows at the top.
2. Edit the typical request estimates on each model card.
3. Select the models for the dashboard.
4. Click `保存并立即应用`.

### Drag model cards

Drag a card in the dashboard and release it in the desired position. The order is saved to `opencode_go_models.json`.

## 5. Tray menu

| Menu item | Function |
| --- | --- |
| `显示并刷新` | Open the dashboard and refresh provider data |
| `静默刷新` | Refresh provider data in the background |
| `开机启动` | Toggle per-user startup |
| `OpenCode Go 登录/检查` | Open the OpenCode Go WebView2 session |
| `OpenCode Go 模型与用量规则...` | Manage models, windows, and estimates |
| `DeepSeek API 配置...` | Enter, verify, save, or clear the DeepSeek Key |
| `打开程序目录` | Open the installed application folder |
| `退出` | Close Honor Quota |

## 6. Local files

Files are stored in the installed application folder unless otherwise noted:

| File | Function |
| --- | --- |
| `opencode_go_models.json` | Model selection, estimates, and drag order |
| `opencode_go_cache.json` | OpenCode Go dashboard cache |
| `usage_history.json` | Local usage history |
| `honor-quota-app.log` | Diagnostic log |
| `%LOCALAPPDATA%\HonorQuota\WebView2` | OpenCode Go login session |

## 7. Troubleshooting

| Situation | Action |
| --- | --- |
| No dashboard after launch | Open the notification-area overflow menu and click the Honor Quota icon. |
| WebView2 initialization error | Install or repair the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/). |
| Codex data is empty | Sign in to Codex and click `显示并刷新`. |
| OpenCode Go shows no windows | Open `OpenCode Go 登录/检查`, sign in again, then refresh. |
| DeepSeek Key is empty | Open `DeepSeek API 配置...`, paste the key, and click `验证并保存 Key`. |
| DeepSeek returns 401 | Create a new key in [DeepSeek Platform](https://platform.deepseek.com/api-docs). |
| A new model is missing | Open the model rules editor and click `刷新官方模型目录`. |
| Model cards are in the wrong order | Drag them into the desired order and wait for the card to settle. |

## 8. Build from source

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build creates `build\HonorQuota` and `dist\HonorQuota-0.0.1-win-x64.zip`. The source uses .NET Framework WinForms and Microsoft WebView2.

## License

MIT License. See [LICENSE](LICENSE).
