using System.Text;

namespace Almirante.Api.Validation.Usuarios;

// Colunas de texto novas são varchar (nunca nvarchar, por desempenho) e usam a página de código da collation do banco:
// 1252 (Latin1), que cobre o português. Um caractere fora dela seria trocado em silêncio por '?' ou por uma aproximação
// na gravação, então a entrada é recusada antes (a migration AddUsuariosResponsavelEFoto exige banco na página 1252).
public static class TextoVarchar
{
    private static readonly Encoding Pagina1252 = CriarPagina1252();

    // Caracteres de controle (C0, DEL e C1) também são recusados: não fazem parte de um nome. Em texto de várias linhas
    // (doenças/alergias, um textarea) só a quebra de linha (\r e \n) é aceita.
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
