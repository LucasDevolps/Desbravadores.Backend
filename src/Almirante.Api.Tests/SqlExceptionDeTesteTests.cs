namespace Almirante.Api.Tests;

public sealed class SqlExceptionDeTesteTests
{
    [Theory]
    [InlineData(2627)]
    [InlineData(1801)]
    public void Criar_DevolveSqlExceptionComONumeroPedido(int numero)
    {
        var erro = SqlExceptionDeTeste.Criar(numero, "mensagem do teste", estado: 3);
        Assert.Equal(numero, erro.Number);
        Assert.Equal(3, erro.State);
        Assert.Equal("mensagem do teste", erro.Message);
    }
}
