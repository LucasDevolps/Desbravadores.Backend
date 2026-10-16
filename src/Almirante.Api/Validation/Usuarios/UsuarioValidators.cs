using Almirante.Api.Dtos;
using Almirante.Api.Security;
using Almirante.Api.Services;
using FluentValidation;

namespace Almirante.Api.Validation.Usuarios;

// Regras de formato comuns a POST e PUT. Unicidade do e-mail e do CPF e existência/atividade do cargo dependem do banco
// e são verificadas em UsuariosService. Dados pessoais e do responsável são opcionais aqui: só o valor informado é
// validado (o CPF é obrigatório só no POST, em RegistrarUsuarioRequestValidator). A obrigatoriedade do responsável para menor de idade (RegraResponsavel) é checada no POST abaixo e, no PUT,
// em UsuariosService, com os valores efetivos (o que não veio no corpo é lido do banco).
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

        CpfOpcional(x => x.Cpf, nameof(UsuarioConteudoRequest.Cpf), "cpf");
        TelefoneOpcional(x => x.Telefone, nameof(UsuarioConteudoRequest.Telefone), "telefone");

        // Mesma referência de "hoje" do restante da API (data UTC do servidor, TimeProvider). No Brasil a data UTC
        // nunca é anterior à local, então quem nasceu hoje (horário local) nunca é recusado.
        RuleFor(x => x.DataNascimento!.Value)
            .LessThanOrEqualTo(_ => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime))
            .WithMessage("dataNascimento não pode ser uma data futura.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.DataNascimento))
            .When(x => x.DataNascimento.HasValue);

        // Responsável: mesmas regras de formato dos campos equivalentes do usuário. O nome vai para varchar, então
        // também precisa caber na página de código 1252 (acentos do português cabem; emoji e outros alfabetos não).
        RuleFor(x => UsuariosService.NormalizarTexto(x.ResponsavelNome))
            .Must(nome => nome!.Length <= UsuariosService.NomeMaximo)
            .WithMessage($"responsavelNome deve ter no máximo {UsuariosService.NomeMaximo} caracteres.")
            .Must(nome => TextoVarchar.CabeNaPagina1252(nome!))
            .WithMessage("responsavelNome contém caracteres não suportados (use letras do alfabeto latino, com ou sem acento; sem emojis).")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.ResponsavelNome))
            .When(x => UsuariosService.NormalizarTexto(x.ResponsavelNome) is not null);

        RuleFor(x => UsuariosService.NormalizarTexto(x.ResponsavelEmail))
            .Must(email => email!.Length <= UsuariosService.EmailMaximo)
            .WithMessage($"responsavelEmail deve ter no máximo {UsuariosService.EmailMaximo} caracteres.")
            .Must(email => UsuariosService.SomenteAsciiImprimivel(email!))
            .WithMessage("responsavelEmail deve conter apenas caracteres ASCII (sem acentos).")
            .Must(email => UsuariosService.EmailValido(email)).WithMessage("responsavelEmail em formato inválido.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.ResponsavelEmail))
            .When(x => UsuariosService.NormalizarTexto(x.ResponsavelEmail) is not null);

        CpfOpcional(x => x.ResponsavelCpf, nameof(UsuarioConteudoRequest.ResponsavelCpf), "responsavelCpf");
        TelefoneOpcional(x => x.ResponsavelTelefone, nameof(UsuarioConteudoRequest.ResponsavelTelefone), "responsavelTelefone");

        // RG e tipo sanguíneo: opcionais; só o valor informado (normalizado) é validado.
        RuleFor(x => Rg.Normalizar(x.Rg))
            .Must(rg => rg!.Length <= Rg.Maximo).WithMessage($"rg deve ter no máximo {Rg.Maximo} caracteres.")
            .Must(Rg.EhValido)
            .WithMessage("rg inválido: use letras sem acento, dígitos, ponto, hífen, barra ou espaço, com pelo menos um dígito.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.Rg))
            .When(x => Rg.Normalizar(x.Rg) is not null);

        RuleFor(x => TipoSanguineo.Normalizar(x.TipoSanguineo))
            .Must(TipoSanguineo.EhValido)
            .WithMessage($"tipoSanguineo inválido: informe um de {string.Join(", ", TipoSanguineo.Valores)} ou deixe em branco.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.TipoSanguineo))
            .When(x => TipoSanguineo.Normalizar(x.TipoSanguineo) is not null);

        // Doenças/alergias: texto de várias linhas em varchar. Acima do limite é recusado (nunca truncado em silêncio).
        RuleFor(x => UsuariosService.NormalizarTexto(x.DoencasAlergias))
            .Must(texto => texto!.Length <= UsuariosService.DoencasAlergiasMaximo)
            .WithMessage($"doencasAlergias deve ter no máximo {UsuariosService.DoencasAlergiasMaximo} caracteres.")
            .Must(texto => TextoVarchar.CabeNaPagina1252(texto!, permitirQuebraDeLinha: true))
            .WithMessage("doencasAlergias contém caracteres não suportados (use letras do alfabeto latino, com ou sem acento; sem emojis).")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.DoencasAlergias))
            .When(x => UsuariosService.NormalizarTexto(x.DoencasAlergias) is not null);
    }

    // CPF e telefone, do usuário e do responsável: validados sobre o valor que será gravado (sem máscara e sem espaços
    // externos), só quando informados.
    private void CpfOpcional(Func<T, string?> campo, string propriedade, string nomeJson) =>
        RuleFor(x => UsuariosService.NormalizarCpf(campo(x)))
            .Must(Cpf.EhValido)
            .WithMessage($"{nomeJson} inválido: informe os 11 dígitos de um CPF válido (com ou sem pontos e hífen).")
            .OverridePropertyName(propriedade)
            .When(x => UsuariosService.NormalizarCpf(campo(x)) is not null);

    private void TelefoneOpcional(Func<T, string?> campo, string propriedade, string nomeJson) =>
        RuleFor(x => UsuariosService.NormalizarTelefone(campo(x)))
            .Must(TelefoneBrasileiro.EhValido)
            .WithMessage($"{nomeJson} inválido: informe DDD e número, fixo com 10 dígitos ou celular com 11 começando com 9 (com ou sem máscara).")
            .OverridePropertyName(propriedade)
            .When(x => UsuariosService.NormalizarTelefone(campo(x)) is not null);
}

public sealed class RegistrarUsuarioRequestValidator : UsuarioConteudoValidator<RegistrarUsuarioRequest>
{
    public RegistrarUsuarioRequestValidator(TimeProvider clock, DataCivil dataCivil) : base(clock)
    {
        // CPF é o documento obrigatório do cadastro (o formato é validado na regra comum, quando informado). No PUT ele
        // continua opcional para usuários antigos sem CPF; remover um CPF gravado é recusado em UsuariosService.
        RuleFor(x => UsuariosService.NormalizarCpf(x.Cpf))
            .NotNull().WithMessage("cpf é obrigatório.")
            .OverridePropertyName(nameof(UsuarioConteudoRequest.Cpf));

        // Mesma política usada no bootstrap do administrador; nunca ecoa a senha na mensagem.
        RuleFor(x => x.Senha).Custom((senha, context) =>
        {
            foreach (var erro in PasswordPolicy.Validate(senha, context.InstanceToValidate.Email?.Trim()))
            {
                context.AddFailure(erro);
            }
        });

        // No cadastro não existe valor gravado: a regra do responsável usa só o corpo.
        RuleFor(x => x).Custom((request, context) =>
        {
            var erros = RegraResponsavel.Validar(request.DataNascimento,
                UsuariosService.NormalizarTexto(request.ResponsavelNome), UsuariosService.NormalizarTexto(request.ResponsavelEmail),
                UsuariosService.NormalizarCpf(request.ResponsavelCpf), UsuariosService.NormalizarCpf(request.Cpf), dataCivil.Hoje());
            foreach (var (campo, mensagens) in erros)
            {
                foreach (var mensagem in mensagens)
                {
                    context.AddFailure(campo, mensagem);
                }
            }
        });
    }
}

public sealed class UpdateUsuarioRequestValidator(TimeProvider clock) : UsuarioConteudoValidator<UpdateUsuarioRequest>(clock);
