using System.Security.Cryptography;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public class SecretsAndPasswordPolicyTests
{
    // ---- Chave JWT ----

    private static JwtOptions Jwt(string? key, string kid = "v1", string activeKid = "v1") => new()
    {
        Issuer = "iss", Audience = "aud", ActiveKeyId = activeKid,
        Keys = key is null ? [] : new Dictionary<string, string> { [kid] = key },
    };

    public static TheoryData<string?, string, string> ChavesInvalidas => new()
    {
        { null, "vazio", "sem chaves" },
        { "", "ausente", "string vazia" },
        { "DEFINA_BASE64_DE_32_BYTES_ALEATORIOS", "placeholder", "placeholder do .env.example" },
        { "não-é-base64!!", "Base64", "formato inválido" },
        // 32 CARACTERES Base64 = só 24 bytes: comprimento textual não é tamanho de chave.
        { Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)), "24 bytes", "32 caracteres porém 24 bytes" },
        { Convert.ToBase64String(RandomNumberGenerator.GetBytes(31)), "31 bytes", "31 bytes" },
        { Convert.ToBase64String(new byte[32]), "trivial", "32 bytes zerados" },
    };

    [Theory]
    [MemberData(nameof(ChavesInvalidas))]
    public void JwtOptionsValidator_RejeitaChaveInvalida_SemExporValor(string? key, string trechoEsperado, string cenario)
    {
        var result = new JwtOptionsValidator().Validate(null, Jwt(key));

        Assert.True(result.Failed, cenario);
        Assert.Contains(trechoEsperado, result.FailureMessage, StringComparison.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(key)) Assert.DoesNotContain(key, result.FailureMessage);
    }

    [Fact]
    public void JwtOptionsValidator_AceitaChaveAleatoriaDe32Bytes_ERejeitaKidAtivoInexistente()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Assert.True(new JwtOptionsValidator().Validate(null, Jwt(key)).Succeeded);
        Assert.True(new JwtOptionsValidator().Validate(null, Jwt(key, activeKid: "v2")).Failed);
    }

    [Theory]
    [InlineData("DEFINA_BASE64_DE_32_BYTES_ALEATORIOS")]
    public void Startup_FalhaComChaveJwtInvalida(string key)
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Jwt:Keys:test-v1"] = key });
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var validation = Assert.IsType<OptionsValidationException>(Unwrap(exception));
        Assert.Contains("Jwt:Keys:test-v1", validation.Message);
    }

    [Fact]
    public void Startup_FalhaSemChavesJwt()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Jwt:Keys:test-v1"] = null, ["Jwt:ActiveKeyId"] = "",
        });
        Assert.IsType<OptionsValidationException>(Unwrap(Assert.ThrowsAny<Exception>(() => factory.CreateClient())));
    }

    // Issue #50 (rotação de chave): compose.yaml passa a mapear Jwt__Keys__v1/v2 como opcionais
    // (${JWT_KEY_V1:-}), então uma chave "não configurada" chega como string vazia, não ausente. Uma
    // entrada vazia deve se comportar como se não existisse (mesmo resultado de Startup_FalhaSemChavesJwt
    // acima), não como "chave presente porém inválida" — ver PostConfigure<JwtOptions> em Program.cs.
    [Fact]
    public void Startup_TrataChaveVazia_ComoNaoConfigurada_NaoComoInvalida()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["Jwt:Keys:test-v1"] = "" });
        var exception = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var validation = Assert.IsType<OptionsValidationException>(Unwrap(exception));
        Assert.DoesNotContain("Jwt:Keys:test-v1", validation.Message);
    }

    [Fact]
    public void Startup_AceitaDuasChaves_EEmiteComAActiveKeyId()
    {
        var v1 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var v2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?>
        {
            ["Jwt:Keys:v1"] = v1, ["Jwt:Keys:v2"] = v2, ["Jwt:ActiveKeyId"] = "v2",
        });
        using var client = factory.CreateClient(); // não lança: as duas chaves e a ativa são válidas.
    }

    [Fact]
    public void Startup_AceitaSoAChaveV2_ComV1EfetivamenteRemovida()
    {
        var v2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?>
        {
            // "" simula compose.yaml com JWT_KEY_V1 vazio/ausente no .env (rotação concluída).
            ["Jwt:Keys:v1"] = "", ["Jwt:Keys:v2"] = v2, ["Jwt:ActiveKeyId"] = "v2",
        });
        using var client = factory.CreateClient();
    }

    // ---- Política de senha ----

    [Theory]
    [InlineData("senha")]
    [InlineData("senha123")]
    [InlineData("Senha@2026!!")]
    [InlineData("Admin#123456789")]
    [InlineData("aaaaaaaaaaaaaa")]
    [InlineData("abababababab")]
    [InlineData("123456789012")]
    [InlineData("abcdefghijklm")]
    [InlineData("4839201758392017")]
    [InlineData("Curta-9x")]
    [InlineData("DEFINA_UMA_SENHA_FORTE_FORA_DO_GIT")]
    [InlineData("")]
    [InlineData("   ")]
    public void PasswordPolicy_RejeitaSenhasFracas(string senha) =>
        Assert.NotEmpty(PasswordPolicy.Validate(senha, "admin@local.dev"));

    [Fact]
    public void PasswordPolicy_RejeitaLimites_EEmailNaSenha()
    {
        Assert.NotEmpty(PasswordPolicy.Validate("Correta-Cavalo-" + new string('x', 114) + "9")); // 130 caracteres
        Assert.NotEmpty(PasswordPolicy.Validate("tesoureiro-Forte-2026", "tesoureiro@clube.org"));
        Assert.NotEmpty(PasswordPolicy.Validate(null));
    }

    [Theory]
    [InlineData("correct-Horse-battery-9")]
    [InlineData("Tres Palavras Longas Aqui")]
    public void PasswordPolicy_AceitaSenhaForte(string senha) =>
        Assert.Empty(PasswordPolicy.Validate(senha, "admin@local.dev"));

    [Fact]
    public void PasswordHasher_NaoTruncaSenhasNoLimiteMaximoDaPolitica()
    {
        var hasher = new PasswordHasher<Usuario>();
        var usuario = new Usuario { Nome = "", Email = "", EmailNormalizado = "", SenhaHash = "" };
        var baseSenha = "Kx7-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(62));  // 128 caracteres
        Assert.Equal(PasswordPolicy.MaxLength, baseSenha.Length);
        Assert.Empty(PasswordPolicy.Validate(baseSenha));

        var hash = hasher.HashPassword(usuario, baseSenha);
        var ultimoDiferente = baseSenha[..^1] + (baseSenha[^1] == 'A' ? 'B' : 'A');

        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(usuario, hash, baseSenha));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(usuario, hash, ultimoDiferente));
    }

    // ---- Seed do administrador ----

    private static AlmiranteDbContext NovoContexto() =>
        new(new DbContextOptionsBuilder<AlmiranteDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    private static IOptions<SeedOptions> Seed(string? senha) =>
        Microsoft.Extensions.Options.Options.Create(new SeedOptions { Nome = "Admin", Email = "admin@local.dev", Senha = senha! });

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("senha")]
    [InlineData("DEFINA_VIA_USER_SECRETS")]
    public async Task Seed_ComSenhaInvalida_FalhaSemCriarAdmin_ESemExporValor(string? senha)
    {
        await using var db = NovoContexto();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DbSeeder.SeedAsync(db, new PasswordHasher<Usuario>(), Seed(senha)));

        Assert.Contains("SeedAdmin:Senha", exception.Message);
        if (!string.IsNullOrEmpty(senha)) Assert.DoesNotContain($"\"{senha}\"", exception.Message);
        Assert.False(await db.Usuarios.AnyAsync());
    }

    [Fact]
    public async Task Seed_EIdempotente_ENaoSobrescreveSenhaDeAdminExistente()
    {
        await using var db = NovoContexto();
        var hasher = new PasswordHasher<Usuario>();
        const string senhaOriginal = "Primeira-Senha-Forte-2026";

        await DbSeeder.SeedAsync(db, hasher, Seed(senhaOriginal));
        var hashOriginal = (await db.Usuarios.SingleAsync()).SenhaHash;

        // Execução posterior com senha diferente (até inválida) não falha nem altera o admin existente.
        await DbSeeder.SeedAsync(db, hasher, Seed("senha"));
        await DbSeeder.SeedAsync(db, hasher, Seed("Outra-Senha-Forte-Valida-99"));

        var admin = await db.Usuarios.SingleAsync();
        Assert.Equal(hashOriginal, admin.SenhaHash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(admin, admin.SenhaHash, senhaOriginal));
    }

    [Fact]
    public void Startup_FalhaQuandoBootstrapUsaSenhaInvalida()
    {
        using var factory = new ConfiguredApiFactory(overrides: new Dictionary<string, string?> { ["SeedAdmin:Senha"] = "senha" });
        var exception = Unwrap(Assert.ThrowsAny<Exception>(() => factory.CreateClient()));
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("SeedAdmin:Senha", exception.Message);
    }

    private static Exception Unwrap(Exception exception)
    {
        while (exception is AggregateException or System.Reflection.TargetInvocationException && exception.InnerException is not null)
            exception = exception.InnerException!;
        return exception;
    }
}
