using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace APIBack.Payments.Provedores
{
    public static class ProvedoresPagamento
    {
        public const string MercadoPago = "mercadopago";
        public const string InfinitePay = "infinitepay";

        public static bool Valido(string? provedor) => provedor is MercadoPago or InfinitePay;
    }

    public static class FluxosPagamento
    {
        public const string CheckoutOnline = "checkout_online";
        public const string PixExclusivo = "pix_exclusivo";
    }

    public static class StatusConfiguracaoProvedor
    {
        public const string NaoConfigurado = "nao_configurado";
        public const string Configurado = "configurado";
        public const string RecebimentoValidado = "recebimento_validado";
        public const string Erro = "erro";
    }

    public static class ProvedorPagamentoCodigos
    {
        public const string NaoSuportado = "PAG_RECURSO_NAO_SUPORTADO";
        public const string ProvedorNaoConfigurado = "PAG_PROVEDOR_NAO_CONFIGURADO";
        public const string ContaDeOutraLoja = "PAG_CONTA_PROVEDOR_DE_OUTRA_LOJA";
    }

    public sealed record ProvedorCapacidades(
        bool PixEmbedded,
        bool CheckoutHospedado,
        bool PixExclusivo,
        bool CartaoHospedado,
        bool DescontoPix,
        bool EstornoApi);

    public static class ProvedorCapacidadesCatalogo
    {
        private static readonly ProvedorCapacidades MercadoPago = new(true, false, true, false, false, true);
        private static readonly ProvedorCapacidades InfinitePay = new(false, true, false, true, true, false);

        public static ProvedorCapacidades Obter(string provedor, string fluxo)
        {
            if (fluxo is not (FluxosPagamento.CheckoutOnline or FluxosPagamento.PixExclusivo))
                throw new ArgumentOutOfRangeException(nameof(fluxo));
            return provedor switch
            {
                ProvedoresPagamento.MercadoPago => MercadoPago,
                ProvedoresPagamento.InfinitePay => InfinitePay,
                _ => throw new ArgumentOutOfRangeException(nameof(provedor))
            };
        }

        public static void Exigir(string provedor, string capacidade, bool disponivel)
        {
            if (!disponivel)
                throw APIBack.Payments.MercadoPago.PagamentoException.Regra(ProvedorPagamentoCodigos.NaoSuportado,
                    $"O recurso '{capacidade}' nao esta disponivel para o provedor '{provedor}'.");
        }
    }

    /// <summary>Snapshot imutavel usado para criar uma intencao sem consultar novamente a preferencia da loja.</summary>
    public sealed record VinculoProvedorPagamento(string Provedor, Guid IdEstabelecimento, Guid IdContaVersao)
    {
        public static VinculoProvedorPagamento InfinitePay(Guid loja, Guid contaLoja, Guid contaVersao)
        {
            if (loja == Guid.Empty || contaLoja != loja || contaVersao == Guid.Empty)
                throw APIBack.Payments.MercadoPago.PagamentoException.Regra(ProvedorPagamentoCodigos.ContaDeOutraLoja,
                    "A conta recebedora nao pertence a loja selecionada.");
            return new VinculoProvedorPagamento(ProvedoresPagamento.InfinitePay, loja, contaVersao);
        }
    }

    public sealed class ProvedorCheckoutRequest
    {
        public Guid IdEstabelecimento { get; init; }
        public Guid IdContaVersao { get; init; }
        public Guid IntentId { get; init; }
        public decimal Valor { get; init; }
        public string Descricao { get; init; } = string.Empty;
        public string IdempotencyKey { get; init; } = string.Empty;
    }

    /// <summary>Retorno comum; checkout hospedado usa Link e nunca simula QR/copia-e-cola.</summary>
    public sealed class ProvedorCheckoutResultado
    {
        public string Provedor { get; init; } = string.Empty;
        public string Status { get; init; } = string.Empty;
        public string Metodo { get; init; } = string.Empty;
        public string Checkout { get; init; } = string.Empty;
        public string? Link { get; init; }
        public string IdempotencyKey { get; init; } = string.Empty;
        public Guid ContaVersaoId { get; init; }
        public string? ReferenciaProvedor { get; init; }
    }

    /// <summary>Contrato para adaptadores futuros. Esta etapa nao cria cobrancas reais na InfinitePay.</summary>
    public interface IProvedorCheckoutGateway
    {
        string Provedor { get; }
        ProvedorCapacidades Capacidades(string fluxo);
        Task<ProvedorCheckoutResultado> CriarAsync(ProvedorCheckoutRequest request, CancellationToken ct = default);
    }

    public sealed record PreferenciaProvedorResolvida(string Fluxo, bool Habilitado, string? Provedor, bool Herdada);

    public interface IPagamentoProvedorPreferenciaResolver
    {
        Task<PreferenciaProvedorResolvida> ResolverAsync(Guid loja, string fluxo, CancellationToken ct = default);
    }
}
