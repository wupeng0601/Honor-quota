# Honor Quota

Honor Quota is a local Windows tray dashboard for Codex, OpenCode Go, and DeepSeek. It brings provider status, OpenCode Go quota windows, model estimates, and personal model ordering into one small desktop utility.

[中文使用说明](README.zh-CN.md) · [Downloads](https://github.com/wupeng0601/Honor-quota/releases) · [OpenCode Go docs](https://opencode.ai/docs/zh-cn/go) · [DeepSeek balance API](https://api-docs.deepseek.com/api/get-user-balance/)

## 1. What the application does

- Shows Codex 5-hour and weekly usage.
- Shows OpenCode Go 5-hour, weekly, and monthly windows.
- Shows DeepSeek balance from the official `GET /user/balance` API.
- Synchronizes the OpenCode Go model catalog from the official documentation and live model directory.
- Lets you choose visible models, edit typical-request estimates, and drag model cards into a personal order.
- Runs locally with a tray-first workflow. Honor Quota has no hosted backend.

The packaged application does not require Python. The optional `src/honor_quota_cli.py` is retained for developer diagnostics only.

## 2. Important: how each provider is read

The three providers do not expose their data in the same way.

| Provider | Normal packaged-app path | What you must provide |
| --- | --- | --- |
| Codex | Reads the existing local Codex authentication file, then calls the Codex usage endpoints. | A normal local Codex login |
| OpenCode Go | Uses an embedded WebView2 login session, reads the signed-in Go dashboard, and stores a local cache. | An OpenCode Go login inside `OpenCode Go 登录/检查` |
| DeepSeek | Calls the official balance API with a user-provided API Key. | A DeepSeek API Key configured in `DeepSeek API 配置...` |

### OpenCode Go is not API-Key-only

The normal dashboard path uses Honor Quota's own WebView2 profile. The login window is a real OpenCode web session stored under the per-user `HonorQuota\WebView2` folder; it is not a hard-coded password and it does not silently reuse Chrome cookies.

When the session is signed in, Honor Quota reads the page's `滚动用量`, `每周用量`, and `每月用量` values and writes the result to `opencode_go_cache.json`. The model API key variables are only kept for the optional diagnostic CLI and model-access checks. A successful model-list request does not prove that subscription quota is available.

### DeepSeek is explicit API configuration

DeepSeek does not have a Codex-style local login file that Honor Quota can safely discover for API balance. The tray menu therefore exposes a visible `DeepSeek API 配置...` window. It tests the official balance endpoint before saving the key to the current Windows user's `DEEPSEEK_API_KEY` environment variable. The key itself is never written to the repository or application log.

## 3. Requirements

- Windows 10 or Windows 11, x64.
- Microsoft Edge WebView2 Runtime. The installer checks for the Evergreen Runtime and attempts the official online installation when it is missing. Offline computers may need the [WebView2 Standalone Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) first.
- A normal Codex login if Codex usage is needed.
- An OpenCode Go login if Go subscription windows are needed.
- A DeepSeek API Key if the DeepSeek balance card is needed.

Python is not required for the release package.

## 4. Install and start

1. Download `HonorQuota-*-win-x64.zip` from [Releases](https://github.com/wupeng0601/Honor-quota/releases).
2. Extract it to a writable folder.
3. Run `install-honor-quota.ps1` from the extracted folder. If PowerShell blocks local scripts, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-honor-quota.ps1
   ```

4. Start `Honor Quota` from the Start Menu, or run `start-honor-quota.ps1`.
5. Left-click the tray icon to show the dashboard and refresh. Right-click the tray icon for the full menu.

The application installs per-user to `%LOCALAPPDATA%\HonorQuota` by default. The application installation is per-user; the WebView2 bootstrapper may still require network access or Windows permission elevation. The installer preserves local selections, ordering, cache, and history during an upgrade.

![Current Honor Quota dashboard](assets/screenshots/dashboard-overview.png)

_Current dashboard: weekly-first summary cards, separate 5-hour/weekly/monthly windows, model estimates, and drag-order hint._

## 5. First-time provider setup

### 5.1 Codex

1. Sign in through the normal Codex application or CLI.
2. Confirm `%CODEX_HOME%\auth.json` exists. If `CODEX_HOME` is not set, the default is `%USERPROFILE%\.codex\auth.json`.
3. Open the tray menu and choose `显示并刷新`.

Honor Quota does not ask you to paste the Codex token. If Codex fails, check the normal Codex login first. Never upload `auth.json` to an issue.

### 5.2 OpenCode Go

1. Right-click the tray icon.
2. Choose `OpenCode Go 登录/检查`.
3. Complete the OpenCode Go login in the opened window.
4. Keep the session signed in, close the window if desired, and choose `显示并刷新`.

The WebView2 session is separate from Chrome or Edge. If Go later shows `OCG 无缓存`, `缓存时间未知`, or no usage windows, open `OpenCode Go 登录/检查` again and complete the login in that window.

### 5.3 DeepSeek official API

1. Create or copy a key in the [DeepSeek Platform](https://platform.deepseek.com/api-docs).
2. Right-click the tray icon and choose `DeepSeek API 配置...`.
3. Paste the key into the masked `API Key` field. Use `显示 Key` only when needed.
4. Click `测试并保存`.
5. Wait for the message that the official balance endpoint succeeded. The dialog closes and Honor Quota refreshes the dashboard.

The test calls:

```text
GET https://api.deepseek.com/user/balance
Authorization: Bearer <your-key>
```

The response contains `is_available` and `balance_infos` with currencies such as CNY or USD. This is an API balance, not an OpenCode Go subscription window. To remove it, open the same dialog and click `清除本机 Key`.

![Current DeepSeek API settings dialog](assets/screenshots/deepseek-api-settings.png)

_Current dialog: the key is masked, the official balance endpoint is shown, and the key can be tested, saved, or cleared explicitly._

For compatibility, `DEEPSEEK_KEY` is also read. The normal UI writes the standard `DEEPSEEK_API_KEY` name.

## 6. Read the dashboard

### Summary cards

- **Codex**: weekly usage is shown first, followed by the 5-hour window. `Reset credits` is shown when the local Codex account exposes that value.
- **OpenCode Go**: weekly remaining percentage is shown first, followed by 5-hour and monthly remaining percentages.
- **DeepSeek**: shows the balance returned by the official API, normally in CNY.

### OpenCode Go quota cards

- **5h window** — short-term usage and next reset.
- **Weekly window** — the default headline window and next reset.
- **Monthly window** — the longer-term window and next reset.

The ring and progress bar show remaining percentage. `剩余` is the remaining dollar amount, while `已用` is the used percentage. These are provider windows, not a guaranteed number of future requests.

### Estimate cards

The estimate panel answers “roughly how many typical requests fit in the remaining window?” It is calculated from the selected model's configured 5-hour, weekly, and monthly typical-request values. It is not a billing meter. Prompt length, output length, tools, reasoning, and provider-side rule changes can change the real result.

## 7. Model catalog, rules, and ordering

Open the tray menu and choose `OpenCode Go 模型与用量规则...`.

![Current OpenCode Go model and usage rules editor](assets/screenshots/model-rules-editor.png)

_Current editor: official catalog sync, search, quota-window values, model selection, and the save/apply action are visible in one place._

### Select models

- Search by display name or model ID.
- Check a model to show it in the main estimate panel.
- Uncheck it to hide it without deleting its rule.

### Refresh new models

Click `刷新官方模型目录` when OpenCode Go publishes new models. Honor Quota combines the official Go documentation with the live model directory. A live model without a published estimate is marked **pending** instead of receiving an invented number.

### Edit estimates

The 5-hour, weekly, and monthly values at the top are the OpenCode Go dollar windows used for conversion. Each model has editable typical-request estimates. Change them only when you have a reliable documented or measured reason, then click `保存并立即应用`.

Manual overrides and enabled state are preserved separately, so a later official catalog refresh does not silently erase your choices.

### Drag cards

The main estimate cards can be dragged into a personal order. Release a card in its new position; the order is saved to `opencode_go_models.json`. Dragging changes presentation only, not provider access or quota calculation.

## 8. Tray actions and local files

| Tray action | Effect |
| --- | --- |
| `显示并刷新` | Opens the dashboard and refreshes provider data |
| `静默刷新` | Refreshes in the background |
| `开机启动` | Toggles the per-user startup entry |
| `OpenCode Go 登录/检查` | Opens the dedicated WebView2 login session |
| `OpenCode Go 模型与用量规则...` | Selects models, changes estimates, and refreshes the catalog |
| `DeepSeek API 配置...` | Tests, saves, or clears the official DeepSeek API Key |
| `打开程序目录` | Opens the local application folder |

Important runtime files:

| File or folder | Purpose |
| --- | --- |
| `opencode_go_models.json` | Model selection, drag order, and manual estimate overrides |
| `opencode_go_cache.json` | OpenCode Go usage cache from the WebView2 session |
| `usage_history.json` | Local dashboard history |
| `honor-quota-app.log` | Local diagnostic log |
| `%LOCALAPPDATA%\HonorQuota\WebView2` | OpenCode Go login/session data |

Keep these files private. They can contain workspace IDs, balances, account identifiers, timing, and session-related data.

## 9. Troubleshooting

| Symptom | What to do |
| --- | --- |
| Nothing appears after launch | Check the notification-area overflow menu, then run `HonorQuota.exe` from the installed folder. |
| `WebView2 初始化失败` | Install or repair the [Microsoft Edge WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/), then restart. |
| Codex unavailable | Complete the normal Codex login and verify `auth.json`. |
| OpenCode Go shows models but no quota | Use `OpenCode Go 登录/检查`; API Key/model access alone is not enough for subscription windows. |
| OpenCode Go says no cache | Sign in again in Honor Quota's own WebView2 window and click `显示并刷新`. |
| DeepSeek says Key missing | Open `DeepSeek API 配置...`, paste the key, and use `测试并保存`. |
| DeepSeek test returns 401 | The key is invalid or expired; create a new key in DeepSeek Platform. |
| DeepSeek test returns 402 | The official account has insufficient balance. |
| A model is missing | Refresh the official model catalog and search by model ID. |
| A model is pending | The live directory knows it, but no official estimate is available yet. |
| Estimates differ from actual use | Estimates are typical-request conversions, not provider billing records. |
| Settings do not persist | Use the default per-user folder; avoid `C:\Program Files` for a portable install. |

## 10. Privacy and security

- Honor Quota has no hosted backend.
- Provider requests go to the provider or official model-directory endpoints used by the application.
- Do not commit or publish `auth.json`, API Keys, caches, or logs.
- The public release package contains no personal credentials or runtime cache.
- The DeepSeek UI stores the key in the current Windows user's environment variables so the application can use it after restart; clear it from the same UI when no longer needed.

## 11. Build from source

The application is a .NET Framework WinForms/WebView2 program. The PowerShell build downloads the pinned Microsoft WebView2 NuGet package and uses the system C# compiler:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

Build output is written to `build\HonorQuota`; the release ZIP is written to `dist\`. Python is not required to build or run the packaged application. The optional diagnostic CLI is at [`src/honor_quota_cli.py`](src/honor_quota_cli.py).

## License and notices

Honor Quota source code is released under the MIT License. Microsoft WebView2 assemblies and runtime components remain subject to Microsoft's terms. Provider names, endpoints, and logos belong to their respective owners. See [LICENSE](LICENSE) and [SECURITY.md](SECURITY.md).

This is an independent local utility. It is not affiliated with OpenAI, OpenCode, DeepSeek, Microsoft, or HONOR.
