using Dapper;
using Npgsql;

namespace APIBack.Service
{
    /// <summary>Uma loja na carteira da Gestao: situacao, contrato, cobranca e uso, numa linha so.</summary>
    public sealed class CarteiraLojaDto
    {
        public Guid Id { get; set; }
        public string Nome { get; set; } = string.Empty;
        public Guid? EmpresaId { get; set; }
        public string? Empresa { get; set; }
        public string? Logo { get; set; }
        public string Situacao { get; set; } = "ativo";
        public string? Plano { get; set; }
        public string? StatusCadastro { get; set; }
        public DateTimeOffset? CriadaEm { get; set; }
        public DateTimeOffset? OperacaoIniciadaEm { get; set; }
        public DateTimeOffset? TesteTerminaEm { get; set; }
        public string? BloqueadoMotivo { get; set; }
        public string[] Modulos { get; set; } = Array.Empty<string>();
        public int MotoboysContratados { get; set; }
        public int Pedidos7d { get; set; }
        public int Pedidos28d { get; set; }
        public DateTimeOffset? UltimoPedidoEm { get; set; }
        /// <summary>Contratos do CRM ainda valendo (nao cancelados) desta loja.</summary>
        public int Contratos { get; set; }
        public string? ContratoStatus { get; set; }
        /// <summary>Mensalidade SaaS + marketing dos contratos valendo. Nulo = sem contrato no CRM.</summary>
        public decimal? Mensalidade { get; set; }
        public decimal? MensalidadeSaas { get; set; }
        public int? DiaVencimento { get; set; }
        public decimal ValorAtrasado { get; set; }
        public DateTime? AtrasadoDesde { get; set; }
        public DateTime? ProximoVencimento { get; set; }
        public DateTimeOffset? UltimoPagamentoEm { get; set; }
        public int NotasAbertas { get; set; }
    }

    public sealed class DinheiroTipoDto
    {
        public string Tipo { get; set; } = string.Empty;
        public decimal Valor { get; set; }
    }

    public sealed class DinheiroMesDto
    {
        public string Mes { get; set; } = string.Empty;
        public decimal Entrou { get; set; }
        public decimal AReceber { get; set; }
        public decimal Atrasado { get; set; }
        public decimal Saiu { get; set; }
        public decimal APagar { get; set; }
        /// <summary>Soma das mensalidades dos contratos ativos ou inadimplentes do CRM.</summary>
        public decimal ReceitaRecorrente { get; set; }
        public decimal Novas { get; set; }
        public decimal Perdidas { get; set; }
        public List<DinheiroTipoDto> EntrouPorTipo { get; set; } = new();
        public List<DinheiroTipoDto> SaiuPorCategoria { get; set; } = new();
    }

    public sealed class ZippyContaDto
    {
        public long Id { get; set; }
        public string Descricao { get; set; } = string.Empty;
        public string Categoria { get; set; } = "outros";
        public decimal Valor { get; set; }
        public bool Estimado { get; set; }
        public DateTime Vencimento { get; set; }
        public string Recorrencia { get; set; } = "unica";
        public int? ResponsavelId { get; set; }
        public string? Responsavel { get; set; }
        public string Status { get; set; } = "a_pagar";
        public decimal? ValorPago { get; set; }
        public DateTimeOffset? PagoEm { get; set; }
        public string? PagoPor { get; set; }
        public string? Observacao { get; set; }
    }

    public sealed record SalvarContaRequest(string? Descricao, string? Categoria, decimal Valor, bool Estimado, DateTime Vencimento,
        string? Recorrencia, int? ResponsavelId, string? Observacao);
    public sealed record PagarContaRequest(decimal? ValorPago);

    public sealed class PendenciaEstadoDto
    {
        public string Chave { get; set; } = string.Empty;
        public DateTimeOffset? AdiadoAte { get; set; }
        public string? DispensadoMotivo { get; set; }
    }

    public sealed record AdiarPendenciaRequest(DateTimeOffset Ate);
    public sealed record DispensarPendenciaRequest(string? Motivo);

    public sealed class GestaoNotaDto
    {
        public long Id { get; set; }
        public string Texto { get; set; } = string.Empty;
        public DateTime? LembrarEm { get; set; }
        public DateTimeOffset? ConcluidaEm { get; set; }
        public string? Autor { get; set; }
        public DateTimeOffset CriadoEm { get; set; }
    }

    public sealed record SalvarNotaRequest(string? Texto, DateTime? LembrarEm);

    public sealed class LinhaDoTempoItemDto
    {
        public DateTimeOffset Em { get; set; }
        /// <summary>situacao | cobranca | nota | gestao</summary>
        public string Tipo { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string? Detalhe { get; set; }
        public string? Autor { get; set; }
    }

    public sealed class ResponsavelDto
    {
        public int Id { get; set; }
        public string Nome { get; set; } = string.Empty;
    }

    public sealed class UltimoAcessoDto
    {
        public int UsuarioId { get; set; }
        public DateTimeOffset? UltimoAcesso { get; set; }
    }

    public sealed class LembreteDto
    {
        public long Id { get; set; }
        public Guid EstabelecimentoId { get; set; }
        public string Loja { get; set; } = string.Empty;
        public string Texto { get; set; } = string.Empty;
        public DateTime LembrarEm { get; set; }
        public string? Autor { get; set; }
    }

    public sealed class MotoboyDaLojaDto
    {
        public int MotoboyId { get; set; }
        public string Nome { get; set; } = string.Empty;
        public string? Telefone { get; set; }
        public bool Ativo { get; set; }
        public DateTimeOffset VinculadoEm { get; set; }
        public DateTimeOffset? EncerradoEm { get; set; }
        public string? MotivoEncerramento { get; set; }
        public int Entregas7d { get; set; }
        public DateTimeOffset? UltimaEntregaEm { get; set; }
    }

    public sealed class GestaoCentralException(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    public interface IGestaoCentralService
    {
        Task<IReadOnlyList<CarteiraLojaDto>> CarteiraAsync(CancellationToken ct = default);
        Task<DinheiroMesDto> DinheiroAsync(DateOnly mes, CancellationToken ct = default);
        Task<IReadOnlyList<ZippyContaDto>> ContasAsync(DateOnly mes, CancellationToken ct = default);
        Task<ZippyContaDto> CriarContaAsync(SalvarContaRequest request, int usuarioId, CancellationToken ct = default);
        Task<ZippyContaDto> AtualizarContaAsync(long id, SalvarContaRequest request, int usuarioId, CancellationToken ct = default);
        Task<ZippyContaDto> PagarContaAsync(long id, PagarContaRequest request, int usuarioId, CancellationToken ct = default);
        Task CancelarContaAsync(long id, int usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<ResponsavelDto>> ResponsaveisAsync(CancellationToken ct = default);
        Task<IReadOnlyList<PendenciaEstadoDto>> PendenciasEstadoAsync(CancellationToken ct = default);
        Task AdiarAsync(string chave, DateTimeOffset ate, int usuarioId, CancellationToken ct = default);
        Task DispensarAsync(string chave, string motivo, int usuarioId, CancellationToken ct = default);
        Task ReabrirAsync(string chave, CancellationToken ct = default);
        Task<IReadOnlyList<GestaoNotaDto>> NotasAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<GestaoNotaDto> CriarNotaAsync(Guid estabelecimentoId, SalvarNotaRequest request, int usuarioId, CancellationToken ct = default);
        Task ConcluirNotaAsync(Guid estabelecimentoId, long notaId, int usuarioId, CancellationToken ct = default);
        Task<IReadOnlyList<LinhaDoTempoItemDto>> LinhaDoTempoAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task<IReadOnlyList<UltimoAcessoDto>> UltimosAcessosAsync(CancellationToken ct = default);
        Task<IReadOnlyList<LembreteDto>> LembretesAsync(CancellationToken ct = default);
        Task<IReadOnlyList<MotoboyDaLojaDto>> MotoboysDaLojaAsync(Guid estabelecimentoId, CancellationToken ct = default);
        Task RegistrarEntradaAsync(Guid estabelecimentoId, int usuarioId, CancellationToken ct = default);
    }

    /// <summary>
    /// Leitura e escrita da Gestao central (so super admin). A carteira junta, em uma consulta, a situacao comercial,
    /// os pedidos dos ultimos 28 dias (sem simulador nem cancelados), os contratos e lancamentos do CRM e as notas abertas.
    /// </summary>
    public sealed class GestaoCentralService(NpgsqlDataSource dataSource, TimeProvider? clock = null) : IGestaoCentralService
    {
        private readonly TimeProvider _clock = clock ?? TimeProvider.System;
        private static readonly string[] Recorrencias = ["unica", "mensal", "anual"];

        private DateOnly Hoje => DateOnly.FromDateTime(_clock.GetUtcNow().ToOffset(TimeSpan.FromHours(-3)).DateTime);

        public async Task<IReadOnlyList<CarteiraLojaDto>> CarteiraAsync(CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var sql = @"
SELECT e.id, e.nome_fantasia AS Nome, e.id_empresa AS EmpresaId, emp.nome_fantasia AS Empresa, e.url_logo AS Logo,
       COALESCE(e.situacao_comercial, 'ativo') AS Situacao, e.plano_comercial AS Plano, e.status AS StatusCadastro,
       e.created_at AS CriadaEm, e.operacao_iniciada_em AS OperacaoIniciadaEm, e.teste_termina_em AS TesteTerminaEm,
       e.bloqueado_motivo AS BloqueadoMotivo, COALESCE(e.modulos_ativos::text[], ARRAY[]::text[]) AS Modulos,
       " + MotoboyLimites.SqlContratados + @" AS MotoboysContratados,
       COALESCE(ped.p7, 0)::int AS Pedidos7d, COALESCE(ped.p28, 0)::int AS Pedidos28d, ped.ultimo AS UltimoPedidoEm,
       COALESCE(ct.contratos, 0)::int AS Contratos, ct.status AS ContratoStatus, ct.mensal AS Mensalidade, ct.saas AS MensalidadeSaas,
       ct.dia AS DiaVencimento,
       COALESCE(fin.atrasado, 0) AS ValorAtrasado, fin.desde AS AtrasadoDesde, fin.proximo AS ProximoVencimento,
       fin.ultimo_pago AS UltimoPagamentoEm,
       COALESCE(nt.abertas, 0)::int AS NotasAbertas
  FROM estabelecimentos e
  LEFT JOIN empresas emp ON emp.id = e.id_empresa
  LEFT JOIN LATERAL (
       SELECT COUNT(*) FILTER (WHERE p.data_pedido >= NOW() - INTERVAL '7 days') AS p7,
              COUNT(*) AS p28, MAX(p.data_pedido) AS ultimo
         FROM pedido p
        WHERE p.id_estabelecimento = e.id AND p.data_pedido >= NOW() - INTERVAL '28 days'
          AND COALESCE(p.origem, '') <> 'simulador' AND COALESCE(p.status_pedido, 1) <> 6) ped ON TRUE
  LEFT JOIN LATERAL (
       SELECT COUNT(*) AS contratos, MIN(c.status::text) AS status,
              SUM(c.mensalidade_saas + c.mensalidade_marketing) AS mensal, SUM(c.mensalidade_saas) AS saas,
              MIN(c.dia_vencimento) AS dia
         FROM crm_contrato c
        WHERE c.estabelecimento_id = e.id AND c.status <> 'cancelado') ct ON TRUE
  LEFT JOIN LATERAL (
       SELECT SUM(l.valor_total - l.valor_pago) FILTER (WHERE l.status IN ('pendente', 'parcial') AND l.data_vencimento < @Hoje) AS atrasado,
              MIN(l.data_vencimento) FILTER (WHERE l.status IN ('pendente', 'parcial') AND l.data_vencimento < @Hoje) AS desde,
              MIN(l.data_vencimento) FILTER (WHERE l.status IN ('pendente', 'parcial') AND l.data_vencimento >= @Hoje) AS proximo,
              MAX(l.data_pagamento) FILTER (WHERE l.status IN ('pago', 'parcial')) AS ultimo_pago
         FROM crm_lancamento l
         JOIN crm_contrato c ON c.id = l.contrato_id
        WHERE c.estabelecimento_id = e.id) fin ON TRUE
  LEFT JOIN LATERAL (
       SELECT COUNT(*) AS abertas FROM gestao_nota n WHERE n.estabelecimento_id = e.id AND n.concluida_em IS NULL) nt ON TRUE
 ORDER BY e.nome_fantasia;";
            var rows = await connection.QueryAsync<CarteiraLojaDto>(new CommandDefinition(sql, new { Hoje = Hoje.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            return rows.ToList();
        }

        public async Task<DinheiroMesDto> DinheiroAsync(DateOnly mes, CancellationToken ct = default)
        {
            var inicio = new DateOnly(mes.Year, mes.Month, 1);
            var fim = inicio.AddMonths(1);
            var args = new { Inicio = inicio.ToDateTime(TimeOnly.MinValue), Fim = fim.ToDateTime(TimeOnly.MinValue), Hoje = Hoje.ToDateTime(TimeOnly.MinValue) };
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var dto = await connection.QuerySingleAsync<DinheiroMesDto>(new CommandDefinition(@"
SELECT
  COALESCE((SELECT SUM(l.valor_pago) FROM crm_lancamento l WHERE l.status IN ('pago', 'parcial')
             AND l.data_pagamento >= @Inicio AND l.data_pagamento < @Fim), 0) AS Entrou,
  COALESCE((SELECT SUM(l.valor_total - l.valor_pago) FROM crm_lancamento l WHERE l.status IN ('pendente', 'parcial')
             AND l.data_vencimento >= GREATEST(@Inicio, @Hoje) AND l.data_vencimento < @Fim), 0) AS AReceber,
  COALESCE((SELECT SUM(l.valor_total - l.valor_pago) FROM crm_lancamento l WHERE l.status IN ('pendente', 'parcial')
             AND l.data_vencimento < LEAST(@Hoje, @Fim)), 0) AS Atrasado,
  COALESCE((SELECT SUM(COALESCE(z.valor_pago, z.valor)) FROM zippy_conta z WHERE z.status = 'paga'
             AND z.pago_em >= @Inicio AND z.pago_em < @Fim), 0) AS Saiu,
  COALESCE((SELECT SUM(z.valor) FROM zippy_conta z WHERE z.status = 'a_pagar' AND z.vencimento < @Fim), 0) AS APagar,
  COALESCE((SELECT SUM(c.mensalidade_saas + c.mensalidade_marketing) FROM crm_contrato c
             WHERE c.status IN ('ativo', 'inadimplente')), 0) AS ReceitaRecorrente,
  COALESCE((SELECT SUM(c.mensalidade_saas + c.mensalidade_marketing) FROM crm_contrato c
             WHERE c.status <> 'cancelado' AND c.dt_inicio_cobranca >= @Inicio AND c.dt_inicio_cobranca < @Fim), 0) AS Novas,
  COALESCE((SELECT SUM(c.mensalidade_saas + c.mensalidade_marketing) FROM crm_contrato c
             WHERE c.status = 'cancelado' AND c.updated_at >= @Inicio AND c.updated_at < @Fim), 0) AS Perdidas;",
                args, cancellationToken: ct));
            dto.Mes = inicio.ToString("yyyy-MM");
            dto.EntrouPorTipo = (await connection.QueryAsync<DinheiroTipoDto>(new CommandDefinition(@"
SELECT l.tipo_lancamento AS Tipo, SUM(l.valor_pago) AS Valor FROM crm_lancamento l
 WHERE l.status IN ('pago', 'parcial') AND l.data_pagamento >= @Inicio AND l.data_pagamento < @Fim
 GROUP BY l.tipo_lancamento ORDER BY 2 DESC;", args, cancellationToken: ct))).ToList();
            dto.SaiuPorCategoria = (await connection.QueryAsync<DinheiroTipoDto>(new CommandDefinition(@"
SELECT z.categoria AS Tipo, SUM(COALESCE(z.valor_pago, z.valor)) AS Valor FROM zippy_conta z
 WHERE z.status = 'paga' AND z.pago_em >= @Inicio AND z.pago_em < @Fim
 GROUP BY z.categoria ORDER BY 2 DESC;", args, cancellationToken: ct))).ToList();
            return dto;
        }

        private const string ContaSelect = @"
SELECT z.id, z.descricao, z.categoria, z.valor, z.estimado, z.vencimento, z.recorrencia, z.responsavel_id AS ResponsavelId,
       r.nome AS Responsavel, z.status, z.valor_pago AS ValorPago, z.pago_em AS PagoEm, p.nome AS PagoPor, z.observacao
  FROM zippy_conta z
  LEFT JOIN usuario r ON r.id = z.responsavel_id
  LEFT JOIN usuario p ON p.id = z.pago_por";

        public async Task<IReadOnlyList<ZippyContaDto>> ContasAsync(DateOnly mes, CancellationToken ct = default)
        {
            var inicio = new DateOnly(mes.Year, mes.Month, 1);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            // As do mes escolhido e, de qualquer mes, as que ainda estao em aberto e ja venceram.
            var rows = await connection.QueryAsync<ZippyContaDto>(new CommandDefinition(ContaSelect + @"
 WHERE z.status <> 'cancelada'
   AND ((z.vencimento >= @Inicio AND z.vencimento < @Fim) OR (z.status = 'a_pagar' AND z.vencimento < @Fim))
 ORDER BY (z.status = 'paga'), z.vencimento, z.id;",
                new { Inicio = inicio.ToDateTime(TimeOnly.MinValue), Fim = inicio.AddMonths(1).ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct));
            return rows.ToList();
        }

        private static (string Descricao, string Categoria, string Recorrencia) Validar(SalvarContaRequest request)
        {
            var descricao = request.Descricao?.Trim();
            if (string.IsNullOrEmpty(descricao)) throw new GestaoCentralException(422, "Diga que conta e esta.");
            if (request.Valor < 0) throw new GestaoCentralException(422, "O valor nao pode ser negativo.");
            var recorrencia = string.IsNullOrWhiteSpace(request.Recorrencia) ? "unica" : request.Recorrencia.Trim();
            if (!Recorrencias.Contains(recorrencia)) throw new GestaoCentralException(422, "Recorrencia invalida.");
            var categoria = string.IsNullOrWhiteSpace(request.Categoria) ? "outros" : request.Categoria.Trim().ToLowerInvariant();
            return (descricao, categoria, recorrencia);
        }

        private static async Task<ZippyContaDto> LerContaAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, long id, CancellationToken ct) =>
            await connection.QuerySingleOrDefaultAsync<ZippyContaDto>(new CommandDefinition(ContaSelect + " WHERE z.id = @Id;", new { Id = id }, tx, cancellationToken: ct))
            ?? throw new GestaoCentralException(404, "Conta nao encontrada.");

        private static Task LogAsync(NpgsqlConnection connection, NpgsqlTransaction? tx, int usuarioId, Guid? loja, string acao, string? detalhe, CancellationToken ct) =>
            connection.ExecuteAsync(new CommandDefinition(
                "INSERT INTO gestao_acao_log (usuario_id, estabelecimento_id, acao, detalhe) VALUES (@Usuario, @Loja, @Acao, @Detalhe);",
                new { Usuario = usuarioId, Loja = loja, Acao = acao, Detalhe = detalhe }, tx, cancellationToken: ct));

        public async Task<ZippyContaDto> CriarContaAsync(SalvarContaRequest request, int usuarioId, CancellationToken ct = default)
        {
            var (descricao, categoria, recorrencia) = Validar(request);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var id = await connection.ExecuteScalarAsync<long>(new CommandDefinition(@"
INSERT INTO zippy_conta (descricao, categoria, valor, estimado, vencimento, recorrencia, responsavel_id, observacao, criado_por)
VALUES (@Descricao, @Categoria, @Valor, @Estimado, @Vencimento, @Recorrencia, @ResponsavelId, @Observacao, @Usuario) RETURNING id;",
                new { Descricao = descricao, Categoria = categoria, request.Valor, request.Estimado, Vencimento = request.Vencimento.Date,
                      Recorrencia = recorrencia, request.ResponsavelId, Observacao = request.Observacao?.Trim(), Usuario = usuarioId }, tx, cancellationToken: ct));
            await LogAsync(connection, tx, usuarioId, null, "conta_criada", $"{descricao} · R$ {request.Valor:0.00}", ct);
            var dto = await LerContaAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);
            return dto;
        }

        public async Task<ZippyContaDto> AtualizarContaAsync(long id, SalvarContaRequest request, int usuarioId, CancellationToken ct = default)
        {
            var (descricao, categoria, recorrencia) = Validar(request);
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var antes = await LerContaAsync(connection, tx, id, ct);
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE zippy_conta SET descricao = @Descricao, categoria = @Categoria, valor = @Valor, estimado = @Estimado, vencimento = @Vencimento,
       recorrencia = @Recorrencia, responsavel_id = @ResponsavelId, observacao = @Observacao WHERE id = @Id;",
                new { Id = id, Descricao = descricao, Categoria = categoria, request.Valor, request.Estimado, Vencimento = request.Vencimento.Date,
                      Recorrencia = recorrencia, request.ResponsavelId, Observacao = request.Observacao?.Trim() }, tx, cancellationToken: ct));
            await LogAsync(connection, tx, usuarioId, null, "conta_alterada", $"{antes.Descricao}: R$ {antes.Valor:0.00} → R$ {request.Valor:0.00}", ct);
            var dto = await LerContaAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);
            return dto;
        }

        public async Task<ZippyContaDto> PagarContaAsync(long id, PagarContaRequest request, int usuarioId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("SELECT 1 FROM zippy_conta WHERE id = @Id FOR UPDATE;", new { Id = id }, tx, cancellationToken: ct));
            var conta = await LerContaAsync(connection, tx, id, ct);
            if (conta.Status != "a_pagar") throw new GestaoCentralException(409, "Esta conta ja foi paga ou cancelada.");
            var valor = request.ValorPago ?? conta.Valor;
            if (valor < 0) throw new GestaoCentralException(422, "O valor nao pode ser negativo.");
            await connection.ExecuteAsync(new CommandDefinition(@"
UPDATE zippy_conta SET status = 'paga', valor_pago = @Valor, pago_em = NOW(), pago_por = @Usuario, estimado = FALSE WHERE id = @Id;",
                new { Id = id, Valor = valor, Usuario = usuarioId }, tx, cancellationToken: ct));
            if (conta.Recorrencia != "unica")
            {
                // A proxima nasce com o valor pago agora, marcada como estimada ate alguem lancar o valor real.
                var proximo = conta.Recorrencia == "anual" ? conta.Vencimento.AddYears(1) : conta.Vencimento.AddMonths(1);
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO zippy_conta (descricao, categoria, valor, estimado, vencimento, recorrencia, responsavel_id, observacao, origem_id, criado_por)
SELECT descricao, categoria, @Valor, TRUE, @Vencimento, recorrencia, responsavel_id, observacao, id, @Usuario FROM zippy_conta WHERE id = @Id
   AND NOT EXISTS (SELECT 1 FROM zippy_conta WHERE origem_id = @Id);",
                    new { Id = id, Valor = valor, Vencimento = proximo, Usuario = usuarioId }, tx, cancellationToken: ct));
            }
            await LogAsync(connection, tx, usuarioId, null, "conta_paga", $"{conta.Descricao} · R$ {valor:0.00}", ct);
            var dto = await LerContaAsync(connection, tx, id, ct);
            await tx.CommitAsync(ct);
            return dto;
        }

        public async Task CancelarContaAsync(long id, int usuarioId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            var conta = await LerContaAsync(connection, tx, id, ct);
            if (conta.Status == "paga") throw new GestaoCentralException(409, "Conta paga nao pode ser cancelada.");
            await connection.ExecuteAsync(new CommandDefinition("UPDATE zippy_conta SET status = 'cancelada' WHERE id = @Id;", new { Id = id }, tx, cancellationToken: ct));
            await LogAsync(connection, tx, usuarioId, null, "conta_cancelada", conta.Descricao, ct);
            await tx.CommitAsync(ct);
        }

        public async Task<IReadOnlyList<ResponsavelDto>> ResponsaveisAsync(CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<ResponsavelDto>(new CommandDefinition(
                "SELECT u.id, u.nome FROM usuario u WHERE u.is_super_admin = TRUE ORDER BY u.nome;", cancellationToken: ct))).ToList();
        }

        public async Task<IReadOnlyList<PendenciaEstadoDto>> PendenciasEstadoAsync(CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<PendenciaEstadoDto>(new CommandDefinition(@"
SELECT chave, adiado_ate AS AdiadoAte, dispensado_motivo AS DispensadoMotivo FROM gestao_pendencia_estado
 WHERE dispensado_motivo IS NOT NULL OR adiado_ate > NOW();", cancellationToken: ct))).ToList();
        }

        private static string Chave(string chave)
        {
            var value = chave?.Trim() ?? string.Empty;
            if (value.Length is 0 or > 200) throw new GestaoCentralException(422, "Pendencia invalida.");
            return value;
        }

        public async Task AdiarAsync(string chave, DateTimeOffset ate, int usuarioId, CancellationToken ct = default)
        {
            var key = Chave(chave);
            if (ate <= _clock.GetUtcNow()) throw new GestaoCentralException(422, "Escolha uma data no futuro.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO gestao_pendencia_estado (chave, adiado_ate, dispensado_motivo, usuario_id, atualizado_em) VALUES (@Chave, @Ate, NULL, @Usuario, NOW())
ON CONFLICT (chave) DO UPDATE SET adiado_ate = EXCLUDED.adiado_ate, dispensado_motivo = NULL, usuario_id = EXCLUDED.usuario_id, atualizado_em = NOW();",
                new { Chave = key, Ate = ate, Usuario = usuarioId }, cancellationToken: ct));
        }

        public async Task DispensarAsync(string chave, string motivo, int usuarioId, CancellationToken ct = default)
        {
            var key = Chave(chave);
            var texto = motivo?.Trim();
            if (string.IsNullOrEmpty(texto)) throw new GestaoCentralException(422, "Diga por que esta pendencia pode ser ignorada.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO gestao_pendencia_estado (chave, adiado_ate, dispensado_motivo, usuario_id, atualizado_em) VALUES (@Chave, NULL, @Motivo, @Usuario, NOW())
ON CONFLICT (chave) DO UPDATE SET adiado_ate = NULL, dispensado_motivo = EXCLUDED.dispensado_motivo, usuario_id = EXCLUDED.usuario_id, atualizado_em = NOW();",
                new { Chave = key, Motivo = texto, Usuario = usuarioId }, cancellationToken: ct));
        }

        public async Task ReabrirAsync(string chave, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await connection.ExecuteAsync(new CommandDefinition("DELETE FROM gestao_pendencia_estado WHERE chave = @Chave;", new { Chave = Chave(chave) }, cancellationToken: ct));
        }

        public async Task<IReadOnlyList<GestaoNotaDto>> NotasAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<GestaoNotaDto>(new CommandDefinition(@"
SELECT n.id, n.texto, n.lembrar_em AS LembrarEm, n.concluida_em AS ConcluidaEm, u.nome AS Autor, n.criado_em AS CriadoEm
  FROM gestao_nota n LEFT JOIN usuario u ON u.id = n.autor_id
 WHERE n.estabelecimento_id = @Id ORDER BY (n.concluida_em IS NOT NULL), n.criado_em DESC LIMIT 100;",
                new { Id = estabelecimentoId }, cancellationToken: ct))).ToList();
        }

        public async Task<GestaoNotaDto> CriarNotaAsync(Guid estabelecimentoId, SalvarNotaRequest request, int usuarioId, CancellationToken ct = default)
        {
            var texto = request.Texto?.Trim();
            if (string.IsNullOrEmpty(texto)) throw new GestaoCentralException(422, "Escreva a nota.");
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var id = await connection.ExecuteScalarAsync<long?>(new CommandDefinition(@"
INSERT INTO gestao_nota (estabelecimento_id, texto, lembrar_em, autor_id)
SELECT @Loja, @Texto, @Lembrar, @Usuario WHERE EXISTS (SELECT 1 FROM estabelecimentos WHERE id = @Loja) RETURNING id;",
                new { Loja = estabelecimentoId, Texto = texto, Lembrar = request.LembrarEm?.Date, Usuario = usuarioId }, cancellationToken: ct))
                ?? throw new GestaoCentralException(404, "Loja nao encontrada.");
            return (await NotasAsync(estabelecimentoId, ct)).First(nota => nota.Id == id);
        }

        public async Task ConcluirNotaAsync(Guid estabelecimentoId, long notaId, int usuarioId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            var linhas = await connection.ExecuteAsync(new CommandDefinition(
                "UPDATE gestao_nota SET concluida_em = NOW() WHERE id = @Id AND estabelecimento_id = @Loja AND concluida_em IS NULL;",
                new { Id = notaId, Loja = estabelecimentoId }, cancellationToken: ct));
            if (linhas == 0) throw new GestaoCentralException(404, "Nota nao encontrada ou ja concluida.");
        }

        public async Task<IReadOnlyList<LinhaDoTempoItemDto>> LinhaDoTempoAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            // Tudo o que aconteceu com a loja num lugar so: situacao comercial, cobrancas do CRM, notas e acoes da Gestao.
            return (await connection.QueryAsync<LinhaDoTempoItemDto>(new CommandDefinition(@"
SELECT * FROM (
  SELECT s.criado_em AS Em, 'situacao' AS Tipo, s.acao AS Titulo,
         CONCAT_WS(' · ', NULLIF(CONCAT_WS(' → ', s.situacao_antes, s.situacao_depois), ''), s.motivo) AS Detalhe, u.nome AS Autor
    FROM estabelecimento_situacao_log s LEFT JOIN usuario u ON u.id = s.usuario_id
   WHERE s.estabelecimento_id = @Id
  UNION ALL
  SELECT COALESCE(l.data_pagamento::timestamptz, l.data_vencimento::timestamptz), 'cobranca',
         CASE WHEN l.status IN ('pago', 'parcial') THEN 'pago' WHEN l.status = 'cancelado' THEN 'cancelado'
              WHEN l.data_vencimento < CURRENT_DATE THEN 'atrasado' ELSE 'a_vencer' END,
         CONCAT(l.descricao, ' · R$ ', TO_CHAR(CASE WHEN l.status IN ('pago', 'parcial') THEN l.valor_pago ELSE l.valor_total END, 'FM999G990D00')), NULL
    FROM crm_lancamento l JOIN crm_contrato c ON c.id = l.contrato_id
   WHERE c.estabelecimento_id = @Id AND l.data_vencimento <= CURRENT_DATE + 40
  UNION ALL
  SELECT n.criado_em, 'nota', 'nota', n.texto, u.nome
    FROM gestao_nota n LEFT JOIN usuario u ON u.id = n.autor_id WHERE n.estabelecimento_id = @Id
  UNION ALL
  SELECT g.criado_em, 'gestao', g.acao, g.detalhe, u.nome
    FROM gestao_acao_log g LEFT JOIN usuario u ON u.id = g.usuario_id WHERE g.estabelecimento_id = @Id
) t ORDER BY Em DESC LIMIT 200;", new { Id = estabelecimentoId }, cancellationToken: ct))).ToList();
        }

        public async Task<IReadOnlyList<UltimoAcessoDto>> UltimosAcessosAsync(CancellationToken ct = default)
        {
            // Cada login ou renovacao de sessao grava um refresh token: o mais recente e o ultimo acesso (AcessoService faz igual).
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<UltimoAcessoDto>(new CommandDefinition(
                "SELECT t.id_usuario AS UsuarioId, MAX(t.created_at) AS UltimoAcesso FROM usuario_refresh_tokens t GROUP BY t.id_usuario;",
                cancellationToken: ct))).ToList();
        }

        public async Task<IReadOnlyList<LembreteDto>> LembretesAsync(CancellationToken ct = default)
        {
            // Notas abertas com lembrete que ja chegou: entram na fila do dia.
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<LembreteDto>(new CommandDefinition(@"
SELECT n.id, n.estabelecimento_id AS EstabelecimentoId, e.nome_fantasia AS Loja, n.texto, n.lembrar_em AS LembrarEm, u.nome AS Autor
  FROM gestao_nota n JOIN estabelecimentos e ON e.id = n.estabelecimento_id LEFT JOIN usuario u ON u.id = n.autor_id
 WHERE n.concluida_em IS NULL AND n.lembrar_em <= @Hoje ORDER BY n.lembrar_em;",
                new { Hoje = Hoje.ToDateTime(TimeOnly.MinValue) }, cancellationToken: ct))).ToList();
        }

        public async Task RegistrarEntradaAsync(Guid estabelecimentoId, int usuarioId, CancellationToken ct = default)
        {
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            await LogAsync(connection, null, usuarioId, estabelecimentoId, "entrou_na_loja", null, ct);
        }

        public async Task<IReadOnlyList<MotoboyDaLojaDto>> MotoboysDaLojaAsync(Guid estabelecimentoId, CancellationToken ct = default)
        {
            // Vinculados e os que ja sairam (com o motivo), com o quanto entregaram na semana.
            await using var connection = await dataSource.OpenConnectionAsync(ct);
            return (await connection.QueryAsync<MotoboyDaLojaDto>(new CommandDefinition(@"
SELECT m.id AS MotoboyId, m.nome AS Nome, m.telefone AS Telefone, me.ativo AS Ativo, me.created_at_utc AS VinculadoEm,
       me.disabled_at_utc AS EncerradoEm, me.disabled_reason AS MotivoEncerramento,
       COALESCE(ent.n, 0)::int AS Entregas7d, ent.ultima AS UltimaEntregaEm
  FROM motoboy_estabelecimento me
  JOIN motoboy m ON m.id = me.motoboy_id
  LEFT JOIN LATERAL (
       SELECT COUNT(*) FILTER (WHERE rs.completed_at_utc >= NOW() - INTERVAL '7 days') AS n, MAX(rs.completed_at_utc) AS ultima
         FROM delivery_route_stops rs
        WHERE rs.estabelecimento_id = me.estabelecimento_id AND rs.motoboy_id = me.motoboy_id AND rs.stop_status = 'completed') ent ON TRUE
 WHERE me.estabelecimento_id = @Loja AND COALESCE(m.is_simulated, FALSE) = FALSE
 ORDER BY me.ativo DESC, ent.ultima DESC NULLS LAST, m.nome;", new { Loja = estabelecimentoId }, cancellationToken: ct))).ToList();
        }
    }
}
