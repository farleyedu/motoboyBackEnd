using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.CRM;
using APIBack.Repository;
using APIBack.Service;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using F = APIBack.Acompanhamento.Fases.FasesAcompanhamento;

namespace APIBack.Acompanhamento.Fases
{
    /// <summary>SQL compartilhado com o fechamento legado do CRM (mesma transacao).</summary>
    public static class AcompanhamentoSql
    {
        public static Task<bool> TabelaExisteAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, string nome, CancellationToken ct = default) =>
            cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT to_regclass(@Nome) IS NOT NULL;", new { Nome = nome }, tx, cancellationToken: ct));

        /// <summary>
        /// Fechamento pelo /crm legado: o acompanhamento da oportunidade passa a ser o da loja. Se a loja ja tinha o seu, os dois viram um
        /// (fica o da loja, com toda a trilha). A fase segue a situacao da loja. Sem a tabela, ou sem acompanhamento ainda, nao faz nada.
        /// </summary>
        public static async Task VincularLojaAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid oportunidadeId, Guid estabelecimentoId, int? usuarioId,
            string motivo, CancellationToken ct = default)
        {
            if (!await TabelaExisteAsync(cx, tx, "acompanhamento", ct)) return;
            var daOportunidade = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM acompanhamento WHERE oportunidade_id = @O FOR UPDATE;", new { O = oportunidadeId }, tx, cancellationToken: ct));
            var daLoja = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM acompanhamento WHERE estabelecimento_id = @E FOR UPDATE;", new { E = estabelecimentoId }, tx, cancellationToken: ct));
            if (daOportunidade == null && daLoja == null) return;

            Guid alvo;
            if (daOportunidade.HasValue && daLoja.HasValue && daOportunidade != daLoja)
            {
                await UnirAsync(cx, tx, daLoja.Value, daOportunidade.Value, ct);
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE acompanhamento SET oportunidade_id = COALESCE(oportunidade_id, @O) WHERE id = @Id;",
                    new { O = oportunidadeId, Id = daLoja.Value }, tx, cancellationToken: ct));
                alvo = daLoja.Value;
            }
            else if (daOportunidade.HasValue)
            {
                await cx.ExecuteAsync(new CommandDefinition(
                    "UPDATE acompanhamento SET estabelecimento_id = @E WHERE id = @Id;",
                    new { E = estabelecimentoId, Id = daOportunidade.Value }, tx, cancellationToken: ct));
                alvo = daOportunidade.Value;
            }
            else
            {
                alvo = daLoja!.Value;
            }

            var atual = await cx.QuerySingleAsync<(string Fase, string? Situacao)>(new CommandDefinition(@"
SELECT a.fase, e.situacao_comercial FROM acompanhamento a LEFT JOIN estabelecimentos e ON e.id = a.estabelecimento_id WHERE a.id = @Id;",
                new { Id = alvo }, tx, cancellationToken: ct));
            var nova = F.Reconciliar(atual.Fase, atual.Situacao);
            if (nova != null)
                await MudarFaseAsync(cx, tx, alvo, atual.Fase, nova, usuarioId, usuarioId.HasValue ? AutoresPassagem.Usuario : AutoresPassagem.Sistema,
                    motivo, null, ct);
        }

        /// <summary>Junta dois acompanhamentos da mesma loja: a trilha de <paramref name="remover"/> vai para <paramref name="manter"/>.</summary>
        public static async Task UnirAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid manter, Guid remover, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento m
   SET teste_concluido_em = COALESCE(m.teste_concluido_em, r.teste_concluido_em),
       documento = COALESCE(m.documento, r.documento),
       quem_decide = COALESCE(m.quem_decide, r.quem_decide),
       pedidos_por_dia = COALESCE(m.pedidos_por_dia, r.pedidos_por_dia),
       dor_principal = COALESCE(m.dor_principal, r.dor_principal),
       valor_mensal = COALESCE(m.valor_mensal, r.valor_mensal)
  FROM acompanhamento r
 WHERE m.id = @Manter AND r.id = @Remover;
UPDATE acompanhamento_passagem p SET acompanhamento_id = @Manter
 WHERE p.acompanhamento_id = @Remover
   AND NOT EXISTS (SELECT 1 FROM acompanhamento_passagem x WHERE x.acompanhamento_id = @Manter AND x.chave_idempotencia = p.chave_idempotencia);
UPDATE acompanhamento_historico SET acompanhamento_id = @Manter WHERE acompanhamento_id = @Remover;
DELETE FROM acompanhamento WHERE id = @Remover;", new { Manter = manter, Remover = remover }, tx, cancellationToken: ct));
        }

        public static async Task MudarFaseAsync(NpgsqlConnection cx, NpgsqlTransaction tx, Guid id, string de, string para, int? usuarioId, string autorTipo,
            string? motivo, Guid? passagemId, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento
   SET fase = @Para, fase_desde = NOW(), versao = versao + 1, atualizado_em = NOW(),
       teste_concluido_em = CASE WHEN @De IN ('teste', 'implantacao') AND @Para = 'operando' THEN COALESCE(teste_concluido_em, NOW())
                                 ELSE teste_concluido_em END
 WHERE id = @Id;
INSERT INTO acompanhamento_historico (acompanhamento_id, tipo, de, para, autor_usuario_id, autor_tipo, motivo, passagem_id)
VALUES (@Id, 'fase', @De, @Para, @Usuario, @Autor, @Motivo, @Passagem);",
                new { Id = id, De = de, Para = para, Usuario = usuarioId, Autor = autorTipo, Motivo = motivo, Passagem = passagemId },
                tx, cancellationToken: ct));
        }
    }

    /// <summary>
    /// Acompanhamento no PostgreSQL. A passagem trava a linha (FOR UPDATE, depois da oportunidade: a mesma ordem do fechamento legado),
    /// confere a chave unica e grava efeitos, estado, trilha e historico na mesma transacao.
    /// </summary>
    public sealed class AcompanhamentoPostgresStore : IAcompanhamentoStore
    {
        private readonly NpgsqlDataSource _dataSource;
        private readonly IMemoryCache _cache;

        public AcompanhamentoPostgresStore(NpgsqlDataSource dataSource, IMemoryCache cache)
        {
            _dataSource = dataSource;
            _cache = cache;
        }

        internal const string EstadoColunas = @"
a.id, a.oportunidade_id AS OportunidadeId, a.estabelecimento_id AS EstabelecimentoId, e.id_empresa AS EmpresaId,
COALESCE(e.nome_fantasia, '') AS Nome, a.fase, a.fase_desde AS FaseDesde, a.faixa, a.versao, e.situacao_comercial AS SituacaoLoja,
a.documento, a.quem_decide AS QuemDecide, a.pedidos_por_dia AS PedidosPorDia, a.dor_principal AS DorPrincipal, a.valor_mensal AS ValorMensal,
a.teste_concluido_em AS TesteConcluidoEm, a.prorrogacoes_no_teste AS ProrrogacoesNoTeste, a.teste_quando_pronta AS TesteQuandoPronta,
a.pausa_ate AS PausaAte, a.retomar_em AS RetomarEm, a.saida_quando AS SaidaQuando, a.saida_em AS SaidaEm, a.forma_pagamento AS FormaPagamento,
a.cobranca_inicia_em AS CobrancaIniciaEm, a.criado_em AS CriadoEm, a.atualizado_em AS AtualizadoEm";

        public async Task<AcompanhamentoEstado?> ResolverAsync(Guid id, CancellationToken ct)
        {
            await using var cx = await _dataSource.OpenConnectionAsync(ct);
            var existente = await LerAsync(cx, null, id, porQualquerId: true, ct);
            if (existente != null) return existente;

            var crm = await AcompanhamentoSql.TabelaExisteAsync(cx, null, "crm_oportunidade", ct);
            var loja = await cx.QuerySingleOrDefaultAsync<(Guid Id, string? Situacao, DateTime? Inicio, string? Cnpj)?>(new CommandDefinition(@"
SELECT e.id, e.situacao_comercial, e.operacao_iniciada_em, emp.cnpj
  FROM estabelecimentos e LEFT JOIN empresas emp ON emp.id = e.id_empresa
 WHERE e.id = @Id;", new { Id = id }, cancellationToken: ct));
            if (loja is { } l)
            {
                var situacao = string.IsNullOrWhiteSpace(l.Situacao) ? SituacoesComerciais.Ativo : l.Situacao;
                Guid? oportunidade = crm && await AcompanhamentoSql.TabelaExisteAsync(cx, null, "crm_contrato", ct)
                    ? await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(@"
SELECT c.id_oportunidade FROM crm_contrato c
 WHERE c.estabelecimento_id = @Id AND c.id_oportunidade IS NOT NULL ORDER BY c.created_at DESC LIMIT 1;", new { Id = id }, cancellationToken: ct))
                    : null;
                // A oportunidade que virou esta loja ja tinha acompanhamento: ele passa a ser o da loja (a fase concilia na abertura).
                if (oportunidade.HasValue && await cx.ExecuteAsync(new CommandDefinition(
                        "UPDATE acompanhamento SET estabelecimento_id = @Id WHERE oportunidade_id = @O AND estabelecimento_id IS NULL;",
                        new { Id = id, O = oportunidade }, cancellationToken: ct)) > 0)
                    return await LerAsync(cx, null, id, porQualquerId: true, ct);

                // Loja que ja operava antes do acompanhamento conta como testada: o portao vale para quem entra daqui para frente.
                await CriarAsync(cx, oportunidade, id, F.PadraoDaSituacao(situacao), Documento(l.Cnpj),
                    situacao == SituacoesComerciais.Ativo ? l.Inicio ?? DateTime.UtcNow : null, ct);
                return await LerAsync(cx, null, id, porQualquerId: true, ct);
            }

            if (!crm) return null;
            var op = await cx.QuerySingleOrDefaultAsync<(Guid Id, string Status, string Coluna, string? Cnpj)?>(new CommandDefinition(
                "SELECT id, status::text, coluna::text, cnpj FROM crm_oportunidade WHERE id = @Id AND deleted_at IS NULL;",
                new { Id = id }, cancellationToken: ct));
            if (op is not { } o) return null;

            var fase = F.FaseDaOportunidade(o.Status, o.Coluna);
            if (fase == null)
            {
                // Fechada pelo /crm legado: o acompanhamento e o da loja do contrato.
                var lojaDoContrato = await AcompanhamentoSql.TabelaExisteAsync(cx, null, "crm_contrato", ct)
                    ? await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                        "SELECT estabelecimento_id FROM crm_contrato WHERE id_oportunidade = @Id ORDER BY created_at DESC LIMIT 1;",
                        new { Id = id }, cancellationToken: ct))
                    : null;
                return lojaDoContrato.HasValue ? await ResolverAsync(lojaDoContrato.Value, ct) : null;
            }

            await CriarAsync(cx, id, null, fase, Documento(o.Cnpj), null, ct);
            return await LerAsync(cx, null, id, porQualquerId: true, ct);
        }

        public async Task<IAcompanhamentoTransacao> AbrirAsync(Guid acompanhamentoId, CancellationToken ct)
        {
            var cx = await _dataSource.OpenConnectionAsync(ct);
            NpgsqlTransaction? tx = null;
            try
            {
                tx = await cx.BeginTransactionAsync(ct);
                var crm = await AcompanhamentoSql.TabelaExisteAsync(cx, tx, "crm_oportunidade", ct);
                // Ordem das travas: oportunidade -> acompanhamento -> loja (a mesma do fechamento legado).
                var oportunidade = await cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT oportunidade_id FROM acompanhamento WHERE id = @Id;", new { Id = acompanhamentoId }, tx, cancellationToken: ct));
                if (crm && oportunidade.HasValue)
                    await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM crm_oportunidade WHERE id = @Id FOR UPDATE;",
                        new { Id = oportunidade }, tx, cancellationToken: ct));
                await cx.ExecuteAsync(new CommandDefinition("SELECT 1 FROM acompanhamento WHERE id = @Id FOR UPDATE;",
                    new { Id = acompanhamentoId }, tx, cancellationToken: ct));

                var estado = await LerAsync(cx, tx, acompanhamentoId, porQualquerId: false, ct)
                    ?? throw new AcompanhamentoException(404, CodigosAcompanhamento.NaoEncontrado, "Loja ou oportunidade não encontrada.");
                var transacao = new Transacao(cx, tx, estado, crm, _cache, new CrmRepository(_dataSource));
                await transacao.ConciliarAsync(ct);
                return transacao;
            }
            catch
            {
                if (tx != null) await tx.DisposeAsync();
                await cx.DisposeAsync();
                throw;
            }
        }

        public async Task<IReadOnlyList<HistoricoDto>> HistoricoAsync(Guid acompanhamentoId, CancellationToken ct)
        {
            await using var cx = await _dataSource.OpenConnectionAsync(ct);
            var rows = await cx.QueryAsync<HistoricoRow>(new CommandDefinition(@"
SELECT id, tipo, de, para, em, autor_usuario_id AS AutorUsuarioId, autor_tipo AS AutorTipo, motivo, passagem_id AS PassagemId
  FROM acompanhamento_historico WHERE acompanhamento_id = @Id ORDER BY em DESC, id DESC LIMIT 200;",
                new { Id = acompanhamentoId }, cancellationToken: ct));
            return rows.Select(r => new HistoricoDto(r.Id, r.Tipo, r.De, r.Para, Utc(r.Em), r.AutorUsuarioId, r.AutorTipo, r.Motivo, r.PassagemId)).ToList();
        }

        private static async Task CriarAsync(NpgsqlConnection cx, Guid? oportunidadeId, Guid? estabelecimentoId, string fase, string? documento,
            DateTime? testeConcluidoEm, CancellationToken ct)
        {
            // Duas consultas ao mesmo tempo: a unica cria (indices unicos), a outra le a que ficou.
            await cx.ExecuteAsync(new CommandDefinition(@"
WITH novo AS (
    INSERT INTO acompanhamento (oportunidade_id, estabelecimento_id, fase, documento, teste_concluido_em)
    VALUES (@O, @E, @Fase, @Documento, @Teste)
    ON CONFLICT DO NOTHING
    RETURNING id, fase)
INSERT INTO acompanhamento_historico (acompanhamento_id, tipo, de, para, autor_tipo, motivo)
SELECT id, 'fase', NULL, fase, 'sistema', 'Início do acompanhamento' FROM novo;",
                new { O = oportunidadeId, E = estabelecimentoId, Fase = fase, Documento = documento, Teste = testeConcluidoEm },
                cancellationToken: ct));
        }

        private static async Task<AcompanhamentoEstado?> LerAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid id, bool porQualquerId, CancellationToken ct)
        {
            var filtro = porQualquerId ? "a.id = @Id OR a.oportunidade_id = @Id OR a.estabelecimento_id = @Id" : "a.id = @Id";
            var row = await cx.QueryFirstOrDefaultAsync<EstadoRow>(new CommandDefinition(
                "SELECT " + EstadoColunas + @"
  FROM acompanhamento a LEFT JOIN estabelecimentos e ON e.id = a.estabelecimento_id
 WHERE " + filtro + " ORDER BY (a.id = @Id) DESC LIMIT 1;", new { Id = id }, tx, cancellationToken: ct));
            return row?.ToEstado();
        }

        private static string? Documento(string? cnpj)
        {
            var d = DocumentoRegras.Digitos(cnpj);
            return d is { Length: 11 or 14 } ? d : null;
        }

        internal static DateTimeOffset Utc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));
        private static DateTimeOffset? Utc(DateTime? value) => value is null ? null : Utc(value.Value);
        private static DateOnly? Dia(DateTime? value) => value is null ? null : DateOnly.FromDateTime(value.Value);

        private sealed class EstadoRow
        {
            public Guid Id { get; set; }
            public Guid? OportunidadeId { get; set; }
            public Guid? EstabelecimentoId { get; set; }
            public Guid? EmpresaId { get; set; }
            public string Nome { get; set; } = string.Empty;
            public string Fase { get; set; } = string.Empty;
            public DateTime FaseDesde { get; set; }
            public string Faixa { get; set; } = string.Empty;
            public long Versao { get; set; }
            public string? SituacaoLoja { get; set; }
            public string? Documento { get; set; }
            public string? QuemDecide { get; set; }
            public int? PedidosPorDia { get; set; }
            public string? DorPrincipal { get; set; }
            public decimal? ValorMensal { get; set; }
            public DateTime? TesteConcluidoEm { get; set; }
            public int ProrrogacoesNoTeste { get; set; }
            public bool TesteQuandoPronta { get; set; }
            public DateTime? PausaAte { get; set; }
            public DateTime? RetomarEm { get; set; }
            public string? SaidaQuando { get; set; }
            public DateTime? SaidaEm { get; set; }
            public string? FormaPagamento { get; set; }
            public DateTime? CobrancaIniciaEm { get; set; }
            public DateTime CriadoEm { get; set; }
            public DateTime AtualizadoEm { get; set; }

            public AcompanhamentoEstado ToEstado() => new()
            {
                Id = Id, OportunidadeId = OportunidadeId, EstabelecimentoId = EstabelecimentoId, EmpresaId = EmpresaId, Nome = Nome,
                Fase = Fase, FaseDesde = Utc(FaseDesde), Faixa = Faixa, Versao = Versao,
                SituacaoLoja = EstabelecimentoId.HasValue && string.IsNullOrWhiteSpace(SituacaoLoja) ? SituacoesComerciais.Ativo : SituacaoLoja,
                Documento = Documento, QuemDecide = QuemDecide, PedidosPorDia = PedidosPorDia, DorPrincipal = DorPrincipal, ValorMensal = ValorMensal,
                TesteConcluidoEm = Utc(TesteConcluidoEm), ProrrogacoesNoTeste = ProrrogacoesNoTeste, TesteQuandoPronta = TesteQuandoPronta,
                PausaAte = Dia(PausaAte), RetomarEm = Dia(RetomarEm), SaidaQuando = SaidaQuando, SaidaEm = Dia(SaidaEm),
                FormaPagamento = FormaPagamento, CobrancaIniciaEm = Dia(CobrancaIniciaEm), CriadoEm = Utc(CriadoEm), AtualizadoEm = Utc(AtualizadoEm)
            };
        }

        private sealed class HistoricoRow
        {
            public long Id { get; set; }
            public string Tipo { get; set; } = string.Empty;
            public string? De { get; set; }
            public string Para { get; set; } = string.Empty;
            public DateTime Em { get; set; }
            public int? AutorUsuarioId { get; set; }
            public string AutorTipo { get; set; } = string.Empty;
            public string? Motivo { get; set; }
            public Guid? PassagemId { get; set; }
        }

        internal sealed class PassagemRow
        {
            public Guid Id { get; set; }
            public string De { get; set; } = string.Empty;
            public string Para { get; set; } = string.Empty;
            public string FaseResultante { get; set; } = string.Empty;
            public string Nome { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public int? AutorUsuarioId { get; set; }
            public string AutorTipo { get; set; } = string.Empty;
            public string? Motivo { get; set; }
            public string ChaveIdempotencia { get; set; } = string.Empty;
            public DateTime CriadaEm { get; set; }

            public PassagemRegistroDto ToDto() =>
                new(Id, De, Para, FaseResultante, Nome, Status, AutorUsuarioId, AutorTipo, Motivo, ChaveIdempotencia, Utc(CriadaEm));
        }

        private sealed class OportunidadeRow
        {
            public Guid Id { get; set; }
            public string NomeEmpresa { get; set; } = string.Empty;
            public string NomeContato { get; set; } = string.Empty;
            public string Telefone { get; set; } = string.Empty;
            public string? Email { get; set; }
            public string Tipo { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public int? ResponsavelUsuarioId { get; set; }
            public string? PropostaJson { get; set; }
        }

        private sealed class PropostaRow
        {
            public Guid Id { get; set; }
            public decimal? ValorMensal { get; set; }
            public decimal? ValorImplantacao { get; set; }
            public string Dados { get; set; } = "{}";
        }

        /// <summary>Uma passagem em andamento. Sem commit, o dispose desfaz.</summary>
        private sealed class Transacao : IAcompanhamentoTransacao
        {
            private readonly NpgsqlConnection _cx;
            private readonly NpgsqlTransaction _tx;
            private readonly bool _crm;
            private readonly IMemoryCache _cache;
            private readonly CrmRepository _crmRepository;
            private bool _situacaoMudou;

            public Transacao(NpgsqlConnection cx, NpgsqlTransaction tx, AcompanhamentoEstado estado, bool crm, IMemoryCache cache, CrmRepository crmRepository)
            {
                _cx = cx;
                _tx = tx;
                Estado = estado;
                _crm = crm;
                _cache = cache;
                _crmRepository = crmRepository;
            }

            public AcompanhamentoEstado Estado { get; private set; }

            /// <summary>
            /// A situacao da loja (ou a oportunidade, sem loja) mudou fora das passagens (pagamento, API legada, "Comecar a operar",
            /// /crm legado): a fase acompanha e o historico registra como sistema.
            /// </summary>
            public async Task ConciliarAsync(CancellationToken ct)
            {
                string? nova;
                string motivo;
                if (Estado.EstabelecimentoId.HasValue)
                {
                    nova = F.Reconciliar(Estado.Fase, Estado.SituacaoLoja);
                    motivo = $"Situação da loja na Gestão: {Estado.SituacaoLoja}";
                }
                else if (_crm && Estado.OportunidadeId.HasValue && (F.EmConquista(Estado.Fase) || Estado.Fase == F.Perdido))
                {
                    var op = await _cx.QuerySingleOrDefaultAsync<(string Status, string Coluna)?>(new CommandDefinition(
                        "SELECT status::text, coluna::text FROM crm_oportunidade WHERE id = @Id;", new { Id = Estado.OportunidadeId }, _tx, cancellationToken: ct));
                    var daOportunidade = op is { } o ? F.FaseDaOportunidade(o.Status, o.Coluna) : null;
                    nova = daOportunidade != null && daOportunidade != Estado.Fase ? daOportunidade : null;
                    motivo = "Oportunidade alterada no CRM";
                }
                else
                {
                    return;
                }

                if (nova == null) return;
                await AcompanhamentoSql.MudarFaseAsync(_cx, _tx, Estado.Id, Estado.Fase, nova, null, AutoresPassagem.Sistema, motivo, null, ct);
                await RecarregarAsync(ct);
            }

            public async Task<PassagemRegistroDto?> PassagemPorChaveAsync(string chave, CancellationToken ct)
            {
                var row = await _cx.QuerySingleOrDefaultAsync<PassagemRow>(new CommandDefinition(PassagemSelect + @"
 WHERE acompanhamento_id = @Id AND chave_idempotencia = @Chave;", new { Id = Estado.Id, Chave = chave }, _tx, cancellationToken: ct));
                return row?.ToDto();
            }

            public async Task<bool> CobrancaEmAbertoAsync(CancellationToken ct)
            {
                if (!Estado.EstabelecimentoId.HasValue || !await AcompanhamentoSql.TabelaExisteAsync(_cx, _tx, "comercial_cobranca", ct)) return false;
                return await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM comercial_cobranca WHERE id_estabelecimento = @Loja AND status IN ('aberta', 'processando'));",
                    new { Loja = Estado.EstabelecimentoId }, _tx, cancellationToken: ct));
            }

            public async Task<bool> TemPropostaAsync(CancellationToken ct)
            {
                if (!Estado.OportunidadeId.HasValue) return false;
                if (await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O AND status IN ('rascunho', 'enviada'));",
                        new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct)))
                    return true;
                return _crm && await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                    "SELECT COALESCE(NULLIF(btrim(proposta_json::text), ''), 'null') <> 'null' FROM crm_oportunidade WHERE id = @O;",
                    new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
            }

            public async Task<PassagemRegistroDto> AplicarAsync(PassagemAplicacao a, CancellationToken ct)
            {
                var d = a.Dados;
                var passagem = await _cx.QuerySingleAsync<PassagemRow>(new CommandDefinition(@"
INSERT INTO acompanhamento_passagem (acompanhamento_id, chave_idempotencia, de, para, fase_resultante, nome, status, autor_usuario_id, autor_tipo, motivo, dados)
VALUES (@Id, @Chave, @De, @Para, @Resultante, @Nome, @Status, @Usuario, @Autor, @Motivo, CAST(@Dados AS jsonb))
RETURNING " + PassagemColunas + ";",
                    new
                    {
                        Id = Estado.Id, a.Chave, a.De, Para = a.Definicao.Para, Resultante = a.FaseResultante, a.Definicao.Nome, a.Status,
                        Usuario = a.UsuarioId, Autor = a.AutorTipo, a.Motivo, Dados = JsonSerializer.Serialize(d, JsonOpcoes)
                    }, _tx, cancellationToken: ct));

                await AplicarEfeitoAsync(a, passagem.Id, ct);

                // Dados da conquista ficam guardados para as proximas passagens (quem decide, documento...).
                await _cx.ExecuteAsync(new CommandDefinition(@"
UPDATE acompanhamento
   SET quem_decide = COALESCE(@QuemDecide, quem_decide),
       pedidos_por_dia = COALESCE(@Pedidos, pedidos_por_dia),
       dor_principal = COALESCE(@Dor, dor_principal),
       documento = COALESCE(@Documento, documento),
       valor_mensal = COALESCE(@Valor, valor_mensal),
       versao = versao + 1, atualizado_em = NOW()
 WHERE id = @Id;",
                    new
                    {
                        Id = Estado.Id, QuemDecide = Limpo(d.QuemDecide), Pedidos = d.PedidosPorDia, Dor = Limpo(d.DorPrincipal),
                        Documento = Documento(d.Documento), Valor = d.ValorMensal
                    }, _tx, cancellationToken: ct));

                await RecarregarAsync(ct);
                if (a.Status == StatusPassagem.Feita && Estado.Fase != a.FaseResultante)
                {
                    await AcompanhamentoSql.MudarFaseAsync(_cx, _tx, Estado.Id, Estado.Fase, a.FaseResultante, a.UsuarioId, a.AutorTipo, a.Motivo, passagem.Id, ct);
                    await RecarregarAsync(ct);
                }

                return passagem.ToDto();
            }

            private async Task AplicarEfeitoAsync(PassagemAplicacao a, Guid passagemId, CancellationToken ct)
            {
                var d = a.Dados;
                switch (a.Efeito)
                {
                    case EfeitoPassagem.MontarProposta:
                        await ExecAsync(@"
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, valor_mensal, valor_implantacao, dados, criada_por, passagem_id)
SELECT @O, COALESCE(MAX(versao), 0) + 1, 'rascunho', @Valor, @Implantacao, CAST(@Dados AS jsonb), @Usuario, @Passagem
  FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O;",
                            new
                            {
                                O = ExigirOportunidade(), Valor = d.ValorMensal, Implantacao = d.ValorImplantacao, Usuario = a.UsuarioId, Passagem = passagemId,
                                Dados = JsonSerializer.Serialize(new { d.QuemDecide, d.PedidosPorDia, d.DorPrincipal, d.Observacao }, JsonOpcoes)
                            }, ct);
                        await CrmAsync("UPDATE crm_oportunidade SET coluna = 'proposta', updated_at = NOW() WHERE id = @O AND status::text = 'ativo';", ct);
                        break;

                    case EfeitoPassagem.VoltarAConversar:
                        // A proposta fica guardada como versao; a proxima "Montar a proposta" abre outra.
                        await ExecAsync(@"
UPDATE acompanhamento_proposta_versao SET status = 'guardada' WHERE oportunidade_id = @O AND status IN ('rascunho', 'enviada');",
                            new { O = ExigirOportunidade() }, ct);
                        await CrmAsync("UPDATE crm_oportunidade SET coluna = 'conversa', updated_at = NOW() WHERE id = @O AND status::text = 'ativo';", ct);
                        break;

                    case EfeitoPassagem.FecharNaConversa:
                    case EfeitoPassagem.PropostaAceita:
                    case EfeitoPassagem.TesteSemCompromisso:
                        await CriarLojaEmImplantacaoAsync(a, passagemId, ct);
                        break;

                    case EfeitoPassagem.ComecarTeste:
                        await SituacaoAsync("iniciar_operacao", a, ct);
                        await ExecAsync("UPDATE acompanhamento SET teste_quando_pronta = FALSE, prorrogacoes_no_teste = 0 WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        break;

                    case EfeitoPassagem.ProrrogarTeste:
                        // A segunda prorrogacao so fica registrada (pendente); a data muda quando o gestor aprovar.
                        if (a.Status != StatusPassagem.Feita) break;
                        await SituacaoAsync("prorrogar_teste", a, ct, dias: a.DiasProrrogacao);
                        await ExecAsync("UPDATE acompanhamento SET prorrogacoes_no_teste = prorrogacoes_no_teste + 1 WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        break;

                    case EfeitoPassagem.AtivarPlano:
                        await SituacaoAsync("ativar", a, ct, plano: Limpo(d.Plano)?.ToLowerInvariant());
                        await ExecAsync(@"
UPDATE acompanhamento SET teste_concluido_em = COALESCE(teste_concluido_em, NOW()), forma_pagamento = @Forma, cobranca_inicia_em = @Cobranca
 WHERE id = @Id;", new { Id = Estado.Id, Forma = Limpo(d.FormaPagamento)?.ToLowerInvariant(), Cobranca = a.CobrancaIniciaEm }, ct);
                        break;

                    case EfeitoPassagem.VoltarAOperar:
                        var plano = await _cx.ExecuteScalarAsync<string?>(new CommandDefinition(
                            "SELECT plano_comercial FROM estabelecimentos WHERE id = @Id;", new { Id = ExigirLoja() }, _tx, cancellationToken: ct));
                        await SituacaoAsync("ativar", a, ct, plano: string.IsNullOrWhiteSpace(plano) ? "personalizado" : plano);
                        break;

                    case EfeitoPassagem.Reimplantar:
                        await SituacaoAsync("reimplantar", a, ct);
                        break;

                    case EfeitoPassagem.PediuParaCancelar:
                        await ExecAsync("UPDATE acompanhamento SET saida_quando = @Quando, saida_em = @Em WHERE id = @Id;",
                            new { Id = Estado.Id, Quando = Limpo(d.Quando)?.ToLowerInvariant(), Em = a.SaidaEm }, ct);
                        break;

                    case EfeitoPassagem.Ficou:
                        await ExecAsync("UPDATE acompanhamento SET saida_quando = NULL, saida_em = NULL WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        break;

                    case EfeitoPassagem.Encerrar:
                    case EfeitoPassagem.PararLoja:
                        await SituacaoAsync("bloquear", a, ct, motivo: a.Motivo ?? "Saída encerrada");
                        break;

                    case EfeitoPassagem.DeclararPausa:
                        await ExecAsync("UPDATE acompanhamento SET pausa_ate = @Ate WHERE id = @Id;", new { Id = Estado.Id, Ate = a.PausaAte }, ct);
                        break;

                    case EfeitoPassagem.VoltarDaPausa:
                        await ExecAsync("UPDATE acompanhamento SET pausa_ate = NULL WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        break;

                    case EfeitoPassagem.Perder:
                        if (Estado.EstabelecimentoId.HasValue && Estado.SituacaoLoja != SituacoesComerciais.Bloqueado)
                            await SituacaoAsync("bloquear", a, ct, motivo: "Perdido: " + a.Motivo);
                        await ExecAsync("UPDATE acompanhamento SET retomar_em = @Em, teste_quando_pronta = FALSE WHERE id = @Id;",
                            new { Id = Estado.Id, Em = a.RetomarEm }, ct);
                        await CrmAsync(@"
UPDATE crm_oportunidade SET status = 'perdido', closed_at = COALESCE(closed_at, NOW()), updated_at = NOW()
 WHERE id = @O AND status::text = 'ativo';", ct);
                        break;

                    case EfeitoPassagem.Retomar:
                        await ExecAsync("UPDATE acompanhamento SET retomar_em = NULL WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        await CrmAsync(@"
UPDATE crm_oportunidade SET status = 'ativo', coluna = 'conversa', closed_at = NULL, updated_at = NOW()
 WHERE id = @O AND status::text = 'perdido';", ct);
                        break;

                    case EfeitoPassagem.QuerVoltar:
                        // Volta com todo o historico; a loja segue bloqueada ate fechar de novo (Implantacao).
                        break;
                }
            }

            /// <summary>
            /// Fechou (na conversa ou pela proposta) ou teste sem compromisso. Loja que ja era deste acompanhamento (perdida e retomada)
            /// volta para implantacao; senao o fechamento do CRM cria empresa e loja em implantacao, sem lancamentos (cobranca so
            /// depois do teste), ligando so a empresa/loja que o operador confirmou.
            /// </summary>
            private async Task CriarLojaEmImplantacaoAsync(PassagemAplicacao a, Guid passagemId, CancellationToken ct)
            {
                var d = a.Dados;
                if (a.TesteQuandoPronta)
                    await ExecAsync("UPDATE acompanhamento SET teste_quando_pronta = TRUE WHERE id = @Id;", new { Id = Estado.Id }, ct);

                PropostaRow? proposta = null;
                if (Estado.OportunidadeId.HasValue)
                {
                    proposta = await _cx.QuerySingleOrDefaultAsync<PropostaRow>(new CommandDefinition(@"
SELECT id, valor_mensal AS ValorMensal, valor_implantacao AS ValorImplantacao, dados::text AS Dados
  FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O AND status IN ('rascunho', 'enviada')
 ORDER BY versao DESC LIMIT 1;", new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
                    if (a.Efeito == EfeitoPassagem.PropostaAceita)
                    {
                        if (proposta != null)
                            await ExecAsync("UPDATE acompanhamento_proposta_versao SET status = 'aceita', passagem_id = @P WHERE id = @Id;",
                                new { Id = proposta.Id, P = passagemId }, ct);
                        else
                            await ExecAsync(@"
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, dados, criada_por, passagem_id)
SELECT @O, COALESCE(MAX(versao), 0) + 1, 'aceita', COALESCE((SELECT NULLIF(btrim(proposta_json::text), '')::jsonb FROM crm_oportunidade WHERE id = @O), '{}'::jsonb), @U, @P
  FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O;", new { O = Estado.OportunidadeId, U = a.UsuarioId, P = passagemId }, ct);
                    }
                }

                if (Estado.EstabelecimentoId.HasValue)
                {
                    // Quem testou e saiu nao testa de novo; quem perdeu no meio do teste, testa.
                    await SituacaoAsync("reimplantar", a, ct, zerarTeste: !Estado.TesteConcluidoEm.HasValue);
                    return;
                }

                if (!_crm) throw new InvalidOperationException("Sem o CRM nao ha como criar a loja.");
                var op = await _cx.QuerySingleAsync<OportunidadeRow>(new CommandDefinition(@"
SELECT id, nome_empresa AS NomeEmpresa, nome_contato AS NomeContato, telefone, email, tipo::text AS Tipo, status::text AS Status,
       responsavel_usuario_id AS ResponsavelUsuarioId, proposta_json::text AS PropostaJson
  FROM crm_oportunidade WHERE id = @O;", new { O = ExigirOportunidade() }, _tx, cancellationToken: ct));
                var responsavel = op.ResponsavelUsuarioId ?? a.UsuarioId
                    ?? throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, "Defina o responsável da oportunidade.");
                var hoje = DateOnly.FromDateTime(a.Agora.UtcDateTime);
                var valorMensal = (a.Efeito == EfeitoPassagem.PropostaAceita ? proposta?.ValorMensal : null) ?? d.ValorMensal ?? 0m;

                var command = new CrmCloseOpportunityCommand
                {
                    OportunidadeId = op.Id,
                    UsuarioId = a.UsuarioId ?? responsavel,
                    ResponsavelUsuarioId = responsavel,
                    EmpresaId = d.EmpresaId,
                    EstabelecimentoId = d.EmpresaId.HasValue ? d.EstabelecimentoId : null,
                    NomeEmpresa = op.NomeEmpresa,
                    NomeContato = op.NomeContato,
                    Telefone = op.Telefone,
                    Email = op.Email,
                    Cnpj = Documento(d.Documento) ?? Estado.Documento,
                    Tipo = op.Tipo,
                    StatusInicial = "implantacao",
                    DiaVencimento = d.DiaVencimento is >= 1 and <= 31 ? d.DiaVencimento.Value : Math.Min(hoje.Day, 28),
                    // So informativo: a cobranca de verdade comeca na ativacao, depois do teste.
                    DataInicioCobranca = hoje.AddDays(SituacaoComercialRules.DiasTeste).ToDateTime(TimeOnly.MinValue),
                    Observacoes = Limpo(d.Observacao),
                    EstabelecimentoTipoSlug = Limpo(d.EstabelecimentoTipoSlug),
                    MensalidadeSaas = valorMensal,
                    ImplantacaoValor = (a.Efeito == EfeitoPassagem.PropostaAceita ? proposta?.ValorImplantacao : null) ?? d.ValorImplantacao ?? 0m,
                    PropostaJson = op.PropostaJson,
                    SemLancamentos = true,
                    RegistrarPropostaAceita = false,
                    VincularAcompanhamento = false
                };
                var resultado = await _crmRepository.FecharOportunidadeNaTransacaoAsync(_cx, _tx, command);

                // Loja ja cadastrada (confirmada) com acompanhamento proprio: a trilha dela vem para este.
                var outro = await _cx.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM acompanhamento WHERE estabelecimento_id = @E AND id <> @Id FOR UPDATE;",
                    new { E = resultado.EstabelecimentoId, Id = Estado.Id }, _tx, cancellationToken: ct));
                if (outro.HasValue) await AcompanhamentoSql.UnirAsync(_cx, _tx, Estado.Id, outro.Value, ct);
                await ExecAsync("UPDATE acompanhamento SET estabelecimento_id = @E WHERE id = @Id;",
                    new { Id = Estado.Id, E = resultado.EstabelecimentoId }, ct);
                _situacaoMudou = true;
                await RecarregarAsync(ct);
            }

            private async Task SituacaoAsync(string acao, PassagemAplicacao a, CancellationToken ct, string? plano = null, string? motivo = null,
                int? dias = null, bool zerarTeste = false)
            {
                await SituacaoComercialService.AplicarNaTransacaoAsync(_cx, _tx, ExigirLoja(),
                    new AcaoSituacaoRequest(acao, motivo ?? a.Motivo, plano, null), a.UsuarioId, OrigensSituacao.Gestao,
                    SituacaoComercialService.AcoesInternas.Acompanhamento, ct, dias, zerarTeste);
                _situacaoMudou = true;
            }

            private async Task RecarregarAsync(CancellationToken ct) =>
                Estado = await LerAsync(_cx, _tx, Estado.Id, porQualquerId: false, ct)
                    ?? throw new InvalidOperationException("Acompanhamento sumiu durante a passagem.");

            private Task ExecAsync(string sql, object param, CancellationToken ct) =>
                _cx.ExecuteAsync(new CommandDefinition(sql, param, _tx, cancellationToken: ct));

            private Task CrmAsync(string sql, CancellationToken ct) =>
                _crm && Estado.OportunidadeId.HasValue ? ExecAsync(sql, new { O = Estado.OportunidadeId }, ct) : Task.CompletedTask;

            private Guid ExigirLoja() => Estado.EstabelecimentoId
                ?? throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, "A loja ainda não foi criada.");

            private Guid ExigirOportunidade() => Estado.OportunidadeId
                ?? throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, "Não há oportunidade ligada a esta loja.");

            public async Task CommitAsync(CancellationToken ct)
            {
                await _tx.CommitAsync(ct);
                if (_situacaoMudou && Estado.EstabelecimentoId.HasValue)
                    _cache.Remove(SituacaoComercialService.CacheKey(Estado.EstabelecimentoId.Value));
            }

            public async ValueTask DisposeAsync()
            {
                await _tx.DisposeAsync();
                await _cx.DisposeAsync();
            }

            private static string? Limpo(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
        }

        private const string PassagemColunas = @"id, de, para, fase_resultante AS FaseResultante, nome, status, autor_usuario_id AS AutorUsuarioId,
       autor_tipo AS AutorTipo, motivo, chave_idempotencia AS ChaveIdempotencia, criada_em AS CriadaEm";
        private const string PassagemSelect = "SELECT " + PassagemColunas + " FROM acompanhamento_passagem";

        private static readonly JsonSerializerOptions JsonOpcoes = new(JsonSerializerDefaults.Web);
    }
}
