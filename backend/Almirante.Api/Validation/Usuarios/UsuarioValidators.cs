using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Usuarios;

// Regras de formato comuns a POST e PUT. Unicidade do e-mail e existência/atividade do cargo dependem do banco e
// são verificadas em UsuariosService.
public abstract class UsuarioConteudoValidator<T> : AbstractValidator<T> where T : UsuarioConteudoRequest
{
    protected UsuarioConteudoValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;

        RuleFor(x => x.Nome)
            .Must(nome => !string.IsNullOrWhiteSpace(nome)).WithMessage("nome é obrigatório.")
            .Must(nome => nome!.Trim().Length <= UsuariosService.NomeMaximo)
            .WithMessage($"nome deve ter no máximo {UsuariosService.NomeMaximo} caracteres.");

        RuleFor(x => x.Email)
            .Must(email => !string.IsNullOrWhiteSpace(email)).WithMessage("email é obrigatório.")
            .Must(email => email!.Trim().Length <= UsuariosService.EmailMaximo)
            .WithMessage($"email deve ter no máximo {UsuariosService.EmailMaximo} caracteres.")
            .Must(UsuariosService.EmailValido).WithMessage("email em formato inválido.");

        RuleFor(x => x.CargoId).Must(cargoId => cargoId is not null && cargoId != Guid.Empty).WithMessage("cargoId é obrigatório.");
    }
}

public sealed class RegistrarUsuarioRequestValidator : UsuarioConteudoValidator<RegistrarUsuarioRequest>
{
    public RegistrarUsuarioRequestValidator()
    {
        // Mesma política usada no bootstrap do administrador; nunca ecoa a senha na mensagem.
        RuleFor(x => x.Senha).Custom((senha, context) =>
        {
            foreach (var erro in PasswordPolicy.Validate(senha, context.InstanceToValidate.Email?.Trim()))
            {
                context.AddFailure(erro);
            }
        });
    }
}

public sealed class UpdateUsuarioRequestValidator : UsuarioConteudoValidator<UpdateUsuarioRequest>;
