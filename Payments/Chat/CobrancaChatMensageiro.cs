using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.Templates;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.Payments.MercadoPago;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Chat
{
    /// <summary>Modelo aprovado da Meta para cobranca: nome, idioma, quantas variaveis o corpo tem e se o 1o botao de URL e variavel.</summary>
    public sealed record TemplateCobranca(string Nome, string Idioma, int VariaveisCorpo, bool BotaoUrlVariavel, string? Corpo);

    /// <summary>
    /// Envio da cobranca pelo WhatsApp que a loja ja usa (Cloud API da Meta). A mensagem entra na conversa como qualquer envio do
    /// atendente; o balao da cobranca e outra coisa (item cobranca_pix em conversa_eventos + evento cobrancaAtualizada).
    /// Erros saem como PagamentoException com codigos de CobrancaChatCodigos.
    /// </summary>
    public interface ICobrancaChatMensageiro
    {
        /// <summary>Texto livre (so dentro da janela de 24 h). Devolve o id da mensagem gravada no chat.</summary>
        Task<Guid> EnviarTextoAsync(Guid conversaId, Guid loja, string texto, string criadaPor, CancellationToken ct);
        Task<TemplateCobranca?> TemplateAprovadoAsync(Guid loja, Guid? canalId, string nome, string idioma, CancellationToken ct);
        Task<Guid> EnviarTemplateAsync(Guid conversaId, Guid loja, TemplateCobranca template, IReadOnlyList<string> corpo, string? sufixoBotaoUrl,
            string textoNoChat, string criadaPor, CancellationToken ct);
    }

    /// <summary>Pedido em rascunho que foi pago: segue para Pendente se a loja confirma pedidos sozinha; senao fica para o atendente.</summary>
    public interface ICobrancaPedidoAvancador
    {
        Task<string> AvancarAsync(Guid loja, int pedidoId, int atorUsuarioId, CancellationToken ct);
    }

    public sealed class CobrancaChatMensageiro : ICobrancaChatMensageiro
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        private readonly ConversationManagementService _conversas;
        private readonly IConversationRepository _conversaRepo;
        private readonly IClienteRepository _clientes;
        private readonly ICanalRepository _canais;
        private readonly IWabaPhoneRepository _waba;
        private readonly IMessageService _mensagens;
        private readonly WhatsAppSender _sender;
        private readonly NpgsqlDataSource _db;
        private readonly ILogger<CobrancaChatMensageiro> _logger;

        public CobrancaChatMensageiro(ConversationManagementService conversas, IConversationRepository conversaRepo, IClienteRepository clientes,
            ICanalRepository canais, IWabaPhoneRepository waba, IMessageService mensagens, WhatsAppSender sender, NpgsqlDataSource db,
            ILogger<CobrancaChatMensageiro> logger)
        {
            _conversas = conversas;
            _conversaRepo = conversaRepo;
            _clientes = clientes;
            _canais = canais;
            _waba = waba;
            _mensagens = mensagens;
            _sender = sender;
            _db = db;
            _logger = logger;
        }

        public async Task<Guid> EnviarTextoAsync(Guid conversaId, Guid loja, string texto, string criadaPor, CancellationToken ct)
        {
            try
            {
                return await _conversas.SendSystemNoticeAsync(conversaId, loja, texto, criadaPor);
            }
            catch (ConversationManagementException ex)
            {
                throw Traduzir(ex);
            }
        }

        public async Task<TemplateCobranca?> TemplateAprovadoAsync(Guid loja, Guid? canalId, string nome, string idioma, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var t = await cx.QueryFirstOrDefaultAsync<(string Nome, string Idioma, string Conteudo)?>(new CommandDefinition(@"
SELECT nome, idioma, conteudo::text FROM whatsapp_template
 WHERE id_estabelecimento = @Loja AND nome = @Nome AND idioma = @Idioma AND situacao = 'enviado' AND upper(COALESCE(status_meta, '')) = 'APPROVED'
   AND (CAST(@Canal AS uuid) IS NULL OR id_canal = @Canal OR id_canal IS NULL)
 ORDER BY CASE WHEN id_canal = @Canal THEN 0 ELSE 1 END LIMIT 1", new { Loja = loja, Nome = nome, Idioma = idioma, Canal = canalId }, cancellationToken: ct));
            if (t == null) return null;
            var conteudo = JsonSerializer.Deserialize<TemplateConteudo>(t.Value.Conteudo, Json);
            var botaoVariavel = conteudo?.Botoes?.FirstOrDefault(b => string.Equals(b.Tipo, TipoBotao.Url, StringComparison.OrdinalIgnoreCase))?.Url?.Contains("{{") == true;
            return new TemplateCobranca(t.Value.Nome, t.Value.Idioma, CobrancaChatRegras.ContarVariaveis(conteudo?.Corpo), botaoVariavel, conteudo?.Corpo);
        }

        public async Task<Guid> EnviarTemplateAsync(Guid conversaId, Guid loja, TemplateCobranca template, IReadOnlyList<string> corpo, string? sufixoBotaoUrl,
            string textoNoChat, string criadaPor, CancellationToken ct)
        {
            var conversa = await _conversaRepo.ObterPorIdAsync(conversaId);
            if (conversa == null || conversa.IdEstabelecimento != loja) throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            var destino = await _clientes.ObterTelefoneClienteAsync(conversa.IdCliente, loja);
            if (string.IsNullOrWhiteSpace(destino))
                throw new PagamentoException(CobrancaChatCodigos.WhatsappNaoConfigurado, 422, "Telefone do cliente nao encontrado para envio.");

            string? display = null, phoneId = null;
            if (conversa.IdCanal.HasValue)
            {
                var canal = await _canais.ObterAsync(conversa.IdCanal.Value);
                if (canal != null && canal.Status != StatusCanal.Inativo) (display, phoneId) = (canal.NumeroE164, canal.PhoneNumberId);
            }
            if (string.IsNullOrWhiteSpace(phoneId))
            {
                display = await _waba.ObterDisplayPhonePorEstabelecimentoAsync(loja);
                phoneId = await _waba.ObterPhoneNumberIdPorEstabelecimentoAsync(loja);
            }
            if (string.IsNullOrWhiteSpace(phoneId))
                throw new PagamentoException(CobrancaChatCodigos.WhatsappNaoConfigurado, 422, "WhatsApp da loja nao configurado.");

            var agora = DateTime.UtcNow;
            var mensagem = new Message
            {
                Id = Guid.NewGuid(), IdConversa = conversaId, IdMensagemWa = $"cobranca-{Guid.NewGuid():N}", Direcao = DirecaoMensagem.Saida, Conteudo = textoNoChat,
                DataHora = agora, DataCriacao = agora, DataEnvio = agora, CriadaPor = criadaPor, TipoOriginal = "template",
                Tipo = MessageTypeMapper.MapType("text", DirecaoMensagem.Saida, criadaPor), Status = "fila"
            };
            if (await _mensagens.AdicionarMensagemAsync(mensagem, display, destino) == null)
                throw new PagamentoException(CobrancaChatCodigos.WhatsappErro, 502, "Nao foi possivel registrar a mensagem.");
            try
            {
                await _sender.SendTemplateAsync(conversaId, phoneId!, destino, template.Nome, template.Idioma, corpo, sufixoBotaoUrl, mensagem.Id);
                await _mensagens.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
                return mensagem.Id;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[cobranca.chat] ev=modelo_falhou conversa={Conversa} modelo={Modelo}", conversaId, template.Nome);
                await _mensagens.AtualizarStatusAsync(mensagem.Id, "falhou", "send_failed", ex.Message);
                throw new PagamentoException(CobrancaChatCodigos.WhatsappErro, 502, "O WhatsApp nao confirmou o envio do modelo.");
            }
        }

        private static PagamentoException Traduzir(ConversationManagementException ex)
        {
            if (string.Equals(ex.Code, "whatsapp_window_expired", StringComparison.OrdinalIgnoreCase))
                return new PagamentoException(CobrancaChatCodigos.JanelaFechada, 422, "A janela de 24 horas do WhatsApp esta fechada.");
            if (ex.StatusCode == 404) return PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            if (ex.Message.StartsWith("Falha ao enviar", StringComparison.OrdinalIgnoreCase))
                return new PagamentoException(CobrancaChatCodigos.WhatsappErro, 502, "O WhatsApp nao confirmou o envio.");
            return new PagamentoException(CobrancaChatCodigos.WhatsappNaoConfigurado, 422, ex.Message);
        }
    }

    public sealed class CobrancaPedidoAvancador : ICobrancaPedidoAvancador
    {
        private readonly IPedidoCoreService _core;
        private readonly IPedidoQueueRepository _fila;

        public CobrancaPedidoAvancador(IPedidoCoreService core, IPedidoQueueRepository fila)
        {
            _core = core;
            _fila = fila;
        }

        public async Task<string> AvancarAsync(Guid loja, int pedidoId, int atorUsuarioId, CancellationToken ct)
        {
            var settings = await _fila.GetSettingsAsync(loja);
            if (settings?.AutoConfirmarPedidos != true) return "loja_confirma_manualmente";
            await _core.ConfirmAsync(loja, atorUsuarioId, pedidoId);
            return "confirmado";
        }
    }
}
