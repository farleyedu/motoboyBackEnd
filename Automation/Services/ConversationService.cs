// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;

namespace APIBack.Automation.Services
{
    public class ConversationService
    {
        private readonly IConversationRepository _repositorio;
        private readonly ILogger<ConversationService> _logger;
        private readonly IQueueBus _queueBus;
        private readonly IClienteRepository _repositorioClientes;
        private readonly IWabaPhoneRepository _wabaPhoneRepository;
        private readonly IMessageService _mensagemService;
        private readonly IConfiguration _configuration;
        private readonly CentralRoutingService _centralRouting;
        private readonly ConversationResetService _conversationReset;

        // mapeia waId -> conversationId (in-memory)
        private readonly ConcurrentDictionary<string, Guid> _waParaConversa = new(StringComparer.OrdinalIgnoreCase);

        // armazenamento in-memory de mensagens por conversa
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<Message>> _mensagens = new();

        public ConversationService(
            IConversationRepository repo,
            ILogger<ConversationService> logger,
            IQueueBus queueBus,
            IClienteRepository repositorioClientes,
            IWabaPhoneRepository wabaPhoneRepository,
            CentralRoutingService centralRouting,
            ConversationResetService conversationReset,
            IConfiguration configuration,
            IMessageService mensagemService)
        {
            _repositorio = repo;
            _logger = logger;
            _queueBus = queueBus;
            _repositorioClientes = repositorioClientes;
            _wabaPhoneRepository = wabaPhoneRepository;
            _centralRouting = centralRouting;
            _conversationReset = conversationReset;
            _configuration = configuration;
            _mensagemService = mensagemService;
        }

        /// <summary>
        /// Adiciona uma mensagem de entrada (do cliente) e persiste no banco.
        /// </summary>
        /// <param name="idWa">WhatsApp ID do cliente (ex: 5534999887766)</param>
        /// <param name="idMensagemWa">ID único da mensagem do WhatsApp</param>
        /// <param name="conteudo">Texto da mensagem</param>
        /// <param name="displayPhoneNumber">Número de telefone visível do estabelecimento (ex: +5534999887766) - USADO PARA BUSCAR ESTABELECIMENTO</param>
        /// <param name="phoneNumberId">Identificador do número WhatsApp no Meta; usado como fallback para resolver estabelecimento</param>
        /// <param name="dataMensagemUtc">Data/hora UTC da mensagem</param>
        /// <param name="tipoOrigem">Tipo da mensagem (text, image, etc)</param>
        public async Task<ConversationIngressResult?> AcrescentarEntradaAsync(
            string idWa,
            string idMensagemWa,
            string conteudo,
            string displayPhoneNumber,
            string? phoneNumberId = null,
            DateTime? dataMensagemUtc = null,
            string? tipoOrigem = null,
            string? telefoneContato = null)
        {
            if (string.IsNullOrWhiteSpace(idMensagemWa))
            {
                _logger.LogWarning("Mensagem de entrada sem IdMensagemWa para IdWa={WaId}", idWa);
                return null;
            }

            // Verificar duplicidade (com exceção em DEV)
            var ambiente = _configuration.GetValue<string>("ASPNETCORE_ENVIRONMENT");
            var isDev = string.Equals(ambiente, "Development", StringComparison.OrdinalIgnoreCase);

            var duplicata = await _repositorio.ExisteIdMensagemPorProvedorWaAsync(idMensagemWa);
            if (duplicata)
            {
                if (isDev)
                {
                    _logger.LogWarning(
                        "DEV: Duplicata detectada IdMensagemWa={WaMessageId}, processamento continuará para testes.",
                        idMensagemWa);
                }
                else
                {
                    _logger.LogInformation("Ignorando duplicata de entrada IdMensagemWa={WaMessageId}", idMensagemWa);
                    return null;
                }
            }

            // ============== CORREÇÃO: SEMPRE USAR DISPLAY PHONE NUMBER ==============
            // Buscar estabelecimento pelo número visível (display_phone_number)
            var idEstabelecimento = await ResolverEstabelecimentoAsync(displayPhoneNumber, phoneNumberId);
            // =========================================================================

            // Fallback para estabelecimento padrão se não encontrar
            if (idEstabelecimento == null || idEstabelecimento == Guid.Empty)
            {
                var fallbackEstabelecimentoId = _configuration.GetValue<string>("WhatsApp:FallbackEstabelecimentoId");
                if (!string.IsNullOrWhiteSpace(fallbackEstabelecimentoId) &&
                    Guid.TryParse(fallbackEstabelecimentoId, out var fallbackGuid) &&
                    fallbackGuid != Guid.Empty)
                {
                    idEstabelecimento = fallbackGuid;
                    _logger.LogWarning(
                        "Usando estabelecimento fallback {IdEstabelecimento} para display_phone_number={Display}",
                        idEstabelecimento,
                        displayPhoneNumber);
                }
                else
                {
                    _logger.LogError(
                        "Não foi possível resolver id_estabelecimento para display_phone_number={Display}, phone_number_id={PhoneNumberId}; fallback ausente ou inválido",
                        displayPhoneNumber,
                        phoneNumberId ?? "(null)");
                    throw new InvalidOperationException(
                        $"Não foi possível resolver id_estabelecimento para display_phone_number={displayPhoneNumber}, phone_number_id={phoneNumberId}");
                }
            }

            if (!string.IsNullOrWhiteSpace(displayPhoneNumber) || !string.IsNullOrWhiteSpace(phoneNumberId))
            {
                await _wabaPhoneRepository.InserirOuAtualizarAsync(new WabaPhone
                {
                    PhoneNumberId = phoneNumberId ?? string.Empty,
                    DisplayPhoneNumber = string.IsNullOrWhiteSpace(displayPhoneNumber) ? null : displayPhoneNumber,
                    IdEstabelecimento = idEstabelecimento.Value,
                    Ativo = true,
                    Descricao = string.IsNullOrWhiteSpace(displayPhoneNumber) ? null : displayPhoneNumber
                });
            }

            // Garantir cliente existe
            var telefonePreferencialBruto = !string.IsNullOrWhiteSpace(telefoneContato) ? telefoneContato : idWa;
            string telefoneE164;

            try
            {
                telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(telefonePreferencialBruto);

                if (telefoneE164.Length < 13 || telefoneE164 == "+55")
                {
                    _logger.LogWarning("Telefone incompleto após normalização: {Telefone}. Tentando fallback para idWa.", telefoneE164);

                    if (!string.Equals(telefonePreferencialBruto, idWa, StringComparison.Ordinal))
                    {
                        try
                        {
                            telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa);

                            if (telefoneE164.Length < 13)
                            {
                                _logger.LogError("Impossível obter telefone válido. idWa: {IdWa}, telefoneContato: {TelefoneContato}", idWa, telefoneContato);
                            }
                        }
                        catch (Exception exFallback)
                        {
                            _logger.LogError(exFallback, "Falha ao normalizar idWa como telefone: {IdWa}", idWa);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Erro ao normalizar telefone. Usando idWa como fallback: {IdWa}", idWa);
                telefoneE164 = APIBack.Automation.Helpers.TelefoneHelper.ToE164(idWa);
            }

            Guid idCliente;
            Guid idConversa;
            Guid idConversaGrupo;
            Guid idEstabelecimentoEfetivo;
            Conversation? existente;
            var ehNumeroCentralEntrada = EhNumeroCentral(idEstabelecimento.Value);
            var conversaResolvidaPorTelefone = await ResolverConversaAbertaPorTelefoneAsync(
                telefoneE164,
                idEstabelecimento.Value,
                ehNumeroCentralEntrada);
            var conversaAbertaPorTelefone = conversaResolvidaPorTelefone.Conversa;

            if (ehNumeroCentralEntrada)
            {
                if (conversaAbertaPorTelefone != null && !conversaAbertaPorTelefone.EhRaizDoGrupo)
                {
                    idCliente = conversaAbertaPorTelefone.IdCliente;
                    idConversa = conversaAbertaPorTelefone.IdConversa;
                    idConversaGrupo = conversaAbertaPorTelefone.IdConversaGrupo == Guid.Empty
                        ? conversaAbertaPorTelefone.IdConversa
                        : conversaAbertaPorTelefone.IdConversaGrupo;
                    idEstabelecimentoEfetivo = conversaAbertaPorTelefone.IdEstabelecimento;
                    existente = conversaAbertaPorTelefone;
                }
                else
                {
                    var idClienteCentral = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimento.Value);
                    var conversaRaiz = conversaAbertaPorTelefone;

                    if (conversaRaiz == null)
                    {
                        var novaConversaRaizId = Guid.NewGuid();
                        conversaRaiz = new Conversation
                        {
                            IdConversa = novaConversaRaizId,
                            IdConversaGrupo = novaConversaRaizId,
                            IdEstabelecimento = idEstabelecimento.Value,
                            IdCliente = idClienteCentral,
                            IdWa = idWa,
                            TelefoneCliente = telefoneE164,
                            Modo = ModoConversa.Bot,
                            CriadoEm = DateTime.UtcNow,
                            AtualizadoEm = DateTime.UtcNow,
                            MessageIdWhatsapp = idMensagemWa
                        };

                        await _repositorio.InserirOuAtualizarAsync(conversaRaiz);
                    }

                    var idConversaRaiz = conversaRaiz.IdConversa;
                    var snapshot = await _centralRouting.ObterSelecaoAtualAsync(idConversaRaiz);
                    var idConversaEfetiva = idConversaRaiz;
                    var conversaEfetiva = conversaRaiz;
                    var idEstabelecimentoSelecionado = idEstabelecimento.Value;
                    var idClienteSelecionado = idClienteCentral;

                    if (snapshot.HasSelection)
                    {
                        var idSegmentoAtivo = await _centralRouting.GarantirSegmentoAtivoAsync(idConversaRaiz, telefoneE164);
                        if (idSegmentoAtivo.HasValue && idSegmentoAtivo.Value != Guid.Empty)
                        {
                            idConversaEfetiva = idSegmentoAtivo.Value;
                            conversaEfetiva = await _repositorio.ObterPorIdAsync(idConversaEfetiva);
                            if (conversaEfetiva != null)
                            {
                                idEstabelecimentoSelecionado = conversaEfetiva.IdEstabelecimento;
                                idClienteSelecionado = conversaEfetiva.IdCliente;
                            }
                            else if (snapshot.EstabelecimentoId.HasValue)
                            {
                                idEstabelecimentoSelecionado = snapshot.EstabelecimentoId.Value;
                                idClienteSelecionado = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimentoSelecionado);
                            }
                        }
                    }

                    idCliente = idClienteSelecionado;
                    idConversa = idConversaEfetiva;
                    idConversaGrupo = conversaRaiz.IdConversaGrupo == Guid.Empty ? conversaRaiz.IdConversa : conversaRaiz.IdConversaGrupo;
                    idEstabelecimentoEfetivo = idEstabelecimentoSelecionado;
                    existente = conversaEfetiva;
                }
            }
            else
            {
                if (conversaAbertaPorTelefone != null)
                {
                    idCliente = conversaAbertaPorTelefone.IdCliente;
                    idConversa = conversaAbertaPorTelefone.IdConversa;
                    idConversaGrupo = conversaAbertaPorTelefone.IdConversaGrupo == Guid.Empty
                        ? conversaAbertaPorTelefone.IdConversa
                        : conversaAbertaPorTelefone.IdConversaGrupo;
                    idEstabelecimentoEfetivo = conversaAbertaPorTelefone.IdEstabelecimento;
                    existente = conversaAbertaPorTelefone;
                }
                else
                {
                    idCliente = await _repositorioClientes.GarantirClienteAsync(telefoneE164, idEstabelecimento.Value);
                    idConversa = Guid.NewGuid();
                    existente = null;
                    idConversaGrupo = idConversa;
                    idEstabelecimentoEfetivo = idEstabelecimento.Value;
                }
            }

            _waParaConversa[idWa] = idConversa; // cache auxiliar

            var conversa = existente ?? new Conversation
            {
                IdConversa = idConversa,
                IdConversaGrupo = idConversaGrupo,
                IdEstabelecimento = idEstabelecimentoEfetivo,
                IdCliente = idCliente,
                IdWa = idWa,
                Modo = ModoConversa.Bot,
                CriadoEm = DateTime.UtcNow,
                AtualizadoEm = DateTime.UtcNow,
                MessageIdWhatsapp = idMensagemWa
            };

            // Garante WaId e Estabelecimento salvos
            conversa.IdWa = idWa;
            conversa.IdConversaGrupo = conversa.IdConversaGrupo == Guid.Empty ? idConversaGrupo : conversa.IdConversaGrupo;
            conversa.IdEstabelecimento = idEstabelecimentoEfetivo;
            conversa.IdCliente = idCliente;

            if (string.IsNullOrWhiteSpace(conversa.TelefoneCliente) && !string.IsNullOrWhiteSpace(telefoneE164))
            {
                conversa.TelefoneCliente = telefoneE164;
            }

            var conversaInserida = await _repositorio.InserirOuAtualizarAsync(conversa);
            if (!conversaInserida)
            {
                _logger.LogError("Falha ao inserir/atualizar conversa {IdConversa}", idConversa);
            }

            if (existente == null)
            {
                _logger.LogInformation(
                    "[Automation] Nova conversa criada: {ConversationId} para WaId={WaId}",
                    idConversa,
                    idWa);
            }

            // Criar mensagem
            const string criador = "cliente";
            var dataMensagem = dataMensagemUtc.HasValue
                ? DateTime.SpecifyKind(dataMensagemUtc.Value, DateTimeKind.Utc)
                : DateTime.UtcNow;
            var tipoMapeado = MessageTypeMapper.MapType(tipoOrigem, DirecaoMensagem.Entrada, criador);

            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = idMensagemWa,
                Direcao = DirecaoMensagem.Entrada,
                Conteudo = conteudo,
                DataHora = dataMensagem,
                DataCriacao = dataMensagem,
                CriadaPor = criador,
                TipoOriginal = tipoOrigem,
                Tipo = tipoMapeado,
            };

            // Persistir
            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, displayPhoneNumber, idWa);

            return new ConversationIngressResult(
                mensagem,
                conversaResolvidaPorTelefone.ReiniciadaPorExpiracao);
        }

        public async Task<Message> AcrescentarSaidaAsync(Guid idConversa, string idWa, string conteudo)
        {
            const string criador = "sistema";
            const string tipoOriginal = "text";
            var dataMensagem = DateTime.UtcNow;
            var tipoMapeado = MessageTypeMapper.MapType(tipoOriginal, DirecaoMensagem.Saida, criador);

            var mensagem = new Message
            {
                IdConversa = idConversa,
                IdMensagemWa = $"local-{Guid.NewGuid():N}",
                Direcao = DirecaoMensagem.Saida,
                Conteudo = conteudo,
                DataHora = dataMensagem,
                DataCriacao = dataMensagem,
                DataEnvio = dataMensagem,
                CriadaPor = criador,
                TipoOriginal = tipoOriginal,
                Tipo = tipoMapeado,
            };

            EnfileirarMensagem(mensagem);
            await _mensagemService.AdicionarMensagemAsync(mensagem, phoneNumberId: null, idWa);
            return mensagem;
        }

        public async Task DefinirModoBotAsync(Guid idConversa, string? mensagemTransicao = null)
        {
            await _repositorio.DefinirModoAsync(idConversa, ModoConversa.Bot, agenteId: null);
            if (!string.IsNullOrWhiteSpace(mensagemTransicao))
            {
                const string criador = "sistema";
                const string tipoOriginal = "text";
                var dataMensagem = DateTime.UtcNow;
                var tipoMapeado = MessageTypeMapper.MapType(tipoOriginal, DirecaoMensagem.Saida, criador);

                var msg = new Message
                {
                    IdConversa = idConversa,
                    IdMensagemWa = $"local-{Guid.NewGuid():N}",
                    Direcao = DirecaoMensagem.Saida,
                    Conteudo = mensagemTransicao,
                    DataHora = dataMensagem,
                    DataCriacao = dataMensagem,
                    DataEnvio = dataMensagem,
                    CriadaPor = criador,
                    TipoOriginal = tipoOriginal,
                    Tipo = tipoMapeado,
                };
                EnfileirarMensagem(msg);
                await _mensagemService.AdicionarMensagemAsync(msg, phoneNumberId: null, idWa: null);
            }
        }

        public async Task<ConversationResponse?> ObterConversaRespostaAsync(Guid idConversa, Guid? idEstabelecimento = null, int ultimasN = 20)
        {
            var conversa = await _repositorio.ObterPorIdAsync(idConversa, idEstabelecimento);
            if (conversa == null) return null;

            if (idEstabelecimento.HasValue)
            {
                var historico = await _repositorio.ObterHistoricoConversaAsync(idConversa, null, ultimasN, idEstabelecimento.Value);
                if (historico != null)
                {
                    return new ConversationResponse
                    {
                        IdConversa = historico.Conversa.Id,
                        IdWa = conversa.IdWa,
                        Modo = conversa.Modo,
                        AgenteDesignadoId = conversa.AgenteDesignadoId,
                        UltimoUsuarioEm = conversa.UltimoUsuarioEm ?? default(DateTime),
                        Janela24hExpiraEm = conversa.Janela24hExpiraEm,
                        CriadoEm = historico.Conversa.DataCriacao,
                        AtualizadoEm = historico.Conversa.DataAtualizacao,
                        Mensagens = historico.Mensagens
                            .Select(item => new ConversationMessageView
                            {
                                Id = item.Id.ToString(),
                                SenderId = string.Equals(item.CriadaPor, "cliente", StringComparison.OrdinalIgnoreCase) ? "in" : "out",
                                Msg = item.Conteudo ?? string.Empty,
                                Type = "text",
                                CreatedAt = item.DataCriacao
                            })
                            .ToList()
                    };
                }
            }

            var lista = _mensagens.GetOrAdd(idConversa, _ => new ConcurrentQueue<Message>()).ToArray();
            var ultimas = lista.Skip(Math.Max(0, lista.Length - ultimasN)).ToList();

            return new ConversationResponse
            {
                IdConversa = conversa.IdConversa,
                IdWa = conversa.IdWa,
                Modo = conversa.Modo,
                AgenteDesignadoId = conversa.AgenteDesignadoId,
                UltimoUsuarioEm = conversa.UltimoUsuarioEm ?? default(DateTime),
                Janela24hExpiraEm = conversa.Janela24hExpiraEm,
                CriadoEm = conversa.CriadoEm,
                AtualizadoEm = conversa.AtualizadoEm ?? default(DateTime),
                Mensagens = ultimas.Select(ConversationMessageView.FromMessage).ToList()
            };
        }

        private void EnfileirarMensagem(Message mensagem)
        {
            var fila = _mensagens.GetOrAdd(mensagem.IdConversa, _ => new ConcurrentQueue<Message>());
            fila.Enqueue(mensagem);
        }

        private async Task<ConversationLookupResult> ResolverConversaAbertaPorTelefoneAsync(
            string telefoneE164,
            Guid idEstabelecimentoEntrada,
            bool ehNumeroCentral)
        {
            if (string.IsNullOrWhiteSpace(telefoneE164))
            {
                return new ConversationLookupResult(null, false);
            }

            var abertas = (await _repositorio.ListarConversasAbertasPorTelefoneAsync(telefoneE164)).ToList();
            if (abertas.Count == 0)
            {
                return new ConversationLookupResult(null, false);
            }

            Conversation? preservada;
            if (ehNumeroCentral)
            {
                preservada = null;

                foreach (var candidata in abertas
                             .Where(c => !c.EhRaizDoGrupo)
                             .OrderByDescending(ConversationTimestamp))
                {
                    var selecao = await _centralRouting.ObterSelecaoAtualAsync(candidata.IdConversa);
                    if (selecao.HasSelection)
                    {
                        preservada = candidata;
                        break;
                    }
                }

                preservada ??= abertas
                    .Where(c => c.IdEstabelecimento == idEstabelecimentoEntrada && c.EhRaizDoGrupo)
                    .OrderByDescending(ConversationTimestamp)
                    .FirstOrDefault();
            }
            else
            {
                preservada = abertas
                    .Where(c => c.IdEstabelecimento == idEstabelecimentoEntrada)
                    .OrderByDescending(ConversationTimestamp)
                    .FirstOrDefault();
            }

            if (preservada == null)
            {
                await _repositorio.FecharConversasAbertasPorTelefoneAsync(
                    telefoneE164,
                    idAgente: null,
                    motivo: "Troca de atendimento por telefone",
                    tipoFechamento: "manual");
                return new ConversationLookupResult(null, false);
            }

            var classificacao = await ClassificarConversaParaEntradaAsync(preservada, ehNumeroCentral);
            if (classificacao.ReiniciadaPorExpiracao)
            {
                return classificacao;
            }

            if (classificacao.Conversa == null)
            {
                return new ConversationLookupResult(null, false);
            }

            await _repositorio.FecharConversasAbertasPorTelefoneAsync(
                telefoneE164,
                idAgente: null,
                motivo: "Reconciliacao de conversa unica por telefone",
                tipoFechamento: "manual",
                preservarConversaId: classificacao.Conversa.IdConversa);

            _logger.LogInformation(
                "[Automation] Conversa preservada por telefone {Telefone}: {Conversa} (Central={Central})",
                telefoneE164,
                classificacao.Conversa.IdConversa,
                ehNumeroCentral);

            return new ConversationLookupResult(
                await _repositorio.ObterPorIdAsync(classificacao.Conversa.IdConversa) ?? classificacao.Conversa,
                false);
        }

        private async Task<ConversationLookupResult> ClassificarConversaParaEntradaAsync(Conversation candidata, bool ehNumeroCentral)
        {
            var controle = await _repositorio.ObterControleConversaAsync(candidata.IdConversa);
            if (controle == null)
            {
                return new ConversationLookupResult(null, false);
            }

            var atualizada = await _repositorio.ObterPorIdAsync(candidata.IdConversa) ?? candidata;
            if (controle.CanBotReply || controle.CanManualReply || IsHumanActiveStatus(controle.Status))
            {
                return new ConversationLookupResult(atualizada, false);
            }

            if (string.Equals(controle.Status, "encerrada_inatividade", StringComparison.OrdinalIgnoreCase))
            {
                var novaConversaId = await _conversationReset.RestartExpiredConversationAsync(candidata.IdConversa, ehNumeroCentral);
                var novaConversa = await _repositorio.ObterPorIdAsync(novaConversaId);
                if (novaConversa != null)
                {
                    _logger.LogInformation(
                        "[Automation] Conversa {ConversaAntiga} reiniciada automaticamente por expiracao. Nova conversa: {NovaConversa}",
                        candidata.IdConversa,
                        novaConversa.IdConversa);
                    return new ConversationLookupResult(novaConversa, true);
                }

                _logger.LogWarning(
                    "[Automation] Reinicio automatico por expiracao falhou ao carregar a nova conversa derivada de {ConversaAntiga}",
                    candidata.IdConversa);
                return new ConversationLookupResult(null, false);
            }

            if (string.Equals(controle.Status, "encerrada_manual", StringComparison.OrdinalIgnoreCase))
            {
                return new ConversationLookupResult(null, false);
            }

            return new ConversationLookupResult(null, false);
        }

        private bool EhNumeroCentral(Guid idEstabelecimento)
        {
            return _centralRouting.CentralEstabelecimentoId.HasValue &&
                   _centralRouting.CentralEstabelecimentoId.Value == idEstabelecimento;
        }

        private static DateTime ConversationTimestamp(Conversation conversa)
            => conversa.AtualizadoEm ?? conversa.CriadoEm;

        private static bool IsHumanActiveStatus(string? status)
            => string.Equals(status, "em_andamento", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "aguardando_cliente", StringComparison.OrdinalIgnoreCase)
               || string.Equals(status, "aguardando_interno", StringComparison.OrdinalIgnoreCase);

        private async Task<Guid?> ResolverEstabelecimentoAsync(string? displayPhoneNumber, string? phoneNumberId)
        {
            if (!string.IsNullOrWhiteSpace(displayPhoneNumber))
            {
                var idPorDisplay = await _wabaPhoneRepository.ObterIdEstabelecimentoPorDisplayPhoneAsync(displayPhoneNumber);
                if (idPorDisplay.HasValue && idPorDisplay.Value != Guid.Empty)
                {
                    _logger.LogDebug(
                        "Estabelecimento {IdEstabelecimento} encontrado para display={Display}",
                        idPorDisplay.Value,
                        displayPhoneNumber);
                    return idPorDisplay.Value;
                }

                _logger.LogWarning(
                    "Estabelecimento não encontrado para display_phone_number={Display}",
                    displayPhoneNumber);
            }

            if (!string.IsNullOrWhiteSpace(phoneNumberId))
            {
                var idPorPhoneNumberId = await _wabaPhoneRepository.ObterIdEstabelecimentoPorPhoneNumberIdAsync(phoneNumberId);
                if (idPorPhoneNumberId.HasValue && idPorPhoneNumberId.Value != Guid.Empty)
                {
                    _logger.LogInformation(
                        "Estabelecimento {IdEstabelecimento} resolvido via phone_number_id={PhoneNumberId} após falha no display_phone_number={Display}",
                        idPorPhoneNumberId.Value,
                        phoneNumberId,
                        displayPhoneNumber ?? "(null)");
                    return idPorPhoneNumberId.Value;
                }

                _logger.LogWarning(
                    "Estabelecimento não encontrado para phone_number_id={PhoneNumberId}",
                    phoneNumberId);
            }

            return null;
        }

        private sealed record ConversationLookupResult(Conversation? Conversa, bool ReiniciadaPorExpiracao);
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================

