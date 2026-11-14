namespace Almirante.Api.Entities;

/// <summary>
/// Cargo do clube e, nesta versão, também o perfil de acesso (issue #182): as permissões vêm de <see cref="CargoPermissao"/> e as regras sobre
/// outros cargos de <see cref="CargoRegraAlvo"/>. <see cref="Role"/> é normalizada, única e imutável depois da criação; Nome nunca decide acesso.
/// </summary>
public class Cargo
{
    public Guid Id { get; set; }
    public required string Nome { get; set; }
    public required string Descricao { get; set; }
    public bool Ativo { get; set; } = true;
    public required string CriadoPor { get; set; }
    public DateTime CriadoEm { get; set; } = DateTime.UtcNow;
    public DateTime? UltimaAtualizacao { get; set; }
    public required string Role { get; set; }

    /// <summary>Perfil administrativo de sistema: não é desativado, não perde as permissões reservadas e é o único que as recebe.</summary>
    public bool SistemaProtegido { get; set; }

    /// <summary>
    /// Versão otimista do cargo, das concessões e das regras por alvo dele: toda alteração administrativa incrementa e exige a versão lida
    /// (409 se mudou). Token de concorrência no EF.
    /// </summary>
    public long Versao { get; set; } = 1;
}
