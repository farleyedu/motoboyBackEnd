using System;
using System.Collections.Generic;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>
    /// Regras puras de estado (sem banco): traducao do status do MP, protecao contra evento fora de ordem e as maquinas de estado
    /// de intencao, cobranca do chat e estorno. Testadas em Tests/Unit/MercadoPagoNucleoTests.cs.
    /// </summary>
    public static class PagamentoStatusRules
    {
        /// <summary>Status do MP -> status local. Estorno parcial nao muda o status do MP (continua approved).</summary>
        public static string Traduzir(string mpStatus, string? statusDetail, decimal valor, decimal? valorEstornado)
        {
            var estornado = valorEstornado ?? 0m;
            return mpStatus switch
            {
                "pending" => StatusPagamento.Pendente,
                "in_process" => StatusPagamento.EmProcesso,
                "authorized" => StatusPagamento.Autorizado,
                "approved" when estornado > 0 && estornado < valor => StatusPagamento.EstornadoParcial,
                "approved" when estornado >= valor && valor > 0 => StatusPagamento.Estornado,
                "approved" => StatusPagamento.Aprovado,
                "rejected" => StatusPagamento.Recusado,
                "cancelled" when statusDetail == "expired" => StatusPagamento.Expirado,
                "cancelled" => StatusPagamento.Cancelado,
                "refunded" when estornado > 0 && estornado < valor => StatusPagamento.EstornadoParcial,
                "refunded" => StatusPagamento.Estornado,
                "charged_back" => StatusPagamento.Contestado,
                "in_mediation" => StatusPagamento.EmMediacao,
                _ => throw new PagamentoException(PagamentoCodigos.MercadoPagoRespostaInconsistente, 502, $"Status desconhecido do Mercado Pago: {mpStatus}.")
            };
        }

        /// <summary>Peso para desempate quando o MP nao manda data (ou manda a mesma): estado mais "avancado" ganha.</summary>
        public static int Peso(string status) => status switch
        {
            StatusPagamento.Pendente => 0,
            StatusPagamento.EmProcesso => 1,
            StatusPagamento.Autorizado => 2,
            StatusPagamento.Aprovado or StatusPagamento.Recusado or StatusPagamento.Cancelado or StatusPagamento.Expirado => 3,
            StatusPagamento.EmMediacao or StatusPagamento.EstornadoParcial => 4,
            StatusPagamento.Contestado => 5,
            StatusPagamento.Estornado => 6,
            _ => 0
        };

        private static bool DinheiroEntrou(string s) =>
            s is StatusPagamento.Aprovado or StatusPagamento.EstornadoParcial or StatusPagamento.Estornado or StatusPagamento.EmMediacao or StatusPagamento.Contestado;

        /// <summary>
        /// Aplica o retrato do MP sobre o estado salvo? Nunca quando o retrato e mais velho (date_last_updated menor), nunca quando
        /// o dinheiro ja entrou e o retrato diz que esta pendente, e no empate de data so se o estado avanca.
        /// </summary>
        public static bool DeveAplicar(string statusAtual, DateTime? atualizadoAtual, string statusNovo, DateTime? atualizadoNovo, decimal estornadoAtual, decimal estornadoNovo)
        {
            if (DinheiroEntrou(statusAtual) && StatusPagamento.Aberto(statusNovo)) return false;
            if (atualizadoAtual.HasValue && atualizadoNovo.HasValue)
            {
                if (atualizadoNovo.Value < atualizadoAtual.Value) return false;
                if (atualizadoNovo.Value > atualizadoAtual.Value) return true;
            }
            if (statusNovo == statusAtual) return estornadoNovo > estornadoAtual;
            // Mediacao resolvida a favor da loja volta para aprovado.
            if (statusAtual == StatusPagamento.EmMediacao && statusNovo == StatusPagamento.Aprovado) return atualizadoNovo.HasValue;
            return Peso(statusNovo) > Peso(statusAtual);
        }

        /// <summary>
        /// Novo status da intencao a partir de um pagamento dela. <paramref name="ehUltimoPagamento"/>: o pagamento e o da tentativa
        /// mais recente (um Pix antigo cancelado nao derruba a intencao que ja tem outro Pix aberto ou pago).
        /// </summary>
        public static string StatusDaIntent(string atual, string statusPagamento, bool ehUltimoPagamento)
        {
            var proposto = statusPagamento switch
            {
                StatusPagamento.Pendente => StatusIntent.AguardandoPagamento,
                StatusPagamento.EmProcesso or StatusPagamento.Autorizado => StatusIntent.Processando,
                StatusPagamento.Aprovado => StatusIntent.Pago,
                StatusPagamento.EstornadoParcial => StatusIntent.EstornadoParcial,
                StatusPagamento.Estornado => StatusIntent.Estornado,
                StatusPagamento.EmMediacao or StatusPagamento.Contestado => StatusIntent.Contestado,
                StatusPagamento.Recusado => StatusIntent.Recusado,
                StatusPagamento.Cancelado => StatusIntent.Cancelado,
                StatusPagamento.Expirado => StatusIntent.Expirado,
                _ => atual
            };

            var pagamentoComDinheiro = DinheiroEntrou(statusPagamento);
            if (!pagamentoComDinheiro && !ehUltimoPagamento) return atual;
            if (StatusIntent.FamiliaPaga(atual) && !pagamentoComDinheiro) return atual;
            return PodeIntent(atual, proposto) ? proposto : atual;
        }

        private static readonly Dictionary<string, string[]> TransicoesIntent = new()
        {
            [StatusIntent.Criado] = new[] { StatusIntent.AguardandoPagamento, StatusIntent.Processando, StatusIntent.Pago, StatusIntent.Recusado, StatusIntent.Cancelado, StatusIntent.Expirado },
            [StatusIntent.AguardandoPagamento] = new[] { StatusIntent.Processando, StatusIntent.Pago, StatusIntent.Recusado, StatusIntent.Cancelado, StatusIntent.Expirado },
            [StatusIntent.Processando] = new[] { StatusIntent.AguardandoPagamento, StatusIntent.Pago, StatusIntent.Recusado, StatusIntent.Cancelado, StatusIntent.Expirado },
            // Cartao recusado pode ser tentado de novo na mesma intencao.
            [StatusIntent.Recusado] = new[] { StatusIntent.AguardandoPagamento, StatusIntent.Processando, StatusIntent.Pago, StatusIntent.Cancelado, StatusIntent.Expirado },
            [StatusIntent.Pago] = new[] { StatusIntent.EstornadoParcial, StatusIntent.Estornado, StatusIntent.Contestado },
            [StatusIntent.EstornadoParcial] = new[] { StatusIntent.Estornado, StatusIntent.Contestado },
            [StatusIntent.Contestado] = new[] { StatusIntent.Pago, StatusIntent.EstornadoParcial, StatusIntent.Estornado },
            // Pix pago depois de expirado/cancelado no nosso lado: o dinheiro entrou, vale o pago.
            [StatusIntent.Expirado] = new[] { StatusIntent.Pago },
            [StatusIntent.Cancelado] = new[] { StatusIntent.Pago },
            [StatusIntent.Estornado] = Array.Empty<string>()
        };

        public static bool PodeIntent(string de, string para) =>
            de == para || (TransicoesIntent.TryGetValue(de, out var destinos) && Array.IndexOf(destinos, para) >= 0);

        private static readonly Dictionary<string, string[]> TransicoesCobranca = new()
        {
            // Aprovada vira 'criando' (gera o Pix); rejeitada ou abandonada vira 'cancelado'. Ainda nao tem intencao.
            [StatusCobranca.AguardandoAprovacao] = new[] { StatusCobranca.Criando, StatusCobranca.Cancelado },
            [StatusCobranca.Criando] = new[] { StatusCobranca.Aguardando, StatusCobranca.Recusado, StatusCobranca.Cancelado, StatusCobranca.Pago },
            [StatusCobranca.Aguardando] = new[] { StatusCobranca.Enviado, StatusCobranca.Pago, StatusCobranca.Expirado, StatusCobranca.Cancelado, StatusCobranca.Recusado },
            [StatusCobranca.Enviado] = new[] { StatusCobranca.Pago, StatusCobranca.Expirado, StatusCobranca.Cancelado, StatusCobranca.Recusado },
            [StatusCobranca.Pago] = new[] { StatusCobranca.ReembolsoSolicitado, StatusCobranca.Reembolsado },
            [StatusCobranca.ReembolsoSolicitado] = new[] { StatusCobranca.Reembolsado, StatusCobranca.Pago },
            [StatusCobranca.Expirado] = new[] { StatusCobranca.Pago },
            [StatusCobranca.Cancelado] = new[] { StatusCobranca.Pago },
            [StatusCobranca.Recusado] = Array.Empty<string>(),
            [StatusCobranca.Reembolsado] = Array.Empty<string>()
        };

        public static bool PodeCobranca(string de, string para) =>
            de == para || (TransicoesCobranca.TryGetValue(de, out var destinos) && Array.IndexOf(destinos, para) >= 0);

        /// <summary>Status da cobranca do chat que acompanha a intencao (null = nao muda).</summary>
        public static string? StatusDaCobranca(string atual, string statusIntent)
        {
            var proposto = statusIntent switch
            {
                StatusIntent.AguardandoPagamento or StatusIntent.Processando => atual == StatusCobranca.Criando ? StatusCobranca.Aguardando : null,
                // Estorno parcial concluido devolve a cobranca para 'pago' (o valor estornado fica na intencao).
                StatusIntent.EstornadoParcial => atual == StatusCobranca.Pago ? null : StatusCobranca.Pago,
                StatusIntent.Pago or StatusIntent.Contestado =>
                    atual is StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado ? null : StatusCobranca.Pago,
                StatusIntent.Estornado => StatusCobranca.Reembolsado,
                StatusIntent.Expirado => StatusCobranca.Expirado,
                StatusIntent.Cancelado => StatusCobranca.Cancelado,
                StatusIntent.Recusado => StatusCobranca.Recusado,
                _ => null
            };
            return proposto != null && proposto != atual && PodeCobranca(atual, proposto) ? proposto : null;
        }

        public static bool PodeEstorno(string de, string para) => (de, para) switch
        {
            (StatusEstorno.Solicitado, StatusEstorno.Processando or StatusEstorno.Concluido or StatusEstorno.Recusado or StatusEstorno.Falhou) => true,
            (StatusEstorno.Processando, StatusEstorno.Concluido or StatusEstorno.Recusado or StatusEstorno.Falhou) => true,
            _ => de == para
        };

        /// <summary>Status do cardapio_pedido_publico.status_pagamento a partir da intencao.</summary>
        public static string StatusPagamentoPrePedido(string statusIntent) => statusIntent switch
        {
            StatusIntent.Pago or StatusIntent.Contestado => "pago",
            StatusIntent.EstornadoParcial => "estornado_parcial",
            StatusIntent.Estornado => "estornado",
            StatusIntent.Recusado => "recusado",
            StatusIntent.Expirado or StatusIntent.Cancelado => "pendente",
            _ => "aguardando"
        };
    }
}
