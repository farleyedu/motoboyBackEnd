using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.IdentidadeWhatsapp;
using APIBack.Repository.Interface;

namespace APIBack.AssistenteComercial.Audio
{
    public sealed record OportunidadeCrm(Guid Id, string Empresa, string? Contato, string Status, int? ResponsavelUsuarioId, string? ProximaAcao);

    /// <summary>
    /// Unica porta do assistente para o CRM. So existem estas operacoes: ler oportunidades do proprio vendedor, anotar historico e definir a
    /// proxima acao. Status, coluna, proposta, contrato, plano, desconto e lancamento nao tem caminho daqui.
    /// </summary>
    public interface IAudioCrm
    {
        /// <summary>Oportunidades ativas em que o usuario e o responsavel (a unica base que o match enxerga).</summary>
        Task<IReadOnlyList<OportunidadeParaMatch>> OportunidadesDoUsuarioAsync(int usuarioId, CancellationToken ct);
        Task<OportunidadeCrm?> ObterAsync(Guid oportunidadeId, CancellationToken ct);
        Task<IReadOnlyList<string>> HistoricoAsync(Guid oportunidadeId, int limite, CancellationToken ct);
        Task AdicionarNotaAsync(Guid oportunidadeId, int usuarioId, string acao, string detalhe, CancellationToken ct);
        Task DefinirProximaAcaoAsync(Guid oportunidadeId, int usuarioId, string valor, CancellationToken ct);
    }

    /// <summary>Quem pode usar o assistente: vinculo de WhatsApp verificado (05) e membro da equipe do CRM.</summary>
    public interface IVerificacaoFuncionario
    {
        Task<bool> VerificadoAsync(int usuarioId, CancellationToken ct);
        Task<bool> EquipeCrmAsync(int usuarioId, CancellationToken ct);
    }

    public sealed class VerificacaoFuncionario : IVerificacaoFuncionario
    {
        private readonly IIdentidadeWhatsappService _identidade;
        private readonly ICrmRepository _crm;

        public VerificacaoFuncionario(IIdentidadeWhatsappService identidade, ICrmRepository crm) { _identidade = identidade; _crm = crm; }

        public async Task<bool> VerificadoAsync(int usuarioId, CancellationToken ct) =>
            (await _identidade.ObterAsync(usuarioId)).Estado == EstadoVinculoWhatsapp.Verificado;

        public async Task<bool> EquipeCrmAsync(int usuarioId, CancellationToken ct) =>
            (await _crm.ListarEquipeZippyAsync()).Any(m => m.Id == usuarioId);
    }

    public sealed class AudioCrmRepositorio : IAudioCrm
    {
        private readonly ICrmRepository _crm;

        public AudioCrmRepositorio(ICrmRepository crm) => _crm = crm;

        public async Task<IReadOnlyList<OportunidadeParaMatch>> OportunidadesDoUsuarioAsync(int usuarioId, CancellationToken ct) =>
            (await _crm.ListarOportunidadesAsync("ativo", null, usuarioId, null))
            .Where(o => o.ResponsavelUsuarioId == usuarioId)
            .Select(o => new OportunidadeParaMatch(o.Id, o.NomeEmpresa, o.NomeContato, o.Telefone, o.Cnpj, string.Empty, o.ProximaAcao))
            .ToList();

        public async Task<OportunidadeCrm?> ObterAsync(Guid oportunidadeId, CancellationToken ct)
        {
            var o = await _crm.ObterOportunidadeAsync(oportunidadeId);
            return o == null ? null : new OportunidadeCrm(o.Id, o.NomeEmpresa, o.NomeContato, o.Status, o.ResponsavelUsuarioId, o.ProximaAcao);
        }

        public async Task<IReadOnlyList<string>> HistoricoAsync(Guid oportunidadeId, int limite, CancellationToken ct) =>
            (await _crm.ListarHistoricoOportunidadeAsync(oportunidadeId))
            .OrderByDescending(h => h.CreatedAt).Take(limite)
            .Select(h => $"{h.CreatedAt:yyyy-MM-dd} {h.Acao}: {h.Detalhe}".Trim())
            .ToList();

        public Task AdicionarNotaAsync(Guid oportunidadeId, int usuarioId, string acao, string detalhe, CancellationToken ct) =>
            _crm.AdicionarHistoricoOportunidadeAsync(oportunidadeId, usuarioId, acao, detalhe);

        public async Task DefinirProximaAcaoAsync(Guid oportunidadeId, int usuarioId, string valor, CancellationToken ct)
        {
            var row = await _crm.ObterOportunidadeAsync(oportunidadeId)
                ?? throw new AssistenteAudioException(404, CodigoAudio.SemAcesso, "Oportunidade nao encontrada.");
            row.ProximaAcao = valor;
            await _crm.AtualizarOportunidadeAsync(row);
            await _crm.AdicionarHistoricoOportunidadeAsync(oportunidadeId, usuarioId, "Proxima acao atualizada por audio", valor);
        }
    }
}
