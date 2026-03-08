using System;
using APIBack.Automation.Models;

namespace APIBack.Automation.Services
{
    public static class MessageStatusMapper
    {
        public const string Fila = "fila";
        public const string Enviada = "enviada";
        public const string Entregue = "entregue";
        public const string Lida = "lida";
        public const string Falhou = "falhou";

        public static string NormalizeForDatabase(string? status, DirecaoMensagem direcao)
        {
            var normalized = (status ?? string.Empty).Trim().ToLowerInvariant();

            if (string.IsNullOrWhiteSpace(normalized))
            {
                return direcao == DirecaoMensagem.Entrada ? Entregue : Fila;
            }

            return normalized switch
            {
                "fila" => Fila,
                "queued" => Fila,
                "pendente" => Fila,
                "enviado" => Enviada,
                "enviada" => Enviada,
                "sent" => Enviada,
                "recebido" => Entregue,
                "recebida" => Entregue,
                "entregue" => Entregue,
                "entregada" => Entregue,
                "delivered" => Entregue,
                "lido" => Lida,
                "lida" => Lida,
                "read" => Lida,
                "falhou" => Falhou,
                "falha" => Falhou,
                "erro" => Falhou,
                "failed" => Falhou,
                _ => direcao == DirecaoMensagem.Entrada ? Entregue : Fila
            };
        }
    }
}
