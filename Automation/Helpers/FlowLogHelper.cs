using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Helpers
{
    public static class FlowLogHelper
    {
        public static void FlowInfo(
            this ILogger logger,
            string eventName,
            Guid? conversationId = null,
            Guid? estabelecimentoId = null,
            Guid? clienteId = null,
            string? telefone = null,
            string? tipoEstabelecimento = null,
            IEnumerable<string>? modulosAtivos = null,
            string? estadoAnterior = null,
            string? estadoNovo = null,
            string? acao = null,
            string? resultado = null,
            string? motivo = null,
            string? traceId = null,
            params (string Key, object? Value)[] extra)
        {
            logger.LogInformation(BuildMessage(eventName, conversationId, estabelecimentoId, clienteId, telefone, tipoEstabelecimento, modulosAtivos, estadoAnterior, estadoNovo, acao, resultado, motivo, traceId, extra));
        }

        public static void FlowWarning(
            this ILogger logger,
            string eventName,
            Guid? conversationId = null,
            Guid? estabelecimentoId = null,
            Guid? clienteId = null,
            string? telefone = null,
            string? tipoEstabelecimento = null,
            IEnumerable<string>? modulosAtivos = null,
            string? estadoAnterior = null,
            string? estadoNovo = null,
            string? acao = null,
            string? resultado = null,
            string? motivo = null,
            string? traceId = null,
            params (string Key, object? Value)[] extra)
        {
            logger.LogWarning(BuildMessage(eventName, conversationId, estabelecimentoId, clienteId, telefone, tipoEstabelecimento, modulosAtivos, estadoAnterior, estadoNovo, acao, resultado, motivo, traceId, extra));
        }

        public static void FlowError(
            this ILogger logger,
            Exception exception,
            string eventName,
            Guid? conversationId = null,
            Guid? estabelecimentoId = null,
            Guid? clienteId = null,
            string? telefone = null,
            string? tipoEstabelecimento = null,
            IEnumerable<string>? modulosAtivos = null,
            string? estadoAnterior = null,
            string? estadoNovo = null,
            string? acao = null,
            string? resultado = null,
            string? motivo = null,
            string? traceId = null,
            params (string Key, object? Value)[] extra)
        {
            logger.LogError(exception, BuildMessage(eventName, conversationId, estabelecimentoId, clienteId, telefone, tipoEstabelecimento, modulosAtivos, estadoAnterior, estadoNovo, acao, resultado, motivo, traceId, extra));
        }

        private static string BuildMessage(
            string eventName,
            Guid? conversationId,
            Guid? estabelecimentoId,
            Guid? clienteId,
            string? telefone,
            string? tipoEstabelecimento,
            IEnumerable<string>? modulosAtivos,
            string? estadoAnterior,
            string? estadoNovo,
            string? acao,
            string? resultado,
            string? motivo,
            string? traceId,
            IReadOnlyCollection<(string Key, object? Value)> extra)
        {
            var parts = new List<string>
            {
                $"event={Sanitize(eventName)}"
            };

            Add(parts, "conversationId", conversationId);
            Add(parts, "estabelecimentoId", estabelecimentoId);
            Add(parts, "clienteId", clienteId);
            Add(parts, "telefone", telefone);
            Add(parts, "tipoEstabelecimento", tipoEstabelecimento);
            Add(parts, "modulosAtivos", modulosAtivos == null ? null : string.Join(",", modulosAtivos));
            Add(parts, "estadoAnterior", estadoAnterior);
            Add(parts, "estadoNovo", estadoNovo);
            Add(parts, "acao", acao);
            Add(parts, "resultado", resultado);
            Add(parts, "motivo", motivo);
            Add(parts, "traceId", traceId);

            foreach (var item in extra.Where(item => !string.IsNullOrWhiteSpace(item.Key)))
            {
                Add(parts, item.Key, item.Value);
            }

            return string.Join(' ', parts);
        }

        private static void Add(ICollection<string> parts, string key, object? value)
        {
            if (value == null)
            {
                return;
            }

            var text = Convert.ToString(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            parts.Add($"{key}={Sanitize(text)}");
        }

        private static string Sanitize(object? value)
        {
            var text = Convert.ToString(value)?.Trim() ?? string.Empty;
            if (text.Length == 0)
            {
                return "\"\"";
            }

            text = text.Replace("\r", " ").Replace("\n", " ").Replace("\"", "'");
            return text.Any(char.IsWhiteSpace) ? $"\"{text}\"" : text;
        }
    }
}
