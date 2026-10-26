using System;

namespace APIBack.Payments.MercadoPago
{
    /// <summary>Ambientes do MP. Linhas, tokens e eventos de um nunca valem no outro.</summary>
    public static class Ambientes
    {
        public const string Sandbox = "sandbox";
        public const string Producao = "producao";

        public static bool Valido(string? ambiente) => ambiente is Sandbox or Producao;

        public static string Normalizar(string? ambiente) =>
            string.Equals(ambiente?.Trim(), Producao, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ambiente?.Trim(), "production", StringComparison.OrdinalIgnoreCase)
                ? Producao
                : Sandbox;

        /// <summary>live_mode que o MP devolve para pagamentos do ambiente.</summary>
        public static bool LiveMode(string ambiente) => ambiente == Producao;
    }

    /// <summary>
    /// Secao <c>Payments:MercadoPago</c>. Segredos de plataforma (aplicacao e recebedor Zippy) moram no banco, cifrados, escritos pela
    /// Gestao; os campos <c>Bootstrap*</c> so valem enquanto o banco nao tem a linha do ambiente (subir sandbox antes da tela da Gestao).
    /// </summary>
    public sealed class MercadoPagoOptions
    {
        public const string Secao = "Payments:MercadoPago";

        /// <summary>Ambiente ativo deste servidor: sandbox ou producao.</summary>
        public string Ambiente { get; set; } = Ambientes.Sandbox;
        public string BaseUrl { get; set; } = "https://api.mercadopago.com/";
        public int TimeoutSegundos { get; set; } = 20;

        /// <summary>URL publica do webhook mandada em cada pagamento (ex.: https://api.zippygo.com.br/api/webhooks/mercadopago).</summary>
        public string? NotificationUrl { get; set; }
        /// <summary>Tolerancia do ts do x-signature, em segundos, para frente e para tras.</summary>
        public int WebhookToleranciaSegundos { get; set; } = 600;
        /// <summary>Recusa pagamento/aviso cujo live_mode nao bate com o ambiente (desligar so se a homologacao com usuario de teste exigir).</summary>
        public bool ExigirLiveModeDoAmbiente { get; set; } = true;

        public int PixValidadePadraoMinutos { get; set; } = 30;
        /// <summary>O MP exige e-mail do pagador no Pix; sem e-mail do cliente vai este (nunca dado de outro cliente).</summary>
        public string EmailPagadorPadrao { get; set; } = "pagador@zippygo.com.br";
        /// <summary>Texto na fatura do cartao (ate 13 caracteres).</summary>
        public string? StatementDescriptor { get; set; } = "ZIPPY";

        /// <summary>Split/comissao de marketplace: preparado e DESLIGADO (comissao zero).</summary>
        public bool SplitHabilitado { get; set; }
        public decimal ComissaoPercentual { get; set; }

        /// <summary>Endereco do painel (zippy-admin) para onde o callback OAuth devolve a loja. Sem valor: Rastreio:PublicBaseUrl.</summary>
        public string? PainelUrl { get; set; }
        /// <summary>Endereco publico desta API (mostrado na Gestao para o callback e o webhook). Sem valor: deduzido da NotificationUrl.</summary>
        public string? ApiPublicaUrl { get; set; }

        public int ConciliadorIntervaloSegundos { get; set; } = 120;
        /// <summary>Pagamento aberto sem noticia ha mais que isto e consultado no MP.</summary>
        public int ConciliadorIdadeMinimaSegundos { get; set; } = 120;
        public int WebhookMaxTentativas { get; set; } = 8;
        public int OutboxIntervaloMilissegundos { get; set; } = 1000;

        public string? BootstrapClientId { get; set; }
        public string? BootstrapClientSecret { get; set; }
        public string? BootstrapPublicKey { get; set; }
        public string? BootstrapWebhookSecret { get; set; }
        public string? BootstrapRedirectUri { get; set; }
        public string? BootstrapZippyAccessToken { get; set; }
        public long? BootstrapZippyUserId { get; set; }
        public string? BootstrapZippyPublicKey { get; set; }

        public string AmbienteAtivo => Ambientes.Normalizar(Ambiente);
    }
}
