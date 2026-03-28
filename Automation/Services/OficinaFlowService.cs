using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public class OficinaFlowService
    {
        public const string EstadoAguardandoNome = "oficina_aguardando_nome";

        private const string ChaveEstabelecimentoId = "oficina_estabelecimento_id";
        private const string ChaveViaNumeroCentral = "oficina_via_numero_central";
        private const string DefaultNomeEstabelecimento = "Citrocar";

        private static readonly Regex EspacosRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex PrefixoNomeRegex = new(
            @"^(meu nome e|me chamo|pode me chamar de|sou o|sou a|sou)\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly IConversationRepository _conversationRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly CentralRoutingService _centralRouting;
        private readonly ILogger<OficinaFlowService> _logger;

        public OficinaFlowService(
            IConversationRepository conversationRepository,
            IClienteRepository clienteRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            CentralRoutingService centralRouting,
            ILogger<OficinaFlowService> logger)
        {
            _conversationRepository = conversationRepository;
            _clienteRepository = clienteRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _centralRouting = centralRouting;
            _logger = logger;
        }

        public async Task<(bool Intercepted, AssistantDecision? Decision)> TryHandleAsync(
            Guid idConversa,
            string mensagemTexto,
            string? phoneNumberDisplay)
        {
            var scope = await _centralRouting.ResolveEffectiveScopeAsync(idConversa);
            if (scope == null || !await IsOficinaEstabelecimentoAsync(scope.IdEstabelecimento))
            {
                return (false, null);
            }

            var contextoAtual = await _conversationRepository.ObterContextoAsync(idConversa);
            var nomeEstabelecimento = await ObterNomeEstabelecimentoAsync(scope.IdEstabelecimento);

            if (string.Equals(contextoAtual?.Estado, EstadoAguardandoNome, StringComparison.OrdinalIgnoreCase))
            {
                return (true, await ProcessarNomeAsync(
                    idConversa,
                    scope.IdCliente,
                    scope.IdEstabelecimento,
                    nomeEstabelecimento,
                    mensagemTexto));
            }

            if (scope.IdCliente == Guid.Empty)
            {
                _logger.LogWarning("[Conversa={Conversa}] Fluxo oficina sem id_cliente resolvido", idConversa);
                return (false, null);
            }

            var cliente = await _clienteRepository.ObterPorIdAsync(scope.IdCliente);
            if (!string.IsNullOrWhiteSpace(cliente?.Nome))
            {
                return (false, null);
            }

            var viaNumeroCentral = _centralRouting.IsCentralDisplayPhone(phoneNumberDisplay);
            await SalvarContextoAguardandoNomeAsync(idConversa, scope.IdEstabelecimento, viaNumeroCentral);

            _logger.LogInformation(
                "[Conversa={Conversa}] Fluxo oficina iniciou captura de nome para estabelecimento {Estabelecimento}",
                idConversa,
                scope.IdEstabelecimento);

            return (true, CriarBoasVindas(nomeEstabelecimento));
        }

        public async Task<bool> IsOficinaEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var tipo = await _estabelecimentoRepository.ObterTipoEstabelecimentoAsync(idEstabelecimento);
            return string.Equals(tipo, "oficina", StringComparison.OrdinalIgnoreCase);
        }

        private async Task<AssistantDecision> ProcessarNomeAsync(
            Guid idConversa,
            Guid idCliente,
            Guid idEstabelecimento,
            string nomeEstabelecimento,
            string mensagemTexto)
        {
            if (!TryExtrairNomeValido(mensagemTexto, out var nome))
            {
                return CriarPedidoNomeComErro(nomeEstabelecimento);
            }

            var atualizado = await _clienteRepository.AtualizarNomeAsync(idCliente, idEstabelecimento, nome);
            if (!atualizado)
            {
                _logger.LogWarning(
                    "[Conversa={Conversa}] Nao foi possivel atualizar nome do cliente {Cliente} no estabelecimento {Estabelecimento}",
                    idConversa,
                    idCliente,
                    idEstabelecimento);

                return new AssistantDecision(
                    "Nao consegui registrar seu nome agora. Me manda novamente so o seu nome, por favor.",
                    "none",
                    null,
                    false,
                    null,
                    null);
            }

            await _conversationRepository.LimparContextoAsync(idConversa);

            _logger.LogInformation(
                "[Conversa={Conversa}] Nome do cliente registrado no fluxo oficina: {Nome}",
                idConversa,
                nome);

            return new AssistantDecision(
                $"Perfeito, {nome}! Vou continuar seu atendimento por aqui na {nomeEstabelecimento}. Como posso te ajudar com seu veiculo hoje?",
                "none",
                null,
                false,
                null,
                null);
        }

        private async Task SalvarContextoAguardandoNomeAsync(
            Guid idConversa,
            Guid idEstabelecimento,
            bool viaNumeroCentral)
        {
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoAguardandoNome,
                DadosColetados = new Dictionary<string, object>
                {
                    [ChaveEstabelecimentoId] = idEstabelecimento.ToString(),
                    [ChaveViaNumeroCentral] = viaNumeroCentral
                },
                ExpiracaoEstado = null
            });
        }

        private async Task<string> ObterNomeEstabelecimentoAsync(Guid idEstabelecimento)
        {
            var nome = await _estabelecimentoRepository.ObterNomeFantasiaAsync(idEstabelecimento);
            return string.IsNullOrWhiteSpace(nome) ? DefaultNomeEstabelecimento : nome.Trim();
        }

        private static AssistantDecision CriarBoasVindas(string nomeEstabelecimento)
        {
            return new AssistantDecision(
                $"Ola! Voce esta falando com a assistente virtual da {nomeEstabelecimento}.\n\nAntes de continuarmos, me fala seu nome, por favor.",
                "none",
                null,
                false,
                null,
                null);
        }

        private static AssistantDecision CriarPedidoNomeComErro(string nomeEstabelecimento)
        {
            return new AssistantDecision(
                $"Nao consegui identificar seu nome ainda.\n\nMe responde so com seu nome para eu continuar o atendimento da {nomeEstabelecimento}, por favor.",
                "none",
                null,
                false,
                null,
                null);
        }

        private static bool TryExtrairNomeValido(string? mensagemTexto, out string nome)
        {
            nome = string.Empty;
            if (string.IsNullOrWhiteSpace(mensagemTexto))
            {
                return false;
            }

            var texto = EspacosRegex.Replace(mensagemTexto.Trim(), " ");
            texto = PrefixoNomeRegex.Replace(texto, string.Empty).Trim();
            texto = texto.Trim(' ', '.', ',', ';', ':', '!', '?', '"', '\'', '-', '_');

            if (texto.Length < 2 || texto.Length > 60)
            {
                return false;
            }

            if (texto.Any(char.IsDigit) || !texto.Any(char.IsLetter))
            {
                return false;
            }

            var partes = texto.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (partes.Length == 0 || partes.Length > 4)
            {
                return false;
            }

            var culture = CultureInfo.GetCultureInfo("pt-BR");
            nome = culture.TextInfo.ToTitleCase(texto.ToLower(culture));
            return true;
        }
    }
}
