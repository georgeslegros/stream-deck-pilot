using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StreamDeckPilot.Infrastructure.Persistence;

namespace StreamDeckPilot.Web.Auth;

/// <summary>
/// A local account (#97): the fallback when no OpenID Connect provider is configured.
/// <see cref="SecurityStamp"/> changes whenever the password or role changes; sessions carry the
/// stamp they were issued with and end when it no longer matches (see <see cref="SessionValidator"/>).
/// </summary>
public sealed record LocalUser(string UserName, string PasswordHash, string Role, DateTimeOffset CreatedAt, string? SecurityStamp = null);

/// <summary>What the accounts file says about first-run setup.</summary>
public enum LocalUsersState
{
    /// <summary>No file or no account: first-run setup is open.</summary>
    Empty,
    HasUsers,
    /// <summary>
    /// The file exists but can't be read. Fails closed: setup stays shut (it would overwrite the
    /// file) and local sign-in is refused, until someone fixes or removes the file.
    /// </summary>
    Unreadable,
}

/// <summary>
/// Local accounts in <c>{Storage:BaseDirectory}/auth/users.json</c>, written atomically like the
/// other stores. Passwords are stored as ASP.NET Core Identity hashes (PBKDF2, salted, iterated),
/// never in clear.
/// </summary>
public sealed class LocalUserStore(IOptions<StorageOptions> storage, TimeProvider? time = null, ILogger<LocalUserStore>? logger = null)
{
    public const int SchemaVersion = 1;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly PasswordHasher<LocalUser> _hasher = new();
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly ILogger _logger = logger ?? NullLogger<LocalUserStore>.Instance;
    private List<LocalUser>? _users;

    // Verified against when the user name is unknown, so a miss costs the same time as a wrong password.
    private readonly Lazy<string> _dummyHash = new(() => new PasswordHasher<LocalUser>().HashPassword(Dummy, "not-a-password"));
    private static readonly LocalUser Dummy = new("", "", AuthRoles.Viewer, DateTimeOffset.MinValue);

    public string FilePath => Path.Combine(storage.Value.BaseDirectory, "auth", "users.json");

    public async Task<LocalUsersState> StateAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            return users is null ? LocalUsersState.Unreadable : users.Count > 0 ? LocalUsersState.HasUsers : LocalUsersState.Empty;
        }
        finally { _lock.Release(); }
    }

    /// <summary>True when any account exists, or when the file can't be read (fail closed).</summary>
    public async Task<bool> AnyAsync(CancellationToken ct = default) => await StateAsync(ct) != LocalUsersState.Empty;

    public async Task<LocalUser?> FindAsync(string userName, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAsync(ct) is { } users ? Find(users, userName) : null; }
        finally { _lock.Release(); }
    }

    /// <summary>The user, if the name exists and the password matches; otherwise null (also when the file is unreadable).</summary>
    public async Task<LocalUser?> VerifyAsync(string userName, string password, CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            var user = users is null ? null : Find(users, userName);
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
                    // Same password, stronger hash: the stamp stays, sessions stay valid.
                    var rehashed = user with { PasswordHash = _hasher.HashPassword(user, password) };
                    users![users.IndexOf(user)] = rehashed;
                    await SaveAsync(users, ct);
                    return rehashed;
                default:
                    return null;
            }
        }
        finally { _lock.Release(); }
    }

    /// <summary>
    /// Creates the first admin. False if any account exists already, or if the file can't be read:
    /// first-run setup happens once, even when two requests race for it, and never over a damaged file.
    /// </summary>
    public Task<bool> CreateFirstAdminAsync(string userName, string password, CancellationToken ct = default) =>
        CreateAsync(userName, password, AuthRoles.Admin, onlyIfEmpty: true, ct);

    /// <summary>Adds an account. False if the name is taken, the file is unreadable, or (with <paramref name="onlyIfEmpty"/>) any account exists.</summary>
    public async Task<bool> CreateAsync(string userName, string password, string role, bool onlyIfEmpty = false, CancellationToken ct = default)
    {
        if (!AuthRoles.IsKnown(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        userName = userName.Trim();
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            if (users is null) return false;
            if (onlyIfEmpty && users.Count > 0) return false;
            if (Find(users, userName) is not null) return false;

            var user = new LocalUser(userName, "", role, _time.GetUtcNow(), NewStamp());
            users.Add(user with { PasswordHash = _hasher.HashPassword(user, password) });
            await SaveAsync(users, ct);
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>New password and a new stamp: the user's existing sessions end.</summary>
    public Task<bool> SetPasswordAsync(string userName, string password, CancellationToken ct = default) =>
        UpdateAsync(userName, u => u with { PasswordHash = _hasher.HashPassword(u, password), SecurityStamp = NewStamp() }, ct);

    /// <summary>New role and a new stamp: the user's existing sessions end.</summary>
    public Task<bool> SetRoleAsync(string userName, string role, CancellationToken ct = default)
    {
        if (!AuthRoles.IsKnown(role)) throw new ArgumentException($"Unknown role '{role}'.", nameof(role));
        return UpdateAsync(userName, u => u with { Role = role, SecurityStamp = NewStamp() }, ct);
    }

    /// <summary>Removes the account: its sessions end at their next check.</summary>
    public Task<bool> DeleteAsync(string userName, CancellationToken ct = default) => UpdateAsync(userName, _ => null, ct);

    private async Task<bool> UpdateAsync(string userName, Func<LocalUser, LocalUser?> change, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var users = await LoadAsync(ct);
            if (users is null || Find(users, userName) is not { } user) return false;
            var index = users.IndexOf(user);
            if (change(user) is { } changed) users[index] = changed;
            else users.RemoveAt(index);
            await SaveAsync(users, ct);
            return true;
        }
        finally { _lock.Release(); }
    }

    /// <summary>The stamp a session must carry; accounts written without one compare as empty.</summary>
    public static string StampOf(LocalUser user) => user.SecurityStamp ?? "";

    private static LocalUser? Find(List<LocalUser> users, string userName) =>
        users.FirstOrDefault(u => string.Equals(u.UserName, userName.Trim(), StringComparison.OrdinalIgnoreCase));

    private static string NewStamp() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    /// <summary>The accounts, or null when the file exists but can't be read (not cached: a fixed file is picked up).</summary>
    private async Task<List<LocalUser>?> LoadAsync(CancellationToken ct)
    {
        if (_users is not null) return _users;
        if (!File.Exists(FilePath)) return _users = [];

        try
        {
            await using var stream = File.OpenRead(FilePath);
            var file = await JsonSerializer.DeserializeAsync<UsersFile>(stream, Json, ct)
                       ?? throw new InvalidDataException("the file is empty");
            if (file.SchemaVersion != SchemaVersion)
                throw new InvalidDataException($"schemaVersion {file.SchemaVersion}; this version reads {SchemaVersion}");
            return _users = [.. file.Users ?? []];
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex,
                "Local accounts file {Path} can't be read ({Reason}). Local sign-in and first-run setup are refused until it is fixed or removed.",
                FilePath, ex.Message);
            return null;
        }
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
