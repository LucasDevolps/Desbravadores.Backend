using Almirante.Api.Options;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

public sealed class RegrasCadastroTests
{
    [Theory]
    [InlineData("52998224725")]
    [InlineData("10000000604")]
    [InlineData("20000000370")]
    [InlineData("01234567890")]
    public void Cpf_Valido(string cpf) => Assert.True(Cpf.EhValido(cpf));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5299822472")]
    [InlineData("529982247250")]
    [InlineData("52998224726")]
    [InlineData("52998224735")]
    [InlineData("5299822472A")]
    [InlineData("１２３４５６７８９０１")]
    [InlineData("00000000000")]
    [InlineData("11111111111")]
    public void Cpf_Invalido(string? cpf) => Assert.False(Cpf.EhValido(cpf));

    [Fact]
    public void Cpf_GeradorDosTestes_SempreProduzCpfValidoComZeroInicial()
    {
        for (var i = 0; i < 200; i++)
        {
            var cpf = TestHelpers.NovoCpf();
            Assert.True(Cpf.EhValido(cpf), cpf);
            Assert.StartsWith("0", cpf);
        }

        Assert.True(Cpf.EhValido(TestHelpers.NovoCpf(comZeroInicial: false)));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ( ) - ", null)]
    [InlineData("  (11) 98765-4321 ", "11987654321")]
    [InlineData("11 3456 7890", "1134567890")]
    [InlineData("+55 (11) 98765-4321", "+5511987654321")]
    [InlineData("(11) 98765‑4321", "1198765‑4321")]
    public void Telefone_Normalizar_RemoveSoAMascaraConvencional(string? entrada, string? esperado) =>
        Assert.Equal(esperado, TelefoneBrasileiro.Normalizar(entrada));

    [Theory]
    [InlineData("11987654321")]
    [InlineData("99987654321")]
    [InlineData("1134567890")]
    [InlineData("2125551234")]
    [InlineData("5155555555")]
    public void Telefone_Valido(string digitos) => Assert.True(TelefoneBrasileiro.EhValido(digitos));

    [Theory]
    [InlineData(null)]
    [InlineData("123456789")]
    [InlineData("119876543210")]
    [InlineData("1198765432A")]
    [InlineData("23987654321")]
    [InlineData("10987654321")]
    [InlineData("11887654321")]
    [InlineData("1167654321")]
    [InlineData("1112345678")]
    [InlineData("+5511987654")]
    public void Telefone_Invalido(string? digitos) => Assert.False(TelefoneBrasileiro.EhValido(digitos));

    [Theory]
    [InlineData("2008-05-10", "2026-05-10")]
    [InlineData("2008-02-29", "2026-03-01")]
    [InlineData("2000-02-29", "2018-03-01")]
    [InlineData("2007-12-31", "2025-12-31")]
    public void DataDaMaioridade_ContaNoCalendario(string nascimento, string esperada) =>
        Assert.Equal(DateOnly.Parse(esperada), IdadeCivil.DataDaMaioridade(DateOnly.Parse(nascimento)));

    [Fact]
    public void DataDaMaioridade_ComDadoCorrompidoNoFimDoCalendario_NaoEstoura() =>
        Assert.Equal(DateOnly.MaxValue, IdadeCivil.DataDaMaioridade(new DateOnly(9990, 1, 1)));

    [Theory]
    [InlineData("2008-05-10", "2026-05-09", true)]
    [InlineData("2008-05-10", "2026-05-10", false)]
    [InlineData("2008-05-10", "2026-05-11", false)]
    [InlineData("2008-02-29", "2026-02-28", true)]
    [InlineData("2008-02-29", "2026-03-01", false)]
    [InlineData("2016-01-01", "2026-10-16", true)]
    [InlineData("1990-01-01", "2026-10-16", false)]
    public void EhMenorDeIdade_ComparaNoNivelDoDia(string nascimento, string hoje, bool menor) =>
        Assert.Equal(menor, IdadeCivil.EhMenorDeIdade(DateOnly.Parse(nascimento), DateOnly.Parse(hoje)));

    private static readonly DateOnly Hoje = new(2026, 10, 16);
    private static readonly DateOnly Menor = new(2014, 3, 3);
    private static readonly DateOnly Maior = new(1990, 3, 3);

    [Fact]
    public void Responsavel_MenorSemNomeEEmail_ExigeOsDois()
    {
        var erros = RegraResponsavel.Validar(Menor, null, null, null, null, Hoje);

        Assert.Equal(new[] { "ResponsavelEmail", "ResponsavelNome" }, erros.Keys.Order());
        Assert.Contains("menor de 18 anos", erros["ResponsavelNome"].Single());
    }

    [Theory]
    [InlineData("Maria", null, "ResponsavelEmail")]
    [InlineData(null, "maria@local.dev", "ResponsavelNome")]
    public void Responsavel_MenorComUmDosDois_ExigeOOutro(string? nome, string? email, string faltante) =>
        Assert.Equal(new[] { faltante }, RegraResponsavel.Validar(Menor, nome, email, null, null, Hoje).Keys);

    [Fact]
    public void Responsavel_MenorCompleto_MaiorOuSemNascimento_SemErros()
    {
        Assert.Empty(RegraResponsavel.Validar(Menor, "Maria", "maria@local.dev", null, null, Hoje));
        Assert.Empty(RegraResponsavel.Validar(Maior, null, null, null, null, Hoje));
        Assert.Empty(RegraResponsavel.Validar(null, null, null, null, null, Hoje));
    }

    [Fact]
    public void Responsavel_CpfIgualAoDoProprioUsuario_EhRecusado_MesmoParaMaior()
    {
        var cpf = TestHelpers.NovoCpf();

        Assert.Equal(new[] { "ResponsavelCpf" }, RegraResponsavel.Validar(Maior, null, null, cpf, cpf, Hoje).Keys);
        Assert.Empty(RegraResponsavel.Validar(Maior, null, null, cpf, TestHelpers.NovoCpf(), Hoje));
        Assert.Empty(RegraResponsavel.Validar(Maior, null, null, cpf, null, Hoje));
    }

    [Theory]
    [InlineData("João Conceição D'Ávila")]
    [InlineData("Ângela Müller-Ñúñez")]
    [InlineData("Aço € “citação” – ok")]
    public void TextoVarchar_AceitaOQueCabeNaPagina1252(string texto) => Assert.True(TextoVarchar.CabeNaPagina1252(texto));

    [Theory]
    [InlineData("Ana 😀")]
    [InlineData("Łukasz")]
    [InlineData("山田")]
    [InlineData("Ana\u0001")]
    [InlineData("Ana\tMaria")]
    [InlineData("Ana\u0081")]
    public void TextoVarchar_RecusaOQueSeriaTrocadoNaGravacao(string texto) => Assert.False(TextoVarchar.CabeNaPagina1252(texto));

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private static DataCivil Civil(DateTimeOffset agoraUtc, string fuso = "America/Sao_Paulo") =>
        new(new RelogioFixo(agoraUtc), Microsoft.Extensions.Options.Options.Create(new ClubeOptions { FusoHorario = fuso }));

    [Fact]
    public void DataCivil_UsaODiaDeSaoPaulo_NaoODiaUtc()
    {
        Assert.Equal(new DateOnly(2026, 10, 15), Civil(new DateTimeOffset(2026, 10, 16, 2, 30, 0, TimeSpan.Zero)).Hoje());
        Assert.Equal(new DateOnly(2026, 10, 16), Civil(new DateTimeOffset(2026, 10, 16, 3, 0, 0, TimeSpan.Zero)).Hoje());
        Assert.Equal(new DateOnly(2026, 10, 16), Civil(new DateTimeOffset(2026, 10, 16, 3, 0, 0, TimeSpan.Zero), " America/Sao_Paulo ").Hoje());
    }

    [Fact]
    public void DataCivil_VesperaDos18NoHorarioLocal_ContinuaMenorMesmoComUtcNoDiaSeguinte()
    {
        var hoje = Civil(new DateTimeOffset(2026, 10, 16, 1, 0, 0, TimeSpan.Zero)).Hoje();
        Assert.True(IdadeCivil.EhMenorDeIdade(new DateOnly(2008, 10, 16), hoje));
    }

    [Theory]
    [InlineData("America/Sao_Paulo", true)]
    [InlineData(" America/Sao_Paulo ", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("Marte/Olympus_Mons", false)]
    public void ClubeOptionsValidator_ExigeFusoExistente(string fuso, bool valido) =>
        Assert.Equal(valido, new ClubeOptionsValidator().Validate(null, new ClubeOptions { FusoHorario = fuso }).Succeeded);
}
