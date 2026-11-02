using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using APIBack.Infrastructure;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>
    /// Corpo de POST /api/acompanhamento/lojas/{id}/passagens/{para}. So os campos que a passagem pede sao lidos
    /// (GET .../passagens/{para} diz quais). A chave de idempotencia tambem pode vir no cabecalho Idempotency-Key.
    /// </summary>
    public sealed class DadosPassagem
    {
        public string? ChaveIdempotencia { get; set; }
        /// <summary>Fase que a tela mostrava. Diferente da atual = 409 FASE_MUDOU (alguem passou antes).</summary>
        public string? De { get; set; }

        public string? QuemDecide { get; set; }
        public int? PedidosPorDia { get; set; }
        public string? DorPrincipal { get; set; }
        public string? Motivo { get; set; }
        /// <summary>verbal | whatsapp | assinado.</summary>
        public string? Aceite { get; set; }
        /// <summary>Chave do catalogo (partida, rota, turbo, frota, personalizado).</summary>
        public string? Plano { get; set; }
        /// <summary>CNPJ ou CPF, com ou sem mascara.</summary>
        public string? Documento { get; set; }
        /// <summary>pix | cartao | boleto.</summary>
        public string? FormaPagamento { get; set; }
        /// <summary>Prorrogacao do teste: 7 ou 15.</summary>
        public int? Dias { get; set; }
        /// <summary>Saida: aviso_30_dias | fim_do_ciclo | agora.</summary>
        public string? Quando { get; set; }
        public string? OQueFez { get; set; }
        public bool? LojaAvisada { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataVolta { get; set; }
        /// <summary>Perder: nao | 30 | 90.</summary>
        public string? TentarDeNovo { get; set; }
        /// <summary>Valor mensal combinado (proposta ou fechamento na conversa).</summary>
        public decimal? ValorMensal { get; set; }
        public decimal? ValorImplantacao { get; set; }
        public string? Observacao { get; set; }

        /// <summary>Fechamento: empresa ja cadastrada que o operador confirmou (GET /api/crm/oportunidades/{id}/candidatos-associacao).</summary>
        public Guid? EmpresaId { get; set; }
        /// <summary>Fechamento: loja da empresa confirmada. So vale junto com EmpresaId.</summary>
        public Guid? EstabelecimentoId { get; set; }
        public int? DiaVencimento { get; set; }
        public string? EstabelecimentoTipoSlug { get; set; }
    }

    /// <summary>O que o servidor sabe da loja para decidir a passagem.</summary>
    public sealed record LojaFatos
    {
        public string Fase { get; init; } = FasesAcompanhamento.Conversa;
        public bool TemOportunidade { get; init; }
        public bool TemLoja { get; init; }
        public string? Situacao { get; init; }
        public string? Documento { get; init; }
        public string? QuemDecide { get; init; }
        public int? PedidosPorDia { get; init; }
        public string? DorPrincipal { get; init; }
        /// <summary>Itens obrigatorios do checklist (prontidao) ainda pendentes. Nulo = nao deu para conferir.</summary>
        public int? PendenciasBloqueantes { get; init; }
        /// <summary>Ja passou pelo teste de 15 dias (ou operava antes do acompanhamento existir).</summary>
        public bool TesteConcluido { get; init; }
        public int ProrrogacoesNoTeste { get; init; }
        public bool CobrancaEmAberto { get; init; }
        public bool TemProposta { get; init; }
    }

    public sealed class ContextoPassagem
    {
        public ContextoPassagem(LojaFatos loja, DadosPassagem dados, DateTimeOffset agora)
        {
            Loja = loja;
            Dados = dados;
            Agora = agora;
        }

        public LojaFatos Loja { get; }
        public DadosPassagem Dados { get; }
        public DateTimeOffset Agora { get; }
    }

    /// <summary>Estado gravado do acompanhamento de uma loja (ou oportunidade, antes de existir loja).</summary>
    public sealed class AcompanhamentoEstado
    {
        public Guid Id { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? EstabelecimentoId { get; set; }
        public Guid? EmpresaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string Fase { get; set; } = FasesAcompanhamento.Conversa;
        public DateTimeOffset FaseDesde { get; set; }
        public string Faixa { get; set; } = FaixasAcompanhamento.Bem;
        public long Versao { get; set; }
        public string? SituacaoLoja { get; set; }
        public string? Documento { get; set; }
        public string? QuemDecide { get; set; }
        public int? PedidosPorDia { get; set; }
        public string? DorPrincipal { get; set; }
        public decimal? ValorMensal { get; set; }
        public DateTimeOffset? TesteConcluidoEm { get; set; }
        public int ProrrogacoesNoTeste { get; set; }
        public bool TesteQuandoPronta { get; set; }
        public DateOnly? PausaAte { get; set; }
        public DateOnly? RetomarEm { get; set; }
        public string? SaidaQuando { get; set; }
        public DateOnly? SaidaEm { get; set; }
        public string? FormaPagamento { get; set; }
        public DateOnly? CobrancaIniciaEm { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
        public DateTimeOffset AtualizadoEm { get; set; }
    }

    /// <summary>O que uma passagem aprovada grava. Montado pelo <see cref="PassagemMotor"/>, aplicado pelo store numa transacao.</summary>
    public sealed class PassagemAplicacao
    {
        public PassagemDef Definicao { get; init; } = null!;
        public string De { get; init; } = string.Empty;
        public string FaseResultante { get; init; } = string.Empty;
        /// <summary>feita | pendente_aprovacao (segunda prorrogacao do teste).</summary>
        public string Status { get; init; } = StatusPassagem.Feita;
        public DadosPassagem Dados { get; init; } = new();
        public string Chave { get; init; } = string.Empty;
        public int? UsuarioId { get; init; }
        public string AutorTipo { get; init; } = AutoresPassagem.Usuario;
        public string? Motivo { get; init; }
        public DateTimeOffset Agora { get; init; }

        public bool TesteQuandoPronta { get; init; }
        public int? DiasProrrogacao { get; init; }
        public DateOnly? PausaAte { get; init; }
        public DateOnly? RetomarEm { get; init; }
        public DateOnly? SaidaEm { get; init; }
        public DateOnly? CobrancaIniciaEm { get; init; }
        public EfeitoPassagem Efeito => Definicao.Efeito;
    }

    public static class StatusPassagem
    {
        public const string Feita = "feita";
        public const string PendenteAprovacao = "pendente_aprovacao";
    }

    // ---- contratos de resposta

    public sealed record RequisitoDto(string Chave, string Descricao);

    public sealed record PassagemDefinicaoDto(
        string De, string Para, string Nome, string Quem, IReadOnlyList<RequisitoDto> Requisitos, string OQueAcontece, string Volta);

    public sealed record RecusaDto(string De, string Para, string Motivo, string Caminho, IReadOnlyList<string> Sequencia);

    /// <summary>GET /api/acompanhamento/passagens: as duas tabelas do item 2.</summary>
    public sealed record PassagensCatalogoDto(
        IReadOnlyList<string> Fases, IReadOnlyList<string> FasesNoQuadro, IReadOnlyList<PassagemDefinicaoDto> Passagens, IReadOnlyList<RecusaDto> Recusas);

    public sealed record RequisitoEstadoDto(string Chave, string Descricao, bool Pronto, string? Pendencia);

    /// <summary>GET /api/acompanhamento/lojas/{id}/passagens/{para}: a passagem e o estado de cada requisito para esta loja.</summary>
    public sealed record AvaliacaoPassagemDto(
        Guid AcompanhamentoId, string FaseAtual, string Para, bool Existe, bool PodeExecutar,
        PassagemDefinicaoDto? Passagem, IReadOnlyList<RequisitoEstadoDto> Requisitos, RecusaDto? Recusa, long Versao);

    public sealed record PassagemRegistroDto(
        Guid Id, string De, string Para, string FaseResultante, string Nome, string Status,
        int? AutorUsuarioId, string AutorTipo, string? Motivo, string ChaveIdempotencia, DateTimeOffset CriadaEm);

    /// <summary>Uma linha do historico: mudanca de fase ou de faixa, com quem, quando e por que.</summary>
    public sealed record HistoricoDto(
        long Id, string Tipo, string? De, string Para, DateTimeOffset Em, int? AutorUsuarioId, string AutorTipo, string? Motivo, Guid? PassagemId);

    /// <summary>Resposta do POST: a fase atual depois da passagem e o historico. Repetida = mesma chave, nada foi feito de novo.</summary>
    public sealed record PassagemResultadoDto(
        Guid AcompanhamentoId, Guid? OportunidadeId, Guid? EstabelecimentoId, string FaseAtual, DateTimeOffset FaseDesde, long Versao,
        bool Repetida, PassagemRegistroDto Passagem, IReadOnlyList<HistoricoDto> Historico);

    /// <summary>Corpo de "details" do 422: motivo, caminho possivel e requisitos pendentes.</summary>
    public sealed record RecusaDetalhesDto(
        string De, string Para, string Motivo, string Caminho, IReadOnlyList<string> Sequencia, IReadOnlyList<RequisitoEstadoDto> RequisitosPendentes);
}
