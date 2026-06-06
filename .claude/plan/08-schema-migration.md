# Plan 08 — Schema Versioning, Chained Migration, Upgrade Endpoint

**Status:** ✅ Complete  
**Prerequisite:** Plan 03 (REST API, `POST /config/upgrade` stub in place)  
**Spec ref:** §9 (Schema versioning & migration), §10 (Upgrade endpoint), §12 step 9

---

## Goal

Make the persisted `catalog.json` and `config/<serial>.json` files forwards-safe. Old files on disk are migrated to the current schema version on read. An explicit upgrade endpoint lets clients (scripts, Claude Code) convert any supported version to the current format without touching disk.

At v1 launch there is only one schema version, so the migration chain starts empty. The infrastructure built here is what makes future schema changes safe.

---

## Scope

**In scope:**
- `SchemaVersion` constant per store
- `IMigration` interface + `MigrationRunner`
- `CatalogueStore` and `ConfigStore` updated to migrate on load
- Support floor (reject files below minimum supported version)
- `POST /config/upgrade` fully implemented (replacing the Plan 03 stub)
- Unit tests for migration chain, floor rejection, round-trip identity

**Out of scope:**
- Actual v1→v2 migrations (no schema changes exist yet; framework ships ready for them)
- Automatic backup of migrated files (nice-to-have; skip at launch)

---

## Implementation steps

### 1. `SchemaVersion` constants

In `StreamDeckPilot.Core`:

```csharp
public static class SchemaVersions
{
    public const int CatalogueCurrentVersion = 1;
    public const int CatalogueMinimumSupported = 1;

    public const int ConfigCurrentVersion = 1;
    public const int ConfigMinimumSupported = 1;
}
```

Update when a migration is added: bump `CurrentVersion`, keep or advance `MinimumSupported`.

### 2. `IMigration` interface

```csharp
public interface IMigration
{
    int FromVersion { get; }          // this migration transforms FromVersion → FromVersion+1
    JsonObject Apply(JsonObject doc); // operates on the raw JSON document
}
```

### 3. `MigrationRunner`

```csharp
public class MigrationRunner
{
    // migrations: ordered list of IMigration, sorted by FromVersion
    public MigrationRunner(IEnumerable<IMigration> migrations) { ... }

    // Migrates doc from its schemaVersion up to targetVersion.
    // Throws UnsupportedSchemaVersionException if doc.schemaVersion < minimumSupported.
    // Returns doc unchanged if doc.schemaVersion == targetVersion.
    public JsonObject Migrate(JsonObject doc, int minimumSupported, int targetVersion);
}
```

Chain application:
```csharp
while (currentVersion < targetVersion)
{
    var migration = _migrations.FirstOrDefault(m => m.FromVersion == currentVersion)
        ?? throw new MissingMigrationException(currentVersion);
    doc = migration.Apply(doc);
    currentVersion++;
}
```

`UnsupportedSchemaVersionException`: caught by the API layer and returned as `422 Unprocessable Entity` with a message instructing the user to use an older release to perform the upgrade.

### 4. Wire into `CatalogueStore` and `ConfigStore`

On `LoadAsync()`:
1. Read raw JSON as `JsonObject` (using `JsonNode.Parse`).
2. Read `schemaVersion` field.
3. Call `MigrationRunner.Migrate(doc, minimum, current)`.
4. Deserialise the (now current) document into the target type.
5. If `UnsupportedSchemaVersionException`: propagate (the calling code should surface this to the user via the API or startup log, then halt).

On `SaveAsync()`:
- Always write the current `schemaVersion` — no migration needed on write.

### 5. Complete `POST /config/upgrade`

Replace the Plan 03 stub:

```
POST /config/upgrade
Content-Type: application/json
Body: any DeviceConfig JSON (any supported schemaVersion)

Response 200: DeviceConfig JSON at current schemaVersion
Response 422: {"error": "unsupported_schema_version", "message": "..."}
```

Implementation:
1. Read body as raw `JsonObject`.
2. Determine the document type from context (only `DeviceConfig` supported via this endpoint for now; a `type` discriminator query param could be added later for catalogue documents).
3. Call `MigrationRunner.Migrate(doc, ConfigMinimumSupported, ConfigCurrentVersion)`.
4. Return the migrated JSON.
5. Do NOT persist.

### 6. Startup behaviour for unmigratable files

In `Program.cs` (or in `CatalogueStore.LoadAsync`):

If `UnsupportedSchemaVersionException` is thrown during startup catalogue/config load:
- Log a fatal structured error with the file path and the minimum supported version.
- Throw to halt startup (the container will restart and the operator will see the log).
- Do not silently ignore or partially load.

### 7. Unit tests

- **Identity:** migrate a v1 doc with no migrations registered → returns identical doc.
- **Chain:** register two mock migrations (v1→v2, v2→v3); apply to a v1 doc → v3 doc with both transforms applied.
- **Floor rejection:** doc with `schemaVersion: 0`, floor `1` → `UnsupportedSchemaVersionException`.
- **`POST /config/upgrade`:** v1 payload → 200 with v1 response (no change yet); below-floor payload → 422.
- **`CatalogueStore` load migration:** write a v1 catalogue file, add a v1→v2 migration, reload → doc is at v2; file on disk is still v1 (load does not persist).

---

## Verification

```bash
dotnet test
```

Manual:
1. Edit `config/ABC123.json` and set `"schemaVersion": 0`.
2. Start the app → logs a fatal error mentioning the file and version floor; container does not start.
3. Set `"schemaVersion": 1` → app starts normally.
4. `POST /config/upgrade` with a valid v1 config → 200, identical document returned.

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 86/86 tests green (13 new migration tests)

**Migration chain state:** v1 only, no actual migrations. `MigrationRunner([])` registered in DI. Add `IMigration` implementations to the constructor list when schema changes are needed.

**Support floor:** `CatalogueMinimumSupported = 1`, `ConfigMinimumSupported = 1`. Files below version 1 throw `UnsupportedSchemaVersionException` → app refuses to start, operator sees the error in logs.

**Decisions / deviations from spec:**
- Bootstrap Serilog logger (`Log.Logger = ...CreateBootstrapLogger()`) removed. It mutates a global static that causes flaky failures when multiple `WebApplicationFactory` instances start concurrently in the test suite. Serilog is still fully wired via `builder.Host.UseSerilog()`.
- `StoreMigrationTests.CatalogueStore_LoadMigrates_FileOnDiskUnchanged` was redesigned: the spec expected `schemaVersion == 2` after registering a v1→v2 migration, but `CatalogueCurrentVersion = 1` means `Migrate(v1, min=1, target=1)` is identity — the chain never runs. The `MigrationRunnerTests` cover chain behavior exhaustively; the store test now verifies v1 identity load and floor rejection.
- `CatalogueStore` and `ConfigStore` accept `MigrationRunner?` as optional constructor parameter (default = empty runner). Production DI injects the singleton; tests construct stores directly with custom runners.
- `SaveAsync` now normalises `SchemaVersion` to `CurrentVersion` regardless of what the caller passes in.

**Key files created:**
- `src/StreamDeckPilot.Core/Migration/{IMigration,MigrationRunner,UnsupportedSchemaVersionException,MissingMigrationException}.cs`
- `src/StreamDeckPilot.Core/SchemaVersions.cs` updated (added `MinimumSupported` constants)
- `src/StreamDeckPilot.Infrastructure/Persistence/{CatalogueStore,ConfigStore}.cs` updated (migrate on load)
- `src/StreamDeckPilot.Api/Endpoints/ConfigEndpoints.cs` updated (`POST /config/upgrade` complete)
- `src/StreamDeckPilot.Api/Program.cs` updated (register `MigrationRunner`, remove flaky bootstrap logger)
- `tests/StreamDeckPilot.Tests/Migration/{MigrationRunnerTests,ConfigUpgradeEndpointTests,StoreMigrationTests}.cs`  
