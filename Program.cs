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
using APIBack.Automation.Infra.Config;
using APIBack.Automation.Repository;
using APIBack.Automation.Repository.Interface;
using APIBack.Automation.Services.Interface;
using APIBack.Automation.Validators;
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
    .WriteTo.Console()
    .CreateLogger();
builder.Host.UseSerilog();
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

// Ensure Dapper maps snake_case columns to PascalCase properties
DefaultTypeMap.MatchNamesWithUnderscores = true;

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
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IUsuarioRepository, UsuarioRepository>();
builder.Services.AddScoped<IUsuarioService, UsuarioService>();
builder.Services.AddScoped<IPedidoRepository, PedidoRepository>();
builder.Services.AddScoped<IPedidoService, PedidoService>();
builder.Services.AddScoped<IMotoboyRepository, MotoboyRepository>();
builder.Services.AddScoped<IReservaRepository, ReservaRepository>();
builder.Services.AddScoped<IReservasRepository, ReservasRepository>();
builder.Services.AddScoped<IMotoboyService, MotoboyService>();
builder.Services.AddScoped<ILocalizacaoService, LocalizacaoService>();
builder.Services.AddScoped<IReservasService, ReservasService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddSingleton<IJwtService, JwtService>();
builder.Services.Configure<GoogleOAuthOptions>(builder.Configuration.GetSection("GoogleOAuth"));


// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// Automation DI
builder.Services.Configure<AutomationOptions>(builder.Configuration.GetSection("Automation"));
builder.Services.Configure<OpenAIOptions>(builder.Configuration.GetSection("OpenAI"));
builder.Services.AddScoped<IConversationRepository, SqlConversationRepository>();
builder.Services.AddScoped<IMessageRepository, SqlMessageRepository>();
builder.Services.AddScoped<IWabaPhoneRepository, SqlWabaPhoneRepository>();
builder.Services.AddScoped<IIARegraRepository, SqlIARegraRepository>();
builder.Services.AddScoped<IIARespostaRepository, SqlIARespostaRepository>();
builder.Services.AddScoped<IEstabelecimentoRepository, SqlEstabelecimentoRepository>();
builder.Services.AddScoped<IClienteRepository, SqlClienteRepository>();
builder.Services.AddSingleton<IQueueBus, InMemoryQueueBus>();
builder.Services.AddScoped<IWebhookSignatureValidator, WebhookSignatureValidator>();
builder.Services.AddScoped<IWhatsappSender, WhatsappSenderStub>();
builder.Services.AddScoped<IEstabelecimentoSelectionRepository, EstabelecimentoSelectionRepository>();
builder.Services.AddScoped<IEstabelecimentoSelectionService, EstabelecimentoSelectionService>();
builder.Services.AddScoped<EstabelecimentoSelectionValidator>();
builder.Services.AddScoped<ToolExecutorService>();
builder.Services.AddScoped<AtualizarReservaHandler>();
builder.Services.AddScoped<ReservaValidator>();




// Provedor de token do WhatsApp em memória (permite atualizar via endpoint)
builder.Services.AddSingleton<IWhatsAppTokenProvider, InMemoryWhatsAppTokenProvider>();
// IA real via OpenAI (novo orquestrador determinístico)
builder.Services.AddScoped<IAssistantService, AssistantService>();
// Envio real de alertas para Telegram
builder.Services.AddScoped<IAlertSender, AlertSenderTelegram>();
builder.Services.AddScoped<IAgenteRepository, SqlAgenteRepository>();
builder.Services.AddScoped<AgenteService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddSingleton<PromptAssembler>();
builder.Services.AddScoped<IMessageService, MessageService>();
builder.Services.AddScoped<HandoverService>();
builder.Services.AddScoped<AutomationHealthService>();
builder.Services.AddScoped<WebhookValidatorService>();
builder.Services.AddScoped<ConversationProcessor>();
builder.Services.AddScoped<IAResponseHandler>();
builder.Services.AddScoped<WhatsAppSender>();
builder.Services.AddScoped<ContextInterceptorService>();
builder.Services.AddSingleton<IWebhookMessageCache, WebhookMessageCache>();
builder.Services.AddSingleton<WebhookMessageQueue>();
builder.Services.AddSingleton<IWebhookDispatchService, WebhookDispatchService>();
builder.Services.AddHostedService<WebhookProcessingWorker>();
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================


// Configurar CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

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
app.UseCors("AllowAll");

app.UseMiddleware<JwtAuthenticationMiddleware>();
app.UseAuthorization();

app.MapControllers();

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
