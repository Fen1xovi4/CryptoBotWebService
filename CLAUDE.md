# CryptoBotWeb

## Position mode

**All exchange integrations use one-way (unilateral) position mode, never hedge mode.**

Concretely:
- **Bybit** — omit `positionIdx` (defaults to one-way); close with `reduceOnly: true`.
- **BingX** — always pass `PositionSide.Both` (one-way); never `Long`/`Short`.
- **Bitget** — **omit `tradeSide` entirely** (pass nothing / `null`). The `JK.Bitget.Net` SDK throws `ArgumentException("Trade side should be either Open or Close if provided")` on any other value (including `BuySingle`/`SellSingle`/`OpenLong`/`CloseLong`/...) — verified by decompiling `PlaceOrderAsync` in v3.6.0. Per Bitget V2 API docs, one-way mode requires `tradeSide` to be empty. Close with `reduceOnly: true` only. Do **not** use `ClosePositionsAsync` with `PositionSide.Long/Short` — that's hedge-mode semantics.

Accounts on all three exchanges must be configured in one-way mode. Sending hedge-mode parameters (`tradeSide=Open/Close`, `holdSide`, `positionIdx != 0`) against a one-way account returns errors from the exchange.

## Local development ports

To avoid conflicts with parallel projects (which use 3000/8000), this project uses:

| Service    | Host port   | Container port |
|------------|-------------|----------------|
| Frontend   | 3100        | 80             |
| API        | 8100        | 5000           |
| PostgreSQL | 5433        | 5432           |
| Worker     | *(none)*    | *(none)*       |

Worker is a background-only service — it has no HTTP port and must stay that way. Do not add `ports:` to its `docker-compose.yml` entry.

Local URLs:
- Frontend: http://localhost:3100
- API: http://localhost:8100

**Never change these ports without checking for conflicts with other local projects.**

## Tech stack

| Layer      | Thing                         | Version        |
|------------|-------------------------------|----------------|
| Backend    | .NET TargetFramework          | `net8.0`       |
| Backend    | EF Core / Npgsql              | 8.0.25 / 8.0.11 |
| Backend    | Bybit.Net                     | 6.6.1          |
| Backend    | JK.Bitget.Net                 | 3.6.0          |
| Backend    | JK.BingX.Net                  | 3.6.0          |
| Backend    | BCrypt.Net-Next / Otp.NET     | 4.1.0 / 1.4.1  |
| Frontend   | React / React DOM             | 19.2.0         |
| Frontend   | TypeScript                    | 5.9.3          |
| Frontend   | Vite                          | 7.3.1          |
| Frontend   | Tailwind CSS                  | 4.2.0 (via `@tailwindcss/vite`) |
| Frontend   | Zustand                       | 5.0.11         |
| Frontend   | TanStack React Query          | 5.90.21        |
| Frontend   | react-router-dom              | 7.13.0         |
| Frontend   | axios                         | 1.13.5         |
| Charting   | lightweight-charts / recharts | 5.1.0 / 3.7.0  |

All NuGet versions are pinned exactly (see NuGet pinning note below).

## Trading strategies

Four state-machine-driven strategies live in `backend/src/CryptoBotWeb.Infrastructure/Strategies/`. Each has its own `*Config` (user-editable) and `*State` (runtime) DTO, both serialized to `ConfigJson` / `StateJson` JSONB columns on the `strategies` table.

- **MaratG / EMA Bounce** — `EmaBounceHandler.cs` + `EmaBounceSimulator.cs` — EMA/SMA indicator entries with martingale on reversal.
- **HuntingFunding** — `HuntingFundingHandler.cs` — funding-rate extreme hunter; hourly auto-rotation of the tradable ticker via `FundingTickerRotationService`.
- **FundingClaim** — `FundingClaimHandler.cs` — opens and holds through funding payout with a min-rate threshold; leverage-aware.
- **SmaDca** — `SmaDcaHandler.cs` — SMA-based tiered DCA grid with configurable market/limit DCA fills and TP exits; one direction per bot.

Execution: `TradingHostedService` in the Worker runs a 5-second loop with up to 20 strategies in parallel, dispatching each active strategy to its matching handler. `FundingTickerRotationService` fires at :50 past each hour to (re)assign tickers to `HuntingFunding` bots based on upcoming funding rates, workspace-level uniqueness, and the symbol blacklist.

## Environment variables

All required for production; `docker-compose.yml` provides dev-safe defaults for everything except `POSTGRES_PASSWORD`.

- `POSTGRES_PASSWORD` — Postgres user password.
- `JWT_SECRET` — ≥ 32 chars; signs JWTs.
- `ENCRYPTION_KEY` — AES key used to encrypt exchange API keys at rest.
- `ADMIN_USERNAME` / `ADMIN_PASSWORD` — seeded on first boot.
- `TRONGRID_API_KEY` — used by payment verification for TRC-20 USDT.
- `BSCSCAN_API_KEY` — used by payment verification for BEP-20.

`.env.example` currently lists only the first four; `TRONGRID_API_KEY` and `BSCSCAN_API_KEY` are referenced in `docker-compose.yml` but missing from the example and should be added when `.env.example` is next touched.

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

### Nginx (frontend → API)

`frontend/nginx.conf` uses Docker's embedded DNS resolver (`127.0.0.11`) plus a variable in `proxy_pass` (`set $api_upstream api; proxy_pass http://$api_upstream:5000;`) so nginx re-resolves the `api` hostname at runtime. This lets the API container be rebuilt (getting a new IP) without restarting the frontend. **Do not remove the `resolver` line or switch `proxy_pass` to a static hostname** — after an API rebuild, frontend would 502 until it was manually restarted.

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

```
backend/
  src/
    CryptoBotWeb.Api/            # ASP.NET Core API (controllers, JWT auth, middleware, DI setup)
    CryptoBotWeb.Core/           # Entities, DTOs, interfaces, enums — no external deps
    CryptoBotWeb.Infrastructure/ # EF Core DbContext, exchange clients, strategy handlers, background services
    CryptoBotWeb.Worker/         # TradingHostedService (5s loop), FundingTickerRotationService
  Dockerfile.api
  Dockerfile.worker
frontend/
  src/
    api/          # axios client + endpoint wrappers; base URL = /api, auto-attaches JWT, 401 refresh flow
    pages/        # route-level components (23 pages: Landing/Login/Dashboard/ActiveBots/TradeHistory/...)
    stores/       # zustand stores (auth, theme, payment, subscription, guestPayment)
    components/   # shared UI — Chart, Layout, ui/
  nginx.conf
  Dockerfile.prod
docker-compose.yml
.claude/agents/   # specialized Claude Code agents (see table below)
```

Core domain tables (all in `CryptoBotDbContext`): `users`, `workspaces`, `exchange_accounts`, `proxy_servers`, `strategies`, `trades`, `strategy_logs`, `subscriptions`, `payment_wallets`, `payment_sessions`, `support_tickets` + `support_messages`, `telegram_bots` + `telegram_subscribers`, `symbol_blacklist_entries`, `invite_codes` + `invite_code_usages`.

## Agents

Agents live in `.claude/agents/` and are launched via the Agent tool. Each has persistent memory in `.claude/agent-memory/<name>/`.

| Agent | Model | Description |
|-------|-------|-------------|
| **backend-api** | Sonnet | .NET API controllers, JWT auth, middleware, EF Core entities, DTOs, interfaces |
| **backend-worker** | Opus | `TradingHostedService` (5s loop, parallelism 20), all four strategies (EmaBounce, HuntingFunding, FundingClaim, SmaDca), `FundingTickerRotationService`, martingale/DCA logic, position management |
| **exchange-integration** | Opus | Bybit/Bitget/BingX futures API clients, SOCKS5 proxy, AES key encryption |
| **frontend-dev** | Sonnet | React 19, TypeScript, Vite, Tailwind 4, Zustand, TanStack React Query v5 |
| **ef-core-database** | Sonnet | EF Core migrations, PostgreSQL 16 schema, query optimization, indexes |
| **devops-engineer** | Sonnet | Docker Compose, Nginx, env vars, deployment, health checks |
| **senior-decision-manager** | Sonnet | Architectural decisions, trade-off analysis, task prioritization |
