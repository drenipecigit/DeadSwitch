# DeadSwitch progress

Updated: 2026-11-12.

DeadSwitch is a personal uptime monitor. The backend is **not finished**: see the MVP status below.

## Architecture

.NET 10, ASP.NET Core Web API, EF Core, SQLite. Dependencies point inward only, confirmed from the project files and `using` statements.

| Project | Contains | References |
|---|---|---|
| `DeadSwitch.Domain` | `MonitoredService`, `CheckRecord`, `ServiceStatus`, `StatusChangeRule`, `SecurityHeaderRule`, `CertificateExpiryRule`, `CertificateWarningRule`, `CertificateFinding`, `CertificateReasons`, `RedirectFinding`, `RedirectWarningRule` | nothing |
| `DeadSwitch.Application` | `MonitoredServiceService`, `ServiceOverview`, `ServiceConfiguration`, `UrlSafety`, `ServiceLocks`, `ServiceValidationException`, `CheckInProgressException`, `NotificationFailedException`, `StatusChange`, `SecurityHeaderCheck`, `CertificateCheck`, `SecurityWarning`, `SecurityWarningFinder`, `SecurityWarningSettings`, `RedirectTargetCheck`, `RedirectTargetParser`, interfaces including `INotifier` | Domain |
| `DeadSwitch.Infrastructure` | `DeadSwitchDbContext`, repository, `HttpHealthProbe`, `SecurityHeaderReader`, `RedirectTargetReader`, `TlsHandshakeRecorder`, `TlsHandshake`, `ProtectedHttpHandler`, `PublicInternetConnector`, `DestinationBlockedException`, `DiscordNotifier`, `DiscordMessage`, `DiscordOptions`, `DiscordWebhookUrl`, migrations | Application, Domain |
| `DeadSwitch.Api` | Startup in `Program.cs`, `ServiceEndpoints`, `RegisterServiceRequest`, `UpdateServiceRequest`, `UtcDateTimeOffsetConverter`, `MonitoringWorker`, `SecurityHeaders`, `CrossSiteRequests`, `ExpectedErrors`, `LocalOnlyCheck`, and the dashboard in `wwwroot` | Application, Domain, Infrastructure |
| `DeadSwitch.Tests` | Automated tests | Api, and the other projects through it |

## Endpoints

| Method and route | Purpose |
|---|---|
| `GET /` | The dashboard: `index.html`, `dashboard-logic.js`, `dashboard.js` and `dashboard.css` from `wwwroot` |
| `GET /health` | Health of the API itself |
| `POST /services` | Register a service |
| `GET /services` | List services |
| `GET /services/overview` | One row per service for the dashboard, with the response time and failure reason of its latest check. Only reads |
| `GET /services/{id}` | Get one service |
| `PUT /services/{id}` | Replace a service's configuration; every field is required |
| `DELETE /services/{id}` | Delete a service and its history |
| `POST /services/{id}/check` | Manual check, saved to the history. Answers with `Cache-Control: no-store` |
| `GET /services/{id}/history?skip=&take=` | Check history, newest first; `take` is 1 to 200 |
| `GET /check?url=` | Check a public URL without saving anything |

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

**Security headers of a check.** A check record, and so `POST /services/{id}/check` and the history, has three more fields. `GET /check` and `GET /services/{id}/check` are unchanged.

| Case | `securityGrade` | `missingSecurityHeaders` | `securityNotAssessedReason` |
|---|---|---|---|
| Assessed, something is missing | `B`, `C`, `D` or `F` | The names, separated by `, ` | null |
| Assessed, nothing is missing | `A` | An empty text | null |
| Not assessed | null | null | `Not an HTTPS address.`, `The response was a redirect.` or `The response was not a success.` |
| No response, or a record older than this feature | null | null | null |

**The certificate of a check.** A check record, and so `POST /services/{id}/check` and the history, has two more fields. `GET /check` and `GET /services/{id}/check` are unchanged.

| Case | `certificateNotAfterUtc` | `certificateNotAssessedReason` | Status and grade |
|---|---|---|---|
| A response, and a fresh accepted handshake for the host | The date, in UTC with `Z` | null | As before |
| A response, and no handshake could be read for this check | null | `No new TLS handshake for this check.` | As before. Never invalid |
| No response, the check's own failure is a TLS failure, and a fresh rejected handshake exists for the host | null | `The certificate was not valid.` | DOWN, no grade |
| No response, a TLS failure, but no matching handshake | null | null | DOWN, not called invalid |
| A timeout, a connection failure, a blocked address, or a record older than this feature | null | null | As before |
| `http` | null | `Not an HTTPS address.` | As before |

**The redirect target of a check.** A check record, and so `POST /services/{id}/check` and the history, has two more fields. `GET /check` and `GET /services/{id}/check` are unchanged. The raw `Location` value is never saved or returned.

| Case | `redirectFinding` | `redirectTargetHost` |
|---|---|---|
| No response, 4xx or 5xx, 3xx without `Location`, or a record older than this feature | null | null |
| 2xx | `NO_REDIRECT` | null |
| A relative target or the same host | `SAME_HOST` | null |
| Another public host | `OTHER_HOST` | The normalised host name or a public IP address |
| A private address, `localhost` or an internal name | `INTERNAL_TARGET` | null, the value is hidden |
| A value that cannot be read safely | `UNREADABLE` | null |

**`GET /services/overview`.** Each row has `id`, `name`, `url`, `currentStatus`, `lastKnownStatus`, `lastCheckedAt`, `isEnabled`, `responseTimeMs`, `failureReason`, `securityGrade`, `missingSecurityHeaders`, `securityNotAssessedReason`, `certificateNotAfterUtc`, `certificateDaysLeft`, `certificateExpiryText`, `certificateNotAssessedReason`, `redirectFinding` and `redirectTargetHost`. The dashboard shows the last two in the column "Redirect", as text and never as a link. `certificateDaysLeft` is counted from now, rounded down, and is negative after the date has passed. `certificateExpiryText` is `Expired`, `Expires today`, `1 day left` or `N days left`. The certificate values only come from the newest check, so an older date is never shown after a check that saw no valid certificate. The values from `responseTimeMs` and on come from the newest check record, and are null when `lastCheckedAt` is null. That condition matters: a new URL empties `lastCheckedAt` but keeps the history, so without it a row would show the response time of the old address, also after changing back to an earlier URL.

**Security headers.** Every answer from the application has the headers listed under "The dashboard" in the security notes, JSON and errors included.

**`lastKnownStatus`.** A service in the JSON also has `lastKnownStatus`: `UP`, `DOWN` or null when nothing is known for the current URL. It is read-only: `POST` and `PUT` ignore it.

## Configuration

| Key | Environment variable | Meaning |
|---|---|---|
| `Discord:WebhookUrl` | `Discord__WebhookUrl` | The Discord webhook address. A secret: set it with `dotnet user-secrets` in Development, or as an environment variable elsewhere. Never in `appsettings.json` or git. Empty or missing turns notifications off |
| `SecurityWarnings:CertificateWarningDays` | `SecurityWarnings__CertificateWarningDays` | Warn when this many days or fewer are left of a certificate. Default 14, allowed 1 to 365. Another value stops the application at startup |

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
| `AddSecurityHeadersToCheckRecords` | Adds the nullable text columns `SecurityGrade`, `MissingSecurityHeaders` and `SecurityNotAssessedReason` to `CheckRecords`. Existing rows get NULL, which means that nothing was assessed. No data is rewritten |
| `AddCertificateToCheckRecords` | Adds the nullable columns `CertificateNotAfterUtc` and `CertificateNotAssessedReason` to `CheckRecords`. Existing rows get NULL, which means that no certificate was assessed. No data is rewritten |
| `AddRedirectTargetToCheckRecords` | Adds the nullable text columns `RedirectFinding` and `RedirectTargetHost` (max 253) to `CheckRecords`. Existing rows get NULL, which means no finding: `Location` was never saved, so nothing can be filled in afterwards. No table is rebuilt and no data is rewritten. As a result, the first check after the upgrade never gives a redirect warning. SQLite does not enforce the length, so the limit of 253 is set in the code and `HasMaxLength` is documentation only. `Up` holds only the two `AddColumn` calls. Proven by `RedirectMigrationKeepsOldRecordsWithoutAFinding` |
| `AddFailedChecksInARowToMonitoredService` | Adds the column `FailedChecksInARow` (INTEGER, NOT NULL, default 0) to `MonitoredServices`. Existing rows get 0, which means that no failed checks are counted for them yet. `Up` holds only the `AddColumn` call. No table is rebuilt and no data is rewritten. A service whose `LastKnownStatus` is already DOWN does not notify again. Proven by `FailedChecksMigrationGivesZeroToOldRows` |

The application does not run migrations at startup. Run `dotnet ef database update` when setting up a database, and after pulling a new migration: without it, reading a service fails because the column is missing. `MakeCurrentStatusRequired` has run in the tests and from empty against a separate database file, but not against the local development database. Run the command there as well.

Status is stored as the text `UP`, `DOWN` or `UNKNOWN`, as it always has been, so old rows did not need rewriting.

`feat/security-warnings` needed no migration: a warning is worked out from the rows already in `CheckRecords`, and the existing index on the service and the time fits the new queries. The price is one or two extra reads for each check with an assessment. Proven by `ModelMatchesTheMigrations`, by `dotnet ef migrations has-pending-model-changes` and by no change in the `Migrations` folder. A future cleanup of the history must keep the newest assessed row of each kind, or the next check starts over without a baseline.

`fix/api-contract` needed no migration. Marking a time read from the database as UTC does not change the column or the saved text. This is proven by `ModelMatchesTheMigrations`, by `dotnet ef migrations has-pending-model-changes`, by no change in the `Migrations` folder, and by tests that read the saved text directly.

## Latest verified result

Date: 2026-11-12, on `feat/finish-a`, after part 3.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 1582 passed, 0 failed, 0 skipped, on Windows. See "Finish A".

Before this, on the same day:

Date: 2026-11-12, on `fix/local-only-check-race`.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 1561 passed, 0 failed, 0 skipped, three runs in a row on Windows. `LocalOnlyCheckTests`: 20 of 20 runs green. No test was added; the local-only check tests now wait for a fixed order instead of catching an exception. See "Local-only check tests without a hidden race".

Before this, on the same day:

Date: 2026-11-12, on `fix/startup-validation-race`.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 1561 passed, 0 failed, 0 skipped, three runs in a row on Windows. No test was added; two tests now force a fixed order. See "Startup validation before app.Run()".

Earlier results:

Date: 2026-11-10, on `feat/dashboard-design`.

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 1554 passed, 0 failed, 0 skipped, on the final code. Run on Windows 10.0.26200. **Nothing has been run on Linux.**
- The card design changes only the dashboard's files and the tests. No API field, table or migration was changed. See "The dashboard as cards".

Before this, on `refactor/check-async`:

- `dotnet build DeadSwitch.slnx`: 0 warnings, 0 errors.
- `dotnet test DeadSwitch.slnx`: 1479 passed, 0 failed, 0 skipped, on the final code. Run on Windows 10.0.26200, `win-x64`, SDK 10.0.301. **Nothing has been run on Linux.**
- The split of `CheckAsync` and of the error translation adds 6 tests and changes none; see "Splitting CheckAsync and the error translation".

On 2026-11-10 on `fix/single-timeout`, before the split, 1473 tests passed, and `dotnet ef migrations has-pending-model-changes` reported no changes. The split touches no model and adds no migration.

- The DOWN confirmation adds 40 tests: 14 test `DownConfirmationRule` directly, 18 the whole flow through the API, the worker and the database with a fake probe and a fake notifier, 5 the real Discord message, 2 that POST and PUT cannot set the count or the status, and 1 the migration. 30 older test methods were changed so that they make two failed checks, and `MessageWithoutAPreviousStatusSaysFirstCheck` was replaced; see "Red runs and mutation check of the DOWN confirmation". No automated test reaches a real website or Discord.

On 2026-11-10 on `feat/redirect-target`, before the DOWN confirmation, 1434 tests passed.

- The redirect target has eight test classes of its own, 204 tests: 107 test the parser as text with no network, 19 the reader with no network, 14 the real probe with a stand-in for the network, 11 the real handler against a local raw server on loopback, 9 the storage through the API, 26 the whole flow through the API and the database with a fake probe and a fake notifier, 11 the real notifier with a stub, and 7 the domain rule. Rows in existing classes cover the migration, the overview fields and the dashboard's files. No automated test reaches a real website or Discord.

The numbers below are from 2026-11-09, when 1221 tests passed, and are kept for the earlier features.

- 206 of the tests come from the security warnings: 8 test the header comparison, 52 the certificate rule, 16 the baseline queries against SQLite, 70 the whole flow through the API and the database with a fake probe and a fake notifier, 1 the threshold time read back from the database, 10 failures and shutdown, 1 a real shutdown of the loop, 34 the real notifier with a stub, and 14 the setting. No automated test reaches a real website or Discord.
- 118 of the tests come from the TLS certificate: 23 test the recorder directly, 9 use the real connector and handler over HTTPS on `127.0.0.1`, 28 use the real probe with a stand-in for the network, 12 use the real probe over real TLS with a local connection step, 6 use the API and the database, 15 test the expiry rule, 12 test the overview, 10 read the dashboard's files as text, 1 tests the migration and 2 test the registered handler. No automated test reaches a real website.
- 76 of the tests come from the security headers: 27 test the reader directly, 9 use the real handler against a local server on `127.0.0.1`, 8 use the real probe with a stand-in for the network, 9 use the API with a fake or scripted probe, 7 test the rule, 15 read the dashboard's files as text, and 1 tests the migration. No automated test reaches a real website.
- 198 of the tests come from phase 5: 14 test the overview route, 37 the files and the headers, 51 read the dashboard's files as text, 70 test cross-site requests and host names, and 26 test the local-only check. No test runs the JavaScript.
- 470 of the tests come from phase 1: 162 only classify URLs and IP addresses, 17 run the real connection code against a local server, 16 test the probe with a stub handler, 11 run the real probe and handler with a fake DNS, 7 test `ServiceLocks` directly, 134 test unusual host names through the API, the worker and the real probe with a fake probe or a fake DNS, and 123 use the API, the worker or the migrations with a fake probe and an in-memory database.
- 153 come from phase 2: 60 classify webhook addresses, 12 test the rule directly, 29 test the real notifier with a stub handler, 2 test the registered handler against a local server, 13 run the real registration with a stub handler, 36 use the API, the worker or the migrations with a fake notifier, and 1 reads the settings files.
- Without the 68 tests that carry the trait `NameLookupWithoutConnector`: 1153 passed.
- No test sends anything to Discord. Real messages are confirmed by the owner's run; see "Manual run".

### The dashboard as cards

The table with twelve columns became one card per service. The top row has the status badge, the name, the URL and the grade box. A smaller row below has the details. A summary such as `2 UP | 1 DOWN | 1 waiting for confirmation` is shown above the cards, and every time is written as `2026-11-10 13:08:59` in local time with 24 hours.

**The status badge.** `currentStatus` and `lastKnownStatus` are merged into one badge. The server only writes DOWN as the last known status after two failed checks in a row, so DOWN in both is a confirmed DOWN, and DOWN now without DOWN before is exactly one failed check. `failedChecksInARow` is not needed and is not part of the overview.

| Current | Last known | Can happen | Badge | Detail |
|---|---|---|---|---|
| UP | UP | Yes | Green `UP` | |
| UP | null or DOWN | No, UP is confirmed at once | Green `UP` | |
| DOWN | DOWN | Yes | Red `DOWN` | `Confirmed after 2 failed checks in a row` |
| DOWN | UP or null | Yes | Yellow `DOWN?` | `1 failed check, no alarm yet` |
| UNKNOWN | null | Yes | Grey `UNKNOWN` | |
| UNKNOWN | UP or DOWN | Yes | Grey `UNKNOWN` | `Last confirmed: UP` or `Last confirmed: DOWN` |
| Any other value | Any | No | Grey `UNKNOWN` | As for UNKNOWN |

The confirmed text does not say "alarm sent", because the overview cannot know whether Discord was turned on or the send succeeded. The cards are sorted as confirmed DOWN, unconfirmed DOWN, UNKNOWN and UP, and by lower case name inside each group.

**The grade box.** The server gives A, B, C, D or F (`SecurityHeaderRule.GradeFor`); E never comes. A and B are green, C and D yellow, E and F red, and anything else, null included, is a grey `-`. Each class name is one of the page's own, compared with fixed letters, so no value from the server becomes a class.

**The certificate.** A date gives the server's text (`14 days left`) with `Not valid after <date>` as a tooltip. Only the exact text of `CertificateReasons.NotValid` gives `Invalid`. Every other reason (no new handshake, not https, or a reason no version knows) gives `Not assessed`. Both show the reason as a tooltip. With nothing known the field is hidden.

**Hidden fields.** A field with nothing to say is not shown: no missing headers, no redirect, no response time, no failure reason, and no certificate information.

**How it is built.** The decisions are pure functions in `dashboard-logic.js`, with no access to the page. `dashboard.js` only builds the page from what they return, and writes every value through `textContent` or `title`. Both files are loaded as our own files, so the Content Security Policy is unchanged.

**How it is tested.** `DashboardLogicTests` runs `dashboard-logic.js` in Jint 4.17.0 (BSD-2-Clause, by Sebastien Ros), a JavaScript engine written in .NET, in a fixed time zone of UTC+1. It is the first test package that runs JavaScript. The time tests use the exact formats the API sends, with Z and none to seven decimals, and one test takes the time from a real overview answer. `dashboard.js` is not run: its text is tested so that it only sets `textContent`, `title`, `className` and `hidden`, and builds the cards from the sorted list.

Red run: against a logic file with empty functions and the old page, 69 of 1550 tests failed, all of them new or changed. 15 of them failed with `ArgumentException` from Jint, because an empty function answered null where an object or a text was read; the rest failed on assertions. The new tests that passed expect a hidden field, which an empty function also gives. They are proven by the mutations.

Mutation check: 46 mutations, one at a time, with a backup and the full test run, and each file checked to be the same afterwards. Nothing was committed and nothing left the machine. 40 were found at once. Two survivors (cards not sorted, the update time in the browser's own format) were closed with two text tests and run again. Four remain:

| Mutation | Why it survives |
|---|---|
| `sortServices` sorts the server's own list | Nothing uses the list in its first order afterwards, so the page does not change |
| A missing badge detail still added | `dashboard.js` is not run in a test. It would add an empty line |
| `Paused` on the wrong services | `dashboard.js` is not run in a test |
| The yellow colour removed from the CSS | No test reads colours. The text `DOWN?` is still there |

Contrast (WCAG), text on its background: light green 6.41, red 6.51, yellow 6.59, grey 5.02, details 6.00; dark green 8.70, red 7.90, yellow 9.25, grey 7.59, details 6.74. All are above 4.5:1. A stale page is shown at 55 % opacity on purpose, which lowers the contrast.

Not proven: how a real browser reads the times and builds the cards, summer time (only a fixed UTC+1 zone is tested), and the light theme. The names are sorted by character code, which does not give Swedish order for the three letters after z. The manual test has not been run yet.

### Red runs and mutation check of the DOWN confirmation

The red run was made against a skeleton: the column, the migration and `DownConfirmationRule` were in place, but the rule behaved like the old code and `CheckAsync` and `DiscordMessage` were unchanged. 24 of 1470 tests failed, all of them new or changed tests, and all on assertions: no `SqliteException`, `NullReferenceException` or `DbUpdateException`. All 1432 unchanged older tests passed.

The new tests that passed cannot be red against the old code, because the old code gives the same answer there: the constant, table rows where old and new agree, two timeouts in a row giving one message, Up after a confirmed Down, Unknown between two failures, the unchanged UP text, the migration, and the two tests that POST and PUT cannot set the count or the status. The last two were green because `RegisterServiceRequest` and `UpdateServiceRequest` already hold only the five configuration fields. All of these are proven by the mutations below.

`UpBetweenTwoTimeoutsStartsTheCountOver` was added after the mutation run, because "Up does not start the count over" was only noticed by the table and the stored count. Its red run is the rows "Up does not start the count over, again" and "The alarm follows the raw status" below.

Older test methods changed, because a single DOWN no longer gives a message: 12 in `NotificationBehaviourTests` (most now through `ConfirmDownAsync`), 4 in `NotificationOrderTests`, 3 in `NotificationFailureTests`, 5 in `WebhookSecrecyTests`, 1 in `LastKnownStatusTests`, 1 in `CertificateStorageTests`, 2 in `SecurityWarningFlowTests`, 1 in `DiscordConfigurationTests` and 1 in `DiscordNotifierTests`. `OneTimeoutIn34Checks...` now expects no status message, which is the case this change was made for. Four of these tests still passed with a single DOWN but no longer tested what their name says: no save, a cancelled check, notifications turned off, and a new URL forgetting the old status. They were changed so that the second failed check is the one that would notify.

The code was broken on purpose, one change at a time, running all tests and restoring the file from a backup. The changes were never committed, and checksums of all files were compared before and after. All of them ran against a fake probe and a fake notifier, or `127.0.0.1`.

| Change | Failing tests |
|---|---|
| The threshold is 1 | 23 |
| The threshold is 3 | 34 |
| `>=` becomes `>` | 33 |
| Unknown starts the count over | 3 |
| Unknown counts as a failed check | 3 |
| Up does not start the count over | 2 |
| Up does not start the count over, again, with `UpBetweenTwoTimeoutsStartsTheCountOver` | 3 |
| Unknown with a count of 2 is confirmed as DOWN | 1 |
| `LastKnownStatus` gets the raw status | 40 |
| The alarm follows the raw status, as before | 36 |
| A new URL keeps the count | 1 |
| The count is kept in a local variable and not saved on the service | 38 |
| `HasDefaultValue(0)` removed | 529, because the model no longer matches the migrations |
| The migration gives old rows 7 | 7 |
| The Discord message says "first check" | 4 |
| The message has a fixed "2", and the threshold is 3 | 37, which is 3 more than the threshold alone: the Discord tests built from the constant |
| The UP message also says "failed checks in a row" | 3 |
| POST and PUT take the count and both statuses from the body | 4, of which 2 in `ServiceInputProtectionTests` and 2 older ones in `LastKnownStatusTests` |

"The migration gives old rows 7" also fails tests for new services: with `HasDefaultValue(0)` EF Core does not send the value 0, so the database default decides. This is why the migration's default and the model's default must agree.

The first run of the `LastKnownStatus` row was not valid: the file has LF line endings, the pattern looked for CRLF, and the check that the change had been made was fooled because `sed` adds a final line break. The check was fixed, proven with a change that cannot match, and the row was run again.

### Readability changes without new behaviour

`refactor/readability` changes names, comments and how values are written, and nothing that can be seen from the outside: named arguments for `ServiceOverview`, the parameter `monitoring` for the service layer, named constants for limits and time values, written-out JSON names on `GET /check` and `GET /services/{id}/check`, clearer names for the timeouts, an ordinary constructor in `DeadSwitchDbContext`, corrected comments and a corrected request file. The migrations and the SSRF protection were not touched.

Tests cannot be red first for a change like this, so the behaviour was compared before and after:

- All 897 tests passed before every commit. The only test lines changed are the two that use the renamed `SendTimeout`, and three comments.
- The real server (Kestrel), with its own database file, the worker off, Discord off and `localhost` only, answered 45 requests identically once ids, trace ids and dates were masked. They cover the limits of a name, an interval, a timeout and `take`, `PUT` without each field, the fixed errors, a blocked `Host` and another site's request, and `/openapi/v1.json`.
- Starting with a poll of 0, 1, 3600 and 3601 gave the same result.
- `dotnet ef migrations script` gave identical SQL, and `has-pending-model-changes` reported no change.

Not shown by this comparison:

- `CheckUrlTimeoutSeconds` and `DefaultHistoryTake`: the first needs a real outgoing check and the second more than 50 records, so these values were only read in the diff.
- A check through `POST`, `GET /services/{id}/check` or the worker against a real website. These are covered by tests with a fake probe.
- That the comparison would have noticed a changed value. It was not run against code broken on purpose.

### Splitting CheckAsync and the error translation

`refactor/check-async` changes how two pieces of code are laid out, and not what they do.

- `MonitoredServiceService.CheckAsync` reads from top to bottom: is the service due, take the lock, `CheckAndSaveAsync`, release the lock in `finally`, `SendAfterSaveAsync`. The lock is still taken and released in `CheckAsync` itself. Everything that ran inside the lock before runs inside `CheckAndSaveAsync`, in the same order, and the notifications are still sent after the lock: the status message first, then the security warnings. The other new methods are `IsDueBeforeTheLockAsync`, `CreateCheckRecord` and `ApplyResultToService`, and a small `CheckOutcome` record carries the result out of the lock.
- The 34-line lambda in `Program.cs` that turned expected exceptions into Problem Details is now `ExpectedErrors.TranslateAsync` in `DeadSwitch.Api/Errors`, registered in the same place in the pipeline. The status codes, titles and texts are the same.

Not every line was moved word for word. `CreateCheckRecord` and `ApplyResultToService` read `service.Id` instead of the `id` parameter, which is the same value because the service was read with that id. `ApplyResultToService` reads the record's status and reason instead of the probe result's, which are the same values because the record copies them.

No existing test was changed. Six tests were added where something was not covered:

- 1 that the 409 answer has the fixed title and text.
- 5 that the 409, 413 and 415 answers have all the security headers.

Each new test was green against the code before the split. Each one is proven by a change that makes only that test fail.

The test names before and after are the same, apart from the six new ones. Every changed piece was then broken on purpose, one at a time, and the file was restored from a copy afterwards. The checksums of all `.cs` files were identical before and after.

| Change made on purpose | Tests that failed |
|---|---|
| The check is not saved | 199 |
| The security warnings are sent before the status message | 1 |
| The notifications are sent inside the lock | 2 |
| The status message is sent before the check is saved | 3 |
| The lock is released before the check and the save | 7 |
| The lock is never released | 199 |
| The warnings are looked for after the service is updated and saved | 75 |
| UNKNOWN is read after the status is overwritten | 1 |
| A cancelled check is not stopped | 1 |
| The second "is it due" inside the lock is removed | 1 |
| The first "is it due" before the lock is removed | 3 |
| The record loses the redirect host | 6 |
| The record loses the certificate date | 42 |
| `LastCheckedAt` is not set | 126 |
| The status change does not leave the lock | 35 |
| The warnings do not leave the lock | 72 |
| The 415 answer is not written | 2 |
| `CheckInProgressException` is not caught | 4 |
| The 409 title is changed | 1 |
| A bad request shows the exception's text | 18 |
| A validation error gives 422 | 112 |
| The error translation is placed before the exception handler | 138 |
| The security headers are added after the error translation and the static files | 11 |
| The security headers are left out of a 409, a 413 or a 415 answer | 1, 2 and 2 |
| The error translation swaps places with the cross-site check | **0** |
| The error translation is registered after `ServiceEndpoints.Map` | **0** |

The two rows with 0 failing tests are moves that change nothing, so they cannot fail a test:

- The cross-site check never throws the exceptions that the translation catches, and it only writes a 403.
- A minimal API runs every `app.Use` before the endpoints, wherever `Map` is written.

The six new tests passed in both runs, so the error answers kept their security headers.

Not shown:

- A real server: the split was only run through the tests, and the earlier comparison of 45 requests against Kestrel was not repeated.
- That the time the lock is held is unchanged. Only method calls and one object were added, but nothing was measured.

### Cost of reused connections

A certificate is only visible during a handshake, so a reused connection has nothing to read. Measured once against a local server on `127.0.0.1`, with a 2048-bit RSA certificate, 200 requests per row, in two passes in opposite order. The two values are the two passes.

| Alternative | Median ms | 95th percentile ms | Memory per request | Handshakes | Connections |
|---|---|---|---|---|---|
| Pooling, as before | 0.14 / 0.09 | 0.22 / 0.15 | 2 KB | 1 of 200 | 1 |
| `ConnectionClose` | 6.09 / 5.56 | 7.19 / 6.49 | 32 / 28 KB | 200 | 200 |
| `PooledConnectionLifetime = 0` | 5.84 / 5.61 | 7.00 / 6.63 | 29 KB | 200 | 200 |
| A new handler per check | 6.03 / 6.02 | 7.67 / 7.27 | 33 KB | 200 | 200 |

`ConnectionClose` was chosen because it keeps one handler and only affects the probe. This is local CPU time and says nothing about the network. The memory figure covers the whole process, the local server included. The measuring code was removed and never committed.

In the same run, `Uri.IdnHost` and `SslStream.TargetHostName` held the same text for an IPv4 address, an IPv6 address, an ordinary name, upper case, a trailing dot and an IDN name.

### Red runs and mutation check of the redirect target

The red run was made against empty skeletons, where the parser answers null and the columns and fields exist. 178 of 1431 tests failed, all of them new tests, and all on assertions: no `SqliteException`, `NullReferenceException` or `DbUpdateException`. The new tests that passed cannot be red against a parser that answers null, for example that a failure gives no finding. They are proven by the mutations instead.

52 mutations were made, one at a time, each restored with an edit and with a clean `git status` between the runs. None turned off `ConnectCallback`. 47 are caught. Five survive, and all are checks that another step also covers: three slashes, a slash after the scheme, more than one `@`, lower case and empty labels. They are kept on purpose; see "Code style exceptions". The rule for the first check survived at first, and is caught after three tests were added for changing the URL and for another service.

Three parts were then mutated one at a time as well, with all tests run each time:

| Change | Failing tests |
|---|---|
| `IsExternal` answers false for `OtherHost` | 15 |
| `IsExternal` answers false for `InternalTarget` | 3 |
| `InternalTarget` gets the warning kind for an unreadable target | 1 |

What was measured on Windows, through the parser's result and the mutations, not by calling `Uri` directly:
- `NonValidated`: two raw `Location` headers show as two values and give `UNREADABLE`, and a value the framework cannot parse is still visible and gives `UNREADABLE`. With the typed `Headers.Location` instead, 5 tests failed.
- `SocketsHttpHandler`, with raw bytes from a local server through the real probe and handler: no case gave DOWN. CR, NUL, 0x01, 0x7F and 0xE9 in the value give UP and `UNREADABLE`, a folded value gives UP and `UNREADABLE`, and a bare LF ends the header and gives UP and `OTHER_HOST`.
- `Uri`: `0x7f.1`, `2130706433`, `127.1`, `0177.0.0.1` and `[::ffff:127.0.0.1]` give `INTERNAL_TARGET`. `example.123`, `1.2.3.4.5`, `256.256.256.256` and `example.0x7f` give `UNREADABLE` because of the rule for a numeric last label; without the rule all four failed. `a@b@c`, `https:///x` and empty labels are stopped even without the parser's own checks, and names become lower case anyway, which is why those four mutations survive.

### Red runs of the security warnings

The new tests cannot compile against the old code, because the types do not exist. They therefore ran against a version with the signatures in place and the old behaviour: no warning is ever found, the rules answer nothing, the message is empty and the setting is 0.

| Tests | Failed | Could not be red |
|---|---|---|
| The certificate rule, 52 rows | 28 | 24 expect "not looked at", no threshold or no warning |
| The header comparison, 8 tests | 4 | 4 expect nothing to have disappeared |
| The baseline queries, 16 rows | 8 | 8 expect no baseline |
| The flow for headers, 33 tests | 18 | 15 expect no warning |
| The flow for the certificate, 33 tests | 26 | 7 expect no warning |
| Failures and shutdown, 10 tests | 8 | 2 expect nothing to be sent or the next service to be checked |
| A real shutdown of the loop, 1 test | 1 | |
| The real notifier, 34 tests | 32 | 2 expect nothing to be sent without an address |
| The setting, 14 tests | 9 | 5 hold with the old behaviour too |

Together 134 of the first 201 tests failed, and all 1015 older tests passed. The tests that could not be red are proven by the mutations below. The status messages' test for line separators ran red against the real, unchanged cleaning.

Five tests were added after this run. `ChangingToAnotherUrlAndBackWithoutACheck...`, two of them, were added when planning the mutations. Their red run is the rows for the `LastCheckedAt` guard. `ChangingBackAfterOnlyTimeouts...`, two of them, were added because the first mutation run found a gap. Their red run is the rows for the `Url` condition.

`PassingTheFourteenDayLimitFollowedBySeveralChecksWithinAnHourWarnsOnce` could not be red: the code was already right, because `DeadSwitchDbContext` marks both times as UTC when they are read. A printout from a real read through `GetCertificateBaselineAsync`, on a machine at UTC+1, gave `Kind` `Utc` on both fields, and `+00:00` from `new DateTimeOffset(...)`. The proof that the test works is the three mutations at the end of the table below. The test also checks `Kind` directly, because a machine running in UTC would not notice local time in the count.

### Mutation check of the security warnings

The code was broken on purpose, one change at a time, running all tests and restoring the file from a copy outside the repository. The sha256 of every file was compared after every restore, and all matched. The changes were never committed. All of them ran against a fake probe, a fake notifier or a stub handler, and none touched `ConnectCallback` or `PublicInternetConnector`. The table shows the final result. The rows for the `Url` condition first gave 0, and were run again after their tests were added. Only tests changed between the two runs.

| Change | Failing tests |
|---|---|
| Header baseline also takes rows that were not assessed | 10 |
| Header baseline: the Url condition removed | 1 |
| Certificate baseline: the Url condition removed | 1 |
| OnlyIfSameUrl does not compare the URL | 3 |
| Header baseline takes the oldest row | 6 |
| Certificate baseline takes the oldest row | 11 |
| Certificate baseline loses Rejected | 6 |
| Certificate baseline also takes NotSeen | 2 |
| Certificate baseline matches with StartsWith | 2 |
| Certificate baseline takes any reason | 8 |
| LastCheckedAt guard removed for headers | 1 |
| LastCheckedAt guard removed for the certificate | 1 |
| Certificate rule only runs when headers were assessed | 26 |
| Header rule runs without an assessment | 21 |
| NotSeen goes through the certificate rule as a verdict | 5 |
| First check with few days left does not warn | 6 |
| Previous threshold counted from the new check | 2 |
| Threshold counted from now | 4 |
| The setting is ignored | 2 |
| The URL is used as the name of the warning | 3 |
| Rejected warning is never returned | 10 |
| A Rejected check also goes on to the expiry rule | 17 |
| Every rejected check warns | 12 |
| Timeout and unknown text count as Rejected | 21 |
| NotSeen counts as Rejected | 4 |
| Classify ignores case for Rejected | 2 |
| Classify uses StartsWith for Rejected | 4 |
| A date no longer wins over a reason | 4 |
| First threshold one day lower | 7 |
| First threshold one day higher | 7 |
| Last threshold one day lower | 4 |
| Last threshold one day higher | 3 |
| Second threshold one day lower | 2 |
| Second threshold one day higher | 2 |
| Second threshold also used below the setting | 2 |
| The same threshold warns again | 9 |
| A better threshold warns too | 1 |
| A first threshold does not count | 21 |
| Every missing header warns at every check | 5 |
| A header that comes back warns | 35 |
| Parts of a name count | 1 |
| Saved text is passed on instead of the fixed list | 7 |
| Warnings sent before the save | 4 |
| Warnings sent inside the lock | 2 |
| Warnings looked for after the save | 56 |
| Warnings looked for after LastCheckedAt is set | 2 |
| Warning sent before the status message | 1 |
| No try/catch around a warning | 5 |
| Only the first warning is sent | 3 |
| The finder inside a try/catch that swallows a shutdown | 1 |
| The finder does not get the token | 0 |
| ThrowIfCancellationRequested between the warnings | 2 |
| The worker's token reaches the sending | 1 |
| The exception object goes to the logger | 1 |
| An unexpected exception's text is logged | 1 |
| The expected failure's text is not logged | 2 |
| parse holds everyone | 10 |
| The name skips FormatAsCode | 24 |
| The name is not cut | 2 |
| A date in the warning | 8 |
| The service id in the warning | 8 |
| Line separator is not cleaned | 2 |
| Paragraph separator is not cleaned | 2 |
| Format characters are not cleaned | 6 |
| Wrong text for one day | 1 |
| Rejected text changed | 1 |
| Header names are separated differently | 3 |
| NotValid text changed | 1 |
| NoNewHandshake text changed | 1 |
| Default becomes 15 | 4 |
| Lower limit becomes 0 | 2 |
| Upper limit becomes 366 | 2 |
| Validate removed | 3 |
| The wrong configuration section is read | 6 |
| CheckedAtUtc is not marked as UTC when read | 5, including the time test |
| CertificateNotAfterUtc is not marked as UTC when read | 3, including the time test |
| The check time is read as local time in the finder | 1, the time test |

The row with 0 failing tests stays on purpose. Without its token the baseline read is not cancelled at once, but `SaveCheckAsync` gets the same token right after it, so nothing is saved and nothing is sent. The difference is only one read that finishes, and a test of it would show how the code is written and not how it behaves.

### Red runs of the TLS certificate

Each group ran before the real code was written.

| Tests | Run against | Failed | Could not be red |
|---|---|---|---|
| The recorder and the real connector over HTTPS, 32 tests | A callback that accepts everything and a recorder that keeps nothing | 23 | 9 expect nothing saved, a refused address, or describe the handler |
| The probe, the storage, the migration and the registered handler, 49 tests | The fields and migration in place, a probe that reads no certificate, no mapping and no registration | 36 | 13 expect no certificate, or that a request is blocked. Proven by the mutations below |
| The rule, the overview and the dashboard's files, 37 new tests | A rule that always answers 0 and an empty text, empty overview fields, and a page without the new parts | 30 | 7 rows expect 0, empty fields or a word that is absent |

### Mutation check of the TLS certificate

The code was broken on purpose, one change at a time, running all tests and restoring the file from a copy outside the repository. The sha256 of every file was compared after every restore, and all matched. The changes were never committed. All of them ran against a stand-in for the network or `127.0.0.1`, and none of them touched `ConnectCallback` or `PublicInternetConnector`.

| Change | Failing tests |
|---|---|
| The callback always returns true | 15 |
| Everything counts as valid | 15 |
| Chain errors are accepted | 7 |
| A name mismatch is accepted | 3 |
| Nothing is saved for a valid certificate | 22 |
| A rejected certificate saves its date | 7 |
| `NotBefore` instead of `NotAfter` | 12 |
| Local time instead of UTC | 12 |
| `catch (CryptographicException)` removed | 0; see below |
| A missing certificate that passed counts as rejected | 1 |
| `ConnectionClose` removed | 3 |
| HTTP/2 requested | 4 |
| `RequestVersionOrHigher` | 4 |
| The `VersionPolicy` line removed | 1 |
| A rejected entry makes a response invalid | 4 |
| A rejected entry makes a response DOWN | 17 |
| The `AuthenticationException` requirement removed | 1 |
| A rejected entry is not read on a TLS failure | 3 |
| The `Take` in `finally` removed | 4 |
| `Take` does not remove the entry | 10 |
| The maximum age check removed | 2 |
| Maximum age 4 minutes | 3 |
| Maximum age 2 minutes | 2 |
| Nothing seen counts as invalid | 7 |
| A missing entry on a failure counts as invalid | 3 |
| A host without an entry gets a rejected entry | 15 |
| The overview searches back for a date | 3 |
| The `Expired` branch removed | 5 |
| The `Expired` limit becomes `<= 0` | 3 |
| The `Expired` limit becomes `< -1` | 3 |
| `DaysLeft` off by one | 15 |
| `DaysLeft` rounds up | 6 |
| Hosts are not lower-cased | 2 |
| A trailing dot is not removed | 2 |
| `Store` does not normalise the host | 3 |
| `Take` does not normalise the host | 3 |
| `DnsSafeHost` instead of `IdnHost` | 2 |
| A rejected certificate gives UP | 24 |
| A rejected certificate gets a grade | 4 |
| The limit of entries removed | 1 |
| Old entries are not cleared | 1 |
| A full recorder refuses a known host | 1 |
| The production code sets a chain policy | 2 |
| The callback is not set on the handler | 19 |
| `Program.cs` uses the handler without a recorder | 1 |
| The record does not get the date | 9 |
| The record does not get the reason | 4 |
| The overview does not get the date | 1 |
| The overview does not get the days | 6 |
| The overview does not get the text | 7 |
| The overview does not get the reason | 2 |
| The days are counted from the check and not from now | 1 |
| The migration lacks the reason column | 183, because reading a record then fails |
| The date is not marked as UTC when read | 2 |
| `innerHTML` instead of `textContent` | 1 |
| The certificate cell removed | 2 |
| The certificate details cell removed | 2 |
| The certificate column removed | 2 |
| The certificate details column removed | 2 |
| The script uses the number of days | 2 |
| `Not assessed` changed in the certificate cell | 0; see below |

The rows with 0 failing tests:

- `catch (CryptographicException)`: no test can make `NotAfter` throw during a real handshake. The catch stays because an exception there would fail the handshake, and only the validation may decide that.
- `Not assessed` in the certificate cell: the search only finds that the text exists in the script, and the security grade cell holds the same text. This is a real gap in a text search, and only a browser shows the right text in this cell.

### Red runs of the security headers

Each group ran before the real code was written, with the columns and the migration in place, `GradeFor` always `F`, an empty header list, a reader that assesses nothing, a probe that does not call it, and a dashboard without the new parts.

| Tests | Failed | Could not be red |
|---|---|---|
| The reader, 27 tests | 27 | |
| The rule, 7 tests | 5 | 2 rows expect `F` |
| The real handler, 9 tests | 6 | 3 describe the framework: where the CSP header lands, and the header limit |
| The real probe with a stand-in, 8 tests | 4 | 4 expect nothing when there was no response |
| The API and the database, 9 tests | 4 | 5 expect empty fields |
| The dashboard's files as text, 12 tests | 12 | |
| The migration, 1 test | 0 | The migration had to exist for the other tests to run. It is proven by the mutation below |

Three later dashboard tests were added after the first mutation run, because of the row "The reason is shown as a dash" below. Their red run is that mutation.

### Mutation check of the security headers

The code was broken on purpose, one change at a time, running all tests and restoring the file from a copy. The changes were never committed. All of them ran against a stand-in for the network, a fake probe or `127.0.0.1`. None of them touched `ConnectCallback` or `PublicInternetConnector`.

| Change | Failing tests |
|---|---|
| Grade A for one missing header | 16 |
| The branch for grade B removed | 16 |
| The branch for grade C removed | 1 |
| The branch for grade D removed | 1 |
| Grade F becomes D | 3 |
| `Strict-Transport-Security` removed from the list | 4 |
| `Content-Security-Policy` removed from the list | 4 |
| `X-Content-Type-Options` removed from the list | 4 |
| `X-Frame-Options` removed from the list | 8 |
| `Referrer-Policy` removed from the list | 5 |
| The separator changed | 1 |
| Every value counts as present, also an empty one | 8, of which 5 through the real handler |
| Only spaces count as present | 2; see below |
| `http` is assessed | 3 |
| A redirect is not recognised | 5 |
| The redirect's lower limit becomes 301 | 1 |
| The redirect's upper limit becomes 398 | 1 |
| 4xx and 5xx are assessed | 5 |
| The upper limit of a success becomes 298 | 1 |
| The header values are saved | 19 |
| Each of the three reason texts changed | 1 each |
| The content headers are read instead | 1 |
| A second request is sent | 5 |
| An UP result does not carry the headers | 3 |
| A DOWN result does not carry the headers | 4 |
| The header limit raised to 128 KB | 2 |
| The header limit lowered to 32 KB | 2 |
| The record does not get the grade | 3 |
| The record does not get the missing names | 3 |
| The record does not get the reason | 1 |
| The overview does not get the grade | 3 |
| The overview does not get the missing names | 3 |
| The overview does not get the reason | 1 |
| The overview uses the second newest record | 10 |
| The `LastCheckedAt` condition removed | 3 |
| A column missing in the migration | 165, because reading a record then fails |
| The explanation of the grade changed | 1 |
| The explanation is not written | 2 |
| The explanation uses `innerHTML` | 2 |
| The cells use `innerHTML` | 1 |
| The reason field is never read in the script | 1 |
| The reason is shown as a dash | 0 at first; 1 after the test was added |
| The cell returns a dash instead of the grade, or of the missing names | 1 each |
| `Not assessed` changed | 1 |
| `None` changed | 1 |
| A column removed from the head of the table | 2 each |
| A cell removed from the row | 2 each |

"Only spaces count as present" fails only the tests of the reader, not the ones through the real handler. Between the server and `HttpResponseMessage` the spaces are removed, so such a header reaches the code as an empty value. The test through the handler therefore proves the empty case end to end, and only the tests of the reader prove the case with spaces.

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
- The new faulty URL `https://does-not-exist.invalid/` gave DOWN: exactly one message, `is DOWN (first check)`. This was the rule and the text at the time; since `fix/single-timeout` the message comes at the second failed check and says `(2 failed checks in a row)`.
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

Run against the real server (Kestrel) on 2026-11-09 for the security headers, with its own database file created from empty by the migrations, the worker off, Discord off and no checks, so that nothing left the machine: the overview had the three new fields as null for a service not yet checked, and the page had the two new columns and the element for the explanation. The `curl.exe` command for reading headers printed `method=GET` against `localhost`. This does not show a grade on a real response; the owner's run below does.

Done by the owner on 2026-11-09 in Chrome against the real server (Kestrel) for the security headers, with its own database file, the worker off and Discord off. Each service was checked manually, and `curl.exe` read the headers of the same address with a `GET` of its own.

| Address | Status | Security grade | Header details | `curl.exe` |
|---|---|---|---|---|
| `https://github.com/` | UP, 200 | A | `None` | All five headers present |
| `https://example.com/` | UP, 200 | F | All five names | None of the five present |
| `http://github.com/` | UP, 301 | `Not assessed` | `Not an HTTPS address.` | |
| `https://www.github.com/` | UP, 301 | `Not assessed` | `The response was a redirect.` | |
| `https://github.com/deadswitch-page-that-does-not-exist` | DOWN, `Check timed out.` after 10008 ms | `-` | `-` | 504 afterwards |
| `https://api.github.com/deadswitch-page-that-does-not-exist` | DOWN, 404 | `Not assessed` | `The response was not a success.` | All five headers present on the 404, and no grade was set |
| A service that was not checked | | `-` | `-` | |

- The timeout row tested no 404. It shows the case with no response: all three fields empty. The `api.github.com` row is the one that shows an error response.
- The fixed explanation was shown above the table, the Console had no red lines, and Discord stayed silent.
- `curl.exe` showed `method=GET` and `redirects=0` in every run.
- The output of the header command cut long lines at 70 characters, so the values of the headers were not compared in full. The comparison shows which headers exist.
- The server was restarted during the run with the same database file, and the check against `api.github.com` ran after the restart.

This is one run in one browser, against sites whose headers can change.

Done by the owner on 2026-11-09 in Windows PowerShell 5.1 and Chrome, for the TLS certificate. The run was approved.

| Address | Status | Security grade | Certificate |
|---|---|---|---|
| `https://github.com/` | UP | A | `notAfter` 2027-01-27T23:59:59Z, `79 days left` |
| `https://1.1.1.1/` | UP, 301 | | A date and `42 days left` |
| `http://github.com/` | UP, 301 | | `Not an HTTPS address.` |
| `https://expired.badssl.com/` | DOWN | None | No date, `The certificate was not valid.` |
| `https://wrong.host.badssl.com/` | DOWN | None | No date, `The certificate was not valid.` |
| `https://self-signed.badssl.com/` | DOWN | None | No date, `The certificate was not valid.` |

- Repeated checks against `github.com`, each with a new connection, took about 76 to 111 ms. The probe has no way to run with a pooled connection, so this is the whole response time and not the extra cost of the new connection.
- After changing a service from `github.com` to `expired.badssl.com`, the overview showed no old date.
- `https://127.0.0.1/` gave HTTP 400.
- The text `Not assessed` in the certificate cell was seen in the browser. This is the text that the mutation check could not prove. The Console was clean.

Timeouts in the same run. **The cause has not been established.**

- 4 of 14 checks against `expired.badssl.com` timed out after 10 seconds.
- `curl` without DeadSwitch also got 2 timeouts of 10 against the same address.
- 1 of 34 checks against `github.com` timed out, directly after a timeout against `badssl`. It could not be reproduced.

This is one run in one browser, against sites whose certificates can change.

### Not run manually

Security warnings: manual test passed on 2026-11-09 with the worker and real Discord. `no longer sends` and `was rejected` each arrived exactly once, with a baseline changed by hand in a separate database. Silent at the following checks and after a real restart. `has at most 365 days left` for github.com without changed rows. The ordinary database was untouched. The reviewed part of the log held no webhooks, but the whole log was not seen. A real deterioration on a real site has not been provoked.

Security warnings, still proven by tests only:

- How Discord shows a line separator or an invisible character. The tests prove what is sent.
- A restart in the tests is a new worker against the same database, and not a new process.

Redirect warning: manual test passed on 2026-11-10 with the worker, real Discord and https://httpbin.org/redirect-to to example.com. The first check gave OTHER_HOST with UP and no message. The baseline was set to NoRedirect by hand in a separate database twice: first by mistake while the app was running, then with the app stopped. Each change gave exactly one warning, the second one after a real restart. Silent at the following checks and after one more restart. The dashboard showed example.com as plain text with no console errors. The marker zqx was found only in the service's own Url: not in the log, not in other columns and not in Discord. No webhook address in the log. Two sends, one per change, and no failed sends. No certificate warning came, as httpbin.org had 85 days left. The ordinary database was untouched. Whether example.com was a link, showed a preview or pinged anyone in Discord was not checked explicitly.

Not run manually: INTERNAL_TARGET and UNREADABLE. httpbin answered 403 Forbidden for redirects to 127.0.0.1 and to a value with a backslash, so they are proven by tests only.

DOWN confirmation (`fix/single-timeout`): the manual test has not been run yet. When it is run, it can only show that a really faulty URL gives one message at the second check and none at the first or third. That a single real timeout followed by UP gives no message cannot be provoked by hand: the SSRF protection blocks a slow local server, and no public server is under our control. It is proven by tests with a fake probe only, as is DOWN to UP on the same URL and a real restart between two failed checks.

Redirect target, still proven by tests only:

- A real hijack cannot be provoked. The manual run changes rows by hand in a separate database, so it proves the chain from the worker to Discord but not the event itself.
- How a real server sends hostile values. The tests send raw bytes from a local server on loopback.
- Linux. The tests run on Windows, where `Uri.TryCreate("/a", ...)` does not behave as on Linux. They show that `/a` gives `SAME_HOST` here and that the form step does not use `Uri`. That it holds on Linux follows from how the code is built, but is not proven until the tests run there, for example in GitHub Actions in phase 4.
- An internal name without DNS: `db01.corp.example.com` looks public and is shown. Looking it up would break the rule that the parser makes no DNS call, and the list of internal suffixes is not complete.
- The table of parsing differences is built on known tricks and cannot be complete against every browser.

TLS certificate, not confirmed in the owner's run:

- `Expired` on the screen. A real run would need a date that has passed in the database.
- Revocation of a certificate.
- The worker with Discord turned on.

Security headers:

- Old rows in a real upgraded database. The migration is proven by a test with an in-memory database and was run from empty against a separate file.
- Whether a grade is fair for the values of the headers. Only their presence is assessed.

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

- The five security headers are read from the response the probe already has, with exactly one request and no redirect followed. Only `https` with a 2xx gets a grade. Every other answer gets a fixed reason, and no response gives empty fields.
- Grades A, B, C, D and F follow the number of missing headers, and an empty value counts as missing. This also holds through the real handler against a local server.
- Grade, missing names and reason are saved per check, and shown in the history and the overview. A new URL empties them in the overview. Older records stay and have empty fields.
- Nothing a server sends reaches any answer: a hostile value in the headers and a `Location` value are absent from the check, the history and the overview. A response header section over 64 KB is refused.

- HTTPS through the real connector and the real handler on `127.0.0.1`: a valid certificate gives its date, and an expired, wrongly named or untrusted one is refused before the server gets a request. The trust is set by each test on its own handler, and the registered handler trusts nothing extra and still blocks `127.0.0.1`.
- The certificate callback answers exactly as the framework does for every combination of errors.
- The real probe over real TLS: date and grade from one response, DOWN with no grade for a bad certificate, one handshake per check, and HTTP/1.1 with `Connection: close` as seen by the server.
- A note in the recorder can never make a received response invalid or DOWN. "Invalid" needs no response, the check's own `AuthenticationException` and a fresh rejected note.
- The recorder's note is removed on every way out of an https check, and a note older than 3 minutes is not used.
- Two checks of the same host at the same time, and `GET /check` next to a saved check, are never called invalid.
- The host is found written with upper case, a trailing dot and IDN, also directly in the recorder and in both directions.
- The overview takes the certificate from the newest check only, and the limits -1, 0 and 1 give the right text with a fake clock.
- Nothing from a certificate reaches an answer, the log or a notification, with a hostile subject.

- A header that disappears gives one warning with its name, compared with the newest assessed check of the same URL. A timeout, a connection failure, a blocked address, a redirect or an error in between neither warns nor changes the comparison.
- A certificate gives one warning when it goes from accepted to rejected. "Not seen", a timeout and an unknown reason text are kept apart from a rejected certificate, give no warning and do not become a baseline. The baseline query agrees with `Classify` for every case.
- Headers and certificate are judged separately: a redirect, 4xx or 5xx with a valid certificate moves only the certificate baseline.
- A certificate warns once at each threshold, also at a first check, and a renewed certificate starts over. Passing the 14 day limit followed by checks within an hour gives one warning, and the times read back are marked as UTC.
- 4 timeouts of 14 against a rejected certificate give one warning, and 1 of 34 against a good site gives none.
- A new worker neither repeats nor misses a warning. A new URL has no baseline, also when changing back to an earlier URL.
- A warning is sent after the save and outside the lock, after the status message. A failed send keeps the check, is logged, and does not stop the next warning or service.
- A shutdown before the save saves and sends nothing, and ends the real loop without an error. A shutdown during the sending still sends every warning of the saved check.
- What is sent: only the service's name and fixed texts, no URL and no date, with the name as inline code and every ping turned off.

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
| SSRF protection | Done | Proven by tests against a local server, over HTTP and HTTPS |
| Tests for all of the above | Done for phase 1 | The limits are listed under "Not proven by a test" and "Never run so far" |
| Notification on UP to DOWN and DOWN to UP | Done | Both directions are proven by tests with a fake notifier and a stub. Since `fix/single-timeout`, DOWN needs 2 failed checks in a row; this is proven by tests only, and the manual test of it has not been run yet. The earlier manual run confirms real messages for UP to DOWN and for DOWN on a new URL with the old rule. DOWN to UP has not been run manually |
| `INotifier` with a Discord webhook | Done | Proven by tests, and confirmed by a manual run against a real channel. A wrong token from Discord and notifications turned off have not been run manually with a real send |
| Webhook address outside git | Done | Proven by tests of the settings files and the log. A manual run confirms the secret is read, and that the log holds no address after a real send |
| Dashboard (phase 5, not part of the MVP) | Done | The server side is proven by tests and a run against the real server, and the page by the owner's run in Chrome. Step D (`eval`) and the visible failure message are not confirmed on the screen |
| Security headers with a grade, saved per check | Done | Proven by tests and by the owner's run against real websites in Chrome. Not tested: old rows in a real upgraded database, and whether a grade is fair for the values |
| Certificate expiry saved per check, days left on the dashboard | Done | Proven by tests against a local server and by the owner's run against real websites in Chrome. Not confirmed manually: `Expired` on the screen, revocation, and the worker with Discord turned on. The cause of the timeouts in that run has not been established |
| Warnings when security gets worse | Done | Proven by tests with a fake probe, a fake notifier and a stub. Manual test passed on 2026-11-09; see "Not run manually" |
| Redirect target saved per check, with a warning when it moves to another domain (phase 3 task 4, not part of the MVP) | Done | Proven by tests with a fake probe, a fake notifier, a stub and a local raw server. Manual test passed on 2026-11-10 for `OTHER_HOST`; `INTERNAL_TARGET` and `UNREADABLE` are proven by tests only. See "Not run manually" |
| README | Missing | |
| GitHub Actions | Partial | The workflow file and its text tests exist on `ci/github-actions`. It has never run on GitHub, and no test has ever run on Linux. See "Phase 4, first part: GitHub Actions" |

## Notification rules

A single failed check is not enough for a DOWN message. A single timeout happens even when nothing is wrong, and it used to give one DOWN and one UP message for a problem that never existed. `DownConfirmationRule` therefore turns the status of a check into a confirmed status first:

| Check | `FailedChecksInARow` afterwards | Confirmed status |
|---|---|---|
| UP | 0 | UP, at once |
| DOWN: timeout, connection, DNS or TLS failure, HTTP 4xx or 5xx | One more | DOWN when the count is 2 (`FailedChecksBeforeDown`) or more, otherwise nothing |
| UNKNOWN | Unchanged: no request was sent, so the row neither grows nor starts over | Nothing |

The count is a column on `MonitoredServices` and is saved with the check, inside the service's lock. It therefore survives a restart, and a manual check and the worker share it. A new URL sets it to 0. Only the message waits: the check record and `CurrentStatus` show DOWN at once, while `LastKnownStatus` is only changed by a confirmed status.

`StatusChangeRule` then compares `LastKnownStatus` with the confirmed status, and is not called when nothing is confirmed. Worker and manual checks share `MonitoredServiceService.CheckAsync`, so they behave the same.

| Last known | Confirmed | Notification | Why |
|---|---|---|---|
| UP | DOWN | Yes | |
| DOWN | UP | Yes | |
| UP or DOWN | The same | No | Nothing changed |
| Any | Nothing: a single failed check, or UNKNOWN | No | Not confirmed. A change to UNKNOWN is logged as a warning |
| Nothing: a new service or a new URL | DOWN | Yes | A misspelled URL that is down from its first checks would otherwise never give an alarm |
| Nothing: a new service or a new URL | UP | No | Nothing to warn about |
| Nothing: a new service or a new URL | UNKNOWN | No | The status stays empty |

The Discord message for DOWN says `is DOWN (2 failed checks in a row)`, also for a new service. The number comes from `FailedChecksBeforeDown`, and the text holds only that constant and our own status, never anything from the site. It is always true: the message is only sent when the count reaches exactly 2, because `LastKnownStatus` then becomes DOWN and later failed checks change nothing. The UP message still says `(was DOWN)`. The text `first check` is gone.

## Security warning rules

Worker and manual checks share `CheckAsync`, so they behave the same. `GET /check` saves nothing and never warns. A warning is worked out from the rows in `CheckRecords` and nothing is kept in memory. A row counts only if it belongs to the service's current URL.

Headers, compared with the newest assessed check:

| Baseline | This check | Warning |
|---|---|---|
| None: a new service or URL | Assessed | No |
| Header present | Assessed, header missing | Yes, one |
| Header missing | Assessed, header missing | No |
| Header missing | Assessed, header present | No |
| Any | Not assessed | No, and the baseline stays |

What a check says about the certificate, read by `CertificateWarningRule.Classify`:

| Finding | Saved fields | Baseline |
|---|---|---|
| `Valid` | A date | Yes |
| `Rejected` | The exact text `The certificate was not valid.` | Yes |
| `NotSeen` | The exact text `No new TLS handshake for this check.` | No |
| `NotLookedAt` | Everything else: a timeout, a connection failure, `http`, an old row, an unknown text | No |

| Baseline | This check | Warning |
|---|---|---|
| None | `Rejected` | No. The DOWN message comes anyway |
| `Valid` | `Rejected` | Yes |
| `Rejected` | `Rejected` | No |
| None or `Rejected` | `Valid` within a threshold | Yes, one |
| `Valid` | `Valid` at a worse threshold | Yes, one |
| `Valid` | `Valid` at the same threshold or none | No |
| Any | `NotSeen` or `NotLookedAt` | No, and the baseline stays |

The thresholds are the setting (default 14) and then 7 and 1, each used only if it is below the setting. Having that many days or fewer left counts, and `DaysLeft` gives the same number as the dashboard. A first check inside a threshold warns because it is a fact about the certificate, and otherwise the next warning would come at the last threshold. An expired certificate is refused by TLS and is covered by `Rejected`.

The redirect target, counted on the rows in `CheckRecords` for the current URL, `RedirectWarningRule` and `SecurityWarningFinder`:

1. The finding is not external (`OtherHost`, `InternalTarget` or `Unreadable`): no warning.
2. `LastCheckedAt` is missing, a new service or a new URL: no warning, the same as for headers.
3. No earlier row with a finding for this URL, the first check after the migration or only timeouts: no warning.
4. The same finding and host exist for this URL within 30 days before the check: no warning.
5. Otherwise one warning.

| Course | Result |
|---|---|
| 200 for months, then 301 to A | Warning |
| A, A, A | One warning |
| A, then B | A warning for B |
| A, B, A within 30 days | No warning for the second A |
| A, 200, A within 30 days | No warning for the second A |
| A, 200 for more than 30 days, A | Warning. Exactly 30 days after A was last seen gives no warning |
| The first check already goes to A | No warning, but A is shown on the dashboard |

`www.` is removed before the comparison, but only if what is left still holds a dot, so `www.com` stays `www.com`. `http` to `https` on the same host is not another domain. The scheme and the port are never saved.

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
- A DOWN message comes one interval later than before: the default is 60 seconds, and with the highest interval it is a whole day.
- A service that is DOWN and UP every other check never gives a DOWN message, because every UP starts the count over. This is a deliberate choice.
- The cause of the timeouts seen in the TLS run is still unknown. The DOWN confirmation hides the false alarms but does not explain them.
- `failedChecksInARow` is shown in the answers from `/services`, because they return the entity. It cannot be set through `POST` or `PUT`; see "Security notes".
- `SocketsHttpHandler.ConnectTimeout` is 10 seconds, even when `TimeoutSeconds` is higher. One address that never answers can use all of it before the next address is tried.
- The local-only check tests hold the application's own stop back with a signal in the test host. Only the order where the application has stopped completely before the test host starts is forced by a test. An order where the test host fails after it has started is covered by the same signal, but no test forces it. See "Local-only check tests without a hidden race".
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
- A service that keeps switching gives one message per change, and one timeout is enough for a DOWN message. The security warnings do not change this: a timeout gives no warning, but it still gives DOWN and UP messages.
- A site that switches between an accepted and a rejected certificate warns at every change to rejected, and there is no limit over time. If it then becomes accepted with few days left, an expiry warning comes as well, because a rejected certificate has no date to compare with.
- A site that switches its headers, such as a CDN with several versions, gives one warning per change. One assessed response is enough.
- A security warning that fails, or a crash between the save and the send, loses that warning. The next check sees no change. An expiring certificate gets a new chance at the next threshold, and a missing header only when it has come back and disappeared again. The dashboard shows the current state all the time.
- During a Discord outage a check can take up to 15 seconds longer: a status message and two warnings, 5 seconds each. A shutdown waits for the same sending. This is calculated from the timeout and has not been measured against the host's own limit.
- Raising the setting also applies to the earlier row, so no warning comes at once. The next threshold warns as usual.
- After changing to another URL and back with no check in between, followed by a timeout, the next assessed check is compared with the old rows of that URL. The comparison is correct, but it does not count as a first check.
- The reasons in `CertificateReasons` are read back from saved rows, so changing a text there makes old rows count as unknown.
- Two checks shortly after each other could in theory send their messages in the wrong order, because they are sent outside the lock.
- A status change can make a manual check answer up to 5 seconds later. During a Discord outage, many changes delay a worker pass by up to 5 seconds per service.
- A name that starts to point to a private address gives UNKNOWN, which is only seen in the log. It could be worth a notification of its own in phase 3.
- A service already UNKNOWN at its first check logs no warning, because nothing changed.
- `GET /services/overview` makes one database read per checked service, on every update. This is simple and fine for a personal tool, but it would need one joined query for many services.
- Two reads can in rare cases give a record newer than the status beside it. The next update corrects it.
- A name with characters that change the writing direction can look odd in the table. It is shown as text and cannot run anything.
- The dashboard shows the whole URL, including any query. The page is local and shows the owner's own data, while Discord is a third party and gets no query. A screenshot for the README must not show a URL with a secret in it.
- The dashboard's decisions run in Jint in the tests, but the page itself cannot be tested without a browser. Playwright would need new packages and a downloaded browser, so it has not been added.
- Jint and a browser can read a time text differently. The seven decimals the API sends are not in the date format of the JavaScript standard, which only describes three. Jint reads them in the tests, and the owner's Chrome run on 2026-11-05 showed a local time from such an answer, but the new format has not been seen in a browser yet.
- The cards are sorted by character code, so the three Swedish letters after z are not in Swedish order.
- The browser asks for `favicon.ico`, which does not exist and is refused by the policy, so one line in the console is expected.
- A security grade measures whether five headers are present, not whether their values are good. `Strict-Transport-Security: max-age=0` and a policy with `unsafe-inline` both count. Every header weighs the same, and a site with `frame-ancestors` but no `X-Frame-Options` counts as missing one.
- A service that answers with a redirect or over `http` never gets a grade, and only the page itself can be assessed: the target of a redirect is not visited.
- Only the first hop of a redirect is assessed. For `example.com` to `www.example.com` to `evil.example`, the second hop is not seen, because redirects are never followed. The owner should therefore monitor the final address.
- Redirects made with JavaScript or HTML are not detected, because only the headers are read and not the page.
- `Unreadable` is one category without a host. After an `Unreadable` row, a new unreadable value gives no warning, so an attacker can use, for example, `/\evil.example` without a new warning. The window moves with every new row, so this holds for as long as the site keeps answering with unreadable values, and for 30 days after the last time.
- `InternalTarget` works the same way: one category and no host, so a new internal target gives no warning after an earlier one.
- A lost redirect warning can give silence for the same target for 30 days, counted from the newest row with that target. A failed send, or a crash between the save and the send, still counts the target as seen. If the target stays, a new row is added at every check, so the silence then lasts for as long as the target stays. The dashboard shows the target all the time.
- An attacker who changes the host name at every answer gives one message per check.
- On the same host, a downgrade from `https` to `http` and another port are not detected.
- Raw UTF-8 in `Location`, for example in the path, gives `Unreadable` and can give a false alarm, because repairing the encoding would be a guess in security code. Such a site also gets the blindness described above.
- A legitimate move, such as a new login domain or a CDN, gives one warning per new target.
- A bare line feed (LF) in a `Location` header ends the header line. The value before it is accepted and can become `OtherHost`, and the rest never reaches the probe result, so it is not saved. Proven by `ARawLineFeedEndsTheHeader` against a local raw server. A raw NUL or carriage return makes the whole value unreadable instead.
- No single automated test runs the connector and the probe together over HTTPS, because the production rules stop loopback addresses and other ports. One group runs the real connector and handler, and another the real probe and handler with a local connection step.
- A probe check always opens a new connection, so `responseTimeMs` always includes TCP and TLS. Before, a check within a minute of the last one could reuse a connection.
- The certificate note is kept per host and not per port or per check, because the callback cannot see either. `https://host:443` and `https://host:80` checked at the same moment could get each other's date. Two checks of the same host at the same moment share a note, so one of them can show that nothing was seen. Neither can affect the validation or the status.
- A certificate's revocation is not checked, the same as before this feature. A certificate that fails validation shows no date, so the dashboard cannot say when an already invalid certificate expired.
- `LocalOnlyCheck` runs after the server has started, so a wrong port is open for a moment. Enabled services can start a check in that moment as well.

## Code style exceptions

These parts do not yet follow the project's plain style, on purpose:

- `PublicInternetConnector.ConnectAsync` keeps a `try`/`catch` around the socket instead of `using`. The socket must be closed on failure but stay open when it is handed to the stream.
- `PublicInternetConnector.ConnectAsync` keeps an explicit check of an empty DNS answer, although the last line of the method would also stop one. This is defence in depth: it states the intent, and the protection must not depend on how the rest of the method is written. The tests therefore stay green if the check is removed.
- `MonitoringWorker` keeps three `catch ... when` filters. Replacing them would duplicate the log lines.
- The queries in the repository stay as LINQ, because EF Core translates them to SQL.
- `UtcDateTimeOffsetConverter` is a class with an abstract `Read` method that no request uses, because `System.Text.Json` has no setting for the time format.
- `UrlSafety.GetConnectionHost` uses `try`/`catch` and returns null. `Uri.IdnHost` has no version that avoids the exception, and a host that cannot be converted must be rejected.
- `UrlSafety.ParseAllowedForm` keeps the check of `UriHostNameType.Unknown`, although no address is known to reach it and the tests stay green without it. The protection must not depend on how `Uri` happens to work. See "Finish A".
- `ExpectedErrors.TranslateAsync` checks for 415 after the next middleware instead of catching an exception, because the framework throws nothing for a wrong content type.
- `DiscordWebhookUrl.IsAllowed` has many separate checks, some of which overlap. They are kept on purpose: the protection must not depend on how the other checks happen to work. The mutation check shows that removing the ASCII check changes no test.
- `ValidateOnStart` is kept for Discord although the startup line reads the options anyway, for the same reason.
- `MonitoredServiceService.SendNotificationAsync` catches `Exception` but does not pass it to the logger, because its text could hold the webhook address.
- `SecurityHeaders` sets its headers in a callback from `OnStarting` instead of directly, because the exception handler empties the headers before it writes a 500.
- `RedirectTargetParser` is security-critical and longer than the rest of the code. It decides the form of a value with plain string checks before `Uri` is used, because `Uri.TryCreate("/a", UriKind.Absolute, ...)` can give `file:///a` on Linux. Five of its checks are covered by another step as well, and are kept on purpose so that the protection does not rely on how `Uri` or the other checks happen to work: three slashes at the start, a slash after `http://` or `https://`, more than one `@` in the authority, lower case, and empty labels. The mutation check shows that removing any one of them changes no test.
- `CrossSiteRequests` has two checks that overlap on purpose: not every browser sends `Sec-Fetch-Site` to `localhost`, and the protection must not depend on one header.

## Security notes

Mass assignment: `POST /services` and `PUT /services/{id}` read `RegisterServiceRequest` and `UpdateServiceRequest`, which hold only `Name`, `Url`, `IntervalSeconds`, `TimeoutSeconds` and `IsEnabled`. A client that sends `failedChecksInARow`, `currentStatus` or `lastKnownStatus` cannot change them, because those values are only set by a check. Proven by `ServiceInputProtectionTests`, which read the database directly, and by the mutation that lets the requests carry the three values.

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

The TLS certificate:
- No new way out. The certificate is read by a callback on the probe's own handler, during the handshake of the connection that carries the request. There is no separate `TcpClient` or `SslStream`, and `PublicInternetConnector` was not changed.
- Certificate validation is never turned off. The callback only takes notes and answers `errors == SslPolicyErrors.None`, which is the framework's own rule when no callback is set.
- The application has no setting for trust. A test sets `CertificateChainPolicy` on a handler it built itself through an internal method, and two tests prove that the registered handler has none.
- A certificate is written by whoever made it, so only a date is kept and no text. The reasons are our own fixed texts of at most 64 characters.
- Ending every check's connection also means that the address is checked again on every check, for the probe. The window of 2 minutes on a reused connection no longer applies to it.

Not proven by a test:
- That the socket is closed when a connection attempt is cancelled.
- That looking up `localhost` stays on the machine. The tests assume this and it has not been measured.

Known problems:
- There is no authentication or rate limiting. `GET /check` is open, and `GET /services/{id}/check` writes data. Other sites are now stopped, but any program on this machine still has full access. This is acceptable on localhost only and must be solved before any deployment.
- Internal hostnames pass registration. They are stopped at connection time, after the DNS lookup has already been made.

Security headers:
- No new way out: the headers come from the probe's existing request, through the same handler. `SecurityHeaderHandlerTests` reaches `127.0.0.1` with a connector built in the test, through the internal `Create` method. No setting in the application opens loopback, and `RegisteredHandlerBlocksLoopbackAddress` proves that the registered handler still blocks it.
- The values are not stored. Only "is it there?" is answered, and what is saved comes from a fixed list of names, so a hostile or long value cannot reach the database or the page. The dashboard writes these fields with `textContent` only.

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

The security warnings:
- A warning holds only the service's name, our own fixed texts and, for a redirect to another domain, the target's host name. The host is the only thing a monitored site has written. A name has passed the allowlist in `RedirectTargetParser`: only `a-z`, `0-9`, `.` and `-`, at most 253 characters. An IP address is written again by `IPAddress.ToString` in its shortest form, so it holds only hex digits, dots and colons. The header names come from the fixed list in `SecurityHeaderRule`, the number of days is one of our own thresholds, and no URL, date or number of days left is sent.
- The name goes through the same cleaning as in a status message: cut at 100 characters, a backtick replaced, and written as inline code, which stops links and previews. The empty `parse` turns off every ping whatever the formatting.
- That cleaning now also replaces line separators, paragraph separators and invisible format characters, which `char.IsControl` misses. This applies to status messages as well. A joined emoji loses its joiner.
- There is no new way out: warnings use the existing Discord client and its protected handler.

The redirect target:
- No new request and no DNS. `AllowAutoRedirect` stays `false` and `ProtectedHttpHandler` was not changed. The parser only uses `Uri.TryCreate`, `Uri.IdnHost` and `IPAddress.TryParse`. Proven by tests where the local server never sees the target and the fake DNS is asked only about the original host.
- The raw `Location` value never leaves the parser. `RedirectTargetCheck` has no field for it, the parser logs nothing and throws nothing with the value in it. Only a fixed category and at most one host are saved. User name, password, path, query, fragment, scheme and port are never saved, shown or sent. Proven by a test with a marker in each of them that is found neither in the history, the overview, Discord nor the log.
- The value is read through `response.Headers.NonValidated`, because the typed `Headers.Location` hides several values and broken values.
- Anything that a browser could read differently from .NET gives `Unreadable`, and the form is decided with string checks before `Uri` is used.
- Discord: the host is written without a scheme and as inline code, so Discord neither links it nor fetches a preview, and the empty `parse` turns off every ping. With the allowlist these are three independent layers. A private or internal target is never shown, only a fixed text.
- The dashboard writes the host with `textContent`, never as a link, and the Content Security Policy is unchanged.

The dashboard:

It only reads. There is no login, so a button would give anyone on the machine an easier way to change data, and writing from a page needs a CSRF protection that belongs with the login. With one `fetch` that only reads, a test can also show that the script does not write.

Names, addresses and failure reasons come from users and servers, and reach the page through `textContent` only, or as a tooltip through the `title` property. Addresses are never links. Class names are always the page's own. Failure reasons are fixed texts or `HTTP` and a status code today, but they are treated the same way.

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

**Next step:** `ci/github-actions` is built and its text tests pass locally. The owner reviews and merges it, and the first real run on GitHub happens after the push to `main`. Before that, `feat/dashboard-design` was built and proven by tests. The owner runs the manual test, then reviews and merges it. The manual test of the DOWN confirmation (`fix/single-timeout`) is still to be run.

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
- DOWN is only notified after 2 failed checks in a row, so a single timeout gives no messages (`fix/single-timeout`, proven by tests; the manual test is still to be run).
- First channel: a Discord webhook, behind an `INotifier` interface in Application, so that more channels can be added without touching the core logic.
- The webhook URL is a secret. It belongs in user secrets or environment variables, never in `appsettings.json` or git.
- Tests use a fake notifier or a stub handler and make no real calls.

### Phase 5: Dashboard

Built on `feat/dashboard` and merged. The server side is verified by tests, and the page by the owner's run in Chrome, except for the parts listed under "Not run manually".

- A simple web page showing all services, status and response time. Phase 3 adds certificate days left and header grade to it.
- `feat/dashboard-design`: one card per service instead of a table. Built and proven by tests; the manual test is still to be run. See "The dashboard as cards".
- The dashboard runs locally only until the authentication in phase 6 is done.
- The simplest technology that is enough, for example a static HTML page calling the API. No heavy frontend framework without a reason.

### Phase 3: Security checks

- TLS certificates: read the expiry date on each check, save it, and warn when a configurable number of days or fewer remain (default 14).
- Security headers: check `Strict-Transport-Security`, `Content-Security-Policy`, `X-Content-Type-Options`, `X-Frame-Options` and `Referrer-Policy`. Give each service a simple grade and save which headers are missing.
- Warn when a certificate is about to expire, or when a header has disappeared since the previous check.
- All network traffic goes through the same SSRF protection as the health check. No new ways out.
- The new values are added to the dashboard.

The phase is built as four tasks, one branch each, in this order:

| Task | Branch | Content | Part of the MVP |
|---|---|---|---|
| 1 | `feat/security-headers` | Read the five headers, give a grade, save it per check, show it in the API and the dashboard | Yes |
| 2 | `feat/tls-certificate` | Read and save the certificate's expiry date, the first automated HTTPS test through the connector, days left in the dashboard | Yes |
| 3 | `feat/security-warnings` | Warnings through `INotifier`: a certificate about to expire (configurable, default 14 days) and a header that has disappeared since the previous check | Yes |
| 4 | `feat/redirect-target` | Save `Location` safely and warn when the host moves to another domain. Built, proven by tests, and manual test passed on 2026-11-10 | No |

Headers come first because they are already in the response the probe receives, so nothing about how connections are opened has to change. The certificate is the only task that changes the connection step. The warnings compare data that the first two tasks save.

Requirements for the tasks:

- **Task 2, built on `feat/tls-certificate`:** Certificate validation must never be turned off. An invalid certificate must give no grade. A certificate that is missing on a reused connection must not be read as invalid. A test that needs HTTPS must trust only its own test certificate, set from the test, and production code must have no setting for it.
- **Task 4:** `Location` must never be sent unprocessed to Discord or to the logs, and is shown as text only, never as a clickable link.

### Phase 4: DevOps

- GitHub Actions: build and test on every push to `main`, and by hand. There is no pull request trigger, by the owner's decision. Built on `ci/github-actions`; see "Phase 4, first part: GitHub Actions".
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
| 3 | 3xx counts as UP and is not followed, so `http://example.com` answering 301 gives only the redirect's headers and no certificate. That would produce a misleading grade and false alarms | Decided: only `https` with a 2xx gets a grade. Everything else gets a fixed reason and no grade |
| 3 | Save the redirect target (`Location`) and warn when it suddenly points to another domain, because that can mean the site has been hijacked. The value comes from the monitored server and can contain internal addresses, so how it is stored and shown must be decided | Decided on 2026-11-09 and built on `feat/redirect-target`: never follow the value, compare only the host with `www.` removed, show it as plain text and never as a link, hide a target that is not a public address, no warning at the first check, fixed texts for unreadable and internal targets, and a window of 30 days against switching. See "Security warning rules". Legitimate moves, such as a new login domain or CDN, give one warning per new target |
| 3 | A certificate is only visible during the TLS handshake, and a reused connection does no new handshake. "On each check" therefore does not work with pooled connections | Decided: the probe asks for a new connection on every check with `Connection: close` over HTTP/1.1. The local cost was measured; see "Cost of reused connections". A check that still sees no handshake says so and is not called invalid |
| 4 | Docker tooling licence | Not settled. Nothing is installed until the licence has been confirmed |
| 4 | Inside a container the application must listen on `0.0.0.0`, which `LocalOnlyCheck` refuses. Compose binds to `127.0.0.1` on the host, which the check cannot see | The check must be turned off explicitly in the container. No such setting exists yet. `AllowedHosts` keeps working, as the browser still sends `localhost` |
| 6 | With a login the check, `AllowedHosts` and the block of other sites must be looked at again, as a real host name and other origins may then be valid | Decide with the login |
| 5 and 6 | A static page cannot hide an API key | Design the authentication with the dashboard in mind |
| 6 | Rate limiting is planned for `/check`, but `POST /services/{id}/check` and `GET /services/{id}/check` also start network requests, and the locks do not limit checks of different services | Cover all the check routes in phase 6 |

## Phase 6, first part: the GET check route is removed

Done on `fix/remove-get-check`, on 2026-11-11. Proven by tests; the manual test is still to be run.

**`GET /services/{id}/check` is removed.** It ran a real check, the same as `POST /services/{id}/check`: it sent a request, saved a history row, changed the status and the count of failed checks, and could send an alarm or a security warning. A GET must not change anything, because a link, an image tag or a prefetch can send one without the owner noticing. No code in the application used it: the dashboard calls only `GET /services/overview`, and the worker calls the service directly in C#. `POST /services/{id}/check` and `GET /check` are unchanged.

A GET on the old path now gives **405 Method Not Allowed**, because a POST still exists on the same path. The test `GetOnTheCheckRouteChangesNothing` proves that such a GET sends no request, saves no history row, changes nothing on the service and sends no alarm. It was run red with the route still there, and turned red again when the route was put back.

Tests removed or shortened, because they only proved the removed route:

| Test | Change |
|---|---|
| `NotificationBehaviourTests.CompatibilityRouteNotifiesLikeTheOtherRoutes` | Removed. The POST route and the worker have their own notification tests. `WorkerNotifiesLikeTheManualRoutes` is renamed to `WorkerNotifiesLikeTheManualRoute` |
| `ResponseShapeTests.CompatibilityCheckRouteReturnsServiceFieldsAndIsNotCached` | Removed. It locked the field names of an answer that no longer exists |
| `ResponseShapeTests.EveryCheckRouteReturnsStatusAsText` | The GET step is removed. POST, the service, the history and `GET /check` are still checked |
| `SecurityWarningFlowTests.OlderCompatibilityRouteAlsoWarns` | Removed. POST and the worker are covered by the other tests in the file |
| `TimeFormatTests.EveryRouteWritesTheStartOfTheCheckInUtcWithZ` | The GET step is removed. POST, the history, the list, the service and PUT are still checked |
| `CrossSiteRequestTests.AllApiRoutes` | The GET row is removed, 3 test cases. The POST row and `GET /check` remain |

**SQL commands are no longer logged.** `appsettings.json` sets `Microsoft.EntityFrameworkCore.Database.Command` to `Warning`. EF Core writes every SQL command on Information, and the worker reads the database every second, so the log used to fill up with "Executed DbCommand" lines. Development reads `appsettings.json` first, so the setting is not repeated in `appsettings.Development.json`. `DatabaseCommandLoggingTests` proves it in Development and Production. It was red before the change. The test reads the log text that EF Core writes, so it would pass without proving anything if EF Core changed that text.

**A new URL after a failed check.** `TwoFailuresOnANewUrlAfterOneOnTheOldUrlGiveOneDownWithoutAnOldStatus` runs UP, a failed check, a new URL, and two failed checks on the new URL. It proves that exactly one DOWN alarm is sent, at the second failure on the new URL, with no previous status. The code already worked, so the test was green from the start. Mutations proved that it catches the errors: without the reset of the count on a new URL it fails (as does `NewUrlStartsTheCountOver`), and without the reset of the last known status it fails (as do three older tests).

Result: `dotnet build DeadSwitch.slnx` 0 warnings, 0 errors. `dotnet test DeadSwitch.slnx` 1552 passed, 0 failed, 0 skipped, on Windows. Before the task 1554 passed; 6 test cases were removed and 4 added. No package and no migration was added.

Not proven:

- Whether a script or bookmark outside the repository used `GET /services/{id}/check`.
- What a GET from another site to the removed path gets. The middleware may answer 403 before routing gives 405. Either way nothing is run, but it is not tested.
- `POST /services/{id}/check` does not send `Cache-Control: no-store`, which the removed route did. That is unchanged and not looked at here.
- The logging change against the real server. Environment variables or user secrets on a machine can still change the log levels.

## Phase 4, first part: GitHub Actions

Built on `ci/github-actions`, on 2026-11-12. The text tests pass locally. **The workflow has never run on GitHub, and no test has ever run on Linux.**

**The workflow** is `.github/workflows/build-and-test.yml`. It runs `dotnet restore`, `dotnet build` and `dotnet test` on `DeadSwitch.slnx`, the same commands as locally, in Debug and with no filter.

| Setting | Value | Why |
|---|---|---|
| Triggers | Push to `main`, and `workflow_dispatch` | No pull request trigger, by the owner's decision |
| `permissions` | `contents: read`, at the top level only | The workflow only reads the code |
| Runner | `ubuntu-24.04` | A fixed version. `ubuntu-latest` changes without any change in the repository |
| `timeout-minutes` | 15 | `dotnet test` took 14 seconds locally. The margin covers installing the SDK and the restore |
| `actions/checkout` | `3d3c42e5aac5ba805825da76410c181273ba90b1`, v7.0.1 | Pinned to a full commit SHA, because a tag can be moved. `persist-credentials: false` keeps the token out of `.git/config` |
| `actions/setup-dotnet` | `0f4a354dd5547a28c70b7853dbd95d5b67886e73`, v6.1.0 | Pinned the same way |
| .NET SDK | `10.0.301` | There is no `global.json`. The projects target `net10.0`, and this is the SDK on the development machine. Update it in the file when the local SDK is updated |
| `env` | `DOTNET_CLI_TELEMETRY_OPTOUT: 1` only | Stops the CLI from sending usage data from the runner. No `TZ` and no other setting is used to make tests pass |

The owner fetched the SHAs with `git ls-remote` from the repositories of the `actions` organisation and compared them with the releases page.

No secret and no webhook is used. No test was changed for Linux.

**`WorkflowFileTests`** reads the file as text, line by line, and has 9 tests: `permissions` at the top level holds only `contents: read`, and no other `permissions` exists; no `pull_request`; every `uses:` has a full lower case commit SHA of 40 characters and a version comment; `persist-credentials: false`; `timeout-minutes`; `runs-on: ubuntu-24.04` and no `-latest`; `dotnet-version: '10.0.301'`; no `secrets.`; no `TZ`. It does not understand YAML, so a wrong indentation is not caught.

Red runs, each a temporary change to the file that was then removed. Every change turned only its own test red:

| Change | Red test |
|---|---|
| Placeholders instead of SHAs, a 7-character SHA, a SHA in upper case, no version comment, `@v4` | `EveryActionIsPinnedToAFullCommitShaWithAVersionComment` |
| `contents: write`, an extra `actions: read`, `permissions` on the job | `PermissionsAtTopLevelOnlyAllowReadingTheCode` |
| `pull_request:` | `ThereIsNoPullRequestTrigger` |
| `persist-credentials: true` | `CheckoutDoesNotKeepTheToken` |
| No `timeout-minutes` | `TheJobHasATimeout` |
| `ubuntu-latest` | `TheRunnerVersionIsFixed` |
| `'10.0.x'` | `TheSdkIsTheSameAsOnTheDevelopmentMachine` |
| `${{ secrets.X }}` | `NoSecretIsUsed` |
| `TZ: Europe/Stockholm` | `NoTimeZoneIsSet` |

A control with a SHA of 40 zeros made all 9 green. The file was then compared byte for byte with the original.

Result: `dotnet build DeadSwitch.slnx` 0 warnings, 0 errors. `dotnet test DeadSwitch.slnx` 1561 passed, 0 failed, 0 skipped, on Windows. Before the task 1552 passed; 9 tests were added. No package and no migration was added.

Tests that use the network:

- No test was found that reaches the internet. HTTP goes to local servers on `127.0.0.1`, to stubs, or through a fake DNS. This is found by reading the code and is not measured on Linux.
- `SsrfProtectionTests.RegisteredHandlerBlocksLocalhostName` looks up `localhost` with the system's own resolver. On Ubuntu this should be answered from `/etc/hosts`, but it has not been measured.
- The restore fetches packages from nuget.org on the runner.

No test checks for Windows. These may behave differently on Linux, and none of them was changed:

- The TLS tests. `TestCertificates` loads its certificate again through PKCS12 for Windows. On Linux, OpenSSL builds the chain with `CustomRootTrust`, offers HTTP/2 and gives the error flags.
- `RedirectTargetParserTests`. `Uri.TryCreate("/a", UriKind.Absolute, ...)` can give `file:///a` on Linux, as the parser's comment says.
- `UrlSafetyTests` with `/relative/path`. On Linux it is likely to become a `file` address and be stopped by the scheme check instead of by `TryCreate`. The test should still pass, but through another line.
- `NextAddressIsTriedWhenFirstDoesNotAnswer` expects `127.0.0.2` to refuse the connection. The server listens on `127.0.0.1` only, so this should hold on Linux as well.
- The runner uses UTC. A test that turns a local time into UTC then tests less than on the development machine.
- The runner has another culture than the development machine. Not every format in the code has been checked for it.
- Linux checks the files out with LF and the development machine with CRLF. No test was found that compares line endings in the served files.
- Tests that wait for something, up to 10 seconds, can become flaky on a slower runner.
- The runner has no user secrets. `TestApp` sets `Discord:WebhookUrl` itself, so this should not matter.

Not proven until it has run on GitHub:

- That GitHub accepts the file. `workflow_dispatch` and the push trigger only work once the file is on `main`, so the first real run comes after the merge and the push.
- That `setup-dotnet` installs exactly 10.0.301 on `ubuntu-24.04`, and that the build has no warnings there.
- That all tests pass on Linux.
- That no traffic leaves the runner except for NuGet and GitHub.
- That 15 minutes is enough.
- That the token can only read. The log shows this under "GITHUB_TOKEN Permissions".
- That the telemetry is turned off. The text test only proves that the line exists.

## Startup validation before app.Run()

Built on `fix/startup-validation-race`, on 2026-11-12.

**The problem.** `SecurityWarningSettingsTests.InvalidSettingStopsTheApplicationWithAFixedText` failed once in three GitHub Actions runs on the same commit, with `ObjectDisposedException: 'IServiceProvider'` instead of `OptionsValidationException`. `WebApplicationFactory` runs `Program.cs` on a thread of its own, and that thread keeps going after `builder.Build()` while the test host starts the application. A bad `SecurityWarnings:CertificateWarningDays` failed inside `app.Run()`, where `RunAsync` disposes the host before the exception leaves `Main`. When that came first, the test host read a disposed service provider.

**The red test.** `TestApp` got `WaitForProgramBeforeStart`. When it is set, `CreateHost` first checks that the thread of `Program` was captured and is not the test's own thread, then waits for that thread to end with `Thread.Join` (10 seconds only as a limit against a hanging test), and only then starts the host. This forces the bad order every time. Both startup failure tests set the flag.

| Run | Before (on `main`) | Forced order, no fix | Forced order, with fix |
|---|---|---|---|
| Both test classes (24 tests), 20 times | 20 of 20 green | Run twice (red test and mutation), not 20 times. Both times the 3 `SecurityWarningSettingsTests` cases red with `ObjectDisposedException` from `DeferredHost.StartAsync`, 21 green | 20 of 20 green |
| Whole suite (1561 tests), 3 times | 3 of 3 green | not run | 3 of 3 green |

`DiscordConfigurationTests.InvalidAddressStopsTheApplicationWithAFixedText` stayed green with the forced order even before the fix. `Program.cs` reads `IOptions<DiscordOptions>.Value` before `app.Run()`, and that runs the Discord validation, so a bad address never reached `RunAsync`.

**The fix.** One line directly after `builder.Build()`: `app.Services.GetRequiredService<IStartupValidator>().Validate();`. It runs exactly the checks that `ValidateOnStart` registered, so a new options class with `ValidateOnStart` is covered without a list to keep up to date. `ValidateOnStart` stays. A bad setting now leaves `Main` before the host is disposed and before anything is written to the log.

**Mutation.** With the line removed, the three `SecurityWarningSettingsTests` cases were red again with the same exception, and the Discord cases green. With the line back, all green, and the diff of `Program.cs` held only the fix.

**Behaviour that changed:**

- The log lines "Discord notifications are turned on/off" and "Hosting failed to start" are not written when the start fails. The unhandled exception with the fixed text is still written by the runtime.
- If both the Discord address and another setting are wrong, the error is now an `AggregateException` with both, as `ValidateOnStart` already gave. Before, only the Discord error was shown. No test covers this.

Not proven:

- The internals of the test host in version 10.0.12 were not read. The red and green runs show the behaviour, not the code.
- That this exact order caused the failed run on GitHub. It is concluded from the stack.
- Nothing of this has run on Linux.

**Manual run** by the owner, with `SecurityWarnings:CertificateWarningDays` set to `0` and `dotnet run --launch-profile http`, on Windows:

| | Before the fix (`main`) | After the fix |
|---|---|---|
| `info: ... Discord notifications are turned on.` | written | not written |
| `fail: Microsoft.Extensions.Hosting.Internal.Host[11] Hosting failed to start` | written | not written |
| `Unhandled exception. Microsoft.Extensions.Options.OptionsValidationException: CertificateWarningDays must be between 1 and 365.` | written | written, same text |
| Stack | `StartupValidator.Validate` → `Host.StartAsync` → `RunAsync` → `Run` → `Program.cs` line 153 | `StartupValidator.Validate` from `Program.cs` line 119 |
| Exit code | `-532462766` | `-532462766` |

## Local-only check tests without a hidden race

Built on `fix/local-only-check-race`, on 2026-11-12. Only test code changed. The real application behaves as before: the same critical log line and exit code 1.

**The problem.** `LocalOnlyCheckTests.AnAddressOnTheNetworkStopsTheApplication` caught `ObjectDisposedException` when it started the application. The check in `LocalOnlyCheck.StopIfListeningBeyondLoopback` stops the application from `ApplicationStarted`, and `RunAsync` then disposes the host on the thread of `Program`. If that came before the test host had read its services, the test host read a disposed service provider. The catch hid that order instead of controlling it. Validating before `app.Run()` does not help here, because this stop can only happen after the server has started and its addresses are known.

**The red test.** With the catch removed and `WaitForProgramBeforeStart` set, the thread of `Program` has ended before the test host starts. All three cases were red with `ObjectDisposedException` from `DeferredHost.StartAsync`, every time.

**The fix.** `PretendedServerAddress.StopAsync` waits for `TestApp.TestHostStarted` before the application goes on to dispose. The test reads `Services`, sets the signal, and waits with `Thread.Join` until `Program` has ended. Then it checks that `ApplicationStopping` happened, that the exit code is 1 and that the critical line was logged. `TestApp.DisposeAsync` sets the signal too, so a test that never sets it does not wait. The wait in `StopAsync` has a limit of 30 seconds, only against a hang.

A change in `Program.cs` was rejected. Returning without disposing the host would keep the services alive, but the console log might then not be written out before the process ends.

| Run | Before (on `main`) | Forced order, no fix | With the fix |
|---|---|---|---|
| `LocalOnlyCheckTests` (26 tests), 20 times | 20 of 20 green (the catch hid the race) | 1 run: 3 red, 23 green | 20 of 20 green, every stopping case under 250 ms |
| Whole suite (1561 tests), 3 times | 3 of 3 green | not run | 3 of 3 green |

**Mutation.**

- Without the wait in `StopAsync`, and with the forced order: 3 red with `ObjectDisposedException` from `DeferredHost.StartAsync`.
- With the wait and the forced order: 3 red with "Program did not finish within 10 seconds." after 10 seconds each. This shows that `Program` cannot end before the test host has started.

No case took near 30 seconds and no `TimeoutException` was seen, so the wait does not lock the test host.

Not proven:

- The internals of the test host in version 10.0.12 were not read.
- Only the order where `Program` has ended before the test host starts is forced. An order where the test host fails after its start, when it asks for the server, follows from the reasoning and is covered by the same signal, but no test forces it.
- How often the catch was hit before. It left no trace.
- Nothing of this has run on Linux.

## Finish A

Built on `feat/finish-a`, on 2026-11-12. Four parts, one commit each. This section replaces older statements where it says so; the older rows are left as they were written.

### Part 1: small security matters

**`POST /services/{id}/check` sends `Cache-Control: no-store`.** The answer describes one moment, so nothing may keep it and show it again. The header is set first in the endpoint, so it is also there on a 404 and on the 409 that the error translation writes afterwards. A 500 goes through the exception handler, which empties the headers and sets its own. This replaces the line "`POST /services/{id}/check` does not send `Cache-Control: no-store`" under "Phase 6, first part". A browser does not normally keep the answer to a POST, so the risk was small; the header states the intent and makes the two check routes alike.

Three tests in `ServiceCheckTests`: `ManualCheckAnswerIsNotStored` (200), `ManualCheckOfMissingServiceIsNotStored` (404) and `OverlappingCheckAnswerIsNotStored` (409). All three were red first, because the header was missing. Mutation: with the line moved to after the check, the 409 test is red and the other two green.

**`UrlSafety`: the check of `UriHostNameType.Unknown`.** What the line stops was looked for with a temporary test of about 1600 odd addresses: an empty host, a single dot, broken IPv6 forms, percent-encoded hosts, and every character from 0 to 0x2FF as a whole host and inside a host. None of them reached the line with `Unknown`: each was either refused by `Uri.TryCreate` or given another type. With the check removed, the whole suite stayed green. The line is kept as an explicit line of defence, and its comment says that no address is known to reach it. No test proves it.

A side finding, not changed here: `http://-/` and `http://_/` get the type `Basic`, pass this line and are accepted as ordinary names. What happens to them at connection time was not run.

**`DiscordWebhookUrl`: the two limits of the ASCII check** are now explained in a comment: the space and everything below it, and everything above `~`, which is DEL and every character outside ASCII. A row with DEL was added to `DiscordWebhookUrlTests`; it was green at once, because no behaviour changed. With the ASCII check removed the whole suite is still green, as noted under "Code style exceptions": the comparison with `AbsoluteUri` stops the same addresses.

**`ServiceLocks`: comments only.** Three comments were corrected and no code was changed.

| Comment | Before | After | Why |
|---|---|---|---|
| The class | "Stops the API and the worker from working on the same service" | Names the callers: a check from the API or the worker, an update or a delete | Two requests to the API are stopped as well, and the old text did not say so |
| `EnterAsync` | "The caller must call Release when done" | Adds that a cancelled wait never took the lock, and that `Release` must then not be called | The old text could be read as "always call Release" |
| `LockEntry.Gate` | "Disposing it could also fail" | Says that it is a later `Gate.Release` on a disposed semaphore that throws | `Dispose` itself does not fail |

The other comments in the file were read against the code and left as they were. The statement that a `SemaphoreSlim` only needs disposing when `AvailableWaitHandle` has been used comes from the .NET documentation and was not checked again here.

Result: `dotnet build DeadSwitch.slnx` 0 warnings, 0 errors. `dotnet test DeadSwitch.slnx` 1565 passed, 0 failed, 0 skipped, on Windows. Before the part 1561 passed; 4 test cases were added. No package and no migration was added.

Not proven:

- That no address at all can reach the `Unknown` check. Only the forms listed above were tried.
- The `no-store` header against the real server and a real browser.

### Part 2: the Discord texts

Every message now answers three questions in one line of plain text: which service, what happened, and when. Nothing that the dashboard already shows better is repeated.

| Kind | Text, for the service `Shop` checked at 2026-11-05T07:15:53Z |
|---|---|
| DOWN | ``DeadSwitch: `Shop` is DOWN (2 failed checks in a row). Reason: HTTP 500. Checked <t:1793862953:f>.`` |
| UP | ``DeadSwitch: `Shop` is UP (was DOWN). Checked <t:1793862953:f>.`` |
| Headers | ``DeadSwitch security warning: `Shop` no longer sends these security headers: Content-Security-Policy, X-Frame-Options. Checked <t:1793862953:f>.`` |
| Certificate rejected | ``DeadSwitch security warning: `Shop` now has a TLS certificate that is not valid. Checked <t:1793862953:f>.`` |
| Certificate expiring | ``DeadSwitch security warning: `Shop` has a TLS certificate with at most 14 days left. Checked <t:1793862953:f>.`` |
| Redirect to another domain | ``DeadSwitch security warning: `Shop` now redirects to another domain: `other.example`. Checked <t:1793862953:f>.`` |
| Redirect to an internal target | ``DeadSwitch security warning: `Shop` now redirects to a private or internal address. Checked <t:1793862953:f>.`` |
| Redirect that cannot be read | ``DeadSwitch security warning: `Shop` now sends a redirect whose target cannot be read. Checked <t:1793862953:f>.`` |

What changed:

- **The URL is no longer sent in a DOWN or UP message.** Discord is a third party and the dashboard shows the address. `RemoveEverythingAfterThePath` and the URL limit of 300 are removed with it. `StatusChange.ServiceUrl` is still there but `DiscordMessage` no longer reads it.
- **The time is Discord's own tag, `<t:SECONDS:f>`,** with the seconds since 1970 in UTC. Discord shows it as a date and a time of day in each reader's own time zone. It replaces the ISO text that ended with `Z`. The time is when the check started, so the word is `Checked` and not `since`. Parts of a second are dropped.
- **The security warnings carry the time as well.** `SecurityWarning` has the new value `CheckedAt`, which `SecurityWarningFinder` takes from the check record. Application carries the time and Infrastructure formats it.
- **Every security warning starts with the service's name.** The header and certificate texts were reworded; the three redirect texts only got the time.

Unchanged: the empty `allowed_mentions.parse`, `FormatAsCode`, the name limit of 100 in Discord (the validation limit of 200 is untouched), and the redirect host as inline code with no link. The tag is built from our own clock, holds only digits and stands outside the code format. No new value from a monitored site reaches Discord.

This replaces these older statements: "the times ended with `Z`" in the manual run of 2026-11-05 describes the old text; "The name and URL come from the user" and "A query is removed" under "The Discord webhook" (only the name is sent now); and "no URL, date or number of days left is sent" under "The security warnings" (the time of the check is now sent; still no URL, no certificate date and no number of days left). The text `no longer sends`, `was rejected` and `has at most 365 days left` in "Not run manually" are the old wordings.

Tests: 3 exact texts in `DiscordNotifierTests`, 5 in `SecurityWarningDiscordTests` and 4 in `RedirectWarningDiscordTests` were updated. `QueryIsNotSentToDiscord` and `UrlIsCutAtItsLimit` were replaced by `UrlIsNeverSentToDiscord` (2 cases) and `LongestUrlIsNeverSentToDiscord`, which require that no host, path, query or scheme is in the message. New: `TimeIsWrittenInDiscordsOwnFormat`, `ATimeTagInANameStaysInsideTheCodeFormat`, `MessageIsOneLine`, and the class `SecurityWarningTimeTests` with one test per kind of warning. `RealNotifierSendsAWarningWithoutTheUrlOrAnyDate` is renamed to `RealNotifierSendsAWarningWithoutTheUrlOrTheCertificateDate`, and `HeaderMessageNamesTheServiceAndTheHeadersAndNothingElse` to `HeaderMessageNamesTheServiceTheHeadersAndTheTime`.

Red run, against a skeleton where the new value existed but was empty and the texts were the old ones: 23 of 1573 failed, all on assertions. `ATimeTagInANameStaysInsideTheCodeFormat` and `MessageIsOneLine` were green from the start and are proven by mutations 9 and 10 instead.

| Mutation | Red |
|---|---|
| 1. The URL is put back in the status message | 7, among them both `UrlIsNeverSentToDiscord` cases and `LongestUrlIsNeverSentToDiscord` |
| 2. An ISO time instead of the tag | 16: `TimeIsWrittenInDiscordsOwnFormat` and every exact text |
| 3. Milliseconds instead of seconds | The same 16 |
| 4. The tag inside the code format, in the status message | 4 |
| 5. The finder gives every warning an empty time | 6: the four `SecurityWarningTimeTests` and the two tests of the whole chain |
| 6. No time in the security warnings | 12: every exact warning text |
| 7. No `(was DOWN)` | 2 |
| 8. No count of failed checks | 3 |
| 9. The name without the code format, in the status message | 17, among them `ATimeTagInANameStaysInsideTheCodeFormat` |
| 10. A line break before `Checked` | 4, among them `MessageIsOneLine` |

After each mutation the file was copied back and compared byte for byte.

Result: `dotnet build DeadSwitch.slnx` 0 warnings, 0 errors. `dotnet test DeadSwitch.slnx` 1573 passed, 0 failed, 0 skipped, on Windows. Before the part 1565 passed; 2 tests were replaced and 10 test cases added. No package and no migration was added.

Not proven:

- How Discord shows `<t:...:f>` in a webhook message. The tests prove what is sent. The format was written from memory of Discord's documentation, which was not read again here.
- That the tag stays unformatted when a name holds one. The test proves that it is sent inside the code format.

### Part 3: two small changes to the dashboard

Only the dashboard's files and their tests changed. `index.html`, the Content Security Policy, the API and the database are untouched, and the page shows nothing new: the same four counts as before.

**The grade stays beside the name on a narrow screen.** The top row of a card is a flex row that wraps. On a narrow screen the address used to wrap to a second line and take the grade with it. A media query for screens of at most 600 px now lets the name take the rest of the first line, which pushes the grade to the right edge, and gives the address `order: 1` and the whole width, so it comes after the grade on a line of its own. The order in the page is not changed, and a wider screen looks as before.

**The numbers in the summary have the colour of their status.** `formatSummary` in `dashboard-logic.js` is replaced by `getSummaryParts`, which returns the count, the word and a class name for each group. The class names are the page's own: `count-up`, `count-down`, `count-pending` and `count-unknown`. `dashboard.js` builds the summary from separate pieces of text, written with `textContent` as everything else. Read as one line it is the same text as before, for example `2 UP | 1 DOWN`. Only the number is coloured, and the word is always there, so the meaning never depends on the colour. The colours are the ones the status badges already use.

Tests: the three summary tests in `DashboardLogicTests` keep their names and their expected texts, and now read the parts. New: `EveryNumberInTheSummaryGetsTheClassOfItsGroup`, `TheSummaryIsEmptiedAndBuiltFromTheParts`, `EveryNumberClassHasItsColour` (4 cases) and `ANarrowScreenMovesTheAddressBelowTheNameLine`. In `TheValuesFromTheServerAreWrittenAsText` the row for the old summary line is replaced by three rows for the new calls.

Red run before the change: 13 of 1582 failed. The 4 logic tests failed because the function did not exist yet, and the 9 text tests on their assertions.

| Mutation | Red |
|---|---|
| 1. Two class names swapped in the logic | `EveryNumberInTheSummaryGetsTheClassOfItsGroup` |
| 2. The number written without its class | `TheValuesFromTheServerAreWrittenAsText` |
| 3. No `order` on the address | `ANarrowScreenMovesTheAddressBelowTheNameLine` |
| 4. The wrong colour for one class | `EveryNumberClassHasItsColour` |
| 5. One group counted as another | 3 logic tests |
| 6. The summary not emptied before it is built | None at first. A check of that line was added to `TheSummaryIsEmptiedAndBuiltFromTheParts`, which then turned red |

Result: `dotnet build DeadSwitch.slnx` 0 warnings, 0 errors. `dotnet test DeadSwitch.slnx` 1582 passed, 0 failed, 0 skipped, on Windows. Before the part 1573 passed; 9 test cases were added. No package and no migration was added.

Not proven:

- How the page looks. The tests of the style sheet read it as text: they prove that a rule is there, not what a browser does with it. The narrow layout, the limit of 600 px and the colours in light and dark mode are only shown by the owner's run in a browser.
- That the summary is really emptied on every update. The test only finds the line in the script.
- How a screen reader reads the summary now that it is several pieces of text.
