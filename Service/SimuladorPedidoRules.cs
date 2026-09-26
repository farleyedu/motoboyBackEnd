using System;
using System.Collections.Generic;
using System.Linq;
using APIBack.Model.Enum;

namespace APIBack.Service
{
    /// <summary>Etapas do fluxo que o simulador de pedido avanca manualmente.</summary>
    public enum SimEtapa { Confirmar, Preparo, Saiu, Entregue }

    /// <summary>Estados-alvo aceitos por "editar status" e "criar pedido do nada".</summary>
    public enum SimAlvo { Recebido, Confirmado, EmPreparo, Saiu, Entregue, Cancelado }

    /// <summary>
    /// Regras do fluxo do pedido no simulador. O simulador e um atalho para testar, nunca uma brecha:
    /// as mesmas regras do fluxo real valem aqui (em rota so com motoboy, entregue so quem estava em rota,
    /// preparo so depois de confirmado...). Puro e sem banco, para testar cada transicao.
    /// </summary>
    public static class SimuladorPedidoRules
    {
        public static SimEtapa? ParseEtapa(string? value) => Normalize(value) switch
        {
            "confirmar" or "confirmado" => SimEtapa.Confirmar,
            "preparo" or "em_preparo" or "empreparo" => SimEtapa.Preparo,
            "saiu" or "saiu_para_entrega" or "saiu_entrega" => SimEtapa.Saiu,
            "entregue" or "entregar" => SimEtapa.Entregue,
            _ => null
        };

        public static SimAlvo? ParseAlvo(string? value) => Normalize(value) switch
        {
            "recebido" or "pendente" or "" => SimAlvo.Recebido,
            "confirmado" or "confirmar" => SimAlvo.Confirmado,
            "em_preparo" or "preparo" or "empreparo" => SimAlvo.EmPreparo,
            "saiu" or "saiu_para_entrega" or "em_rota" => SimAlvo.Saiu,
            "entregue" or "concluido" => SimAlvo.Entregue,
            "cancelado" => SimAlvo.Cancelado,
            _ => null
        };

        private static string Normalize(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant().Replace(' ', '_');

        /// <summary>Mensagem de erro se a etapa nao pode ser avancada agora; null se pode.</summary>
        public static string? ValidateEtapa(SimEtapa etapa, StatusPedido status, bool confirmado, bool preparo)
        {
            if (status == StatusPedido.Concluido || status == StatusPedido.Cancelado)
            {
                return "O pedido ja terminou. Para simular de novo, reabra o pedido (ele volta a pendente).";
            }
            if (status == StatusPedido.Rascunho)
            {
                return "O pedido ainda e um rascunho: confirme-o antes de avancar as etapas.";
            }

            switch (etapa)
            {
                case SimEtapa.Confirmar:
                    return confirmado ? "O pedido ja foi confirmado." : null;
                case SimEtapa.Preparo:
                    if (!confirmado) return "Confirme o pedido antes de iniciar o preparo.";
                    return preparo ? "O pedido ja esta em preparo." : null;
                case SimEtapa.Saiu:
                    if (!confirmado || !preparo) return "O pedido precisa estar confirmado e em preparo antes de sair para entrega.";
                    return status == StatusPedido.EmRota ? "O pedido ja saiu para entrega." : null;
                case SimEtapa.Entregue:
                    return status == StatusPedido.EmRota
                        ? null
                        : "So da para entregar um pedido que esta em rota com um motoboy. Envie-o para entrega primeiro.";
                default:
                    return "Etapa desconhecida.";
            }
        }

        /// <summary>Quais etapas os botoes podem oferecer agora.</summary>
        public static List<string> Proximos(StatusPedido status, bool confirmado, bool preparo)
        {
            var list = new List<string>();
            foreach (var (etapa, nome) in new[] { (SimEtapa.Confirmar, "confirmar"), (SimEtapa.Preparo, "preparo"), (SimEtapa.Saiu, "saiu"), (SimEtapa.Entregue, "entregue") })
            {
                if (ValidateEtapa(etapa, status, confirmado, preparo) == null) list.Add(nome);
            }
            return list;
        }

        /// <summary>Etapa em que o pedido esta, para o marcador do fluxo.</summary>
        public static string EtapaAtual(StatusPedido status, bool confirmado, bool preparo) => status switch
        {
            StatusPedido.Cancelado => "cancelado",
            StatusPedido.Concluido => "entregue",
            StatusPedido.EmRota => "saiu",
            _ => preparo ? "em_preparo" : confirmado ? "confirmado" : "recebido"
        };

        /// <summary>Estados que exigem um motoboy (e por isso nao se chega a eles sem escolher ou auto-atribuir um).</summary>
        public static bool RequiresMotoboy(SimAlvo alvo) => alvo == SimAlvo.Saiu || alvo == SimAlvo.Entregue;

        /// <summary>Sequencia de etapas a executar para chegar ao alvo a partir do zero (pedido pendente, sem etapas).</summary>
        public static IReadOnlyList<SimEtapa> PathTo(SimAlvo alvo) => alvo switch
        {
            SimAlvo.Confirmado => new[] { SimEtapa.Confirmar },
            SimAlvo.EmPreparo => new[] { SimEtapa.Confirmar, SimEtapa.Preparo },
            SimAlvo.Saiu => new[] { SimEtapa.Confirmar, SimEtapa.Preparo, SimEtapa.Saiu },
            SimAlvo.Entregue => new[] { SimEtapa.Confirmar, SimEtapa.Preparo, SimEtapa.Saiu, SimEtapa.Entregue },
            _ => Array.Empty<SimEtapa>()
        };

        /// <summary>"TEST-7832": como o simulador chama o pedido de teste.</summary>
        public static string DisplayId(int pedidoId, bool simulado) => simulado ? $"TEST-{pedidoId}" : pedidoId.ToString();

        /// <summary>Formas de pagamento canonicas do nucleo de pedido (OrderCoreRules.NormalizePayment): o simulador grava exatamente estas.</summary>
        public static readonly string[] PaymentTypes = { "dinheiro", "pix", "cartao_entrega", "link" };

        /// <summary>
        /// Leva "Cartao de credito", "cartão", "PIX", "Pago Online", "Dinheiro"... para o vocabulario canonico do nucleo. Vazio = sem forma informada (null);
        /// desconhecido = false, para o servidor recusar em vez de gravar um texto que o resto do sistema nao entende.
        /// </summary>
        public static bool TryNormalizePayment(string? value, out string? normalized)
        {
            normalized = null;
            var text = RemoveDiacritics((value ?? string.Empty).Trim()).ToLowerInvariant();
            if (text.Length == 0) return true;
            if (text.Contains("dinheiro")) normalized = "dinheiro";
            else if (text.Contains("pix")) normalized = "pix";
            else if (text.Contains("online") || text.Contains("link")) normalized = "link";
            else if (text.StartsWith("cart") || text.Contains("credito") || text.Contains("debito")) normalized = "cartao_entrega";
            return normalized != null;
        }

        private static string RemoveDiacritics(string value)
        {
            var decomposed = value.Normalize(System.Text.NormalizationForm.FormD);
            var builder = new System.Text.StringBuilder(decomposed.Length);
            foreach (var c in decomposed)
            {
                if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark) builder.Append(c);
            }
            return builder.ToString().Normalize(System.Text.NormalizationForm.FormC);
        }

        public static readonly string[] Channels = { "whatsapp", "app", "balcao" };

        /// <summary>Canais aceitos; vazio vira whatsapp. Null quando desconhecido.</summary>
        public static string? NormalizeChannel(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? "whatsapp" : value.Trim().ToLowerInvariant();
            return Array.IndexOf(Channels, normalized) >= 0 ? normalized : null;
        }

        /// <summary>Etiqueta de exibicao do cliente na lista do simulador.</summary>
        public static string ClienteEtiqueta(bool ativo, string[] tags, bool humano, int totalPedidos)
        {
            if (!ativo) return "Inativo";
            if (tags.Any(tag => string.Equals(tag, "VIP", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(tag, "Cliente VIP", StringComparison.OrdinalIgnoreCase))) return "VIP";
            if (humano) return "Em tratamento";
            return totalPedidos >= 5 ? "Frequente" : "Novo";
        }
    }
}
