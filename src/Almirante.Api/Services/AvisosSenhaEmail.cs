using System.Globalization;
using Almirante.Api.Entities;
using Almirante.Api.Security;

namespace Almirante.Api.Services;

/// <summary>
/// Aviso posterior por e-mail de que a senha foi alterada (issue #159), preparado para quando houver provedor. Não há implementação
/// registrada nem envio: a chamada fica comentada depois do commit em <see cref="AlteracaoSenhaService"/>. O e-mail só avisa; nunca contém a
/// senha nem autentica a operação. O destinatário é resolvido no servidor pela implementação futura a partir do cadastro do titular, nunca
/// recebido do cliente. Avisos não são enfileirados enquanto o envio estiver desabilitado, para que nada antigo saia ao habilitar.
/// </summary>
public interface IAvisoSenhaAlteradaEmail
{
    Task EnviarAvisoSenhaAlteradaAsync(AvisoSenhaAlterada aviso, CancellationToken ct);
}

/// <summary>Dados do aviso: o titular e o que aconteceu, sem senha, token, IP ou endereço de e-mail.</summary>
public sealed record AvisoSenhaAlterada(Guid UsuarioId, TipoAlteracaoSenha Tipo, DateTime AlteradaEmUtc);

public sealed record MensagemEmail(string Assunto, string Corpo);

/// <summary>Texto do aviso de senha alterada, pronto para o provedor futuro.</summary>
public static class ModeloAvisoSenhaAlterada
{
    public static MensagemEmail Montar(string nomeTitular, TipoAlteracaoSenha tipo, DateTime alteradaEmUtc)
    {
        var quando = alteradaEmUtc.ToString("dd/MM/yyyy 'às' HH:mm 'UTC'", CultureInfo.InvariantCulture);
        var como = tipo switch
        {
            TipoAlteracaoSenha.Propria => "por você, com a senha anterior,",
            TipoAlteracaoSenha.Administrativa => "por um usuário autorizado da administração do clube",
            TipoAlteracaoSenha.RecuperacaoAssistida => "com uma autorização de recuperação emitida pela administração do clube",
            _ => "pela recuperação de acesso",
        };
        return new MensagemEmail(
            "Sua senha de acesso foi alterada",
            $"Olá, {nomeTitular}.\n\n" +
            $"A senha da sua conta no sistema do clube foi alterada {como} em {quando}. Todas as sessões abertas foram encerradas; " +
            "entre novamente com a nova senha.\n\n" +
            "Se você não reconhece esta ação, entre em contato com a administração do clube imediatamente.\n\n" +
            "Este aviso não contém sua senha e nenhuma resposta a ele é necessária.");
    }
}

/// <summary>
/// Contrato futuro de entrega do token de recuperação por um canal previamente verificado do titular (recuperação autônoma). Sem provedor
/// nem canal comprovado, a recuperação automática fica desabilitada: nada implementa esta interface, o pedido público nunca gera token e
/// nenhum token sai pela resposta anônima, por log ou pelo console. A recuperação disponível hoje é a assistida, com entrega presencial.
/// </summary>
public interface IEntregaTokenRecuperacaoSenha
{
    Task EntregarTokenRecuperacaoAsync(EntregaTokenRecuperacaoSenha entrega, CancellationToken ct);
}

public sealed record EntregaTokenRecuperacaoSenha(Guid UsuarioId, Guid RecuperacaoId, string Token, DateTime ExpiraEmUtc)
{
    public override string ToString() => $"{nameof(EntregaTokenRecuperacaoSenha)} {ConteudoSensivelAttribute.MarcadorOmitido}";
}
