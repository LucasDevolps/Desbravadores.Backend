using System.Text.Json;
using Almirante.Api.Dtos;
using Almirante.Api.Services;
using Almirante.Api.Validation.Eventos;

namespace Almirante.Api.Tests;

// Regras puras (sem banco/HTTP): contrato "GUID | GUID[]", cálculo, datas, hash de idempotência e validação.
public sealed class EventosRegrasTests
{
    private static readonly Guid A = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid B = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid C = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed class RelogioFixo(DateTimeOffset agora) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => agora;
    }

    private static RelogioFixo Em(string iso) => new(DateTimeOffset.Parse(iso, null, System.Globalization.DateTimeStyles.AssumeUniversal));

    private static string Json(string membros, string data = "2026-10-18", string transporte = """{"valor":10.00,"ehGratis":false}""",
        string alimentacao = """{"individual":false,"valor":8.00}""", string seguro = "2.00", string extra = "") =>
        $$"""{"dataEvento":"{{data}}","local":"Parque Ibirapuera","transporte":{{transporte}},"alimentacao":{{alimentacao}},"seguroObrigatorio":{{seguro}},"membros":{{membros}}{{extra}}}""";

    private static RegistrarEventoRequest Ler(string json, string? chave = null)
    {
        var request = JsonSerializer.Deserialize<RegistrarEventoRequest>(json, Web)!;
        request.IdempotencyKey = chave ?? Guid.NewGuid().ToString();
        return request;
    }

    private static IEnumerable<string> Erros(RegistrarEventoRequest request, string agora = "2026-09-18T12:00:00Z") =>
        new RegistrarEventoRequestValidator(Em(agora)).Validate(request).Errors.Select(e => e.ErrorMessage);

    // ------------------------------------------------------------------ GUID | GUID[]

    [Fact]
    public void Membros_GuidUnicoEArrayDeUm_ViramAMesmaLista()
    {
        var unico = Ler(Json($"\"{A}\""));
        var array = Ler(Json($"[\"{A}\"]"));

        Assert.Equal([A], unico.Membros);
        Assert.Equal(unico.Membros, array.Membros);
    }

    [Fact]
    public void Membros_ArrayComVarios_PreservaTodos()
    {
        Assert.Equal([A, B, C], Ler(Json($"[\"{A}\",\"{B}\",\"{C}\"]")).Membros);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("123")]
    [InlineData("true")]
    [InlineData("{}")]
    [InlineData("\"nao-e-guid\"")]
    [InlineData("\"\"")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    [InlineData("[\"nao-e-guid\"]")]
    public void Membros_FormatosNaoSuportados_SaoRejeitadosNaDesserializacao(string membros) =>
        Assert.Throws<JsonException>(() => Ler(Json(membros)));

    [Fact]
    public void Membros_AusenteNaoEhAceito()
    {
        var request = JsonSerializer.Deserialize<RegistrarEventoRequest>(
            """{"dataEvento":"2026-10-18","local":"x","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1}""", Web)!;
        request.IdempotencyKey = Guid.NewGuid().ToString();
        Assert.Contains(Erros(request), e => e.Contains("membros é obrigatório"));
    }

    [Fact]
    public void Membros_ArrayVazio_ComGuidVazio_EDuplicados_GeramErroDeValidacao_SemRemoverSilenciosamente()
    {
        Assert.Contains(Erros(Ler(Json("[]"))), e => e.Contains("membros não pode ser vazio"));
        Assert.Contains(Erros(Ler(Json($"\"{Guid.Empty}\""))), e => e.Contains("GUID vazio"));
        Assert.Contains(Erros(Ler(Json($"[\"{A}\",\"{Guid.Empty}\"]"))), e => e.Contains("GUID vazio"));

        var duplicado = Ler(Json($"[\"{A}\",\"{B}\",\"{A}\"]"));
        Assert.Equal(3, duplicado.Membros!.Count); // nada removido em silêncio
        Assert.Contains(Erros(duplicado), e => e.Contains("repetidos") && e.Contains(A.ToString()));
    }

    [Fact]
    public void Membros_GuidUnicoEArrayValidos_NaoGeramErroDeMembros()
    {
        Assert.Empty(Erros(Ler(Json($"\"{A}\""))));
        Assert.Empty(Erros(Ler(Json($"[\"{A}\"]"))));
        Assert.Empty(Erros(Ler(Json($"[\"{A}\",\"{B}\"]"))));
    }

    [Fact]
    public void Membros_AcimaDoLimite_EhRejeitado()
    {
        var ids = string.Join(',', Enumerable.Range(0, MembrosJsonConverter.MaximoMembros + 1).Select(_ => $"\"{Guid.NewGuid()}\""));
        Assert.Throws<JsonException>(() => Ler(Json($"[{ids}]")));
    }

    [Fact]
    public void Membros_Serializacao_SempreArray()
    {
        var texto = JsonSerializer.Serialize(new { membros = (IReadOnlyList<Guid>)[A] }, Web);
        Assert.Contains("\"membros\":[", texto);
        var dto = new MembrosHolder { Membros = [A] };
        Assert.Equal($$"""{"membros":["{{A}}"]}""", JsonSerializer.Serialize(dto, Web));
    }

    private sealed class MembrosHolder
    {
        [System.Text.Json.Serialization.JsonConverter(typeof(MembrosJsonConverter))]
        public IReadOnlyList<Guid>? Membros { get; set; }
    }

    // ------------------------------------------------------------------ cálculo

    [Theory]
    // transporte, gratis, alimentação, individual, seguro, membros, valorPorMembro, total
    [InlineData(10, false, 8, false, 2, 2, 20, 40)]     // exemplo da issue
    [InlineData(10, true, 8, false, 2, 2, 10, 20)]      // transporte grátis ignora o valor
    [InlineData(10, false, 8, true, 2, 2, 12, 24)]      // alimentação individual ignora o valor
    [InlineData(10, true, 8, true, 2, 3, 2, 6)]         // só seguro
    [InlineData(10, false, 8, false, 0, 2, 18, 36)]     // seguro zero
    [InlineData(0, false, 0, false, 0, 4, 0, 0)]        // todos os componentes zerados: válido
    [InlineData(10, false, 8, false, 2, 1, 20, 20)]     // um membro
    [InlineData(3, false, 0, true, 2, 2, 5, 10)]        // segundo grupo do Ibirapuera: 2 × R$ 5
    public void Calculo_ValorPorMembroETotal(decimal transporte, bool gratis, decimal alimentacao, bool individual, decimal seguro,
        int membros, decimal esperadoPorMembro, decimal esperadoTotal)
    {
        var request = Ler(Json(
            "[" + string.Join(',', Enumerable.Range(0, membros).Select(_ => $"\"{Guid.NewGuid()}\"")) + "]",
            transporte: $$"""{"valor":{{transporte.ToString(System.Globalization.CultureInfo.InvariantCulture)}},"ehGratis":{{gratis.ToString().ToLowerInvariant()}}}""",
            alimentacao: $$"""{"individual":{{individual.ToString().ToLowerInvariant()}},"valor":{{alimentacao.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}""",
            seguro: seguro.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        Assert.Empty(Erros(request));
        var normalizado = EventoRegras.Normalizar(request);
        Assert.Equal(esperadoPorMembro, normalizado.ValorPorMembro);
        Assert.Equal(esperadoTotal, normalizado.Total);
        Assert.Equal(membros, normalizado.Membros.Count);
    }

    [Fact]
    public void Calculo_ComponenteIgnoradoPelosBooleanos_ViraZeroAntesDaValidacao()
    {
        // valor absurdo (negativo, 3 casas decimais) em campo ignorado não invalida a requisição
        var request = Ler(Json($"\"{A}\"", transporte: """{"valor":-5.123,"ehGratis":true}""", alimentacao: """{"individual":true,"valor":99999999999.999}"""));
        Assert.Empty(Erros(request));
        var normalizado = EventoRegras.Normalizar(request);
        Assert.Equal(0m, normalizado.TransporteValor);
        Assert.Equal(0m, normalizado.AlimentacaoValor);
        Assert.Equal(2m, normalizado.ValorPorMembro);
    }

    [Theory]
    [InlineData("-0.01")]
    [InlineData("10.005")]
    [InlineData("10000000000000000")]
    public void Valores_NegativosCasasExtrasEOverflow_SaoRejeitados(string valor)
    {
        Assert.Contains(Erros(Ler(Json($"\"{A}\"", transporte: $$"""{"valor":{{valor}},"ehGratis":false}"""))), e => e.Contains("transporte.valor"));
        Assert.Contains(Erros(Ler(Json($"\"{A}\"", alimentacao: $$"""{"individual":false,"valor":{{valor}}}"""))), e => e.Contains("alimentacao.valor"));
        Assert.Contains(Erros(Ler(Json($"\"{A}\"", seguro: valor))), e => e.Contains("seguroObrigatorio"));
    }

    [Fact]
    public void Valores_TotalComOverflowEhRejeitado()
    {
        var valor = "9999999999999999.99";
        Assert.Contains(Erros(Ler(Json($"[\"{A}\",\"{B}\"]", transporte: $$"""{"valor":{{valor}},"ehGratis":false}"""))), e => e.Contains("total"));
        Assert.Empty(Erros(Ler(Json($"\"{A}\"", transporte: $$"""{"valor":{{valor}},"ehGratis":false}""", alimentacao: """{"individual":true,"valor":0}""", seguro: "0"))));
    }

    [Theory]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "transporte é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","transporte":{"valor":1},"alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "transporte.ehGratis é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","transporte":{"ehGratis":false},"alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "transporte.valor é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "alimentacao.individual é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"individual":false},"seguroObrigatorio":1,"membros":"@"}""", "alimentacao.valor é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","local":"x","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"individual":false,"valor":1},"membros":"@"}""", "seguroObrigatorio é obrigatório")]
    [InlineData("""{"local":"x","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "dataEvento é obrigatório")]
    [InlineData("""{"dataEvento":"2026-10-18","transporte":{"valor":1,"ehGratis":false},"alimentacao":{"individual":false,"valor":1},"seguroObrigatorio":1,"membros":"@"}""", "local é obrigatório")]
    public void CamposObrigatoriosAusentes_SaoDistintosDeZeroEFalse(string json, string mensagem) =>
        Assert.Contains(Erros(Ler(json.Replace("@", A.ToString()))), e => e.Contains(mensagem));

    [Fact]
    public void ZeroEFalse_SaoValoresValidos()
    {
        Assert.Empty(Erros(Ler(Json($"\"{A}\"", transporte: """{"valor":0,"ehGratis":false}""", alimentacao: """{"individual":false,"valor":0}""", seguro: "0"))));
    }

    [Fact]
    public void Local_EhNormalizadoELimitado()
    {
        Assert.Equal("Parque Ibirapuera", EventoRegras.NormalizarLocal("  Parque \t Ibirapuera \n"));
        var longo = Ler(Json($"\"{A}\"").Replace("Parque Ibirapuera", new string('x', 201)));
        Assert.Contains(Erros(longo), e => e.Contains("local deve ter no máximo 200"));
        var limite = Ler(Json($"\"{A}\"").Replace("Parque Ibirapuera", "  " + new string('x', 200) + "  "));
        Assert.Empty(Erros(limite));
        Assert.Contains(Erros(Ler(Json($"\"{A}\"").Replace("Parque Ibirapuera", "   "))), e => e.Contains("local é obrigatório"));
    }

    // ------------------------------------------------------------------ datas

    [Theory]
    [InlineData("2026-09-01", true)]   // primeiro dia do mês
    [InlineData("2026-09-10", true)]   // ontem/dia passado dentro do mês
    [InlineData("2026-09-17", true)]
    [InlineData("2026-09-18", true)]   // hoje
    [InlineData("2026-09-30", true)]
    [InlineData("2027-03-01", true)]   // futuro
    [InlineData("2026-08-31", false)]  // último dia do mês anterior
    [InlineData("2025-09-18", false)]
    public void DataEvento_AceitaAPartirDoPrimeiroDiaDoMesAtual(string data, bool valida)
    {
        var erros = Erros(Ler(Json($"\"{A}\"", data: data)), "2026-09-18T12:00:00Z");
        Assert.Equal(valida, !erros.Any(e => e.Contains("dataEvento")));
    }

    [Theory]
    [InlineData("2027-01-05T10:00:00Z", "2027-01-01", true)]   // virada de ano
    [InlineData("2027-01-05T10:00:00Z", "2026-12-31", false)]
    [InlineData("2028-02-29T23:59:59Z", "2028-02-01", true)]   // fevereiro bissexto
    [InlineData("2028-02-29T23:59:59Z", "2028-01-31", false)]
    [InlineData("2026-09-30T22:00:00-03:00", "2026-10-01", true)]  // 01:00Z de outubro
    [InlineData("2026-09-30T22:00:00-03:00", "2026-09-30", false)] // fuso local não conta: referência é UTC
    public void DataEvento_UsaReferenciaUtc(string agora, string data, bool valida)
    {
        var erros = new RegistrarEventoRequestValidator(new RelogioFixo(DateTimeOffset.Parse(agora)))
            .Validate(Ler(Json($"\"{A}\"", data: data))).Errors.Select(e => e.ErrorMessage);
        Assert.Equal(valida, !erros.Any(e => e.Contains("dataEvento")));
    }

    [Theory]
    [InlineData("2026-09-18T12:00:00Z", "2026-09-01", "2026-09-30", "2026-08-02", "2026-10-30")] // exemplo da issue
    [InlineData("2027-01-15T00:00:00Z", "2027-01-01", "2027-01-31", "2026-12-02", "2027-03-02")] // virada de ano
    [InlineData("2028-02-10T00:00:00Z", "2028-02-01", "2028-02-29", "2028-01-02", "2028-03-30")] // bissexto
    [InlineData("2027-02-10T00:00:00Z", "2027-02-01", "2027-02-28", "2027-01-02", "2027-03-30")] // não bissexto
    public void PeriodoPadrao_MesAtualMais30DiasParaCadaLado(string agora, string primeiro, string ultimo, string inicial, string final)
    {
        var relogio = DateTimeOffset.Parse(agora);
        Assert.Equal(DateOnly.Parse(primeiro), EventoRegras.PrimeiroDiaDoMes(relogio));
        Assert.Equal(DateOnly.Parse(ultimo), EventoRegras.UltimoDiaDoMes(relogio));
        var (i, f) = EventoRegras.PeriodoPadrao(relogio);
        Assert.Equal(DateOnly.Parse(inicial), i);
        Assert.Equal(DateOnly.Parse(final), f);
    }

    // ------------------------------------------------------------------ idempotência (hash)

    private static string Hash(string json, Guid? referencia = null) =>
        EventoRegras.Hash(EventoRegras.Normalizar(Ler(json)), referencia);

    [Fact]
    public void Hash_GuidUnicoEArrayDeUmElemento_SaoAMesmaOperacao() =>
        Assert.Equal(Hash(Json($"\"{A}\"")), Hash(Json($"[\"{A}\"]")));

    [Fact]
    public void Hash_OrdemDosMembrosNaoMudaAOperacao() =>
        Assert.Equal(Hash(Json($"[\"{A}\",\"{B}\",\"{C}\"]")), Hash(Json($"[\"{C}\",\"{A}\",\"{B}\"]")));

    [Fact]
    public void Hash_ValoresIgnoradosPelosBooleanosNaoMudamAOperacao()
    {
        Assert.Equal(
            Hash(Json($"\"{A}\"", transporte: """{"valor":10,"ehGratis":true}""", alimentacao: """{"individual":true,"valor":8}""")),
            Hash(Json($"\"{A}\"", transporte: """{"valor":0,"ehGratis":true}""", alimentacao: """{"individual":true,"valor":500}""")));
        Assert.Equal(Hash(Json($"\"{A}\"", seguro: "2.00")), Hash(Json($"\"{A}\"", seguro: "2")));
    }

    [Fact]
    public void Hash_LocalNormalizadoIgualAoEspacado()
    {
        Assert.Equal(Hash(Json($"\"{A}\"")), Hash(Json($"\"{A}\"").Replace("Parque Ibirapuera", "  Parque   Ibirapuera ")));
    }

    [Fact]
    public void Hash_QualquerDadoDeNegocioDiferenteMudaAOperacao()
    {
        var baseHash = Hash(Json($"\"{A}\""));
        Assert.NotEqual(baseHash, Hash(Json($"\"{B}\"")));
        Assert.NotEqual(baseHash, Hash(Json($"[\"{A}\",\"{B}\"]")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\"", data: "2026-10-19")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\"", seguro: "3")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\"", transporte: """{"valor":11,"ehGratis":false}""")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\"", transporte: """{"valor":10,"ehGratis":true}""")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\"").Replace("Parque Ibirapuera", "Outro local")));
        Assert.NotEqual(baseHash, Hash(Json($"\"{A}\""), referencia: B));
    }

    [Theory]
    [InlineData("3f2504e0-4f89-11d3-9a0c-0305e82c3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("{3F2504E0-4F89-11D3-9A0C-0305E82C3301}", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("3F2504E04F8911D39A0C0305E82C3301", "3f2504e0-4f89-11d3-9a0c-0305e82c3301")]
    [InlineData("nao-e-uuid", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void IdempotencyKey_NormalizadaParaUuidCanonico(string? entrada, string? esperado) =>
        Assert.Equal(esperado, EventoRegras.NormalizarIdempotencyKey(entrada));

    [Fact]
    public void IdempotencyKey_AusenteOuInvalida_EhErroDeValidacao()
    {
        var request = Ler(Json($"\"{A}\""));
        request.IdempotencyKey = null;
        Assert.Contains(Erros(request), e => e.Contains("Idempotency-Key"));
        request.IdempotencyKey = "abc";
        Assert.Contains(Erros(request), e => e.Contains("UUID válido"));
    }

    // ------------------------------------------------------------------ PUT / DELETE / GET

    [Fact]
    public void Put_ExigeVersaoValidaEMotivoLimitado()
    {
        var validator = new UpdateEventoRequestValidator();
        UpdateEventoRequest Put(string? versao, string? motivo) => new()
        {
            DataEvento = new DateOnly(2026, 10, 18), Local = "x", Membros = [A], SeguroObrigatorio = 1,
            Transporte = new TransporteRequest { Valor = 1, EhGratis = false }, Alimentacao = new AlimentacaoRequest { Individual = false, Valor = 1 },
            Versao = versao, Motivo = motivo,
        };

        Assert.True(validator.Validate(Put("AAAAAAAAB9E=", null)).IsValid);
        Assert.True(validator.Validate(Put("AAAAAAAAB9E=", "remoção")).IsValid);
        Assert.False(validator.Validate(Put(null, null)).IsValid);
        Assert.False(validator.Validate(Put("nao-base64!", null)).IsValid);
        Assert.False(validator.Validate(Put("AAAA", null)).IsValid);
        Assert.False(validator.Validate(Put("AAAAAAAAB9E=", "")).IsValid);
        Assert.False(validator.Validate(Put("AAAAAAAAB9E=", new string('m', 256))).IsValid);
        Assert.True(validator.Validate(Put("AAAAAAAAB9E=", new string('m', 255))).IsValid);
    }

    [Fact]
    public void Delete_ExigeMotivoDe1a255EVersao()
    {
        var validator = new DeleteEventoRequestValidator();
        Assert.True(validator.Validate(new DeleteEventoRequest { Motivo = "x", Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.True(validator.Validate(new DeleteEventoRequest { Motivo = new string('m', 255), Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.False(validator.Validate(new DeleteEventoRequest { Motivo = "", Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.False(validator.Validate(new DeleteEventoRequest { Motivo = "   ", Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.False(validator.Validate(new DeleteEventoRequest { Motivo = null, Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.False(validator.Validate(new DeleteEventoRequest { Motivo = new string('m', 256), Versao = "AAAAAAAAB9E=" }).IsValid);
        Assert.False(validator.Validate(new DeleteEventoRequest { Motivo = "x", Versao = null }).IsValid);
    }

    [Fact]
    public void ListQuery_ExigeAsDuasDatasEOrdemValida()
    {
        var validator = new ListEventosQueryValidator();
        Assert.True(validator.Validate(new ListEventosQuery(null, null)).IsValid);
        Assert.True(validator.Validate(new ListEventosQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30))).IsValid);
        Assert.True(validator.Validate(new ListEventosQuery(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 1))).IsValid);
        Assert.False(validator.Validate(new ListEventosQuery(new DateOnly(2026, 9, 1), null)).IsValid);
        Assert.False(validator.Validate(new ListEventosQuery(null, new DateOnly(2026, 9, 30))).IsValid);
        Assert.False(validator.Validate(new ListEventosQuery(new DateOnly(2026, 10, 1), new DateOnly(2026, 9, 30))).IsValid);
    }

    [Fact]
    public void Versao_RoundTripBase64De8Bytes()
    {
        byte[] bytes = [0, 0, 0, 0, 0, 0, 7, 209];
        var texto = EventoRegras.VersaoParaTexto(bytes);
        Assert.True(EventoRegras.TentarLerVersao(texto, out var lido));
        Assert.Equal(bytes, lido);
        Assert.False(EventoRegras.TentarLerVersao("AAAA", out _));
    }
}
