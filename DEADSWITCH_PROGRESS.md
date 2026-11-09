# DeadSwitch progress

Updated: 2026-11-09.

DeadSwitch is a personal uptime monitor. The backend is **not finished**: see the MVP status below.

## Architecture

.NET 10, ASP.NET Core Web API, EF Core, SQLite. Dependencies point inward only, confirmed from the project files and `using` statements.

| Project | Contains | References |
|---|---|---|
| `DeadSwitch.Domain` | `MonitoredService`, `CheckRecord`, `ServiceStatus`, `StatusChangeRule` | nothing |
| `DeadSwitch.Application` | `MonitoredServiceService`, `ServiceOverview`, `ServiceConfiguration`, `UrlSafety`, `ServiceLocks`, `ServiceValidationException`, `CheckInProgressException`, `NotificationFailedException`, `StatusChange`, interfaces including `INotifier` | Domain |
| `DeadSwitch.Infrastructure` | `DeadSwitchDbContext`, repository, `HttpHealthProbe`, `ProtectedHttpHandler`, `PublicInternetConnector`, `DestinationBlockedException`, `DiscordNotifier`, `DiscordMessage`, `DiscordOptions`, `DiscordWebhookUrl`, migrations | Application, Domain |
| `DeadSwitch.Api` | Startup in `Program.cs`, `ServiceEndpoints`, `RegisterServiceRequest`, `UpdateServiceRequest`, `UtcDateTimeOffsetConverter`, `MonitoringWorker`, `SecurityHeaders`, `CrossSiteRequests`, `LocalOnlyCheck`, and the dashboard in `wwwroot` | Application, Domain, Infrastructure |
| `DeadSwitch.Tests` | Automated tests | Api, and the other projects through it |

## Endpoints

| Method and route | Purpose |
|---|---|
| `GET /` | The dashboard: `index.html`, `dashboard.js` and `dashboard.css` from `wwwroot` |
| `GET /health` | Health of the API itself |
| `POST /services` | Register a service |
| `GET /services` | List services |
| `GET /services/overview` | One row per service for the dashboard, with the response time and failure reason of its latest check. Only reads |
| `GET /services/{id}` | Get one service |
| `PUT /services/{id}` | Replace a service's configuration; every field is required |
| `DELETE /services/{id}` | Delete a service and its history |
| `POST /services/{id}/check` | Manual check, saved to the history |
| `GET /services/{id}/history?skip=&take=` | Check history, newest first; `take` is 1 to 200 |
| `GET /check?url=` | Check a public URL without saving anything |
| `GET /services/{id}/check` | Older compatibility route; also saves to the history |

## API contract

**Times.** Every time in the API is UTC and ends with `Z`, for example `2026-11-03T12:00:00Z`. Parts of a second are written only when they exist. `checkedAtUtc` and `lastCheckedAt` mean when the check started. `lastCheckedAt` used to end with `+00:00`; the moment it describes is the same.

**`POST` and `PUT`.** `POST /services` requires `name` and `url`, and defaults to `intervalSeconds` 60, `timeoutSeconds` 10 and `isEnabled` true. `PUT /services/{id}` replaces the whole configuration, so all five fields are required. A missing or null field gives 400 and changes nothing. A default value on `PUT` could silently enable a paused service, or reset its interval.

**Errors.** Every error below uses the Problem Details format, with fixed texts only.

| Case | Status | `title` | `detail` |
|---|---|---|---|
| Rejected configuration or URL | 400 | `Invalid configuration` | A specific fixed text, never containing the input |
| JSON or a parameter that cannot be read | 400 | `Invalid request` | `The request could not be read.` |
| Wrong content type | 415 | `Invalid request` | `The request could not be read.` |
| A `BadHttpRequestException` with another status code | That code | `Invalid request` | `The request could not be read.` |
| A check or change already in progress | 409 | `Check in progress` | A fixed text |
| A request from another site | 403 | `Request blocked` | `Requests from other sites are not allowed.` |
| Any unexpected exception | 500 | The framework's general title | None |

A `404` has an empty body. A host name that is not allowed gets a `400` from the framework's own host filter, which does not use this format.

**`GET /services/overview`.** Each row has `id`, `name`, `url`, `currentStatus`, `lastKnownStatus`, `lastCheckedAt`, `isEnabled`, `responseTimeMs` and `failureReason`. The last two come from the newest check record, and are null when `lastCheckedAt` is null. That condition matters: a new URL empties `lastCheckedAt` but keeps the history, so without it a row would show the response time of the old address, also after changing back to an earlier URL.

**Security headers.** Every answer from the application has the headers listed under "The dashboard" in the security notes, JSON and errors included.

**`lastKnownStatus`.** A service in the JSON also has `lastKnownStatus`: `UP`, `DOWN` or null when nothing is known for the current URL. It is read-only: `POST` and `PUT` ignore it.

## Configuration

| Key | Environment variable | Meaning |
|---|---|---|
| `Discord:WebhookUrl` | `Discord__WebhookUrl` | The Discord webhook address. A secret: set it with `dotnet user-secrets` in Development, or as an environment variable elsewhere. Never in `appsettings.json` or git. Empty or missing turns notifications off |

**Setting the secret in Windows PowerShell 5.1.** `Read-Host` does not work inside a pasted block, because the next pasted line becomes its answer. The method that worked was to copy the webhook address and run one line that reads the clipboard, writes `secrets.json` as UTF-8 without a BOM and then empties the clipboard. The address never appears in the command, so it stays out of the PowerShell history. The folder name is the `UserSecretsId` in `DeadSwitch.Api.csproj`. The line replaces the file, so any other secret in it is lost. The two lines below are written from that method and have not been run in exactly this form.

```powershell
$u = (Get-Clipboard).Trim(); $dir = "$env:APPDATA\Microsoft\UserSecrets\5b81803a-e9fc-45d0-8fd9-6caba300b0b2"; New-Item -ItemType Directory -Force -Path $dir | Out-Null; [IO.File]::WriteAllText("$dir\secrets.json", (@{ "Discord:WebhookUrl" = $u } | ConvertTo-Json), (New-Object Text.UTF8Encoding($false))); Set-Clipboard -Value " "; Remove-Variable u
```

To check the result, show only the length and whether the address starts correctly. Never print the value, and do not run `dotnet user-secrets list` without hiding it.

```powershell
$v = (Get-Content "$env:APPDATA\Microsoft\UserSecrets\5b81803a-e9fc-45d0-8fd9-6caba300b0b2\secrets.json" -Raw | ConvertFrom-Json)."Discord:WebhookUrl"; "Length: $($v.Length)"; "Starts correctly: $($v.StartsWith('https://discord.com/api/webhooks/'))"; Remove-Variable v
```

Emptying the clipboard does not reach the Windows clipboard history, if that is turned on: the address stays there until it is removed by hand.

**Turning notifications off for one run.** In PowerShell, `$env:Discord__WebhookUrl = ""` removes the variable instead of emptying it, so the address in user secrets stays active. This was measured. Use an argument instead, which was tried with the secret in place and gave the startup line `Discord notifications are turned off`:

```powershell
dotnet run --project DeadSwitch.Api --launch-profile http -- --Discord:WebhookUrl=
```

## Migrations

| Migration | Change |
|---|---|
| `InitialCreate` | Creates `MonitoredServices` |
| `AddCurrentStatusToMonitoredService` | Adds `CurrentStatus` |
| `AddLastCheckedAtToMonitoredService` | Adds `LastCheckedAt` |
| `AddMonitoringHistoryAndScheduling` | Adds `IntervalSeconds`, `TimeoutSeconds` and `IsEnabled`, and creates `CheckRecords` with cascade delete. Existing rows stay paused because `IsEnabled` defaults to false |
| `MakeCurrentStatusRequired` | Makes `CurrentStatus` NOT NULL with the default `UNKNOWN`. Rows with NULL become `UNKNOWN`, other values and all check records are kept. SQLite rebuilds the table for this, with foreign keys turned off around the drop so that the cascade does not delete the history |
| `AddLastKnownStatusToMonitoredService` | Adds the nullable `LastKnownStatus` and copies `CurrentStatus` into it where that is `UP` or `DOWN`, so that an existing service does not lose its next change. `UNKNOWN` stays NULL. `CurrentStatus` cannot do this job: after UP, UNKNOWN, DOWN it no longer remembers that the service was up |

The application does not run migrations at startup. Run `dotnet ef database update` when setting up a database, and after pulling a new migration: without it, reading a service fails because the column is missing. `MakeCurrentStatusRequired` has run in the tests and from empty against a separate database file, but not against the local development database. Run the command there as well.

Status is stored as the text `UP`, `DOWN` or `UNKNOWN`, as it always has been, so old rows did not need rewriting.

`fix/api-contract` needed no migration. Marking a time read from the database as UTC does not change the column or the saved text. This is proven by `ModelMatchesTheMigrations`, by `dotnet ef migrations has-pending-model-changes`, by no change in the `Migrations` folder, and by tests that read the saved text directly.

## Latest verified result

Date: 2026-11-05.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 821 passed, 0 failed, on the final code. Five runs in a row.
- 198 of the tests come from phase 5: 14 test the overview route, 37 the files and the headers, 51 read the dashboard's files as text, 70 test cross-site requests and host names, and 26 test the local-only check. No test runs the JavaScript.
- 470 of the tests come from phase 1: 162 only classify URLs and IP addresses, 17 run the real connection code against a local server, 16 test the probe with a stub handler, 11 run the real probe and handler with a fake DNS, 7 test `ServiceLocks` directly, 134 test unusual host names through the API, the worker and the real probe with a fake probe or a fake DNS, and 123 use the API, the worker or the migrations with a fake probe and an in-memory database.
- 153 come from phase 2: 60 classify webhook addresses, 12 test the rule directly, 29 test the real notifier with a stub handler, 2 test the registered handler against a local server, 13 run the real registration with a stub handler, 36 use the API, the worker or the migrations with a fake notifier, and 1 reads the settings files.
- Without the 68 tests that carry the trait `NameLookupWithoutConnector`: 753 passed.
- No test sends anything to Discord. Real messages are confirmed by the owner's run; see "Manual run".

### Red runs of the dashboard

Each group ran before the real code was written.

| Tests | Run against | Result |
|---|---|---|
| The overview route, 14 tests | An empty list from `GetOverviewAsync` | 11 failed; the other 3 expect an empty list, no check and working routes |
| Files and headers, 37 tests | No files and no headers | 30 failed; the other 7 expect a refused path or a refused `POST`, which also holds with no page. They are proven by the mutations |
| The dashboard's files as text, 51 tests | An empty script | 15 failed: the field names, the element ids and the rules that require something |
| The same tests | An empty script and searches that find nothing | 36 failed. The 3 rows for `eval` stayed green, because the change did not reach that search. The mutation "The eval search removed" below covers them, with 3 failing tests |
| Cross-site requests and host names, 70 tests | The code before the protection | 46 failed. **This confirms the holes were real:** another site's request reached every route, and a foreign host name got an answer |
| The local-only check, 25 tests | A check that accepts everything and does nothing | 16 failed |

One test was unreliable when first written: the application stopped so quickly that the test could no longer read its services. The test now listens for the stop from inside the application. The whole suite then passed eight times in a row.

Host names gave one failure in an existing test: `LocalTestServer` read the API's `appsettings.json` from the test folder and refused `monitored.test`. It now has a setting of its own.

### Mutation check of the dashboard

The code was broken on purpose, one change at a time, running all tests and restoring the file from a copy. The changes were never committed. All of them ran against the fake probe and fake notifier, none opened a port, and none touched the SSRF protection.

| Change | Failing tests |
|---|---|
| CSP header removed | 19 |
| `nosniff` removed | 16 |
| `X-Frame-Options` removed | 16 |
| `Referrer-Policy` removed | 16 |
| CSP gets `unsafe-inline` in `script-src` | 17 |
| CSP gets `unsafe-inline` in `style-src` | 17 |
| CSP gets `unsafe-eval` | 17 |
| `frame-ancestors` removed | 16 |
| `default-src` allows `self` | 16 |
| `connect-src` allows every https address | 17 |
| `base-uri` removed | 16 |
| `X-Frame-Options` becomes `SAMEORIGIN` | 16 |
| `Referrer-Policy` becomes `unsafe-url` | 16 |
| Headers set directly and not in `OnStarting` | 2, the 500 answers |
| `SecurityHeaders` not registered | 19 |
| `SecurityHeaders` after the exception handler | 0; see below |
| `UseDefaultFiles` removed | 16 |
| `UseStaticFiles` removed | 50 |
| `no-cache` removed | 4 |
| `textContent` becomes `innerHTML` | 1 |
| `outerHTML`, `insertAdjacentHTML`, `document.write`, `eval`, `new Function` or `setAttribute` added, one at a time | 1 each |
| The `eval` search removed from the test | 3 |
| A link, an `href` or `window.open` added, one at a time | 1 each |
| The fetch uses `POST` | 1 |
| A second fetch added | 1 |
| The fetch goes to another route | 1 |
| An outside address in the script | 1 |
| A field name changed in the script | 1 |
| An element id changed in the script | 1 |
| A character that is not ASCII in the script | 1 |
| Inline script, `style` attribute, style block or event attribute in the page, one at a time | 1 each |
| Script from another site | 2 |
| A button, a form or a link in the page, one at a time | 1 each |
| The page has no script, or no stylesheet | 1 each |
| An id missing in the page | 1 |
| `@import` or `url()` in the styles | 1 each |
| The `LastCheckedAt` condition removed | 2 |
| The second newest record is used | 6 |
| The response time is not filled in | 6 |
| The failure reason is not filled in | 2 |
| Overview route removed | 22 |
| `same-site` allowed | 9 |
| `cross-site` allowed | 11 |
| Only `cross-site` and `same-site` refused, unknown values allowed | 2 |
| The `Sec-Fetch-Site` check removed | 22 |
| `none` refused | 1 |
| The `Origin` check removed | 14 |
| The `Origin` check only compares the start | 2 |
| The `Origin` check ignores the scheme | 1 |
| A blocked request goes on anyway | 36 |
| The blocked text changed | 2 |
| `CrossSiteRequests` not registered | 36 |
| `CrossSiteRequests` before the files | 4 |
| `AllowedHosts` back to `*` | 10 |
| `AllowedHosts` without `127.0.0.1` | 2 |
| `AllowedHosts` without `[::1]` | 2 |
| `AllowedHosts` allows `*.example` | 10 |
| Every address counts as local | 12 |
| An address that cannot be read counts as local | 5 |
| The check does not stop the application | 3 |
| The check does not set the exit code | 3 |
| The check does not log | 3 |
| The check is not registered | 3 |

The row with 0 failing tests: the callback from `OnStarting` is kept when the exception handler empties the headers, so the place in the pipeline cannot be seen in any answer the tests produce. `SecurityHeaders` stays first because it is then clear that nothing comes before it.

A text search proves little about the page's behaviour. The rows for `innerHTML` and the like show that the search notices the word, not that the page is safe.

### Red runs of the notifications

Each group ran against an empty version of the code, before the real one was written.

| Tests | Run against | Result |
|---|---|---|
| The rule, 12 rows | `ShouldNotify` always false | 4 failed: every row that should notify |
| Webhook addresses, 58 rows | `IsAllowed` always true | 53 failed; 5 could not be red, because they expect an accepted or empty address, or test the fixed text |
| The real notifier, 29 tests | Sends plain text, with no checks | 27 failed; the 2 that passed test a successful answer |
| Notifications from checks | `CheckAsync` sends nothing | 21 failed |
| The address in the log | Sending on, `RemoveAllLoggers` missing | 4 failed: the framework wrote the whole address with its token to the log. This confirms the leak is real |

### Mutation check of the notifications

The code was broken on purpose, one change at a time, running all tests and restoring the file from a backup. The changes were never committed. All of them ran against a fake notifier, a stub handler or `127.0.0.1`, and no mutation gave the notifier a client of its own, because that would send a real request.

| Change | Failing tests |
|---|---|
| The rule notifies on UP to UNKNOWN | 4 |
| The rule does not notify on null to DOWN | 18 |
| The rule notifies on null to UP | 14 |
| The rule notifies on null to UNKNOWN | 3 |
| The rule notifies on every check | 19 |
| `PreviousStatus` is the new status | 10 |
| The message does not say "first check" | 1 |
| UNKNOWN overwrites `LastKnownStatus` | 6 |
| `LastKnownStatus` is never written | 18 |
| A new URL does not empty the status | 3 |
| A new name empties the status | 1 |
| The migration does not copy the value | 1 |
| The notification comes before the save | 3 |
| The notification comes inside the lock | 1 |
| No `catch (Exception)` | 2 |
| No `catch (NotificationFailedException)` | 3 |
| The exception object goes to the logger | 2 |
| A change to UNKNOWN is not logged | 1 |
| `RemoveAllLoggers` removed for Discord | 4 |
| The inner exception is kept | 1 |
| The failure text holds the exception's text | 2 |
| The failure text holds the address | 5 |
| No `catch (HttpRequestException)` | 2 |
| No timeout | 1 |
| The default timeout is 100 seconds | 1 |
| The answer's status is not checked | 5 |
| `wait=true` removed | 2 |
| `allowed_mentions` removed | 5 |
| `parse` holds `everyone` | 5 |
| The name is not cut | 3 |
| The URL is not cut | 2 |
| The cut ignores emoji | 1 |
| A backtick is not replaced | 1 |
| A control character is not replaced | 3 |
| The query is kept | 1 |
| No address does not return at once | 4 |
| A plain handler instead of the protected one | 2 |
| `Validate` removed | 5 |
| `ValidateOnStart` removed | 0; see below |
| The startup line says the opposite | 3 |
| A validation failure shows the address | 7 |
| Address rule removed: only plain ASCII | 0; see below |
| Address rule removed: the same text as `Uri` writes | 4 |
| Address rule removed: https | 2 |
| Address rule removed: port | 1 |
| Address rule removed: credentials | 1 |
| Address rule removed: query | 2 |
| Address rule removed: fragment | 1 |
| Address rule removed: host | 9 |
| `discordapp.com` not allowed | 1 |
| Address rule removed: start of the path | 1 |
| Address rule removed: exactly two parts | 4 |
| Address rule removed: digits | 3 |
| Address rule removed: plain token | 2 |
| `TestApp` does not set the address | 12 |
| `TestApp` keeps the real notifier | 17 |

The rows with 0 failing tests, which stay on purpose as defence in depth:

- `ValidateOnStart`: `Program.cs` reads the options right after `Build` for the startup line, which runs the same validation.
- Only plain ASCII: every such address is also stopped by the comparison with what `Uri` writes, or by the token rule.

The https row first gave 0 failing tests, which was a real gap in the tests: `http://discord.com:443/...` passes the port rule and only the scheme rule stops it. Two rows were added, and that row was then run again. The other rows ran before those two were added.

### Red runs of the API contract

Each test was run against the old code before the fix.

| Test | Old result | Expected |
|---|---|---|
| History time, read as raw JSON (3 tests) | `2026-11-03T12:00:00`, no `Z` | With `Z` |
| `lastCheckedAt` on every route and from saved rows (4 cases) | `+00:00` | `Z` |
| 26 unusual URLs on `/check`, `POST`, `PUT`, `UrlSafety`, the real probe and the worker (156 cases) | Accepted, and 6 of them made the real probe throw | 400, or UNKNOWN from the probe |
| `ArgumentException` holding a secret text | 400 showing the text | 500 without it |
| Unreadable JSON and parameters, Development | 500 | Fixed 400 |
| Unreadable JSON and parameters, Production | 400 with an empty body | Fixed 400 |
| Wrong content type, both environments | 415 with an empty body | Fixed 415 |
| `PUT` without `isEnabled`, `intervalSeconds` or `timeoutSeconds`, or with a misspelled `isEnabled` | 200, and the service changed | 400, nothing changed |
| `PUT` without `name` or `url`, or with `isEnabled` null | 400 with another text | 400 with the exact text |

Some tests could not be red, because the behaviour was already right: `PUT` to a forbidden URL, 500 without text for an `InvalidOperationException`, the exact validation texts, explicit `true` and `false` on `PUT`, the default values on `POST` and the saved text in the database. These are proven by the mutations below.

### Mutation check of the API contract

The code was broken on purpose, one change at a time, running all tests and restoring the file from a backup. The changes were never committed. All of them ran against the fake probe, the stub or a fake DNS, and none of them touched `PublicInternetConnector`, `ProtectedHttpHandler` or `ConnectCallback`. Two small changes to the tests came after the run: escape codes for invisible characters, and one explicit type. Neither changes what is tested.

| Change | Failing tests |
|---|---|
| `SpecifyKind` removed | 4 |
| `SpecifyKind` with `Unspecified` | 4 |
| Time converter not registered | 4 |
| Converter writes the value as it is | 4 |
| Converter writes `value.DateTime` | 5 |
| Converter marks a local time as UTC, which gives the wrong moment | 1 |
| The time is read after the probe | 3 |
| The time is saved with `ToLocalTime` | 5 |
| No `catch (ServiceValidationException)` | 112 |
| `catch (ArgumentException)` back, showing its message | 1 |
| The validation `detail` is another text | 16 |
| The first URL message contains the URL | 3 |
| The second URL message contains the URL | 3 |
| Name check removed | 4 |
| Interval check removed | 2 |
| Timeout check removed | 2 |
| `Validate` removed in `UpdateAsync` | 36 |
| `Validate` removed in `RegisterAsync` | 40 |
| `UrlSafety` removed from `ServiceConfiguration` | 68 |
| `UrlSafety` removed from `/check` | 28 |
| The probe does not catch `ServiceValidationException` | 54 |
| No `catch (BadHttpRequestException)` | 18 |
| `detail` is `ex.Message` | 18 |
| `detail` is the inner exception's message | 18 |
| `detail` is `ex.ToString()` | 18 |
| The status is always 400 | 2 |
| `ThrowOnBadRequest` removed | 8, all in Production |
| `ThrowOnBadRequest` set to false | 16 |
| `UseExceptionHandler` removed | 3 |
| The 500 answer gets the exception's text | 3 |
| A 415 does not get the fixed answer | 2 |
| `IsEnabled` falls back to true on `PUT` | 4 |
| `IsEnabled` falls back to false on `PUT` | 4 |
| `IntervalSeconds` falls back to 60 on `PUT` | 1 |
| `TimeoutSeconds` falls back to 10 on `PUT` | 1 |
| The `Name` check on `PUT` removed | 1 |
| The `Url` check on `PUT` removed | 1 |
| `PUT` uses `RegisterServiceRequest` | 8 |
| `POST` uses `UpdateServiceRequest` | 3 |
| `Apply` does not write `IsEnabled` | 3 |
| The probe does not catch `HttpRequestException` | 15 |
| `catch (ArgumentException)` back in the probe | 0; see below |
| `DnsSafeHost` instead of `IdnHost` | 156 |
| `UriFormatException` not caught | 36 |
| A host that cannot be converted is not rejected | 36 |
| The IP check is decided from `HostNameType` | 85 |
| No handling of a trailing dot | 21 |

The row with 0 failing tests: nothing that was found can throw an `ArgumentException` that reaches this catch, because the framework wraps failures from the connection step in `HttpRequestException`. The catch is therefore not needed, and its absence cannot be observed. This does not prove that no other case exists.

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
- A bug in the history's time format, fixed since then in `fix/api-contract`.

Done by the owner on 2026-11-04:

- `GET /check?url=http://github.com` returned UP with status code 301. This is a redirect from a real website through the whole chain, and the status code shows that the redirect was not followed.
- The background worker against a real database, with its own database file: `intervalSeconds` = 10 against `https://github.com` with a poll of 1 second. 16 gaps between checks: minimum 9.975 s, maximum 10.001 s, mean 9.9985 s, all UP with status code 200. A check with a response time of 104 ms still gave a gap of 10.001 s. This is one run with one interval and one poll.

Run against the real server (Kestrel) on 2026-11-05, in Windows PowerShell 5.1 against `localhost` in Development, with its own database file and the worker turned off. Only routes that send no traffic out were used: malformed JSON and `GET /check` without a URL gave the fixed 400, a wrong content type gave the fixed 415, `PUT` without `isEnabled` gave 400 and the service stayed paused, and unusual host names on `POST`, `PUT` and `GET /check` gave 400.

Done by the owner on 2026-11-05, against the real server (Kestrel) in Windows PowerShell 5.1, with its own database file: `POST /services/{id}/check` against `https://example.com` gave UP with status code 200. `checkedAtUtc` in the answer, the same field in `GET /services/{id}/history` and `lastCheckedAt` in `GET /services/{id}` all held the same time, `2026-11-05T07:15:53.2904021Z`, with `Z`.

Run against the real server (Kestrel) on 2026-11-05 with its own database file and no checks, so that nothing left the machine: with no webhook address the startup line says that notifications are off, and an invalid address in `Discord__WebhookUrl` stops the application with the fixed text and without showing the address. The `POST` and `PUT` commands for the manual run were tried in Windows PowerShell 5.1.

Done by the owner on 2026-11-05 against a real Discord channel, on the real server (Kestrel) in Windows PowerShell 5.1, with its own database file and the worker turned off. The service was named `@everyone @here test <@1> [click](https://example.com)` and had the URL `https://httpbin.org/delay/3?secret=abc`.

- The startup line said `Discord notifications are turned on.`
- The first check was UP: no message.
- `timeoutSeconds` 1 gave DOWN (`Check timed out`): exactly one message, `is DOWN (was UP)`.
- Another check was DOWN: no new message.
- The new faulty URL `https://does-not-exist.invalid/` gave DOWN: exactly one message, `is DOWN (first check)`.
- In Discord: no ping, `@everyone` and `@here` were shown as inline code, `[click](...)` was no link, `?secret=abc` was not shown, and the times ended with `Z`.
- The log held `Discord notification sent` and no match for `api/webhooks`.

This also confirms that a real address under `/api/webhooks/` on `discord.com` works, and that HTTPS through the connector reaches Discord. It is one run with one channel, and it shows that Discord obeyed the empty `parse` in these messages.

Done by the owner on 2026-11-05: `dotnet test` with the real webhook secret set in user secrets gave 623 passed, and no new message arrived in the Discord channel. This is one run.

Run against the real server (Kestrel) on 2026-11-05 for the dashboard, with its own database file, the worker off, Discord off and no checks, so that nothing left the machine:

- `Get-NetTCPConnection` showed the application listening on `127.0.0.1` and `::1` only.
- `/`, `/dashboard.js`, `/dashboard.css`, `/services/overview` and a 404 all had the four security headers. The three files had `text/html`, `text/javascript` and `text/css`.
- `Host: evil.example` gave 400, and `Sec-Fetch-Site: cross-site` gave 403 with the fixed text.
- A service named `<img src=x onerror=alert(1)>` was saved, and came back unchanged from `/services/overview`.
- Listening on a Unix domain socket made `LocalOnlyCheck` write its text and stop the application with exit code 1. This is the real server reporting a real address that is not loopback, without opening a port on the network.

These requests came from `curl` and Windows PowerShell 5.1, with the headers set by hand. They show the server, not what a browser sends. The run below is in a real browser.

Done by the owner on 2026-11-05 in Chrome against the real server (Kestrel), with its own database file, the worker off and Discord off through the argument `-- --Discord:WebhookUrl=`:

| Step | What was done | Result |
|---|---|---|
| A | `Get-NetTCPConnection` | Listening on `127.0.0.1` and `::1` only |
| B | A service named `<img src=x onerror=alert(1)>` and a URL containing `<img ...>` | Both shown as text, no alert, `document.images.length` gave 0, and both rows had `Paused` |
| C | Response headers of the document | CSP with the exact value, `nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, `Cache-Control: no-cache`, and 304 on reload |
| D | `eval` in the page | **Not run** |
| E | `innerHTML` set to `<img src=x onerror=alert(3)>` in the Console | No alert and two CSP errors: the inline event handler blocked by `script-src 'self'`, and the image blocked by `default-src 'none'` |
| F | `POST /services/{id}/check` with the page open | The row showed UP, UP, 117 ms and local time 11:11:12 (UTC 10:11:12Z), without a reload |
| G | The server was stopped, then started again | The Console showed `ERR_CONNECTION_REFUSED` on `/services/overview` every 10 seconds, so the page kept trying. After the restart the page updated again. The failure message was **not seen** on the screenshot, because DevTools covered the page |
| H | A form `POST` and a link `GET` from a `file://` page | Both gave 403 `Request blocked` with the fixed text. The history held 1 record afterwards, the one from step F |
| I | `Host: evil.example` | 400 |

This is one run in one browser. Step E was typed by hand in the Console, so it shows that the browser obeys the policy, not that the page's own code is free of mistakes. Step H shows the 403 from a `file://` page, and which of `Sec-Fetch-Site` and `Origin` caused it was not read.

### Not run manually

Proven by tests only, and not confirmed against the real Discord:

- The DOWN to UP message.
- A wrong token: the check is saved and the log shows the status code.
- Notifications turned off with an empty address, during a run with a status change. The startup line and the application starting were confirmed on the real server without any check.

Not confirmed on the screen in the browser run above:

- That `eval` is refused by the policy (step D was not run).
- That the failure message becomes visible when the API stops, and goes away when it returns. The page kept trying and updated again, but the message itself was hidden behind DevTools on the screenshot.
- Which headers the browser sends to `localhost`.

### Never run so far

- The application listening on a real network address. It would open a port on the network, so only the Unix socket and a test server that claims an address are used.
- A real attack from a public site, and real DNS rebinding. The tests send the headers and host names those would produce.

- The API on the real server in Production.
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
- Every time in the API is written in UTC with `Z` on every route, and is the start of the check. Old rows get `Z`, are sorted correctly together with new ones, and keep their saved text.
- `PUT` requires all five fields; a missing, null or misspelled field gives 400 and changes nothing. Explicit values work, and `POST` keeps its default values.
- `PUT` to a forbidden URL gives 400 and changes nothing, also when the service does not exist.
- Unusual host names, such as fullwidth forms and other dot characters, give 400 on `GET /check`, `POST` and `PUT`. The real probe gives UNKNOWN before any name lookup, and the worker saves UNKNOWN and then waits for the interval.
- Failures from the DNS step, including `ArgumentException`, give DOWN through the real handler.
- Unreadable JSON and parameters give a fixed 400, and a wrong content type a fixed 415, in both Development and Production on the test server.
- Validation errors have exact fixed texts and never repeat the URL. A 500 shows nothing from the exception. No error answer contains a stack trace, a class name, a path or a JSON position, as far as a list of forbidden texts can show.
- A notification is sent on UP to DOWN and DOWN to UP, and on DOWN when nothing is known yet, from `POST`, the compatibility route and the worker. A repeated status, a first UP and UNKNOWN send nothing. UNKNOWN does not change what is remembered. A new URL forgets the old status and a new name does not. `GET /check` never notifies.
- A notification is sent after the check is saved and after the lock is released. A failed save or a cancelled check sends nothing.
- A failed notification keeps the check, is logged, and the worker goes on to the next service.
- What is sent to Discord: a POST with `wait=true` and an empty `parse`, also with `@everyone` in the name; the name and URL as inline code, cut within the limit, with no query. Any answer that is not a success gives a fixed failure after one request, and a Discord that never answers gives one after the timeout.
- The webhook address is only accepted in its exact form, is checked at startup, and is missing from every log line, exception text and API answer the tests can see, at the lowest log level.
- Without an address the application works as before, in Development and Production, and sends nothing.
- The ordinary tests use a fake notifier, also when the environment holds another address.

- The dashboard's files are served with the right content type in Development and Production, must be checked again before reuse, and nothing outside `wwwroot` is served.
- Every answer has the exact security headers: the files, JSON, a 400, a 404 and a 500. The policy allows no inline code, no `eval` and no outside address.
- The overview gives each service its own newest result, nothing for a URL that has not been checked, also after changing back to an earlier URL, and starts no check.
- As text only: the script holds none of the listed words that run text, creates no links, has one `fetch` to the overview, and reads every field. The page has no inline code and no controls, and no file loads anything from outside or holds a character that is not ASCII. The searches are themselves tested on bad code.
- A request from another site is blocked on the API's routes and changes nothing, and so is an unknown `Sec-Fetch-Site` value or an origin that is not exactly ours. The owner's own requests, and programs that send neither header, work.
- A body that a form can send gets 415 and creates nothing, and no answer has a CORS header.
- A host name other than `localhost`, `127.0.0.1` or `[::1]` gets 400.
- An address that is not loopback stops the application with exit code 1 and a log line, through a test server that only claims the address.

Dependency injection lifetimes were reviewed by reading the code only.

## MVP status

| Item | Status | What remains |
|---|---|---|
| CRUD and validation | Done | Proven by tests. `POST` ignores unknown fields; see the known limitations |
| Manual check and history | Done | |
| Background worker | Done | Loop, error isolation, shutdown and interval are proven with a fake clock, and one manual run against a real database confirms interval 10 with a poll of 1 second. Intervals 12 and 17 are proven with the fake clock only |
| No overlapping checks | Partial | Protects a single process only; see the lock limitation below |
| Status UP / DOWN / UNKNOWN | Done | Proven by tests. A 3xx from a real website is confirmed by a manual run |
| History endpoint | Done | Proven by tests, with `Z` on every time, and confirmed by a manual run after a real check |
| SSRF protection | Done for HTTP | Proven by tests against a local server. HTTPS through the connector is untested; see the security notes |
| Tests for all of the above | Done for phase 1 | The limits are listed under "Not proven by a test" and "Never run so far" |
| Notification on UP to DOWN and DOWN to UP | Done | Both directions are proven by tests with a fake notifier and a stub. A manual run confirms real messages for UP to DOWN and for DOWN on a new URL. DOWN to UP has not been run manually |
| `INotifier` with a Discord webhook | Done | Proven by tests, and confirmed by a manual run against a real channel. A wrong token from Discord and notifications turned off have not been run manually with a real send |
| Webhook address outside git | Done | Proven by tests of the settings files and the log. A manual run confirms the secret is read, and that the log holds no address after a real send |
| Dashboard (phase 5, not part of the MVP) | Done | The server side is proven by tests and a run against the real server, and the page by the owner's run in Chrome. Step D (`eval`) and the visible failure message are not confirmed on the screen |
| README | Missing | |
| GitHub Actions | Missing | Waits for the owner's approval |

## Notification rules

`StatusChangeRule` compares `LastKnownStatus` with the new status. Worker and manual checks share `MonitoredServiceService.CheckAsync`, so they behave the same.

| Last known | New | Notification | Why |
|---|---|---|---|
| UP | DOWN | Yes | |
| DOWN | UP | Yes | |
| UP or DOWN | The same | No | Nothing changed |
| Any | UNKNOWN | No | No request was sent, so nothing was learned. A change to UNKNOWN is logged as a warning |
| Nothing: a new service or a new URL | DOWN | Yes | A misspelled URL that is down from its first check would otherwise never give an alarm |
| Nothing: a new service or a new URL | UP | No | Nothing to warn about |
| Nothing: a new service or a new URL | UNKNOWN | No | The status stays empty |

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
- `POST` ignores fields it does not know, so a misspelled `"isEnable": false` gives an enabled service. `PUT` catches this, because the real field is then missing.
- If an unexpected kind of exception ever gets out of the probe, a manual check gives 500 and the worker logs the error and tries again on every pass, because the service stays due. None is known now.
- `TimeoutSeconds` edited by hand to a value the API would reject throws outside the probe's `try`, which gives 500. The API itself never saves such a value.
- A check record saved by hand with another time zone would be read as UTC. The application has only ever written UTC.
- Code in the framework that answers before the middleware, such as Kestrel's own HTTP errors, does not use the fixed error texts. This has not been tested.
- How the time fields are described in the OpenAPI document with the converter in place has not been checked.
- A value in a status column other than `UP`, `DOWN` or `UNKNOWN` makes reading that row fail. The application only writes these three, so this needs a manual edit of the database.
- A notification that fails, or a crash between the save and the send, loses that alarm. There is no retry, and the status will not change again until the service does.
- A 429 from Discord is logged but not waited out. How rate limits work for webhooks has not been read or tested.
- A service that keeps switching gives one message per change, and one timeout is enough for a DOWN message.
- Two checks shortly after each other could in theory send their messages in the wrong order, because they are sent outside the lock.
- A status change can make a manual check answer up to 5 seconds later. During a Discord outage, many changes delay a worker pass by up to 5 seconds per service.
- A name that starts to point to a private address gives UNKNOWN, which is only seen in the log. It could be worth a notification of its own in phase 3.
- A service already UNKNOWN at its first check logs no warning, because nothing changed.
- `GET /services/overview` makes one database read per checked service, on every update. This is simple and fine for a personal tool, but it would need one joined query for many services.
- Two reads can in rare cases give a record newer than the status beside it. The next update corrects it.
- A name with characters that change the writing direction can look odd in the table. It is shown as text and cannot run anything.
- The dashboard shows the whole URL, including any query. The page is local and shows the owner's own data, while Discord is a third party and gets no query. A screenshot for the README must not show a URL with a secret in it.
- The dashboard cannot be tested without a browser. Playwright would need new packages and a downloaded browser, so it has not been added.
- The browser asks for `favicon.ico`, which does not exist and is refused by the policy, so one line in the console is expected.
- `LocalOnlyCheck` runs after the server has started, so a wrong port is open for a moment. Enabled services can start a check in that moment as well.

## Code style exceptions

These parts do not yet follow the project's plain style, on purpose:

- `PublicInternetConnector.ConnectAsync` keeps a `try`/`catch` around the socket instead of `using`. The socket must be closed on failure but stay open when it is handed to the stream.
- `PublicInternetConnector.ConnectAsync` keeps an explicit check of an empty DNS answer, although the last line of the method would also stop one. This is defence in depth: it states the intent, and the protection must not depend on how the rest of the method is written. The tests therefore stay green if the check is removed.
- `MonitoringWorker` keeps three `catch ... when` filters. Replacing them would duplicate the log lines.
- The queries in the repository stay as LINQ, because EF Core translates them to SQL.
- `UtcDateTimeOffsetConverter` is a class with an abstract `Read` method that no request uses, because `System.Text.Json` has no setting for the time format.
- `UrlSafety.GetConnectionHost` uses `try`/`catch` and returns null. `Uri.IdnHost` has no version that avoids the exception, and a host that cannot be converted must be rejected.
- `Program.cs` checks for 415 after the next middleware instead of catching an exception, because the framework throws nothing for a wrong content type.
- `DiscordWebhookUrl.IsAllowed` has many separate checks, some of which overlap. They are kept on purpose: the protection must not depend on how the other checks happen to work. The mutation check shows that removing the ASCII check changes no test.
- `ValidateOnStart` is kept for Discord although the startup line reads the options anyway, for the same reason.
- `MonitoredServiceService.SendNotificationAsync` catches `Exception` but does not pass it to the logger, because its text could hold the webhook address.
- `SecurityHeaders` sets its headers in a callback from `OnStarting` instead of directly, because the exception handler empties the headers before it writes a 500.
- `CrossSiteRequests` has two checks that overlap on purpose: not every browser sends `Sec-Fetch-Site` to `localhost`, and the protection must not depend on one header.

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
- There is no authentication or rate limiting. `GET /check` is open, and `GET /services/{id}/check` writes data. Other sites are now stopped, but any program on this machine still has full access. This is acceptable on localhost only and must be solved before any deployment.
- Internal hostnames pass registration. They are stopped at connection time, after the DNS lookup has already been made.

Error answers:
- Only `ServiceValidationException` and `CheckInProgressException` show their message, and both hold fixed texts. The middleware no longer catches `ArgumentException`, so an internal error cannot become a 400 that shows its text.
- The tests of leaks search for a list of forbidden texts. They prove exact answers for the known cases, not for every possible failure. Only the answer is tested: exception texts in the log are intended.

The Discord webhook:
- The address is a secret, because its token is all anyone needs to post to the channel. User secrets are a plain unencrypted file in the user's profile: they protect against git, not against someone who can read that profile. If the address leaks, delete the webhook in Discord and create a new one.
- There is no new way out. The Discord client uses the same `ProtectedHttpHandler` as the probe, so no redirect can move the message, no proxy sees it, and a private address stays blocked. The difference is the destination: the probe goes where a user says, while this client only goes to the address checked at startup, and nothing a user sends can change it. User data only reaches the JSON body.
- The automatic logging of `HttpClient` writes the whole address, so it is removed for this client. A red test confirmed the leak.
- `NotificationFailedException` holds fixed texts and no inner exception, and an unexpected exception is logged by type only.
- The name and URL come from the user. Pings are turned off with an empty `parse`, and inline code stops markdown, links and link previews. A query is removed, because it can hold a token that Discord has no reason to see.

Checked against Discord's documentation, on the webhook and message pages only:
- `content` is limited to 2000 characters. How they are counted is not stated, so the limits here leave a wide margin: the longest message also fits in 2000 bytes.
- `allowed_mentions` is a parameter of Execute Webhook, and `{"parse": []}` is the documented way to suppress all mentions. The example shows a normal message.
- The answer is a message or 204 depending on `wait`. It defaults to false, and then "a message that is not saved does not return an error". That is why `wait=true` is sent.
- The `/slack` and `/github` variants exist, which is why the address rule rejects them.
- The pages say nothing about `/api/` in the path or about rate limits. The manual run confirms that a real address under `/api/webhooks/` on `discord.com` works. Rate limits remain unread and untested.

The dashboard:

It only reads. There is no login, so a button would give anyone on the machine an easier way to change data, and writing from a page needs a CSRF protection that belongs with the login. With one `fetch` that only reads, a test can also show that the script does not write.

Names, addresses and failure reasons come from users and servers, and reach the page through `textContent` only. Addresses are never links. Failure reasons are fixed texts or `HTTP` and a status code today, but they are treated the same way.

| Header | Value | Why |
|---|---|---|
| `Content-Security-Policy` | `default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'` | Everything is forbidden except our own files. Inline scripts, `onerror`, `eval` and `style` attributes do not run, and an injected image does not load. This is the second layer, if a later change ever gets the first one wrong |
| `X-Content-Type-Options` | `nosniff` | The browser must not guess the file type, so JSON or text cannot run as a script |
| `X-Frame-Options` | `DENY` | The page cannot be placed in a frame, which stops clickjacking. It means the same as `frame-ancestors`, for browsers that do not read that part |
| `Referrer-Policy` | `no-referrer` | No address from this application is passed on to another site |

`Strict-Transport-Security` is left out, because the page runs over HTTP on `localhost`. It belongs with HTTPS in a deployment.

Local only until the login:

`launchSettings.json` says `http://localhost:5201`, but that is a setting and no protection. It is only read by `dotnet run` and Visual Studio, and `--urls` or an environment variable can change the address.

| Layer | Stops | Does not stop |
|---|---|---|
| Listening on loopback | Other machines | Other programs and users on this machine |
| `LocalOnlyCheck` reads the real addresses and stops the application | A wrong address by mistake | A port open for a moment before it stops. `netsh portproxy`, an SSH tunnel or a reverse proxy cannot be seen from inside |
| `AllowedHosts` set to `localhost;127.0.0.1;[::1]` | DNS rebinding | A request that sends `Host: localhost` |
| The login in phase 6 | All of the above | Does not exist yet |

Requests from other sites:

There are no cookies, so classic CSRF has no login to abuse. But being on this machine is the only permission, and a page in the owner's browser is on this machine.

| From another site | Before this phase | Now |
|---|---|---|
| `GET /services/{id}/check` with an image tag | Worked if the id was known. Wrote history, sent a request and could send an alarm | 403 |
| `POST /services/{id}/check` with a form | The same, as there is no body for the content type to stop | 403 |
| `GET /check?url=` with an image tag | Worked with no id at all. The machine contacted any public address; private ones stay blocked | 403 |
| `POST /services` with a form | 415, as only JSON is accepted | 403, and still 415 without the block |
| `PUT` and `DELETE` | The browser needs permission first and never gets it, as there is no CORS | 403 |
| Reading an answer | The browser refuses, as there is no CORS | 403 |
| Everything above, through DNS rebinding | **Worked:** the browser counts it as the same origin, so a site could read, create, change and delete | 400 from the host filter |

Reading `Sec-Fetch-Site` and `Origin` protects against browsers only. A program sends neither header and is let through, and it can send what it wants in them anyway. The block sits after the static files, because they hold no data and a link to the page must work. Browsers have started to limit requests from public sites to local addresses, but this differs between browsers and nothing here depends on it.

Waiting for phase 6: the login, with a CSRF token if it uses cookies; rate limiting on all check routes; and removing the old `GET` route that writes, which is a contract change with tests of its own.

Untested assumption:
- `168.63.129.16` (an Azure platform address) counts as public. This matters if the API is hosted on Azure.

## Roadmap

DeadSwitch is growing into a tool that watches both the availability and the security configuration of web services, and says so when something goes wrong. The goal is not to sell it. The goal is a quality that a real development team would want to use internally. The code stays simple and readable: the difficulty belongs in the design decisions, not in the syntax.

The phases are done in the order below: 1, 2, 5, 3, 4, 6. The dashboard comes early so that every new feature is visible in it at once. The MVP still covers phases 1 to 4; the dashboard is built earlier but is not part of the MVP. Each task is one branch, with a plan approved first.

**Next step:** phase 3, one task at a time; see "Phase 3: Security checks".

### Phase 1: Stable foundation

1. Done: `test/ssrf-real-handler`: move the handler configuration to Infrastructure and test redirects, private addresses and DNS rebinding against a local test server. Then bring the security code into the plain style, protected by those tests.
2. Done: `fix/service-locks`: one lock per service, with a test that different services do not block each other.
3. Done: `fix/status-enum`: add `ServiceStatus`. A timeout is DOWN and a blocked address is UNKNOWN.
4. Done: `fix/redirect-status`: 3xx is UP, without following the redirect. Document the limitation.
5. Done: `test/worker-loop`: test error isolation, shutdown and worker against manual check. A manual run had shown that `intervalSeconds` = 10 usually became 15 seconds. The cause is confirmed and fixed; see "Red runs of the check interval". A manual run against a real database confirms it at interval 10; see "Manual run".
6. Done: `fix/api-contract`: dedicated validation exception, 400 on malformed JSON, `PUT` that requires every field, and UTC times with `Z`. It also made `UrlSafety` judge the same name as the connection; see the security notes.

**Contract change from `test/worker-loop`:** `checkedAtUtc` in the check responses and the history means when the check **started**, not when it ended. The same applies to `lastCheckedAt` on the service and in the compatibility route. Check records saved before this change keep their end time. A record's end time is not stored, but it can be estimated by adding `responseTimeMs`.

### Phase 2: Notifications

Built on `feat/discord-notifications` and confirmed by the owner's manual run, except for the parts listed under "Not run manually":

- Notify on a status change, UP to DOWN and DOWN to UP, never on every check. See "Notification rules".
- First channel: a Discord webhook, behind an `INotifier` interface in Application, so that more channels can be added without touching the core logic.
- The webhook URL is a secret. It belongs in user secrets or environment variables, never in `appsettings.json` or git.
- Tests use a fake notifier or a stub handler and make no real calls.

### Phase 5: Dashboard

Built on `feat/dashboard` and merged. The server side is verified by tests, and the page by the owner's run in Chrome, except for the parts listed under "Not run manually".

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
| 3 | A service going to UNKNOWN can mean that its name now points to a private address. Phase 2 decided on a warning in the log and no message, because nothing was learned about the service | Consider a notification of its own in phase 3 |
| 3 | 3xx counts as UP and is not followed, so `http://example.com` answering 301 gives only the redirect's headers and no certificate. That would produce a misleading grade and false alarms | Grade only a real HTTPS response; anything else is "cannot be assessed". Decide in phase 3 |
| 3 | Save the redirect target (`Location`) and warn when it suddenly points to another domain, because that can mean the site has been hijacked. The value comes from the monitored server and can contain internal addresses, so how it is stored and shown must be decided | Not decided and not built. Starting point: never follow the value, compare only the host, show it as plain text and never as a link, and hide a target that is not a public address. Legitimate moves, such as a new login domain or CDN, can cause false alarms |
| 3 | A certificate is only visible during the TLS handshake, and a reused connection does no new handshake. "On each check" therefore does not work with pooled connections | Either turn off pooling for the probe, which also rechecks the address on every check, or use the most recently seen certificate. The cost of the first option has not been measured |
| 4 | Docker tooling licence | Not settled. Nothing is installed until the licence has been confirmed |
| 4 | Inside a container the application must listen on `0.0.0.0`, which `LocalOnlyCheck` refuses. Compose binds to `127.0.0.1` on the host, which the check cannot see | The check must be turned off explicitly in the container. No such setting exists yet. `AllowedHosts` keeps working, as the browser still sends `localhost` |
| 6 | With a login the check, `AllowedHosts` and the block of other sites must be looked at again, as a real host name and other origins may then be valid | Decide with the login |
| 5 and 6 | A static page cannot hide an API key | Design the authentication with the dashboard in mind |
| 6 | Rate limiting is planned for `/check`, but `POST /services/{id}/check` and `GET /services/{id}/check` also start network requests, and the locks do not limit checks of different services | Cover all the check routes in phase 6 |
