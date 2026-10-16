using Almirante.Api.Infrastructure;

namespace Almirante.Api.Tests;

public sealed class ModelBindingErrorsTests
{
    [Theory]
    [InlineData("The JSON value could not be converted to System.Nullable`1[System.Decimal]. Path: $.seguroObrigatorio")]
    [InlineData("Failed to bind Almirante.Api.Dtos.RegistrarEventoRequest")]
    [InlineData("Erro interno: System.InvalidOperationException ocorreu")]
    [InlineData("JsonReaderException while parsing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Sanitizar_TrocaPorTextoGenerico_QuandoRevelaTipoInternoOuEstaVazia(string? mensagem)
    {
        var resultado = ModelBindingErrors.Sanitizar(mensagem);

        Assert.Equal("O valor informado é inválido ou está em formato incorreto para este campo.", resultado);
    }

    [Theory]
    [InlineData("membros deve ser um GUID ou um array de GUIDs (não pode ser null nem outro tipo JSON).")]
    [InlineData("membros contém um GUID inválido.")]
    [InlineData("membros aceita no máximo 1000 itens.")]
    [InlineData("dataEvento é obrigatório (yyyy-MM-dd).")]
    [InlineData("Informe o header Idempotency-Key (UUID).")]
    public void Sanitizar_PreservaMensagensProprias_DaApi(string mensagem)
    {
        Assert.Equal(mensagem, ModelBindingErrors.Sanitizar(mensagem));
    }
}
