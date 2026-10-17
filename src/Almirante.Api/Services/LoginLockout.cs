using System.Linq.Expressions;
using Almirante.Api.Data;
using Almirante.Api.Entities;
using Almirante.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Almirante.Api.Services;

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

    public static void RegistrarSucesso(Usuario usuario)
    {
        if (usuario.FalhasLoginConsecutivas == 0 && usuario.UltimaFalhaLoginUtc is null && usuario.LoginBloqueadoAteUtc is null) return;
        usuario.FalhasLoginConsecutivas = 0;
        usuario.UltimaFalhaLoginUtc = null;
        usuario.LoginBloqueadoAteUtc = null;
    }
}
