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
| `DeadSwitch.Api` | Endpoints, startup, `MonitoringWorker` | Application, Domain, Infrastructure |
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

The application does not run migrations at startup. The local development database appears not to have the latest migration, judging only by its file timestamp; the database itself was not opened. Run `dotnet ef database update` before starting the API against it.

## Latest verified result

Date: 2026-11-04.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 41 passed, 0 failed. Three consecutive runs were stable during the review.
- Of the 41 tests, 25 only classify URLs and IP addresses, 7 test the probe with a stub handler, and 9 go through the API.

**Never run so far:** the API against a real database, real outgoing network calls, the requests in `DeadSwitch.Api.http`, and the migration on a real database. The routes in the `.http` file were only compared with `Program.cs`.

## Verified by tests

- CRUD, 400 on invalid configuration, and 404 on missing resources.
- A manual check saves status and history; history paging works.
- Deleting a service also deletes its history.
- Overlapping manual checks return 409, and a cancelled check saves nothing.
- A worker pass (`RunPassAsync`) checks only enabled services that are due, and continues correctly after a restart.
- The probe maps status codes, network failures and timeouts to the expected result.
- The latest migration keeps existing rows and pauses them.
- URL validation and private-address classification.

Dependency injection lifetimes were reviewed by reading the code only.

## MVP status

| Item | Status | What remains |
|---|---|---|
| CRUD and validation | Partial | `PUT` without `isEnabled` enables a paused service. Malformed JSON is untested |
| Manual check and history | Done | |
| Background worker | Partial | The `ExecuteAsync` loop, error isolation between services and shutdown are untested |
| No overlapping checks | Partial | Protects a single process only; see the lock limitation below |
| Status UP / DOWN / UNKNOWN | Partial | Decided rules are not built yet; see below |
| History endpoint | Partial | Timestamps read from SQLite probably lack the UTC marker; not yet confirmed by a run |
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

Untested assumptions:
- Existing rows in a local database have not been reviewed. Review their URLs before enabling them.
- `168.63.129.16` (an Azure platform address) counts as public. This matters if the API is hosted on Azure.

## Next steps

Each step is one branch, in priority order:

1. `test/ssrf-real-handler`: move the handler configuration to Infrastructure and test it against a local test server.
2. `fix/service-locks`: one lock per service, with a test that different services do not block each other.
3. `fix/status-enum`: add `ServiceStatus`; a blocked address becomes UNKNOWN.
4. `fix/redirect-status`: 3xx becomes UP without following the redirect; document the limitation.
5. `test/worker-loop`: test error isolation, shutdown and worker against manual check.
6. `fix/api-contract`: dedicated validation exception, 400 on malformed JSON, safe `PUT` defaults, UTC timestamps.
7. `docs/readme`: how to run, migrate and test, plus all endpoints.

After that: split `Program.cs` and the test files for readability, and add GitHub Actions once approved.
