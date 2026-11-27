using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.Gestao;
using APIBack.Service.Interface;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>
    /// O que a loja tem ligado. "DoPlano" é o que vem do plano e dos extras valendo; as duas listas "PelaGestao" são os ajustes só desta
    /// loja. No teste a loja usa todos os módulos: os ajustes passam a valer quando o teste acaba.
    /// </summary>
    public sealed record ModulosDaLojaDto(bool EmTeste, IReadOnlyList<string> Ligados, IReadOnlyList<string> DoPlano,
        IReadOnlyList<string> LigadosPelaGestao, IReadOnlyList<string> DesligadosPelaGestao);

    public sealed class AlterarModuloRequest
    {
        public bool Ligado { get; set; }
        public string? Motivo { get; set; }
    }

    public sealed class ModuloDaLojaException : Exception
    {
        public ModuloDaLojaException(int status, string codigo, string mensagem, object? detalhes = null) : base(mensagem)
        {
            Status = status;
            Codigo = codigo;
            Detalhes = detalhes;
        }

        public int Status { get; }
        public string Codigo { get; }
        public object? Detalhes { get; }
    }

    public interface IModulosDaLojaService
    {
        Task<ModulosDaLojaDto> ObterAsync(Guid loja, CancellationToken ct = default);
        Task<ModulosDaLojaDto> AlterarAsync(Guid loja, string modulo, AlterarModuloRequest pedido, int? usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// Módulo avulso por loja, só pela Gestão. Não muda plano nem cobrança. O ajuste fica gravado à parte e o gatilho
    /// trg_modulos_da_gestao o reaplica quando o plano muda, um extra vence ou o cadastro é salvo.
    /// </summary>
    public sealed class ModulosDaLojaService : IModulosDaLojaService
    {
        public const string CodigoInvalido = "MODULO_INVALIDO";
        public const string CodigoExigido = "MODULO_EXIGIDO";
        private const string Geral = "GERAL";

        private readonly NpgsqlDataSource _db;
        private readonly IMemoryCache _cache;
        private readonly IAuthDecisionCache? _acesso;

        public ModulosDaLojaService(NpgsqlDataSource db, IMemoryCache cache, IAuthDecisionCache? acesso = null)
        {
            _db = db;
            _cache = cache;
            _acesso = acesso;
        }

        public async Task<ModulosDaLojaDto> ObterAsync(Guid loja, CancellationToken ct = default)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            return Montar(await LerAsync(cx, null, loja, false, ct));
        }

        public async Task<ModulosDaLojaDto> AlterarAsync(Guid loja, string modulo, AlterarModuloRequest pedido, int? usuarioId, CancellationToken ct = default)
        {
            var chave = Chave(modulo)
                ?? throw new ModuloDaLojaException(422, CodigoInvalido, "Módulo desconhecido.");

            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var atual = await LerAsync(cx, tx, loja, true, ct);

            var ativos = new HashSet<string>(atual.Ativos, StringComparer.OrdinalIgnoreCase);
            var doPlano = new HashSet<string>(atual.DoPlano, StringComparer.OrdinalIgnoreCase);
            var ligados = new HashSet<string>(atual.Ligados, StringComparer.OrdinalIgnoreCase);
            var desligados = new HashSet<string>(atual.Desligados, StringComparer.OrdinalIgnoreCase);

            if (pedido.Ligado)
            {
                // Ligar um módulo liga junto o que ele precisa para funcionar.
                foreach (var m in ModuleDependencies.Close(new[] { chave }))
                {
                    if (m != chave && ativos.Contains(m)) continue;
                    desligados.Remove(m);
                    if (!doPlano.Contains(m)) ligados.Add(m);
                    ativos.Add(m);
                }
            }
            else
            {
                var exigem = ModuleDependencies.RequiredBy(chave, ativos);
                if (exigem.Count > 0)
                {
                    var nomes = Nomes(exigem);
                    throw new ModuloDaLojaException(409, CodigoExigido,
                        $"Desligue antes: {string.Join(", ", nomes)}.", new { exigidoPor = nomes });
                }
                ligados.Remove(chave);
                // Só precisa lembrar do desligamento o que o plano ou um extra ligaria de novo.
                if (doPlano.Contains(chave)) desligados.Add(chave); else desligados.Remove(chave);
                ativos.Remove(chave);
            }

            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET modulos_ligados_gestao = @Ligados, modulos_desligados_gestao = @Desligados,
       modulos_ativos = CAST(@Ativos AS text[])::modulo_enum[], data_atualizacao = NOW()
 WHERE id = @Loja", new { Loja = loja, Ligados = ligados.ToArray(), Desligados = desligados.ToArray(), Ativos = ativos.ToArray() },
                tx, cancellationToken: ct));

            var motivo = string.IsNullOrWhiteSpace(pedido.Motivo) ? null : pedido.Motivo.Trim();
            await cx.ExecuteAsync(new CommandDefinition(
                "INSERT INTO gestao_acao_log (usuario_id, estabelecimento_id, acao, detalhe) VALUES (@U, @Loja, @Acao, @Detalhe)",
                new
                {
                    U = usuarioId, Loja = loja, Acao = pedido.Ligado ? "modulo_ligado" : "modulo_desligado",
                    Detalhe = string.Join(" · ", new[] { Nomes(new[] { chave }).FirstOrDefault() ?? chave, motivo }.Where(x => x != null))
                }, tx, cancellationToken: ct));

            var depois = Montar(await LerAsync(cx, tx, loja, false, ct));
            await tx.CommitAsync(ct);
            _acesso?.InvalidateEstablishment(loja);
            _cache.Remove(SituacaoComercialService.CacheKey(loja));
            return depois;
        }

        private sealed class Linha
        {
            public string Situacao { get; set; } = string.Empty;
            public string[] Ativos { get; set; } = Array.Empty<string>();
            public string[] DoPlano { get; set; } = Array.Empty<string>();
            public string[] Ligados { get; set; } = Array.Empty<string>();
            public string[] Desligados { get; set; } = Array.Empty<string>();
        }

        private static async Task<Linha> LerAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, bool travar, CancellationToken ct) =>
            await cx.QuerySingleOrDefaultAsync<Linha>(new CommandDefinition(@"
SELECT e.situacao_comercial AS Situacao,
       COALESCE(e.modulos_ativos::text[], '{}'::text[]) AS Ativos,
       COALESCE((SELECT p.modulos_ativos::text[] FROM pacotes_comerciais p WHERE p.chave = e.plano_comercial), '{}'::text[])
       || COALESCE((SELECT array_agg(m::text) FROM compra_extra ce, unnest(ce.modulos_ativos) m
                     WHERE ce.id_estabelecimento = e.id AND ce.status = 'ativa' AND ce.vigencia_fim > NOW()), '{}'::text[]) AS DoPlano,
       e.modulos_ligados_gestao AS Ligados,
       e.modulos_desligados_gestao AS Desligados
  FROM estabelecimentos e
 WHERE e.id = @Loja" + (travar ? " FOR UPDATE OF e" : string.Empty), new { Loja = loja }, tx, cancellationToken: ct))
            ?? throw new ModuloDaLojaException(404, "ESTABELECIMENTO_NAO_ENCONTRADO", "Estabelecimento não encontrado.");

        private static ModulosDaLojaDto Montar(Linha l) =>
            new(l.Situacao == "teste", Nomes(l.Ativos), Nomes(l.DoPlano), Nomes(l.Ligados), Nomes(l.Desligados));

        private static IReadOnlyList<string> Nomes(IEnumerable<string> chaves) =>
            EstabelecimentoModuleMapper.ToUiModules(string.Empty, chaves.ToArray()).OrderBy(n => n, StringComparer.Ordinal).ToList();

        /// <summary>Do nome que o painel usa para o valor do banco. GERAL não se liga nem se desliga.</summary>
        private static string? Chave(string? nome)
        {
            if (string.IsNullOrWhiteSpace(nome)) return null;
            return EstabelecimentoModuleMapper.ToDatabaseModules(new[] { nome }, null)
                .Where(m => m != Geral)
                .FirstOrDefault(m => EstabelecimentoModuleMapper.ToUiModules(string.Empty, new[] { m })
                    .Any(u => string.Equals(u, nome.Trim(), StringComparison.OrdinalIgnoreCase)));
        }
    }
}
