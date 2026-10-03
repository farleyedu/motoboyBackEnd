using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public sealed record ServicoDaLojaItem(
        string Codigo, string Nome, string? Descricao, bool Permitido, bool PadraoDoTipo, bool Ativo, IReadOnlyList<string> ModulosExigidos);

    public sealed record ServicosDaLojaView(Guid EstabelecimentoId, IReadOnlyList<ServicoDaLojaItem> Servicos, bool PodeEditar);

    public interface IServicosDaLojaService
    {
        Task<ServicosDaLojaView> ObterAsync(AtendimentoAtor ator, Guid estabelecimentoId);
        /// <summary>So a Gestao liga e desliga servicos. Ligar um servico liga junto os modulos que ele exige.</summary>
        Task<ServicosDaLojaView> DefinirAsync(AtendimentoAtor ator, Guid estabelecimentoId, IEnumerable<string>? servicos);
    }

    public sealed class ServicosDaLojaService : IServicosDaLojaService
    {
        private readonly ICatalogoRepository _catalogo;
        private readonly ILogger<ServicosDaLojaService> _logger;

        public ServicosDaLojaService(ICatalogoRepository catalogo, ILogger<ServicosDaLojaService> logger)
        {
            _catalogo = catalogo;
            _logger = logger;
        }

        public async Task<ServicosDaLojaView> ObterAsync(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            ExigirLeitura(ator, estabelecimentoId);
            return await MontarAsync(ator, estabelecimentoId);
        }

        public async Task<ServicosDaLojaView> DefinirAsync(AtendimentoAtor ator, Guid estabelecimentoId, IEnumerable<string>? servicos)
        {
            if (!ator.Gestao)
            {
                _logger.LogWarning("[servicos] ev=negado motivo=so_gestao_liga_servicos usuario={Usuario} loja={Loja}", ator.UsuarioId, estabelecimentoId);
                throw new UnauthorizedAccessException("Somente a Gestao liga ou desliga servicos.");
            }

            var tipo = await _catalogo.ObterTipoDaLojaAsync(estabelecimentoId);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId))
            {
                throw new KeyNotFoundException("Estabelecimento nao encontrado.");
            }

            var catalogo = await _catalogo.ListarCatalogoAsync();
            var permitidos = tipo.HasValue ? await _catalogo.ListarServicosDoTipoAsync(tipo.Value) : Array.Empty<ServicoDoTipo>();
            var ativos = AtendimentoRegras.ValidarServicosDaLoja(servicos, catalogo, permitidos);
            var modulos = AtendimentoRegras.ModulosParaServicos(catalogo, ativos);

            await _catalogo.DefinirServicosDaLojaAsync(estabelecimentoId, ativos, modulos, ator.UsuarioId);

            _logger.LogInformation(
                "[servicos] ev=definidos loja={Loja} usuario={Usuario} ativos={Ativos} modulos_garantidos={Modulos}",
                estabelecimentoId, ator.UsuarioId, string.Join(",", ativos), string.Join(",", modulos));

            return await MontarAsync(ator, estabelecimentoId);
        }

        private async Task<ServicosDaLojaView> MontarAsync(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            var tipo = await _catalogo.ObterTipoDaLojaAsync(estabelecimentoId);
            var catalogo = await _catalogo.ListarCatalogoAsync();
            var doTipo = tipo.HasValue ? await _catalogo.ListarServicosDoTipoAsync(tipo.Value) : Array.Empty<ServicoDoTipo>();
            var daLoja = await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId);

            var itens = catalogo
                .Where(s => s.Ativo)
                .Select(s =>
                {
                    var permitido = doTipo.FirstOrDefault(t => t.Codigo == s.Codigo);
                    return new ServicoDaLojaItem(
                        s.Codigo, s.Nome, s.Descricao,
                        Permitido: permitido != null,
                        PadraoDoTipo: permitido?.Padrao ?? false,
                        Ativo: daLoja.Any(l => l.Codigo == s.Codigo && l.Ativo),
                        ModulosExigidos: s.ModulosExigidos);
                })
                .ToList();

            return new ServicosDaLojaView(estabelecimentoId, itens, PodeEditar: ator.Gestao);
        }

        internal static void ExigirLeitura(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            if (!ator.Gestao && ator.EstabelecimentoId != estabelecimentoId)
            {
                throw new UnauthorizedAccessException("Acesso negado a este estabelecimento.");
            }
        }
    }
}
