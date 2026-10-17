namespace Almirante.Api.Infrastructure;

public interface IFileStorage
{
    Task SaveAsync(string chave, ReadOnlyMemory<byte> conteudo, CancellationToken ct);

    Task<Stream?> OpenReadAsync(string chave, CancellationToken ct);

    Task DeleteAsync(string chave, CancellationToken ct);

    Task<IReadOnlyList<ArquivoArmazenado>> ListAsync(CancellationToken ct);

    Task<int> PurgeIncompleteAsync(DateTime anterioresAUtc, CancellationToken ct);
}

public sealed record ArquivoArmazenado(string Chave, DateTime ModificadoEmUtc);
