using FluentValidation;
using MediatR;

namespace Almirante.Api.Validation;

// Roda, antes de qualquer Handler, todos os IValidator<TRequest> registrados para o request do
// MediatR (ver Program.cs: AddValidatorsFromAssemblyContaining + AddOpenBehavior). Falhas viram
// FluentValidation.ValidationException, tratada globalmente por ValidationExceptionHandler — os
// Handlers em si nunca precisam validar entrada.
public sealed class ValidationBehavior<TRequest, TResponse>(IEnumerable<IValidator<TRequest>> validators)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : notnull
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        if (!validators.Any())
        {
            return await next();
        }

        var context = new ValidationContext<TRequest>(request);

        var failures = (await Task.WhenAll(validators.Select(v => v.ValidateAsync(context, cancellationToken))))
            .SelectMany(result => result.Errors)
            .Where(failure => failure is not null)
            .ToList();

        if (failures.Count > 0)
        {
            throw new ValidationException(failures);
        }

        return await next();
    }
}
