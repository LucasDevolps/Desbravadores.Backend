namespace Almirante.Api.Security;

// Escopo de autorização do lançamento geral (LancamentosGeraisController): criação, listagem e
// exclusão exigem uma das roles abaixo (claim ClaimTypes.Role, emitida a partir de
// Usuario.Cargo.Role em JwtTokenService — o mesmo mecanismo de claims já usado no restante da
// API). A policy é registrada em Program.cs e combinada com
// AcessoNegadoAuthorizationMiddlewareResultHandler para transformar tanto "não autenticado"
// quanto "autenticado sem role permitida" em 401 "ACESSO NEGADO!" — mas somente para os
// endpoints que declaram esta policy; os demais endpoints continuam com o comportamento padrão
// (401 sem role / 403 com role insuficiente).
public static class LancamentoGeralAuthorization
{
    public const string PolicyName = "LancamentoGeral";

    public static readonly string[] RolesPermitidas = ["ADM", "DIR", "DIRA", "SEC", "TES"];
}
