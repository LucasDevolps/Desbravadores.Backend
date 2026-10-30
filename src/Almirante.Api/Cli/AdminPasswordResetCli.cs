using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Cli;

public static class AdminPasswordResetCli
{
    public const string CommandName = "reset-admin-password";
    private const string MotivoRevogacao = "reset-senha-admin";
    private const int MaxTentativasConcorrencia = 5;

    public enum ResetResult
    {
        Sucesso,
        UsuarioNaoEncontrado,
        SenhaInvalida,
    }

    public static async Task<(ResetResult Resultado, IReadOnlyList<string> Erros)> ResetPasswordAsync(
        AlmiranteDbContext db,
        IPasswordHasher<Usuario> passwordHasher,
        TimeProvider clock,
        string email,
        string novaSenha,
        CancellationToken cancellationToken = default)
    {
        var emailNormalizado = email.Trim().ToUpperInvariant();

        for (var tentativa = 1; ; tentativa++)
        {
            db.ChangeTracker.Clear();
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
            usuario.SecurityVersion++;

            var agora = clock.GetUtcNow().UtcDateTime;
            var sessoesAtivas = await db.AuthSessions
                .Where(s => s.UsuarioId == usuario.Id && s.RevokedAtUtc == null)
                .ToListAsync(cancellationToken);
            foreach (var sessao in sessoesAtivas)
            {
                sessao.RevokedAtUtc = agora;
                sessao.RevocationReason = MotivoRevogacao;
            }

            // A autorização emitida antes já ficaria obsoleta pela SecurityVersion; encerrá-la tira o pedido das pendências. O CLI não tem
            // usuário autenticado: não grava em Usuarios_alteracoes_senha nem inventa responsável (limite documentado).
            var recuperacoesAbertas = await db.RecuperacoesSenha
                .Where(r => r.UsuarioId == usuario.Id && (r.Status == StatusRecuperacaoSenha.Pendente || r.Status == StatusRecuperacaoSenha.Autorizada))
                .ToListAsync(cancellationToken);
            foreach (var recuperacao in recuperacoesAbertas)
            {
                recuperacao.Status = StatusRecuperacaoSenha.Invalidada;
                recuperacao.EncerradaEmUtc = agora;
                recuperacao.MotivoEncerramento = MotivoRevogacao;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return (ResetResult.Sucesso, []);
            }
            catch (DbUpdateConcurrencyException) when (tentativa < MaxTentativasConcorrencia)
            {
            }
        }
    }

    public static Task<int> RunAsync(IServiceProvider services, string[] args, string? connectionStringOverride = null, CancellationToken cancellationToken = default) =>
        ExecutarAsync(services, args, TerminalCli.Console, connectionStringOverride, cancellationToken);

    public static async Task<int> ExecutarAsync(IServiceProvider services, string[] args, TerminalCli terminal, string? connectionStringOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            terminal.Erro.WriteLine($"Uso: dotnet run --project src/Almirante.Api -- {CommandName} <email>");
            return 1;
        }

        var email = args[1];
        var senha = ReadPasswordFromConsole(terminal, "Nova senha: ");
        var confirmacao = ReadPasswordFromConsole(terminal, "Confirme a nova senha: ");
        if (!string.Equals(senha, confirmacao, StringComparison.Ordinal))
        {
            terminal.Erro.WriteLine("As senhas informadas não coincidem.");
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
                terminal.Erro.WriteLine("Nenhum usuário encontrado com esse e-mail.");
                return 1;
            case ResetResult.SenhaInvalida:
                terminal.Erro.WriteLine("Senha não atende à política de senha: " + string.Join(" ", erros));
                return 1;
            default:
                terminal.Saida.WriteLine("Senha atualizada e sessões ativas dessa conta foram revogadas (novo login será exigido em todo dispositivo).");
                return 0;
        }
    }

    private static string ReadPasswordFromConsole(TerminalCli terminal, string prompt)
    {
        terminal.Saida.Write(prompt);
        if (terminal.EntradaRedirecionada)
        {
            var line = terminal.Entrada.ReadLine();
            terminal.Saida.WriteLine();
            return line ?? "";
        }

        var buffer = new System.Text.StringBuilder();
        ConsoleKeyInfo key;
        while ((key = terminal.LerTecla()).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (buffer.Length > 0)
                {
                    buffer.Length--;
                    terminal.Saida.Write(" ");
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                buffer.Append(key.KeyChar);
                terminal.Saida.Write('*');
            }
        }

        terminal.Saida.WriteLine();
        return buffer.ToString();
    }
}

/// <summary>
/// Entrada e saída da CLI. O padrão é o console do processo (prompt sem eco por <see cref="System.Console.ReadKey(bool)"/>); os testes
/// passam um terminal próprio para exercitar o prompt, a confirmação e as mensagens sem depender de um console real.
/// </summary>
public sealed record TerminalCli(bool EntradaRedirecionada, TextReader Entrada, Func<ConsoleKeyInfo> LerTecla, TextWriter Saida, TextWriter Erro)
{
    public static TerminalCli Console => new(System.Console.IsInputRedirected, System.Console.In, () => System.Console.ReadKey(intercept: true),
        System.Console.Out, System.Console.Error);
}
