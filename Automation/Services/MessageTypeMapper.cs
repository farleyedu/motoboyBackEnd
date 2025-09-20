// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public static class MessageTypeMapper
    {
        private const string Texto = "texto";

        private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = Texto,
            ["image"] = "imagem",
            ["video"] = "video",
            ["audio"] = "audio",
            ["document"] = "documento",
            ["sticker"] = "sticker",
            ["contacts"] = "contato",
            ["contact"] = "contato",
            ["location"] = "localizacao",
            ["interactive"] = "interativo",
            ["button"] = "interativo",
            ["template"] = "template",
            ["reaction"] = Texto,
            ["system"] = Texto,
            ["message_status"] = Texto,
            ["unsupported"] = Texto,
            ["unknown"] = Texto
        };

        public static string MapType(string? waType, DirecaoMensagem direcao, string? criadaPor)
        {
            if (!string.IsNullOrWhiteSpace(waType) && Map.TryGetValue(waType.Trim(), out var mapped))
            {
                return mapped;
            }

            if (!string.IsNullOrWhiteSpace(criadaPor) && criadaPor.StartsWith("ia", StringComparison.OrdinalIgnoreCase))
            {
                return Texto;
            }

            return Texto;
        }
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================