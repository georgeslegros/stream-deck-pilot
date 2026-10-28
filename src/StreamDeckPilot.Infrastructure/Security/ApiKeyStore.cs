using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Infrastructure.Security;

public enum ApiKeyScope
{
    /// <summary>GET and HEAD only, plus the event stream and the live hub.</summary>
    ReadOnly,
    /// <summary>Everything the bootstrap key can do, except first-run setup.</summary>
    Full,
}

/// <summary>A named API key as stored (#98): never the key itself, only its SHA-256 <see cref="Hash"/>.</summary>
public sealed record ApiKeyRecord(
    string Id,
    string Name,
    ApiKeyScope Scope,
    string Hash,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt = null,
    DateTimeOffset? LastUsedAt = null,
    DateTimeOffset? RevokedAt = null,
    string? CreatedBy = null,
    string? RevokedBy = null)
{
    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } expires && now >= expires;
    public bool IsActive(DateTimeOffset now) => RevokedAt is null && !IsExpired(now);
}

/// <summary>A key just created: <see cref="Key"/> is shown once and never stored.</summary>
public sealed record CreatedApiKey(ApiKeyRecord Record, string Key);

/// <summary>The outcome of <see cref="ApiKeyStore.CreateAsync"/>: the key, or why it wasn't created.</summary>
public sealed record ApiKeyCreateResult(CreatedApiKey? Created, string? Error);

/// <summary>
/// Named, revocable API keys (#98) in <c>{Storage:BaseDirectory}/auth/api-keys.json</c>, written
/// atomically like the other stores. Keys are created and revoked by an admin in the web UI; the
/// API key middleware looks them up on every request (<see cref="Find"/> reads memory, no I/O), so
/// a revocation or an expiry applies to the next request, without a restart.
/// <para>
/// Last-used times are kept in memory, recorded at most once a minute per key (<see cref="Touch"/>),
/// and written by <see cref="ApiKeyLastUsedWriter"/> in the background, never on the request path.
/// Every write (create, revoke, last-used flush) runs under one lock and starts from the current
/// records, so a flush can never write back a key that was revoked meanwhile.
/// </para>
/// <para>
/// A file that can't be read fails closed: no named key is accepted and none can be created or
/// revoked until it is fixed or removed and the service restarted. The bootstrap key still works.
/// </para>
/// </summary>
public sealed class ApiKeyStore
{
    public const int SchemaVersion = 1;
    public const int MaxNameLength = 64;
    public const int MaxActiveKeys = 200;

    /// <summary>The bootstrap key's name in the audit log; a named key can't take it.</summary>
    public const string ReservedName = "bootstrap";

    /// <summary>Last-used times are recorded at most this often per key.</summary>
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly IOptions<StorageOptions> _storage;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly ILogger _audit;
    private readonly IEventBus? _bus;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly object _loadGate = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastUsed = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _revocations = new();
    private readonly Channel<bool> _flushRequests = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile IReadOnlyDictionary<string, ApiKeyRecord>? _records;
    private volatile bool _unreadable;
    private int _lastUsedPending;

    public ApiKeyStore(IOptions<StorageOptions> storage, TimeProvider? time = null, ILoggerFactory? loggers = null, IEventBus? bus = null)
    {
        _storage = storage;
        _time = time ?? TimeProvider.System;
        loggers ??= NullLoggerFactory.Instance;
        _logger = loggers.CreateLogger<ApiKeyStore>();
        _audit = loggers.CreateLogger("StreamDeckPilot.Audit");
        _bus = bus;
    }

    public string FilePath => Path.Combine(_storage.Value.BaseDirectory, "auth", "api-keys.json");

    /// <summary>True when the keys file exists but can't be read: named keys are refused (fail closed).</summary>
    public bool Unreadable
    {
        get { EnsureLoaded(); return _unreadable; }
    }

    /// <summary>Signals that last-used times are waiting to be written; read by <see cref="ApiKeyLastUsedWriter"/>.</summary>
    public ChannelReader<bool> FlushRequests => _flushRequests.Reader;

    /// <summary>The stored record for an id, from memory (no I/O after the first load); null if unknown or unreadable.</summary>
    public ApiKeyRecord? Find(string id)
    {
        EnsureLoaded();
        return _records is { } records && records.TryGetValue(id, out var record) ? record : null;
    }

    /// <summary>Every key, newest first, with the latest last-used time (including times not yet written).</summary>
    public IReadOnlyList<ApiKeyRecord> List()
    {
        EnsureLoaded();
        return [.. (_records?.Values ?? []).Select(WithLastUsed).OrderByDescending(r => r.CreatedAt)];
    }

    /// <summary>
    /// Records a use of the key: at most once per <see cref="LastUsedResolution"/>, in memory, and asks
    /// the background writer to persist it. Never does I/O itself.
    /// </summary>
    public void Touch(string id)
    {
        var now = _time.GetUtcNow();
        var previous = _lastUsed.TryGetValue(id, out var recorded) ? recorded : Find(id)?.LastUsedAt;
        if (previous is { } p && now - p < LastUsedResolution) return;
        _lastUsed[id] = now;
        Interlocked.Exchange(ref _lastUsedPending, 1);
        _flushRequests.Writer.TryWrite(true);
    }

    /// <summary>
    /// Cancelled when the key is revoked (or already cancelled if it isn't active), so a long-lived
    /// connection opened with it (event stream, hub) can be ended at once.
    /// </summary>
    public CancellationToken RevocationToken(string id)
    {
        if (Find(id) is not { RevokedAt: null }) return new CancellationToken(canceled: true);
        var token = _revocations.GetOrAdd(id, _ => new CancellationTokenSource()).Token;
        // Revoke publishes the revoked record before it cancels and drops the source: if it ran in
        // between, the source above is a fresh one nobody will cancel, but the record says revoked.
        return Find(id) is { RevokedAt: null } ? token : new CancellationToken(canceled: true);
    }

    public async Task<ApiKeyCreateResult> CreateAsync(string name, ApiKeyScope scope, DateTimeOffset? expiresAt, string createdBy, CancellationToken ct = default)
    {
        name = (name ?? "").Trim();
        var now = _time.GetUtcNow();
        if (name.Length == 0) return Fail("Give the key a name, e.g. the script or device that will use it.");
        if (name.Length > MaxNameLength) return Fail($"The name can be at most {MaxNameLength} characters.");
        if (string.Equals(name, ReservedName, StringComparison.OrdinalIgnoreCase)) return Fail($"'{ReservedName}' is reserved for the ApiKey setting.");
        if (expiresAt is { } e && e <= now) return Fail("The expiry must be in the future.");

        await _write.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            if (_unreadable || _records is not { } records)
                return Fail("The API keys file can't be read, so no key can be created until it is fixed (see the service log).");
            var active = records.Values.Where(r => r.IsActive(now)).ToList();
            if (active.Any(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase)))
                return Fail($"An active key is already named '{name}'.");
            if (active.Count >= MaxActiveKeys)
                return Fail($"There are already {MaxActiveKeys} active keys; revoke some first.");

            string id, key;
            do (id, key) = ApiKeyFormat.Generate();
            while (records.ContainsKey(id));

            var record = new ApiKeyRecord(id, name, scope, ApiKeyFormat.Hash(key), now, expiresAt, CreatedBy: createdBy);
            await SaveAsync(new Dictionary<string, ApiKeyRecord>(records) { [id] = record }, ct);

            _audit.LogWarning("API key {KeyName} ({KeyId}, {Scope}) created by {User}{Expiry}",
                name, id, scope, createdBy, expiresAt is { } x ? $", expires {x:O}" : "");
            _bus?.Publish(new ApiKeyCreated(id, name, scope.ToString(), expiresAt));
            return new ApiKeyCreateResult(new CreatedApiKey(record, key), null);
        }
        finally { _write.Release(); }

        static ApiKeyCreateResult Fail(string error) => new(null, error);
    }

    /// <summary>Revokes the key: the next request with it gets 401, and its open streams and hub connections end.</summary>
    public async Task<bool> RevokeAsync(string id, string revokedBy, CancellationToken ct = default)
    {
        ApiKeyRecord revoked;
        await _write.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            if (_unreadable || _records is not { } records || !records.TryGetValue(id, out var record) || record.RevokedAt is not null)
                return false;
            revoked = WithLastUsed(record) with { RevokedAt = _time.GetUtcNow(), RevokedBy = revokedBy };
            await SaveAsync(new Dictionary<string, ApiKeyRecord>(records) { [id] = revoked }, ct);
        }
        finally { _write.Release(); }

        if (_revocations.TryRemove(id, out var source))
        {
            source.Cancel();
            source.Dispose();
        }
        _audit.LogWarning("API key {KeyName} ({KeyId}) revoked by {User}", revoked.Name, id, revokedBy);
        _bus?.Publish(new ApiKeyRevoked(id, revoked.Name));
        return true;
    }

    /// <summary>Writes last-used times recorded since the last flush, merged onto the current records.</summary>
    public async Task FlushLastUsedAsync(CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _lastUsedPending, 0) == 0) return;
        await _write.WaitAsync(ct);
        try
        {
            EnsureLoaded();
            if (_unreadable || _records is not { } records) return;
            var merged = records.ToDictionary(kv => kv.Key, kv => WithLastUsed(kv.Value));
            if (merged.All(kv => kv.Value.LastUsedAt == records[kv.Key].LastUsedAt)) return;
            await SaveAsync(merged, ct);
        }
        catch
        {
            Interlocked.Exchange(ref _lastUsedPending, 1);   // try again with the next request
            throw;
        }
        finally { _write.Release(); }
    }

    private ApiKeyRecord WithLastUsed(ApiKeyRecord record) =>
        _lastUsed.TryGetValue(record.Id, out var used) && (record.LastUsedAt is null || used > record.LastUsedAt)
            ? record with { LastUsedAt = used }
            : record;

    private void EnsureLoaded()
    {
        if (_records is not null || _unreadable) return;
        lock (_loadGate)
        {
            if (_records is not null || _unreadable) return;
            if (!File.Exists(FilePath))
            {
                _records = new Dictionary<string, ApiKeyRecord>();
                return;
            }

            try
            {
                var file = JsonSerializer.Deserialize<KeysFile>(File.ReadAllText(FilePath), Json)
                           ?? throw new InvalidDataException("the file is empty");
                if (file.SchemaVersion != SchemaVersion)
                    throw new InvalidDataException($"schemaVersion {file.SchemaVersion}; this version reads {SchemaVersion}");
                var keys = file.Keys ?? [];
                if (keys.Any(k => k is null || string.IsNullOrEmpty(k.Id) || string.IsNullOrEmpty(k.Name) ||
                                  k.Hash is not { Length: 64 } || !k.Hash.All(char.IsAsciiHexDigit)))
                    throw new InvalidDataException("a key entry is incomplete or malformed");
                _records = keys.ToDictionary(k => k.Id, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Cached: a file fixed by hand is read again after a restart.
                _unreadable = true;
                _logger.LogError(ex,
                    "API keys file {Path} can't be read ({Reason}). Named API keys are refused, and none can be created or revoked, until it is fixed or removed and the service restarted. The bootstrap key (ApiKey) still works.",
                    FilePath, ex.Message);
            }
        }
    }

    private async Task SaveAsync(Dictionary<string, ApiKeyRecord> records, CancellationToken ct)
    {
        await AtomicFileWriter.WriteJsonAsync(FilePath,
            new KeysFile(SchemaVersion, [.. records.Values.OrderBy(r => r.CreatedAt)]), Json, ct);
        _records = records;
    }

    private sealed record KeysFile(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("keys")] List<ApiKeyRecord>? Keys);
}
