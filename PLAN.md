# CryptoBotWeb — Plan

## Overview
Web-service for managing cryptocurrency exchange accounts and automated trading.
Dark-themed UI (similar to revert.finance). Docker deployment.

## Tech Stack
- **Backend**: ASP.NET Core 8 (Web API + Background Services)
- **Frontend**: React 18 + Vite + TypeScript
- **Database**: PostgreSQL 16 (via Entity Framework Core)
- **Exchanges**: JKorf libraries — Bybit.Net, JK.Bitget.Net, JK.BingX.Net
- **Auth**: JWT (access + refresh tokens)
- **Deploy**: Docker Compose (nginx + api + postgres)

---

## Project Structure

```
CryptoBotWeb/
├── docker-compose.yml
├── nginx/
│   └── nginx.conf
│
├── backend/
│   └── CryptoBotWeb.sln
│   └── src/
│       ├── CryptoBotWeb.Api/              # ASP.NET Core Web API
│       │   ├── Controllers/
│       │   │   ├── AuthController.cs       # Login, refresh token
│       │   │   ├── AccountsController.cs   # CRUD exchange accounts
│       │   │   ├── ExchangeController.cs   # Balances, orders, ticker
│       │   │   ├── StrategiesController.cs # Strategy CRUD + start/stop
│       │   │   └── DashboardController.cs  # Overview / stats
│       │   ├── Middleware/
│       │   │   └── JwtMiddleware.cs
│       │   ├── Program.cs
│       │   └── Dockerfile
│       │
│       ├── CryptoBotWeb.Core/             # Domain models + interfaces
│       │   ├── Entities/
│       │   │   ├── User.cs
│       │   │   ├── ExchangeAccount.cs
│       │   │   ├── Strategy.cs
│       │   │   ├── Trade.cs
│       │   │   └── OrderLog.cs
│       │   ├── Enums/
│       │   │   ├── ExchangeType.cs         # Bybit, Bitget, BingX
│       │   │   └── StrategyStatus.cs       # Idle, Running, Stopped, Error
│       │   ├── Interfaces/
│       │   │   ├── IExchangeService.cs
│       │   │   ├── IAccountRepository.cs
│       │   │   └── IStrategyEngine.cs
│       │   └── DTOs/
│       │       ├── LoginRequest.cs
│       │       ├── ExchangeAccountDto.cs
│       │       └── BalanceDto.cs
│       │
│       ├── CryptoBotWeb.Infrastructure/   # EF Core, Exchange clients
│       │   ├── Data/
│       │   │   ├── AppDbContext.cs
│       │   │   └── Migrations/
│       │   ├── Repositories/
│       │   │   └── AccountRepository.cs
│       │   └── Services/
│       │       ├── ExchangeServiceFactory.cs
│       │       ├── BybitExchangeService.cs
│       │       ├── BitgetExchangeService.cs
│       │       ├── BingXExchangeService.cs
│       │       └── EncryptionService.cs    # AES encrypt API keys
│       │
│       └── CryptoBotWeb.Worker/           # Background trading engine
│           ├── TradingHostedService.cs
│           ├── StrategyRunner.cs
│           ├── Program.cs
│           └── Dockerfile
│
├── frontend/
│   ├── package.json
│   ├── vite.config.ts
│   ├── Dockerfile
│   ├── index.html
│   ├── src/
│   │   ├── main.tsx
│   │   ├── App.tsx
│   │   ├── api/
│   │   │   └── client.ts               # Axios instance + interceptors
│   │   ├── stores/
│   │   │   └── authStore.ts             # Zustand auth state
│   │   ├── pages/
│   │   │   ├── LoginPage.tsx
│   │   │   ├── DashboardPage.tsx        # Main overview
│   │   │   ├── AccountsPage.tsx         # Exchange accounts list
│   │   │   ├── AccountDetailPage.tsx    # Balances, open orders
│   │   │   ├── StrategiesPage.tsx       # Strategy management
│   │   │   └── TradeHistoryPage.tsx     # Log of all trades
│   │   ├── components/
│   │   │   ├── Layout/
│   │   │   │   ├── Sidebar.tsx
│   │   │   │   ├── Header.tsx
│   │   │   │   └── MainLayout.tsx
│   │   │   ├── Dashboard/
│   │   │   │   ├── BalanceCard.tsx
│   │   │   │   ├── PnlChart.tsx
│   │   │   │   └── ActiveStrategies.tsx
│   │   │   ├── Accounts/
│   │   │   │   ├── AccountCard.tsx
│   │   │   │   └── AddAccountModal.tsx
│   │   │   └── ui/                      # Shared UI primitives
│   │   │       ├── Button.tsx
│   │   │       ├── Input.tsx
│   │   │       ├── Modal.tsx
│   │   │       ├── Table.tsx
│   │   │       └── StatusBadge.tsx
│   │   └── styles/
│   │       └── globals.css              # CSS variables, dark theme
│   └── tsconfig.json
│
└── PLAN.md
```

---

## Database Schema

### Users
| Column | Type | Note |
|---|---|---|
| id | uuid PK | |
| username | varchar(50) | unique |
| password_hash | varchar(256) | BCrypt |
| created_at | timestamptz | |

### ExchangeAccounts
| Column | Type | Note |
|---|---|---|
| id | uuid PK | |
| user_id | uuid FK → Users | |
| name | varchar(100) | Display name, e.g. "My Bybit" |
| exchange_type | smallint | Enum: Bybit=1, Bitget=2, BingX=3 |
| api_key_encrypted | text | AES-256 encrypted |
| api_secret_encrypted | text | AES-256 encrypted |
| passphrase_encrypted | text | nullable, for Bitget |
| is_active | boolean | |
| created_at | timestamptz | |

### Strategies
| Column | Type | Note |
|---|---|---|
| id | uuid PK | |
| account_id | uuid FK → ExchangeAccounts | |
| name | varchar(100) | |
| type | varchar(50) | strategy code name |
| config_json | jsonb | strategy parameters |
| status | smallint | Idle, Running, Stopped, Error |
| created_at | timestamptz | |
| started_at | timestamptz | nullable |

### Trades
| Column | Type | Note |
|---|---|---|
| id | uuid PK | |
| strategy_id | uuid FK → Strategies | |
| account_id | uuid FK → ExchangeAccounts | |
| exchange_order_id | varchar(100) | |
| symbol | varchar(30) | e.g. ETHUSDT |
| side | varchar(4) | Buy/Sell |
| quantity | decimal(18,8) | |
| price | decimal(18,8) | |
| status | varchar(20) | Filled, Cancelled, etc. |
| executed_at | timestamptz | |

---

## Color Theme (matching screenshot)

```css
:root {
  --bg-primary: #0b0e17;       /* main background — very dark blue */
  --bg-secondary: #111827;      /* cards, sidebar */
  --bg-tertiary: #1a1f2e;       /* table rows, inputs */
  --border: #1e2a3a;            /* subtle borders */
  --text-primary: #e2e8f0;      /* main text — light gray */
  --text-secondary: #8892a4;    /* muted text */
  --accent-green: #22c55e;      /* positive, active, profit */
  --accent-red: #ef4444;        /* negative, error, loss */
  --accent-purple: #8b5cf6;     /* buttons, links, highlights */
  --accent-blue: #3b82f6;       /* info, secondary actions */
}
```

---

## API Endpoints

### Auth
```
POST /api/auth/login        → { accessToken, refreshToken }
POST /api/auth/refresh       → { accessToken, refreshToken }
```

### Exchange Accounts
```
GET    /api/accounts          → list all accounts
POST   /api/accounts          → add new account (name, exchangeType, apiKey, apiSecret)
PUT    /api/accounts/{id}     → update account
DELETE /api/accounts/{id}     → delete account
POST   /api/accounts/{id}/test → test connection (verify API keys)
```

### Exchange Data (live from exchange)
```
GET /api/exchange/{accountId}/balances   → account balances
GET /api/exchange/{accountId}/ticker?symbol=ETHUSDT → ticker
GET /api/exchange/{accountId}/orders     → open orders
```

### Strategies (placeholder for future)
```
GET    /api/strategies                  → list all
POST   /api/strategies                  → create
PUT    /api/strategies/{id}             → update config
POST   /api/strategies/{id}/start       → start
POST   /api/strategies/{id}/stop        → stop
DELETE /api/strategies/{id}             → delete
```

### Dashboard
```
GET /api/dashboard/summary   → total balance, PnL, active strategies count
```

---

## Implementation Phases

### Phase 1 — Foundation (current scope)
1. Init backend solution (3 projects: Api, Core, Infrastructure)
2. Init frontend (React + Vite + TS)
3. Docker Compose (postgres + api + frontend via nginx)
4. Database + EF Core migrations
5. JWT Auth (login page + middleware)
6. Dark-themed layout (sidebar, header, routing)

### Phase 2 — Exchange Accounts
7. CRUD for exchange accounts (encrypted API keys)
8. ExchangeServiceFactory — creates appropriate JKorf client per exchange type
9. Test connection endpoint
10. Balances page — fetch & display live balances
11. Ticker data display

### Phase 3 — Dashboard & Monitoring
12. Dashboard page — aggregated balances, account statuses
13. Open orders view
14. Trade history table

### Phase 4 — Strategy Engine (future)
15. Worker service (BackgroundService)
16. Strategy interface + runner
17. Strategy CRUD UI
18. Start/Stop controls

---

## Key Architecture Decisions

1. **API keys encrypted at rest** — AES-256 with key from environment variable. Never stored in plaintext.
2. **ExchangeServiceFactory pattern** — Given an ExchangeAccount entity, creates the right JKorf client (BybitRestClient / BitgetRestClient / BingXRestClient). Unified `IExchangeService` interface for balance/orders/ticker operations.
3. **Worker as separate process** — The trading engine runs in CryptoBotWeb.Worker, a separate Docker container. Reads strategies from DB, executes trades. Decoupled from the API.
4. **Single admin user** — No registration flow. One admin user seeded on first run via env vars.
5. **JWT with refresh tokens** — Short-lived access token (15 min) + long-lived refresh token (7 days) stored in httpOnly cookie.

---

## Docker Compose Overview

```yaml
services:
  postgres:
    image: postgres:16-alpine
    volumes: [pgdata:/var/lib/postgresql/data]
    env: POSTGRES_DB, POSTGRES_USER, POSTGRES_PASSWORD

  api:
    build: ./backend (CryptoBotWeb.Api)
    depends_on: postgres
    env: ConnectionStrings__Default, Jwt__Secret, Encryption__Key

  worker:
    build: ./backend (CryptoBotWeb.Worker)
    depends_on: postgres
    env: same as api

  frontend:
    build: ./frontend (nginx serves built React app)
    ports: ["80:80"]
    proxy_pass /api → api:5000
```

---

## NuGet Packages (backend)

```xml
<!-- CryptoBotWeb.Api -->
Microsoft.AspNetCore.Authentication.JwtBearer
Swashbuckle.AspNetCore

<!-- CryptoBotWeb.Infrastructure -->
Npgsql.EntityFrameworkCore.PostgreSQL
Microsoft.EntityFrameworkCore.Design
Bybit.Net
JK.Bitget.Net
JK.BingX.Net
BCrypt.Net-Next

<!-- CryptoBotWeb.Core -->
(no external deps — pure domain)
```

## NPM Packages (frontend)

```json
{
  "dependencies": {
    "react": "^18",
    "react-dom": "^18",
    "react-router-dom": "^6",
    "zustand": "^4",
    "axios": "^1",
    "recharts": "^2",
    "@tanstack/react-query": "^5"
  },
  "devDependencies": {
    "typescript": "^5",
    "vite": "^5",
    "@vitejs/plugin-react": "^4",
    "tailwindcss": "^3",
    "autoprefixer": "^10",
    "postcss": "^8"
  }
}
```

---

## Scope of first implementation

Phases 1 + 2 — the goal is a working system where the user can:
1. Log in to the admin panel
2. Add/edit/delete exchange accounts (Bybit, Bitget, BingX)
3. Test API connection
4. View live balances per account
5. See a dashboard with aggregated data
6. Everything runs in Docker Compose

Strategy engine (Phase 4) will be a skeleton with interfaces only — ready for future strategies.
