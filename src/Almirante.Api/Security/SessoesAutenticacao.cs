using System.Linq.Expressions;
using Almirante.Api.Entities;

namespace Almirante.Api.Security;

public static class SessoesAutenticacao
{
    /// <summary>
    /// Sessão utilizável: não revogada, dentro do prazo absoluto, de usuário ativo, com cargo ativo e a mesma versão de segurança
    /// do usuário. Usada na validação de cada token e na revalidação periódica das conexões persistentes do hub.
    /// </summary>
    public static Expression<Func<AuthSession, bool>> Vigente(DateTime agoraUtc) => x =>
        x.RevokedAtUtc == null && x.AbsoluteExpiresAtUtc > agoraUtc && x.Usuario != null && x.Usuario.Ativo &&
        x.Usuario.SecurityVersion == x.SecurityVersion && x.Usuario.Cargo != null && x.Usuario.Cargo.Ativo;
}
