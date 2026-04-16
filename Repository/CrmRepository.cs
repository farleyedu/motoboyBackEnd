using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Model.CRM;
using APIBack.Repository.Interface;
using Dapper;
using Npgsql;

namespace APIBack.Repository
{
    public class CrmRepository : ICrmRepository
    {
        private readonly NpgsqlDataSource _dataSource;

        public CrmRepository(NpgsqlDataSource dataSource)
        {
            _dataSource = dataSource;
        }

        public async Task<IReadOnlyCollection<CrmOportunidadeRow>> ListarOportunidadesAsync(string? status, string? tipo, string? responsavel, string? busca)
        {
            const string sql = @"
SELECT id,
       nome_empresa,
       nome_contato,
       telefone,
       email,
       cnpj,
       tipo,
       responsavel,
       coluna,
       status,
       substatus,
       proxima_acao,
       estimativa_json,
       proposta_json,
       proposta_enviada_em,
       reserva_reuniao_id   AS reuniao_reserva_id,
       reuniao_local,
       reserva_resposta_id  AS resposta_reserva_id,
       reserva_pagamento_id AS pagamento_reserva_id,
       dt_resposta_prevista,
       dt_pagamento_prevista,
       prox_acao_data,
       valor_mensal_est,
       valor_impl_est,
       id_empresa,
       created_at,
       updated_at,
       deleted_at,
       closed_at
  FROM crm_oportunidade
 WHERE (@Status IS NULL OR status::text = @Status)
   AND (@Tipo IS NULL OR tipo::text = @Tipo)
   AND (@Responsavel IS NULL OR responsavel::text = @Responsavel)
   AND (
       @Busca IS NULL
       OR nome_empresa ILIKE '%' || @Busca || '%'
       OR nome_contato ILIKE '%' || @Busca || '%'
       OR telefone ILIKE '%' || @Busca || '%'
       OR COALESCE(email, '') ILIKE '%' || @Busca || '%'
   )
 ORDER BY CASE coluna
            WHEN 'conversa' THEN 1
            WHEN 'reuniao' THEN 2
            WHEN 'proposta' THEN 3
            WHEN 'quase' THEN 4
            WHEN 'aguardando' THEN 5
            ELSE 99
          END,
          updated_at DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmOportunidadeRow>(sql, new
            {
                Status = NormalizeNullable(status),
                Tipo = NormalizeNullable(tipo),
                Responsavel = NormalizeNullable(responsavel),
                Busca = NormalizeNullable(busca)
            });

            return rows.ToList();
        }

        public async Task<CrmOportunidadeRow?> ObterOportunidadeAsync(Guid oportunidadeId)
        {
            const string sql = @"
SELECT id,
       nome_empresa,
       nome_contato,
       telefone,
       email,
       cnpj,
       tipo,
       responsavel,
       coluna,
       status,
       substatus,
       proxima_acao,
       estimativa_json,
       proposta_json,
       proposta_enviada_em,
       reserva_reuniao_id   AS reuniao_reserva_id,
       reuniao_local,
       reserva_resposta_id  AS resposta_reserva_id,
       reserva_pagamento_id AS pagamento_reserva_id,
       dt_resposta_prevista,
       dt_pagamento_prevista,
       prox_acao_data,
       valor_mensal_est,
       valor_impl_est,
       id_empresa,
       created_at,
       updated_at,
       deleted_at,
       closed_at
  FROM crm_oportunidade
 WHERE id = @OportunidadeId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmOportunidadeRow>(sql, new { OportunidadeId = oportunidadeId });
        }

        public async Task<Guid> CriarOportunidadeAsync(CrmOportunidadeRow oportunidade)
        {
            const string sql = @"
INSERT INTO crm_oportunidade (
    nome_empresa, nome_contato, telefone, email, cnpj, tipo, responsavel, coluna, status,
    substatus, proxima_acao, estimativa_json, proposta_json, proposta_enviada_em,
    reserva_reuniao_id, reuniao_local, reserva_resposta_id, reserva_pagamento_id,
    dt_resposta_prevista, dt_pagamento_prevista, closed_at)
VALUES (
    @NomeEmpresa, @NomeContato, @Telefone, @Email, @Cnpj, @Tipo::crm_tipo_servico, @Responsavel::crm_responsavel, @Coluna::crm_kanban_col, @Status::crm_oportunidade_status,
    @Substatus, @ProximaAcao, COALESCE(@EstimativaJson::jsonb, '{}'::jsonb), @PropostaJson::jsonb, @PropostaEnviadaEm,
    @ReuniaoReservaId, @ReuniaoLocal, @RespostaReservaId, @PagamentoReservaId,
    @DtRespostaPrevista, @DtPagamentoPrevista, @ClosedAt)
RETURNING id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                oportunidade.NomeEmpresa,
                oportunidade.NomeContato,
                oportunidade.Telefone,
                oportunidade.Email,
                oportunidade.Cnpj,
                oportunidade.Tipo,
                oportunidade.Responsavel,
                oportunidade.Coluna,
                oportunidade.Status,
                oportunidade.Substatus,
                oportunidade.ProximaAcao,
                oportunidade.EstimativaJson,
                oportunidade.PropostaJson,
                oportunidade.PropostaEnviadaEm,
                oportunidade.ReuniaoReservaId,
                oportunidade.ReuniaoLocal,
                oportunidade.RespostaReservaId,
                oportunidade.PagamentoReservaId,
                oportunidade.DtRespostaPrevista,
                oportunidade.DtPagamentoPrevista,
                oportunidade.ClosedAt
            });
        }

        public async Task AtualizarOportunidadeAsync(CrmOportunidadeRow oportunidade)
        {
            const string sql = @"
UPDATE crm_oportunidade
   SET nome_empresa = @NomeEmpresa,
       nome_contato = @NomeContato,
       telefone = @Telefone,
       email = @Email,
       cnpj = @Cnpj,
       responsavel = @Responsavel::crm_responsavel,
       status = @Status::crm_oportunidade_status,
       substatus = @Substatus,
       proxima_acao = @ProximaAcao,
       estimativa_json = COALESCE(@EstimativaJson::jsonb, '{}'::jsonb),
       proposta_json = @PropostaJson::jsonb,
       proposta_enviada_em = @PropostaEnviadaEm,
       reuniao_local = @ReuniaoLocal,
       closed_at = @ClosedAt,
       updated_at = NOW()
 WHERE id = @Id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                oportunidade.Id,
                oportunidade.NomeEmpresa,
                oportunidade.NomeContato,
                oportunidade.Telefone,
                oportunidade.Email,
                oportunidade.Cnpj,
                oportunidade.Responsavel,
                oportunidade.Status,
                oportunidade.Substatus,
                oportunidade.ProximaAcao,
                oportunidade.EstimativaJson,
                oportunidade.PropostaJson,
                oportunidade.PropostaEnviadaEm,
                oportunidade.ReuniaoLocal,
                oportunidade.ClosedAt
            });
        }

        public async Task AtualizarMovimentacaoOportunidadeAsync(
            Guid oportunidadeId,
            string coluna,
            string? status,
            string? substatus,
            string? propostaJson,
            DateTimeOffset? propostaEnviadaEm,
            long? reuniaoReservaId,
            string? reuniaoLocal,
            long? respostaReservaId,
            long? pagamentoReservaId,
            DateTime? dtRespostaPrevista,
            DateTime? dtPagamentoPrevista,
            DateTimeOffset? closedAt)
        {
            const string sql = @"
UPDATE crm_oportunidade
   SET coluna = @Coluna::crm_kanban_col,
       status = @Status::crm_oportunidade_status,
       substatus = @Substatus,
       proposta_json = @PropostaJson::jsonb,
       proposta_enviada_em = @PropostaEnviadaEm,
       reserva_reuniao_id = @ReuniaoReservaId,
       reuniao_local = @ReuniaoLocal,
       reserva_resposta_id = @RespostaReservaId,
       reserva_pagamento_id = @PagamentoReservaId,
       dt_resposta_prevista = @DtRespostaPrevista,
       dt_pagamento_prevista = @DtPagamentoPrevista,
       closed_at = @ClosedAt,
       updated_at = NOW()
 WHERE id = @OportunidadeId;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                OportunidadeId = oportunidadeId,
                Coluna = coluna,
                Status = NormalizeNullable(status),
                Substatus = NormalizeNullable(substatus),
                PropostaJson = propostaJson,
                PropostaEnviadaEm = propostaEnviadaEm,
                ReuniaoReservaId = reuniaoReservaId,
                ReuniaoLocal = NormalizeNullable(reuniaoLocal),
                RespostaReservaId = respostaReservaId,
                PagamentoReservaId = pagamentoReservaId,
                DtRespostaPrevista = dtRespostaPrevista?.Date,
                DtPagamentoPrevista = dtPagamentoPrevista?.Date,
                ClosedAt = closedAt
            });
        }

        public async Task<IReadOnlyCollection<CrmHistoricoRow>> ListarHistoricoOportunidadeAsync(Guid oportunidadeId)
        {
            const string sql = @"
SELECT id,
       id_oportunidade AS oportunidade_id,
       NULL::uuid      AS contrato_id,
       id_usuario      AS usuario_id,
       acao,
       detalhe,
       created_at
  FROM crm_oportunidade_historico
 WHERE id_oportunidade = @OportunidadeId
 ORDER BY created_at DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmHistoricoRow>(sql, new { OportunidadeId = oportunidadeId });
            return rows.ToList();
        }

        public async Task AdicionarHistoricoOportunidadeAsync(Guid oportunidadeId, int usuarioId, string acao, string? detalhe)
        {
            const string sql = @"
INSERT INTO crm_oportunidade_historico (id_oportunidade, id_usuario, acao, detalhe)
VALUES (@OportunidadeId, @UsuarioId, @Acao, @Detalhe);";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                OportunidadeId = oportunidadeId,
                UsuarioId = usuarioId,
                Acao = acao,
                Detalhe = NormalizeNullable(detalhe)
            });
        }

        public async Task<CrmReservaRow?> ObterReservaAsync(long reservaId)
        {
            const string sql = @"
SELECT id,
       data_reserva,
       hora_inicio,
       hora_fim,
       observacoes
  FROM reservas
 WHERE id = @ReservaId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmReservaRow>(sql, new { ReservaId = reservaId });
        }

        public async Task<Guid?> ObterContratoIdPorOportunidadeAsync(Guid oportunidadeId)
        {
            const string sql = @"
SELECT id
  FROM crm_contrato
 WHERE id_oportunidade = @OportunidadeId
 ORDER BY created_at DESC
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.ExecuteScalarAsync<Guid?>(sql, new { OportunidadeId = oportunidadeId });
        }

        public async Task<CrmCloseOpportunityResult> FecharOportunidadeAsync(CrmCloseOpportunityCommand command)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();

            var oportunidade = await connection.QueryFirstOrDefaultAsync<CrmOportunidadeRow>(@"
SELECT id,
       nome_empresa,
       nome_contato,
       telefone,
       email,
       cnpj,
       tipo,
       responsavel,
       coluna,
       status,
       substatus,
       proxima_acao,
       estimativa_json,
       proposta_json,
       proposta_enviada_em,
       reserva_reuniao_id   AS reuniao_reserva_id,
       reuniao_local,
       reserva_resposta_id  AS resposta_reserva_id,
       reserva_pagamento_id AS pagamento_reserva_id,
       dt_resposta_prevista,
       dt_pagamento_prevista,
       prox_acao_data,
       valor_mensal_est,
       valor_impl_est,
       id_empresa,
       created_at,
       updated_at,
       deleted_at,
       closed_at
  FROM crm_oportunidade
 WHERE id = @OportunidadeId
 FOR UPDATE;",
                new { command.OportunidadeId },
                transaction);

            if (oportunidade == null)
            {
                throw new KeyNotFoundException("Oportunidade nao encontrada.");
            }

            var empresaId = await ObterEmpresaExistenteAsync(connection, transaction, command.Cnpj, command.NomeEmpresa)
                ?? await CriarEmpresaAsync(connection, transaction, command);
            var tipoEstabelecimentoId = await ResolverTipoEstabelecimentoIdAsync(connection, transaction, command.EstabelecimentoTipoSlug);
            var estabelecimentoId = await ObterEstabelecimentoExistenteAsync(connection, transaction, empresaId)
                ?? await CriarEstabelecimentoAsync(connection, transaction, command, empresaId, tipoEstabelecimentoId);
            var responsavelUserId = await ResolverUsuarioResponsavelAsync(connection, transaction, command.Responsavel);

            await VincularUsuarioEmpresaAsync(connection, transaction, responsavelUserId, empresaId, command.UsuarioId);
            await VincularUsuarioEstabelecimentoAsync(connection, transaction, responsavelUserId, estabelecimentoId, command.UsuarioId);

            var contratoId = await connection.ExecuteScalarAsync<Guid>(@"
INSERT INTO crm_contrato (
    id_oportunidade,
    empresa_id,
    estabelecimento_id,
    tipo,
    status,
    responsavel,
    mensalidade_saas,
    mensalidade_marketing,
    marketing_valor_fixo,
    implantacao_valor,
    implantacao_paga,
    dia_vencimento,
    dt_inicio_cobranca,
    observacoes)
VALUES (
    @OportunidadeId,
    @EmpresaId,
    @EstabelecimentoId,
    @Tipo::crm_tipo_servico,
    @Status::crm_contrato_status,
    @Responsavel::crm_responsavel,
    @MensalidadeSaas,
    @MensalidadeMarketing,
    @MarketingValorFixo,
    @ImplantacaoValor,
    @ImplantacaoJaPaga,
    @DiaVencimento,
    @DataInicioCobranca,
    @Observacoes)
RETURNING id;",
                new
                {
                    command.OportunidadeId,
                    EmpresaId = empresaId,
                    EstabelecimentoId = estabelecimentoId,
                    command.Tipo,
                    Status = command.StatusInicial,
                    command.Responsavel,
                    command.MensalidadeSaas,
                    command.MensalidadeMarketing,
                    command.MarketingValorFixo,
                    command.ImplantacaoValor,
                    ImplantacaoJaPaga = command.ImplantacaoJaPaga,
                    command.DiaVencimento,
                    DataInicioCobranca = command.DataInicioCobranca.Date,
                    Observacoes = NormalizeNullable(command.Observacoes)
                },
                transaction);

            await InserirHistoricoContratoAsync(
                connection,
                transaction,
                contratoId,
                command.UsuarioId,
                "contrato_criado",
                $"Contrato criado a partir da oportunidade {command.NomeEmpresa}.");

            Guid? implantacaoId = null;
            if (command.ImplantacaoValor > 0)
            {
                implantacaoId = await connection.ExecuteScalarAsync<Guid>(@"
INSERT INTO crm_implantacao (
    contrato_id,
    status,
    valor_total,
    valor_pago,
    paga,
    observacoes)
VALUES (
    @ContratoId,
    @Status::crm_implantacao_status,
    @ValorTotal,
    @ValorPago,
    @Paga,
    @Observacoes)
RETURNING id;",
                    new
                    {
                        ContratoId = contratoId,
                        Status = command.ImplantacaoJaPaga ? "concluida" : "aguardando",
                        ValorTotal = command.ImplantacaoValor,
                        ValorPago = command.ImplantacaoJaPaga ? command.ImplantacaoValor : 0m,
                        Paga = command.ImplantacaoJaPaga,
                        Observacoes = NormalizeNullable(command.Observacoes)
                    },
                    transaction);
            }

            var lancamentoIds = new List<Guid>();
            var dataVencimentoBase = CalcularDataVencimento(command.DataInicioCobranca.Date, command.DiaVencimento);

            if (command.MensalidadeSaas > 0)
            {
                lancamentoIds.Add(await InserirLancamentoAsync(
                    connection,
                    transaction,
                    contratoId,
                    empresaId,
                    "mensalidade_saas",
                    $"Mensalidade SaaS - {command.NomeEmpresa}",
                    command.DataInicioCobranca.Date,
                    dataVencimentoBase,
                    command.MensalidadeSaas,
                    0m,
                    null,
                    "pendente"));
            }

            if (command.MensalidadeMarketing > 0)
            {
                lancamentoIds.Add(await InserirLancamentoAsync(
                    connection,
                    transaction,
                    contratoId,
                    empresaId,
                    "mensalidade_marketing",
                    $"Mensalidade Marketing - {command.NomeEmpresa}",
                    command.DataInicioCobranca.Date,
                    dataVencimentoBase,
                    command.MensalidadeMarketing,
                    0m,
                    null,
                    "pendente"));
            }

            if (command.MarketingValorFixo > 0)
            {
                lancamentoIds.Add(await InserirLancamentoAsync(
                    connection,
                    transaction,
                    contratoId,
                    empresaId,
                    "marketing_valor_fixo",
                    $"Valor fixo Marketing - {command.NomeEmpresa}",
                    command.DataInicioCobranca.Date,
                    dataVencimentoBase,
                    command.MarketingValorFixo,
                    0m,
                    null,
                    "pendente"));
            }

            if (command.ImplantacaoValor > 0)
            {
                lancamentoIds.Add(await InserirLancamentoAsync(
                    connection,
                    transaction,
                    contratoId,
                    empresaId,
                    "implantacao_saas",
                    $"Implantacao SaaS - {command.NomeEmpresa}",
                    command.DataInicioCobranca.Date,
                    command.DataInicioCobranca.Date,
                    command.ImplantacaoValor,
                    command.ImplantacaoJaPaga ? command.ImplantacaoValor : 0m,
                    command.ImplantacaoJaPaga ? command.DataInicioCobranca.Date : null,
                    command.ImplantacaoJaPaga ? "pago" : "pendente"));
            }

            await connection.ExecuteAsync(@"
UPDATE crm_oportunidade
   SET status = 'fechado',
       closed_at = NOW(),
       updated_at = NOW()
 WHERE id = @OportunidadeId;",
                new { command.OportunidadeId },
                transaction);

            await InserirHistoricoOportunidadeAsync(
                connection,
                transaction,
                command.OportunidadeId,
                command.UsuarioId,
                "negocio_fechado",
                $"Contrato {contratoId} criado para {command.NomeEmpresa}.");

            await transaction.CommitAsync();

            return new CrmCloseOpportunityResult
            {
                ContratoId = contratoId,
                EmpresaId = empresaId,
                EstabelecimentoId = estabelecimentoId,
                LancamentoIds = lancamentoIds,
                ImplantacaoId = implantacaoId
            };
        }

        public async Task<int> SincronizarContratosLegadosAsync()
        {
            const string sql = @"
WITH empresas_alvo AS (
    SELECT e.id AS empresa_id,
           e.nome_fantasia AS nome_empresa,
           COALESCE(NULLIF(e.razao_social, ''), e.nome_fantasia) AS nome_contato,
           e.telefone,
           e.email,
           e.cnpj,
           COALESCE(e.data_criacao::date, CURRENT_DATE) AS data_inicio_base
      FROM empresas e
     WHERE NOT EXISTS (
           SELECT 1
             FROM crm_contrato c
            WHERE c.empresa_id = e.id
     )
),
estabelecimento_principal AS (
    SELECT ranked.empresa_id,
           ranked.estabelecimento_id,
           ranked.nome_estabelecimento,
           ranked.ativo,
           ranked.status_estabelecimento
      FROM (
            SELECT est.id_empresa AS empresa_id,
                   est.id AS estabelecimento_id,
                   est.nome_fantasia AS nome_estabelecimento,
                   COALESCE(est.ativo, TRUE) AS ativo,
                   COALESCE(est.status, 'ativo') AS status_estabelecimento,
                   ROW_NUMBER() OVER (
                       PARTITION BY est.id_empresa
                       ORDER BY CASE
                                   WHEN COALESCE(est.ativo, TRUE) = TRUE
                                    AND COALESCE(est.status, 'ativo') IN ('ativo', 'trial') THEN 0
                                   ELSE 1
                                END,
                                CASE WHEN est.tipo_unidade::text = 'matriz' THEN 0 ELSE 1 END,
                                est.data_criacao,
                                est.id
                   ) AS rn
              FROM estabelecimentos est
      ) ranked
     WHERE ranked.rn = 1
),
fonte AS (
    SELECT alvo.empresa_id,
           alvo.nome_empresa,
           alvo.nome_contato,
           alvo.telefone,
           alvo.email,
           alvo.cnpj,
           ep.estabelecimento_id,
           COALESCE(NULLIF(opp.tipo::text, ''), 'saas') AS tipo,
           CASE
               WHEN ep.ativo = FALSE THEN 'pausado'
               WHEN ep.status_estabelecimento IN ('ativo', 'trial') THEN 'ativo'
               ELSE 'pausado'
           END AS status,
           COALESCE(NULLIF(opp.responsavel::text, ''), 'farley') AS responsavel,
           COALESCE(NULLIF(opp.proposta_json ->> 'saaSMensalidade', '')::numeric, 0) AS mensalidade_saas,
           COALESCE(NULLIF(opp.proposta_json ->> 'marketingMensalidade', '')::numeric, 0) AS mensalidade_marketing,
           COALESCE(NULLIF(opp.proposta_json ->> 'marketingValorFixo', '')::numeric, 0) AS marketing_valor_fixo,
           COALESCE(NULLIF(opp.proposta_json ->> 'implantacao', '')::numeric, 0) AS implantacao_valor,
           CASE
               WHEN COALESCE(NULLIF(opp.proposta_json ->> 'implantacao', '')::numeric, 0) <= 0 THEN TRUE
               ELSE FALSE
           END AS implantacao_paga,
           1 AS dia_vencimento,
           COALESCE(opp.closed_at::date, alvo.data_inicio_base) AS data_inicio_cobranca,
           CASE
               WHEN opp.id IS NOT NULL THEN 'Contrato legado equalizado com proposta historica do CRM. Revisar vencimento e status.'
               ELSE 'Contrato legado equalizado a partir de empresa existente. Revisar valores, vencimento e responsavel.'
           END AS observacoes,
           opp.id AS oportunidade_id
      FROM empresas_alvo alvo
      JOIN estabelecimento_principal ep ON ep.empresa_id = alvo.empresa_id
      LEFT JOIN LATERAL (
            SELECT o.id,
                   o.tipo,
                   o.responsavel,
                   o.proposta_json,
                   o.closed_at,
                   o.updated_at,
                   o.created_at
              FROM crm_oportunidade o
             WHERE (
                       o.id_empresa = alvo.empresa_id
                    OR (
                        NULLIF(regexp_replace(COALESCE(alvo.cnpj, ''), '[^0-9]', '', 'g'), '') IS NOT NULL
                        AND regexp_replace(COALESCE(o.cnpj, ''), '[^0-9]', '', 'g') = regexp_replace(COALESCE(alvo.cnpj, ''), '[^0-9]', '', 'g')
                    )
                    OR lower(btrim(o.nome_empresa)) = lower(btrim(alvo.nome_empresa))
                   )
               AND (o.status::text = 'fechado' OR o.closed_at IS NOT NULL)
             ORDER BY CASE
                          WHEN o.status::text = 'fechado' THEN 0
                          WHEN o.closed_at IS NOT NULL THEN 1
                          ELSE 2
                      END,
                      o.closed_at DESC NULLS LAST,
                      o.updated_at DESC,
                      o.created_at DESC
             LIMIT 1
      ) opp ON TRUE
),
oportunidades_legado AS (
    INSERT INTO crm_oportunidade (
        nome_empresa,
        nome_contato,
        telefone,
        email,
        cnpj,
        tipo,
        responsavel,
        coluna,
        status,
        substatus,
        proxima_acao,
        estimativa_json,
        proposta_json,
        proposta_enviada_em,
        id_empresa,
        valor_mensal_est,
        valor_impl_est,
        closed_at
    )
    SELECT fonte.nome_empresa,
           fonte.nome_contato,
           fonte.telefone,
           fonte.email,
           fonte.cnpj,
           fonte.tipo::crm_tipo_servico,
           fonte.responsavel::crm_responsavel,
           'aguardando'::crm_kanban_col,
           'fechado'::crm_oportunidade_status,
           'legacy_equalized',
           'Equalizacao automatica de contrato legado',
           '{}'::jsonb,
           jsonb_build_object(
               'saaSMensalidade', fonte.mensalidade_saas,
               'marketingMensalidade', fonte.mensalidade_marketing,
               'marketingValorFixo', fonte.marketing_valor_fixo,
               'implantacao', fonte.implantacao_valor,
               'observacoes', fonte.observacoes,
               'enviadaEm', fonte.data_inicio_cobranca
           ),
           fonte.data_inicio_cobranca,
           fonte.empresa_id,
           fonte.mensalidade_saas + fonte.mensalidade_marketing + fonte.marketing_valor_fixo,
           fonte.implantacao_valor,
           NOW()
      FROM fonte
     WHERE fonte.oportunidade_id IS NULL
    RETURNING id, id_empresa
),
fonte_resolvida AS (
    SELECT fonte.empresa_id,
           fonte.estabelecimento_id,
           fonte.tipo,
           fonte.status,
           fonte.responsavel,
           fonte.mensalidade_saas,
           fonte.mensalidade_marketing,
           fonte.marketing_valor_fixo,
           fonte.implantacao_valor,
           fonte.implantacao_paga,
           fonte.dia_vencimento,
           fonte.data_inicio_cobranca,
           fonte.observacoes,
           COALESCE(fonte.oportunidade_id, oportunidades_legado.id) AS oportunidade_id
      FROM fonte
      LEFT JOIN oportunidades_legado ON oportunidades_legado.id_empresa = fonte.empresa_id
),
inserted AS (
    INSERT INTO crm_contrato (
        id_oportunidade,
        empresa_id,
        estabelecimento_id,
        tipo,
        status,
        responsavel,
        mensalidade_saas,
        mensalidade_marketing,
        marketing_valor_fixo,
        implantacao_valor,
        implantacao_paga,
        dia_vencimento,
        dt_inicio_cobranca,
        observacoes
    )
    SELECT fonte_resolvida.oportunidade_id,
           fonte_resolvida.empresa_id,
           fonte_resolvida.estabelecimento_id,
           fonte_resolvida.tipo::crm_tipo_servico,
           fonte_resolvida.status::crm_contrato_status,
           fonte_resolvida.responsavel::crm_responsavel,
           fonte_resolvida.mensalidade_saas,
           fonte_resolvida.mensalidade_marketing,
           fonte_resolvida.marketing_valor_fixo,
           fonte_resolvida.implantacao_valor,
           fonte_resolvida.implantacao_paga,
           fonte_resolvida.dia_vencimento,
           fonte_resolvida.data_inicio_cobranca,
           fonte_resolvida.observacoes
      FROM fonte_resolvida
    RETURNING id
)
SELECT COUNT(*)
  FROM inserted;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await connection.ExecuteAsync("SELECT pg_advisory_xact_lock(hashtext('crm_sync_contratos_legados'));", transaction: transaction);
            var inseridos = await connection.ExecuteScalarAsync<int>(sql, transaction: transaction);
            await transaction.CommitAsync();
            return inseridos;
        }

        public async Task<IReadOnlyCollection<CrmContratoRow>> ListarContratosAsync(string? status)
        {
            const string sql = @"
SELECT c.id,
       c.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.estabelecimento_id,
       est.nome_fantasia AS nome_estabelecimento,
       c.id_oportunidade AS oportunidade_id,
       c.tipo,
       c.status,
       c.responsavel,
       c.mensalidade_saas,
       c.mensalidade_marketing,
       c.marketing_valor_fixo,
       c.implantacao_valor,
       c.implantacao_paga,
       c.dia_vencimento,
       c.dt_inicio_cobranca AS data_inicio_cobranca,
       c.observacoes,
       c.created_at,
       c.updated_at
  FROM crm_contrato c
  JOIN empresas e ON e.id = c.empresa_id
  JOIN estabelecimentos est ON est.id = c.estabelecimento_id
 WHERE (
       @Status IS NOT NULL AND c.status::text = @Status
   )
    OR (
       @Status IS NULL AND c.status::text <> 'cancelado'
   )
 ORDER BY c.updated_at DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmContratoRow>(sql, new { Status = NormalizeNullable(status) });
            return rows.ToList();
        }

        public async Task<CrmContratoRow?> ObterContratoAsync(Guid contratoId)
        {
            const string sql = @"
SELECT c.id,
       c.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.estabelecimento_id,
       est.nome_fantasia AS nome_estabelecimento,
       c.id_oportunidade AS oportunidade_id,
       c.tipo,
       c.status,
       c.responsavel,
       c.mensalidade_saas,
       c.mensalidade_marketing,
       c.marketing_valor_fixo,
       c.implantacao_valor,
       c.implantacao_paga,
       c.dia_vencimento,
       c.dt_inicio_cobranca AS data_inicio_cobranca,
       c.observacoes,
       c.created_at,
       c.updated_at
  FROM crm_contrato c
  JOIN empresas e ON e.id = c.empresa_id
  JOIN estabelecimentos est ON est.id = c.estabelecimento_id
 WHERE c.id = @ContratoId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmContratoRow>(sql, new { ContratoId = contratoId });
        }

        public async Task AtualizarContratoAsync(Guid contratoId, string? status, string? responsavel, int? diaVencimento, DateTime? dataInicioCobranca, decimal? mensalidadeSaas, decimal? mensalidadeMarketing, decimal? marketingValorFixo, string? observacoes)
        {
            const string sql = @"
UPDATE crm_contrato
   SET status = COALESCE(@Status::crm_contrato_status, status),
       responsavel = COALESCE(@Responsavel::crm_responsavel, responsavel),
       dia_vencimento = COALESCE(@DiaVencimento, dia_vencimento),
       dt_inicio_cobranca = COALESCE(@DataInicioCobranca, dt_inicio_cobranca),
       mensalidade_saas = COALESCE(@MensalidadeSaas, mensalidade_saas),
       mensalidade_marketing = COALESCE(@MensalidadeMarketing, mensalidade_marketing),
       marketing_valor_fixo = COALESCE(@MarketingValorFixo, marketing_valor_fixo),
       observacoes = COALESCE(@Observacoes, observacoes),
       canceled_at = CASE
                        WHEN COALESCE(@Status::crm_contrato_status, status)::text = 'cancelado' THEN NOW()
                        ELSE canceled_at
                     END,
       updated_at = NOW()
 WHERE id = @ContratoId;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                ContratoId = contratoId,
                Status = NormalizeNullable(status),
                Responsavel = NormalizeNullable(responsavel),
                DiaVencimento = diaVencimento,
                DataInicioCobranca = dataInicioCobranca?.Date,
                MensalidadeSaas = mensalidadeSaas,
                MensalidadeMarketing = mensalidadeMarketing,
                MarketingValorFixo = marketingValorFixo,
                Observacoes = NormalizeNullable(observacoes)
            });
        }

        public async Task<IReadOnlyCollection<CrmHistoricoRow>> ListarHistoricoContratoAsync(Guid contratoId)
        {
            const string sql = @"
SELECT id,
       NULL::uuid AS oportunidade_id,
       contrato_id,
       usuario_id,
       acao,
       detalhe,
       created_at
  FROM crm_contrato_historico
 WHERE contrato_id = @ContratoId
 ORDER BY created_at DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmHistoricoRow>(sql, new { ContratoId = contratoId });
            return rows.ToList();
        }

        public async Task AdicionarHistoricoContratoAsync(Guid contratoId, int usuarioId, string acao, string? detalhe)
        {
            const string sql = @"
INSERT INTO crm_contrato_historico (contrato_id, usuario_id, acao, detalhe)
VALUES (@ContratoId, @UsuarioId, @Acao, @Detalhe);";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                ContratoId = contratoId,
                UsuarioId = usuarioId,
                Acao = acao,
                Detalhe = NormalizeNullable(detalhe)
            });
        }

        public async Task<CrmImplantacaoRow?> ObterImplantacaoAsync(Guid contratoId)
        {
            const string sql = @"
SELECT id,
       contrato_id,
       status,
       valor_total,
       valor_pago,
       paga,
       observacoes,
       created_at,
       updated_at
  FROM crm_implantacao
 WHERE contrato_id = @ContratoId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmImplantacaoRow>(sql, new { ContratoId = contratoId });
        }

        public async Task<IReadOnlyCollection<CrmLancamentoRow>> ListarLancamentosContratoAsync(Guid contratoId)
        {
            const string sql = @"
SELECT l.id,
       l.contrato_id,
       l.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.tipo AS tipo_contrato,
       l.tipo_lancamento,
       l.descricao,
       l.competencia,
       l.data_vencimento,
       l.data_pagamento,
       l.valor_total,
       l.valor_pago,
       l.status,
       l.created_at,
       l.updated_at
  FROM crm_lancamento l
  JOIN empresas e ON e.id = l.empresa_id
  JOIN crm_contrato c ON c.id = l.contrato_id
 WHERE l.contrato_id = @ContratoId
 ORDER BY l.data_vencimento ASC, l.created_at DESC;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmLancamentoRow>(sql, new { ContratoId = contratoId });
            return rows.ToList();
        }

        public async Task<IReadOnlyCollection<CrmLancamentoRow>> ListarLancamentosFinanceiroAsync(DateTime referenciaMes, bool apenasEmAberto)
        {
            string sql;
            if (apenasEmAberto)
            {
                sql = @"
SELECT l.id,
       l.contrato_id,
       l.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.tipo AS tipo_contrato,
       l.tipo_lancamento,
       l.descricao,
       l.competencia,
       l.data_vencimento,
       l.data_pagamento,
       l.valor_total,
       l.valor_pago,
       l.status,
       l.created_at,
       l.updated_at
  FROM crm_lancamento l
  JOIN empresas e ON e.id = l.empresa_id
  JOIN crm_contrato c ON c.id = l.contrato_id
 WHERE l.status IN ('pendente', 'parcial')
 ORDER BY l.data_vencimento ASC, l.created_at DESC;";
                await using var conn1 = await _dataSource.OpenConnectionAsync();
                var rows1 = await conn1.QueryAsync<CrmLancamentoRow>(sql);
                return rows1.ToList();
            }
            else
            {
                sql = @"
SELECT l.id,
       l.contrato_id,
       l.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.tipo AS tipo_contrato,
       l.tipo_lancamento,
       l.descricao,
       l.competencia,
       l.data_vencimento,
       l.data_pagamento,
       l.valor_total,
       l.valor_pago,
       l.status,
       l.created_at,
       l.updated_at
  FROM crm_lancamento l
  JOIN empresas e ON e.id = l.empresa_id
  JOIN crm_contrato c ON c.id = l.contrato_id
 WHERE l.status IN ('pago', 'parcial')
   AND l.data_pagamento IS NOT NULL
   AND DATE_TRUNC('month', l.data_pagamento::timestamp) = DATE_TRUNC('month', @ReferenciaMes::timestamp)
 ORDER BY l.data_pagamento DESC, l.created_at DESC;";
                await using var conn2 = await _dataSource.OpenConnectionAsync();
                var rows2 = await conn2.QueryAsync<CrmLancamentoRow>(sql, new { ReferenciaMes = referenciaMes.Date });
                return rows2.ToList();
            }
        }

        public async Task<CrmLancamentoRow?> ObterLancamentoAsync(Guid lancamentoId)
        {
            const string sql = @"
SELECT l.id,
       l.contrato_id,
       l.empresa_id,
       e.nome_fantasia AS nome_empresa,
       c.tipo AS tipo_contrato,
       l.tipo_lancamento,
       l.descricao,
       l.competencia,
       l.data_vencimento,
       l.data_pagamento,
       l.valor_total,
       l.valor_pago,
       l.status,
       l.created_at,
       l.updated_at
  FROM crm_lancamento l
  JOIN empresas e ON e.id = l.empresa_id
  JOIN crm_contrato c ON c.id = l.contrato_id
 WHERE l.id = @LancamentoId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmLancamentoRow>(sql, new { LancamentoId = lancamentoId });
        }

        public async Task<Guid> CriarLancamentoAsync(CrmLancamentoRow lancamento)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            return await InserirLancamentoAsync(
                connection,
                transaction: null,
                lancamento.ContratoId,
                lancamento.EmpresaId,
                lancamento.TipoLancamento,
                lancamento.Descricao,
                lancamento.Competencia,
                lancamento.DataVencimento,
                lancamento.ValorTotal,
                lancamento.ValorPago,
                lancamento.DataPagamento,
                lancamento.Status);
        }

        public async Task AtualizarLancamentoAsync(Guid lancamentoId, decimal valorPago, DateTime? dataPagamento, string status, DateTime? dataVencimento, decimal? valorTotal)
        {
            const string sql = @"
UPDATE crm_lancamento
   SET valor_pago = @ValorPago,
       data_pagamento = @DataPagamento,
       status = @Status::crm_lancamento_status,
       data_vencimento = COALESCE(@DataVencimento, data_vencimento),
       valor_total = COALESCE(@ValorTotal, valor_total),
       updated_at = NOW()
 WHERE id = @LancamentoId;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                LancamentoId = lancamentoId,
                ValorPago = valorPago,
                DataPagamento = dataPagamento?.Date,
                Status = status,
                DataVencimento = dataVencimento?.Date,
                ValorTotal = valorTotal
            });
        }

        public async Task AtualizarImplantacaoPagamentoAsync(Guid contratoId, decimal valorPago, bool paga)
        {
            const string sql = @"
UPDATE crm_implantacao
   SET valor_pago = @ValorPago,
       paga = @Paga,
       status = CASE
                    WHEN @Paga THEN 'concluida'
                    WHEN @ValorPago > 0 THEN 'em_andamento'
                    ELSE status
                END,
       updated_at = NOW()
 WHERE contrato_id = @ContratoId;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new
            {
                ContratoId = contratoId,
                ValorPago = valorPago,
                Paga = paga
            });
        }

        public async Task AtualizarContratoImplantacaoPagaAsync(Guid contratoId, bool implantacaoPaga)
        {
            const string sql = @"
UPDATE crm_contrato
   SET implantacao_paga = @ImplantacaoPaga,
       updated_at = NOW()
 WHERE id = @ContratoId;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            await connection.ExecuteAsync(sql, new { ContratoId = contratoId, ImplantacaoPaga = implantacaoPaga });
        }

        public async Task<CrmFinanceiroResumoRow> ObterResumoFinanceiroAsync(DateTime referenciaMes)
        {
            const string sql = @"
SELECT COALESCE(SUM(CASE
                        WHEN status IN ('pago', 'parcial')
                         AND data_pagamento IS NOT NULL
                         AND DATE_TRUNC('month', data_pagamento::timestamp) = DATE_TRUNC('month', @ReferenciaMes::timestamp)
                        THEN valor_pago
                        ELSE 0
                    END), 0) AS recebido_mes,
       COALESCE(SUM(CASE
                        WHEN status IN ('pendente', 'parcial')
                         AND DATE_TRUNC('month', COALESCE(competencia::timestamp, data_vencimento::timestamp))
                             = DATE_TRUNC('month', @ReferenciaMes::timestamp)
                        THEN valor_total - valor_pago
                        ELSE 0
                    END), 0) AS pendente_mes
  FROM crm_lancamento;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstAsync<CrmFinanceiroResumoRow>(sql, new { ReferenciaMes = referenciaMes.Date });
        }

        public async Task<IReadOnlyCollection<CrmDivisaoRow>> ObterDivisaoFinanceiraAsync(DateTime referenciaMes)
        {
            const string sql = @"
SELECT CASE
           WHEN l.tipo_lancamento::text = 'marketing_fixo' THEN 'marketing_valor_fixo'
           ELSE l.tipo_lancamento::text
       END AS tipo_lancamento,
       SUM(l.valor_pago) AS total_pago,
       SUM(CASE
               WHEN l.tipo_lancamento::text = 'implantacao_saas' THEN ROUND(l.valor_pago * 0.5, 2)
               WHEN l.tipo_lancamento::text = 'mensalidade_saas' THEN ROUND(l.valor_pago * 0.8, 2)
               ELSE 0
           END) AS farley_valor,
       SUM(CASE
               WHEN l.tipo_lancamento::text = 'implantacao_saas' THEN ROUND(l.valor_pago * 0.5, 2)
               WHEN l.tipo_lancamento::text = 'mensalidade_saas' THEN ROUND(l.valor_pago * 0.2, 2)
               WHEN l.tipo_lancamento::text IN ('mensalidade_marketing', 'marketing_valor_fixo', 'marketing_fixo') THEN l.valor_pago
               ELSE l.valor_pago
           END) AS socio_valor
  FROM crm_lancamento l
 WHERE l.status IN ('pago', 'parcial')
   AND l.valor_pago > 0
   AND l.data_pagamento IS NOT NULL
   AND DATE_TRUNC('month', l.data_pagamento::timestamp) = DATE_TRUNC('month', @ReferenciaMes::timestamp)
 GROUP BY CASE
              WHEN l.tipo_lancamento::text = 'marketing_fixo' THEN 'marketing_valor_fixo'
              ELSE l.tipo_lancamento::text
          END
 ORDER BY tipo_lancamento;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmDivisaoRow>(sql, new { ReferenciaMes = referenciaMes.Date });
            return rows.ToList();
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private async Task<Guid?> ObterEmpresaExistenteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? cnpj, string nomeEmpresa)
        {
            var cnpjNormalizado = NormalizeDigits(cnpj);
            if (!string.IsNullOrWhiteSpace(cnpjNormalizado))
            {
                const string sqlByCnpj = @"
SELECT id
  FROM empresas
 WHERE regexp_replace(COALESCE(cnpj, ''), '[^0-9]', '', 'g') = @Cnpj
 LIMIT 1;";

                var empresaIdPorCnpj = await connection.ExecuteScalarAsync<Guid?>(sqlByCnpj, new { Cnpj = cnpjNormalizado }, transaction);
                if (empresaIdPorCnpj.HasValue)
                {
                    return empresaIdPorCnpj;
                }
            }

            const string sqlByNome = @"
SELECT id
  FROM empresas
 WHERE lower(btrim(nome_fantasia)) = lower(btrim(@NomeEmpresa))
 ORDER BY created_at
 LIMIT 1;";

            return await connection.ExecuteScalarAsync<Guid?>(sqlByNome, new { NomeEmpresa = nomeEmpresa.Trim() }, transaction);
        }

        private async Task<Guid> CriarEmpresaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CrmCloseOpportunityCommand command)
        {
            const string sql = @"
INSERT INTO empresas (nome_fantasia, razao_social, cnpj, telefone, email, timezone_iana)
VALUES (@NomeFantasia, @RazaoSocial, @Cnpj, @Telefone, @Email, 'America/Sao_Paulo')
RETURNING id;";

            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                NomeFantasia = command.NomeEmpresa.Trim(),
                RazaoSocial = command.NomeEmpresa.Trim(),
                Cnpj = NormalizeDigits(command.Cnpj),
                Telefone = NormalizeNullable(command.Telefone),
                Email = NormalizeNullable(command.Email)
            }, transaction);
        }

        private async Task<Guid?> ObterEstabelecimentoExistenteAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid empresaId)
        {
            const string sql = @"
SELECT id
  FROM estabelecimentos
 WHERE id_empresa = @EmpresaId
 ORDER BY ativo DESC, created_at, id
 LIMIT 1;";

            return await connection.ExecuteScalarAsync<Guid?>(sql, new { EmpresaId = empresaId }, transaction);
        }

        private async Task<Guid> ResolverTipoEstabelecimentoIdAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string? establishmentTypeSlug)
        {
            if (!string.IsNullOrWhiteSpace(establishmentTypeSlug))
            {
                var bySlug = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 WHERE lower(slug) = lower(@Slug)
 ORDER BY ativo DESC NULLS LAST, nome
 LIMIT 1;",
                    new { Slug = establishmentTypeSlug.Trim() },
                    transaction);

                if (bySlug.HasValue)
                {
                    return bySlug.Value;
                }
            }

            var fallback = await connection.ExecuteScalarAsync<Guid?>(@"
SELECT id
  FROM tipo_estabelecimento
 ORDER BY ativo DESC NULLS LAST, nome
 LIMIT 1;",
                transaction: transaction);

            if (!fallback.HasValue)
            {
                throw new InvalidOperationException("Nenhum tipo de estabelecimento cadastrado.");
            }

            return fallback.Value;
        }

        private async Task<Guid> CriarEstabelecimentoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            CrmCloseOpportunityCommand command,
            Guid empresaId,
            Guid tipoEstabelecimentoId)
        {
            var slug = await GerarSlugDisponivelAsync(connection, transaction, command.NomeEmpresa);

            const string sql = @"
INSERT INTO estabelecimentos (
    id_empresa,
    tipo_unidade,
    nome_fantasia,
    whatsapp_e164,
    email,
    ativo,
    aceita_pedidos,
    timezone_iana,
    raio_entrega_km,
    pedido_minimo,
    taxa_entrega_fixa,
    taxa_entrega_por_km,
    tempo_preparo_min,
    modulos_ativos,
    id_tipo_estabelecimento,
    slug,
    status)
VALUES (
    @EmpresaId,
    'matriz'::tipo_unidade_enum,
    @NomeFantasia,
    @WhatsappE164,
    @Email,
    TRUE,
    TRUE,
    'America/Sao_Paulo',
    0,
    0,
    0,
    0,
    20,
    @ModulosAtivos::modulo_enum[],
    @TipoEstabelecimentoId,
    @Slug,
    'ativo')
RETURNING id;";

            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                EmpresaId = empresaId,
                NomeFantasia = command.NomeEmpresa.Trim(),
                WhatsappE164 = NormalizeNullable(command.Telefone),
                Email = NormalizeNullable(command.Email),
                ModulosAtivos = new[] { "GERAL", "WHATSAPP" },
                TipoEstabelecimentoId = tipoEstabelecimentoId,
                Slug = slug
            }, transaction);
        }

        private async Task VincularUsuarioEmpresaAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int userId,
            Guid empresaId,
            int createdByUserId)
        {
            const string sql = @"
INSERT INTO usuario_empresas (
    id, id_usuario, id_empresa, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EmpresaId, 'gerente_empresa', 'ativo', @CreatedByUserId, @CreatedByUserId,
    NOW(), NOW(), TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_empresa) DO UPDATE
SET tipo_acesso = EXCLUDED.tipo_acesso,
    status = 'ativo',
    ativo = TRUE,
    convidado_por = EXCLUDED.convidado_por,
    aprovado_por = EXCLUDED.aprovado_por,
    data_aprovacao = NOW(),
    updated_at = NOW();";

            await connection.ExecuteAsync(sql, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EmpresaId = empresaId,
                CreatedByUserId = createdByUserId
            }, transaction);
        }

        private async Task VincularUsuarioEstabelecimentoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            int userId,
            Guid estabelecimentoId,
            int createdByUserId)
        {
            const string sql = @"
INSERT INTO usuario_estabelecimentos (
    id, id_usuario, id_estabelecimento, tipo_acesso, status, convidado_por, aprovado_por,
    data_convite, data_aprovacao, permissoes_customizadas, ativo, created_at, updated_at)
VALUES (
    @Id, @UserId, @EstabelecimentoId, 'gerente_estabelecimento', 'ativo', @CreatedByUserId, @CreatedByUserId,
    NOW(), NOW(), NULL, TRUE, NOW(), NOW())
ON CONFLICT (id_usuario, id_estabelecimento) DO UPDATE
SET tipo_acesso = EXCLUDED.tipo_acesso,
    status = 'ativo',
    ativo = TRUE,
    convidado_por = EXCLUDED.convidado_por,
    aprovado_por = EXCLUDED.aprovado_por,
    data_aprovacao = NOW(),
    updated_at = NOW();";

            await connection.ExecuteAsync(sql, new
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                EstabelecimentoId = estabelecimentoId,
                CreatedByUserId = createdByUserId
            }, transaction);
        }

        private async Task<int> ResolverUsuarioResponsavelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string responsavel)
        {
            var admins = (await connection.QueryAsync<(int Id, string? Nome, string? Email)>(@"
SELECT id, nome, email
  FROM usuario
 WHERE COALESCE(is_super_admin, FALSE) = TRUE
   AND deleted_at IS NULL
 ORDER BY created_at, id;",
                transaction: transaction)).ToList();

            if (admins.Count == 0)
            {
                throw new InvalidOperationException("Nenhum super admin cadastrado para vincular o contrato.");
            }

            var normalizedResponsavel = NormalizeToken(responsavel);
            if (normalizedResponsavel == "farley")
            {
                var farley = admins.FirstOrDefault(item => MatchResponsavel(item.Nome, item.Email, "farley"));
                return farley.Id != 0 ? farley.Id : admins[0].Id;
            }

            var socio = admins.FirstOrDefault(item => !MatchResponsavel(item.Nome, item.Email, "farley"));
            return socio.Id != 0 ? socio.Id : admins[0].Id;
        }

        private static bool MatchResponsavel(string? nome, string? email, string target)
        {
            return NormalizeToken(nome).Contains(target, StringComparison.Ordinal)
                || NormalizeToken(email).Contains(target, StringComparison.Ordinal);
        }

        private async Task<string> GerarSlugDisponivelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string nomeFantasia)
        {
            var baseSlug = BuildSlug(nomeFantasia);
            if (string.IsNullOrWhiteSpace(baseSlug))
            {
                baseSlug = $"crm-{Guid.NewGuid():N}"[..12];
            }

            var slug = baseSlug;
            var suffix = 2;
            while (await connection.ExecuteScalarAsync<int>(@"
SELECT COUNT(1)
  FROM estabelecimentos
 WHERE slug = @Slug;",
                new { Slug = slug },
                transaction) > 0)
            {
                slug = $"{baseSlug}-{suffix}";
                suffix++;
            }

            return slug;
        }

        private static string BuildSlug(string? value)
        {
            var normalized = NormalizeToken(value)
                .Replace('_', '-')
                .Trim('-');

            return normalized.Length > 60 ? normalized[..60].Trim('-') : normalized;
        }

        private static string NormalizeToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var normalized = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            var lastWasSeparator = false;

            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(ch))
                {
                    builder.Append(ch);
                    lastWasSeparator = false;
                    continue;
                }

                if (!lastWasSeparator)
                {
                    builder.Append('_');
                    lastWasSeparator = true;
                }
            }

            return builder.ToString().Trim('_').Normalize(NormalizationForm.FormC);
        }

        private static string? NormalizeDigits(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());
            return string.IsNullOrWhiteSpace(digits) ? null : digits;
        }

        private static DateTime CalcularDataVencimento(DateTime dataInicio, int diaVencimento)
        {
            var clamped = Math.Clamp(diaVencimento, 1, 31);
            var dueDay = Math.Min(clamped, DateTime.DaysInMonth(dataInicio.Year, dataInicio.Month));
            var candidate = new DateTime(dataInicio.Year, dataInicio.Month, dueDay);

            if (candidate < dataInicio.Date)
            {
                var nextMonth = dataInicio.AddMonths(1);
                dueDay = Math.Min(clamped, DateTime.DaysInMonth(nextMonth.Year, nextMonth.Month));
                candidate = new DateTime(nextMonth.Year, nextMonth.Month, dueDay);
            }

            return candidate;
        }

        private static async Task<Guid> InserirLancamentoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction? transaction,
            Guid contratoId,
            Guid empresaId,
            string tipoLancamento,
            string descricao,
            DateTime? competencia,
            DateTime dataVencimento,
            decimal valorTotal,
            decimal valorPago,
            DateTime? dataPagamento,
            string status)
        {
            const string sql = @"
INSERT INTO crm_lancamento (
    contrato_id,
    empresa_id,
    tipo_lancamento,
    descricao,
    competencia,
    data_vencimento,
    data_pagamento,
    valor_total,
    valor_pago,
    status)
VALUES (
    @ContratoId,
    @EmpresaId,
    @TipoLancamento::crm_lancamento_tipo,
    @Descricao,
    @Competencia,
    @DataVencimento,
    @DataPagamento,
    @ValorTotal,
    @ValorPago,
    @Status::crm_lancamento_status)
RETURNING id;";

            return await connection.ExecuteScalarAsync<Guid>(sql, new
            {
                ContratoId = contratoId,
                EmpresaId = empresaId,
                TipoLancamento = tipoLancamento,
                Descricao = descricao,
                Competencia = competencia?.Date,
                DataVencimento = dataVencimento.Date,
                DataPagamento = dataPagamento?.Date,
                ValorTotal = valorTotal,
                ValorPago = valorPago,
                Status = status
            }, transaction);
        }

        private static async Task InserirHistoricoOportunidadeAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid oportunidadeId,
            int usuarioId,
            string acao,
            string? detalhe)
        {
            await connection.ExecuteAsync(@"
INSERT INTO crm_oportunidade_historico (id_oportunidade, id_usuario, acao, detalhe)
VALUES (@OportunidadeId, @UsuarioId, @Acao, @Detalhe);",
                new
                {
                    OportunidadeId = oportunidadeId,
                    UsuarioId = usuarioId,
                    Acao = acao,
                    Detalhe = NormalizeNullable(detalhe)
                },
                transaction);
        }

        private static async Task InserirHistoricoContratoAsync(
            NpgsqlConnection connection,
            NpgsqlTransaction transaction,
            Guid contratoId,
            int usuarioId,
            string acao,
            string? detalhe)
        {
            await connection.ExecuteAsync(@"
INSERT INTO crm_contrato_historico (contrato_id, usuario_id, acao, detalhe)
VALUES (@ContratoId, @UsuarioId, @Acao, @Detalhe);",
                new
                {
                    ContratoId = contratoId,
                    UsuarioId = usuarioId,
                    Acao = acao,
                    Detalhe = NormalizeNullable(detalhe)
                },
                transaction);
        }
    }
}
