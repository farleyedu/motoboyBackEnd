using System;
using System.Collections.Generic;

namespace APIBack.GestaoMensagens
{
    public static class CanaisMensagem
    {
        public const string Painel = "painel";
        public const string Pessoal = "pessoal";
        public const string PrefixoNumero = "numero:";
    }

    public static class ViaMensagem
    {
        public const string Painel = "painel";
        public const string Numero = "numero";
        public const string Pessoal = "pessoal";
    }

    public static class EstadoMensagem
    {
        public const string Enviando = "enviando";
        public const string Enviado = "enviado";
        public const string Entregue = "entregue";
        public const string Lido = "lido";
        public const string Falhou = "falhou";
        /// <summary>WhatsApp pessoal sem Meta: o link foi entregue a quem envia; nada saiu ainda.</summary>
        public const string AguardandoConfirmacao = "aguardando_confirmacao";
        /// <summary>Uma pessoa confirmou que enviou por fora. Nao e entrega da Meta e nao ha recibo.</summary>
        public const string EnviadoPorFora = "enviado_por_fora";
        public const string Recebida = "recebida";
    }

    public static class DirecaoMensagem
    {
        public const string Saida = "saida";
        public const string Entrada = "entrada";
    }

    public static class FinalidadeMensagem
    {
        public const string Utilidade = "utilidade";
        public const string Marketing = "marketing";
    }

    public static class CodigosGestaoMensagem
    {
        public const string ParametrosInvalidos = "PARAMETROS_INVALIDOS";
        public const string AlvoNaoEncontrado = "ALVO_NAO_ENCONTRADO";
        public const string ContatoNaoEncontrado = "CONTATO_NAO_ENCONTRADO";
        public const string ContatoAmbiguo = "CONTATO_AMBIGUO";
        public const string ContatoNaoConfirmado = "CONTATO_NAO_CONFIRMADO";
        public const string CanalNaoEncontrado = "CANAL_NAO_ENCONTRADO";
        public const string CanalIndisponivel = "CANAL_INDISPONIVEL";
        public const string CanalInvalido = "CANAL_INVALIDO";
        public const string SemPermissaoNoCanal = "SEM_PERMISSAO_NO_CANAL";
        public const string JanelaFechadaSemModelo = "JANELA_FECHADA_SEM_MODELO";
        public const string ModeloNaoAprovado = "MODELO_NAO_APROVADO";
        public const string VariaveisInvalidas = "VARIAVEIS_INVALIDAS";
        public const string ConsentimentoObrigatorio = "CONSENTIMENTO_OBRIGATORIO";
        public const string OptOut = "OPT_OUT";
        public const string ChaveReutilizada = "CHAVE_REUTILIZADA";
        public const string EnvioEmAndamento = "ENVIO_EM_ANDAMENTO";
        public const string EstadoInvalido = "ESTADO_INVALIDO";
        public const string MensagemNaoEncontrada = "MENSAGEM_NAO_ENCONTRADA";
        public const string FalhaNaMeta = "FALHA_NA_META";
    }

    /// <summary>Erro de regra com status HTTP (403/404/409/422) e o proximo passo para quem enviava.</summary>
    public sealed class GestaoMensagensException : Exception
    {
        public GestaoMensagensException(int status, string codigo, string mensagem, string? proximoPasso = null, object? detalhes = null)
            : base(mensagem)
        {
            Status = status;
            Codigo = codigo;
            ProximoPasso = proximoPasso;
            Detalhes = detalhes;
        }

        public int Status { get; }
        public string Codigo { get; }
        public string? ProximoPasso { get; }
        public object? Detalhes { get; }
    }

    // ---- entrada ---------------------------------------------------------------------------------------------------------

    public sealed record ModeloEnvio(Guid? Id, string? Nome, IReadOnlyList<string>? Variaveis, string? SufixoBotaoUrl);

    /// <param name="Canal">painel | numero:{canalId} | pessoal</param>
    /// <param name="ContatoId">Loja: id do usuario da loja. Oportunidade: omitido ou "principal" (telefone do lead).</param>
    /// <param name="Finalidade">utilidade (padrao) | marketing. Marketing exige modelo, consentimento e ausencia de opt-out.</param>
    public sealed record EnviarMensagemRequest(
        Guid? LojaId, Guid? OportunidadeId, string? ContatoId, string? Canal, string? Texto, ModeloEnvio? Modelo,
        string? ChaveIdempotencia, string? Finalidade, bool ConsentimentoMarketing, long? ChamadoId);

    // ---- dominio ---------------------------------------------------------------------------------------------------------

    public sealed record AlvoMensagem(string Tipo, Guid? LojaId, Guid? OportunidadeId, string Nome)
    {
        /// <summary>Id aceito pelo relacionamento (loja ou oportunidade) para o marco de contato.</summary>
        public Guid ReferenciaRelacionamento => LojaId ?? OportunidadeId!.Value;
    }

    public sealed record ContatoAlvo(string Id, string Nome, string? TelefoneE164, bool Confirmado);

    public sealed record CanalGestao(
        Guid GestaoCanalId, Guid CanalId, string PhoneNumberId, string NumeroE164, string Nome, string Finalidade, int? DonoUsuarioId, bool Ativo,
        bool ConectadoMeta, string? EstadoMeta, IReadOnlyList<int> Remetentes)
    {
        public bool Pessoal => Finalidade == "pessoal";
        public bool PodeEnviar(int usuarioId) => DonoUsuarioId == usuarioId || Remetentes.Contains(usuarioId);
    }

    public sealed record ModeloAprovado(Guid Id, string Nome, string Idioma, string Categoria, string? Corpo, int QtdVariaveis, bool BotaoUrlVariavel);

    public sealed class MensagemRegistro
    {
        public Guid Id { get; set; }
        public string Direcao { get; set; } = DirecaoMensagem.Saida;
        public string Via { get; set; } = ViaMensagem.Numero;
        public Guid? CanalId { get; set; }
        public string? AlvoTipo { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public Guid? OportunidadeId { get; set; }
        public string? ContatoRef { get; set; }
        public string? TelefoneE164 { get; set; }
        public string? TelefoneChave { get; set; }
        public int? AutorUsuarioId { get; set; }
        public string TipoConteudo { get; set; } = "texto";
        public string? Texto { get; set; }
        public string? ModeloNome { get; set; }
        public string? ModeloIdioma { get; set; }
        public string? ModeloCategoria { get; set; }
        public string? VariaveisJson { get; set; }
        public string Finalidade { get; set; } = FinalidadeMensagem.Utilidade;
        public string Estado { get; set; } = EstadoMensagem.Enviando;
        public string? ErroCodigo { get; set; }
        public string? ErroDetalhe { get; set; }
        public string? ReferenciaExterna { get; set; }
        public string? ChaveIdempotencia { get; set; }
        public long? ChamadoId { get; set; }
        public int? ConfirmadoPor { get; set; }
        public DateTimeOffset? ConfirmadoEm { get; set; }
        public DateTimeOffset? EnviadoEm { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
    }

    // ---- saida -----------------------------------------------------------------------------------------------------------

    public sealed record CanalGestaoDto(
        string Id, Guid? CanalId, string Nome, string? Numero, string Finalidade, int? DonoUsuarioId, bool ConectadoMeta, string? EstadoMeta,
        IReadOnlyList<int> Remetentes, bool VoceEnvia);

    public sealed record ModeloOpcaoDto(Guid Id, string Nome, string Idioma, string Categoria, int Variaveis, bool BotaoUrlVariavel);

    /// <param name="Registro">completo = historico e recibos da Meta; interno = chamado; manual = enviado por fora, so confirmacao humana.</param>
    public sealed record OpcaoCanalDto(
        string Canal, string Nome, string Finalidade, bool Disponivel, string Registro, bool? JanelaAberta, DateTimeOffset? JanelaAteUtc,
        bool ExigeModelo, IReadOnlyList<ModeloOpcaoDto> Modelos, string? Motivo, string? ProximoPasso, IReadOnlyList<string> Alternativas);

    public sealed record OpcoesMensagemDto(AlvoMensagem Alvo, ContatoAlvo Contato, IReadOnlyList<ContatoAlvo> Contatos, IReadOnlyList<OpcaoCanalDto> Canais);

    public sealed record MensagemDto(
        Guid Id, string Direcao, string Via, string Estado, string? Canal, string? Texto, string? Modelo, string? Finalidade, string? ReferenciaExterna,
        string? ErroCodigo, string? ErroDetalhe, long? ChamadoId, string? LinkWhatsapp, bool Repetida, string? Aviso, DateTimeOffset CriadoEm);
}
