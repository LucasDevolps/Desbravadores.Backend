using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Senhas;

/// <summary>
/// Regras estruturais das operações de senha. Mensagens fixas: nenhuma inclui o valor recebido (nada de {PropertyValue} ou
/// {ComparisonValue}). As regras que dependem do alvo (e-mail na senha, senha igual à atual) ficam no serviço, depois da autorização.
/// </summary>
public static class RegrasSenha
{
    /// <summary>Limite só contra abuso de tamanho: a senha atual não precisa atender à política nova para reautenticar.</summary>
    public const int SenhaAtualMaxima = 1024;

    public static void SenhaAtualDoSolicitante<T>(this AbstractValidator<T> validator, Func<T, string?> senhaAtual)
    {
        validator.RuleFor(x => senhaAtual(x))
            .Cascade(CascadeMode.Stop)
            .Must(s => !string.IsNullOrEmpty(s)).WithMessage("senhaAtualSolicitante é obrigatória.")
            .Must(s => s!.Length <= SenhaAtualMaxima).WithMessage("senhaAtualSolicitante é longa demais.")
            .OverridePropertyName(ContasSenha.CampoSenhaAtual);
    }

    public static void NovaSenhaConfirmada<T>(this AbstractValidator<T> validator, Func<T, string?> novaSenha, Func<T, string?> confirmacao)
    {
        validator.RuleFor(x => x).Custom((pedido, context) =>
        {
            foreach (var erro in PasswordPolicy.Validate(novaSenha(pedido)))
            {
                context.AddFailure(ContasSenha.CampoNovaSenha, erro);
            }
        });

        validator.RuleFor(x => confirmacao(x))
            .Must(c => !string.IsNullOrEmpty(c)).WithMessage("confirmacaoNovaSenha é obrigatória.")
            .OverridePropertyName("confirmacaoNovaSenha");

        // Igualdade ordinal exata: sem trim, sem diferenciar caixa e sem normalização.
        validator.RuleFor(x => x)
            .Must(x => string.Equals(novaSenha(x), confirmacao(x), StringComparison.Ordinal))
            .WithMessage("confirmacaoNovaSenha deve ser idêntica a novaSenha.")
            .OverridePropertyName("confirmacaoNovaSenha")
            .When(x => !string.IsNullOrEmpty(novaSenha(x)) && !string.IsNullOrEmpty(confirmacao(x)));
    }
}

public sealed class AlterarPropriaSenhaRequestValidator : AbstractValidator<AlterarPropriaSenhaRequest>
{
    public AlterarPropriaSenhaRequestValidator()
    {
        this.SenhaAtualDoSolicitante(x => x.SenhaAtualSolicitante);
        this.NovaSenhaConfirmada(x => x.NovaSenha, x => x.ConfirmacaoNovaSenha);
    }
}

public sealed class AlterarSenhaUsuarioRequestValidator : AbstractValidator<AlterarSenhaUsuarioRequest>
{
    public AlterarSenhaUsuarioRequestValidator()
    {
        this.SenhaAtualDoSolicitante(x => x.SenhaAtualSolicitante);
        this.NovaSenhaConfirmada(x => x.NovaSenha, x => x.ConfirmacaoNovaSenha);
        RuleFor(x => x.MetodoVerificacao).IsInEnum().WithMessage(MetodoInvalido).When(x => x.MetodoVerificacao is not null)
            .OverridePropertyName("metodoVerificacao");
    }

    internal const string MetodoInvalido =
        "metodoVerificacao deve ser AtendimentoPresencial ou ProcedimentoIndependenteEstabelecido.";
}

public sealed class EmitirAutorizacaoRecuperacaoRequestValidator : AbstractValidator<EmitirAutorizacaoRecuperacaoRequest>
{
    public EmitirAutorizacaoRecuperacaoRequestValidator()
    {
        this.SenhaAtualDoSolicitante(x => x.SenhaAtualSolicitante);
        RuleFor(x => x.MetodoVerificacao)
            .Cascade(CascadeMode.Stop)
            .NotNull().WithMessage("metodoVerificacao é obrigatório: informe como a identidade do titular foi confirmada.")
            .IsInEnum().WithMessage(AlterarSenhaUsuarioRequestValidator.MetodoInvalido)
            .OverridePropertyName("metodoVerificacao");
    }
}

public sealed class SolicitarRecuperacaoSenhaRequestValidator : AbstractValidator<SolicitarRecuperacaoSenhaRequest>
{
    public SolicitarRecuperacaoSenhaRequestValidator()
    {
        RuleLevelCascadeMode = CascadeMode.Stop;
        RuleFor(x => x.Email)
            .Must(e => !string.IsNullOrWhiteSpace(e)).WithMessage("email é obrigatório.")
            .Must(e => e!.Trim().Length <= UsuariosService.EmailMaximo && UsuariosService.SomenteAsciiImprimivel(e.Trim()))
            .WithMessage("email em formato inválido.")
            .Must(UsuariosService.EmailValido).WithMessage("email em formato inválido.")
            .OverridePropertyName("email");
    }
}

public sealed class ConfirmarRecuperacaoSenhaRequestValidator : AbstractValidator<ConfirmarRecuperacaoSenhaRequest>
{
    public ConfirmarRecuperacaoSenhaRequestValidator()
    {
        RuleFor(x => x.Token)
            .Cascade(CascadeMode.Stop)
            .Must(t => !string.IsNullOrEmpty(t)).WithMessage("token é obrigatório.")
            .Must(t => t!.Length <= 256).WithMessage("token em formato inválido.")
            .OverridePropertyName("token");
        this.NovaSenhaConfirmada(x => x.NovaSenha, x => x.ConfirmacaoNovaSenha);
    }
}

public sealed class ListarRecuperacoesSenhaPendentesQueryValidator : AbstractValidator<ListarRecuperacoesSenhaPendentesQuery>
{
    public ListarRecuperacoesSenhaPendentesQueryValidator()
    {
        RuleFor(x => x.PageSize).InclusiveBetween(1, NotificacoesService.PageSizeMaximo).WithName("pageSize")
            .WithMessage($"pageSize deve estar entre 1 e {NotificacoesService.PageSizeMaximo}.");
        RuleFor(x => x.Cursor).Must(c => CursorNotificacoes.TentarLer(c, out _, out _)).WithName("cursor")
            .WithMessage("cursor inválido: use o proximoCursor devolvido pela página anterior.")
            .When(x => x.Cursor is not null);
    }
}
