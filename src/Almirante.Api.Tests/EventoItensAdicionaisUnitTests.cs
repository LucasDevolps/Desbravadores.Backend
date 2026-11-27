using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Almirante.Api.Services;
using Almirante.Api.Tests.Builders;
using Almirante.Api.Validation.Eventos;

namespace Almirante.Api.Tests;

public sealed class EventoItensAdicionaisUnitTests
{
    private static IReadOnlyList<string> ErrosPorCampo(RegistrarEventoRequestBuilder builder) =>
        new RegistrarEventoRequestValidator().Validate(builder.Build()).Errors.Select(e => e.PropertyName).ToList();

    [Fact]
    public void BaseDe100ComInscricao30EMaterial12e50_Resulta142e50PorParticipante()
    {
        var normalizado = EventoRegras.Normalizar(new RegistrarEventoRequestBuilder().ComBase(100m)
            .ComItem("Inscrição", 30m).ComItem("Material", 12.50m).ComMembros(2).Build());

        Assert.Equal((142.50m, 42.50m, 285m), (normalizado.ValorPorMembro, normalizado.ItensAdicionaisValor, normalizado.Total));
    }

    [Fact]
    public void TituloDoItem_TemEspacosColapsados()
    {
        var normalizado = EventoRegras.Normalizar(new RegistrarEventoRequestBuilder().ComItem("  Kit   de  boas-vindas ", 5m).Build());

        Assert.Equal("Kit de boas-vindas", Assert.Single(normalizado.Itens).Titulo);
    }

    [Fact]
    public void ItensOmitidosNaoContamComoInformados_ListaVaziaConta()
    {
        Assert.False(EventoRegras.Normalizar(new RegistrarEventoRequestBuilder().Build()).ItensInformados);
        Assert.True(EventoRegras.Normalizar(new RegistrarEventoRequestBuilder().ComItensVazios().Build()).ItensInformados);
    }

    [Fact]
    public void HashSemItens_ContinuaIgualAoFormatoAnterior()
    {
        var request = new RegistrarEventoRequestBuilder().ComBase(100m, 8m, 2m).Build();
        var e = EventoRegras.Normalizar(request);
        var canonicalAnterior = string.Join('|', "evento.v2", JsonSerializer.Serialize(e.Titulo), JsonSerializer.Serialize(e.Descricao),
            e.DataEvento.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), JsonSerializer.Serialize(e.Local), "0", "100.00", "0", "8.00", "2.00", "-",
            string.Join(',', e.Membros.Select(m => m.ToString("D"))));

        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalAnterior))), EventoRegras.Hash(e, null));
        Assert.Equal(EventoRegras.Hash(e, null), EventoRegras.Hash(EventoRegras.Normalizar(new RegistrarEventoRequestBuilder().ComBase(100m, 8m, 2m)
            .ComItensVazios().Build()) with { DataEvento = e.DataEvento }, null));
    }

    [Fact]
    public void HashComItens_MudaComTituloValorOuOrdem()
    {
        string Hash(RegistrarEventoRequestBuilder b) => EventoRegras.Hash(EventoRegras.Normalizar(b.Build()), null);
        var original = Hash(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 30m).ComItem("Material", 12.50m));

        Assert.NotEqual(Hash(new RegistrarEventoRequestBuilder()), original);
        Assert.NotEqual(Hash(new RegistrarEventoRequestBuilder().ComItem("Taxa", 30m).ComItem("Material", 12.50m)), original);
        Assert.NotEqual(Hash(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 31m).ComItem("Material", 12.50m)), original);
        Assert.NotEqual(Hash(new RegistrarEventoRequestBuilder().ComItem("Material", 12.50m).ComItem("Inscrição", 30m)), original);
        Assert.Equal(Hash(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 30.00m).ComItem(" Material ", 12.5m)), original);
    }

    [Fact]
    public void RenomearOuReordenarItens_MantemAComposicaoDeValores()
    {
        var atuais = new[] { new EventoItemNormalizado("Inscrição", 30m), new EventoItemNormalizado("Material", 12.50m) };

        Assert.True(EventoRegras.MesmaComposicaoDeValores(atuais, [new("Taxa", 30m), new("Kit", 12.50m)]));
        Assert.True(EventoRegras.MesmaComposicaoDeValores(atuais, [new("Material", 12.50m), new("Inscrição", 30m)]));
        Assert.False(EventoRegras.MesmaComposicaoDeValores(atuais, [new("Inscrição", 30m), new("Material", 10m)]));
        Assert.False(EventoRegras.MesmaComposicaoDeValores(atuais, [new("Inscrição", 30m)]));
    }

    [Fact]
    public void ItemSemTitulo_ErroIdentificaOItem()
    {
        Assert.Contains("ItensAdicionais[1].Titulo", ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 30m).ComItem("   ", 5m)));
    }

    [Fact]
    public void ItemComValorZero_ErroIdentificaOItem()
    {
        Assert.Contains("ItensAdicionais[0].Valor", ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 0m)));
    }

    [Fact]
    public void ItemComTresCasasDecimais_ErroIdentificaOItem()
    {
        Assert.Contains("ItensAdicionais[0].Valor", ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 10.005m)));
    }

    [Fact]
    public void ItemSemValor_ErroIdentificaOItem()
    {
        Assert.Contains("ItensAdicionais[0].Valor", ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem("Inscrição", null)));
    }

    [Fact]
    public void ItemComTituloLongoDemais_ErroIdentificaOItem()
    {
        Assert.Contains("ItensAdicionais[0].Titulo",
            ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem(new string('a', EventoRegras.ItemTituloMaximo + 1), 5m)));
    }

    [Fact]
    public void MaisItensQueOLimite_Recusa()
    {
        var builder = new RegistrarEventoRequestBuilder();
        for (var i = 0; i <= EventoRegras.ItensMaximo; i++)
        {
            builder.ComItem($"Item {i}", 1m);
        }

        Assert.Contains("ItensAdicionais", ErrosPorCampo(builder));
    }

    [Fact]
    public void ItensValidos_PassamNaValidacao()
    {
        Assert.Empty(ErrosPorCampo(new RegistrarEventoRequestBuilder().ComItem("Inscrição", 30m).ComItem("Material", 12.50m)));
    }

    [Fact]
    public void TotalComItensAcimaDoLimite_Recusa()
    {
        Assert.Contains("total",
            ErrosPorCampo(new RegistrarEventoRequestBuilder().ComBase(EventoRegras.ValorMaximo).ComItem("Inscrição", 1m)), StringComparer.OrdinalIgnoreCase);
    }
}
