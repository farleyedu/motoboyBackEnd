using APIBack.Repository;
using APIBack.Service.Interface;
using APIBack.Service;
using APIBack.Repository.Interface;
using Dapper;
using APIBack.Middleware;
using APIBack.Options;
// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using Serilog;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Infra;
using APIBack.Automation.Services;
using APIBack.Automation.Repository;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services.Interface;
using APIBack.Automation.Validators;
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
using APIBack.Payments.Options;
using APIBack.Payments.Repository;
using APIBack.Payments.Repository.Interface;
using APIBack.Payments.Services;
using APIBack.Payments.Services.Interface;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using APIBack.Hubs;
using APIBack.Services;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
// ================= ADIÇÕES NECESSÁRIAS (BEGIN) ========================
using Npgsql;
using APIBack.Model; // Namespace onde seu enum ReservaStatus está
// ================= ADIÇÕES NECESSÁRIAS (END) ==========================


var builder = WebApplication.CreateBuilder(args);

// Load local-only overrides when not running on Render (e.g., developer machine)
var runningOnRender = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RENDER")) ||
                      !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RENDER_EXTERNAL_URL"));
if (!runningOnRender)
{
    builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
}
else
{
    // Render Secret File support (runtime mount path for image/native services).
    // Accept both root-relative and /etc/secrets to cover different Render runtimes.
    builder.Configuration.AddJsonFile("appsettings.secrets.json", optional: true, reloadOnChange: false);
    builder.Configuration.AddJsonFile("/etc/secrets/appsettings.secrets.json", optional: true, reloadOnChange: false);
}
// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// Serilog basic console logger
Log.Logger = new LoggerConfiguration()
    .Enrich.FromLogContext()
    // Mascara access_token (JWT do SignalR) e afins antes de qualquer log sair.
    .Enrich.With(new APIBack.Infrastructure.Logging.SensitiveQueryStringEnricher())
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

// Dapper: snake_case -> PascalCase e conversao timestamptz -> DateTimeOffset (sem o
// handler, toda propriedade DateTimeOffset do delivery estoura InvalidCastException).
// Centralizado para os testes exercitarem exatamente o mesmo registro.
APIBack.Infrastructure.DapperConfiguration.Configure();

// ================= CONFIGURAÇÃO DO NPGSQL (BEGIN) ======================
// 1. Pega a connection string do appsettings.json
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

// 2. Cria um "construtor de fonte de dados" com a connection string
var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

// 3. ✨ AQUI ESTÁ A CORREÇÃO: Mapeia o enum do C# para o tipo do PostgreSQL
dataSourceBuilder.MapEnum<ReservaStatus>();

// 4. Constrói a fonte de dados
var dataSource = dataSourceBuilder.Build();

// 5. Registra a fonte de dados como um singleton para ser usada em toda a aplicação
builder.Services.AddSingleton(dataSource);
// ================= CONFIGURAÇÃO DO NPGSQL (END) ========================


// Add services to the container.
builder.Services.AddControllers(options =>
{
    // Global auth by default. Use [AllowAnonymous] explicitly on public endpoints.
    options.Filters.Add(new APIBack.Attributes.AuthorizeAttribute());
});
builder.Services.AddSignalR();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient("chat-media").ConfigurePrimaryHttpMessageHandler(()=>new HttpClientHandler{AllowAutoRedirect=false});
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IUsuarioRepository, UsuarioRepository>();
builder.Services.AddScoped<IUsuarioService, UsuarioService>();
builder.Services.AddScoped<ICrmRepository, CrmRepository>();
builder.Services.AddScoped<ICrmService, CrmService>();
builder.Services.AddScoped<IAdminUsuariosRepository, AdminUsuariosRepository>();
builder.Services.AddScoped<IAdminUsuariosService, AdminUsuariosService>();
builder.Services.AddScoped<IGestaoRepository, GestaoRepository>();
builder.Services.AddScoped<IGestaoService, GestaoService>();
builder.Services.AddScoped<MotoboyContaService>();
builder.Services.AddScoped<MotoboyWorkService>();
builder.Services.AddScoped<MotoboyRouteHistoryService>();
builder.Services.AddScoped<IEstabelecimentoFaqRepository, EstabelecimentoFaqRepository>();
builder.Services.AddScoped<ICardapioRepository, CardapioRepository>();
builder.Services.AddScoped<IPedidoRepository, PedidoRepository>();
builder.Services.AddScoped<IEstabelecimentoServicoRepository, EstabelecimentoServicoRepository>();
builder.Services.AddScoped<IConfiguracaoCarroRepository, ConfiguracaoCarroRepository>();
builder.Services.AddScoped<IEstabelecimentoAgendamentoConfigRepository, EstabelecimentoAgendamentoConfigRepository>();
builder.Services.AddScoped<IAgendaDisponibilidadeRepository, AgendaDisponibilidadeRepository>();
builder.Services.AddScoped<IPedidoService, PedidoService>();
builder.Services.AddScoped<IMotoboyRepository, MotoboyRepository>();
builder.Services.AddScoped<ITrackingRepository, TrackingRepository>();
builder.Services.AddScoped<IOperationalSessionRepository, OperationalSessionRepository>();
builder.Services.AddSingleton<DeliverySyncMetrics>();
builder.Services.AddSingleton<IDeliveryOutboxRepository, DeliveryOutboxRepository>();
builder.Services.AddScoped<IPedidoQueueRepository, PedidoQueueRepository>();
builder.Services.AddScoped<IPedidoQueueService, PedidoQueueService>();
builder.Services.AddScoped<IPedidoCoreService, PedidoCoreService>();
builder.Services.AddScoped<IProdutoAtendimentoRepository, ProdutoAtendimentoRepository>();
builder.Services.AddScoped<CardapioMotoboyAttentionRepository>();
builder.Services.AddScoped<IPedidoConsultaRepository, PedidoConsultaRepository>();
builder.Services.AddScoped<IAtendimentoRepository, AtendimentoRepository>();
builder.Services.AddScoped<IRastreioRepository, RastreioRepository>();
builder.Services.AddScoped<ITrackingNoticeSender, ConversationNoticeSender>();
builder.Services.AddScoped<IAtendenteConfirmacaoSender, AtendenteConfirmacaoService>();
builder.Services.AddScoped<TrackingNoticeService>();
builder.Services.AddHostedService<TrackingNoticeWorker>();
builder.Services.AddScoped<APIBack.Automation.Services.IConversationCloser>(sp => sp.GetRequiredService<ConversationManagementService>());
builder.Services.AddScoped<ConversationAutoCloseService>();
builder.Services.AddHostedService<ConversationAutoCloseWorker>();
builder.Services.AddScoped<EncerramentoService>();
builder.Services.AddHostedService<PedidoEncerramentoWorker>();
builder.Services.AddHostedService<OfertaRotaWorker>();
builder.Services.AddScoped<AtendimentoService>();
builder.Services.AddScoped<APIBack.Repository.CommunicationRepository>();
builder.Services.AddSingleton<CommunicationAudioConverter>();
builder.Services.AddScoped<CommunicationService>();
builder.Services.AddScoped<APIBack.Repository.ClientCommunicationRepository>();
builder.Services.AddScoped<ClientCommunicationService>();
builder.Services.AddScoped<ChatPushService>();
builder.Services.AddHostedService<ChatPushWorker>();
builder.Services.AddHostedService<APIBack.Services.DeliveryOfferPushWorker>();
builder.Services.AddScoped<MotoboyPedidoService>();
builder.Services.AddScoped<ICardapioFichaService, CardapioFichaService>();
builder.Services.AddScoped<IPedidoHistoricoRepository, PedidoHistoricoRepository>();
builder.Services.AddScoped<IRestaurantSettingsRepository, RestaurantSettingsRepository>();
builder.Services.AddScoped<IDeliveryZonaRepository, DeliveryZonaRepository>();
builder.Services.AddScoped<IHorarioOperacaoRepository, HorarioOperacaoRepository>();
builder.Services.AddScoped<IClienteCadastroRepository, ClienteCadastroRepository>();
builder.Services.AddScoped<IClienteEnderecoRepository, ClienteEnderecoRepository>();
builder.Services.AddScoped<IClienteAcessoService, ClienteAcessoService>();
builder.Services.AddScoped<ISimulatedCustomerGuard, SimulatedCustomerGuard>();
builder.Services.AddScoped<IClienteSimulatorService, ClienteSimulatorService>();
builder.Services.AddScoped<IReservaRepository, ReservaRepository>();
builder.Services.AddScoped<ISimuladorRepository, SimuladorRepository>();
builder.Services.AddScoped<ISimuladorPedidoService, SimuladorPedidoService>();
builder.Services.AddScoped<ISimuladorIntegracoes, SimuladorIntegracoes>();
builder.Services.AddScoped<IMotoboyService, MotoboyService>();
builder.Services.AddScoped<ILocalizacaoService, LocalizacaoService>();
builder.Services.AddScoped<ITrackingService, TrackingService>();
builder.Services.AddScoped<IOperationalSessionService, OperationalSessionService>();
builder.Services.AddScoped<IEstabelecimentoFaqService, EstabelecimentoFaqService>();
builder.Services.AddScoped<ICardapioService, CardapioService>();
builder.Services.AddScoped<ICardapioPedidoWebRepository, CardapioPedidoWebRepository>();
builder.Services.AddScoped<ICardapioPedidoWebService, CardapioPedidoWebService>();
builder.Services.AddScoped<ICardapioPublicService, CardapioPublicService>();
builder.Services.AddScoped<ICardapioContractService, CardapioContractService>();
builder.Services.AddScoped<CardapioImagemService>();
builder.Services.AddScoped<IPedidosAbertosService, PedidosAbertosService>();
builder.Services.AddScoped<IEstabelecimentoServicoService, EstabelecimentoServicoService>();
builder.Services.AddScoped<IConfiguracaoCarroService, ConfiguracaoCarroService>();
builder.Services.AddScoped<IEstabelecimentoAgendamentoConfigService, EstabelecimentoAgendamentoConfigService>();
builder.Services.AddScoped<IAgendaDisponibilidadeService, AgendaDisponibilidadeService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.Configure<GoogleOAuthOptions>(builder.Configuration.GetSection("GoogleOAuth"));
builder.Services.Configure<DeliveryTrackingOptions>(builder.Configuration.GetSection(DeliveryTrackingOptions.SectionName));
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        var body = APIBack.DTOs.Common.ApiResponse<object>.Fail(
            "Muitas requisicoes em pouco tempo. Aguarde e tente novamente.",
            "RATE_LIMITED");
        await context.HttpContext.Response.WriteAsync(
            System.Text.Json.JsonSerializer.Serialize(body),
            cancellationToken);
    };
    // Link publico de rastreio: sem login, entao limita por IP (protege contra varredura de tokens).
    options.AddPolicy("public-tracking", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    options.AddPolicy("delivery-location", httpContext =>
    {
        var payload = httpContext.Items.TryGetValue("JwtPayload", out var rawPayload)
            ? rawPayload as APIBack.Model.Auth.JwtPayload
            : null;
        var partitionKey = payload?.MotoboySessionId?.ToString("N")
            ?? httpContext.Connection.RemoteIpAddress?.ToString()
            ?? "anonymous";

        return RateLimitPartition.GetTokenBucketLimiter(
            partitionKey,
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = 10,
                TokensPerPeriod = 10,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                AutoReplenishment = true,
                QueueLimit = 0,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst
            });
    });
    // O lote aceita ate 20 pontos: limitar requisicoes separadamente evita multiplicar
    // por 20 a taxa permitida ao endpoint legado.
    options.AddPolicy("delivery-location-batch", httpContext =>
    {
        var payload = httpContext.Items.TryGetValue("JwtPayload", out var raw)
            ? raw as APIBack.Model.Auth.JwtPayload : null;
        return RateLimitPartition.GetTokenBucketLimiter(
            payload?.MotoboySessionId?.ToString("N") ?? httpContext.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            _ => new TokenBucketRateLimiterOptions { TokenLimit = 5, TokensPerPeriod = 1,
                ReplenishmentPeriod = TimeSpan.FromSeconds(2), AutoReplenishment = true, QueueLimit = 0 });
    });
});
builder.Services.AddHostedService<DeliveryMigrationHostedService>();
builder.Services.AddHostedService<DeliveryTrackingMaintenanceWorker>();
builder.Services.AddHostedService<DeliveryOutboxPublisher>();
builder.Services.Configure<AsaasCheckoutOptions>(builder.Configuration.GetSection("Payments:Asaas"));


// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// Automation DI
builder.Services.Configure<AutomationOptions>(builder.Configuration.GetSection("Automation"));
builder.Services.AddScoped<IConversationRepository, SqlConversationRepository>();
builder.Services.AddScoped<IMessageRepository, SqlMessageRepository>();
builder.Services.AddScoped<IWabaPhoneRepository, SqlWabaPhoneRepository>();
// Atendimento (modulo novo: catalogo de servicos e numeros de WhatsApp por loja)
builder.Services.AddSingleton<APIBack.Atendimento.ITokenProtector, APIBack.Atendimento.TokenProtector>();
builder.Services.AddScoped<APIBack.Atendimento.ICatalogoRepository, APIBack.Atendimento.SqlCatalogoRepository>();
builder.Services.AddScoped<APIBack.Atendimento.ICanalRepository, APIBack.Atendimento.SqlCanalRepository>();
builder.Services.AddScoped<APIBack.Atendimento.IServicosDaLojaService, APIBack.Atendimento.ServicosDaLojaService>();
builder.Services.AddScoped<APIBack.Atendimento.IChatRealtimePublisher, APIBack.Atendimento.ChatRealtimePublisher>();
builder.Services.AddScoped<APIBack.Atendimento.ICanalVerificador, APIBack.Atendimento.CanalVerificador>();
builder.Services.AddScoped<APIBack.Atendimento.ICanaisWhatsappService, APIBack.Atendimento.CanaisWhatsappService>();
builder.Services.AddScoped<IEstabelecimentoRepository, SqlEstabelecimentoRepository>();
builder.Services.AddScoped<IClienteRepository, SqlClienteRepository>();
builder.Services.AddScoped<IWebhookSignatureValidator, WebhookSignatureValidator>();
builder.Services.AddScoped<IEstabelecimentoSelectionRepository, EstabelecimentoSelectionRepository>();
builder.Services.AddScoped<IEstabelecimentoSelectionService, EstabelecimentoSelectionService>();
builder.Services.AddScoped<EstabelecimentoSelectionValidator>();




// Provedor de token do WhatsApp em memória (permite atualizar via endpoint)
builder.Services.AddSingleton<IWhatsAppTokenProvider, InMemoryWhatsAppTokenProvider>();
builder.Services.AddScoped<IAgenteRepository, SqlAgenteRepository>();
builder.Services.AddScoped<AgenteService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<ConversationManagementService>();
builder.Services.AddScoped<ConversaAnexoService>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<WebhookValidatorService>();
builder.Services.AddScoped<WhatsAppSender>();
// Webhook do WhatsApp: evento gravado em wa_evento (Postgres) e processado pelo worker, sem fila em memoria.
builder.Services.AddScoped<APIBack.Atendimento.IWaEventoRepository, APIBack.Atendimento.SqlWaEventoRepository>();
builder.Services.AddScoped<APIBack.Atendimento.IWaEventoProcessor, APIBack.Atendimento.WaEventoProcessor>();
builder.Services.AddScoped<APIBack.Atendimento.IIngressoDeConversa>(sp => sp.GetRequiredService<ConversationService>());
builder.Services.AddScoped<APIBack.Atendimento.IPipelineDeMensagem, APIBack.Atendimento.PipelineDeMensagem>();
// Motor de atendimento: decisao pura (AtendimentoMotor) + fluxos por servico + executor com banco e envio.
builder.Services.AddSingleton<APIBack.Atendimento.Motor.IFluxoDeServico, APIBack.Atendimento.Motor.FluxoCardapioWeb>();
builder.Services.AddSingleton<APIBack.Atendimento.Motor.IFluxoDeServico, APIBack.Atendimento.Motor.FluxoDelivery>();
builder.Services.AddSingleton<APIBack.Atendimento.Motor.IFluxoDeServico, APIBack.Atendimento.Motor.FluxoAgendamento>();
builder.Services.AddSingleton<APIBack.Atendimento.Motor.AtendimentoMotor>();
builder.Services.AddScoped<APIBack.Atendimento.Motor.IFluxoEstadoRepository, APIBack.Atendimento.Motor.SqlFluxoEstadoRepository>();
builder.Services.AddScoped<APIBack.Atendimento.Motor.IEnviadorDeRespostas, APIBack.Atendimento.Motor.EnviadorDeRespostas>();
builder.Services.AddScoped<APIBack.Atendimento.Motor.IExecutorDeAtendimento, APIBack.Atendimento.Motor.ExecutorDeAtendimento>();
builder.Services.AddHostedService<APIBack.Atendimento.WaEventoWorker>();
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

// ================= PAYMENTS SECTION (BEGIN) ===================
builder.Services.AddScoped<ICheckoutRepository, CheckoutRepository>();
builder.Services.AddScoped<ICheckoutPaymentService, CheckoutPaymentService>();
builder.Services.AddScoped<ICheckoutWebhookService, CheckoutWebhookService>();
builder.Services.AddScoped<IPublicCheckoutService, PublicCheckoutService>();
builder.Services.AddHttpClient<IAsaasCheckoutClient, AsaasCheckoutClient>((serviceProvider, client) =>
{
    var settings = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AsaasCheckoutOptions>>().Value;
    var baseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl) ? "https://sandbox.asaas.com/api/" : settings.BaseUrl;

    client.BaseAddress = new Uri(baseUrl);
    client.DefaultRequestHeaders.Remove("access_token");
    if (!string.IsNullOrWhiteSpace(settings.ApiKey))
    {
        client.DefaultRequestHeaders.Add("access_token", settings.ApiKey);
    }

    client.DefaultRequestHeaders.Accept.Clear();
    client.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ZippyCheckout/1.0");
    client.Timeout = TimeSpan.FromSeconds(30);
});
// ================= PAYMENTS SECTION (END) ===================


// Configurar CORS
// O hub SignalR (/hubs/delivery) negocia com credentials mode 'include', e o navegador
// recusa 'Access-Control-Allow-Origin: *' nesse caso. Por isso a politica lista origens
// explicitas e habilita AllowCredentials em vez de AllowAnyOrigin.
var corsAllowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? new[]
    {
        "https://zippy-admin-one.vercel.app",
        "http://localhost:3000",
        "http://127.0.0.1:3000",
        "http://localhost:8081",
        "http://127.0.0.1:8081"
    };

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
              {
                  if (string.IsNullOrWhiteSpace(origin)) return false;
                  if (corsAllowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase)) return true;

                  if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;

                  // Deploys de preview da Vercel trocam de subdominio a cada build.
                  if (uri.Scheme == Uri.UriSchemeHttps && uri.Host.EndsWith(".vercel.app", StringComparison.OrdinalIgnoreCase))
                      return true;

                  // Libera qualquer porta local (apps/paginas rodando na maquina de quem acessa,
                  // ex. Expo web do app motoboy). Nao depende de configurar Cors:AllowedOrigins
                  // a cada porta nova de dev; so quem roda algo na propria maquina usa essa origem.
                  return uri.Scheme == Uri.UriSchemeHttp &&
                         (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host == "127.0.0.1");
              })
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials();
    });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SupportNonNullableReferenceTypes();
    options.CustomSchemaIds(type => (type.FullName ?? type.Name).Replace("+", "."));

    var securityScheme = new Microsoft.OpenApi.Models.OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Cole apenas o JWT (sem aspas e sem o prefixo Bearer).",
        In = Microsoft.OpenApi.Models.ParameterLocation.Header,
        Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new Microsoft.OpenApi.Models.OpenApiReference
        {
            Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };

    options.AddSecurityDefinition("Bearer", securityScheme);
    options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
    {
        { securityScheme, Array.Empty<string>() }
    });
});

// Explicit Kestrel binding: HTTP on port 7137
builder.WebHost.ConfigureKestrel(options =>
{
    // Listen on all network interfaces (IPv4/IPv6) on port 7137 using HTTP
    options.ListenAnyIP(7137);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


// Do not force HTTPS redirection; webhook expects HTTP on port 7137

// Usar o middleware de CORS
app.UseRouting();
app.UseCors("AllowAll");
app.UseMiddleware<DeliverySyncMetricsMiddleware>();
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseStaticFiles();

app.UseMiddleware<JwtAuthenticationMiddleware>();
app.UseRateLimiter();
app.UseAuthorization();

app.MapControllers();
app.MapHub<DeliveryHub>("/hubs/delivery");

// Log bound URLs at startup
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var server = app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>();
        var addresses = feature?.Addresses ?? new List<string>();
        app.Logger.LogInformation("Environment: {Env}", app.Environment.EnvironmentName);
        app.Logger.LogInformation("Listening on: {Addresses}", string.Join(", ", addresses));
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Unable to enumerate server addresses");
    }
});

app.Run();
