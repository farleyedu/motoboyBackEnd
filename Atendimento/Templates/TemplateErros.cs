using System;

namespace APIBack.Atendimento.Templates
{
    public static class TemplateCodigos
    {
        public const string Duplicado = "WA_TPL_DUPLICADO";
        public const string NomeEmExclusao = "WA_TPL_NOME_EM_EXCLUSAO";
        public const string SemPermissaoGestao = "WA_TPL_SEM_PERMISSAO_GESTAO";
        public const string WabaNaoConfigurada = "WA_TPL_WABA_NAO_CONFIGURADA";
        public const string WabaIncorreta = "WA_TPL_WABA_INCORRETA";
        public const string ConteudoRecusado = "WA_TPL_CONTEUDO_RECUSADO";
        public const string Limite = "WA_TPL_LIMITE";
        public const string SemToken = "WA_TPL_SEM_TOKEN";
        public const string NumeroInativo = "WA_TPL_NUMERO_INATIVO";
        public const string EnvioIncerto = "WA_TPL_ENVIO_INCERTO";
        public const string NaoExcluivel = "WA_TPL_NAO_EXCLUIVEL";
    }

    /// <summary>
    /// Falhas da Meta nos modelos explicadas para a tela (codigo estavel + como resolver). Nunca levam token, app secret, WABA de
    /// outra loja ou o corpo cru da Meta: no maximo o error_user_msg, que a Meta escreve para o usuario final.
    /// </summary>
    public static class TemplateErros
    {
        public const string LinkWhatsappManager = "https://business.facebook.com/wa/manage/message-templates";

        public static WhatsappException Duplicado() => new(new WhatsappErro(TemplateCodigos.Duplicado,
            "Ja existe um modelo com este nome e idioma nesta conta do WhatsApp.",
            "Escolha outro nome tecnico (ex.: acrescente _v2). A Meta nao permite dois modelos com o mesmo nome e idioma.",
            "nome"), 409);

        public static WhatsappException NomeEmExclusao() => new(new WhatsappErro(TemplateCodigos.NomeEmExclusao,
            "Este nome foi usado por um modelo excluido ha pouco e ainda esta bloqueado pela Meta.",
            "A Meta bloqueia o nome de um modelo excluido por ate 30 dias. Use outro nome tecnico.",
            "nome"), 409);

        public static WhatsappException EmEnvio() => new(new WhatsappErro(TemplateCodigos.Duplicado,
            "Este modelo ja esta sendo enviado para a Meta.",
            "Aguarde o retorno e use Sincronizar para ver o status. Nao e preciso enviar de novo.",
            "nome"), 409);

        public static WhatsappException SemPermissaoGestao() => new(new WhatsappErro(TemplateCodigos.SemPermissaoGestao,
            "O token deste numero nao tem permissao para gerenciar modelos de mensagem.",
            "No Meta Business Suite (Usuarios do sistema), gere o token com a permissao whatsapp_business_management e atribua a conta do " +
            "WhatsApp Business com controle total. Numero da Zippy: fale com a Zippy.",
            "token", WhatsappErros.LinkUsuariosDoSistema), 422);

        public static WhatsappException WabaNaoConfigurada() => new(new WhatsappErro(TemplateCodigos.WabaNaoConfigurada,
            "A conta do WhatsApp Business da Zippy nao esta configurada no servidor.",
            "Configure a variavel de ambiente WhatsApp__WabaIds (IDs das contas WhatsApp Business da Zippy, separados por virgula) e reinicie. " +
            "Nao e um campo da tela.",
            null), 503);

        public static WhatsappException WabaIncorreta() => new(new WhatsappErro(TemplateCodigos.WabaIncorreta,
            "Nao foi possivel confirmar que este numero pertence a conta do WhatsApp Business conectada.",
            "Confira o ID da conta do WhatsApp Business (WABA) na conexao do numero: ele deve ser a conta onde o numero esta no WhatsApp Manager. " +
            "Corrija a conexao na aba de conexao.",
            "wabaId", WhatsappErros.LinkWhatsappManager), 422);

        public static WhatsappException SemToken(bool global) => global
            ? new WhatsappException(WhatsappErros.TokenGlobalAusente())
            : new WhatsappException(new WhatsappErro(TemplateCodigos.SemToken,
                "Este numero ainda nao tem token da Meta.",
                "Conecte o numero de novo na aba de conexao, informando o token do usuario do sistema.",
                "token", WhatsappErros.LinkUsuariosDoSistema), 422);

        public static WhatsappException NumeroInativo() => new(new WhatsappErro(TemplateCodigos.NumeroInativo,
            "Este numero esta desativado.",
            "Reative ou conecte o numero na aba de conexao antes de gerenciar modelos.",
            null), 422);

        public static WhatsappException Limite() => new(new WhatsappErro(TemplateCodigos.Limite,
            "Muitas solicitacoes a Meta em pouco tempo.",
            "Aguarde alguns minutos e tente de novo. A Meta limita quantos modelos podem ser criados e consultados por hora.",
            null), 429);

        public static WhatsappException NaoExcluivel(string motivo) => new(new WhatsappErro(TemplateCodigos.NaoExcluivel,
            motivo, "So modelos ja confirmados pela Meta podem ser excluidos por aqui.", null), 409);

        /// <summary>Traduz a falha da Graph API em erro explicado com o HTTP da tabela da tarefa (409/422/429/502/503).</summary>
        public static WhatsappException DaMeta(MetaFalha falha)
        {
            if (falha.SemResposta) return new WhatsappException(WhatsappErros.MetaIndisponivel(), 503);
            if (falha.Http == 429) return Limite();

            switch (falha.Codigo)
            {
                case 190:
                    return new WhatsappException(WhatsappErros.TokenInvalido(), 422);
                case 4 or 17 or 32 or 613 or 80008 or 130429:
                    return Limite();
                case 10 or 200 or 3 or 294:
                    return SemPermissaoGestao();
                case 1 or 2:
                    return new WhatsappException(WhatsappErros.MetaIndisponivel(), 503);
                case 100 when falha.Subcodigo is 2388024 or 2388025:
                    return Duplicado();
                case 100 when falha.Subcodigo is 2388023:
                    return NomeEmExclusao();
                case 100 when falha.Subcodigo == 33:
                    return WabaIncorreta();
            }

            if (falha.Http >= 500) return new WhatsappException(WhatsappErros.MetaIndisponivel(), 503);

            if (falha.Codigo == 100 || falha.Http is 400 or 422)
            {
                // Conteudo recusado na hora pela Meta: o texto dela (para o usuario final) orienta a correcao.
                var detalhe = Curto(falha.MensagemUsuario);
                return new WhatsappException(new WhatsappErro(TemplateCodigos.ConteudoRecusado,
                    "A Meta recusou o conteudo do modelo" + (detalhe == null ? "." : ": " + detalhe),
                    "Corrija o modelo conforme indicado e envie de novo. As regras estao no WhatsApp Manager > Modelos de mensagem.",
                    null, LinkWhatsappManager), 422);
            }

            return new WhatsappException(WhatsappErros.Generico(falha.Http, Curto(falha.MensagemUsuario)), 502);
        }

        /// <summary>Codigo estavel da falha, para gravar no modelo e na auditoria.</summary>
        public static string CodigoDe(MetaFalha falha) => DaMeta(falha).Erro.Codigo;

        private static string? Curto(string? texto)
        {
            var t = texto?.Trim();
            if (string.IsNullOrEmpty(t)) return null;
            return t.Length > 300 ? t[..300] : t;
        }
    }
}
