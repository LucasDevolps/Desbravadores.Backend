using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEventos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Finalidade",
                table: "lancamentos_deletados",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<Guid>(
                name: "EventoId",
                table: "lancamentos_deletados",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Finalidade",
                table: "Lancamentos",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50);

            migrationBuilder.AddColumn<Guid>(
                name: "EventoId",
                table: "Lancamentos",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "eventos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoGrupoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataEvento = table.Column<DateOnly>(type: "date", nullable: false),
                    Local = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransporteEhGratis = table.Column<bool>(type: "bit", nullable: false),
                    TransporteValor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AlimentacaoIndividual = table.Column<bool>(type: "bit", nullable: false),
                    AlimentacaoValor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SeguroObrigatorio = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ValorPorMembro = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eventos", x => x.Id);
                    table.UniqueConstraint("AK_eventos_Id_EventoGrupoId", x => new { x.Id, x.EventoGrupoId });
                    table.CheckConstraint("CK_eventos_grupo", "[EventoReferenciaId] IS NOT NULL OR [EventoGrupoId] = [Id]");
                    table.CheckConstraint("CK_eventos_valores_nao_negativos", "[TransporteValor] >= 0 AND [AlimentacaoValor] >= 0 AND [SeguroObrigatorio] >= 0 AND [ValorPorMembro] >= 0");
                    table.CheckConstraint("CK_eventos_valores_normalizados", "([TransporteEhGratis] = 0 OR [TransporteValor] = 0) AND ([AlimentacaoIndividual] = 0 OR [AlimentacaoValor] = 0) AND [ValorPorMembro] = [TransporteValor] + [AlimentacaoValor] + [SeguroObrigatorio]");
                    table.ForeignKey(
                        name: "FK_eventos_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eventos_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eventos_eventos_EventoReferenciaId",
                        column: x => x.EventoReferenciaId,
                        principalTable: "eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evento_membros",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoGrupoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LancamentoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DesativadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evento_membros", x => x.Id);
                    table.ForeignKey(
                        name: "FK_evento_membros_Lancamentos_LancamentoId",
                        column: x => x.LancamentoId,
                        principalTable: "Lancamentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evento_membros_Usuarios_MembroId",
                        column: x => x.MembroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evento_membros_eventos_EventoId_EventoGrupoId",
                        columns: x => new { x.EventoId, x.EventoGrupoId },
                        principalTable: "eventos",
                        principalColumns: new[] { "Id", "EventoGrupoId" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "eventos_operacoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    UsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    RequestHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    EventoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_eventos_operacoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_eventos_operacoes_Usuarios_UsuarioId",
                        column: x => x.UsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_eventos_operacoes_eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "historico_eventos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoGrupoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EventoReferenciaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataEvento = table.Column<DateOnly>(type: "date", nullable: false),
                    Local = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TransporteEhGratis = table.Column<bool>(type: "bit", nullable: false),
                    TransporteValor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    AlimentacaoIndividual = table.Column<bool>(type: "bit", nullable: false),
                    AlimentacaoValor = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    SeguroObrigatorio = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ValorPorMembro = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    QuantidadeMembros = table.Column<int>(type: "int", nullable: false),
                    Total = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    ParticipantesJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpResponsavel = table.Column<string>(type: "varchar(45)", nullable: false),
                    Motivo = table.Column<string>(type: "nvarchar(255)", maxLength: 255, nullable: false),
                    ExcluidoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_historico_eventos", x => x.Id);
                    table.CheckConstraint("CK_historico_eventos_ParticipantesJson", "ISJSON([ParticipantesJson]) = 1");
                    table.ForeignKey(
                        name: "FK_historico_eventos_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_historico_eventos_eventos_EventoId",
                        column: x => x.EventoId,
                        principalTable: "eventos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lancamentos_EventoId",
                table: "Lancamentos",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_EventoId",
                table: "evento_membros",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_EventoId_EventoGrupoId",
                table: "evento_membros",
                columns: new[] { "EventoId", "EventoGrupoId" });

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_LancamentoId",
                table: "evento_membros",
                column: "LancamentoId");

            migrationBuilder.CreateIndex(
                name: "IX_evento_membros_MembroId",
                table: "evento_membros",
                column: "MembroId");

            migrationBuilder.CreateIndex(
                name: "UX_evento_membros_grupo_membro_ativo",
                table: "evento_membros",
                columns: new[] { "EventoGrupoId", "MembroId" },
                unique: true,
                filter: "[Ativo] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_Ativo_DataEvento",
                table: "eventos",
                columns: new[] { "Ativo", "DataEvento" });

            migrationBuilder.CreateIndex(
                name: "IX_eventos_AtualizadoPorUsuarioId",
                table: "eventos",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_CriadoPorUsuarioId",
                table: "eventos",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_EventoGrupoId",
                table: "eventos",
                column: "EventoGrupoId");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_EventoReferenciaId",
                table: "eventos",
                column: "EventoReferenciaId");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_operacoes_EventoId",
                table: "eventos_operacoes",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_eventos_operacoes_UsuarioId_IdempotencyKey",
                table: "eventos_operacoes",
                columns: new[] { "UsuarioId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_historico_eventos_EventoId",
                table: "historico_eventos",
                column: "EventoId");

            migrationBuilder.CreateIndex(
                name: "IX_historico_eventos_UsuarioResponsavelId",
                table: "historico_eventos",
                column: "UsuarioResponsavelId");

            migrationBuilder.AddForeignKey(
                name: "FK_Lancamentos_eventos_EventoId",
                table: "Lancamentos",
                column: "EventoId",
                principalTable: "eventos",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // O trigger de Lancamentos passa a copiar EventoId e a aceitar Finalidade nula (lançamentos de evento).
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql(LancamentosTrigger(comEventoId: true));
            migrationBuilder.Sql(EventosTrigger);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_eventos_AuditoriaExclusaoLogica];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [TR_Lancamentos_AuditoriaExclusaoLogica];");

            migrationBuilder.DropForeignKey(
                name: "FK_Lancamentos_eventos_EventoId",
                table: "Lancamentos");

            migrationBuilder.DropTable(
                name: "evento_membros");

            migrationBuilder.DropTable(
                name: "eventos_operacoes");

            migrationBuilder.DropTable(
                name: "historico_eventos");

            migrationBuilder.DropTable(
                name: "eventos");

            migrationBuilder.DropIndex(
                name: "IX_Lancamentos_EventoId",
                table: "Lancamentos");

            migrationBuilder.DropColumn(
                name: "EventoId",
                table: "lancamentos_deletados");

            migrationBuilder.DropColumn(
                name: "EventoId",
                table: "Lancamentos");

            migrationBuilder.AlterColumn<string>(
                name: "Finalidade",
                table: "lancamentos_deletados",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Finalidade",
                table: "Lancamentos",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "nvarchar(50)",
                oldMaxLength: 50,
                oldNullable: true);

            migrationBuilder.Sql(LancamentosTrigger(comEventoId: false));
        }

        // Mesmo texto do trigger criado em UnifyLancamentosFlow, acrescido de EventoId quando pedido.
        private static string LancamentosTrigger(bool comEventoId) => $$"""
            CREATE TRIGGER [TR_Lancamentos_AuditoriaExclusaoLogica] ON [Lancamentos] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo nvarchar(255)=TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(255));
              IF @Usuario IS NULL OR @Ip IS NULL OR @Motivo IS NULL THROW 50001, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [lancamentos_deletados] ([Id],[LancamentoId],[UsuarioResponsavelId],[IpResponsavel],[ExcluidoEmUtc],[Motivo],[MembroId],[Finalidade],[Categoria],[TipoFluxo],[Valor],[Vencimento],[Status],[OperacaoId],[DataCriacaoOriginal]{{(comEventoId ? ",[EventoId]" : "")}})
              SELECT NEWID(),d.Id,@Usuario,@Ip,SYSUTCDATETIME(),@Motivo,d.MembroId,d.Finalidade,d.Categoria,d.TipoFluxo,d.Valor,d.Vencimento,d.Status,d.OperacaoId,d.DataCriacao{{(comEventoId ? ",d.EventoId" : "")}}
              FROM deleted d JOIN inserted i ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0;
            END
            """;

        // Auditoria da exclusão lógica de eventos: dispara SOMENTE na transição Ativo 1 -> 0 (várias linhas por
        // vez), exige o SESSION_CONTEXT definido pela aplicação e é a única a escrever em historico_eventos.
        // O snapshot dos participantes/lançamentos é lido no instante da desativação (a aplicação desativa o
        // evento antes dos participantes) e guardado em JSON, sem depender de linhas que continuam mutáveis.
        private const string EventosTrigger = """
            CREATE TRIGGER [TR_eventos_AuditoriaExclusaoLogica] ON [eventos] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted i JOIN deleted d ON i.Id=d.Id WHERE d.Ativo=1 AND i.Ativo=0) RETURN;
              DECLARE @Usuario uniqueidentifier=TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45)=TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo nvarchar(255)=TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS nvarchar(255));
              IF @Usuario IS NULL OR @Ip IS NULL OR @Motivo IS NULL THROW 50011, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [historico_eventos] ([Id],[EventoId],[EventoGrupoId],[EventoReferenciaId],[DataEvento],[Local],[TransporteEhGratis],[TransporteValor],[AlimentacaoIndividual],[AlimentacaoValor],[SeguroObrigatorio],[ValorPorMembro],[QuantidadeMembros],[Total],[ParticipantesJson],[UsuarioResponsavelId],[IpResponsavel],[Motivo],[ExcluidoEmUtc])
              SELECT NEWID(),d.Id,d.EventoGrupoId,d.EventoReferenciaId,d.DataEvento,d.[Local],d.TransporteEhGratis,d.TransporteValor,d.AlimentacaoIndividual,d.AlimentacaoValor,d.SeguroObrigatorio,d.ValorPorMembro,
                     p.Quantidade,CAST(d.ValorPorMembro * p.Quantidade AS decimal(18,2)),p.Json,@Usuario,@Ip,@Motivo,SYSUTCDATETIME()
              FROM deleted d JOIN inserted i ON i.Id=d.Id
              CROSS APPLY (SELECT (SELECT COUNT(*) FROM [evento_membros] em WHERE em.EventoId=d.Id AND em.Ativo=1) AS Quantidade,
                                   ISNULL((SELECT em.MembroId AS membroId, em.LancamentoId AS lancamentoId, l.Valor AS valor,
                                                  CASE l.Status WHEN 0 THEN N'Pendente' WHEN 1 THEN N'Pago' WHEN 2 THEN N'Atrasado' END AS status
                                           FROM [evento_membros] em JOIN [Lancamentos] l ON l.Id=em.LancamentoId
                                           WHERE em.EventoId=d.Id AND em.Ativo=1 ORDER BY em.MembroId FOR JSON PATH), N'[]') AS Json) p
              WHERE d.Ativo=1 AND i.Ativo=0;
            END
            """;
    }
}
