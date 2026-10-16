namespace Almirante.Api.Dtos;

public class CargoDto
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public bool Ativo { get; set; }
    public required string CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; }
    public DateTime? UltimaAtualizacao { get; set; }
    public required string Role { get; set; }
}
