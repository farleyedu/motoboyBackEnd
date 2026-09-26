using System;
using System.Text.Json;
using APIBack.DTOs.Simulador;

namespace APIBack.Service
{
    /// <summary>Evento do simulador validado e normalizado, pronto para gravar.</summary>
    public sealed record SimEventoInput(
        string Entidade, string? EntidadeRef, string Tipo, string Titulo, string? Detalhe, string Status, string? CenarioId, string? DadosJson);

    public static class SimuladorEventoRules
    {
        public static readonly string[] Entidades = { "motoboy", "cliente", "pedido", "conversa", "sistema", "cenario" };
        public static readonly string[] Statuses = { "sucesso", "atencao", "erro" };
        public static readonly string[] SessaoTipos = { "motoboy", "cliente", "pedido", "conversa", "cenario" };

        public static SimEventoInput Validate(SimEventoRequest? request)
        {
            if (request == null) throw Invalid("Corpo da requisicao obrigatorio.");

            var entidade = (request.Entidade ?? "sistema").Trim().ToLowerInvariant();
            if (Array.IndexOf(Entidades, entidade) < 0) throw Invalid($"Entidade invalida. Use: {string.Join(", ", Entidades)}.");

            var status = (request.Status ?? "sucesso").Trim().ToLowerInvariant();
            if (Array.IndexOf(Statuses, status) < 0) throw Invalid($"Status invalido. Use: {string.Join(", ", Statuses)}.");

            var tipo = Text(request.Tipo, "tipo", 60) ?? throw Invalid("Informe o tipo do evento.");
            var titulo = Text(request.Titulo, "titulo", 160) ?? throw Invalid("Informe o titulo do evento.");

            string? dados = null;
            if (request.Dados is { ValueKind: not JsonValueKind.Undefined and not JsonValueKind.Null } raw)
            {
                dados = raw.GetRawText();
                if (dados.Length > 4000) throw Invalid("Dados do evento excedem 4000 caracteres.");
            }

            return new SimEventoInput(
                entidade, Text(request.EntidadeRef, "referencia", 80), tipo.ToLowerInvariant(), titulo,
                Text(request.Detalhe, "detalhe", 500), status, Text(request.CenarioId, "cenario", 60), dados);
        }

        public static string ValidateSessaoTipo(string? tipo)
        {
            var normalized = (tipo ?? string.Empty).Trim().ToLowerInvariant();
            if (Array.IndexOf(SessaoTipos, normalized) < 0) throw Invalid($"Tipo de sessao invalido. Use: {string.Join(", ", SessaoTipos)}.");
            return normalized;
        }

        /// <summary>Estado da sessao como JSON (objeto), ate 20 KB; vazio vira {}.</summary>
        public static string SessaoEstado(JsonElement? estado)
        {
            if (estado is not { ValueKind: JsonValueKind.Object } raw) return "{}";
            var text = raw.GetRawText();
            if (text.Length > 20_000) throw Invalid("Estado da sessao excede 20 KB.");
            return text;
        }

        private static string? Text(string? value, string label, int max)
        {
            var trimmed = value?.Trim();
            if (string.IsNullOrEmpty(trimmed)) return null;
            if (trimmed.Length > max) throw Invalid($"Campo {label} excede {max} caracteres.");
            return trimmed;
        }

        private static DeliveryDomainException Invalid(string message) => new(422, "INVALID_SIM_EVENT", message);
    }
}
