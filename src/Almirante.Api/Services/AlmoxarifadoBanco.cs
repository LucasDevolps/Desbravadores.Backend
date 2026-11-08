using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Transação auditada, travas e consultas comuns ao patrimônio e à lista de desejos (issue #126). Reutiliza a auditoria da formação:
/// o SESSION_CONTEXT (responsável, IP, ação e justificativa) é lido pelos triggers que gravam almoxarifado_historico.
/// </summary>
internal static class AlmoxarifadoBanco
{
    /// <summary>Conflito de rowversion ou de índice único (operação simultânea) vira 409 com <paramref name="conflito"/>.</summary>
    public static Task<T> ExecutarAsync<T>(AlmiranteDbContext db, ILogger logger, ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao,
        string? justificativa, Func<DateTime, Task<T>> trabalho, ApiProblemException conflito, TimeProvider clock, CancellationToken ct) =>
        FormacaoBanco.ExecutarAsync(db, logger, r.UsuarioId, r.IpResponsavel, acao.ToString(), justificativa,
            _ => trabalho(clock.GetUtcNow().UtcDateTime), conflito, ct);

    /// <summary>Serializa as escritas no mesmo registro até o fim da transação (as regras são conferidas com o estado travado).</summary>
    public static Task BloquearAsync(AlmiranteDbContext db, string tabela, Guid id, CancellationToken ct) => tabela switch
    {
        "patrimonio_bens" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[patrimonio_bens] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "patrimonio_emprestimos" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[patrimonio_emprestimos] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "patrimonio_manutencoes" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[patrimonio_manutencoes] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "patrimonio_inventarios" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[patrimonio_inventarios] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "almoxarifado_desejos" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[almoxarifado_desejos] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        "almoxarifado_responsaveis" => db.Database.ExecuteSqlAsync($"SELECT 1 FROM [dbo].[almoxarifado_responsaveis] WITH (UPDLOCK, ROWLOCK) WHERE [Id] = {id}", ct),
        _ => throw new ArgumentOutOfRangeException(nameof(tabela), tabela, "Tabela sem trava prevista."),
    };

    /// <summary>Referências a membros (guarda, retirada, recebimento, inventário, designação) exigem um usuário ativo.</summary>
    public static async Task ConferirMembroAtivoAsync(AlmiranteDbContext db, string campo, Guid? membroId, CancellationToken ct)
    {
        if (membroId is { } id && !await db.Usuarios.AnyAsync(u => u.Id == id && u.Ativo, ct))
        {
            throw ApiProblemException.Invalid(campo, $"{campo} deve ser um membro ativo.");
        }
    }

    public static async Task<Dictionary<Guid, PessoaResumoDto>> PessoasAsync(AlmiranteDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var distintos = ids.OfType<Guid>().Distinct().ToList();
        return distintos.Count == 0
            ? []
            : await db.Usuarios.AsNoTracking().Where(u => distintos.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => new PessoaResumoDto(u.Id, u.Nome), ct);
    }

    public static string? Opcional(string? texto) => FormacaoBanco.Opcional(texto);

    public static IReadOnlyList<HistoricoAlmoxarifadoDto> Historico(IEnumerable<(HistoricoAlmoxarifado Linha, string Nome)> linhas) =>
        linhas.Select(l => new HistoricoAlmoxarifadoDto(l.Linha.Id, l.Linha.Tabela, l.Linha.RegistroId, RegrasAlmoxarifado.Tipo(l.Linha.Acao),
            RegrasAlmoxarifado.Tipo(l.Linha.Acao) is { } tipo ? RegrasAlmoxarifado.Descricao(tipo) : null, l.Linha.Motivo,
            new PessoaResumoDto(l.Linha.UsuarioResponsavelId, l.Nome), DateTime.SpecifyKind(l.Linha.OcorridoEmUtc, DateTimeKind.Utc),
            l.Linha.EstadoAnteriorJson is null ? null : Json(l.Linha.EstadoAnteriorJson), Json(l.Linha.EstadoNovoJson))).ToList();

    public static async Task<IReadOnlyList<HistoricoAlmoxarifadoDto>> HistoricoAsync(AlmiranteDbContext db, HistoricoQuery q, CancellationToken ct)
    {
        var (inicio, fim) = RegrasAlmoxarifado.Periodo(q.De, q.Ate);
        var acao = q.Tipo?.ToString();
        var linhas = await (
            from h in db.HistoricoAlmoxarifado.AsNoTracking()
            join u in db.Usuarios.AsNoTracking() on h.UsuarioResponsavelId equals u.Id
            where (q.DoDesejo ? h.DesejoId == q.Id : h.BemId == q.Id)
                && (inicio == null || h.OcorridoEmUtc >= inicio) && (fim == null || h.OcorridoEmUtc < fim) && (acao == null || h.Acao == acao)
            orderby h.OcorridoEmUtc, h.Id
            select new { h, u.Nome }).ToListAsync(ct);
        return Historico(linhas.Select(l => (l.h, l.Nome)));
    }

    /// <summary>Pessoa carregada por <see cref="PessoasAsync"/>; sem ela (não deveria faltar: as FKs garantem o usuário), só o id.</summary>
    public static PessoaResumoDto Pessoa(IReadOnlyDictionary<Guid, PessoaResumoDto> pessoas, Guid id) =>
        pessoas.GetValueOrDefault(id) ?? new PessoaResumoDto(id, string.Empty);

    public static RegistroUsuarioDto Registro(IReadOnlyDictionary<Guid, PessoaResumoDto> pessoas, Guid usuarioId, DateTime emUtc) =>
        new(Pessoa(pessoas, usuarioId), DateTime.SpecifyKind(emUtc, DateTimeKind.Utc));

    public static RegistroUsuarioDto? Registro(IReadOnlyDictionary<Guid, PessoaResumoDto> pessoas, Guid? usuarioId, DateTime? emUtc) =>
        usuarioId is { } id && emUtc is { } em ? Registro(pessoas, id, em) : null;

    private static System.Text.Json.JsonElement Json(string json)
    {
        using var documento = System.Text.Json.JsonDocument.Parse(json);
        return documento.RootElement.Clone();
    }
}
