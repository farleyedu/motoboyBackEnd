using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using APIBack.DTOs.Configuracoes;
using APIBack.Repository.Interface;

namespace APIBack.Service
{
    public interface IOperacaoLojaService
    {
        Task<StoreOperationDto> StatusAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<StoreOperationDto> AbrirHojeAsync(Guid estabelecimentoId, int actorUserId, OpenStoreTodayRequest request, CancellationToken ct = default);
        Task<StoreOperationDto> FecharAsync(Guid estabelecimentoId, int actorUserId, CancellationToken ct = default);
    }

    /// <summary>
    /// Abrir e fechar a loja pelo painel. Os dois exigem situacao teste/ativo (nada abre em implantacao nem bloqueada) e, para abrir,
    /// a prontidao obrigatoria ok. Super admin recebe as mesmas travas. A recusa diz o motivo, como resolver e para onde ir.
    /// </summary>
    public sealed class OperacaoLojaService : IOperacaoLojaService
    {
        private readonly IHorarioOperacaoRepository _horarios;
        private readonly ISituacaoComercialService _situacoes;
        private readonly IProntidaoService _prontidao;

        public OperacaoLojaService(IHorarioOperacaoRepository horarios, ISituacaoComercialService situacoes, IProntidaoService prontidao)
        {
            _horarios = horarios;
            _situacoes = situacoes;
            _prontidao = prontidao;
        }

        public async Task<StoreOperationDto> StatusAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            var estado = await _horarios.ObterEstadoAsync(estabelecimentoId, ct);
            if (estado.AbertoAgora)
            {
                estado.PodeAbrir = false;
                return estado;
            }

            // Fechada: a loja so pode abrir na mao se a situacao permitir e a prontidao obrigatoria estiver ok.
            var impedimento = await ImpedimentoParaAbrirAsync(estabelecimentoId, ct);
            estado.PodeAbrir = impedimento == null;
            if (impedimento != null)
            {
                estado.ComoResolver = impedimento.ComoResolver;
                estado.Correcao = impedimento.Correcao;
            }
            return estado;
        }

        public async Task<StoreOperationDto> AbrirHojeAsync(Guid estabelecimentoId, int actorUserId, OpenStoreTodayRequest request, CancellationToken ct = default)
        {
            await ExigirOperacaoAsync(estabelecimentoId, exigirProntidao: true, ct);
            var estado = await _horarios.AbrirHojeAsync(estabelecimentoId, actorUserId, request, ct);
            estado.PodeAbrir = false;
            return estado;
        }

        public async Task<StoreOperationDto> FecharAsync(Guid estabelecimentoId, int actorUserId, CancellationToken ct = default)
        {
            await ExigirOperacaoAsync(estabelecimentoId, exigirProntidao: false, ct);
            var estado = await _horarios.FecharAgoraAsync(estabelecimentoId, actorUserId, ct);
            if (!estado.AbertoAgora) estado.PodeAbrir = await ImpedimentoParaAbrirAsync(estabelecimentoId, ct) == null;
            return estado;
        }

        /// <summary>Por que nao da para abrir na mao agora (nulo = pode).</summary>
        private async Task<DetalhesErroOperacional?> ImpedimentoParaAbrirAsync(Guid id, CancellationToken ct)
        {
            try
            {
                await ExigirOperacaoAsync(id, exigirProntidao: true, ct);
                return null;
            }
            catch (DeliveryDomainException ex) when (ex.Details is DetalhesErroOperacional detalhes)
            {
                // A primeira pendencia diz onde ir; sem pendencia, vale o caminho do proprio erro.
                var primeira = detalhes.Pendencias is { Count: > 0 } ? detalhes.Pendencias[0] : null;
                return primeira == null ? detalhes : detalhes with { ComoResolver = primeira.ComoResolver, Correcao = primeira.Correcao ?? detalhes.Correcao };
            }
        }

        private async Task ExigirOperacaoAsync(Guid id, bool exigirProntidao, CancellationToken ct)
        {
            var situacao = await _situacoes.ObterAsync(id, ct)
                ?? throw new DeliveryDomainException(404, "ESTABELECIMENTO_NOT_FOUND", "Estabelecimento não encontrado.");
            if (situacao.Situacao == SituacoesComerciais.Bloqueado)
                throw ErrosOperacionais.Excecao(ErrosOperacionais.EstabelecimentoBloqueado);

            var prontidao = situacao.Situacao == SituacoesComerciais.Implantacao || exigirProntidao
                ? await _prontidao.ObterAsync(id, visaoGestao: false, ct)
                : null;
            if (situacao.Situacao == SituacoesComerciais.Implantacao)
                throw ErrosOperacionais.Excecao(ErrosOperacionais.OperacaoNaoIniciada,
                    "A operação desta loja ainda não foi iniciada.", prontidao == null ? null : ProntidaoRegras.Pendencias(prontidao, 3));
            if (exigirProntidao && prontidao is { PodeComecarAOperar: false })
                throw ErrosOperacionais.Excecao(ErrosOperacionais.ProntidaoIncompleta,
                    $"Ainda faltam {prontidao.PendenciasBloqueantes} item(ns) obrigatório(s) para abrir a loja.", ProntidaoRegras.Pendencias(prontidao));
        }
    }
}
