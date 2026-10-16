using System.Linq.Expressions;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

// Bloqueio temporário por conta após falhas de senha consecutivas.
//
// - Recuperação: o bloqueio expira sozinho após LockoutMinutes; tentativas durante o bloqueio não
//   o prolongam nem incrementam o contador (o fim do bloqueio é determinável).
// - Janela: falhas mais antigas que FailureWindowMinutes reiniciam a contagem.
// - Sucesso (fora de bloqueio) zera o contador.
// - Concorrência: no SQL Server o incremento é um único UPDATE atômico (ExecuteUpdate), com as
//   expressões avaliadas sobre os valores anteriores da linha; tentativas simultâneas não perdem
//   incrementos. O provider InMemory (testes) não suporta ExecuteUpdate, então aplica as MESMAS
//   expressões compiladas sobre a entidade rastreada.
// - Enumeração: o chamador responde exatamente igual (401 genérico) para conta inexistente, senha
//   errada ou conta bloqueada; e-mails inexistentes não geram contador.
public sealed class LoginLockout(AlmiranteDbContext db, IOptions<LoginProtectionOptions> options)
{
    private LoginProtectionOptions.LockoutSettings Settings => options.Value.Lockout;

    public static bool EstaBloqueado(Usuario usuario, DateTime agoraUtc) =>
        usuario.LoginBloqueadoAteUtc is { } ate && ate > agoraUtc;

    public async Task RegistrarFalhaAsync(Usuario usuario, DateTime agoraUtc, CancellationToken ct)
    {
        var inicioJanela = agoraUtc.AddMinutes(-Settings.FailureWindowMinutes);
        var bloqueioAte = agoraUtc.AddMinutes(Settings.LockoutMinutes);
        var maximo = Settings.MaxFailedAttempts;

        // "Reinicia" quando não há falha recente ou quando um bloqueio anterior já expirou.
        Expression<Func<Usuario, int>> novoContador = u =>
            u.UltimaFalhaLoginUtc == null || u.UltimaFalhaLoginUtc <= inicioJanela ||
            (u.LoginBloqueadoAteUtc != null && u.LoginBloqueadoAteUtc <= agoraUtc)
                ? 1
                : u.FalhasLoginConsecutivas + 1;
        Expression<Func<Usuario, DateTime?>> novoBloqueio = u =>
            (u.UltimaFalhaLoginUtc == null || u.UltimaFalhaLoginUtc <= inicioJanela ||
             (u.LoginBloqueadoAteUtc != null && u.LoginBloqueadoAteUtc <= agoraUtc)
                ? 1
                : u.FalhasLoginConsecutivas + 1) >= maximo
                ? bloqueioAte
                : (u.LoginBloqueadoAteUtc != null && u.LoginBloqueadoAteUtc <= agoraUtc ? null : u.LoginBloqueadoAteUtc);

        if (db.Database.IsRelational())
        {
            await db.Usuarios
                .Where(u => u.Id == usuario.Id && (u.LoginBloqueadoAteUtc == null || u.LoginBloqueadoAteUtc <= agoraUtc))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.FalhasLoginConsecutivas, novoContador)
                    .SetProperty(u => u.LoginBloqueadoAteUtc, novoBloqueio)
                    .SetProperty(u => u.UltimaFalhaLoginUtc, agoraUtc), ct);
            return;
        }

        if (EstaBloqueado(usuario, agoraUtc)) return;
        var contador = novoContador.Compile()(usuario);
        var bloqueio = novoBloqueio.Compile()(usuario);
        usuario.FalhasLoginConsecutivas = contador;
        usuario.LoginBloqueadoAteUtc = bloqueio;
        usuario.UltimaFalhaLoginUtc = agoraUtc;
        await db.SaveChangesAsync(ct);
    }

    // Marca a entidade rastreada; persiste junto com o SaveChanges do login bem-sucedido.
    public static void RegistrarSucesso(Usuario usuario)
    {
        if (usuario.FalhasLoginConsecutivas == 0 && usuario.UltimaFalhaLoginUtc is null && usuario.LoginBloqueadoAteUtc is null) return;
        usuario.FalhasLoginConsecutivas = 0;
        usuario.UltimaFalhaLoginUtc = null;
        usuario.LoginBloqueadoAteUtc = null;
    }
}
