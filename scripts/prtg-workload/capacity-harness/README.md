# E07 capacity prerequisite fixture

This tool prepares a disposable Web data root from a **completed** SQLite output of `storage-generator`. It first validates the generator receipt against the unchanged 3,000-host, 15,000-sensor, 180-day, 64,800,000-row, four-slot shape. It fingerprints the generator receipt and the source database plus SQLite sidecars, copies the database and WAL bytes without opening the source through SQLite, then checks that the source fingerprints did not change. The isolated copy is schema-checked and opened through production `StorageBackend`. The new copy receives one synthetic `hosts` blob through the production `HostStore` serialization contract. The seeded Web host IDs match the generator's host map IDs.

The input must be quiescent. Do not run this tool while the generator is active. It requires the explicit `--confirm-source-quiescent` acknowledgement, independently verifies persisted output counts, checks the synthetic source marker in four-slot proofs, and refuses any existing qualification job, binding, or profile state. It never opens SQLite against or writes to the generator root. Source hash passes may take several minutes for the full database. The same destination volume must have room for an isolated working copy, a backup copy, and at least 25% of total capacity left free.

Build and run the small self-test using a new, dedicated output path:

```powershell
dotnet build .\scripts\prtg-workload\capacity-harness\CapacityHarness.csproj -c Debug
dotnet .\scripts\prtg-workload\capacity-harness\bin\Debug\net8.0-windows\CapacityHarness.dll `
  --self-test-root .\work\e07-preparation-self-test-001
python .\scripts\prtg-workload\capacity-harness\test_contracts.py
```

The self-test seeds only three synthetic Web hosts into a separately owned SQLite database. Its receipt is explicitly `self-test-only-not-e07-evidence`; it does not dilute the fixed scale requirements.

Prepare the full prerequisite only after the storage generator exits and writes its final receipt:

```powershell
dotnet .\scripts\prtg-workload\capacity-harness\bin\Release\net8.0-windows\CapacityHarness.dll `
  --generator-root .\work\<completed-generator-run> `
  --output-root .\work\e07-capacity-prerequisite-001 `
  --confirm-source-quiescent
```

The generated preparation receipt is `capacity-prerequisite-only`. It records the unchanged six-cell sequence, 1,200 route-request denominator, route and NetIQ p95 limits, 75% schedule-window limit, and 25% reserve. It does not configure a NetIQ source, create outcomes, run schedule cells, measure routes, or pass the public matrix's live qualification fence. It creates no durable qualified job, binding, profile, or native qualification proof. Therefore this preparation result is only a capacity prerequisite; the PLAN E07 gate remains open until the actual workload, backup/recovery, and retention checks pass. The public qualification matrix scripts and their live fence remain unchanged.
