using System.Text.Json;
using System.Text.RegularExpressions;
using Almirante.Api.Data;
using Almirante.Api.Dtos;
using Almirante.Api.Entities;
using Almirante.Api.Infrastructure;
using Almirante.Api.Security;
using Almirante.Api.Validation.Usuarios;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace Almirante.Api.Services;

/// <summary>
/// Administração de cargos, concessões e regras por alvo (issue #182). Toda escrita exige motivo e a senha atual de quem está logado,
/// confere a versão do cargo (409 sem sobrescrever) e acontece numa transação que primeiro incrementa a revisão global com trava exclusiva:
/// as alterações administrativas são serializadas entre si e com as operações sensíveis, que leem a revisão com trava compartilhada. Dentro
/// da transação a permissão administrativa do executor é conferida de novo, a mudança é aplicada (revogação lógica, nunca DELETE) e o
/// histórico imutável é gravado. Sem escalada: permissões reservadas só no perfil protegido, que não perde as mínimas; ninguém concede o
/// que não tem nem cria regra fora do próprio escopo.
/// </summary>
public sealed partial class AutorizacaoAdminService(AlmiranteDbContext db, ContasSenha contas, PermissoesService permissoes, TimeProvider clock,
    ILogger<AutorizacaoAdminService> logger)
{
    public const int RoleMaximo = 20;
    public const int NomeMaximo = 200;
    public const int DescricaoMaximo = 1000;
    private const int SqlUnicidadeIndice = 2601;
    private const int SqlUnicidadeConstraint = 2627;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,19}$")]
    private static partial Regex RoleValida();

    public static string NormalizarRole(string role) => role.Trim().ToUpperInvariant();

    // ---------------------------------------------------------------- consultas

    public async Task<IReadOnlyList<CargoDto>> ListarCargosAsync(CancellationToken ct) =>
        await db.Cargos.AsNoTracking().OrderBy(c => c.Nome).Select(ParaDto).ToListAsync(ct);

    public async Task<IReadOnlyList<PermissaoDto>> ListarPermissoesAsync(CancellationToken ct) =>
        await db.Permissoes.AsNoTracking()
            .OrderBy(p => p.Modulo).ThenBy(p => p.Chave)
            .Select(p => new PermissaoDto
            {
                Chave = p.Chave, Modulo = p.Modulo, Descricao = p.Descricao, Escrita = p.Escrita, Ativo = p.Ativo,
                ReservadaSistema = p.ReservadaSistema, ExigeRegraAlvo = p.ExigeRegraAlvo,
            })
            .ToListAsync(ct);

    public async Task<PermissoesCargoDto> ObterPermissoesAsync(Guid cargoId, CancellationToken ct)
    {
        var cargo = await db.Cargos.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cargoId, ct) ?? throw CargoNaoEncontrado();
        return new PermissoesCargoDto
        {
            CargoId = cargo.Id, Role = cargo.Role, SistemaProtegido = cargo.SistemaProtegido, Versao = cargo.Versao,
            Permissoes = await ConcessoesAtivasAsync(cargo.Id, ct),
        };
    }

    public async Task<RegrasAlvoCargoDto> ObterRegrasAlvoAsync(Guid cargoId, CancellationToken ct)
    {
        var cargo = await db.Cargos.AsNoTracking().SingleOrDefaultAsync(c => c.Id == cargoId, ct) ?? throw CargoNaoEncontrado();
        return new RegrasAlvoCargoDto { CargoId = cargo.Id, Role = cargo.Role, Versao = cargo.Versao, Regras = await RegrasAtivasAsync(cargo.Id, ct) };
    }

    /// <summary>Permissões efetivas do próprio usuário (cargo + designação) e a revisão global; nunca de terceiros.</summary>
    public async Task<MinhasPermissoesDto> MinhasPermissoesAsync(Guid usuarioId, CancellationToken ct)
    {
        var cargo = await db.Usuarios.AsNoTracking().Where(u => u.Id == usuarioId)
                        .Select(u => new MinhasPermissoesCargoDto { Id = u.Cargo!.Id, Role = u.Cargo.Role, Nome = u.Cargo.Nome })
                        .SingleOrDefaultAsync(ct)
                    ?? throw ApiProblemException.NotFound("Usuário não encontrado.");
        var efetivas = await permissoes.DoUsuarioAsync(usuarioId, ct);
        return new MinhasPermissoesDto
        {
            Cargo = cargo,
            Permissoes = efetivas.Todas,
            PermissoesPorDesignacao = efetivas.PorDesignacao,
            VersaoAutorizacao = await db.AutorizacaoEstado.AsNoTracking().Where(e => e.Id == AutorizacaoEstado.IdUnico)
                .Select(e => e.Revisao).SingleAsync(ct),
        };
    }

    // ---------------------------------------------------------------- cargos

    public async Task<CargoDto> CriarCargoAsync(CriarCargoRequest request, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        var role = NormalizarRole(request.Role ?? string.Empty);
        if (!RoleValida().IsMatch(role))
        {
            erros["role"] = [$"role é obrigatória: de 2 a {RoleMaximo} caracteres, começando por letra, só letras sem acento, dígitos e _ (gravada em maiúsculas)."];
        }

        var (nome, descricao) = ValidarNomeDescricao(request.Nome, request.Descricao, erros);
        var executor = await PrepararAsync(request, erros, ct);

        var dto = await EmTransacaoAsync(async revisao =>
        {
            await ExigirPermissaoDoExecutorAsync(executor, Permissoes.AutorizacaoCargosGerenciar, ct);
            if (await db.Cargos.AnyAsync(c => c.Role == role, ct))
            {
                throw RoleDuplicada(role);
            }

            var agora = Agora();
            var cargo = new Cargo
            {
                Id = Guid.NewGuid(), Role = role, Nome = nome, Descricao = descricao, Ativo = true, SistemaProtegido = false, Versao = 1,
                CriadoPor = executor.Id.ToString(), CriadoEm = agora,
            };
            db.Cargos.Add(cargo);
            Auditar(executor, request, cargo.Id, AutorizacaoHistorico.CargoCriado, antes: null, depois: Fotografia(cargo), revisao, agora);
            await SalvarAsync(() => RoleDuplicada(role), ct);
            return ParaDtoCompilado(cargo);
        }, ct);

        logger.LogInformation("Cargo {CargoId} ({Role}) criado por {UsuarioId}, sem permissões.", dto.Id, dto.Role, executor.Id);
        return dto;
    }

    public async Task<CargoDto> AtualizarCargoAsync(Guid cargoId, AtualizarCargoRequest request, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        var (nome, descricao) = ValidarNomeDescricao(request.Nome, request.Descricao, erros);
        ExigirVersaoInformada(request.Versao, erros);
        var executor = await PrepararAsync(request, erros, ct);

        return await EmTransacaoAsync(async revisao =>
        {
            await ExigirPermissaoDoExecutorAsync(executor, Permissoes.AutorizacaoCargosGerenciar, ct);
            var cargo = await CargoNaVersaoAsync(cargoId, request.Versao!.Value, ct);
            var antes = Fotografia(cargo);
            cargo.Nome = nome;
            cargo.Descricao = descricao;
            var agora = Agora();
            Avancar(cargo, agora);
            Auditar(executor, request, cargo.Id, AutorizacaoHistorico.CargoAlterado, antes, Fotografia(cargo), revisao, agora);
            await SalvarAsync(null, ct);
            return ParaDtoCompilado(cargo);
        }, ct);
    }

    public Task<CargoDto> DesativarCargoAsync(Guid cargoId, SituacaoCargoRequest request, CancellationToken ct) =>
        AlterarSituacaoAsync(cargoId, request, ativo: false, ct);

    public Task<CargoDto> ReativarCargoAsync(Guid cargoId, SituacaoCargoRequest request, CancellationToken ct) =>
        AlterarSituacaoAsync(cargoId, request, ativo: true, ct);

    private async Task<CargoDto> AlterarSituacaoAsync(Guid cargoId, SituacaoCargoRequest request, bool ativo, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        ExigirVersaoInformada(request.Versao, erros);
        var executor = await PrepararAsync(request, erros, ct);

        return await EmTransacaoAsync(async revisao =>
        {
            await ExigirPermissaoDoExecutorAsync(executor, Permissoes.AutorizacaoCargosGerenciar, ct);
            var cargo = await CargoNaVersaoAsync(cargoId, request.Versao!.Value, ct);
            if (cargo.Ativo == ativo)
            {
                throw ApiProblemException.Conflict(ativo ? "Cargo já está ativo." : "Cargo já está inativo.", "Nenhum registro foi alterado.");
            }

            if (!ativo)
            {
                if (cargo.SistemaProtegido)
                {
                    throw ApiProblemException.Conflict("Perfil de sistema protegido.",
                        "O perfil administrativo de sistema não pode ser desativado. Nenhum registro foi alterado.");
                }

                var usuariosAtivos = await UsuariosAtivosNoCargoAsync(cargo.Id, ct);
                if (usuariosAtivos > 0)
                {
                    throw ApiProblemException.Conflict("Cargo com usuários ativos.",
                        $"{usuariosAtivos} usuário(s) ativo(s) têm este cargo. Reatribua-os a outro cargo antes de desativá-lo. Nenhum registro foi alterado.",
                        new Dictionary<string, object?> { ["usuariosAtivos"] = usuariosAtivos });
                }
            }

            var antes = Fotografia(cargo);
            cargo.Ativo = ativo;
            var agora = Agora();
            Avancar(cargo, agora);
            Auditar(executor, request, cargo.Id, ativo ? AutorizacaoHistorico.CargoReativado : AutorizacaoHistorico.CargoDesativado, antes,
                Fotografia(cargo), revisao, agora);
            await SalvarAsync(null, ct);
            return ParaDtoCompilado(cargo);
        }, ct);
    }

    // ---------------------------------------------------------------- concessões

    public async Task<PermissoesCargoDto> SubstituirPermissoesAsync(Guid cargoId, SubstituirPermissoesCargoRequest request, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        ExigirVersaoInformada(request.Versao, erros);
        if (request.Permissoes is null)
        {
            erros["permissoes"] = ["permissoes é obrigatório (lista completa; envie [] para revogar todas)."];
        }

        var executor = await PrepararAsync(request, erros, ct);
        var desejadas = request.Permissoes!.Select(p => p?.Trim() ?? string.Empty).ToHashSet(StringComparer.Ordinal);

        var resultado = await EmTransacaoAsync(async revisao =>
        {
            await ExigirPermissaoDoExecutorAsync(executor, Permissoes.AutorizacaoConcessoesGerenciar, ct);
            var cargo = await CargoNaVersaoAsync(cargoId, request.Versao!.Value, ct);
            var catalogo = await db.Permissoes.AsNoTracking().ToDictionaryAsync(p => p.Chave, StringComparer.Ordinal, ct);

            var desconhecidas = desejadas.Where(c => !catalogo.TryGetValue(c, out var p) || !p.Ativo).Order(StringComparer.Ordinal).ToList();
            if (desconhecidas.Count > 0)
            {
                throw ApiProblemException.Invalid("permissoes", $"Permissões inexistentes ou inativas: {string.Join(", ", desconhecidas)}.");
            }

            var reservadas = catalogo.Values.Where(p => p.ReservadaSistema).Select(p => p.Chave).ToHashSet(StringComparer.Ordinal);
            if (!cargo.SistemaProtegido && desejadas.Overlaps(reservadas))
            {
                throw ApiProblemException.Invalid("permissoes",
                    "Permissões de administração de autorização são reservadas ao perfil de sistema protegido e não podem ser concedidas a este cargo: " +
                    string.Join(", ", desejadas.Intersect(reservadas).Order(StringComparer.Ordinal)) + ".");
            }

            if (cargo.SistemaProtegido && !reservadas.IsSubsetOf(desejadas))
            {
                throw ApiProblemException.Conflict("Capacidades mínimas do perfil protegido.",
                    "O perfil de sistema protegido não pode perder as permissões de administração de autorização. Nenhum registro foi alterado.");
            }

            var atuais = await db.CargoPermissoes.Include(cp => cp.Permissao).Where(cp => cp.CargoId == cargo.Id).ToListAsync(ct);
            var antes = atuais.Where(cp => cp.Ativo).Select(cp => cp.Permissao!.Chave).Order(StringComparer.Ordinal).ToList();
            var novas = desejadas.Except(antes, StringComparer.Ordinal).ToList();
            var doExecutor = await permissoes.DoCargoAsync(executor.CargoId, ct);
            var semEscopo = novas.Where(c => !doExecutor.Contains(c)).Order(StringComparer.Ordinal).ToList();
            if (semEscopo.Count > 0)
            {
                throw new ApiProblemException(StatusCodes.Status403Forbidden, "ACESSO NEGADO!",
                    $"Você só concede permissões que o seu cargo tem. Fora do seu escopo: {string.Join(", ", semEscopo)}.");
            }

            var agora = Agora();
            foreach (var concessao in atuais)
            {
                var manter = desejadas.Contains(concessao.Permissao!.Chave);
                if (manter && !concessao.Ativo)
                {
                    Reconceder(concessao, executor.Id, agora);
                }
                else if (!manter && concessao.Ativo)
                {
                    (concessao.Ativo, concessao.RevogadaEmUtc, concessao.RevogadaPorUsuarioId) = (false, agora, executor.Id);
                }
            }

            foreach (var chave in desejadas.Where(c => atuais.All(a => a.Permissao!.Chave != c)))
            {
                db.CargoPermissoes.Add(new CargoPermissao
                {
                    Id = Guid.NewGuid(), CargoId = cargo.Id, PermissaoId = catalogo[chave].Id, Ativo = true, ConcedidaEmUtc = agora,
                    ConcedidaPorUsuarioId = executor.Id,
                });
            }

            Avancar(cargo, agora);
            var depois = desejadas.Order(StringComparer.Ordinal).ToList();
            Auditar(executor, request, cargo.Id, AutorizacaoHistorico.ConcessoesSubstituidas, Serializar(antes), Serializar(depois), revisao, agora);
            await SalvarAsync(null, ct);
            return new PermissoesCargoDto
            {
                CargoId = cargo.Id, Role = cargo.Role, SistemaProtegido = cargo.SistemaProtegido, Versao = cargo.Versao, Permissoes = depois,
            };
        }, ct);

        logger.LogInformation("Concessões do cargo {CargoId} substituídas por {UsuarioId}: {Quantidade} permissões.", cargoId, executor.Id,
            resultado.Permissoes.Count);
        return resultado;
    }

    // ---------------------------------------------------------------- regras por alvo

    public async Task<RegrasAlvoCargoDto> SubstituirRegrasAlvoAsync(Guid cargoId, SubstituirRegrasAlvoRequest request, CancellationToken ct)
    {
        var erros = new Dictionary<string, string[]>();
        ExigirVersaoInformada(request.Versao, erros);
        if (request.Regras is null)
        {
            erros["regras"] = ["regras é obrigatório (lista completa; envie [] para revogar todas)."];
        }
        else if (request.Regras.Any(r => r is null || string.IsNullOrWhiteSpace(r.Permissao) || r.CargoAlvoId is null))
        {
            erros["regras"] = ["Cada regra precisa de permissao e cargoAlvoId."];
        }

        var executor = await PrepararAsync(request, erros, ct);
        var desejadas = request.Regras!.Select(r => (Permissao: r.Permissao!.Trim(), CargoAlvoId: r.CargoAlvoId!.Value)).ToHashSet();

        var resultado = await EmTransacaoAsync(async revisao =>
        {
            await ExigirPermissaoDoExecutorAsync(executor, Permissoes.AutorizacaoRegrasAlvoGerenciar, ct);
            var cargo = await CargoNaVersaoAsync(cargoId, request.Versao!.Value, ct);
            var catalogo = await db.Permissoes.AsNoTracking().ToDictionaryAsync(p => p.Chave, StringComparer.Ordinal, ct);
            var invalidas = desejadas.Select(d => d.Permissao).Distinct()
                .Where(c => !catalogo.TryGetValue(c, out var p) || !p.Ativo || !p.ExigeRegraAlvo).Order(StringComparer.Ordinal).ToList();
            if (invalidas.Count > 0)
            {
                throw ApiProblemException.Invalid("regras",
                    $"Permissões inexistentes, inativas ou que não usam regra por alvo: {string.Join(", ", invalidas)}.");
            }

            var idsAlvo = desejadas.Select(d => d.CargoAlvoId).Distinct().ToList();
            var alvos = await db.Cargos.AsNoTracking().Where(c => idsAlvo.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
            if (alvos.Count != idsAlvo.Count)
            {
                throw ApiProblemException.Invalid("regras", "cargoAlvoId inexistente em uma ou mais regras.");
            }

            var atuais = await db.CargoRegrasAlvo.Include(r => r.Permissao).Include(r => r.CargoAlvo)
                .Where(r => r.CargoSolicitanteId == cargo.Id).ToListAsync(ct);
            var antes = atuais.Where(r => r.Ativo).Select(r => Regra(r.Permissao!.Chave, r.CargoAlvo!)).OrderBy(r => r.Permissao).ThenBy(r => r.CargoAlvoRole).ToList();

            // Escopo delegável: só regras para permissões que o executor tem e, sobre o perfil protegido, só as que ele próprio tem.
            var doExecutor = await permissoes.DoCargoAsync(executor.CargoId, ct);
            foreach (var (permissao, alvoId) in desejadas.Where(d => !atuais.Any(a => a.Ativo && a.Permissao!.Chave == d.Permissao && a.CargoAlvoId == d.CargoAlvoId)))
            {
                var alvo = alvos[alvoId];
                if (!doExecutor.Contains(permissao)
                    || (alvo.SistemaProtegido && !await permissoes.PodeSobreAlvoAsync(executor.Role, permissao, alvo.Role, ct)))
                {
                    throw new ApiProblemException(StatusCodes.Status403Forbidden, "ACESSO NEGADO!",
                        $"A regra {permissao} sobre o cargo {alvo.Role} está fora do seu escopo delegável.");
                }
            }

            var agora = Agora();
            foreach (var regra in atuais)
            {
                var manter = desejadas.Contains((regra.Permissao!.Chave, regra.CargoAlvoId));
                if (manter && !regra.Ativo)
                {
                    (regra.Ativo, regra.ConcedidaEmUtc, regra.ConcedidaPorUsuarioId, regra.RevogadaEmUtc, regra.RevogadaPorUsuarioId) =
                        (true, agora, executor.Id, null, null);
                }
                else if (!manter && regra.Ativo)
                {
                    (regra.Ativo, regra.RevogadaEmUtc, regra.RevogadaPorUsuarioId) = (false, agora, executor.Id);
                }
            }

            foreach (var (permissao, alvoId) in desejadas.Where(d => !atuais.Any(a => a.Permissao!.Chave == d.Permissao && a.CargoAlvoId == d.CargoAlvoId)))
            {
                db.CargoRegrasAlvo.Add(new CargoRegraAlvo
                {
                    Id = Guid.NewGuid(), CargoSolicitanteId = cargo.Id, PermissaoId = catalogo[permissao].Id, CargoAlvoId = alvoId, Ativo = true,
                    ConcedidaEmUtc = agora, ConcedidaPorUsuarioId = executor.Id,
                });
            }

            Avancar(cargo, agora);
            var depois = desejadas.Select(d => Regra(d.Permissao, alvos[d.CargoAlvoId])).OrderBy(r => r.Permissao).ThenBy(r => r.CargoAlvoRole).ToList();
            Auditar(executor, request, cargo.Id, AutorizacaoHistorico.RegrasAlvoSubstituidas, Serializar(antes), Serializar(depois), revisao, agora);
            await SalvarAsync(null, ct);
            return new RegrasAlvoCargoDto { CargoId = cargo.Id, Role = cargo.Role, Versao = cargo.Versao, Regras = depois };
        }, ct);

        logger.LogInformation("Regras por alvo do cargo {CargoId} substituídas por {UsuarioId}: {Quantidade} regras.", cargoId, executor.Id,
            resultado.Regras.Count);
        return resultado;
    }

    // ---------------------------------------------------------------- infraestrutura

    private sealed record Executor(Guid Id, Guid CargoId, string Role);

    /// <summary>
    /// Validação do motivo e da senha, nesta ordem: erros de campo (400) antes de verificar a senha; a senha é verificada contra o hash do
    /// banco (PBKDF2) fora da transação, sobre a sessão vigente do token (401 se deixou de valer).
    /// </summary>
    private async Task<Executor> PrepararAsync(AlteracaoAutorizacaoRequest request, Dictionary<string, string[]> erros, CancellationToken ct)
    {
        var motivo = request.Motivo?.Trim();
        if (string.IsNullOrEmpty(motivo))
        {
            erros["motivo"] = ["motivo é obrigatório."];
        }
        else if (motivo.Length > AlmiranteDbContext.MotivoAutorizacaoMaximo || !TextoVarchar.CabeNaPagina1252(motivo))
        {
            erros["motivo"] = [$"motivo deve ter no máximo {AlmiranteDbContext.MotivoAutorizacaoMaximo} caracteres, sem emojis ou caracteres de controle."];
        }

        if (string.IsNullOrEmpty(request.SenhaAtualSolicitante))
        {
            erros[ContasSenha.CampoSenhaAtual] = ["senhaAtualSolicitante é obrigatória: confirme a sua senha."];
        }

        if (erros.Count > 0)
        {
            throw new ApiProblemException(StatusCodes.Status400BadRequest, "Dados inválidos.", errors: erros);
        }

        var solicitante = await contas.CarregarSolicitanteAsync(request.Contexto, Agora(), ct);
        contas.ConfirmarSenhaSolicitante(solicitante, request.SenhaAtualSolicitante!);
        var cargo = await db.Usuarios.AsNoTracking().Where(u => u.Id == solicitante.Id).Select(u => u.CargoId).SingleAsync(ct);
        return new Executor(solicitante.Id, cargo, solicitante.Role);
    }

    /// <summary>
    /// Transação com a revisão global incrementada primeiro (trava exclusiva até o commit). No EF InMemory dos testes sem dependências
    /// externas não há transação: a revisão é incrementada pelo próprio SaveChanges do trabalho.
    /// </summary>
    private async Task<T> EmTransacaoAsync<T>(Func<long, Task<T>> trabalho, CancellationToken ct)
    {
        T resultado;
        if (!db.Database.IsRelational())
        {
            db.ChangeTracker.Clear();
            var estado = await db.AutorizacaoEstado.SingleAsync(e => e.Id == AutorizacaoEstado.IdUnico, ct);
            estado.Revisao++;
            resultado = await trabalho(estado.Revisao);
        }
        else
        {
            var strategy = db.Database.CreateExecutionStrategy();
            resultado = await strategy.ExecuteAsync(async () =>
            {
                db.ChangeTracker.Clear();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var revisao = (await db.Database.SqlQuery<long>($"""
                    UPDATE [dbo].[AutorizacaoEstado] SET [Revisao] = [Revisao] + 1 OUTPUT inserted.[Revisao] AS [Value] WHERE [Id] = 1
                    """).ToListAsync(ct)).Single();
                var dentro = await trabalho(revisao);
                await tx.CommitAsync(ct);
                return dentro;
            });
        }

        permissoes.Esquecer();
        return resultado;
    }

    /// <summary>A permissão administrativa é conferida de novo dentro da transação: uma revogação concorrente já confirmada vale.</summary>
    private async Task ExigirPermissaoDoExecutorAsync(Executor executor, string permissao, CancellationToken ct)
    {
        permissoes.Esquecer();
        if (!(await permissoes.DoUsuarioAsync(executor.Id, ct)).Tem(permissao))
        {
            throw new ApiProblemException(StatusCodes.Status403Forbidden, "ACESSO NEGADO!", $"Seu cargo não tem a permissão {permissao}.");
        }
    }

    private async Task<Cargo> CargoNaVersaoAsync(Guid cargoId, long versao, CancellationToken ct)
    {
        var cargo = await db.Cargos.SingleOrDefaultAsync(c => c.Id == cargoId, ct) ?? throw CargoNaoEncontrado();
        return cargo.Versao == versao ? cargo : throw VersaoObsoleta(cargo.Versao);
    }

    private async Task<int> UsuariosAtivosNoCargoAsync(Guid cargoId, CancellationToken ct) =>
        db.Database.IsRelational()
            ? await db.Database.SqlQuery<int>($"""
                SELECT COUNT(*) AS [Value] FROM [dbo].[Usuarios] WITH (UPDLOCK, HOLDLOCK, ROWLOCK) WHERE [CargoId] = {cargoId} AND [Ativo] = 1
                """).SingleAsync(ct)
            : await db.Usuarios.CountAsync(u => u.CargoId == cargoId && u.Ativo, ct);

    private async Task SalvarAsync(Func<ApiProblemException>? unicidade, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw VersaoObsoleta(null);
        }
        catch (DbUpdateException ex) when (unicidade is not null && ex.InnerException is SqlException { Number: SqlUnicidadeIndice or SqlUnicidadeConstraint })
        {
            throw unicidade();
        }
    }

    private void Auditar(Executor executor, AlteracaoAutorizacaoRequest request, Guid cargoId, string operacao, string? antes, string depois,
        long revisao, DateTime agora) =>
        db.AutorizacaoHistorico.Add(new AutorizacaoHistorico
        {
            Id = Guid.NewGuid(), OcorridoEmUtc = agora, UsuarioResponsavelId = executor.Id, CargoId = cargoId, Operacao = operacao, Antes = antes,
            Depois = depois, Motivo = request.Motivo!.Trim(), IpResponsavel = request.Contexto.Ip, TraceId = request.Contexto.TraceId,
            RevisaoAutorizacao = revisao,
        });

    private static void Avancar(Cargo cargo, DateTime agora)
    {
        cargo.Versao++;
        cargo.UltimaAtualizacao = agora;
    }

    private static void Reconceder(CargoPermissao concessao, Guid executorId, DateTime agora) =>
        (concessao.Ativo, concessao.ConcedidaEmUtc, concessao.ConcedidaPorUsuarioId, concessao.RevogadaEmUtc, concessao.RevogadaPorUsuarioId) =
        (true, agora, executorId, null, null);

    private static (string Nome, string Descricao) ValidarNomeDescricao(string? nome, string? descricao, Dictionary<string, string[]> erros)
    {
        var n = nome?.Trim() ?? string.Empty;
        var d = descricao?.Trim() ?? string.Empty;
        if (n.Length == 0 || n.Length > NomeMaximo || !TextoVarchar.CabeNaPagina1252(n))
        {
            erros["nome"] = [$"nome é obrigatório, com no máximo {NomeMaximo} caracteres, sem emojis ou caracteres de controle."];
        }

        if (d.Length == 0 || d.Length > DescricaoMaximo || !TextoVarchar.CabeNaPagina1252(d))
        {
            erros["descricao"] = [$"descricao é obrigatória, com no máximo {DescricaoMaximo} caracteres, sem emojis ou caracteres de controle."];
        }

        return (n, d);
    }

    private static void ExigirVersaoInformada(long? versao, Dictionary<string, string[]> erros)
    {
        if (versao is null or < 1)
        {
            erros["versao"] = ["versao é obrigatória: use a versão devolvida na última consulta do cargo."];
        }
    }

    private async Task<IReadOnlyList<string>> ConcessoesAtivasAsync(Guid cargoId, CancellationToken ct) =>
        (await db.CargoPermissoes.AsNoTracking().Where(cp => cp.CargoId == cargoId && cp.Ativo).Select(cp => cp.Permissao!.Chave).ToListAsync(ct))
        .Order(StringComparer.Ordinal).ToList();

    private async Task<IReadOnlyList<RegraAlvoDto>> RegrasAtivasAsync(Guid cargoId, CancellationToken ct) =>
        (await db.CargoRegrasAlvo.AsNoTracking().Where(r => r.CargoSolicitanteId == cargoId && r.Ativo)
            .Select(r => new RegraAlvoDto { Permissao = r.Permissao!.Chave, CargoAlvoId = r.CargoAlvoId, CargoAlvoRole = r.CargoAlvo!.Role, CargoAlvoNome = r.CargoAlvo.Nome })
            .ToListAsync(ct))
        .OrderBy(r => r.Permissao, StringComparer.Ordinal).ThenBy(r => r.CargoAlvoRole, StringComparer.Ordinal).ToList();

    private static RegraAlvoDto Regra(string permissao, Cargo alvo) =>
        new() { Permissao = permissao, CargoAlvoId = alvo.Id, CargoAlvoRole = alvo.Role, CargoAlvoNome = alvo.Nome };

    private static string Fotografia(Cargo c) =>
        Serializar(new { c.Role, c.Nome, c.Descricao, c.Ativo, c.SistemaProtegido, c.Versao });

    private static string Serializar(object valor) => JsonSerializer.Serialize(valor, Json);

    private DateTime Agora() => clock.GetUtcNow().UtcDateTime;

    private static readonly System.Linq.Expressions.Expression<Func<Cargo, CargoDto>> ParaDto = c => new CargoDto
    {
        Id = c.Id, Nome = c.Nome, Descricao = c.Descricao, Ativo = c.Ativo, CriadoPor = c.CriadoPor, CriadoEm = c.CriadoEm,
        UltimaAtualizacao = c.UltimaAtualizacao, Role = c.Role, SistemaProtegido = c.SistemaProtegido, Versao = c.Versao,
    };

    private static readonly Func<Cargo, CargoDto> ParaDtoCompilado = ParaDto.Compile();

    private static ApiProblemException CargoNaoEncontrado() => ApiProblemException.NotFound("Cargo não encontrado.");

    private static ApiProblemException VersaoObsoleta(long? atual) => ApiProblemException.Conflict("Versão obsoleta.",
        "O cargo, as concessões ou as regras dele foram alterados por outra pessoa. Nada foi sobrescrito: recarregue e refaça a alteração.",
        atual is null ? null : new Dictionary<string, object?> { ["versaoAtual"] = atual });

    private static ApiProblemException RoleDuplicada(string role) => new(StatusCodes.Status409Conflict, "Role já cadastrada.",
        "Já existe um cargo com esta role. Nenhum registro foi alterado.",
        errors: new Dictionary<string, string[]> { ["role"] = [$"A role {role} já está em uso por outro cargo."] });
}
