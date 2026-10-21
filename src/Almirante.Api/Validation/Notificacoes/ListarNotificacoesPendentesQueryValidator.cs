using Almirante.Api.Dtos;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Notificacoes;

public sealed class ListarNotificacoesPendentesQueryValidator : AbstractValidator<ListarNotificacoesPendentesQuery>
{
    public ListarNotificacoesPendentesQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, NotificacoesService.PageSizeMaximo).WithName("pageSize")
            .WithMessage($"pageSize deve estar entre 1 e {NotificacoesService.PageSizeMaximo}.");
        RuleFor(x => x.Cursor).Must(c => CursorNotificacoes.TentarLer(c, out _, out _)).WithName("cursor")
            .WithMessage("cursor inválido: use o proximoCursor devolvido pela página anterior.")
            .When(x => x.Cursor is not null);
    }
}
