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
        /// <summary>
        /// Mensagem para a loja Gestao, com o remetente ja classificado nesta mensagem. Devolve false quando nao ha como entregar com
        /// seguranca (funcionario verificado sem assistente interno disponivel): o evento fica bloqueado, nunca cai no bot publico.
        /// </summary>
        Task<bool> ExecutarGestaoAsync(ConversationProcessingInput input, CanalWhatsapp canal, ContextoGestao contexto);
    }

    /// <summary>Referencia segura da midia para a Parte B (transcricao/interpretacao): so o ID da Meta, nunca o conteudo.</summary>
    public sealed record ReferenciaMidia(string? MidiaId, string? Mime, string? Tipo, string WaMensagemId)
    {
        public bool Audio => Tipo is "audio" or "voice";
    }

    /// <param name="Destino"><see cref="IdentidadeWhatsapp.DestinoRemetente"/>.</param>
    /// <param name="Categoria"><see cref="CategoriaRemetente"/>.</param>
    /// <param name="Autorizacao">So no assistente interno: o usuario verificado e as permissoes reais dele (nunca super admin).</param>
    public sealed record ContextoGestao(string Destino, string Categoria, IdentidadeWhatsapp.AutorizacaoWhatsapp? Autorizacao, ReferenciaMidia Midia);

    /// <summary>Mensagem de funcionario verificado para a loja Gestao, ja autorizada nesta mensagem.</summary>
    public sealed record MensagemInterna(
        string WaMensagemId, Guid CanalId, Guid LojaGestaoId, string TelefoneE164, string Texto, string? TextoInterpretado, DateTime RecebidaEmUtc,
        IdentidadeWhatsapp.AutorizacaoWhatsapp Autorizacao, ReferenciaMidia Midia);

    /// <summary>
    /// Assistente interno da Gestao (implementado em outra etapa). Toda acao confere <see cref="IdentidadeWhatsapp.AutorizacaoWhatsapp.TemPermissao"/>;
    /// a loja de destino do numero nao concede nada.
    /// </summary>
    public interface IAssistenteInternoWhatsapp
    {
        Task AtenderAsync(MensagemInterna mensagem, CanalWhatsapp canal);
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
        internal const string PedidoIdentificacao =
            "Olá! Este número aparece tanto na nossa equipe quanto como contato comercial. " +
            "Para continuar, confirme como você quer ser atendido: responda EQUIPE ou CLIENTE.";

        private readonly IExecucaoAtendimentoRepository? _execucoes;
        private readonly APIBack.Service.ISituacaoComercialService? _situacoes;
        private readonly IIngressoDeConversa _ingresso;
        private readonly IConversationRepository _conversas;
        private readonly IExecutorDeAtendimento _executor;
        private readonly IEnviadorDeRespostas _enviador;
        private readonly ICardapioPedidoWebService? _pedidosWeb;
        private readonly ILogger<PipelineDeMensagem> _logger;
        private readonly IAssistenteInternoWhatsapp? _assistente;

        public PipelineDeMensagem(
            IIngressoDeConversa ingresso,
            IConversationRepository conversas,
            IExecutorDeAtendimento executor,
            IEnviadorDeRespostas enviador,
            ILogger<PipelineDeMensagem> logger,
            ICardapioPedidoWebService? pedidosWeb = null, IExecucaoAtendimentoRepository? execucoes = null,
            APIBack.Service.ISituacaoComercialService? situacoes = null, IAssistenteInternoWhatsapp? assistente = null)
        {
            _assistente = assistente;
            _situacoes = situacoes;
            _execucoes = execucoes;
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

            // Segunda trava: a loja capturada no ingresso e a do canal precisam ser a mesma; nunca grava historico na loja errada.
            if (LojaDivergente(input, canal))
            {
                _logger.LogWarning("[atend] ev=recusada wa={Wa} canal={Canal} loja_canal={LojaCanal} loja_mensagem={LojaMensagem} motivo=loja_divergente",
                    wa, canal.Id, canal.IdEstabelecimento, input.IdEstabelecimento);
                throw new InvalidOperationException("Loja da mensagem diferente da loja do canal: entrega recusada.");
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

            // Loja bloqueada pela Gestao ou ainda em implantacao: a mensagem fica gravada, mas o bot nao responde.
            // O webhook continua devolvendo 200 a Meta (quem chama este pipeline nao muda).
            if (_situacoes != null)
            {
                var situacao = await _situacoes.ObterAsync(canal.IdEstabelecimento);
                if (situacao?.Situacao is APIBack.Service.SituacoesComerciais.Bloqueado or APIBack.Service.SituacoesComerciais.Implantacao)
                {
                    _logger.LogInformation("[atend] ev=sem_resposta conversa={Conversa} loja={Loja} motivo=loja_{Situacao}", idConversa, canal.IdEstabelecimento, situacao.Situacao);
                    return;
                }
                if (!await _situacoes.TemModuloAsync(canal.IdEstabelecimento, "WHATSAPP"))
                {
                    _logger.LogInformation("[atend] ev=sem_resposta conversa={Conversa} loja={Loja} motivo=modulo_whatsapp_inativo", idConversa, canal.IdEstabelecimento);
                    return;
                }
            }

            if (ingresso.EmpresaPausada)
            {
                _logger.LogInformation("[atend] ev=sem_resposta conversa={Conversa} motivo=empresa_pausada", idConversa);
                await _enviador.EnviarAsync(idConversa, canal, telefone, new AcaoResponder(AvisoEmpresaPausada));
                return;
            }

            var execucao = _execucoes != null ? await _execucoes.ObterAsync(wa!) : null;
            if (execucao != null)
            {
                if (!execucao.Concluida)
                    await _executor.ExecutarAsync(new MensagemRecebida(idConversa, canal, telefone, input.Texto, input.TextoInterpretado,
                        false, input.DataMensagemUtc ?? DateTime.UtcNow, wa, input.Mensagem.Tipo));
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
                idConversa, canal, telefone, input.Texto, input.TextoInterpretado, primeira, input.DataMensagemUtc ?? DateTime.UtcNow, wa, input.Mensagem.Tipo));
        }

        public async Task<bool> ExecutarGestaoAsync(ConversationProcessingInput input, CanalWhatsapp canal, ContextoGestao contexto)
        {
            var wa = input.Mensagem.Id;
            var de = input.Mensagem.De ?? string.Empty;
            if (EhEco(de, input))
            {
                _logger.LogInformation("[atend] ev=ignorada wa={Wa} canal={Canal} motivo=mensagem_do_proprio_numero", wa, canal.Id);
                return true;
            }
            if (LojaDivergente(input, canal))
                throw new InvalidOperationException("Loja da mensagem diferente da loja do canal: entrega recusada.");

            switch (contexto.Destino)
            {
                case IdentidadeWhatsapp.DestinoRemetente.AssistenteInterno:
                    // Nunca cai no bot publico nem na conversa publica da loja: sem assistente, o evento fica bloqueado para reprocessar.
                    if (_assistente == null || contexto.Autorizacao == null)
                    {
                        _logger.LogWarning("[atend] ev=sem_resposta wa={Wa} canal={Canal} rota=gestao motivo=assistente_interno_indisponivel", wa, canal.Id);
                        return false;
                    }
                    _logger.LogInformation("[atend] ev=assistente_interno wa={Wa} canal={Canal} usuario={Usuario} midia={Midia}",
                        wa, canal.Id, contexto.Autorizacao.UsuarioId, contexto.Midia.MidiaId != null);
                    await _assistente.AtenderAsync(new MensagemInterna(
                        wa!, canal.Id, canal.IdEstabelecimento, NormalizarTelefone(de), input.Texto, input.TextoInterpretado,
                        input.DataMensagemUtc ?? DateTime.UtcNow, contexto.Autorizacao, contexto.Midia), canal);
                    return true;

                case IdentidadeWhatsapp.DestinoRemetente.ConfirmarPapel:
                    await PedirIdentificacaoAsync(input, canal, wa);
                    return true;

                default:
                    // Contato do CRM (atendimento publico) ou desconhecido (qualificacao): o atendimento publico da loja Gestao.
                    _logger.LogInformation("[atend] ev=gestao_publico wa={Wa} canal={Canal} categoria={Categoria}", wa, canal.Id, contexto.Categoria);
                    await ExecutarAsync(input, canal);
                    return true;
            }
        }

        /// <summary>
        /// Telefone da equipe que tambem e contato comercial: grava na conversa publica (nada interno), nao chama o bot e pede que a
        /// pessoa diga o papel. Nada e decidido em silencio; o pedido sai uma vez por conversa.
        /// </summary>
        private async Task PedirIdentificacaoAsync(ConversationProcessingInput input, CanalWhatsapp canal, string? wa)
        {
            var telefone = NormalizarTelefone(input.Mensagem.De ?? string.Empty);
            var ingresso = await _ingresso.AcrescentarEntradaAsync(
                idWa: input.Mensagem.De!, idMensagemWa: wa!, conteudo: input.Texto, displayPhoneNumber: input.PhoneNumberDisplay ?? string.Empty,
                phoneNumberId: input.PhoneNumberId, dataMensagemUtc: input.DataMensagemUtc, tipoOrigem: input.Mensagem.Tipo,
                telefoneContato: telefone, idEstabelecimentoDoCanal: canal.IdEstabelecimento, idCanal: canal.Id);
            if (ingresso == null)
            {
                _logger.LogInformation("[atend] ev=ignorada wa={Wa} canal={Canal} motivo=duplicada_ou_empresa_desativada", wa, canal.Id);
                return;
            }

            var primeira = ingresso.NovaConversa || ingresso.ReiniciadaPorExpiracao || ingresso.AposEncerramentoManualEm.HasValue;
            _logger.LogInformation("[atend] ev=identificacao_pendente wa={Wa} conversa={Conversa} canal={Canal} pedido_enviado={Pedido}",
                wa, ingresso.Mensagem.IdConversa, canal.Id, primeira);
            if (primeira)
                await _enviador.EnviarAsync(ingresso.Mensagem.IdConversa, canal, telefone, new AcaoResponder(PedidoIdentificacao));
        }

        private static bool LojaDivergente(ConversationProcessingInput input, CanalWhatsapp canal) =>
            input.IdEstabelecimento.HasValue && input.IdEstabelecimento.Value != canal.IdEstabelecimento;

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
