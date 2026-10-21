using System.Reflection;
using Almirante.Api.Dtos;
using Almirante.Api.Options;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

/// <summary>
/// Testes unitários da trava de texto do cadastro de usuários: todo campo string gravado vai para coluna varchar (página 1252),
/// então emoji, ideograma ou caractere de controle é recusado com 400 em vez de virar "?" no banco ou passar por uma coluna
/// nvarchar legada (Usuarios.Nome, achado do E2E de 2026-10-21). A varredura por reflexão falha se um campo string novo for
/// adicionado ao request sem validação.
/// </summary>
public sealed class UsuariosTextoVarcharUnitTests
{
    // Senha não é gravada (só o hash), então pode ter qualquer caractere.
    private static readonly HashSet<string> CamposSemTexto = [nameof(RegistrarUsuarioRequest.Senha)];

    private static readonly RegistrarUsuarioRequestValidator Registrar =
        new(TimeProvider.System, new DataCivil(TimeProvider.System, Microsoft.Extensions.Options.Options.Create(new ClubeOptions())));

    private static readonly UpdateUsuarioRequestValidator Atualizar = new(TimeProvider.System);

    private static T Valido<T>() where T : UsuarioConteudoRequest, new() => new()
    {
        Nome = "José Ação Conceição", Email = "jose@local.dev", CargoId = Guid.NewGuid(), Cpf = TestHelpers.NovoCpf(),
        DataNascimento = new DateOnly(2000, 1, 15), Telefone = "11987654321", Rg = "12.345.678-9", TipoSanguineo = "O+",
        DoencasAlergias = "Alergia a amendoim.\nAsma leve.", ResponsavelNome = "Maria Conceição", ResponsavelEmail = "maria@local.dev",
        ResponsavelCpf = TestHelpers.NovoCpf(), ResponsavelTelefone = "1132345678",
        Endereco = new EnderecoRequest { Cep = "01001-000", Numero = "100", Complemento = "Apto 2" },
    };

    private static RegistrarUsuarioRequest ValidoParaRegistro()
    {
        var request = Valido<RegistrarUsuarioRequest>();
        request.Senha = "Clube-Forte-2026!x";
        return request;
    }

    private static IEnumerable<PropertyInfo> CamposTexto(Type tipo) =>
        tipo.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.PropertyType == typeof(string) && p.CanWrite && !CamposSemTexto.Contains(p.Name));

    public static TheoryData<string> Invalidos => ["😀", "中", "\u0007"];

    [Fact]
    public void RequestValido_ComAcentosDaPagina1252_EhAceito()
    {
        Assert.True(Registrar.Validate(ValidoParaRegistro()).IsValid, string.Join("; ", Registrar.Validate(ValidoParaRegistro()).Errors));
        Assert.True(Atualizar.Validate(Valido<UpdateUsuarioRequest>()).IsValid);
    }

    [Theory]
    [MemberData(nameof(Invalidos))]
    public void Nome_ForaDaPagina1252_EhRecusado_NoCadastroENaEdicao(string invalido)
    {
        var registro = ValidoParaRegistro();
        registro.Nome = $"Nome {invalido}";
        var edicao = Valido<UpdateUsuarioRequest>();
        edicao.Nome = $"Nome {invalido}";

        Assert.Contains(Registrar.Validate(registro).Errors, e => e.PropertyName == nameof(UsuarioConteudoRequest.Nome));
        Assert.Contains(Atualizar.Validate(edicao).Errors, e => e.PropertyName == nameof(UsuarioConteudoRequest.Nome));
    }

    [Theory]
    [MemberData(nameof(Invalidos))]
    public void TodoCampoTextoDoRequest_ForaDaPagina1252_EhRecusado(string invalido)
    {
        var verificados = new List<string>();
        foreach (var campo in CamposTexto(typeof(RegistrarUsuarioRequest)))
        {
            AssertRecusado(Registrar, ValidoParaRegistro(), r => r, campo, invalido);
            AssertRecusado(Atualizar, Valido<UpdateUsuarioRequest>(), r => r, campo, invalido);
            verificados.Add(campo.Name);
        }

        foreach (var campo in CamposTexto(typeof(EnderecoRequest)))
        {
            AssertRecusado(Registrar, ValidoParaRegistro(), r => r.Endereco!, campo, invalido);
            AssertRecusado(Atualizar, Valido<UpdateUsuarioRequest>(), r => r.Endereco!, campo, invalido);
            verificados.Add($"Endereco.{campo.Name}");
        }

        // Garante que a varredura não passou vazia: nome, e-mail, documentos, contato, responsável e endereço.
        Assert.Superset(new HashSet<string>
        {
            "Nome", "Email", "Cpf", "Telefone", "Rg", "TipoSanguineo", "DoencasAlergias", "ResponsavelNome", "ResponsavelEmail",
            "ResponsavelCpf", "ResponsavelTelefone", "Endereco.Cep", "Endereco.Numero", "Endereco.Complemento",
        }, verificados.ToHashSet());
    }

    private static void AssertRecusado<TRequest>(IValidator<TRequest> validator, TRequest request, Func<TRequest, object> alvo,
        PropertyInfo campo, string invalido)
    {
        var dono = alvo(request);
        campo.SetValue(dono, $"{campo.GetValue(dono)}{invalido}");

        var erros = validator.Validate(request).Errors;

        Assert.True(erros.Any(e => e.PropertyName.EndsWith(campo.Name, StringComparison.OrdinalIgnoreCase)),
            $"{typeof(TRequest).Name}.{campo.Name} aceitou o caractere U+{char.ConvertToUtf32(invalido, 0):X4}; erros: " +
            string.Join("; ", erros.Select(e => $"{e.PropertyName}: {e.ErrorMessage}")));
    }
}
