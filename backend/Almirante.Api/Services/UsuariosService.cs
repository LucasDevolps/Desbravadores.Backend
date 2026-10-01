using System.Linq.Expressions;
using System.Net.Mail;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

// Regras de /api/Usuarios. Alteração e exclusão lógica acontecem em UMA transação com SESSION_CONTEXT (responsável
// das claims e IP da conexão, nunca do corpo): o trigger TR_Usuarios_Historico copia o estado anterior para
// _usuarios_hist na mesma instrução do UPDATE, então não existe alteração sem histórico nem histórico de alteração
// desfeita. O e-mail único é garantido pelo índice IX_Usuarios_EmailNormalizado; a consulta prévia só produz a
// mensagem de negócio, e a violação do índice (corrida entre duas requisições) vira o mesmo 409.
public class UsuariosService(AlmiranteDbContext db, IPasswordHasher<Usuario> passwordHasher, TimeProvider clock, ILogger<UsuariosService> logger)
{
    public const int NomeMaximo = 200;
    public const int EmailMaximo = 256;

    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;
    private const string IndiceEmailUnico = "IX_Usuarios_EmailNormalizado";

    private static readonly Expression<Func<Usuario, UsuarioListItemDto>> ParaDto = u => new UsuarioListItemDto
    {
        Id = u.Id,
        Nome = u.Nome,
        Email = u.Email,
        DataCriacao = u.DataCriacao,
        Funcao = u.Cargo!.Nome,
        Ativo = u.Ativo,
        Cargo = new CargoDto
        {
            Id = u.Cargo.Id,
            Nome = u.Cargo.Nome,
            Descricao = u.Cargo.Descricao,
            Ativo = u.Cargo.Ativo,
            CriadoPor = u.Cargo.CriadoPor,
            CriadoEm = u.Cargo.CriadoEm,
            UltimaAtualizacao = u.Cargo.UltimaAtualizacao,
            Role = u.Cargo.Role,
        },
    };

    // Mesma normalização do login (AuthService) e do seed: e-mails iguais a menos de maiúsculas/minúsculas colidem.
    public static string NormalizarEmail(string email) => email.Trim().ToUpperInvariant();

    // Endereço simples (sem nome de exibição) com domínio que contenha ponto.
    public static bool EmailValido(string? email)
    {
        var valor = email?.Trim();
        return !string.IsNullOrEmpty(valor) && MailAddress.TryCreate(valor, out var endereco) && endereco.Address == valor
            && endereco.Host.Contains('.') && !endereco.Host.StartsWith('.') && !endereco.Host.EndsWith('.');
    }

    // ---------------------------------------------------------------- GET

    public async Task<IReadOnlyList<UsuarioListItemDto>> ListAsync(bool incluirInativos, CancellationToken cancellationToken)
    {
        return await db.Usuarios
            .Where(u => incluirInativos || u.Ativo)
            .OrderBy(u => u.Nome)
            .Select(ParaDto)
            .ToListAsync(cancellationToken);
    }

    // ---------------------------------------------------------------- POST

    public async Task<UsuarioListItemDto> RegistrarAsync(RegistrarUsuarioRequest request, CancellationToken ct)
    {
        var email = request.Email!.Trim();
        var emailNormalizado = NormalizarEmail(email);
        var cargo = await ObterCargoAtivoAsync(request.CargoId!.Value, ct);
        ExigirPermissaoSobreCargo(cargo.Role, request.SolicitanteEhAdmin);

        if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == emailNormalizado, ct))
        {
            throw EmailJaCadastrado();
        }

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = request.Nome!.Trim(),
            Email = email,
            EmailNormalizado = emailNormalizado,
            SenhaHash = string.Empty,
            CargoId = cargo.Id,
            DataCriacao = clock.GetUtcNow().UtcDateTime,
            Ativo = true,
        };
        usuario.SenhaHash = passwordHasher.HashPassword(usuario, request.Senha!);
        db.Usuarios.Add(usuario);

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ViolouEmailUnico(ex.InnerException))
        {
            db.ChangeTracker.Clear();
            throw EmailJaCadastrado();
        }

        logger.LogInformation("Usuário {UsuarioId} criado.", usuario.Id);
        return await ObterDtoAsync(usuario.Id, ct);
    }

    // ---------------------------------------------------------------- PUT

    public async Task<UsuarioListItemDto> UpdateAsync(UpdateUsuarioRequest request, CancellationToken ct)
    {
        ExigirSqlServer();
        var nome = request.Nome!.Trim();
        var email = request.Email!.Trim();
        var emailNormalizado = NormalizarEmail(email);

        await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            var atual = await TravarUsuarioAsync(request.Id, ct);
            if (!atual.Ativo)
            {
                throw ApiProblemException.Conflict("Usuário inativo.", "Um usuário excluído não pode ser alterado. Nenhum registro foi alterado.");
            }

            ExigirPermissaoSobreCargo(atual.Role, request.SolicitanteEhAdmin);
            var cargo = await ObterCargoAtivoAsync(request.CargoId!.Value, ct);
            ExigirPermissaoSobreCargo(cargo.Role, request.SolicitanteEhAdmin);

            if (await db.Usuarios.AnyAsync(u => u.Id != request.Id && u.EmailNormalizado == emailNormalizado, ct))
            {
                throw EmailDeOutroUsuario();
            }

            // O trigger grava o estado ANTERIOR em _usuarios_hist (TipoOperacao UPDATE) antes de a alteração valer.
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoUpdate, ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"UPDATE dbo.Usuarios SET Nome = {nome}, Email = {email}, EmailNormalizado = {emailNormalizado}, CargoId = {cargo.Id} WHERE Id = {request.Id} AND Ativo = 1", ct);
            }
            catch (SqlException ex) when (ViolouEmailUnico(ex))
            {
                throw EmailDeOutroUsuario();
            }

            return true;
        }, ct);

        logger.LogInformation("Usuário {UsuarioId} alterado por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
        return await ObterDtoAsync(request.Id, ct);
    }

    // ---------------------------------------------------------------- DELETE (exclusão lógica)

    public async Task DeleteAsync(DeleteUsuarioRequest request, CancellationToken ct)
    {
        ExigirSqlServer();
        if (request.Id == request.UsuarioResponsavelId)
        {
            throw ApiProblemException.Conflict("Não é possível excluir o próprio usuário.");
        }

        await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            // XLOCK: um cadastro de evento concorrente com este membro espera a exclusão terminar (e então o vê
            // inativo), ou a exclusão espera o cadastro e o encontra abaixo (ver EventosService.ExigirMembrosExistentesAsync).
            var atual = await TravarUsuarioAsync(request.Id, ct);
            if (!atual.Ativo)
            {
                throw ApiProblemException.Conflict("Usuário já está inativo.", "O usuário já foi excluído. Nenhum registro foi alterado.");
            }

            ExigirPermissaoSobreCargo(atual.Role, request.SolicitanteEhAdmin);

            // Só eventos na data da exclusão ou depois (referência UTC do servidor, mesma de /api/Eventos) bloqueiam;
            // participações em eventos passados permanecem no histórico e não impedem a exclusão.
            var hoje = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var bloqueadores = (await db.EventosMembros.AsNoTracking()
                .Where(m => m.MembroId == request.Id && m.Ativo && m.Evento!.Ativo && m.Evento.DataEvento >= hoje)
                .OrderBy(m => m.Evento!.DataEvento).ThenBy(m => m.Evento!.Local)
                .Select(m => new { m.Evento!.Id, m.Evento.Local, m.Evento.DataEvento })
                .ToListAsync(ct))
                .Select(e => new EventoBloqueadorDto(e.Id, e.Local, e.DataEvento))
                .ToList();
            if (bloqueadores.Count > 0)
            {
                throw EventosFuturos(bloqueadores);
            }

            // O trigger grava o estado anterior (TipoOperacao DELETE). SecurityVersion + 1 invalida sessões e refresh
            // tokens do usuário (OnTokenValidated e AuthService.RefreshAsync comparam a versão); o login recusa inativos.
            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoDelete, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Usuarios SET Ativo = 0, SecurityVersion = SecurityVersion + 1 WHERE Id = {request.Id} AND Ativo = 1", ct);
            return true;
        }, ct);

        logger.LogInformation("Usuário {UsuarioId} excluído logicamente por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
    }

    // ---------------------------------------------------------------- apoio

    private sealed record UsuarioTravado(bool Ativo, string Role);

    private sealed record CargoResumo(Guid Id, string Role);

    // Lock exclusivo da linha até o fim da transação: PUT/DELETE concorrentes do mesmo usuário se serializam.
    private async Task<UsuarioTravado> TravarUsuarioAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.FromSqlInterpolated($"SELECT * FROM dbo.Usuarios WITH (XLOCK, ROWLOCK) WHERE Id = {id}")
            .AsNoTracking()
            .Select(u => new UsuarioTravado(u.Ativo, u.Cargo!.Role))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.NotFound("Usuário não encontrado.");

    private async Task<CargoResumo> ObterCargoAtivoAsync(Guid cargoId, CancellationToken ct) =>
        await db.Cargos.AsNoTracking()
            .Where(c => c.Id == cargoId && c.Ativo)
            .Select(c => new CargoResumo(c.Id, c.Role))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.Invalid("CargoId", "cargoId inexistente ou inativo.");

    // Todos os papéis da diretoria administram usuários (policy GestaoCadastros), mas só um Administrador cria, altera,
    // exclui ou promove alguém a Administrador: evita que outro papel desative ou assuma a conta administrativa.
    private static void ExigirPermissaoSobreCargo(string role, bool solicitanteEhAdmin)
    {
        if (role == Roles.Admin && !solicitanteEhAdmin)
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "ACESSO NEGADO!",
                "Somente um Administrador pode gerenciar usuários com o cargo Administrador.");
        }
    }

    private void ExigirSqlServer()
    {
        if (!db.Database.IsRelational())
        {
            throw new NotSupportedException("Alteração e exclusão de usuários exigem SQL Server (transação, trigger e SESSION_CONTEXT).");
        }
    }

    private async Task<UsuarioListItemDto> ObterDtoAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.AsNoTracking().Where(u => u.Id == id).Select(ParaDto).SingleAsync(ct);

    private static bool ViolouEmailUnico(Exception? ex) =>
        ex is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint } sql && sql.Message.Contains(IndiceEmailUnico, StringComparison.Ordinal);

    private static ApiProblemException EmailJaCadastrado() => ApiProblemException.Conflict("E-mail já cadastrado.",
        "Já existe um usuário cadastrado com o e-mail informado.");

    private static ApiProblemException EmailDeOutroUsuario() => ApiProblemException.Conflict("E-mail já cadastrado.",
        "Existe outro usuário cadastrado com esse e-mail.");

    private static ApiProblemException EventosFuturos(IReadOnlyList<EventoBloqueadorDto> eventos)
    {
        var detalhe = eventos.Count == 1
            ? $"Não é possível excluir o usuário, pois ele está vinculado ao evento \"{eventos[0].Nome}\" ({eventos[0].Data:dd/MM/yyyy})."
            : "Não é possível excluir o usuário porque ele está vinculado a eventos futuros: " +
              string.Join(", ", eventos.Select(e => $"\"{e.Nome}\" ({e.Data:dd/MM/yyyy})")) + ".";
        return ApiProblemException.Conflict("Usuário vinculado a eventos futuros.", detalhe,
            new Dictionary<string, object?> { ["eventos"] = eventos });
    }
}
