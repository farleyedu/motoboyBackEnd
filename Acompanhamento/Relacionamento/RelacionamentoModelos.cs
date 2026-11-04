using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using APIBack.Infrastructure;

namespace APIBack.Acompanhamento.Relacionamento
{
    public static class TiposProximoPasso
    {
        public const string Mensagem = "mensagem", Ligacao = "ligacao", Reuniao = "reuniao", Proposta = "proposta", Visita = "visita", Retorno = "retorno";
        public static readonly string[] Todos = { Mensagem, Ligacao, Reuniao, Proposta, Visita, Retorno };
    }

    public static class TiposMarcacao
    {
        public const string Atencao = "atencao", PrecisaDeAjuda = "precisa_ajuda", RiscoDeSaida = "risco_de_saida", NaoIncomodar = "nao_incomodar";
        public static readonly string[] Todos = { Atencao, PrecisaDeAjuda, RiscoDeSaida, NaoIncomodar };
    }

    public static class EstadosPasso
    {
        public const string Aberto = "aberto", Feito = "feito", SemSucesso = "sem_sucesso", Reagendado = "reagendado", Cancelado = "cancelado", Substituido = "substituido";
    }

    /// <summary>Resultado de um toque ao concluir. Quem marca e uma pessoa: "sem_resposta" conta a tentativa, mas grava sem_sucesso.</summary>
    public static class ResultadosPasso
    {
        public const string ContatoRealizado = "contato_realizado", SemResposta = "sem_resposta", Interessado = "interessado",
            NegociacaoPendente = "negociacao_pendente", ProblemaEncontrado = "problema_encontrado", AguardandoTerceiro = "aguardando_terceiro";
        public static readonly string[] Todos = { ContatoRealizado, SemResposta, Interessado, NegociacaoPendente, ProblemaEncontrado, AguardandoTerceiro };
        public static string Estado(string resultado) => resultado == SemResposta ? EstadosPasso.SemSucesso : EstadosPasso.Feito;
    }

    public static class EscoposAtividade
    {
        public const string Hoje = "hoje", Vencidas = "vencidas", Proximas = "proximas", Feitas = "feitas";
        public static readonly string[] Todos = { Hoje, Vencidas, Proximas, Feitas };
    }

    public static class CanaisRetencao
    {
        public static readonly string[] Todos = { "whatsapp", "ligacao", "presencial", "email" };
    }

    public static class ResultadosRetencao
    {
        public const string Retida = "retida", Saiu = "saiu", EmNegociacao = "em_negociacao", Pausada = "pausada";
        public static readonly string[] Todos = { Retida, Saiu, EmNegociacao, Pausada };
    }

    public static class TiposOferta
    {
        public const string Nenhuma = "nenhuma", Pausa = "pausa", AjudaOperacional = "ajuda_operacional", Desconto = "desconto", Estorno = "estorno", Outra = "outra";
        public static readonly string[] Todos = { Nenhuma, Pausa, AjudaOperacional, Desconto, Estorno, Outra };
        /// <summary>Mexem em dinheiro: so valem com aprovacao humana registrada. O sistema nunca executa desconto nem estorno.</summary>
        public static bool ExigeAprovacao(string tipo) => tipo is Desconto or Estorno;
    }

    public static class SaidasQuando
    {
        public const string Aviso30Dias = "aviso_30_dias", FimDoCiclo = "fim_do_ciclo", Agora = "agora";
        public static readonly string[] Todos = { Aviso30Dias, FimDoCiclo, Agora };
    }

    public static class DirecoesContato
    {
        public const string Enviada = "enviada", Recebida = "recebida";
    }

    public sealed class RelacionamentoException : Exception
    {
        public RelacionamentoException(int status, string code, string message, object? details = null) : base(message)
        {
            Status = status;
            Code = code;
            Details = details;
        }

        public int Status { get; }
        public string Code { get; }
        public object? Details { get; }
    }

    // ---- saidas

    public sealed record UsuarioEquipeDto(int Id, string Nome);

    public sealed record ProximoPassoDto(Guid Id, string Tipo, DateOnly Data, string? Nota, int? CriadoPor, DateTimeOffset CriadoEm, bool Vencido);

    /// <summary>"Aguardando resposta" so existe depois de uma mensagem efetivamente enviada e sem recebimento posterior.</summary>
    public sealed record EsperaRespostaDto(bool Aguardando, DateTimeOffset? Desde, DateTimeOffset? UltimaEnviada, DateTimeOffset? UltimaRecebida);

    public sealed record MarcacaoDto(string Tipo, string Motivo, int PorUsuarioId, DateTimeOffset Desde, string? Categoria = null, string? AcaoEsperada = null);

    public sealed record VisaoAnteriorDto(int UsuarioId, DateTimeOffset Ate);

    public sealed record EventoRelacionamentoDto(long Id, string Tipo, int? UsuarioId, DateTimeOffset Em, JsonElement? Detalhe);

    public sealed class RelacionamentoDto
    {
        public Guid AcompanhamentoId { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Fase { get; set; } = string.Empty;
        public UsuarioEquipeDto? Responsavel { get; set; }
        public long Versao { get; set; }
        public ProximoPassoDto? ProximoPasso { get; set; }
        public EsperaRespostaDto Espera { get; set; } = new(false, null, null, null);
        public MarcacaoDto? Marcacao { get; set; }
        public VisaoAnteriorDto? VisaoAnterior { get; set; }
        public List<EventoRelacionamentoDto> Eventos { get; set; } = new();
        public bool Repetida { get; set; }
    }

    public sealed record MembroEquipeDto(int Id, string Nome, int Lojas, int PassosVencidos, int PassosHoje, int AguardandoResposta);

    public sealed class LojaEquipeDto
    {
        public Guid AcompanhamentoId { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Fase { get; set; } = string.Empty;
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public long Versao { get; set; }
        public ProximoPassoDto? ProximoPasso { get; set; }
        public DateTimeOffset? AguardandoRespostaDesde { get; set; }
        public string? Marcacao { get; set; }
        /// <summary>Quem passou a loja ainda a enxerga ate esta data: a loja aparece para ele como visao transitoria.</summary>
        public int? VisaoTransitoriaUsuarioId { get; set; }
        public DateTimeOffset? VisaoTransitoriaAte { get; set; }
    }

    /// <summary>Vencido continua aqui ate alguem resolver (concluir, reagendar ou cancelar).</summary>
    public sealed record LembreteProximoPassoDto(Guid ProximoPassoId, Guid AcompanhamentoId, Guid? EstabelecimentoId, string Nome, string Tipo, DateOnly Data,
        int? ResponsavelUsuarioId, bool Vencido, string? Nota = null, DateTimeOffset? CriadoEm = null, UsuarioEquipeDto? Responsavel = null);

    /// <summary>GET /api/acompanhamento/atividades. <c>VersaoRelacionamento</c> e o valor de versaoEsperada ao agir sobre a atividade.</summary>
    public sealed record AtividadeDto(Guid Id, Guid AcompanhamentoId, Guid? OportunidadeId, Guid? EstabelecimentoId, string Nome, string Fase, string Tipo, DateOnly Data,
        string? Nota, string Estado, string? Resultado, string? ResultadoNota, DateTimeOffset? RealizadoEm, UsuarioEquipeDto? Responsavel, DateTimeOffset CriadoEm,
        bool Vencido, long VersaoRelacionamento);

    public sealed record AtividadesDto(string Escopo, DateOnly Hoje, IReadOnlyList<AtividadeDto> Itens);

    public sealed record AvisoRelacionamentoDto(long Id, Guid AcompanhamentoId, string Tipo, string Mensagem, int? DeUsuarioId, DateTimeOffset CriadoEm, bool Lido);

    public sealed class EquipeDto
    {
        public List<MembroEquipeDto> Membros { get; set; } = new();
        public int SemResponsavel { get; set; }
        public List<LojaEquipeDto> Lojas { get; set; } = new();
        /// <summary>Proximos passos de hoje ou vencidos: a fila do "Hoje" le daqui, ligada ao proximo passo (sem duplicar nota).</summary>
        public List<LembreteProximoPassoDto> Lembretes { get; set; } = new();
        public List<AvisoRelacionamentoDto> Avisos { get; set; } = new();
    }

    public sealed class PassarItemDto
    {
        public Guid LojaId { get; set; }
        public Guid? AcompanhamentoId { get; set; }
        /// <summary>passada | sem_mudanca | erro</summary>
        public string Status { get; set; } = string.Empty;
        public string? Codigo { get; set; }
        public string? Mensagem { get; set; }
        public long? Versao { get; set; }
    }

    public sealed class PassarResultadoDto
    {
        public int Passadas { get; set; }
        public List<PassarItemDto> Itens { get; set; } = new();
    }

    public sealed class RetencaoDto
    {
        public Guid Id { get; set; }
        public Guid AcompanhamentoId { get; set; }
        public string Pedido { get; set; } = string.Empty;
        public DateTimeOffset PedidoEm { get; set; }
        public string Leitura { get; set; } = string.Empty;
        public OfertaRetencaoDto Oferta { get; set; } = new();
        public string Canal { get; set; } = string.Empty;
        public string Resultado { get; set; } = string.Empty;
        public string? SaidaQuando { get; set; }
        public DateOnly? SaidaEm { get; set; }
        public DateOnly? PausaAte { get; set; }
        public bool CobrancaEmAberto { get; set; }
        public string? Observacao { get; set; }
        public int? CriadoPor { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public bool Repetida { get; set; }
        /// <summary>Fase depois do registro: quem operava e saiu passa para Saindo na mesma transacao.</summary>
        public string? FaseAtual { get; set; }
        /// <summary>Sempre falso: registrar a oferta nao executa desconto, estorno nem cobranca.</summary>
        public bool ExecutouFinanceiro { get; set; }
    }

    public sealed class OfertaRetencaoDto
    {
        public string Tipo { get; set; } = TiposOferta.Nenhuma;
        public string? Descricao { get; set; }
        public decimal? Valor { get; set; }
        public bool Aprovada { get; set; }
        public int? AprovadaPor { get; set; }
    }

    // ---- entradas

    public sealed class PassarRequest
    {
        public List<Guid> LojaIds { get; set; } = new();
        public int ParaUsuarioId { get; set; }
        public string? Motivo { get; set; }
        /// <summary>Versao que o operador viu de cada loja; se mudou, a loja volta como VERSAO_MUDOU e nada e passado.</summary>
        public Dictionary<Guid, long>? VersoesEsperadas { get; set; }
    }

    public sealed class AssumirRequest
    {
        public long? VersaoEsperada { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class CobrarResponsavelRequest
    {
        public string? Mensagem { get; set; }
    }

    public sealed class DefinirProximoPassoRequest
    {
        public string? Tipo { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? Data { get; set; }
        public string? Nota { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class ProximoPassoNovoRequest
    {
        public string? Tipo { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? Data { get; set; }
        public string? Nota { get; set; }
    }

    public sealed class ConcluirPassoRequest
    {
        public string? Resultado { get; set; }
        public string? Nota { get; set; }
        /// <summary>Quando foi feito. Omitido = agora; no futuro = 422.</summary>
        public DateTimeOffset? RealizadoEm { get; set; }
        /// <summary>"E depois?": abre o seguinte na mesma transacao, ligado por origem_passo_id.</summary>
        public ProximoPassoNovoRequest? Proximo { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class ReagendarPassoRequest
    {
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? Data { get; set; }
        public string? Motivo { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class CancelarPassoRequest
    {
        public string? Motivo { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class MarcarRequest
    {
        public string? Tipo { get; set; }
        public string? Motivo { get; set; }
        /// <summary>Texto livre, ate 80 caracteres. Opcional.</summary>
        public string? Categoria { get; set; }
        /// <summary>O que se espera que aconteca, ate 300 caracteres. Opcional.</summary>
        public string? AcaoEsperada { get; set; }
        /// <summary>DELETE: a resolucao escrita (vale tambem no campo motivo).</summary>
        public string? Resolucao { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class OfertaRetencaoRequest
    {
        public string? Tipo { get; set; }
        public string? Descricao { get; set; }
        public decimal? Valor { get; set; }
        /// <summary>Um humano aprovou esta oferta. Obrigatorio para desconto e estorno.</summary>
        public bool Aprovada { get; set; }
    }

    public sealed class CancelamentoRequest
    {
        public string? ChaveIdempotencia { get; set; }
        public string? Pedido { get; set; }
        public DateTimeOffset? PedidoEm { get; set; }
        public string? Leitura { get; set; }
        public OfertaRetencaoRequest? Oferta { get; set; }
        public string? Canal { get; set; }
        public string? Resultado { get; set; }
        public string? SaidaQuando { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? SaidaEm { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? PausaAte { get; set; }
        public string? Observacao { get; set; }
        public long? VersaoEsperada { get; set; }
    }

    public sealed class AvisosLidosRequest
    {
        public List<long> Ids { get; set; } = new();
    }
}
