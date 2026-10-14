# Round 54 acceptance using inherited Round 53 user matrix (not browser accepted)

`user-matrix.json` is the fixed inventory for 22 AC, 10 user journeys, 6 quality objectives, six roles, and four synthetic source kinds. Run its integrity check with:

```powershell
pwsh -NoProfile -File scripts/round53-user-matrix/Test-UserMatrixManifest.ps1
```

The fixture is launched from an explicit completed build and an immutable synthetic role bundle. The launcher checks `build-identity.json` against the selected Web/Core DLL hashes, requires the same parseable `lfbuild-v1` source stamp and matching informational-version/override tokens, then copies the selected assemblies and metadata reader independently verifies the exact copied runtime assemblies that Web will load. It hashes the selected static asset tree before and after copying. It performs SQLite online backup into a unique owned run, copies the key, disables external source/mail/schedule and local-host analysis settings in the clone, and verifies the source DB/key stayed byte-identical. It starts Web and controlled sources only on loopback and writes exact PID/start-time/executable/command-line/port ownership records. The launcher never seeds run status, finding, risk, workflow, case, or mail outcomes.

Use the paths from the latest completed build, not the older v11/v12 fixtures:

```powershell
$webOut = '<latest completed LogForesight.Web bin output>'
$webAssets = '<same build Web wwwroot assets>'
$expectedSourceCommit = '<40-hex source commit frozen by root for this build>'
$sourceBundle = '<owned immutable synthetic role bundle with verified DB/key hashes>'
$started = & scripts/round53-user-matrix/Start-UserMatrixFixture.ps1 -WebOutputDirectory $webOut -WebAssetsDirectory $webAssets -SourceFixtureBundlePath $sourceBundle -ExpectedSourceCommit $expectedSourceCommit
$started | Format-List
# Root operates only the printed loopback login URL, then stops the exact owned processes:
& scripts/round53-user-matrix/Stop-UserMatrixFixture.ps1 -OwnerFile $started.OwnerFile
```

Use `Set-UserMatrixMode.ps1 -OwnerFile <owner> -HttpMode <mode> -SmtpMode <mode>` to switch bounded controls while the fixture is running. HTTP modes include `netiq-healthy`, `netiq-events`, `netiq-risk-events`, `netiq-audit-cleared-events`, `netiq-risk-events-ai-hold-prtg-503`, `late-events-held`, `late-events-released`, `prtg-healthy`, `ai-healthy`, `all-sources-healthy`, and explicit source failure/hold modes. `netiq-audit-cleared-events` returns exactly one bounded Security event from `Microsoft-Windows-Security-Auditing` with event ID 1102 and an explicit `r53FixtureSourceKind=synthetic` marker. It is a loopback-only fixture for exercising the existing audit-clear detector; it does not create a persisted record or claim a severity/outcome. SMTP modes are `accept`, `reject-next-recipient` (one 451 response), and `drop-after-data` (connection drops after DATA before 250, so no accepted message is captured). Every connection remains on 127.0.0.1; message capture records recipient, byte count and hash, while `smtp-inbox.eml` stores the accepted synthetic message for local inspection.

The source helper also has `late-events-held`: it completes the Sentinel search with zero events. Switch to `late-events-released` before the next normal UI run to return one bounded synthetic event for the same host/time shape. `prtg-healthy` supplies a small PRTG JSON/XML response set based on parser fixtures already exercised by this repository; `ai-healthy` returns an OpenAI-compatible synthetic chat completion explicitly stating no real model was called. These modes test only loopback request/response parsing. They do not qualify native sources, prove new-site readiness, or establish a production-quality model result; normal UI semantic qualification and trusted-evidence gates still apply. The helper refuses SMTP sender/recipient addresses outside `.invalid` and discards HTTP query strings from its request log. On Windows, `HttpListener` sockets can be reported under HTTP.sys PID 4; mode switching and stop accept that OS listener only when the exact helper process remains verified and its loopback health response carries this run's ID. SMTP still must be owned by the exact helper PID.

For Local or custom-proxy OpenAI-compatible AI, set `AiBaseUrl` to the server origin or to a path that already ends in `/v1`. The shared resolver ensures one `/v1` segment before `/chat/completions`, retaining proxy prefixes and non-secret query parameters. The controlled source helper serves only `/v1/chat/completions`; it deliberately returns 404 for `/v1/v1/chat/completions` so a doubled-version client path cannot look healthy. `Test-ControlledSources.ps1` checks both paths against the actual loopback HTTP listener.

The expected commit is an explicit fence: it must match the `lfbuild-v1` source token in both Web/Core identity and remains part of the fixture manifest and every recorded browser observation. Do not fill this from an assumed “latest” label. To test the controlled source protocols and SMTP sink independently, run `pwsh -NoProfile -File scripts/round53-user-matrix/Test-ControlledSources.ps1`; it verifies bounded NetIQ event/late-arrival data, successful synthetic PRTG JSON/XML and AI chat-completion responses, NetIQ/PRTG/AI failures, privacy-safe request logs, SMTP 451/accept/unknown-commit behavior, and rejection of non-`.invalid` addresses. For AC/U/Q/rule inventory consistency without claiming acceptance, run `pwsh -NoProfile -File scripts/round53-user-matrix/Test-UserMatrixCoverageInventory.ps1`; the fixed source matrix validator is `pwsh -NoProfile -File scripts/round53-user-matrix/Test-UserMatrixManifest.ps1`. For actual start/mode-change/stop lifecycle and source-clone immutability against a selected build, run `Test-UserMatrixFixtureLifecycle.ps1` with the three paths plus `-ExpectedSourceCommit $expectedSourceCommit`; it writes `BrowserAcceptance=not-performed`. The cloned `schedule_options.LocalAnalysisEnabled` is explicitly false (UI: 「系統 > 排程作業 > 分析本機主機」) so synthetic NetIQ runs do not read the Web host's local Security log. For clone timestamp and consecutive EF/SQLite concurrency-token mutation checks, run `Test-UserMatrixSqliteConcurrency.ps1 -WebOutputDirectory <selected build output> -SourceFixtureBundlePath <owned role bundle>`; it builds only under its ignored per-run directory and checks that source DB/key hashes remain unchanged. To prove bad build claims never reach a listener, run `Test-UserMatrixBuildFence.ps1` with those paths plus an actual `LogForesight.Core.dll` from a different build. It rejects a forged MVID manifest and a mixed-source Core/Web pair. To create a preparatory matrix file, use `Initialize-UserMatrixEvidence.ps1 -ExpectedSourceCommit $expectedSourceCommit`; the initializer checks the same commit token before writing the pending matrix.

The shortest role pass is six independent sessions on the same run origin: owner-handler marks and replies to the assigned issue; read-only manager attempts Handle/Assign/Maintain; restricted Dev opens Dev Monitor then tries Maintain and handling routes; case-grant handler opens its one granted case and tries host-wide PRTG detail; empty-visible checks dashboard plus direct host/case URLs; Maintain opens settings and exports evidence. Record each result against its role ID and AC21/U53-05; record file/hash plus source identity against AC01/AC20/U53-10. Capture visible controls, response/empty state for each denied route/API, and actual download where applicable. Do not visit the displayed PRTG URL. The AC/U/Q links are prefilled in the run manifest, so each browser observation should be attached to the specific IDs rather than described as a single matrix pass.

### Restart the owned Web process over the same run database

`Restart-UserMatrixWeb.ps1 -OwnerFile <owner.json>` restarts only the exact verified Web PID. It keeps the controlled HTTP/SMTP helper alive, relaunches the copied Web DLL with the same command line, URL, build, static assets, data root and key, then updates the existing owner and fixture manifest with the new PID/start-time and a durable restart receipt. It uses a per-run lifecycle lock shared with `Stop-UserMatrixFixture.ps1`; mismatched owner/manifest/process/build/data-root identities, foreign listeners, and concurrent lifecycle actions fail closed. It records before/after database SHA-256 values when the open database permits hashing, without requiring those hashes to match because startup reconciliation can make legitimate database changes.

The isolated acceptance script starts a real loopback Web/source fixture, writes a unique harmless sentinel with production `StorageBackend.Blob`, captures read-only hashes/IDs for the existing run/record/case/work-order tables, runs wrong-owner/PID/build/data-root refusal checks, restarts the Web process against the same `data` directory, then reads the sentinel through a new production store instance. It verifies key hash, database path/application/schema identity, selected durable rows, retained source-helper PID, synchronized owner/manifest process entries, and the restart receipt; it stops the exact owned fixture at the end. It writes evidence only under that new owned run and does not create findings, cases, risks, assignments, exports, or pass outcomes.

```powershell
& scripts/round53-user-matrix/Test-UserMatrixWebRestart.ps1 `
  -WebOutputDirectory $webOut `
  -WebAssetsDirectory $webAssets `
  -SourceFixtureBundlePath $sourceBundle `
  -ExpectedSourceCommit $expectedSourceCommit
```

This controlled same-database restart is U53-08 component evidence only. It does not complete browser acceptance, all recovery branches, or N6/full-capacity acceptance.
### Prepare an actual owner-handler case (normal UI workflow)

The immutable role bundle contains inherited synthetic records and a case-grant case used only to test authorization. Normal Web startup may reconcile that inherited case into a work order. Neither inherited outcomes nor startup reconciliation prove a newly produced NetIQ/PRTG risk. The launcher clones this data without inserting new findings, cases or source-run outcomes. Before testing a new source-driven owner-handler case, use a Maintain session on the run's printed login URL and operate only on the run-owned SQLite clone and loopback sources:

1. In Sentinel maintenance, add a `R53-LOOPBACK` server with Base URL equal to the printed `SourceHttpBaseUrl` (for the current root-owned run this is `http://127.0.0.1:1475`), synthetic credentials (`r53.synthetic` / `r53-fixture-only`), Windows OS and ESM directory off. Test connection, save, and activate it. The fixture implements the app's actual Sentinel REST sequence: token POST with `Token`, event-search POST with `filter/start/end/pgsize/max-results/type/init-user/InitiatingHostName`, `201 + Location`, status `status/found/avail/results.href`, and `{events:[...]}`.
2. In host maintenance, find the cloned ordinary host by its `192.0.2.3` address and existing synthetic name. Confirm it remains active and has only the intended `fixture-…-ordinary` owner; set its NetIQ server to exactly `R53-LOOPBACK`, preserving its owner list. Do not add or edit any case/finding data.
3. Set the controlled source to `netiq-risk-events` with `Set-UserMatrixMode.ps1` and run a manual one-host NetIQ analysis from the normal Runs UI. Select the ordinary host, one-day backfill, no PRTG values, no rerun. Wait for the run to finish; verify the run activity reports a successful host/day and the record detail shows a real mapped 4625 finding. The stub supplies 12 bounded synthetic 4625 events to cross the existing count threshold (10); the UI output, not this count alone, is the evidence.
4. As Maintain, open that host/day's handling panel. Confirm the handler defaults to the single host owner; explicitly save/assign that handler in the normal UI. This operation builds a case only for actual unhandled-severity issues in the generated record. Verify the UI reports a created/open case and a corresponding work order; do not insert a case or claim assignment before this result exists.
5. Sign in as `fixture-…-ordinary` in a separate browser session on the same origin. Open `/handlers/4` only after confirming ID 4 is still the ordinary role in this cloned source bundle, then open the listed case. Mark/reply using the visible controls and confirm the response in the UI. Capture screenshots and record only what was actually observed.

If the run finishes without a mapped 4625 record, stop before assignment and record the exact source/run/record state as inconclusive; do not lower the 10-event rule threshold, change the fixed role bundle, or fabricate a case. Root owns browser interaction and must record this journey as a real browser observation. This path covers one synthetic owner-handler scenario only; it does not pass the remaining E08 matrix.

Save each actual screenshot or sanitized text note under `<BundlePath>\evidence\browser-artifacts`, then append a manually assessed observation for its matrix ID. The recorder binds the note to that run's source stamp and Web/Core/static-asset hashes, but does not grade the screenshot or change aggregate readiness:

```powershell
$artifact = Join-Path $started.BundlePath 'evidence\browser-artifacts\U53-05-owner-handler.png'
& scripts/round53-user-matrix/Record-UserMatrixBrowserObservation.ps1 `
  -OwnerFile $started.OwnerFile -RequirementId 'U53-05' -ObservedResult inconclusive `
  -Observation 'Describe only the visible result and the direct-route checks actually performed.' -EvidenceFile $artifact
```

For coverage, use the fixed ID links below to record evidence one requirement at a time; one shared happy-path screenshot cannot pass an entire row.

| Journey | Linked acceptance criteria | Quality objectives |
| --- | --- | --- |
| U53-01 | AC01, AC07, AC08, AC17, AC22 | Q1, Q6 |
| U53-02 | AC06, AC11, AC13, AC22 | Q2 |
| U53-03 | AC07, AC08, AC10, AC22 | Q4 |
| U53-04 | AC11, AC22 | Q2 |
| U53-05 | AC14, AC21, AC22 | Q2 |
| U53-06 | AC06, AC12, AC14, AC22 | Q3, Q4 |
| U53-07 | AC02, AC05, AC09, AC22 | Q1, Q6 |
| U53-08 | AC01, AC09, AC12, AC15, AC16, AC22 | Q5 |
| U53-09 | AC02, AC03, AC04, AC21, AC22 | Q6 |
| U53-10 | AC01, AC17, AC18, AC19, AC20, AC22 | Q1, Q6 |

For U53-08, a compact operation order is: `netiq-healthy` with PRTG disabled; enable the loopback PRTG endpoint and run `prtg-503`; set AI base to `<SourceHttpBaseUrl>/v1` and use `ai-hold` or `netiq-risk-events-ai-hold-prtg-503`; run once with `late-events-held`, then switch to `late-events-released` and run again; finally use `Restart-UserMatrixWeb.ps1` to restart the verified Web process over the same run-owned database while leaving the controlled source helper alive. At each step record the UI's parent/source/AI/PRTG status plus persisted run, batch, receipt, and workflow identifiers. This is a source-progress subset, not all of U53-08's cancellation, retry, disabled-scope, and reconciliation conditions.

Configure source settings through the normal UI using only the printed source endpoints: create a Sentinel pointing at `SourceHttpBaseUrl`, select a synthetic `192.0.2.x` host, set PRTG URL to the same HTTP stub, set Local AI base URL to `<SourceHttpBaseUrl>/v1`, and set SMTP to `127.0.0.1:<SourceSmtpPort>` with TLS/auth off. Keep schedules disabled; the clone starts with source calls and mail off. Use `.invalid` recipient addresses only. Those settings are written through normal UI actions and remain inside the run-owned SQLite clone.

For U53-08, through the normal Runs controls, capture NetIQ success/zero events, then switch to a held or failed PRTG branch, disabled PRTG, synthetic late input, and AI pending/failure; finally retry/restart only within the run-owned fixture. Compare progress API state with the persisted run summary and batch/receipt/workflow IDs. Keep NetIQ risk visible when AI or PRTG fails. The stub emits bounded synthetic Sentinel REST responses and controlled PRTG/AI failures only; it does not establish native protocol compatibility. For U53-05/AC15, enable notification for the synthetic `.invalid` recipient, set SMTP mode to `reject-next-recipient`, observe the failed claim in UI, switch to `accept`, retry through the UI, then inspect accepted capture plus persisted intent/recipient state. `drop-after-data` is an optional accepted-commit-unknown observation.

## Known gaps

- The current launcher is build-parameterized, hashes the selected artifact at runtime, and requires Root to pass the exact expected 40-hex source commit; it contains no pinned product SHA. Root still has to supply and freeze the current Round 54 frozen clean Web output and matching assets before browser operation.
- The source HTTP stub emits the Sentinel REST token/job/event response, bounded PRTG/AI failures, and synthetic PRTG JSON/XML and OpenAI-compatible AI success responses; it does not claim native-source qualification or real-model quality. SMTP local accept/reject/unknown-commit shapes are executable, while browser retry remains to be operated and recorded by Root.
- E08 audit finding `RUNS-DATE-SEMANTICS-001`: commit e8350207 adds a visible execution-day D / NetIQ data D-1 / PRTG data D caption and hash tab navigation. Root independently built and browser-verified the caption, deep link, keyboard switch and reload on an owned loopback fixture. The original popover already explained the NetIQ offset. Final same-version summary/drilldown checks remain part of full U53-08; this subcheck does not complete that journey.
- The browser observation recorder is a log helper only. It requires an already captured file and operator-entered result; it does not verify that a browser was used, inspect pixels, grade the UI, or mark any aggregate gate complete.
- The currently listed worker scenarios do not complete U53-01 through U53-10, all 22 AC, or Q1-Q6. In particular full 3,000/15,000 scope, every mapping conflict/stale write, each of eight-rule fixed matrix in user-facing detail, recovery faults, export/import, retention/capacity, and self-service documentation remain separate gates unless current valid evidence is mapped to the same code and environment.
- Markup/API contracts, synthetic seeds, stub responses, and this manifest are preparation evidence only. UC01/04/05/06 stay reminders; they do not block the isolated agent acceptance.


### Direct export API authority subcheck

`Test-UserMatrixApiExportAuthority.ps1` takes the same explicit Web output, assets, immutable role bundle and expected source commit as the launcher. It builds helpers in a private temporary stage, starts a real owned Web/SQLite copy, signs in six synthetic identities, and requests the actual acceptance export. Maintain must receive bounded format-v2 JSON; the five other roles must receive HTTP 403 without export payload fields. It checks source DB/key hashes, stops exact owned processes and verifies persisted stopped status. Raw responses and the actual export remain in the run evidence directory. Results retain `BrowserAccepted`, `MatrixAccepted` and `WholeRoundAccepted` as false: this is an API permission subcheck, not the entire AC21/U53-05 journey. Root independently verified the current frozen 467776/c52b122 artifact; no external source or onsite acceptance was used.

### Composed synthetic PRTG/NetIQ protocol mode

`Set-UserMatrixMode.ps1 -OwnerFile <owner.json> -HttpMode netiq-risk-audit-prtg-healthy` selects the loopback-only composite mode after validating the exact run owner, helper process, source assembly hash, and run-local mode file. It combines the existing 12-event synthetic 4625 risk shape with one separately marked synthetic 1102 audit-clear event and PRTG device 112 at `192.0.2.3`, with sensors 212–214 parented to that device. PRTG channels are positive IDs 1212–1214; the per-sensor primary-channel property, XML history, and JSON history agree on channel and value. Sensor rows expose stable synthetic `cumsince`, current-check text/OA fields, and 900-second interval fields. The clock is stable within each UTC quarter-hour and advances automatically for long-running fixtures. Inventory supports the production sync query `content=sensors&id=112`, exact sensor `filter_objid` intersections, repeated filters, count, and start pagination. Historic XML/JSON parsing honors `now-1h`/`now` and `yyyy-MM-dd-HH-mm-ss` query ranges, returning an empty result outside the requested interval and HTTP 400 for malformed bounds. The JSON row uses production `datetime`, `datetime_raw`, `value_raw`, and `coverage_raw` fields. The existing 5301 Ping response and standalone 12-row `netiq-risk-events` behavior remain available.

`Test-ControlledSources.ps1` makes actual HTTP requests to verify wrong IDs and filter intersections return no sensors, pagination selects the expected row, and all three resource IDs preserve primary-channel/value/time coherence across sensors, channels, property XML, historic XML, and historic JSON. It also checks malformed history times fail, a current one-hour window returns one row, and a past window returns none. It then verifies 12 risk events plus one synthetic audit-clear event while PRTG requests succeed in that same mode. All source records remain synthetic and loopback-only. These protocol checks do not set up a PRTG binding, provide timezone or other time-basis authority, create a qualification proof/job/outcome, write application data, establish two completed analysis hours, or provide 28-day value history.

## Round 54 review additions and maintenance roles (planned only)

The original six roles, 22 AC, 10 journeys, six Q, seven source scenarios and 222 fixed rule cases remain. The 53 inherited uncovered entries are a baseline, not a maximum. `coverage-inventory.json.round54ReviewAdditions` adds R54-01 through R54-08 and linked C54-01 through C54-05 contract corrections, each with original TODO IDs, AC/journey links, required checks and pending evidence. The authoritative behavior is in `docs/FEEDBACK-54-PLAN.md` §§4.6–4.8. No new role is provisioned and no validator/launcher/recorder is implemented by this planning update. Existing consistency scripts cover only their existing schema checks; supplementary checks must be implemented in A2 before claiming the new matrix is executable.

Use the current Round 54 frozen clean build and exact expected commit, matching Web/Core/static assets and owned fixture/environment hashes. Do not select a Round 53 build merely because the directory/schema retains its historical name. Reuse historical evidence only after verifying unchanged code/configuration/environment and the exact covered subset. No external site or subjective user confirmation is required.

| Maintenance scenario | Identity/capabilities | Readable data | Expected operation |
|---|---|---|---|
| M54-rescue | Real serverAdmin login; Maintain, no ViewAll | Necessary global counts and safe reasons; no business names/IDs | Global purge only under current preview and safety gates |
| M54-global | Ordinary Maintain + ViewAll | All eligible details with declared pagination | Same global purge set under the same scope |
| M54-readall | ViewAll without Maintain | No maintenance preview payload | Preview and confirm denied |
| M54-nomaintain | Scoped user without Maintain | No maintenance preview payload | Preview and confirm denied |

Each of the four real-login scenarios uses an isolated session; test direct APIs as well as controls. Capture capabilities/visible set, global-vs-visible counts, no-leak responses, accepted preview identity and exact deletion outcome. Include hidden devices, more than 20 visible candidates, orphan sensors and zero-visible accounts. Actual rescue authentication must not be replaced by renaming an ordinary user.

| Review ID | Planned stage | Required result |
|---|---|---|
| R54-01 | F2 | Version-bound purge, concurrency protection and exact partial/retry results |
| R54-02 | C1 | One backend analysis-date contract including timezone/midnight/DST |
| R54-03 | F1 | Distinct actionable states and authorized repair/reverification flow |
| R54-04 | F2 | Honest global impact disclosure with restricted readable details |
| R54-05 | B2 | Upgrade/change/replay/mail lifecycle without trusting legacy authority |
| R54-06 | D1 | Character limits, provider boundaries, isolated failure and safe repair |
| R54-07 | A2 | Supplementary role identity and frozen Round 54 build/evidence |
| R54-08 | A2 | Substage traceability and mandatory omission-repair loop |

### Corrected contract boundaries (planning only)

Four maintenance scenarios are real-login acceptance: M54-rescue, M54-global, M54-readall and M54-nomaintain. `maintenanceRoleMatrix.syntheticDefenseCases` retains M54-scoped solely as a synthetic authorization-component defense. Ordinary Maintain currently implies ViewAll; do not provision this impossible account, inject claims for browser acceptance, add a role or expand the authorization model. The original six-role matrix is unchanged.

C54-01 maps to R54-07/A2; C54-02 to R54-06/D1; C54-03 and C54-05 to R54-01/F2; C54-04 to R54-05/B2. All are pending contract checks, not additional passed tests. Shared unreadable disk evidence blocks all dependent disk qualifications while independent NetIQ/evidence/notifications continue; do not partially trust damaged JSON or redesign storage merely to meet isolation wording.

Manual purge and both automatic callers (PrtgDailyPipeline, PrtgStructureSyncRunner) share ownership and durable partial-progress protection. Automatic purge binds the successful sync generation and keeps existing scope/shrink guards, without requiring manual preview. A manual preview lasts five minutes from server creation and binds actor/permission version, source/mapping/keep-set and candidate data boundary. Duplicate accepted confirmation returns the same operation. Permission loss before acceptance means zero deletion; during execution it stops at the next safe point before any new batch. Resume needs current Maintain and unchanged accepted scope/generation; otherwise obtain a new preview and operation linked to previous partial results. Never silently add newly eligible data to a prior approval.

A2 freezes an exact previous artifact and paired pre-upgrade DB/key/config backup for B2 rollback. Setting restoration is distinct from binary rollback. Same-data downgrade is allowed only with explicit compatibility evidence; otherwise reject it and use paired restore. Verify cases/replies/jobs and mail dedupe, account for changes after the backup and never blindly resend externally accepted mail after restore. PLAN §4.9 specifies authoritative details. No feature, fixture or validation script is implemented by this update.
