using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class NauticaFlowService
    {
        public const string EstadoQuestionario   = "nautica_questionario";
        public const string EstadoConcluido      = "nautica_lead_concluido";
        public const string EstadoDesqualificado = "nautica_lead_desqualificado";
        public const string EstadoConsumidorFinal = "nautica_consumidor_final";

        private const string ChaveLeadId             = "nautica_lead_id";
        private const string ChaveEstabelecimentoId  = "nautica_estabelecimento_id";
        private const string ChaveEtapa              = "nautica_etapa";
        private const string ChaveViaNumeroCentral   = "nautica_via_numero_central";
        private const string ChaveCnpjTentativas     = "nautica_cnpj_tentativas";
        private const string ChaveConsumidorFinalStep = "nautica_cf_step";

        private const string EtapaNome          = "nome";
        private const string EtapaLojaFisica    = "loja_fisica";
        private const string EtapaNomeEmpresa   = "nome_empresa";
        private const string EtapaCnpj          = "cnpj";
        private const string EtapaPedidoMinimo  = "pedido_minimo";
        private const string EtapaCidadeEstado  = "cidade_estado";

        private static readonly HashSet<string> EtapasValidas = new(StringComparer.Ordinal)
        {
            EtapaNome, EtapaLojaFisica, EtapaNomeEmpresa, EtapaCnpj, EtapaPedidoMinimo, EtapaCidadeEstado
        };

        private const int MaxTentativasCnpj = 5;
        private const string DefaultNauticaCatalogUrl = "https://amazonnautica.com.br/catalogo";

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly INauticaLeadRepository _nauticaLeadRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<NauticaFlowService> _logger;
        private readonly string _catalogUrl;

        public NauticaFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            INauticaLeadRepository nauticaLeadRepository,
            CentralRoutingService centralRouting,
            IConfiguration configuration,
            ILogger<NauticaFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _nauticaLeadRepository = nauticaLeadRepository;
            _centralRouting = centralRouting;
            _catalogUrl = configuration["Automation:NauticaCatalogUrl"] ?? DefaultNauticaCatalogUrl;
            _logger = logger;
        }

        public async Task<AssistantDecision?> TryStartAfterCentralSelectionAsync(Guid idConversa)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsNauticaEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return null;
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo nautica sem telefone do cliente", idConversa);
                return null;
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var lead = await ObterOuCriarLeadAsync(
                idConversa,
                scope.IdEstabelecimento,
                scope.IdCliente,
                telefoneCliente,
                viaNumeroCentral: true);

            var etapa = DeterminarEtapaAtual(lead);
            if (string.Equals(etapa, EtapaNome, StringComparison.Ordinal))
            {
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true, 0);
                return CriarBoasVindas();
            }

            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true, 0);
            return CriarPergunta(etapa, incluirErro: false);
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsNauticaEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);

            if (string.Equals(contextoAtual?.Estado, EstadoConcluido, StringComparison.OrdinalIgnoreCase))
            {
                return (true, CriarMensagemPosConclusao());
            }

            if (string.Equals(contextoAtual?.Estado, EstadoConsumidorFinal, StringComparison.OrdinalIgnoreCase))
            {
                return (true, await ProcessarConsumidorFinalAsync(idConversa, contextoAtual));
            }

            if (string.Equals(contextoAtual?.Estado, EstadoDesqualificado, StringComparison.OrdinalIgnoreCase))
            {
                return (true, CriarMensagemPosDesqualificacao());
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo nautica sem telefone do cliente", idConversa);
                return (false, null);
            }

            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            var lead = await _nauticaLeadRepository.ObterLeadAbertoAsync(scope.IdEstabelecimento, telefoneCliente);
            if (lead == null)
            {
                lead = await ObterOuCriarLeadAsync(
                    idConversa,
                    scope.IdEstabelecimento,
                    scope.IdCliente,
                    telefoneCliente,
                    viaNumeroCentral);

                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral, 0);
                return (true, CriarBoasVindas());
            }

            var etapaContexto = ObterEtapaDoContexto(contextoAtual);
            // Ignorar etapas legadas removidas do novo fluxo
            if (etapaContexto != null && !EtapasValidas.Contains(etapaContexto))
            {
                etapaContexto = null;
            }

            var etapaAtual = etapaContexto ?? DeterminarEtapaAtual(lead);
            var conversaGrupoId = CentralRoutingService.GetGuidValue(
                contextoAtual?.DadosColetados,
                CentralRoutingService.ChaveConversaGrupoId);
            var ehSegmentoCentral = viaNumeroCentral &&
                                    conversaGrupoId.HasValue &&
                                    conversaGrupoId.Value != idConversa;
            var estadoCentral = string.Equals(contextoAtual?.Estado, CentralRoutingService.EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase) ||
                                string.Equals(contextoAtual?.Estado, CentralRoutingService.EstadoEstabelecimentoSelecionado, StringComparison.OrdinalIgnoreCase);

            if (ehSegmentoCentral &&
                estadoCentral &&
                string.IsNullOrWhiteSpace(etapaContexto) &&
                !string.Equals(etapaAtual, EtapaNome, StringComparison.Ordinal))
            {
                _logger.LogInformation(
                    "[Conversa={Conversa}] Restaurando contexto da nautica a partir do lead na etapa {Etapa}",
                    idConversa,
                    etapaAtual);

                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapaAtual, viaNumeroCentral, 0);
                contextoAtual = new ConversationContext
                {
                    Estado = EstadoQuestionario,
                    DadosColetados = BuildDadosContexto(contextoAtual, lead, etapaAtual, viaNumeroCentral, 0),
                    ExpiracaoEstado = null
                };
            }

            if (!string.Equals(contextoAtual?.Estado, EstadoQuestionario, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(etapaAtual, EtapaNome, StringComparison.Ordinal))
            {
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapaAtual, viaNumeroCentral, 0);
                return (true, CriarBoasVindas());
            }

            var tentativasCnpj = ObterTentativasCnpjDoContexto(contextoAtual);
            var decision = await ProcessarEtapaAsync(
                idConversa,
                contextoAtual,
                lead,
                etapaAtual,
                mensagemTexto,
                viaNumeroCentral,
                tentativasCnpj);

            return (true, decision);
        }

        public async Task<bool> IsNauticaEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var tipo = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(idEstabelecimento);
            return string.Equals(tipo, "nautica", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<AssistantDecision> ProcessarEtapaAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            NauticaLead lead,
            string etapaAtual,
            string mensagemTexto,
            bool viaNumeroCentral,
            int tentativasCnpj)
        {
            switch (etapaAtual)
            {
                case EtapaNome:
                {
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral, tentativasCnpj);
                        return CriarPergunta(EtapaNome, incluirErro: false);
                    }

                    lead.NomeCliente = mensagemTexto.Trim();
                    await _nauticaLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaLojaFisica, viaNumeroCentral, tentativasCnpj);
                    return CriarPergunta(EtapaLojaFisica, incluirErro: false);
                }

                case EtapaLojaFisica:
                {
                    var temLoja = ParseSimNao(mensagemTexto);
                    if (temLoja == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaLojaFisica, viaNumeroCentral, tentativasCnpj);
                        return CriarPergunta(EtapaLojaFisica, incluirErro: true);
                    }

                    lead.TemLojaFisica = temLoja.Value;
                    await _nauticaLeadRepository.AtualizarAsync(lead);

                    if (!temLoja.Value)
                    {
                        await _nauticaLeadRepository.DesqualificarAsync(lead, "consumidor_final");
                        await SalvarConsumidorFinalContextoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                        return CriarMensagemInicialAmazonPrime();
                    }

                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNomeEmpresa, viaNumeroCentral, tentativasCnpj);
                    return CriarPergunta(EtapaNomeEmpresa, incluirErro: false);
                }

                case EtapaNomeEmpresa:
                {
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNomeEmpresa, viaNumeroCentral, tentativasCnpj);
                        return CriarPergunta(EtapaNomeEmpresa, incluirErro: false);
                    }

                    lead.NomeEmpresa = mensagemTexto.Trim();
                    await _nauticaLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCnpj, viaNumeroCentral, tentativasCnpj);
                    return CriarPergunta(EtapaCnpj, incluirErro: false);
                }

                case EtapaCnpj:
                {
                    var cnpjDigitos = SanitizarCnpj(mensagemTexto);
                    if (!ValidarCnpj(cnpjDigitos))
                    {
                        var novasTentativas = tentativasCnpj + 1;
                        if (novasTentativas >= MaxTentativasCnpj)
                        {
                            await SalvarContextoDesqualificadoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                            return CriarMensagemDesqualificadoCnpjInvalido();
                        }

                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCnpj, viaNumeroCentral, novasTentativas);
                        return CriarPerguntaCnpjComErro(novasTentativas);
                    }

                    lead.Cnpj = cnpjDigitos;
                    await _nauticaLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaPedidoMinimo, viaNumeroCentral, 0);
                    return CriarPergunta(EtapaPedidoMinimo, incluirErro: false);
                }

                case EtapaPedidoMinimo:
                {
                    var consegue = ParseSimNao(mensagemTexto);
                    if (consegue == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaPedidoMinimo, viaNumeroCentral, tentativasCnpj);
                        return CriarPergunta(EtapaPedidoMinimo, incluirErro: true);
                    }

                    lead.ConsegueMinimo = consegue.Value;
                    await _nauticaLeadRepository.AtualizarAsync(lead);

                    if (!consegue.Value)
                    {
                        await _nauticaLeadRepository.DesqualificarAsync(lead, "lojista");
                        await SalvarContextoDesqualificadoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                        return CriarMensagemDesqualificadoPedidoMinimo();
                    }

                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCidadeEstado, viaNumeroCentral, tentativasCnpj);
                    return CriarPergunta(EtapaCidadeEstado, incluirErro: false);
                }

                case EtapaCidadeEstado:
                {
                    // Compatibilidade com lead antigo: se ja tem cidade_estado, concluir imediatamente
                    if (!string.IsNullOrWhiteSpace(lead.CidadeEstado))
                    {
                        await _nauticaLeadRepository.ConcluirAsync(lead);
                        await SalvarContextoConcluidoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                        return CriarMensagemConclusao();
                    }

                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaCidadeEstado, viaNumeroCentral, tentativasCnpj);
                        return CriarPergunta(EtapaCidadeEstado, incluirErro: false);
                    }

                    lead.CidadeEstado = mensagemTexto.Trim();
                    await _nauticaLeadRepository.ConcluirAsync(lead);
                    await SalvarContextoConcluidoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                    return CriarMensagemConclusao();
                }

                default:
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral, tentativasCnpj);
                    return CriarBoasVindas();
            }
        }

        private async Task<AssistantDecision> ProcessarConsumidorFinalAsync(
            Guid idConversa,
            ConversationContext? contextoAtual)
        {
            var step = ObterConsumidorFinalStepDoContexto(contextoAtual);
            if (step == 0)
            {
                var dados = contextoAtual?.DadosColetados != null
                    ? new Dictionary<string, object>(contextoAtual.DadosColetados)
                    : new Dictionary<string, object>();
                dados[ChaveConsumidorFinalStep] = 1;

                await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
                {
                    Estado = EstadoConsumidorFinal,
                    DadosColetados = dados,
                    ExpiracaoEstado = null
                });

                return CriarFichaTecnicaAmazonPrime();
            }

            return CriarMensagemTerminalConsumidorFinal();
        }

        private async Task<NauticaLead> ObterOuCriarLeadAsync(
            Guid idConversa,
            Guid idEstabelecimento,
            Guid idCliente,
            string telefoneCliente,
            bool viaNumeroCentral)
        {
            var existente = await _nauticaLeadRepository.ObterLeadAbertoAsync(idEstabelecimento, telefoneCliente);
            if (existente != null)
            {
                existente.IdConversa = idConversa;
                existente.IdCliente = idCliente;
                existente.TelefoneE164 = telefoneCliente;
                existente.ViaNumeroCentral = viaNumeroCentral;
                await _nauticaLeadRepository.AtualizarAsync(existente);
                return existente;
            }

            var lead = new NauticaLead
            {
                Id = Guid.NewGuid(),
                IdConversa = idConversa,
                IdCliente = idCliente,
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneCliente,
                ViaNumeroCentral = viaNumeroCentral,
                Status = "incompleto",
                DataCriacao = DateTime.UtcNow,
                DataAtualizacao = DateTime.UtcNow
            };

            await _nauticaLeadRepository.CriarAsync(lead);
            return lead;
        }

        private async Task<string?> GarantirTelefoneClienteAsync(EffectiveConversationScope scope)
        {
            if (!string.IsNullOrWhiteSpace(scope.TelefoneCliente))
            {
                return scope.TelefoneCliente;
            }

            if (scope.IdCliente == Guid.Empty || scope.IdEstabelecimento == Guid.Empty)
            {
                return null;
            }

            return await _clienteRepository.ObterTelefoneClienteAsync(scope.IdCliente, scope.IdEstabelecimento);
        }

        private async Task SalvarContextoQuestionarioAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            NauticaLead lead,
            string etapa,
            bool viaNumeroCentral,
            int tentativasCnpj)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoQuestionario,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, etapa, viaNumeroCentral, tentativasCnpj),
                ExpiracaoEstado = null
            });
        }

        private async Task SalvarContextoConcluidoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            NauticaLead lead,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoConcluido,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, EtapaCidadeEstado, viaNumeroCentral, 0),
                ExpiracaoEstado = null
            });
        }

        private async Task SalvarContextoDesqualificadoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            NauticaLead lead,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoDesqualificado,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, string.Empty, viaNumeroCentral, 0),
                ExpiracaoEstado = null
            });
        }

        private async Task SalvarConsumidorFinalContextoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            NauticaLead lead,
            bool viaNumeroCentral)
        {
            var dados = BuildDadosContexto(contextoAtual, lead, string.Empty, viaNumeroCentral, 0);
            dados[ChaveConsumidorFinalStep] = 0;

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoConsumidorFinal,
                DadosColetados = dados,
                ExpiracaoEstado = null
            });
        }

        private Dictionary<string, object> BuildDadosContexto(
            ConversationContext? contextoAtual,
            NauticaLead lead,
            string etapa,
            bool viaNumeroCentral,
            int tentativasCnpj)
        {
            var dados = new Dictionary<string, object>
            {
                [ChaveLeadId] = lead.Id.ToString(),
                [ChaveEstabelecimentoId] = lead.IdEstabelecimento.ToString(),
                [ChaveEtapa] = etapa,
                [ChaveViaNumeroCentral] = viaNumeroCentral,
                [ChaveCnpjTentativas] = tentativasCnpj
            };

            if (contextoAtual?.DadosColetados != null)
            {
                var centralKeys = new[]
                {
                    CentralRoutingService.ChaveConversaGrupoId,
                    CentralRoutingService.ChaveConversaSegmentoAtivaId,
                    CentralRoutingService.ChaveEstabelecimentoEscolhidoId,
                    CentralRoutingService.ChaveEstabelecimentoEscolhidoNome,
                    CentralRoutingService.ChaveEstabelecimentoEscolhidoTipo
                };

                foreach (var key in centralKeys)
                {
                    if (contextoAtual.DadosColetados.TryGetValue(key, out var val))
                    {
                        dados[key] = val;
                    }
                }
            }

            return dados;
        }

        private static string? ObterEtapaDoContexto(ConversationContext? contexto)
        {
            return CentralRoutingService.GetStringValue(contexto?.DadosColetados, ChaveEtapa);
        }

        private static int ObterTentativasCnpjDoContexto(ConversationContext? contexto)
        {
            if (contexto?.DadosColetados == null)
            {
                return 0;
            }

            if (contexto.DadosColetados.TryGetValue(ChaveCnpjTentativas, out var val))
            {
                if (val is int i) return i;
                if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
            }

            return 0;
        }

        private static int ObterConsumidorFinalStepDoContexto(ConversationContext? contexto)
        {
            if (contexto?.DadosColetados == null)
            {
                return 0;
            }

            if (contexto.DadosColetados.TryGetValue(ChaveConsumidorFinalStep, out var val))
            {
                if (val is int i) return i;
                if (int.TryParse(val?.ToString(), out var parsed)) return parsed;
            }

            return 0;
        }

        internal static string DeterminarEtapaAtual(NauticaLead lead)
        {
            if (string.IsNullOrWhiteSpace(lead.NomeCliente))  return EtapaNome;
            if (lead.TemLojaFisica == null)                   return EtapaLojaFisica;
            if (string.IsNullOrWhiteSpace(lead.NomeEmpresa))  return EtapaNomeEmpresa;
            if (string.IsNullOrWhiteSpace(lead.Cnpj))         return EtapaCnpj;
            if (lead.ConsegueMinimo == null)                   return EtapaPedidoMinimo;
            return EtapaCidadeEstado;
        }

        // ─── Messages ────────────────────────────────────────────────────────────

        private static AssistantDecision CriarBoasVindas()
        {
            var mensagem = "Ola, tudo bem? 👋\n\nAqui e o Claudelino, da Amazon Nautica ⚓🚤\n\nVou fazer algumas perguntas rapidas para entender melhor o seu perfil e ja te enviar as informacoes certas.\n\nPara comecar: qual o seu nome?";
            return new AssistantDecision(mensagem, "none", null, false, null);
        }

        private AssistantDecision CriarPergunta(string etapa, bool incluirErro)
        {
            return etapa switch
            {
                EtapaNome => new AssistantDecision(
                    "Qual o seu nome?",
                    "none", null, false, null),

                EtapaLojaFisica => new AssistantDecision(
                    incluirErro
                        ? "Nao consegui identificar sua resposta. 😊\n\nVoce possui endereco de loja fisica?"
                        : "Voce possui endereco de loja fisica?",
                    "none", null, false, null, null, BuildSimNaoButtons()),

                EtapaNomeEmpresa => new AssistantDecision(
                    "Otimo! Qual o nome da sua empresa?",
                    "none", null, false, null),

                EtapaCnpj => new AssistantDecision(
                    "Qual o CNPJ da sua empresa? 📋",
                    "none", null, false, null),

                EtapaPedidoMinimo => new AssistantDecision(
                    incluirErro
                        ? "Nao consegui identificar sua resposta. 😊\n\nConsegue realizar pedidos minimos de 3 embarcacoes? (Investimento medio de R$ 39.000)"
                        : "Consegue realizar pedidos minimos de 3 embarcacoes? (Investimento medio de R$ 39.000)",
                    "none", null, false, null, null, BuildSimNaoButtons()),

                EtapaCidadeEstado => new AssistantDecision(
                    "Em qual cidade e estado fica sua loja?",
                    "none", null, false, null),

                _ => CriarBoasVindas()
            };
        }

        private static AssistantDecision CriarPerguntaCnpjComErro(int tentativas)
        {
            var restantes = MaxTentativasCnpj - tentativas;
            return new AssistantDecision(
                $"CNPJ invalido. Por favor, verifique e tente novamente. ({restantes} tentativa{(restantes == 1 ? "" : "s")} restante{(restantes == 1 ? "" : "s")})\n\nQual o CNPJ da sua empresa?",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemInicialAmazonPrime()
        {
            return new AssistantDecision(
                "Para consumidor final, hoje trabalhamos com o *Amazon Prime*, um barco lancamento pensado para quem busca durabilidade, praticidade e baixo custo de manutencao.\n\nEsse modelo oferece:\n✅ Casco 100% soldado\n✅ Zero manutencao\n✅ Piso nautico instalado\n✅ Preparado para motorizacao de ate 50 HP\n\n💰 Valor: R$ 39.990\n\nSe quiser, posso te passar mais informacoes sobre esse barco.",
                "none", null, false, null);
        }

        private static AssistantDecision CriarFichaTecnicaAmazonPrime()
        {
            return new AssistantDecision(
                "📋 *Especificacoes do modelo:*\n\n• Pintura poliester automotiva premium\n• Porta documento na proa\n• Tubulacao 1\" para conducao de fios para porta-documento e motor eletrico\n• 2 caixas secas na proa, caixa de varas e porta-baterias\n• Caixa termica\n• Tomada de engate rapido na proa para motor eletrico\n• 2 bombas de porao\n• Base para motor eletrico de pedal\n• Viveiro com aerador\n• 3 plataformas para pesca esportiva\n• Viveiro com separador de isca viva\n• Porta-tanque com tampa superior\n• Porta-baterias de popa para motor com partida eletrica\n• Bases em inox para cadeira giratoria\n• Piso nautico\n\n📐 *Dados tecnicos:*\n\n• Comprimento: 6M\n• Boca moldada: 1,80 cm\n• Peso maximo de carga: 0,750 t\n• Pontal: 0,65 cm\n• Calado: 0,15 cm\n• Casco: 3,00 mm\n• Peso aproximado: 280 kg\n• Potencia maxima: 50 HP",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemTerminalConsumidorFinal()
        {
            return new AssistantDecision(
                "Seu contato ja foi registrado. Obrigado pelo interesse na Amazon Nautica! 🙏",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemDesqualificadoPedidoMinimo()
        {
            return new AssistantDecision(
                "Entendido! Nosso modelo de parceria exige pedidos minimos de 3 embarcacoes.\n\nQuando estiver preparado para esse volume, adorariamos conversar. Obrigado pelo interesse! 🙏",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemDesqualificadoCnpjInvalido()
        {
            return new AssistantDecision(
                "Nao conseguimos validar seu CNPJ apos varias tentativas.\n\nEntre em contato pelo nosso site ou telefone para darmos continuidade ao seu cadastro. Obrigado! 🙏",
                "none", null, false, null);
        }

        private AssistantDecision CriarMensagemConclusao()
        {
            return new AssistantDecision(
                $"Perfeito! 🚤✨\n\nVamos te enviar o catalogo agora. Me fala os modelos que voce se interessou que ja retorno com a sua cotacao personalizada! 😊\n\n📄 Catalogo Amazon Nautica: {_catalogUrl}",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemPosConclusao()
        {
            return new AssistantDecision(
                "Suas informacoes ja foram registradas ✅\n\nEm breve nossa equipe comercial entra em contato com a sua cotacao. 🚤",
                "none", null, false, null);
        }

        private static AssistantDecision CriarMensagemPosDesqualificacao()
        {
            return new AssistantDecision(
                "Seu contato ja foi registrado. Obrigado pelo interesse na Amazon Nautica! 🙏",
                "none", null, false, null);
        }

        // ─── Button builders ─────────────────────────────────────────────────────

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildSimNaoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("nautica_sim", "Sim ✅"),
                new WhatsAppReplyButtonOption("nautica_nao", "Nao ❌")
            };
        }

        // ─── Parse helpers ───────────────────────────────────────────────────────

        private static bool? ParseSimNao(string? texto)
        {
            var n = NormalizeText(texto);
            return n switch
            {
                "1" or "sim" or "s" or "nautica_sim" => true,
                "2" or "nao" or "n" or "nautica_nao" => false,
                _ => null
            };
        }

        // ─── CNPJ validation ─────────────────────────────────────────────────────

        internal static string SanitizarCnpj(string? input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                return string.Empty;
            }

            return new string(input.Where(char.IsDigit).ToArray());
        }

        internal static bool ValidarCnpj(string? cnpj)
        {
            var digits = SanitizarCnpj(cnpj);
            if (digits.Length != 14)
            {
                return false;
            }

            if (digits.Distinct().Count() == 1)
            {
                return false;
            }

            int[] w1 = { 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
            var sum1 = 0;
            for (var i = 0; i < 12; i++)
            {
                sum1 += (digits[i] - '0') * w1[i];
            }

            var rem1 = sum1 % 11;
            var d1 = rem1 < 2 ? 0 : 11 - rem1;
            if ((digits[12] - '0') != d1)
            {
                return false;
            }

            int[] w2 = { 6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2 };
            var sum2 = 0;
            for (var i = 0; i < 13; i++)
            {
                sum2 += (digits[i] - '0') * w2[i];
            }

            var rem2 = sum2 % 11;
            var d2 = rem2 < 2 ? 0 : 11 - rem2;
            return (digits[13] - '0') == d2;
        }

        // ─── Text normalization ───────────────────────────────────────────────────

        private static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch) || ch == '_' || char.IsWhiteSpace(ch))
                {
                    builder.Append(char.ToLowerInvariant(ch));
                    continue;
                }

                builder.Append(' ');
            }

            var flattened = builder.ToString().Normalize(NormalizationForm.FormC);
            return string.Join(" ", flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }
    }
}
