using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Options;

public sealed class LoginProtectionOptions
{
    public const string SectionName = "LoginProtection";

    public RateLimitSettings RateLimit { get; set; } = new();
    public LockoutSettings Lockout { get; set; } = new();

    public sealed class RateLimitSettings
    {
        [Range(1, 10_000)] public int PermitLimit { get; set; } = 5;
        [Range(1, 3600)] public int WindowSeconds { get; set; } = 60;
    }

    public sealed class LockoutSettings
    {
        [Range(1, 1000)] public int MaxFailedAttempts { get; set; } = 10;
        [Range(1, 1440)] public int FailureWindowMinutes { get; set; } = 15;
        [Range(1, 1440)] public int LockoutMinutes { get; set; } = 15;
    }
}
