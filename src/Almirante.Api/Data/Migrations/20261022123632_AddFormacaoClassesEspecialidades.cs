using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFormacaoClassesEspecialidades : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "especialidades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nome = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    CodigoOficial = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: true),
                    Area = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: true),
                    NivelDificuldade = table.Column<int>(type: "int", nullable: true),
                    Modalidade = table.Column<int>(type: "int", nullable: true),
                    PreRequisitosTexto = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    ReferenciaDistintivo = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    FonteOficial = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    EdicaoRevisao = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: true),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    MotivoInativacao = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_especialidades", x => x.Id);
                    table.CheckConstraint("CK_especialidades_Inativacao", "[Ativo] = 1 OR [MotivoInativacao] IS NOT NULL");
                    table.CheckConstraint("CK_especialidades_Modalidade", "[Modalidade] IS NULL OR [Modalidade] IN (1, 2)");
                    table.CheckConstraint("CK_especialidades_NivelDificuldade", "[NivelDificuldade] IS NULL OR [NivelDificuldade] BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_especialidades_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_especialidades_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "formacao_historico",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tabela = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RegistroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    VinculoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Acao = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: true),
                    Motivo = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    EstadoAnteriorJson = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    EstadoNovoJson = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IpResponsavel = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: false),
                    OcorridoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_formacao_historico", x => x.Id);
                    table.CheckConstraint("CK_formacao_historico_EstadoAnteriorJson", "[EstadoAnteriorJson] IS NULL OR ISJSON([EstadoAnteriorJson]) = 1");
                    table.CheckConstraint("CK_formacao_historico_EstadoNovoJson", "ISJSON([EstadoNovoJson]) = 1");
                    table.ForeignKey(
                        name: "FK_formacao_historico_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "especialidades_prerequisitos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EspecialidadeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PreRequisitoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_especialidades_prerequisitos", x => x.Id);
                    table.CheckConstraint("CK_especialidades_prerequisitos_Proprio", "[EspecialidadeId] <> [PreRequisitoId]");
                    table.ForeignKey(
                        name: "FK_especialidades_prerequisitos_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_especialidades_prerequisitos_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_especialidades_prerequisitos_especialidades_EspecialidadeId",
                        column: x => x.EspecialidadeId,
                        principalTable: "especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_especialidades_prerequisitos_especialidades_PreRequisitoId",
                        column: x => x.PreRequisitoId,
                        principalTable: "especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "requisitos_revisoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Classe = table.Column<int>(type: "int", nullable: true),
                    EspecialidadeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Edicao = table.Column<string>(type: "varchar(50)", unicode: false, maxLength: 50, nullable: false),
                    FonteOficial = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: false),
                    Observacoes = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: true),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    MotivoDescarte = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PublicadaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    PublicadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requisitos_revisoes", x => x.Id);
                    table.CheckConstraint("CK_requisitos_revisoes_Alvo", "([Classe] IS NOT NULL AND [EspecialidadeId] IS NULL) OR ([Classe] IS NULL AND [EspecialidadeId] IS NOT NULL)");
                    table.CheckConstraint("CK_requisitos_revisoes_Classe", "[Classe] IS NULL OR [Classe] IN (1, 2, 3, 4, 5, 6, 11, 12, 13, 14, 15, 16, 21, 22, 23)");
                    table.CheckConstraint("CK_requisitos_revisoes_Descarte", "[Situacao] <> 4 OR [MotivoDescarte] IS NOT NULL");
                    table.CheckConstraint("CK_requisitos_revisoes_Publicacao", "([Situacao] IN (1, 4) AND [PublicadaPorUsuarioId] IS NULL AND [PublicadaEmUtc] IS NULL) OR ([Situacao] IN (2, 3) AND [PublicadaPorUsuarioId] IS NOT NULL AND [PublicadaEmUtc] IS NOT NULL)");
                    table.CheckConstraint("CK_requisitos_revisoes_Situacao", "[Situacao] IN (1, 2, 3, 4)");
                    table.ForeignKey(
                        name: "FK_requisitos_revisoes_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requisitos_revisoes_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requisitos_revisoes_Usuarios_PublicadaPorUsuarioId",
                        column: x => x.PublicadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requisitos_revisoes_especialidades_EspecialidadeId",
                        column: x => x.EspecialidadeId,
                        principalTable: "especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membros_classes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Classe = table.Column<int>(type: "int", nullable: false),
                    Modalidade = table.Column<int>(type: "int", nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataInicio = table.Column<DateOnly>(type: "date", nullable: true),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    InstrutorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Historico = table.Column<bool>(type: "bit", nullable: false),
                    OrigemHistorica = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    DataConclusao = table.Column<DateOnly>(type: "date", nullable: true),
                    ConclusaoRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConclusaoRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DataInvestidura = table.Column<DateOnly>(type: "date", nullable: true),
                    InvestiduraRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    InvestiduraRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    MotivoCancelamento = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    CanceladoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CanceladoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membros_classes", x => x.Id);
                    table.CheckConstraint("CK_membros_classes_Cancelamento", "([Situacao] = 3 AND [MotivoCancelamento] IS NOT NULL AND [CanceladoPorUsuarioId] IS NOT NULL AND [CanceladoEmUtc] IS NOT NULL) OR ([Situacao] <> 3 AND [MotivoCancelamento] IS NULL AND [CanceladoPorUsuarioId] IS NULL AND [CanceladoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_membros_classes_Classe", "[Classe] IN (1, 2, 3, 4, 5, 6, 11, 12, 13, 14, 15, 16, 21, 22, 23)");
                    table.CheckConstraint("CK_membros_classes_Conclusao", "([Situacao] = 2 AND [ConclusaoRegistradaPorUsuarioId] IS NOT NULL AND [ConclusaoRegistradaEmUtc] IS NOT NULL AND ([DataConclusao] IS NOT NULL OR [Historico] = 1)) OR ([Situacao] <> 2 AND [DataConclusao] IS NULL AND [ConclusaoRegistradaPorUsuarioId] IS NULL AND [ConclusaoRegistradaEmUtc] IS NULL)");
                    table.CheckConstraint("CK_membros_classes_Cronologia", "([DataConclusao] IS NULL OR [DataInicio] IS NULL OR [DataConclusao] >= [DataInicio]) AND ([DataInvestidura] IS NULL OR [DataConclusao] IS NULL OR [DataInvestidura] >= [DataConclusao]) AND ([DataInvestidura] IS NULL OR [DataInicio] IS NULL OR [DataInvestidura] >= [DataInicio])");
                    table.CheckConstraint("CK_membros_classes_Fluxo", "([Historico] = 0 AND [RevisaoId] IS NOT NULL AND [DataInicio] IS NOT NULL AND [OrigemHistorica] IS NULL) OR ([Historico] = 1 AND [OrigemHistorica] IS NOT NULL)");
                    table.CheckConstraint("CK_membros_classes_Investidura", "([DataInvestidura] IS NULL AND [InvestiduraRegistradaPorUsuarioId] IS NULL AND [InvestiduraRegistradaEmUtc] IS NULL) OR ([Situacao] = 2 AND [InvestiduraRegistradaPorUsuarioId] IS NOT NULL AND [InvestiduraRegistradaEmUtc] IS NOT NULL AND ([DataInvestidura] IS NOT NULL OR [Historico] = 1))");
                    table.CheckConstraint("CK_membros_classes_Modalidade", "[Modalidade] IN (1, 2)");
                    table.CheckConstraint("CK_membros_classes_Situacao", "[Situacao] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_CanceladoPorUsuarioId",
                        column: x => x.CanceladoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_ConclusaoRegistradaPorUsuarioId",
                        column: x => x.ConclusaoRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_InstrutorId",
                        column: x => x.InstrutorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_InvestiduraRegistradaPorUsuarioId",
                        column: x => x.InvestiduraRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_Usuarios_MembroId",
                        column: x => x.MembroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_classes_requisitos_revisoes_RevisaoId",
                        column: x => x.RevisaoId,
                        principalTable: "requisitos_revisoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "membros_especialidades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    EspecialidadeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DataInicio = table.Column<DateOnly>(type: "date", nullable: true),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    InstrutorId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Historico = table.Column<bool>(type: "bit", nullable: false),
                    OrigemHistorica = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    DataConclusao = table.Column<DateOnly>(type: "date", nullable: true),
                    ConclusaoRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ConclusaoRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    DataEntrega = table.Column<DateOnly>(type: "date", nullable: true),
                    EntregaRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EntregaRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    MotivoCancelamento = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    CanceladoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CanceladoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_membros_especialidades", x => x.Id);
                    table.CheckConstraint("CK_membros_especialidades_Cancelamento", "([Situacao] = 3 AND [MotivoCancelamento] IS NOT NULL AND [CanceladoPorUsuarioId] IS NOT NULL AND [CanceladoEmUtc] IS NOT NULL) OR ([Situacao] <> 3 AND [MotivoCancelamento] IS NULL AND [CanceladoPorUsuarioId] IS NULL AND [CanceladoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_membros_especialidades_Conclusao", "([Situacao] = 2 AND [ConclusaoRegistradaPorUsuarioId] IS NOT NULL AND [ConclusaoRegistradaEmUtc] IS NOT NULL AND ([DataConclusao] IS NOT NULL OR [Historico] = 1)) OR ([Situacao] <> 2 AND [DataConclusao] IS NULL AND [ConclusaoRegistradaPorUsuarioId] IS NULL AND [ConclusaoRegistradaEmUtc] IS NULL)");
                    table.CheckConstraint("CK_membros_especialidades_Cronologia", "([DataConclusao] IS NULL OR [DataInicio] IS NULL OR [DataConclusao] >= [DataInicio]) AND ([DataEntrega] IS NULL OR [DataConclusao] IS NULL OR [DataEntrega] >= [DataConclusao]) AND ([DataEntrega] IS NULL OR [DataInicio] IS NULL OR [DataEntrega] >= [DataInicio])");
                    table.CheckConstraint("CK_membros_especialidades_Entrega", "([DataEntrega] IS NULL AND [EntregaRegistradaPorUsuarioId] IS NULL AND [EntregaRegistradaEmUtc] IS NULL) OR ([Situacao] = 2 AND [EntregaRegistradaPorUsuarioId] IS NOT NULL AND [EntregaRegistradaEmUtc] IS NOT NULL AND ([DataEntrega] IS NOT NULL OR [Historico] = 1))");
                    table.CheckConstraint("CK_membros_especialidades_Fluxo", "([Historico] = 0 AND [RevisaoId] IS NOT NULL AND [DataInicio] IS NOT NULL AND [OrigemHistorica] IS NULL) OR ([Historico] = 1 AND [OrigemHistorica] IS NOT NULL)");
                    table.CheckConstraint("CK_membros_especialidades_Situacao", "[Situacao] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_CanceladoPorUsuarioId",
                        column: x => x.CanceladoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_ConclusaoRegistradaPorUsuarioId",
                        column: x => x.ConclusaoRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_EntregaRegistradaPorUsuarioId",
                        column: x => x.EntregaRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_InstrutorId",
                        column: x => x.InstrutorId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_Usuarios_MembroId",
                        column: x => x.MembroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_especialidades_EspecialidadeId",
                        column: x => x.EspecialidadeId,
                        principalTable: "especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_membros_especialidades_requisitos_revisoes_RevisaoId",
                        column: x => x.RevisaoId,
                        principalTable: "requisitos_revisoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "requisitos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RevisaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequisitoPaiId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Codigo = table.Column<string>(type: "varchar(20)", unicode: false, maxLength: 20, nullable: false),
                    Secao = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Ordem = table.Column<int>(type: "int", nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    Obrigatorio = table.Column<bool>(type: "bit", nullable: false),
                    Regra = table.Column<int>(type: "int", nullable: true),
                    QuantidadeMinima = table.Column<int>(type: "int", nullable: true),
                    ReferenciaOficial = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    EspecialidadesExigidas = table.Column<int>(type: "int", nullable: true),
                    EspecialidadeExigidaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AreaEspecialidadeExigida = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    SomenteEspecialidadeNaoRealizada = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_requisitos", x => x.Id);
                    table.CheckConstraint("CK_requisitos_Especialidades", "([EspecialidadesExigidas] IS NULL AND [EspecialidadeExigidaId] IS NULL AND [AreaEspecialidadeExigida] IS NULL AND [SomenteEspecialidadeNaoRealizada] = 0) OR ([Regra] IS NULL AND [EspecialidadesExigidas] BETWEEN 1 AND 50)");
                    table.CheckConstraint("CK_requisitos_Ordem", "[Ordem] >= 1");
                    table.CheckConstraint("CK_requisitos_Regra", "([Regra] IS NULL AND [QuantidadeMinima] IS NULL) OR ([Regra] = 1 AND [QuantidadeMinima] IS NULL) OR ([Regra] = 2 AND [QuantidadeMinima] >= 1)");
                    table.ForeignKey(
                        name: "FK_requisitos_especialidades_EspecialidadeExigidaId",
                        column: x => x.EspecialidadeExigidaId,
                        principalTable: "especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requisitos_requisitos_RequisitoPaiId",
                        column: x => x.RequisitoPaiId,
                        principalTable: "requisitos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_requisitos_requisitos_revisoes_RevisaoId",
                        column: x => x.RevisaoId,
                        principalTable: "requisitos_revisoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "validacoes_externas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroClasseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Codigo = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Confirmada = table.Column<bool>(type: "bit", nullable: false),
                    Data = table.Column<DateOnly>(type: "date", nullable: true),
                    Referencia = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    Observacao = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_validacoes_externas", x => x.Id);
                    table.CheckConstraint("CK_validacoes_externas_Data", "[Confirmada] = 0 OR [Data] IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_validacoes_externas_Usuarios_RegistradaPorUsuarioId",
                        column: x => x.RegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_validacoes_externas_membros_classes_MembroClasseId",
                        column: x => x.MembroClasseId,
                        principalTable: "membros_classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "evidencias_especialidades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroClasseId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RequisitoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroEspecialidadeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RemovidaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RemovidaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_evidencias_especialidades", x => x.Id);
                    table.CheckConstraint("CK_evidencias_especialidades_Remocao", "([Ativo] = 1 AND [RemovidaPorUsuarioId] IS NULL AND [RemovidaEmUtc] IS NULL) OR ([Ativo] = 0 AND [RemovidaPorUsuarioId] IS NOT NULL AND [RemovidaEmUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_evidencias_especialidades_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidencias_especialidades_Usuarios_RemovidaPorUsuarioId",
                        column: x => x.RemovidaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidencias_especialidades_membros_classes_MembroClasseId",
                        column: x => x.MembroClasseId,
                        principalTable: "membros_classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidencias_especialidades_membros_especialidades_MembroEspecialidadeId",
                        column: x => x.MembroEspecialidadeId,
                        principalTable: "membros_especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_evidencias_especialidades_requisitos_RequisitoId",
                        column: x => x.RequisitoId,
                        principalTable: "requisitos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "progresso_requisitos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroClasseId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    MembroEspecialidadeId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RequisitoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    DataCumprimento = table.Column<DateOnly>(type: "date", nullable: true),
                    ValidadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ValidadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Observacao = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    ReferenciaEvidencia = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_progresso_requisitos", x => x.Id);
                    table.CheckConstraint("CK_progresso_requisitos_Situacao", "[Situacao] IN (1, 2, 3)");
                    table.CheckConstraint("CK_progresso_requisitos_Validacao", "([Situacao] = 3 AND [ValidadoPorUsuarioId] IS NOT NULL AND [ValidadoEmUtc] IS NOT NULL) OR ([Situacao] <> 3 AND [ValidadoPorUsuarioId] IS NULL AND [ValidadoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_progresso_requisitos_Vinculo", "([MembroClasseId] IS NOT NULL AND [MembroEspecialidadeId] IS NULL) OR ([MembroClasseId] IS NULL AND [MembroEspecialidadeId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_Usuarios_ValidadoPorUsuarioId",
                        column: x => x.ValidadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_membros_classes_MembroClasseId",
                        column: x => x.MembroClasseId,
                        principalTable: "membros_classes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_membros_especialidades_MembroEspecialidadeId",
                        column: x => x.MembroEspecialidadeId,
                        principalTable: "membros_especialidades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_progresso_requisitos_requisitos_RequisitoId",
                        column: x => x.RequisitoId,
                        principalTable: "requisitos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_Area_Ativo",
                table: "especialidades",
                columns: new[] { "Area", "Ativo" });

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_AtualizadoPorUsuarioId",
                table: "especialidades",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_CriadoPorUsuarioId",
                table: "especialidades",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_especialidades_CodigoOficial",
                table: "especialidades",
                column: "CodigoOficial",
                unique: true,
                filter: "[CodigoOficial] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_especialidades_Nome_Area_Modalidade_ativa",
                table: "especialidades",
                columns: new[] { "Nome", "Area", "Modalidade" },
                unique: true,
                filter: "[Ativo] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_prerequisitos_AtualizadoPorUsuarioId",
                table: "especialidades_prerequisitos",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_prerequisitos_CriadoPorUsuarioId",
                table: "especialidades_prerequisitos",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_prerequisitos_EspecialidadeId_PreRequisitoId",
                table: "especialidades_prerequisitos",
                columns: new[] { "EspecialidadeId", "PreRequisitoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_especialidades_prerequisitos_PreRequisitoId",
                table: "especialidades_prerequisitos",
                column: "PreRequisitoId");

            migrationBuilder.CreateIndex(
                name: "IX_evidencias_especialidades_CriadoPorUsuarioId",
                table: "evidencias_especialidades",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_evidencias_especialidades_MembroEspecialidadeId",
                table: "evidencias_especialidades",
                column: "MembroEspecialidadeId");

            migrationBuilder.CreateIndex(
                name: "IX_evidencias_especialidades_RemovidaPorUsuarioId",
                table: "evidencias_especialidades",
                column: "RemovidaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_evidencias_especialidades_RequisitoId",
                table: "evidencias_especialidades",
                column: "RequisitoId");

            migrationBuilder.CreateIndex(
                name: "UX_evidencias_especialidades_classe_especialidade_ativa",
                table: "evidencias_especialidades",
                columns: new[] { "MembroClasseId", "MembroEspecialidadeId" },
                unique: true,
                filter: "[Ativo] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_formacao_historico_Tabela_RegistroId",
                table: "formacao_historico",
                columns: new[] { "Tabela", "RegistroId" });

            migrationBuilder.CreateIndex(
                name: "IX_formacao_historico_UsuarioResponsavelId",
                table: "formacao_historico",
                column: "UsuarioResponsavelId");

            migrationBuilder.CreateIndex(
                name: "IX_formacao_historico_VinculoId_OcorridoEmUtc",
                table: "formacao_historico",
                columns: new[] { "VinculoId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_AtualizadoPorUsuarioId",
                table: "membros_classes",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_CanceladoPorUsuarioId",
                table: "membros_classes",
                column: "CanceladoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_Classe_Situacao",
                table: "membros_classes",
                columns: new[] { "Classe", "Situacao" });

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_ConclusaoRegistradaPorUsuarioId",
                table: "membros_classes",
                column: "ConclusaoRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_CriadoPorUsuarioId",
                table: "membros_classes",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_InstrutorId",
                table: "membros_classes",
                column: "InstrutorId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_InvestiduraRegistradaPorUsuarioId",
                table: "membros_classes",
                column: "InvestiduraRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_MembroId",
                table: "membros_classes",
                column: "MembroId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_classes_RevisaoId",
                table: "membros_classes",
                column: "RevisaoId");

            migrationBuilder.CreateIndex(
                name: "UX_membros_classes_membro_classe_ativa",
                table: "membros_classes",
                columns: new[] { "MembroId", "Classe" },
                unique: true,
                filter: "[Situacao] <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_AtualizadoPorUsuarioId",
                table: "membros_especialidades",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_CanceladoPorUsuarioId",
                table: "membros_especialidades",
                column: "CanceladoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_ConclusaoRegistradaPorUsuarioId",
                table: "membros_especialidades",
                column: "ConclusaoRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_CriadoPorUsuarioId",
                table: "membros_especialidades",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_EntregaRegistradaPorUsuarioId",
                table: "membros_especialidades",
                column: "EntregaRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_EspecialidadeId_Situacao",
                table: "membros_especialidades",
                columns: new[] { "EspecialidadeId", "Situacao" });

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_InstrutorId",
                table: "membros_especialidades",
                column: "InstrutorId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_MembroId",
                table: "membros_especialidades",
                column: "MembroId");

            migrationBuilder.CreateIndex(
                name: "IX_membros_especialidades_RevisaoId",
                table: "membros_especialidades",
                column: "RevisaoId");

            migrationBuilder.CreateIndex(
                name: "UX_membros_especialidades_membro_especialidade_ativa",
                table: "membros_especialidades",
                columns: new[] { "MembroId", "EspecialidadeId" },
                unique: true,
                filter: "[Situacao] <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_AtualizadoPorUsuarioId",
                table: "progresso_requisitos",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_CriadoPorUsuarioId",
                table: "progresso_requisitos",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_MembroClasseId_RequisitoId",
                table: "progresso_requisitos",
                columns: new[] { "MembroClasseId", "RequisitoId" },
                unique: true,
                filter: "[MembroClasseId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_MembroEspecialidadeId_RequisitoId",
                table: "progresso_requisitos",
                columns: new[] { "MembroEspecialidadeId", "RequisitoId" },
                unique: true,
                filter: "[MembroEspecialidadeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_RequisitoId",
                table: "progresso_requisitos",
                column: "RequisitoId");

            migrationBuilder.CreateIndex(
                name: "IX_progresso_requisitos_ValidadoPorUsuarioId",
                table: "progresso_requisitos",
                column: "ValidadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_EspecialidadeExigidaId",
                table: "requisitos",
                column: "EspecialidadeExigidaId");

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_RequisitoPaiId",
                table: "requisitos",
                column: "RequisitoPaiId");

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_RevisaoId_Codigo",
                table: "requisitos",
                columns: new[] { "RevisaoId", "Codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_revisoes_AtualizadoPorUsuarioId",
                table: "requisitos_revisoes",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_revisoes_Classe_Situacao",
                table: "requisitos_revisoes",
                columns: new[] { "Classe", "Situacao" });

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_revisoes_CriadoPorUsuarioId",
                table: "requisitos_revisoes",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_revisoes_Especialidade_Situacao",
                table: "requisitos_revisoes",
                columns: new[] { "EspecialidadeId", "Situacao" });

            migrationBuilder.CreateIndex(
                name: "IX_requisitos_revisoes_PublicadaPorUsuarioId",
                table: "requisitos_revisoes",
                column: "PublicadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_requisitos_revisoes_Classe_publicada",
                table: "requisitos_revisoes",
                column: "Classe",
                unique: true,
                filter: "[Situacao] = 2 AND [Classe] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_requisitos_revisoes_Especialidade_publicada",
                table: "requisitos_revisoes",
                column: "EspecialidadeId",
                unique: true,
                filter: "[Situacao] = 2 AND [EspecialidadeId] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_validacoes_externas_MembroClasseId_Codigo",
                table: "validacoes_externas",
                columns: new[] { "MembroClasseId", "Codigo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_validacoes_externas_RegistradaPorUsuarioId",
                table: "validacoes_externas",
                column: "RegistradaPorUsuarioId");

            foreach (var (tabela, vinculo) in TabelasHistorico)
            {
                migrationBuilder.Sql(TriggerHistorico(tabela, vinculo));
            }

            migrationBuilder.Sql(TriggerRequisitosImutaveis);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_requisitos_Imutaveis];");
            foreach (var (tabela, _) in TabelasHistorico)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [dbo].[TR_{tabela}_Historico];");
            }

            migrationBuilder.DropTable(
                name: "especialidades_prerequisitos");

            migrationBuilder.DropTable(
                name: "evidencias_especialidades");

            migrationBuilder.DropTable(
                name: "formacao_historico");

            migrationBuilder.DropTable(
                name: "progresso_requisitos");

            migrationBuilder.DropTable(
                name: "validacoes_externas");

            migrationBuilder.DropTable(
                name: "membros_especialidades");

            migrationBuilder.DropTable(
                name: "requisitos");

            migrationBuilder.DropTable(
                name: "membros_classes");

            migrationBuilder.DropTable(
                name: "requisitos_revisoes");

            migrationBuilder.DropTable(
                name: "especialidades");
        }

        /// <summary>Tabelas auditadas e a expressão (sobre inserted i) do vínculo a que cada linha pertence; fixas nesta migration.</summary>
        private static readonly (string Tabela, string Vinculo)[] TabelasHistorico =
        [
            ("especialidades", "NULL"),
            ("especialidades_prerequisitos", "NULL"),
            ("requisitos_revisoes", "NULL"),
            ("membros_classes", "i.[Id]"),
            ("membros_especialidades", "i.[Id]"),
            ("progresso_requisitos", "COALESCE(i.[MembroClasseId], i.[MembroEspecialidadeId])"),
            ("evidencias_especialidades", "i.[MembroClasseId]"),
            ("validacoes_externas", "i.[MembroClasseId]"),
        ];

        /// <summary>
        /// Grava em formacao_historico cada linha incluída ou alterada: estado anterior (null na inclusão) e novo, em JSON com todas as
        /// colunas, com responsável, IP, ação e justificativa do SESSION_CONTEXT. Sem contexto, a escrita é recusada (toda escrita passa pela
        /// API auditada). formacao_historico é tabela de auditoria (runtime só com SELECT); o trigger grava por encadeamento de propriedade.
        /// </summary>
        private static string TriggerHistorico(string tabela, string vinculo) => $"""
            CREATE TRIGGER [dbo].[TR_{tabela}_Historico] ON [dbo].[{tabela}] AFTER INSERT, UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
              DECLARE @Usuario uniqueidentifier = TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45) = TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo varchar(255) = NULLIF(TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS varchar(255)), '');
              DECLARE @Acao varchar(40) = TRY_CAST(SESSION_CONTEXT(N'AcaoAuditoria') AS varchar(40));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50125, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [dbo].[formacao_historico]
                ([Id],[Tabela],[RegistroId],[VinculoId],[Acao],[Motivo],[EstadoAnteriorJson],[EstadoNovoJson],[UsuarioResponsavelId],[IpResponsavel],[OcorridoEmUtc])
              SELECT NEWID(), '{tabela}', i.[Id], {vinculo}, @Acao, @Motivo,
                     CAST((SELECT x.* FROM deleted x WHERE x.[Id] = i.[Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS varchar(max)),
                     CAST((SELECT y.* FROM inserted y WHERE y.[Id] = i.[Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS varchar(max)),
                     @Usuario, @Ip, SYSUTCDATETIME()
              FROM inserted i;
            END
            """;

        /// <summary>Requisitos nunca são alterados: uma mudança oficial gera nova revisão, sem sobrescrever o conjunto já usado.</summary>
        private const string TriggerRequisitosImutaveis = """
            CREATE TRIGGER [dbo].[TR_requisitos_Imutaveis] ON [dbo].[requisitos] AFTER UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF EXISTS (SELECT 1 FROM deleted) THROW 50126, 'Requisitos são imutáveis: cadastre uma nova revisão.', 1;
            END
            """;
    }
}