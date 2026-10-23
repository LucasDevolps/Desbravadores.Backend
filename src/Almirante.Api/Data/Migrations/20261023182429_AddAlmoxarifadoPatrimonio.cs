using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Almirante.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAlmoxarifadoPatrimonio : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "almoxarifado_desejos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Nome = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    Justificativa = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: false),
                    TipoItem = table.Column<int>(type: "int", nullable: false),
                    QuantidadeDesejada = table.Column<int>(type: "int", nullable: false),
                    QuantidadeAtendida = table.Column<int>(type: "int", nullable: false),
                    Urgencia = table.Column<int>(type: "int", nullable: false),
                    Categoria = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    CustoUnitarioEstimado = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DataNecessidade = table.Column<DateOnly>(type: "date", nullable: true),
                    LinkReferencia = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: true),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    MotivoCancelamento = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    CanceladoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CanceladoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Excluido = table.Column<bool>(type: "bit", nullable: false),
                    MotivoExclusao = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    ExcluidoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExcluidoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_almoxarifado_desejos", x => x.Id);
                    table.CheckConstraint("CK_almoxarifado_desejos_Custo", "[CustoUnitarioEstimado] IS NULL OR [CustoUnitarioEstimado] >= 0");
                    table.CheckConstraint("CK_almoxarifado_desejos_Exclusao", "([Excluido] = 1 AND [MotivoExclusao] IS NOT NULL AND [ExcluidoPorUsuarioId] IS NOT NULL AND [ExcluidoEmUtc] IS NOT NULL) OR ([Excluido] = 0 AND [MotivoExclusao] IS NULL AND [ExcluidoPorUsuarioId] IS NULL AND [ExcluidoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_almoxarifado_desejos_Quantidades", "[QuantidadeDesejada] BETWEEN 1 AND 10000 AND [QuantidadeAtendida] BETWEEN 0 AND [QuantidadeDesejada]");
                    table.CheckConstraint("CK_almoxarifado_desejos_Situacao", "([CanceladoEmUtc] IS NOT NULL AND [Situacao] = 4 AND [MotivoCancelamento] IS NOT NULL AND [CanceladoPorUsuarioId] IS NOT NULL) OR ([CanceladoEmUtc] IS NULL AND [MotivoCancelamento] IS NULL AND [CanceladoPorUsuarioId] IS NULL AND [Situacao] = CASE WHEN [QuantidadeAtendida] = 0 THEN 1 WHEN [QuantidadeAtendida] < [QuantidadeDesejada] THEN 2 ELSE 3 END)");
                    table.CheckConstraint("CK_almoxarifado_desejos_TipoItem", "[TipoItem] IN (1, 2)");
                    table.CheckConstraint("CK_almoxarifado_desejos_Urgencia", "[Urgencia] IN (1, 2, 3)");
                    table.ForeignKey(
                        name: "FK_almoxarifado_desejos_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_desejos_Usuarios_CanceladoPorUsuarioId",
                        column: x => x.CanceladoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_desejos_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_desejos_Usuarios_ExcluidoPorUsuarioId",
                        column: x => x.ExcluidoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "almoxarifado_historico",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tabela = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    RegistroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BemId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DesejoId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
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
                    table.PrimaryKey("PK_almoxarifado_historico", x => x.Id);
                    table.CheckConstraint("CK_almoxarifado_historico_EstadoAnteriorJson", "[EstadoAnteriorJson] IS NULL OR ISJSON([EstadoAnteriorJson]) = 1");
                    table.CheckConstraint("CK_almoxarifado_historico_EstadoNovoJson", "ISJSON([EstadoNovoJson]) = 1");
                    table.ForeignKey(
                        name: "FK_almoxarifado_historico_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "almoxarifado_responsaveis",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataInicio = table.Column<DateOnly>(type: "date", nullable: false),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    DesignadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DesignadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MotivoRevogacao = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    RevogadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevogadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_almoxarifado_responsaveis", x => x.Id);
                    table.CheckConstraint("CK_almoxarifado_responsaveis_Revogacao", "([RevogadoEmUtc] IS NULL AND [MotivoRevogacao] IS NULL AND [RevogadoPorUsuarioId] IS NULL) OR ([RevogadoEmUtc] IS NOT NULL AND [MotivoRevogacao] IS NOT NULL AND [RevogadoPorUsuarioId] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_almoxarifado_responsaveis_Usuarios_DesignadoPorUsuarioId",
                        column: x => x.DesignadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_responsaveis_Usuarios_MembroId",
                        column: x => x.MembroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_responsaveis_Usuarios_RevogadoPorUsuarioId",
                        column: x => x.RevogadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patrimonio_bens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NumeroTombo = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Tombo = table.Column<string>(type: "varchar(14)", unicode: false, nullable: false, computedColumnSql: "CONVERT(varchar(14), 'PAT-' + CASE WHEN [NumeroTombo] < 1000000 THEN RIGHT('00000' + CONVERT(varchar(10), [NumeroTombo]), 6) ELSE CONVERT(varchar(10), [NumeroTombo]) END)", stored: true),
                    Nome = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    Categoria = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    EstadoConservacao = table.Column<int>(type: "int", nullable: false),
                    PossuiValorComercial = table.Column<bool>(type: "bit", nullable: false),
                    ValorEstimado = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    DataAquisicao = table.Column<DateOnly>(type: "date", nullable: true),
                    DataAquisicaoDesconhecida = table.Column<bool>(type: "bit", nullable: false),
                    JustificativaDataDesconhecida = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    Origem = table.Column<int>(type: "int", nullable: false),
                    OrigemDetalhe = table.Column<string>(type: "varchar(300)", unicode: false, maxLength: 300, nullable: true),
                    Localizacao = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    ResponsavelGuardaId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Marca = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    Modelo = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    NumeroSerie = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    ValorAquisicao = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    DataBaixa = table.Column<DateOnly>(type: "date", nullable: true),
                    MotivoBaixa = table.Column<int>(type: "int", nullable: true),
                    JustificativaBaixa = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    BaixadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    BaixadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Excluido = table.Column<bool>(type: "bit", nullable: false),
                    MotivoExclusao = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    ExcluidoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExcluidoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CriadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    AtualizadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    AtualizadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrimonio_bens", x => x.Id);
                    table.CheckConstraint("CK_patrimonio_bens_Baixa", "([Situacao] = 4 AND [DataBaixa] IS NOT NULL AND [MotivoBaixa] IN (1, 2, 3, 4, 5) AND [JustificativaBaixa] IS NOT NULL AND [BaixadoPorUsuarioId] IS NOT NULL AND [BaixadoEmUtc] IS NOT NULL) OR ([Situacao] <> 4 AND [DataBaixa] IS NULL AND [MotivoBaixa] IS NULL AND [JustificativaBaixa] IS NULL AND [BaixadoPorUsuarioId] IS NULL AND [BaixadoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_patrimonio_bens_Conservacao", "[EstadoConservacao] IN (1, 2, 3, 4, 5)");
                    table.CheckConstraint("CK_patrimonio_bens_DataAquisicao", "([DataAquisicaoDesconhecida] = 0 AND [DataAquisicao] IS NOT NULL AND [JustificativaDataDesconhecida] IS NULL) OR ([DataAquisicaoDesconhecida] = 1 AND [DataAquisicao] IS NULL AND [JustificativaDataDesconhecida] IS NOT NULL)");
                    table.CheckConstraint("CK_patrimonio_bens_Exclusao", "([Excluido] = 1 AND [MotivoExclusao] IS NOT NULL AND [ExcluidoPorUsuarioId] IS NOT NULL AND [ExcluidoEmUtc] IS NOT NULL) OR ([Excluido] = 0 AND [MotivoExclusao] IS NULL AND [ExcluidoPorUsuarioId] IS NULL AND [ExcluidoEmUtc] IS NULL)");
                    table.CheckConstraint("CK_patrimonio_bens_Origem", "[Origem] IN (1, 2, 3) AND ([Origem] <> 3 OR [OrigemDetalhe] IS NOT NULL)");
                    table.CheckConstraint("CK_patrimonio_bens_Situacao", "[Situacao] IN (1, 2, 3, 4)");
                    table.CheckConstraint("CK_patrimonio_bens_ValorAquisicao", "[ValorAquisicao] IS NULL OR [ValorAquisicao] >= 0");
                    table.CheckConstraint("CK_patrimonio_bens_ValorEstimado", "([PossuiValorComercial] = 1 AND [ValorEstimado] IS NOT NULL AND [ValorEstimado] >= 0) OR ([PossuiValorComercial] = 0 AND [ValorEstimado] IS NULL)");
                    table.ForeignKey(
                        name: "FK_patrimonio_bens_Usuarios_AtualizadoPorUsuarioId",
                        column: x => x.AtualizadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_bens_Usuarios_BaixadoPorUsuarioId",
                        column: x => x.BaixadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_bens_Usuarios_CriadoPorUsuarioId",
                        column: x => x.CriadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_bens_Usuarios_ExcluidoPorUsuarioId",
                        column: x => x.ExcluidoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_bens_Usuarios_ResponsavelGuardaId",
                        column: x => x.ResponsavelGuardaId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patrimonio_inventarios",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataInventario = table.Column<DateOnly>(type: "date", nullable: false),
                    ResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    TotalBens = table.Column<int>(type: "int", nullable: false),
                    TotalBensComValorComercial = table.Column<int>(type: "int", nullable: false),
                    ValorEstimadoPatrimonial = table.Column<decimal>(type: "decimal(18,2)", nullable: false),
                    FechadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    FechadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    DataVotacao = table.Column<DateOnly>(type: "date", nullable: true),
                    ReferenciaAta = table.Column<string>(type: "varchar(100)", unicode: false, maxLength: 100, nullable: true),
                    VotacaoRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    VotacaoRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    Versao = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrimonio_inventarios", x => x.Id);
                    table.CheckConstraint("CK_patrimonio_inventarios_Totais", "[TotalBens] >= 0 AND [TotalBensComValorComercial] BETWEEN 0 AND [TotalBens] AND [ValorEstimadoPatrimonial] >= 0");
                    table.CheckConstraint("CK_patrimonio_inventarios_Votacao", "([DataVotacao] IS NULL AND [ReferenciaAta] IS NULL AND [VotacaoRegistradaPorUsuarioId] IS NULL AND [VotacaoRegistradaEmUtc] IS NULL) OR ([DataVotacao] >= [DataInventario] AND [ReferenciaAta] IS NOT NULL AND [VotacaoRegistradaPorUsuarioId] IS NOT NULL AND [VotacaoRegistradaEmUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_patrimonio_inventarios_Usuarios_FechadoPorUsuarioId",
                        column: x => x.FechadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_inventarios_Usuarios_ResponsavelId",
                        column: x => x.ResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_inventarios_Usuarios_VotacaoRegistradaPorUsuarioId",
                        column: x => x.VotacaoRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "almoxarifado_atendimentos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DesejoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Quantidade = table.Column<int>(type: "int", nullable: false),
                    Data = table.Column<DateOnly>(type: "date", nullable: false),
                    ResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Origem = table.Column<int>(type: "int", nullable: false),
                    Observacoes = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    IdempotencyKey = table.Column<string>(type: "varchar(36)", unicode: false, maxLength: 36, nullable: false),
                    RequestHash = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RegistradoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistradoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    MotivoEstorno = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: true),
                    EstornadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EstornadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_almoxarifado_atendimentos", x => x.Id);
                    table.CheckConstraint("CK_almoxarifado_atendimentos_Estorno", "([EstornadoEmUtc] IS NULL AND [MotivoEstorno] IS NULL AND [EstornadoPorUsuarioId] IS NULL) OR ([EstornadoEmUtc] IS NOT NULL AND [MotivoEstorno] IS NOT NULL AND [EstornadoPorUsuarioId] IS NOT NULL)");
                    table.CheckConstraint("CK_almoxarifado_atendimentos_Origem", "[Origem] IN (1, 2)");
                    table.CheckConstraint("CK_almoxarifado_atendimentos_Quantidade", "[Quantidade] BETWEEN 1 AND 10000");
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_Usuarios_EstornadoPorUsuarioId",
                        column: x => x.EstornadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_Usuarios_RegistradoPorUsuarioId",
                        column: x => x.RegistradoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_Usuarios_ResponsavelId",
                        column: x => x.ResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_almoxarifado_desejos_DesejoId",
                        column: x => x.DesejoId,
                        principalTable: "almoxarifado_desejos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patrimonio_emprestimos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MembroId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataRetirada = table.Column<DateOnly>(type: "date", nullable: false),
                    PrevisaoDevolucao = table.Column<DateOnly>(type: "date", nullable: false),
                    ConservacaoSaida = table.Column<int>(type: "int", nullable: false),
                    ObservacoesSaida = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    Encerramento = table.Column<int>(type: "int", nullable: true),
                    DataEncerramento = table.Column<DateOnly>(type: "date", nullable: true),
                    ConservacaoRetorno = table.Column<int>(type: "int", nullable: true),
                    ObservacoesRetorno = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    RegistradoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistradoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    EncerramentoRegistradoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    EncerramentoRegistradoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrimonio_emprestimos", x => x.Id);
                    table.CheckConstraint("CK_patrimonio_emprestimos_ConservacaoSaida", "[ConservacaoSaida] IN (1, 2, 3, 4, 5) AND [ConservacaoSaida] <> 5");
                    table.CheckConstraint("CK_patrimonio_emprestimos_Encerramento", "([Encerramento] IS NULL AND [DataEncerramento] IS NULL AND [ConservacaoRetorno] IS NULL AND [ObservacoesRetorno] IS NULL AND [EncerramentoRegistradoPorUsuarioId] IS NULL AND [EncerramentoRegistradoEmUtc] IS NULL) OR ([Encerramento] IN (1, 2, 3) AND [DataEncerramento] >= [DataRetirada] AND [EncerramentoRegistradoPorUsuarioId] IS NOT NULL AND [EncerramentoRegistradoEmUtc] IS NOT NULL AND ([Encerramento] <> 1 OR [ConservacaoRetorno] IN (1, 2, 3, 4, 5)))");
                    table.CheckConstraint("CK_patrimonio_emprestimos_Previsao", "[PrevisaoDevolucao] >= [DataRetirada]");
                    table.ForeignKey(
                        name: "FK_patrimonio_emprestimos_Usuarios_EncerramentoRegistradoPorUsuarioId",
                        column: x => x.EncerramentoRegistradoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_emprestimos_Usuarios_MembroId",
                        column: x => x.MembroId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_emprestimos_Usuarios_RegistradoPorUsuarioId",
                        column: x => x.RegistradoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_emprestimos_patrimonio_bens_BemId",
                        column: x => x.BemId,
                        principalTable: "patrimonio_bens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patrimonio_manutencoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DataEntrada = table.Column<DateOnly>(type: "date", nullable: false),
                    Motivo = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    ObservacoesEntrada = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    DataSaida = table.Column<DateOnly>(type: "date", nullable: true),
                    ConservacaoSaida = table.Column<int>(type: "int", nullable: true),
                    ObservacoesSaida = table.Column<string>(type: "varchar(1000)", unicode: false, maxLength: 1000, nullable: true),
                    RegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SaidaRegistradaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SaidaRegistradaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrimonio_manutencoes", x => x.Id);
                    table.CheckConstraint("CK_patrimonio_manutencoes_Saida", "([DataSaida] IS NULL AND [ConservacaoSaida] IS NULL AND [ObservacoesSaida] IS NULL AND [SaidaRegistradaPorUsuarioId] IS NULL AND [SaidaRegistradaEmUtc] IS NULL) OR ([DataSaida] >= [DataEntrada] AND [ConservacaoSaida] IN (1, 2, 3, 4, 5) AND [SaidaRegistradaPorUsuarioId] IS NOT NULL AND [SaidaRegistradaEmUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_patrimonio_manutencoes_Usuarios_RegistradaPorUsuarioId",
                        column: x => x.RegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_manutencoes_Usuarios_SaidaRegistradaPorUsuarioId",
                        column: x => x.SaidaRegistradaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_manutencoes_patrimonio_bens_BemId",
                        column: x => x.BemId,
                        principalTable: "patrimonio_bens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "patrimonio_inventario_itens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    InventarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Tombo = table.Column<string>(type: "varchar(14)", unicode: false, maxLength: 14, nullable: false),
                    Nome = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(2000)", unicode: false, maxLength: 2000, nullable: false),
                    Categoria = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: true),
                    EstadoConservacao = table.Column<int>(type: "int", nullable: false),
                    Situacao = table.Column<int>(type: "int", nullable: false),
                    Localizacao = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    Origem = table.Column<int>(type: "int", nullable: false),
                    DataAquisicao = table.Column<DateOnly>(type: "date", nullable: true),
                    DataAquisicaoDesconhecida = table.Column<bool>(type: "bit", nullable: false),
                    ValorAquisicao = table.Column<decimal>(type: "decimal(18,2)", nullable: true),
                    PossuiValorComercial = table.Column<bool>(type: "bit", nullable: false),
                    ValorEstimado = table.Column<decimal>(type: "decimal(18,2)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patrimonio_inventario_itens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_patrimonio_inventario_itens_patrimonio_bens_BemId",
                        column: x => x.BemId,
                        principalTable: "patrimonio_bens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_patrimonio_inventario_itens_patrimonio_inventarios_InventarioId",
                        column: x => x.InventarioId,
                        principalTable: "patrimonio_inventarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "almoxarifado_atendimentos_bens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AtendimentoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BemId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CriadoPeloAtendimento = table.Column<bool>(type: "bit", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    DesvinculadoPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DesvinculadoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_almoxarifado_atendimentos_bens", x => x.Id);
                    table.CheckConstraint("CK_almoxarifado_atendimentos_bens_Vinculo", "([Ativo] = 1 AND [DesvinculadoPorUsuarioId] IS NULL AND [DesvinculadoEmUtc] IS NULL) OR ([Ativo] = 0 AND [DesvinculadoPorUsuarioId] IS NOT NULL AND [DesvinculadoEmUtc] IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_bens_Usuarios_DesvinculadoPorUsuarioId",
                        column: x => x.DesvinculadoPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_bens_almoxarifado_atendimentos_AtendimentoId",
                        column: x => x.AtendimentoId,
                        principalTable: "almoxarifado_atendimentos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_almoxarifado_atendimentos_bens_patrimonio_bens_BemId",
                        column: x => x.BemId,
                        principalTable: "patrimonio_bens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_atendimentos_DesejoId_Data",
                table: "almoxarifado_atendimentos",
                columns: new[] { "DesejoId", "Data" });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_atendimentos_EstornadoPorUsuarioId",
                table: "almoxarifado_atendimentos",
                column: "EstornadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_atendimentos_ResponsavelId",
                table: "almoxarifado_atendimentos",
                column: "ResponsavelId");

            migrationBuilder.CreateIndex(
                name: "UX_almoxarifado_atendimentos_idempotencia",
                table: "almoxarifado_atendimentos",
                columns: new[] { "RegistradoPorUsuarioId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_atendimentos_bens_AtendimentoId",
                table: "almoxarifado_atendimentos_bens",
                column: "AtendimentoId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_atendimentos_bens_DesvinculadoPorUsuarioId",
                table: "almoxarifado_atendimentos_bens",
                column: "DesvinculadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_almoxarifado_atendimentos_bens_bem_ativo",
                table: "almoxarifado_atendimentos_bens",
                column: "BemId",
                unique: true,
                filter: "[Ativo] = 1");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_AtualizadoPorUsuarioId",
                table: "almoxarifado_desejos",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_CanceladoPorUsuarioId",
                table: "almoxarifado_desejos",
                column: "CanceladoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_Categoria",
                table: "almoxarifado_desejos",
                column: "Categoria");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_CriadoPorUsuarioId",
                table: "almoxarifado_desejos",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_ExcluidoPorUsuarioId",
                table: "almoxarifado_desejos",
                column: "ExcluidoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_desejos_ordem_padrao",
                table: "almoxarifado_desejos",
                columns: new[] { "Excluido", "Situacao", "Urgencia", "CriadoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_historico_BemId_OcorridoEmUtc",
                table: "almoxarifado_historico",
                columns: new[] { "BemId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_historico_DesejoId_OcorridoEmUtc",
                table: "almoxarifado_historico",
                columns: new[] { "DesejoId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_historico_Tabela_RegistroId",
                table: "almoxarifado_historico",
                columns: new[] { "Tabela", "RegistroId" });

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_historico_UsuarioResponsavelId",
                table: "almoxarifado_historico",
                column: "UsuarioResponsavelId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_responsaveis_DesignadoPorUsuarioId",
                table: "almoxarifado_responsaveis",
                column: "DesignadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_almoxarifado_responsaveis_RevogadoPorUsuarioId",
                table: "almoxarifado_responsaveis",
                column: "RevogadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_almoxarifado_responsaveis_membro_vigente",
                table: "almoxarifado_responsaveis",
                column: "MembroId",
                unique: true,
                filter: "[RevogadoEmUtc] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_AtualizadoPorUsuarioId",
                table: "patrimonio_bens",
                column: "AtualizadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_BaixadoPorUsuarioId",
                table: "patrimonio_bens",
                column: "BaixadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_Categoria",
                table: "patrimonio_bens",
                column: "Categoria");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_CriadoPorUsuarioId",
                table: "patrimonio_bens",
                column: "CriadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_Excluido_Situacao_Nome",
                table: "patrimonio_bens",
                columns: new[] { "Excluido", "Situacao", "Nome" });

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_ExcluidoPorUsuarioId",
                table: "patrimonio_bens",
                column: "ExcluidoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_NumeroTombo",
                table: "patrimonio_bens",
                column: "NumeroTombo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_ResponsavelGuardaId",
                table: "patrimonio_bens",
                column: "ResponsavelGuardaId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_bens_Tombo",
                table: "patrimonio_bens",
                column: "Tombo",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_emprestimos_Bem_Retirada",
                table: "patrimonio_emprestimos",
                columns: new[] { "BemId", "DataRetirada" });

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_emprestimos_Encerramento_PrevisaoDevolucao",
                table: "patrimonio_emprestimos",
                columns: new[] { "Encerramento", "PrevisaoDevolucao" });

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_emprestimos_EncerramentoRegistradoPorUsuarioId",
                table: "patrimonio_emprestimos",
                column: "EncerramentoRegistradoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_emprestimos_MembroId",
                table: "patrimonio_emprestimos",
                column: "MembroId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_emprestimos_RegistradoPorUsuarioId",
                table: "patrimonio_emprestimos",
                column: "RegistradoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_patrimonio_emprestimos_bem_aberto",
                table: "patrimonio_emprestimos",
                column: "BemId",
                unique: true,
                filter: "[Encerramento] IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventario_itens_BemId",
                table: "patrimonio_inventario_itens",
                column: "BemId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventario_itens_InventarioId_BemId",
                table: "patrimonio_inventario_itens",
                columns: new[] { "InventarioId", "BemId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventarios_DataInventario_FechadoEmUtc",
                table: "patrimonio_inventarios",
                columns: new[] { "DataInventario", "FechadoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventarios_FechadoPorUsuarioId",
                table: "patrimonio_inventarios",
                column: "FechadoPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventarios_ResponsavelId",
                table: "patrimonio_inventarios",
                column: "ResponsavelId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_inventarios_VotacaoRegistradaPorUsuarioId",
                table: "patrimonio_inventarios",
                column: "VotacaoRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_manutencoes_Bem_Entrada",
                table: "patrimonio_manutencoes",
                columns: new[] { "BemId", "DataEntrada" });

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_manutencoes_RegistradaPorUsuarioId",
                table: "patrimonio_manutencoes",
                column: "RegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_patrimonio_manutencoes_SaidaRegistradaPorUsuarioId",
                table: "patrimonio_manutencoes",
                column: "SaidaRegistradaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_patrimonio_manutencoes_bem_aberta",
                table: "patrimonio_manutencoes",
                column: "BemId",
                unique: true,
                filter: "[DataSaida] IS NULL");

            foreach (var (tabela, bem, desejo) in TabelasHistorico)
            {
                migrationBuilder.Sql(TriggerHistorico(tabela, bem, desejo));
            }

            migrationBuilder.Sql(TriggerItensInventarioImutaveis);
            migrationBuilder.Sql(TriggerInventarioFechado);
            migrationBuilder.Sql(CargoAlmoxarife);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // O cargo ALM não é removido: pode estar atribuído a usuários e já existia nas instalações semeadas.
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_patrimonio_inventarios_Fechado];");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS [dbo].[TR_patrimonio_inventario_itens_Imutaveis];");
            foreach (var (tabela, _, _) in TabelasHistorico)
            {
                migrationBuilder.Sql($"DROP TRIGGER IF EXISTS [dbo].[TR_{tabela}_Historico];");
            }

            migrationBuilder.DropTable(
                name: "almoxarifado_atendimentos_bens");

            migrationBuilder.DropTable(
                name: "almoxarifado_historico");

            migrationBuilder.DropTable(
                name: "almoxarifado_responsaveis");

            migrationBuilder.DropTable(
                name: "patrimonio_emprestimos");

            migrationBuilder.DropTable(
                name: "patrimonio_inventario_itens");

            migrationBuilder.DropTable(
                name: "patrimonio_manutencoes");

            migrationBuilder.DropTable(
                name: "almoxarifado_atendimentos");

            migrationBuilder.DropTable(
                name: "patrimonio_inventarios");

            migrationBuilder.DropTable(
                name: "patrimonio_bens");

            migrationBuilder.DropTable(
                name: "almoxarifado_desejos");
        }

        /// <summary>
        /// Tabelas auditadas e as expressões (sobre inserted i) do bem e da necessidade a que cada linha pertence; fixas nesta migration.
        /// </summary>
        internal static readonly (string Tabela, string Bem, string Desejo)[] TabelasHistorico =
        [
            ("patrimonio_bens", "i.[Id]", "NULL"),
            ("patrimonio_emprestimos", "i.[BemId]", "NULL"),
            ("patrimonio_manutencoes", "i.[BemId]", "NULL"),
            ("patrimonio_inventarios", "NULL", "NULL"),
            ("patrimonio_inventario_itens", "i.[BemId]", "NULL"),
            ("almoxarifado_desejos", "NULL", "i.[Id]"),
            ("almoxarifado_atendimentos", "NULL", "i.[DesejoId]"),
            ("almoxarifado_atendimentos_bens", "i.[BemId]",
                "(SELECT a.[DesejoId] FROM [dbo].[almoxarifado_atendimentos] a WHERE a.[Id] = i.[AtendimentoId])"),
            ("almoxarifado_responsaveis", "NULL", "NULL"),
        ];

        /// <summary>
        /// Grava em almoxarifado_historico cada linha incluída ou alterada: estado anterior (null na inclusão) e novo, em JSON com todas as
        /// colunas, com responsável, IP, operação e justificativa do SESSION_CONTEXT (o mesmo mecanismo de formacao_historico). Sem contexto,
        /// a escrita é recusada. almoxarifado_historico é tabela de auditoria (runtime só com SELECT); o trigger grava por encadeamento de
        /// propriedade.
        /// </summary>
        private static string TriggerHistorico(string tabela, string bem, string desejo) => $"""
            CREATE TRIGGER [dbo].[TR_{tabela}_Historico] ON [dbo].[{tabela}] AFTER INSERT, UPDATE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted) RETURN;
              DECLARE @Usuario uniqueidentifier = TRY_CAST(SESSION_CONTEXT(N'UsuarioResponsavelId') AS uniqueidentifier);
              DECLARE @Ip varchar(45) = TRY_CAST(SESSION_CONTEXT(N'IpResponsavelExclusao') AS varchar(45));
              DECLARE @Motivo varchar(255) = NULLIF(TRY_CAST(SESSION_CONTEXT(N'MotivoExclusao') AS varchar(255)), '');
              DECLARE @Acao varchar(40) = TRY_CAST(SESSION_CONTEXT(N'AcaoAuditoria') AS varchar(40));
              IF @Usuario IS NULL OR @Ip IS NULL THROW 50130, 'Contexto de auditoria obrigatório.', 1;
              INSERT INTO [dbo].[almoxarifado_historico]
                ([Id],[Tabela],[RegistroId],[BemId],[DesejoId],[Acao],[Motivo],[EstadoAnteriorJson],[EstadoNovoJson],[UsuarioResponsavelId],[IpResponsavel],[OcorridoEmUtc])
              SELECT NEWID(), '{tabela}', i.[Id], {bem}, {desejo}, @Acao, @Motivo,
                     CAST((SELECT x.* FROM deleted x WHERE x.[Id] = i.[Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS varchar(max)),
                     CAST((SELECT y.* FROM inserted y WHERE y.[Id] = i.[Id] FOR JSON PATH, WITHOUT_ARRAY_WRAPPER, INCLUDE_NULL_VALUES) AS varchar(max)),
                     @Usuario, @Ip, SYSUTCDATETIME()
              FROM inserted i;
            END
            """;

        /// <summary>A fotografia de um inventário fechado nunca muda: alterações posteriores nos bens não a recalculam.</summary>
        private const string TriggerItensInventarioImutaveis = """
            CREATE TRIGGER [dbo].[TR_patrimonio_inventario_itens_Imutaveis] ON [dbo].[patrimonio_inventario_itens] AFTER UPDATE, DELETE AS
            BEGIN
              SET NOCOUNT ON;
              IF EXISTS (SELECT 1 FROM deleted) THROW 50131, 'Itens de inventário fechado são imutáveis.', 1;
            END
            """;

        /// <summary>
        /// Inventário fechado: só a votação (data, referência da ata e quem registrou) é preenchida depois, uma única vez; os dados da
        /// conferência e os totais não mudam, e o inventário não é excluído.
        /// </summary>
        private const string TriggerInventarioFechado = """
            CREATE TRIGGER [dbo].[TR_patrimonio_inventarios_Fechado] ON [dbo].[patrimonio_inventarios] AFTER UPDATE, DELETE AS
            BEGIN
              SET NOCOUNT ON;
              IF NOT EXISTS (SELECT 1 FROM inserted) AND EXISTS (SELECT 1 FROM deleted)
                THROW 50132, 'Inventário fechado não é excluído.', 1;
              IF EXISTS (SELECT 1 FROM deleted d JOIN inserted i ON i.[Id] = d.[Id]
                         WHERE d.[VotacaoRegistradaEmUtc] IS NOT NULL OR i.[DataInventario] <> d.[DataInventario]
                            OR i.[ResponsavelId] <> d.[ResponsavelId] OR i.[FechadoPorUsuarioId] <> d.[FechadoPorUsuarioId]
                            OR i.[FechadoEmUtc] <> d.[FechadoEmUtc] OR i.[TotalBens] <> d.[TotalBens]
                            OR i.[TotalBensComValorComercial] <> d.[TotalBensComValorComercial]
                            OR i.[ValorEstimadoPatrimonial] <> d.[ValorEstimadoPatrimonial]
                            OR ISNULL(i.[Observacoes], '') <> ISNULL(d.[Observacoes], ''))
                THROW 50132, 'Inventário fechado só recebe o registro da votação, uma única vez.', 1;
            END
            """;

        /// <summary>
        /// Cria o cargo Almoxarife (ALM) só se ele ainda não existir numa instalação já semeada. Banco novo (sem cargos) fica para o
        /// DbSeeder, que semeia todos os cargos, inclusive o ALM; um ALM existente (ativo ou não) nunca é alterado.
        /// </summary>
        internal const string CargoAlmoxarife = """
            IF EXISTS (SELECT 1 FROM [dbo].[Cargos]) AND NOT EXISTS (SELECT 1 FROM [dbo].[Cargos] WHERE [Role] = 'ALM')
              INSERT INTO [dbo].[Cargos] ([Id], [Nome], [Descricao], [Ativo], [CriadoPor], [CriadoEm], [Role])
              VALUES (NEWID(), 'Almoxarife', 'Guarda e conserva o material da unidade.', 1, 'Sistema', SYSUTCDATETIME(), 'ALM');
            """;
    }
}
