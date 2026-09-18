using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Cli;

// Rotação de senha do admin (issue #50): hoje não existe endpoint de autoatendimento nem ferramenta
// alguma para isso fora de editar o banco direto (ver docs/authentication-security.md, seção
// "Rotação de valores expostos"). Este comando reaproveita o mesmo IPasswordHasher<Usuario> e a
// mesma PasswordPolicy já usados pelo seed (DbSeeder), sem tocar no fluxo de seed idempotente, e
// revoga as sessões ativas do usuário na mesma chamada a SaveChangesAsync (atômico via o DbContext).
// A senha nunca passa por argv (só prompt interativo sem eco) nem é logada em nenhum ponto.
public static class AdminPasswordResetCli
{
    public const string CommandName = "reset-admin-password";
    private const string MotivoRevogacao = "reset-senha-admin";

    public enum ResetResult
    {
        Sucesso,
        UsuarioNaoEncontrado,
        SenhaInvalida,
    }

    // Lógica testável (sem I/O de console): usada tanto pelo CLI real quanto pelos testes.
    public static async Task<(ResetResult Resultado, IReadOnlyList<string> Erros)> ResetPasswordAsync(
        AlmiranteDbContext db,
        IPasswordHasher<Usuario> passwordHasher,
        TimeProvider clock,
        string email,
        string novaSenha,
        CancellationToken cancellationToken = default)
    {
        var emailNormalizado = email.Trim().ToUpperInvariant();
        var usuario = await db.Usuarios.SingleOrDefaultAsync(u => u.EmailNormalizado == emailNormalizado, cancellationToken);
        if (usuario is null)
        {
            return (ResetResult.UsuarioNaoEncontrado, []);
        }

        var erros = PasswordPolicy.Validate(novaSenha, usuario.Email);
        if (erros.Count > 0)
        {
            return (ResetResult.SenhaInvalida, erros);
        }

        usuario.SenhaHash = passwordHasher.HashPassword(usuario, novaSenha);

        // Só remover a senha antiga não invalida sessões já emitidas (refresh token é opaco e não
        // revalida credenciais) — revogar aqui é o que de fato força novo login em todo dispositivo.
        var agora = clock.GetUtcNow().UtcDateTime;
        var sessoesAtivas = await db.AuthSessions
            .Where(s => s.UsuarioId == usuario.Id && s.RevokedAtUtc == null)
            .ToListAsync(cancellationToken);
        foreach (var sessao in sessoesAtivas)
        {
            sessao.RevokedAtUtc = agora;
            sessao.RevocationReason = MotivoRevogacao;
        }

        await db.SaveChangesAsync(cancellationToken);
        return (ResetResult.Sucesso, []);
    }

    // Entrada de linha de comando: `dotnet run --project backend/Almirante.Api -- reset-admin-password <email>`.
    // Roda antes do host normal subir (ver Program.cs) e nunca aceita a senha como argumento —
    // só via prompt interativo sem eco, para não sobrar em histórico de shell/log de processo.
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, string? connectionStringOverride = null, CancellationToken cancellationToken = default)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Uso: dotnet run --project backend/Almirante.Api -- {CommandName} <email>");
            return 1;
        }

        var email = args[1];
        var senha = ReadPasswordFromConsole("Nova senha: ");
        var confirmacao = ReadPasswordFromConsole("Confirme a nova senha: ");
        if (!string.Equals(senha, confirmacao, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("As senhas informadas não coincidem.");
            return 1;
        }

        using var scope = services.CreateScope();
        await using var overrideDb = connectionStringOverride is null ? null
            : new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>().UseSqlServer(connectionStringOverride).Options);
        var db = overrideDb ?? scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
        var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>();
        var clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

        var (resultado, erros) = await ResetPasswordAsync(db, passwordHasher, clock, email, senha, cancellationToken);
        switch (resultado)
        {
            case ResetResult.UsuarioNaoEncontrado:
                Console.Error.WriteLine("Nenhum usuário encontrado com esse e-mail.");
                return 1;
            case ResetResult.SenhaInvalida:
                Console.Error.WriteLine("Senha não atende à política de senha: " + string.Join(" ", erros));
                return 1;
            default:
                Console.WriteLine("Senha atualizada e sessões ativas dessa conta foram revogadas (novo login será exigido em todo dispositivo).");
                return 0;
        }
    }

    private static string ReadPasswordFromConsole(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            // Sem TTY (pipe, docker exec sem -it): ReadKey lançaria InvalidOperationException. Lê uma
            // linha do stdin (não há eco, pois não há terminal); a senha continua fora de argv/log.
            var line = Console.In.ReadLine();
            Console.WriteLine();
            return line ?? "";
        }

        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    Console.Write("\b \b");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                Console.Write('*');
            }
        }

        Console.WriteLine();
        return buffer.ToString();
    }
}
