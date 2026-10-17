using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Almirante.Api.Infrastructure;

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

    [GeneratedRegex(@"\b[A-Za-z_]\w*(\.[A-Za-z_]\w*)*\.[A-Z]\w*(`\d+)?|\b\w+Exception\b")]
    private static partial Regex NomeDeTipoClr();
}
