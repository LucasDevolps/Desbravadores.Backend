using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace Almirante.Api.Options;

/// <summary>
/// Auditoria HTTP (issue #158, docs/auditoria-http.md). Todos os valores têm padrão; nenhuma variável de ambiente é obrigatória. No
/// Compose a pasta de pendentes é um volume (compose.yaml); sem configuração ela fica na pasta temporária do sistema, separada por banco.
/// </summary>
public sealed class AuditoriaHttpOptions
{
    public const string SectionName = "AuditoriaHttp";

    public bool Habilitada { get; set; } = true;

    /// <summary>Pasta do diário em disco (registros já sanitizados aguardando o banco). Vazia: <see cref="DiretorioPadrao"/>.</summary>
    public string? DiretorioPendentes { get; set; }

    /// <summary>Bytes de cada corpo JSON guardados em memória para sanitizar; acima disso o conteúdo é omitido (OmitidaPorTamanho).</summary>
    [Range(1024, 4 * 1024 * 1024)]
    public int LimiteCapturaBytes { get; set; } = 64 * 1024;

    /// <summary>Caracteres do conteúdo já sanitizado gravados por corpo; acima disso o trecho é cortado (Truncada).</summary>
    [Range(512, 1024 * 1024)]
    public int LimiteGravacaoCaracteres { get; set; } = 16 * 1024;

    /// <summary>Espaço máximo do diário em disco. Cheio, os registros vão para a reserva em memória.</summary>
    [Range(1, 100 * 1024)]
    public int LimiteDiarioMb { get; set; } = 256;

    /// <summary>Registros mantidos em memória quando o disco falha ou enche; além disso, descartados com erro no log e health check degradado.</summary>
    [Range(0, 1_000_000)]
    public int LimiteReservaMemoria { get; set; } = 5_000;

    [Range(100, 60_000)]
    public int IntervaloGravacaoMs { get; set; } = 1_000;

    [Range(1, 1_000)]
    public int TamanhoLote { get; set; } = 100;

    /// <summary>Espera máxima entre tentativas quando o banco está indisponível (dobra a cada falha, a partir do intervalo).</summary>
    [Range(1, 3_600)]
    public int EsperaMaximaSegundos { get; set; } = 60;

    public long LimiteDiarioBytes => LimiteDiarioMb * 1024L * 1024L;

    /// <summary>Pasta temporária do sistema + hash do servidor/banco: duas instalações na mesma máquina nunca dividem pendentes.</summary>
    public static string DiretorioPadrao(string? connectionString)
    {
        var alvo = "sem-banco";
        if (!string.IsNullOrWhiteSpace(connectionString))
        {
            try
            {
                var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
                alvo = $"{builder.DataSource}|{builder.InitialCatalog}".ToUpperInvariant();
            }
            catch (ArgumentException)
            {
                alvo = connectionString;
            }
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(alvo)))[..16];
        return Path.Combine(Path.GetTempPath(), "almirante", "auditoria-http", hash);
    }
}
