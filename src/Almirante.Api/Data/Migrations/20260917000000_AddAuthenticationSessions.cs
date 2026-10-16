using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations;

[DbContext(typeof(AlmiranteDbContext))]
[Migration("20260917000000_AddAuthenticationSessions")]
public partial class AddAuthenticationSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<long>(name: "SecurityVersion", table: "Usuarios", type: "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.CreateTable(name: "AuthSessions", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            LastRenewedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            AbsoluteExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
            RevocationReason = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
            SecurityVersion = table.Column<long>(type: "bigint", nullable: false),
            RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
        }, constraints: table => { table.PrimaryKey("PK_AuthSessions", x => x.Id); table.ForeignKey("FK_AuthSessions_Usuarios_UsuarioId", x => x.UsuarioId, "Usuarios", "Id", onDelete: ReferentialAction.Cascade); });
        migrationBuilder.CreateTable(name: "RefreshTokens", columns: table => new
        {
            Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            SessionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
            TokenHash = table.Column<byte[]>(type: "binary(32)", nullable: false),
            CreatedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
            ConsumedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
            RevokedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
            ReplacedByTokenId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
            RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_RefreshTokens", x => x.Id);
            table.ForeignKey("FK_RefreshTokens_AuthSessions_SessionId", x => x.SessionId, "AuthSessions", "Id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_RefreshTokens_RefreshTokens_ReplacedByTokenId", x => x.ReplacedByTokenId, "RefreshTokens", "Id", onDelete: ReferentialAction.NoAction);
        });
        migrationBuilder.CreateIndex("IX_AuthSessions_AbsoluteExpiresAtUtc", "AuthSessions", "AbsoluteExpiresAtUtc");
        migrationBuilder.CreateIndex("IX_AuthSessions_UsuarioId_AbsoluteExpiresAtUtc", "AuthSessions", new[] { "UsuarioId", "AbsoluteExpiresAtUtc" });
        migrationBuilder.CreateIndex("IX_RefreshTokens_ExpiresAtUtc", "RefreshTokens", "ExpiresAtUtc");
        migrationBuilder.CreateIndex("IX_RefreshTokens_ReplacedByTokenId", "RefreshTokens", "ReplacedByTokenId");
        migrationBuilder.CreateIndex("IX_RefreshTokens_SessionId_ExpiresAtUtc", "RefreshTokens", new[] { "SessionId", "ExpiresAtUtc" });
        migrationBuilder.CreateIndex("IX_RefreshTokens_TokenHash", "RefreshTokens", "TokenHash", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("RefreshTokens"); migrationBuilder.DropTable("AuthSessions");
        migrationBuilder.DropColumn("SecurityVersion", "Usuarios");
    }
}
