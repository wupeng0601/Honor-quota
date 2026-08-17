# Honor Quota

Honor Quota is a lightweight Windows tray dashboard for checking Codex, OpenCode Go, and DeepSeek usage in one place. It is designed for people who want a quick local view of quota windows without putting API keys into a third-party service.

[中文说明](README.zh-CN.md) · [Releases](https://github.com/wupeng0601/Honor-quota/releases) · [OpenCode Go documentation](https://opencode.ai/docs/zh-cn/go)

## What it does

- Shows Codex usage, OpenCode Go 5-hour/weekly/monthly windows, and DeepSeek balance.
- Reads credentials from the local Codex installation or environment variables; secrets are never committed to this repository.
- Keeps the OpenCode Go model catalog current from the official documentation and live `/models` endpoint.
- Lets you choose which models appear in the estimate panel.
- Lets you drag model cards into a personal order; the order is saved locally.
- Supports manual per-model request estimates while preserving them across official catalog refreshes.
- Uses a local WebView2 UI with a tray-first workflow and no hosted backend.

## Screens and data flow

The desktop application runs locally. The normal refresh path is:

1. The built-in C# refresher reads local Codex credentials and configured environment variables.
2. The application requests provider status data from the relevant official endpoints, or uses the local OpenCode Go session/cache where that is the supported path.
3. The tray application renders the result locally and stores only local cache/history files beside the executable.
4. OpenCode Go model rules are refreshed separately from the official Go documentation and the live model directory.

OpenCode Go model IDs and live availability come from:

- Documentation: <https://opencode.ai/docs/zh-cn/go>
- Live model directory: <https://opencode.ai/zen/go/v1/models>

Models that appear in the live directory before an official usage estimate is published are shown as pending instead of being assigned a made-up quota.

## Requirements

- Windows 10 or Windows 11, x64.
- Microsoft Edge WebView2 Runtime (Evergreen). The installer checks for it and attempts to install the official Evergreen Runtime automatically. Windows 11 normally includes it; offline users may need to install the [WebView2 Standalone Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) once.
- Python is **not required** for the packaged application. `src/honor_quota_cli.py` is retained only as an optional diagnostic/backward-compatibility tool for developers.
- An authenticated Codex installation for Codex status.
- For OpenCode Go and DeepSeek status, use the supported local login/cache flow or the environment variables described below.

## Install the release package

1. Download the latest `HonorQuota-*-win-x64.zip` from [Releases](https://github.com/wupeng0601/Honor-quota/releases).
2. Extract it to a normal writable folder, then run `install-honor-quota.ps1` from the extracted folder. The installer checks WebView2 and tries to install the official Evergreen Runtime if it is missing.
3. If PowerShell blocks local scripts, run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install-honor-quota.ps1
   ```

4. Launch `HonorQuota.exe` from the Start Menu shortcut or run `start-honor-quota.ps1`.
5. Left-click the tray icon to refresh and open the dashboard. Right-click it for settings, login, startup, and model controls.

The application itself is a per-user install and uses `%LOCALAPPDATA%\HonorQuota` by default. The WebView2 bootstrapper may require network access and Windows permission elevation; on an offline machine, install WebView2 separately first. Existing local model selections and caches are preserved during an upgrade.

## First-time configuration

### Codex

The CLI looks for Codex authentication in `%CODEX_HOME%\auth.json`; when `CODEX_HOME` is not set it uses the normal `~\.codex\auth.json` location. The app does not ask you to paste a Codex token into Honor Quota.

### OpenCode Go

Use the tray menu entry `OpenCode Go 登录/检查` to establish the local WebView session. The app may also use an existing local cache. An API key can expose model access, but it does not necessarily expose subscription usage; do not assume that a model-list request proves that quota data is available.

### DeepSeek

Set one of the following environment variables if you want the DeepSeek balance API to be queried:

```text
DEEPSEEK_API_KEY
DEEPSEEK_KEY
```

For OpenCode Go API/model access, the CLI recognizes:

```text
OPENCODE_GO_API_KEY
OPENCODE_API_KEY
```

Environment variables are read locally and are never written to the repository or included in diagnostics.

## Model rules and ordering

Open the tray menu and choose `OpenCode Go 模型与用量规则...`.

- Check or uncheck models to control the main estimate panel.
- Edit 5-hour, weekly, or monthly typical request estimates only when you have a reason to override the official values.
- Use `刷新官方目录` to fetch current documentation and live model IDs.
- Models with no official estimate remain pending until you configure them manually.
- In the main dashboard, drag a model card to reorder it. The order is saved in `opencode_go_models.json`.

The top summary cards prioritize weekly quota for Codex and OpenCode Go. The detailed 5-hour and monthly windows remain visible below them.

## Local files and privacy

The following files are runtime state and are deliberately not part of the public repository:

- `opencode_go_cache.json`
- `honor_quota_cli_cache.json`
- `usage_history.json`
- `honor-quota-app.log`
- `opencode_go_models.json` (your personal model selection/order)

Do not upload these files to an issue or attach them to a public bug report without removing account, workspace, balance, and timing information first.

## Build from source

The source is a single .NET Framework WinForms/WebView2 application for easy local maintenance. A PowerShell build script downloads the pinned Microsoft WebView2 NuGet package and invokes the system C# compiler; Python is not needed to build or run the packaged app:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build.ps1
```

The build output is written to `build\HonorQuota`. The script requires the .NET Framework 4.x developer compiler under `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`.

The public source is in [`src/HonorQuotaApp.cs`](src/HonorQuotaApp.cs). The build script and installer are in [`scripts/`](scripts/).

## Troubleshooting

### The tray icon does not show usage

Check `honor-quota-app.log`. Developers may also run the optional diagnostic CLI directly:

```powershell
python .\src\honor_quota_cli.py --pretty --fast
```

An unavailable provider is reported separately; a failed provider request does not mean that all providers are broken.

### WebView2 initialization fails

Install or repair the Microsoft Edge WebView2 Evergreen Runtime, then restart Honor Quota.

### OpenCode Go shows a model but no usage

The model directory and the usage dashboard are different data sources. Complete the local OpenCode Go login flow and allow the app to refresh its local cache.

### The app cannot write settings

Install it under the default per-user path or another writable folder. Avoid placing the portable folder directly under `C:\Program Files` unless you intentionally run with the required permissions.

## License and third-party notices

Honor Quota source code is released under the MIT License. The application also uses Microsoft WebView2 assemblies and runtime components; those components remain subject to Microsoft's terms. Provider names, endpoints, and logos belong to their respective owners. See [LICENSE](LICENSE) and [SECURITY.md](SECURITY.md).

This project is an independent local utility. It is not affiliated with OpenAI, OpenCode, DeepSeek, Microsoft, or HONOR.
