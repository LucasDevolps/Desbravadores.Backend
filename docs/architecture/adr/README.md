# Architecture Decision Records (ADRs)

Um ADR registra uma decisão arquitetural: o contexto que levou a ela, o que foi decidido e as
consequências. Ele responde "por que o sistema é assim?". Já os documentos C4 respondem "como o
sistema está estruturado hoje?" ([voltar à documentação de arquitetura](../README.md)).

## Índice

| ADR | Decisão | Status |
| --- | --- | --- |
| [0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md) | Usar JWT de curta duração com sessões persistidas e refresh token rotativo | Accepted |
| [0002](0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md) | Usar Nginx como reverse proxy e único ponto de entrada HTTP dos deploys | Accepted |
| [0003](0003-exigir-idempotency-key-nas-criacoes-em-lote.md) | Exigir Idempotency-Key persistida nas criações que geram lançamentos em lote | Accepted |
| [0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md) | Auditar exclusões lógicas com trigger no SQL Server e contexto via `SESSION_CONTEXT` | Accepted |
| [0005](0005-separar-identidades-sql-e-rotacionar-credenciais.md) | Separar identidades SQL da API e rotacionar a credencial de runtime | Accepted |
| [0006](0006-controlar-concorrencia-com-rowversion.md) | Controlar concorrência em eventos e sessões com `rowversion` | Accepted |
| [0007](0007-testar-integracao-com-sql-server-real-no-ci.md) | Testar com SQL Server real, descartável, no CI | Accepted |
| [0008](0008-usar-aspire-no-desenvolvimento-e-opentelemetry-na-api.md) | Usar .NET Aspire para desenvolvimento local e ServiceDefaults com OpenTelemetry na API | Accepted |
| [0009](0009-aplicar-migrations-na-inicializacao-da-api.md) | Aplicar as migrations do EF Core na inicialização da API | Accepted |

Template: [`0000-template.md`](0000-template.md).

## Registros retroativos

Os ADRs 0001 a 0009 foram escritos em 2026-09-23, na issue #69, para registrar decisões que já estavam
implementadas no commit `57f24dd` da `main`. Eles não indicam que houve uma aprovação formal antes da
implementação: o status `Accepted` reflete que a decisão está em vigor no código.

Regras seguidas nesses registros:

- toda afirmação sobre o sistema aponta para código, configuração, teste, workflow, documento, commit
  ou PR;
- motivação histórica só aparece quando está escrita em um PR, issue, commit ou documento, e a fonte é
  citada;
- conclusões que decorrem do código, mas não estão escritas em lugar algum, começam com "Inferido:";
- quando não há registro de alternativas avaliadas, o ADR diz isso em vez de criá-las;
- o que não pôde ser determinado fica em "Questões em aberto".

## Quando criar um ADR

Crie um ADR quando a decisão:

- muda a estrutura do sistema, um contrato externo (API, banco, deploy) ou um atributo de qualidade
  (segurança, consistência, operação);
- é cara ou arriscada de reverter;
- precisa ser entendida por quem não participou dela.

Não crie ADR para escolhas pequenas e locais, já cobertas por padrão ou lint, nem para soluções
temporárias ou provas de conceito.

## Como criar um ADR

1. Copie [`0000-template.md`](0000-template.md) para `NNNN-titulo.md`, com o próximo número livre.
2. Preencha as seções. Use `Proposed` enquanto a decisão não estiver aceita.
3. Adicione a linha no índice acima.
4. Abra o PR junto com a mudança que implementa a decisão, ou antes dela, para discussão. Se a decisão
   mudar a estrutura descrita nos diagramas, atualize também [`c4-context.md`](../c4-context.md) ou
   [`c4-container.md`](../c4-container.md) no mesmo PR.

## Numeração e nomes

- Números sequenciais com quatro dígitos (`0001`, `0002`...). Um número nunca é reutilizado, nem
  quando o ADR é descontinuado ou substituído.
- `0000` é reservado para o template.
- Nome do arquivo: `NNNN-` seguido de verbo no infinitivo e assunto, em minúsculas, sem acentos,
  separado por hífens. Exemplo: `0002-usar-nginx-como-reverse-proxy-e-ponto-de-entrada.md`.
- Título interno: `# ADR-NNNN: Título`, com verbo no infinitivo.

## Status

| Status | Significado |
| --- | --- |
| `Proposed` | Em discussão; ainda não descreve o sistema. |
| `Accepted` | Em vigor: o código e a infraestrutura seguem a decisão. |
| `Deprecated` | Deixou de valer sem ser substituída por outra decisão. |
| `Superseded by ADR-NNNN` | Substituída pela decisão indicada. |

## Alterar ou substituir uma decisão

Um ADR aceito é um registro histórico. Não reescreva o contexto, a decisão ou as justificativas
depois que ele foi aceito.

- **Correção sem mudança de decisão** (link quebrado, caminho de arquivo que mudou, erro de digitação):
  pode ser feita no próprio ADR.
- **Nova informação sobre a mesma decisão**: acrescente uma nota datada ao final, sem apagar o texto
  anterior.
- **Mudança de decisão**: crie um ADR novo que descreva a nova decisão. No ADR antigo, mude apenas o
  status para `Superseded by [ADR-NNNN](NNNN-titulo.md)`. No novo, cite o ADR que ele substitui em
  "Decisões relacionadas".
- **Decisão abandonada sem substituta**: mude o status para `Deprecated` e acrescente uma nota
  explicando o motivo.
