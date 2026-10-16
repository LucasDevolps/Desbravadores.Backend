# ADR-0011: Armazenar fotos de usuários em disco fora do site e servi-las pela API

## Status

Accepted. Implementado na issue #116.

## Contexto

A tela de usuários passou a ter foto de perfil (issue #116). A maior parte dos usuários são desbravadores,
"jovem de 10 a 15 anos" pela descrição do cargo `DS` em [`DbSeeder.cs`](../../../backend/Almirante.Api/Data/DbSeeder.cs):
são fotos de crianças e adolescentes, dado pessoal com tratamento reforçado (LGPD, art. 14).

Forças em tensão:

- **onde guardar:** a #116 sugeria object storage (S3, Azure Blob, MinIO) com o disco "apenas como alternativa de
  desenvolvimento" e uma tabela de metadados; o mantenedor pediu, ao planejar a implementação, uma pasta no próprio
  servidor e só o caminho no banco;
- **deploy:** o job `deploy-windows` de [`backend-deploy.yml`](../../../.github/workflows/backend-deploy.yml) faz
  `robocopy /MIR` em `C:\desbravadores\site` (apaga o que não veio do publish), e o deploy Linux recria o contêiner a
  cada push em `develop`;
- **acesso:** a API autentica por Bearer ([ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md)),
  então uma `<img src>` comum não envia credencial;
- **entrada não confiável:** decodificar imagem enviada por usuário é superfície de ataque conhecida (decompression
  bomb, falhas de memória em decoders, arquivos poliglotas, metadados com localização);
- **licença da biblioteca:** a SixLabors.ImageSharp 4.x passou a validar uma chave de licença no build: o
  `build/SixLabors.ImageSharp.targets` do pacote 4.1.3 roda `ValidateLicenseTask` antes do `CoreCompile`, com
  `ContinueOnError` só em Debug. Sem a chave falhariam todos os builds Release (CI, Docker, deploy Windows, release no
  GHCR) e os PRs do Dependabot, que não recebem os secrets do repositório.

## Decisão

- **Disco local atrás de uma abstração.** O arquivo vai para a pasta `Media:Storage:Path`
  ([`MediaOptions.cs`](../../../backend/Almirante.Api/Options/MediaOptions.cs)) por meio de
  [`IFileStorage`](../../../backend/Almirante.Api/Infrastructure/IFileStorage.cs), implementada em disco por
  [`LocalFileStorage`](../../../backend/Almirante.Api/Infrastructure/LocalFileStorage.cs). A pasta é obrigatória fora
  de Development, absoluta, fora do site (e sem contê-lo) e gravável — conferido no startup do host
  (`MediaOptionsValidator`) e, no Windows, também no preflight do deploy.
- **Banco só com o caminho.** `Usuarios.FotoCaminho varchar(79)` guarda o caminho RELATIVO
  `usuarios/{id}/{aleatório}.webp` ([`FotoChave.cs`](../../../backend/Almirante.Api/Services/FotoChave.cs)); a CHECK
  `CK_Usuarios_FotoCaminho` exige esse formato e a pasta do próprio usuário. Troca e remoção passam pelo trigger de
  histórico ([ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md)).
- **Entrega só pela API.** Nenhum servidor web publica a pasta. `GET /api/Usuarios/{id}/foto` exige a mesma policy do
  CRUD de usuários e responde com `ETag`, `Cache-Control: private, no-cache` e `Cross-Origin-Resource-Policy:
  same-origin`. O Angular busca com `HttpClient` (blob) e exibe com `URL.createObjectURL`.
- **Só pixels chegam ao disco.** [`ProcessadorFotos`](../../../backend/Almirante.Api/Services/ProcessadorFotos.cs)
  aceita só JPEG, PNG e WebP (assinatura real igual ao tipo declarado), lê o cabeçalho antes de decodificar (limite de
  megapixels), decodifica num bitmap de tamanho já limitado, aplica a orientação EXIF, reduz para no máximo 1024x1024 e
  regrava em WebP sem nenhum metadado. A saída é conferida de novo. O original nunca é gravado (nem como temporário:
  [`LeitorUploadFoto`](../../../backend/Almirante.Api/Infrastructure/LeitorUploadFoto.cs) lê o multipart em streaming).
- **SkiaSharp (MIT)** como biblioteca de imagem, com o binário nativo do Linux pelo pacote
  `SkiaSharp.NativeAssets.Linux.NoDependencies`.

Fora do escopo: object storage, foto enviada pelo próprio usuário (rota `/me`), antivírus e redimensionamento no cliente.

## Consequências

### Positivas

- Deploy não apaga fotos (pasta fora do site e volume nomeado `almirante-fotos` no Compose), e um host sem a pasta
  configurada falha antes de sair do ar.
- Foto de menor nunca fica acessível por URL pública; o histórico mostra quem trocou ou removeu cada foto.
- Banco e backups não crescem com binários; trocar o disco por object storage é uma nova implementação de `IFileStorage`.
- EXIF com GPS, perfis de cor, conteúdo anexado e metadados escondidos não sobrevivem ao reencode.
- Sem chave de licença para manter: builds de CI, Docker, deploy e Dependabot continuam como estão.

### Negativas / trade-offs

- Decoders nativos (C/C++) têm uma classe de falha mais grave que código gerenciado (ex.: `GHSA-j7hp-h8jx-5ppr`, o
  libwebp de 2023). Mitigações: só três formatos chegam ao Skia, o cabeçalho é validado antes, o upload é limitado a
  10 MB e a usuários da diretoria autenticados, e a biblioteca é acompanhada pelo Dependabot.
- A decodificação nativa não é interrompível: o prazo (`ProcessingTimeoutMs`) é conferido entre as etapas, e os
  limites de tamanho mantêm cada etapa curta; no máximo `MaxConcurrentProcessing` imagens são processadas ao mesmo tempo.
- O SkiaSharp traz o binário nativo de todas as plataformas (e, no Windows, o `libSkiaSharp.pdb`, ~90 MB por
  arquitetura): um publish sem RID passaria de 490 MB. A imagem publica só a arquitetura dela (`-a $TARGETARCH` no
  [`Dockerfile`](../../../backend/Almirante.Api/Dockerfile)), o deploy Windows publica `win-x64` e o PDB nativo fica fora
  do build (`SemPdbNativoDoSkia` no `.csproj`): imagem com 112 MB, site do IIS com 33 MB.
- Em mais de uma instância da API, a pasta precisa ser compartilhada (SMB/NFS) ou trocada por object storage.
- A foto não é exibida por `<img src>` direto: o front precisa do passo `HttpClient` + blob.

## Alternativas consideradas

- **Object storage (S3, Azure Blob, MinIO)**, como sugerido na #116: adiado. O deploy atual é de um servidor só por
  ambiente, e o mantenedor pediu a pasta no servidor; a interface `IFileStorage` deixa a troca para depois.
- **Tabela de metadados (`UsuarioMidia`)**, sugerida na #116: substituída por uma coluna com o caminho, como pedido; o
  trigger já registra quem alterou e quando.
- **Servir a pasta como conteúdo estático do IIS:** descartado. Exporia fotos de menores a quem tivesse a URL e, dentro
  do site, seria apagada pelo `robocopy /MIR`.
- **Binário no SQL Server:** descartado pela #116 (crescimento do banco e dos backups).
- **SixLabors.ImageSharp 4.1.3** (gerenciada): exigiria cadastrar uma chave comunitária e configurá-la em todos os
  builds Release; a 3.x não tem a chave, mas tem advisories altos sem correção na linha 3 (`GHSA-jjfr-hcj7-qf5w`,
  `GHSA-j9gm-c75j-xc9q`, `GHSA-j3p4-wp97-rph4`), que reprovariam o gate do Trivy.

## Evidências no repositório

- [`ProcessadorFotos.cs`](../../../backend/Almirante.Api/Services/ProcessadorFotos.cs), [`UsuarioFotoService.cs`](../../../backend/Almirante.Api/Services/UsuarioFotoService.cs),
  [`LocalFileStorage.cs`](../../../backend/Almirante.Api/Infrastructure/LocalFileStorage.cs), [`FotosCleanupService.cs`](../../../backend/Almirante.Api/Services/FotosCleanupService.cs).
- Migration `20261016103555_AddUsuariosResponsavelEFoto` (coluna, CHECK e trigger).
- [`nginx.conf`](../../../nginx/nginx.conf), [`nginx.tls.conf`](../../../nginx/nginx.tls.conf),
  [`nginx.windows.conf`](../../../nginx/nginx.windows.conf): `client_max_body_size 11m` só na rota da foto.
- Testes: [`ProcessadorFotosTests.cs`](../../../backend/Almirante.Api.Tests/ProcessadorFotosTests.cs),
  [`UsuariosFotoSqlServerTests.cs`](../../../backend/Almirante.Api.Tests/UsuariosFotoSqlServerTests.cs),
  [`ApiProcessUsuariosFotoTests.cs`](../../../backend/Almirante.Api.Tests/ApiProcessUsuariosFotoTests.cs).

## Decisões relacionadas

- [ADR-0001](0001-usar-jwt-com-sessoes-persistidas-e-refresh-rotativo.md) (autenticação por Bearer).
- [ADR-0004](0004-auditar-exclusoes-logicas-com-trigger-e-session-context.md) (histórico por trigger).
- [ADR-0009](0009-aplicar-migrations-na-inicializacao-da-api.md) (a migration roda no startup).
