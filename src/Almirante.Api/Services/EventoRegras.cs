using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Almirante.Api.Dtos;

namespace Almirante.Api.Services;

public sealed record EventoNormalizado(
    DateOnly DataEvento,
    string Local,
    bool TransporteEhGratis,
    decimal TransporteValor,
    bool AlimentacaoIndividual,
    decimal AlimentacaoValor,
    decimal SeguroObrigatorio,
    IReadOnlyList<Guid> Membros)
{
    /// <summary>transporte efetivo + alimentação efetiva + seguro.</summary>
    public decimal ValorPorMembro => TransporteValor + AlimentacaoValor + SeguroObrigatorio;

    /// <summary>valorPorMembro × quantidade de membros.</summary>
    public decimal Total => ValorPorMembro * Membros.Count;
}

public static class EventoRegras
{
    public const decimal ValorMaximo = 9_999_999_999_999_999.99m;
    public const int LocalMaximo = 200;
    public const int MotivoMaximo = 255;

    public static DateOnly PrimeiroDiaDoMes(DateTimeOffset agoraUtc) => new(agoraUtc.UtcDateTime.Year, agoraUtc.UtcDateTime.Month, 1);

    public static bool DataDeCadastroValida(DateOnly dataEvento, DateTimeOffset agoraUtc) => dataEvento >= PrimeiroDiaDoMes(agoraUtc);

    public static string MensagemDataDeCadastro(DateTimeOffset agoraUtc) =>
        $"dataEvento deve ser a partir de {PrimeiroDiaDoMes(agoraUtc):yyyy-MM-dd} (primeiro dia do mês atual, UTC).";

    public static DateOnly UltimoDiaDoMes(DateTimeOffset agoraUtc) =>
        PrimeiroDiaDoMes(agoraUtc).AddMonths(1).AddDays(-1);

    public static (DateOnly Inicial, DateOnly Final) PeriodoPadrao(DateTimeOffset agoraUtc) =>
        (PrimeiroDiaDoMes(agoraUtc).AddDays(-30), UltimoDiaDoMes(agoraUtc).AddDays(30));

    public static string NormalizarLocal(string? local) =>
        string.Join(' ', (local ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool ValorValido(decimal valor) => valor >= 0 && valor <= ValorMaximo && decimal.Round(valor, 2) == valor;

    public static string? NormalizarIdempotencyKey(string? key) =>
        Guid.TryParse(key?.Trim(), out var id) ? id.ToString("D") : null;

    public static EventoNormalizado Normalizar(EventoConteudoRequest request)
    {
        var gratis = request.Transporte!.EhGratis!.Value;
        var individual = request.Alimentacao!.Individual!.Value;
        return new EventoNormalizado(
            request.DataEvento!.Value,
            NormalizarLocal(request.Local),
            gratis,
            gratis ? 0m : request.Transporte.Valor!.Value,
            individual,
            individual ? 0m : request.Alimentacao.Valor!.Value,
            request.SeguroObrigatorio!.Value,
            request.Membros!.OrderBy(m => m).ToArray());
    }

    public static string DescricaoLancamento(string local, DateOnly data)
    {
        var texto = $"Evento - {local} - {data.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";
        return texto.Length <= 500 ? texto : texto[..500];
    }

    public static string Hash(EventoNormalizado e, Guid? eventoReferenciaId)
    {
        var canonical = string.Join('|',
            "evento.v1",
            e.DataEvento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            JsonSerializer.Serialize(e.Local),
            e.TransporteEhGratis ? "1" : "0",
            e.TransporteValor.ToString("F2", CultureInfo.InvariantCulture),
            e.AlimentacaoIndividual ? "1" : "0",
            e.AlimentacaoValor.ToString("F2", CultureInfo.InvariantCulture),
            e.SeguroObrigatorio.ToString("F2", CultureInfo.InvariantCulture),
            eventoReferenciaId?.ToString("D") ?? "-",
            string.Join(',', e.Membros.OrderBy(m => m).Select(m => m.ToString("D"))));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    public static string VersaoParaTexto(byte[] versao) => Convert.ToBase64String(versao);

    public static bool TentarLerVersao(string? texto, out byte[] versao)
    {
        versao = new byte[8];
        return !string.IsNullOrWhiteSpace(texto) && Convert.TryFromBase64String(texto.Trim(), versao, out var escritos) && escritos == 8;
    }
}
