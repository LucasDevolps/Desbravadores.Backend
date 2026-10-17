using System.Text.RegularExpressions;
using Almirante.Api.Infrastructure;
using Microsoft.Data.SqlClient;

namespace Almirante.Api.Tests;

public class DbCredentialManagerUnitTests
{
    [Fact]
    public void GeneratePassword_HasComplexityAndIsRandom()
    {
        var a = DbCredentialManager.GeneratePassword();
        var b = DbCredentialManager.GeneratePassword();

        Assert.NotEqual(a, b);
        Assert.Equal(48, a.Length);
        Assert.Contains(a, char.IsAsciiLetterUpper);
        Assert.Contains(a, char.IsAsciiLetterLower);
        Assert.Contains(a, char.IsAsciiDigit);
        Assert.All(a, c => Assert.True(char.IsAsciiLetterOrDigit(c)));
    }

    [Fact]
    public void GeneratePassword_NaoRepeteEUsaOAlfabetoTodo()
    {
        var senhas = Enumerable.Range(0, 1000).Select(_ => DbCredentialManager.GeneratePassword()).ToList();

        Assert.Equal(senhas.Count, senhas.Distinct().Count());
        Assert.True(string.Concat(senhas).Distinct().Count() >= 62);
    }

    [Theory]
    [InlineData("x]; DROP LOGIN sa;--")]
    [InlineData("1abc")]
    [InlineData("")]
    [InlineData("com espaco")]
    public void BuildRotateSql_RejectsUnsafeUserNames(string user) =>
        Assert.Throws<ArgumentException>(() => DbCredentialManager.BuildRotateSql(user, "Abc123"));

    [Theory]
    [InlineData("abc'; --")]
    [InlineData("")]
    [InlineData("com espaco")]
    public void BuildRotateSql_RejectsNonAlphanumericPassword(string password) =>
        Assert.Throws<ArgumentException>(() => DbCredentialManager.BuildRotateSql("almirante_user_bd", password));

    [Fact]
    public void BuildRotateSql_AlteraSoASenhaDoUsuarioContido_SemCriarLoginNemMexerEmPermissao()
    {
        var sql = DbCredentialManager.BuildRotateSql("almirante_user_bd", "Abc123def");

        Assert.Equal("ALTER USER [almirante_user_bd] WITH PASSWORD = N'Abc123def';", sql);
        Assert.DoesNotContain("LOGIN", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("GRANT", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("x]; DROP LOGIN sa;--")]
    [InlineData("1abc")]
    [InlineData("")]
    public void BuildProvisionSql_RejectsUnsafeUserNames(string user) =>
        Assert.Throws<ArgumentException>(() => DbCredentialManager.BuildProvisionSql(user));

    [Fact]
    public void BuildProvisionSql_GrantsOnlySelectInsertUpdateAndDeleteOnlyOnAuthSessions()
    {
        var sql = DbCredentialManager.BuildProvisionSql("almirante_user_bd");

        Assert.DoesNotContain("ON SCHEMA::dbo TO", sql);
        Assert.Contains("GRANT SELECT, INSERT, UPDATE ON OBJECT::dbo.", sql);
        Assert.Contains("DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.lancamentos_deletados TO [almirante_app_role]", sql);
        Assert.Contains("DENY INSERT, UPDATE, DELETE ON OBJECT::dbo.historico_eventos TO [almirante_app_role]", sql);
        Assert.Contains("DENY INSERT, UPDATE, DELETE ON OBJECT::dbo._usuarios_hist TO [almirante_app_role]", sql);
        Assert.Contains("GRANT DELETE ON OBJECT::dbo.AuthSessions TO [almirante_app_role]", sql);
        Assert.DoesNotContain("db_owner", sql);
        Assert.DoesNotContain("db_datawriter", sql);
    }

    [Fact]
    public void BuildProvisionSql_NaoCriaNemAlteraLoginUsuarioOuRole_SoConcedeNaRoleDeRuntime()
    {
        var sql = DbCredentialManager.BuildProvisionSql("almirante_user_bd");

        Assert.DoesNotMatch(new Regex(@"\b(CREATE|DROP)\s+(LOGIN|USER|ROLE)\b", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotMatch(new Regex(@"\bALTER\s+(LOGIN|USER|ROLE|SERVER)\b", RegexOptions.IgnoreCase), sql);
        Assert.DoesNotContain("ADD MEMBER", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IMPERSONATE", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("execute o bootstrap", sql);
    }

    [Fact]
    public void BuildRotateSql_NaoContemPermissoes() =>
        Assert.DoesNotMatch(new Regex(@"\b(GRANT|DENY|REVOKE)\b", RegexOptions.IgnoreCase), DbCredentialManager.BuildRotateSql("almirante_user_bd", "Abc123def"));
}
