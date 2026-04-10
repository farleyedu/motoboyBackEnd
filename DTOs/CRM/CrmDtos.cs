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
        public Guid Id { get; set; }
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
        public string Responsavel { get; set; } = string.Empty;
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
        public string Responsavel { get; set; } = string.Empty;
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
        public string? Responsavel { get; set; }
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
        public string? EstabelecimentoTipoSlug { get; set; }
        public string? Observacoes { get; set; }
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
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
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
        public string Responsavel { get; set; } = string.Empty;
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
        public int? DiaVencimento { get; set; }
        [JsonConverter(typeof(FlexibleNullableDateOnlyJsonConverter))]
        public DateOnly? DataInicioCobranca { get; set; }
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
        public decimal RecebidoMes { get; set; }
        public decimal PendenteMes { get; set; }
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
}
