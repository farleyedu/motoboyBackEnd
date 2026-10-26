using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using APIBack.Payments.MercadoPago;

namespace APIBack.Payments.Chat
{
    /// <summary>
    /// Regras puras da cobranca do chat (sem banco): valor, saldo, limite de alcada, janela de 24 h, textos e parametros do modelo
    /// da Meta. Testadas em Tests/Unit/CobrancaChatRegrasTests.cs.
    /// </summary>
    public static class CobrancaChatRegras
    {
        public const decimal ValorMaximo = 999_999.99m;
        public const int DescricaoMax = 140;
        public const int MotivoMax = 300;
        /// <summary>Clique duplo no Enviar: dentro deste intervalo devolve o envio que acabou de sair em vez de mandar de novo.</summary>
        public static readonly TimeSpan JanelaReenvioDuplicado = TimeSpan.FromSeconds(15);

        private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
        private static readonly Regex Variavel = new(@"\{\{\s*(\d+)\s*\}\}", RegexOptions.Compiled);

        public static bool ValorValido(decimal valor) => valor > 0 && valor <= ValorMaximo && decimal.Round(valor, 2) == valor;

        public static void ExigirValor(decimal? valor)
        {
            if (valor is not { } v || !ValorValido(v))
                throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Valor deve ser positivo, com no maximo 2 casas e ate R$ 999.999,99.");
        }

        public static string ExigirDescricao(string? descricao, string? padrao = null)
        {
            var d = string.IsNullOrWhiteSpace(descricao) ? padrao?.Trim() : descricao.Trim();
            if (string.IsNullOrEmpty(d) || d.Length > DescricaoMax)
                throw PagamentoException.Invalida($"Descricao obrigatoria (ate {DescricaoMax} caracteres).", "descricao");
            return d;
        }

        public static string? Motivo(string? motivo)
        {
            var m = motivo?.Trim();
            if (string.IsNullOrEmpty(m)) return null;
            return m.Length <= MotivoMax ? m : m[..MotivoMax];
        }

        public static string ExigirChave(string? chave)
        {
            if (string.IsNullOrWhiteSpace(chave) || chave.Trim().Length > 120)
                throw PagamentoException.Invalida("idempotencyKey obrigatoria (ate 120 caracteres).", "idempotencyKey");
            return chave.Trim();
        }

        /// <summary>Tipo final da cobranca: vinculada quando ha pedido ou pre-pedido; o campo tipo, se vier, tem de concordar.</summary>
        public static string Tipo(string? informado, int? pedidoId, Guid? prePedidoId)
        {
            var vinculada = pedidoId.HasValue || prePedidoId.HasValue;
            var tipo = vinculada ? TiposCobranca.Vinculada : TiposCobranca.Avulsa;
            if (!string.IsNullOrWhiteSpace(informado) && !string.Equals(informado.Trim(), tipo, StringComparison.OrdinalIgnoreCase))
                throw PagamentoException.Invalida(vinculada
                    ? "Cobranca com pedido e sempre do tipo vinculada."
                    : "Cobranca vinculada exige pedidoId ou prePedidoId.", "tipo");
            if (pedidoId is <= 0) throw PagamentoException.Invalida("pedidoId invalido.", "pedidoId");
            return tipo;
        }

        /// <summary>Pedido ja quitado: status de pagamento textual do pedido ("Pago", "pago_online"...).</summary>
        public static bool StatusPago(string? statusPagamento) =>
            statusPagamento?.Trim().ToLowerInvariant() is "pago" or "paid" or "aprovado" or "approved" or "pago_online" or "settled";

        /// <summary>Saldo devido: total do pedido menos o que ja entrou online (liquido de estornos). Nunca negativo.</summary>
        public static decimal Saldo(decimal? total, decimal recebido) => Math.Max(0m, decimal.Round((total ?? 0m) - recebido, 2));

        /// <summary>
        /// Valor da cobranca vinculada: SEMPRE o saldo calculado no servidor. Se o atendente mandou um valor, ele so serve de conferencia
        /// (tela desatualizada) e precisa ser igual ao saldo.
        /// </summary>
        public static decimal ValorVinculada(AlvoCobranca alvo, decimal? informado)
        {
            if (StatusPago(alvo.StatusPagamento))
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja esta pago.");
            var saldo = Saldo(alvo.Total, alvo.Recebido);
            if (alvo.Total is null or <= 0)
                throw PagamentoException.Regra(PagamentoCodigos.PedidoNaoPagavel, "Pedido sem valor a cobrar.");
            if (saldo <= 0)
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Este pedido ja esta pago.");
            if (informado.HasValue && informado.Value != saldo)
                throw new PagamentoException(CobrancaChatCodigos.ValorDivergente, 422, "O valor nao confere com o saldo do pedido.",
                    new Dictionary<string, object?> { ["saldo"] = saldo });
            return saldo;
        }

        /// <summary>Acima do limite da loja e sem poder de aprovacao: a cobranca nasce aguardando aprovacao (sem Pix).</summary>
        public static bool ExigeAprovacao(decimal valor, decimal? limite, bool podeAprovar) =>
            !podeAprovar && limite.HasValue && valor > limite.Value;

        /// <summary>Conversa aberta e cliente falou nas ultimas 24 h: texto livre permitido.</summary>
        public static bool JanelaAberta(DateTime? janelaFim, string? estado, DateTime agora) =>
            janelaFim.HasValue && janelaFim.Value > agora &&
            estado is not ("fechado_automaticamente" or "fechado_agente" or "arquivada");

        public static string Formato(string? formato) => (formato ?? "link").Trim().ToLowerInvariant() switch
        {
            "" or "link" => "link",
            "copia_e_cola" => "copia_e_cola",
            "ambos" => "ambos",
            _ => throw PagamentoException.Invalida("formato deve ser link, copia_e_cola ou ambos.", "formato")
        };

        public static string Moeda(decimal valor) => valor.ToString("C", PtBr);

        public static string Hora(DateTime? utc)
        {
            if (utc == null) return string.Empty;
            TimeZoneInfo tz;
            try { tz = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo"); }
            catch (TimeZoneNotFoundException) { tz = TimeZoneInfo.FindSystemTimeZoneById("E. South America Standard Time"); }
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), tz).ToString("HH:mm", PtBr);
        }

        /// <summary>Mensagens de texto (dentro da janela). Copia e cola vai sozinho numa segunda mensagem, facil de copiar no celular.</summary>
        public static IReadOnlyList<string> Textos(string formato, string descricao, decimal valor, DateTime? expiraEm, string? link, string? copiaECola)
        {
            var validade = expiraEm.HasValue ? $"\nValido ate {Hora(expiraEm)}." : string.Empty;
            var cabecalho = $"*{descricao}*\nValor: {Moeda(valor)}";
            var lista = new List<string>();
            if (formato is "link" or "ambos")
            {
                if (string.IsNullOrEmpty(link)) throw PagamentoException.Estado("Link de pagamento indisponivel.");
                lista.Add($"{cabecalho}\nPague com Pix pelo link: {link}{validade}");
            }
            if (formato is "copia_e_cola" or "ambos")
            {
                if (string.IsNullOrEmpty(copiaECola)) throw PagamentoException.Estado("Codigo Pix indisponivel.");
                if (formato == "copia_e_cola") lista.Add($"{cabecalho}\nCopie o codigo Pix abaixo e cole no app do seu banco.{validade}");
                lista.Add(copiaECola);
            }
            return lista;
        }

        public static string TextoConfirmacao(string descricao, decimal valor) =>
            $"Pagamento de {Moeda(valor)} recebido. Obrigado! ({descricao})";

        /// <summary>Numero de variaveis distintas ({{n}}) no corpo do modelo.</summary>
        public static int ContarVariaveis(string? corpo) =>
            string.IsNullOrEmpty(corpo) ? 0 : Variavel.Matches(corpo).Select(m => m.Groups[1].Value).Distinct().Count();

        /// <summary>
        /// Parametros do corpo do modelo de cobranca. Convencao: 1 variavel = link; 2 = valor, link; 3 = valor, descricao, link.
        /// Mais de 3 variaveis nao sao preenchidas por nos: o modelo nao serve (null).
        /// </summary>
        public static IReadOnlyList<string>? ParametrosModelo(int variaveis, string descricao, decimal valor, string link) => variaveis switch
        {
            0 => Array.Empty<string>(),
            1 => new[] { link },
            2 => new[] { Moeda(valor), link },
            3 => new[] { Moeda(valor), descricao, link },
            _ => null
        };

        /// <summary>Acoes que o balao oferece (o backend confere de novo em cada rota).</summary>
        public static void PreencherAcoes(CobrancaChatDto d, bool podeAprovar, bool podeReembolsar, bool temSucessoraViva)
        {
            var aberta = d.Status is StatusCobranca.Aguardando or StatusCobranca.Enviado;
            d.PodeEnviar = aberta && (d.ExpiraEm == null || d.ExpiraEm > DateTime.UtcNow);
            d.PodeCancelar = aberta || d.Status is StatusCobranca.AguardandoAprovacao or StatusCobranca.Criando;
            d.PodeReemitir = !temSucessoraViva && (aberta || d.Status is StatusCobranca.Expirado or StatusCobranca.Cancelado or StatusCobranca.Recusado);
            var pagoComSaldo = d.Status == StatusCobranca.Pago && d.ValorEstornado < d.Valor;
            d.PodeReembolsar = pagoComSaldo && podeReembolsar;
            d.PodeSolicitarAprovacao = d.AprovacaoPendente == null && pagoComSaldo && !(podeReembolsar && podeAprovar);
        }
    }
}
