using System;
using System.Threading.Tasks;
using APIBack.DTOs.Atendimento;
using APIBack.Repository.Interface;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public interface IPedidosAbertosService
    {
        /// <summary>A loja aceita pedido agora? Interruptor "aceitando pedidos" + horario de atendimento.</summary>
        Task<SituacaoPedidos> AvaliarAsync(Guid estabelecimentoId, bool aceitaPedidos);
    }

    /// <summary>Uma unica leitura da regra de pedidos abertos, para o cardapio (vitrine, cotacao e pedido) nunca divergir.</summary>
    public sealed class PedidosAbertosService : IPedidosAbertosService
    {
        private readonly IAtendimentoRepository _atendimento;
        private readonly ILogger<PedidosAbertosService> _logger;
        private readonly TimeProvider _relogio;

        public PedidosAbertosService(IAtendimentoRepository atendimento, ILogger<PedidosAbertosService> logger, TimeProvider? relogio = null)
        {
            _atendimento = atendimento;
            _logger = logger;
            _relogio = relogio ?? TimeProvider.System;
        }

        public async Task<SituacaoPedidos> AvaliarAsync(Guid estabelecimentoId, bool aceitaPedidos)
        {
            HorarioAtendimentoDto? horario = null;
            try
            {
                horario = (await _atendimento.GetConfigAsync(estabelecimentoId)).HorarioAtendimento;
            }
            catch (Exception ex)
            {
                // Sem o horario nao da para negar com certeza: vale so o interruptor, e o erro fica no log.
                _logger.LogWarning(ex, "[cardapio] ev=sem_horario loja={Loja}", estabelecimentoId);
            }

            return PedidosAbertosRules.Avaliar(aceitaPedidos, horario, AtendimentoConfigRules.ParaHorarioLocal(_relogio.GetUtcNow().UtcDateTime));
        }
    }
}
