# E2 owned HTTP qualification preparation

Page snapshots require a positive, unchanged `readStateVersion` as well as the job lease version and wave. The lease version alone does not advance on each qualification commit. A qualified row must also prove the current identity epoch and channel generation through its persisted `qualifiedIdentityEpoch` and `qualifiedChannelGeneration`; missing legacy proof fields cannot authorize a changed binding or channel.

This directory adds the D54-02 preparation entrypoint for the later G launcher. It is separate from the older fixture-only lifecycle scripts. `Prepare-Qualification.ps1` accepts only a complete owned SQLite source receipt, an exact 3,000-device / 15,000-sensor / 3,000-map / 64,800,000-value catalog, a saved provider descriptor, an operator-reviewed public settings plan, and an explicit reviewed binding plan. It never seeds the database, changes generator checkpoints, or marks capacity, retention, native-source, formal, or whole-round acceptance.

The source preflight reads `n6-run-manifest.json`, `n6-high-water-result.json`, the E1 ownership marker, and catalog counts in SQLite read-only/query-only mode. It also validates the reviewed monitoring plan's time-basis reference using the public API's exact safe-reference rules before any settings or source-policy write. The completed checkpoint must be absent; any pending checkpoint, incomplete count, foreign marker, mismatched manifest checksum, or promoted acceptance flag fails before Web requests. It emits a stable source-identity digest on stdout and never overwrites a fixed preflight receipt. The command-line preflight always uses the fixed production denominator. Tiny contract tests call its in-process validator with test-only expected counts; the CLI has no count override.

The Web process must be started with an explicit configuration equivalent to:

```text
--Storage:DataRoot=<owned-root>
--Storage:Type=Sqlite
--Storage:ConnectionString=Data Source=<owned-db>;Pooling=False;Cache=Shared
--E2:ProviderDescriptorSha256=<sha256-of-provider-descriptor>
```

The saved provider descriptor is a separate, operator-owned file. Its minimum shape is:

```json
{
  "schema": "e2-owned-provider-v1",
  "provider": "sqlite",
  "dataRoot": "C:\\owned\\round54-full-source",
  "databaseFile": "C:\\owned\\round54-full-source\\n6-sqlite-<run-id>.db",
  "retentionDays": 180,
  "prtgUrl": "http://127.0.0.1:5100/"
}
```

The command-line descriptor hash must be the SHA-256 of these exact file bytes. The separate operator-reviewed settings plan is a sparse body for the normal public compare-and-save route:

```json
{
  "schema": "round54-e2-settings-plan-v1",
  "evidenceReference": "approved settings change record",
  "expectedRevision": "revision copied from the current public settings GET",
  "prtgEnabled": true,
  "allowDisabledBootstrap": true,
  "prtgUrl": "http://127.0.0.1:5100/"
}
```

It may include only non-secret public PRTG setting fields from `UpdatePrtgSettingsRequest`, plus the explicit `allowDisabledBootstrap: true` marker for a first-run bootstrap. For either `Pilot` or `Start`, bootstrap saves the reviewed source settings disabled, saves the reviewed source/time-basis policy, runs the snapshot admission pilot and up to five profile-transport pilot samples, then enables only through the normal guarded settings route. The tool verifies the exact enabled or disabled settings readback and revision when reconciling an unknown enable result. Resume/Check never write source settings, source policy, bindings, admission pilots, or qualification pilots. If an ordinary settings save response was lost, it continues only when the public GET matches every requested field; a stale revision with different values is refused. The completed public settings must match the descriptor's loopback `prtgUrl`.

After settings, `Prepare-Qualification.ps1` applies an explicit monitoring source plan through the normal public route sequence: `GET api/prtg/monitoring`, `POST api/prtg/monitoring/estimate`, `POST api/prtg/monitoring/source-preview`, then `PUT api/prtg/monitoring` with the public revision, catalogue token, and fresh estimate token. It reads back and compares every source-identity/time-basis field and the complete host/sensor scope before requesting the qualification contract. A lost save response is reconciled only by exact public readback; Resume/Check never perform source-policy writes. No `unknown` source mode or continuity without evidence is accepted.

The operator-reviewed monitoring plan is a separate JSON input (`round54-e2-monitoring-plan-v1`) with `evidenceReference`, the exact `expectedRevision` from public monitoring GET, `coreSystemId`, `sourceTimeZoneId`, `sourceCultureName`, `messageDateFormat`, `rawTimestampTimeZoneId`, `analysisTimeZoneId`, `timeBasisEvidenceReference`, `sourceChangeMode` (`new` or `continue`), `identityConfirmed: true`, explicit `hostIds` and `sensorIds`, and continuity confirmation/evidence only for `continue`. It must list exactly 3,000 unique positive host IDs and 15,000 unique positive sensor IDs. `timeBasisEvidenceReference` must be 1–128 UTF-16 code units and cannot contain control characters, `://`, `/`, `\`, `?`, `#`, or `@`, matching `SafeTimeBasisReference` in the normal API. Both the PowerShell plan check and Python preflight enforce this contract before any write route. These IDs and source identity/time-basis inputs come from an authorized operator's reviewed scope; the saved time-basis reference records operator input and does not claim remote-account readback or native sample verification. The tool does not discover, guess, truncate, or infer them. The source plan hash is sealed into the same partial receipt, so a retry cannot change scope silently. `continue` requires `continuityConfirmed: true` and non-empty `continuityEvidenceReference`; `new` rejects continuity claims. The separate binding plan then supplies each selected sensor's channel, semantics, interval unit, and matching raw/analysis zones and evidence reference.

Before each Web request, the script checks there is exactly one listener on the selected port and that its exact loopback address and owning PID match. It then calls the separate Maintain-only `GET api/admin/prtg-runtime-ownership/status` seam over that loopback connection. The response reports the actual loaded provider, SQLite data source, data root, process start identity, loaded Web/Core assembly locations and MVIDs, and the descriptor hash read from the running process's `IConfiguration`; it contains no connection string or business payload. The verifier independently reads the selected Web/Core files' PE metadata with a read-only parser (it does not load or execute them) and requires both disk MVIDs and loaded locations to match. It also compares the endpoint's actual configuration/backend values with local process argv, selected paths, and the descriptor file hash before every API call. SQL Server, missing runtime metadata, an unset descriptor hash, forwarded/non-loopback callers, or any mismatch fail closed. The descriptor hash remains an explicit configuration declaration, not independent proof of source contents. Receipt output contains only token count/hash, not raw command line.

The binding plan is an operator-reviewed file no larger than 8 MiB:

```json
{
  "schema": "round54-e2-binding-plan-v1",
  "rows": [
    {
      "sensorObjid": 1000001,
      "channelObjectId": "1234",
      "expectedCaption": "Disk Used Percent",
      "quantity": "DiskUsedPercent",
      "unit": "%",
      "scale": 1,
      "direction": "used",
      "intervalRawUnit": "seconds",
      "rawTimestampTimeZoneId": "UTC",
      "analysisTimeZoneId": "UTC",
      "timeBasisEvidenceReference": "operator-reviewed evidence ID"
    }
  ]
}
```

The production file must contain all 15,000 sensors in the same ascending order as the public policy catalog. Channel IDs, captions, units, scale, direction, timezones, and evidence references must come from operator-reviewed source evidence; the tool does not infer them from synthetic value rows. Existing bindings are compared field-for-field and skipped only when they match the plan. Missing bindings are saved through `POST api/prtg/monitoring/trusted-sampling/bindings/batch` in batches of at most 100, with current settings/policy/identity/channel/binding revision fences. Rejected rows or a partial commit leave a receipt; rerunning re-reads live state and only skips exact matches. A changed binding is never silently overwritten.

The public sequence is settings/source-policy bootstrap when needed, exact profile catalog read, normal binding batch saves, bounded qualification pilot (`POST .../qualification-jobs/pilot`, at most five sensors), then the durable qualification job (`POST .../qualification-jobs/start`). `Pilot` also completes a required initial disabled bootstrap and qualification pilot, then stops before creating the durable job. `Start` is hard-pinned to 720 hours and one attempt per sensor. Resume requires the exact saved job ID, version, and wave and uses only the existing resume route; Check is read-only. The original manifest and receipt digest remain sealed. Resume/Check retain the original settings, scope, source-generation, raw-authority, identity, channel, and binding-plan fences; a DateFormat-only policy revision may advance without replacing the sealed policy revision. A changed binding is accepted only when the durable job page proves the current binding revision/fingerprint and qualification proof, and every page read is enclosed by an unchanged job version/wave snapshot. Receipt CAS continues to reject settings, scope, source, or raw-authority drift. The job and receipt still report source/authority and qualification only; they do not claim physical-native authority, full capacity, retention, or formal acceptance.

If the start response is lost after submission, the durable receipt marks the outcome as unknown. The operator must inspect the public `qualification-jobs/current` response and provide its exact job ID, version, and wave to `Check` to attach the read-only outcome to the receipt. `Resume` uses the same exact identifiers and only succeeds when the public service says the job is resumable. Changed settings, raw source/time-basis authority, scope, source generation, identity/channel generation, binding semantics, descriptor, binding plan, or E1 receipt refuses replay before further writes. A message DateFormat-only revision can continue because it does not change the captured raw authority; the original manifest and policy revision remain unchanged.

Example entrypoint (run only against an owned, approved Web process and source root):

```powershell
$session = [Microsoft.PowerShell.Commands.WebRequestSession]::new()
# Authenticate the existing Maintain session using the normal application login flow.
pwsh -NoProfile -File .\Prepare-Qualification.ps1 `
  -Action Pilot `
  -BaseUri 'http://127.0.0.1:5100/' `
  -SourceRoot 'C:\owned\round54-full-source' `
  -ProviderDescriptorPath 'C:\owned\round54-full-source\provider.json' `
  -SettingsPlanPath 'C:\owned\round54-full-source\settings-plan.json' `
  -MonitoringPlanPath 'C:\owned\round54-full-source\monitoring-plan.json' `
  -BindingPlanPath 'C:\owned\round54-full-source\binding-plan.json' `
  -WebAssemblyPath 'C:\release\LogForesight.Web.dll' `
  -ManifestPath 'C:\owned\round54-full-source\qualification-job-manifest.json' `
  -ReceiptPath 'C:\owned\round54-full-source\qualification-preparation-receipt.json' `
  -WebSession $session
```

The E2 contract tests use small, test-owned temporary SQLite databases and synthetic receipts to check the accepted receipt/catalog shape and refusal of partial counts, a pending checkpoint, a foreign ownership marker, stale manifest linkage, and acceptance promotion. A pinned checksum vector cross-checks `System.Text.Json`'s Windows CRLF manifest bytes; the retained 205-row E1 manifest also matched its stored producer checksum without opening its database. Runtime ownership token tests use synthesized identities. `test_contracts.ps1` executes the same pure PowerShell functions used for listener PID extraction, revision fences, mixed binding outcomes, stale/rejected pilots, exact job identity, and the fixed attempt window. Static route checks confirm normal public settings/profile/binding/pilot/job endpoints. No Web service or full qualification route was started; no 64.8M catalog scan, source generation, capacity service, 41M database access, or formal qualification was run here.
