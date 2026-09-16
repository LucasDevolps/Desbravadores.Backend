using Microsoft.AspNetCore.Authorization;

namespace Almirante.Api.Security;

// Atributo de autorização por role, reutilizável em qualquer controller/endpoint (não específico
// de nenhuma feature). Uma única aplicação com vários papéis = "qualquer um destes passa" (OR),
// igual a [Authorize(Roles = "A,B,C")] — é só uma casca com constantes fortemente tipadas
// (Roles.*) no lugar de strings soltas. Empilhar duas aplicações deste atributo no mesmo endpoint
// exigiria as duas roles ao mesmo tempo (AND, do jeito que [Authorize] combina múltiplos
// atributos) — como cada usuário tem só uma role, isso nunca passaria; sempre use uma única
// aplicação com a lista completa de papéis aceitos.
//
// Qualquer endpoint que usa este atributo é automaticamente reconhecido por
// AcessoNegadoAuthorizationMiddlewareResultHandler, que converte a falha de autorização (sem
// token, token inválido/expirado, ou role insuficiente) em 401 "ACESSO NEGADO!" — sem precisar
// registrar nada em Program.cs.
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AutorizarRolesAttribute : AuthorizeAttribute
{
    public AutorizarRolesAttribute(params string[] roles)
    {
        Roles = string.Join(',', roles);
    }
}
