using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>Situacao comercial do estabelecimento (coluna estabelecimentos.situacao_comercial).</summary>
    public static class SituacoesComerciais
    {
        public const string Implantacao = "implantacao";
        public const string Teste = "teste";
        public const string Ativo = "ativo";
        public const string Bloqueado = "bloqueado";

        public static readonly string[] Planos = { "partida", "rota", "turbo", "frota", "personalizado" };
    }

    /// <summary>Regras puras da situacao comercial (sem banco, para testar).</summary>
    public static class SituacaoComercialRules
    {
        public const int DiasTeste = 15;
        public const string CodigoBloqueado = "ESTABELECIMENTO_BLOQUEADO";
        public const string CodigoOperacaoNaoIniciada = "OPERACAO_NAO_INICIADA";

        private const string SessaoMotoboy = "/api/v2/motoboys/me/session";

        /// <summary>
        /// O que uma loja bloqueada ainda pode chamar. As excecoes do motoboy so servem para terminar
        /// uma rota existente; os servicos dessas rotas validam a sessao e a posse do pedido.
        /// </summary>
        public static bool PermitidoQuandoBloqueado(string? path, string? method)
        {
            var p = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();
            if (p == "/api/auth" || p.StartsWith("/api/auth/", StringComparison.Ordinal)) return true;
            if (p == "/api/me/estabelecimentos" && HttpMethods.IsGet(method ?? string.Empty)) return true;
            if (p == "/api/v2/comercial/situacao" && HttpMethods.IsGet(method ?? string.Empty)) return true;
            if (HttpMethods.IsPost(method ?? string.Empty))
            {
                var parts = p.Split('/');
                if (parts.Length == 8 && parts[1] == "api" && parts[2] == "v2" &&
                    parts[3] == "delivery" && parts[4] == "motoboys" &&
                    int.TryParse(parts[5], out var motoboyId) && motoboyId > 0 &&
                    parts[6] == "fila" && parts[7] == "concluir-atual") return true;
            }

            if (p == SessaoMotoboy || p.StartsWith(SessaoMotoboy + "/", StringComparison.Ordinal))
            {
                var resto = p.Length > SessaoMotoboy.Length ? p[(SessaoMotoboy.Length + 1)..] : string.Empty;
                var post = HttpMethods.IsPost(method ?? string.Empty);
                var get = HttpMethods.IsGet(method ?? string.Empty);
                if (resto.Length == 0) return get || HttpMethods.IsDelete(method ?? string.Empty);
                if (get && (resto == "queue" || resto.StartsWith("orders/", StringComparison.Ordinal) ||
                            resto.StartsWith("completion/", StringComparison.Ordinal))) return true;
                if (!post) return false;
                return resto is "heartbeat" or "location" or "location/batch" or
                    "stops/current/arrive" or "stops/current/deliver" or
                    "stops/current/fail" or "queue/arrived-at-store" or
                    "completion" or "completion/ifood" ||
                    resto.StartsWith("completion/", StringComparison.Ordinal);
            }

            return false;
        }

        /// <summary>Antes de iniciar a operacao, a loja configura o painel mas nao cria pedidos nem opera delivery.</summary>
        public static bool OperacaoExigeInicio(string? path, string? method)
        {
            var p = (path ?? string.Empty).TrimEnd('/').ToLowerInvariant();
            // A implantacao pode montar as regras do delivery e preparar a impressao.
            if (p == "/api/v2/delivery/configuracoes" ||
                p == "/api/v2/delivery/zonas" || p.StartsWith("/api/v2/delivery/zonas/", StringComparison.Ordinal) ||
                p == "/api/v2/delivery/horarios-especiais" || p.StartsWith("/api/v2/delivery/horarios-especiais/", StringComparison.Ordinal) ||
                p == "/api/v2/delivery/impressao/configuracao" ||
                p == "/api/v2/delivery/impressao/loja" ||
                p == "/api/v2/delivery/impressao/estacoes" ||
                p == "/api/v2/delivery/impressao/permissao" ||
                p == "/api/v2/delivery/impressao/rascunho") return false;
            if (p == "/api/v2/delivery" || p.StartsWith("/api/v2/delivery/", StringComparison.Ordinal) ||
                p == "/api/delivery" || p.StartsWith("/api/delivery/", StringComparison.Ordinal) ||
                p == "/api/v2/simulador" || p.StartsWith("/api/v2/simulador/", StringComparison.Ordinal) ||
                p == "/api/v2/ifood/store" || p.StartsWith("/api/v2/ifood/store/", StringComparison.Ordinal)) return true;
            if (p == "/api/pedido" || p.StartsWith("/api/pedido/", StringComparison.Ordinal))
                return !HttpMethods.IsGet(method ?? string.Empty);
            if (p == "/api/checkout" || p.StartsWith("/api/checkout/", StringComparison.Ordinal)) return true;
            return p == SessaoMotoboy || p.StartsWith(SessaoMotoboy + "/", StringComparison.Ordinal);
        }

        public static DateTimeOffset FimDoTeste(DateTimeOffset inicio) => inicio.AddDays(DiasTeste);
    }

    /// <summary>
    /// Limites de motoboy por pacotes de 3 comprados (tabela 1.5 do plano): vagas por dia operacional = 3 x pacotes;
    /// vinculados = 10, 15, 20, 25 e para em 30.
    /// </summary>
    public static class MotoboyLimites
    {
        public const int VinculadosTeto = 30;

        public static int VagasPorDia(int pacotes) => 3 * Math.Max(1, pacotes);

        public static int Vinculados(int pacotes) => Math.Min(VinculadosTeto, 5 + 5 * Math.Max(1, pacotes));
    }

    public sealed record SituacaoComercialDto(
        Guid EstabelecimentoId,
        string Situacao,
        DateTimeOffset? OperacaoIniciadaEm,
        DateTimeOffset? TesteTerminaEm,
        string? Plano,
        int PacotesMotoboy,
        int VagasPorDia,
        int VinculadosMaximo,
        DateTimeOffset? BloqueadoEm,
        string? BloqueadoMotivo);

    /// <summary>acao: iniciar_operacao | ativar | bloquear | desbloquear.</summary>
    public sealed record AcaoSituacaoRequest(string? Acao, string? Motivo, string? Plano, int? PacotesMotoboy);

    public sealed class SituacaoComercialException : Exception
    {
        public SituacaoComercialException(int status, string code, string message) : base(message)
        {
            Status = status;
            Code = code;
        }

        public int Status { get; }
        public string Code { get; }
    }

    public sealed record TesteVencidoDto(Guid EstabelecimentoId, string Nome, string? Empresa, DateTimeOffset? TesteTerminaEm);
    public sealed record SituacaoComercialHistoricoDto(
        long Id, string Acao, string? SituacaoAntes, string SituacaoDepois,
        string? Motivo, int? UsuarioId, DateTimeOffset CriadoEm);

    public interface ISituacaoComercialService
    {
        Task<SituacaoComercialDto?> ObterAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<SituacaoComercialDto> AplicarAsync(Guid estabelecimentoId, AcaoSituacaoRequest request, int? usuarioId, CancellationToken ct = default);
        /// <summary>Lojas em teste com o prazo de 15 dias vencido. So informa: nada e bloqueado sozinho.</summary>
        Task<IReadOnlyList<TesteVencidoDto>> ListarTestesVencidosAsync(CancellationToken ct = default);
        Task<IReadOnlyList<SituacaoComercialHistoricoDto>> ListarHistoricoAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<bool> TemModuloAsync(Guid estabelecimentoId, string modulo, CancellationToken ct = default);
    }

    public sealed class SituacaoComercialService : ISituacaoComercialService
    {
        private sealed class PacoteRow
        {
            public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
            public int PacotesMotoboyBase { get; set; }
        }
        // Curto o bastante para um bloqueio valer rapido; a Gestao invalida na hora ao mudar.
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

        private readonly NpgsqlDataSource _dataSource;
        private readonly IMemoryCache _cache;

        public SituacaoComercialService(NpgsqlDataSource dataSource, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _cache = cache;
        }

        private static string Key(Guid id) => $"situacao-comercial:{id}";

        public async Task<SituacaoComercialDto?> ObterAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            if (_cache.TryGetValue(Key(estabelecimentoId), out SituacaoComercialDto? cached)) return cached;
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            SituacaoComercialDto? dto;
            try
            {
                dto = await LerAsync(connection, null, estabelecimentoId, ct);
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                // Codigo publicado antes da migracao 20261020_02: sem a coluna, ninguem fica bloqueado.
                return null;
            }
            if (dto != null) _cache.Set(Key(estabelecimentoId), dto, CacheTtl);
            return dto;
        }

        public async Task<bool> TemModuloAsync(Guid estabelecimentoId, string modulo, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT e.situacao_comercial = 'teste'
       OR COALESCE(CAST(@Modulo AS modulo_enum) = ANY (e.modulos_ativos), FALSE)
  FROM estabelecimentos e WHERE e.id = @EstabelecimentoId;",
                new { EstabelecimentoId = estabelecimentoId, Modulo = modulo }, cancellationToken: ct));
        }

        public async Task<SituacaoComercialDto> AplicarAsync(Guid estabelecimentoId, AcaoSituacaoRequest request, int? usuarioId, CancellationToken ct = default)
        {
            var acao = (request.Acao ?? string.Empty).Trim().ToLowerInvariant();
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);

            // A edicao de pacote trava primeiro o pacote e depois as lojas; a ativacao usa a mesma ordem.
            var planoSolicitado = (request.Plano ?? string.Empty).Trim().ToLowerInvariant();
            PacoteRow? pacote = null;
            if (acao == "ativar" && planoSolicitado != "personalizado")
            {
                pacote = await connection.QuerySingleOrDefaultAsync<PacoteRow>(new CommandDefinition(@"
SELECT modulos_ativos::text[] AS ModulosAtivos, pacotes_motoboy_base AS PacotesMotoboyBase
  FROM pacotes_comerciais WHERE chave=@Chave FOR SHARE;",
                    new { Chave = planoSolicitado }, tx, cancellationToken: ct));
                if (pacote == null)
                    throw new SituacaoComercialException(422, "PLANO_INVALIDO", "Escolha um pacote comercial válido.");
            }

            var atual = await LerAsync(connection, tx, estabelecimentoId, ct, forUpdate: true)
                ?? throw new SituacaoComercialException(404, "ESTABELECIMENTO_NAO_ENCONTRADO", "Estabelecimento nao encontrado.");
            var antes = atual.Situacao;
            string depois;
            var motivo = string.IsNullOrWhiteSpace(request.Motivo) ? null : request.Motivo.Trim();

            switch (acao)
            {
                case "iniciar_operacao":
                    if (antes != SituacoesComerciais.Implantacao)
                        throw new SituacaoComercialException(409, "OPERACAO_JA_INICIADA", "A operacao so pode ser iniciada a partir da implantacao.");
                    depois = SituacoesComerciais.Teste;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET situacao_comercial = 'teste', operacao_iniciada_em = NOW(), teste_termina_em = NOW() + make_interval(days => @Dias)
 WHERE id = @Id;", new { Id = estabelecimentoId, Dias = SituacaoComercialRules.DiasTeste }, tx, cancellationToken: ct));
                    break;

                case "ativar":
                    if (antes == SituacoesComerciais.Bloqueado)
                        throw new SituacaoComercialException(409, "ESTABELECIMENTO_BLOQUEADO", "Desbloqueie a loja antes de ativar ou alterar o plano.");
                    var plano = planoSolicitado;
                    if (!SituacoesComerciais.Planos.Contains(plano))
                        throw new SituacaoComercialException(422, "PLANO_INVALIDO", "Escolha o plano: partida, rota, turbo, frota ou personalizado.");
                    var pacotes = request.PacotesMotoboy ?? Math.Max(atual.PacotesMotoboy, pacote?.PacotesMotoboyBase ?? 1);
                    if (pacotes < (pacote?.PacotesMotoboyBase ?? 1))
                        throw new SituacaoComercialException(422, "PACOTES_INVALIDOS", "Informe ao menos os pacotes de motoboy incluídos no plano.");
                    depois = SituacoesComerciais.Ativo;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET situacao_comercial = 'ativo', plano_comercial = @Plano, pacotes_motoboy = @Pacotes,
       modulos_ativos = COALESCE(@Modulos::modulo_enum[], modulos_ativos),
       operacao_iniciada_em = COALESCE(operacao_iniciada_em, NOW()),
       bloqueado_em = NULL, bloqueado_por = NULL, bloqueado_motivo = NULL
 WHERE id = @Id;", new { Id = estabelecimentoId, Plano = plano, Pacotes = pacotes, Modulos = pacote?.ModulosAtivos }, tx, cancellationToken: ct));
                    break;

                case "bloquear":
                    if (antes == SituacoesComerciais.Bloqueado)
                        throw new SituacaoComercialException(409, "JA_BLOQUEADO", "O estabelecimento ja esta bloqueado.");
                    if (motivo == null)
                        throw new SituacaoComercialException(422, "MOTIVO_OBRIGATORIO", "Informe o motivo do bloqueio.");
                    depois = SituacoesComerciais.Bloqueado;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET situacao_comercial = 'bloqueado', bloqueado_em = NOW(), bloqueado_por = @Usuario, bloqueado_motivo = @Motivo
 WHERE id = @Id;", new { Id = estabelecimentoId, Usuario = usuarioId, Motivo = motivo }, tx, cancellationToken: ct));
                    break;

                case "desbloquear":
                    if (antes != SituacoesComerciais.Bloqueado)
                        throw new SituacaoComercialException(409, "NAO_BLOQUEADO", "O estabelecimento nao esta bloqueado.");
                    // Volta para onde estava antes do ultimo bloqueio (teste ou ativo).
                    var anterior = await connection.ExecuteScalarAsync<string?>(new CommandDefinition(@"
SELECT situacao_antes FROM estabelecimento_situacao_log
 WHERE estabelecimento_id = @Id AND acao = 'bloquear'
 ORDER BY criado_em DESC, id DESC LIMIT 1;", new { Id = estabelecimentoId }, tx, cancellationToken: ct));
                    depois = anterior is SituacoesComerciais.Teste or SituacoesComerciais.Implantacao ? anterior : SituacoesComerciais.Ativo;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET situacao_comercial = @Depois, bloqueado_em = NULL, bloqueado_por = NULL, bloqueado_motivo = NULL
 WHERE id = @Id;", new { Id = estabelecimentoId, Depois = depois }, tx, cancellationToken: ct));
                    break;

                default:
                    throw new SituacaoComercialException(422, "ACAO_INVALIDA", "Acao invalida. Use iniciar_operacao, ativar, bloquear ou desbloquear.");
            }

            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO estabelecimento_situacao_log (estabelecimento_id, acao, situacao_antes, situacao_depois, motivo, usuario_id, detalhes)
VALUES (@Id, @Acao, @Antes, @Depois, @Motivo, @Usuario, CAST(@Detalhes AS jsonb));",
                new
                {
                    Id = estabelecimentoId, Acao = acao, Antes = antes, Depois = depois, Motivo = motivo, Usuario = usuarioId,
                    Detalhes = JsonSerializer.Serialize(new { request.Plano, request.PacotesMotoboy })
                }, tx, cancellationToken: ct));

            var resultado = await LerAsync(connection, tx, estabelecimentoId, ct)
                ?? throw new SituacaoComercialException(404, "ESTABELECIMENTO_NAO_ENCONTRADO", "Estabelecimento nao encontrado.");
            await tx.CommitAsync(ct);
            _cache.Remove(Key(estabelecimentoId));
            return resultado;
        }

        public async Task<IReadOnlyList<TesteVencidoDto>> ListarTestesVencidosAsync(CancellationToken ct = default)
        {
            try
            {
                await using var connection = await _dataSource.OpenConnectionAsync(ct);
                var rows = await connection.QueryAsync<(Guid Id, string Nome, string? Empresa, DateTime? Fim)>(new CommandDefinition(@"
SELECT e.id, e.nome_fantasia, emp.nome_fantasia, e.teste_termina_em
  FROM estabelecimentos e
  LEFT JOIN empresas emp ON emp.id = e.id_empresa
 WHERE e.situacao_comercial = 'teste' AND e.teste_termina_em IS NOT NULL AND e.teste_termina_em < NOW()
 ORDER BY e.teste_termina_em;", cancellationToken: ct));
                return rows.Select(r => new TesteVencidoDto(r.Id, r.Nome, r.Empresa, Utc(r.Fim))).ToList();
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedColumn)
            {
                return Array.Empty<TesteVencidoDto>();
            }
        }

        public async Task<IReadOnlyList<SituacaoComercialHistoricoDto>> ListarHistoricoAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<HistoricoRow>(new CommandDefinition(@"
SELECT id, acao, situacao_antes AS SituacaoAntes, situacao_depois AS SituacaoDepois,
       motivo, usuario_id AS UsuarioId, criado_em AS CriadoEm
  FROM estabelecimento_situacao_log
 WHERE estabelecimento_id = @EstabelecimentoId
 ORDER BY criado_em DESC, id DESC
 LIMIT 100;", new { EstabelecimentoId = estabelecimentoId }, cancellationToken: ct));
            return rows.Select(row => new SituacaoComercialHistoricoDto(
                row.Id, row.Acao, row.SituacaoAntes, row.SituacaoDepois,
                row.Motivo, row.UsuarioId,
                new DateTimeOffset(DateTime.SpecifyKind(row.CriadoEm, DateTimeKind.Utc)))).ToList();
        }

        private sealed class HistoricoRow
        {
            public long Id { get; set; }
            public string Acao { get; set; } = string.Empty;
            public string? SituacaoAntes { get; set; }
            public string SituacaoDepois { get; set; } = string.Empty;
            public string? Motivo { get; set; }
            public int? UsuarioId { get; set; }
            public DateTime CriadoEm { get; set; }
        }

        private sealed class Row
        {
            public Guid Id { get; set; }
            public string? SituacaoComercial { get; set; }
            public DateTime? OperacaoIniciadaEm { get; set; }
            public DateTime? TesteTerminaEm { get; set; }
            public string? PlanoComercial { get; set; }
            public int PacotesMotoboy { get; set; }
            public DateTime? BloqueadoEm { get; set; }
            public string? BloqueadoMotivo { get; set; }
        }

        private static async Task<SituacaoComercialDto?> LerAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid id, CancellationToken ct, bool forUpdate = false)
        {
            var sql = @"
SELECT id, situacao_comercial AS SituacaoComercial, operacao_iniciada_em AS OperacaoIniciadaEm, teste_termina_em AS TesteTerminaEm,
       plano_comercial AS PlanoComercial, pacotes_motoboy AS PacotesMotoboy, bloqueado_em AS BloqueadoEm, bloqueado_motivo AS BloqueadoMotivo
  FROM estabelecimentos WHERE id = @Id" + (forUpdate ? " FOR UPDATE" : string.Empty) + ";";
            var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { Id = id }, tx, cancellationToken: ct));
            if (row == null) return null;
            var pacotes = Math.Max(1, row.PacotesMotoboy);
            return new SituacaoComercialDto(
                row.Id,
                string.IsNullOrWhiteSpace(row.SituacaoComercial) ? SituacoesComerciais.Ativo : row.SituacaoComercial,
                Utc(row.OperacaoIniciadaEm),
                Utc(row.TesteTerminaEm),
                row.PlanoComercial,
                pacotes,
                MotoboyLimites.VagasPorDia(pacotes),
                MotoboyLimites.Vinculados(pacotes),
                Utc(row.BloqueadoEm),
                row.BloqueadoMotivo);
        }

        private static DateTimeOffset? Utc(DateTime? value) =>
            value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }
}
