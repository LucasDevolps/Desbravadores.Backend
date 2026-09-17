using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Options;

// Proteção de POST /api/Auth/login na própria aplicação (independente do nginx).
public sealed class LoginProtectionOptions
{
    public const string SectionName = "LoginProtection";

    public RateLimitSettings RateLimit { get; set; } = new();
    public LockoutSettings Lockout { get; set; } = new();

    // Janela fixa por IP (após ForwardedHeaders). Conta todas as tentativas, com ou sem sucesso.
    // Contador em memória: vale por instância da API, não é global.
    public sealed class RateLimitSettings
    {
        [Range(1, 10_000)] public int PermitLimit { get; set; } = 5;
        [Range(1, 3600)] public int WindowSeconds { get; set; } = 60;
    }

    // Por conta, persistido no banco (global entre instâncias). Só falhas de senha contam.
    public sealed class LockoutSettings
    {
        [Range(1, 1000)] public int MaxFailedAttempts { get; set; } = 10;
        [Range(1, 1440)] public int FailureWindowMinutes { get; set; } = 15;
        [Range(1, 1440)] public int LockoutMinutes { get; set; } = 15;
    }
}
