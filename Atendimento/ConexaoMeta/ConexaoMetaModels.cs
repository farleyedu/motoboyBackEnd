using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace APIBack.Atendimento.ConexaoMeta
{
    /// <summary>
    /// Configuracao do Embedded Signup (secao WhatsApp:EmbeddedSignup). Nada aqui e inventado: sem App ID, Configuration ID ou App Secret
    /// o fluxo fica indisponivel e a tela explica o que falta. O App Secret nunca sai do servidor.
    /// </summary>
    public sealed class ConfigEmbeddedSignup
    {
        /// <summary>Permissoes sem as quais o Zippy nao opera o numero (App Review da Meta).</summary>
        public static readonly IReadOnlyList<string> PermissoesExigidas = new[] { "whatsapp_business_management", "whatsapp_business_messaging" };

        public bool Habilitado { get; init; }
        public string? AppId { get; init; }
        public string? ConfigurationId { get; init; }
        public string? AppSecret { get; init; }
        /// <summary>Versao do SDK/Graph para o FB.init do navegador (a Meta pede a mais recente).</summary>
        public string SdkVersion { get; init; } = "v23.0";
        /// <summary>
        /// App Review das permissoes: "aprovada" libera qualquer restaurante; qualquer outro valor = so contas com funcao no app ZippyGo
        /// conseguem concluir (a tela avisa que a producao ainda nao esta liberada).
        /// </summary>
        public bool PermissoesAprovadas { get; init; }
        public bool CoexistenciaHabilitada { get; init; }
        /// <summary>Origens (https://admin...) de onde o painel abre o fluxo; vazio = nao confere o cabecalho Origin.</summary>
        public IReadOnlyList<string> OrigensPermitidas { get; init; } = Array.Empty<string>();
        /// <summary>Portfolio da Zippy (dono do app). WABAs dele nunca perdem a inscricao do app ao desconectar uma loja.</summary>
        public string? BusinessIdIntegrador { get; init; }
        /// <summary>WABAs da propria Zippy (WhatsApp:WabaIds): idem, nunca perdem a inscricao.</summary>
        public IReadOnlyList<string> WabasDaZippy { get; init; } = Array.Empty<string>();
        public int MinutosParaAutorizar { get; init; } = 15;
        public int HorasParaAtivar { get; init; } = 24;

        public bool Configurado => Habilitado && Digitos(AppId) && Digitos(ConfigurationId) && Valor(AppSecret);

        /// <summary>O que falta para o fluxo ficar disponivel (pendencias de implantacao, nunca preenchidas por padrao).</summary>
        public IReadOnlyList<string> Pendencias()
        {
            var faltas = new List<string>();
            if (!Habilitado) faltas.Add("WhatsApp__EmbeddedSignup__Habilitado nao esta ligado no servidor.");
            if (!Digitos(AppId)) faltas.Add("WhatsApp__EmbeddedSignup__AppId (ID do app ZippyGo) nao configurado.");
            if (!Digitos(ConfigurationId)) faltas.Add("WhatsApp__EmbeddedSignup__ConfigurationId (Facebook Login for Business, configuracao do WhatsApp Embedded Signup v4) nao configurado.");
            if (!Valor(AppSecret)) faltas.Add("App Secret do ZippyGo nao configurado (WhatsApp__EmbeddedSignup__AppSecret ou Automation__Meta__AppSecret).");
            if (!PermissoesAprovadas) faltas.Add("App Review de whatsapp_business_management e whatsapp_business_messaging ainda nao aprovado: so contas com funcao no app ZippyGo concluem a conexao.");
            if (OrigensPermitidas.Count == 0) faltas.Add("WhatsApp__EmbeddedSignup__OrigensPermitidas vazio: configure o dominio https do painel (o mesmo cadastrado em Allowed domains do Login for Business).");
            return faltas;
        }

        public static ConfigEmbeddedSignup Ler(IConfiguration c)
        {
            var s = c.GetSection("WhatsApp:EmbeddedSignup");
            return new ConfigEmbeddedSignup
            {
                Habilitado = bool.TryParse(s["Habilitado"], out var h) && h,
                AppId = Limpo(s["AppId"]),
                ConfigurationId = Limpo(s["ConfigurationId"]),
                AppSecret = Limpo(s["AppSecret"]) ?? Limpo(c["Automation:Meta:AppSecret"]),
                SdkVersion = Limpo(s["SdkVersion"]) ?? Limpo(c["WhatsApp:GraphApiVersion"]) ?? "v23.0",
                PermissoesAprovadas = string.Equals(Limpo(s["AppReview"]), "aprovada", StringComparison.OrdinalIgnoreCase),
                CoexistenciaHabilitada = bool.TryParse(s["CoexistenciaHabilitada"], out var co) && co,
                OrigensPermitidas = Lista(s["OrigensPermitidas"]).Select(o => o.TrimEnd('/')).ToList(),
                BusinessIdIntegrador = Limpo(s["BusinessIdIntegrador"]),
                WabasDaZippy = Lista(c["WhatsApp:WabaIds"]).Concat(Lista(c["WhatsApp:WabaId"])).Where(v => v.All(char.IsAsciiDigit)).Distinct().ToList(),
                MinutosParaAutorizar = int.TryParse(s["MinutosParaAutorizar"], out var m) && m is >= 5 and <= 60 ? m : 15,
                HorasParaAtivar = int.TryParse(s["HorasParaAtivar"], out var hr) && hr is >= 1 and <= 72 ? hr : 24
            };
        }

        /// <summary>Valores de modelo do appsettings ("__SET_IN_ENV__") contam como ausentes.</summary>
        private static string? Limpo(string? v)
        {
            var t = v?.Trim();
            return string.IsNullOrEmpty(t) || t.StartsWith("__", StringComparison.Ordinal) ? null : t;
        }

        private static IEnumerable<string> Lista(string? v) =>
            (Limpo(v) ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim());

        private static bool Digitos(string? v) => !string.IsNullOrEmpty(v) && v.All(char.IsAsciiDigit);
        private static bool Valor(string? v) => !string.IsNullOrEmpty(v);
    }

    /// <summary>Eventos que o Embedded Signup devolve ao navegador (message type WA_EMBEDDED_SIGNUP).</summary>
    public static class EventoFluxo
    {
        public const string Finish = "FINISH";
        public const string FinishSoWaba = "FINISH_ONLY_WABA";
        public const string FinishCoexistencia = "FINISH_WHATSAPP_BUSINESS_APP_ONBOARDING";
        public const string FinishMigracaoObo = "FINISH_OBO_MIGRATION";
        public const string FinishSoAcessoApi = "FINISH_GRANT_ONLY_API_ACCESS";
        public const string Cancel = "CANCEL";
        public const string Error = "ERROR";

        public static bool Concluiu(string? e) => e is Finish or FinishSoWaba or FinishCoexistencia or FinishMigracaoObo or FinishSoAcessoApi;
        public static bool Valido(string? e) => Concluiu(e) || e is Cancel or Error;
    }

    public static class EstadoTentativa
    {
        public const string Iniciada = "iniciada";
        public const string Trocando = "trocando";
        /// <summary>Token obtido e validado; falta escolher/ativar o numero.</summary>
        public const string Autorizada = "autorizada";
        public const string Ativando = "ativando";
        public const string Concluida = "concluida";
        public const string Cancelada = "cancelada";
        public const string Expirada = "expirada";
        public const string Erro = "erro";
        public const string Substituida = "substituida";

        public static bool Final(string e) => e is Concluida or Cancelada or Expirada or Substituida;
    }

    public static class EstadoAutorizacao
    {
        public const string Ativa = "ativa";
        public const string Revogada = "revogada";
        public const string Expirada = "expirada";
        public const string Erro = "erro";
    }

    /// <summary>Situacao de um numero da WABA autorizada em relacao a esta loja.</summary>
    public static class SituacaoNumero
    {
        public const string Livre = "livre";
        public const string DestaLoja = "desta_loja";
        public const string OutraLoja = "outra_loja";
    }

    /// <summary>Estado da conexao como a tela mostra (um so, derivado das verificacoes reais).</summary>
    public static class EstadoConexao
    {
        public const string AguardandoAutorizacao = "aguardando_autorizacao";
        public const string Configurando = "configurando";
        public const string Conectado = "conectado";
        public const string AtencaoNecessaria = "atencao_necessaria";
        public const string Erro = "erro";
        public const string Revogado = "revogado";
        public const string Desconectado = "desconectado";
    }

    public static class EstadoEtapa
    {
        public const string Ok = "ok";
        public const string Pendente = "pendente";
        public const string Atencao = "atencao";
        public const string Erro = "erro";
        public const string NaoAplicavel = "nao_aplicavel";
    }

    /// <summary>Etapas verificaveis de uma conexao, na ordem em que acontecem.</summary>
    public static class EtapaConexao
    {
        public const string Autorizacao = "autorizacao";
        public const string Permissoes = "permissoes";
        public const string Waba = "waba";
        public const string Numero = "numero";
        public const string Registro = "registro";
        public const string Inscricao = "inscricao";
        public const string Webhook = "webhook";
        public const string Recebimento = "recebimento";
        public const string Coexistencia = "coexistencia";
    }

    /// <summary>Quem faz a acao pendente: a loja (na Meta ou no Zippy), a Zippy (configuracao/servidor) ou ninguem (aguardar a Meta).</summary>
    public static class ResponsavelPendencia
    {
        public const string Loja = "loja";
        public const string Zippy = "zippy";
        public const string Meta = "meta";
    }

    // ---- requisicoes ----------------------------------------------------------------------------------------------

    /// <param name="CanalId">Reconectar este canal (mantem id, conversas e configuracoes). Nulo = conexao nova.</param>
    /// <param name="Coexistencia">A loja quer manter o numero no app WhatsApp Business (so se a Zippy liberou a coexistencia).</param>
    public sealed record IniciarConexaoMetaRequest(Guid? CanalId = null, bool Coexistencia = false);

    /// <summary>
    /// O que o navegador recebeu do Embedded Signup. Codigo e IDs sao so pistas: o servidor troca o codigo e confere tudo na Meta.
    /// O codigo vale 30 segundos: o painel manda assim que o FB.login devolve.
    /// </summary>
    public sealed record ConcluirConexaoMetaRequest(
        Guid TentativaId, string? State, string? Evento, string? Code = null, string? WabaId = null, string? PhoneNumberId = null,
        string? BusinessId = null, string? EtapaCancelada = null, string? ErroMensagem = null, string? ErroCodigo = null, string? SessaoMeta = null);

    /// <param name="Pin">PIN de confirmacao em duas etapas do numero, so quando a Meta exigir (numero que ja tinha PIN).</param>
    public sealed record AtivarNumeroMetaRequest(Guid TentativaId, string? PhoneNumberId, string? Pin = null);

    /// <param name="Reparar">Alem de conferir, refaz as etapas seguras que faltam (inscricao do app; registro do numero se permitido).</param>
    public sealed record RevalidarCanalRequest(bool Reparar = false, string? Pin = null);

    // ---- respostas ------------------------------------------------------------------------------------------------

    /// <summary>Configuracao publica para o painel abrir o Embedded Signup. Nunca contem segredo.</summary>
    /// <param name="Modo">producao (App Review aprovado), teste (so contas com funcao no app) ou indisponivel.</param>
    public sealed record ConfiguracaoConexaoMetaView(
        bool Disponivel, string Modo, string? AppId, string? ConfigurationId, string SdkVersion, bool CoexistenciaDisponivel,
        IReadOnlyList<string> PermissoesExigidas, IReadOnlyList<string> Pendencias, bool PodeConectar, bool ConexaoManualDisponivel = true);

    /// <summary>
    /// Devolvido ao iniciar: o painel passa o <paramref name="State"/> de volta no concluir. <paramref name="Extras"/> vai no FB.login
    /// (extras: setup e featureType de coexistencia, quando pedida).
    /// </summary>
    public sealed record TentativaIniciadaView(Guid TentativaId, string State, DateTime ExpiraEm, string Modo, IReadOnlyDictionary<string, object> Extras,
        string AppId, string ConfigurationId, string SdkVersion);

    public sealed record NumeroAutorizadoView(
        string PhoneNumberId, string? Telefone, string? Nome, string? EstadoMeta, string? VerificacaoCodigo, string Situacao, bool Selecionavel,
        bool? NoAppBusiness, string? Motivo);

    /// <param name="Acao">O que o painel oferece: tentar_de_novo, escolher_numero, informar_pin, reconectar, conectar_manual, aguardar, contatar_suporte.</param>
    public sealed record EtapaConexaoView(string Codigo, string Estado, string Mensagem, string? ComoResolver = null, string? Responsavel = null,
        string? Acao = null, string? Link = null, string? CodigoErro = null);

    /// <summary>
    /// Andamento de uma tentativa: autorizacao concedida, numero registrado, webhook funcional e conexao operacional sao etapas separadas,
    /// e o <paramref name="Estado"/> geral so e "conectado" quando todas as verificaveis estao ok.
    /// </summary>
    public sealed record EstadoConexaoMetaView(
        Guid? TentativaId, string EstadoTentativa, string Estado, DateTime? ExpiraEm, string? Evento, bool Coexistencia,
        string? WabaId, string? BusinessId, IReadOnlyList<string> Permissoes, IReadOnlyList<NumeroAutorizadoView> Numeros,
        IReadOnlyList<EtapaConexaoView> Etapas, IReadOnlyList<string> Acoes, CanalView? Canal, string? ErroCodigo, string? ErroMensagem);

    /// <summary>Diagnostico de um canal (qualquer origem), so com verificacoes reais. Sem segredos.</summary>
    public sealed record DiagnosticoConexaoView(
        Guid CanalId, string Origem, string Estado, DateTime VerificadoEm, string? AppId, string? WabaId, string? PhoneNumberId,
        IReadOnlyList<EtapaConexaoView> Etapas, IReadOnlyList<string> Acoes, CanalView Canal);

    /// <param name="InscricaoRemovida">O app ZippyGo deixou de receber eventos da WABA (so quando nenhuma outra loja/numero usa a WABA).</param>
    public sealed record DesconexaoView(Guid CanalId, string Origem, bool HistoricoPreservado, bool InscricaoRemovida, string? MotivoInscricaoMantida,
        string Mensagem);

    /// <summary>Resumo da conexao no CanalView (o painel diferencia conexao ativa de autorizacao incompleta sem outra chamada).</summary>
    public sealed record CanalConexaoView(
        string? AutorizacaoEstado, bool Coexistencia, string RegistroEstado, string? EstadoMeta, DateTime? InscricaoWabaEm, DateTime? DesconectadoEm);
}
