using System.Text;

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
