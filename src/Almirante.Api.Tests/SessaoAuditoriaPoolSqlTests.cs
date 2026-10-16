using System.Data.Common;
using System.Net.Http.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Almirante.Api.Tests;

// Outros fixtures chamam ClearAllPools ao terminar. Isolar a coleção impede que esse descarte
// externo esconda uma regressão na ordem ClearPool/Close ou invalide o controle de reutilização.
[CollectionDefinition("SessaoAuditoriaPool", DisableParallelization = true)]
public sealed class SessaoAuditoriaPoolCollection;

[Collection("SessaoAuditoriaPool")]
[Trait("Category", "RequiresSqlServer")]
public sealed class SessaoAuditoriaPoolSqlTests
{
    [Theory]
    [InlineData("evento-delete", true)]
    [InlineData("evento-put", true)]
    [InlineData("lancamento-delete", true)]
    [InlineData("evento-delete", false)]
    [InlineData("evento-put", false)]
    [InlineData("lancamento-delete", false)]
    public async Task Limpeza_DescartaConexaoComFalhaAntesDeFechar_MasReutilizaConexaoSaudavel(string fluxo, bool falhar)
    {
        using var factory = new SqlServerApiFactory();
        var (client, usuarioId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        using var cliente = client;
        var membros = await EventosSqlSupport.MembrosAsync(factory, 2);
        var evento = await EventosSqlSupport.CriarAsync(client, membros);

        Guid lancamentoId;
        if (fluxo == "lancamento-delete")
        {
            using var response = await client.PostAsJsonAsync("/api/Lancamentos/Registrar", new
            {
                membroId = membros[0], finalidade = "Mensalidade", descricao = "pool",
                categoria = "Clube", tipoFluxo = "Entrada", valor = 15m,
                vencimento = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(3), aplicarATodosOsMembros = false,
            });
            response.EnsureSuccessStatusCode();
            lancamentoId = (await response.Content.ReadFromJsonAsync<LancamentoDto>())!.Id;
        }
        else
        {
            lancamentoId = evento.Lancamentos.Single(l => l.MembroId == membros[1]).Id;
        }

        // Pool exclusivo, com uma única conexão física possível. Sem atrasos nem probabilidade
        // de selecionar outra conexão ociosa quando sondamos a janela entre Close e ClearPool.
        var cs = new SqlConnectionStringBuilder(factory.ConnectionString)
        {
            ApplicationName = $"audit-pool-{Guid.NewGuid():N}", MaxPoolSize = 1, Pooling = true,
        }.ConnectionString;
        var limpeza = new FalhaLimpezaInterceptor(falhar);
        var fechamento = new SondaAoFecharInterceptor(limpeza, cs);
        await using var db = new AlmiranteDbContext(new DbContextOptionsBuilder<AlmiranteDbContext>()
            .UseSqlServer(cs).AddInterceptors(limpeza, fechamento).Options);
        var service = new EventosService(db, TimeProvider.System, NullLogger<EventosService>.Instance, new EventosLockOptions());
        const string motivo = "auditoria preservada apesar da falha de limpeza";
        const string ip = "2001:db8::42";

        if (fluxo == "evento-delete")
        {
            await service.DeleteAsync(new DeleteEventoRequest
            {
                Id = evento.Id, Versao = evento.Versao, Motivo = motivo,
                UsuarioResponsavelId = usuarioId, IpResponsavel = ip,
            }, CancellationToken.None);
        }
        else if (fluxo == "evento-put")
        {
            await service.UpdateAsync(new UpdateEventoRequest
            {
                Id = evento.Id, Versao = evento.Versao, Motivo = motivo,
                UsuarioResponsavelId = usuarioId, IpResponsavel = ip,
                DataEvento = evento.DataEvento, Local = evento.Local, Membros = [membros[0]],
                Transporte = new TransporteRequest { Valor = evento.Transporte.Valor, EhGratis = evento.Transporte.EhGratis },
                Alimentacao = new AlimentacaoRequest { Valor = evento.Alimentacao.Valor, Individual = evento.Alimentacao.Individual },
                SeguroObrigatorio = evento.SeguroObrigatorio,
            }, CancellationToken.None);
        }
        else
        {
            Assert.True(await new LancamentoExclusaoService(db, NullLogger<LancamentoExclusaoService>.Instance)
                .DeleteAsync(new DeleteLancamentoRequest
                {
                    Id = lancamentoId, Motivo = motivo, UsuarioResponsavelId = usuarioId, IpResponsavel = ip,
                }, CancellationToken.None));
        }

        Assert.Equal(1, limpeza.Tentativas);
        Assert.Equal(falhar, limpeza.FalhaSqlObservada);
        Assert.True(fechamento.Sondou);
        Assert.NotEqual(Guid.Empty, limpeza.ConexaoFisica);
        if (falhar)
            Assert.NotEqual(limpeza.ConexaoFisica, fechamento.ConexaoFisica);
        else
            Assert.Equal(limpeza.ConexaoFisica, fechamento.ConexaoFisica);
        Assert.Equal("|||", fechamento.Contexto);

        // O commit anterior à limpeza continua válido, com a identidade correta e sem duplicação.
        await TestHelpers.WithDbAsync(factory, async verificacao =>
        {
            Assert.False((await verificacao.Lancamentos.SingleAsync(l => l.Id == lancamentoId)).Ativo);
            var historico = Assert.Single(await verificacao.LancamentosDeletados
                .Where(l => l.LancamentoId == lancamentoId).ToListAsync());
            Assert.Equal(usuarioId, historico.UsuarioResponsavelId);
            Assert.Equal(ip, historico.IpResponsavel);
            Assert.Equal(motivo, historico.Motivo);
            Assert.Equal(fluxo == "evento-delete" ? 1 : 0,
                await verificacao.HistoricoEventos.CountAsync(h => h.EventoId == evento.Id));
            return true;
        });
    }

    private sealed class FalhaLimpezaInterceptor(bool falhar) : DbCommandInterceptor
    {
        public DbConnection? Conexao { get; private set; }
        public Guid ConexaoFisica { get; private set; }
        public int Tentativas { get; private set; }
        public bool FalhaSqlObservada { get; private set; }

        private static bool Limpeza(string sql) => sql.Contains("sp_set_session_context") && sql.Contains("@value=NULL");

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Limpeza(command.CommandText)) return result;
            Tentativas++;
            Conexao = command.Connection!;
            ConexaoFisica = ((SqlConnection)Conexao).ClientConnectionId;
            if (falhar)
            {
                // Falha REAL do SQL Server: a última chave não poderá ser apagada. As duas
                // primeiras já terão sido limpas quando o batch falhar (limpeza parcial).
                await using var bloquear = Conexao.CreateCommand();
                bloquear.CommandText = """
                    DECLARE @motivo sql_variant = SESSION_CONTEXT(N'MotivoExclusao');
                    EXEC sys.sp_set_session_context @key=N'MotivoExclusao', @value=@motivo, @read_only=1;
                    """;
                await bloquear.ExecuteNonQueryAsync(cancellationToken);
            }
            return result;
        }

        public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (Limpeza(command.CommandText)) FalhaSqlObservada = eventData.Exception is SqlException;
            return Task.CompletedTask;
        }
    }

    private sealed class SondaAoFecharInterceptor(FalhaLimpezaInterceptor limpeza, string cs) : DbConnectionInterceptor
    {
        public bool Sondou { get; private set; }
        public Guid ConexaoFisica { get; private set; }
        public string? Contexto { get; private set; }

        public override async Task ConnectionClosedAsync(DbConnection connection, ConnectionEndEventData eventData)
        {
            if (Sondou || !ReferenceEquals(connection, limpeza.Conexao)) return;
            Sondou = true;
            // Executa ANTES de CloseConnectionAsync retornar ao serviço: o código antigo
            // ainda não chamou ClearPool e obrigatoriamente reutiliza a conexão contaminada.
            await using var sonda = new SqlConnection(cs);
            await sonda.OpenAsync();
            ConexaoFisica = sonda.ClientConnectionId;
            await using var comando = sonda.CreateCommand();
            comando.CommandText = """
                SELECT CONCAT(CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS nvarchar(60)), '|',
                              CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS nvarchar(60)), '|',
                              CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(255)), '|');
                """;
            Contexto = (string)(await comando.ExecuteScalarAsync())!;
        }
    }
}
