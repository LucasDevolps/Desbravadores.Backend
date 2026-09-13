namespace Almirante.Api.Entities;

public class Usuario
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string EmailNormalizado { get; set; }
    public required string SenhaHash { get; set; }
    public string Roles { get; set; } = "Admin";
    public DateTime DataCriacao { get; set; } = DateTime.UtcNow;
}
