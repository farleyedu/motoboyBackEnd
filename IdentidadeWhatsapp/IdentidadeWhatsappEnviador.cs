using System;
using System.Net.Http;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.IdentidadeWhatsapp
{
    /// <summary>Entrega o codigo de verificacao no WhatsApp da pessoa. Falha vira IdentidadeWhatsappException 503.</summary>
    public interface IIdentidadeWhatsappEnviador
    {
        Task EnviarCodigoAsync(string telefoneE164, string codigo);
    }

    /// <summary>
    /// Envia pelo numero central da Zippy (Cloud API da Meta) com um modelo de AUTENTICACAO aprovado: {{1}} do corpo e o botao de copiar
    /// recebem o codigo. Fora da janela de 24 h so modelo e aceito, por isso nao ha texto livre aqui. Nada e gravado em conversa: o codigo
    /// nao pode aparecer no chat nem no historico de mensagens.
    /// Config: IdentidadeWhatsapp:TemplateNome (obrigatorio), IdentidadeWhatsapp:TemplateIdioma (padrao pt_BR).
    /// </summary>
    public sealed class MetaIdentidadeWhatsappEnviador : IIdentidadeWhatsappEnviador
    {
        private readonly ICanalRepository _canais;
        private readonly WhatsAppSender _sender;
        private readonly IConfiguration _configuration;
        private readonly ILogger<MetaIdentidadeWhatsappEnviador> _logger;

        public MetaIdentidadeWhatsappEnviador(ICanalRepository canais, WhatsAppSender sender, IConfiguration configuration, ILogger<MetaIdentidadeWhatsappEnviador> logger)
        {
            _canais = canais;
            _sender = sender;
            _configuration = configuration;
            _logger = logger;
        }

        public async Task EnviarCodigoAsync(string telefoneE164, string codigo)
        {
            var template = _configuration["IdentidadeWhatsapp:TemplateNome"];
            var idioma = _configuration["IdentidadeWhatsapp:TemplateIdioma"] ?? "pt_BR";
            var canal = await _canais.ObterCentralAsync();
            if (string.IsNullOrWhiteSpace(template) || canal == null || canal.Status == StatusCanal.Inativo || string.IsNullOrWhiteSpace(canal.PhoneNumberId))
            {
                _logger.LogError("[identidade.wa] ev=envio_indisponivel canal={Canal} modelo_configurado={Modelo}", canal?.Id, !string.IsNullOrWhiteSpace(template));
                throw new IdentidadeWhatsappException(503, "envio_indisponivel", "O numero da Zippy para verificacao nao esta configurado.");
            }

            try
            {
                // Sem conversa (Guid.Empty): o envio nao vira mensagem gravada. O log do sender nao inclui o payload.
                await _sender.SendTemplateAsync(Guid.Empty, canal.PhoneNumberId, telefoneE164, template, idioma, new[] { codigo }, codigo);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogWarning("[identidade.wa] ev=envio_falhou canal={Canal} erro={Erro}", canal.Id, ex.GetType().Name);
                throw new IdentidadeWhatsappException(503, "envio_falhou", "O WhatsApp nao confirmou o envio do codigo. Tente de novo em instantes.");
            }
        }
    }
}
