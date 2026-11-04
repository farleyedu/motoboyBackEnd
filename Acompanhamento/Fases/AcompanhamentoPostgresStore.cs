using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Model.CRM;
using APIBack.Payments.Assinaturas;
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

        private const string VersaoVigenteExiste =
            "SELECT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O AND status IN ('enviada', 'em_negociacao'));";

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

            var enviada = await cx.ExecuteScalarAsync<bool>(new CommandDefinition(VersaoVigenteExiste, new { O = id }, cancellationToken: ct));
            var fase = F.FaseDaOportunidade(o.Status, o.Coluna, enviada);
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
            public Guid OportunidadeId { get; set; }
            public int Versao { get; set; }
            public long Edicao { get; set; }
            public string Status { get; set; } = string.Empty;
            public decimal? ValorMensal { get; set; }
            public decimal? ValorImplantacao { get; set; }
            public bool ValorZeroConsciente { get; set; }
            public string Dados { get; set; } = "{}";
            public string? Canal { get; set; }
            public string? Destinatario { get; set; }
            public int? ApresentadaPor { get; set; }
            public Guid? EvidenciaMensagemId { get; set; }
            public string? Origem { get; set; }
            public int? RevisaVersao { get; set; }
            public string? Resposta { get; set; }
            public string? RespostaNota { get; set; }
            public DateTime? RespostaEm { get; set; }
            public string? ChaveEnvio { get; set; }
            public string? ChaveResposta { get; set; }
            public int? CriadaPor { get; set; }
            public DateTime CriadaEm { get; set; }
            public DateTime? EnviadaEm { get; set; }
            public DateTime? ValidadeAte { get; set; }
            public DateTime AtualizadaEm { get; set; }
            public Guid? PassagemId { get; set; }

            public PropostaVersao ToVersao() => new()
            {
                Id = Id, OportunidadeId = OportunidadeId, Versao = Versao, Edicao = Edicao, Status = Status, ValorMensal = ValorMensal, ValorImplantacao = ValorImplantacao,
                ValorZeroConsciente = ValorZeroConsciente, Dados = LerDados(Dados), Canal = Canal, Destinatario = Destinatario, ApresentadaPor = ApresentadaPor,
                EvidenciaMensagemId = EvidenciaMensagemId, Origem = Origem, RevisaVersao = RevisaVersao, Resposta = Resposta, RespostaNota = RespostaNota,
                RespostaEm = Utc(RespostaEm), ChaveEnvio = ChaveEnvio, ChaveResposta = ChaveResposta, CriadaPor = CriadaPor, CriadaEm = Utc(CriadaEm),
                EnviadaEm = Utc(EnviadaEm), ValidadeAte = Dia(ValidadeAte), AtualizadaEm = Utc(AtualizadaEm), PassagemId = PassagemId
            };
        }

        private const string PropostaColunas = @"
id, oportunidade_id AS OportunidadeId, versao, edicao, status, valor_mensal AS ValorMensal, valor_implantacao AS ValorImplantacao,
valor_zero_consciente AS ValorZeroConsciente, dados::text AS Dados, canal, destinatario, apresentada_por AS ApresentadaPor,
evidencia_mensagem_id AS EvidenciaMensagemId, origem, revisa_versao AS RevisaVersao, resposta, resposta_nota AS RespostaNota, resposta_em AS RespostaEm,
chave_envio AS ChaveEnvio, chave_resposta AS ChaveResposta, criada_por AS CriadaPor, criada_em AS CriadaEm, enviada_em AS EnviadaEm,
validade_ate::timestamp AS ValidadeAte, atualizada_em AS AtualizadaEm, passagem_id AS PassagemId";

        private static PropostaDados LerDados(string json)
        {
            try { return JsonSerializer.Deserialize<PropostaDados>(json, JsonOpcoes) ?? new PropostaDados(); }
            catch (JsonException) { return new PropostaDados(); }
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
                    // Proposta recusada continua em Proposta: a falta de versao vigente so impede ENTRAR na fase.
                    var enviada = Estado.Fase == F.Proposta || await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(
                        VersaoVigenteExiste, new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
                    var daOportunidade = op is { } o ? F.FaseDaOportunidade(o.Status, o.Coluna, enviada) : null;
                    nova = daOportunidade != null && daOportunidade != Estado.Fase ? daOportunidade : null;
                    motivo = "Oportunidade alterada no CRM";
                }
                else
                {
                    return;
                }

                if (nova == null) return;
                await AcompanhamentoSql.MudarFaseAsync(_cx, _tx, Estado.Id, Estado.Fase, nova, null, AutoresPassagem.Sistema, motivo, null, ct);
                if (nova is F.Perdido or F.Encerrado) await EncerrarAbertosAsync(motivo, null, propostas: true, passo: true, ct);
                else if (nova == F.Conversa) await EncerrarAbertosAsync(motivo, null, propostas: true, passo: false, ct);
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

            public async Task<PropostaVersao?> PropostaVigenteAsync(CancellationToken ct) =>
                (await PropostasAsync(ct)).FirstOrDefault(v => StatusProposta.Vigente(v.Status));

            public async Task<IReadOnlyList<PropostaVersao>> PropostasAsync(CancellationToken ct)
            {
                if (!Estado.OportunidadeId.HasValue) return Array.Empty<PropostaVersao>();
                var linhas = await _cx.QueryAsync<PropostaRow>(new CommandDefinition(
                    "SELECT " + PropostaColunas + " FROM acompanhamento_proposta_versao WHERE oportunidade_id = @O ORDER BY versao;",
                    new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
                return linhas.Select(l => l.ToVersao()).ToList();
            }

            public async Task<PropostaContexto> ContextoPropostaAsync(CancellationToken ct)
            {
                if (!_crm || !Estado.OportunidadeId.HasValue) return new PropostaContexto(null, null, null, null, null, null);
                return await _cx.QuerySingleAsync<PropostaContexto>(new CommandDefinition(@"
SELECT nome_contato AS Contato, COALESCE(whatsapp_e164, telefone) AS Telefone, email AS Email, decisor AS Decisor,
       valor_mensal_est AS ValorMensal, valor_impl_est AS ValorImplantacao
  FROM crm_oportunidade WHERE id = @O;", new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct));
            }

            public async Task<PropostaVersao> CriarPropostaAsync(PropostaVersao n, CancellationToken ct)
            {
                var row = await _cx.QuerySingleAsync<PropostaRow>(new CommandDefinition(@"
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, valor_mensal, valor_implantacao, valor_zero_consciente, dados, criada_por, origem, revisa_versao)
VALUES (@OportunidadeId, @Versao, @Status, @ValorMensal, @ValorImplantacao, @ValorZeroConsciente, CAST(@Dados AS jsonb), @CriadaPor, @Origem, @RevisaVersao)
RETURNING " + PropostaColunas + ";", new
                {
                    n.OportunidadeId, n.Versao, n.Status, n.ValorMensal, n.ValorImplantacao, n.ValorZeroConsciente,
                    Dados = JsonSerializer.Serialize(n.Dados, JsonOpcoes), n.CriadaPor, n.Origem, n.RevisaVersao
                }, _tx, cancellationToken: ct));
                return row.ToVersao();
            }

            public async Task<PropostaVersao> SalvarPropostaAsync(PropostaVersao v, CancellationToken ct)
            {
                var row = await _cx.QuerySingleAsync<PropostaRow>(new CommandDefinition(@"
UPDATE acompanhamento_proposta_versao
   SET status = @Status, valor_mensal = @ValorMensal, valor_implantacao = @ValorImplantacao, valor_zero_consciente = @ValorZeroConsciente,
       dados = CAST(@Dados AS jsonb), canal = @Canal, destinatario = @Destinatario, apresentada_por = @ApresentadaPor,
       evidencia_mensagem_id = @EvidenciaMensagemId, revisa_versao = @RevisaVersao, resposta = @Resposta, resposta_nota = @RespostaNota,
       resposta_em = @RespostaEm, chave_envio = @ChaveEnvio, chave_resposta = @ChaveResposta, enviada_em = @EnviadaEm, validade_ate = @ValidadeAte,
       passagem_id = @PassagemId, edicao = edicao + 1, atualizada_em = NOW()
 WHERE id = @Id
RETURNING " + PropostaColunas + ";", new
                {
                    v.Id, v.Status, v.ValorMensal, v.ValorImplantacao, v.ValorZeroConsciente, Dados = JsonSerializer.Serialize(v.Dados, JsonOpcoes), v.Canal,
                    v.Destinatario, v.ApresentadaPor, v.EvidenciaMensagemId, v.RevisaVersao, v.Resposta, v.RespostaNota, RespostaEm = v.RespostaEm?.UtcDateTime,
                    v.ChaveEnvio, v.ChaveResposta, EnviadaEm = v.EnviadaEm?.UtcDateTime, ValidadeAte = v.ValidadeAte?.ToDateTime(TimeOnly.MinValue), v.PassagemId
                }, _tx, cancellationToken: ct));
                return row.ToVersao();
            }

            public async Task EspelharPropostaAsync(PropostaVersao v, int? usuarioId, CancellationToken ct)
            {
                if (!_crm) return;
                await ExecAsync("UPDATE crm_oportunidade SET proposta_json = CAST(@J AS jsonb), proposta_enviada_em = @Em, updated_at = NOW() WHERE id = @O;",
                    new { J = PropostaRegras.Espelho(v), Em = v.EnviadaEm?.UtcDateTime, O = v.OportunidadeId }, ct);
                if (usuarioId.HasValue)
                    await ExecAsync("INSERT INTO crm_oportunidade_historico (id_oportunidade, id_usuario, acao, detalhe) VALUES (@O, @U, 'Proposta registrada', @D);",
                        new { O = v.OportunidadeId, U = usuarioId, D = $"Versão {v.Versao} enviada por {v.Canal} para {v.Destinatario}." }, ct);
            }

            public async Task<bool> MensagemConfereAsync(Guid mensagemId, CancellationToken ct)
            {
                if (!await AcompanhamentoSql.TabelaExisteAsync(_cx, _tx, "gestao_mensagem", ct)) return false;
                return await _cx.ExecuteScalarAsync<bool>(new CommandDefinition(@"
SELECT EXISTS (SELECT 1 FROM gestao_mensagem m
                WHERE m.id = @M AND m.direcao = 'saida' AND m.estado IN ('enviado', 'entregue', 'lido', 'enviado_por_fora')
                  AND (m.oportunidade_id = @O OR (@E::uuid IS NOT NULL AND m.estabelecimento_id = @E)));",
                    new { M = mensagemId, O = Estado.OportunidadeId, E = Estado.EstabelecimentoId }, _tx, cancellationToken: ct));
            }

            public Task EventoAsync(string tipo, object detalhe, int? usuarioId, CancellationToken ct) =>
                ExecAsync("INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, usuario_id) VALUES (@Id, @Tipo, CAST(@D AS jsonb), @U);",
                    new { Id = Estado.Id, Tipo = tipo, D = JsonSerializer.Serialize(detalhe, JsonOpcoes), U = usuarioId }, ct);

            /// <summary>Quem saiu do caminho nao deixa versao aberta nem passo aberto: nada disso volta a contar ao retomar.</summary>
            private async Task EncerrarAbertosAsync(string? motivo, int? usuarioId, bool propostas, bool passo, CancellationToken ct)
            {
                if (propostas && Estado.OportunidadeId.HasValue)
                {
                    var guardadas = (await _cx.QueryAsync<int>(new CommandDefinition(@"
UPDATE acompanhamento_proposta_versao SET status = 'guardada', edicao = edicao + 1, atualizada_em = NOW()
 WHERE oportunidade_id = @O AND status IN ('rascunho', 'enviada', 'em_negociacao') RETURNING versao;",
                        new { O = Estado.OportunidadeId }, _tx, cancellationToken: ct))).ToList();
                    if (guardadas.Count > 0) await EventoAsync("proposta_guardada", new { versoes = guardadas, motivo }, usuarioId, ct);
                }
                if (!passo) return;

                var cancelados = (await _cx.QueryAsync<(Guid Id, string Tipo, DateTime Data)>(new CommandDefinition(@"
UPDATE acompanhamento_proximo_passo
   SET estado = 'cancelado', encerrado_em = NOW(), encerrado_por = @U, encerrado_motivo = @M
 WHERE acompanhamento_id = @Id AND estado = 'aberto' RETURNING id, tipo, data;",
                    new { Id = Estado.Id, U = usuarioId, M = motivo ?? "Encerrado com a passagem." }, _tx, cancellationToken: ct))).ToList();
                if (cancelados.Count == 0) return;
                foreach (var c in cancelados)
                    await EventoAsync("atividade_cancelada", new { passoId = c.Id, tipo = c.Tipo, data = DateOnly.FromDateTime(c.Data), motivo, automatica = true }, usuarioId, ct);
                // O relacionamento mudou: quem estava com a versao antiga na tela recebe 409 em vez de agir sobre um passo que acabou.
                await ExecAsync("UPDATE acompanhamento_relacionamento SET versao = versao + 1, atualizado_em = NOW() WHERE acompanhamento_id = @Id;", new { Id = Estado.Id }, ct);
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
                    case EfeitoPassagem.EnviarProposta:
                        // A versao ja foi marcada como enviada por quem registrou o envio; aqui so a coluna do /crm acompanha.
                        await CrmAsync("UPDATE crm_oportunidade SET coluna = 'proposta', updated_at = NOW() WHERE id = @O AND status::text = 'ativo';", ct);
                        break;

                    case EfeitoPassagem.VoltarAConversar:
                        // A proposta fica guardada como versao; o proximo envio abre outra.
                        await EncerrarAbertosAsync(a.Motivo, a.UsuarioId, propostas: true, passo: false, ct);
                        await CrmAsync("UPDATE crm_oportunidade SET coluna = 'conversa', updated_at = NOW() WHERE id = @O AND status::text = 'ativo';", ct);
                        break;

                    case EfeitoPassagem.FecharNaConversa:
                    case EfeitoPassagem.TesteSemCompromisso:
                        await EncerrarAbertosAsync(a.Motivo ?? a.Definicao.Nome, a.UsuarioId, propostas: true, passo: true, ct);
                        await CriarLojaEmImplantacaoAsync(a, passagemId, ct);
                        break;

                    case EfeitoPassagem.PropostaAceita:
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
                        await EncerrarAbertosAsync(a.Motivo ?? a.Definicao.Nome, a.UsuarioId, propostas: true, passo: true, ct);
                        await SituacaoAsync("bloquear", a, ct, motivo: a.Motivo ?? "Saída encerrada");
                        break;

                    case EfeitoPassagem.DeclararPausa:
                        await ExecAsync("UPDATE acompanhamento SET pausa_ate = @Ate WHERE id = @Id;", new { Id = Estado.Id, Ate = a.PausaAte }, ct);
                        break;

                    case EfeitoPassagem.VoltarDaPausa:
                        await ExecAsync("UPDATE acompanhamento SET pausa_ate = NULL WHERE id = @Id;", new { Id = Estado.Id }, ct);
                        break;

                    case EfeitoPassagem.Perder:
                        await EncerrarAbertosAsync(a.Motivo, a.UsuarioId, propostas: true, passo: true, ct);
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

                PropostaVersao? proposta = null;
                if (a.Efeito == EfeitoPassagem.PropostaAceita)
                {
                    proposta = await PropostaVigenteAsync(ct)
                        ?? throw new AcompanhamentoException(422, CodigosAcompanhamento.RequisitosPendentes, "Não há proposta enviada para aceitar.");
                    var aposValidade = PropostaRegras.Vencida(proposta, FusoComercial.Data(a.Agora));
                    proposta.Status = StatusProposta.Aceita;
                    proposta.PassagemId = passagemId;
                    await SalvarPropostaAsync(proposta, ct);
                    await EncerrarAbertosAsync(a.Motivo, a.UsuarioId, propostas: true, passo: false, ct); // o rascunho de revisao que sobrou
                    await EventoAsync("proposta_aceita", new
                    {
                        proposta.Versao, proposta.ValorMensal, proposta.ValorImplantacao, proposta.ValorZeroConsciente,
                        aceite = Limpo(d.Aceite)?.ToLowerInvariant(), aceitaAposValidade = aposValidade
                    }, a.UsuarioId, ct);
                    await ExecAsync("UPDATE acompanhamento SET valor_mensal = @V WHERE id = @Id;", new { Id = Estado.Id, V = proposta.ValorMensal }, ct);
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
                var hoje = FusoComercial.Data(a.Agora);
                // O valor vem da versao aceita ou do que foi combinado; "Teste sem compromisso" segue sem valor (cobranca so na ativacao).
                var valorMensal = proposta != null ? proposta.ValorMensal!.Value : d.ValorMensal ?? 0m;
                var marketing = proposta?.Dados.MarketingMensalidade ?? 0m;

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
                    MensalidadeSaas = valorMensal - marketing,
                    MensalidadeMarketing = marketing,
                    MarketingValorFixo = proposta?.Dados.MarketingValorFixo ?? 0m,
                    ImplantacaoValor = proposta?.ValorImplantacao ?? d.ValorImplantacao ?? 0m,
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
