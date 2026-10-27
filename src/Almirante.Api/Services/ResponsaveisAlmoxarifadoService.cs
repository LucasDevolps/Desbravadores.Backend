using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Designação do responsável pelo almoxarifado do clube (issue #126), separada do cargo: o membro mantém o próprio cargo e passa a gerenciar
/// o almoxarifado enquanto a designação estiver vigente. Designar e revogar são auditados; a revogação vale na requisição seguinte.
/// </summary>
public sealed class ResponsaveisAlmoxarifadoService(AlmiranteDbContext db, TimeProvider clock, DataCivil dataCivil,
    ILogger<ResponsaveisAlmoxarifadoService> logger)
{
    public async Task<ResponsavelAlmoxarifadoDto> DesignarAsync(DesignarResponsavelRequest r, CancellationToken ct)
    {
        RegrasAlmoxarifado.ConferirNaoFutura("dataInicio", r.DataInicio, dataCivil.Hoje());
        r.Id = Guid.NewGuid();
        await AlmoxarifadoBanco.ExecutarAsync(db, logger, r, TipoMovimentacaoAlmoxarifado.Designacao, null, async agora =>
        {
            await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "membroId", r.MembroId, ct);
            if (await db.ResponsaveisAlmoxarifado.AnyAsync(x => x.MembroId == r.MembroId && x.RevogadoEmUtc == null, ct))
            {
                throw ConflitoJaDesignado();
            }

            db.ResponsaveisAlmoxarifado.Add(new ResponsavelAlmoxarifado
            {
                Id = r.Id, MembroId = r.MembroId!.Value, DataInicio = r.DataInicio!.Value, Observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes),
                DesignadoPorUsuarioId = r.UsuarioId, DesignadoEmUtc = agora,
            });
            await db.SaveChangesAsync(ct);
            return true;
        }, ConflitoJaDesignado(), clock, ct);

        logger.LogInformation("Membro {MembroId} designado responsável pelo almoxarifado por {UsuarioId}.", r.MembroId, r.UsuarioId);
        return (await ListarAsync(true, ct, r.Id)).Single();
    }

    public async Task<ResponsavelAlmoxarifadoDto> RevogarAsync(RevogarResponsavelRequest r, CancellationToken ct)
    {
        var motivo = r.Motivo!.Trim();
        await AlmoxarifadoBanco.ExecutarAsync(db, logger, r, TipoMovimentacaoAlmoxarifado.RevogacaoDesignacao, motivo, async agora =>
        {
            await AlmoxarifadoBanco.BloquearAsync(db, "almoxarifado_responsaveis", r.Id, ct);
            var designacao = await db.ResponsaveisAlmoxarifado.SingleOrDefaultAsync(x => x.Id == r.Id, ct)
                ?? throw ApiProblemException.NotFound("Designação não encontrada.");
            FormacaoBanco.ConferirVersao(designacao.Versao, r.Versao);
            RegrasAlmoxarifado.ConferirRevogacao(designacao);
            designacao.MotivoRevogacao = motivo;
            designacao.RevogadoPorUsuarioId = r.UsuarioId;
            designacao.RevogadoEmUtc = agora;
            await db.SaveChangesAsync(ct);
            return true;
        }, ApiProblemException.Conflict("Conflito ao revogar a designação.", "Consulte novamente."), clock, ct);

        return (await ListarAsync(true, ct, r.Id)).Single();
    }

    public async Task<IReadOnlyList<ResponsavelAlmoxarifadoDto>> ListarAsync(bool incluirRevogados, CancellationToken ct, Guid? somenteId = null)
    {
        var designacoes = await db.ResponsaveisAlmoxarifado.AsNoTracking()
            .Where(x => (incluirRevogados || x.RevogadoEmUtc == null) && (somenteId == null || x.Id == somenteId))
            .OrderBy(x => x.RevogadoEmUtc != null).ThenByDescending(x => x.DesignadoEmUtc).ThenBy(x => x.Id).ToListAsync(ct);
        var membros = designacoes.Select(d => d.MembroId).Distinct().ToList();
        var cargos = await db.Usuarios.AsNoTracking().Where(u => membros.Contains(u.Id))
            .Select(u => new { u.Id, Cargo = u.Cargo!.Nome }).ToDictionaryAsync(u => u.Id, u => u.Cargo, ct);
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db,
            designacoes.SelectMany(d => new[] { d.MembroId, d.DesignadoPorUsuarioId, d.RevogadoPorUsuarioId }), ct);
        return designacoes.Select(d => new ResponsavelAlmoxarifadoDto(d.Id,
            AlmoxarifadoBanco.Pessoa(pessoas, d.MembroId), cargos.GetValueOrDefault(d.MembroId),
            d.DataInicio, d.Observacoes, AlmoxarifadoBanco.Registro(pessoas, d.DesignadoPorUsuarioId, d.DesignadoEmUtc), d.Vigente, d.MotivoRevogacao,
            AlmoxarifadoBanco.Registro(pessoas, d.RevogadoPorUsuarioId, d.RevogadoEmUtc), EventoRegras.VersaoParaTexto(d.Versao))).ToList();
    }

    private static ApiProblemException ConflitoJaDesignado() =>
        ApiProblemException.Conflict("Membro já designado.", "O membro já é responsável pelo almoxarifado; revogue a designação vigente antes.");
}
