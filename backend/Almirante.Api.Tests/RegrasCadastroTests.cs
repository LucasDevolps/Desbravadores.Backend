using Almirante.Api.Options;
using Almirante.Api.Services;
using Almirante.Api.Validation.Usuarios;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Tests;

// Regras puras do cadastro (issues #112 e #131): CPF, telefone, maioridade civil, responsável, texto varchar e "hoje" civil.
public sealed class RegrasCadastroTests
{
    // ------------------------------------------------------------------ CPF

    [Theory]
    [InlineData("52998224725")]   // dígitos verificadores 2 e 5 (resto >= 2 nos dois)
    [InlineData("10000000604")]   // 1º verificador 0 (resto 0)
    [InlineData("20000000370")]   // 2º verificador 0 (resto 1)
    [InlineData("01234567890")]   // zero à esquerda
    public void Cpf_Valido(string cpf) => Assert.True(Cpf.EhValido(cpf));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5299822472")]     // 10 dígitos
    [InlineData("529982247250")]   // 12 dígitos
    [InlineData("52998224726")]    // 2º verificador errado
    [InlineData("52998224735")]    // 1º verificador errado
    [InlineData("5299822472A")]    // letra
    [InlineData("１２３４５６７８９０１")] // dígitos fora do ASCII
    [InlineData("00000000000")]    // sequência de um dígito
    [InlineData("11111111111")]    // passa no módulo 11, mas não é CPF
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

    // ------------------------------------------------------------------ telefone

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  ( ) - ", null)]
    [InlineData("  (11) 98765-4321 ", "11987654321")]
    [InlineData("11 3456 7890", "1134567890")]
    [InlineData("+55 (11) 98765-4321", "+5511987654321")]  // "+" fica e reprova EhValido
    [InlineData("(11) 98765‑4321", "1198765‑4321")]        // hífen Unicode fica e reprova
    public void Telefone_Normalizar_RemoveSoAMascaraConvencional(string? entrada, string? esperado) =>
        Assert.Equal(esperado, TelefoneBrasileiro.Normalizar(entrada));

    [Theory]
    [InlineData("11987654321")]   // celular SP
    [InlineData("99987654321")]   // celular, maior DDD
    [InlineData("1134567890")]    // fixo começando com 3
    [InlineData("2125551234")]    // fixo começando com 2
    [InlineData("5155555555")]    // fixo começando com 5
    public void Telefone_Valido(string digitos) => Assert.True(TelefoneBrasileiro.EhValido(digitos));

    [Theory]
    [InlineData(null)]
    [InlineData("123456789")]     // 9 dígitos
    [InlineData("119876543210")]  // 12 dígitos
    [InlineData("1198765432A")]   // letra
    [InlineData("23987654321")]   // DDD 23 não existe
    [InlineData("10987654321")]   // DDD com zero
    [InlineData("11887654321")]   // celular sem o 9
    [InlineData("1167654321")]    // fixo começando com 6
    [InlineData("1112345678")]    // fixo começando com 1
    [InlineData("+5511987654")]   // "+" (não é dígito)
    public void Telefone_Invalido(string? digitos) => Assert.False(TelefoneBrasileiro.EhValido(digitos));

    // ------------------------------------------------------------------ maioridade

    [Theory]
    [InlineData("2008-05-10", "2026-05-10")]
    [InlineData("2008-02-29", "2026-03-01")]   // CC art. 132 §3º: sem correspondência, vence no dia seguinte
    [InlineData("2000-02-29", "2018-03-01")]
    [InlineData("2007-12-31", "2025-12-31")]
    public void DataDaMaioridade_ContaNoCalendario(string nascimento, string esperada) =>
        Assert.Equal(DateOnly.Parse(esperada), IdadeCivil.DataDaMaioridade(DateOnly.Parse(nascimento)));

    [Fact]
    public void DataDaMaioridade_ComDadoCorrompidoNoFimDoCalendario_NaoEstoura() =>
        Assert.Equal(DateOnly.MaxValue, IdadeCivil.DataDaMaioridade(new DateOnly(9990, 1, 1)));

    [Theory]
    [InlineData("2008-05-10", "2026-05-09", true)]    // véspera do 18º aniversário: ainda menor
    [InlineData("2008-05-10", "2026-05-10", false)]   // no dia: maior
    [InlineData("2008-05-10", "2026-05-11", false)]
    [InlineData("2008-02-29", "2026-02-28", true)]    // DateOnly.AddYears daria 28/02 (erro de um dia)
    [InlineData("2008-02-29", "2026-03-01", false)]
    [InlineData("2016-01-01", "2026-10-16", true)]
    [InlineData("1990-01-01", "2026-10-16", false)]
    public void EhMenorDeIdade_ComparaNoNivelDoDia(string nascimento, string hoje, bool menor) =>
        Assert.Equal(menor, IdadeCivil.EhMenorDeIdade(DateOnly.Parse(nascimento), DateOnly.Parse(hoje)));

    // ------------------------------------------------------------------ responsável

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

    // ------------------------------------------------------------------ varchar (página 1252)

    [Theory]
    [InlineData("João Conceição D'Ávila")]
    [InlineData("Ângela Müller-Ñúñez")]
    [InlineData("Aço € “citação” – ok")]   // € e aspas tipográficas existem na 1252
    public void TextoVarchar_AceitaOQueCabeNaPagina1252(string texto) => Assert.True(TextoVarchar.CabeNaPagina1252(texto));

    [Theory]
    [InlineData("Ana 😀")]       // emoji (par substituto)
    [InlineData("Łukasz")]        // Ł não existe na 1252
    [InlineData("山田")]
    [InlineData("Ana\u0001")]     // controle C0
    [InlineData("Ana\tMaria")]
    [InlineData("Ana\u0081")]     // controle C1 (a 1252 até "aceitaria")
    public void TextoVarchar_RecusaOQueSeriaTrocadoNaGravacao(string texto) => Assert.False(TextoVarchar.CabeNaPagina1252(texto));

    // ------------------------------------------------------------------ "hoje" civil

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private static DataCivil Civil(DateTimeOffset agoraUtc, string fuso = "America/Sao_Paulo") =>
        new(new RelogioFixo(agoraUtc), Microsoft.Extensions.Options.Options.Create(new ClubeOptions { FusoHorario = fuso }));

    [Fact]
    public void DataCivil_UsaODiaDeSaoPaulo_NaoODiaUtc()
    {
        // 23h30 de 15/10 em São Paulo = 02h30 de 16/10 em UTC.
        Assert.Equal(new DateOnly(2026, 10, 15), Civil(new DateTimeOffset(2026, 10, 16, 2, 30, 0, TimeSpan.Zero)).Hoje());
        Assert.Equal(new DateOnly(2026, 10, 16), Civil(new DateTimeOffset(2026, 10, 16, 3, 0, 0, TimeSpan.Zero)).Hoje());
        Assert.Equal(new DateOnly(2026, 10, 16), Civil(new DateTimeOffset(2026, 10, 16, 3, 0, 0, TimeSpan.Zero), " America/Sao_Paulo ").Hoje());
    }

    [Fact]
    public void DataCivil_VesperaDos18NoHorarioLocal_ContinuaMenorMesmoComUtcNoDiaSeguinte()
    {
        // Faz 18 em 16/10/2026; às 22h de 15/10 (horário de Brasília) o UTC já é 16/10.
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
