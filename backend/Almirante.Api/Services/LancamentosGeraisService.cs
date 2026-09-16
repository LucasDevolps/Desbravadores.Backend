using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public enum LancamentoGeralCreateOutcome
{
    Criado,
    Reutilizado,
    ConflitoIdempotencia,
}

public record LancamentoGeralCreateResult(LancamentoGeralCreateOutcome Outcome, LancamentoGeralResponse? Response);

public enum LancamentoGeralDeleteOutcome
{
    Excluido,
    NaoEncontrado,
}

public class LancamentosGeraisService(AlmiranteDbContext db, ILogger<LancamentosGeraisService> logger)
{
    private const string DateFormat = "yyyy-MM-dd";

    // Resumo financeiro provisório: valores fixos exigidos pelo requisito atual, independentes
    // do período filtrado ou da existência de lançamentos. Substituir por um cálculo real (soma
    // de Valor dos lançamentos ativos, por ex. status "Pago" para entradas) quando a regra de
    // negócio for definida.
    private static readonly ResumoFinanceiroDto ResumoFixoProvisorio = new()
    {
        TotalDespesas = 800.00m,
        TotalEntradas = 1000.00m,
        SaldoAtual = 200.00m,
    };

    public async Task<LancamentoGeralCreateResult> CreateAsync(
        string idempotencyKey,
        CreateLancamentoGeralRequest request,
        Guid usuarioSolicitanteId,
        CancellationToken cancellationToken)
    {
        var vencimento = DateOnly.ParseExact(request.Vencimento, DateFormat, CultureInfo.InvariantCulture);

        var requestHash = ComputeRequestHash(request.Tipo, request.Categoria, request.TipoFluxo, request.Valor, vencimento);

        // Fast-path: se a chave já foi usada por uma operação concluída anteriormente (caso comum
        // de reenvio, não concorrente), resolve sem tentar inserir de novo.
        var existentePreCheck = await db.LancamentosOperacoes
            .AsNoTracking()
            .SingleOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existentePreCheck is not null)
        {
            return ResultadoParaExistente(existentePreCheck, requestHash);
        }

        var operacaoId = Guid.NewGuid();
        var agora = DateTime.UtcNow;

        // Usuários elegíveis: o domínio não define, em nenhum outro lugar do sistema, uma regra
        // de elegibilidade para o lançamento geral (Usuario não tem flag de status/ativo, nem
        // distinção por cargo usada para esse fim em nenhum serviço existente). Na ausência dessa
        // regra, consideramos elegível TODO usuário cadastrado. Documentado também no README —
        // caso exista uma regra diferente (ex.: restringir ao cargo "DS"), ajustar esta consulta.
        var usuarios = await db.Usuarios
            .Select(u => new { u.Id, u.Nome })
            .ToListAsync(cancellationToken);

        var lancamentos = usuarios.Select(u => new Lancamento
        {
            Id = Guid.NewGuid(),
            MembroId = u.Id,
            MembroNome = u.Nome,
            Tipo = request.Tipo,
            Categoria = request.Categoria,
            TipoFluxo = request.TipoFluxo,
            Valor = request.Valor,
            Moeda = "BRL",
            Vencimento = vencimento,
            Status = LancamentoStatuses.Pendente,
            DataCriacao = agora,
            Ativo = true,
            OperacaoId = operacaoId,
        }).ToList();

        var operacao = new LancamentoOperacao
        {
            Id = operacaoId,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            Tipo = request.Tipo,
            Categoria = request.Categoria,
            TipoFluxo = request.TipoFluxo,
            Valor = request.Valor,
            Vencimento = vencimento,
            UsuariosProcessados = usuarios.Count,
            LancamentosCriados = lancamentos.Count,
            CriadoPorUsuarioId = usuarioSolicitanteId,
            CriadoEmUtc = agora,
        };

        db.LancamentosOperacoes.Add(operacao);
        db.Lancamentos.AddRange(lancamentos);

        try
        {
            // Uma única SaveChangesAsync: sobre um provider relacional, o EF Core grava todas as
            // alterações pendentes (a operação + todos os lançamentos) em uma única transação
            // implícita — ou tudo é persistido, ou nada é (sem cadastros parciais em caso de
            // erro). O índice único em LancamentosOperacoes.IdempotencyKey é a rede de segurança
            // real contra a corrida entre duas requisições concorrentes com a mesma chave que
            // passaram pelo fast-path acima antes de qualquer uma delas gravar: no SQL Server, a
            // segunda a chegar ao SaveChanges é bloqueada pelo índice até a primeira confirmar, e
            // então falha aqui em vez de duplicar a operação — tratado no catch abaixo.
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();

            var existente = await db.LancamentosOperacoes
                .AsNoTracking()
                .SingleOrDefaultAsync(o => o.IdempotencyKey == idempotencyKey, cancellationToken);

            if (existente is null)
            {
                // Falha não relacionada à chave de idempotência (ex.: violação de FK) — não
                // mascarar o erro original.
                throw;
            }

            return ResultadoParaExistente(existente, requestHash);
        }

        logger.LogInformation(
            "Lançamento geral criado: operação {OperacaoId}, solicitante {UsuarioSolicitanteId}, tipo {Tipo}, categoria {Categoria}, valor {Valor}, vencimento {Vencimento}, usuários processados {UsuariosProcessados}, lançamentos criados {LancamentosCriados}, resultado Sucesso.",
            operacaoId, usuarioSolicitanteId, request.Tipo, request.Categoria, request.Valor, vencimento,
            operacao.UsuariosProcessados, operacao.LancamentosCriados);

        return new LancamentoGeralCreateResult(LancamentoGeralCreateOutcome.Criado, ToResponse(operacao));
    }

    public async Task<LancamentosGeralListResponse> ListAsync(
        string? periodo,
        string? dataInicio,
        string? dataFim,
        CancellationToken cancellationToken)
    {
        var (inicio, fim) = ResolvePeriodo(periodo, dataInicio, dataFim);

        var items = await db.Lancamentos
            .Where(l => l.Ativo && l.OperacaoId != null)
            .Where(l => l.Vencimento >= inicio && l.Vencimento <= fim)
            .OrderByDescending(l => l.Vencimento)
            .ThenByDescending(l => l.DataCriacao)
            .ToListAsync(cancellationToken);

        return new LancamentosGeralListResponse
        {
            Lancamentos = items.Select(ToDto).ToList(),
            Resumo = ResumoFixoProvisorio,
        };
    }

    public async Task<LancamentoGeralDeleteOutcome> DeleteAsync(
        Guid id,
        string motivo,
        Guid usuarioResponsavelId,
        string ipResponsavel,
        CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
        {
            // Provider não relacional (testes com EF Core InMemory, ver AlmiranteApiFactory): não
            // existem trigger nem SESSION_CONTEXT nesse provider, então a exclusão lógica é feita
            // diretamente pela aplicação, sem gerar linha de auditoria. Mesma convenção de
            // branch por IsRelational() já usada em DbSeeder.SeedAsync. A auditoria via trigger em
            // si só é coberta pelos testes contra SQL Server real (ver
            // LancamentosGeraisAuditoriaSqlServerTests).
            var lancamentoEmMemoria = await db.Lancamentos
                .SingleOrDefaultAsync(l => l.Id == id && l.Ativo && l.OperacaoId != null, cancellationToken);

            if (lancamentoEmMemoria is null)
            {
                return LancamentoGeralDeleteOutcome.NaoEncontrado;
            }

            lancamentoEmMemoria.Ativo = false;
            await db.SaveChangesAsync(cancellationToken);
            return LancamentoGeralDeleteOutcome.Excluido;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // SESSION_CONTEXT alimentado na mesma conexão física e transação do UPDATE logo
            // abaixo, para que o trigger TR_Lancamentos_AuditoriaExclusaoLogica (migration
            // AddLancamentoGeral) audite com o usuário/IP/motivo desta requisição.
            // @read_only = 0 é o padrão do sp_set_session_context, o que permite sobrescrever o
            // valor em requisições futuras que reutilizem a mesma conexão física do pool.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'UsuarioResponsavelId', @value = {usuarioResponsavelId};",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'IpResponsavelExclusao', @value = {ipResponsavel};",
                cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"EXEC sys.sp_set_session_context @key = N'MotivoExclusao', @value = {motivo};",
                cancellationToken);

            // UPDATE bruto (não via SaveChanges): dispensa carregar a entidade e nunca gera
            // cláusula OUTPUT (já desligada para esta tabela em AlmiranteDbContext, mas o SQL
            // bruto nem chega a gerar uma). Escopado a Ativo = 1 AND OperacaoId IS NOT NULL: só
            // afeta o lançamento pedido dentro do escopo do lançamento geral — nunca outro item
            // do lote, nunca um lançamento do CRUD genérico — e 0 linhas afetadas cobre tanto "não
            // existe" quanto "já está inativo" (evitando reauditar uma exclusão repetida), tratado
            // abaixo como 404.
            var linhasAfetadas = await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Lancamentos SET Ativo = 0 WHERE Id = {id} AND Ativo = 1 AND OperacaoId IS NOT NULL;",
                cancellationToken);

            if (linhasAfetadas == 0)
            {
                await transaction.RollbackAsync(cancellationToken);
                return LancamentoGeralDeleteOutcome.NaoEncontrado;
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Lançamento geral excluído (lógico): lançamento {LancamentoId}, responsável {UsuarioResponsavelId}, resultado Sucesso.",
                id, usuarioResponsavelId);

            return LancamentoGeralDeleteOutcome.Excluido;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
        finally
        {
            // Limpeza sempre executada, mesmo em falha: evita vazar o usuário/IP/motivo desta
            // requisição para a próxima que reutilizar a mesma conexão física do pool. Melhor
            // esforço: uma falha aqui nunca deve mascarar o resultado (ou a exceção) real.
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"EXEC sys.sp_set_session_context @key = N'UsuarioResponsavelId', @value = NULL; EXEC sys.sp_set_session_context @key = N'IpResponsavelExclusao', @value = NULL; EXEC sys.sp_set_session_context @key = N'MotivoExclusao', @value = NULL;",
                    CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Falha ao limpar SESSION_CONTEXT após exclusão lógica de lançamento geral.");
            }
        }
    }

    private static (DateOnly Inicio, DateOnly Fim) ResolvePeriodo(string? periodo, string? dataInicio, string? dataFim)
    {
        // Data de referência do filtro: Vencimento. O domínio não define, em nenhum outro lugar,
        // qual data deve orientar esta listagem (não há um campo "data do evento" separado do
        // vencimento) — Vencimento foi escolhido por representar quando a cobrança é devida, que é
        // o sentido mais próximo de "lançamentos dos últimos N dias" para um extrato financeiro.
        // Vencimento é DateOnly (sem hora nem fuso): a comparação é feita dia a dia contra a data
        // UTC atual do servidor, então o dia inicial e o dia final do intervalo entram por
        // completo, sem qualquer perda por causa de horário — não há componente de hora para
        // ambiguidade nas bordas.
        var hoje = DateOnly.FromDateTime(DateTime.UtcNow);

        if (string.IsNullOrWhiteSpace(periodo) || periodo == "30")
        {
            return (hoje.AddDays(-29), hoje);
        }

        if (periodo == "60")
        {
            return (hoje.AddDays(-59), hoje);
        }

        if (periodo == "90")
        {
            return (hoje.AddDays(-89), hoje);
        }

        // periodo/dataInicio/dataFim já foram validados por ListLancamentosGeraisQueryValidator
        // antes do MediatR chegar a este handler/serviço (período pertence a {30, 60, 90,
        // personalizado}, e no caso "personalizado" as datas são obrigatórias, bem formadas e
        // dataInicio <= dataFim) — daqui para baixo os valores são tratados como confiáveis.
        if (periodo == "personalizado")
        {
            var inicio = DateOnly.ParseExact(dataInicio!, DateFormat, CultureInfo.InvariantCulture);
            var fim = DateOnly.ParseExact(dataFim!, DateFormat, CultureInfo.InvariantCulture);
            return (inicio, fim);
        }

        throw new InvalidOperationException($"Período inesperado: {periodo}. Deveria ter sido rejeitado na validação.");
    }

    private LancamentoGeralCreateResult ResultadoParaExistente(LancamentoOperacao existente, string requestHash)
    {
        if (existente.RequestHash != requestHash)
        {
            logger.LogWarning(
                "Lançamento geral: reuso da chave de idempotência {IdempotencyKey} com dados diferentes da operação original {OperacaoId}.",
                existente.IdempotencyKey, existente.Id);
            return new LancamentoGeralCreateResult(LancamentoGeralCreateOutcome.ConflitoIdempotencia, null);
        }

        logger.LogInformation(
            "Lançamento geral: reenvio idempotente da chave {IdempotencyKey}; operação {OperacaoId} reaproveitada sem novos lançamentos.",
            existente.IdempotencyKey, existente.Id);
        return new LancamentoGeralCreateResult(LancamentoGeralCreateOutcome.Reutilizado, ToResponse(existente));
    }

    private static string ComputeRequestHash(string tipo, string categoria, TipoFluxoLancamento tipoFluxo, decimal valor, DateOnly vencimento)
    {
        var canonical = string.Join(
            '|',
            tipo,
            categoria,
            tipoFluxo.ToString(),
            valor.ToString("F2", CultureInfo.InvariantCulture),
            vencimento.ToString(DateFormat, CultureInfo.InvariantCulture));

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes);
    }

    private static LancamentoGeralResponse ToResponse(LancamentoOperacao operacao) => new()
    {
        OperacaoId = operacao.Id,
        UsuariosProcessados = operacao.UsuariosProcessados,
        LancamentosCriados = operacao.LancamentosCriados,
        DataHoraUtc = operacao.CriadoEmUtc,
    };

    private static LancamentoDto ToDto(Lancamento lancamento) => new()
    {
        Id = lancamento.Id,
        MembroId = lancamento.MembroId,
        MembroNome = lancamento.MembroNome,
        Tipo = lancamento.Tipo,
        Descricao = lancamento.Descricao,
        Categoria = lancamento.Categoria,
        Valor = lancamento.Valor,
        Moeda = lancamento.Moeda,
        Vencimento = lancamento.Vencimento.ToString(DateFormat, CultureInfo.InvariantCulture),
        Status = lancamento.Status,
        TipoFluxo = lancamento.TipoFluxo,
    };
}
