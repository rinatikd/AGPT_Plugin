# Agent tools — MCP-first

Дата: 2026-10-31

## Подключённые MCP (Grok connectors)

| MCP | Статус | Как вызывать |
|-----|--------|--------------|
| **GitHub** | ✅ connected | `search_connected_tools` → `call_connected_tool` (`github___*`) |
| **Voice** | ✅ | `voice_list_voices` и т.д. |
| **Automations** | ✅ | расписания / event triggers |

GitHub user: **rinatikd** — репозитории APS.AutoCAD, APS_Expert_0_5, APS-Suite, nss-engine, Autoplot…

## Внешние MCP (конфиг проекта)

Файлы:
- `artifacts/.mcp.json`
- `artifacts/tools/mcp.json`
- `artifacts/tools/mcp-composio.json`

```json
{
  "mcpServers": {
    "composio": { "url": "https://connect.composio.dev/mcp" },
    "browsermcp": { "command": "npx", "args": ["-y", "@browsermcp/mcp@latest"] },
    "playwright": { "command": "npx", "args": ["-y", "@playwright/mcp@latest"] }
  }
}
```

**Composio** (CLI уже стоит):
```bash
export PATH="$HOME/.local/bin:$PATH"
composio login
composio search github --human
```

**Claude in Chrome** — только на ПК пользователя (Chrome Web Store).

## Skills (локальные)

`artifacts/.agents/skills/`: graphify, find-skills, ponytail*, Osmani skills (spec/plan/review…).

## Политика: всё через MCP где возможно

1. GitHub (код, репо, PR, файлы) → **GitHub MCP**  
2. Внешние сервисы (Instagram, Gmail…) → **Composio MCP** после `composio login`  
3. Браузер → Browser MCP / Playwright MCP / Claude in Chrome  
4. Локальный код AGPT в sandbox → файлы + graphify-out (граф уже построен)

## Можно подключить ещё (request_connector_auth)

Gmail, Google Drive, Notion, Linear, Figma, Slack/Teams… — по запросу, максимум 3 за раз.
