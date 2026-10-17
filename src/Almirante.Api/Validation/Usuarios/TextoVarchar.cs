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

    private static Encoding CriarPagina1252()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }
}
