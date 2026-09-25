using System;
using System.Collections.Generic;

namespace APIBack.Model.Delivery
{
    /// <summary>
    /// De onde o pedido veio. Todas as origens passam pelo mesmo nucleo de criacao; o que muda
    /// entre elas e o rigor das regras (ver <see cref="Rules"/>). Os valores sao gravados em
    /// pedido.origem e restritos por CHECK no banco (20260925_05_constraints.sql).
    /// </summary>
    public static class PedidoOrigem
    {
        public const string Atendente = "atendente";
        public const string CardapioWeb = "cardapio_web";
        public const string Ifood = "ifood";
        public const string IaWhatsapp = "ia_whatsapp";
        public const string Simulador = "simulador";

        public static readonly IReadOnlyList<string> All = new[] { Atendente, CardapioWeb, Ifood, IaWhatsapp, Simulador };

        /// <summary>Normaliza (aparar, minusculas); vazio vira "atendente". Null quando desconhecida.</summary>
        public static string? Normalize(string? value)
        {
            var normalized = string.IsNullOrWhiteSpace(value) ? Atendente : value.Trim().ToLowerInvariant();
            return Array.IndexOf((string[])All, normalized) >= 0 ? normalized : null;
        }

        /// <summary>Rigor das regras do estabelecimento para cada origem.</summary>
        public sealed record OriginRules(
            bool EnforceStoreRules,
            bool AllowFreeLines,
            bool AllowFeeOverride);

        /// <summary>
        /// - cardapio_web e ia_whatsapp: o cliente nao manda preco nem linha avulsa; aceita pedidos,
        ///   pedido minimo e raio bloqueiam; taxa vem sempre do servidor.
        /// - atendente: as regras viram avisos (o atendente decide), pode informar linha avulsa e taxa.
        /// - ifood e simulador: preco e taxa vem de fora; sem regras do estabelecimento.
        /// </summary>
        public static OriginRules Rules(string origin) => origin switch
        {
            CardapioWeb or IaWhatsapp => new OriginRules(EnforceStoreRules: true, AllowFreeLines: false, AllowFeeOverride: false),
            _ => new OriginRules(EnforceStoreRules: false, AllowFreeLines: true, AllowFeeOverride: true),
        };
    }
}
