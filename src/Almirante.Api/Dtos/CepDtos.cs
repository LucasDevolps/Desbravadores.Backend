namespace Almirante.Api.Dtos;

/// <summary>
/// Endereço de um CEP como o ViaCEP o devolve. cep vem só com os 8 dígitos (zeros à esquerda preservados); os demais campos vêm
/// sem espaços externos e null quando o ViaCEP não os informa ou os devolve vazios (nenhum valor é inventado).
/// </summary>
public sealed record CepDto
{
    public required string Cep { get; init; }
    public string? Logradouro { get; init; }
    /// <summary>Complemento do próprio CEP (ex.: "lado ímpar", "até 999/1000"), não o complemento do endereço do usuário.</summary>
    public string? Complemento { get; init; }
    public string? Unidade { get; init; }
    public string? Bairro { get; init; }
    public required string Localidade { get; init; }
    public required string Uf { get; init; }
    public string? Estado { get; init; }
    public string? Regiao { get; init; }
    public string? Ibge { get; init; }
    public string? Gia { get; init; }
    public string? Ddd { get; init; }
    public string? Siafi { get; init; }
}
