using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Issue #158. auditoria_http: uma linha por chamada HTTP que chegou à API, já sanitizada (sem login, credenciais nem dados
    /// pessoais). Só inserção: o trigger TR_auditoria_http_Imutavel recusa UPDATE e DELETE, e a role de runtime recebe só SELECT e INSERT
    /// (DbCredentialManager). Chave primária não clusterizada (o Id é gerado na captura e deduplica as novas tentativas); a chave
    /// clusterizada é a data, para a inserção sempre no fim. Tabela nova e vazia: nenhuma linha existente muda, e a API anterior a
    /// ignora. Down remove a tabela com os registros.
    /// </summary>
    public partial class AddAuditoriaHttp : Migration
    {
        private const string TriggerImutavel = $"""
            CREATE TRIGGER [dbo].[{AlmiranteDbContext.TriggerAuditoriaHttpImutavel}] ON [dbo].[{AlmiranteDbContext.TabelaAuditoriaHttp}]
            INSTEAD OF UPDATE, DELETE AS
            BEGIN
              SET NOCOUNT ON;
              THROW 50158, 'auditoria_http aceita somente insercao: a auditoria HTTP nao pode ser alterada nem excluida.', 1;
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auditoria_http",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Url = table.Column<string>(type: "varchar(2048)", unicode: false, maxLength: 2048, nullable: false),
                    MetodoHttp = table.Column<string>(type: "varchar(16)", unicode: false, maxLength: 16, nullable: false),
                    Request = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    Response = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    StatusCode = table.Column<int>(type: "int", nullable: true),
                    Headers = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    Exception = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    DataRequestUtc = table.Column<DateTimeOffset>(type: "datetimeoffset(7)", nullable: false),
                    DuracaoMs = table.Column<int>(type: "int", nullable: false),
                    IpUsuario = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: false),
                    IdentificacaoUsuario = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: false),
                    TraceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    EstadoCapturaRequest = table.Column<int>(type: "int", nullable: false),
                    EstadoCapturaResponse = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auditoria_http", x => x.Id)
                        .Annotation("SqlServer:Clustered", false);
                    table.CheckConstraint("CK_auditoria_http_DuracaoMs", "[DuracaoMs] >= 0");
                    table.CheckConstraint("CK_auditoria_http_Estados", "[EstadoCapturaRequest] IN (1, 2, 3, 4, 5, 6, 7, 8, 9) AND [EstadoCapturaResponse] IN (1, 2, 3, 4, 5, 6, 7, 8, 9)");
                    table.CheckConstraint("CK_auditoria_http_Json", "ISJSON([Request]) = 1 AND ISJSON([Headers]) = 1 AND ([Response] IS NULL OR ISJSON([Response]) = 1) AND ([Exception] IS NULL OR ISJSON([Exception]) = 1)");
                    table.CheckConstraint("CK_auditoria_http_StatusCode", "[StatusCode] IS NULL OR [StatusCode] BETWEEN 100 AND 999");
                    table.CheckConstraint("CK_auditoria_http_Usuario", "[IdentificacaoUsuario] = 'ANONIMO' OR TRY_CONVERT(uniqueidentifier, [IdentificacaoUsuario]) IS NOT NULL");
                });

            migrationBuilder.CreateIndex(
                name: "CIX_auditoria_http_data",
                table: "auditoria_http",
                column: "DataRequestUtc")
                .Annotation("SqlServer:Clustered", true);

            migrationBuilder.CreateIndex(
                name: "IX_auditoria_http_status_data",
                table: "auditoria_http",
                columns: new[] { "StatusCode", "DataRequestUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_auditoria_http_trace",
                table: "auditoria_http",
                column: "TraceId");

            migrationBuilder.CreateIndex(
                name: "IX_auditoria_http_usuario_data",
                table: "auditoria_http",
                columns: new[] { "IdentificacaoUsuario", "DataRequestUtc" });

            migrationBuilder.Sql(TriggerImutavel);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auditoria_http");
        }
    }
}
