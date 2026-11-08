using System.Reflection;
using Microsoft.Data.SqlClient;

namespace Almirante.Api.Tests;

/// <summary>
/// <see cref="SqlException"/> com o número de erro escolhido, para testar sem banco os ramos que decidem pelo número (erro permanente da
/// auditoria, dicas de diagnóstico, corrida de criação do banco). O SqlClient não expõe construtor público: monta-se pelos membros
/// internos que o próprio driver usa. Se uma versão nova do driver os mudar, os testes que dependem disto falham na hora, sem falso
/// positivo.
/// </summary>
public static class SqlExceptionDeTeste
{
    public static SqlException Criar(int numero, string mensagem = "erro simulado de teste", byte estado = 1)
    {
        var construtor = typeof(SqlError).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(c => c.GetParameters() is { Length: >= 7 } p && p[0].ParameterType == typeof(int) && p[1].ParameterType == typeof(byte))
            .OrderBy(c => c.GetParameters().Length)
            .First();
        var argumentos = construtor.GetParameters().Select(p => p.Name switch
        {
            "infoNumber" => numero,
            "errorState" => estado,
            "errorClass" => (byte)16,
            "server" => "teste",
            "errorMessage" => mensagem,
            "procedure" => "",
            _ => p.HasDefaultValue ? p.DefaultValue : p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null,
        }).ToArray();
        var erro = (SqlError)construtor.Invoke(argumentos);

        var erros = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection).GetMethod("Add", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(erros, [erro]);
        var criar = typeof(SqlException).GetMethod("CreateException", BindingFlags.NonPublic | BindingFlags.Static,
            [typeof(SqlErrorCollection), typeof(string)])!;
        return (SqlException)criar.Invoke(null, [erros, "16.0.0"])!;
    }
}
