# Política de segurança

Este documento descreve como reportar vulnerabilidades do backend **Almirante**
(`Desbravadores.Backend`) e como esses reportes são tratados.

## Versões suportadas

O projeto publica releases com Semantic Versioning (tags `vMAJOR.MINOR.PATCH` e imagem da API em
`ghcr.io/lucasdevolps/almirante-api`), conforme [`docs/release-process.md`](docs/release-process.md).
Enquanto estiver em `0.x`, **apenas a release mais recente** recebe correções de segurança. Não há
backport para versões anteriores, nem suporte de longo prazo (LTS):

| Versão / branch | Suporte |
|---|---|
| Release mais recente (`vX.Y.Z`) | Sim |
| Releases anteriores | Não: atualize para a mais recente |
| `main` | Sim, é a origem das releases |
| `develop` | Sim, com integração ativa e a próxima versão |
| Demais branches (`feature/*`, `fix/*`, branches antigas) | Não garantido |

Correções entram primeiro em `develop`, chegam a `main` pelo fluxo normal de Pull Request descrito em
[`CONTRIBUTING.md`](CONTRIBUTING.md) e são entregues numa **nova versão PATCH**. Uma release já
publicada não é alterada nem republicada.

## Como reportar uma vulnerabilidade

**Não abra uma issue pública, Pull Request ou discussão com detalhes de uma vulnerabilidade
sensível ou explorável ainda não corrigida.**

1. Se a aba **Security** do repositório oferecer a opção **Report a vulnerability**
   (GitHub Private Vulnerability Reporting / Security Advisories), use-a. O reporte fica visível
   apenas para os mantenedores.
2. Se essa opção não estiver disponível, procure um canal privado com o mantenedor
   ([@LucasDevolps](https://github.com/LucasDevolps)) antes de qualquer divulgação. Caso seja
   necessário abrir uma issue pública para pedir esse contato, escreva apenas que deseja reportar
   um problema de segurança, **sem descrever a vulnerabilidade**.

Melhorias preventivas que podem ser discutidas abertamente (endurecer configuração, reduzir
privilégios, ampliar testes de segurança) podem usar o template público
**Melhoria de segurança** das issues.

## O que incluir no reporte

Sempre que possível, informe:

- descrição da vulnerabilidade;
- componente afetado (endpoint, arquivo, configuração, workflow);
- passos para reprodução;
- pré-condições (autenticação necessária, cargo, configuração específica);
- impacto potencial;
- versão (`vX.Y.Z`), branch ou commit afetados;
- evidências relevantes (requisições e respostas **sanitizadas**, trechos de log sem dados reais);
- possível mitigação, se conhecida.

**Não envie** dados reais, mesmo em canal privado:

- senhas, tokens JWT, refresh tokens ou cookies de sessão;
- connection strings, credenciais SQL (incluindo a do `sa`) ou outras credenciais operacionais;
- chaves de assinatura JWT, certificados ou chaves privadas;
- dumps de banco ou arquivos com dados pessoais;
- endereços IP ou nomes de host internos da infraestrutura.

Use valores fictícios ou mascarados (`<token>`, `<senha>`, `10.x.x.x`). Se um segredo real tiver
sido exposto, informe apenas **que** ele foi exposto e onde, para que seja rotacionado.

## Processo de tratamento

```text
recebimento
   ↓
triagem
   ↓
confirmação / reprodução
   ↓
classificação do impacto
   ↓
desenvolvimento da correção
   ↓
validação e testes (incluindo regressão)
   ↓
correção em develop → main
   ↓
nova release (PATCH)
   ↓
divulgação responsável, quando aplicável
```

- O projeto é mantido individualmente. Não há prazo formal de resposta ou de correção; os
  reportes são tratados com prioridade proporcional ao impacto.
- Quem reportou é mantido informado sobre a confirmação e a correção pelo mesmo canal privado.
- Detalhes técnicos só são publicados depois que a correção estiver disponível numa release e nas
  branches suportadas e, quando aplicável, depois da rotação de segredos afetados.

Decisões de segurança já adotadas pela aplicação estão em
[`docs/authentication-security.md`](docs/authentication-security.md).
