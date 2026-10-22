# ADR-0013: Separar as permissões por perfil, com hierarquia de cargos e autoatendimento

## Status

Accepted. Implementado na issue #127.

## Contexto

Até a issue #127, as policies `GestaoFinanceira` e `GestaoCadastros` exigiam o mesmo conjunto de cargos
(`Roles.Diretoria` = ADM, DIR, DIRA, SEC, TES, em `Program.cs` do commit `504cda5`). Na prática, o
Secretário operava lançamentos e pagamentos, o Tesoureiro gerenciava usuários e cargos, e o
Desbravador recebia notificações com `eventoId` sem conseguir consultar o evento nem atualizar os
próprios dados. A própria documentação registrava a separação como "decisão a confirmar com o negócio"
([`authentication-security.md`](../../authentication-security.md), versão anterior).

Forças em tensão:

- a issue pede acesso compatível com a responsabilidade de cada perfil, com o diretor podendo tudo;
- criar, editar e excluir um evento gera, sincroniza e desativa as cobranças dos participantes na mesma
  transação, com idempotência, `rowversion`, lock do passeio e auditoria
  ([`EventosService`](../../../src/Almirante.Api/Services/EventosService.cs));
- quem gerencia cadastros também atribui cargos: sem um limite, separar cadastros do financeiro não
  impediria o Secretário de se promover a Tesoureiro ou Diretor;
- a regra técnica de que só ADM gerencia usuários ADM (`UsuariosService.ExigirPermissaoSobreCargo`)
  precisava ser preservada.

## Decisão

- Os conjuntos de cargos e as policies ficam centralizados em
  [`Security/Roles.cs`](../../../src/Almirante.Api/Security/Roles.cs) (`Policies.Registrar`):
  `GestaoCadastros` (ADM, DIR, DIRA, SEC), `GestaoFinanceira` (ADM, DIR, DIRA, TES), `GestaoEventos`,
  `ConsultaMembros` e `Painel` (ADM, DIR, DIRA, SEC, TES) e `Autoatendimento` (qualquer cargo ativo).
- Eventos continuam uma operação só (cadastro + cobranças) sob `GestaoEventos`; registrar ou corrigir
  pagamentos fica só em `/api/Lancamentos` (`GestaoFinanceira`).
- O dashboard usa `Painel`; o bloco `financeiro` só é calculado para quem também passa em
  `GestaoFinanceira` (para os demais, `null`).
- `Roles.PodeGerenciar` define a hierarquia usada no cadastro, na edição, na exclusão e nas fotos de
  terceiros: ADM só por ADM; cargos de gestão (DIR, DIRA, SEC, TES) só pela diretoria (ADM, DIR, DIRA);
  os demais por quem tem `GestaoCadastros`.
- O autoatendimento (`/api/Me`) identifica o usuário só pelo token, aceita apenas os dados pessoais em
  um DTO fechado (`JsonUnmappedMemberHandling.Disallow`) e reutiliza o mesmo serviço, validação,
  auditoria e armazenamento de fotos do cadastro administrativo. Eventos do participante têm uma
  representação própria, sem dados de outros participantes.
- O Tesoureiro identifica membros por `GET /api/Membros` (id, nome, cargo), sem acesso ao cadastro completo.

Fora do escopo: patrimônios, especialidades, relatórios da secretaria, troca de senha pelo usuário e
consulta dos próprios débitos (pendências registradas na documentação).

## Consequências

### Positivas

- Cada perfil recebe só os módulos e indicadores de sua responsabilidade, verificado no backend e por
  testes endpoint × perfil.
- Nenhum perfil consegue conceder a si ou a outra conta mais permissões do que tem.
- O desbravador (e qualquer membro) mantém os próprios dados e consulta os eventos de que participa sem
  ganhar acesso administrativo.

### Negativas / trade-offs

- Mudança incompatível para clientes que usavam SEC no financeiro ou TES no cadastro (descrita no
  README).
- O Secretário vê, na visão de gestão do evento, o status das cobranças de cada participante (necessário
  para entender os `409` de edição); não vê valores consolidados nem lista lançamentos.
- O Secretário não altera usuários com cargo de gestão, nem o próprio cadastro pela rota administrativa
  (usa `/api/Me`).

## Alternativas consideradas

- Manter as duas policies com o mesmo conjunto (estado anterior, `504cda5`).
- Separar a criação de eventos das cobranças: descartada por quebrar a atomicidade e a idempotência do
  fluxo existente.

## Evidências no repositório

- [`Roles.cs`](../../../src/Almirante.Api/Security/Roles.cs): `Roles`, `PodeGerenciar`, `Policies.Registrar`.
- [`MeController`](../../../src/Almirante.Api/Controllers/MeController.cs),
  [`MembrosController`](../../../src/Almirante.Api/Controllers/MembrosController.cs),
  [`FotoUsuarioControllerBase`](../../../src/Almirante.Api/Controllers/FotoUsuarioControllerBase.cs).
- [`UsuariosService.AtualizarMeusDadosAsync`](../../../src/Almirante.Api/Services/UsuariosService.cs),
  [`EventosService.ListarDoParticipanteAsync`](../../../src/Almirante.Api/Services/EventosService.cs),
  [`DashboardService`](../../../src/Almirante.Api/Services/DashboardService.cs).
- Testes: [`AuthorizationTests`](../../../src/Almirante.Api.Tests/AuthorizationTests.cs),
  [`PermissoesUnitTests`](../../../src/Almirante.Api.Tests/PermissoesUnitTests.cs),
  [`AutoatendimentoSqlServerTests`](../../../src/Almirante.Api.Tests/AutoatendimentoSqlServerTests.cs),
  [`EventosParticipanteSqlServerTests`](../../../src/Almirante.Api.Tests/EventosParticipanteSqlServerTests.cs).
- Documentação: [`authentication-security.md`](../../authentication-security.md#perfis-e-matriz-de-acesso-31-127).

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md): validação do cargo a cada requisição.
- [ADR-0011](0011-armazenar-fotos-em-disco-fora-do-site-e-servir-pela-api.md): fotos reutilizadas no autoatendimento.
- [ADR-0012](0012-notificar-com-outbox-na-propria-linha-e-signalr.md): `eventoId` das notificações.
