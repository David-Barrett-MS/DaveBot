using Microsoft.AspNetCore.Identity;

namespace DaveIdentity;

public enum SignInResult
{
    Succeeded,
    InvalidCredentials,
    Disabled,
    LockedOut,
}

public sealed record SignInAttempt(SignInResult Result, IdentityUserRecord? User);

public sealed class LocalUserService(
    IdentityStateStore stateStore,
    IPasswordHasher<IdentityUserRecord> passwordHasher)
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await stateStore.InitializeAsync(cancellationToken);
        if (stateStore.GetUsers().Any(user => user.Enabled &&
            user.Roles.Contains(DaveRoles.Admin, StringComparer.Ordinal)))
        {
            return;
        }

        await stateStore.UpdateAsync(document =>
        {
            if (document.Users.Any(user => user.NormalizedUserName == NormalizeUserName("Dave")))
            {
                throw new InvalidOperationException(
                    "No active administrator exists and the default Dave account cannot be recreated.");
            }

            return document with
            {
                Users = document.Users.Append(CreateInitialAdministrator()).ToArray(),
            };
        }, cancellationToken);
    }

    public async Task<IdentityUserRecord> CreateUserAsync(
        string userName,
        string password,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken = default)
    {
        ValidateCredentials(userName, password);
        ValidateRoles(roles);
        IdentityUserRecord? created = null;
        var normalized = NormalizeUserName(userName);
        await stateStore.UpdateAsync(document =>
        {
            if (document.Users.Any(user => user.NormalizedUserName == normalized))
            {
                throw new InvalidOperationException("The username is already in use.");
            }

            created = CreateUser(userName, password, roles);
            return document with { Users = document.Users.Append(created).ToArray() };
        }, cancellationToken);
        return created ?? throw new InvalidOperationException("The user was not created.");
    }

    public async Task<SignInAttempt> ValidatePasswordAsync(
        string userName,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await stateStore.FindByNormalizedUserNameAsync(
            NormalizeUserName(userName),
            cancellationToken);
        if (user is null)
        {
            return new SignInAttempt(SignInResult.InvalidCredentials, null);
        }

        if (!user.Enabled)
        {
            return new SignInAttempt(SignInResult.Disabled, user);
        }

        if (user.LockoutEndUtc > DateTimeOffset.UtcNow)
        {
            return new SignInAttempt(SignInResult.LockedOut, user);
        }

        var verification = passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (verification is PasswordVerificationResult.Failed)
        {
            await RecordFailedSignInAsync(user.Id, cancellationToken);
            return new SignInAttempt(SignInResult.InvalidCredentials, null);
        }

        if (verification is PasswordVerificationResult.SuccessRehashNeeded)
        {
            await stateStore.UpdateAsync(document => document with
            {
                Users = document.Users.Select(candidate => candidate.Id == user.Id
                    ? candidate with { PasswordHash = passwordHasher.HashPassword(candidate, password) }
                    : candidate).ToArray(),
            }, cancellationToken);
        }

        await stateStore.UpdateAsync(document => document with
        {
            Users = document.Users.Select(candidate => candidate.Id == user.Id
                ? candidate with { AccessFailedCount = 0, LockoutEndUtc = null }
                : candidate).ToArray(),
        }, cancellationToken);
        return new SignInAttempt(SignInResult.Succeeded, user);
    }

    public IReadOnlyList<IdentityUserRecord> GetUsers() => stateStore.GetUsers();

    public async Task<bool> IsActiveAdministratorAsync(
        Guid userId,
        CancellationToken cancellationToken = default)
    {
        var user = await stateStore.FindUserAsync(userId, cancellationToken);
        return user is { Enabled: true } &&
            user.Roles.Contains(DaveRoles.Admin, StringComparer.Ordinal);
    }

    public Task SetRolesAsync(
        Guid userId,
        IReadOnlyCollection<string> roles,
        CancellationToken cancellationToken = default)
    {
        ValidateRoles(roles);
        return UpdateUserAsync(userId, user => user with
        {
            Roles = roles.Distinct(StringComparer.Ordinal).ToArray(),
        }, revokeGrants: true, cancellationToken);
    }

    public Task SetEnabledAsync(
        Guid userId,
        bool enabled,
        CancellationToken cancellationToken = default) =>
        UpdateUserAsync(userId, user => user with { Enabled = enabled }, !enabled, cancellationToken);

    public Task ResetPasswordAsync(
        Guid userId,
        string password,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(password);
        return UpdateUserAsync(userId, user => user with
        {
            PasswordHash = passwordHasher.HashPassword(user, password),
        }, revokeGrants: true, cancellationToken);
    }

    public async Task CompleteInitialPasswordChangeAsync(
        Guid userId,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        ValidatePassword(newPassword);
        var user = await stateStore.FindUserAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("The user does not exist.");
        if (!user.Enabled || !user.MustChangePassword ||
            passwordHasher.VerifyHashedPassword(user, user.PasswordHash, currentPassword)
                is PasswordVerificationResult.Failed)
        {
            throw new InvalidOperationException("The current password is incorrect.");
        }

        await UpdateUserAsync(userId, candidate => candidate with
        {
            PasswordHash = passwordHasher.HashPassword(candidate, newPassword),
            MustChangePassword = false,
            AccessFailedCount = 0,
            LockoutEndUtc = null,
        }, revokeGrants: true, cancellationToken);
    }

    private async Task RecordFailedSignInAsync(Guid userId, CancellationToken cancellationToken)
    {
        await stateStore.UpdateAsync(document => document with
        {
            Users = document.Users.Select(user =>
            {
                if (user.Id != userId)
                {
                    return user;
                }

                var failures = user.AccessFailedCount + 1;
                return user with
                {
                    AccessFailedCount = failures,
                    LockoutEndUtc = failures >= 5
                        ? DateTimeOffset.UtcNow.AddMinutes(15)
                        : user.LockoutEndUtc,
                };
            }).ToArray(),
        }, cancellationToken);
    }

    private IdentityUserRecord CreateUser(
        string userName,
        string password,
        IEnumerable<string> roles,
        bool mustChangePassword = false)
    {
        var user = new IdentityUserRecord(
            Guid.NewGuid(),
            userName.Trim(),
            NormalizeUserName(userName),
            PasswordHash: string.Empty,
            roles.Distinct(StringComparer.Ordinal).ToArray(),
            Enabled: true,
            AccessFailedCount: 0,
            LockoutEndUtc: null,
            SecurityStamp: Guid.NewGuid().ToString("N"),
            MustChangePassword: mustChangePassword);
        return user with { PasswordHash = passwordHasher.HashPassword(user, password) };
    }

    private IdentityUserRecord CreateInitialAdministrator() =>
        CreateUser("Dave", "Dave", [DaveRoles.Admin], mustChangePassword: true);

    private async Task UpdateUserAsync(
        Guid userId,
        Func<IdentityUserRecord, IdentityUserRecord> update,
        bool revokeGrants,
        CancellationToken cancellationToken)
    {
        await stateStore.UpdateAsync(document =>
        {
            if (document.Users.All(user => user.Id != userId))
            {
                throw new InvalidOperationException("The user does not exist.");
            }

            return document with
            {
                Users = document.Users.Select(user => user.Id == userId
                    ? update(user) with { SecurityStamp = Guid.NewGuid().ToString("N") }
                    : user).ToArray(),
                Grants = revokeGrants
                    ? document.Grants.Select(grant => grant.UserId == userId && grant.RevokedUtc is null
                        ? grant with { RevokedUtc = DateTimeOffset.UtcNow }
                        : grant).ToArray()
                    : document.Grants,
            };
        }, cancellationToken);
    }

    private static void ValidateCredentials(string userName, string password)
    {
        if (string.IsNullOrWhiteSpace(userName) || userName.Length > 256)
        {
            throw new ArgumentException("A username from 1 to 256 characters is required.", nameof(userName));
        }

        ValidatePassword(password);
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 8)
        {
            throw new ArgumentException("A password of at least 8 characters is required.", nameof(password));
        }
    }

    private static void ValidateRoles(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0 ||
            roles.Any(role => role is not (DaveRoles.Admin or DaveRoles.User)))
        {
            throw new ArgumentException("Users must have at least one supported role.", nameof(roles));
        }
    }

    public static string NormalizeUserName(string userName) => userName.Trim().ToUpperInvariant();
}
