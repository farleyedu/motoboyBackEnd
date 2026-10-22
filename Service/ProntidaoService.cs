using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace APIBack.Service
{
    public interface IProntidaoService
    {
        /// <summary>Checklist de prontidao da loja. <paramref name="visaoGestao"/> mostra o detalhe dos itens de infraestrutura.</summary>
        Task<ProntidaoDto?> ObterAsync(Guid estabelecimentoId, bool visaoGestao, CancellationToken ct = default);
    }

    /// <summary>Le do banco e da configuracao o que <see cref="ProntidaoRegras"/> precisa. Tabela ou coluna ausente vale como "nao configurado".</summary>
    public sealed class ProntidaoService : IProntidaoService
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly IConfiguration _configuration;
        private readonly TimeProvider _clock;

        public ProntidaoService(NpgsqlDataSource dataSource, IConfiguration configuration, TimeProvider? clock = null)
        {
            _dataSource = dataSource;
            _configuration = configuration;
            _clock = clock ?? TimeProvider.System;
        }

        public async Task<ProntidaoDto?> ObterAsync(Guid estabelecimentoId, bool visaoGestao, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            return await ObterAsync(connection, null, estabelecimentoId, visaoGestao, ct);
        }

        public async Task<ProntidaoDto?> ObterAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, bool visaoGestao, CancellationToken ct = default)
        {
            var dados = await CarregarAsync(c, tx, id, ct);
            return dados == null ? null : ProntidaoRegras.Avaliar(dados, visaoGestao, _clock.GetUtcNow());
        }

        private sealed class LojaRow
        {
            public string? SituacaoComercial { get; set; }
            public DateTime? TesteTerminaEm { get; set; }
            public Guid? IdTipoEstabelecimento { get; set; }
            public string? PlanoComercial { get; set; }
            public string[]? Modulos { get; set; }
            public string? Logradouro { get; set; }
            public string? Numero { get; set; }
            public string? Bairro { get; set; }
            public string? Cidade { get; set; }
            public string? Uf { get; set; }
            public string? Cep { get; set; }
            public double? Latitude { get; set; }
            public double? Longitude { get; set; }
            public decimal? RaioEntregaKm { get; set; }
            public decimal? TaxaEntregaFixa { get; set; }
            public decimal? TaxaEntregaPorKm { get; set; }
            public int? TempoPreparoMin { get; set; }
            public decimal? PedidoMinimo { get; set; }
            public string? UrlLogo { get; set; }
            public string? CorPrimaria { get; set; }
            public string? Telefone { get; set; }
            public string? Email { get; set; }
            public string? CnpjLoja { get; set; }
        }

        private sealed class WebRow
        {
            public bool Publicado { get; set; }
            public string? NomePublico { get; set; }
        }

        private sealed class CanalRow
        {
            public string Status { get; set; } = "";
            public string? Origem { get; set; }
            public string? WebhookEstado { get; set; }
            public DateTime? UltimoRecebimentoEm { get; set; }
            public DateTime? UltimoEnvioOkEm { get; set; }
        }

        private async Task<ProntidaoDados?> CarregarAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
        {
            var loja = await c.QuerySingleOrDefaultAsync<LojaRow>(new CommandDefinition(@"
SELECT e.situacao_comercial AS SituacaoComercial, e.teste_termina_em AS TesteTerminaEm, e.id_tipo_estabelecimento AS IdTipoEstabelecimento,
       e.plano_comercial AS PlanoComercial, e.modulos_ativos::text[] AS Modulos,
       e.logradouro AS Logradouro, e.numero AS Numero, e.bairro AS Bairro, e.cidade AS Cidade, e.uf AS Uf, e.cep AS Cep,
       e.latitude::double precision AS Latitude, e.longitude::double precision AS Longitude, e.raio_entrega_km AS RaioEntregaKm,
       e.taxa_entrega_fixa AS TaxaEntregaFixa, e.taxa_entrega_por_km AS TaxaEntregaPorKm, e.tempo_preparo_min AS TempoPreparoMin,
       e.pedido_minimo AS PedidoMinimo, e.url_logo AS UrlLogo, e.cor_primaria AS CorPrimaria,
       e.telefone AS Telefone, e.email AS Email, e.cnpj_loja AS CnpjLoja
  FROM estabelecimentos e WHERE e.id = @Id;", new { Id = id }, tx, cancellationToken: ct));
            if (loja == null) return null;

            var dados = new ProntidaoDados
            {
                Situacao = string.IsNullOrWhiteSpace(loja.SituacaoComercial) ? SituacoesComerciais.Ativo : loja.SituacaoComercial,
                TesteTerminaEm = loja.TesteTerminaEm is { } fim ? new DateTimeOffset(DateTime.SpecifyKind(fim, DateTimeKind.Utc)) : null,
                TipoEstabelecimentoId = loja.IdTipoEstabelecimento,
                PlanoComercial = loja.PlanoComercial,
                Modulos = loja.Modulos ?? Array.Empty<string>(),
                Logradouro = loja.Logradouro, Numero = loja.Numero, Bairro = loja.Bairro, Cidade = loja.Cidade, Uf = loja.Uf, Cep = loja.Cep,
                Latitude = loja.Latitude, Longitude = loja.Longitude, RaioEntregaKm = loja.RaioEntregaKm,
                TaxaEntregaFixa = loja.TaxaEntregaFixa, TaxaEntregaPorKm = loja.TaxaEntregaPorKm, TempoPreparoMin = loja.TempoPreparoMin,
                PedidoMinimo = loja.PedidoMinimo, UrlLogo = loja.UrlLogo, CorPrimaria = loja.CorPrimaria,
                Telefone = loja.Telefone, Email = loja.Email, Cnpj = loja.CnpjLoja
            };

            // Responsaveis (dono/gerente) com acesso ativo e se ja definiram a senha. Antes da migracao do convite, todos contam como com senha.
            const string responsaveisSql = @"
SELECT COUNT(DISTINCT u.id)::int AS Total, COUNT(DISTINCT u.id) FILTER (WHERE {0})::int AS ComSenha
  FROM usuario_estabelecimentos ue
  JOIN estabelecimentos e ON e.id = ue.id_estabelecimento
  JOIN usuario u ON u.id = ue.id_usuario AND u.deleted_at IS NULL
  LEFT JOIN usuario_empresas uemp ON uemp.id_empresa = e.id_empresa AND uemp.id_usuario = u.id
                                 AND COALESCE(uemp.ativo, TRUE) = TRUE AND COALESCE(uemp.status, 'ativo') = 'ativo'
 WHERE ue.id_estabelecimento = @Id AND COALESCE(ue.ativo, TRUE) = TRUE AND COALESCE(ue.status, 'ativo') = 'ativo'
   AND COALESCE(u.is_super_admin, FALSE) = FALSE AND ue.tipo_acesso::text <> 'motoboy'
   AND (uemp.tipo_acesso::text IN ('dono', 'gerente_empresa') OR ue.tipo_acesso::text = 'gerente_estabelecimento');";
            var responsaveis = await Seguro(() => c.QuerySingleAsync<(int Total, int ComSenha)>(new CommandDefinition(
                    string.Format(responsaveisSql, "NOT COALESCE(u.senha_pendente, FALSE)"), new { Id = id }, tx, cancellationToken: ct)),
                async () =>
                {
                    var total = await c.QuerySingleAsync<(int Total, int ComSenha)>(new CommandDefinition(
                        string.Format(responsaveisSql, "TRUE"), new { Id = id }, tx, cancellationToken: ct));
                    return total;
                });
            dados.ResponsaveisAtivos = responsaveis.Total;
            dados.ResponsaveisComSenha = responsaveis.ComSenha;

            dados.DiasAbertos = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM estabelecimento_horario
 WHERE estabelecimento_id = @Id AND COALESCE(fechado, FALSE) = FALSE AND abre_as IS NOT NULL AND fecha_as IS NOT NULL AND abre_as <> fecha_as;", id);
            dados.ProdutosVendaveis = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM cardapio_produto
 WHERE id_estabelecimento = @Id AND deleted_at IS NULL AND ativo = TRUE AND disponivel = TRUE AND preco_base > 0;", id);
            dados.ProdutosTotal = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM cardapio_produto WHERE id_estabelecimento = @Id AND deleted_at IS NULL AND ativo = TRUE;", id);
            dados.ProdutosComImagem = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM cardapio_produto
 WHERE id_estabelecimento = @Id AND deleted_at IS NULL AND ativo = TRUE AND COALESCE(btrim(imagem_url), '') <> '';", id);
            dados.Categorias = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM cardapio_categoria WHERE id_estabelecimento = @Id AND deleted_at IS NULL AND ativo = TRUE;", id);
            dados.ZonasAtivas = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM delivery_zona WHERE estabelecimento_id = @Id AND ativo = TRUE;", id);

            var web = await Seguro(() => c.QuerySingleOrDefaultAsync<WebRow>(new CommandDefinition(@"
SELECT COALESCE(publicado, FALSE) AS Publicado, nome_publico AS NomePublico FROM cardapio_web_config WHERE id_estabelecimento = @Id;",
                new { Id = id }, tx, cancellationToken: ct)), () => Task.FromResult<WebRow?>(null));
            dados.CardapioWebConfigurado = web != null;
            dados.CardapioWebPublicado = web?.Publicado ?? false;
            dados.NomePublico = web?.NomePublico;

            dados.Canais = await LerCanaisAsync(c, tx, id, ct);

            dados.IfoodAutorizado = await Contar(c, tx, ct,
                "SELECT COUNT(*)::int FROM ifood_connections WHERE estabelecimento_id = @Id AND status = 'authorized';", id) > 0;
            // Estacao online = ativa, nao revogada e com sinal nos ultimos 2 minutos.
            dados.ImpressaoOnline = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM impressao_estacao
 WHERE estabelecimento_id = @Id AND ativa = TRUE AND revogada_em IS NULL AND ultimo_sinal_em > NOW() - INTERVAL '2 minutes';", id) > 0;
            dados.Atendentes = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM usuario_estabelecimentos ue JOIN usuario u ON u.id = ue.id_usuario AND u.deleted_at IS NULL
 WHERE ue.id_estabelecimento = @Id AND COALESCE(ue.ativo, TRUE) = TRUE AND ue.tipo_acesso::text = 'atendente';", id);
            dados.MotoboysVinculados = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM motoboy_estabelecimento me JOIN motoboy m ON m.id = me.motoboy_id
 WHERE me.estabelecimento_id = @Id AND me.ativo = TRUE AND COALESCE(m.is_simulated, FALSE) = FALSE;", id);
            dados.MotoboysComPlano = await Contar(c, tx, ct, @"
SELECT COUNT(*)::int FROM motoboy_estabelecimento me JOIN motoboy m ON m.id = me.motoboy_id
 WHERE me.estabelecimento_id = @Id AND me.ativo = TRUE AND COALESCE(m.is_simulated, FALSE) = FALSE
   AND EXISTS (SELECT 1 FROM delivery_rider_pay_plans p WHERE p.estabelecimento_id = me.estabelecimento_id AND p.motoboy_id = me.motoboy_id);", id);
            dados.RemuneracaoPadraoDefinida = await Contar(c, tx, ct,
                "SELECT COUNT(*)::int FROM delivery_rider_pay_plans WHERE estabelecimento_id = @Id AND motoboy_id IS NULL;", id) > 0;

            dados.InfraDeliveryTracking = _configuration.GetValue<bool>("DeliveryTracking:Enabled");
            dados.InfraMapsApiKey = Configurado("Google:MapsApiKey");
            dados.InfraChaveToken = Configurado("Atendimento:ChaveToken");
            dados.InfraCardapioBaseUrl = Configurado("Atendimento:CardapioBaseUrl");
            dados.InfraMetaAppSecret = Configurado("Automation:Meta:AppSecret");
            dados.InfraMetaVerifyToken = Configurado("Automation:VerifyToken");
            return dados;
        }

        /// <summary>Configurado = nao vazio e fora dos marcadores de modelo (__SET_IN_ENV__, &lt;TODO&gt;).</summary>
        private bool Configurado(string chave)
        {
            var valor = _configuration[chave]?.Trim();
            return !string.IsNullOrEmpty(valor) && !valor.StartsWith("__", StringComparison.Ordinal)
                && !valor.Equals("<TODO>", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<IReadOnlyList<CanalEstadoLeitura>> LerCanaisAsync(NpgsqlConnection c, NpgsqlTransaction? tx, Guid id, CancellationToken ct)
        {
            // Somente leitura: o canal e mantido por outra area. webhook_estado so existe depois da migracao do numero proprio.
            var rows = await Seguro(() => c.QueryAsync<CanalRow>(new CommandDefinition(@"
SELECT status AS Status, origem AS Origem, webhook_estado AS WebhookEstado,
       ultimo_recebimento_em AS UltimoRecebimentoEm, ultimo_envio_ok_em AS UltimoEnvioOkEm
  FROM canal_whatsapp WHERE id_estabelecimento = @Id;", new { Id = id }, tx, cancellationToken: ct)),
                async () => await Seguro(() => c.QueryAsync<CanalRow>(new CommandDefinition(@"
SELECT status AS Status, 'zippy' AS Origem, NULL AS WebhookEstado,
       ultimo_recebimento_em AS UltimoRecebimentoEm, ultimo_envio_ok_em AS UltimoEnvioOkEm
  FROM canal_whatsapp WHERE id_estabelecimento = @Id;", new { Id = id }, tx, cancellationToken: ct)),
                    () => Task.FromResult<IEnumerable<CanalRow>>(Array.Empty<CanalRow>())));
            return rows.Select(r => new CanalEstadoLeitura(r.Status, r.Origem ?? "zippy", r.WebhookEstado, r.UltimoRecebimentoEm, r.UltimoEnvioOkEm)).ToList();
        }

        private static Task<int> Contar(NpgsqlConnection c, NpgsqlTransaction? tx, CancellationToken ct, string sql, Guid id) =>
            Seguro(() => c.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { Id = id }, tx, cancellationToken: ct)), () => Task.FromResult(0));

        /// <summary>
        /// Tabela ou coluna que ainda nao existe neste ambiente vale como "nao configurado". Dentro de uma transacao o erro a aborta,
        /// entao a leitura de prontidao usada ao iniciar a operacao roda numa conexao sem transacao (ver SituacaoComercialService).
        /// </summary>
        private static async Task<T> Seguro<T>(Func<Task<T>> consulta, Func<Task<T>> alternativa)
        {
            try
            {
                return await consulta();
            }
            catch (PostgresException ex) when (ex.SqlState is PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn)
            {
                return await alternativa();
            }
        }
    }
}
