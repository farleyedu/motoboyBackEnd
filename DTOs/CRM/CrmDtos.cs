using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using APIBack.Infrastructure;

namespace APIBack.DTOs.CRM
{
    public class CrmValoresDto
    {
        public decimal? SaaSMensalidade { get; set; }
        public decimal? Implantacao { get; set; }
        public decimal? MarketingMensalidade { get; set; }
        public decimal? MarketingValorFixo { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CrmPropostaDto : CrmValoresDto
    {
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? EnviadaEm { get; set; }
    }

    public class CrmReservaVinculadaDto
    {
        public long Id { get; set; }
        [JsonConverter(typeof(FlexibleDateOnlyJsonConverter))]
        public DateOnly DataReserva { get; set; }
        public string HoraInicio { get; set; } = string.Empty;
        public string? HoraFim { get; set; }
        public string? Local { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CrmHistoricoItemDto
    {
        public long Id { get; set; }
        public int UsuarioId { get; set; }
        public string Acao { get; set; } = string.Empty;
        public string? Detalhe { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class CrmOportunidadeResumoDto
    {
        public Guid Id { get; set; }
        public string NomeEmpresa { get; set; } = string.Empty;
        public string NomeContato { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Cnpj { get; set; }
        public string Tipo { get; set; } = string.Empty;
        /// <summary>Usuario da equipe Zippy responsavel (super admin). Nulo = ninguem definido.</summary>
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public string Coluna { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? Substatus { get; set; }
        public string? ProximaAcao { get; set; }
        public CrmValoresDto Estimativa { get; set; } = new();
        public CrmPropostaDto? Proposta { get; set; }
        public long? ReuniaoReservaId { get; set; }
        public long? RespostaReservaId { get; set; }
        public long? PagamentoReservaId { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DtRespostaPrevista { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DtPagamentoPrevista { get; set; }
        public CrmReservaVinculadaDto? Reuniao { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
    }

    public class CrmOportunidadeDetalheDto : CrmOportunidadeResumoDto
    {
        public Guid? ContratoId { get; set; }
        public List<CrmHistoricoItemDto> Historico { get; set; } = new();
    }

    public class CriarCrmOportunidadeRequest
    {
        public string NomeEmpresa { get; set; } = string.Empty;
        public string NomeContato { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Cnpj { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public int? ResponsavelUsuarioId { get; set; }
        public string? Substatus { get; set; }
        public CrmValoresDto? Estimativa { get; set; }
    }

    public class AtualizarCrmOportunidadeRequest
    {
        public string? NomeEmpresa { get; set; }
        public string? NomeContato { get; set; }
        public string? Telefone { get; set; }
        public string? Email { get; set; }
        public string? Cnpj { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public string? Substatus { get; set; }
        public string? ProximaAcao { get; set; }
        public string? Status { get; set; }
        public CrmPropostaDto? Proposta { get; set; }
    }

    public class CrmAgendamentoReuniaoRequest
    {
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? Data { get; set; }
        public string? HoraInicio { get; set; }
        public string? HoraFim { get; set; }
        public string? Local { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CrmDatasQuaseFechadoRequest
    {
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DtRespostaPrevista { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DtPagamentoPrevista { get; set; }
        public string? Observacoes { get; set; }
    }

    public class MoverCrmOportunidadeRequest
    {
        public string ColunaDestino { get; set; } = string.Empty;
        public CrmAgendamentoReuniaoRequest? Reuniao { get; set; }
        public CrmPropostaDto? Proposta { get; set; }
        public CrmDatasQuaseFechadoRequest? QuaseFechado { get; set; }
    }

    public class FecharCrmOportunidadeRequest
    {
        public string StatusInicial { get; set; } = string.Empty;
        public int DiaVencimento { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataInicioCobranca { get; set; }
        public bool ImplantacaoJaPaga { get; set; }
        public string? NomeEmpresaOverride { get; set; }
        public string? NomeContatoOverride { get; set; }
        public string? TelefoneOverride { get; set; }
        public string? EmailOverride { get; set; }
        public string? CnpjOverride { get; set; }
        public string? EstabelecimentoTipoSlug { get; set; }
        public string? Observacoes { get; set; }
        /// <summary>Empresa ja cadastrada que o operador confirmou (de GET .../candidatos-associacao). Nulo = criar empresa nova.</summary>
        public Guid? EmpresaId { get; set; }
        /// <summary>Loja da empresa confirmada. Nulo = criar loja nova. So vale junto com EmpresaId.</summary>
        public Guid? EstabelecimentoId { get; set; }
        /// <summary>
        /// Fechou na conversa: verbal | whatsapp | assinado. Com aceite valido, fecha sem proposta formal e de qualquer coluna.
        /// Opcional: sem ele, o fechamento continua exigindo proposta e coluna quase/aguardando.
        /// </summary>
        public string? Aceite { get; set; }
        /// <summary>Valor mensal combinado no aceite, usado quando nao ha proposta registrada.</summary>
        public decimal? ValorMensal { get; set; }
    }

    public class CrmCandidatoLojaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public bool Ativo { get; set; }
        public string? Situacao { get; set; }
    }

    public class CrmCandidatoAssociacaoDto
    {
        public Guid EmpresaId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Cnpj { get; set; }
        /// <summary>cnpj (mesma empresa: o fechamento so aceita ligar a esta) | nome_parecido (so sugestao).</summary>
        public string Motivo { get; set; } = string.Empty;
        public List<CrmCandidatoLojaDto> Lojas { get; set; } = new();
    }

    public class CrmResponsavelDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
    }

    public class FecharCrmOportunidadeResponse
    {
        public Guid ContratoId { get; set; }
    }

    public class CrmImplantacaoDto
    {
        public Guid Id { get; set; }
        public Guid ContratoId { get; set; }
        public string Status { get; set; } = string.Empty;
        public decimal ValorTotal { get; set; }
        public decimal ValorPago { get; set; }
        public bool Paga { get; set; }
        public string? Observacoes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class CrmLancamentoDto
    {
        public Guid Id { get; set; }
        public Guid ContratoId { get; set; }
        public Guid EmpresaId { get; set; }
        public string? NomeEmpresa { get; set; }
        public string? TipoContrato { get; set; }
        public string TipoLancamento { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        public string? Competencia { get; set; }
        [JsonConverter(typeof(FlexibleDateOnlyJsonConverter))]
        public DateOnly DataVencimento { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataPagamento { get; set; }
        public decimal ValorTotal { get; set; }
        public decimal ValorPago { get; set; }
        /// <summary>
        /// pendente | parcial_manual | registrado_manual | cancelado. Lancamento do CRM e anotacao da equipe, nunca "pago":
        /// pagamento de verdade vem da assinatura (comercial_cobranca).
        /// </summary>
        public string Status { get; set; } = string.Empty;
        public string Origem { get; set; } = CrmLancamentoOrigem.Manual;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public static class CrmLancamentoOrigem
    {
        public const string Manual = "manual";
    }

    public class CrmContratoResumoDto
    {
        public Guid Id { get; set; }
        public Guid EmpresaId { get; set; }
        public string NomeEmpresa { get; set; } = string.Empty;
        public Guid EstabelecimentoId { get; set; }
        public string NomeEstabelecimento { get; set; } = string.Empty;
        public Guid? OportunidadeId { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public decimal MensalidadeSaas { get; set; }
        public decimal MensalidadeMarketing { get; set; }
        public decimal MarketingValorFixo { get; set; }
        public decimal ImplantacaoValor { get; set; }
        public bool ImplantacaoPaga { get; set; }
        public int DiaVencimento { get; set; }
        [JsonConverter(typeof(FlexibleDateOnlyJsonConverter))]
        public DateOnly DataInicioCobranca { get; set; }
        public string? Observacoes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public decimal MensalidadeTotal => MensalidadeSaas + MensalidadeMarketing + MarketingValorFixo;
    }

    public class CrmContratoDetalheDto : CrmContratoResumoDto
    {
        public CrmImplantacaoDto? Implantacao { get; set; }
        public List<CrmLancamentoDto> Lancamentos { get; set; } = new();
        public List<CrmHistoricoItemDto> Historico { get; set; } = new();
    }

    public class AtualizarCrmContratoRequest
    {
        public string? Status { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public int? DiaVencimento { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataInicioCobranca { get; set; }
        public decimal? MensalidadeSaas { get; set; }
        public decimal? MensalidadeMarketing { get; set; }
        public decimal? MarketingValorFixo { get; set; }
        public string? Observacoes { get; set; }
    }

    public class RegistrarCrmLancamentoRequest
    {
        public Guid? LancamentoId { get; set; }
        public Guid? ContratoId { get; set; }
        public string? TipoLancamento { get; set; }
        public string? Descricao { get; set; }
        public string? Competencia { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataVencimento { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataPagamento { get; set; }
        public decimal ValorTotal { get; set; }
        public decimal ValorPago { get; set; }
    }

    public class CrmFinanceiroResumoDto
    {
        /// <summary>Pago na assinatura (comercial_cobranca paga) no mes.</summary>
        public decimal RecebidoMes { get; set; }
        /// <summary>Cobrancas da assinatura em aberto com vencimento no mes.</summary>
        public decimal PendenteMes { get; set; }
        /// <summary>Registrado a mao nos lancamentos do CRM no mes. Nao e pagamento confirmado.</summary>
        public decimal RegistradoManualMes { get; set; }
        public decimal PendenteManualMes { get; set; }
        public decimal FarleyMes { get; set; }
        public decimal SocioMes { get; set; }
    }

    public class CrmDivisaoDetalheDto
    {
        public string TipoLancamento { get; set; } = string.Empty;
        public decimal TotalPago { get; set; }
        public decimal FarleyValor { get; set; }
        public decimal SocioValor { get; set; }
    }

    public class CrmDivisaoFinanceiraDto
    {
        public string ReferenciaMes { get; set; } = string.Empty;
        public decimal TotalPago { get; set; }
        public decimal FarleyTotal { get; set; }
        public decimal SocioTotal { get; set; }
        public List<CrmDivisaoDetalheDto> Detalhes { get; set; } = new();
    }

    // ---- Leads (POST /api/crm/leads): contrato independente da tela legada

    public class CrmLeadProximoPassoRequest
    {
        public string? Tipo { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? Data { get; set; }
        public string? Nota { get; set; }
    }

    public class CriarCrmLeadRequest
    {
        public string? Nome { get; set; }
        /// <summary>WhatsApp em E.164 (+5534991480112).</summary>
        public string? Whatsapp { get; set; }
        public string? Origem { get; set; }
        public string? Decisor { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public CrmLeadProximoPassoRequest? ProximoPasso { get; set; }
        public string? Cnpj { get; set; }
        public string? Email { get; set; }
        /// <summary>"criar_novo": a pessoa conferiu os candidatos parecidos e confirma que este lead e outro.</summary>
        public string? DecisaoDuplicidade { get; set; }
        public string? MotivoDuplicidade { get; set; }
        /// <summary>Ids dos candidatos que a pessoa viu. Candidato novo desde a ultima conferencia volta como 409.</summary>
        public List<Guid>? CandidatosConfirmados { get; set; }
    }

    public class CrmLeadCandidatoDto
    {
        public Guid Id { get; set; }
        /// <summary>lead | loja</summary>
        public string Tipo { get; set; } = string.Empty;
        public string Nome { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public int? ResponsavelUsuarioId { get; set; }
        public string? ResponsavelNome { get; set; }
        public string Situacao { get; set; } = string.Empty;
        /// <summary>telefone_igual | nome_igual | nome_parecido</summary>
        public List<string> Motivos { get; set; } = new();
        public double Similaridade { get; set; }
    }

    public class CrmLeadEnriquecimentoDto
    {
        public bool Solicitado { get; set; }
        public bool Disponivel { get; set; }
        public string? Motivo { get; set; }
        public string? RazaoSocial { get; set; }
        public string? NomeFantasia { get; set; }
        public string? Situacao { get; set; }
    }

    public class CrmLeadCriadoDto
    {
        public Guid OportunidadeId { get; set; }
        public Guid AcompanhamentoId { get; set; }
        public APIBack.Acompanhamento.Relacionamento.UsuarioEquipeDto? Responsavel { get; set; }
        public APIBack.Acompanhamento.Relacionamento.ProximoPassoDto? ProximoPasso { get; set; }
        public string WhatsappE164 { get; set; } = string.Empty;
        public CrmLeadEnriquecimentoDto Enriquecimento { get; set; } = new();
        /// <summary>A mesma chave de idempotencia ja tinha criado este lead: nada novo foi gravado.</summary>
        public bool Repetida { get; set; }
        public bool DuplicidadeDecidida { get; set; }
        public CrmOportunidadeResumoDto? Oportunidade { get; set; }
    }
}
