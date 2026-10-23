using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Inventário patrimonial (issue #126): fecha uma conferência com data e responsável e preserva a fotografia dos bens e valores daquele
/// momento. Os itens são imutáveis (trigger) e alterações posteriores nos bens não os modificam. A votação da Comissão é registrada depois,
/// uma única vez, por usuário autorizado; o sistema não simula a aprovação.
/// </summary>
public sealed class InventarioService(AlmiranteDbContext db, TimeProvider clock, DataCivil dataCivil, ILogger<InventarioService> logger)
{
    public async Task<InventarioDto> FecharAsync(FecharInventarioRequest r, CancellationToken ct)
    {
        var data = r.DataInventario!.Value;
        RegrasAlmoxarifado.ConferirNaoFutura("dataInventario", data, dataCivil.Hoje());
        r.Id = Guid.NewGuid();
        await AlmoxarifadoBanco.ExecutarAsync(db, logger, r, TipoMovimentacaoAlmoxarifado.FechamentoInventario, null, async agora =>
        {
            await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "responsavelId", r.ResponsavelId, ct);

            // Trava compartilhada da tabela até o commit: escritas simultâneas em bens esperam, e a fotografia fica consistente.
            await db.Database.ExecuteSqlRawAsync("SELECT COUNT(*) FROM [dbo].[patrimonio_bens] WITH (TABLOCK, HOLDLOCK);", ct);
            var bens = await db.BensPatrimoniais.AsNoTracking().Where(b => !b.Excluido && b.Situacao != SituacaoBem.Baixado)
                .OrderBy(b => b.NumeroTombo).ToListAsync(ct);
            db.InventariosPatrimonio.Add(new InventarioPatrimonio
            {
                Id = r.Id, DataInventario = data, ResponsavelId = r.ResponsavelId!.Value, Observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes),
                TotalBens = bens.Count, TotalBensComValorComercial = bens.Count(b => b.PossuiValorComercial),
                ValorEstimadoPatrimonial = bens.Where(b => b.PossuiValorComercial).Sum(b => b.ValorEstimado ?? 0m),
                FechadoPorUsuarioId = r.UsuarioId, FechadoEmUtc = agora,
            });
            db.InventarioItens.AddRange(bens.Select(b => new InventarioItem
            {
                Id = Guid.NewGuid(), InventarioId = r.Id, BemId = b.Id, Tombo = b.Tombo, Nome = b.Nome, Descricao = b.Descricao, Categoria = b.Categoria,
                EstadoConservacao = b.EstadoConservacao, Situacao = b.Situacao, Localizacao = b.Localizacao, Origem = b.Origem,
                DataAquisicao = b.DataAquisicao, DataAquisicaoDesconhecida = b.DataAquisicaoDesconhecida, ValorAquisicao = b.ValorAquisicao,
                PossuiValorComercial = b.PossuiValorComercial, ValorEstimado = b.ValorEstimado,
            }));
            await db.SaveChangesAsync(ct);
            return true;
        }, ApiProblemException.Conflict("Conflito ao fechar o inventário.", "Repita a operação."), clock, ct);

        logger.LogInformation("Inventário {InventarioId} fechado por {UsuarioId}.", r.Id, r.UsuarioId);
        return (await ObterAsync(r.Id, ct))!;
    }

    public async Task<InventarioDto> RegistrarVotacaoAsync(RegistrarVotacaoInventarioRequest r, CancellationToken ct)
    {
        await AlmoxarifadoBanco.ExecutarAsync(db, logger, r, TipoMovimentacaoAlmoxarifado.VotacaoInventario, null, async agora =>
        {
            await AlmoxarifadoBanco.BloquearAsync(db, "patrimonio_inventarios", r.Id, ct);
            var inventario = await db.InventariosPatrimonio.SingleOrDefaultAsync(i => i.Id == r.Id, ct) ?? throw NaoEncontrado();
            FormacaoBanco.ConferirVersao(inventario.Versao, r.Versao);
            RegrasAlmoxarifado.ConferirVotacao(inventario, r.DataVotacao!.Value, dataCivil.Hoje());
            inventario.DataVotacao = r.DataVotacao;
            inventario.ReferenciaAta = r.ReferenciaAta!.Trim();
            inventario.VotacaoRegistradaPorUsuarioId = r.UsuarioId;
            inventario.VotacaoRegistradaEmUtc = agora;
            await db.SaveChangesAsync(ct);
            return true;
        }, ApiProblemException.Conflict("Conflito ao registrar a votação.", "Consulte o inventário novamente."), clock, ct);

        return (await ObterAsync(r.Id, ct))!;
    }

    public async Task<PaginaDto<InventarioResumoDto>> ListarAsync(ListarInventariosQuery q, CancellationToken ct)
    {
        var total = await db.InventariosPatrimonio.CountAsync(ct);
        var pagina = await db.InventariosPatrimonio.AsNoTracking().OrderByDescending(i => i.DataInventario).ThenByDescending(i => i.FechadoEmUtc)
            .ThenBy(i => i.Id).Skip((q.Page - 1) * q.PageSize).Take(q.PageSize).ToListAsync(ct);
        return FormacaoMapping.Pagina(await ResumirAsync(pagina, ct), total, q.Page, q.PageSize);
    }

    public async Task<InventarioDto?> ObterAsync(Guid id, CancellationToken ct)
    {
        var inventario = await db.InventariosPatrimonio.AsNoTracking().SingleOrDefaultAsync(i => i.Id == id, ct);
        if (inventario is null)
        {
            return null;
        }

        var itens = await db.InventarioItens.AsNoTracking().Where(i => i.InventarioId == id).OrderBy(i => i.Tombo).ToListAsync(ct);
        return new InventarioDto((await ResumirAsync([inventario], ct))[0], itens.Select(i => new InventarioItemDto(i.BemId, i.Tombo, i.Nome, i.Descricao,
            i.Categoria, i.EstadoConservacao, RegrasAlmoxarifado.Descricao(i.EstadoConservacao), i.Situacao, RegrasAlmoxarifado.Descricao(i.Situacao),
            i.Localizacao, i.Origem, i.DataAquisicao, i.DataAquisicaoDesconhecida, i.ValorAquisicao, i.PossuiValorComercial, i.ValorEstimado)).ToList());
    }

    private async Task<List<InventarioResumoDto>> ResumirAsync(IReadOnlyList<InventarioPatrimonio> inventarios, CancellationToken ct)
    {
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db,
            inventarios.SelectMany(i => new[] { i.ResponsavelId, i.FechadoPorUsuarioId, i.VotacaoRegistradaPorUsuarioId }), ct);
        return inventarios.Select(i => new InventarioResumoDto
        {
            Id = i.Id, DataInventario = i.DataInventario,
            Responsavel = pessoas.GetValueOrDefault(i.ResponsavelId) ?? new PessoaResumoDto(i.ResponsavelId, string.Empty),
            Observacoes = i.Observacoes, TotalBens = i.TotalBens, TotalBensComValorComercial = i.TotalBensComValorComercial,
            ValorEstimadoPatrimonial = i.ValorEstimadoPatrimonial, Fechamento = AlmoxarifadoBanco.Registro(pessoas, i.FechadoPorUsuarioId, i.FechadoEmUtc),
            Votado = i.VotacaoRegistradaEmUtc is not null,
            Votacao = AlmoxarifadoBanco.Registro(pessoas, i.VotacaoRegistradaPorUsuarioId, i.VotacaoRegistradaEmUtc) is { } registro
                ? new VotacaoInventarioDto(i.DataVotacao!.Value, i.ReferenciaAta!, registro.Por, registro.EmUtc)
                : null,
            Versao = EventoRegras.VersaoParaTexto(i.Versao),
        }).ToList();
    }

    private static ApiProblemException NaoEncontrado() => ApiProblemException.NotFound("Inventário não encontrado.");
}
