using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Text;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;

namespace Almirante.Api.Services;

/// <summary>
/// Regras de domínio do patrimônio e da lista de desejos (issue #126), sem acesso a banco: o serviço carrega e trava os registros e chama
/// estas conferências antes de gravar. Uma regra violada vira 400 (dado inválido) ou 409 (operação incompatível com a situação atual).
/// </summary>
public static class RegrasAlmoxarifado
{
    public const int NomeMaximo = 150;
    public const int DescricaoMaximo = 2000;
    public const int CategoriaMaximo = 60;
    public const int LocalizacaoMaximo = 150;
    public const int MarcaModeloSerieMaximo = 100;
    public const int ObservacoesMaximo = 1000;
    public const int OrigemDetalheMaximo = 300;
    public const int JustificativaMaximo = EventoRegras.MotivoMaximo;
    public const int JustificativaDesejoMaximo = 1000;
    public const int LinkMaximo = 500;
    public const int ReferenciaAtaMaximo = 100;
    public const int BuscaMaximo = 150;
    public const int AcaoMaximo = 40;
    public const int TomboMaximo = 14;
    public const int IdempotencyKeyTamanho = 36;
    public const int QuantidadeMaxima = 10_000;
    public const int BensPorAtendimentoMaximo = 100;
    public const decimal ValorMaximo = EventoRegras.ValorMaximo;
    public const int PageSizePadrao = 20;
    public const int PageSizeMaximo = 100;

    /// <summary>Expressão da coluna calculada Tombo (PAT-000001; acima de 999999 o número cresce sem truncar).</summary>
    public const string TomboSql =
        "CONVERT(varchar(14), 'PAT-' + CASE WHEN [NumeroTombo] < 1000000 THEN RIGHT('00000' + CONVERT(varchar(10), [NumeroTombo]), 6)" +
        " ELSE CONVERT(varchar(10), [NumeroTombo]) END)";

    public static string Tombo(int numero) => numero < 1_000_000 ? $"PAT-{numero:000000}" : $"PAT-{numero}";

    public static SituacaoDesejo SituacaoDesejo(int desejada, int atendida, bool cancelada) =>
        cancelada ? Entities.SituacaoDesejo.Cancelada
        : atendida <= 0 ? Entities.SituacaoDesejo.Pendente
        : atendida < desejada ? Entities.SituacaoDesejo.ParcialmenteAtendida
        : Entities.SituacaoDesejo.Atendida;

    public static bool Aberta(SituacaoDesejo situacao) => situacao is Entities.SituacaoDesejo.Pendente or Entities.SituacaoDesejo.ParcialmenteAtendida;

    public static int Pendente(int desejada, int atendida) => Math.Max(0, desejada - atendida);

    /// <summary>Sem estimativa unitária o custo é desconhecido (null), nunca zero.</summary>
    public static decimal? Custo(decimal? unitario, int quantidade) => unitario * quantidade;

    /// <summary>Atraso calculado na consulta (previsão anterior a hoje e empréstimo aberto), sem depender de atualização manual.</summary>
    public static bool Atrasado(EmprestimoBem emprestimo, DateOnly hoje) => emprestimo.Aberto && emprestimo.PrevisaoDevolucao < hoje;

    public static int DiasAtraso(EmprestimoBem emprestimo, DateOnly hoje) =>
        Atrasado(emprestimo, hoje) ? hoje.DayNumber - emprestimo.PrevisaoDevolucao.DayNumber : 0;

    public static void ConferirNaoFutura(string campo, DateOnly? data, DateOnly hoje)
    {
        if (data > hoje)
        {
            throw ApiProblemException.Invalid(campo, $"{campo} não pode ser futura.");
        }
    }

    /// <summary>
    /// Uma nova movimentação (retirada, entrada em manutenção, baixa) não começa antes da aquisição nem antes do encerramento da
    /// movimentação anterior do bem: o histórico nunca mostra dois empréstimos ou manutenções sobrepostos.
    /// </summary>
    public static void ConferirPosterior(string campo, DateOnly data, DateOnly? aquisicao, DateOnly? ultimaMovimentacao)
    {
        if (data < aquisicao)
        {
            throw ApiProblemException.Invalid(campo, $"{campo} não pode ser anterior à aquisição do bem ({aquisicao:yyyy-MM-dd}).");
        }

        if (data < ultimaMovimentacao)
        {
            throw ApiProblemException.Invalid(campo,
                $"{campo} não pode ser anterior ao encerramento da última movimentação do bem ({ultimaMovimentacao:yyyy-MM-dd}).");
        }
    }

    public static void ConferirAlteracao(BemPatrimonial bem)
    {
        if (bem.Excluido)
        {
            throw ApiProblemException.Conflict("Bem excluído.", "Um cadastro excluído logicamente não é alterado nem movimentado.");
        }

        if (bem.Situacao == SituacaoBem.Baixado)
        {
            throw ApiProblemException.Conflict("Bem baixado.", "Um bem baixado saiu do patrimônio: o cadastro e o histórico ficam preservados, sem alterações.");
        }
    }

    /// <summary>Só bem disponível e utilizável (não inservível) é emprestado; a saída não pode ser declarada inservível.</summary>
    public static void ConferirEmprestimo(BemPatrimonial bem, EstadoConservacao conservacaoSaida)
    {
        ConferirAlteracao(bem);
        switch (bem.Situacao)
        {
            case SituacaoBem.Emprestado:
                throw ApiProblemException.Conflict("O bem já está emprestado.", "Registre a devolução do empréstimo aberto antes de um novo empréstimo.");
            case SituacaoBem.EmManutencao:
                throw ApiProblemException.Conflict("O bem está em manutenção.", "Registre a saída da manutenção antes de emprestar.");
        }

        if (bem.EstadoConservacao == EstadoConservacao.Inservivel || conservacaoSaida == EstadoConservacao.Inservivel)
        {
            throw ApiProblemException.Conflict("Bem inservível.", "Bens em estado inservível não são emprestados.");
        }
    }

    public static void ConferirDatasEmprestimo(DateOnly retirada, DateOnly previsao, DateOnly hoje)
    {
        ConferirNaoFutura("dataRetirada", retirada, hoje);
        if (previsao < retirada)
        {
            throw ApiProblemException.Invalid("previsaoDevolucao", "previsaoDevolucao não pode ser anterior à dataRetirada.");
        }
    }

    /// <summary>Encerramento (devolução ou perda/furto) só de empréstimo aberto, entre a retirada e hoje.</summary>
    public static void ConferirEncerramento(EmprestimoBem emprestimo, string campo, DateOnly data, DateOnly hoje)
    {
        if (!emprestimo.Aberto)
        {
            throw ApiProblemException.Conflict("Empréstimo já encerrado.", "A devolução (ou perda/furto) deste empréstimo já foi registrada.");
        }

        ConferirNaoFutura(campo, data, hoje);
        if (data < emprestimo.DataRetirada)
        {
            throw ApiProblemException.Invalid(campo, $"{campo} não pode ser anterior à retirada ({emprestimo.DataRetirada:yyyy-MM-dd}).");
        }
    }

    public static void ConferirEntradaManutencao(BemPatrimonial bem, DateOnly data, DateOnly hoje)
    {
        ConferirAlteracao(bem);
        switch (bem.Situacao)
        {
            case SituacaoBem.Emprestado:
                throw ApiProblemException.Conflict("O bem está emprestado.", "Registre a devolução antes (a devolução pode encaminhar o bem para manutenção).");
            case SituacaoBem.EmManutencao:
                throw ApiProblemException.Conflict("O bem já está em manutenção.");
        }

        ConferirNaoFutura("dataEntrada", data, hoje);
    }

    public static void ConferirSaidaManutencao(ManutencaoBem manutencao, DateOnly data, DateOnly hoje)
    {
        if (!manutencao.Aberta)
        {
            throw ApiProblemException.Conflict("Manutenção já encerrada.");
        }

        ConferirNaoFutura("dataSaida", data, hoje);
        if (data < manutencao.DataEntrada)
        {
            throw ApiProblemException.Invalid("dataSaida", $"dataSaida não pode ser anterior à entrada ({manutencao.DataEntrada:yyyy-MM-dd}).");
        }
    }

    /// <summary>Baixa exige data e motivo; empréstimo aberto precisa ser encerrado antes (devolução ou perda/furto).</summary>
    public static void ConferirBaixa(BemPatrimonial bem, DateOnly data, DateOnly hoje)
    {
        ConferirAlteracao(bem);
        if (bem.Situacao == SituacaoBem.Emprestado)
        {
            throw ApiProblemException.Conflict("O bem está emprestado.",
                "Encerre o empréstimo aberto como devolução ou como perda/furto (que já baixa o bem) antes de concluir a baixa.");
        }

        ConferirNaoFutura("dataBaixa", data, hoje);
    }

    /// <summary>
    /// Exclusão lógica é só para cadastro indevido: não oculta empréstimo aberto e não representa descarte de um bem que já teve
    /// movimentações (esse é baixado) nem desfaz o recebimento de um atendimento (esse é estornado).
    /// </summary>
    public static void ConferirExclusao(BemPatrimonial bem, bool teveMovimentacoes, bool vinculadoAAtendimento)
    {
        if (bem.Excluido)
        {
            throw ApiProblemException.Conflict("Bem já excluído.");
        }

        if (bem.Situacao == SituacaoBem.Emprestado)
        {
            throw ApiProblemException.Conflict("O bem tem empréstimo aberto.", "A exclusão lógica não pode ocultar um empréstimo aberto.");
        }

        if (teveMovimentacoes || bem.Situacao != SituacaoBem.Disponivel)
        {
            throw ApiProblemException.Conflict("O bem já teve movimentações.",
                "Bens com empréstimos, manutenções ou baixa saem do patrimônio pela baixa (POST .../Baixa), não pela exclusão.");
        }

        if (vinculadoAAtendimento)
        {
            throw ApiProblemException.Conflict("O bem foi recebido em um atendimento da lista de desejos.",
                "Estorne o atendimento (com justificativa) para corrigir um recebimento indevido.");
        }
    }

    public static void ConferirAlteracaoDesejo(DesejoAlmoxarifado desejo)
    {
        if (desejo.Excluido)
        {
            throw ApiProblemException.Conflict("Necessidade excluída.", "Uma necessidade excluída logicamente não é alterada nem atendida.");
        }

        if (desejo.Situacao == Entities.SituacaoDesejo.Cancelada)
        {
            throw ApiProblemException.Conflict("Necessidade cancelada.", "Necessidades canceladas não são alteradas nem recebem novos atendimentos.");
        }
    }

    /// <summary>
    /// A quantidade desejada pode aumentar (com histórico), mas nunca ficar abaixo do já atendido; o tipo do item não muda depois de um
    /// atendimento (os recebimentos já criaram, ou não, bens patrimoniais).
    /// </summary>
    public static void ConferirAtualizacaoDesejo(DesejoAlmoxarifado desejo, int novaQuantidade, TipoItemDesejo novoTipo)
    {
        ConferirAlteracaoDesejo(desejo);
        if (novaQuantidade < desejo.QuantidadeAtendida)
        {
            throw ApiProblemException.Conflict("Quantidade desejada menor que a atendida.",
                $"Já foram atendidas {desejo.QuantidadeAtendida} unidade(s); a quantidade desejada não pode ficar abaixo disso.");
        }

        if (novoTipo != desejo.TipoItem && desejo.QuantidadeAtendida > 0)
        {
            throw ApiProblemException.Conflict("Tipo do item não pode mudar.",
                "A necessidade já tem atendimentos; estorne-os antes de trocar entre bem patrimonial e material consumível.");
        }
    }

    /// <summary>
    /// Atendimento: necessidade ativa e aberta, quantidade que não supera a pendente e bens físicos explícitos (um por unidade) só para item
    /// patrimonial; consumível não gera patrimônio.
    /// </summary>
    public static void ConferirAtendimento(DesejoAlmoxarifado desejo, int quantidade, DateOnly data, int bensInformados, DateOnly hoje)
    {
        ConferirAlteracaoDesejo(desejo);
        var pendente = Pendente(desejo.QuantidadeDesejada, desejo.QuantidadeAtendida);
        if (pendente == 0)
        {
            throw ApiProblemException.Conflict("Necessidade já atendida.", "A quantidade desejada já foi atendida; aumente-a (PUT) se a necessidade cresceu.");
        }

        if (quantidade > pendente)
        {
            throw ApiProblemException.Conflict("Quantidade superior à pendente.",
                $"Restam {pendente} unidade(s) pendente(s). Para receber mais, atualize antes a quantidade desejada.");
        }

        ConferirNaoFutura("data", data, hoje);
        if (desejo.TipoItem == TipoItemDesejo.Patrimonial && bensInformados != quantidade)
        {
            throw ApiProblemException.Invalid("bens",
                $"Item patrimonial: informe exatamente {quantidade} bem(ns) físico(s) em bensNovos e/ou bensExistentes (um por unidade recebida).");
        }

        if (desejo.TipoItem == TipoItemDesejo.Consumivel && bensInformados > 0)
        {
            throw ApiProblemException.Invalid("bens", "Material consumível não gera patrimônio: não informe bensNovos nem bensExistentes.");
        }
    }

    public static void ConferirCancelamento(DesejoAlmoxarifado desejo)
    {
        ConferirAlteracaoDesejo(desejo);
        if (desejo.Situacao == Entities.SituacaoDesejo.Atendida)
        {
            throw ApiProblemException.Conflict("Necessidade já atendida.", "Não há quantidade pendente a cancelar.");
        }
    }

    public static void ConferirExclusaoDesejo(DesejoAlmoxarifado desejo)
    {
        if (desejo.Excluido)
        {
            throw ApiProblemException.Conflict("Necessidade já excluída.");
        }
    }

    /// <summary>Estorno de atendimento indevido: uma vez só e consistente com os bens que ele criou (sem movimentações).</summary>
    public static void ConferirEstorno(AtendimentoDesejo atendimento, IReadOnlyCollection<BemPatrimonial> bensCriados, bool bensCriadosMovimentados)
    {
        if (atendimento.Estornado)
        {
            throw ApiProblemException.Conflict("Atendimento já estornado.");
        }

        if (bensCriadosMovimentados || bensCriados.Any(b => b.Excluido || b.Situacao != SituacaoBem.Disponivel))
        {
            throw ApiProblemException.Conflict("Bens criados pelo atendimento já foram movimentados.",
                "O estorno exclui logicamente os bens que o atendimento criou; com empréstimo, manutenção ou baixa eles precisam ficar no patrimônio.");
        }
    }

    public static void ConferirVotacao(InventarioPatrimonio inventario, DateOnly dataVotacao, DateOnly hoje)
    {
        if (inventario.VotacaoRegistradaEmUtc is not null)
        {
            throw ApiProblemException.Conflict("Votação já registrada.", "O inventário fechado e a votação registrada não são alterados.");
        }

        ConferirNaoFutura("dataVotacao", dataVotacao, hoje);
        if (dataVotacao < inventario.DataInventario)
        {
            throw ApiProblemException.Invalid("dataVotacao", $"dataVotacao não pode ser anterior ao inventário ({inventario.DataInventario:yyyy-MM-dd}).");
        }
    }

    public static void ConferirRevogacao(ResponsavelAlmoxarifado responsavel)
    {
        if (!responsavel.Vigente)
        {
            throw ApiProblemException.Conflict("Designação já revogada.");
        }
    }

    public static string Descricao<T>(T valor) where T : struct, Enum =>
        typeof(T).GetField(valor.ToString())?.GetCustomAttribute<DescriptionAttribute>()?.Description ?? valor.ToString();

    public static string? Descricao<T>(T? valor) where T : struct, Enum => valor is { } v ? Descricao(v) : null;

    public static IReadOnlyList<EnumItemDto> Itens<T>() where T : struct, Enum =>
        Enum.GetValues<T>().Select(v => new EnumItemDto(Convert.ToInt32(v, CultureInfo.InvariantCulture), v.ToString(), Descricao(v))).ToList();

    /// <summary>Converte uma operação de movimentação em TipoMovimentacaoAlmoxarifado; null para ações desconhecidas.</summary>
    public static TipoMovimentacaoAlmoxarifado? Tipo(string? acao) =>
        Enum.TryParse<TipoMovimentacaoAlmoxarifado>(acao, ignoreCase: false, out var tipo) && Enum.IsDefined(tipo) ? tipo : null;

    /// <summary>Intervalo UTC [de 00:00, ate+1 00:00) dos filtros de período dos históricos.</summary>
    public static (DateTime? Inicio, DateTime? Fim) Periodo(DateOnly? de, DateOnly? ate) =>
        (de?.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc), ate?.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}

/// <summary>
/// CSV para planilhas em pt-BR: separador ";", decimal com vírgula, datas yyyy-MM-dd, UTF-8 com BOM. Campos que começam com = + - @ ou
/// tabulação recebem um apóstrofo (proteção contra injeção de fórmulas).
/// </summary>
public static class CsvAlmoxarifado
{
    public const string ContentType = "text/csv; charset=utf-8";

    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public static string Campo(string? valor)
    {
        if (string.IsNullOrEmpty(valor))
        {
            return string.Empty;
        }

        if (valor[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            valor = "'" + valor;
        }

        return valor.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{valor.Replace("\"", "\"\"")}\"" : valor;
    }

    public static string Valor(decimal? valor) => valor?.ToString("0.00", PtBr) ?? string.Empty;

    public static string Data(DateOnly? data) => data?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    public static string SimNao(bool valor) => valor ? "Sim" : "Não";

    public static byte[] Gerar(IEnumerable<string> cabecalho, IEnumerable<IEnumerable<string?>> linhas)
    {
        var texto = new StringBuilder();
        texto.Append(string.Join(';', cabecalho.Select(Campo))).Append("\r\n");
        foreach (var linha in linhas)
        {
            texto.Append(string.Join(';', linha.Select(Campo))).Append("\r\n");
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(texto.ToString())];
    }
}
