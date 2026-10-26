using System;
using System.Globalization;
using System.Text;

namespace APIBack.Service
{
    /// <summary>Estados do reembolso do cardapio (cardapio_pedido_reembolso.status). So "reembolsado" depois do Mercado Pago confirmar.</summary>
    public static class CardapioReembolsoStatus
    {
        /// <summary>Pedido aceito: espera o gerente (antes do preparo) ou a analise (em preparo/entrega).</summary>
        public const string AguardandoAprovacao = "aguardando_aprovacao";
        /// <summary>Decidido; o pedido de estorno ainda nao foi aceito pelo Mercado Pago (ou vai ser repetido).</summary>
        public const string Pendente = "reembolso_pendente";
        /// <summary>O Mercado Pago aceitou o estorno e ainda esta processando.</summary>
        public const string EmProcessamento = "reembolso_em_processamento";
        public const string Reembolsado = "reembolsado";
        public const string Negado = "negado";
        /// <summary>O Mercado Pago recusou o estorno (ex.: saldo da loja): a loja precisa resolver.</summary>
        public const string Falhou = "falhou";

        public static bool Vivo(string status) => status is not (Negado or Falhou);
    }

    public static class CardapioReembolsoTipo
    {
        public const string Automatico = "automatico";
        public const string AprovacaoGerente = "aprovacao_gerente";
        public const string Analise = "analise";
    }

    public static class CardapioReembolsoMotivo
    {
        public const string RecusaLoja = "recusa_loja";
        public const string CancelamentoCliente = "cancelamento_cliente";
        /// <summary>O dinheiro entrou depois de o pedido ter sido recusado ou cancelado.</summary>
        public const string PagoAposEncerramento = "pago_apos_encerramento";
        /// <summary>Segundo pagamento do mesmo pedido (ex.: Pix pago e cartao aprovado).</summary>
        public const string PagamentoDuplicado = "pagamento_duplicado";
        /// <summary>Pagou e nunca confirmou o numero pelo WhatsApp.</summary>
        public const string ConfirmacaoExpirada = "confirmacao_expirada";
        public const string CancelamentoLoja = "cancelamento_loja";
    }

    /// <summary>Em que ponto o pedido esta quando alguem quer cancelar: decide quem aprova e quanto pode voltar.</summary>
    public enum FaseCancelamento
    {
        /// <summary>Ainda nao aceito: cancela a intencao (nao pago) ou estorna tudo automaticamente (pago).</summary>
        AntesDoAceite,
        /// <summary>Aceito e o preparo ainda nao comecou: o gerente aprova e o estorno e integral.</summary>
        AceitoAntesDoPreparo,
        /// <summary>Em preparo ou em entrega: vira uma solicitacao para analise (total ou parcial, com justificativa).</summary>
        EmPreparoOuEntrega,
        /// <summary>Recusado, cancelado ou expirado: nada a cancelar.</summary>
        Encerrado
    }

    /// <summary>
    /// Regras puras do pagamento online do cardapio web: qual meio o cliente escolheu, para onde vai a confirmacao do numero,
    /// a fase do cancelamento, as chaves de idempotencia e o status que o cliente ve. Pagamento confirmado e pedido aceito sao
    /// coisas diferentes: o pedido online so aparece para o restaurante depois do dinheiro confirmado pelo Mercado Pago.
    /// </summary>
    public static class CardapioPagamentoRules
    {
        public const string MetodoPix = "pix";
        public const string MetodoCartao = "cartao";

        /// <summary>Depois do aceite, quanto tempo a cozinha ainda nao comecou (o nucleo do delivery nao tem status de preparo).</summary>
        public static readonly TimeSpan JanelaAntesDoPreparo = TimeSpan.FromMinutes(5);

        /// <summary>Pagou e nao confirmou o numero: depois deste tempo o dinheiro volta sozinho.</summary>
        public static readonly TimeSpan PrazoConfirmacaoDepoisDePagar = TimeSpan.FromMinutes(60);

        /// <summary>
        /// Forma de pagamento do checkout -> meio online (pix/cartao) ou null (presencial: dinheiro, maquininha, Pix na entrega).
        /// Aceita "pix_online", "Pix online", "cartao_online", "Cartão online".
        /// </summary>
        public static string? MetodoOnline(string? formaPagamento)
        {
            var chave = Chave(formaPagamento);
            return chave switch
            {
                "pix_online" or "pix_pelo_app" => MetodoPix,
                "cartao_online" or "credito_online" => MetodoCartao,
                _ => null
            };
        }

        /// <summary>Texto que o restaurante e o motoboy veem no pedido real: deixa claro que nao ha nada a cobrar na entrega.</summary>
        public static string FormaPagamentoDoPedido(string? metodo) =>
            metodo == MetodoCartao ? "Pago online - cartao" : "Pago online - Pix";

        /// <summary>Numero confirmado: presencial ou online ja pago vai ao restaurante; online sem pagamento espera o dinheiro.</summary>
        public static string StatusAposConfirmar(bool pagamentoOnline, bool pago) =>
            pagamentoOnline && !pago ? CardapioPedidoStatus.AguardandoPagamento : CardapioPedidoStatus.AguardandoAceite;

        /// <summary>Status em que o cliente ainda pode pagar (ou trocar de Pix para cartao).</summary>
        public static bool AceitaPagamento(string status) => status is CardapioPedidoStatus.Pendente or CardapioPedidoStatus.AguardandoCodigo
            or CardapioPedidoStatus.Expirado or CardapioPedidoStatus.AguardandoPagamento;

        /// <summary>
        /// Fase do cancelamento. <paramref name="statusPedidoReal"/> e o status do pedido do delivery (pendente, atribuido, em_rota...),
        /// nulo na retirada. Aceito ha pouco e sem motoboy = antes do preparo.
        /// </summary>
        public static FaseCancelamento Fase(string status, DateTimeOffset? aceitoEm, string? statusPedidoReal, DateTimeOffset agora)
        {
            switch (status)
            {
                case CardapioPedidoStatus.Recusado or CardapioPedidoStatus.Cancelado:
                    return FaseCancelamento.Encerrado;
                case CardapioPedidoStatus.Aceito:
                    var saiu = statusPedidoReal is "atribuido" or "em_rota" or "aguardando_motoboy" or "concluido" or "encerrado_auto";
                    if (saiu || statusPedidoReal == "cancelado") return FaseCancelamento.EmPreparoOuEntrega;
                    return aceitoEm.HasValue && agora - aceitoEm.Value <= JanelaAntesDoPreparo
                        ? FaseCancelamento.AceitoAntesDoPreparo
                        : FaseCancelamento.EmPreparoOuEntrega;
                case CardapioPedidoStatus.Aceitando:
                    // O pedido real esta nascendo agora: trata como aceito antes do preparo (o gerente decide).
                    return FaseCancelamento.AceitoAntesDoPreparo;
                default:
                    return FaseCancelamento.AntesDoAceite;
            }
        }

        // ---- Chaves de idempotencia (convencao em docs/pagamentos/CONTRATOS.md §4.2) -------------------------

        /// <summary>Primeiro Pix: cardapio:{id}. Pix novo depois de um vencido/cancelado: cardapio:{id}:pix:{n} (n = 2, 3...).</summary>
        public static string ChavePix(Guid prePedidoId, int numero) =>
            numero <= 1 ? $"cardapio:{prePedidoId:N}" : $"cardapio:{prePedidoId:N}:pix:{numero}";

        /// <summary>Cartao: cardapio:{id}:cartao (recusado pode tentar outro cartao na mesma intencao); encerrada: cardapio:{id}:cartao:{n}.</summary>
        public static string ChaveCartao(Guid prePedidoId, int numero) =>
            numero <= 1 ? $"cardapio:{prePedidoId:N}:cartao" : $"cardapio:{prePedidoId:N}:cartao:{numero}";

        public static bool EhChaveCartao(string? chave) => chave != null && chave.Contains(":cartao", StringComparison.Ordinal);

        /// <summary>Uma decisao, um reembolso: a mesma recusa/cancelamento/webhook repetido cai na mesma linha.</summary>
        public static string ChaveReembolso(Guid prePedidoId, Guid intentId, string motivo) =>
            $"cardapio:{prePedidoId:N}:{intentId:N}:{motivo}";

        // ---- O que o cliente ve ------------------------------------------------------------------------------

        /// <summary>
        /// Status do pagamento na tela do cliente: nenhum (presencial), aguardando, processando, aprovado, recusado, expirado, cancelado,
        /// reembolso_pendente, reembolso_em_processamento, reembolsado, reembolsado_parcial.
        /// O reembolso em andamento manda sobre o status da intencao; reembolso falhou aparece como pendente (a loja resolve).
        /// </summary>
        public static string StatusParaCliente(bool online, string? statusIntent, string? statusReembolso, decimal valorPago, decimal valorEstornado)
        {
            if (!online) return "nenhum";
            if (statusReembolso is CardapioReembolsoStatus.Pendente or CardapioReembolsoStatus.Falhou or CardapioReembolsoStatus.AguardandoAprovacao)
                return CardapioReembolsoStatus.Pendente;
            if (statusReembolso == CardapioReembolsoStatus.EmProcessamento) return CardapioReembolsoStatus.EmProcessamento;
            return statusIntent switch
            {
                null or "criado" or "aguardando_pagamento" => "aguardando",
                "processando" => "processando",
                "pago" or "contestado" => "aprovado",
                "estornado" => "reembolsado",
                "estornado_parcial" => valorEstornado > 0 && valorEstornado < valorPago ? "reembolsado_parcial" : "reembolsado",
                "recusado" => "recusado",
                "expirado" => "expirado",
                "cancelado" => "cancelado",
                _ => "aguardando"
            };
        }

        public static string PedidoRecusadoComEstorno(string nomeCliente, string loja, string? motivo, decimal valor)
        {
            var porque = string.IsNullOrWhiteSpace(motivo) ? string.Empty : $" Motivo: {motivo.Trim()}.";
            return $"{PrimeiroNome(nomeCliente)}, infelizmente a {loja} não conseguiu aceitar seu pedido agora.{porque} " +
                   $"O valor pago ({CardapioConfirmacaoRules.Dinheiro(valor)}) será devolvido integralmente pelo mesmo meio de pagamento.";
        }

        private static string Chave(string? texto)
        {
            var semAcento = (texto ?? string.Empty).Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(semAcento.Length);
            foreach (var ch in semAcento)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(char.IsLetterOrDigit(ch) ? ch : '_');
            }
            return builder.ToString().Trim('_');
        }

        private static string PrimeiroNome(string? nome)
        {
            var primeiro = (nome ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return primeiro.Length == 0 ? "Olá" : primeiro[0];
        }
    }
}
