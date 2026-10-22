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
    IConsultaCep consultaCep, ILogger<UsuariosService> logger)
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
        EhCin = u.EhCin,
        DataExpiracao = u.DataExpiracao,
        Endereco = u.EnderecoCep == null ? null : new EnderecoDto
        {
            Cep = u.EnderecoCep,
            Logradouro = u.EnderecoLogradouro,
            Numero = u.EnderecoNumero,
            Complemento = u.EnderecoComplemento,
            ComplementoCep = u.EnderecoComplementoCep,
            Unidade = u.EnderecoUnidade,
            Bairro = u.EnderecoBairro,
            Localidade = u.EnderecoLocalidade!,
            Uf = u.EnderecoUf!,
            Estado = u.EnderecoEstado,
            Regiao = u.EnderecoRegiao,
            Ibge = u.EnderecoIbge,
            Gia = u.EnderecoGia,
            Ddd = u.EnderecoDdd,
            Siafi = u.EnderecoSiafi,
        },
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

    private static readonly Expression<Func<Usuario, MeusDadosDto>> ParaMeusDados = u => new MeusDadosDto
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
        EhCin = u.EhCin,
        DataExpiracao = u.DataExpiracao,
        Endereco = u.EnderecoCep == null ? null : new EnderecoDto
        {
            Cep = u.EnderecoCep,
            Logradouro = u.EnderecoLogradouro,
            Numero = u.EnderecoNumero,
            Complemento = u.EnderecoComplemento,
            ComplementoCep = u.EnderecoComplementoCep,
            Unidade = u.EnderecoUnidade,
            Bairro = u.EnderecoBairro,
            Localidade = u.EnderecoLocalidade!,
            Uf = u.EnderecoUf!,
            Estado = u.EnderecoEstado,
            Regiao = u.EnderecoRegiao,
            Ibge = u.EnderecoIbge,
            Gia = u.EnderecoGia,
            Ddd = u.EnderecoDdd,
            Siafi = u.EnderecoSiafi,
        },
        PossuiFoto = u.FotoCaminho != null,
        Cargo = new MeCargoDto { Id = u.Cargo!.Id, Nome = u.Cargo.Nome, Role = u.Cargo.Role },
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

    /// <summary>Usuários ativos com o mínimo para identificá-los (id, nome, cargo), para seletores de participantes e cobrados.</summary>
    public async Task<IReadOnlyList<MembroResumoDto>> ListarMembrosAsync(CancellationToken ct) =>
        await db.Usuarios.AsNoTracking()
            .Where(u => u.Ativo)
            .OrderBy(u => u.Nome).ThenBy(u => u.Id)
            .Select(u => new MembroResumoDto(u.Id, u.Nome, u.Cargo!.Nome, u.Cargo.Role))
            .ToListAsync(ct);

    /// <summary>Dados do próprio usuário ativo; null se ele foi desativado entre a validação do token e a consulta.</summary>
    public Task<MeusDadosDto?> ObterMeusDadosAsync(Guid id, CancellationToken ct) =>
        db.Usuarios.AsNoTracking().Where(u => u.Id == id && u.Ativo).Select(ParaMeusDados).SingleOrDefaultAsync(ct);

    public async Task<UsuarioListItemDto> RegistrarAsync(RegistrarUsuarioRequest request, CancellationToken ct)
    {
        var email = request.Email!.Trim();
        var emailNormalizado = NormalizarEmail(email);
        var cargo = await ObterCargoAtivoAsync(request.CargoId!.Value, ct);
        ExigirPermissaoSobreCargo(cargo.Role, request.SolicitanteRole);

        if (await db.Usuarios.AnyAsync(u => u.EmailNormalizado == emailNormalizado, ct))
        {
            throw EmailJaCadastrado();
        }

        var cpf = NormalizarCpf(request.Cpf);
        if (cpf is not null && await db.Usuarios.AnyAsync(u => u.Cpf == cpf, ct))
        {
            throw CpfJaCadastrado();
        }

        var endereco = EnderecoGravado.Vazio;
        if (request.Endereco is not null)
        {
            var dadosCep = await ConsultarCepAsync(Cep.Normalizar(request.Endereco.Cep)!, "Nenhum usuário foi cadastrado.", ct);
            endereco = EnderecoGravado.DoCep(dadosCep, NormalizarTexto(request.Endereco.Numero), NormalizarTexto(request.Endereco.Complemento));
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
            EhCin = request.EhCin ?? false,
            DataExpiracao = request.DataExpiracao,
            EnderecoCep = endereco.Cep,
            EnderecoLogradouro = endereco.Logradouro,
            EnderecoNumero = endereco.Numero,
            EnderecoComplemento = endereco.Complemento,
            EnderecoComplementoCep = endereco.ComplementoCep,
            EnderecoUnidade = endereco.Unidade,
            EnderecoBairro = endereco.Bairro,
            EnderecoLocalidade = endereco.Localidade,
            EnderecoUf = endereco.Uf,
            EnderecoEstado = endereco.Estado,
            EnderecoRegiao = endereco.Regiao,
            EnderecoIbge = endereco.Ibge,
            EnderecoGia = endereco.Gia,
            EnderecoDdd = endereco.Ddd,
            EnderecoSiafi = endereco.Siafi,
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
        var cadastro = new CadastroAdministrativo(request.Nome!.Trim(), request.Email!.Trim(), request.CargoId!.Value, request.SolicitanteRole);
        await AlterarAsync(request.Id, request, cadastro, request.UsuarioResponsavelId, request.IpResponsavel, ct);
        logger.LogInformation("Usuário {UsuarioId} alterado por {ResponsavelId}.", request.Id, request.UsuarioResponsavelId);
        return await ObterDtoAsync(request.Id, ct);
    }

    /// <summary>
    /// Autoatendimento: o próprio usuário altera só os dados pessoais. Nome, e-mail e cargo são mantidos a partir da linha travada
    /// na mesma transação (uma troca de cargo concorrente nunca é desfeita). Auditado em _usuarios_hist como qualquer alteração.
    /// </summary>
    public async Task<MeusDadosDto> AtualizarMeusDadosAsync(AtualizarMeusDadosRequest request, CancellationToken ct)
    {
        await AlterarAsync(request.UsuarioId, request, null, request.UsuarioId, request.IpResponsavel, ct);
        logger.LogInformation("Usuário {UsuarioId} alterou os próprios dados pessoais.", request.UsuarioId);
        return await db.Usuarios.AsNoTracking().Where(u => u.Id == request.UsuarioId).Select(ParaMeusDados).SingleAsync(ct);
    }

    /// <summary>Nome, e-mail e cargo do PUT administrativo, com o cargo de quem pede (hierarquia de <see cref="Roles.PodeGerenciar"/>).</summary>
    private sealed record CadastroAdministrativo(string Nome, string Email, Guid CargoId, string SolicitanteRole);

    private async Task AlterarAsync(Guid id, DadosPessoaisRequest request, CadastroAdministrativo? cadastro, Guid responsavelId,
        string ipResponsavel, CancellationToken ct)
    {
        ExigirSqlServer();
        var cepPreConsultado = await PreConsultarCepAlteradoAsync(id, request, ct);

        await SessaoAuditoria.ExecutarEmTransacaoAsync(db, logger, async sessao =>
        {
            var atual = await TravarUsuarioAsync(id, ct);
            if (!atual.Ativo)
            {
                throw ApiProblemException.Conflict("Usuário inativo.", "Um usuário excluído não pode ser alterado. Nenhum registro foi alterado.");
            }

            var (nome, email, emailNormalizado, cargoId) = (atual.Nome, atual.Email, atual.EmailNormalizado, atual.CargoId);
            if (cadastro is not null)
            {
                ExigirPermissaoSobreCargo(atual.Role, cadastro.SolicitanteRole);
                var cargo = await ObterCargoAtivoAsync(cadastro.CargoId, ct);
                ExigirPermissaoSobreCargo(cargo.Role, cadastro.SolicitanteRole);
                (nome, email, emailNormalizado, cargoId) = (cadastro.Nome, cadastro.Email, NormalizarEmail(cadastro.Email), cargo.Id);

                if (await db.Usuarios.AnyAsync(u => u.Id != id && u.EmailNormalizado == emailNormalizado, ct))
                {
                    throw EmailDeOutroUsuario();
                }
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
            var ehCin = request.EhCinInformado ? request.EhCin!.Value : atual.EhCin;
            var dataExpiracao = request.DataExpiracaoInformada ? request.DataExpiracao : atual.DataExpiracao;

            if (cpf is null && atual.Cpf is not null)
            {
                throw ApiProblemException.Invalid("Cpf", "cpf é obrigatório e não pode ser removido.");
            }

            if (Rg.FaltaNoPut(ehCin, rg, atual.EhCin, atual.Rg, request.EhCinInformado || request.RgInformado))
            {
                throw ApiProblemException.Invalid("Rg", Rg.ObrigatorioSemCin);
            }

            var erros = RegraResponsavel.Validar(dataNascimento, responsavelNome, responsavelEmail, responsavelCpf, cpf, dataCivil.Hoje());
            if (erros.Count > 0)
            {
                throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.", errors: erros);
            }

            if (cpf is not null && await db.Usuarios.AnyAsync(u => u.Id != id && u.Cpf == cpf, ct))
            {
                throw CpfDeOutroUsuario();
            }

            var endereco = await EnderecoEfetivoAsync(request, atual.Endereco, cepPreConsultado, ct);

            await sessao.DefinirContextoAuditoriaAsync(responsavelId, ipResponsavel, UsuarioHistorico.OperacaoUpdate, ct);
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(
                    $"""
                    UPDATE dbo.Usuarios SET Nome = {Varchar("@nome", nome, NomeMaximo)},Email = {Varchar("@email", email, EmailMaximo)},
                        EmailNormalizado = {Varchar("@emailNormalizado", emailNormalizado, EmailMaximo)}, CargoId = {cargoId},
                        Cpf = {Varchar("@cpf", cpf, 20)}, DataNascimento = {dataNascimento}, Telefone = {Varchar("@telefone", telefone, 20)},
                        ResponsavelNome = {Varchar("@responsavelNome", responsavelNome, NomeMaximo)},
                        ResponsavelEmail = {Varchar("@responsavelEmail", responsavelEmail, EmailMaximo)},
                        ResponsavelCpf = {Varchar("@responsavelCpf", responsavelCpf, Cpf.Digitos)},
                        ResponsavelTelefone = {Varchar("@responsavelTelefone", responsavelTelefone, TelefoneBrasileiro.MaximoDigitos)},
                        Rg = {Varchar("@rg", rg, Rg.Maximo)}, TipoSanguineo = {Varchar("@tipoSanguineo", tipoSanguineo, TipoSanguineo.Maximo)},
                        DoencasAlergias = {Varchar("@doencasAlergias", doencasAlergias, DoencasAlergiasMaximo)},
                        EhCin = {ehCin}, DataExpiracao = {dataExpiracao},
                        EnderecoCep = {Varchar("@enderecoCep", endereco.Cep, Cep.Digitos)},
                        EnderecoLogradouro = {Varchar("@enderecoLogradouro", endereco.Logradouro, Endereco.LogradouroMaximo)},
                        EnderecoNumero = {Varchar("@enderecoNumero", endereco.Numero, Endereco.NumeroMaximo)},
                        EnderecoComplemento = {Varchar("@enderecoComplemento", endereco.Complemento, Endereco.ComplementoMaximo)},
                        EnderecoComplementoCep = {Varchar("@enderecoComplementoCep", endereco.ComplementoCep, Endereco.ComplementoCepMaximo)},
                        EnderecoUnidade = {Varchar("@enderecoUnidade", endereco.Unidade, Endereco.UnidadeMaximo)},
                        EnderecoBairro = {Varchar("@enderecoBairro", endereco.Bairro, Endereco.BairroMaximo)},
                        EnderecoLocalidade = {Varchar("@enderecoLocalidade", endereco.Localidade, Endereco.LocalidadeMaximo)},
                        EnderecoUf = {Varchar("@enderecoUf", endereco.Uf, Endereco.UfTamanho)},
                        EnderecoEstado = {Varchar("@enderecoEstado", endereco.Estado, Endereco.EstadoMaximo)},
                        EnderecoRegiao = {Varchar("@enderecoRegiao", endereco.Regiao, Endereco.RegiaoMaximo)},
                        EnderecoIbge = {Varchar("@enderecoIbge", endereco.Ibge, Endereco.IbgeMaximo)},
                        EnderecoGia = {Varchar("@enderecoGia", endereco.Gia, Endereco.GiaMaximo)},
                        EnderecoDdd = {Varchar("@enderecoDdd", endereco.Ddd, Endereco.DddMaximo)},
                        EnderecoSiafi = {Varchar("@enderecoSiafi", endereco.Siafi, Endereco.SiafiMaximo)}
                    WHERE Id = {id} AND Ativo = 1
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

            ExigirPermissaoSobreCargo(atual.Role, request.SolicitanteRole);

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

    private sealed record UsuarioTravado(bool Ativo, string Nome, string Email, string EmailNormalizado, Guid CargoId, string Role,
        string? Cpf, DateOnly? DataNascimento, string? Telefone, string? ResponsavelNome, string? ResponsavelEmail, string? ResponsavelCpf, string? ResponsavelTelefone,
        string? Rg, string? TipoSanguineo, string? DoencasAlergias, bool EhCin, DateOnly? DataExpiracao, EnderecoGravado Endereco);

    private sealed record EnderecoGravado(string? Cep, string? Logradouro, string? Numero, string? Complemento, string? ComplementoCep,
        string? Unidade, string? Bairro, string? Localidade, string? Uf, string? Estado, string? Regiao, string? Ibge, string? Gia,
        string? Ddd, string? Siafi)
    {
        public static readonly EnderecoGravado Vazio = new(null, null, null, null, null, null, null, null, null, null, null, null, null, null, null);

        public static EnderecoGravado DoCep(CepDto cep, string? numero, string? complemento) =>
            new(cep.Cep, cep.Logradouro, numero, complemento, cep.Complemento, cep.Unidade, cep.Bairro, cep.Localidade, cep.Uf,
                cep.Estado, cep.Regiao, cep.Ibge, cep.Gia, cep.Ddd, cep.Siafi);
    }

    private sealed record CargoResumo(Guid Id, string Role);

    private const string NadaAlterado = "Nenhum registro foi alterado.";

    private async Task<UsuarioTravado> TravarUsuarioAsync(Guid id, CancellationToken ct) =>
        await db.Usuarios.FromSqlInterpolated($"SELECT * FROM dbo.Usuarios WITH (XLOCK, ROWLOCK) WHERE Id = {id}")
            .AsNoTracking()
            .Select(u => new UsuarioTravado(u.Ativo, u.Nome, u.Email, u.EmailNormalizado, u.CargoId, u.Cargo!.Role,
                u.Cpf, u.DataNascimento, u.Telefone, u.ResponsavelNome, u.ResponsavelEmail, u.ResponsavelCpf, u.ResponsavelTelefone, u.Rg, u.TipoSanguineo, u.DoencasAlergias,
                u.EhCin, u.DataExpiracao,
                new EnderecoGravado(u.EnderecoCep, u.EnderecoLogradouro, u.EnderecoNumero, u.EnderecoComplemento, u.EnderecoComplementoCep,
                    u.EnderecoUnidade, u.EnderecoBairro, u.EnderecoLocalidade, u.EnderecoUf, u.EnderecoEstado, u.EnderecoRegiao,
                    u.EnderecoIbge, u.EnderecoGia, u.EnderecoDdd, u.EnderecoSiafi)))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.NotFound("Usuário não encontrado.");

    private async Task<CepDto> ConsultarCepAsync(string cep, string consequencia, CancellationToken ct) =>
        (await consultaCep.ConsultarAsync(cep, ct)).ExigirEndereco(
            () => ApiProblemException.Invalid(Cep.CampoErro, $"endereco.cep não encontrado no ViaCEP. {consequencia}"), consequencia);

    /// <summary>
    /// Consulta o ViaCEP antes da transação (sem segurar o lock da linha durante a chamada HTTP), só quando o PUT troca o CEP de um
    /// usuário ativo. CEP igual ao gravado não é consultado: os dados do CEP já gravados são mantidos.
    /// </summary>
    private async Task<CepDto?> PreConsultarCepAlteradoAsync(Guid id, DadosPessoaisRequest request, CancellationToken ct)
    {
        if (!request.EnderecoInformado || request.Endereco is not { CepInformado: true } pedido || Cep.Normalizar(pedido.Cep) is not { } cep)
        {
            return null;
        }

        var gravado = await db.Usuarios.AsNoTracking().Where(u => u.Id == id && u.Ativo)
            .Select(u => new { u.EnderecoCep }).SingleOrDefaultAsync(ct);
        return gravado is null || gravado.EnderecoCep == cep ? null : await ConsultarCepAsync(cep, NadaAlterado, ct);
    }

    /// <summary>
    /// Endereço resultante do PUT. Omitido mantém tudo; null ou cep vazio remove tudo; CEP igual mantém os dados do CEP gravados; CEP
    /// novo substitui todos os dados do CEP pelos do ViaCEP e, se número ou complemento forem omitidos, eles ficam vazios (eram do
    /// endereço anterior). Falha do ViaCEP recusa o PUT inteiro, sem tocar no endereço gravado.
    /// </summary>
    private async Task<EnderecoGravado> EnderecoEfetivoAsync(DadosPessoaisRequest request, EnderecoGravado atual, CepDto? cepPreConsultado,
        CancellationToken ct)
    {
        if (!request.EnderecoInformado)
        {
            return atual;
        }

        if (request.Endereco is not { } pedido)
        {
            return EnderecoGravado.Vazio;
        }

        var cep = pedido.CepInformado ? Cep.Normalizar(pedido.Cep) : atual.Cep;
        var mesmoCep = cep == atual.Cep;
        var numero = pedido.NumeroInformado ? NormalizarTexto(pedido.Numero) : mesmoCep ? atual.Numero : null;
        var complemento = pedido.ComplementoInformado ? NormalizarTexto(pedido.Complemento) : mesmoCep ? atual.Complemento : null;

        if (cep is null)
        {
            return numero is null && complemento is null
                ? EnderecoGravado.Vazio
                : throw ApiProblemException.Invalid(Cep.CampoErro, "endereco.cep é obrigatório para informar número ou complemento.");
        }

        if (mesmoCep)
        {
            return atual with { Numero = numero, Complemento = complemento };
        }

        var dadosCep = cepPreConsultado?.Cep == cep ? cepPreConsultado : await ConsultarCepAsync(cep, NadaAlterado, ct);
        return EnderecoGravado.DoCep(dadosCep, numero, complemento);
    }

    private async Task<CargoResumo> ObterCargoAtivoAsync(Guid cargoId, CancellationToken ct) =>
        await db.Cargos.AsNoTracking()
            .Where(c => c.Id == cargoId && c.Ativo)
            .Select(c => new CargoResumo(c.Id, c.Role))
            .SingleOrDefaultAsync(ct)
        ?? throw ApiProblemException.Invalid("CargoId", "cargoId inexistente ou inativo.");

    internal static void ExigirPermissaoSobreCargo(string role, string solicitanteRole)
    {
        if (!Roles.PodeGerenciar(solicitanteRole, role))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "ACESSO NEGADO!", role == Roles.Admin
                ? "Somente um Administrador pode gerenciar usuários com o cargo Administrador."
                : "Somente a diretoria (Administrador, Diretor ou Diretor Associado) pode gerenciar usuários com os cargos Diretor, " +
                  "Diretor Associado, Secretário ou Tesoureiro.");
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
