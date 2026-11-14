using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Almirante.Api.Data.Migrations
{
    /// <summary>
    /// Issue #182: RBAC dinâmico. Cria o catálogo Permissoes (dados fixos), CargoPermissoes, CargoRegrasAlvo, AutorizacaoEstado (revisão
    /// global) e Autorizacao_historico (só inserção: o trigger TR_Autorizacao_historico_Imutavel recusa UPDATE e DELETE); acrescenta a
    /// Cargos SistemaProtegido e Versao. Conversão dos acessos atuais, preservando Ids e vínculos: o cargo de role ADM vira o perfil de
    /// sistema protegido e os 17 cargos conhecidos (comparação exata da role) recebem as concessões e regras por alvo equivalentes às
    /// listas estáticas anteriores (<see cref="Concessoes"/> e <see cref="RegrasAlvo"/>, congeladas aqui). Cargos customizados preexistentes
    /// não recebem nada por aproximação (inclusive a consulta ao almoxarifado, que antes valia para qualquer cargo fora de DS): conceda pela
    /// API. Num banco novo os cargos ainda não existem aqui e o DbSeeder semeia a mesma matriz ao criá-los. Down remove as tabelas novas
    /// (com o histórico) e as colunas; a API anterior volta a decidir pelas listas estáticas.
    /// </summary>
    public partial class AddRbacDinamico : Migration
    {
        private const string TriggerImutavel = $"""
            CREATE TRIGGER [dbo].[{AlmiranteDbContext.TriggerAutorizacaoHistoricoImutavel}] ON [dbo].[{AlmiranteDbContext.TabelaAutorizacaoHistorico}]
            INSTEAD OF UPDATE, DELETE AS
            BEGIN
              SET NOCOUNT ON;
              THROW 50182, 'Autorizacao_historico aceita somente insercao: o historico de autorizacao nao pode ser alterado nem excluido.', 1;
            END
            """;

        /// <summary>Concessões iniciais (role, chave), congeladas nesta migration; MatrizInicialAutorizacao tem a mesma lista.</summary>
        public static readonly (string Role, string Permissao)[] Concessoes =
        [
            ("ADM", "usuarios.consultar"), ("ADM", "usuarios.criar"), ("ADM", "usuarios.editar"), ("ADM", "usuarios.excluir"),
            ("ADM", "usuarios.foto.alterar"), ("ADM", "cargos.consultar"), ("ADM", "usuarios.senha.alterar-terceiros"), ("ADM", "usuarios.senha.recuperacao-atender"),
            ("ADM", "financeiro.lancamentos.consultar"), ("ADM", "financeiro.lancamentos.criar"), ("ADM", "financeiro.lancamentos.editar"), ("ADM", "financeiro.lancamentos.excluir"),
            ("ADM", "financeiro.pagamentos.registrar"), ("ADM", "financeiro.tesouraria.consultar"), ("ADM", "dashboard.financeiro.consultar"), ("ADM", "eventos.consultar"),
            ("ADM", "eventos.criar"), ("ADM", "eventos.editar"), ("ADM", "eventos.excluir"), ("ADM", "membros.consultar"),
            ("ADM", "dashboard.consultar"), ("ADM", "formacao.gestao.consultar"), ("ADM", "formacao.gerenciar"), ("ADM", "formacao.revisoes.administrar"),
            ("ADM", "almoxarifado.consultar"), ("ADM", "almoxarifado.gerenciar"), ("ADM", "almoxarifado.responsaveis.designar"), ("ADM", "autorizacao.consultar"),
            ("ADM", "autorizacao.cargos.gerenciar"), ("ADM", "autorizacao.concessoes.gerenciar"), ("ADM", "autorizacao.regras-alvo.gerenciar"), ("DIR", "usuarios.consultar"),
            ("DIR", "usuarios.criar"), ("DIR", "usuarios.editar"), ("DIR", "usuarios.excluir"), ("DIR", "usuarios.foto.alterar"),
            ("DIR", "cargos.consultar"), ("DIR", "usuarios.senha.alterar-terceiros"), ("DIR", "usuarios.senha.recuperacao-atender"), ("DIR", "financeiro.lancamentos.consultar"),
            ("DIR", "financeiro.lancamentos.criar"), ("DIR", "financeiro.lancamentos.editar"), ("DIR", "financeiro.lancamentos.excluir"), ("DIR", "financeiro.pagamentos.registrar"),
            ("DIR", "financeiro.tesouraria.consultar"), ("DIR", "dashboard.financeiro.consultar"), ("DIR", "eventos.consultar"), ("DIR", "eventos.criar"),
            ("DIR", "eventos.editar"), ("DIR", "eventos.excluir"), ("DIR", "membros.consultar"), ("DIR", "dashboard.consultar"),
            ("DIR", "formacao.gestao.consultar"), ("DIR", "formacao.gerenciar"), ("DIR", "formacao.revisoes.administrar"), ("DIR", "almoxarifado.consultar"),
            ("DIR", "almoxarifado.gerenciar"), ("DIR", "almoxarifado.responsaveis.designar"), ("DIRA", "usuarios.consultar"), ("DIRA", "usuarios.criar"),
            ("DIRA", "usuarios.editar"), ("DIRA", "usuarios.excluir"), ("DIRA", "usuarios.foto.alterar"), ("DIRA", "cargos.consultar"),
            ("DIRA", "financeiro.lancamentos.consultar"), ("DIRA", "financeiro.lancamentos.criar"), ("DIRA", "financeiro.lancamentos.editar"), ("DIRA", "financeiro.lancamentos.excluir"),
            ("DIRA", "financeiro.pagamentos.registrar"), ("DIRA", "financeiro.tesouraria.consultar"), ("DIRA", "dashboard.financeiro.consultar"), ("DIRA", "eventos.consultar"),
            ("DIRA", "eventos.criar"), ("DIRA", "eventos.editar"), ("DIRA", "eventos.excluir"), ("DIRA", "membros.consultar"),
            ("DIRA", "dashboard.consultar"), ("DIRA", "formacao.gestao.consultar"), ("DIRA", "formacao.gerenciar"), ("DIRA", "formacao.revisoes.administrar"),
            ("DIRA", "almoxarifado.consultar"), ("DIRA", "almoxarifado.gerenciar"), ("DIRA", "almoxarifado.responsaveis.designar"), ("SEC", "usuarios.consultar"),
            ("SEC", "usuarios.criar"), ("SEC", "usuarios.editar"), ("SEC", "usuarios.excluir"), ("SEC", "usuarios.foto.alterar"),
            ("SEC", "cargos.consultar"), ("SEC", "usuarios.senha.alterar-terceiros"), ("SEC", "usuarios.senha.recuperacao-atender"), ("SEC", "eventos.consultar"),
            ("SEC", "eventos.criar"), ("SEC", "eventos.editar"), ("SEC", "eventos.excluir"), ("SEC", "membros.consultar"),
            ("SEC", "dashboard.consultar"), ("SEC", "formacao.gestao.consultar"), ("SEC", "formacao.gerenciar"), ("SEC", "formacao.revisoes.administrar"),
            ("SEC", "almoxarifado.consultar"), ("SEC", "almoxarifado.gerenciar"), ("SEC", "almoxarifado.responsaveis.designar"), ("TES", "financeiro.lancamentos.consultar"),
            ("TES", "financeiro.lancamentos.criar"), ("TES", "financeiro.lancamentos.editar"), ("TES", "financeiro.lancamentos.excluir"), ("TES", "financeiro.pagamentos.registrar"),
            ("TES", "financeiro.tesouraria.consultar"), ("TES", "dashboard.financeiro.consultar"), ("TES", "eventos.consultar"), ("TES", "eventos.criar"),
            ("TES", "eventos.editar"), ("TES", "eventos.excluir"), ("TES", "membros.consultar"), ("TES", "dashboard.consultar"),
            ("TES", "almoxarifado.consultar"), ("ALM", "almoxarifado.consultar"), ("ALM", "almoxarifado.gerenciar"), ("CAP", "almoxarifado.consultar"),
            ("CONS", "almoxarifado.consultar"), ("CONSA", "almoxarifado.consultar"), ("INST", "almoxarifado.consultar"), ("CPT", "almoxarifado.consultar"),
            ("SECU", "almoxarifado.consultar"), ("TESU", "almoxarifado.consultar"), ("PAD", "almoxarifado.consultar"), ("COREC", "almoxarifado.consultar"),
            ("CAPU", "almoxarifado.consultar"),
        ];

        /// <summary>Regras por alvo iniciais (solicitante, chave, alvo), congeladas nesta migration.</summary>
        public static readonly (string Solicitante, string Permissao, string Alvo)[] RegrasAlvo =
        [
            ("ADM", "usuarios.criar", "ADM"), ("ADM", "usuarios.criar", "DIR"), ("ADM", "usuarios.criar", "DIRA"), ("ADM", "usuarios.criar", "SEC"),
            ("ADM", "usuarios.criar", "TES"), ("ADM", "usuarios.criar", "CAP"), ("ADM", "usuarios.criar", "CONS"), ("ADM", "usuarios.criar", "CONSA"),
            ("ADM", "usuarios.criar", "INST"), ("ADM", "usuarios.criar", "CPT"), ("ADM", "usuarios.criar", "SECU"), ("ADM", "usuarios.criar", "TESU"),
            ("ADM", "usuarios.criar", "ALM"), ("ADM", "usuarios.criar", "PAD"), ("ADM", "usuarios.criar", "COREC"), ("ADM", "usuarios.criar", "CAPU"),
            ("ADM", "usuarios.criar", "DS"), ("ADM", "usuarios.editar", "ADM"), ("ADM", "usuarios.editar", "DIR"), ("ADM", "usuarios.editar", "DIRA"),
            ("ADM", "usuarios.editar", "SEC"), ("ADM", "usuarios.editar", "TES"), ("ADM", "usuarios.editar", "CAP"), ("ADM", "usuarios.editar", "CONS"),
            ("ADM", "usuarios.editar", "CONSA"), ("ADM", "usuarios.editar", "INST"), ("ADM", "usuarios.editar", "CPT"), ("ADM", "usuarios.editar", "SECU"),
            ("ADM", "usuarios.editar", "TESU"), ("ADM", "usuarios.editar", "ALM"), ("ADM", "usuarios.editar", "PAD"), ("ADM", "usuarios.editar", "COREC"),
            ("ADM", "usuarios.editar", "CAPU"), ("ADM", "usuarios.editar", "DS"), ("ADM", "usuarios.excluir", "ADM"), ("ADM", "usuarios.excluir", "DIR"),
            ("ADM", "usuarios.excluir", "DIRA"), ("ADM", "usuarios.excluir", "SEC"), ("ADM", "usuarios.excluir", "TES"), ("ADM", "usuarios.excluir", "CAP"),
            ("ADM", "usuarios.excluir", "CONS"), ("ADM", "usuarios.excluir", "CONSA"), ("ADM", "usuarios.excluir", "INST"), ("ADM", "usuarios.excluir", "CPT"),
            ("ADM", "usuarios.excluir", "SECU"), ("ADM", "usuarios.excluir", "TESU"), ("ADM", "usuarios.excluir", "ALM"), ("ADM", "usuarios.excluir", "PAD"),
            ("ADM", "usuarios.excluir", "COREC"), ("ADM", "usuarios.excluir", "CAPU"), ("ADM", "usuarios.excluir", "DS"), ("ADM", "usuarios.foto.alterar", "ADM"),
            ("ADM", "usuarios.foto.alterar", "DIR"), ("ADM", "usuarios.foto.alterar", "DIRA"), ("ADM", "usuarios.foto.alterar", "SEC"), ("ADM", "usuarios.foto.alterar", "TES"),
            ("ADM", "usuarios.foto.alterar", "CAP"), ("ADM", "usuarios.foto.alterar", "CONS"), ("ADM", "usuarios.foto.alterar", "CONSA"), ("ADM", "usuarios.foto.alterar", "INST"),
            ("ADM", "usuarios.foto.alterar", "CPT"), ("ADM", "usuarios.foto.alterar", "SECU"), ("ADM", "usuarios.foto.alterar", "TESU"), ("ADM", "usuarios.foto.alterar", "ALM"),
            ("ADM", "usuarios.foto.alterar", "PAD"), ("ADM", "usuarios.foto.alterar", "COREC"), ("ADM", "usuarios.foto.alterar", "CAPU"), ("ADM", "usuarios.foto.alterar", "DS"),
            ("ADM", "usuarios.senha.alterar-terceiros", "SEC"), ("ADM", "usuarios.senha.alterar-terceiros", "TES"), ("ADM", "usuarios.senha.alterar-terceiros", "DS"), ("ADM", "usuarios.senha.recuperacao-atender", "SEC"),
            ("ADM", "usuarios.senha.recuperacao-atender", "TES"), ("ADM", "usuarios.senha.recuperacao-atender", "DS"), ("DIR", "usuarios.criar", "DIR"), ("DIR", "usuarios.criar", "DIRA"),
            ("DIR", "usuarios.criar", "SEC"), ("DIR", "usuarios.criar", "TES"), ("DIR", "usuarios.criar", "CAP"), ("DIR", "usuarios.criar", "CONS"),
            ("DIR", "usuarios.criar", "CONSA"), ("DIR", "usuarios.criar", "INST"), ("DIR", "usuarios.criar", "CPT"), ("DIR", "usuarios.criar", "SECU"),
            ("DIR", "usuarios.criar", "TESU"), ("DIR", "usuarios.criar", "ALM"), ("DIR", "usuarios.criar", "PAD"), ("DIR", "usuarios.criar", "COREC"),
            ("DIR", "usuarios.criar", "CAPU"), ("DIR", "usuarios.criar", "DS"), ("DIR", "usuarios.editar", "DIR"), ("DIR", "usuarios.editar", "DIRA"),
            ("DIR", "usuarios.editar", "SEC"), ("DIR", "usuarios.editar", "TES"), ("DIR", "usuarios.editar", "CAP"), ("DIR", "usuarios.editar", "CONS"),
            ("DIR", "usuarios.editar", "CONSA"), ("DIR", "usuarios.editar", "INST"), ("DIR", "usuarios.editar", "CPT"), ("DIR", "usuarios.editar", "SECU"),
            ("DIR", "usuarios.editar", "TESU"), ("DIR", "usuarios.editar", "ALM"), ("DIR", "usuarios.editar", "PAD"), ("DIR", "usuarios.editar", "COREC"),
            ("DIR", "usuarios.editar", "CAPU"), ("DIR", "usuarios.editar", "DS"), ("DIR", "usuarios.excluir", "DIR"), ("DIR", "usuarios.excluir", "DIRA"),
            ("DIR", "usuarios.excluir", "SEC"), ("DIR", "usuarios.excluir", "TES"), ("DIR", "usuarios.excluir", "CAP"), ("DIR", "usuarios.excluir", "CONS"),
            ("DIR", "usuarios.excluir", "CONSA"), ("DIR", "usuarios.excluir", "INST"), ("DIR", "usuarios.excluir", "CPT"), ("DIR", "usuarios.excluir", "SECU"),
            ("DIR", "usuarios.excluir", "TESU"), ("DIR", "usuarios.excluir", "ALM"), ("DIR", "usuarios.excluir", "PAD"), ("DIR", "usuarios.excluir", "COREC"),
            ("DIR", "usuarios.excluir", "CAPU"), ("DIR", "usuarios.excluir", "DS"), ("DIR", "usuarios.foto.alterar", "DIR"), ("DIR", "usuarios.foto.alterar", "DIRA"),
            ("DIR", "usuarios.foto.alterar", "SEC"), ("DIR", "usuarios.foto.alterar", "TES"), ("DIR", "usuarios.foto.alterar", "CAP"), ("DIR", "usuarios.foto.alterar", "CONS"),
            ("DIR", "usuarios.foto.alterar", "CONSA"), ("DIR", "usuarios.foto.alterar", "INST"), ("DIR", "usuarios.foto.alterar", "CPT"), ("DIR", "usuarios.foto.alterar", "SECU"),
            ("DIR", "usuarios.foto.alterar", "TESU"), ("DIR", "usuarios.foto.alterar", "ALM"), ("DIR", "usuarios.foto.alterar", "PAD"), ("DIR", "usuarios.foto.alterar", "COREC"),
            ("DIR", "usuarios.foto.alterar", "CAPU"), ("DIR", "usuarios.foto.alterar", "DS"), ("DIR", "usuarios.senha.alterar-terceiros", "SEC"), ("DIR", "usuarios.senha.alterar-terceiros", "TES"),
            ("DIR", "usuarios.senha.alterar-terceiros", "DS"), ("DIR", "usuarios.senha.recuperacao-atender", "SEC"), ("DIR", "usuarios.senha.recuperacao-atender", "TES"), ("DIR", "usuarios.senha.recuperacao-atender", "DS"),
            ("DIRA", "usuarios.criar", "DIR"), ("DIRA", "usuarios.criar", "DIRA"), ("DIRA", "usuarios.criar", "SEC"), ("DIRA", "usuarios.criar", "TES"),
            ("DIRA", "usuarios.criar", "CAP"), ("DIRA", "usuarios.criar", "CONS"), ("DIRA", "usuarios.criar", "CONSA"), ("DIRA", "usuarios.criar", "INST"),
            ("DIRA", "usuarios.criar", "CPT"), ("DIRA", "usuarios.criar", "SECU"), ("DIRA", "usuarios.criar", "TESU"), ("DIRA", "usuarios.criar", "ALM"),
            ("DIRA", "usuarios.criar", "PAD"), ("DIRA", "usuarios.criar", "COREC"), ("DIRA", "usuarios.criar", "CAPU"), ("DIRA", "usuarios.criar", "DS"),
            ("DIRA", "usuarios.editar", "DIR"), ("DIRA", "usuarios.editar", "DIRA"), ("DIRA", "usuarios.editar", "SEC"), ("DIRA", "usuarios.editar", "TES"),
            ("DIRA", "usuarios.editar", "CAP"), ("DIRA", "usuarios.editar", "CONS"), ("DIRA", "usuarios.editar", "CONSA"), ("DIRA", "usuarios.editar", "INST"),
            ("DIRA", "usuarios.editar", "CPT"), ("DIRA", "usuarios.editar", "SECU"), ("DIRA", "usuarios.editar", "TESU"), ("DIRA", "usuarios.editar", "ALM"),
            ("DIRA", "usuarios.editar", "PAD"), ("DIRA", "usuarios.editar", "COREC"), ("DIRA", "usuarios.editar", "CAPU"), ("DIRA", "usuarios.editar", "DS"),
            ("DIRA", "usuarios.excluir", "DIR"), ("DIRA", "usuarios.excluir", "DIRA"), ("DIRA", "usuarios.excluir", "SEC"), ("DIRA", "usuarios.excluir", "TES"),
            ("DIRA", "usuarios.excluir", "CAP"), ("DIRA", "usuarios.excluir", "CONS"), ("DIRA", "usuarios.excluir", "CONSA"), ("DIRA", "usuarios.excluir", "INST"),
            ("DIRA", "usuarios.excluir", "CPT"), ("DIRA", "usuarios.excluir", "SECU"), ("DIRA", "usuarios.excluir", "TESU"), ("DIRA", "usuarios.excluir", "ALM"),
            ("DIRA", "usuarios.excluir", "PAD"), ("DIRA", "usuarios.excluir", "COREC"), ("DIRA", "usuarios.excluir", "CAPU"), ("DIRA", "usuarios.excluir", "DS"),
            ("DIRA", "usuarios.foto.alterar", "DIR"), ("DIRA", "usuarios.foto.alterar", "DIRA"), ("DIRA", "usuarios.foto.alterar", "SEC"), ("DIRA", "usuarios.foto.alterar", "TES"),
            ("DIRA", "usuarios.foto.alterar", "CAP"), ("DIRA", "usuarios.foto.alterar", "CONS"), ("DIRA", "usuarios.foto.alterar", "CONSA"), ("DIRA", "usuarios.foto.alterar", "INST"),
            ("DIRA", "usuarios.foto.alterar", "CPT"), ("DIRA", "usuarios.foto.alterar", "SECU"), ("DIRA", "usuarios.foto.alterar", "TESU"), ("DIRA", "usuarios.foto.alterar", "ALM"),
            ("DIRA", "usuarios.foto.alterar", "PAD"), ("DIRA", "usuarios.foto.alterar", "COREC"), ("DIRA", "usuarios.foto.alterar", "CAPU"), ("DIRA", "usuarios.foto.alterar", "DS"),
            ("SEC", "usuarios.criar", "CAP"), ("SEC", "usuarios.criar", "CONS"), ("SEC", "usuarios.criar", "CONSA"), ("SEC", "usuarios.criar", "INST"),
            ("SEC", "usuarios.criar", "CPT"), ("SEC", "usuarios.criar", "SECU"), ("SEC", "usuarios.criar", "TESU"), ("SEC", "usuarios.criar", "ALM"),
            ("SEC", "usuarios.criar", "PAD"), ("SEC", "usuarios.criar", "COREC"), ("SEC", "usuarios.criar", "CAPU"), ("SEC", "usuarios.criar", "DS"),
            ("SEC", "usuarios.editar", "CAP"), ("SEC", "usuarios.editar", "CONS"), ("SEC", "usuarios.editar", "CONSA"), ("SEC", "usuarios.editar", "INST"),
            ("SEC", "usuarios.editar", "CPT"), ("SEC", "usuarios.editar", "SECU"), ("SEC", "usuarios.editar", "TESU"), ("SEC", "usuarios.editar", "ALM"),
            ("SEC", "usuarios.editar", "PAD"), ("SEC", "usuarios.editar", "COREC"), ("SEC", "usuarios.editar", "CAPU"), ("SEC", "usuarios.editar", "DS"),
            ("SEC", "usuarios.excluir", "CAP"), ("SEC", "usuarios.excluir", "CONS"), ("SEC", "usuarios.excluir", "CONSA"), ("SEC", "usuarios.excluir", "INST"),
            ("SEC", "usuarios.excluir", "CPT"), ("SEC", "usuarios.excluir", "SECU"), ("SEC", "usuarios.excluir", "TESU"), ("SEC", "usuarios.excluir", "ALM"),
            ("SEC", "usuarios.excluir", "PAD"), ("SEC", "usuarios.excluir", "COREC"), ("SEC", "usuarios.excluir", "CAPU"), ("SEC", "usuarios.excluir", "DS"),
            ("SEC", "usuarios.foto.alterar", "CAP"), ("SEC", "usuarios.foto.alterar", "CONS"), ("SEC", "usuarios.foto.alterar", "CONSA"), ("SEC", "usuarios.foto.alterar", "INST"),
            ("SEC", "usuarios.foto.alterar", "CPT"), ("SEC", "usuarios.foto.alterar", "SECU"), ("SEC", "usuarios.foto.alterar", "TESU"), ("SEC", "usuarios.foto.alterar", "ALM"),
            ("SEC", "usuarios.foto.alterar", "PAD"), ("SEC", "usuarios.foto.alterar", "COREC"), ("SEC", "usuarios.foto.alterar", "CAPU"), ("SEC", "usuarios.foto.alterar", "DS"),
            ("SEC", "usuarios.senha.alterar-terceiros", "DS"), ("SEC", "usuarios.senha.recuperacao-atender", "DS"),
        ];

        private static string Valores(IEnumerable<string[]> linhas) =>
            string.Join(",\n    ", linhas.Select(colunas => "(" + string.Join(", ", colunas.Select(c => $"'{c}'")) + ")"));

        /// <summary>Concessões para os cargos existentes cuja role é exatamente a da matriz (Latin1_General_BIN2: sem casar 'adm' com 'ADM').</summary>
        public static string SqlConcessoes => $"""
            INSERT INTO [dbo].[CargoPermissoes] ([Id], [CargoId], [PermissaoId], [Ativo], [ConcedidaEmUtc])
            SELECT NEWID(), c.[Id], p.[Id], 1, SYSUTCDATETIME()
            FROM (VALUES
                {Valores(Concessoes.Select(x => new[] { x.Role, x.Permissao }))}
            ) AS v([Role], [Chave])
            INNER JOIN [dbo].[Cargos] c ON c.[Role] COLLATE Latin1_General_BIN2 = v.[Role] COLLATE Latin1_General_BIN2
            INNER JOIN [dbo].[Permissoes] p ON p.[Chave] COLLATE Latin1_General_BIN2 = v.[Chave] COLLATE Latin1_General_BIN2;
            """;

        public static string SqlRegrasAlvo => $"""
            INSERT INTO [dbo].[CargoRegrasAlvo] ([Id], [CargoSolicitanteId], [PermissaoId], [CargoAlvoId], [Ativo], [ConcedidaEmUtc])
            SELECT NEWID(), s.[Id], p.[Id], a.[Id], 1, SYSUTCDATETIME()
            FROM (VALUES
                {Valores(RegrasAlvo.Select(x => new[] { x.Solicitante, x.Permissao, x.Alvo }))}
            ) AS v([Solicitante], [Chave], [Alvo])
            INNER JOIN [dbo].[Cargos] s ON s.[Role] COLLATE Latin1_General_BIN2 = v.[Solicitante] COLLATE Latin1_General_BIN2
            INNER JOIN [dbo].[Cargos] a ON a.[Role] COLLATE Latin1_General_BIN2 = v.[Alvo] COLLATE Latin1_General_BIN2
            INNER JOIN [dbo].[Permissoes] p ON p.[Chave] COLLATE Latin1_General_BIN2 = v.[Chave] COLLATE Latin1_General_BIN2;
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "SistemaProtegido",
                table: "Cargos",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "Versao",
                table: "Cargos",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.CreateTable(
                name: "Autorizacao_historico",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    OcorridoEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsuarioResponsavelId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Operacao = table.Column<string>(type: "varchar(40)", unicode: false, maxLength: 40, nullable: false),
                    Antes = table.Column<string>(type: "varchar(max)", unicode: false, nullable: true),
                    Depois = table.Column<string>(type: "varchar(max)", unicode: false, nullable: false),
                    Motivo = table.Column<string>(type: "varchar(255)", unicode: false, maxLength: 255, nullable: false),
                    IpResponsavel = table.Column<string>(type: "varchar(45)", unicode: false, maxLength: 45, nullable: false),
                    TraceId = table.Column<string>(type: "varchar(64)", unicode: false, maxLength: 64, nullable: false),
                    RevisaoAutorizacao = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Autorizacao_historico", x => x.Id);
                    table.CheckConstraint("CK_Autorizacao_historico_Json", "([Antes] IS NULL OR ISJSON([Antes]) = 1) AND ISJSON([Depois]) = 1");
                    table.CheckConstraint("CK_Autorizacao_historico_Operacao", "[Operacao] IN ('CargoCriado', 'CargoAlterado', 'CargoDesativado', 'CargoReativado', 'ConcessoesSubstituidas', 'RegrasAlvoSubstituidas')");
                    table.ForeignKey(
                        name: "FK_Autorizacao_historico_Cargos_CargoId",
                        column: x => x.CargoId,
                        principalTable: "Cargos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Autorizacao_historico_Usuarios_UsuarioResponsavelId",
                        column: x => x.UsuarioResponsavelId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutorizacaoEstado",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false),
                    Revisao = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutorizacaoEstado", x => x.Id);
                    table.CheckConstraint("CK_AutorizacaoEstado_Unico", "[Id] = 1");
                });

            migrationBuilder.CreateTable(
                name: "Permissoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Chave = table.Column<string>(type: "varchar(150)", unicode: false, maxLength: 150, nullable: false),
                    Modulo = table.Column<string>(type: "varchar(60)", unicode: false, maxLength: 60, nullable: false),
                    Descricao = table.Column<string>(type: "varchar(500)", unicode: false, maxLength: 500, nullable: false),
                    Escrita = table.Column<bool>(type: "bit", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    ReservadaSistema = table.Column<bool>(type: "bit", nullable: false),
                    ExigeRegraAlvo = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Permissoes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CargoPermissoes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    ConcedidaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcedidaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevogadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevogadaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CargoPermissoes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CargoPermissoes_Cargos_CargoId",
                        column: x => x.CargoId,
                        principalTable: "Cargos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoPermissoes_Permissoes_PermissaoId",
                        column: x => x.PermissaoId,
                        principalTable: "Permissoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoPermissoes_Usuarios_ConcedidaPorUsuarioId",
                        column: x => x.ConcedidaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoPermissoes_Usuarios_RevogadaPorUsuarioId",
                        column: x => x.RevogadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "CargoRegrasAlvo",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoSolicitanteId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PermissaoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CargoAlvoId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Ativo = table.Column<bool>(type: "bit", nullable: false),
                    ConcedidaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    ConcedidaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    RevogadaEmUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    RevogadaPorUsuarioId = table.Column<Guid>(type: "uniqueidentifier", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CargoRegrasAlvo", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CargoRegrasAlvo_Cargos_CargoAlvoId",
                        column: x => x.CargoAlvoId,
                        principalTable: "Cargos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoRegrasAlvo_Cargos_CargoSolicitanteId",
                        column: x => x.CargoSolicitanteId,
                        principalTable: "Cargos",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoRegrasAlvo_Permissoes_PermissaoId",
                        column: x => x.PermissaoId,
                        principalTable: "Permissoes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoRegrasAlvo_Usuarios_ConcedidaPorUsuarioId",
                        column: x => x.ConcedidaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_CargoRegrasAlvo_Usuarios_RevogadaPorUsuarioId",
                        column: x => x.RevogadaPorUsuarioId,
                        principalTable: "Usuarios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "AutorizacaoEstado",
                columns: new[] { "Id", "Revisao" },
                values: new object[] { 1, 1L });

            migrationBuilder.InsertData(
                table: "Permissoes",
                columns: new[] { "Id", "Ativo", "Chave", "Descricao", "Escrita", "ExigeRegraAlvo", "Modulo", "ReservadaSistema" },
                values: new object[,]
                {
                    { new Guid("18200000-0000-4000-8000-000000000001"), true, "usuarios.consultar", "Consultar o cadastro completo de usuários e a foto de outros usuários.", false, false, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000002"), true, "usuarios.criar", "Cadastrar usuários nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000003"), true, "usuarios.editar", "Alterar o cadastro e o cargo de usuários nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000004"), true, "usuarios.excluir", "Excluir (logicamente) usuários nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000005"), true, "usuarios.foto.alterar", "Enviar ou remover a foto de usuários nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000006"), true, "usuarios.senha.alterar-terceiros", "Alterar a senha de usuários nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000007"), true, "usuarios.senha.recuperacao-atender", "Listar e atender pedidos de recuperação de senha nos cargos permitidos pelas regras por alvo.", true, true, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000008"), true, "cargos.consultar", "Consultar os cargos para o cadastro de usuários.", false, false, "Usuários", false },
                    { new Guid("18200000-0000-4000-8000-000000000009"), true, "financeiro.lancamentos.consultar", "Consultar lançamentos.", false, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000010"), true, "financeiro.lancamentos.criar", "Registrar lançamentos.", true, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000011"), true, "financeiro.lancamentos.editar", "Corrigir lançamentos.", true, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000012"), true, "financeiro.lancamentos.excluir", "Excluir lançamentos.", true, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000013"), true, "financeiro.pagamentos.registrar", "Registrar e corrigir pagamentos de lançamentos.", true, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000014"), true, "financeiro.tesouraria.consultar", "Consultar o resumo da tesouraria.", false, false, "Financeiro", false },
                    { new Guid("18200000-0000-4000-8000-000000000015"), true, "eventos.consultar", "Consultar eventos.", false, false, "Eventos", false },
                    { new Guid("18200000-0000-4000-8000-000000000016"), true, "eventos.criar", "Cadastrar eventos (e as cobranças que eles geram).", true, false, "Eventos", false },
                    { new Guid("18200000-0000-4000-8000-000000000017"), true, "eventos.editar", "Alterar eventos (e sincronizar as cobranças).", true, false, "Eventos", false },
                    { new Guid("18200000-0000-4000-8000-000000000018"), true, "eventos.excluir", "Excluir eventos.", true, false, "Eventos", false },
                    { new Guid("18200000-0000-4000-8000-000000000019"), true, "membros.consultar", "Consultar a lista mínima de membros (id, nome e cargo) para seleção.", false, false, "Membros", false },
                    { new Guid("18200000-0000-4000-8000-000000000020"), true, "dashboard.consultar", "Consultar o painel.", false, false, "Dashboard", false },
                    { new Guid("18200000-0000-4000-8000-000000000021"), true, "dashboard.financeiro.consultar", "Ver o bloco financeiro do painel.", false, false, "Dashboard", false },
                    { new Guid("18200000-0000-4000-8000-000000000022"), true, "formacao.gestao.consultar", "Consultar a formação de qualquer membro e os relatórios de formação.", false, false, "Formação", false },
                    { new Guid("18200000-0000-4000-8000-000000000023"), true, "formacao.gerenciar", "Registrar a formação dos membros e manter o catálogo de especialidades.", true, false, "Formação", false },
                    { new Guid("18200000-0000-4000-8000-000000000024"), true, "formacao.revisoes.administrar", "Criar, publicar e descartar revisões de requisitos e ver rascunhos.", true, false, "Formação", false },
                    { new Guid("18200000-0000-4000-8000-000000000025"), true, "almoxarifado.consultar", "Consultar bens, empréstimos, inventários, desejos e relatórios do almoxarifado.", false, false, "Almoxarifado", false },
                    { new Guid("18200000-0000-4000-8000-000000000026"), true, "almoxarifado.gerenciar", "Cadastrar e movimentar bens, inventários e a lista de desejos.", true, false, "Almoxarifado", false },
                    { new Guid("18200000-0000-4000-8000-000000000027"), true, "almoxarifado.responsaveis.designar", "Designar e revogar o responsável pelo almoxarifado.", true, false, "Almoxarifado", false },
                    { new Guid("18200000-0000-4000-8000-000000000028"), true, "autorizacao.consultar", "Consultar o catálogo de permissões, as concessões e as regras por alvo dos cargos.", false, false, "Autorização", true },
                    { new Guid("18200000-0000-4000-8000-000000000029"), true, "autorizacao.cargos.gerenciar", "Criar, alterar, desativar e reativar cargos.", true, false, "Autorização", true },
                    { new Guid("18200000-0000-4000-8000-000000000030"), true, "autorizacao.concessoes.gerenciar", "Conceder e revogar permissões de cargos.", true, false, "Autorização", true },
                    { new Guid("18200000-0000-4000-8000-000000000031"), true, "autorizacao.regras-alvo.gerenciar", "Alterar as regras por alvo dos cargos.", true, false, "Autorização", true }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Autorizacao_historico_cargo_data",
                table: "Autorizacao_historico",
                columns: new[] { "CargoId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_Autorizacao_historico_responsavel_data",
                table: "Autorizacao_historico",
                columns: new[] { "UsuarioResponsavelId", "OcorridoEmUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CargoPermissoes_ConcedidaPorUsuarioId",
                table: "CargoPermissoes",
                column: "ConcedidaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CargoPermissoes_PermissaoId",
                table: "CargoPermissoes",
                column: "PermissaoId");

            migrationBuilder.CreateIndex(
                name: "IX_CargoPermissoes_RevogadaPorUsuarioId",
                table: "CargoPermissoes",
                column: "RevogadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_CargoPermissoes_Cargo_Permissao",
                table: "CargoPermissoes",
                columns: new[] { "CargoId", "PermissaoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CargoRegrasAlvo_CargoAlvoId",
                table: "CargoRegrasAlvo",
                column: "CargoAlvoId");

            migrationBuilder.CreateIndex(
                name: "IX_CargoRegrasAlvo_ConcedidaPorUsuarioId",
                table: "CargoRegrasAlvo",
                column: "ConcedidaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "IX_CargoRegrasAlvo_PermissaoId",
                table: "CargoRegrasAlvo",
                column: "PermissaoId");

            migrationBuilder.CreateIndex(
                name: "IX_CargoRegrasAlvo_RevogadaPorUsuarioId",
                table: "CargoRegrasAlvo",
                column: "RevogadaPorUsuarioId");

            migrationBuilder.CreateIndex(
                name: "UX_CargoRegrasAlvo_Solicitante_Permissao_Alvo",
                table: "CargoRegrasAlvo",
                columns: new[] { "CargoSolicitanteId", "PermissaoId", "CargoAlvoId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_Permissoes_Chave",
                table: "Permissoes",
                column: "Chave",
                unique: true);

            migrationBuilder.Sql(TriggerImutavel);
            migrationBuilder.Sql("UPDATE [dbo].[Cargos] SET [SistemaProtegido] = 1 WHERE [Role] COLLATE Latin1_General_BIN2 = 'ADM';");
            migrationBuilder.Sql(SqlConcessoes);
            migrationBuilder.Sql(SqlRegrasAlvo);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Autorizacao_historico");

            migrationBuilder.DropTable(
                name: "AutorizacaoEstado");

            migrationBuilder.DropTable(
                name: "CargoPermissoes");

            migrationBuilder.DropTable(
                name: "CargoRegrasAlvo");

            migrationBuilder.DropTable(
                name: "Permissoes");

            migrationBuilder.DropColumn(
                name: "SistemaProtegido",
                table: "Cargos");

            migrationBuilder.DropColumn(
                name: "Versao",
                table: "Cargos");
        }
    }
}
