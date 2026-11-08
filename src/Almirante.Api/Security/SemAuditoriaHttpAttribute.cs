namespace Almirante.Api.Security;

/// <summary>
/// Endpoint inteiramente fora da auditoria HTTP (issue #158): nem metadados são gravados, e corpo, resposta, cabeçalhos e exceção nunca
/// são lidos pelo middleware. Só o login (POST /api/Auth/login): o caminho também é excluído por comparação normalizada (caixa, barras),
/// valendo mesmo quando o roteamento não chega ao endpoint (405, 404).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class SemAuditoriaHttpAttribute : Attribute;
