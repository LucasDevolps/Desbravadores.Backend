using System.Data;
using System.Linq.Expressions;
using System.Net.Mail;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Validation.Usuarios;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

public class UsuariosService(AlmiranteDbContext db, IPasswordHasher<Usuario> passwordHasher, TimeProvider clock, DataCivil dataCivil,
    ILogger<UsuariosService> logger)
{
    public const int NomeMaximo = 200;
    public const int EmailMaximo = 100;
    public const int DoencasAlergiasMaximo = 2000;

    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;
    private const string IndiceEmailUnico = "IX_Usuarios_EmailNormalizado";
    private const string IndiceCpfUnico = "UX_Usuarios_Cpf";

    private static readonly Expression<Func<Usuario, UsuarioListItemDto>> ParaDto = u => new UsuarioListItemDto
    {
        Id = u.Id,
        Nome = u.Nome,
        Email = u.Email,
        Cpf = u.Cpf,
        DataNascimento = u.DataNascimento,
        Telefone = u.Telefone,
        ResponsavelNome = u.ResponsavelNome,
        ResponsavelEmail = u.ResponsavelEmail,
        ResponsavelCpf = u.ResponsavelCpf,
        ResponsavelTelefone = u.ResponsavelTelefone,
        Rg = u.Rg,
        TipoSanguineo = u.TipoSanguineo,
        DoencasAlergias = u.DoencasAlergias,
        PossuiFoto = u.FotoCaminho != null,
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

    public static string NormalizarEmail(string email) => email.Trim().ToUpperInvariant();

    public static bool EmailValido(string? email)
    {
        var valor = email?.Trim();
        return !string.IsNullOrEmpty(valor) && MailAddress.TryCreate(valor, out var endereco) && endereco.Address == valor
            && endereco.Host.Contains('.') && !endereco.Host.StartsWith('.') && !endereco.Host.EndsWith('.');
    }

    public static bool SomenteAsciiImprimivel(string valor) => valor.All(c => c is >= ' ' and <= '~');

    public static bool EmailArmazenavel(string emailNormalizado) =>
        emailNormalizado.Length <= EmailMaximo && SomenteAsciiImprimivel(emailNormalizado);

    public static string? NormalizarCpf(string? cpf)
    {
        var valor = cpf?.Replace(".", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
        return string.IsNullOrEmpty(valor) ? null : valor;
    }

    public static string? NormalizarTelefone(string? telefone) => TelefoneBrasileiro.Normalizar(telefone);

    public static string? NormalizarTexto(string? valor)
    {
        var texto = valor?.Trim();
        return string.IsNullOrEmpty(texto) ? null : texto;
    }

    public static SqlParameter Varchar(string nome, string? valor, int tamanho) =>
        new(nome, SqlDbType.VarChar, tamanho) { Value = (object?)valor ?? DBNull.Value };

    public async Task<IReadOnlyList<UsuarioListItemDto>> ListAsync(bool incluirInativos, CancellationToken cancellationToken)
    {
        return await db.Usuarios
            .Where(u => incluirInativos || u.Ativo)
            .OrderBy(u => u.Nome)
            .Select(ParaDto)
            .ToListAsync(cancellationToken);
    }

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

        var cpf = NormalizarCpf(request.Cpf);
        if (cpf is not null && await db.Usuarios.AnyAsync(u => u.Cpf == cpf, ct))
        {
            throw CpfJaCadastrado();
        }

        var usuario = new Usuario
        {
            Id = Guid.NewGuid(),
            Nome = request.Nome!.Trim(),
            Email = email,
            EmailNormalizado = emailNormalizado,
            Cpf = cpf,
            DataNascimento = request.DataNascimento,
            Telefone = NormalizarTelefone(request.Telefone),
            ResponsavelNome = NormalizarTexto(request.ResponsavelNome),
            ResponsavelEmail = NormalizarTexto(request.ResponsavelEmail),
            ResponsavelCpf = NormalizarCpf(request.ResponsavelCpf),
            ResponsavelTelefone = NormalizarTelefone(request.ResponsavelTelefone),
            Rg = Rg.Normalizar(request.Rg),
            TipoSanguineo = TipoSanguineo.Normalizar(request.TipoSanguineo),
            DoencasAlergias = NormalizarTexto(request.DoencasAlergias),
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
        catch (DbUpdateException ex) when (ViolouCpfUnico(ex.InnerException))
        {
            db.ChangeTracker.Clear();
            throw CpfJaCadastrado();
        }

        logger.LogInformation("Usuário {UsuarioId} criado.", usuario.Id);
        return await ObterDtoAsync(usuario.Id, ct);
    }

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

            var cpf = request.CpfInformado ? NormalizarCpf(request.Cpf) : atual.Cpf;
            var dataNascimento = request.DataNascimentoInformada ? request.DataNascimento : atual.DataNascimento;
            var telefone = request.TelefoneInformado ? NormalizarTelefone(request.Telefone) : atual.Telefone;
            var responsavelNome = request.ResponsavelNomeInformado ? NormalizarTexto(request.ResponsavelNome) : atual.ResponsavelNome;
            var responsavelEmail = request.ResponsavelEmailInformado ? NormalizarTexto(request.ResponsavelEmail) : atual.ResponsavelEmail;
            var responsavelCpf = request.ResponsavelCpfInformado ? NormalizarCpf(request.ResponsavelCpf) : atual.ResponsavelCpf;
            var responsavelTelefone = request.ResponsavelTelefoneInformado ? NormalizarTelefone(request.ResponsavelTelefone) : atual.ResponsavelTelefone;
            var rg = request.RgInformado ? Rg.Normalizar(request.Rg) : atual.Rg;
            var tipoSanguineo = request.TipoSanguineoInformado ? TipoSanguineo.Normalizar(request.TipoSanguineo) : atual.TipoSanguineo;
            var doencasAlergias = request.DoencasAlergiasInformado ? NormalizarTexto(request.DoencasAlergias) : atual.DoencasAlergias;

            if (cpf is null && atual.Cpf is not null)
            {
                throw ApiProblemException.Invalid("Cpf", "cpf é obrigatório e não pode ser removido.");
            }

            var erros = RegraResponsavel.Validar(dataNascimento, responsavelNome, responsavelEmail, responsavelCpf, cpf, dataCivil.Hoje());
            if (erros.Count > 0)
            {
                throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.", errors: erros);
            }

            if (cpf is not null && await db.Usuarios.AnyAsync(u => u.Id != request.Id && u.Cpf == cpf, ct))
            {
                throw CpfDeOutroUsuario();
            }

            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoUpdate, ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE dbo.Usuarios SET Nome = {nome}, Email = {Varchar("@email", email, EmailMaximo)},
                        EmailNormalizado = {Varchar("@emailNormalizado", emailNormalizado, EmailMaximo)}, CargoId = {cargo.Id},
                        Cpf = {Varchar("@cpf", cpf, 20)}, DataNascimento = {dataNascimento}, Telefone = {Varchar("@telefone", telefone, 20)},
                        ResponsavelNome = {Varchar("@responsavelNome", responsavelNome, NomeMaximo)},
                        ResponsavelEmail = {Varchar("@responsavelEmail", responsavelEmail, EmailMaximo)},
                        ResponsavelCpf = {Varchar("@responsavelCpf", responsavelCpf, Cpf.Digitos)},
                        ResponsavelTelefone = {Varchar("@responsavelTelefone", responsavelTelefone, TelefoneBrasileiro.MaximoDigitos)},
                        Rg = {Varchar("@rg", rg, Rg.Maximo)}, TipoSanguineo = {Varchar("@tipoSanguineo", tipoSanguineo, TipoSanguineo.Maximo)},
                        DoencasAlergias = {Varchar("@doencasAlergias", doencasAlergias, DoencasAlergiasMaximo)}
                    WHERE Id = {request.Id} AND Ativo = 1
                    """, ct);
            }
            catch (SqlException ex) when (ViolouEmailUnico(ex))
            {
                throw EmailDeOutroUsuario();
            }
            catch (SqlException ex) when (ViolouCpfUnico(ex))
            {
                throw CpfDeOutroUsuario();
            }

            return true;
        }, ct);

        logger.LogInformation("Usuário {UsuarioId} alterado por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
        return await ObterDtoAsync(request.Id, ct);
    }

    public async Task DeleteAsync(DeleteUsuarioRequest request, CancellationToken ct)
    {
        ExigirSqlServer();
        if (request.Id == request.UsuarioResponsavelId)
        {
            throw ApiProblemException.Conflict("Não é possível excluir o próprio usuário.");
        }

        await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            var atual = await TravarUsuarioAsync(request.Id, ct);
            if (!atual.Ativo)
            {
                throw ApiProblemException.Conflict("Usuário já está inativo.", "O usuário já foi excluído. Nenhum registro foi alterado.");
            }

            ExigirPermissaoSobreCargo(atual.Role, request.SolicitanteEhAdmin);

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

            await sessao.DefinirContextoAuditoriaAsync(request.UsuarioResponsavelId, request.IpResponsavel, UsuarioHistorico.OperacaoDelete, ct);
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE dbo.Usuarios SET Ativo = 0, SecurityVersion = SecurityVersion + 1 WHERE Id = {request.Id} AND Ativo = 1", ct);
            return true;
        }, ct);

        logger.LogInformation("Usuário {UsuarioId} excluído logicamente por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
    }

    private sealed record UsuarioTravado(bool Ativo, string Role, string? Cpf, DateOnly? DataNascimento, string? Telefone,
        string? ResponsavelNome, string? ResponsavelEmail, string? ResponsavelCpf, string? ResponsavelTelefone,
        string? Rg, string? TipoSanguineo, string? DoencasAlergias);

    private sealed record CargoResumo(Guid Id, string Role);

    private async Task<UsuarioTravado> TravarUsuarioAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.FromSqlInterpolated($"SELECT * FROM dbo.Usuarios WITH (XLOCK, ROWLOCK) WHERE Id = {id}")
            .AsNoTracking()
            .Select(u => new UsuarioTravado(u.Ativo, u.Cargo!.Role, u.Cpf, u.DataNascimento, u.Telefone,
                u.ResponsavelNome, u.ResponsavelEmail, u.ResponsavelCpf, u.ResponsavelTelefone, u.Rg, u.TipoSanguineo, u.DoencasAlergias))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.NotFound("Usuário não encontrado.");

    private async Task<CargoResumo> ObterCargoAtivoAsync(Guid cargoId, CancellationToken ct) =>
        await db.Cargos.AsNoTracking()
            .Where(c => c.Id == cargoId && c.Ativo)
            .Select(c => new CargoResumo(c.Id, c.Role))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.Invalid("CargoId", "cargoId inexistente ou inativo.");

    internal static void ExigirPermissaoSobreCargo(string role, bool solicitanteEhAdmin)
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

    internal async Task<UsuarioListItemDto> ObterDtoAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.AsNoTracking().Where(u => u.Id == id).Select(ParaDto).SingleAsync(ct);

    private static bool ViolouEmailUnico(Exception? ex) =>
        ex is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint } sql && sql.Message.Contains(IndiceEmailUnico, StringComparison.Ordinal);

    private static bool ViolouCpfUnico(Exception? ex) =>
        ex is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint } sql && sql.Message.Contains(IndiceCpfUnico, StringComparison.Ordinal);

    private static ApiProblemException CpfJaCadastrado() => ApiProblemException.Conflict("CPF já cadastrado.",
        "Já existe um usuário cadastrado com o CPF informado.");

    private static ApiProblemException CpfDeOutroUsuario() => ApiProblemException.Conflict("CPF já cadastrado.",
        "Existe outro usuário cadastrado com esse CPF.");

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
