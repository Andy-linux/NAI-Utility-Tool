# Persistence regression checks

Run in the client repository:

```powershell
dotnet run --project src/NAITool.PersistenceTests/NAITool.PersistenceTests.csproj -c Release
```

These checks use a newly created temporary directory and synthetic credentials only. Production `SettingsService`, `AtomicJsonFile`, parameter serialization and Windows DPAPI run from linked source. Localization language normalization and local upscale scale normalization are platform/UI dependency stubs; they are not tested here.

Coverage: initial save/recovery copy, previous valid backup, corrupt and missing primary recovery, schema-invalid primary recovery, valid backup usable with a locked primary, invalid backup rejection and repair on new save, invalid JSON publication preservation, temporary cleanup, complete concurrent-reader snapshots, typed settings recovery, encrypted credential recovery and plaintext exclusion, mutable credential re-encryption, legacy single-account migration and encrypted recovery backup, decrypt-invalid primary recovery, undecryptable-copy preservation/save rejection, legacy encrypted-over-plaintext precedence, backup-only plaintext sanitation, saved steps/Effort/High draft and configurable timeouts roundtrip, URL credential/query/fragment rejection.

The test does not kill the process mid-system-call or claim power-loss guarantees beyond the filesystem's flushed atomic replacement semantics. Each JSON file is published atomically; settings and credentials are not a multi-file filesystem transaction. Foreign-machine/invalid DPAPI ciphertext is preserved with a warning when no decryptable backup exists; saving is rejected until that credential is explicitly repaired, rather than destroying its recovery copy. Current settings/credential serialization and encryption are prepared before either primary is published; legacy backup encryption is prepared when rotating that file's backup.
