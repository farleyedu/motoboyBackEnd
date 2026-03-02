// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public static class MessageTypeMapper
    {
        private const string Texto = "texto";
        private const string Imagem = "imagem";
        private const string Audio = "audio";
        private const string Arquivo = "arquivo";
        private const string Template = "template";
        private const string Sistema = "sistema";
        private const string Interativo = "interativo";

        private static readonly IReadOnlyDictionary<string, string> Map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["text"] = Texto,
            ["texto"] = Texto,
            ["image"] = Imagem,
            ["imagem"] = Imagem,
            ["audio"] = Audio,
            ["document"] = Arquivo,
            ["video"] = Arquivo,
            ["sticker"] = Imagem,
            ["contacts"] = Arquivo,
            ["contact"] = Arquivo,
            ["location"] = Arquivo,
            ["arquivo"] = Arquivo,
            ["interactive"] = Interativo,
            ["button"] = Interativo,
            ["interativo"] = Interativo,
            ["template"] = Template,
            ["sistema"] = Sistema,
            ["reaction"] = Texto,
            ["system"] = Sistema,
            ["message_status"] = Sistema,
            ["unsupported"] = Sistema,
            ["unknown"] = Sistema
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
