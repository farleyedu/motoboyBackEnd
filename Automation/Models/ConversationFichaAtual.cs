using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace APIBack.Automation.Models
{
    public sealed class ConversationFichaAtual
    {
        [JsonPropertyName("nome_cliente")]
        public string? NomeCliente { get; set; }

        [JsonPropertyName("modulo_em_foco")]
        public string? ModuloEmFoco { get; set; }

        [JsonPropertyName("servico")]
        public string? Servico { get; set; }

        [JsonPropertyName("veiculo_marca")]
        public string? VeiculoMarca { get; set; }

        [JsonPropertyName("veiculo_modelo")]
        public string? VeiculoModelo { get; set; }

        [JsonPropertyName("marca_peca")]
        public string? MarcaPeca { get; set; }

        [JsonPropertyName("pendencias")]
        public List<string>? Pendencias { get; set; } = new();

        [JsonPropertyName("pronto_para_agendamento")]
        public bool? ProntoParaAgendamento { get; set; }
    }

    public static class ConversationFichaAtualStore
    {
        public const string ContextKey = "ficha_atual";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        public static ConversationFichaAtual? Read(ConversationContext? contexto)
        {
            if (contexto?.DadosColetados == null ||
                !contexto.DadosColetados.TryGetValue(ContextKey, out var raw) ||
                raw == null)
            {
                return null;
            }

            return Deserialize(raw);
        }

        public static void Write(ConversationContext contexto, ConversationFichaAtual? fichaAtual)
        {
            if (contexto == null)
            {
                throw new ArgumentNullException(nameof(contexto));
            }

            contexto.DadosColetados ??= new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            if (fichaAtual == null || !HasMeaningfulData(fichaAtual))
            {
                contexto.DadosColetados.Remove(ContextKey);
                return;
            }

            contexto.DadosColetados[ContextKey] = JsonSerializer.Serialize(Normalize(fichaAtual), JsonOptions);
        }

        public static ConversationFichaAtual Merge(ConversationFichaAtual? atual, ConversationFichaAtual? incoming)
        {
            var merged = Normalize(atual ?? new ConversationFichaAtual());

            if (incoming == null)
            {
                return merged;
            }

            var normalizedIncoming = Normalize(incoming);

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.NomeCliente))
            {
                merged.NomeCliente = normalizedIncoming.NomeCliente;
            }

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.ModuloEmFoco))
            {
                merged.ModuloEmFoco = normalizedIncoming.ModuloEmFoco;
            }

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.Servico))
            {
                merged.Servico = normalizedIncoming.Servico;
            }

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.VeiculoMarca))
            {
                merged.VeiculoMarca = normalizedIncoming.VeiculoMarca;
            }

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.VeiculoModelo))
            {
                merged.VeiculoModelo = normalizedIncoming.VeiculoModelo;
            }

            if (!string.IsNullOrWhiteSpace(normalizedIncoming.MarcaPeca))
            {
                merged.MarcaPeca = normalizedIncoming.MarcaPeca;
            }

            if (incoming.Pendencias != null)
            {
                merged.Pendencias = normalizedIncoming.Pendencias ?? new List<string>();
            }

            if (normalizedIncoming.ProntoParaAgendamento.HasValue)
            {
                merged.ProntoParaAgendamento = normalizedIncoming.ProntoParaAgendamento;
            }

            return Normalize(merged);
        }

        public static ConversationFichaAtual Normalize(ConversationFichaAtual fichaAtual)
        {
            if (fichaAtual == null)
            {
                throw new ArgumentNullException(nameof(fichaAtual));
            }

            return new ConversationFichaAtual
            {
                NomeCliente = TrimToNull(fichaAtual.NomeCliente),
                ModuloEmFoco = TrimToNull(fichaAtual.ModuloEmFoco)?.ToLowerInvariant(),
                Servico = TrimToNull(fichaAtual.Servico),
                VeiculoMarca = TrimToNull(fichaAtual.VeiculoMarca),
                VeiculoModelo = TrimToNull(fichaAtual.VeiculoModelo),
                MarcaPeca = TrimToNull(fichaAtual.MarcaPeca),
                Pendencias = (fichaAtual.Pendencias ?? new List<string>())
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                ProntoParaAgendamento = fichaAtual.ProntoParaAgendamento
            };
        }

        public static bool HasMeaningfulData(ConversationFichaAtual? fichaAtual)
        {
            if (fichaAtual == null)
            {
                return false;
            }

            var normalized = Normalize(fichaAtual);
            return !string.IsNullOrWhiteSpace(normalized.NomeCliente) ||
                   !string.IsNullOrWhiteSpace(normalized.ModuloEmFoco) ||
                   !string.IsNullOrWhiteSpace(normalized.Servico) ||
                   !string.IsNullOrWhiteSpace(normalized.VeiculoMarca) ||
                   !string.IsNullOrWhiteSpace(normalized.VeiculoModelo) ||
                   !string.IsNullOrWhiteSpace(normalized.MarcaPeca) ||
                   normalized.Pendencias?.Count > 0 ||
                   normalized.ProntoParaAgendamento.HasValue;
        }

        public static string ToJson(ConversationFichaAtual? fichaAtual)
        {
            return JsonSerializer.Serialize(Normalize(fichaAtual ?? new ConversationFichaAtual()), JsonOptions);
        }

        private static ConversationFichaAtual? Deserialize(object raw)
        {
            try
            {
                return raw switch
                {
                    ConversationFichaAtual fichaAtual => Normalize(fichaAtual),
                    string texto when !string.IsNullOrWhiteSpace(texto) => Normalize(
                        JsonSerializer.Deserialize<ConversationFichaAtual>(texto, JsonOptions) ?? new ConversationFichaAtual()),
                    JsonElement element when element.ValueKind == JsonValueKind.String => Normalize(
                        JsonSerializer.Deserialize<ConversationFichaAtual>(element.GetString() ?? string.Empty, JsonOptions) ?? new ConversationFichaAtual()),
                    JsonElement element when element.ValueKind == JsonValueKind.Object => Normalize(
                        JsonSerializer.Deserialize<ConversationFichaAtual>(element.GetRawText(), JsonOptions) ?? new ConversationFichaAtual()),
                    _ => Normalize(
                        JsonSerializer.Deserialize<ConversationFichaAtual>(JsonSerializer.Serialize(raw, JsonOptions), JsonOptions) ?? new ConversationFichaAtual())
                };
            }
            catch
            {
                return null;
            }
        }

        private static string? TrimToNull(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
    }
}
