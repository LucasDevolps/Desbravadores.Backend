namespace Almirante.Api.Options;

public class SeedOptions
{
    public const string SectionName = "SeedAdmin";

    public required string Nome { get; set; }
    public required string Email { get; set; }
    public required string Senha { get; set; }
}
