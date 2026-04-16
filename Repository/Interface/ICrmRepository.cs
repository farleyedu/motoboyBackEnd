using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Model.CRM;

namespace APIBack.Repository.Interface
{
    public interface ICrmRepository
    {
        Task<IReadOnlyCollection<CrmOportunidadeRow>> ListarOportunidadesAsync(string? status, string? tipo, string? responsavel, string? busca);
        Task<CrmOportunidadeRow?> ObterOportunidadeAsync(Guid oportunidadeId);
        Task<Guid> CriarOportunidadeAsync(CrmOportunidadeRow oportunidade);
        Task AtualizarOportunidadeAsync(CrmOportunidadeRow oportunidade);
        Task AtualizarMovimentacaoOportunidadeAsync(Guid oportunidadeId, string coluna, string? status, string? substatus, string? propostaJson, DateTimeOffset? propostaEnviadaEm, long? reuniaoReservaId, string? reuniaoLocal, long? respostaReservaId, long? pagamentoReservaId, DateTime? dtRespostaPrevista, DateTime? dtPagamentoPrevista, DateTimeOffset? closedAt);
        Task<IReadOnlyCollection<CrmHistoricoRow>> ListarHistoricoOportunidadeAsync(Guid oportunidadeId);
        Task AdicionarHistoricoOportunidadeAsync(Guid oportunidadeId, int usuarioId, string acao, string? detalhe);
        Task<CrmReservaRow?> ObterReservaAsync(long reservaId);
        Task<Guid?> ObterContratoIdPorOportunidadeAsync(Guid oportunidadeId);

        Task<CrmCloseOpportunityResult> FecharOportunidadeAsync(CrmCloseOpportunityCommand command);
        Task<int> SincronizarContratosLegadosAsync();

        Task<IReadOnlyCollection<CrmContratoRow>> ListarContratosAsync(string? status);
        Task<CrmContratoRow?> ObterContratoAsync(Guid contratoId);
        Task AtualizarContratoAsync(Guid contratoId, string? status, string? responsavel, int? diaVencimento, DateTime? dataInicioCobranca, decimal? mensalidadeSaas, decimal? mensalidadeMarketing, decimal? marketingValorFixo, string? observacoes);
        Task<IReadOnlyCollection<CrmHistoricoRow>> ListarHistoricoContratoAsync(Guid contratoId);
        Task AdicionarHistoricoContratoAsync(Guid contratoId, int usuarioId, string acao, string? detalhe);
        Task<CrmImplantacaoRow?> ObterImplantacaoAsync(Guid contratoId);
        Task<IReadOnlyCollection<CrmLancamentoRow>> ListarLancamentosContratoAsync(Guid contratoId);
        Task<IReadOnlyCollection<CrmLancamentoRow>> ListarLancamentosFinanceiroAsync(DateTime referenciaMes, bool apenasEmAberto);
        Task<CrmLancamentoRow?> ObterLancamentoAsync(Guid lancamentoId);
        Task<Guid> CriarLancamentoAsync(CrmLancamentoRow lancamento);
        Task AtualizarLancamentoAsync(Guid lancamentoId, decimal valorPago, DateTime? dataPagamento, string status, DateTime? dataVencimento, decimal? valorTotal);
        Task AtualizarImplantacaoPagamentoAsync(Guid contratoId, decimal valorPago, bool paga);
        Task AtualizarContratoImplantacaoPagaAsync(Guid contratoId, bool implantacaoPaga);

        Task<CrmFinanceiroResumoRow> ObterResumoFinanceiroAsync(DateTime referenciaMes);
        Task<IReadOnlyCollection<CrmDivisaoRow>> ObterDivisaoFinanceiraAsync(DateTime referenciaMes);
        Task<IReadOnlyCollection<string>> ListarMesesComLancamentosAsync(int ultimos);
    }
}
