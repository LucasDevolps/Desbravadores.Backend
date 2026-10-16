using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Almirante.Api.Infrastructure;

// Falha de negócio com status HTTP explícito (404/409/400 por campo), lançada por serviços chamados a partir
// de handlers do MediatR e convertida em ProblemDetails por ApiProblemExceptionHandler. Evita espalhar
// "outcomes" pelo controller e mantém a resposta de erro centralizada (mesmo padrão do
// ValidationExceptionHandler para FluentValidation).
public sealed class ApiProblemException(int status, string title, string? detail = null,
    IReadOnlyDictionary<string, string[]>? errors = null, IReadOnlyDictionary<string, object?>? extensions = null)
    : Exception(title)
{
    public int Status { get; } = status;
    public string Title { get; } = title;
    public string? Detail { get; } = detail;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public IReadOnlyDictionary<string, object?>? Extensions { get; } = extensions;

    public static ApiProblemException NotFound(string title, string? detail = null) => new(StatusCodes.Status404NotFound, title, detail);
    public static ApiProblemException Conflict(string title, string? detail = null, IReadOnlyDictionary<string, object?>? extensions = null) =>
        new(StatusCodes.Status409Conflict, title, detail, extensions: extensions);
    public static ApiProblemException Invalid(string field, string message) =>
        new(StatusCodes.Status400BadRequest, "Dados inválidos.", errors: new Dictionary<string, string[]> { [field] = [message] });
}

public sealed class ApiProblemExceptionHandler(ILogger<ApiProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is not ApiProblemException problem)
        {
            return false;
        }

        // Evento de segurança/negócio (ASVS 16.3): toda recusa fica registrada com quem, onde e por quê. Nada do corpo
        // da requisição, tokens ou cookies entra no log; só método, rota, status, título e o usuário das claims.
        logger.LogWarning("Operação recusada {Status} {Titulo}: {Metodo} {Rota} (usuário {UsuarioId})",
            problem.Status, problem.Title, httpContext.Request.Method, httpContext.Request.Path.Value,
            httpContext.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? "anônimo");

        ProblemDetails body = problem.Errors is null
            ? new ProblemDetails { Title = problem.Title, Detail = problem.Detail, Status = problem.Status }
            : new ValidationProblemDetails(problem.Errors.ToDictionary(e => e.Key, e => e.Value))
            { Title = problem.Title, Detail = problem.Detail, Status = problem.Status };
        foreach (var (key, value) in problem.Extensions ?? new Dictionary<string, object?>())
        {
            body.Extensions[key] = value;
        }

        httpContext.Response.StatusCode = problem.Status;
        await httpContext.Response.WriteAsJsonAsync(body, body.GetType(), options: null, contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}
