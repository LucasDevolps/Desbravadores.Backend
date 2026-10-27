using System.Net;
using Almirante.Api.Cli;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

public sealed class AdminPasswordResetCliTests : IClassFixture<AlmiranteApiFactory>
{
    private readonly AlmiranteApiFactory factory;
    public AdminPasswordResetCliTests(AlmiranteApiFactory factory) => this.factory = factory;

    private (AlmiranteDbContext Db, IPasswordHasher<Usuario> Hasher, TimeProvider Clock, IServiceScope Scope) ResolveDependencies()
    {
        var scope = factory.Services.CreateScope();
        return (
            scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>(),
            scope.ServiceProvider.GetRequiredService<IPasswordHasher<Usuario>>(),
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope);
    }

    [Fact]
    public async Task ResetPasswordAsync_EncerraRecuperacaoAberta_SemGravarAuditoriaDeResponsavel()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "ComPedidoAberto", "DS");
        var recuperacaoId = Guid.NewGuid();
        await TestHelpers.WithDbAsync(factory, async db =>
        {
            db.RecuperacoesSenha.Add(new RecuperacaoSenha
            {
                Id = recuperacaoId, UsuarioId = usuario.Id, Origem = OrigemRecuperacaoSenha.SolicitacaoPublica, Status = StatusRecuperacaoSenha.Pendente,
                SolicitadaEmUtc = DateTime.UtcNow, UltimaSolicitacaoEmUtc = DateTime.UtcNow, Solicitacoes = 1, IpSolicitacao = "127.0.0.1",
                ExpiraEmUtc = DateTime.UtcNow.AddDays(7),
            });
            return await db.SaveChangesAsync();
        });

        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, _) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "Uma frase nova e bem forte 2026");
            Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado);
        }

        var recuperacao = await TestHelpers.WithDbAsync(factory, db => db.RecuperacoesSenha.AsNoTracking().SingleAsync(r => r.Id == recuperacaoId));
        Assert.Equal((StatusRecuperacaoSenha.Invalidada, "reset-senha-admin"), (recuperacao.Status, recuperacao.MotivoEncerramento));
        Assert.NotNull(recuperacao.EncerradaEmUtc);
        Assert.False(await TestHelpers.WithDbAsync(factory, db => db.AlteracoesSenha.AnyAsync(a => a.UsuarioAfetadoId == usuario.Id)));
    }

    [Fact]
    public async Task ResetPasswordAsync_AtualizaHash_ERevogaSessaoAtiva_ImpedindoRefreshAntigo()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "ParaResetarSenha", "DS");
        var client = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(client, usuario.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);

        const string novaSenha = "Uma$enhaNovaBemForte2026";
        var (db, hasher, clock, scope) = ResolveDependencies();
        (AdminPasswordResetCli.ResetResult Resultado, IReadOnlyList<string> Erros) resultado;
        using (scope)
        {
            resultado = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, novaSenha);
        }

        Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado.Resultado);
        Assert.Empty(resultado.Erros);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsync("/api/Auth/refresh", null)).StatusCode);

        var novoClient = factory.CreateClient();
        await TestHelpers.AddCsrfAsync(novoClient);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TestHelpers.PostLoginAsync(novoClient, usuario.Email, TestHelpers.SenhaPadraoTeste)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await TestHelpers.PostLoginAsync(novoClient, usuario.Email, novaSenha)).StatusCode);
    }

    [Fact]
    public async Task ResetPasswordAsync_UsuarioInexistente_RetornaResultadoEspecifico_SemAlterarNada()
    {
        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, erros) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, "nao-existe@local.dev", "Qualquer$enhaForte123");
            Assert.Equal(AdminPasswordResetCli.ResetResult.UsuarioNaoEncontrado, resultado);
            Assert.Empty(erros);
        }
    }

    [Fact]
    public async Task ResetPasswordAsync_SenhaForaDaPolitica_NaoAlteraOHashExistente()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "SenhaFraca", "DS");
        var hashAntes = usuario.SenhaHash;

        var (db, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, erros) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "senha");
            Assert.Equal(AdminPasswordResetCli.ResetResult.SenhaInvalida, resultado);
            Assert.NotEmpty(erros);
        }

        var hashDepois = await TestHelpers.WithDbAsync(factory, db =>
            db.Usuarios.AsNoTracking().Where(u => u.Id == usuario.Id).Select(u => u.SenhaHash).SingleAsync());
        Assert.Equal(hashAntes, hashDepois);
    }

    private sealed class TerminalDeTeste
    {
        private readonly Queue<ConsoleKeyInfo> _teclas = new();
        private StringReader _linhas = new("");
        private bool _interativo;

        public StringWriter Saida { get; } = new();
        public StringWriter Erro { get; } = new();
        public TerminalCli Cli => new(!_interativo, _linhas, () => _teclas.Dequeue(), Saida, Erro);

        public static TerminalDeTeste Redirecionado(params string[] linhas) => new() { _linhas = new StringReader(string.Join('\n', linhas)) };

        public static TerminalDeTeste Interativo(params ConsoleKeyInfo[] teclas)
        {
            var terminal = new TerminalDeTeste { _interativo = true };
            foreach (var tecla in teclas) terminal._teclas.Enqueue(tecla);
            return terminal;
        }
    }

    private static ConsoleKeyInfo Tecla(char c) => new(c, ConsoleKey.A, false, false, false);
    private static ConsoleKeyInfo Tecla(ConsoleKey key, char c = '\0') => new(c, key, false, false, false);
    private static ConsoleKeyInfo[] Digitar(string texto) => [.. texto.Select(Tecla)];

    [Theory]
    [InlineData(null)]
    [InlineData("  ")]
    public async Task Cli_SemEmail_MostraUso_SemLerSenha(string? email)
    {
        string[] args = email is null ? [AdminPasswordResetCli.CommandName] : [AdminPasswordResetCli.CommandName, email];
        var terminal = TerminalDeTeste.Redirecionado();
        Assert.Equal(1, await AdminPasswordResetCli.ExecutarAsync(factory.Services, args, terminal.Cli));
        Assert.Contains($"Uso: dotnet run --project src/Almirante.Api -- {AdminPasswordResetCli.CommandName} <email>", terminal.Erro.ToString());
        Assert.Equal("", terminal.Saida.ToString());
    }

    [Fact]
    public async Task Cli_RunAsync_UsaOConsoleDoProcesso()
    {
        var erroOriginal = Console.Error;
        var erro = new StringWriter();
        Console.SetError(erro);
        try
        {
            Assert.Equal(1, await AdminPasswordResetCli.RunAsync(factory.Services, [AdminPasswordResetCli.CommandName]));
        }
        finally
        {
            Console.SetError(erroOriginal);
        }
        Assert.Contains("Uso: dotnet run", erro.ToString());
    }

    [Fact]
    public async Task Cli_ConfirmacaoDiferente_RecusaSemAlterarASenha()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "ConfirmacaoDiferente", "DS");
        var terminal = TerminalDeTeste.Redirecionado("Uma frase nova e bem forte 2026", "Outra frase nova e bem forte 2026");

        Assert.Equal(1, await AdminPasswordResetCli.ExecutarAsync(factory.Services, [AdminPasswordResetCli.CommandName, usuario.Email], terminal.Cli));

        Assert.Contains("As senhas informadas não coincidem.", terminal.Erro.ToString());
        Assert.Equal($"Nova senha: {Environment.NewLine}Confirme a nova senha: {Environment.NewLine}", terminal.Saida.ToString());
        Assert.Equal(usuario.SenhaHash, await TestHelpers.WithDbAsync(factory, db =>
            db.Usuarios.AsNoTracking().Where(u => u.Id == usuario.Id).Select(u => u.SenhaHash).SingleAsync()));
    }

    [Fact]
    public async Task Cli_UsuarioInexistente_E_SenhaForaDaPolitica_SaoRecusados()
    {
        var inexistente = TerminalDeTeste.Redirecionado("Uma frase nova e bem forte 2026", "Uma frase nova e bem forte 2026");
        Assert.Equal(1, await AdminPasswordResetCli.ExecutarAsync(factory.Services, [AdminPasswordResetCli.CommandName, "ninguem@local.dev"], inexistente.Cli));
        Assert.Contains("Nenhum usuário encontrado com esse e-mail.", inexistente.Erro.ToString());

        var usuario = await TestHelpers.AddUsuarioAsync(factory, "PoliticaNaCli", "DS");
        var fraca = TerminalDeTeste.Redirecionado("admin2026", "admin2026");
        Assert.Equal(1, await AdminPasswordResetCli.ExecutarAsync(factory.Services, [AdminPasswordResetCli.CommandName, usuario.Email], fraca.Cli));
        Assert.StartsWith("Senha não atende à política de senha: ", fraca.Erro.ToString());
        Assert.DoesNotContain("admin2026", fraca.Saida.ToString() + fraca.Erro.ToString());
    }

    [Fact]
    public async Task Cli_EntradaRedirecionadaVazia_EhSenhaVaziaRecusadaPelaPolitica()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "EntradaVazia", "DS");
        var terminal = TerminalDeTeste.Redirecionado();

        Assert.Equal(1, await AdminPasswordResetCli.ExecutarAsync(factory.Services, [AdminPasswordResetCli.CommandName, usuario.Email], terminal.Cli));
        Assert.StartsWith("Senha não atende à política de senha: ", terminal.Erro.ToString());
    }

    [Fact]
    public async Task Cli_PromptInterativo_NaoEcoaASenha_TrataBackspaceEIgnoraTeclasDeControle()
    {
        var usuario = await TestHelpers.AddUsuarioAsync(factory, "PromptInterativo", "DS");
        const string senha = "Uma frase nova e bem forte 2026";
        // Backspace com o buffer vazio não faz nada; "xy" é digitado e apagado; Tab (controle) é ignorado.
        ConsoleKeyInfo[] primeira = [Tecla(ConsoleKey.Backspace), Tecla('x'), Tecla('y'), Tecla(ConsoleKey.Backspace), Tecla(ConsoleKey.Backspace),
            Tecla(ConsoleKey.Tab, '\t'), .. Digitar(senha), Tecla(ConsoleKey.Enter, '\r')];
        var terminal = TerminalDeTeste.Interativo([.. primeira, .. Digitar(senha), Tecla(ConsoleKey.Enter, '\r')]);

        // E-mail com espaços e outra caixa: a busca é pelo e-mail normalizado.
        Assert.Equal(0, await AdminPasswordResetCli.ExecutarAsync(factory.Services,
            [AdminPasswordResetCli.CommandName, $"  {usuario.Email.ToUpperInvariant()} "], terminal.Cli));

        var saida = terminal.Saida.ToString();
        Assert.DoesNotContain(senha, saida);
        Assert.Contains("Nova senha: **\b \b\b \b" + new string('*', senha.Length) + Environment.NewLine, saida);
        Assert.Contains("Confirme a nova senha: " + new string('*', senha.Length) + Environment.NewLine, saida);
        Assert.Contains("Senha atualizada e sessões ativas dessa conta foram revogadas", saida);
        Assert.Equal("", terminal.Erro.ToString());

        var gravado = await TestHelpers.WithDbAsync(factory, db => db.Usuarios.AsNoTracking().SingleAsync(u => u.Id == usuario.Id));
        Assert.Equal(PasswordVerificationResult.Success,
            factory.Services.GetRequiredService<IPasswordHasher<Usuario>>().VerifyHashedPassword(gravado, gravado.SenhaHash, senha));
        Assert.Equal(usuario.SecurityVersion + 1, gravado.SecurityVersion);
    }

    /// <summary>Muda a SecurityVersion do usuário por fora antes de cada SaveChanges, como um login ou outro reset concorrente.</summary>
    private sealed class ConcorrenteNoSave(DbContextOptions<AlmiranteDbContext> opcoesSemInterceptor, Guid usuarioId, int conflitos)
        : Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override async ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int>> SavingChangesAsync(
            Microsoft.EntityFrameworkCore.Diagnostics.DbContextEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Saves++ < conflitos)
            {
                await using var outro = new AlmiranteDbContext(opcoesSemInterceptor);
                var usuario = await outro.Usuarios.SingleAsync(u => u.Id == usuarioId, cancellationToken);
                usuario.SecurityVersion += 10;
                await outro.SaveChangesAsync(cancellationToken);
            }
            return result;
        }
    }

    private static async Task<(DbContextOptions<AlmiranteDbContext> Opcoes, Usuario Usuario)> BancoIsoladoAsync()
    {
        var opcoes = new DbContextOptionsBuilder<AlmiranteDbContext>().UseInMemoryDatabase($"cli-{Guid.NewGuid():N}").Options;
        await using var db = new AlmiranteDbContext(opcoes);
        var cargo = new Cargo { Id = Guid.NewGuid(), Nome = "Administrador", Descricao = "Admin", CriadoPor = "Teste", Role = "ADM" };
        var usuario = new Usuario
        {
            Id = Guid.NewGuid(), Nome = "Admin", Email = "admin.cli@local.dev", EmailNormalizado = "ADMIN.CLI@LOCAL.DEV", SenhaHash = "hash-antigo",
            CargoId = cargo.Id, DataCriacao = DateTime.UtcNow, SecurityVersion = 3,
        };
        db.AddRange(cargo, usuario);
        await db.SaveChangesAsync();
        return (opcoes, usuario);
    }

    [Fact]
    public async Task ResetPasswordAsync_ConflitoDeConcorrencia_ReleEAplicaSobreAVersaoNova()
    {
        var (opcoes, usuario) = await BancoIsoladoAsync();
        var interceptor = new ConcorrenteNoSave(opcoes, usuario.Id, conflitos: 2);
        await using var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>(opcoes).AddInterceptors(interceptor).Options);
        var (_, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            var (resultado, _) = await AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "Uma frase nova e bem forte 2026");
            Assert.Equal(AdminPasswordResetCli.ResetResult.Sucesso, resultado);
        }

        Assert.Equal(3, interceptor.Saves);
        await using var leitura = new AlmiranteDbContext(opcoes);
        var gravado = await leitura.Usuarios.SingleAsync(u => u.Id == usuario.Id);
        Assert.Equal(3 + 10 + 10 + 1, gravado.SecurityVersion);
        Assert.NotEqual("hash-antigo", gravado.SenhaHash);
    }

    [Fact]
    public async Task ResetPasswordAsync_ConflitoPersistente_DesisteNaQuintaTentativa_SemGravarHash()
    {
        var (opcoes, usuario) = await BancoIsoladoAsync();
        var interceptor = new ConcorrenteNoSave(opcoes, usuario.Id, conflitos: int.MaxValue);
        await using var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>(opcoes).AddInterceptors(interceptor).Options);
        var (_, hasher, clock, scope) = ResolveDependencies();
        using (scope)
        {
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() =>
                AdminPasswordResetCli.ResetPasswordAsync(db, hasher, clock, usuario.Email, "Uma frase nova e bem forte 2026"));
        }

        Assert.Equal(5, interceptor.Saves);
        await using var leitura = new AlmiranteDbContext(opcoes);
        Assert.Equal("hash-antigo", (await leitura.Usuarios.SingleAsync(u => u.Id == usuario.Id)).SenhaHash);
    }
}
