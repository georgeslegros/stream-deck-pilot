using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Web.Auth;

/// <summary>A local account (#97): the fallback when no OpenID Connect provider is configured.</summary>
public sealed record LocalUser(string UserName, string PasswordHash, string Role, DateTimeOffset CreatedAt);

/// <summary>
/// Local accounts in <c>{Storage:BaseDirectory}/auth/users.json</c>, written atomically like the
/// other stores. Passwords are stored as ASP.NET Core Identity hashes (PBKDF2, salted, iterated),
/// never in clear.
/// </summary>
public sealed class LocalUserStore(IOptions<StorageOptions> storage, TimeProvider? time = null)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PasswordHasher<LocalUser> _hasher = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private List<LocalUser>? _users;

    // Verified against when the user name is unknown, so a miss costs the same time as a wrong password.
    private readonly Lazy<string> _dummyHash = new(() => new PasswordHasher<LocalUser>().HashPassword(Dummy, "not-a-password"));
    private static readonly LocalUser Dummy = new("", "", AuthRoles.Viewer, DateTimeOffset.MinValue);

    public string FilePath => Path.Combine(storage.Value.BaseDirectory, "auth", "users.json");

    public async Task<bool> AnyAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Count > 0; }
        finally { _lock.Release(); }
    }

    /// <summary>The user, if the name exists and the password matches; otherwise null.</summary>
    public async Task<LocalUser?> VerifyAsync(string userName, string password, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            var user = users.FirstOrDefault(u => string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (user is null)
            {
                _hasher.VerifyHashedPassword(Dummy, _dummyHash.Value, password);
                return null;
            }

            switch (_hasher.VerifyHashedPassword(user, user.PasswordHash, password))
            {
                case PasswordVerificationResult.Success:
                    return user;
                case PasswordVerificationResult.SuccessRehashNeeded:
                    var rehashed = user with { PasswordHash = _hasher.HashPassword(user, password) };
                    users[users.IndexOf(user)] = rehashed;
                    await SaveAsync(users, ct);
                    return rehashed;
                default:
                    return null;
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Creates the first admin. False if any account exists already: first-run setup happens once,
    /// even when two requests race for it.
    /// </summary>
    public Task<bool> CreateFirstAdminAsync(string userName, string password, CancellationToken ct = default) =>
        CreateAsync(userName, password, AuthRoles.Admin, onlyIfEmpty: true, ct);

    /// <summary>Adds an account. False if the name is taken (or, with <paramref name="onlyIfEmpty"/>, if any account exists).</summary>
    public async Task<bool> CreateAsync(string userName, string password, string role, bool onlyIfEmpty = false, CancellationToken ct = default)
    {
        if (!AuthRoles.IsKnown(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        userName = userName.Trim();
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            if (onlyIfEmpty && users.Count > 0) return false;
            if (users.Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase))) return false;

            var user = new LocalUser(userName, "", role, _time.GetUtcNow());
            users.Add(user with { PasswordHash = _hasher.HashPassword(user, password) });
            await SaveAsync(users, ct);
            return true;
        }
        finally { _lock.Release(); }
    }

    private async Task<List<LocalUser>> LoadAsync(CancellationToken ct)
    {
        if (_users is not null) return _users;
        if (!File.Exists(FilePath)) return _users = [];

        await using var stream = File.OpenRead(FilePath);
        var file = await JsonSerializer.DeserializeAsync<UsersFile>(stream, Json, ct)
                   ?? throw new InvalidDataException($"{FilePath} is empty.");
        if (file.SchemaVersion != SchemaVersion)
            throw new InvalidDataException($"{FilePath} has schemaVersion {file.SchemaVersion}; this version reads {SchemaVersion}.");
        return _users = [.. file.Users ?? []];
    }

    private async Task SaveAsync(List<LocalUser> users, CancellationToken ct)
    {
        await AtomicFileWriter.WriteJsonAsync(FilePath, new UsersFile(SchemaVersion, users), Json, ct);
        _users = users;
    }

    private sealed record UsersFile(
        [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
        [property: JsonPropertyName("users")] List<LocalUser>? Users);
}
