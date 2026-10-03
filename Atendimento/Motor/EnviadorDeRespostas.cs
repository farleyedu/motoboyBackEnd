using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.Motor
{
    public interface IEnviadorDeRespostas
    {
        /// <summary>Grava a resposta do bot na conversa (o chat ja mostra) e envia pelo numero que recebeu a mensagem. Nunca lanca.</summary>
        Task<bool> EnviarAsync(Guid conversaId, CanalWhatsapp canal, string telefone, AcaoMotor acao);
    }

    /// <summary>
    /// Persiste e envia a resposta do bot. A mensagem entra na conversa ANTES do envio: se a Meta recusar, o atendente
    /// ve a mensagem marcada como "falhou" em vez de ela sumir. Falha de envio nao interrompe o resto do atendimento.
    /// </summary>
    public sealed class EnviadorDeRespostas : IEnviadorDeRespostas
    {
        private readonly IMessageService _mensagens;
        private readonly WhatsAppSender _sender;
        private readonly ILogger<EnviadorDeRespostas> _logger;

        public EnviadorDeRespostas(IMessageService mensagens, WhatsAppSender sender, ILogger<EnviadorDeRespostas> logger)
        {
            _mensagens = mensagens;
            _sender = sender;
            _logger = logger;
        }

        public async Task<bool> EnviarAsync(Guid conversaId, CanalWhatsapp canal, string telefone, AcaoMotor acao)
        {
            var (texto, botoes) = acao switch
            {
                AcaoResponder r => (r.Mensagem, null),
                // O chat so guarda texto: as opcoes aparecem listadas para o atendente saber o que o cliente viu.
                AcaoBotoes b => ($"{b.Mensagem}\n{string.Join("\n", b.Opcoes.Select(o => "• " + o.Titulo))}", b.Opcoes),
                _ => (null, null)
            };

            if (string.IsNullOrWhiteSpace(texto)) return true;

            var mensagem = MessageFactory.CreateMessage(conversaId, texto, DirecaoMensagem.Saida, "bot", tipoOrigem: "text");
            await _mensagens.AdicionarMensagemAsync(mensagem, canal.NumeroE164, telefone);

            try
            {
                if (botoes != null)
                {
                    var opcoes = botoes.Select(b => new WhatsAppReplyButtonOption(b.Id, Limitar(b.Titulo, 20))).ToList();
                    await _sender.SendReplyButtonsAsync(conversaId, canal.PhoneNumberId, telefone, ((AcaoBotoes)acao).Mensagem, opcoes, canal.NumeroE164, mensagem.Id);
                }
                else
                {
                    await _sender.SendTextAsync(conversaId, canal.PhoneNumberId, telefone, texto, canal.NumeroE164, mensagem.Id);
                }

                await _mensagens.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Enviada);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend] ev=resposta_falhou conversa={Conversa} canal={Canal} mensagem={Mensagem}", conversaId, canal.Id, mensagem.Id);
                await _mensagens.AtualizarStatusAsync(mensagem.Id, MessageStatusMapper.Falhou, "send_failed", ex.Message);
                return false;
            }
        }

        // O WhatsApp limita o titulo do botao a 20 caracteres.
        private static string Limitar(string titulo, int max) => titulo.Length <= max ? titulo : titulo[..max];
    }
}
