# ZippyGo — Plataforma Multi-Tenant de Gestão e Automação

ZippyGo é um ecossistema completo de gestão para estabelecimentos comerciais. A plataforma combina um painel administrativo web, uma API REST robusta e um aplicativo móvel para entregadores, permitindo que restaurantes, oficinas, barbearias, marinas e outros negócios operem com automação de atendimento via WhatsApp, rastreamento de entregas em tempo real, CRM, reservas, cardápio digital e gestão financeira — tudo a partir de uma única plataforma multi-tenant.

🔗 **API em produção:** [https://zippy-api.onrender.com/swagger/index.html](https://zippy-api.onrender.com/swagger/index.html)

---

## Visão Geral do Ecossistema

```
┌─────────────────────┐     ┌─────────────────────────────────────┐
│   zippy-admin       │     │         motoboyBackEnd              │
│   (Next.js 15)      │────▶│   (ASP.NET Core 8 · PostgreSQL)     │
│   Painel Web Admin  │     │   API REST · JWT · SignalR          │
└─────────────────────┘     └────────────────────┬────────────────┘
                                                  │
┌─────────────────────┐                           │
│  zippygo-motoboy    │                           │
│  (Expo / RN 0.79)   │◀──────────────────────────┘
│  App do Entregador  │
└─────────────────────┘
```

Os três projetos compartilham a mesma API e banco de dados PostgreSQL. Cada estabelecimento cadastrado é independente (multi-tenant): possui seus próprios usuários, módulos ativos, configurações e fluxo de automação WhatsApp.

---

## Componentes

### 1. motoboyBackEnd — API REST

**Tecnologias:** .NET 8, C#, PostgreSQL, Dapper, Npgsql, SignalR, Serilog, JWT, BCrypt, Swagger, xUnit  
**Hospedagem:** Render.com (HTTP na porta 7137)

#### Módulos principais

| Módulo | Descrição |
|--------|-----------|
| **Auth** | Autenticação JWT com refresh token e login social via Google OAuth |
| **Usuários** | Cadastro, perfis e vínculo de usuários a estabelecimentos |
| **Gestão** | CRUD de empresas, estabelecimentos e usuários administrativos |
| **Pedidos** | Ciclo completo de pedidos: criação, atribuição, rastreamento e confirmação |
| **Motoboy / Tracking** | Localização em tempo real via GPS com batch de coordenadas e SignalR |
| **Reservas** | Agendamentos com controle de disponibilidade por estabelecimento |
| **Cardápio** | Gerenciamento de categorias, itens, variações e snapshot público |
| **CRM** | Oportunidades, contratos, lançamentos financeiros e gestão de leads (exclusivo ZippyGo Centro) |
| **Garagem** | Leads de compra/venda/troca de veículos com simulações financeiras e upload de arquivos |
| **Náutica** | Leads e pipeline para o segmento náutico |
| **Automation** | Bot de atendimento WhatsApp com IA (OpenAI GPT-4o-mini), orquestração de fluxos por tipo de estabelecimento |
| **Payments** | Checkout e webhooks via Asaas (PIX, boleto, cartão) |
| **FAQ / Serviços** | Bases de conhecimento públicas por estabelecimento para uso do bot |

#### Automação WhatsApp

O módulo de automação (`Automation/`) é o coração diferenciado da plataforma. Cada mensagem recebida via WhatsApp Cloud API (Meta WABA) passa pelo seguinte pipeline:

```
Webhook Meta → WebhookProcessingWorker (fila em memória)
  → ConversationProcessor
    → ContextInterceptorService   (contexto do estabelecimento)
    → CentralRoutingService       (seleciona o fluxo pelo tipo)
    → [OficinaFlow | GarageFlow | NauticaFlow | ServicosFlow | ...]
    → AssistantService            (OpenAI GPT-4o-mini)
    → IAResponseHandler           (interpreta tool calls da IA)
    → ToolExecutorService         (executa ações: reservas, leads, etc.)
    → WhatsAppSender              (envia resposta via Graph API)
    → AlertSenderTelegram         (alertas de escalação humana)
```

Cada fluxo possui seu próprio `PromptAssembler` que injeta catálogo de serviços, FAQ, dados do cliente e histórico da conversa no prompt enviado à OpenAI.

#### Estrutura de pastas

```
motoboyBackEnd/
├── Controllers/          # Endpoints HTTP (estabelecimento + gestão + auth)
├── Service/              # Lógica de negócio dos módulos core
├── Repository/           # Acesso ao banco via Dapper (SQL puro)
├── Model/                # Entidades e enums do domínio
├── DTOs/                 # Objetos de transferência de dados
├── Automation/
│   ├── Controllers/      # Webhooks WhatsApp, Conversas, Leads, Reservas
│   ├── Services/         # Bot, flows, OpenAI, WhatsApp Sender, handover
│   ├── Repository/       # Conversas, mensagens, agentes, WABA phones
│   ├── Validators/       # Validação de assinatura, reservas, seleção
│   └── Infra/            # InMemoryQueueBus, config options
├── Payments/             # Asaas: checkout, webhook, cliente HTTP
├── Hubs/                 # DeliveryHub (SignalR)
├── Middleware/           # JwtAuthenticationMiddleware
├── Attributes/           # [AuthorizeAttribute], [RequirePermission]
├── Options/              # GoogleOAuthOptions, etc.
├── Tests/                # xUnit + Moq
├── sql/                  # Scripts de migração e setup do banco
└── Program.cs            # Bootstrap, DI, Kestrel
```

#### Como rodar localmente

**Pré-requisitos:** .NET 8 SDK, PostgreSQL

```bash
git clone https://github.com/farleyedu/motoboyBackEnd.git
cd motoboyBackEnd

# Crie o arquivo de configuração local (ignorado pelo git)
cp appsettings.json appsettings.Local.json
# Edite appsettings.Local.json com sua connection string e chaves de API

dotnet run
# API disponível em http://localhost:7137
# Swagger em http://localhost:7137/swagger
```

O arquivo `appsettings.Local.json` é carregado automaticamente fora do ambiente Render e sobrescreve os valores de `appsettings.json`. No Render, os segredos são fornecidos via `appsettings.secrets.json` montado em `/etc/secrets/`.

**Variáveis obrigatórias para funcionamento completo:**

| Chave | Descrição |
|-------|-----------|
| `ConnectionStrings:DefaultConnection` | PostgreSQL connection string |
| `Jwt:SecretKey` | Chave HMAC ≥ 32 caracteres para assinar JWT |
| `App:BaseUrl` | URL pública da API (usada para construir URLs de arquivos) |
| `OpenAI:ApiKey` | Chave da API OpenAI para o bot |
| `Automation:Meta:AccessToken` | Token de acesso WhatsApp Cloud API |
| `Automation:Meta:AppSecret` | Secret para validação de assinatura do webhook |
| `Automation:Meta:PhoneNumberId` | ID do número de telefone WABA |
| `Automation:Telegram:BotToken` | Bot do Telegram para alertas de escalação |
| `Payments:Asaas:ApiKey` | Chave da API Asaas |

---

### 2. zippy-admin — Painel Administrativo Web

**Tecnologias:** Next.js 15, React 19, TypeScript, MUI v7, Emotion, Axios, SignalR, TipTap, React Hook Form, Zod, SWR, ApexCharts

#### Funcionalidades por módulo

| Tela | Descrição |
|------|-----------|
| **Dashboard** | Resumo de pedidos, motoboys e indicadores por estabelecimento |
| **Pedidos** | Listagem, atribuição a motoboys e acompanhamento de status |
| **Motoboys** | Cadastro, convite e mapa de rastreamento em tempo real (SignalR) |
| **Reservas** | Calendário de agendamentos com configuração de disponibilidade |
| **Cardápio** | Editor de categorias e itens; snapshot para QR code público |
| **Leads Garagem** | Pipeline de compra/venda/troca de veículos com upload de simulações |
| **Leads Náutica** | Pipeline para o segmento náutico |
| **CRM** | Oportunidades, contratos, financeiro e lançamentos (ZippyGo Centro) |
| **Chats** | Interface de atendimento humano: conversas WhatsApp, envio de texto/imagem/arquivo |
| **Configurações** | FAQ, serviços, configurações de agendamento, cardápio e veículos por estabelecimento |
| **Gestão** | Administração de empresas, estabelecimentos e usuários (acesso restrito) |

#### Arquitetura multi-tenant no frontend

O painel adapta os menus e módulos disponíveis conforme o `tipo` do estabelecimento selecionado (`restaurante`, `barbearia`, `garagem`, `nautica`, `hotel`, `outros`). O contexto do estabelecimento ativo é mantido globalmente e cada rota verifica permissão via `canAccessRoute()` e `hasActiveModule()`.

O módulo **CRM** é exclusivo do estabelecimento `zippygocentro` (identificado por nome) e não aparece no menu de outros estabelecimentos.

#### Como rodar localmente

```bash
cd zippy-admin
cp .env.example .env.local
# Defina NEXT_PUBLIC_API_BASE_URL=http://localhost:7137

yarn install
yarn dev
# Disponível em http://localhost:3000
```

---

### 3. zippygo-motoboy — Aplicativo do Entregador

**Tecnologias:** React Native 0.79, Expo SDK 53, Expo Router v5, TypeScript, Expo Location, Expo Notifications, Expo SecureStore, Axios

#### Funcionalidades

| Tela | Descrição |
|------|-----------|
| **Login / Registro** | Autenticação JWT com armazenamento seguro do token |
| **Pedidos** | Lista de pedidos atribuídos ao motoboy autenticado |
| **Rastreamento** | Envio contínuo de coordenadas GPS em background via `expo-task-manager` |
| **Confirmação de entrega** | Registro de entrega com confirmação pelo cliente |
| **Divisão de pagamento** | Tela auxiliar para divisão de valores na entrega |
| **Notificações** | Alertas push quando novos pedidos são atribuídos |

O app se comunica exclusivamente com a API REST. A URL base é configurada via variável de ambiente `EXPO_PUBLIC_API_BASE_URL` (padrão: `https://zippy-api.onrender.com`).

#### Como rodar localmente

```bash
cd zippygo-motoboy
cp config/apiConfig.example.ts config/apiConfig.ts

npm install
npx expo start

# Android:  npx expo run:android
# iOS:      npx expo run:ios
```

---

## Fluxo de dados completo (exemplo: entrega)

```
[Estabelecimento cria pedido no zippy-admin]
         │
         ▼
[API motoboyBackEnd: POST /Pedido]
         │
         ▼
[Motoboy recebe notificação push no zippygo-motoboy]
         │
         ▼
[App envia localização em batch: PATCH /motoboys/me/location/batch]
         │
         ▼
[SignalR DeliveryHub publica posição em tempo real]
         │
         ▼
[zippy-admin exibe mapa ao vivo com posição do motoboy]
         │
         ▼
[App confirma entrega: POST /Entregas/confirmar]
```

---

## Infraestrutura e Deploy

| Componente | Plataforma | Notas |
|------------|------------|-------|
| API (motoboyBackEnd) | Render.com | Dockerfile incluído; porta 7137 HTTP |
| Banco de dados | PostgreSQL (Render managed) | SSL obrigatório |
| Admin (zippy-admin) | Vercel / Render Static | Build Next.js |
| App motoboy | EAS Build (Expo) | `eas.json` configurado para Android/iOS |
| Arquivos estáticos | `wwwroot/uploads/` na API | Servidos via `app.UseStaticFiles()` |

---

## Segurança

- Todas as rotas da API requerem JWT por padrão via `[AuthorizeAttribute]` global; endpoints públicos usam `[AllowAnonymous]` explicitamente.
- Permissões granulares por módulo e ação via `[RequirePermission("Módulo", "ação")]`.
- Validação de assinatura HMAC-SHA256 nos webhooks do WhatsApp (`WebhookSignatureValidator`).
- Senhas armazenadas com BCrypt.
- Tokens JWT armazenados com `expo-secure-store` no app mobile.
- Segredos de produção via Secret Files do Render (nunca no repositório).

---

## Autor

**Farley Eduardo**  
📧 farleysilvae@gmail.com  
🔗 [LinkedIn](https://www.linkedin.com/in/farley-eduardo-490913175)

---

## Licença

Este projeto está sob a licença MIT.
