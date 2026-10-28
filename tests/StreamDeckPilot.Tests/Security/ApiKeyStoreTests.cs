using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using StreamDeckPilot.Api.Middleware;
using StreamDeckPilot.Infrastructure.Events;
using StreamDeckPilot.Infrastructure.Persistence;
using StreamDeckPilot.Infrastructure.Security;

namespace StreamDeckPilot.Tests.Security;

/// <summary>Named API keys (#98): the store and the validator, without the web host.</summary>
public sealed class ApiKeyStoreTests : IDisposable
{
    private const string BootstrapKey = "bootstrap-key-0123456789";
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private ApiKeyStore Store(IEventBus? bus = null) =>
        new(Options.Create(new StorageOptions { BaseDirectory = _dir }), _time, bus: bus);

    private ApiKeyValidator Validator(ApiKeyStore store, bool bootstrapEnabled = true) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ApiKey"] = BootstrapKey,
            ["Auth:BootstrapKeyEnabled"] = bootstrapEnabled ? "true" : "false",
        }).Build(), store, _time);

    private static async Task<CreatedApiKey> CreateAsync(ApiKeyStore store, string name = "backup script",
        ApiKeyScope scope = ApiKeyScope.Full, DateTimeOffset? expires = null)
    {
        var result = await store.CreateAsync(name, scope, expires, "admin");
        Assert.Null(result.Error);
        return result.Created!;
    }

    // ── Format ──────────────────────────────────────────────────────────────

    [Fact]
    public void GeneratedKeys_HaveThePrefix_AndTheirIdCanBeReadBack()
    {
        var (id, key) = ApiKeyFormat.Generate();
        Assert.StartsWith("sdp_", key);
        Assert.Equal(id, ApiKeyFormat.IdOf(key));
        Assert.True(key.Length > 50);
        Assert.NotEqual(key, ApiKeyFormat.Generate().Key);
    }

    [Theory]
    [InlineData("")]
    [InlineData("sdp_")]
    [InlineData("sdp_0123456789ab")]
    [InlineData("sdp_0123456789AB_secret")]   // upper-case id
    [InlineData("sdp_0123456789zz_secret")]
    [InlineData("sdp_0123456789a-secret")]
    [InlineData("xyz_0123456789ab_secret")]
    public void MalformedKeys_HaveNoId(string key) => Assert.Null(ApiKeyFormat.IdOf(key));

    // ── Store ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresOnlyTheHash_AtomicallyWithASchemaVersion_AndRoundTrips()
    {
        var store = Store();
        var created = await CreateAsync(store, expires: _time.GetUtcNow().AddDays(30));

        var json = await File.ReadAllTextAsync(store.FilePath);
        Assert.DoesNotContain(created.Key, json);
        Assert.DoesNotContain(created.Key[(ApiKeyFormat.Prefix.Length + ApiKeyFormat.IdLength + 1)..], json);   // the secret part
        Assert.Contains(ApiKeyFormat.Hash(created.Key), json);
        Assert.Equal(1, JsonDocument.Parse(json).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!, "*.tmp"));   // temp file renamed over

        var reloaded = Store().Find(created.Record.Id);
        Assert.Equal(created.Record, reloaded);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bootstrap")]
    public async Task Create_RefusesEmptyOrReservedNames(string name)
    {
        var result = await Store().CreateAsync(name, ApiKeyScope.Full, null, "admin");
        Assert.Null(result.Created);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Create_RefusesADuplicateActiveName_AndAnExpiryInThePast()
    {
        var store = Store();
        await CreateAsync(store, "ci");
        Assert.NotNull((await store.CreateAsync("CI", ApiKeyScope.Full, null, "admin")).Error);
        Assert.NotNull((await store.CreateAsync("other", ApiKeyScope.Full, _time.GetUtcNow().AddMinutes(-1), "admin")).Error);
    }

    [Fact]
    public async Task UnreadableFile_FailsClosed_AndIsNeverOverwritten()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "auth"));
        var path = Path.Combine(_dir, "auth", "api-keys.json");
        await File.WriteAllTextAsync(path, "{ not json");

        var store = Store();
        Assert.True(store.Unreadable);
        Assert.Empty(store.List());
        Assert.NotNull((await store.CreateAsync("x", ApiKeyScope.Full, null, "admin")).Error);
        Assert.False(await store.RevokeAsync("0123456789ab", "admin"));
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path));

        // The bootstrap key still works.
        Assert.True(Validator(store).Validate(BootstrapKey)?.IsBootstrap);
    }

    [Fact]
    public async Task MalformedEntry_MakesTheFileUnreadable()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "auth"));
        await File.WriteAllTextAsync(Path.Combine(_dir, "auth", "api-keys.json"),
            """{ "schemaVersion": 1, "keys": [ { "id": "0123456789ab", "name": "x", "scope": "Full", "hash": "nothex", "createdAt": "2026-01-01T00:00:00Z" } ] }""");
        Assert.True(Store().Unreadable);
    }

    // ── Validator ───────────────────────────────────────────────────────────

    [Fact]
    public async Task NamedKey_IsAccepted_WithItsNameScopeAndId()
    {
        var store = Store();
        var full = await CreateAsync(store, "deploy", ApiKeyScope.Full);
        var read = await CreateAsync(store, "dashboard", ApiKeyScope.ReadOnly);
        var validator = Validator(store);

        var identity = validator.Validate(full.Key)!;
        Assert.Equal(("deploy", false, false, full.Record.Id), (identity.Name, identity.ReadOnly, identity.IsBootstrap, identity.KeyId));
        Assert.True(validator.Validate(read.Key)!.ReadOnly);
    }

    [Fact]
    public async Task Failures_AreAllTheSameNull()
    {
        var store = Store();
        var key = (await CreateAsync(store)).Key;
        var validator = Validator(store);
        var (otherId, _) = ApiKeyFormat.Generate();

        Assert.Null(validator.Validate(""));
        Assert.Null(validator.Validate("not-a-key"));
        Assert.Null(validator.Validate(key[..^1] + (key[^1] == 'A' ? 'B' : 'A')));          // wrong secret
        Assert.Null(validator.Validate($"sdp_{otherId}{key[(4 + ApiKeyFormat.IdLength)..]}")); // unknown id, real secret
        Assert.Null(validator.Validate(key.ToUpperInvariant()));
    }

    [Fact]
    public async Task Revocation_AndExpiry_ApplyToTheNextValidation_WithoutARestart()
    {
        var store = Store();
        var revoked = await CreateAsync(store, "revoked");
        var expiring = await CreateAsync(store, "expiring", expires: _time.GetUtcNow().AddHours(1));
        var validator = Validator(store);
        Assert.NotNull(validator.Validate(revoked.Key));
        Assert.NotNull(validator.Validate(expiring.Key));

        Assert.True(await store.RevokeAsync(revoked.Record.Id, "admin"));
        Assert.False(await store.RevokeAsync(revoked.Record.Id, "admin"));   // once
        Assert.Null(validator.Validate(revoked.Key));

        _time.Advance(TimeSpan.FromHours(1));
        Assert.Null(validator.Validate(expiring.Key));

        // And after a restart.
        Assert.Null(Validator(Store()).Validate(revoked.Key));
    }

    [Fact]
    public async Task BootstrapKey_CanBeDisabled_NamedKeysStillWork()
    {
        var store = Store();
        var named = await CreateAsync(store);
        var validator = Validator(store, bootstrapEnabled: false);

        Assert.Null(validator.Validate(BootstrapKey));
        Assert.False(validator.BootstrapKeyAvailable);
        Assert.NotNull(validator.Validate(named.Key));
        Assert.True(Validator(store).BootstrapKeyAvailable);
    }

    [Fact]
    public async Task RevocationToken_IsCancelledByRevoke_AndAlreadyCancelledForARevokedKey()
    {
        var store = Store();
        var key = await CreateAsync(store);
        var token = store.RevocationToken(key.Record.Id);
        Assert.False(token.IsCancellationRequested);

        await store.RevokeAsync(key.Record.Id, "admin");
        Assert.True(token.IsCancellationRequested);
        Assert.True(store.RevocationToken(key.Record.Id).IsCancellationRequested);
        Assert.True(store.RevocationToken("0123456789ab").IsCancellationRequested);
    }

    // ── Last used ───────────────────────────────────────────────────────────

    [Fact]
    public async Task LastUsed_IsRecordedAtMostOnceAMinute_AndOnlyWrittenByTheFlush()
    {
        var store = Store();
        var key = await CreateAsync(store);
        var validator = Validator(store);
        var written = File.GetLastWriteTimeUtc(store.FilePath);
        var first = _time.GetUtcNow();

        validator.Validate(key.Key);
        Assert.True(store.FlushRequests.TryRead(out _));
        Assert.Equal(first, store.List().Single().LastUsedAt);
        Assert.Null(Store().Find(key.Record.Id)!.LastUsedAt);   // not written on the request path

        _time.Advance(TimeSpan.FromSeconds(59));
        validator.Validate(key.Key);
        Assert.False(store.FlushRequests.TryRead(out _));
        Assert.Equal(first, store.List().Single().LastUsedAt);

        _time.Advance(TimeSpan.FromSeconds(1));
        validator.Validate(key.Key);
        Assert.True(store.FlushRequests.TryRead(out _));

        await store.FlushLastUsedAsync();
        Assert.Equal(_time.GetUtcNow(), Store().Find(key.Record.Id)!.LastUsedAt);
        Assert.True(File.GetLastWriteTimeUtc(store.FilePath) >= written);
    }

    [Fact]
    public async Task LastUsedFlush_AfterARevoke_KeepsTheKeyRevoked()
    {
        var store = Store();
        var key = await CreateAsync(store);
        Validator(store).Validate(key.Key);   // a last-used time is now pending

        await store.RevokeAsync(key.Record.Id, "admin");
        await store.FlushLastUsedAsync();

        var stored = Store().Find(key.Record.Id)!;
        Assert.NotNull(stored.RevokedAt);
        Assert.NotNull(stored.LastUsedAt);
        Assert.Null(Validator(Store()).Validate(key.Key));
    }

    [Fact]
    public async Task ConcurrentFlushesAndRevokes_NeverLoseARevocation()
    {
        var store = Store();
        var keys = new List<CreatedApiKey>();
        for (var i = 0; i < 10; i++) keys.Add(await CreateAsync(store, $"key {i}"));
        var validator = Validator(store);

        var work = new List<Task>();
        foreach (var key in keys)
        {
            _time.Advance(TimeSpan.FromMinutes(1));
            validator.Validate(key.Key);
            work.Add(Task.Run(() => store.FlushLastUsedAsync()));
            work.Add(Task.Run(() => store.RevokeAsync(key.Record.Id, "admin")));
        }
        await Task.WhenAll(work);

        Assert.All(Store().List(), k => Assert.NotNull(k.RevokedAt));
    }

    // ── Events ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAndRevoke_PublishEvents_WithoutTheKey()
    {
        var bus = new RecordingBus();
        var store = Store(bus);
        var key = await CreateAsync(store, "ci", ApiKeyScope.ReadOnly);
        await store.RevokeAsync(key.Record.Id, "admin");

        var created = Assert.IsType<ApiKeyCreated>(bus.Events[0]);
        Assert.Equal((key.Record.Id, "ci", "ReadOnly"), (created.Id, created.Name, created.Scope));
        Assert.Equal(key.Record.Id, Assert.IsType<ApiKeyRevoked>(bus.Events[1]).Id);
        Assert.All(bus.Events, e => Assert.DoesNotContain(key.Key, JsonSerializer.Serialize(e, e.GetType())));
        Assert.All(bus.Events, e => Assert.DoesNotContain(key.Record.Hash, JsonSerializer.Serialize(e, e.GetType())));
    }

    private sealed class RecordingBus : IEventBus
    {
        public List<LiveEvent> Events { get; } = [];
        public string BootId => "test";
        public long LastSequence => Events.Count;
        public void Publish(LiveEvent e) => Events.Add(e);
        public EventSubscription Subscribe(EventFilter? filter = null) => throw new NotSupportedException();
        public bool TryReplay(long after, EventFilter filter, out IReadOnlyList<LiveEvent> events, out long upTo) =>
            throw new NotSupportedException();
    }
}
