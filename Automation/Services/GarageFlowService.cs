using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class GarageFlowService
    {
        public const string EstadoQuestionario = "garagem_questionario";
        public const string EstadoConcluido = "garagem_lead_concluido";

        private const string ChaveLeadId = "garagem_lead_id";
        private const string ChaveEstabelecimentoId = "garagem_estabelecimento_id";
        private const string ChaveEtapa = "garagem_etapa";
        private const string ChaveViaNumeroCentral = "garagem_via_numero_central";
        private const string ChaveNomeCliente = "garagem_nome_cliente";
        private const string ChaveObjetivo = "garagem_objetivo";
        private const string ChaveModeloInteresse = "garagem_modelo_interesse";
        private const string ChaveFaixaInvestimento = "garagem_faixa_investimento";
        private const string ChaveFormaPagamento = "garagem_forma_pagamento";
        private const string ChaveValorEntrada = "garagem_valor_entrada_texto";
        private const string ChaveUrgencia = "garagem_urgencia";

        private const string EtapaNome = "nome";
        private const string EtapaObjetivo = "objetivo";
        private const string EtapaModelo = "modelo";
        private const string EtapaFaixaInvestimento = "faixa_investimento";
        private const string EtapaFormaPagamento = "forma_pagamento";
        private const string EtapaValorEntrada = "valor_entrada";
        private const string EtapaUrgencia = "urgencia";

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IGaragemLeadRepository _garagemLeadRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<GarageFlowService> _logger;

        public GarageFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IGaragemLeadRepository garagemLeadRepository,
            CentralRoutingService centralRouting,
            ILogger<GarageFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _garagemLeadRepository = garagemLeadRepository;
            _centralRouting = centralRouting;
            _logger = logger;
        }

        public async Task<AssistantDecision?> TryStartAfterCentralSelectionAsync(Guid idConversa)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsGarageEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return null;
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo garagem sem telefone do cliente", idConversa);
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
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);

            if (string.Equals(etapa, EtapaNome, StringComparison.Ordinal))
            {
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true);
                return CriarBoasVindas(nomeEstabelecimento);
            }

            await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapa, true);
            return CriarPergunta(etapa, lead, nomeEstabelecimento, incluirErro: false);
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsGarageEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            if (string.Equals(contextoAtual?.Estado, EstadoConcluido, StringComparison.OrdinalIgnoreCase))
            {
                return (true, CriarMensagemPosConclusao());
            }

            var telefoneCliente = await GarantirTelefoneClienteAsync(scope);
            if (string.IsNullOrWhiteSpace(telefoneCliente))
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo garagem sem telefone do cliente", idConversa);
                return (false, null);
            }

            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            var lead = await _garagemLeadRepository.ObterLeadAbertoAsync(scope.IdEstabelecimento, telefoneCliente);
            if (lead == null)
            {
                lead = await ObterOuCriarLeadAsync(
                    idConversa,
                    scope.IdEstabelecimento,
                    scope.IdCliente,
                    telefoneCliente,
                    viaNumeroCentral);

                var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                return (true, CriarBoasVindas(nomeEstabelecimento));
            }

            var etapaAtual = ObterEtapaDoContexto(contextoAtual) ?? DeterminarEtapaAtual(lead);
            if (!string.Equals(contextoAtual?.Estado, EstadoQuestionario, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(etapaAtual, EtapaNome, StringComparison.Ordinal))
            {
                var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);
                await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, etapaAtual, viaNumeroCentral);
                return (true, CriarBoasVindas(nomeEstabelecimento));
            }

            var decision = await ProcessarEtapaAsync(
                idConversa,
                contextoAtual,
                lead,
                etapaAtual,
                mensagemTexto,
                viaNumeroCentral,
                scope.IdEstabelecimento);

            return (true, decision);
        }

        public async Task<bool> IsGarageEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var modulos = await _estabelecimentoRepository.ObterModulosAtivosAsync(idEstabelecimento);
            return modulos.Any(modulo => string.Equals(modulo, "GARAGEM", StringComparison.OrdinalIgnoreCase));
        }

        private async Task<AssistantDecision> ProcessarEtapaAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapaAtual,
            string mensagemTexto,
            bool viaNumeroCentral,
            Guid idEstabelecimento)
        {
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(idEstabelecimento);

            switch (etapaAtual)
            {
                case EtapaNome:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                        return CriarBoasVindas(nomeEstabelecimento);
                    }

                    lead.NomeCliente = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaObjetivo, viaNumeroCentral);
                    return CriarPergunta(EtapaObjetivo, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaObjetivo:
                {
                    var objetivo = ParseObjetivo(mensagemTexto);
                    if (objetivo == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaObjetivo, viaNumeroCentral);
                        return CriarPergunta(EtapaObjetivo, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.Objetivo = objetivo;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaModelo, viaNumeroCentral);
                    return CriarPergunta(EtapaModelo, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.ModeloInteresse = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFaixaInvestimento, viaNumeroCentral);
                    return CriarPergunta(EtapaFaixaInvestimento, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaFaixaInvestimento:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFaixaInvestimento, viaNumeroCentral);
                        return CriarPergunta(EtapaFaixaInvestimento, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.FaixaInvestimento = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFormaPagamento, viaNumeroCentral);
                    return CriarPergunta(EtapaFormaPagamento, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaFormaPagamento:
                {
                    var formaPagamento = ParseFormaPagamento(mensagemTexto);
                    if (formaPagamento == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaFormaPagamento, viaNumeroCentral);
                        return CriarPergunta(EtapaFormaPagamento, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.FormaPagamento = formaPagamento;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaValorEntrada, viaNumeroCentral);
                    return CriarPergunta(EtapaValorEntrada, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaValorEntrada:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaValorEntrada, viaNumeroCentral);
                        return CriarPergunta(EtapaValorEntrada, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.ValorEntradaTexto = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaUrgencia:
                {
                    var urgencia = ParseUrgencia(mensagemTexto);
                    if (urgencia == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                        return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.Urgencia = urgencia;
                    await _garagemLeadRepository.ConcluirAsync(
                        lead.Id,
                        lead.NomeCliente ?? string.Empty,
                        lead.Objetivo ?? string.Empty,
                        lead.ModeloInteresse ?? string.Empty,
                        lead.FaixaInvestimento ?? string.Empty,
                        lead.FormaPagamento ?? string.Empty,
                        lead.ValorEntradaTexto ?? string.Empty,
                        urgencia);

                    lead.Status = "concluido";
                    lead.DataConclusao = DateTime.UtcNow;
                    await SalvarContextoConcluidoAsync(idConversa, contextoAtual, lead, viaNumeroCentral);
                    return CriarMensagemConclusao(lead.NomeCliente);
                }

                default:
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaNome, viaNumeroCentral);
                    return CriarBoasVindas(nomeEstabelecimento);
            }
        }

        private async Task<GarageLead> ObterOuCriarLeadAsync(
            Guid idConversa,
            Guid idEstabelecimento,
            Guid idCliente,
            string telefoneCliente,
            bool viaNumeroCentral)
        {
            var existente = await _garagemLeadRepository.ObterLeadAbertoAsync(idEstabelecimento, telefoneCliente);
            if (existente != null)
            {
                existente.IdConversa = idConversa;
                existente.IdCliente = idCliente;
                existente.TelefoneE164 = telefoneCliente;
                existente.ViaNumeroCentral = viaNumeroCentral;
                await _garagemLeadRepository.AtualizarAsync(existente);
                return existente;
            }

            var lead = new GarageLead
            {
                Id = Guid.NewGuid(),
                IdConversa = idConversa,
                IdCliente = idCliente,
                IdEstabelecimento = idEstabelecimento,
                TelefoneE164 = telefoneCliente,
                ViaNumeroCentral = viaNumeroCentral,
                Status = "em_andamento",
                DataCriacao = DateTime.UtcNow,
                DataAtualizacao = DateTime.UtcNow
            };

            await _garagemLeadRepository.CriarAsync(lead);
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

        private async Task<string> ObterNomeEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var nome = await _estabelecimentoRepository.ObterNomeFantasiaAsync(idEstabelecimento);
            return string.IsNullOrWhiteSpace(nome) ? "Brasil Motors" : nome.Trim();
        }

        private async Task SalvarContextoQuestionarioAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapa,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoQuestionario,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, etapa, viaNumeroCentral),
                ExpiracaoEstado = null
            });
        }

        private async Task SalvarContextoConcluidoAsync(
            Guid idConversa,
            ConversationContext? contextoAtual,
            GarageLead lead,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoConcluido,
                DadosColetados = BuildDadosContexto(contextoAtual, lead, EtapaUrgencia, viaNumeroCentral),
                ExpiracaoEstado = null
            });
        }

        private Dictionary<string, object> BuildDadosContexto(
            ConversationContext? contextoAtual,
            GarageLead lead,
            string etapa,
            bool viaNumeroCentral)
        {
            var dados = new Dictionary<string, object>
            {
                [ChaveLeadId] = lead.Id.ToString(),
                [ChaveEstabelecimentoId] = lead.IdEstabelecimento.ToString(),
                [ChaveEtapa] = etapa,
                [ChaveViaNumeroCentral] = viaNumeroCentral
            };

            if (!string.IsNullOrWhiteSpace(lead.NomeCliente))
            {
                dados[ChaveNomeCliente] = lead.NomeCliente!;
            }

            if (!string.IsNullOrWhiteSpace(lead.Objetivo))
            {
                dados[ChaveObjetivo] = lead.Objetivo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.ModeloInteresse))
            {
                dados[ChaveModeloInteresse] = lead.ModeloInteresse!;
            }

            if (!string.IsNullOrWhiteSpace(lead.FaixaInvestimento))
            {
                dados[ChaveFaixaInvestimento] = lead.FaixaInvestimento!;
            }

            if (!string.IsNullOrWhiteSpace(lead.FormaPagamento))
            {
                dados[ChaveFormaPagamento] = lead.FormaPagamento!;
            }

            if (!string.IsNullOrWhiteSpace(lead.ValorEntradaTexto))
            {
                dados[ChaveValorEntrada] = lead.ValorEntradaTexto!;
            }

            if (!string.IsNullOrWhiteSpace(lead.Urgencia))
            {
                dados[ChaveUrgencia] = lead.Urgencia!;
            }

            if (viaNumeroCentral)
            {
                var snapshot = CentralRoutingService.BuildSnapshot(contextoAtual);
                if (!string.IsNullOrWhiteSpace(snapshot.CentralDisplayPhone))
                {
                    dados[CentralRoutingService.ChaveCentralDisplayPhone] = snapshot.CentralDisplayPhone!;
                }

                if (snapshot.EstabelecimentoId.HasValue)
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoId] = snapshot.EstabelecimentoId.Value.ToString();
                }

                if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoNome))
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoNome] = snapshot.EstabelecimentoNome!;
                }

                if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoTipo))
                {
                    dados[CentralRoutingService.ChaveEstabelecimentoEscolhidoTipo] = snapshot.EstabelecimentoTipo!;
                }
            }

            return dados;
        }

        private static AssistantDecision CriarBoasVindas(string nomeEstabelecimento)
        {
            var mensagem = $"Ola, tudo bem? 👋\n\nSeja bem-vindo(a) a {nomeEstabelecimento} 🚗✨\n\nVou te fazer algumas perguntas rapidas para que nosso vendedor te chame ja com uma simulacao mais assertiva, combinado?\n\nQual e o seu nome?";
            return new AssistantDecision(mensagem, "none", null, false, null);
        }

        private AssistantDecision CriarPergunta(string etapa, GarageLead lead, string nomeEstabelecimento, bool incluirErro)
        {
            return etapa switch
            {
                EtapaObjetivo => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : $"Perfeito, {ExtrairPrimeiroNome(lead.NomeCliente)}! Agora me diz uma coisa 😊",
                        "Voce quer:"),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildObjetivoButtons()),
                EtapaModelo => new AssistantDecision(
                    "Voce ja tem algum modelo em mente ou ainda esta pesquisando? 🔍",
                    "none",
                    null,
                    false,
                    null),
                EtapaFaixaInvestimento => new AssistantDecision(
                    "Em qual faixa voce pretende investir? 💵\n\nExemplo: ate 60 mil, entre 80 e 100 mil, ainda nao defini.",
                    "none",
                    null,
                    false,
                    null),
                EtapaFormaPagamento => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Como voce pretende fazer a compra?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildFormaPagamentoButtons()),
                EtapaValorEntrada => new AssistantDecision(
                    "Qual valor voce consegue dar de entrada? 💰\n\nSe for sem entrada, pode me responder assim mesmo.",
                    "none",
                    null,
                    false,
                    null),
                EtapaUrgencia => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Pra quando voce pretende resolver isso?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildUrgenciaButtons()),
                _ => CriarBoasVindas(nomeEstabelecimento)
            };
        }

        private static AssistantDecision CriarMensagemConclusao(string? nomeCliente)
        {
            var prefixo = string.IsNullOrWhiteSpace(nomeCliente)
                ? "Perfeito! 🚗✨"
                : $"Perfeito, {ExtrairPrimeiroNome(nomeCliente)}! 🚗✨";

            return new AssistantDecision(
                $"{prefixo}\n\nJa encaminhei suas informacoes para o nosso vendedor.\nEm breve ele vai te chamar ja com uma simulacao 😉",
                "none",
                null,
                false,
                null);
        }

        private static AssistantDecision CriarMensagemPosConclusao()
        {
            return new AssistantDecision(
                "Suas informacoes ja foram enviadas para o vendedor ✅\n\nEm breve ele entra em contato com voce com a simulacao 😉",
                "none",
                null,
                false,
                null);
        }

        private static string BuildStructuredPrompt(string titulo, string subtitulo)
        {
            var builder = new StringBuilder();
            builder.AppendLine(titulo);

            if (!string.IsNullOrWhiteSpace(subtitulo))
            {
                builder.AppendLine();
                builder.Append(subtitulo);
            }

            return builder.ToString().Trim();
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildObjetivoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_objetivo_comprar", "Comprar 🚘"),
                new WhatsAppReplyButtonOption("garagem_objetivo_vender", "Vender 💰"),
                new WhatsAppReplyButtonOption("garagem_objetivo_trocar", "Trocar 🔄")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildFormaPagamentoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_pagamento_avista", "A vista 💸"),
                new WhatsAppReplyButtonOption("garagem_pagamento_entrada_financiamento", "Entrada + Fin."),
                new WhatsAppReplyButtonOption("garagem_pagamento_financiado_100", "100% financ.")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildUrgenciaButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_urgencia_esta_semana", "Essa semana ⚡"),
                new WhatsAppReplyButtonOption("garagem_urgencia_proximas_semanas", "Prox. semanas"),
                new WhatsAppReplyButtonOption("garagem_urgencia_so_pesquisando", "So pesquisando")
            };
        }

        private static string? ParseObjetivo(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "comprar" or "garagem_objetivo_comprar" => "comprar",
                "2" or "vender" or "garagem_objetivo_vender" => "vender",
                "3" or "trocar" or "garagem_objetivo_trocar" => "trocar",
                _ => null
            };
        }

        private static string? ParseFormaPagamento(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "avista" or "a vista" or "garagem_pagamento_avista" => "avista",
                "2" or "entrada financiamento" or "entrada + financiamento" or "garagem_pagamento_entrada_financiamento" => "entrada_financiamento",
                "3" or "financiado" or "100 financiado" or "100% financiado" or "garagem_pagamento_financiado_100" => "financiado_100",
                _ => null
            };
        }

        private static string? ParseUrgencia(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "ainda essa semana" or "essa semana" or "garagem_urgencia_esta_semana" => "esta_semana",
                "2" or "proximas semanas" or "nas proximas semanas" or "garagem_urgencia_proximas_semanas" => "proximas_semanas",
                "3" or "so pesquisando" or "garagem_urgencia_so_pesquisando" => "so_pesquisando",
                _ => null
            };
        }

        private static string DeterminarEtapaAtual(GarageLead lead)
        {
            if (string.IsNullOrWhiteSpace(lead.NomeCliente))
            {
                return EtapaNome;
            }

            if (string.IsNullOrWhiteSpace(lead.Objetivo))
            {
                return EtapaObjetivo;
            }

            if (string.IsNullOrWhiteSpace(lead.ModeloInteresse))
            {
                return EtapaModelo;
            }

            if (string.IsNullOrWhiteSpace(lead.FaixaInvestimento))
            {
                return EtapaFaixaInvestimento;
            }

            if (string.IsNullOrWhiteSpace(lead.FormaPagamento))
            {
                return EtapaFormaPagamento;
            }

            if (string.IsNullOrWhiteSpace(lead.ValorEntradaTexto))
            {
                return EtapaValorEntrada;
            }

            if (string.IsNullOrWhiteSpace(lead.Urgencia))
            {
                return EtapaUrgencia;
            }

            return EtapaUrgencia;
        }

        private static string? ObterEtapaDoContexto(ConversationContext? contexto)
        {
            return CentralRoutingService.GetStringValue(contexto?.DadosColetados, ChaveEtapa);
        }

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
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            var flattened = builder.ToString().Normalize(NormalizationForm.FormC);
            return string.Join(" ", flattened.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        }

        private static string ExtrairPrimeiroNome(string? nomeCompleto)
        {
            if (string.IsNullOrWhiteSpace(nomeCompleto))
            {
                return "cliente";
            }

            return nomeCompleto
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? "cliente";
        }
    }
}
