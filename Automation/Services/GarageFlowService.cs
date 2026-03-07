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
        private const string ChaveTrocaModeloAtual = "garagem_troca_modelo_atual";
        private const string ChaveTrocaAnoModelo = "garagem_troca_ano_modelo";
        private const string ChaveTrocaKm = "garagem_troca_km";
        private const string ChaveTrocaQuitado = "garagem_troca_quitado";
        private const string ChaveTrocaModeloDesejado = "garagem_troca_modelo_desejado";
        private const string ChaveTrocaCondicao = "garagem_troca_condicao";
        private const string ChaveVendaModelo = "garagem_venda_modelo";
        private const string ChaveVendaAno = "garagem_venda_ano";
        private const string ChaveVendaKm = "garagem_venda_km";
        private const string ChaveVendaQuitado = "garagem_venda_quitado";

        private const string EtapaNome = "nome";
        private const string EtapaObjetivo = "objetivo";
        private const string EtapaModelo = "modelo";
        private const string EtapaFaixaInvestimento = "faixa_investimento";
        private const string EtapaFormaPagamento = "forma_pagamento";
        private const string EtapaValorEntrada = "valor_entrada";
        private const string EtapaTrocaModeloAtual = "troca_modelo_atual";
        private const string EtapaTrocaAnoModelo = "troca_ano_modelo";
        private const string EtapaTrocaKm = "troca_km";
        private const string EtapaTrocaQuitado = "troca_quitado";
        private const string EtapaTrocaModeloDesejado = "troca_modelo_desejado";
        private const string EtapaTrocaCondicao = "troca_condicao";
        private const string EtapaVendaModelo = "venda_modelo";
        private const string EtapaVendaAno = "venda_ano";
        private const string EtapaVendaKm = "venda_km";
        private const string EtapaVendaQuitado = "venda_quitado";
        private const string EtapaUrgencia = "urgencia";

        private const string ObjetivoComprar = "comprar";
        private const string ObjetivoVender = "vender";
        private const string ObjetivoTrocar = "trocar";

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
                    LimparCamposNaoRelacionadosAoObjetivo(lead, objetivo);
                    lead.Urgencia = null;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    var proximaEtapa = DeterminarPrimeiraEtapaPorObjetivo(objetivo);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, proximaEtapa, viaNumeroCentral);
                    return CriarPergunta(proximaEtapa, lead, nomeEstabelecimento, incluirErro: false);
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

                case EtapaTrocaModeloAtual:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloAtual, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaModeloAtual, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaModeloAtual = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaAnoModelo, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaAnoModelo, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaAnoModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaAnoModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaAnoModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaAnoModelo = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaKm, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaKm, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaKm:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaKm, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaKm, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaKm = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaQuitado, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaQuitado, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaQuitado:
                {
                    var quitado = ParseQuitado(mensagemTexto);
                    if (quitado == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaQuitado, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaQuitado, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.TrocaQuitado = quitado;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloDesejado, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaModeloDesejado, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaTrocaModeloDesejado:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaModeloDesejado, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaModeloDesejado, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.TrocaModeloDesejado = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaCondicao, viaNumeroCentral);
                    return CriarPergunta(EtapaTrocaCondicao, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaTrocaCondicao:
                {
                    var condicao = ParseCondicaoTroca(mensagemTexto);
                    if (condicao == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaTrocaCondicao, viaNumeroCentral);
                        return CriarPergunta(EtapaTrocaCondicao, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.TrocaCondicao = condicao;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaVendaModelo:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaModelo, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaModelo, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaModelo = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaAno, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaAno, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaAno:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaAno, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaAno, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaAno = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaKm, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaKm, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaKm:
                    if (string.IsNullOrWhiteSpace(mensagemTexto))
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaKm, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaKm, lead, nomeEstabelecimento, incluirErro: false);
                    }

                    lead.VendaKm = mensagemTexto.Trim();
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaQuitado, viaNumeroCentral);
                    return CriarPergunta(EtapaVendaQuitado, lead, nomeEstabelecimento, incluirErro: false);

                case EtapaVendaQuitado:
                {
                    var quitado = ParseQuitado(mensagemTexto);
                    if (quitado == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaVendaQuitado, viaNumeroCentral);
                        return CriarPergunta(EtapaVendaQuitado, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.VendaQuitado = quitado;
                    await _garagemLeadRepository.AtualizarAsync(lead);
                    await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                    return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: false);
                }

                case EtapaUrgencia:
                {
                    var urgencia = ParseUrgencia(mensagemTexto);
                    if (urgencia == null)
                    {
                        await SalvarContextoQuestionarioAsync(idConversa, contextoAtual, lead, EtapaUrgencia, viaNumeroCentral);
                        return CriarPergunta(EtapaUrgencia, lead, nomeEstabelecimento, incluirErro: true);
                    }

                    lead.Urgencia = urgencia;
                    await _garagemLeadRepository.ConcluirAsync(lead);

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

            if (!string.IsNullOrWhiteSpace(lead.TrocaModeloAtual))
            {
                dados[ChaveTrocaModeloAtual] = lead.TrocaModeloAtual!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaAnoModelo))
            {
                dados[ChaveTrocaAnoModelo] = lead.TrocaAnoModelo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaKm))
            {
                dados[ChaveTrocaKm] = lead.TrocaKm!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaQuitado))
            {
                dados[ChaveTrocaQuitado] = lead.TrocaQuitado!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaModeloDesejado))
            {
                dados[ChaveTrocaModeloDesejado] = lead.TrocaModeloDesejado!;
            }

            if (!string.IsNullOrWhiteSpace(lead.TrocaCondicao))
            {
                dados[ChaveTrocaCondicao] = lead.TrocaCondicao!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaModelo))
            {
                dados[ChaveVendaModelo] = lead.VendaModelo!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaAno))
            {
                dados[ChaveVendaAno] = lead.VendaAno!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaKm))
            {
                dados[ChaveVendaKm] = lead.VendaKm!;
            }

            if (!string.IsNullOrWhiteSpace(lead.VendaQuitado))
            {
                dados[ChaveVendaQuitado] = lead.VendaQuitado!;
            }

            if (viaNumeroCentral)
            {
                var snapshot = CentralRoutingService.BuildSnapshot(contextoAtual);
                if (!string.IsNullOrWhiteSpace(snapshot.CentralDisplayPhone))
                {
                    dados[CentralRoutingService.ChaveCentralDisplayPhone] = snapshot.CentralDisplayPhone!;
                }

                if (snapshot.ConversaGrupoId.HasValue)
                {
                    dados[CentralRoutingService.ChaveConversaGrupoId] = snapshot.ConversaGrupoId.Value.ToString();
                }

                if (snapshot.ConversaSegmentoAtivaId.HasValue)
                {
                    dados[CentralRoutingService.ChaveConversaSegmentoAtivaId] = snapshot.ConversaSegmentoAtivaId.Value.ToString();
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
                EtapaTrocaModeloAtual => new AssistantDecision(
                    "Qual e o modelo atual do seu veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaAnoModelo => new AssistantDecision(
                    "Qual e o ano do veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaKm => new AssistantDecision(
                    "Qual a quilometragem aproximada?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaQuitado => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "O veiculo esta quitado?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildQuitadoButtons()),
                EtapaTrocaModeloDesejado => new AssistantDecision(
                    "Qual modelo voce deseja na troca?",
                    "none",
                    null,
                    false,
                    null),
                EtapaTrocaCondicao => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "Qual condicao de troca voce busca?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildCondicaoTrocaButtons()),
                EtapaVendaModelo => new AssistantDecision(
                    "Qual e o modelo do veiculo que voce quer vender?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaAno => new AssistantDecision(
                    "Qual e o ano do veiculo?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaKm => new AssistantDecision(
                    "Qual a quilometragem aproximada?",
                    "none",
                    null,
                    false,
                    null),
                EtapaVendaQuitado => new AssistantDecision(
                    BuildStructuredPrompt(
                        incluirErro ? "Nao consegui identificar essa opcao. 😊" : "O veiculo esta quitado?",
                        string.Empty),
                    "none",
                    null,
                    false,
                    null,
                    null,
                    BuildQuitadoButtons()),
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

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildQuitadoButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_quitado_sim", "Sim"),
                new WhatsAppReplyButtonOption("garagem_quitado_nao", "Nao")
            };
        }

        private static IReadOnlyList<WhatsAppReplyButtonOption> BuildCondicaoTrocaButtons()
        {
            return new[]
            {
                new WhatsAppReplyButtonOption("garagem_troca_condicao_direta", "Troca direta"),
                new WhatsAppReplyButtonOption("garagem_troca_condicao_volta", "Troca + volta"),
                new WhatsAppReplyButtonOption("garagem_troca_condicao_financiamento", "Troca + financ.")
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
                "1" or "comprar" or "garagem_objetivo_comprar" => ObjetivoComprar,
                "2" or "vender" or "garagem_objetivo_vender" => ObjetivoVender,
                "3" or "trocar" or "garagem_objetivo_trocar" => ObjetivoTrocar,
                _ => null
            };
        }

        private static string? ParseFormaPagamento(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "avista" or "a vista" or "garagem_pagamento_avista" => "avista",
                "2" or "entrada financiamento" or "entrada fin" or "entrada + financiamento" or "garagem_pagamento_entrada_financiamento" => "entrada_financiamento",
                "3" or "financiado" or "100 financiado" or "100 financ" or "100% financiado" or "garagem_pagamento_financiado_100" => "financiado_100",
                _ => null
            };
        }

        private static string? ParseQuitado(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "sim" or "s" or "garagem_quitado_sim" => "sim",
                "2" or "nao" or "n" or "garagem_quitado_nao" => "nao",
                _ => null
            };
        }

        private static string? ParseCondicaoTroca(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "troca direta" or "direta" or "garagem_troca_condicao_direta" => "troca_direta",
                "2" or "troca + volta" or "troca volta" or "volta" or "garagem_troca_condicao_volta" => "troca_volta",
                "3" or "troca + financiamento" or "troca financiamento" or "troca + financ." or "troca financ." or "troca financ" or "financiamento" or "garagem_troca_condicao_financiamento" => "troca_financiamento",
                _ => null
            };
        }

        private static string? ParseUrgencia(string? texto)
        {
            var normalizado = NormalizeText(texto);
            return normalizado switch
            {
                "1" or "ainda essa semana" or "essa semana" or "garagem_urgencia_esta_semana" => "esta_semana",
                "2" or "prox semanas" or "proximas semanas" or "nas proximas semanas" or "garagem_urgencia_proximas_semanas" => "proximas_semanas",
                "3" or "so pesquisando" or "garagem_urgencia_so_pesquisando" => "so_pesquisando",
                _ => null
            };
        }

        private static void LimparCamposNaoRelacionadosAoObjetivo(GarageLead lead, string objetivo)
        {
            if (string.Equals(objetivo, ObjetivoTrocar, StringComparison.Ordinal))
            {
                lead.ModeloInteresse = null;
                lead.FaixaInvestimento = null;
                lead.FormaPagamento = null;
                lead.ValorEntradaTexto = null;
                lead.VendaModelo = null;
                lead.VendaAno = null;
                lead.VendaKm = null;
                lead.VendaQuitado = null;
                return;
            }

            if (string.Equals(objetivo, ObjetivoVender, StringComparison.Ordinal))
            {
                lead.ModeloInteresse = null;
                lead.FaixaInvestimento = null;
                lead.FormaPagamento = null;
                lead.ValorEntradaTexto = null;
                lead.TrocaModeloAtual = null;
                lead.TrocaAnoModelo = null;
                lead.TrocaKm = null;
                lead.TrocaQuitado = null;
                lead.TrocaModeloDesejado = null;
                lead.TrocaCondicao = null;
                return;
            }

            lead.TrocaModeloAtual = null;
            lead.TrocaAnoModelo = null;
            lead.TrocaKm = null;
            lead.TrocaQuitado = null;
            lead.TrocaModeloDesejado = null;
            lead.TrocaCondicao = null;
            lead.VendaModelo = null;
            lead.VendaAno = null;
            lead.VendaKm = null;
            lead.VendaQuitado = null;
        }

        private static string DeterminarPrimeiraEtapaPorObjetivo(string? objetivo)
        {
            return objetivo switch
            {
                ObjetivoTrocar => EtapaTrocaModeloAtual,
                ObjetivoVender => EtapaVendaModelo,
                _ => EtapaModelo
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

            var objetivo = NormalizeText(lead.Objetivo);

            if (string.Equals(objetivo, ObjetivoTrocar, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(lead.TrocaModeloAtual))
                {
                    return EtapaTrocaModeloAtual;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaAnoModelo))
                {
                    return EtapaTrocaAnoModelo;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaKm))
                {
                    return EtapaTrocaKm;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaQuitado))
                {
                    return EtapaTrocaQuitado;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaModeloDesejado))
                {
                    return EtapaTrocaModeloDesejado;
                }

                if (string.IsNullOrWhiteSpace(lead.TrocaCondicao))
                {
                    return EtapaTrocaCondicao;
                }

                return EtapaUrgencia;
            }

            if (string.Equals(objetivo, ObjetivoVender, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(lead.VendaModelo))
                {
                    return EtapaVendaModelo;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaAno))
                {
                    return EtapaVendaAno;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaKm))
                {
                    return EtapaVendaKm;
                }

                if (string.IsNullOrWhiteSpace(lead.VendaQuitado))
                {
                    return EtapaVendaQuitado;
                }

                return EtapaUrgencia;
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
