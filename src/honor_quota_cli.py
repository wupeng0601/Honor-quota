#!/usr/bin/env python3
"""
Honor Quota CLI: Codex + OpenCode Go + DeepSeek API status.

No secrets are printed. API keys are read from environment variables:
  OPENCODE_GO_API_KEY or OPENCODE_API_KEY
  DEEPSEEK_API_KEY or DEEPSEEK_KEY
Codex auth is read from %CODEX_HOME%/auth.json or ~/.codex/auth.json.
"""

from __future__ import annotations

import argparse
import concurrent.futures
import json
import os
import re
import socket
import sys
import time
import urllib.error
import urllib.request
import uuid
from functools import lru_cache, partial
from pathlib import Path
from typing import Any


DEFAULT_CODEX_BASE_URL = "https://chatgpt.com/backend-api"
OPENCODE_GO_BASE_URL = "https://opencode.ai/zen/go/v1"
OPENCODE_BASE_URL = "https://opencode.ai"
OPENCODE_SERVER_URL = "https://opencode.ai/_server"
OPENCODE_WORKSPACES_SERVER_ID = "def39973159c7f0483d8793a822b8dbb10d067e12c65455fcb4608459ba0234f"
DEEPSEEK_BASE_URL = "https://api.deepseek.com"
CACHE_PATH = Path(__file__).with_name("honor_quota_cli_cache.json")
TRANSIENT_HTTP_CODES = {408, 425, 429, 500, 502, 503, 504}


def retry_delay(attempt: int) -> None:
    time.sleep(min(1.5, 0.35 * (attempt + 1)))


def http_json(url: str, headers: dict[str, str], timeout: int = 30, retries: int = 2) -> tuple[dict[str, Any] | list[Any], int]:
    for attempt in range(retries + 1):
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
                return json.loads(raw), resp.status
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", errors="replace")
            if e.code in TRANSIENT_HTTP_CODES and attempt < retries:
                retry_delay(attempt)
                continue
            try:
                parsed: dict[str, Any] | list[Any] = json.loads(raw)
            except Exception:
                parsed = {"error": raw[:500]}
            raise RuntimeError(f"HTTP {e.code}: {json.dumps(parsed, ensure_ascii=False)[:500]}") from None
        except (urllib.error.URLError, TimeoutError, socket.timeout) as e:
            if attempt < retries:
                retry_delay(attempt)
                continue
            raise RuntimeError(f"Network error: {e}") from None
    raise RuntimeError("HTTP request failed")


def http_text(url: str, headers: dict[str, str], timeout: int = 30, retries: int = 2) -> tuple[str, int]:
    for attempt in range(retries + 1):
        req = urllib.request.Request(url, headers=headers, method="GET")
        try:
            with urllib.request.urlopen(req, timeout=timeout) as resp:
                raw = resp.read().decode("utf-8", errors="replace")
                return raw, resp.status
        except urllib.error.HTTPError as e:
            raw = e.read().decode("utf-8", errors="replace")
            if e.code in TRANSIENT_HTTP_CODES and attempt < retries:
                retry_delay(attempt)
                continue
            raise RuntimeError(f"HTTP {e.code}: {raw[:500]}") from None
        except (urllib.error.URLError, TimeoutError, socket.timeout) as e:
            if attempt < retries:
                retry_delay(attempt)
                continue
            raise RuntimeError(f"Network error: {e}") from None
    raise RuntimeError("HTTP request failed")


@lru_cache(maxsize=None)
def registry_env(name: str) -> str | None:
    if os.name != "nt":
        return None
    try:
        import winreg

        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, "Environment") as key:
            value, _ = winreg.QueryValueEx(key, name)
        if isinstance(value, str) and value.strip():
            return value.strip()
    except Exception:
        return None
    return None


def env_first(names: list[str]) -> str | None:
    for name in names:
        value = os.environ.get(name)
        if value and value.strip():
            return value.strip()
        value = registry_env(name)
        if value:
            return value
    return None


def load_cli_cache() -> dict[str, Any]:
    try:
        if CACHE_PATH.exists():
            data = json.loads(CACHE_PATH.read_text(encoding="utf-8", errors="replace"))
            if isinstance(data, dict):
                return data
    except Exception:
        pass
    return {}


def save_cli_cache(cache: dict[str, Any]) -> None:
    try:
        CACHE_PATH.write_text(json.dumps(cache, ensure_ascii=False), encoding="utf-8")
    except Exception as e:
        print(f"warning: failed to save CLI cache: {e}", file=sys.stderr)


def codex_home() -> Path:
    raw = os.environ.get("CODEX_HOME")
    if raw and raw.strip():
        return Path(raw.strip()).expanduser()
    return Path.home() / ".codex"


def parse_chatgpt_base_url(config_text: str) -> str | None:
    match = re.search(r"(?m)^\s*chatgpt_base_url\s*=\s*['\"]([^'\"]+)['\"]", config_text)
    if not match:
        return None
    url = match.group(1).rstrip("/")
    if url.startswith("https://") or url.startswith("http://127.0.0.1") or url.startswith("http://localhost"):
        return url
    return None


def codex_base_url(home: Path) -> str:
    config_path = home / "config.toml"
    if config_path.exists():
        try:
            custom = parse_chatgpt_base_url(config_path.read_text(encoding="utf-8", errors="replace"))
            if custom:
                return custom
        except Exception:
            pass
    return DEFAULT_CODEX_BASE_URL


def load_codex_credentials() -> tuple[str, str | None]:
    auth_path = codex_home() / "auth.json"
    if not auth_path.exists():
        raise RuntimeError(f"Codex auth not found: {auth_path}")
    data = json.loads(auth_path.read_text(encoding="utf-8", errors="replace"))
    api_key = data.get("OPENAI_API_KEY")
    if isinstance(api_key, str) and api_key.strip():
        return api_key.strip(), None
    tokens = data.get("tokens") or {}
    access_token = tokens.get("access_token")
    account_id = tokens.get("account_id")
    if not isinstance(access_token, str) or not access_token.strip():
        raise RuntimeError("Codex auth.json has no access_token")
    return access_token.strip(), account_id if isinstance(account_id, str) and account_id.strip() else None


def number_at(value: Any, keys: list[str]) -> float | None:
    if not isinstance(value, dict):
        return None
    for key in keys:
        raw = value.get(key)
        if isinstance(raw, (int, float)):
            return float(raw)
        if isinstance(raw, str):
            try:
                return float(raw)
            except ValueError:
                pass
    return None


def text_at(value: Any, keys: list[str]) -> str | None:
    if not isinstance(value, dict):
        return None
    for key in keys:
        raw = value.get(key)
        if isinstance(raw, str) and raw.strip():
            return raw.strip()
    return None


def codex_window(obj: Any) -> dict[str, Any] | None:
    if not isinstance(obj, dict):
        return None
    used_percent = number_at(obj, ["used_percent", "usage_percent", "percent_used", "percent"])
    if used_percent is None:
        used = number_at(obj, ["used", "current", "count"])
        limit = number_at(obj, ["limit", "max", "total"])
        if used is not None and limit and limit > 0:
            used_percent = used / limit * 100.0
    if used_percent is None:
        return None
    return {
        "used_percent": round(max(0.0, min(100.0, used_percent)), 2),
        "resets_at": text_at(obj, ["resets_at", "reset_at", "resetAt"]),
        "reset_description": text_at(obj, ["reset_description", "resetDescription", "description"]),
        "window_minutes": number_at(obj, ["window_minutes", "windowMinutes"]),
    }


def codex_status(
    include_reset_credits: bool = True,
    reset_credits_timeout: int = 10,
    use_cached_reset_credits: bool = False,
) -> dict[str, Any]:
    token, account_id = load_codex_credentials()
    base = codex_base_url(codex_home())
    headers = {
        "Authorization": f"Bearer {token}",
        "Accept": "application/json",
        "User-Agent": "HonorQuotaCLI",
    }
    if account_id:
        headers["ChatGPT-Account-Id"] = account_id
    credits_future: concurrent.futures.Future[tuple[dict[str, Any] | list[Any], int]] | None = None
    if include_reset_credits:
        with concurrent.futures.ThreadPoolExecutor(max_workers=2) as executor:
            usage_future = executor.submit(http_json, f"{base}/wham/usage", headers)
            credits_future = executor.submit(
                http_json,
                f"{base}/wham/rate-limit-reset-credits",
                headers,
                reset_credits_timeout,
            )
            usage, _ = usage_future.result()
    else:
        usage, _ = http_json(f"{base}/wham/usage", headers)
    if not isinstance(usage, dict):
        raise RuntimeError("Codex usage response was not an object")
    rate = usage.get("rate_limit") if isinstance(usage.get("rate_limit"), dict) else {}
    primary = (
        codex_window(rate.get("primary_window"))
        or codex_window(usage.get("primary"))
        or codex_window(usage.get("primary_window"))
        or codex_window(usage)
    )
    secondary = (
        codex_window(rate.get("secondary_window"))
        or codex_window(usage.get("secondary"))
        or codex_window(usage.get("secondary_window"))
    )
    reset_credits: dict[str, Any] | None = None
    if use_cached_reset_credits:
        cached_reset = load_cli_cache().get("codex_reset_credits")
        if isinstance(cached_reset, dict):
            reset_credits = cached_reset
    if include_reset_credits and credits_future is not None:
        try:
            credits, _ = credits_future.result()
            if isinstance(credits, dict):
                reset_credits = {
                    "available_count": credits.get("available_count") or credits.get("availableCount"),
                }
                cache = load_cli_cache()
                cache["codex_reset_credits"] = reset_credits
                cache["codex_reset_credits_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
                save_cli_cache(cache)
        except Exception as e:
            if reset_credits is None:
                reset_credits = {"error": str(e)}
    return {
        "provider": "codex",
        "ok": True,
        "source": "codex_chatgpt_auth",
        "plan": usage.get("plan_type"),
        "primary": primary,
        "secondary": secondary,
        "reset_credits": reset_credits,
    }


def opencode_go_status(use_web: bool = True, check_api: bool = True) -> dict[str, Any]:
    cookie = env_first(["OPENCODE_GO_COOKIE", "OPENCODE_COOKIE"]) if use_web else None
    web_error = None
    if cookie:
        try:
            return opencode_go_web_usage(cookie)
        except Exception as e:
            web_error = str(e)
    cached = normalize_opencode_cache(load_opencode_go_cache())

    key = env_first(["OPENCODE_GO_API_KEY", "OPENCODE_API_KEY"])
    if not key:
        if cached:
            if web_error and not cached.get("primary"):
                cached["web_usage_error"] = web_error
            return cached
        return {
            "provider": "opencode_go",
            "ok": False,
            "error": web_error or "missing OPENCODE_GO_API_KEY / OPENCODE_API_KEY and OPENCODE_GO_COOKIE",
            "usage": "usage requires opencode.ai cookie or local OpenCode usage DB; API key only exposes models",
        }
    if cached and not check_api:
        return cached
    if not check_api:
        return cached or {
            "provider": "opencode_go",
            "ok": False,
            "error": "missing opencode_go_cache.json",
        }
    headers = {
        "Authorization": f"Bearer {key}",
        "Accept": "application/json",
        "User-Agent": "HonorQuotaCLI",
    }
    data, _ = http_json(f"{OPENCODE_GO_BASE_URL}/models", headers)
    models = data.get("data") if isinstance(data, dict) else data
    model_ids: list[str] = []
    if isinstance(models, list):
        for item in models[:10]:
            if isinstance(item, dict) and isinstance(item.get("id"), str):
                model_ids.append(item["id"])
    return {
        "provider": "opencode_go",
        "ok": True,
        "source": cached.get("source", "official_openai_compatible_api_health") if cached else "official_openai_compatible_api_health",
        "base_url": OPENCODE_GO_BASE_URL,
        "models_seen": len(models) if isinstance(models, list) else None,
        "sample_models": model_ids,
        "usage": "official API key endpoint does not expose remaining Go quota",
        **({"web_usage_error": web_error} if web_error and not (cached and cached.get("primary")) else {}),
        **({k: v for k, v in cached.items() if k in ["primary", "secondary", "monthly", "cached_at"]} if cached else {}),
    }


def load_opencode_go_cache() -> dict[str, Any] | None:
    path = Path(__file__).with_name("opencode_go_cache.json")
    if not path.exists():
        return None
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
        if isinstance(data, dict) and data.get("provider") == "opencode_go":
            data.setdefault("ok", True)
            return data
    except Exception:
        return None
    return None


def normalize_opencode_cache(data: dict[str, Any] | None) -> dict[str, Any] | None:
    if not data:
        return None
    limits = {"primary": 12.0, "secondary": 30.0, "monthly": 60.0}
    labels = {"primary": "5-hour", "secondary": "weekly", "monthly": "monthly"}
    for key, limit in limits.items():
        window = data.get(key)
        if isinstance(window, dict):
            used = window.get("used_percent")
            if isinstance(used, (int, float)):
                remaining_percent = max(0.0, min(100.0, 100.0 - float(used)))
                window["remaining_percent"] = round(remaining_percent, 2)
                window["limit_usd"] = limit
                window["remaining_usd"] = round(limit * remaining_percent / 100.0, 2)
                window["label"] = labels[key]
    return data


def opencode_go_web_usage(cookie: str) -> dict[str, Any]:
    ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
    workspace_override = env_first(["OPENCODE_GO_WORKSPACE_ID", "CODEXBAR_OPENCODEGO_WORKSPACE_ID"])
    workspace_id = normalize_workspace_id(workspace_override) if workspace_override else None
    base_headers = {
        "Cookie": cookie,
        "User-Agent": ua,
        "Origin": OPENCODE_BASE_URL,
        "Referer": OPENCODE_BASE_URL,
        "Accept": "text/javascript, application/json;q=0.9, */*;q=0.8",
    }
    if not workspace_id:
        headers = dict(base_headers)
        headers["X-Server-Id"] = OPENCODE_WORKSPACES_SERVER_ID
        headers["X-Server-Instance"] = f"server-fn:{uuid.uuid4()}"
        text, _ = http_text(f"{OPENCODE_SERVER_URL}?id={OPENCODE_WORKSPACES_SERVER_ID}", headers, timeout=30)
        workspace_id = first_workspace_id(text)
    if not workspace_id:
        raise RuntimeError("OpenCode Go workspace id not found from cookie session")

    page_headers = {
        "Cookie": cookie,
        "User-Agent": ua,
        "Referer": OPENCODE_BASE_URL,
        "Accept": "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8",
    }
    page, _ = http_text(f"{OPENCODE_BASE_URL}/workspace/{workspace_id}/go", page_headers, timeout=30)
    if looks_signed_out(page):
        raise RuntimeError("OpenCode cookie is signed out or expired")
    primary = extract_usage_window(page, ["rollingUsage", "rolling_usage", "rolling"])
    secondary = extract_usage_window(page, ["weeklyUsage", "weekly_usage", "weekly"])
    monthly = extract_usage_window(page, ["monthlyUsage", "monthly_usage", "monthly"])
    if not primary and not secondary and not monthly:
        raise RuntimeError("OpenCode Go usage windows not found in dashboard response")
    return {
        "provider": "opencode_go",
        "ok": True,
        "source": "opencode_web_dashboard_cookie",
        "workspace_id": workspace_id,
        "primary": primary,
        "secondary": secondary,
        "monthly": monthly,
        "balance": extract_zen_balance(page),
    }


def normalize_workspace_id(raw: str | None) -> str | None:
    if not raw:
        return None
    match = re.search(r"(wrk_[A-Za-z0-9_-]+)", raw)
    return match.group(1) if match else None


def first_workspace_id(text: str) -> str | None:
    seen = re.findall(r"(wrk_[A-Za-z0-9_-]+)", text)
    return seen[0] if seen else None


def looks_signed_out(text: str) -> bool:
    lower = text.lower()
    return "auth/authorize" in lower or '"signin"' in lower or "please sign in" in lower


def extract_usage_window(text: str, names: list[str]) -> dict[str, Any] | None:
    for name in names:
        segment = re.search(name + r".{0,1200}", text, flags=re.I | re.S)
        haystack = segment.group(0) if segment else text
        percent = re.search(r"(?:usagePercent|usedPercent|percentUsed|percent)\s*[:=]\s*([0-9]+(?:\.[0-9]+)?)", haystack, flags=re.I)
        if not percent:
            continue
        reset = re.search(r"(?:resetInSec|resetInSeconds|resetSeconds|resetSec)\s*[:=]\s*([0-9]+)", haystack, flags=re.I)
        used = float(percent.group(1))
        if used <= 1.0:
            used *= 100.0
        window = {"used_percent": round(max(0.0, min(100.0, used)), 2)}
        if reset:
            seconds = max(0, int(reset.group(1)))
            window["reset_description"] = human_duration(seconds)
            window["resets_at_epoch"] = int(time.time()) + seconds
        return window
    return None


def human_duration(seconds: int) -> str:
    minutes = seconds // 60
    if minutes < 60:
        return f"{minutes}m"
    hours = minutes // 60
    mins = minutes % 60
    if hours < 24:
        return f"{hours}h {mins}m" if mins else f"{hours}h"
    days = hours // 24
    rem_hours = hours % 24
    return f"{days}d {rem_hours}h" if rem_hours else f"{days}d"


def extract_zen_balance(text: str) -> dict[str, Any] | None:
    match = re.search(r"(?:zenBalance|balance|credits)[^0-9$¥￥]{0,80}([$¥￥]?)([0-9]+(?:\.[0-9]+)?)", text, flags=re.I)
    if not match:
        return None
    return {"amount": match.group(2), "symbol": match.group(1) or None}


def deepseek_status() -> dict[str, Any]:
    key = env_first(["DEEPSEEK_API_KEY", "DEEPSEEK_KEY"])
    if not key:
        return {
            "provider": "deepseek",
            "ok": False,
            "error": "missing DEEPSEEK_API_KEY or DEEPSEEK_KEY",
        }
    headers = {
        "Authorization": f"Bearer {key}",
        "Accept": "application/json",
        "User-Agent": "HonorQuotaCLI",
    }
    data, _ = http_json(f"{DEEPSEEK_BASE_URL}/user/balance", headers)
    if not isinstance(data, dict):
        raise RuntimeError("DeepSeek balance response was not an object")
    return {
        "provider": "deepseek",
        "ok": True,
        "source": "official_balance_api",
        "is_available": data.get("is_available"),
        "balance_infos": data.get("balance_infos", []),
    }


def capture(fn, provider: str) -> dict[str, Any]:
    try:
        return fn()
    except Exception as e:
        return {"provider": provider, "ok": False, "error": str(e)}


def main() -> int:
    parser = argparse.ArgumentParser(description="Check Codex, OpenCode Go, and DeepSeek API status.")
    parser.add_argument("--provider", choices=["all", "codex", "opencode_go", "deepseek"], default="all")
    parser.add_argument("--pretty", action="store_true", help="Pretty-print JSON")
    parser.add_argument("--strict", action="store_true", help="Exit non-zero if any selected provider fails")
    parser.add_argument("--fast", action="store_true", help="Skip slow optional provider probes and run providers concurrently")
    args = parser.parse_args()

    started = time.time()
    codex_probe = partial(
        codex_status,
        include_reset_credits=not args.fast,
        reset_credits_timeout=10,
        use_cached_reset_credits=args.fast,
    )
    opencode_probe = partial(opencode_go_status, use_web=not args.fast, check_api=not args.fast)
    providers = {
        "codex": partial(capture, codex_probe, "codex"),
        "opencode_go": partial(capture, opencode_probe, "opencode_go"),
        "deepseek": partial(capture, deepseek_status, "deepseek"),
    }
    selected = list(providers) if args.provider == "all" else [args.provider]
    provider_results: dict[str, dict[str, Any]] = {}
    if len(selected) > 1:
        with concurrent.futures.ThreadPoolExecutor(max_workers=len(selected)) as executor:
            future_to_name = {executor.submit(providers[name]): name for name in selected}
            for future in concurrent.futures.as_completed(future_to_name):
                provider_results[future_to_name[future]] = future.result()
    else:
        provider_results[selected[0]] = providers[selected[0]]()
    result = {
        "generated_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
        "elapsed_ms": None,
        "providers": [provider_results[name] for name in selected],
    }
    result["elapsed_ms"] = int((time.time() - started) * 1000)
    print(json.dumps(result, ensure_ascii=False, indent=2 if args.pretty else None))
    if args.strict and not all(item.get("ok") for item in result["providers"]):
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
