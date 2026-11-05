# DeadSwitch progress

Updated: 2026-11-04.

DeadSwitch is a personal uptime monitor. The backend is **not finished**: see the MVP status below.

## Architecture

.NET 10, ASP.NET Core Web API, EF Core, SQLite. Dependencies point inward only, confirmed from the project files and `using` statements.

| Project | Contains | References |
|---|---|---|
| `DeadSwitch.Domain` | `MonitoredService`, `CheckRecord`, `ServiceStatus` | nothing |
| `DeadSwitch.Application` | `MonitoredServiceService`, `ServiceConfiguration`, `UrlSafety`, `ServiceLocks`, interfaces | Domain |
| `DeadSwitch.Infrastructure` | `DeadSwitchDbContext`, repository, `HttpHealthProbe`, `ProtectedHttpHandler`, `PublicInternetConnector`, `DestinationBlockedException`, migrations | Application, Domain |
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
| `MakeCurrentStatusRequired` | Makes `CurrentStatus` NOT NULL with the default `UNKNOWN`. Rows with NULL become `UNKNOWN`, other values and all check records are kept. SQLite rebuilds the table for this, with foreign keys turned off around the drop so that the cascade does not delete the history |

The application does not run migrations at startup. Run `dotnet ef database update` when setting up a database. `MakeCurrentStatusRequired` has run in the tests and from empty against a separate database file, but not against the local development database. Run the command there as well.

Status is stored as the text `UP`, `DOWN` or `UNKNOWN`, as it always has been, so old rows did not need rewriting.

## Latest verified result

Date: 2026-11-04.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 228 passed, 0 failed, five runs in a row.
- Of the 228 tests, 130 only classify URLs and IP addresses, 17 run the real connection code against a local server, 16 test the probe with a stub handler, 7 test `ServiceLocks` directly, and 58 use the API, the worker or the migrations with a fake probe and an in-memory database.

### Red runs of the check interval

These tests failed against the old logic, which also gave 409 during a pass where the service is not due. The cause of the 15 seconds found in the manual run is therefore confirmed by tests with a fake clock.

| Test case | Expected | Result on the old code |
|---|---|---|
| Interval 10, check takes 500 ms | 10 s | 15 s |
| Interval 12 with the default poll, real loop | 12 s | 15 s |
| Interval 17 with the default poll, real loop | 17 s | 20 s |
| Check of 30 s with interval 10, next pass at 31 s | second check | no check |
| Pass 2 ms early | second check | no check |
| Pass at 9.75 s | second check | no check |

There were three separate causes, each fixed by its own change, with the tests run after each one:

1. The time was read after the probe, so every response time moved the next check past a poll. Reading it before the probe fixed the 500 ms and 30 s cases.
2. A check can only start on a poll, so the interval was rounded up to a multiple of it. A default poll of 1 second fixed intervals 12 and 17.
3. Two nearly equal times were compared, so a pass a few milliseconds early missed the check. A tolerance of 250 ms fixed the last two cases.

Interval 10 with the default poll in the real loop was green on the old code, because that probe takes no time. The response time is tested with passes run by hand: moving the fake clock during a pass also moves the next tick of the fake timer. A tick expected at 20 s came at 20.2 s when measured. The real timer has been measured once, at interval 10; see "Manual run".

### Mutation check of the worker and the lock check

The code was broken on purpose, one change at a time, running all tests. The changes were never committed. All of them ran against the fake probe, and none of them touched the SSRF protection.

| Change | Failing tests |
|---|---|
| The time is read after the probe | 2 |
| The default poll goes back to 5 | 2 |
| Tolerance 0 | 2 |
| Tolerance 300 ms | 1 |
| `>=` becomes `>` in `IsDue` | 1 |
| The timer does not use the injected clock | 1 |
| The poll setting is ignored, always 5 | 7 |
| The poll setting is ignored, always 1 | 0; see below |
| The first pass waits for a tick | 15 |
| No `catch (Exception)` per service | 2 |
| No `catch (Exception)` per pass | 1 |
| No `catch (CheckInProgressException)` | 1 |
| Shutdown is not rethrown per service | 2 |
| Shutdown is not rethrown per pass | 2 |
| No `catch` in `ExecuteAsync` | 3 |
| The `Enabled` check is removed | 2 |
| `ThrowIfCancellationRequested` removed in `RunPassAsync` | 0; see below |
| The `IsEnabled` filter removed in the worker | 0; see below |
| The `IsDue` check before the lock is removed | 1 |
| The whole block before the lock is removed, as before the fix | 3 |
| The `IsDue` check after the lock is removed | 1 |
| The check after the lock uses the first read | 1 |
| The check before the lock also runs for manual checks | 11 |
| The null check before the lock is removed | 1 |
| A rejected lock is ignored | 3 |
| The pass-finished line is not logged | 13 |

The three rows with 0 failing tests:

- The poll setting always 1: the tests only move the fake clock forwards, and showing that a tick did not happen would need a real wait. The default value and the upper limit of the setting are covered, a custom value below it is not.
- `ThrowIfCancellationRequested`: the next call throws through the same token anyway, so the line changes nothing that can be observed. It stays because it stops the pass without opening a new scope.
- The `IsEnabled` filter: `IsDue` makes the same check. The filter stays because it saves a read for paused services.

The mutation that removes the `Enabled` check most likely also starts the worker inside the test host, which would explain why a second test fails with it. This has not been confirmed.

The first mutation run found gaps in the tests, which were then closed. With a 200 ms probe, the tolerance hid a time read after the probe. A real timer ticking every second was fast enough to pass the loop tests, only slower. And removing the `IsDue` check before the lock passed the 409 test, because that test only stops the first read. All rows above were run again on the final tests.

### Mutation check of the redirect status

The code was broken on purpose, one change at a time, running all tests. The changes were never committed. All of them ran against the stub handler, and none of them touched the SSRF protection.

| Change | Failing tests |
|---|---|
| 3xx is no longer counted as UP | 6 |
| The lower limit becomes 301 | 1 |
| The upper limit becomes 398 | 1 |
| The upper limit becomes 400 | 1 |
| The probe sends a second request on 3xx | 1 |

### Mutation check of the status enum

The code was broken on purpose, one change at a time, running all tests. The changes were never committed. None of them removed the SSRF protection.

| Change | Failing tests |
|---|---|
| The connector throws a plain `HttpRequestException` | 1 |
| The probe does not search `InnerException` | 1 |
| A blocked address gives DOWN | 1 |
| A rejected URL gives DOWN | 2 |
| The JSON converter is removed | 6 |
| The JSON converter has no naming policy | 6 |
| The database conversion is removed | 31 |
| The database text is not uppercase | 31 |
| The migration has no default | 1 |
| The table is rebuilt with foreign keys left on | 1 |
| An update keeps the old status when the URL changes | 1 |

The two database rows fail so many tests because the model no longer matches the migrations, and EF Core then refuses to migrate. They therefore do not show which single test would have noticed the change.

The rebuild row shows that the migration test notices lost history: without EF Core's handling of foreign keys, the cascade deletes the check records and the test fails.

### Mutation check of the service locks

The test with 65 services failed against the old design, with 22 of the 65 checks wrongly rejected. The new locks were then broken on purpose, one change at a time, running only the lock and check tests. The changes were never committed.

| Change | Failing tests |
|---|---|
| An entry is never removed | 8 |
| All services share one lock | 3 |
| A cancelled waiter stays counted | 1 |
| `TryEnter` ignores a held lock | 3 |
| `Release` does not release the gate | 5 |
| `CheckAsync` does not release in `finally` | 5 |
| `DeleteAsync` does not take the lock | 1 |
| `UpdateAsync` does not take the lock | 1 |
| `EnterAsync` does not count the user | 6 |

### Mutation check of the SSRF protection

The protection was broken on purpose, one change at a time, to confirm that the tests notice. The changes were never committed.

| Change | Failing tests |
|---|---|
| Remove `ConnectCallback` | 8, with 3 tests left out; see below |
| `AllowAutoRedirect = true` | 2 |
| Remove the address check | 5 |
| Check only the first address | 1 |
| Add a second DNS lookup before connecting | 2 |
| `Program.cs` stops using the protected handler | 3 |
| The production rule allows every address | 2 |
| `UseCookies = true` | 2 |
| No fallback to the next address | 1 |
| Remove the empty DNS answer check | 0; see "Code style exceptions" |
| `172.16.0.0/12` one step too wide | 1 |
| No handling of IPv4-mapped IPv6 | 1 |
| No handling of a trailing dot | 3 |
| `3fff::/20` one step too wide | 1 |
| IPv6 allowlist without an upper limit | 3 |
| Any port allowed | 1 |

The row for removing `ConnectCallback` was run on the current code and tests. The other rows were run on the same code, just before the test URLs changed from invented names to `127.0.0.1`.

Removing `ConnectCallback` needs extra care, because the handler then looks up names itself. The tests therefore use `127.0.0.1` in their URLs, which needs no lookup. Three tests need a name and carry the trait `NameLookupWithoutConnector`, so this run leaves them out:

```powershell
dotnet test DeadSwitch.slnx --filter "Category!=NameLookupWithoutConnector"
```

That run was watched from inside the test process with the .NET network events: 0 name lookups, and every connection went to `127.0.0.1` or `::1`. A control confirmed that the events do show a lookup. This covers the test process only, not all traffic from the machine.

Whether names under `.localhost` stay on the machine could not be measured without administrator rights, so they are not used for this.

### Manual run

Done by the owner against a newly created database:

- The application starts.
- CRUD works.
- A manual check against `example.com` works.
- Registering `127.0.0.1` returns 400. This proves the URL validation, not the check of resolved addresses at connection time.
- A confirmed bug: see the history row in the MVP status.

Done by the owner on 2026-11-04:

- `GET /check?url=http://github.com` returned UP with status code 301. This is a redirect from a real website through the whole chain, and the status code shows that the redirect was not followed.
- The background worker against a real database, with its own database file: `intervalSeconds` = 10 against `https://github.com` with a poll of 1 second. 16 gaps between checks: minimum 9.975 s, maximum 10.001 s, mean 9.9985 s, all UP with status code 200. A check with a response time of 104 ms still gave a gap of 10.001 s. This is one run with one interval and one poll.

### Never run so far

- A hostname on the real network that resolves to a private address. Tests cover this with `localhost` and a fake DNS.
- The compatibility route against the real network.

The automated tests still cover a redirect in two separate parts, because `UrlSafety.Validate` stops the probe from reaching the local test server: the probe against a stub handler, and the real handler against a local server. No single test runs the whole chain; only the manual run above does.

## Verified by tests

- CRUD, 400 on invalid configuration, and 404 on missing resources.
- A manual check saves status and history; history paging works.
- Deleting a service also deletes its history.
- Overlapping manual checks return 409, and a cancelled check saves nothing.
- Every service has its own lock: 65 services can be checked at the same time, and an update does not wait for another service's check.
- An update or delete waits for a running check of the same service.
- Locks exist only while in use. Requests with random ids and cancelled waiters leave nothing behind, and 50 callers on one service never overlap.
- A worker pass (`RunPassAsync`) checks only enabled services that are due, and continues correctly after a restart.
- The worker loop in `ExecuteAsync`, with a fake clock and a fake probe: the first pass runs at once, the timer follows the injected clock, and a slow check never overlaps another.
- The real check interval matches `intervalSeconds` at 10, 12 and 17 seconds with the default poll in the loop, and over 16 checks at a 5 second poll and a 500 ms response time. A pass a few milliseconds early still checks the service, and a check is never more than 250 ms early.
- One failing service does not stop the others and is tried again. A failed pass is logged and the next one runs. A cancellation that is not a shutdown is logged and the loop continues. A service deleted during a pass causes no error.
- Shutdown while waiting and during a check ends the loop without error, saves nothing and leaves no lock. No further service is checked after a shutdown.
- Worker against manual check: the worker skips a service with a manual check running, a manual check gets 409 while the worker checks the same service, and a manual check moves the next worker check forward.
- A manual check gets no 409 during a pass where the service is not due, and the worker holds no lock in such a pass. If a manual check finishes after the worker's first read, the worker does not check again.
- The probe maps status codes, network failures and timeouts to the expected result.
- A 3xx response is UP with its status code saved and no failure reason, the limits 299/300 and 399/400 are correct, and the probe sends only one request.
- An address blocked at connection time and a rejected URL give UNKNOWN, through the real connector and `SocketsHttpHandler` for the blocked address. An empty DNS answer gives DOWN.
- Status is `UP`, `DOWN` or `UNKNOWN` as text in the JSON of every check route, the service and the history, and as text in the database.
- `AddMonitoringHistoryAndScheduling` keeps existing rows and pauses them.
- `MakeCurrentStatusRequired` turns NULL into `UNKNOWN`, and keeps other statuses and the check history.
- The model matches the migrations.
- URL validation, and address classification at the first and last address of every blocked range.
- The handler the application registers blocks `127.0.0.1` and `localhost`, and the local server receives no request.
- Redirects are returned and not followed, and cookies are not sent back.
- A DNS answer with a private address is blocked, as is an answer with one forbidden address among several and an empty answer.
- DNS rebinding: every new connection looks up and checks the address again, and connects to the checked address without a second lookup.
- The next address is tried when the first does not answer.
- The exact JSON field names of `/health`, `/check` and `GET /services/{id}/check`.

Dependency injection lifetimes were reviewed by reading the code only.

## MVP status

| Item | Status | What remains |
|---|---|---|
| CRUD and validation | Partial | `PUT` without `isEnabled` enables a paused service. Malformed JSON is untested |
| Manual check and history | Done | |
| Background worker | Done | Loop, error isolation, shutdown and interval are proven with a fake clock, and one manual run against a real database confirms interval 10 with a poll of 1 second. Intervals 12 and 17 are proven with the fake clock only |
| No overlapping checks | Partial | Protects a single process only; see the lock limitation below |
| Status UP / DOWN / UNKNOWN | Done | Proven by tests. A 3xx from a real website is confirmed by a manual run |
| History endpoint | **Bug, confirmed** | `POST /services/{id}/check` returns `checkedAtUtc` with `Z`, but `GET /services/{id}/history` returns the same time without `Z`. A client can read it as local time. Not fixed yet |
| SSRF protection | Done for HTTP | Proven by tests against a local server. HTTPS through the connector is untested; see the security notes |
| Tests for all of the above | Partial | See the gaps above |
| README | Missing | |
| GitHub Actions | Missing | Waits for the owner's approval |

## Status rules

| Case | Decided | Current behaviour |
|---|---|---|
| Timeout | DOWN | DOWN, proven by a test |
| Connection, DNS or TLS failure, including an empty DNS answer | DOWN | DOWN, proven by tests |
| Blocked address or rejected URL | UNKNOWN, because no request was sent and nothing was learned about the service | UNKNOWN, proven by tests |
| 3xx response | UP, without following the redirect | UP, proven by tests. The status code is saved, so the history shows that the answer was a redirect |

Limitation of the 3xx decision: an answer of 3xx only proves that the server responded. The redirect target is never checked, so a redirect to a broken page still counts as UP. This must be documented in the README when it is built.

Check records saved before this rule keep their old status: an earlier 3xx stays DOWN in the history. A service's current status changes at its next check.

## Known limitations

- `ServiceLocks` works within one process. Two running instances could check the same service at the same time. This is fine with SQLite and one container, but not for scaling out.
- An update or delete waits for a running check of the same service, up to its `TimeoutSeconds`.
- There is no limit on how many callers can wait for one lock, or on how many manual checks can run at once. Both remain open until authentication and rate limiting in phase 6.
- The worker checks services one at a time, so slow services delay all the others. The real interval therefore only matches `intervalSeconds` while a pass is short.
- A check can start up to 250 ms before its interval has passed.
- The worker reads the service list once per second, reads every enabled service once per pass, and reads a service that is due once more.
- A manual check gets 409 while the worker really is checking the same service, which is intended. A very short window also remains: a service that was due at the first read but not at the second holds the lock for a moment.
- History is never cleaned up and grows without limit.
- `SocketsHttpHandler.ConnectTimeout` is 10 seconds, even when `TimeoutSeconds` is higher. One address that never answers can use all of it before the next address is tried.
- A host name longer than 255 characters passes `UrlSafety` and only fails at the DNS step, which gives DOWN. It should be rejected earlier.
- A value in a status column other than `UP`, `DOWN` or `UNKNOWN` makes reading that row fail. The application only writes these three, so this needs a manual edit of the database.

## Code style exceptions

These parts do not yet follow the project's plain style, on purpose:

- `PublicInternetConnector.ConnectAsync` keeps a `try`/`catch` around the socket instead of `using`. The socket must be closed on failure but stay open when it is handed to the stream.
- `PublicInternetConnector.ConnectAsync` keeps an explicit check of an empty DNS answer, although the last line of the method would also stop one. This is defence in depth: it states the intent, and the protection must not depend on how the rest of the method is written. The tests therefore stay green if the check is removed.
- `MonitoringWorker` keeps three `catch ... when` filters. Replacing them would duplicate the log lines.
- The queries in the repository stay as LINQ, because EF Core translates them to SQL.

## Security notes

Proven by tests against a local server:
- `PublicInternetConnector` resolves the host, rejects it if any address is non-public, then connects directly to a validated IP. This is the protection against DNS rebinding.
- Redirects are not followed and cookies are not sent back.
- The configuration lives in `ProtectedHttpHandler`. The version that accepts other address rules is `internal`, so only the test project can build a connector that allows private addresses.

Layer 1 and the connection now judge the same name:
- **What got through:** `UrlSafety` used to judge `Uri.DnsSafeHost`, while the connection uses `Uri.IdnHost`. These can differ. `http://ｌｏｃａｌｈｏｓｔ/` and `http://１２７.０.０.１/` were therefore accepted by `POST`, by `PUT` and by `GET /check`, although the connection would use `localhost` and `127.0.0.1`. The same went for other characters that count as a dot, such as `127。0。0。1`, and for fullwidth forms of `10.0.0.1` and `169.254.169.254`.
- **What stopped it:** `PublicInternetConnector`, layer 2, blocked these addresses at connection time, and the status became UNKNOWN. This was measured with a fake DNS and through the real handler. Only the first line of defence was missing.
- **A second effect of the same cause:** a host that cannot be converted, such as one with a soft hyphen as its only label character (`http://\u00AD.example.com/`), was accepted by `UrlSafety` and then threw `UriFormatException` inside `HttpClient`. `GET /check` returned 500. The worker logged the error, saved no history and tried again on every pass, because the service stayed due.
- **The fix:** layer 1 now judges `IdnHost`, the same name the connection uses, and always tries the converted text as an IP address instead of relying on `Uri.HostNameType`. A host that cannot be converted is rejected. A row saved before the fix gets UNKNOWN at its next check, and its interval applies again.
- `DnsSafeHost` and `IdnHost` gave the same text for all 25 addresses compared, IPv6 included, so ordinary addresses are not affected.

Checked as settings only, **not by running them**:
- `UseProxy = false` and `PooledConnectionLifetime`. `ConnectTimeout` is not checked at all.

Not proven by a test:
- HTTPS through the connector. A test would have to change the certificate validation in the handler it is testing. To be covered in phase 3.
- That the socket is closed when a connection attempt is cancelled.
- That looking up `localhost` stays on the machine. The tests assume this and it has not been measured.

Known problems:
- There is no authentication or rate limiting. `GET /check` is open, and `GET /services/{id}/check` writes data. This is acceptable on localhost only and must be solved before any deployment.
- Internal hostnames pass registration. They are stopped at connection time, after the DNS lookup has already been made.
- The middleware in `Program.cs` catches every `ArgumentException`, so an internal error can become a 400 that shows its message.

Untested assumption:
- `168.63.129.16` (an Azure platform address) counts as public. This matters if the API is hosted on Azure.

## Roadmap

DeadSwitch is growing into a tool that watches both the availability and the security configuration of web services, and says so when something goes wrong. The goal is not to sell it. The goal is a quality that a real development team would want to use internally. The code stays simple and readable: the difficulty belongs in the design decisions, not in the syntax.

The phases are done in the order below: 1, 2, 5, 3, 4, 6. The dashboard comes early so that every new feature is visible in it at once. The MVP still covers phases 1 to 4; the dashboard is built earlier but is not part of the MVP. Each task is one branch, with a plan approved first.

**Next step:** `fix/api-contract`.

### Phase 1: Stable foundation

1. Done: `test/ssrf-real-handler`: move the handler configuration to Infrastructure and test redirects, private addresses and DNS rebinding against a local test server. Then bring the security code into the plain style, protected by those tests.
2. Done: `fix/service-locks`: one lock per service, with a test that different services do not block each other.
3. Done: `fix/status-enum`: add `ServiceStatus`. A timeout is DOWN and a blocked address is UNKNOWN.
4. Done: `fix/redirect-status`: 3xx is UP, without following the redirect. Document the limitation.
5. Done: `test/worker-loop`: test error isolation, shutdown and worker against manual check. A manual run had shown that `intervalSeconds` = 10 usually became 15 seconds. The cause is confirmed and fixed; see "Red runs of the check interval". A manual run against a real database confirms it at interval 10; see "Manual run".
6. `fix/api-contract`: dedicated validation exception, 400 on malformed JSON, safe `PUT` defaults, and UTC timestamps with `Z` (the confirmed bug above). This task must start from the contract change below.

**Contract change from `test/worker-loop`:** `checkedAtUtc` in the check responses and the history now means when the check **started**, not when it ended. The same applies to `lastCheckedAt` on the service and in the compatibility route. The field names and the format are unchanged. Check records saved before this change keep their end time. A record's end time is not stored, but it can be estimated by adding `responseTimeMs`.

### Phase 2: Notifications

- Notify on a status change, UP to DOWN and DOWN to UP, never on every check.
- First channel: a Discord webhook, behind an `INotifier` interface in Application, so that more channels can be added without touching the core logic.
- The webhook URL is a secret. It belongs in user secrets or environment variables, never in `appsettings.json` or git.
- Tests use a fake notifier and make no real calls.

### Phase 5: Dashboard

- A simple web page showing all services, status and response time. Phase 3 adds certificate days left and header grade to it.
- The dashboard runs locally only until the authentication in phase 6 is done.
- The simplest technology that is enough, for example a static HTML page calling the API. No heavy frontend framework without a reason.

### Phase 3: Security checks

- TLS certificates: read the expiry date on each check, save it, and warn when fewer than a configurable number of days remain (default 14).
- Security headers: check `Strict-Transport-Security`, `Content-Security-Policy`, `X-Content-Type-Options`, `X-Frame-Options` and `Referrer-Policy`. Give each service a simple grade and save which headers are missing.
- Warn when a certificate is about to expire, or when a header has disappeared since the previous check.
- All network traffic goes through the same SSRF protection as the health check. No new ways out.
- The new values are added to the dashboard.

### Phase 4: DevOps

- GitHub Actions: build and test on every push and pull request.
- Dockerfile and docker-compose, with SQLite in a volume. Until phase 6 is done, compose binds to `127.0.0.1`, because the API has no authentication before then.
- Migrations run in a controlled way at startup, or with a documented command. The choice is proposed and justified in this phase.
- README: what the application does, how to run it locally and in Docker, all endpoints, and the security decisions, with a screenshot of the dashboard.

### Phase 6: Ready for a team

- Authentication, for example an API key, so that not just anyone can add services.
- Rate limiting on `/check`.
- A configurable allowlist for internal networks, off by default. A team must actively turn it on, and the README must explain the security risk.
- Structured logging.

### Open design decisions

| Phase | Question | Current thinking |
|---|---|---|
| 2 | Notifications are planned for UP to DOWN and DOWN to UP. A service going from UP to UNKNOWN can mean that its name now points to a private address, which is worth knowing | Not decided. Decide in phase 2 |
| 3 | 3xx counts as UP and is not followed, so `http://example.com` answering 301 gives only the redirect's headers and no certificate. That would produce a misleading grade and false alarms | Grade only a real HTTPS response; anything else is "cannot be assessed". Decide in phase 3 |
| 3 | Save the redirect target (`Location`) and warn when it suddenly points to another domain, because that can mean the site has been hijacked. The value comes from the monitored server and can contain internal addresses, so how it is stored and shown must be decided | Not decided and not built. Starting point: never follow the value, compare only the host, show it as plain text and never as a link, and hide a target that is not a public address. Legitimate moves, such as a new login domain or CDN, can cause false alarms |
| 3 | A certificate is only visible during the TLS handshake, and a reused connection does no new handshake. "On each check" therefore does not work with pooled connections | Either turn off pooling for the probe, which also rechecks the address on every check, or use the most recently seen certificate. The cost of the first option has not been measured |
| 4 | Docker tooling licence | Not settled. Nothing is installed until the licence has been confirmed |
| 5 and 6 | A static page cannot hide an API key | Design the authentication with the dashboard in mind |
| 6 | Rate limiting is planned for `/check`, but `POST /services/{id}/check` and `GET /services/{id}/check` also start network requests, and the locks do not limit checks of different services | Cover all the check routes in phase 6 |
