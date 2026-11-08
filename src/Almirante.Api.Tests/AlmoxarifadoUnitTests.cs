using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Services;
using Almirante.Api.Validation.Almoxarifado;
using FluentValidation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Almirante.Api.Tests;

/// <summary>Issue #126 sem banco: regras de domínio, CSV, validações, enums, permissões e documentação do almoxarifado.</summary>
public sealed class AlmoxarifadoUnitTests
{
    private static readonly DateOnly Hoje = new(2026, 10, 20);

    private static BemPatrimonial Bem(SituacaoBem situacao = SituacaoBem.Disponivel, EstadoConservacao conservacao = EstadoConservacao.Bom,
        bool excluido = false, DateOnly? aquisicao = null) => new()
    {
        Id = Guid.NewGuid(), Nome = "Barraca", Descricao = "Barraca", Localizacao = "Almoxarifado", Situacao = situacao, EstadoConservacao = conservacao,
        Excluido = excluido, DataAquisicao = aquisicao ?? Hoje.AddDays(-30),
    };

    private static DesejoAlmoxarifado Desejo(int desejada = 5, int atendida = 0, TipoItemDesejo tipo = TipoItemDesejo.Patrimonial,
        SituacaoDesejo? situacao = null, bool excluido = false) => new()
    {
        Nome = "Barraca", Descricao = "d", Justificativa = "j", QuantidadeDesejada = desejada, QuantidadeAtendida = atendida, TipoItem = tipo,
        Situacao = situacao ?? RegrasAlmoxarifado.SituacaoDesejo(desejada, atendida, false), Excluido = excluido,
    };

    private static EmprestimoBem Emprestimo(DateOnly retirada, DateOnly previsao, EncerramentoEmprestimo? encerramento = null) => new()
    {
        DataRetirada = retirada, PrevisaoDevolucao = previsao, Encerramento = encerramento,
    };

    private static ApiProblemException Falha(Action acao, int status, string? titulo = null)
    {
        var erro = Assert.Throws<ApiProblemException>(acao);
        Assert.Equal(status, erro.Status);
        if (titulo is not null)
        {
            Assert.Equal(titulo, erro.Title);
        }

        return erro;
    }

    private static void Invalido(Action acao, string campo) =>
        Assert.True(Falha(acao, StatusCodes.Status400BadRequest).Errors!.ContainsKey(campo), $"campo {campo} esperado");

    [Fact]
    public void Enums_TemValoresEstaveisEDescricoes()
    {
        Assert.Equal([(1, "Novo"), (2, "Bom"), (3, "Regular"), (4, "Ruim"), (5, "Inservível")],
            RegrasAlmoxarifado.Itens<EstadoConservacao>().Select(i => (i.Valor, i.Descricao)));
        Assert.Equal([(1, "PoucoUrgenteMasNecessario", "Pouco urgente, mas necessário"), (2, "Urgente", "Urgente"), (3, "MuitoUrgente", "Muito urgente")],
            RegrasAlmoxarifado.Itens<NivelUrgenciaAlmoxarifado>().Select(i => (i.Valor, i.Codigo, i.Descricao)));
        Assert.Equal([1, 2, 3, 4], Enum.GetValues<SituacaoBem>().Select(v => (int)v));
        Assert.Equal(["Disponível", "Emprestado", "Em manutenção", "Baixado"], RegrasAlmoxarifado.Itens<SituacaoBem>().Select(i => i.Descricao));
        Assert.Equal(["Pendente", "Parcialmente atendida", "Atendida", "Cancelada"], RegrasAlmoxarifado.Itens<SituacaoDesejo>().Select(i => i.Descricao));
        Assert.Equal(["Compra", "Doação", "Outro"], RegrasAlmoxarifado.Itens<OrigemBem>().Select(i => i.Descricao));
        Assert.Equal(["Perda", "Furto", "Descarte", "Doação", "Outro"], RegrasAlmoxarifado.Itens<MotivoBaixa>().Select(i => i.Descricao));
        Assert.Equal(19, RegrasAlmoxarifado.Itens<TipoMovimentacaoAlmoxarifado>().Count);
        Assert.All(new[]
        {
            RegrasAlmoxarifado.Itens<EncerramentoEmprestimo>(), RegrasAlmoxarifado.Itens<TipoItemDesejo>(), RegrasAlmoxarifado.Itens<OrigemAtendimento>(),
            RegrasAlmoxarifado.Itens<TipoMovimentacaoAlmoxarifado>(),
        }, lista => Assert.All(lista, i => Assert.False(string.IsNullOrWhiteSpace(i.Descricao))));
        Assert.Equal("Inservível", RegrasAlmoxarifado.Descricao((EstadoConservacao?)EstadoConservacao.Inservivel));
        Assert.Null(RegrasAlmoxarifado.Descricao((EstadoConservacao?)null));
        Assert.Equal("99", RegrasAlmoxarifado.Descricao((EstadoConservacao)99));
    }

    [Fact]
    public void Tipo_ConverteSoAcoesConhecidas()
    {
        Assert.Equal(TipoMovimentacaoAlmoxarifado.Emprestimo, RegrasAlmoxarifado.Tipo("Emprestimo"));
        Assert.Null(RegrasAlmoxarifado.Tipo("ATUALIZAR"));
        Assert.Null(RegrasAlmoxarifado.Tipo("emprestimo"));
        Assert.Null(RegrasAlmoxarifado.Tipo("99"));
        Assert.Null(RegrasAlmoxarifado.Tipo(null));
    }

    [Theory]
    [InlineData(1, "PAT-000001")]
    [InlineData(999999, "PAT-999999")]
    [InlineData(1000000, "PAT-1000000")]
    public void Tombo_FormataComSeisDigitosSemTruncar(int numero, string esperado) => Assert.Equal(esperado, RegrasAlmoxarifado.Tombo(numero));

    [Theory]
    [InlineData(5, 0, false, SituacaoDesejo.Pendente)]
    [InlineData(5, 2, false, SituacaoDesejo.ParcialmenteAtendida)]
    [InlineData(5, 5, false, SituacaoDesejo.Atendida)]
    [InlineData(5, 2, true, SituacaoDesejo.Cancelada)]
    [InlineData(5, 0, true, SituacaoDesejo.Cancelada)]
    public void SituacaoDesejo_DecorreDasQuantidades(int desejada, int atendida, bool cancelada, SituacaoDesejo esperada)
    {
        Assert.Equal(esperada, RegrasAlmoxarifado.SituacaoDesejo(desejada, atendida, cancelada));
        Assert.Equal(esperada is SituacaoDesejo.Pendente or SituacaoDesejo.ParcialmenteAtendida, RegrasAlmoxarifado.Aberta(esperada));
    }

    [Fact]
    public void QuantidadesECusto_PendenteNuncaNegativa_ECustoDesconhecidoNaoEZero()
    {
        Assert.Equal((3, 0), (RegrasAlmoxarifado.Pendente(5, 2), RegrasAlmoxarifado.Pendente(5, 7)));
        Assert.Equal(1140m, RegrasAlmoxarifado.Custo(380m, 3));
        Assert.Equal(0m, RegrasAlmoxarifado.Custo(0m, 3));
        Assert.Null(RegrasAlmoxarifado.Custo(null, 3));
    }

    [Fact]
    public void Atraso_CalculadoPelaPrevisaoESemDevolucao()
    {
        var atrasado = Emprestimo(Hoje.AddDays(-10), Hoje.AddDays(-4));
        Assert.Equal((true, 4), (RegrasAlmoxarifado.Atrasado(atrasado, Hoje), RegrasAlmoxarifado.DiasAtraso(atrasado, Hoje)));
        var noPrazo = Emprestimo(Hoje.AddDays(-2), Hoje);
        Assert.Equal((false, 0), (RegrasAlmoxarifado.Atrasado(noPrazo, Hoje), RegrasAlmoxarifado.DiasAtraso(noPrazo, Hoje)));
        var devolvido = Emprestimo(Hoje.AddDays(-10), Hoje.AddDays(-4), EncerramentoEmprestimo.Devolvido);
        Assert.Equal((false, 0), (RegrasAlmoxarifado.Atrasado(devolvido, Hoje), RegrasAlmoxarifado.DiasAtraso(devolvido, Hoje)));
    }

    [Fact]
    public void Datas_NaoFuturas_PosterioresAAquisicaoEAUltimaMovimentacao()
    {
        RegrasAlmoxarifado.ConferirNaoFutura("data", Hoje, Hoje);
        RegrasAlmoxarifado.ConferirNaoFutura("data", null, Hoje);
        Invalido(() => RegrasAlmoxarifado.ConferirNaoFutura("data", Hoje.AddDays(1), Hoje), "data");

        RegrasAlmoxarifado.ConferirPosterior("data", Hoje, Hoje, Hoje);
        RegrasAlmoxarifado.ConferirPosterior("data", Hoje, null, null);
        Invalido(() => RegrasAlmoxarifado.ConferirPosterior("data", Hoje, Hoje.AddDays(1), null), "data");
        Assert.Contains("última movimentação",
            Falha(() => RegrasAlmoxarifado.ConferirPosterior("data", Hoje, Hoje.AddDays(-5), Hoje.AddDays(1)), 400).Errors!["data"][0]);

        RegrasAlmoxarifado.ConferirDatasEmprestimo(Hoje.AddDays(-1), Hoje.AddDays(-1), Hoje);
        Invalido(() => RegrasAlmoxarifado.ConferirDatasEmprestimo(Hoje.AddDays(1), Hoje.AddDays(2), Hoje), "dataRetirada");
        Invalido(() => RegrasAlmoxarifado.ConferirDatasEmprestimo(Hoje, Hoje.AddDays(-1), Hoje), "previsaoDevolucao");
    }

    [Fact]
    public void Emprestimo_SoDeBemDisponivelEUtilizavel()
    {
        RegrasAlmoxarifado.ConferirEmprestimo(Bem(), EstadoConservacao.Ruim);
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(SituacaoBem.Emprestado), EstadoConservacao.Bom), 409, "O bem já está emprestado.");
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(SituacaoBem.EmManutencao), EstadoConservacao.Bom), 409, "O bem está em manutenção.");
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(SituacaoBem.Baixado), EstadoConservacao.Bom), 409, "Bem baixado.");
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(excluido: true), EstadoConservacao.Bom), 409, "Bem excluído.");
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(conservacao: EstadoConservacao.Inservivel), EstadoConservacao.Bom), 409, "Bem inservível.");
        Falha(() => RegrasAlmoxarifado.ConferirEmprestimo(Bem(), EstadoConservacao.Inservivel), 409, "Bem inservível.");
    }

    [Fact]
    public void Encerramento_SoDeEmprestimoAberto_EntreARetiradaEHoje()
    {
        var aberto = Emprestimo(Hoje.AddDays(-3), Hoje);
        RegrasAlmoxarifado.ConferirEncerramento(aberto, "dataDevolucao", Hoje.AddDays(-3), Hoje);
        RegrasAlmoxarifado.ConferirEncerramento(aberto, "dataDevolucao", Hoje, Hoje);
        Invalido(() => RegrasAlmoxarifado.ConferirEncerramento(aberto, "dataDevolucao", Hoje.AddDays(-4), Hoje), "dataDevolucao");
        Invalido(() => RegrasAlmoxarifado.ConferirEncerramento(aberto, "dataDevolucao", Hoje.AddDays(1), Hoje), "dataDevolucao");
        Falha(() => RegrasAlmoxarifado.ConferirEncerramento(Emprestimo(Hoje.AddDays(-3), Hoje, EncerramentoEmprestimo.Devolvido), "data", Hoje, Hoje),
            409, "Empréstimo já encerrado.");
        Falha(() => RegrasAlmoxarifado.ConferirEncerramento(Emprestimo(Hoje.AddDays(-3), Hoje, EncerramentoEmprestimo.Furto), "data", Hoje, Hoje),
            409, "Empréstimo já encerrado.");
    }

    [Fact]
    public void Manutencao_EntradaSoDeBemDisponivel_SaidaSoDeManutencaoAberta()
    {
        RegrasAlmoxarifado.ConferirEntradaManutencao(Bem(conservacao: EstadoConservacao.Inservivel), Hoje, Hoje);
        Falha(() => RegrasAlmoxarifado.ConferirEntradaManutencao(Bem(SituacaoBem.Emprestado), Hoje, Hoje), 409, "O bem está emprestado.");
        Falha(() => RegrasAlmoxarifado.ConferirEntradaManutencao(Bem(SituacaoBem.EmManutencao), Hoje, Hoje), 409, "O bem já está em manutenção.");
        Falha(() => RegrasAlmoxarifado.ConferirEntradaManutencao(Bem(SituacaoBem.Baixado), Hoje, Hoje), 409, "Bem baixado.");
        Invalido(() => RegrasAlmoxarifado.ConferirEntradaManutencao(Bem(), Hoje.AddDays(1), Hoje), "dataEntrada");

        var aberta = new ManutencaoBem { Motivo = "m", DataEntrada = Hoje.AddDays(-2) };
        RegrasAlmoxarifado.ConferirSaidaManutencao(aberta, Hoje.AddDays(-2), Hoje);
        Invalido(() => RegrasAlmoxarifado.ConferirSaidaManutencao(aberta, Hoje.AddDays(-3), Hoje), "dataSaida");
        Invalido(() => RegrasAlmoxarifado.ConferirSaidaManutencao(aberta, Hoje.AddDays(1), Hoje), "dataSaida");
        Falha(() => RegrasAlmoxarifado.ConferirSaidaManutencao(new ManutencaoBem { Motivo = "m", DataEntrada = Hoje, DataSaida = Hoje }, Hoje, Hoje),
            409, "Manutenção já encerrada.");
    }

    [Fact]
    public void Baixa_ExigeEmprestimoEncerrado_EAceitaBemEmManutencao()
    {
        RegrasAlmoxarifado.ConferirBaixa(Bem(), Hoje, Hoje);
        RegrasAlmoxarifado.ConferirBaixa(Bem(SituacaoBem.EmManutencao), Hoje, Hoje);
        RegrasAlmoxarifado.ConferirBaixa(Bem(conservacao: EstadoConservacao.Inservivel), Hoje, Hoje);
        Assert.Contains("perda/furto", Falha(() => RegrasAlmoxarifado.ConferirBaixa(Bem(SituacaoBem.Emprestado), Hoje, Hoje), 409).Detail);
        Falha(() => RegrasAlmoxarifado.ConferirBaixa(Bem(SituacaoBem.Baixado), Hoje, Hoje), 409, "Bem baixado.");
        Falha(() => RegrasAlmoxarifado.ConferirBaixa(Bem(excluido: true), Hoje, Hoje), 409, "Bem excluído.");
        Invalido(() => RegrasAlmoxarifado.ConferirBaixa(Bem(), Hoje.AddDays(1), Hoje), "dataBaixa");
    }

    [Fact]
    public void Exclusao_SoParaCadastroIndevido()
    {
        RegrasAlmoxarifado.ConferirExclusao(Bem(), teveMovimentacoes: false, vinculadoAAtendimento: false);
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(excluido: true), false, false), 409, "Bem já excluído.");
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(SituacaoBem.Emprestado), true, false), 409, "O bem tem empréstimo aberto.");
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(), true, false), 409, "O bem já teve movimentações.");
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(SituacaoBem.EmManutencao), false, false), 409, "O bem já teve movimentações.");
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(SituacaoBem.Baixado), false, false), 409, "O bem já teve movimentações.");
        Falha(() => RegrasAlmoxarifado.ConferirExclusao(Bem(), false, true), 409, "O bem foi recebido em um atendimento da lista de desejos.");
    }

    [Fact]
    public void Alteracao_BloqueiaBemExcluidoOuBaixado()
    {
        RegrasAlmoxarifado.ConferirAlteracao(Bem(SituacaoBem.Emprestado));
        RegrasAlmoxarifado.ConferirAlteracao(Bem(SituacaoBem.EmManutencao));
        Falha(() => RegrasAlmoxarifado.ConferirAlteracao(Bem(excluido: true)), 409, "Bem excluído.");
        Falha(() => RegrasAlmoxarifado.ConferirAlteracao(Bem(SituacaoBem.Baixado)), 409, "Bem baixado.");
    }

    [Fact]
    public void AtualizacaoDoDesejo_NaoReduzAbaixoDoAtendido_NemTrocaOTipoDepoisDeAtendimentos()
    {
        RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(5, 2), 2, TipoItemDesejo.Patrimonial);
        RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(5, 2), 9, TipoItemDesejo.Patrimonial);
        RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(5, 0), 1, TipoItemDesejo.Consumivel);
        Falha(() => RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(5, 2), 1, TipoItemDesejo.Patrimonial), 409, "Quantidade desejada menor que a atendida.");
        Falha(() => RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(5, 2), 5, TipoItemDesejo.Consumivel), 409, "Tipo do item não pode mudar.");
        Falha(() => RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(excluido: true), 5, TipoItemDesejo.Patrimonial), 409, "Necessidade excluída.");
        Falha(() => RegrasAlmoxarifado.ConferirAtualizacaoDesejo(Desejo(situacao: SituacaoDesejo.Cancelada), 5, TipoItemDesejo.Patrimonial), 409,
            "Necessidade cancelada.");
    }

    [Fact]
    public void Atendimento_RespeitaPendente_BensFisicosESituacao()
    {
        RegrasAlmoxarifado.ConferirAtendimento(Desejo(5, 0), 2, Hoje, 2, Hoje);
        RegrasAlmoxarifado.ConferirAtendimento(Desejo(5, 2), 3, Hoje, 3, Hoje);
        RegrasAlmoxarifado.ConferirAtendimento(Desejo(10, 0, TipoItemDesejo.Consumivel), 10, Hoje, 0, Hoje);
        Falha(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(5, 5), 1, Hoje, 1, Hoje), 409, "Necessidade já atendida.");
        Assert.Contains("Restam 3", Falha(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(5, 2), 4, Hoje, 4, Hoje), 409,
            "Quantidade superior à pendente.").Detail);
        Invalido(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(), 1, Hoje.AddDays(1), 1, Hoje), "data");
        Invalido(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(), 2, Hoje, 1, Hoje), "bens");
        Invalido(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(), 2, Hoje, 3, Hoje), "bens");
        Invalido(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(tipo: TipoItemDesejo.Consumivel), 1, Hoje, 1, Hoje), "bens");
        Falha(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(situacao: SituacaoDesejo.Cancelada), 1, Hoje, 1, Hoje), 409, "Necessidade cancelada.");
        Falha(() => RegrasAlmoxarifado.ConferirAtendimento(Desejo(excluido: true), 1, Hoje, 1, Hoje), 409, "Necessidade excluída.");
    }

    [Fact]
    public void CancelamentoExclusaoEEstorno_DoDesejo()
    {
        RegrasAlmoxarifado.ConferirCancelamento(Desejo(5, 0));
        RegrasAlmoxarifado.ConferirCancelamento(Desejo(5, 3));
        Falha(() => RegrasAlmoxarifado.ConferirCancelamento(Desejo(5, 5)), 409, "Necessidade já atendida.");
        Falha(() => RegrasAlmoxarifado.ConferirCancelamento(Desejo(situacao: SituacaoDesejo.Cancelada)), 409, "Necessidade cancelada.");
        RegrasAlmoxarifado.ConferirExclusaoDesejo(Desejo(situacao: SituacaoDesejo.Cancelada));
        Falha(() => RegrasAlmoxarifado.ConferirExclusaoDesejo(Desejo(excluido: true)), 409, "Necessidade já excluída.");

        var atendimento = new AtendimentoDesejo { IdempotencyKey = "k", RequestHash = "h" };
        RegrasAlmoxarifado.ConferirEstorno(atendimento, [Bem()], false);
        RegrasAlmoxarifado.ConferirEstorno(atendimento, [], false);
        Falha(() => RegrasAlmoxarifado.ConferirEstorno(atendimento, [Bem()], true), 409, "Bens criados pelo atendimento já foram movimentados.");
        Falha(() => RegrasAlmoxarifado.ConferirEstorno(atendimento, [Bem(SituacaoBem.Emprestado)], false), 409);
        Falha(() => RegrasAlmoxarifado.ConferirEstorno(atendimento, [Bem(excluido: true)], false), 409);
        atendimento.EstornadoEmUtc = DateTime.UtcNow;
        Falha(() => RegrasAlmoxarifado.ConferirEstorno(atendimento, [], false), 409, "Atendimento já estornado.");
    }

    [Fact]
    public void VotacaoERevogacao_UmaUnicaVez()
    {
        var inventario = new InventarioPatrimonio { DataInventario = Hoje.AddDays(-5) };
        RegrasAlmoxarifado.ConferirVotacao(inventario, Hoje.AddDays(-5), Hoje);
        Invalido(() => RegrasAlmoxarifado.ConferirVotacao(inventario, Hoje.AddDays(-6), Hoje), "dataVotacao");
        Invalido(() => RegrasAlmoxarifado.ConferirVotacao(inventario, Hoje.AddDays(1), Hoje), "dataVotacao");
        inventario.VotacaoRegistradaEmUtc = DateTime.UtcNow;
        Falha(() => RegrasAlmoxarifado.ConferirVotacao(inventario, Hoje, Hoje), 409, "Votação já registrada.");

        var designacao = new ResponsavelAlmoxarifado();
        RegrasAlmoxarifado.ConferirRevogacao(designacao);
        designacao.RevogadoEmUtc = DateTime.UtcNow;
        Falha(() => RegrasAlmoxarifado.ConferirRevogacao(designacao), 409, "Designação já revogada.");
    }

    [Fact]
    public void Periodo_IntervaloUtcInclusivoNosDias()
    {
        Assert.Equal((new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 3, 0, 0, 0, DateTimeKind.Utc)),
            RegrasAlmoxarifado.Periodo(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 2)));
        Assert.Equal(((DateTime?)null, (DateTime?)null), RegrasAlmoxarifado.Periodo(null, null));
    }

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("Barraca", "Barraca")]
    [InlineData("=SOMA(A1)", "'=SOMA(A1)")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@cmd", "'@cmd")]
    [InlineData("\tx", "'\tx")]
    [InlineData("a;b", "\"a;b\"")]
    [InlineData("diz \"oi\"", "\"diz \"\"oi\"\"\"")]
    [InlineData("linha\nnova", "\"linha\nnova\"")]
    [InlineData("\rx", "\"'\rx\"")]
    public void Csv_EscapaSeparadorAspasQuebrasEFormulas(string? valor, string esperado) => Assert.Equal(esperado, CsvAlmoxarifado.Campo(valor));

    [Fact]
    public void Csv_FormatosPtBrEUtf8ComBom()
    {
        Assert.Equal(("1234,50", "", "2026-10-20", "", "Sim", "Não"), (CsvAlmoxarifado.Valor(1234.5m), CsvAlmoxarifado.Valor(null),
            CsvAlmoxarifado.Data(Hoje), CsvAlmoxarifado.Data(null), CsvAlmoxarifado.SimNao(true), CsvAlmoxarifado.SimNao(false)));
        var bytes = CsvAlmoxarifado.Gerar(["A", "B;C"], [["1", null], ["=x", "ç"]]);
        Assert.Equal(Encoding.UTF8.GetPreamble(), bytes[..3]);
        Assert.Equal("A;\"B;C\"\r\n1;\r\n'=x;ç\r\n", Encoding.UTF8.GetString(bytes[3..]));
        Assert.Equal("text/csv; charset=utf-8", CsvAlmoxarifado.ContentType);
    }

    private static T Bem<T>(Action<T>? ajuste = null) where T : BemDadosRequest, new()
    {
        var r = new T
        {
            Nome = "Barraca", Descricao = "Barraca iglu", Categoria = "Acampamento", EstadoConservacao = EstadoConservacao.Bom, PossuiValorComercial = true,
            ValorEstimado = 100m, Localizacao = "Almoxarifado",
        };
        if (r is BemConteudoRequest conteudo)
        {
            conteudo.DataAquisicao = Hoje;
            conteudo.Origem = OrigemBem.Compra;
        }

        ajuste?.Invoke(r);
        return r;
    }

    private static IReadOnlyList<string> Erros<T>(IValidator<T> validator, T request) =>
        validator.Validate(request).Errors.Select(e => e.ErrorMessage).ToList();

    [Fact]
    public void BemValidator_AceitaCadastroValido_ComESemValorComercialEAcervoSemData()
    {
        var cadastrar = new CadastrarBemRequestValidator();
        Assert.Empty(Erros(cadastrar, Bem<CadastrarBemRequest>()));
        Assert.Empty(Erros(cadastrar, Bem<CadastrarBemRequest>(r => { r.PossuiValorComercial = false; r.ValorEstimado = null; })));
        Assert.Empty(Erros(cadastrar, Bem<CadastrarBemRequest>(r =>
        {
            r.DataAquisicao = null;
            r.DataAquisicaoDesconhecida = true;
            r.JustificativaDataDesconhecida = "Acervo antigo";
        })));
        Assert.Empty(Erros(cadastrar, Bem<CadastrarBemRequest>(r => { r.Origem = OrigemBem.Outro; r.OrigemDetalhe = "Premiação"; })));
        Assert.Empty(Erros(cadastrar, Bem<CadastrarBemRequest>(r => { r.OrigemDetalhe = "Loja X"; r.ValorAquisicao = 0m; r.ValorEstimado = 0m; })));
        Assert.Empty(Erros(new NovoBemAtendimentoRequestValidator(), Bem<NovoBemAtendimentoRequest>()));
    }

    public static TheoryData<string, string> BemInvalido => new()
    {
        { "nome", "nome é obrigatório." },
        { "nomeLongo", "nome deve ter no máximo 150 caracteres." },
        { "nomeEmoji", "nome contém caracteres não suportados" },
        { "descricao", "descricao é obrigatório." },
        { "categoria", "categoria deve ter no máximo 60 caracteres." },
        { "conservacao", "estadoConservacao é obrigatório." },
        { "conservacaoForaDoEnum", "estadoConservacao deve ser um destes valores" },
        { "possui", "possuiValorComercial é obrigatório." },
        { "semEstimativa", "valorEstimado é obrigatório quando o bem possui valor comercial." },
        { "estimativaSemValor", "valorEstimado deve ser nulo quando o bem não possui valor comercial." },
        { "estimativaNegativa", "valorEstimado deve ser maior ou igual a zero" },
        { "aquisicaoNegativa", "valorAquisicao deve ser maior ou igual a zero" },
        { "localizacao", "localizacao é obrigatório." },
        { "guardaVazia", "responsavelGuardaId não pode ser um GUID vazio." },
        { "marca", "marca deve ter no máximo 100 caracteres." },
        { "observacoes", "observacoes deve ter no máximo 1000 caracteres." },
        { "semData", "dataAquisicao é obrigatória (yyyy-MM-dd)." },
        { "dataComDesconhecida", "dataAquisicao deve ser nula quando dataAquisicaoDesconhecida = true" },
        { "justificativaSemDesconhecida", "justificativaDataDesconhecida só é aceita com dataAquisicaoDesconhecida = true." },
        { "desconhecidaSemJustificativa", "justificativaDataDesconhecida é obrigatório." },
        { "origem", "origem é obrigatório." },
        { "outroSemDetalhe", "origemDetalhe é obrigatório." },
        { "detalheLongo", "origemDetalhe deve ter no máximo 300 caracteres." },
    };

    [Theory]
    [MemberData(nameof(BemInvalido))]
    public void BemValidator_RecusaCadaRegra(string caso, string mensagem)
    {
        var request = Bem<CadastrarBemRequest>(r =>
        {
            switch (caso)
            {
                case "nome": r.Nome = " "; break;
                case "nomeLongo": r.Nome = new string('a', 151); break;
                case "nomeEmoji": r.Nome = "Barraca 😀"; break;
                case "descricao": r.Descricao = null; break;
                case "categoria": r.Categoria = new string('c', 61); break;
                case "conservacao": r.EstadoConservacao = null; break;
                case "conservacaoForaDoEnum": r.EstadoConservacao = (EstadoConservacao)9; break;
                case "possui": r.PossuiValorComercial = null; break;
                case "semEstimativa": r.ValorEstimado = null; break;
                case "estimativaSemValor": r.PossuiValorComercial = false; break;
                case "estimativaNegativa": r.ValorEstimado = -1m; break;
                case "aquisicaoNegativa": r.ValorAquisicao = -1m; break;
                case "localizacao": r.Localizacao = ""; break;
                case "guardaVazia": r.ResponsavelGuardaId = Guid.Empty; break;
                case "marca": r.Marca = new string('m', 101); break;
                case "observacoes": r.Observacoes = new string('o', 1001); break;
                case "semData": r.DataAquisicao = null; break;
                case "dataComDesconhecida": r.DataAquisicaoDesconhecida = true; r.JustificativaDataDesconhecida = "x"; break;
                case "justificativaSemDesconhecida": r.JustificativaDataDesconhecida = "x"; break;
                case "desconhecidaSemJustificativa": r.DataAquisicao = null; r.DataAquisicaoDesconhecida = true; break;
                case "origem": r.Origem = null; break;
                case "outroSemDetalhe": r.Origem = OrigemBem.Outro; break;
                case "detalheLongo": r.OrigemDetalhe = new string('d', 301); break;
            }
        });
        Assert.Contains(Erros(new CadastrarBemRequestValidator(), request), e => e.StartsWith(mensagem, StringComparison.Ordinal));
    }

    [Fact]
    public void AtualizacaoEExclusaoDoBem_ExigemVersaoEJustificativa()
    {
        Assert.Contains("versao é obrigatória e deve ser a versão devolvida pela API.", Erros(new AtualizarBemRequestValidator(), Bem<AtualizarBemRequest>()));
        Assert.Empty(Erros(new AtualizarBemRequestValidator(), Bem<AtualizarBemRequest>(r => r.Versao = "AAAAAAAAB9E=")));
        var excluir = new ExcluirBemRequestValidator();
        Assert.Empty(Erros(excluir, new ExcluirBemRequest { Justificativa = "duplicado", Versao = "AAAAAAAAB9E=" }));
        Assert.Contains("justificativa é obrigatório.", Erros(excluir, new ExcluirBemRequest { Versao = "AAAAAAAAB9E=" }));
        Assert.Contains("justificativa deve ter no máximo 255 caracteres.", Erros(excluir, new ExcluirBemRequest { Justificativa = new string('j', 256), Versao = "AAAAAAAAB9E=" }));
        Assert.Contains("versao é obrigatória e deve ser a versão devolvida pela API.", Erros(excluir, new ExcluirBemRequest { Justificativa = "x", Versao = "curta" }));
    }

    [Fact]
    public void MovimentacoesValidator_ExigemCamposObrigatoriosEEnums()
    {
        var emprestar = new EmprestarBemRequestValidator();
        Assert.Empty(Erros(emprestar, new EmprestarBemRequest { MembroId = Guid.NewGuid(), DataRetirada = Hoje, PrevisaoDevolucao = Hoje, ConservacaoSaida = EstadoConservacao.Bom }));
        Assert.Equal(["membroId é obrigatório (GUID não vazio).", "dataRetirada é obrigatória (yyyy-MM-dd).", "previsaoDevolucao é obrigatória (yyyy-MM-dd).",
            "conservacaoSaida é obrigatório."], Erros(emprestar, new EmprestarBemRequest { MembroId = Guid.Empty }));

        var devolver = new DevolverEmprestimoRequestValidator();
        Assert.Empty(Erros(devolver, new DevolverEmprestimoRequest { DataDevolucao = Hoje, ConservacaoRetorno = EstadoConservacao.Ruim }));
        Assert.Empty(Erros(devolver, new DevolverEmprestimoRequest
        {
            DataDevolucao = Hoje, ConservacaoRetorno = EstadoConservacao.Ruim, EncaminharParaManutencao = true, MotivoManutencao = "zíper",
        }));
        Assert.Contains("motivoManutencao é obrigatório.", Erros(devolver, new DevolverEmprestimoRequest
        {
            DataDevolucao = Hoje, ConservacaoRetorno = EstadoConservacao.Ruim, EncaminharParaManutencao = true,
        }));
        Assert.Contains("motivoManutencao só é aceito com encaminharParaManutencao = true.", Erros(devolver, new DevolverEmprestimoRequest
        {
            DataDevolucao = Hoje, ConservacaoRetorno = EstadoConservacao.Ruim, MotivoManutencao = "x",
        }));
        Assert.Equal(["dataDevolucao é obrigatória (yyyy-MM-dd).", "conservacaoRetorno é obrigatório."], Erros(devolver, new DevolverEmprestimoRequest()));

        var perda = new RegistrarPerdaFurtoRequestValidator();
        Assert.Empty(Erros(perda, new RegistrarPerdaFurtoRequest { Data = Hoje, Motivo = EncerramentoEmprestimo.Perda, Justificativa = "x" }));
        Assert.Empty(Erros(perda, new RegistrarPerdaFurtoRequest { Data = Hoje, Motivo = EncerramentoEmprestimo.Furto, Justificativa = "x" }));
        Assert.Contains("motivo deve ser Perda ou Furto.", Erros(perda, new RegistrarPerdaFurtoRequest { Data = Hoje, Motivo = EncerramentoEmprestimo.Devolvido, Justificativa = "x" }));
        Assert.Equal(["data é obrigatória (yyyy-MM-dd).", "motivo deve ser Perda ou Furto.", "justificativa é obrigatório."], Erros(perda, new RegistrarPerdaFurtoRequest()));

        Assert.Empty(Erros(new EntradaManutencaoRequestValidator(), new EntradaManutencaoRequest { DataEntrada = Hoje, Motivo = "m" }));
        Assert.Equal(["dataEntrada é obrigatória (yyyy-MM-dd).", "motivo é obrigatório."], Erros(new EntradaManutencaoRequestValidator(), new EntradaManutencaoRequest()));
        Assert.Empty(Erros(new SaidaManutencaoRequestValidator(), new SaidaManutencaoRequest { DataSaida = Hoje, ConservacaoSaida = EstadoConservacao.Novo }));
        Assert.Equal(["dataSaida é obrigatória (yyyy-MM-dd).", "conservacaoSaida deve ser um destes valores: Novo, Bom, Regular, Ruim, Inservivel."],
            Erros(new SaidaManutencaoRequestValidator(), new SaidaManutencaoRequest { ConservacaoSaida = (EstadoConservacao)0 }));
        Assert.Empty(Erros(new BaixarBemRequestValidator(), new BaixarBemRequest { DataBaixa = Hoje, Motivo = MotivoBaixa.Descarte, Justificativa = "x" }));
        Assert.Equal(["dataBaixa é obrigatória (yyyy-MM-dd).", "motivo é obrigatório.", "justificativa é obrigatório."],
            Erros(new BaixarBemRequestValidator(), new BaixarBemRequest()));
    }

    [Fact]
    public void InventarioValidator_ExigeDataResponsavelEReferenciaDaAta()
    {
        Assert.Empty(Erros(new FecharInventarioRequestValidator(), new FecharInventarioRequest { DataInventario = Hoje, ResponsavelId = Guid.NewGuid() }));
        Assert.Equal(["dataInventario é obrigatória (yyyy-MM-dd).", "responsavelId é obrigatório (GUID não vazio)."],
            Erros(new FecharInventarioRequestValidator(), new FecharInventarioRequest()));
        var votacao = new RegistrarVotacaoInventarioRequestValidator();
        Assert.Empty(Erros(votacao, new RegistrarVotacaoInventarioRequest { DataVotacao = Hoje, ReferenciaAta = "Ata 1", Versao = "AAAAAAAAB9E=" }));
        Assert.Equal(["dataVotacao é obrigatória (yyyy-MM-dd).", "referenciaAta é obrigatório.", "versao é obrigatória e deve ser a versão devolvida pela API."],
            Erros(votacao, new RegistrarVotacaoInventarioRequest()));
        Assert.Contains("referenciaAta deve ter no máximo 100 caracteres.",
            Erros(votacao, new RegistrarVotacaoInventarioRequest { DataVotacao = Hoje, ReferenciaAta = new string('a', 101), Versao = "AAAAAAAAB9E=" }));
    }

    private static CadastrarDesejoRequest DesejoRequest(Action<CadastrarDesejoRequest>? ajuste = null)
    {
        var r = new CadastrarDesejoRequest
        {
            Nome = "Barraca", Descricao = "Barracas", Justificativa = "Acampamento", TipoItem = TipoItemDesejo.Patrimonial, QuantidadeDesejada = 5,
            Urgencia = NivelUrgenciaAlmoxarifado.Urgente,
        };
        ajuste?.Invoke(r);
        return r;
    }

    [Theory]
    [InlineData("valido", null)]
    [InlineData("http", null)]
    [InlineData("semUrgencia", "urgencia é obrigatório.")]
    [InlineData("urgenciaForaDoEnum", "urgencia deve ser um destes valores: PoucoUrgenteMasNecessario, Urgente, MuitoUrgente.")]
    [InlineData("semTipo", "tipoItem é obrigatório.")]
    [InlineData("quantidadeZero", "quantidadeDesejada deve ser um inteiro entre 1 e 10000.")]
    [InlineData("quantidadeAlta", "quantidadeDesejada deve ser um inteiro entre 1 e 10000.")]
    [InlineData("semQuantidade", "quantidadeDesejada é obrigatória.")]
    [InlineData("custoNegativo", "custoUnitarioEstimado deve ser maior ou igual a zero, com até duas casas decimais.")]
    [InlineData("linkFtp", "linkReferencia deve ser uma URL http ou https absoluta.")]
    [InlineData("linkRelativo", "linkReferencia deve ser uma URL http ou https absoluta.")]
    [InlineData("semJustificativa", "justificativa é obrigatório.")]
    [InlineData("justificativaLonga", "justificativa deve ter no máximo 1000 caracteres.")]
    [InlineData("semNome", "nome é obrigatório.")]
    [InlineData("semDescricao", "descricao é obrigatório.")]
    public void DesejoValidator_CadaRegra(string caso, string? mensagem)
    {
        var request = DesejoRequest(r =>
        {
            switch (caso)
            {
                case "http": r.LinkReferencia = "http://exemplo.com/x"; r.CustoUnitarioEstimado = 0m; r.Categoria = "Acampamento"; break;
                case "semUrgencia": r.Urgencia = null; break;
                case "urgenciaForaDoEnum": r.Urgencia = (NivelUrgenciaAlmoxarifado)4; break;
                case "semTipo": r.TipoItem = null; break;
                case "quantidadeZero": r.QuantidadeDesejada = 0; break;
                case "quantidadeAlta": r.QuantidadeDesejada = 10_001; break;
                case "semQuantidade": r.QuantidadeDesejada = null; break;
                case "custoNegativo": r.CustoUnitarioEstimado = -0.5m; break;
                case "linkFtp": r.LinkReferencia = "ftp://exemplo.com"; break;
                case "linkRelativo": r.LinkReferencia = "/barraca"; break;
                case "semJustificativa": r.Justificativa = null; break;
                case "justificativaLonga": r.Justificativa = new string('j', 1001); break;
                case "semNome": r.Nome = ""; break;
                case "semDescricao": r.Descricao = "  "; break;
            }
        });
        var erros = Erros(new CadastrarDesejoRequestValidator(), request);
        if (mensagem is null)
        {
            Assert.Empty(erros);
        }
        else
        {
            Assert.Contains(mensagem, erros);
        }
    }

    [Fact]
    public void DesejoAtualizacaoECancelamento_ExigemVersaoEJustificativa()
    {
        var atualizar = new AtualizarDesejoRequest
        {
            Nome = "n", Descricao = "d", Justificativa = "j", TipoItem = TipoItemDesejo.Consumivel, QuantidadeDesejada = 1,
            Urgencia = NivelUrgenciaAlmoxarifado.MuitoUrgente,
        };
        Assert.Equal(["versao é obrigatória e deve ser a versão devolvida pela API."], Erros(new AtualizarDesejoRequestValidator(), atualizar));
        atualizar.Versao = "AAAAAAAAB9E=";
        Assert.Empty(Erros(new AtualizarDesejoRequestValidator(), atualizar));
        Assert.Equal(["justificativa é obrigatório.", "versao é obrigatória e deve ser a versão devolvida pela API."],
            Erros(new EncerrarDesejoRequestValidator(), new EncerrarDesejoRequest()));
        Assert.Empty(Erros(new EncerrarDesejoRequestValidator(), new EncerrarDesejoRequest { Justificativa = "x", Versao = "AAAAAAAAB9E=" }));
    }

    [Fact]
    public void AtendimentoValidator_ExigeChaveDeIdempotenciaEBensValidos()
    {
        var validator = new RegistrarAtendimentoRequestValidator();
        RegistrarAtendimentoRequest Valido() => new()
        {
            IdempotencyKey = Guid.NewGuid().ToString(), Quantidade = 2, Data = Hoje, ResponsavelId = Guid.NewGuid(), Origem = OrigemAtendimento.Compra,
        };
        Assert.Empty(Erros(validator, Valido()));
        var comBens = Valido();
        comBens.BensNovos = [Bem<NovoBemAtendimentoRequest>()];
        comBens.BensExistentes = [Guid.NewGuid()];
        Assert.Empty(Erros(validator, comBens));

        Assert.Equal(["O header Idempotency-Key é obrigatório e deve ser um UUID.", "quantidade é obrigatória.", "data é obrigatória (yyyy-MM-dd).",
            "responsavelId é obrigatório (GUID não vazio).", "origem é obrigatório."], Erros(validator, new RegistrarAtendimentoRequest { IdempotencyKey = "abc" }));
        var quantidade = Valido();
        quantidade.Quantidade = 0;
        Assert.Contains("quantidade deve ser um inteiro entre 1 e 10000.", Erros(validator, quantidade));
        var origem = Valido();
        origem.Origem = (OrigemAtendimento)3;
        Assert.Contains("origem deve ser um destes valores: Compra, Doacao.", Erros(validator, origem));

        var bemInvalido = Valido();
        bemInvalido.BensNovos = [Bem<NovoBemAtendimentoRequest>(b => b.Nome = null)];
        Assert.Equal(["nome é obrigatório."], Erros(validator, bemInvalido));
        var nulo = Valido();
        nulo.BensNovos = [Bem<NovoBemAtendimentoRequest>(), null!];
        Assert.Equal(["bensNovos não pode conter itens nulos."], Erros(validator, nulo));

        var vazio = Valido();
        vazio.BensExistentes = [Guid.Empty];
        Assert.Contains("bensExistentes não pode conter GUID vazio.", Erros(validator, vazio));
        var repetido = Valido();
        var id = Guid.NewGuid();
        repetido.BensExistentes = [id, id];
        Assert.Contains("bensExistentes não pode conter GUIDs repetidos.", Erros(validator, repetido));
        var muitos = Valido();
        muitos.BensExistentes = [.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())];
        Assert.Contains("bensExistentes aceita no máximo 100 itens.", Erros(validator, muitos));
        var somados = Valido();
        somados.BensNovos = [.. Enumerable.Range(0, 60).Select(_ => Bem<NovoBemAtendimentoRequest>())];
        somados.BensExistentes = [.. Enumerable.Range(0, 41).Select(_ => Guid.NewGuid())];
        Assert.Contains("Um atendimento aceita no máximo 100 bens.", Erros(validator, somados));

        Assert.Equal(["justificativa é obrigatório."], Erros(new EstornarAtendimentoRequestValidator(), new EstornarAtendimentoRequest()));
        Assert.Empty(Erros(new EstornarAtendimentoRequestValidator(), new EstornarAtendimentoRequest { Justificativa = "lançado errado" }));
    }

    [Fact]
    public void DesignacaoValidator_ExigeMembroDataEMotivo()
    {
        Assert.Empty(Erros(new DesignarResponsavelRequestValidator(), new DesignarResponsavelRequest { MembroId = Guid.NewGuid(), DataInicio = Hoje }));
        Assert.Equal(["membroId é obrigatório (GUID não vazio).", "dataInicio é obrigatória (yyyy-MM-dd)."],
            Erros(new DesignarResponsavelRequestValidator(), new DesignarResponsavelRequest()));
        Assert.Equal(["motivo é obrigatório.", "versao é obrigatória e deve ser a versão devolvida pela API."],
            Erros(new RevogarResponsavelRequestValidator(), new RevogarResponsavelRequest()));
        Assert.Empty(Erros(new RevogarResponsavelRequestValidator(), new RevogarResponsavelRequest { Motivo = "fim", Versao = "AAAAAAAAB9E=" }));
    }

    [Fact]
    public void ConsultasValidator_FiltrosEPaginacao()
    {
        Assert.Empty(Erros(new ListarBensQueryValidator(), new ListarBensQuery { Busca = "PAT", Situacao = SituacaoBem.Baixado, Conservacao = EstadoConservacao.Bom }));
        Assert.Equal(["situacao deve ser um destes valores: Disponivel, Emprestado, EmManutencao, Baixado.", "page deve ser maior ou igual a 1.",
            "pageSize deve estar entre 1 e 100."], Erros(new ListarBensQueryValidator(), new ListarBensQuery { Situacao = (SituacaoBem)9, Page = 0, PageSize = 0 }));
        Assert.Contains("busca deve ter no máximo 150 caracteres.", Erros(new ListarBensQueryValidator(), new ListarBensQuery { Busca = new string('b', 151) }));
        Assert.Contains("conservacao deve ser um destes valores: Novo, Bom, Regular, Ruim, Inservivel.",
            Erros(new RelatorioPatrimonioQueryValidator(), new RelatorioPatrimonioQuery(new ListarBensQuery { Conservacao = (EstadoConservacao)8 })));
        Assert.Contains("localizacao deve ter no máximo 150 caracteres.",
            Erros(new ExportarPatrimonioQueryValidator(), new ExportarPatrimonioQuery(new ListarBensQuery { Localizacao = new string('l', 151) })));

        Assert.Empty(Erros(new ListarEmprestimosQueryValidator(), new ListarEmprestimosQuery { Atrasados = true, Abertos = true }));
        Assert.Equal(["bemId não pode ser um GUID vazio.", "membroId não pode ser um GUID vazio.",
            "atrasados = true já implica empréstimos abertos; não combine com abertos = false."],
            Erros(new ListarEmprestimosQueryValidator(), new ListarEmprestimosQuery { BemId = Guid.Empty, MembroId = Guid.Empty, Atrasados = true, Abertos = false }));
        Assert.Equal(["pageSize deve estar entre 1 e 100."], Erros(new ListarInventariosQueryValidator(), new ListarInventariosQuery(1, 101)));
        Assert.Empty(Erros(new ListarDesejosQueryValidator(), new ListarDesejosQuery { Urgencia = NivelUrgenciaAlmoxarifado.MuitoUrgente, TipoItem = TipoItemDesejo.Consumivel }));
        Assert.Equal(["categoria deve ter no máximo 60 caracteres.", "urgencia deve ser um destes valores: PoucoUrgenteMasNecessario, Urgente, MuitoUrgente.",
            "situacao deve ser um destes valores: Pendente, ParcialmenteAtendida, Atendida, Cancelada.", "tipoItem deve ser um destes valores: Patrimonial, Consumivel."],
            Erros(new ListarDesejosQueryValidator(), new ListarDesejosQuery
            {
                Categoria = new string('c', 61), Urgencia = (NivelUrgenciaAlmoxarifado)7, Situacao = (SituacaoDesejo)0, TipoItem = (TipoItemDesejo)5,
            }));
        Assert.Empty(Erros(new HistoricoQueryValidator(), new HistoricoQuery(Guid.NewGuid(), false, Hoje, Hoje, TipoMovimentacaoAlmoxarifado.Baixa)));
        Assert.Equal(["tipo deve ser um destes valores: " + string.Join(", ", Enum.GetNames<TipoMovimentacaoAlmoxarifado>()) + ".", "ate não pode ser anterior a de."],
            Erros(new HistoricoQueryValidator(), new HistoricoQuery(Guid.NewGuid(), true, Hoje, Hoje.AddDays(-1), (TipoMovimentacaoAlmoxarifado)0)));
    }

    [Fact]
    public void HashDoAtendimento_IgnoraOrdemDosBensExistentesEEspacos_EMudaComOsDados()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        RegistrarAtendimentoRequest Pedido(params Guid[] bens) => new()
        {
            Id = Guid.Parse("11111111-1111-4111-8111-111111111111"), Quantidade = 2, Data = Hoje, ResponsavelId = a, Origem = OrigemAtendimento.Doacao,
            Observacoes = " doação ", BensExistentes = [.. bens], IdempotencyKey = Guid.NewGuid().ToString(), UsuarioId = Guid.NewGuid(),
        };
        var original = DesejosAlmoxarifadoService.Hash(Pedido(a, b));
        Assert.Matches("^[0-9A-F]{64}$", original);
        Assert.Equal(original, DesejosAlmoxarifadoService.Hash(Pedido(b, a)));
        var espacos = Pedido(a, b);
        espacos.Observacoes = "doação";
        Assert.Equal(original, DesejosAlmoxarifadoService.Hash(espacos));
        var outraQuantidade = Pedido(a, b);
        outraQuantidade.Quantidade = 3;
        Assert.NotEqual(original, DesejosAlmoxarifadoService.Hash(outraQuantidade));
        var outroDesejo = Pedido(a, b);
        outroDesejo.Id = Guid.NewGuid();
        Assert.NotEqual(original, DesejosAlmoxarifadoService.Hash(outroDesejo));
        var comNovo = Pedido(a, b);
        comNovo.BensNovos = [Bem<NovoBemAtendimentoRequest>()];
        Assert.NotEqual(original, DesejosAlmoxarifadoService.Hash(comNovo));
    }

    [Fact]
    public void Roles_GestaoEConsultaDoAlmoxarifado()
    {
        Assert.Equal(["ADM", "DIR", "DIRA", "SEC", "ALM"], Roles.Almoxarifado);
        Assert.Equal(["DS"], Roles.SemConsultaAlmoxarifado);
        Assert.DoesNotContain("ALM", Roles.CargosDeGestao);
        foreach (var role in new[] { "ADM", "DIR", "DIRA", "SEC", "ALM" })
        {
            Assert.True(AcessoAlmoxarifadoRequirement.Gestao.PermitidoPeloCargo(role));
            Assert.True(AcessoAlmoxarifadoRequirement.Consulta.PermitidoPeloCargo(role));
        }

        foreach (var role in new[] { "TES", "CAP", "CONS", "CONSA", "INST", "CPT", "SECU", "TESU", "PAD", "COREC", "CAPU", "NOVO" })
        {
            Assert.False(AcessoAlmoxarifadoRequirement.Gestao.PermitidoPeloCargo(role));
            Assert.True(AcessoAlmoxarifadoRequirement.Consulta.PermitidoPeloCargo(role));
        }

        Assert.False(AcessoAlmoxarifadoRequirement.Gestao.PermitidoPeloCargo("DS"));
        Assert.False(AcessoAlmoxarifadoRequirement.Consulta.PermitidoPeloCargo("DS"));
        Assert.False(AcessoAlmoxarifadoRequirement.Consulta.PermitidoPeloCargo(string.Empty));
        Assert.True(AcessoAlmoxarifadoRequirement.Gestao.SomenteGestao);
        Assert.False(AcessoAlmoxarifadoRequirement.Consulta.SomenteGestao);
    }

    private static ClaimsPrincipal Usuario(string role, Guid? id = null) => new(new ClaimsIdentity(
        [new Claim("role", role), .. id is { } valor ? new[] { new Claim(ClaimTypes.NameIdentifier, valor.ToString()) } : []], "Bearer", "sub", "role"));

    [Fact]
    public async Task Policies_PorCargoEPorDesignacaoVigente()
    {
        var banco = Guid.NewGuid().ToString();
        var services = new ServiceCollection().AddLogging().AddAuthorization(Policies.Registrar)
            .AddDbContext<AlmiranteDbContext>(o => o.UseInMemoryDatabase(banco))
            .AddScoped<IAuthorizationHandler, AcessoAlmoxarifadoHandler>().BuildServiceProvider();
        var (designado, revogado, semDesignacao) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        using (var scope = services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlmiranteDbContext>();
            db.ResponsaveisAlmoxarifado.AddRange(
                new ResponsavelAlmoxarifado { Id = Guid.NewGuid(), MembroId = designado, DataInicio = Hoje },
                new ResponsavelAlmoxarifado { Id = Guid.NewGuid(), MembroId = revogado, DataInicio = Hoje, RevogadoEmUtc = DateTime.UtcNow, MotivoRevogacao = "fim" });
            await db.SaveChangesAsync();
        }

        async Task<string[]> Concedidas(ClaimsPrincipal usuario)
        {
            using var scope = services.CreateScope();
            var autorizacao = scope.ServiceProvider.GetRequiredService<IAuthorizationService>();
            var lista = new List<string>();
            foreach (var policy in new[] { Policies.GestaoAlmoxarifado, Policies.ConsultaAlmoxarifado, Policies.DesignacaoAlmoxarifado })
            {
                if ((await autorizacao.AuthorizeAsync(usuario, policy)).Succeeded) lista.Add(policy);
            }

            return [.. lista];
        }

        string[] todas = [Policies.GestaoAlmoxarifado, Policies.ConsultaAlmoxarifado, Policies.DesignacaoAlmoxarifado];
        string[] gestao = [Policies.GestaoAlmoxarifado, Policies.ConsultaAlmoxarifado];
        Assert.Equal(todas, await Concedidas(Usuario("ADM")));
        Assert.Equal(todas, await Concedidas(Usuario("SEC")));
        Assert.Equal(gestao, await Concedidas(Usuario("ALM", Guid.NewGuid())));
        Assert.Equal([Policies.ConsultaAlmoxarifado], await Concedidas(Usuario("TES", semDesignacao)));
        Assert.Equal([Policies.ConsultaAlmoxarifado], await Concedidas(Usuario("CONS", revogado)));
        Assert.Empty(await Concedidas(Usuario("DS", semDesignacao)));
        Assert.Empty(await Concedidas(Usuario("DS")));
        Assert.Equal(gestao, await Concedidas(Usuario("DS", designado)));
        Assert.Equal(gestao, await Concedidas(Usuario("CONS", designado)));
        Assert.Empty(await Concedidas(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task Api_SemTokenRecebe401_EDesbravadorRecebe403_EmTodasAsRotas()
    {
        using var factory = new AlmiranteApiFactory();
        using var anonimo = factory.CreateClient();
        var (ds, _) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "DS");
        var (tes, tesId) = await TestHelpers.CreateAuthenticatedClientForRoleAsync(factory, "TES");
        var id = Guid.NewGuid();
        var leituras = new[]
        {
            "/api/Almoxarifado/Bens", $"/api/Almoxarifado/Bens/{id}", $"/api/Almoxarifado/Bens/{id}/Historico", $"/api/Almoxarifado/Bens/{id}/Manutencoes",
            "/api/Almoxarifado/Emprestimos", $"/api/Almoxarifado/Emprestimos/{id}", "/api/Almoxarifado/Inventarios", $"/api/Almoxarifado/Inventarios/{id}",
            "/api/Almoxarifado/Desejos", $"/api/Almoxarifado/Desejos/{id}", $"/api/Almoxarifado/Desejos/{id}/Historico", "/api/Almoxarifado/Enums",
            "/api/Almoxarifado/Relatorios/Patrimonio", "/api/Almoxarifado/Relatorios/Patrimonio/Csv", "/api/Almoxarifado/Relatorios/Emprestimos",
            "/api/Almoxarifado/Relatorios/Emprestimos/Csv", "/api/Almoxarifado/Relatorios/Desejos", "/api/Almoxarifado/Responsaveis",
        };
        foreach (var rota in leituras)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync(rota)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ds.GetAsync(rota)).StatusCode);
        }

        var escritas = new (HttpMethod Metodo, string Rota)[]
        {
            (HttpMethod.Post, "/api/Almoxarifado/Bens"), (HttpMethod.Put, $"/api/Almoxarifado/Bens/{id}"), (HttpMethod.Delete, $"/api/Almoxarifado/Bens/{id}"),
            (HttpMethod.Post, $"/api/Almoxarifado/Bens/{id}/Emprestimos"), (HttpMethod.Post, $"/api/Almoxarifado/Bens/{id}/Manutencoes"),
            (HttpMethod.Post, $"/api/Almoxarifado/Bens/{id}/Baixa"), (HttpMethod.Post, $"/api/Almoxarifado/Emprestimos/{id}/Devolucao"),
            (HttpMethod.Post, $"/api/Almoxarifado/Emprestimos/{id}/PerdaFurto"), (HttpMethod.Post, $"/api/Almoxarifado/Manutencoes/{id}/Saida"),
            (HttpMethod.Post, "/api/Almoxarifado/Inventarios"), (HttpMethod.Post, $"/api/Almoxarifado/Inventarios/{id}/Votacao"),
            (HttpMethod.Post, "/api/Almoxarifado/Desejos"), (HttpMethod.Put, $"/api/Almoxarifado/Desejos/{id}"), (HttpMethod.Delete, $"/api/Almoxarifado/Desejos/{id}"),
            (HttpMethod.Post, $"/api/Almoxarifado/Desejos/{id}/Cancelar"), (HttpMethod.Post, $"/api/Almoxarifado/Desejos/{id}/Atendimentos"),
            (HttpMethod.Post, $"/api/Almoxarifado/Desejos/{id}/Atendimentos/{id}/Estorno"), (HttpMethod.Post, "/api/Almoxarifado/Responsaveis"),
            (HttpMethod.Post, $"/api/Almoxarifado/Responsaveis/{id}/Revogar"),
        };
        foreach (var (metodo, rota) in escritas)
        {
            HttpRequestMessage Requisicao() => new(metodo, rota) { Content = JsonContent.Create(new { }) };
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.SendAsync(Requisicao())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await ds.SendAsync(Requisicao())).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await tes.SendAsync(Requisicao())).StatusCode);
        }

        var enums = await tes.GetFromJsonAsync<JsonObject>("/api/Almoxarifado/Enums");
        Assert.Equal("Muito urgente", (string)enums!["nivelUrgenciaAlmoxarifado"]![2]!["descricao"]!);
        Assert.Equal(HttpStatusCode.NotFound, (await tes.GetAsync($"/api/Almoxarifado/Bens/{id}")).StatusCode);
        Assert.NotEqual(Guid.Empty, tesId);
    }

    [Fact]
    public async Task Swagger_DocumentaRotasExemplosEIdempotencia()
    {
        using var factory = new AlmiranteApiFactory();
        using var client = factory.CreateClient();
        var swagger = JsonNode.Parse(await client.GetStringAsync("/swagger/v1/swagger.json"))!;
        var paths = swagger["paths"]!.AsObject();
        foreach (var rota in new[]
        {
            "/api/Almoxarifado/Bens", "/api/Almoxarifado/Bens/{id}/Emprestimos", "/api/Almoxarifado/Emprestimos/{id}/Devolucao", "/api/Almoxarifado/Inventarios",
            "/api/Almoxarifado/Inventarios/{id}/Votacao", "/api/Almoxarifado/Desejos", "/api/Almoxarifado/Desejos/{id}/Atendimentos", "/api/Almoxarifado/Enums",
            "/api/Almoxarifado/Relatorios/Patrimonio/Csv", "/api/Almoxarifado/Responsaveis",
        })
        {
            Assert.True(paths.ContainsKey(rota), rota);
        }

        var atendimento = paths["/api/Almoxarifado/Desejos/{id}/Atendimentos"]!["post"]!;
        Assert.Contains("Idempotency-Key", (string)atendimento["description"]!);
        var chave = atendimento["parameters"]!.AsArray().Single(p => (string)p!["name"]! == "Idempotency-Key")!;
        Assert.True((bool)chave["required"]!);
        Assert.Contains("parcialPatrimonial", atendimento["requestBody"]!["content"]!["application/json"]!["examples"]!.AsObject().Select(e => e.Key));
        var cadastro = paths["/api/Almoxarifado/Bens"]!["post"]!;
        Assert.Equal(["comValorComercial", "semValorComercial", "acervoSemData"],
            cadastro["requestBody"]!["content"]!["application/json"]!["examples"]!.AsObject().Select(e => e.Key));
        Assert.False(string.IsNullOrWhiteSpace((string?)cadastro["summary"]));
        Assert.NotNull(paths["/api/Almoxarifado/Emprestimos/{id}/Devolucao"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!["manutencao"]);
        Assert.NotNull(paths["/api/Almoxarifado/Inventarios/{id}/Votacao"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!["votacao"]);
        Assert.NotNull(paths["/api/Almoxarifado/Desejos"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!["consumivel"]);
        Assert.NotNull(paths["/api/Almoxarifado/Inventarios"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!["fechamento"]);
        Assert.NotNull(paths["/api/Almoxarifado/Bens/{id}/Emprestimos"]!["post"]!["requestBody"]!["content"]!["application/json"]!["examples"]!["emprestimo"]);
        Assert.Null(paths["/api/Almoxarifado/Bens/{id}"]!["put"]!["requestBody"]!["content"]!["application/json"]!["examples"]);
    }
}
