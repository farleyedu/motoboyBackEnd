using System;
using System.Collections.Generic;

namespace APIBack.Model.CRM
{
    public class CrmOportunidadeRow
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
        public string? EstimativaJson { get; set; }
        public string? PropostaJson { get; set; }
        public DateTimeOffset? PropostaEnviadaEm { get; set; }
        public long? ReuniaoReservaId { get; set; }
        public string? ReuniaoLocal { get; set; }
        public long? RespostaReservaId { get; set; }
        public long? PagamentoReservaId { get; set; }
        public DateTime? DtRespostaPrevista { get; set; }
        public DateTime? DtPagamentoPrevista { get; set; }
        public DateTime? ProxAcaoData { get; set; }
        public decimal? ValorMensalEst { get; set; }
        public decimal? ValorImplEst { get; set; }
        public Guid? IdEmpresa { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? DeletedAt { get; set; }
        public DateTimeOffset? ClosedAt { get; set; }
    }

    public class CrmHistoricoRow
    {
        public long Id { get; set; }
        public Guid? OportunidadeId { get; set; }
        public Guid? ContratoId { get; set; }
        public int UsuarioId { get; set; }
        public string Acao { get; set; } = string.Empty;
        public string? Detalhe { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    public class CrmReservaRow
    {
        public long Id { get; set; }
        public DateTime DataReserva { get; set; }
        public TimeSpan HoraInicio { get; set; }
        public TimeSpan? HoraFim { get; set; }
        public string? Observacoes { get; set; }
    }

    public class CrmContratoRow
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
        public DateTime DataInicioCobranca { get; set; }
        public string? Observacoes { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class CrmImplantacaoRow
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

    public class CrmLancamentoRow
    {
        public Guid Id { get; set; }
        public Guid ContratoId { get; set; }
        public Guid EmpresaId { get; set; }
        public string? NomeEmpresa { get; set; }
        public string? TipoContrato { get; set; }
        public string TipoLancamento { get; set; } = string.Empty;
        public string Descricao { get; set; } = string.Empty;
        public DateTime? Competencia { get; set; }
        public DateTime DataVencimento { get; set; }
        public DateTime? DataPagamento { get; set; }
        public decimal ValorTotal { get; set; }
        public decimal ValorPago { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    public class CrmFinanceiroResumoRow
    {
        public decimal RecebidoMes { get; set; }
        public decimal PendenteMes { get; set; }
    }

    public class CrmDivisaoRow
    {
        public string TipoLancamento { get; set; } = string.Empty;
        public decimal TotalPago { get; set; }
        public decimal FarleyValor { get; set; }
        public decimal SocioValor { get; set; }
    }

    public class CrmCloseOpportunityCommand
    {
        public Guid OportunidadeId { get; set; }
        public int UsuarioId { get; set; }
        public string Responsavel { get; set; } = string.Empty;
        public string NomeEmpresa { get; set; } = string.Empty;
        public string NomeContato { get; set; } = string.Empty;
        public string Telefone { get; set; } = string.Empty;
        public string? Email { get; set; }
        public string? Cnpj { get; set; }
        public string Tipo { get; set; } = string.Empty;
        public string StatusInicial { get; set; } = string.Empty;
        public int DiaVencimento { get; set; }
        public DateTime DataInicioCobranca { get; set; }
        public bool ImplantacaoJaPaga { get; set; }
        public string? Observacoes { get; set; }
        public string? EstabelecimentoTipoSlug { get; set; }
        public decimal MensalidadeSaas { get; set; }
        public decimal MensalidadeMarketing { get; set; }
        public decimal MarketingValorFixo { get; set; }
        public decimal ImplantacaoValor { get; set; }
        public string? PropostaJson { get; set; }
    }

    public class CrmCloseOpportunityResult
    {
        public Guid ContratoId { get; set; }
        public Guid EmpresaId { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public List<Guid> LancamentoIds { get; set; } = new();
        public Guid? ImplantacaoId { get; set; }
    }
}
