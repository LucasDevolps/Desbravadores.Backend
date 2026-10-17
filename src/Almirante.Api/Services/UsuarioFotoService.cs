using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public sealed class UsuarioFotoService(AlmiranteDbContext db, UsuariosService usuarios, IFileStorage armazenamento,
    ProcessadorFotos processador, TimeProvider clock, ILogger<UsuarioFotoService> logger)
{
    private static readonly TimeSpan[] EsperasExclusao = [TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(400)];

    private sealed record UsuarioAlvo(bool Ativo, string Role, string? FotoCaminho);

    public async Task<UsuarioListItemDto> EnviarAsync(EnviarFotoUsuarioRequest request, CancellationToken ct)
    {
        ExigirSqlServer();

        var alvo = await db.Usuarios.AsNoTracking().Where(u => u.Id == request.Id)
                       .Select(u => new UsuarioAlvo(u.Ativo, u.Cargo!.Role, u.FotoCaminho)).SingleOrDefaultAsync(ct)
                   ?? throw ApiProblemException.NotFound("Usuário não encontrado.");
        ExigirAlvoAlteravel(alvo, request.SolicitanteEhAdmin);

        var foto = processador.Processar(request.Conteudo, request.Formato, ct);
        var chave = FotoChave.Gerar(request.Id);
        await GravarAsync(chave, foto.Conteudo, ct);

        string? anterior;
        try
        {
            anterior = await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
            {
                var atual = await TravarAsync(request.Id, ct);
                ExigirAlvoAlteravel(atual, request.SolicitanteEhAdmin);
                await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoUpdate, ct);
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE dbo.Usuarios SET FotoCaminho = {UsuariosService.Varchar("@fotoCaminho", chave, FotoChave.Tamanho)} WHERE Id = {request.Id}", ct);
                return atual.FotoCaminho;
            }, ct);
        }
        catch
        {
            if (!await ReferenciadoAsync(request.Id, chave))
            {
                await ExcluirArquivoAsync(chave);
            }

            throw;
        }

        if (anterior is not null)
        {
            await ExcluirArquivoAsync(anterior);
        }

        logger.LogInformation("Foto do usuário {UsuarioId} gravada por {ResponsavelId}: {Largura}x{Altura}, {Bytes} bytes, qualidade {Qualidade}.",
            request.Id, request.UsuarioResponsavelId, foto.Largura, foto.Altura, foto.Conteudo.Length, foto.Qualidade);
        return await usuarios.ObterDtoAsync(request.Id, ct);
    }

    public async Task<FotoUsuarioArquivo?> ObterAsync(Guid id, CancellationToken ct)
    {
        var chave = await db.Usuarios.AsNoTracking().Where(u => u.Id == id).Select(u => u.FotoCaminho).SingleOrDefaultAsync(ct);
        if (chave is null)
        {
            return null;
        }

        if (!FotoChave.PertenceA(chave, id))
        {
            logger.LogError("FotoCaminho inconsistente no usuário {UsuarioId}: a foto não é servida.", id);
            return null;
        }

        var conteudo = await armazenamento.OpenReadAsync(chave, ct);
        if (conteudo is null)
        {
            logger.LogError("Arquivo da foto do usuário {UsuarioId} não existe no armazenamento.", id);
            return null;
        }

        return new FotoUsuarioArquivo(conteudo, FotoChave.ETag(chave));
    }

    public async Task ExcluirAsync(ExcluirFotoUsuarioRequest request, CancellationToken ct)
    {
        ExigirSqlServer();

        var anterior = await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            var atual = await TravarAsync(request.Id, ct);
            UsuariosService.ExigirPermissaoSobreCargo(atual.Role, request.SolicitanteEhAdmin);
            var caminho = atual.FotoCaminho ?? throw ApiProblemException.NotFound("Foto não encontrada.", "O usuário não tem foto cadastrada.");
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoUpdate, ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE dbo.Usuarios SET FotoCaminho = NULL WHERE Id = {request.Id}", ct);
            return caminho;
        }, ct);

        await ExcluirArquivoAsync(anterior);
        logger.LogInformation("Foto do usuário {UsuarioId} removida por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
    }

    private static void ExigirAlvoAlteravel(UsuarioAlvo alvo, bool solicitanteEhAdmin)
    {
        if (!alvo.Ativo)
        {
            throw ApiProblemException.Conflict("Usuário inativo.", "Um usuário excluído não pode receber foto. Nenhum registro foi alterado.");
        }

        UsuariosService.ExigirPermissaoSobreCargo(alvo.Role, solicitanteEhAdmin);
    }

    private async Task<UsuarioAlvo> TravarAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.FromSqlInterpolated($"SELECT * FROM dbo.Usuarios WITH (XLOCK, ROWLOCK) WHERE Id = {id}")
            .AsNoTracking()
            .Select(u => new UsuarioAlvo(u.Ativo, u.Cargo!.Role, u.FotoCaminho))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.NotFound("Usuário não encontrado.");

    private async Task GravarAsync(string chave, byte[] conteudo, CancellationToken ct)
    {
        try
        {
            await armazenamento.SaveAsync(chave, conteudo, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Falha ao gravar foto no armazenamento; o cadastro não foi alterado.");
            throw new ApiProblemException(StatusCodes.Status503ServiceUnavailable, "Armazenamento de fotos indisponível.",
                "A foto não foi salva e o cadastro não foi alterado. Tente novamente mais tarde.");
        }
    }

    private async Task<bool> ReferenciadoAsync(Guid id, string chave)
    {
        try
        {
            return await db.Usuarios.AsNoTracking().AnyAsync(u => u.Id == id && u.FotoCaminho == chave, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Não foi possível confirmar se a foto {Chave} foi gravada no cadastro; o arquivo fica para a faxina.", chave);
            return true;
        }
    }

    private async Task ExcluirArquivoAsync(string chave)
    {
        for (var tentativa = 0; ; tentativa++)
        {
            try
            {
                await armazenamento.DeleteAsync(chave, CancellationToken.None);
                return;
            }
            catch (Exception ex)
            {
                if (tentativa == EsperasExclusao.Length)
                {
                    logger.LogWarning(ex, "Não foi possível apagar o arquivo de foto {Chave}; a faxina diária tenta de novo.", chave);
                    return;
                }

                await Task.Delay(EsperasExclusao[tentativa], clock);
            }
        }
    }

    private void ExigirSqlServer()
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("Envio e exclusão de foto exigem SQL Server (transação, trigger e SESSION_CONTEXT).");
        }
    }
}
