using APIBack.Repository;
using APIBack.Service.Interface;
using APIBack.Service;
using APIBack.Repository.Interface;
using Dapper;
// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using Serilog;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Infra;
using APIBack.Automation.Services;
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

var builder = WebApplication.CreateBuilder(args);
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

// Add services to the container.
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddScoped<IUsuarioRepository, UsuarioRepository>();
builder.Services.AddScoped<IUsuarioService, UsuarioService>();
builder.Services.AddScoped<IPedidoRepository, PedidoRepository>();
builder.Services.AddScoped<IPedidoService, PedidoService>();
builder.Services.AddScoped<IMotoboyRepository, MotoboyRepository>();
builder.Services.AddScoped<IMotoboyService, MotoboyService>();
builder.Services.AddScoped<ILocalizacaoService, LocalizacaoService>();


// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
// Automation DI
builder.Services.Configure<AutomationOptions>(builder.Configuration.GetSection("Automation"));
builder.Services.AddScoped<IConversationRepository, SqlConversationRepository>();
builder.Services.AddSingleton<IQueueBus, InMemoryQueueBus>();
builder.Services.AddScoped<IWebhookSignatureValidator, WebhookSignatureValidator>();
builder.Services.AddScoped<IWhatsappSender, WhatsappSenderStub>();
builder.Services.AddScoped<IAlertSender, AlertSenderTelegramStub>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<HandoverService>();
builder.Services.AddScoped<AutomationHealthService>();
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

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}


app.UseHttpsRedirection();

// Usar o middleware de CORS
app.UseCors("AllowAll");

app.UseAuthorization();

app.MapControllers();

app.Run();
