using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace Almirante.Api.Data.Migrations;

// [DbContext] é obrigatório para o EF Core descobrir a migration (ver MigrationsTests).
[DbContext(typeof(AlmiranteDbContext))]
[Migration("20260917120000_UnifyLancamentosFlow")]
public partial class UnifyLancamentosFlow : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");
        migrationBuilder.DropIndex(name: "IX_Lancamentos_Tipo", table: "Lancamentos");
        migrationBuilder.RenameColumn(name: "Tipo", table: "Lancamentos", newName: "Finalidade");
        migrationBuilder.RenameColumn(name: "Tipo", table: "LancamentosOperacoes", newName: "Finalidade");
        migrationBuilder.RenameColumn(name: "Tipo", table: "lancamentos_deletados", newName: "Finalidade");
        migrationBuilder.DropColumn(name: "MembroNome", table: "Lancamentos");
        migrationBuilder.DropColumn(name: "Moeda", table: "Lancamentos");
        migrationBuilder.DropColumn(name: "MembroNome", table: "lancamentos_deletados");
        migrationBuilder.DropColumn(name: "Moeda", table: "lancamentos_deletados");
        migrationBuilder.AddColumn<string>(name: "Descricao", table: "LancamentosOperacoes", type: "nvarchar(500)", maxLength: 500, nullable: true);
        migrationBuilder.CreateIndex(name: "IX_Lancamentos_Finalidade", table: "Lancamentos", column: "Finalidade");
        migrationBuilder.AddForeignKey(name: "FK_Lancamentos_Usuarios_MembroId", table: "Lancamentos", column: "MembroId", principalTable: "Usuarios", principalColumn: "Id", onDelete: ReferentialAction.Restrict);
        CreateTrigger(migrationBuilder);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");
        migrationBuilder.DropForeignKey(name: "FK_Lancamentos_Usuarios_MembroId", table: "Lancamentos");
        migrationBuilder.DropIndex(name: "IX_Lancamentos_Finalidade", table: "Lancamentos");
        migrationBuilder.DropColumn(name: "Descricao", table: "LancamentosOperacoes");
        migrationBuilder.AddColumn<string>(name: "MembroNome", table: "Lancamentos", type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "Moeda", table: "Lancamentos", type: "nvarchar(3)", maxLength: 3, nullable: false, defaultValue: "BRL");
        migrationBuilder.AddColumn<string>(name: "MembroNome", table: "lancamentos_deletados", type: "nvarchar(200)", maxLength: 200, nullable: false, defaultValue: "");
        migrationBuilder.AddColumn<string>(name: "Moeda", table: "lancamentos_deletados", type: "nvarchar(3)", maxLength: 3, nullable: false, defaultValue: "BRL");
        migrationBuilder.RenameColumn(name: "Finalidade", table: "Lancamentos", newName: "Tipo");
        migrationBuilder.RenameColumn(name: "Finalidade", table: "LancamentosOperacoes", newName: "Tipo");
        migrationBuilder.RenameColumn(name: "Finalidade", table: "lancamentos_deletados", newName: "Tipo");
        migrationBuilder.CreateIndex(name: "IX_Lancamentos_Tipo", table: "Lancamentos", column: "Tipo");
    }

    private static void CreateTrigger(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica] ON [Lancamentos] AFTER UPDATE AS
        BEGIN
          SET NOCOUNT ON;
          IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0) RETURN;
          DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
          DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
          DECLARE @Motivo nvarchar(255)=TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(255));
          IF @Usuario IS NULL OR @Ip IS NULL OR @Motivo IS NULL THROW 50001, 'Contexto de auditoria obrigatório.', 1;
          INSERT INTO [lancamentos_deletados] ([Id],[LancamentoId],[UsuarioResponsavelId],[IpResponsavel],[ExcluidoEmUtc],[Motivo],[MembroId],[Finalidade],[Categoria],[TipoFluxo],[Valor],[Vencimento],[Status],[OperacaoId],[DataCriacaoOriginal])
          SELECT NEWID(),d.Id,@Usuario,@Ip,SYSUTCDATETIME(),@Motivo,d.MembroId,d.Finalidade,d.Categoria,d.TipoFluxo,d.Valor,d.Vencimento,d.Status,d.OperacaoId,d.DataCriacao
          FROM deleted d JOIN inserted i ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0;
        END
        """);
}
