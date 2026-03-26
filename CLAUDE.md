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

## Production deployment (VPS)

- **Domain:** `bot.cryptopizza.pl`
- **Reverse proxy:** Traefik v3 (separate compose at `/srv/proxy/`) on external Docker network `web`
- **SSL:** Let's Encrypt via Traefik certresolver `le`
- **Server path:** `/srv/apps/CryptoBotWeb`

### Traefik integration

Frontend container has labels for Traefik auto-discovery and is connected to the external `web` network. Do NOT remove these labels or the `networks.web` section from `docker-compose.yml`.

### Deploy commands (on VPS)

```bash
git pull && docker compose build --no-cache && docker compose up -d
```

### NuGet version pinning

**Always pin exact NuGet package versions** (e.g. `Version="8.0.25"`), never use floating versions like `Version="8.0.*"`. Floating versions resolve differently on the VPS Docker build vs local machine, causing assembly version conflicts at compile time.

### Dockerfiles

The project has two sets of Dockerfiles:
- `backend/Dockerfile.api` / `backend/Dockerfile.worker` — **multi-stage build** (restore + publish inside Docker). Used by `docker-compose.yml` for production.
- `backend/src/*/Dockerfile` — **runtime-only** (expects pre-built artifacts). NOT used in docker-compose.

Do NOT switch docker-compose to use `src/*/Dockerfile` — they will fail without pre-built publish output.

Similarly for frontend:
- `frontend/Dockerfile.prod` — multi-stage build with `npm ci && npm run build`. Used by docker-compose.
- `frontend/Dockerfile` — runtime-only, expects pre-built `dist/`.

### PostgreSQL

`PGDATA` is set to `/var/lib/postgresql/data/pgdata` (a subdirectory) to avoid `initdb` failures caused by filesystem artifacts like `lost+found` in the volume root.

## Git branching

- `main` — production branch, deploys to VPS. **Never commit directly to main.**
- `dev` — development branch, all daily work happens here.
- Flow: work in `dev` → test → merge into `main` → deploy.

**When the user asks to push, ALWAYS ask which branch (`dev` or `main`) before executing, to prevent accidental pushes to the wrong branch.**

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
