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
`Microsoft.Extensions.Caching.StackExchangeRedis` 8.0.28. Reaproveita
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

- SDK .NET 8;
- SQL Server;
- RabbitMQ;
- Redis (a API opera degradada se ele estiver indisponível).

Comandos:

```bash
dotnet restore TechChallenge.Catalog.sln
dotnet run --project tests/FCG.Catalog.Tests/FCG.Catalog.Tests.csproj --configuration Release
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

`appsettings.Development.json` nao contem segredos. Em desenvolvimento, use
variaveis de ambiente ou `dotnet user-secrets`.

Opcao 1 - variaveis de ambiente:

```bash
export ConnectionStrings__CatalogDatabase='Server=localhost,1433;Database=FcgCatalogDb;User Id=sa;Password=<local-sql-password>;TrustServerCertificate=True'
export Jwt__Key='<same-32-byte-or-longer-key-used-by-users-api>'
export RabbitMq__Username='<rabbitmq-username>'
export RabbitMq__Password='<rabbitmq-password>'
export CatalogCache__Configuration='localhost:6379'
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

Opcao 2 - user-secrets:

```bash
dotnet user-secrets set 'ConnectionStrings:CatalogDatabase' 'Server=localhost,1433;Database=FcgCatalogDb;User Id=sa;Password=<local-sql-password>;TrustServerCertificate=True' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'Jwt:Key' '<same-32-byte-or-longer-key-used-by-users-api>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'RabbitMq:Username' '<rabbitmq-username>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet user-secrets set 'RabbitMq:Password' '<rabbitmq-password>' --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
dotnet run --project src/FCG.Catalog.Api/FCG.Catalog.Api.csproj
```

Para comandos de migration com `dotnet ef`, forneca
`ConnectionStrings__CatalogDatabase` como variavel de ambiente.

## Docker

```bash
docker build -t tech-challenge-2-catalog-api:latest .
docker run --rm -p 8083:8080 \
  -e ASPNETCORE_ENVIRONMENT=Production \
  -e ConnectionStrings__CatalogDatabase="Server=host.docker.internal,1433;Database=FcgCatalogDb;User Id=sa;Password=<sql-password>;TrustServerCertificate=True" \
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
