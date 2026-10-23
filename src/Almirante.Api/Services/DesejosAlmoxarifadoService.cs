using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Lista de desejos do almoxarifado (issue #126): necessidades de aquisição com urgência, atendimentos parciais ou totais (compra ou doação)
/// e o vínculo explícito dos bens patrimoniais recebidos. Cadastrar ou atender um desejo não gera despesa, pagamento, reserva nem lançamento.
/// </summary>
public sealed class DesejosAlmoxarifadoService(AlmiranteDbContext db, TimeProvider clock, DataCivil dataCivil, ILogger<DesejosAlmoxarifadoService> logger)
{
    private const string TabelaDesejos = "almoxarifado_desejos";

    public async Task<DesejoDto> CadastrarAsync(CadastrarDesejoRequest r, CancellationToken ct)
    {
        r.Id = Guid.NewGuid();
        await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.CadastroDesejo, null, async agora =>
        {
            var desejo = new DesejoAlmoxarifado
            {
                Id = r.Id, Nome = string.Empty, Descricao = string.Empty, Justificativa = string.Empty, CriadoPorUsuarioId = r.UsuarioId, CriadoEmUtc = agora,
            };
            Aplicar(desejo, r);
            db.DesejosAlmoxarifado.Add(desejo);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return (await ObterAsync(r.Id, ct))!;
    }

    public Task<DesejoDto> AtualizarAsync(AtualizarDesejoRequest r, CancellationToken ct) =>
        AlterarAsync(r, TipoMovimentacaoAlmoxarifado.AlteracaoDesejo, null, (desejo, agora) =>
        {
            FormacaoBanco.ConferirVersao(desejo.Versao, r.Versao);
            RegrasAlmoxarifado.ConferirAtualizacaoDesejo(desejo, r.QuantidadeDesejada!.Value, r.TipoItem!.Value);
            Aplicar(desejo, r);
            Tocar(desejo, r.UsuarioId, agora);
            return Task.CompletedTask;
        }, ct);

    /// <summary>Cancelamento (motivo) ou exclusão lógica (justificativa). Os atendimentos anteriores e os bens vinculados são preservados.</summary>
    public Task<DesejoDto> EncerrarAsync(EncerrarDesejoRequest r, CancellationToken ct)
    {
        var justificativa = r.Justificativa!.Trim();
        var acao = r.Excluir ? TipoMovimentacaoAlmoxarifado.ExclusaoDesejo : TipoMovimentacaoAlmoxarifado.CancelamentoDesejo;
        return AlterarAsync(r, acao, justificativa, (desejo, agora) =>
        {
            FormacaoBanco.ConferirVersao(desejo.Versao, r.Versao);
            if (r.Excluir)
            {
                RegrasAlmoxarifado.ConferirExclusaoDesejo(desejo);
                desejo.Excluido = true;
                desejo.MotivoExclusao = justificativa;
                desejo.ExcluidoPorUsuarioId = r.UsuarioId;
                desejo.ExcluidoEmUtc = agora;
            }
            else
            {
                RegrasAlmoxarifado.ConferirCancelamento(desejo);
                desejo.Situacao = SituacaoDesejo.Cancelada;
                desejo.MotivoCancelamento = justificativa;
                desejo.CanceladoPorUsuarioId = r.UsuarioId;
                desejo.CanceladoEmUtc = agora;
            }

            Tocar(desejo, r.UsuarioId, agora);
            return Task.CompletedTask;
        }, ct);
    }

    /// <summary>
    /// Registra um recebimento, de forma transacional e idempotente (usuário + Idempotency-Key): o reenvio com os mesmos dados devolve o
    /// atendimento original (Criado = false) sem duplicar recebimentos nem bens; a mesma chave com dados diferentes recebe 409.
    /// </summary>
    public async Task<RegistrarAtendimentoResultado> RegistrarAtendimentoAsync(RegistrarAtendimentoRequest r, CancellationToken ct)
    {
        var chave = EventoRegras.NormalizarIdempotencyKey(r.IdempotencyKey)!;
        var hash = Hash(r);
        if (await LerOperacaoAsync(r.UsuarioId, chave, ct) is { } existente)
        {
            return await ReplayAsync(existente, hash, ct);
        }

        var novos = r.BensNovos ?? [];
        var existentes = r.BensExistentes ?? [];
        var quantidade = r.Quantidade!.Value;
        var data = r.Data!.Value;
        var atendimentoId = Guid.NewGuid();
        var simultaneo = ApiProblemException.Conflict("Conflito de operação simultânea.",
            "Outra operação vinculou os mesmos bens ou usou a mesma Idempotency-Key; consulte a necessidade novamente.");
        bool criado;
        try
        {
            criado = await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.Atendimento, null, async agora =>
            {
                var desejo = await DesejoTravadoAsync(r.Id, ct);

                // Reenvio simultâneo com a mesma chave: com a necessidade travada, o primeiro já gravou; responde como reenvio.
                if (await LerOperacaoAsync(r.UsuarioId, chave, ct) is not null)
                {
                    return false;
                }

                RegrasAlmoxarifado.ConferirAtendimento(desejo, quantidade, data, novos.Count + existentes.Count, dataCivil.Hoje());
                await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "responsavelId", r.ResponsavelId, ct);
                foreach (var guarda in novos.Select(n => n.ResponsavelGuardaId).Distinct())
                {
                    await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "responsavelGuardaId", guarda, ct);
                }

                await ConferirBensExistentesAsync(existentes, data, ct);
                db.AtendimentosDesejos.Add(new AtendimentoDesejo
                {
                    Id = atendimentoId, DesejoId = desejo.Id, Quantidade = quantidade, Data = data, ResponsavelId = r.ResponsavelId!.Value,
                    Origem = r.Origem!.Value, Observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes), IdempotencyKey = chave, RequestHash = hash,
                    RegistradoPorUsuarioId = r.UsuarioId, RegistradoEmUtc = agora,
                });
                foreach (var novo in novos)
                {
                    var bem = PatrimonioService.NovoBem(novo, r.UsuarioId, agora);
                    bem.DataAquisicao = data;
                    bem.Origem = r.Origem == OrigemAtendimento.Doacao ? OrigemBem.Doacao : OrigemBem.Compra;
                    db.BensPatrimoniais.Add(bem);
                    db.AtendimentosBens.Add(new AtendimentoBem { Id = Guid.NewGuid(), AtendimentoId = atendimentoId, BemId = bem.Id, CriadoPeloAtendimento = true });
                }

                db.AtendimentosBens.AddRange(existentes.Select(id => new AtendimentoBem { Id = Guid.NewGuid(), AtendimentoId = atendimentoId, BemId = id }));
                desejo.QuantidadeAtendida += quantidade;
                desejo.Situacao = RegrasAlmoxarifado.SituacaoDesejo(desejo.QuantidadeDesejada, desejo.QuantidadeAtendida, cancelada: false);
                Tocar(desejo, r.UsuarioId, agora);
                await db.SaveChangesAsync(ct);
                return true;
            }, ct, simultaneo);
        }
        catch (ApiProblemException ex) when (ReferenceEquals(ex, simultaneo))
        {
            db.ChangeTracker.Clear();
            if (await LerOperacaoAsync(r.UsuarioId, chave, ct) is { } concorrente)
            {
                return await ReplayAsync(concorrente, hash, ct);
            }

            throw;
        }

        if (!criado)
        {
            return await ReplayAsync((await LerOperacaoAsync(r.UsuarioId, chave, ct))!, hash, ct);
        }

        logger.LogInformation("Atendimento {AtendimentoId} da necessidade {DesejoId}: {Quantidade} unidade(s), {Bens} bem(ns), por {UsuarioId}.",
            atendimentoId, r.Id, quantidade, novos.Count + existentes.Count, r.UsuarioId);
        var dto = (await ObterAsync(r.Id, ct))!;
        return new RegistrarAtendimentoResultado(dto, dto.Atendimentos.Single(a => a.Id == atendimentoId), true);
    }

    /// <summary>
    /// Corrige um atendimento indevido: marca-o como estornado, desfaz os vínculos e exclui logicamente os bens que ele criou (que precisam
    /// estar sem movimentações); os bens existentes vinculados voltam a ficar livres. A quantidade atendida é recalculada.
    /// </summary>
    public Task<DesejoDto> EstornarAtendimentoAsync(EstornarAtendimentoRequest r, CancellationToken ct)
    {
        var justificativa = r.Justificativa!.Trim();
        return AlterarAsync(r, TipoMovimentacaoAlmoxarifado.EstornoAtendimento, justificativa, async (desejo, agora) =>
        {
            var atendimento = await db.AtendimentosDesejos.SingleOrDefaultAsync(a => a.Id == r.AtendimentoId && a.DesejoId == desejo.Id, ct)
                ?? throw ApiProblemException.NotFound("Atendimento não encontrado nesta necessidade.");
            var vinculos = await db.AtendimentosBens.Where(v => v.AtendimentoId == atendimento.Id && v.Ativo).ToListAsync(ct);
            var criados = vinculos.Where(v => v.CriadoPeloAtendimento).Select(v => v.BemId).ToList();
            foreach (var id in criados)
            {
                await AlmoxarifadoBanco.BloquearAsync(db, "patrimonio_bens", id, ct);
            }

            var bensCriados = await db.BensPatrimoniais.Where(b => criados.Contains(b.Id)).ToListAsync(ct);
            var movimentados = await db.EmprestimosBens.AnyAsync(e => criados.Contains(e.BemId), ct)
                || await db.ManutencoesBens.AnyAsync(m => criados.Contains(m.BemId), ct);
            RegrasAlmoxarifado.ConferirEstorno(atendimento, bensCriados, movimentados);

            atendimento.MotivoEstorno = justificativa;
            atendimento.EstornadoPorUsuarioId = r.UsuarioId;
            atendimento.EstornadoEmUtc = agora;
            foreach (var vinculo in vinculos)
            {
                vinculo.Ativo = false;
                vinculo.DesvinculadoPorUsuarioId = r.UsuarioId;
                vinculo.DesvinculadoEmUtc = agora;
            }

            foreach (var bem in bensCriados)
            {
                bem.Excluido = true;
                bem.MotivoExclusao = justificativa;
                bem.ExcluidoPorUsuarioId = r.UsuarioId;
                bem.ExcluidoEmUtc = agora;
                PatrimonioService.Tocar(bem, r.UsuarioId, agora);
            }

            desejo.QuantidadeAtendida -= atendimento.Quantidade;
            desejo.Situacao = RegrasAlmoxarifado.SituacaoDesejo(desejo.QuantidadeDesejada, desejo.QuantidadeAtendida, desejo.CanceladoEmUtc is not null);
            Tocar(desejo, r.UsuarioId, agora);
        }, ct);
    }

    public async Task<DesejoDto?> ObterAsync(Guid id, CancellationToken ct)
    {
        var desejo = await db.DesejosAlmoxarifado.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (desejo is null)
        {
            return null;
        }

        var atendimentos = await db.AtendimentosDesejos.AsNoTracking().Where(a => a.DesejoId == id)
            .OrderBy(a => a.RegistradoEmUtc).ThenBy(a => a.Id).ToListAsync(ct);
        var ids = atendimentos.Select(a => a.Id).ToList();
        var bens = await (
            from v in db.AtendimentosBens.AsNoTracking()
            join b in db.BensPatrimoniais.AsNoTracking() on v.BemId equals b.Id
            where ids.Contains(v.AtendimentoId)
            orderby b.NumeroTombo
            select new { v.AtendimentoId, Bem = new BemAtendimentoDto(b.Id, b.Tombo, b.Nome, v.CriadoPeloAtendimento, v.Ativo) }).ToListAsync(ct);
        var porAtendimento = bens.ToLookup(b => b.AtendimentoId, b => b.Bem);
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db,
            atendimentos.SelectMany(a => new[] { a.ResponsavelId, a.RegistradoPorUsuarioId, a.EstornadoPorUsuarioId }), ct);
        var dtos = atendimentos.Select(a => new AtendimentoDto
        {
            Id = a.Id, DesejoId = a.DesejoId, Quantidade = a.Quantidade, Data = a.Data,
            Responsavel = pessoas.GetValueOrDefault(a.ResponsavelId) ?? new PessoaResumoDto(a.ResponsavelId, string.Empty),
            Origem = a.Origem, OrigemDescricao = RegrasAlmoxarifado.Descricao(a.Origem), Observacoes = a.Observacoes, Bens = [.. porAtendimento[a.Id]],
            Registro = AlmoxarifadoBanco.Registro(pessoas, a.RegistradoPorUsuarioId, a.RegistradoEmUtc), Estornado = a.Estornado, MotivoEstorno = a.MotivoEstorno,
            Estorno = AlmoxarifadoBanco.Registro(pessoas, a.EstornadoPorUsuarioId, a.EstornadoEmUtc),
        }).ToList();
        return (await MapearAsync([desejo], ct))[0] with { Atendimentos = dtos };
    }

    /// <summary>Ordem padrão: abertas (pendente/parcial) primeiro, maior urgência e, no empate, a criação mais antiga.</summary>
    public async Task<PaginaDto<DesejoDto>> ListarAsync(ListarDesejosQuery f, CancellationToken ct)
    {
        var consulta = db.DesejosAlmoxarifado.AsNoTracking();
        if (!f.IncluirExcluidos)
        {
            consulta = consulta.Where(d => !d.Excluido);
        }

        if (AlmoxarifadoBanco.Opcional(f.Busca) is { } busca)
        {
            consulta = consulta.Where(d => d.Nome.Contains(busca));
        }

        if (f.Urgencia is { } urgencia)
        {
            consulta = consulta.Where(d => d.Urgencia == urgencia);
        }

        if (f.Situacao is { } situacao)
        {
            consulta = consulta.Where(d => d.Situacao == situacao);
        }

        if (AlmoxarifadoBanco.Opcional(f.Categoria) is { } categoria)
        {
            consulta = consulta.Where(d => d.Categoria == categoria);
        }

        if (f.TipoItem is { } tipo)
        {
            consulta = consulta.Where(d => d.TipoItem == tipo);
        }

        if (f.SomenteAbertas)
        {
            consulta = consulta.Where(d => d.Situacao == SituacaoDesejo.Pendente || d.Situacao == SituacaoDesejo.ParcialmenteAtendida);
        }

        var total = await consulta.CountAsync(ct);
        var pagina = await consulta
            .OrderBy(d => d.Situacao == SituacaoDesejo.Pendente || d.Situacao == SituacaoDesejo.ParcialmenteAtendida ? 0 : 1)
            .ThenByDescending(d => d.Urgencia).ThenBy(d => d.CriadoEmUtc).ThenBy(d => d.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct);
        return FormacaoMapping.Pagina(await MapearAsync(pagina, ct), total, f.Page, f.PageSize);
    }

    /// <summary>Necessidades abertas por urgência (da maior para a menor): quantidade pendente e custo pendente quando estimado.</summary>
    public async Task<IReadOnlyList<DesejosPorUrgenciaDto>> RelatorioAsync(CancellationToken ct)
    {
        var abertas = await db.DesejosAlmoxarifado.AsNoTracking()
            .Where(d => !d.Excluido && (d.Situacao == SituacaoDesejo.Pendente || d.Situacao == SituacaoDesejo.ParcialmenteAtendida))
            .Select(d => new { d.Urgencia, d.QuantidadeDesejada, d.QuantidadeAtendida, d.CustoUnitarioEstimado }).ToListAsync(ct);
        return Enum.GetValues<NivelUrgenciaAlmoxarifado>().OrderDescending().Select(u =>
        {
            var grupo = abertas.Where(d => d.Urgencia == u).ToList();
            return new DesejosPorUrgenciaDto(u, RegrasAlmoxarifado.Descricao(u), grupo.Count,
                grupo.Sum(d => RegrasAlmoxarifado.Pendente(d.QuantidadeDesejada, d.QuantidadeAtendida)),
                grupo.Sum(d => RegrasAlmoxarifado.Custo(d.CustoUnitarioEstimado, RegrasAlmoxarifado.Pendente(d.QuantidadeDesejada, d.QuantidadeAtendida)) ?? 0m),
                grupo.Count(d => d.CustoUnitarioEstimado is null));
        }).ToList();
    }

    public static string Hash(RegistrarAtendimentoRequest r)
    {
        var canonico = JsonSerializer.Serialize(new
        {
            v = "atendimento.v1", desejoId = r.Id, r.Quantidade, r.Data, r.ResponsavelId, r.Origem, observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes),
            bensExistentes = (r.BensExistentes ?? []).Order().ToList(), bensNovos = r.BensNovos ?? [],
        }, JsonSerializerOptions.Web);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonico)));
    }

    private async Task<RegistrarAtendimentoResultado> ReplayAsync(AtendimentoDesejo operacao, string hash, CancellationToken ct)
    {
        if (!string.Equals(operacao.RequestHash, hash, StringComparison.Ordinal))
        {
            throw ApiProblemException.Conflict("Chave de idempotência já usada com dados diferentes.",
                "O header Idempotency-Key informado já registrou outro atendimento; use uma nova chave para um novo recebimento.");
        }

        var dto = (await ObterAsync(operacao.DesejoId, ct))!;
        return new RegistrarAtendimentoResultado(dto, dto.Atendimentos.Single(a => a.Id == operacao.Id), false);
    }

    private Task<AtendimentoDesejo?> LerOperacaoAsync(Guid usuarioId, string chave, CancellationToken ct) =>
        db.AtendimentosDesejos.AsNoTracking().SingleOrDefaultAsync(a => a.RegistradoPorUsuarioId == usuarioId && a.IdempotencyKey == chave, ct);

    /// <summary>Bens já cadastrados recebidos no atendimento: existentes, ativos, não baixados, adquiridos até a data e sem outro vínculo ativo.</summary>
    private async Task ConferirBensExistentesAsync(IReadOnlyList<Guid> ids, DateOnly data, CancellationToken ct)
    {
        foreach (var id in ids)
        {
            await AlmoxarifadoBanco.BloquearAsync(db, "patrimonio_bens", id, ct);
        }

        var bens = await db.BensPatrimoniais.AsNoTracking().Where(b => ids.Contains(b.Id)).ToListAsync(ct);
        if (ids.Except(bens.Select(b => b.Id)).FirstOrDefault() is var faltante && faltante != Guid.Empty)
        {
            throw ApiProblemException.NotFound("Bem não encontrado.", $"bensExistentes contém um bem inexistente ({faltante}).");
        }

        if (bens.FirstOrDefault(b => b.Excluido || b.Situacao == SituacaoBem.Baixado) is { } inativo)
        {
            throw ApiProblemException.Conflict("Bem excluído ou baixado.", $"O bem {inativo.Tombo} não pode ser vinculado a um atendimento.");
        }

        if (bens.FirstOrDefault(b => b.DataAquisicao > data) is { } posterior)
        {
            throw ApiProblemException.Invalid("bensExistentes", $"O bem {posterior.Tombo} foi adquirido depois da data do atendimento.");
        }

        if (await db.AtendimentosBens.AsNoTracking().Where(v => v.Ativo && ids.Contains(v.BemId))
                .Join(db.BensPatrimoniais, v => v.BemId, b => b.Id, (v, b) => b.Tombo).FirstOrDefaultAsync(ct) is { } vinculado)
        {
            throw ApiProblemException.Conflict("Bem já vinculado a um atendimento.", $"O bem {vinculado} já foi recebido em outro atendimento.");
        }
    }

    private static void Aplicar(DesejoAlmoxarifado desejo, DesejoConteudoRequest r)
    {
        desejo.Nome = r.Nome!.Trim();
        desejo.Descricao = r.Descricao!.Trim();
        desejo.Justificativa = r.Justificativa!.Trim();
        desejo.TipoItem = r.TipoItem!.Value;
        desejo.QuantidadeDesejada = r.QuantidadeDesejada!.Value;
        desejo.Urgencia = r.Urgencia!.Value;
        desejo.Categoria = AlmoxarifadoBanco.Opcional(r.Categoria);
        desejo.CustoUnitarioEstimado = r.CustoUnitarioEstimado;
        desejo.DataNecessidade = r.DataNecessidade;
        desejo.LinkReferencia = AlmoxarifadoBanco.Opcional(r.LinkReferencia);
        desejo.Observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes);
        desejo.Situacao = RegrasAlmoxarifado.SituacaoDesejo(desejo.QuantidadeDesejada, desejo.QuantidadeAtendida, desejo.CanceladoEmUtc is not null);
    }

    private static void Tocar(DesejoAlmoxarifado desejo, Guid usuarioId, DateTime agora)
    {
        desejo.AtualizadoPorUsuarioId = usuarioId;
        desejo.AtualizadoEmUtc = agora;
    }

    private Task<T> ExecutarAsync<T>(ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao, string? justificativa, Func<DateTime, Task<T>> trabalho,
        CancellationToken ct, ApiProblemException? conflito = null) =>
        AlmoxarifadoBanco.ExecutarAsync(db, logger, r, acao, justificativa, trabalho,
            conflito ?? ApiProblemException.Conflict("Conflito de operação simultânea.", "Outra operação alterou a mesma necessidade. Consulte novamente."),
            clock, ct);

    private async Task<DesejoDto> AlterarAsync(ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao, string? justificativa,
        Func<DesejoAlmoxarifado, DateTime, Task> trabalho, CancellationToken ct)
    {
        await ExecutarAsync(r, acao, justificativa, async agora =>
        {
            await trabalho(await DesejoTravadoAsync(r.Id, ct), agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return (await ObterAsync(r.Id, ct))!;
    }

    private async Task<DesejoAlmoxarifado> DesejoTravadoAsync(Guid id, CancellationToken ct)
    {
        await AlmoxarifadoBanco.BloquearAsync(db, TabelaDesejos, id, ct);
        return await db.DesejosAlmoxarifado.SingleOrDefaultAsync(d => d.Id == id, ct) ?? throw ApiProblemException.NotFound("Necessidade não encontrada.");
    }

    private async Task<List<DesejoDto>> MapearAsync(IReadOnlyList<DesejoAlmoxarifado> desejos, CancellationToken ct)
    {
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db, desejos.SelectMany(d => new[]
        {
            d.CriadoPorUsuarioId, d.AtualizadoPorUsuarioId, d.CanceladoPorUsuarioId, d.ExcluidoPorUsuarioId,
        }), ct);
        return desejos.Select(d =>
        {
            var pendente = RegrasAlmoxarifado.Pendente(d.QuantidadeDesejada, d.QuantidadeAtendida);
            return new DesejoDto
            {
                Id = d.Id, Nome = d.Nome, Descricao = d.Descricao, Justificativa = d.Justificativa, TipoItem = d.TipoItem,
                TipoItemDescricao = RegrasAlmoxarifado.Descricao(d.TipoItem), QuantidadeDesejada = d.QuantidadeDesejada,
                QuantidadeAtendida = d.QuantidadeAtendida, QuantidadePendente = d.Situacao == SituacaoDesejo.Cancelada ? 0 : pendente,
                Urgencia = d.Urgencia, UrgenciaDescricao = RegrasAlmoxarifado.Descricao(d.Urgencia), Categoria = d.Categoria,
                CustoUnitarioEstimado = d.CustoUnitarioEstimado, CustoTotalEstimado = RegrasAlmoxarifado.Custo(d.CustoUnitarioEstimado, d.QuantidadeDesejada),
                CustoPendenteEstimado = d.Situacao == SituacaoDesejo.Cancelada ? null : RegrasAlmoxarifado.Custo(d.CustoUnitarioEstimado, pendente),
                DataNecessidade = d.DataNecessidade, LinkReferencia = d.LinkReferencia, Observacoes = d.Observacoes, Situacao = d.Situacao,
                SituacaoDescricao = RegrasAlmoxarifado.Descricao(d.Situacao), MotivoCancelamento = d.MotivoCancelamento,
                Cancelamento = AlmoxarifadoBanco.Registro(pessoas, d.CanceladoPorUsuarioId, d.CanceladoEmUtc), Excluido = d.Excluido,
                Exclusao = d.Excluido
                    ? new ExclusaoDto(d.MotivoExclusao!, AlmoxarifadoBanco.Registro(pessoas, d.ExcluidoPorUsuarioId!.Value, d.ExcluidoEmUtc!.Value).Por,
                        DateTime.SpecifyKind(d.ExcluidoEmUtc.Value, DateTimeKind.Utc))
                    : null,
                Solicitante = pessoas.GetValueOrDefault(d.CriadoPorUsuarioId) ?? new PessoaResumoDto(d.CriadoPorUsuarioId, string.Empty),
                CriadoEmUtc = DateTime.SpecifyKind(d.CriadoEmUtc, DateTimeKind.Utc),
                UltimaAlteracao = AlmoxarifadoBanco.Registro(pessoas, d.AtualizadoPorUsuarioId, d.AtualizadoEmUtc), Atendimentos = [],
                Versao = EventoRegras.VersaoParaTexto(d.Versao),
            };
        }).ToList();
    }
}
