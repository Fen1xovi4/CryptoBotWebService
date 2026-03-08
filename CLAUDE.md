# CryptoBotWeb

## Local development ports

To avoid conflicts with parallel projects (which use 3000/8000), this project uses:

| Service    | Host port | Container port |
|------------|-----------|----------------|
| Frontend   | 3100      | 80             |
| API        | 8100      | 5000           |
| PostgreSQL | 5433      | 5432           |

Local URLs:
- Frontend: http://localhost:3100
- API: http://localhost:8100

**Never change these ports without checking for conflicts with other local projects.**

## Docker commands

```bash
docker compose up --build       # build and start
docker compose up --build -d    # build and start in background
docker compose down             # stop
docker compose logs -f          # view logs
```

## Project structure

- `backend/` — .NET API + Worker
- `frontend/` — React + Vite + Tailwind
- `docker-compose.yml` — orchestration
- `.claude/agents/` — specialized Claude Code agents

## Agents

Agents live in `.claude/agents/` and are launched via the Agent tool. Each has persistent memory in `.claude/agent-memory/<name>/`.

| Agent | Model | Description |
|-------|-------|-------------|
| **backend-api** | Sonnet | .NET API controllers, JWT auth, middleware, EF Core entities, DTOs, interfaces |
| **backend-worker** | Opus | TradingHostedService (5s loop), EMA Bounce strategy, martingale logic, position management |
| **exchange-integration** | Opus | Bybit/Bitget/BingX futures API clients, SOCKS5 proxy, AES key encryption |
| **frontend-dev** | Sonnet | React 19, TypeScript, Vite, Tailwind, Zustand, TanStack React Query v5 |
| **ef-core-database** | Sonnet | EF Core migrations, PostgreSQL 16 schema, query optimization, indexes |
| **devops-engineer** | Sonnet | Docker Compose, Nginx, env vars, deployment, health checks |
| **senior-decision-manager** | Sonnet | Architectural decisions, trade-off analysis, task prioritization |
