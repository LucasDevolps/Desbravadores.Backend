using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Almirante.Api.Infrastructure;

// Resposta 400 do model binding SEM detalhes internos. O texto padrão do System.Text.Json para valores que não
// convertem ("The JSON value could not be converted to System.Nullable`1[System.Decimal]") revela nomes de tipos
// CLR e namespaces da aplicação a qualquer cliente. Mantém o nome do campo e mensagens próprias da API (validators
// e conversores); troca só o que expõe tipos internos por um texto genérico.
public static partial class ModelBindingErrors
{
    private const string MensagemGenerica = "O valor informado é inválido ou está em formato incorreto para este campo.";

    public static IActionResult Responder(ActionContext context)
    {
        var limpo = new ModelStateDictionary();
        foreach (var (chave, entrada) in context.ModelState)
        {
            foreach (var erro in entrada.Errors)
            {
                var mensagem = string.IsNullOrEmpty(erro.ErrorMessage) ? erro.Exception?.Message : erro.ErrorMessage;
                limpo.AddModelError(chave, Sanitizar(mensagem));
            }
        }

        var factory = context.HttpContext.RequestServices.GetRequiredService<ProblemDetailsFactory>();
        var problema = factory.CreateValidationProblemDetails(context.HttpContext, limpo);
        return new BadRequestObjectResult(problema) { ContentTypes = { "application/problem+json", "application/problem+xml" } };
    }

    public static string Sanitizar(string? mensagem)
    {
        if (string.IsNullOrWhiteSpace(mensagem))
        {
            return MensagemGenerica;
        }

        var revelaTipos = mensagem.Contains("could not be converted", StringComparison.OrdinalIgnoreCase)
            || NomeDeTipoClr().IsMatch(mensagem);
        return revelaTipos ? MensagemGenerica : mensagem;
    }

    // Nome qualificado de tipo CLR (System.Nullable`1[System.Decimal], Almirante.Api.Dtos.X, Microsoft.…) ou de
    // exceção (FooException). Casa a forma do vazamento, não prefixos soltos, então frases da API como
    // "membros inválidos." passam intactas.
    [GeneratedRegex(@"\b[A-Za-z_]\w*(\.[A-Za-z_]\w*)*\.[A-Z]\w*(`\d+)?|\b\w+Exception\b")]
    private static partial Regex NomeDeTipoClr();
}
