using System.ComponentModel.DataAnnotations;

namespace Almirante.Api.Options;

/// <summary>Limites das operações de senha (issue #159). Todos têm padrão seguro; nenhuma variável de ambiente é obrigatória.</summary>
public sealed class SenhasOptions
{
    public const string SectionName = "Senhas";

    /// <summary>Tentativas de troca (própria ou de terceiro) e de emissão de autorização por usuário autenticado, por janela.</summary>
    [Range(1, 10_000)]
    public int TentativasPorUsuario { get; set; } = 5;

    /// <summary>Mesmas tentativas somadas por IP observado, por janela (vários usuários atrás do mesmo IP).</summary>
    [Range(1, 10_000)]
    public int TentativasPorIp { get; set; } = 20;

    /// <summary>Pedidos e conclusões de recuperação sem login por IP observado, por janela.</summary>
    [Range(1, 10_000)]
    public int RecuperacoesPorIp { get; set; } = 10;

    [Range(1, 1440)]
    public int JanelaMinutos { get; set; } = 15;

    /// <summary>Quanto tempo um pedido público fica nas pendências antes de expirar sem atendimento.</summary>
    [Range(1, 30)]
    public int ValidadeSolicitacaoDias { get; set; } = 7;
}
