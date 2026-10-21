using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Options;

public sealed class NotificacoesOptions
{
    public const string SectionName = "Notificacoes";

    /// <summary>Varredura do banco atrás de avisos pendentes quando nenhum commit acordou o publicador.</summary>
    [Range(100, 60_000)]
    public int VarreduraAvisosMs { get; set; } = 5_000;

    [Range(1, 500)]
    public int LoteAvisos { get; set; } = 100;

    /// <summary>Depois disso o aviso deixa de ser tentado; a notificação continua disponível pela API.</summary>
    [Range(1, 50)]
    public int MaxTentativasAviso { get; set; } = 10;

    /// <summary>Espera antes da 2ª tentativa; dobra a cada falha até <see cref="AtrasoMaximoAvisoMs"/>.</summary>
    [Range(50, 600_000)]
    public int AtrasoInicialAvisoMs { get; set; } = 5_000;

    [Range(50, 3_600_000)]
    public int AtrasoMaximoAvisoMs { get; set; } = 300_000;

    /// <summary>Reserva de um aviso em emissão: se a instância cair no meio, outra varredura o retoma depois disso.</summary>
    [Range(1_000, 600_000)]
    public int ReservaAvisoMs { get; set; } = 30_000;

    [Range(100, 60_000)]
    public int TempoLimiteEnvioMs { get; set; } = 10_000;

    /// <summary>Revalidação das sessões das conexões abertas do hub (revogação, logout, cargo inativo).</summary>
    [Range(100, 300_000)]
    public int VerificacaoSessoesMs { get; set; } = 30_000;
}
