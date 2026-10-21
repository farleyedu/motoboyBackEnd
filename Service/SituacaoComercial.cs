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
    /// Limites de motoboy (planos v2): contratados = base do plano (Turbo 3, Frota 5) + pacotes de 3 ou 7 vigentes
    /// (mensal = 30 dias, anual = 365). Vagas por dia operacional = contratados; vinculados = contratados + 7, ate 30.
    /// No teste a loja tem ao menos 3, porque tudo fica liberado. Pacote vencido so deixa de contar: nada roda sozinho.
    /// </summary>
    public static class MotoboyLimites
    {
        public const int VinculadosTeto = 30;
        public const int VinculosFolga = 7;
        public const int BaseTeste = 3;

        public static int VagasPorDia(int contratados) => Math.Max(0, contratados);

        public static int Vinculados(int contratados) =>
            contratados <= 0 ? 0 : Math.Min(VinculadosTeto, contratados + VinculosFolga);

        public static bool QuantidadeValida(int quantidade) => quantidade is 3 or 7;

        public static int DiasDoPacote(string periodicidade) => periodicidade == "anual" ? 365 : 30;

        /// <summary>Motoboys contratados agora, em SQL. Exige a tabela estabelecimentos com o alias "e".</summary>
        public const string SqlContratados = @"(CASE WHEN e.situacao_comercial = 'teste' THEN GREATEST(e.motoboys_base, 3) ELSE e.motoboys_base END
 + COALESCE((SELECT SUM(mp.quantidade) FROM motoboy_pacotes mp
             WHERE mp.estabelecimento_id = e.id AND mp.cancelado_em IS NULL
               AND mp.inicio_utc <= NOW() AND mp.fim_utc > NOW()), 0))::int";
    }

    public sealed record SituacaoComercialDto(
        Guid EstabelecimentoId,
        string Situacao,
        DateTimeOffset? OperacaoIniciadaEm,
        DateTimeOffset? TesteTerminaEm,
        string? Plano,
        int MotoboysBase,
        int MotoboysContratados,
        int VagasPorDia,
        int VinculadosMaximo,
        DateTimeOffset? BloqueadoEm,
        string? BloqueadoMotivo);

    /// <summary>acao: iniciar_operacao | ativar | bloquear | desbloquear.</summary>
    public sealed record AcaoSituacaoRequest(string? Acao, string? Motivo, string? Plano, int? MotoboysBase);

    public sealed record MotoboyPacoteDto(
        long Id, int Quantidade, string Periodicidade, DateTimeOffset InicioUtc, DateTimeOffset FimUtc,
        bool Vigente, DateTimeOffset? CanceladoEm);

    /// <summary>periodicidade: mensal (30 dias) | anual (365 dias). Sem inicio, vale a partir de agora.</summary>
    public sealed record LancarMotoboyPacoteRequest(int Quantidade, string? Periodicidade, DateTimeOffset? InicioUtc);

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
        Task<IReadOnlyList<MotoboyPacoteDto>> ListarMotoboyPacotesAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<MotoboyPacoteDto> LancarMotoboyPacoteAsync(Guid estabelecimentoId, LancarMotoboyPacoteRequest request, int? usuarioId, CancellationToken ct = default);
        Task CancelarMotoboyPacoteAsync(Guid estabelecimentoId, long pacoteId, int? usuarioId, CancellationToken ct = default);
    }

    public sealed class SituacaoComercialService : ISituacaoComercialService
    {
        private sealed class PacoteRow
        {
            public string[] ModulosAtivos { get; set; } = Array.Empty<string>();
            public int MotoboysBase { get; set; }
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
SELECT modulos_ativos::text[] AS ModulosAtivos, motoboys_base AS MotoboysBase
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
                    // Sem valor informado vale a base do plano escolhido (Turbo 3, Frota 5); "personalizado" mantem a atual.
                    var motoboysBase = request.MotoboysBase ?? pacote?.MotoboysBase ?? atual.MotoboysBase;
                    if (motoboysBase < 0)
                        throw new SituacaoComercialException(422, "MOTOBOYS_INVALIDOS", "A base de motoboys não pode ser negativa.");
                    depois = SituacoesComerciais.Ativo;
                    await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE estabelecimentos
   SET situacao_comercial = 'ativo', plano_comercial = @Plano, motoboys_base = @MotoboysBase,
       modulos_ativos = COALESCE(@Modulos::modulo_enum[], modulos_ativos),
       operacao_iniciada_em = COALESCE(operacao_iniciada_em, NOW()),
       bloqueado_em = NULL, bloqueado_por = NULL, bloqueado_motivo = NULL
 WHERE id = @Id;", new { Id = estabelecimentoId, Plano = plano, MotoboysBase = motoboysBase, Modulos = pacote?.ModulosAtivos }, tx, cancellationToken: ct));
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
                    Detalhes = JsonSerializer.Serialize(new { request.Plano, request.MotoboysBase })
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
            public int MotoboysBase { get; set; }
            public int MotoboysContratados { get; set; }
            public DateTime? BloqueadoEm { get; set; }
            public string? BloqueadoMotivo { get; set; }
        }

        private static async Task<SituacaoComercialDto?> LerAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, Guid id, CancellationToken ct, bool forUpdate = false)
        {
            // A trava vem antes, separada: a leitura tem a soma dos pacotes numa subconsulta.
            if (forUpdate)
                await connection.ExecuteAsync(new CommandDefinition(
                    "SELECT 1 FROM estabelecimentos WHERE id = @Id FOR UPDATE;", new { Id = id }, tx, cancellationToken: ct));
            var sql = @"
SELECT e.id, e.situacao_comercial AS SituacaoComercial, e.operacao_iniciada_em AS OperacaoIniciadaEm, e.teste_termina_em AS TesteTerminaEm,
       e.plano_comercial AS PlanoComercial, e.motoboys_base AS MotoboysBase, " + MotoboyLimites.SqlContratados + @" AS MotoboysContratados,
       e.bloqueado_em AS BloqueadoEm, e.bloqueado_motivo AS BloqueadoMotivo
  FROM estabelecimentos e WHERE e.id = @Id;";
            var row = await connection.QuerySingleOrDefaultAsync<Row>(new CommandDefinition(sql, new { Id = id }, tx, cancellationToken: ct));
            if (row == null) return null;
            return new SituacaoComercialDto(
                row.Id,
                string.IsNullOrWhiteSpace(row.SituacaoComercial) ? SituacoesComerciais.Ativo : row.SituacaoComercial,
                Utc(row.OperacaoIniciadaEm),
                Utc(row.TesteTerminaEm),
                row.PlanoComercial,
                row.MotoboysBase,
                row.MotoboysContratados,
                MotoboyLimites.VagasPorDia(row.MotoboysContratados),
                MotoboyLimites.Vinculados(row.MotoboysContratados),
                Utc(row.BloqueadoEm),
                row.BloqueadoMotivo);
        }

        public async Task<IReadOnlyList<MotoboyPacoteDto>> ListarMotoboyPacotesAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var rows = await connection.QueryAsync<MotoboyPacoteRow>(new CommandDefinition(MotoboyPacoteSelect + @"
 WHERE estabelecimento_id = @EstabelecimentoId
 ORDER BY (cancelado_em IS NULL AND fim_utc > NOW()) DESC, fim_utc DESC
 LIMIT 50;", new { EstabelecimentoId = estabelecimentoId }, cancellationToken: ct));
            return rows.Select(ToDto).ToList();
        }

        public async Task<MotoboyPacoteDto> LancarMotoboyPacoteAsync(Guid estabelecimentoId, LancarMotoboyPacoteRequest request, int? usuarioId, CancellationToken ct = default)
        {
            if (!MotoboyLimites.QuantidadeValida(request.Quantidade))
                throw new SituacaoComercialException(422, "PACOTE_INVALIDO", "O pacote de motoboys é de 3 ou de 7.");
            var periodicidade = (request.Periodicidade ?? "mensal").Trim().ToLowerInvariant();
            if (periodicidade is not ("mensal" or "anual"))
                throw new SituacaoComercialException(422, "PERIODICIDADE_INVALIDA", "Escolha mensal ou anual.");
            var inicio = request.InicioUtc ?? DateTimeOffset.UtcNow;
            var fim = inicio.AddDays(MotoboyLimites.DiasDoPacote(periodicidade));

            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var existe = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                "SELECT EXISTS (SELECT 1 FROM estabelecimentos WHERE id = @Id);", new { Id = estabelecimentoId }, cancellationToken: ct));
            if (!existe) throw new SituacaoComercialException(404, "ESTABELECIMENTO_NAO_ENCONTRADO", "Estabelecimento nao encontrado.");
            var row = await connection.QuerySingleAsync<MotoboyPacoteRow>(new CommandDefinition(@"
INSERT INTO motoboy_pacotes (estabelecimento_id, quantidade, periodicidade, inicio_utc, fim_utc, lancado_por)
VALUES (@EstabelecimentoId, @Quantidade, @Periodicidade, @Inicio, @Fim, @Usuario)
RETURNING " + MotoboyPacoteColumns + ";",
                new { EstabelecimentoId = estabelecimentoId, request.Quantidade, Periodicidade = periodicidade, Inicio = inicio, Fim = fim, Usuario = usuarioId },
                cancellationToken: ct));
            _cache.Remove(Key(estabelecimentoId));
            return ToDto(row);
        }

        public async Task CancelarMotoboyPacoteAsync(Guid estabelecimentoId, long pacoteId, int? usuarioId, CancellationToken ct = default)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var updated = await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE motoboy_pacotes SET cancelado_em = NOW(), cancelado_por = @Usuario
 WHERE id = @Id AND estabelecimento_id = @EstabelecimentoId AND cancelado_em IS NULL;",
                new { Id = pacoteId, EstabelecimentoId = estabelecimentoId, Usuario = usuarioId }, cancellationToken: ct));
            if (updated == 0)
                throw new SituacaoComercialException(404, "PACOTE_NAO_ENCONTRADO", "Pacote de motoboys não encontrado ou já cancelado.");
            _cache.Remove(Key(estabelecimentoId));
        }

        private const string MotoboyPacoteColumns =
            "id, quantidade, periodicidade, inicio_utc AS InicioUtc, fim_utc AS FimUtc, cancelado_em AS CanceladoEm";
        private const string MotoboyPacoteSelect = "SELECT " + MotoboyPacoteColumns + " FROM motoboy_pacotes";

        private sealed class MotoboyPacoteRow
        {
            public long Id { get; set; }
            public int Quantidade { get; set; }
            public string Periodicidade { get; set; } = string.Empty;
            public DateTime InicioUtc { get; set; }
            public DateTime FimUtc { get; set; }
            public DateTime? CanceladoEm { get; set; }
        }

        private static MotoboyPacoteDto ToDto(MotoboyPacoteRow row)
        {
            var inicio = Utc(row.InicioUtc)!.Value;
            var fim = Utc(row.FimUtc)!.Value;
            var agora = DateTimeOffset.UtcNow;
            return new MotoboyPacoteDto(row.Id, row.Quantidade, row.Periodicidade, inicio, fim,
                row.CanceladoEm == null && inicio <= agora && fim > agora, Utc(row.CanceladoEm));
        }

        private static DateTimeOffset? Utc(DateTime? value) =>
            value is null ? null : new DateTimeOffset(DateTime.SpecifyKind(value.Value, DateTimeKind.Utc));
    }
}
