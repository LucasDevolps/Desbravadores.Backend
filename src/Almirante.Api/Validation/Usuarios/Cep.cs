namespace Almirante.Api.Validation.Usuarios;

public static class Cep
{
    public const int Digitos = 8;

    /// <summary>Chave do erro de CEP do endereço do usuário no ValidationProblemDetails.</summary>
    public const string CampoErro = "Endereco.Cep";

    /// <summary>Remove espaços externos e o hífen da máscara 00000-000; não completa nem corta dígitos (zeros à esquerda ficam).</summary>
    public static string? Normalizar(string? cep)
    {
        var valor = cep?.Trim();
        if (string.IsNullOrEmpty(valor))
        {
            return null;
        }

        return valor.Length == Digitos + 1 && valor[5] == '-' ? valor.Remove(5, 1) : valor;
    }

    public static bool EhValido(string? cepNormalizado) =>
        cepNormalizado is { Length: Digitos } && cepNormalizado.All(char.IsAsciiDigit);
}

/// <summary>Tamanhos das colunas varchar do endereço (Usuarios e _usuarios_hist).</summary>
public static class Endereco
{
    public const int NumeroMaximo = 20;
    public const int ComplementoMaximo = 100;

    public const int LogradouroMaximo = 200;
    public const int ComplementoCepMaximo = 200;
    public const int UnidadeMaximo = 200;
    public const int BairroMaximo = 100;
    public const int LocalidadeMaximo = 100;
    public const int UfTamanho = 2;
    public const int EstadoMaximo = 50;
    public const int RegiaoMaximo = 20;
    public const int IbgeMaximo = 7;
    public const int GiaMaximo = 10;
    public const int DddMaximo = 2;
    public const int SiafiMaximo = 10;
}
