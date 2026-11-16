# FIAP Cloud Games - CatalogAPI

## Execução integrada da Fase 3

O [README central de orquestração](https://github.com/arthuurqueirozz/tech-challenge-3-orchestration)
contém o deploy completo no Kind, configuração AWS, acesso pelo Kong, dashboard,
teste integrado e encerramento. Use esse guia para reproduzir a entrega.
Arquivos de Compose/Kubernetes e comandos individuais preservados neste
repositório servem a desenvolvimento e histórico; a stack final é mantida na
orquestração e não executa a NotificationsAPI antiga.

Código da Fase 3 na branch `fase-3`; baseline preservada em `fase-2-final`.

## Métricas da Fase 3

`GET /metrics` expõe Prometheus (prometheus-net.AspNetCore 8.2.1).
`fcg_http_requests_total` conta requisições de negócio; o histograma
`fcg_http_request_duration_seconds` mede sua duração. Labels: `route` com template
do endpoint (ou `unmatched`), `method` com verbos conhecidos/`OTHER` e `status_code`.
GUIDs, usuários, tokens e query strings não viram labels. Somente `/api` entra
nessas métricas; health e scraping ficam fora. Exceções tratadas mantêm seu status
HTTP final, inclusive 500, e o tempo observado inclui o processamento da resposta.

Dashboard e manifests no [guia da etapa 6](https://github.com/arthuurqueirozz/tech-challenge-3-orchestration/blob/main/docs/ETAPA-6.md).
O scraper acrescenta `job=catalog-api`. `/metrics` permanece interno ao cluster;
o gateway final não deve expor essa rota. Contadores reiniciam com o processo;
taxas usam `rate`. Gate: 30 testes CatalogAPI e 33 verificações integradas da stack
de métricas passaram.

Microsserviço de catálogo evoluído para a Fase 3 do Tech Challenge FIAP.
Mantém CRUD, compra e biblioteca, acrescentando Redis às consultas públicas.
A baseline da Fase 2 está preservada na tag `fase-2-final`.

## Responsabilidades

- Expor consulta publica de jogos.
- Restringir criacao, atualizacao e exclusao logica de jogos ao perfil `Admin`.
- Validar localmente o JWT emitido pela UsersAPI, sem chamada HTTP entre
  microsservicos.
- Publicar `OrderPlacedEvent` no RabbitMQ ao solicitar compra.
- Consumir `PaymentProcessedEvent` e adicionar o jogo a biblioteca somente
  quando o status for `Approved`.
- Expor a biblioteca do usuario autenticado.

## Endpoints

- `GET /api/games`: lista jogos ativos.
- `GET /api/games/{id}`: consulta um jogo ativo.
- `POST /api/games`: cria jogo, exige JWT com role `Admin`.
- `PUT /api/games/{id}`: atualiza jogo ativo, exige JWT com role `Admin`.
- `DELETE /api/games/{id}`: desativa jogo, exige JWT com role `Admin`.
- `GET /api/me/library/games`: lista biblioteca do usuario autenticado.
- `POST /api/me/library/games/{gameId}`: solicita compra do jogo autenticado.
- `GET /health`: SQL Server/RabbitMQ obrigatórios; Redis indisponível retorna `Degraded` com HTTP 200.
- `GET /health/live`: liveness do processo.

## Variaveis de ambiente

| Nome | Descricao |
|---|---|
| `ConnectionStrings__CatalogDatabase` | Connection string exclusiva da CatalogAPI. |
| `Jwt__Issuer` | Issuer esperado no JWT. Deve bater com a UsersAPI. |
| `Jwt__Audience` | Audience esperada no JWT. Deve bater com a UsersAPI. |
| `Jwt__Key` | Chave simetrica usada para validar o JWT. Deve ser a mesma da UsersAPI. |
| `RabbitMq__Host` | Host ou Service do RabbitMQ. |
| `RabbitMq__Port` | Porta AMQP do RabbitMQ. |
| `RabbitMq__VirtualHost` | Virtual host do RabbitMQ. |
| `RabbitMq__Username` | Usuario do RabbitMQ. |
| `RabbitMq__Password` | Senha do RabbitMQ. |
| `RabbitMq__PaymentProcessedQueue` | Fila do consumidor de resultado de pagamento. |
| `CatalogCache__Configuration` | Conexão Redis, padrão `localhost:6379`; use Secret se incluir senha. |
| `CatalogCache__TtlSeconds` | TTL absoluto: padrão 60 segundos, permitido 1–300. |
| `CatalogCache__TimeoutMilliseconds` | Timeout Redis: padrão 500 ms, permitido 100–2000. |

## Cache distribuído da Fase 3

`CachedGameCatalogService` usa `IDistributedCache` com
`Microsoft.Extensions.Caching.StackExchangeRedis` 10.0.14. Reaproveita
`GameCatalogService` para SQL e mutações, preservando o contrato HTTP.

| Consulta pública | Chave Redis |
|---|---|
| Lista de jogos ativos, ordenada por título | `fcg:catalog:v1:games:active:title-asc` |
| Detalhe de jogo ativo | `fcg:catalog:v1:game:{id}` com GUID canônico |

As rotas atuais não aceitam filtros/paginação que alterem a resposta. Se forem
adicionados, as chaves devem incluir esses parâmetros. Cache não depende de JWT;
compra e biblioteca continuam usando SQL diretamente. Respostas 404 não são
armazenadas. Cache miss consulta SQL e grava JSON; hit evita essa consulta.
TTL é absoluto, contado desde antes da consulta SQL, e não é renovado por hits.

Após create/update/delete confirmado no SQL, remove a lista e o detalhe afetado.
Falhas da mutação não invalidam. Ambas as remoções são tentadas mesmo se uma
falhar ou se o cliente cancelar depois do commit. Não há transação SQL/Redis:
invalidação malsucedida ou leitura concorrente à alteração pode manter dado
antigo até o TTL. O cache é descartável e não deve ser usado para decidir preços
de compra ou invariantes; esses caminhos continuam consultando SQL.

Falhas Redis em leitura/gravação/invalidação são registradas sem connection
strings; leituras retornam SQL e escritas confirmadas não viram erro do negócio.
Payload JSON inválido é recarregado. Cancelamento do cliente continua respeitado
nas leituras. A conexão não bloqueia startup, usa fail-fast sem fila de comandos
durante desconexão e tenta reconectar automaticamente. Timeouts são limites do
cliente Redis, sujeitos ao agendamento/heartbeat, não SLA HTTP rígido.

`/health` retorna `Healthy` quando tudo funciona e `Degraded`/200 com Redis fora;
falha SQL/RabbitMQ continua `Unhealthy`/503. `/health/live` independe de serviços.
Redis não deve ser condição obrigatória de startup/readiness no ambiente final.

Validação: 29 testes locais (19 herdados e 10 de cache), incluindo contagem de
SELECTs por interceptor EF/SQLite. Ensaio com SQL Server e Redis reais passou
em 21 verificações, com zero SELECTs nos hits e recuperação após indisponibilidade.
[Guia e evidências da etapa 5](https://github.com/arthuurqueirozz/tech-challenge-3-orchestration/blob/main/docs/ETAPA-5.md).

## Desenvolvimento local

Requisitos:

- SDK .NET 10;
- SQL Server;
- RabbitMQ;
- Redis (a API opera degradada se ele estiver indisponível).

Comandos:

```bash
dotnet restore TechChallenge.Catalog.sln
dotnet run --project tests/FCG.Catalog.Tests/FCG.Catalog.Tests.csproj --configuration Release
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj --no-launch-profile -- --migrate
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

`appsettings.Development.json` nao contem segredos. Em desenvolvimento, use
variaveis de ambiente ou `dotnet user-secrets`.

Opcao 1 - variaveis de ambiente:

```bash
export ConnectionStrings__CatalogDatabase="$FCG_CATALOG_CONNECTION_STRING"
export Jwt__Key='<same-32-byte-or-longer-key-used-by-users-api>'
export RabbitMq__Username='<rabbitmq-username>'
export RabbitMq__Password='<rabbitmq-password>'
export CatalogCache__Configuration='localhost:6379'
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj --no-launch-profile -- --migrate
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

Opcao 2 - user-secrets:

```bash
dotnet user-secrets set 'ConnectionStrings:CatalogDatabase' "$FCG_CATALOG_CONNECTION_STRING" --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'Jwt:Key' '<same-32-byte-or-longer-key-used-by-users-api>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'RabbitMq:Username' '<rabbitmq-username>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'RabbitMq:Password' '<rabbitmq-password>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj --no-launch-profile -- --migrate --environment Development
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

Para comandos de migration com `dotnet ef`, forneca
`ConnectionStrings__CatalogDatabase` como variavel de ambiente.

## Migrations administrativas — Fase 4

`--migrate` aplica somente as migrations SQL e termina com código 0 em sucesso;
falhas retornam código diferente de zero. A flag é removida antes de configurar
o host. Esse modo exige apenas `ConnectionStrings__CatalogDatabase`: não exige
JWT, RabbitMQ, Redis, configuração de busca ou credenciais AWS e não inicia HTTP,
consumidores ou workers. Na imagem Docker, acrescente `--migrate` depois do nome
da imagem, sem publicar porta.

O startup web não aplica migrations. Execute `--migrate` antes do primeiro
startup e de cada rollout, usando a mesma imagem da API em um Job separado.
Migrations devem preservar a compatibilidade com os pods antigos; não apague
dados nem reverta automaticamente o schema ao voltar uma imagem. O check
`catalog-sqlserver` exige conexão e ausência de migrations conhecidas pendentes.
Banco existente vazio deixa `/health` em 503 sem alterar o schema; migrations
adicionais de uma versão mais nova não reprovam esse check. `/health/live`
continua indicando apenas o processo; a busca permanece fora dos health checks.

`--search-bootstrap` continua sendo o comando separado de preparação/reconstrução
da busca, incluindo suas migrations SQL, mapping e pendências. Escolha apenas um
modo administrativo por execução: combiná-lo com `--migrate` falha antes de
configurar o host ou acessar dependências. As condições de uso do bootstrap
permanecem descritas abaixo.

## Docker

```bash
docker build -t tech-challenge-2-catalog-api:latest .
docker run --rm -p 8083:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__CatalogDatabase="$FCG_CATALOG_CONNECTION_STRING" \
  -e Jwt__Issuer=FCG \
  -e Jwt__Audience=FCG \
  -e Jwt__Key="<same-32-byte-or-longer-key-used-by-users-api>" \
  -e RabbitMq__Host=host.docker.internal \
  -e RabbitMq__Port=5672 \
  -e RabbitMq__VirtualHost=/ \
  -e RabbitMq__Username="<rabbitmq-username>" \
  -e RabbitMq__Password="<rabbitmq-password>" \
  -e RabbitMq__PaymentProcessedQueue=catalog-payment-processed \
  -e CatalogCache__Configuration=host.docker.internal:6379 \
  -e Search__Endpoint="https://<opensearch-domain>" \
  -e Search__Region=us-east-2 \
  -e Search__UseAwsAuth=true \
  tech-challenge-2-catalog-api:latest
```

## Orquestração e Kubernetes

O guia da Fase 3 fica em [tech-challenge-3-orchestration](https://github.com/arthuurqueirozz/tech-challenge-3-orchestration).
O ensaio do cache usa compose.stage5.yaml desse repositório e não exige AWS.
Os manifests individuais em k8s receberam configuração de Redis; a stack final
Kind/Kong/monitoramento ainda será consolidada na orquestração.

Os arquivos docker-compose.yml, k8s/all e scripts integrados herdados nesta
pasta representam a Fase 2. Para reproduzir aquela versão, consulte a tag
fase-2-final de todos os projetos. Não são o guia de subida da Fase 3.

## Contratos de eventos

Namespace: `FCG.IntegrationEvents.V1`.

`OrderPlacedEvent`:

- `OrderId`
- `OccurredAtUtc`
- `UserId`
- `UserEmail`
- `GameId`
- `Price`

`PaymentProcessedEvent`:

- `OrderId`
- `ProcessedAtUtc`
- `UserId`
- `UserEmail`
- `GameId`
- `Price`
- `Status`: `Approved` ou `Rejected`

## Busca — Fase 4

`GET /search?q=termo` é público e devolve um array `GameResponse`, com até 20
jogos ativos, em ordem de relevância e desempate por identificador. A busca usa
`multi_match`, título com peso 3 e fuzzy `AUTO` em título, descrição e desenvolvedor.
Não há paginação nem fallback SQL. Ausência de `q` ou texto em branco retorna
`400`; nenhum resultado retorna `200 []`; indisponibilidade, timeout ou índice
ausente retorna `503` com mensagem recuperável. A atualização é eventual; CRUD,
cache e health checks não dependem da disponibilidade do motor.

| Variável | Padrão / regra |
| --- | --- |
| `Search__Endpoint` | Obrigatório; origem HTTP(S), sem caminho, query ou credenciais |
| `Search__IndexName` | `catalog-games-v1`; nome em minúsculas |
| `Search__Region` | Obrigatória com autenticação AWS |
| `Search__UseAwsAuth` | `true`; usa SigV4, serviço `es` e cadeia padrão do AWS SDK |
| `Search__RequestTimeoutSeconds` | `5`; entre 1 e 60 |
| `Search__BatchSize` | `20`; entre 1 e 100 |
| `Search__PollIntervalSeconds` | `2`; entre 1 e 60 |
| `Search__MaxRetryDelaySeconds` | `60`; entre 1 e 3600 |

Fora de `Development`, HTTPS e autenticação AWS são obrigatórios. Em EKS,
configurar identidade do pod; não montar perfil local nem fixar chaves no código.
O cliente oficial singleton usa `OpenSearch.Client` e
`OpenSearch.Net.Auth.AwsSigV4` 2.3.0. Os locks NuGet são versionados; restaurar com
`dotnet restore TechChallenge.Catalog.sln --locked-mode`.

Para o motor local isolado, configure explicitamente `Development` e autenticação
desligada. Exemplo PowerShell, a partir desta pasta, com SQL já configurado por
`ConnectionStrings__CatalogDatabase` ou user secrets:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:Search__Endpoint = 'http://localhost:9200'
$env:Search__UseAwsAuth = 'false'
$env:Search__IndexName = 'catalog-games-v1'
dotnet run --project src/FCG.Catalog.Api --configuration Release --no-launch-profile -- --search-bootstrap
dotnet run --project src/FCG.Catalog.Api --configuration Release --no-launch-profile
```

O modo `--search-bootstrap` remove a flag antes de configurar o host, aplica as
migrations SQL, cria o índice ausente ou verifica seu mapping e enfileira o estado
canônico de todos os jogos, inclusive inativos. O comando termina sem iniciar
HTTP, RabbitMQ ou workers. Ele não entrega os documentos ao motor: o worker da
aplicação faz isso após a inicialização normal. Repetir o bootstrap conserva a
revisão e o payload; novas pendências permitem reconstruir um índice perdido.
Mapping incompatível interrompe o comando e requer intervenção; nunca há drop ou
recriação automática. O mapping camelCase estrito tem versão 1 em `_meta`, ID
keyword, textos, preço `scaled_float` com fator 100, ativo boolean e datas UTC.

Pré-condição operacional: configurar `action.auto_create_index=false` no motor
local e no domínio AWS dedicado antes do bootstrap. Assim, a perda do índice não
permite que um writer crie implicitamente outro índice com mapping dinâmico. A
criação explícita pelo bootstrap continua permitida. Confirmar essa configuração
no ambiente antes da primeira ativação e da validação cloud.

Antes da primeira ativação, interrompa todos os escritores antigos e mantenha
escritores/workers parados durante o bootstrap. Para reconstrução, interrompa
todos os escritores e workers, preserve o estado SQL e obtenha autorização para
qualquer descarte de índice; após preparar o destino e executar o bootstrap,
retome somente versões compatíveis com revisão/outbox. Não confie no índice até
a carga terminar. Trocas posteriores entre versões compatíveis podem usar o
rollout normal. O comando administrativo não oferece endpoint HTTP.

Cada criação, edição e desativação confirma uma pendência junto com o jogo no
SQL. O worker abre um escopo por lote, envia o `PayloadJson` imutável com versão
externa e conclui apenas aquela pendência. Somente o HTTP 409 com tipo exato
`version_conflict_engine_exception` dessa operação equivale a entrega satisfeita;
outros erros permanecem pendentes, com backoff exponencial limitado e tentativa
por item. A entrega é ao menos uma vez; concorrência e replay não substituem uma
revisão nova por uma antiga. Documentos inativos permanecem como marcadores de
revisão. Métricas HTTP incluem a rota `/search`, método e status, sem termo de
consulta nos labels. Logs de falha do worker usam códigos fixos/IDs de pendência.

## CI/CD da Fase 4

O workflow `.github/workflows/ci-cd.yml` executa restore, build Release e o
executável de testes xUnit em PRs para `main` e pushes nessa branch. PRs não
recebem credenciais de deploy. Na `main`, o job de deploy serializa as execuções,
descarta candidatos superados e exige configuração completa antes de publicar.

A imagem recebe `--build-arg BUILD_SHA=<SHA completo>`; seu label OCI e o
cabeçalho HTTP `X-Build-Sha` identificam essa versão. Sem o argumento, o build
local usa `development`. O modo administrativo `--migrate` permanece independente
da configuração do cabeçalho.

Com a sessão STS restrita e a infraestrutura disponíveis, a pipeline publica
uma tag contendo SHA/execução/tentativa no ECR, resolve o digest, executa o Job
de migration da mesma imagem e só então atualiza esta API. Confere a versão em
`/status/catalog` pelo Kong. Falha de migration bloqueia a troca dos pods;
falha posterior exige recuperação de imagem compatível, sem rollback de schema.

O guia central `docs/DEPLOY.md` no repositório Orchestration lista variáveis,
Secrets, nomes dos recursos e pré-condições. `ORCHESTRATION_REF` deve apontar
para um commit completo revisado e publicado; não use uma branch móvel.
Para outro repositório privado, configure o token de leitura restrito documentado.
Sessões e sua expiração precisam ser renovadas antes da janela de deploy.

Os arquivos estão preparados localmente; ativação e execução no GitHub/AWS
dependem da publicação, permissões e infraestrutura. Validação local não comprova
conectividade EKS, identidade dos pods ou ausência de interrupção na nuvem.
