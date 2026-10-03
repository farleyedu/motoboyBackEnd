using System;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Atendimento.Motor;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Automation.Services;
using APIBack.Service.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public interface IPipelineDeMensagem
    {
        Task ExecutarAsync(ConversationProcessingInput input, CanalWhatsapp canal);
    }

    /// <summary>
    /// O caminho de uma mensagem recebida, na ordem: gravar na conversa, confirmar pedido do cardapio por codigo, respeitar o
    /// modo do numero e da conversa (bot ou humano) e so entao deixar o motor de atendimento responder. Cada parada deixa
    /// uma linha [atend] explicando por que o bot nao respondeu.
    /// </summary>
    public sealed class PipelineDeMensagem : IPipelineDeMensagem
    {
        private const string AvisoEmpresaPausada =
            "Estamos enfrentando uma instabilidade temporária no atendimento. Assim que possível, retornaremos por aqui.";

        private readonly IIngressoDeConversa _ingresso;
        private readonly IConversationRepository _conversas;
        private readonly IExecutorDeAtendimento _executor;
        private readonly IEnviadorDeRespostas _enviador;
        private readonly ICardapioPedidoWebService? _pedidosWeb;
        private readonly ILogger<PipelineDeMensagem> _logger;

        public PipelineDeMensagem(
            IIngressoDeConversa ingresso,
            IConversationRepository conversas,
            IExecutorDeAtendimento executor,
            IEnviadorDeRespostas enviador,
            ILogger<PipelineDeMensagem> logger,
            ICardapioPedidoWebService? pedidosWeb = null)
        {
            _ingresso = ingresso;
            _conversas = conversas;
            _executor = executor;
            _enviador = enviador;
            _logger = logger;
            _pedidosWeb = pedidosWeb;
        }

        public async Task ExecutarAsync(ConversationProcessingInput input, CanalWhatsapp canal)
        {
            var wa = input.Mensagem.Id;
            var de = input.Mensagem.De ?? string.Empty;

            // Eco: mensagem que o proprio numero da loja mandou para si.
            if (EhEco(de, input))
            {
                _logger.LogInformation("[atend] ev=ignorada wa={Wa} canal={Canal} motivo=mensagem_do_proprio_numero", wa, canal.Id);
                return;
            }

            var telefone = NormalizarTelefone(de);
            var textoUsuario = string.IsNullOrWhiteSpace(input.TextoInterpretado) ? input.Texto : input.TextoInterpretado!;

            var ingresso = await _ingresso.AcrescentarEntradaAsync(
                idWa: de, idMensagemWa: input.Mensagem.Id!, conteudo: input.Texto, displayPhoneNumber: input.PhoneNumberDisplay ?? string.Empty,
                phoneNumberId: input.PhoneNumberId, dataMensagemUtc: input.DataMensagemUtc, tipoOrigem: input.Mensagem.Tipo,
                telefoneContato: telefone, idEstabelecimentoDoCanal: canal.IdEstabelecimento, idCanal: canal.Id);

            if (ingresso == null)
            {
                _logger.LogInformation("[atend] ev=ignorada wa={Wa} canal={Canal} motivo=duplicada_ou_empresa_desativada", wa, canal.Id);
                return;
            }

            var idConversa = ingresso.Mensagem.IdConversa;
            _logger.LogInformation("[atend] ev=gravada wa={Wa} conversa={Conversa} canal={Canal} loja={Loja} nova={Nova}", wa, idConversa, canal.Id, canal.IdEstabelecimento, ingresso.NovaConversa);

            if (ingresso.EmpresaPausada)
            {
                _logger.LogInformation("[atend] ev=sem_resposta conversa={Conversa} motivo=empresa_pausada", idConversa);
                await _enviador.EnviarAsync(idConversa, canal, telefone, new AcaoResponder(AvisoEmpresaPausada));
                return;
            }

            // Codigo de 4 digitos do cardapio web: confirma o pedido e nao segue para o atendimento.
            if (await TentarConfirmarPedidoAsync(idConversa, textoUsuario))
            {
                _logger.LogInformation("[atend] ev=decisao conversa={Conversa} regra=codigo_cardapio acao=pedido_confirmado", idConversa);
                return;
            }

            if (canal.ModoAtendimento == ModoAtendimento.Humano)
            {
                _logger.LogInformation("[atend] ev=sem_resposta conversa={Conversa} canal={Canal} motivo=numero_em_modo_humano", idConversa, canal.Id);
                return;
            }

            var controle = await _conversas.ObterControleConversaAsync(idConversa);
            if (controle == null)
            {
                _logger.LogWarning("[atend] ev=sem_resposta conversa={Conversa} motivo=controle_nao_encontrado", idConversa);
                return;
            }

            if (!controle.CanBotReply)
            {
                _logger.LogInformation(
                    "[atend] ev=sem_resposta conversa={Conversa} motivo=conversa_com_humano status={Status} atendente={Atendente}",
                    idConversa, controle.Status, controle.AssignedAgentId);
                return;
            }

            var primeira = ingresso.NovaConversa || ingresso.ReiniciadaPorExpiracao || ingresso.AposEncerramentoManualEm.HasValue;
            await _executor.ExecutarAsync(new MensagemRecebida(
                idConversa, canal, telefone, input.Texto, input.TextoInterpretado, primeira, input.DataMensagemUtc ?? DateTime.UtcNow));
        }

        /// <summary>Qualquer falha aqui (banco sem a migration, WhatsApp fora) e ignorada: a mensagem segue o fluxo normal.</summary>
        private async Task<bool> TentarConfirmarPedidoAsync(Guid idConversa, string? texto)
        {
            if (_pedidosWeb == null) return false;

            try
            {
                return await _pedidosWeb.TentarConfirmarPorMensagemAsync(idConversa, texto);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[atend] ev=erro conversa={Conversa} etapa=confirmacao_cardapio", idConversa);
                return false;
            }
        }

        private static bool EhEco(string de, ConversationProcessingInput input)
        {
            var origem = Digitos(de);
            return origem.Length > 0 && (origem == Digitos(input.PhoneNumberDisplay) || origem == Digitos(input.PhoneNumberId));
        }

        private static string NormalizarTelefone(string numero)
        {
            try
            {
                return TelefoneHelper.ToE164(numero);
            }
            catch (Exception)
            {
                return "+" + Digitos(numero);
            }
        }

        private static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
