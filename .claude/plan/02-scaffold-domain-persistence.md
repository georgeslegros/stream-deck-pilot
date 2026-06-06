# Plan 02 — Project Scaffold, Domain Model, Persistence

**Status:** ✅ Complete  
**Prerequisite:** Plan 01 (device spike confirmed the platform works)  
**Spec ref:** §4 (Domain Model), §9 (Persistence), §12 step 2

---

## Goal

Establish the solution structure and express the complete domain model as C# types, then implement file-based JSON persistence with atomic writes and schema versioning. Everything in this plan is testable without hardware, a running broker, or a web server — pure logic and I/O.

---

## Scope

**In scope:**
- Solution + project layout
- All domain types (Core project, no external runtime deps)
- JSON persistence layer (Infrastructure project)
- Unit tests for round-trip serialisation and persistence correctness

**Out of scope:**
- HTTP endpoints (Plan 03)
- Device connection or rendering (Plan 04)
- MQTT (Plan 05)
- Schema migrations (Plan 08 — persistence stores just need `schemaVersion` on files for now)

---

## Implementation steps

### 1. Solution structure

```
StreamDeckPilot.sln
src/
  StreamDeckPilot.Core/          # domain types, interfaces, pure logic
  StreamDeckPilot.Infrastructure/ # persistence, file I/O
  StreamDeckPilot.Api/           # (created in Plan 03)
tests/
  StreamDeckPilot.Tests/         # xUnit, covers Core + Infrastructure
spike/                           # from Plan 01 (keep or delete)
```

### 2. Core domain types

All types in `StreamDeckPilot.Core`. No external NuGet dependencies (only `System.*`).

**Catalogue (`Models/Catalogue/`):**
```csharp
record DeviceCatalogue(int SchemaVersion, IReadOnlyList<DeviceEntry> Devices);

record DeviceEntry(
    string Serial,
    string Model,
    int KeyRows,
    int KeyColumns,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen);
```
`KeyCount = KeyRows * KeyColumns`.

**Config (`Models/Config/`):**

```csharp
record DeviceConfig(int SchemaVersion, string Serial, IReadOnlyList<Page> Pages);

// Page is a discriminated union via a type discriminator
abstract record Page(string PageId, string PageType);
record ButtonGridPage(string PageId, IReadOnlyList<ButtonDefinition> Buttons)
    : Page(PageId, "ButtonGrid");

record ButtonDefinition(
    string ButtonId,
    int KeyIndex,
    string PageId,
    DisplaySpec Display,
    InboundBinding? Inbound,
    IReadOnlyList<ConditionalRule> Rules,
    IReadOnlyDictionary<string, IReadOnlyList<ButtonAction>> Gestures);

record DisplaySpec(string? BaseIcon, string? StaticLabel, string? FormatTemplate);

record InboundBinding(
    string Topic,
    string? ValueField,        // JSON path-lite, e.g. "value" or "sensor.value"
    string? UnitField,
    bool ExpectsRetained,
    TimeSpan? StalenessTimeout);

record ConditionalRule(string Condition, string? BackgroundColour, string? Icon);
// Condition examples: ">1000", "<=500", "between:200:500"
```

**Actions (discriminated union via `System.Text.Json` polymorphic serialisation):**
```csharp
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PublishAction), "Publish")]
[JsonDerivedType(typeof(NavigateAction), "Navigate")]
abstract record ButtonAction;

record PublishAction(string Topic, string Payload) : ButtonAction;
record NavigateAction(string TargetPageId) : ButtonAction;
```

### 3. Validation helpers (Core)

`ConfigValidator` — static methods, return `ValidationResult` (list of errors), not exceptions:
- `ValidateConfig(DeviceConfig config, DeviceEntry device)`:
  - Key positions are within geometry (`KeyIndex < device.KeyCount`)
  - No two buttons share the same `PageId + KeyIndex`
  - All `NavigateAction.TargetPageId` values resolve to a page in the same config
  - Warn (not error) on pages unreachable by any navigation action

### 4. Persistence (Infrastructure)

**Atomic write helper** (`AtomicFileWriter`):
```csharp
// Write to <path>.tmp, then File.Move(tmp, path, overwrite: true)
static Task WriteJsonAsync<T>(string path, T value, JsonSerializerOptions options);
```

**`CatalogueStore`:**
- `LoadAsync()` → reads `catalog.json`; if missing, returns empty catalogue at current schema version
- `AppendDeviceAsync(DeviceEntry entry)` → load, upsert by serial (update LastSeen if exists), save atomically
- File path from injected `IOptions<StorageOptions>` (base directory)

**`ConfigStore`:**
- `LoadAsync(string serial)` → reads `config/{serial}.json`; returns null if missing
- `SaveAsync(DeviceConfig config)` → validate schema version, save atomically to `config/{serial}.json`
- `ListSerialsAsync()` → list files in `config/` directory

**`StorageOptions`:**
```csharp
record StorageOptions(string BaseDirectory);
// Injected via IConfiguration ("Storage:BaseDirectory"), defaults to "/data"
```

### 5. `System.Text.Json` options

Define a shared `JsonOptions.Default` (in Core or Infrastructure):
- `PropertyNamingPolicy = CamelCase`
- `WriteIndented = true` (human-readable persisted files)
- Include the `JsonPolymorphicAttribute` resolvers for `ButtonAction`
- `JsonStringEnumConverter`

### 6. Unit tests

In `StreamDeckPilot.Tests` (NuGet: `xunit.v3 3.2.2`, `Microsoft.NET.Test.Sdk`):

- **Serialisation round-trips:** serialise then deserialise each domain type; assert equality. Include a `ButtonDefinition` with both action types to exercise the polymorphic serialiser.
- **Atomic write:** write a file, verify the `.tmp` file does not remain after success; simulate a crash during write (throw before rename) and verify the target is unchanged.
- **Validation — happy path:** a valid config for a 5×3 device passes.
- **Validation — position out of range:** key index ≥ 15 rejected.
- **Validation — duplicate position:** two buttons on same page+index rejected.
- **Validation — broken nav target:** `NavigateAction` pointing to a non-existent page rejected.

---

## Verification

```bash
dotnet build
dotnet test
```

All tests green. No hardware, broker, or network required.

---

## Completion notes

**Status:** ✅ Complete — 2026-06-05 — 17/17 tests green

**Decisions / deviations from spec:**
- `IReadOnlyList<T>` record equality fails after JSON round-trip (concrete type changes from `ReadOnlySingleElementList` to `List<T>`). Tests on collection-bearing records use `Assert.Equivalent` (xunit.v3 deep structural comparison) rather than `Assert.Equal`. Production code is unaffected; this is a test-only concern.
- `using static StreamDeckPilot.Core.SchemaVersions` used in `CatalogueStore` to reference constants without repeating the type name.
- `CancellationToken` threaded through `AtomicFileWriter.WriteJsonAsync` (xunit.v3 analyzer xUnit1051 recommendation).

**Key files created:**
- `src/StreamDeckPilot.Core/` — `SchemaVersions.cs`, `Json/JsonOptions.cs`, `Models/Catalogue/{DeviceEntry,DeviceCatalogue}.cs`, `Models/Config/{ButtonAction,DisplaySpec,InboundBinding,ConditionalRule,ButtonDefinition,Page,DeviceConfig}.cs`, `Validation/{ValidationResult,ConfigValidator}.cs`
- `src/StreamDeckPilot.Infrastructure/` — `Persistence/{StorageOptions,AtomicFileWriter,CatalogueStore,ConfigStore}.cs`
- `tests/StreamDeckPilot.Tests/` — `Serialisation/SerialisationTests.cs`, `Persistence/AtomicFileWriterTests.cs`, `Validation/ConfigValidatorTests.cs`

**NuGet packages added:**
- `Microsoft.Extensions.Options 10.0.8` (Infrastructure)
- `xunit.v3 3.2.2` replacing deprecated `xunit 2.9.3` (Tests)  
