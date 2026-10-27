using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Cadastro e movimentação dos bens patrimoniais (issue #126): empréstimo, devolução, perda/furto, manutenção, baixa e exclusão lógica,
/// com consultas, histórico e relatórios. Toda escrita é transacional, trava o bem (UPDLOCK) e tem o estado anterior gravado por trigger em
/// almoxarifado_historico. Nada aqui gera lançamento financeiro nem altera o dashboard.
/// </summary>
public sealed class PatrimonioService(AlmiranteDbContext db, TimeProvider clock, DataCivil dataCivil, ILogger<PatrimonioService> logger)
{
    private const string TabelaBens = "patrimonio_bens";

    public async Task<BemDto> CadastrarAsync(CadastrarBemRequest r, CancellationToken ct)
    {
        RegrasAlmoxarifado.ConferirNaoFutura("dataAquisicao", r.DataAquisicao, dataCivil.Hoje());
        r.Id = Guid.NewGuid();
        await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.Cadastro, null, async agora =>
        {
            await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "responsavelGuardaId", r.ResponsavelGuardaId, ct);
            var bem = NovoBem(r, r.UsuarioId, agora);
            bem.Id = r.Id;
            AplicarAquisicao(bem, r);
            db.BensPatrimoniais.Add(bem);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        logger.LogInformation("Bem {BemId} cadastrado por {UsuarioId}.", r.Id, r.UsuarioId);
        return (await ObterAsync(r.Id, ct))!;
    }

    public async Task<BemDto> AtualizarAsync(AtualizarBemRequest r, CancellationToken ct)
    {
        RegrasAlmoxarifado.ConferirNaoFutura("dataAquisicao", r.DataAquisicao, dataCivil.Hoje());
        return await AlterarBemAsync(r, TipoMovimentacaoAlmoxarifado.Alteracao, null, async (bem, agora) =>
        {
            FormacaoBanco.ConferirVersao(bem.Versao, r.Versao);
            RegrasAlmoxarifado.ConferirAlteracao(bem);
            if (r.ResponsavelGuardaId != bem.ResponsavelGuardaId)
            {
                await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "responsavelGuardaId", r.ResponsavelGuardaId, ct);
            }

            AplicarDados(bem, r);
            AplicarAquisicao(bem, r);
            Tocar(bem, r.UsuarioId, agora);
        }, ct);
    }

    public Task<BemDto> ExcluirAsync(ExcluirBemRequest r, CancellationToken ct)
    {
        var justificativa = r.Justificativa!.Trim();
        return AlterarBemAsync(r, TipoMovimentacaoAlmoxarifado.Exclusao, justificativa, async (bem, agora) =>
        {
            FormacaoBanco.ConferirVersao(bem.Versao, r.Versao);
            var teveMovimentacoes = await db.EmprestimosBens.AnyAsync(e => e.BemId == bem.Id, ct)
                || await db.ManutencoesBens.AnyAsync(m => m.BemId == bem.Id, ct);
            var vinculado = await db.AtendimentosBens.AnyAsync(v => v.BemId == bem.Id && v.Ativo, ct);
            RegrasAlmoxarifado.ConferirExclusao(bem, teveMovimentacoes, vinculado);
            bem.Excluido = true;
            bem.MotivoExclusao = justificativa;
            bem.ExcluidoPorUsuarioId = r.UsuarioId;
            bem.ExcluidoEmUtc = agora;
            Tocar(bem, r.UsuarioId, agora);
        }, ct);
    }

    public async Task<EmprestimoDto> EmprestarAsync(EmprestarBemRequest r, CancellationToken ct)
    {
        var (retirada, previsao) = (r.DataRetirada!.Value, r.PrevisaoDevolucao!.Value);
        RegrasAlmoxarifado.ConferirDatasEmprestimo(retirada, previsao, dataCivil.Hoje());
        var emprestimoId = Guid.NewGuid();
        await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.Emprestimo, null, async agora =>
        {
            var bem = await BemTravadoAsync(r.Id, ct);
            RegrasAlmoxarifado.ConferirEmprestimo(bem, r.ConservacaoSaida!.Value);
            RegrasAlmoxarifado.ConferirPosterior("dataRetirada", retirada, bem.DataAquisicao, await UltimaMovimentacaoAsync(bem.Id, ct));
            await AlmoxarifadoBanco.ConferirMembroAtivoAsync(db, "membroId", r.MembroId, ct);
            db.EmprestimosBens.Add(new EmprestimoBem
            {
                Id = emprestimoId, BemId = bem.Id, MembroId = r.MembroId!.Value, DataRetirada = retirada, PrevisaoDevolucao = previsao,
                ConservacaoSaida = r.ConservacaoSaida.Value, ObservacoesSaida = AlmoxarifadoBanco.Opcional(r.Observacoes),
                RegistradoPorUsuarioId = r.UsuarioId, RegistradoEmUtc = agora,
            });
            bem.Situacao = SituacaoBem.Emprestado;
            bem.EstadoConservacao = r.ConservacaoSaida.Value;
            Tocar(bem, r.UsuarioId, agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct, ConflitoEmprestimoAberto());

        return (await ObterEmprestimoAsync(emprestimoId, ct))!;
    }

    public Task<EmprestimoDto> DevolverAsync(DevolverEmprestimoRequest r, CancellationToken ct) =>
        EncerrarEmprestimoAsync(r, TipoMovimentacaoAlmoxarifado.Devolucao, null, (emprestimo, bem, agora) =>
        {
            var data = r.DataDevolucao!.Value;
            RegrasAlmoxarifado.ConferirEncerramento(emprestimo, "dataDevolucao", data, dataCivil.Hoje());
            emprestimo.Encerramento = EncerramentoEmprestimo.Devolvido;
            emprestimo.DataEncerramento = data;
            emprestimo.ConservacaoRetorno = r.ConservacaoRetorno!.Value;
            emprestimo.ObservacoesRetorno = AlmoxarifadoBanco.Opcional(r.Observacoes);
            bem.EstadoConservacao = r.ConservacaoRetorno.Value;
            if (r.EncaminharParaManutencao == true)
            {
                db.ManutencoesBens.Add(new ManutencaoBem
                {
                    Id = Guid.NewGuid(), BemId = bem.Id, DataEntrada = data, Motivo = r.MotivoManutencao!.Trim(),
                    ObservacoesEntrada = "Encaminhado para manutenção na devolução do empréstimo.", RegistradaPorUsuarioId = r.UsuarioId,
                    RegistradaEmUtc = agora,
                });
                bem.Situacao = SituacaoBem.EmManutencao;
            }
            else
            {
                bem.Situacao = SituacaoBem.Disponivel;
            }
        }, ct);

    /// <summary>Perda ou furto durante o empréstimo: encerra o empréstimo e baixa o bem com o mesmo motivo, na mesma transação.</summary>
    public Task<EmprestimoDto> RegistrarPerdaFurtoAsync(RegistrarPerdaFurtoRequest r, CancellationToken ct)
    {
        var justificativa = r.Justificativa!.Trim();
        return EncerrarEmprestimoAsync(r, TipoMovimentacaoAlmoxarifado.PerdaOuFurto, justificativa, (emprestimo, bem, agora) =>
        {
            var data = r.Data!.Value;
            RegrasAlmoxarifado.ConferirEncerramento(emprestimo, "data", data, dataCivil.Hoje());
            emprestimo.Encerramento = r.Motivo!.Value;
            emprestimo.DataEncerramento = data;
            emprestimo.ObservacoesRetorno = justificativa;
            Baixar(bem, data, r.Motivo == EncerramentoEmprestimo.Furto ? MotivoBaixa.Furto : MotivoBaixa.Perda, justificativa, r.UsuarioId, agora);
        }, ct);
    }

    public async Task<ManutencaoDto> RegistrarEntradaManutencaoAsync(EntradaManutencaoRequest r, CancellationToken ct)
    {
        var manutencaoId = Guid.NewGuid();
        await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.EntradaManutencao, null, async agora =>
        {
            var bem = await BemTravadoAsync(r.Id, ct);
            var data = r.DataEntrada!.Value;
            RegrasAlmoxarifado.ConferirEntradaManutencao(bem, data, dataCivil.Hoje());
            RegrasAlmoxarifado.ConferirPosterior("dataEntrada", data, bem.DataAquisicao, await UltimaMovimentacaoAsync(bem.Id, ct));
            db.ManutencoesBens.Add(new ManutencaoBem
            {
                Id = manutencaoId, BemId = bem.Id, DataEntrada = data, Motivo = r.Motivo!.Trim(), ObservacoesEntrada = AlmoxarifadoBanco.Opcional(r.Observacoes),
                RegistradaPorUsuarioId = r.UsuarioId, RegistradaEmUtc = agora,
            });
            bem.Situacao = SituacaoBem.EmManutencao;
            Tocar(bem, r.UsuarioId, agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct, ApiProblemException.Conflict("O bem já tem manutenção aberta.", "Outra operação abriu uma manutenção para este bem."));

        return (await ObterManutencaoAsync(manutencaoId, ct))!;
    }

    public async Task<ManutencaoDto> RegistrarSaidaManutencaoAsync(SaidaManutencaoRequest r, CancellationToken ct)
    {
        var bemId = await db.ManutencoesBens.AsNoTracking().Where(m => m.Id == r.Id).Select(m => (Guid?)m.BemId).SingleOrDefaultAsync(ct)
            ?? throw ApiProblemException.NotFound("Manutenção não encontrada.");
        await ExecutarAsync(r, TipoMovimentacaoAlmoxarifado.SaidaManutencao, null, async agora =>
        {
            var bem = await BemTravadoAsync(bemId, ct);
            await AlmoxarifadoBanco.BloquearAsync(db, "patrimonio_manutencoes", r.Id, ct);
            var manutencao = await db.ManutencoesBens.SingleAsync(m => m.Id == r.Id, ct);
            RegrasAlmoxarifado.ConferirSaidaManutencao(manutencao, r.DataSaida!.Value, dataCivil.Hoje());
            manutencao.DataSaida = r.DataSaida;
            manutencao.ConservacaoSaida = r.ConservacaoSaida;
            manutencao.ObservacoesSaida = AlmoxarifadoBanco.Opcional(r.Observacoes);
            manutencao.SaidaRegistradaPorUsuarioId = r.UsuarioId;
            manutencao.SaidaRegistradaEmUtc = agora;
            bem.Situacao = SituacaoBem.Disponivel;
            bem.EstadoConservacao = r.ConservacaoSaida!.Value;
            Tocar(bem, r.UsuarioId, agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return (await ObterManutencaoAsync(r.Id, ct))!;
    }

    /// <summary>Baixa justificada; em manutenção, a manutenção aberta é encerrada na data da baixa. O cadastro e o histórico ficam.</summary>
    public Task<BemDto> BaixarAsync(BaixarBemRequest r, CancellationToken ct)
    {
        var justificativa = r.Justificativa!.Trim();
        return AlterarBemAsync(r, TipoMovimentacaoAlmoxarifado.Baixa, justificativa, async (bem, agora) =>
        {
            var data = r.DataBaixa!.Value;
            RegrasAlmoxarifado.ConferirBaixa(bem, data, dataCivil.Hoje());
            RegrasAlmoxarifado.ConferirPosterior("dataBaixa", data, bem.DataAquisicao, await UltimaMovimentacaoAsync(bem.Id, ct));
            if (await db.ManutencoesBens.SingleOrDefaultAsync(m => m.BemId == bem.Id && m.DataSaida == null, ct) is { } aberta)
            {
                aberta.DataSaida = data;
                aberta.ConservacaoSaida = bem.EstadoConservacao;
                aberta.ObservacoesSaida = "Manutenção encerrada pela baixa do bem.";
                aberta.SaidaRegistradaPorUsuarioId = r.UsuarioId;
                aberta.SaidaRegistradaEmUtc = agora;
            }

            Baixar(bem, data, r.Motivo!.Value, justificativa, r.UsuarioId, agora);
        }, ct);
    }

    public async Task<BemDto?> ObterAsync(Guid id, CancellationToken ct)
    {
        var bem = await db.BensPatrimoniais.AsNoTracking().SingleOrDefaultAsync(b => b.Id == id, ct);
        return bem is null ? null : (await MapearAsync([bem], ct))[0];
    }

    public async Task<PaginaDto<BemDto>> ListarAsync(ListarBensQuery f, CancellationToken ct)
    {
        var consulta = Filtrar(db.BensPatrimoniais.AsNoTracking(), f);
        var total = await consulta.CountAsync(ct);
        var pagina = await consulta.OrderBy(b => b.Nome).ThenBy(b => b.NumeroTombo).ThenBy(b => b.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct);
        return FormacaoMapping.Pagina(await MapearAsync(pagina, ct), total, f.Page, f.PageSize);
    }

    public async Task<IReadOnlyList<HistoricoAlmoxarifadoDto>?> HistoricoAsync(HistoricoQuery q, CancellationToken ct)
    {
        var existe = q.DoDesejo
            ? await db.DesejosAlmoxarifado.AnyAsync(d => d.Id == q.Id, ct)
            : await db.BensPatrimoniais.AnyAsync(b => b.Id == q.Id, ct);
        return existe ? await AlmoxarifadoBanco.HistoricoAsync(db, q, ct) : null;
    }

    public async Task<EmprestimoDto?> ObterEmprestimoAsync(Guid id, CancellationToken ct) =>
        (await MapearEmprestimosAsync(await db.EmprestimosBens.AsNoTracking().Where(e => e.Id == id).ToListAsync(ct), ct)).SingleOrDefault();

    public async Task<PaginaDto<EmprestimoDto>> ListarEmprestimosAsync(ListarEmprestimosQuery f, CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var consulta = db.EmprestimosBens.AsNoTracking();
        if (f.BemId is { } bemId)
        {
            consulta = consulta.Where(e => e.BemId == bemId);
        }

        if (f.MembroId is { } membroId)
        {
            consulta = consulta.Where(e => e.MembroId == membroId);
        }

        if (f.Abertos is { } abertos)
        {
            consulta = abertos ? consulta.Where(e => e.Encerramento == null) : consulta.Where(e => e.Encerramento != null);
        }

        if (f.Atrasados is { } atrasados)
        {
            consulta = atrasados
                ? consulta.Where(e => e.Encerramento == null && e.PrevisaoDevolucao < hoje)
                : consulta.Where(e => e.Encerramento != null || e.PrevisaoDevolucao >= hoje);
        }

        var total = await consulta.CountAsync(ct);
        var pagina = await consulta.OrderByDescending(e => e.DataRetirada).ThenByDescending(e => e.RegistradoEmUtc).ThenBy(e => e.Id)
            .Skip((f.Page - 1) * f.PageSize).Take(f.PageSize).ToListAsync(ct);
        return FormacaoMapping.Pagina(await MapearEmprestimosAsync(pagina, ct), total, f.Page, f.PageSize);
    }

    public async Task<IReadOnlyList<ManutencaoDto>?> ListarManutencoesAsync(Guid bemId, CancellationToken ct)
    {
        if (!await db.BensPatrimoniais.AnyAsync(b => b.Id == bemId, ct))
        {
            return null;
        }

        var manutencoes = await db.ManutencoesBens.AsNoTracking().Where(m => m.BemId == bemId)
            .OrderByDescending(m => m.DataEntrada).ThenByDescending(m => m.RegistradaEmUtc).ToListAsync(ct);
        return await MapearManutencoesAsync(manutencoes, ct);
    }

    public async Task<RelatorioPatrimonioDto> RelatorioAsync(ListarBensQuery f, CancellationToken ct)
    {
        var consulta = Filtrar(db.BensPatrimoniais.AsNoTracking(), f);
        var porSituacao = await consulta.GroupBy(b => b.Situacao).Select(g => new { g.Key, Quantidade = g.Count() }).ToListAsync(ct);
        var comValor = await consulta.CountAsync(b => b.PossuiValorComercial, ct);
        var valor = await consulta.Where(b => b.PossuiValorComercial && b.Situacao != SituacaoBem.Baixado).SumAsync(b => b.ValorEstimado, ct) ?? 0m;
        var total = porSituacao.Sum(s => s.Quantidade);
        return new RelatorioPatrimonioDto(total,
            Enum.GetValues<SituacaoBem>().Select(s => new TotalPorSituacaoDto(s, RegrasAlmoxarifado.Descricao(s),
                porSituacao.SingleOrDefault(p => p.Key == s)?.Quantidade ?? 0)).ToList(),
            comValor, total - comValor, valor, "Valor estimado patrimonial (estimativa, não é saldo financeiro)");
    }

    public async Task<ArquivoCsv> ExportarAsync(ListarBensQuery f, CancellationToken ct)
    {
        var bens = await Filtrar(db.BensPatrimoniais.AsNoTracking(), f).OrderBy(b => b.NumeroTombo).ToListAsync(ct);
        var guarda = await AlmoxarifadoBanco.PessoasAsync(db, bens.Select(b => b.ResponsavelGuardaId), ct);
        var conteudo = CsvAlmoxarifado.Gerar(
            ["Tombo", "Nome", "Descrição", "Categoria", "Conservação", "Localização", "Situação", "Responsável pela guarda", "Data de aquisição",
                "Origem", "Valor de aquisição", "Possui valor comercial", "Valor estimado", "Excluído"],
            bens.Select(b => new[]
            {
                b.Tombo, b.Nome, b.Descricao, b.Categoria, RegrasAlmoxarifado.Descricao(b.EstadoConservacao), b.Localizacao,
                RegrasAlmoxarifado.Descricao(b.Situacao), b.ResponsavelGuardaId is { } g ? guarda.GetValueOrDefault(g)?.Nome : null,
                b.DataAquisicaoDesconhecida ? "Desconhecida" : CsvAlmoxarifado.Data(b.DataAquisicao),
                RegrasAlmoxarifado.Descricao(b.Origem), CsvAlmoxarifado.Valor(b.ValorAquisicao), CsvAlmoxarifado.SimNao(b.PossuiValorComercial),
                CsvAlmoxarifado.Valor(b.ValorEstimado), CsvAlmoxarifado.SimNao(b.Excluido),
            }));
        return new ArquivoCsv($"patrimonio-{dataCivil.Hoje():yyyy-MM-dd}.csv", conteudo);
    }

    /// <summary>Empréstimos abertos (ou só os atrasados), da previsão mais antiga para a mais recente.</summary>
    public async Task<IReadOnlyList<EmprestimoDto>> RelatorioEmprestimosAsync(bool somenteAtrasados, CancellationToken ct)
    {
        var hoje = dataCivil.Hoje();
        var emprestimos = await db.EmprestimosBens.AsNoTracking()
            .Where(e => e.Encerramento == null && (!somenteAtrasados || e.PrevisaoDevolucao < hoje))
            .OrderBy(e => e.PrevisaoDevolucao).ThenBy(e => e.DataRetirada).ThenBy(e => e.Id).ToListAsync(ct);
        return await MapearEmprestimosAsync(emprestimos, ct);
    }

    public async Task<ArquivoCsv> ExportarEmprestimosAsync(bool somenteAtrasados, CancellationToken ct)
    {
        var emprestimos = await RelatorioEmprestimosAsync(somenteAtrasados, ct);
        var conteudo = CsvAlmoxarifado.Gerar(
            ["Tombo", "Bem", "Membro", "Data de retirada", "Previsão de devolução", "Conservação na saída", "Atrasado", "Dias de atraso", "Observações"],
            emprestimos.Select(e => new[]
            {
                e.Bem.Tombo, e.Bem.Nome, e.Membro.Nome, CsvAlmoxarifado.Data(e.DataRetirada), CsvAlmoxarifado.Data(e.PrevisaoDevolucao),
                e.ConservacaoSaidaDescricao, CsvAlmoxarifado.SimNao(e.Atrasado), e.DiasAtraso.ToString(System.Globalization.CultureInfo.InvariantCulture),
                e.ObservacoesSaida,
            }));
        return new ArquivoCsv($"emprestimos-{(somenteAtrasados ? "atrasados" : "abertos")}-{dataCivil.Hoje():yyyy-MM-dd}.csv", conteudo);
    }

    /// <summary>Bem novo (cadastro direto ou recebido num atendimento): disponível, com dados físicos e de guarda do pedido.</summary>
    internal static BemPatrimonial NovoBem(BemDadosRequest r, Guid usuarioId, DateTime agora)
    {
        var bem = new BemPatrimonial
        {
            Id = Guid.NewGuid(), Nome = string.Empty, Descricao = string.Empty, Localizacao = string.Empty, Situacao = SituacaoBem.Disponivel,
            CriadoPorUsuarioId = usuarioId, CriadoEmUtc = agora,
        };
        AplicarDados(bem, r);
        return bem;
    }

    private static void AplicarDados(BemPatrimonial bem, BemDadosRequest r)
    {
        bem.Nome = r.Nome!.Trim();
        bem.Descricao = r.Descricao!.Trim();
        bem.Categoria = AlmoxarifadoBanco.Opcional(r.Categoria);
        bem.EstadoConservacao = r.EstadoConservacao!.Value;
        bem.PossuiValorComercial = r.PossuiValorComercial!.Value;
        bem.ValorEstimado = bem.PossuiValorComercial ? r.ValorEstimado : null;
        bem.Localizacao = r.Localizacao!.Trim();
        bem.ResponsavelGuardaId = r.ResponsavelGuardaId;
        bem.Marca = AlmoxarifadoBanco.Opcional(r.Marca);
        bem.Modelo = AlmoxarifadoBanco.Opcional(r.Modelo);
        bem.NumeroSerie = AlmoxarifadoBanco.Opcional(r.NumeroSerie);
        bem.ValorAquisicao = r.ValorAquisicao;
        bem.Observacoes = AlmoxarifadoBanco.Opcional(r.Observacoes);
    }

    private static void AplicarAquisicao(BemPatrimonial bem, BemConteudoRequest r)
    {
        bem.DataAquisicaoDesconhecida = r.DataAquisicaoDesconhecida == true;
        bem.DataAquisicao = bem.DataAquisicaoDesconhecida ? null : r.DataAquisicao;
        bem.JustificativaDataDesconhecida = bem.DataAquisicaoDesconhecida ? r.JustificativaDataDesconhecida!.Trim() : null;
        bem.Origem = r.Origem!.Value;
        bem.OrigemDetalhe = AlmoxarifadoBanco.Opcional(r.OrigemDetalhe);
    }

    private static void Baixar(BemPatrimonial bem, DateOnly data, MotivoBaixa motivo, string justificativa, Guid usuarioId, DateTime agora)
    {
        bem.Situacao = SituacaoBem.Baixado;
        bem.DataBaixa = data;
        bem.MotivoBaixa = motivo;
        bem.JustificativaBaixa = justificativa;
        bem.BaixadoPorUsuarioId = usuarioId;
        bem.BaixadoEmUtc = agora;
        Tocar(bem, usuarioId, agora);
    }

    internal static void Tocar(BemPatrimonial bem, Guid usuarioId, DateTime agora)
    {
        bem.AtualizadoPorUsuarioId = usuarioId;
        bem.AtualizadoEmUtc = agora;
    }

    private Task<T> ExecutarAsync<T>(ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao, string? justificativa, Func<DateTime, Task<T>> trabalho,
        CancellationToken ct, ApiProblemException? conflito = null) =>
        AlmoxarifadoBanco.ExecutarAsync(db, logger, r, acao, justificativa, trabalho, conflito ?? ConflitoGenerico(), clock, ct);

    private async Task<BemDto> AlterarBemAsync(ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao, string? justificativa,
        Func<BemPatrimonial, DateTime, Task> trabalho, CancellationToken ct)
    {
        await ExecutarAsync(r, acao, justificativa, async agora =>
        {
            await trabalho(await BemTravadoAsync(r.Id, ct), agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return (await ObterAsync(r.Id, ct))!;
    }

    /// <summary>Trava o bem e o empréstimo (nessa ordem, a mesma das demais escritas) e encerra o empréstimo aberto.</summary>
    private async Task<EmprestimoDto> EncerrarEmprestimoAsync(ComandoAlmoxarifado r, TipoMovimentacaoAlmoxarifado acao, string? justificativa,
        Action<EmprestimoBem, BemPatrimonial, DateTime> trabalho, CancellationToken ct)
    {
        var bemId = await db.EmprestimosBens.AsNoTracking().Where(e => e.Id == r.Id).Select(e => (Guid?)e.BemId).SingleOrDefaultAsync(ct)
            ?? throw ApiProblemException.NotFound("Empréstimo não encontrado.");
        await ExecutarAsync(r, acao, justificativa, async agora =>
        {
            var bem = await BemTravadoAsync(bemId, ct);
            await AlmoxarifadoBanco.BloquearAsync(db, "patrimonio_emprestimos", r.Id, ct);
            var emprestimo = await db.EmprestimosBens.SingleAsync(e => e.Id == r.Id, ct);
            trabalho(emprestimo, bem, agora);
            emprestimo.EncerramentoRegistradoPorUsuarioId = r.UsuarioId;
            emprestimo.EncerramentoRegistradoEmUtc = agora;
            Tocar(bem, r.UsuarioId, agora);
            await db.SaveChangesAsync(ct);
            return true;
        }, ct);

        return (await ObterEmprestimoAsync(r.Id, ct))!;
    }

    private async Task<BemPatrimonial> BemTravadoAsync(Guid id, CancellationToken ct)
    {
        await AlmoxarifadoBanco.BloquearAsync(db, TabelaBens, id, ct);
        return await db.BensPatrimoniais.SingleOrDefaultAsync(b => b.Id == id, ct) ?? throw NaoEncontrado();
    }

    /// <summary>Data da movimentação mais recente do bem (encerramento, ou início se ainda aberta) de empréstimos e manutenções.</summary>
    private async Task<DateOnly?> UltimaMovimentacaoAsync(Guid bemId, CancellationToken ct)
    {
        var emprestimo = await db.EmprestimosBens.Where(e => e.BemId == bemId)
            .MaxAsync(e => (DateOnly?)(e.DataEncerramento ?? e.DataRetirada), ct);
        var manutencao = await db.ManutencoesBens.Where(m => m.BemId == bemId)
            .MaxAsync(m => (DateOnly?)(m.DataSaida ?? m.DataEntrada), ct);
        return emprestimo > manutencao || manutencao is null ? emprestimo : manutencao;
    }

    private static IQueryable<BemPatrimonial> Filtrar(IQueryable<BemPatrimonial> consulta, ListarBensQuery f)
    {
        if (!f.IncluirExcluidos)
        {
            consulta = consulta.Where(b => !b.Excluido);
        }

        if (AlmoxarifadoBanco.Opcional(f.Busca) is { } busca)
        {
            consulta = consulta.Where(b => b.Nome.Contains(busca) || b.Tombo.Contains(busca));
        }

        if (AlmoxarifadoBanco.Opcional(f.Categoria) is { } categoria)
        {
            consulta = consulta.Where(b => b.Categoria == categoria);
        }

        if (f.Conservacao is { } conservacao)
        {
            consulta = consulta.Where(b => b.EstadoConservacao == conservacao);
        }

        if (f.Situacao is { } situacao)
        {
            consulta = consulta.Where(b => b.Situacao == situacao);
        }

        if (AlmoxarifadoBanco.Opcional(f.Localizacao) is { } localizacao)
        {
            consulta = consulta.Where(b => b.Localizacao.Contains(localizacao));
        }

        if (f.PossuiValorComercial is { } possui)
        {
            consulta = consulta.Where(b => b.PossuiValorComercial == possui);
        }

        return consulta;
    }

    private async Task<List<BemDto>> MapearAsync(IReadOnlyList<BemPatrimonial> bens, CancellationToken ct)
    {
        var ids = bens.Select(b => b.Id).ToList();
        var emprestimos = (await MapearEmprestimosAsync(
            await db.EmprestimosBens.AsNoTracking().Where(e => ids.Contains(e.BemId) && e.Encerramento == null).ToListAsync(ct), ct))
            .ToDictionary(e => e.Bem.Id);
        var manutencoes = (await MapearManutencoesAsync(
            await db.ManutencoesBens.AsNoTracking().Where(m => ids.Contains(m.BemId) && m.DataSaida == null).ToListAsync(ct), ct))
            .ToDictionary(m => m.Bem.Id);
        var origens = await (
            from v in db.AtendimentosBens.AsNoTracking()
            join a in db.AtendimentosDesejos.AsNoTracking() on v.AtendimentoId equals a.Id
            join d in db.DesejosAlmoxarifado.AsNoTracking() on a.DesejoId equals d.Id
            where v.Ativo && ids.Contains(v.BemId)
            select new { v.BemId, Origem = new OrigemDesejoDto(d.Id, d.Nome, a.Id, v.CriadoPeloAtendimento) }).ToDictionaryAsync(o => o.BemId, o => o.Origem, ct);
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db, bens.SelectMany(b => new[]
        {
            b.ResponsavelGuardaId, b.CriadoPorUsuarioId, b.AtualizadoPorUsuarioId, b.BaixadoPorUsuarioId, b.ExcluidoPorUsuarioId,
        }), ct);

        return bens.Select(b => new BemDto
        {
            Id = b.Id, Tombo = b.Tombo, Nome = b.Nome, Descricao = b.Descricao, Categoria = b.Categoria, EstadoConservacao = b.EstadoConservacao,
            EstadoConservacaoDescricao = RegrasAlmoxarifado.Descricao(b.EstadoConservacao), PossuiValorComercial = b.PossuiValorComercial,
            ValorEstimado = b.ValorEstimado, DataAquisicao = b.DataAquisicao, DataAquisicaoDesconhecida = b.DataAquisicaoDesconhecida,
            JustificativaDataDesconhecida = b.JustificativaDataDesconhecida, Origem = b.Origem, OrigemDescricao = RegrasAlmoxarifado.Descricao(b.Origem),
            OrigemDetalhe = b.OrigemDetalhe, Localizacao = b.Localizacao,
            ResponsavelGuarda = b.ResponsavelGuardaId is { } g ? pessoas.GetValueOrDefault(g) : null,
            Marca = b.Marca, Modelo = b.Modelo, NumeroSerie = b.NumeroSerie, ValorAquisicao = b.ValorAquisicao, Observacoes = b.Observacoes,
            Situacao = b.Situacao, SituacaoDescricao = RegrasAlmoxarifado.Descricao(b.Situacao),
            EmprestimoAberto = emprestimos.GetValueOrDefault(b.Id), ManutencaoAberta = manutencoes.GetValueOrDefault(b.Id),
            Baixa = b.DataBaixa is { } data
                ? new BaixaDto(data, b.MotivoBaixa!.Value, RegrasAlmoxarifado.Descricao(b.MotivoBaixa.Value), b.JustificativaBaixa!,
                    AlmoxarifadoBanco.Registro(pessoas, b.BaixadoPorUsuarioId!.Value, b.BaixadoEmUtc!.Value).Por, DateTime.SpecifyKind(b.BaixadoEmUtc.Value, DateTimeKind.Utc))
                : null,
            OrigemDesejo = origens.GetValueOrDefault(b.Id),
            Excluido = b.Excluido,
            Exclusao = b.Excluido
                ? new ExclusaoDto(b.MotivoExclusao!, AlmoxarifadoBanco.Registro(pessoas, b.ExcluidoPorUsuarioId!.Value, b.ExcluidoEmUtc!.Value).Por,
                    DateTime.SpecifyKind(b.ExcluidoEmUtc.Value, DateTimeKind.Utc))
                : null,
            Criacao = AlmoxarifadoBanco.Registro(pessoas, b.CriadoPorUsuarioId, b.CriadoEmUtc),
            UltimaAlteracao = AlmoxarifadoBanco.Registro(pessoas, b.AtualizadoPorUsuarioId, b.AtualizadoEmUtc),
            Versao = EventoRegras.VersaoParaTexto(b.Versao),
        }).ToList();
    }

    private async Task<List<EmprestimoDto>> MapearEmprestimosAsync(IReadOnlyList<EmprestimoBem> emprestimos, CancellationToken ct)
    {
        var bens = await ReferenciasAsync(emprestimos.Select(e => e.BemId), ct);
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db,
            emprestimos.SelectMany(e => new[] { e.MembroId, e.RegistradoPorUsuarioId, e.EncerramentoRegistradoPorUsuarioId }), ct);
        var hoje = dataCivil.Hoje();
        return emprestimos.Select(e => new EmprestimoDto
        {
            Id = e.Id, Bem = bens[e.BemId], Membro = AlmoxarifadoBanco.Pessoa(pessoas, e.MembroId),
            DataRetirada = e.DataRetirada, PrevisaoDevolucao = e.PrevisaoDevolucao, ConservacaoSaida = e.ConservacaoSaida,
            ConservacaoSaidaDescricao = RegrasAlmoxarifado.Descricao(e.ConservacaoSaida), ObservacoesSaida = e.ObservacoesSaida, Aberto = e.Aberto,
            Atrasado = RegrasAlmoxarifado.Atrasado(e, hoje), DiasAtraso = RegrasAlmoxarifado.DiasAtraso(e, hoje), Encerramento = e.Encerramento,
            EncerramentoDescricao = RegrasAlmoxarifado.Descricao(e.Encerramento), DataEncerramento = e.DataEncerramento,
            ConservacaoRetorno = e.ConservacaoRetorno, ConservacaoRetornoDescricao = RegrasAlmoxarifado.Descricao(e.ConservacaoRetorno),
            ObservacoesRetorno = e.ObservacoesRetorno, Registro = AlmoxarifadoBanco.Registro(pessoas, e.RegistradoPorUsuarioId, e.RegistradoEmUtc),
            RegistroEncerramento = AlmoxarifadoBanco.Registro(pessoas, e.EncerramentoRegistradoPorUsuarioId, e.EncerramentoRegistradoEmUtc),
        }).ToList();
    }

    private async Task<ManutencaoDto?> ObterManutencaoAsync(Guid id, CancellationToken ct) =>
        (await MapearManutencoesAsync(await db.ManutencoesBens.AsNoTracking().Where(m => m.Id == id).ToListAsync(ct), ct)).SingleOrDefault();

    private async Task<List<ManutencaoDto>> MapearManutencoesAsync(IReadOnlyList<ManutencaoBem> manutencoes, CancellationToken ct)
    {
        var bens = await ReferenciasAsync(manutencoes.Select(m => m.BemId), ct);
        var pessoas = await AlmoxarifadoBanco.PessoasAsync(db,
            manutencoes.SelectMany(m => new[] { m.RegistradaPorUsuarioId, m.SaidaRegistradaPorUsuarioId }), ct);
        return manutencoes.Select(m => new ManutencaoDto
        {
            Id = m.Id, Bem = bens[m.BemId], DataEntrada = m.DataEntrada, Motivo = m.Motivo, ObservacoesEntrada = m.ObservacoesEntrada, Aberta = m.Aberta,
            DataSaida = m.DataSaida, ConservacaoSaida = m.ConservacaoSaida, ConservacaoSaidaDescricao = RegrasAlmoxarifado.Descricao(m.ConservacaoSaida),
            ObservacoesSaida = m.ObservacoesSaida, Registro = AlmoxarifadoBanco.Registro(pessoas, m.RegistradaPorUsuarioId, m.RegistradaEmUtc),
            RegistroSaida = AlmoxarifadoBanco.Registro(pessoas, m.SaidaRegistradaPorUsuarioId, m.SaidaRegistradaEmUtc),
        }).ToList();
    }

    private async Task<Dictionary<Guid, BemReferenciaDto>> ReferenciasAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var distintos = ids.Distinct().ToList();
        return await db.BensPatrimoniais.AsNoTracking().Where(b => distintos.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => new BemReferenciaDto(b.Id, b.Tombo, b.Nome), ct);
    }

    private static ApiProblemException NaoEncontrado() => ApiProblemException.NotFound("Bem não encontrado.");

    private static ApiProblemException ConflitoEmprestimoAberto() =>
        ApiProblemException.Conflict("O bem já tem empréstimo aberto.", "Outra operação emprestou este bem ao mesmo tempo; consulte o bem novamente.");

    private static ApiProblemException ConflitoGenerico() =>
        ApiProblemException.Conflict("Conflito de operação simultânea.", "Outra operação alterou os mesmos dados. Consulte novamente e repita.");
}
