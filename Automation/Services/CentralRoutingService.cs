using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Services
{
    public sealed class CentralSelectionSnapshot
    {
        public ConversationContext? Contexto { get; init; }
        public string? CentralDisplayPhone { get; init; }
        public Guid? EstabelecimentoId { get; init; }
        public string? EstabelecimentoNome { get; init; }
        public string? EstabelecimentoTipo { get; init; }
        public DateTime? ExpiraEmUtc { get; init; }
        public bool IsCentralContext => !string.IsNullOrWhiteSpace(CentralDisplayPhone);
        public bool HasSelection => EstabelecimentoId.HasValue && !SelectionExpired;
        public bool SelectionExpired { get; init; }
    }

    public sealed class EffectiveConversationScope
    {
        public Guid IdEstabelecimento { get; init; }
        public Guid IdCliente { get; init; }
        public string? TelefoneCliente { get; init; }
        public bool UsaEstabelecimentoSelecionado { get; init; }
        public string? NomeEstabelecimentoSelecionado { get; init; }
    }

    public class CentralRoutingService
    {
        public const string EstadoAguardandoEscolha = "aguardando_escolha_estabelecimento";
        public const string EstadoEstabelecimentoSelecionado = "estabelecimento_selecionado";
        public const string ChaveCentralDisplayPhone = "central_display_phone";
        public const string ChaveEstabelecimentosDisponiveis = "estabelecimentos_disponiveis";
        public const string ChaveEstabelecimentoEscolhidoId = "estabelecimento_escolhido_id";
        public const string ChaveEstabelecimentoEscolhidoNome = "estabelecimento_escolhido_nome";
        public const string ChaveEstabelecimentoEscolhidoTipo = "estabelecimento_escolhido_tipo";
        public const string ChaveEstabelecimentoEscolhidoExpiraEm = "estabelecimento_escolhido_expira_em";

        private readonly IConversationRepository _conversationRepository;
        private readonly IEstabelecimentoRepository _estabelecimentoRepository;
        private readonly IClienteRepository _clienteRepository;
        private readonly ILogger<CentralRoutingService> _logger;
        private readonly string _centralDisplayPhone;
        private readonly string _centralDisplayPhoneDigits;
        private readonly string _centralPhoneNumberId;
        private readonly string _centralPhoneNumberIdDigits;
        private readonly Guid? _centralEstabelecimentoId;
        private readonly string _resetCommand;
        private readonly TimeSpan _selectionTtl;

        public CentralRoutingService(
            IConfiguration configuration,
            IConversationRepository conversationRepository,
            IEstabelecimentoRepository estabelecimentoRepository,
            IClienteRepository clienteRepository,
            ILogger<CentralRoutingService> logger)
        {
            _conversationRepository = conversationRepository;
            _estabelecimentoRepository = estabelecimentoRepository;
            _clienteRepository = clienteRepository;
            _logger = logger;
            _centralDisplayPhone = configuration["WhatsApp:CentralDisplayPhone"] ?? string.Empty;
            _centralDisplayPhoneDigits = OnlyDigits(_centralDisplayPhone);
            _centralPhoneNumberId = configuration["WhatsApp:CentralPhoneNumberId"]
                ?? configuration["Automation:Meta:PhoneNumberId"]
                ?? string.Empty;
            _centralPhoneNumberIdDigits = OnlyDigits(_centralPhoneNumberId);
            _centralEstabelecimentoId = Guid.TryParse(configuration["WhatsApp:CentralEstabelecimentoId"], out var parsed)
                ? parsed
                : null;
            _resetCommand = configuration["WhatsApp:CentralResetCommand"] ?? "*3275";
            var ttlMinutes = configuration.GetValue<int?>("WhatsApp:CentralSelectionContextTtlMinutes") ?? 30;
            _selectionTtl = TimeSpan.FromMinutes(Math.Max(1, ttlMinutes));
        }

        public Guid? CentralEstabelecimentoId => _centralEstabelecimentoId;
        public TimeSpan SelectionTtl => _selectionTtl;

        public bool IsCentralDisplayPhone(string? displayPhone)
        {
            if (string.IsNullOrWhiteSpace(_centralDisplayPhoneDigits) || string.IsNullOrWhiteSpace(displayPhone))
            {
                return false;
            }

            return string.Equals(OnlyDigits(displayPhone), _centralDisplayPhoneDigits, StringComparison.Ordinal);
        }

        public bool IsCentralPhoneNumberId(string? phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(_centralPhoneNumberIdDigits) || string.IsNullOrWhiteSpace(phoneNumberId))
            {
                return false;
            }

            return string.Equals(OnlyDigits(phoneNumberId), _centralPhoneNumberIdDigits, StringComparison.Ordinal);
        }

        public bool IsCentralRoutingTarget(string? displayPhone, string? phoneNumberId)
        {
            return IsCentralDisplayPhone(displayPhone) || IsCentralPhoneNumberId(phoneNumberId);
        }

        public bool IsResetCommand(string? text)
        {
            return string.Equals(text?.Trim(), _resetCommand, StringComparison.Ordinal);
        }

        public async Task<IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto>> ListarEstabelecimentosElegiveisAsync()
        {
            return await _estabelecimentoRepository.ListarEstabelecimentosComWhatsappAtivoAsync(_centralEstabelecimentoId);
        }

        public async Task<CentralSelectionSnapshot> ObterSelecaoAtualAsync(Guid idConversa, ConversationContext? contexto = null)
        {
            contexto ??= await _conversationRepository.ObterContextoAsync(idConversa);
            return BuildSnapshot(contexto);
        }

        public async Task SalvarMenuEscolhaAsync(Guid idConversa, IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto> estabelecimentos)
        {
            var expiraEmUtc = DateTime.UtcNow.Add(_selectionTtl);
            var dados = new Dictionary<string, object>
            {
                [ChaveCentralDisplayPhone] = _centralDisplayPhone,
                [ChaveEstabelecimentosDisponiveis] = estabelecimentos
                    .Select((item, index) => new Dictionary<string, object>
                    {
                        ["indice"] = index + 1,
                        ["id"] = item.Id.ToString(),
                        ["nome"] = item.Nome,
                        ["tipo"] = item.TipoEstabelecimento
                    })
                    .ToList()
            };

            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoAguardandoEscolha,
                DadosColetados = dados,
                ExpiracaoEstado = expiraEmUtc
            });
        }

        public async Task SalvarEstabelecimentoSelecionadoAsync(Guid idConversa, EstabelecimentoWhatsappAtivoDto estabelecimento)
        {
            var expiraEmUtc = DateTime.UtcNow.Add(_selectionTtl);
            await _conversationRepository.SalvarContextoAsync(idConversa, new ConversationContext
            {
                Estado = EstadoEstabelecimentoSelecionado,
                DadosColetados = new Dictionary<string, object>
                {
                    [ChaveCentralDisplayPhone] = _centralDisplayPhone,
                    [ChaveEstabelecimentoEscolhidoId] = estabelecimento.Id.ToString(),
                    [ChaveEstabelecimentoEscolhidoNome] = estabelecimento.Nome,
                    [ChaveEstabelecimentoEscolhidoTipo] = estabelecimento.TipoEstabelecimento,
                    [ChaveEstabelecimentoEscolhidoExpiraEm] = expiraEmUtc.ToString("O", CultureInfo.InvariantCulture)
                },
                ExpiracaoEstado = expiraEmUtc
            });
        }

        public async Task RenovarSelecaoAsync(Guid idConversa, ConversationContext? contexto = null)
        {
            contexto ??= await _conversationRepository.ObterContextoAsync(idConversa);
            var snapshot = BuildSnapshot(contexto);
            if (!snapshot.HasSelection || contexto == null)
            {
                return;
            }

            contexto.DadosColetados ??= new Dictionary<string, object>();
            var novoPrazo = DateTime.UtcNow.Add(_selectionTtl);
            contexto.DadosColetados[ChaveCentralDisplayPhone] = snapshot.CentralDisplayPhone ?? _centralDisplayPhone;
            contexto.DadosColetados[ChaveEstabelecimentoEscolhidoId] = snapshot.EstabelecimentoId!.Value.ToString();
            contexto.DadosColetados[ChaveEstabelecimentoEscolhidoNome] = snapshot.EstabelecimentoNome ?? string.Empty;
            contexto.DadosColetados[ChaveEstabelecimentoEscolhidoTipo] = snapshot.EstabelecimentoTipo ?? string.Empty;
            contexto.DadosColetados[ChaveEstabelecimentoEscolhidoExpiraEm] = novoPrazo.ToString("O", CultureInfo.InvariantCulture);

            if (string.Equals(contexto.Estado, EstadoEstabelecimentoSelecionado, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(contexto.Estado, EstadoAguardandoEscolha, StringComparison.OrdinalIgnoreCase))
            {
                contexto.ExpiracaoEstado = novoPrazo;
            }

            await _conversationRepository.SalvarContextoAsync(idConversa, contexto);
        }

        public async Task<EffectiveConversationScope?> ResolveEffectiveScopeAsync(Guid idConversa, Conversation? conversa = null)
        {
            conversa ??= await _conversationRepository.ObterPorIdAsync(idConversa);
            if (conversa == null)
            {
                return null;
            }

            var snapshot = await ObterSelecaoAtualAsync(idConversa);
            if (!snapshot.HasSelection)
            {
                return new EffectiveConversationScope
                {
                    IdCliente = conversa.IdCliente,
                    IdEstabelecimento = conversa.IdEstabelecimento,
                    TelefoneCliente = conversa.TelefoneCliente,
                    UsaEstabelecimentoSelecionado = false
                };
            }

            var telefone = conversa.TelefoneCliente;
            if (string.IsNullOrWhiteSpace(telefone) && conversa.IdCliente != Guid.Empty && conversa.IdEstabelecimento != Guid.Empty)
            {
                telefone = await _clienteRepository.ObterTelefoneClienteAsync(conversa.IdCliente, conversa.IdEstabelecimento);
            }

            var idCliente = conversa.IdCliente;
            if (!string.IsNullOrWhiteSpace(telefone))
            {
                idCliente = await _clienteRepository.GarantirClienteAsync(telefone, snapshot.EstabelecimentoId!.Value);
            }

            return new EffectiveConversationScope
            {
                IdCliente = idCliente,
                IdEstabelecimento = snapshot.EstabelecimentoId!.Value,
                TelefoneCliente = telefone,
                UsaEstabelecimentoSelecionado = true,
                NomeEstabelecimentoSelecionado = snapshot.EstabelecimentoNome
            };
        }

        public EstabelecimentoWhatsappAtivoDto? TryResolveEscolha(string mensagemTexto, IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto> estabelecimentos, out bool ambiguaPorNome)
        {
            ambiguaPorNome = false;
            if (string.IsNullOrWhiteSpace(mensagemTexto) || estabelecimentos.Count == 0)
            {
                return null;
            }

            var lista = estabelecimentos.ToList();
            var texto = mensagemTexto.Trim();

            if (int.TryParse(texto, NumberStyles.Integer, CultureInfo.InvariantCulture, out var indice) &&
                indice >= 1 && indice <= lista.Count)
            {
                return lista[indice - 1];
            }

            var matchOpcao = Regex.Match(texto, @"(?:opcao|opção)\s*(\d{1,2})", RegexOptions.IgnoreCase);
            if (matchOpcao.Success &&
                int.TryParse(matchOpcao.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out indice) &&
                indice >= 1 && indice <= lista.Count)
            {
                return lista[indice - 1];
            }

            var normalizado = NormalizeText(texto);
            var porNome = lista
                .Where(item => string.Equals(NormalizeText(item.Nome), normalizado, StringComparison.Ordinal))
                .ToList();

            if (porNome.Count > 1)
            {
                ambiguaPorNome = true;
                return null;
            }

            return porNome.Count == 1 ? porNome[0] : null;
        }

        public string BuildSelectionMenuMessage(IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto> estabelecimentos, bool reiniciado = false)
        {
            if (estabelecimentos.Count == 0)
            {
                return "No momento não há estabelecimentos disponíveis para atendimento automático por este número.";
            }

            var builder = new StringBuilder();
            if (reiniciado)
            {
                builder.AppendLine("Atendimento reiniciado.");
                builder.AppendLine();
                builder.AppendLine("Escolha novamente o estabelecimento com que deseja falar:");
            }
            else
            {
                builder.AppendLine("Olá! Este é o atendimento central.");
                builder.AppendLine();
                builder.AppendLine("Escolha o estabelecimento com que deseja falar:");
            }

            builder.AppendLine();

            var indice = 1;
            foreach (var estabelecimento in estabelecimentos)
            {
                builder.AppendLine($"{indice}. {estabelecimento.Nome}");
                indice++;
            }

            builder.AppendLine();
            builder.Append("Responda com o número da opção ou com o nome do estabelecimento.");
            return builder.ToString();
        }

        public string BuildSelectionInvalidMessage(IReadOnlyCollection<EstabelecimentoWhatsappAtivoDto> estabelecimentos, bool ambiguaPorNome)
        {
            if (estabelecimentos.Count == 0)
            {
                return "No momento não há estabelecimentos disponíveis para atendimento automático por este número.";
            }

            var builder = new StringBuilder();
            builder.AppendLine(ambiguaPorNome
                ? "Encontrei mais de um estabelecimento com nome parecido."
                : "Não consegui identificar o estabelecimento.");
            builder.AppendLine();
            builder.AppendLine("Responda com o número da opção ou com o nome do estabelecimento:");
            builder.AppendLine();

            var indice = 1;
            foreach (var estabelecimento in estabelecimentos)
            {
                builder.AppendLine($"{indice}. {estabelecimento.Nome}");
                indice++;
            }

            return builder.ToString();
        }

        public static ConversationContext MergeCentralSelection(ConversationContext? contextoExistente, ConversationContext novoContexto)
        {
            var snapshot = BuildSnapshot(contextoExistente);
            if (!snapshot.HasSelection)
            {
                return novoContexto;
            }

            novoContexto.DadosColetados ??= new Dictionary<string, object>();
            PreserveSelection(novoContexto.DadosColetados, snapshot);
            return novoContexto;
        }

        public static ConversationContext? BuildPreservedSelectionContext(ConversationContext? contextoExistente)
        {
            var snapshot = BuildSnapshot(contextoExistente);
            if (!snapshot.HasSelection)
            {
                return null;
            }

            var dados = new Dictionary<string, object>();
            PreserveSelection(dados, snapshot);
            return new ConversationContext
            {
                Estado = EstadoEstabelecimentoSelecionado,
                DadosColetados = dados,
                ExpiracaoEstado = snapshot.ExpiraEmUtc
            };
        }

        public static CentralSelectionSnapshot BuildSnapshot(ConversationContext? contexto)
        {
            var dados = contexto?.DadosColetados;
            var centralDisplay = GetStringValue(dados, ChaveCentralDisplayPhone);
            var idSelecionado = GetGuidValue(dados, ChaveEstabelecimentoEscolhidoId);
            var expiraEm = GetDateTimeValue(dados, ChaveEstabelecimentoEscolhidoExpiraEm) ?? contexto?.ExpiracaoEstado;
            var expirado = expiraEm.HasValue && expiraEm.Value < DateTime.UtcNow;

            return new CentralSelectionSnapshot
            {
                Contexto = contexto,
                CentralDisplayPhone = centralDisplay,
                EstabelecimentoId = idSelecionado,
                EstabelecimentoNome = GetStringValue(dados, ChaveEstabelecimentoEscolhidoNome),
                EstabelecimentoTipo = GetStringValue(dados, ChaveEstabelecimentoEscolhidoTipo),
                ExpiraEmUtc = expiraEm,
                SelectionExpired = idSelecionado.HasValue && expirado
            };
        }

        public static string? GetStringValue(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            if (dados == null || !dados.TryGetValue(chave, out var valor) || valor == null)
            {
                return null;
            }

            return valor switch
            {
                string texto => texto,
                Guid guid => guid.ToString(),
                JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
                JsonElement json => json.ToString(),
                _ => valor.ToString()
            };
        }

        public static Guid? GetGuidValue(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            var valor = GetStringValue(dados, chave);
            return Guid.TryParse(valor, out var parsed) ? parsed : null;
        }

        public static DateTime? GetDateTimeValue(IReadOnlyDictionary<string, object>? dados, string chave)
        {
            var valor = GetStringValue(dados, chave);
            return DateTime.TryParse(valor, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;
        }

        private static void PreserveSelection(IDictionary<string, object> destino, CentralSelectionSnapshot snapshot)
        {
            if (!string.IsNullOrWhiteSpace(snapshot.CentralDisplayPhone))
            {
                destino[ChaveCentralDisplayPhone] = snapshot.CentralDisplayPhone!;
            }

            if (snapshot.EstabelecimentoId.HasValue)
            {
                destino[ChaveEstabelecimentoEscolhidoId] = snapshot.EstabelecimentoId.Value.ToString();
            }

            if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoNome))
            {
                destino[ChaveEstabelecimentoEscolhidoNome] = snapshot.EstabelecimentoNome!;
            }

            if (!string.IsNullOrWhiteSpace(snapshot.EstabelecimentoTipo))
            {
                destino[ChaveEstabelecimentoEscolhidoTipo] = snapshot.EstabelecimentoTipo!;
            }

            if (snapshot.ExpiraEmUtc.HasValue)
            {
                destino[ChaveEstabelecimentoEscolhidoExpiraEm] = snapshot.ExpiraEmUtc.Value.ToString("O", CultureInfo.InvariantCulture);
            }
        }

        private static string NormalizeText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var noAccents = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(noAccents.Length);
            foreach (var ch in noAccents)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(char.ToLowerInvariant(ch));
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC).Trim();
        }

        private static string OnlyDigits(string? value)
        {
            return new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
        }
    }
}
