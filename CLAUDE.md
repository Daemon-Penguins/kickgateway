# CLAUDE.md — kickgateway

**Źródło prawdy:** [[D:/Obsidian/nieprzecietny/20_Repozytoria/kickgateway.md]] + [[D:/Obsidian/nieprzecietny/10_Tematy/kickgateway/README.md]]

Zanim cokolwiek zrobisz w tym repo — przeczytaj powyższe notatki Vault. One są źródłem prawdy o "co", "dla kogo", "dlaczego" i "w jakim stanie". Ten plik trzyma tylko reguły techniczne.

## 🎯 Kontekst biznesowy
- **Firma:** `[[tailored-apps]]`
- **Temat:** `[[kickgateway]]`
- **Status projektu:** aktywny (rozwijany od 2026-05-19)
- **Cel:** Single point of ingestion dla Kick events w ekosystemie TA — wspiera `[[plan-1mln-2027]]` przez ułatwienie reuse webhook-driven integracji w kolejnych produktach.

## 🔗 Relacja do innych projektów
- **Następca po:** `libs/MudCarp.Kick` z `[[mudcarp]]` — patrz `[[10_Tematy/kickgateway/decyzje/ADR-0001-fork-mudcarp-kick]]`.
- **Long-form pułapki API Kicka (referencja):** `[[10_Tematy/mudcarp/integracje/kick]]` — gateway implementuje obronę przed wszystkimi 4 znanymi.
- **Potencjalny target ekstrakcji:** `[[shared-components|SharedComponents]]` — wzorzec "webhook gateway + outbox" jest kandydatem do osobnej biblioteki TA.

## 🛑 SharedComponents-first (MUST)

**Przed napisaniem jakiegokolwiek crosscutting concern** — sprawdź `[[shared-components]]`:
- 📦 Repo: https://github.com/tailored-apps/SharedComponents
- 📖 Docs: https://shared.tailoredapps.pl/
- 💻 Lokalnie: `C:\analiza\SharedComponents`

**⚠️ Świadome odstępstwa od standardu TA w tym repo** (udokumentowane w ADR-ach):

- **MassTransit + RabbitMQ** zamiast `SC.MediatR` — bo cały sens projektu to fanout do **wielu procesów** subscriberów, a SC.MediatR jest in-process. Patrz `[[10_Tematy/kickgateway/decyzje/ADR-0002-postgres-for-outbox]]` + supersede w `[[10_Tematy/kickgateway/decyzje/ADR-0005-sqlserver-supersedes-postgres]]`.
- **SQL Server** (na hq-config) — DB to standard TA + już istniejący shared SQL Server na hq-config. Patrz ADR-0005.
- **Brak SC.EntityFramework.UnitOfWork.WebApiCore** — projekt nie ma business commandów, tylko webhook dispatch. Lokalne `KickGatewayDbContext` bez SC.

Jakikolwiek **nowy** crosscutting concern (auth, logging behavior, payments, email) — najpierw SC, dopiero potem lokalna implementacja. Wymaga ADR.

## 🏗️ Stack (zatwierdzony)

### Backend
- **.NET 10** + ASP.NET Core Minimal APIs
- **EF Core 10** + **PostgreSQL** (odstępstwo udokumentowane)
- **MassTransit 8** + **RabbitMQ** (cross-process bus)
- **Blazor Server** (admin UI pod `/admin`)
- **.NET Aspire** AppHost (`TailoredApps.KickGateway.AppHost`) + `ServiceDefaults` (OTel, health, resilience)

### Frontend
- **Blazor Server** (interactive server render) — preferencja #1 TA, tu naturalne (single Web project z webhookami + UI)
- ❌ Nie React, nie Angular

### Infrastructure
- **Docker** + (TBD na prod) Traefik v2.11
- Dev: Aspire kontenery zarządzane przez AppHost (SQL Server + RabbitMQ)
- Fallback dev: `docker/docker-compose.yml`

### Language
- Kod: English (namespace TailoredApps, types, methods, comments-in-code)
- Commit messages, dokumentacja Vault, user-facing: Polish
- README repo: English (techniczne) + PL banner ze ścieżką do Vaulta

## 🔄 Auto-sync do Obsidian (MUST)

**Vault ma być zawsze aktualny bez ręcznej roboty Łukasza.** Ty (Claude) dbasz o to.

### Kiedy aktualizować `20_Repozytoria/kickgateway.md`:
- ✅ Po znaczącej zmianie stacka (nowa lib, porzucony framework)
- ✅ Po zmianie statusu (aktywny → pauza → ukończony)
- ✅ Po dodaniu/usunięciu zależności (zwłaszcza z SC, gdyby się pojawiła)
- ✅ Po deploy na nową domenę / serwer
- ✅ Pole `zaktualizowano: YYYY-MM-DD` zawsze na bieżąco

### Kiedy aktualizować `10_Tematy/kickgateway/README.md`:
- ✅ Po dodaniu nowego endpointa / contractu / consumer'a
- ✅ Po zmianie statusu otwartych pytań / "Następne kroki"
- ✅ Po decyzji architektonicznej (równocześnie: nowy ADR w `decyzje/`)

### Kiedy tworzyć ADR (`10_Tematy/kickgateway/decyzje/ADR-NNNN-<slug>.md`):
- ✅ Wybór biblioteki/frameworka (poza SC)
- ✅ Zmiana strategii deploy
- ✅ Odstępstwo od standardowego stacku TA
- ✅ Decyzja `build vs buy` o module
- ✅ Decyzja o ekstrakcji do SC / NuGet

### Kiedy aktualizować `tech/` notes:
- ✅ Znacząca zmiana flow (np. new auth method, new dispatch step)
- ✅ Nowy gotcha API Kicka — patrz też `[[../mudcarp/integracje/kick]]` jeśli dotyczy ogólnie Kicka

### Kiedy dodać wzorzec do `40_Wzorce/`:
- ⏳ Gdy "Webhook Gateway + Outbox" zostanie zatwierdzony jako TA pattern — promuj do `40_Wzorce/architektoniczne/webhook-gateway-outbox.md`. **Wymaga ADR.**

### Kiedy NIE tworzyć notatek
- ❌ Mały bugfix, rename, cosmetic — nie zaśmiecaj Vaulta
- ❌ Refactor wewnętrzny bez wpływu na API publiczne / kontrakty

## 🧪 Testy

- **Integration tests muszą hitnąć prawdziwą DB** (SQL Server testcontainer / EF in-memory **NIE**) — patrz feedback w `[[shared-components]]`.
- MassTransit **`ITestHarness`** dla testowania publish-flow bez stawiania RabbitMQ (in-memory transport w testach).
- Unit tests: **xUnit** (standard TA).
- Status: **brak testów na 2026-05-20** — priorytet medium, w "Następne kroki" tematu.

## 📁 Struktura solucji

```
TailoredApps.KickGateway.slnx
├── src/
│   ├── TailoredApps.Integrations.Kick/        # fork libs/MudCarp.Kick, multi-client
│   ├── TailoredApps.KickGateway.Contracts/    # shared MassTransit contracts
│   ├── TailoredApps.KickGateway.Api/          # WebAPI + Blazor + webhooks + EF
│   ├── TailoredApps.KickGateway.Worker/       # sample subscriber
│   ├── TailoredApps.KickGateway.AppHost/      # Aspire orchestrator (F5 entrypoint)
│   └── TailoredApps.KickGateway.ServiceDefaults/  # OTel/health/resilience shared
└── docker/docker-compose.yml                  # fallback bez Aspire
```

## 🔒 Bezpieczeństwo

- Sekrety w `appsettings.Development.json` (gitignored) / user-secrets / `.env` — **nigdy w repo**.
- `ClientId` / `ClientSecret` z Kick portalu wpisywane przez Blazor UI → DB. **Nie commitować** żadnego konkretnego `ClientId` w repo.
- Gitignore: `.env`, `bin/`, `obj/`, `appsettings.Development.json`, `appsettings.local.json`, `*.user`, `.vs/`.

## 💻 Komendy dev

```pwsh
# Aspire (preferowane) — F5 z VS na TailoredApps.KickGateway.AppHost
dotnet run --project src/TailoredApps.KickGateway.AppHost

# Manualnie (fallback)
docker compose -f docker/docker-compose.yml up -d
dotnet ef database update --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Api
dotnet run --project src/TailoredApps.KickGateway.Worker

# Migracje EF
dotnet ef migrations add <Name> --project src/TailoredApps.KickGateway.Api --output-dir Data/Migrations

# Build całej solucji
dotnet build
```

## Powiązania (do Vault)

- `[[20_Repozytoria/kickgateway]]` — stan repo
- `[[10_Tematy/kickgateway/README]]` — projekt + open questions + next steps
- `[[10_Tematy/kickgateway/decyzje/]]` — ADR-y 0001..0004
- `[[10_Tematy/kickgateway/tech/]]` — głębsze tech-notes (flow, model, topology)
- `[[10_Tematy/mudcarp/integracje/kick]]` — long-form Kick API gotchas (referencja)
- `[[shared-components]]` — biblioteki TA
- `[[40_Wzorce/architektoniczne/stos-tailored-apps]]` — stack TA (są odstępstwa)
- `[[40_Wzorce/proces/sharedcomponents-first]]` — reguła SC-first
