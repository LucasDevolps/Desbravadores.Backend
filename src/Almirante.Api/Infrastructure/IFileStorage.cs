namespace Almirante.Api.Infrastructure;

// Armazenamento dos arquivos de foto (issue #116), desacoplado do provedor: hoje disco local (LocalFileStorage). Um
// object storage (MinIO/S3/Azure Blob) ou um compartilhamento SMB entram como outra implementação, sem mudar as regras.
// As chaves são sempre FotoChave já validadas; a implementação revalida o formato (defesa em profundidade).
public interface IFileStorage
{
    // Atômico: ou o arquivo final existe completo, ou não existe. Nunca sobrescreve uma chave existente.
    Task SaveAsync(string chave, ReadOnlyMemory<byte> conteudo, CancellationToken ct);

    // null quando o arquivo não existe.
    Task<Stream?> OpenReadAsync(string chave, CancellationToken ct);

    // Idempotente: chave inexistente não é erro.
    Task DeleteAsync(string chave, CancellationToken ct);

    // Fotos gravadas (só chaves no formato de FotoChave), para a faxina de órfãs.
    Task<IReadOnlyList<ArquivoArmazenado>> ListAsync(CancellationToken ct);

    // Remove gravações interrompidas (temporários) mais antigas que o corte; devolve quantas removeu.
    Task<int> PurgeIncompleteAsync(DateTime anterioresAUtc, CancellationToken ct);
}

public sealed record ArquivoArmazenado(string Chave, DateTime ModificadoEmUtc);
