namespace APIBack.Atendimento.ConexaoMeta
{
    /// <summary>Codigos estaveis do fluxo "Conectar com a Meta" (o painel decide o texto e a acao por eles).</summary>
    public static class ConexaoMetaCodigos
    {
        public const string Indisponivel = "WA_META_CONEXAO_INDISPONIVEL";
        public const string OrigemInvalida = "WA_META_ORIGEM_INVALIDA";
        public const string TentativaInvalida = "WA_META_TENTATIVA_INVALIDA";
        public const string TentativaExpirada = "WA_META_TENTATIVA_EXPIRADA";
        public const string TentativaEmUso = "WA_META_TENTATIVA_EM_USO";
        public const string FluxoCancelado = "WA_META_FLUXO_CANCELADO";
        public const string CodigoInvalido = "WA_META_CODIGO_INVALIDO";
        public const string AppDiferente = "WA_META_APP_DIFERENTE";
        public const string AutorizacaoIncompleta = "WA_META_AUTORIZACAO_INCOMPLETA";
        public const string TokenRevogado = "WA_META_TOKEN_REVOGADO";
        public const string WabaNaoAutorizada = "WA_META_WABA_NAO_AUTORIZADA";
        public const string NumeroNaoAutorizado = "WA_META_NUMERO_NAO_AUTORIZADO";
        public const string SemNumero = "WA_META_SEM_NUMERO";
        public const string PinNecessario = "WA_META_PIN_NECESSARIO";
        public const string RegistroLimite = "WA_META_REGISTRO_LIMITE";
        public const string NumeroNaoVerificado = "WA_META_NUMERO_NAO_VERIFICADO";
        public const string CoexistenciaIndisponivel = "WA_META_COEXISTENCIA_INDISPONIVEL";
        public const string ConexaoExistente = "WA_META_CONEXAO_EXISTENTE";
    }

    public static class ConexaoMetaErros
    {
        public const string LinkWhatsappManager = "https://business.facebook.com/wa/manage/phone-numbers";
        public const string LinkIntegracoes = "https://business.facebook.com/settings/integrations";

        public static WhatsappException Excecao(WhatsappErro erro, int http) => new(erro, http);

        public static WhatsappErro Indisponivel() => new(ConexaoMetaCodigos.Indisponivel,
            "A conexao pela Meta ainda nao esta disponivel neste servidor.",
            "Use a conexao manual (opcao avancada) ou fale com a Zippy. A configuracao do app ZippyGo esta pendente no servidor.", null);

        public static WhatsappErro OrigemInvalida() => new(ConexaoMetaCodigos.OrigemInvalida,
            "Esta conexao precisa ser feita pelo painel oficial do Zippy.",
            "Abra o painel do Zippy pelo endereco de sempre e tente de novo.", null);

        public static WhatsappErro TentativaInvalida() => new(ConexaoMetaCodigos.TentativaInvalida,
            "Esta conexao nao confere com a que foi iniciada aqui.",
            "Clique em \"Conectar com a Meta\" de novo nesta mesma tela.", null);

        public static WhatsappErro TentativaExpirada() => new(ConexaoMetaCodigos.TentativaExpirada,
            "O prazo para concluir esta conexao acabou.",
            "Clique em \"Conectar com a Meta\" de novo. O que voce ja criou na Meta (conta e numero) continua la e pode ser escolhido de novo.", null);

        public static WhatsappErro TentativaEmUso() => new(ConexaoMetaCodigos.TentativaEmUso,
            "Esta conexao ja foi concluida ou esta sendo processada em outra aba.",
            "Aguarde alguns segundos e atualize a tela para ver o andamento. Nao e preciso autorizar de novo.", null);

        public static WhatsappErro CodigoInvalido() => new(ConexaoMetaCodigos.CodigoInvalido,
            "A Meta nao aceitou a autorizacao (o codigo expira em 30 segundos ou ja foi usado).",
            "Clique em \"Conectar com a Meta\" de novo e conclua sem fechar a janela. Se repetir, confira se o dominio do painel esta liberado no app ZippyGo.", null);

        public static WhatsappErro AppDiferente() => new(ConexaoMetaCodigos.AppDiferente,
            "A autorizacao recebida nao e do app ZippyGo.",
            "Conecte de novo pelo botao \"Conectar com a Meta\" deste painel.", null);

        public static WhatsappErro AutorizacaoIncompleta(string faltando) => new(ConexaoMetaCodigos.AutorizacaoIncompleta,
            $"A autorizacao nao concedeu o que o Zippy precisa ({faltando}).",
            "Conecte de novo e, na tela da Meta, mantenha marcadas todas as permissoes e a conta do WhatsApp Business deste restaurante.",
            null, LinkIntegracoes);

        public static WhatsappErro TokenRevogado() => new(ConexaoMetaCodigos.TokenRevogado,
            "O acesso do Zippy a este WhatsApp foi removido ou expirou na Meta.",
            "Use \"Reconectar\": a Meta pede a autorizacao de novo e o numero continua com as mesmas conversas e configuracoes.",
            "token", LinkIntegracoes);

        public static WhatsappErro WabaNaoAutorizada() => new(ConexaoMetaCodigos.WabaNaoAutorizada,
            "A conta do WhatsApp Business informada nao esta entre as que voce autorizou.",
            "Conecte de novo e selecione, na tela da Meta, a conta do WhatsApp Business deste restaurante.", "wabaId");

        public static WhatsappErro NumeroNaoAutorizado() => new(ConexaoMetaCodigos.NumeroNaoAutorizado,
            "Este numero nao esta na conta do WhatsApp Business autorizada.",
            "Escolha um dos numeros listados ou adicione o numero na conta pelo WhatsApp Manager e atualize.", "phoneNumberId", LinkWhatsappManager);

        public static WhatsappErro SemNumero() => new(ConexaoMetaCodigos.SemNumero,
            "A conta do WhatsApp Business foi autorizada, mas ainda nao tem numero.",
            "Adicione e verifique um numero no WhatsApp Manager e volte aqui para escolher. Nao e preciso autorizar de novo nas proximas 24 horas.",
            "phoneNumberId", LinkWhatsappManager);

        public static WhatsappErro PinNecessario() => new(ConexaoMetaCodigos.PinNecessario,
            "Este numero ja tem confirmacao em duas etapas e a Meta pediu o PIN para registra-lo.",
            "Informe o PIN de 6 digitos da confirmacao em duas etapas (WhatsApp Manager > numero > Configuracoes > Confirmacao em duas etapas). " +
            "Se nao souber, redefina o PIN la e informe o novo.", "pin", LinkWhatsappManager);

        public static WhatsappErro RegistroLimite() => new(ConexaoMetaCodigos.RegistroLimite,
            "Muitas tentativas de registrar este numero em pouco tempo.",
            "A Meta limita os registros do numero. Aguarde algumas horas e use \"Verificar\" de novo; o Zippy nao vai repetir sozinho.", null);

        public static WhatsappErro NumeroNaoVerificado() => new(ConexaoMetaCodigos.NumeroNaoVerificado,
            "O numero ainda nao foi verificado na Meta.",
            "No WhatsApp Manager, conclua a verificacao do numero (codigo por SMS ou ligacao) e depois use \"Verificar\" aqui.",
            "phoneNumberId", LinkWhatsappManager);

        public static WhatsappErro CoexistenciaIndisponivel() => new(ConexaoMetaCodigos.CoexistenciaIndisponivel,
            "Manter o numero no app WhatsApp Business ao mesmo tempo ainda nao esta disponivel no Zippy.",
            "Voce pode conectar outro numero, ou conectar este numero so na plataforma (ele deixa de funcionar no app WhatsApp Business). " +
            "Nada e alterado sem a sua confirmacao na tela da Meta.", null);

        public static WhatsappErro ConexaoExistente(string origem) => new(ConexaoMetaCodigos.ConexaoExistente,
            origem == OrigemCanal.Zippy || origem == OrigemCanal.Central
                ? "Este numero ja esta conectado a esta loja pela Zippy."
                : "Este numero ja esta conectado a esta loja de outra forma.",
            "Para trocar a forma de conexao sem perder conversas, use \"Reconectar com a Meta\" neste numero.", "phoneNumberId");

        public static WhatsappErro LojaJaTemNumero() => new(WhatsappCodigos.CampoInvalido,
            "Esta loja ja tem outro numero conectado.",
            "Desconecte o numero atual antes de conectar outro, ou use \"Reconectar\" no numero atual.", "phoneNumberId");
    }
}
