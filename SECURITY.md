# Security Policy

## Reporting a vulnerability

Please do not post credentials, tokens, cookies, workspace IDs, cache files, or complete logs in a public issue.

For a suspected security problem, open a private GitHub security advisory if enabled for the repository, or contact the maintainer through the GitHub profile before disclosure.

## Local secret handling

Honor Quota reads credentials from the local Codex auth file and environment variables. It is designed not to print secrets, but the surrounding operating system, Python environment, browser session, and provider endpoints remain outside the project's control.

Before sharing diagnostics, remove:

- `auth.json` contents;
- API keys and cookies;
- OpenCode Go workspace IDs;
- balance and usage history;
- local cache files and logs.

## Scope

The project is a local dashboard. It does not provide a hosted authentication service and does not promise that provider APIs, web sessions, quotas, or model catalogs remain stable.
