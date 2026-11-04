# DeadSwitch progress

Updated: 2026-11-04.

DeadSwitch is a personal uptime monitor. The backend is **not finished**: see the MVP status below.

## Architecture

.NET 10, ASP.NET Core Web API, EF Core, SQLite. Dependencies point inward only, confirmed from the project files and `using` statements.

| Project | Contains | References |
|---|---|---|
| `DeadSwitch.Domain` | `MonitoredService`, `CheckRecord` | nothing |
| `DeadSwitch.Application` | `MonitoredServiceService`, `ServiceConfiguration`, `UrlSafety`, `ServiceLocks`, interfaces | Domain |
| `DeadSwitch.Infrastructure` | `DeadSwitchDbContext`, repository, `HttpHealthProbe`, `PublicInternetConnector`, migrations | Application, Domain |
| `DeadSwitch.Api` | Startup in `Program.cs`, `ServiceEndpoints`, `MonitoringWorker` | Application, Domain, Infrastructure |
| `DeadSwitch.Tests` | Automated tests | Api, and the other projects through it |

## Endpoints

| Method and route | Purpose |
|---|---|
| `GET /health` | Health of the API itself |
| `POST /services` | Register a service |
| `GET /services` | List services |
| `GET /services/{id}` | Get one service |
| `PUT /services/{id}` | Update a service |
| `DELETE /services/{id}` | Delete a service and its history |
| `POST /services/{id}/check` | Manual check, saved to the history |
| `GET /services/{id}/history?skip=&take=` | Check history, newest first; `take` is 1 to 200 |
| `GET /check?url=` | Check a public URL without saving anything |
| `GET /services/{id}/check` | Older compatibility route; also saves to the history |

## Migrations

| Migration | Change |
|---|---|
| `InitialCreate` | Creates `MonitoredServices` |
| `AddCurrentStatusToMonitoredService` | Adds `CurrentStatus` |
| `AddLastCheckedAtToMonitoredService` | Adds `LastCheckedAt` |
| `AddMonitoringHistoryAndScheduling` | Adds `IntervalSeconds`, `TimeoutSeconds` and `IsEnabled`, and creates `CheckRecords` with cascade delete. Existing rows stay paused because `IsEnabled` defaults to false |

The application does not run migrations at startup. Run `dotnet ef database update` when setting up a database. The local development database has been recreated from all migrations.

## Latest verified result

Date: 2026-11-04.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 52 passed, 0 failed.
- Of the 52 tests, 25 only classify URLs and IP addresses, 7 test the probe with a stub handler, and 20 use the API, the worker or the migrations with a fake probe and an in-memory database.

### Manual run

Done by the owner against a newly created database:

- The application starts.
- CRUD works.
- A manual check against `example.com` works.
- Registering `127.0.0.1` returns 400. This proves the URL validation, not the check of resolved addresses at connection time.
- A confirmed bug: see the history row in the MVP status.

### Never run so far

- The background worker's loop against a real database.
- A hostname that resolves to a private address.
- A real redirect.
- The compatibility route and `GET /check` against the real network.

## Verified by tests

- CRUD, 400 on invalid configuration, and 404 on missing resources.
- A manual check saves status and history; history paging works.
- Deleting a service also deletes its history.
- Overlapping manual checks return 409, and a cancelled check saves nothing.
- A worker pass (`RunPassAsync`) checks only enabled services that are due, and continues correctly after a restart.
- The probe maps status codes, network failures and timeouts to the expected result.
- The latest migration keeps existing rows and pauses them.
- URL validation and private-address classification.
- The exact JSON field names of `/health`, `/check` and `GET /services/{id}/check`.

Dependency injection lifetimes were reviewed by reading the code only.

## MVP status

| Item | Status | What remains |
|---|---|---|
| CRUD and validation | Partial | `PUT` without `isEnabled` enables a paused service. Malformed JSON is untested |
| Manual check and history | Done | |
| Background worker | Partial | The `ExecuteAsync` loop, error isolation between services and shutdown are untested |
| No overlapping checks | Partial | Protects a single process only; see the lock limitation below |
| Status UP / DOWN / UNKNOWN | Partial | Decided rules are not built yet; see below |
| History endpoint | **Bug, confirmed** | `POST /services/{id}/check` returns `checkedAtUtc` with `Z`, but `GET /services/{id}/history` returns the same time without `Z`. A client can read it as local time. Not fixed yet |
| SSRF protection | Not proven | The code exists, but no test exercises the real connection path |
| Tests for all of the above | Partial | See the gaps above |
| README | Missing | |
| GitHub Actions | Missing | Waits for the owner's approval |

## Decided but not built

| Case | Decided | Current behaviour |
|---|---|---|
| Timeout | DOWN | DOWN, already correct |
| Blocked address | UNKNOWN | **Still DOWN** until step 3 below |
| 3xx response | UP, without following the redirect | **Still DOWN** until step 4 below |

Limitation of the 3xx decision: an answer of 3xx only proves that the server responded. The redirect target is never checked, so a redirect to a broken page still counts as UP. This must be documented in the README when it is built.

## Known limitations

- `ServiceLocks` shares 64 locks by hash, so two different services can share a lock. That can cause a false 409 or make an update wait for another service's check. With 10 services the chance of at least one collision is about 50 %.
- Checks run one at a time, so slow services delay all the others.
- History is never cleaned up and grows without limit.
- `SocketsHttpHandler.ConnectTimeout` is 10 seconds, even when `TimeoutSeconds` is higher.
- Status values are plain strings; there is no `ServiceStatus` enum.

## Code style exceptions

These parts do not yet follow the project's plain style, on purpose:

- `UrlSafety`, `PublicInternetConnector` and the HTTP handler configuration in `Program.cs` are security-critical and have no tests for the real connection path. They have comments only, until step 1 below adds those tests.
- `MonitoringWorker` keeps three `catch ... when` filters. Replacing them would duplicate the log lines.
- The queries in the repository stay as LINQ, because EF Core translates them to SQL.

## Security notes

Confirmed by reading the code, but **not proven by a test**:
- Redirects, proxies and cookies are disabled.
- `PublicInternetConnector` resolves the host, rejects it if any address is non-public, then connects directly to a validated IP. This is the protection against DNS rebinding.

Known problems:
- No test exercises `PublicInternetConnector` or the handler configuration in `Program.cs`. The tests would stay green if either were removed.
- `UrlSafety.IsPublicAddress` rejects all of `192.0.0.0/16` and `192.2.0.0/16`, which is wider than intended. This over-blocks public addresses and does not open a hole.
- There is no authentication or rate limiting. `GET /check` is open, and `GET /services/{id}/check` writes data. This is acceptable on localhost only and must be solved before any deployment.
- Internal hostnames pass registration. They are stopped at connection time, after the DNS lookup has already been made.
- The middleware in `Program.cs` catches every `ArgumentException`, so an internal error can become a 400 that shows its message.

Untested assumption:
- `168.63.129.16` (an Azure platform address) counts as public. This matters if the API is hosted on Azure.

## Next steps

Each step is one branch, in priority order:

1. `test/ssrf-real-handler`: move the handler configuration to Infrastructure, test it against a local test server, and bring the security code into the plain style.
2. `fix/service-locks`: one lock per service, with a test that different services do not block each other.
3. `fix/status-enum`: add `ServiceStatus`; a blocked address becomes UNKNOWN.
4. `fix/redirect-status`: 3xx becomes UP without following the redirect; document the limitation.
5. `test/worker-loop`: test error isolation, shutdown and worker against manual check.
6. `fix/api-contract`: dedicated validation exception, 400 on malformed JSON, safe `PUT` defaults, and `Z` on history timestamps.
7. `docs/readme`: how to run, migrate and test, plus all endpoints.

After that: add GitHub Actions once approved.
