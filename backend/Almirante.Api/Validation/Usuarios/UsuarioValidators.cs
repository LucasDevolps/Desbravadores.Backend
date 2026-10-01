using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Usuarios;

// Regras de formato comuns a POST e PUT. Unicidade do e-mail e do CPF e existência/atividade do cargo dependem do banco
// e são verificadas em UsuariosService. Os dados pessoais são opcionais: só o valor informado é validado.
public abstract class UsuarioConteudoValidator<T> : AbstractValidator<T> where T : UsuarioConteudoRequest
{
    protected UsuarioConteudoValidator(TimeProvider clock)
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
            .Must(email => UsuariosService.SomenteAsciiImprimivel(email!.Trim()))
            .WithMessage("email deve conter apenas caracteres ASCII (sem acentos).")
            .Must(UsuariosService.EmailValido).WithMessage("email em formato inválido.");

        RuleFor(x => x.CargoId).Must(cargoId => cargoId is not null && cargoId != Guid.Empty).WithMessage("cargoId é obrigatório.");

        // O limite vale para o valor que será gravado (já sem máscara e espaços externos).
        RuleFor(x => UsuariosService.NormalizarCpf(x.Cpf))
            .Must(cpf => cpf!.Length <= UsuariosService.CpfMaximo)
            .WithMessage($"cpf deve ter no máximo {UsuariosService.CpfMaximo} caracteres, desconsiderando pontos, hífen e espaços externos.")
            .Must(cpf => UsuariosService.SomenteAsciiImprimivel(cpf!))
            .WithMessage("cpf contém caracteres não suportados.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.Cpf))
            .When(x => UsuariosService.NormalizarCpf(x.Cpf) is not null);

        RuleFor(x => UsuariosService.NormalizarTelefone(x.Telefone))
            .Must(telefone => telefone!.Length <= UsuariosService.TelefoneMaximo)
            .WithMessage($"telefone deve ter no máximo {UsuariosService.TelefoneMaximo} caracteres.")
            .Must(telefone => UsuariosService.SomenteAsciiImprimivel(telefone!))
            .WithMessage("telefone contém caracteres não suportados.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.Telefone))
            .When(x => UsuariosService.NormalizarTelefone(x.Telefone) is not null);

        // Mesma referência de "hoje" do restante da API (data UTC do servidor, TimeProvider). No Brasil a data UTC
        // nunca é anterior à local, então quem nasceu hoje (horário local) nunca é recusado.
        RuleFor(x => x.DataNascimento!.Value)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .WithMessage("dataNascimento não pode ser uma data futura.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.DataNascimento))
            .When(x => x.DataNascimento.HasValue);
    }
}

public sealed class RegistrarUsuarioRequestValidator : UsuarioConteudoValidator<RegistrarUsuarioRequest>
{
    public RegistrarUsuarioRequestValidator(TimeProvider clock) : base(clock)
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

public sealed class UpdateUsuarioRequestValidator(TimeProvider clock) : UsuarioConteudoValidator<UpdateUsuarioRequest>(clock);
