using System.Text;
using FluentValidation;

namespace Almirante.Api.Validation.Usuarios;

public static class TextoVarchar
{
    private static readonly Encoding Pagina1252 = CriarPagina1252();

    public static bool CabeNaPagina1252(string texto, bool permitirQuebraDeLinha = false)
    {
        if (texto.Any(c => char.IsControl(c) && !(permitirQuebraDeLinha && c is '\r' or '\n')))
        {
            return false;
        }

        try
        {
            Pagina1252.GetByteCount(texto);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>Texto livre gravado em coluna varchar(<paramref name="maximo"/>): limita o tamanho e recusa o que não cabe na página 1252.</summary>
    public static IRuleBuilderOptions<T, string> CabeEmVarchar<T>(this IRuleBuilder<T, string> regra, string campo, int maximo) =>
        regra.Must(texto => texto.Length <= maximo).WithMessage($"{campo} deve ter no máximo {maximo} caracteres.")
            .Must(texto => CabeNaPagina1252(texto)).WithMessage(ForaDaPagina1252(campo));

    public static string ForaDaPagina1252(string campo) =>
        $"{campo} contém caracteres não suportados (use letras do alfabeto latino, com ou sem acento; sem emojis).";

    private static readonly Encoding Pagina1252Substituta = CriarPagina1252(new EncoderReplacementFallback("?"));

    /// <summary>
    /// Para texto gerado pelo sistema a partir de dados já gravados (que não pode ser recusado): troca controles por espaço e
    /// o que não existe na página 1252 por '?', de forma explícita, em vez de deixar a conversão implícita do SQL Server decidir.
    /// </summary>
    public static string ParaPagina1252(string texto)
    {
        var resultado = new StringBuilder(texto.Length);
        foreach (var caractere in texto.EnumerateRunes())
        {
            var original = caractere.ToString();
            var convertido = Pagina1252Substituta.GetString(Pagina1252Substituta.GetBytes(original));
            resultado.Append(Rune.IsControl(caractere) ? " " : convertido == original ? original : "?");
        }

        return resultado.ToString();
    }

    private static Encoding CriarPagina1252(EncoderFallback? fallback = null)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, fallback ?? EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
