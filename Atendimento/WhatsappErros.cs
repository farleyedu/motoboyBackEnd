using System;
using System.Collections.Generic;
using APIBack.Service;

namespace APIBack.Atendimento
{
    /// <summary>
    /// Falha do WhatsApp explicada para quem usa a tela: codigo estavel (o front decide o que mostrar), o que aconteceu, o passo para
    /// resolver (no painel da Meta ou no campo da tela) e qual campo corrigir.
    /// </summary>
    public sealed record WhatsappErro(string Codigo, string Mensagem, string ComoResolver, string? Campo, string? Link = null)
    {
        /// <summary>Formato de <c>details</c> na resposta: { comoResolver, campo, link? }.</summary>
        public object Detalhes => new Dictionary<string, object?> { ["comoResolver"] = ComoResolver, ["campo"] = Campo, ["link"] = Link };
    }

    /// <summary>
    /// Erro de WhatsApp com codigo estavel. Herda de <see cref="RequestValidationException"/> para quem ainda trata so a validacao
    /// de campo (Errors traz o campo e a mensagem); a API devolve o formato completo { success, error, code, details }.
    /// </summary>
    public sealed class WhatsappException : RequestValidationException
    {
        public WhatsappException(WhatsappErro erro, int? http = null)
            : base(erro.Mensagem, new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
            {
                [erro.Campo ?? "whatsapp"] = new List<string> { erro.Mensagem }
            })
        {
            Erro = erro;
            Http = http ?? HttpDoCodigo(erro.Codigo);
        }

        public WhatsappErro Erro { get; }
        public int Http { get; }

        /// <summary>Erro de configuracao do servidor ou da Meta fora do ar = 503; numero de outra loja = 409; o resto e dado errado do usuario = 422.</summary>
        private static int HttpDoCodigo(string codigo) => codigo switch
        {
            WhatsappCodigos.TokenGlobalAusente or WhatsappCodigos.ChaveCifragemAusente or WhatsappCodigos.MetaIndisponivel => 503,
            WhatsappCodigos.NumeroOutraLoja => 409,
            _ => 422
        };
    }

    /// <summary>Em que chamada da Meta o erro aconteceu: o mesmo codigo da Meta significa coisas diferentes em cada uma.</summary>
    public enum ChamadaMeta
    {
        /// <summary>GET /{phone-number-id}: conferir o numero e o token.</summary>
        Numero,
        /// <summary>POST /{app-id}/subscriptions: registrar a URL do webhook no app (token de app = appId|appSecret).</summary>
        AssinaturaDoApp,
        /// <summary>POST /{waba-id}/subscribed_apps: ligar o app a conta do WhatsApp Business (token do cliente).</summary>
        AppDaConta
    }

    public static class WhatsappCodigos
    {
        public const string TokenInvalido = "WA_TOKEN_INVALIDO";
        public const string SemPermissao = "WA_SEM_PERMISSAO";
        public const string IdNaoEncontrado = "WA_ID_NAO_ENCONTRADO";
        public const string IdParecePhone = "WA_ID_PARECE_TELEFONE";
        public const string AppSecretInvalido = "WA_APP_SECRET_INVALIDO";
        public const string WabaInvalida = "WA_WABA_INVALIDA";
        public const string NumeroOutraLoja = "WA_NUMERO_OUTRA_LOJA";
        public const string TokenGlobalAusente = "WA_TOKEN_GLOBAL_AUSENTE";
        public const string ChaveCifragemAusente = "WA_CHAVE_CIFRAGEM_AUSENTE";
        public const string WebhookNaoRegistrado = "WA_WEBHOOK_NAO_REGISTRADO";
        public const string SemRecebimento = "WA_SEM_RECEBIMENTO";
        public const string CampoInvalido = "WA_CAMPO_INVALIDO";
        public const string MetaIndisponivel = "WA_META_INDISPONIVEL";
        public const string MetaErro = "WA_META_ERRO";
    }

    /// <summary>Catalogo das mensagens de erro do WhatsApp (todas dizem o passo para corrigir).</summary>
    public static class WhatsappErros
    {
        public const string LinkPainelApp = "https://developers.facebook.com/apps";
        public const string LinkUsuariosDoSistema = "https://business.facebook.com/settings/system-users";
        public const string LinkWhatsappManager = "https://business.facebook.com/wa/manage/phone-numbers";

        public static WhatsappErro TokenInvalido() => new(WhatsappCodigos.TokenInvalido,
            "O token da Meta e invalido ou expirou.",
            "No Meta Business Suite (Configuracoes > Usuarios > Usuarios do sistema), gere um token para o usuario do sistema com as permissoes " +
            "whatsapp_business_messaging e whatsapp_business_management, sem prazo de validade, e cole no campo Token.",
            "token", LinkUsuariosDoSistema);

        public static WhatsappErro SemPermissao() => new(WhatsappCodigos.SemPermissao,
            "O token nao tem permissao para este numero ou conta do WhatsApp.",
            "No Meta Business Suite (Usuarios do sistema), clique em Atribuir ativos, escolha a conta do WhatsApp Business deste numero " +
            "com controle total e confirme que o token foi gerado com as permissoes whatsapp_business_messaging e whatsapp_business_management.",
            "token", LinkUsuariosDoSistema);

        public static WhatsappErro IdNaoEncontrado() => new(WhatsappCodigos.IdNaoEncontrado,
            "A Meta nao encontrou este Phone Number ID (ou o token nao enxerga esse numero).",
            "No painel do seu app (developers.facebook.com > seu app > WhatsApp > Configuracao da API), copie o campo \"ID do numero de telefone\". " +
            "Nao e o telefone nem o ID da conta do WhatsApp Business.",
            "phoneNumberId", LinkPainelApp);

        public static WhatsappErro IdParecePhone() => new(WhatsappCodigos.IdParecePhone,
            "Isso parece um telefone, nao o Phone Number ID da Meta.",
            "O Phone Number ID tem so digitos e uns 15 numeros. Encontre em developers.facebook.com > seu app > WhatsApp > Configuracao da API, " +
            "no campo \"ID do numero de telefone\" (logo abaixo do telefone).",
            "phoneNumberId", LinkPainelApp);

        public static WhatsappErro IdVazio() => new(WhatsappCodigos.CampoInvalido,
            "Informe o Phone Number ID da Meta.",
            "Copie o \"ID do numero de telefone\" em developers.facebook.com > seu app > WhatsApp > Configuracao da API.",
            "phoneNumberId", LinkPainelApp);

        public static WhatsappErro WabaFormato() => new(WhatsappCodigos.WabaInvalida,
            "O ID da conta do WhatsApp Business (WABA) esta vazio ou invalido.",
            "Copie o \"ID da conta do WhatsApp Business\" em developers.facebook.com > seu app > WhatsApp > Configuracao da API " +
            "(ou no WhatsApp Manager > Configuracoes da conta). Sao so digitos e e diferente do Phone Number ID.",
            "wabaId", LinkWhatsappManager);

        public static WhatsappErro WabaNaoEncontrada() => new(WhatsappCodigos.WabaInvalida,
            "A Meta nao encontrou esta conta do WhatsApp Business (WABA) ou o token nao tem acesso a ela.",
            "Confira o \"ID da conta do WhatsApp Business\" em developers.facebook.com > seu app > WhatsApp > Configuracao da API e confirme " +
            "que o usuario do sistema do token tem esta conta atribuida (Usuarios do sistema > Atribuir ativos).",
            "wabaId", LinkWhatsappManager);

        public static WhatsappErro AppIdFormato() => new(WhatsappCodigos.AppSecretInvalido,
            "O ID do app esta vazio ou invalido.",
            "Copie o \"ID do aplicativo\" em developers.facebook.com > seu app > Configuracoes do app > Basico. Sao so digitos.",
            "appId", LinkPainelApp);

        public static WhatsappErro AppSecretFormato() => new(WhatsappCodigos.AppSecretInvalido,
            "A chave secreta do app esta vazia ou invalida.",
            "Em developers.facebook.com > seu app > Configuracoes do app > Basico, clique em Mostrar na \"Chave secreta do app\" e cole no campo.",
            "appSecret", LinkPainelApp);

        public static WhatsappErro AppSecretInvalido() => new(WhatsappCodigos.AppSecretInvalido,
            "A Meta recusou o ID do app ou a chave secreta do app.",
            "Em developers.facebook.com > seu app > Configuracoes do app > Basico, confira o \"ID do aplicativo\" e a \"Chave secreta do app\" " +
            "(clique em Mostrar) e cole os dois de novo. Se redefiniu a chave secreta, use a nova.",
            "appSecret", LinkPainelApp);

        public static WhatsappErro NumeroOutraLoja(string campo = "phoneNumberId") => new(WhatsappCodigos.NumeroOutraLoja,
            "Este numero ja esta conectado a outra loja.",
            "Um numero de WhatsApp pertence a uma loja so. Se o numero e seu, peca a Zippy para remove-lo da outra loja; " +
            "se nao, confira o Phone Number ID.",
            campo);

        public static WhatsappErro TokenGlobalAusente() => new(WhatsappCodigos.TokenGlobalAusente,
            "O token global do WhatsApp da Zippy nao esta configurado no servidor.",
            "Configure a variavel de ambiente WhatsApp__AccessToken (token permanente da conta Meta da Zippy) no ambiente da API e reinicie. " +
            "Nao e um campo da tela.",
            null);

        public static WhatsappErro ChaveCifragemAusente() => new(WhatsappCodigos.ChaveCifragemAusente,
            "O servidor nao tem a chave para guardar o token com seguranca.",
            "Configure a variavel de ambiente Atendimento__ChaveToken (qualquer frase longa e secreta) no ambiente da API e reinicie. " +
            "Nao e um campo da tela.",
            null);

        public static WhatsappErro MetaIndisponivel() => new(WhatsappCodigos.MetaIndisponivel,
            "Nao foi possivel falar com a Meta agora.",
            "Tente de novo em instantes. Se continuar, confira o status da Meta em metastatus.com.",
            null);

        public static WhatsappErro BaseUrlAusente() => new(WhatsappCodigos.WebhookNaoRegistrado,
            "A API nao sabe o proprio endereco publico (https) para dizer a Meta onde entregar as mensagens.",
            "Configure a variavel de ambiente App__BaseUrl (ex.: https://api.zippygo.com.br) no ambiente da API e use \"Registrar de novo\". " +
            "A Meta so aceita enderecos https.",
            null);

        public static WhatsappErro WebhookRecusado(string? detalheMeta) => new(WhatsappCodigos.WebhookNaoRegistrado,
            "A Meta nao aceitou o endereco do webhook" + (string.IsNullOrWhiteSpace(detalheMeta) ? "." : ": " + detalheMeta),
            ComoRegistrarManual,
            "webhook", LinkPainelApp);

        public static WhatsappErro WebhookNaoRegistrado() => new(WhatsappCodigos.WebhookNaoRegistrado,
            "O webhook ainda nao foi registrado na Meta.",
            ComoRegistrarManual,
            "webhook", LinkPainelApp);

        public static WhatsappErro SemRecebimento(int minutos) => new(WhatsappCodigos.SemRecebimento,
            $"Nenhuma mensagem chegou ha mais de {minutos} minutos desde que o numero foi conectado.",
            "Mande um \"oi\" de outro telefone para este numero. Se nada chegar: em developers.facebook.com > seu app > WhatsApp > Configuracao > Webhook, " +
            "confirme que a URL e o token de verificacao sao os mostrados aqui e que o campo \"messages\" esta assinado; confirme tambem que o app esta " +
            "no modo Ativo (em modo de desenvolvimento a Meta so entrega mensagens de quem tem funcao no app). Depois use \"Registrar de novo\".",
            "webhook", LinkPainelApp);

        public static WhatsappErro Generico(int? http, string? detalheMeta) => new(WhatsappCodigos.MetaErro,
            $"A Meta recusou a operacao{(http.HasValue ? $" (HTTP {http})" : string.Empty)}: {(string.IsNullOrWhiteSpace(detalheMeta) ? "sem detalhe" : detalheMeta)}",
            "Confira os dados informados e tente de novo. Se persistir, fale com a Zippy informando este codigo.",
            null);

        private const string ComoRegistrarManual =
            "Registre na mao: developers.facebook.com > seu app > WhatsApp > Configuracao > Webhook > Editar. Cole a URL de retorno e o token " +
            "de verificacao mostrados aqui, salve e assine o campo \"messages\". Quando a Meta validar, o estado passa para registrado.";

        /// <summary>Traduz o erro devolvido pela Graph API (codigo e subcodigo) para o erro explicado, conforme a chamada que falhou.</summary>
        public static WhatsappErro DaMeta(ChamadaMeta chamada, int? http, int? codigo, int? subcodigo, string? mensagemMeta)
        {
            switch (chamada)
            {
                case ChamadaMeta.Numero:
                    return (codigo, subcodigo) switch
                    {
                        (190, _) => TokenInvalido(),
                        (10, _) or (200, _) => SemPermissao(),
                        (100, _) => IdNaoEncontrado(),
                        _ => Generico(http, mensagemMeta)
                    };

                case ChamadaMeta.AssinaturaDoApp:
                    return codigo switch
                    {
                        // A Meta responde 190 ("Invalid OAuth access token - Signature mismatch") para segredo errado e 100 para app inexistente.
                        190 or 100 or 101 => AppSecretInvalido(),
                        // 2200: a Meta chamou a URL para validar e nao recebeu o desafio de volta.
                        2200 or 2201 => WebhookRecusado(mensagemMeta),
                        10 or 200 => SemPermissao(),
                        _ => Generico(http, mensagemMeta)
                    };

                default:
                    return codigo switch
                    {
                        190 => TokenInvalido(),
                        10 or 200 => SemPermissao(),
                        100 or 33 => WabaNaoEncontrada(),
                        2200 or 2201 => WebhookRecusado(mensagemMeta),
                        _ => Generico(http, mensagemMeta)
                    };
            }
        }

        /// <summary>
        /// Resultado de uma consulta ao numero (que pode vir de um verificador antigo, sem codigo) como erro explicado.
        /// </summary>
        public static WhatsappErro DaVerificacao(VerificacaoCanal verificacao)
        {
            switch (verificacao.Codigo)
            {
                case WhatsappCodigos.TokenInvalido: return TokenInvalido();
                case WhatsappCodigos.SemPermissao: return SemPermissao();
                case WhatsappCodigos.IdNaoEncontrado: return IdNaoEncontrado();
                case WhatsappCodigos.TokenGlobalAusente: return TokenGlobalAusente();
                case WhatsappCodigos.MetaIndisponivel: return MetaIndisponivel();
                case WhatsappCodigos.MetaErro: return new WhatsappErro(WhatsappCodigos.MetaErro, verificacao.Mensagem,
                    "Confira o Phone Number ID e o token e tente de novo.", verificacao.Campo ?? "phoneNumberId");
            }

            // Sem codigo (verificacao de outra origem): a mensagem decide, como antes.
            var campo = verificacao.Campo ?? (verificacao.Mensagem.Contains("token", StringComparison.OrdinalIgnoreCase) ? "token" : "phoneNumberId");
            if (campo == "token") return TokenInvalido() with { Mensagem = verificacao.Mensagem };
            return new WhatsappErro(WhatsappCodigos.IdNaoEncontrado, verificacao.Mensagem, IdNaoEncontrado().ComoResolver, campo, LinkPainelApp);
        }
    }
}
