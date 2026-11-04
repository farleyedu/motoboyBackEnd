using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using APIBack.Model.CRM;
using APIBack.Repository.Interface;
using APIBack.Service;
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

        public async Task<IReadOnlyCollection<CrmOportunidadeRow>> ListarOportunidadesAsync(string? status, string? tipo, int? responsavelUsuarioId, string? busca)
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
       responsavel_usuario_id,
       (SELECT u.nome FROM usuario u WHERE u.id = crm_oportunidade.responsavel_usuario_id) AS responsavel_nome,
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
   AND (@ResponsavelUsuarioId IS NULL OR responsavel_usuario_id = @ResponsavelUsuarioId)
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
                ResponsavelUsuarioId = responsavelUsuarioId,
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
       responsavel_usuario_id,
       (SELECT u.nome FROM usuario u WHERE u.id = crm_oportunidade.responsavel_usuario_id) AS responsavel_nome,
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
    nome_empresa, nome_contato, telefone, email, cnpj, tipo, responsavel_usuario_id, coluna, status,
    substatus, proxima_acao, estimativa_json, proposta_json, proposta_enviada_em,
    reserva_reuniao_id, reuniao_local, reserva_resposta_id, reserva_pagamento_id,
    dt_resposta_prevista, dt_pagamento_prevista, closed_at)
VALUES (
    @NomeEmpresa, @NomeContato, @Telefone, @Email, @Cnpj, @Tipo::crm_tipo_servico, @ResponsavelUsuarioId, @Coluna::crm_kanban_col, @Status::crm_oportunidade_status,
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
                oportunidade.ResponsavelUsuarioId,
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
       responsavel_usuario_id = @ResponsavelUsuarioId,
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
                oportunidade.ResponsavelUsuarioId,
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
            var result = await FecharOportunidadeNaTransacaoAsync(connection, transaction, command);
            await transaction.CommitAsync();
            return result;
        }

        /// <summary>
        /// Fechamento dentro de uma transacao ja aberta (a do fechamento legado ou a da passagem do acompanhamento).
        /// Loja nova nasce sempre em implantacao: so passa a operar depois do teste. Quem chama faz o commit.
        /// </summary>
        public async Task<CrmCloseOpportunityResult> FecharOportunidadeNaTransacaoAsync(
            NpgsqlConnection connection, NpgsqlTransaction transaction, CrmCloseOpportunityCommand command)
        {
            var oportunidade = await connection.QueryFirstOrDefaultAsync<CrmOportunidadeRow>(@"
SELECT id,
       nome_empresa,
       nome_contato,
       telefone,
       email,
       cnpj,
       tipo,
       responsavel,
       responsavel_usuario_id,
       (SELECT u.nome FROM usuario u WHERE u.id = crm_oportunidade.responsavel_usuario_id) AS responsavel_nome,
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

            // So liga a empresa e a loja que o operador confirmou (CrmAssociacaoRegras). Sem confirmacao, as duas nascem novas:
            // nada de procurar por nome nem de pegar o primeiro estabelecimento da empresa.
            var empresaId = command.EmpresaId.HasValue
                ? await ConfirmarEmpresaAsync(connection, transaction, command.EmpresaId.Value)
                : await CriarEmpresaAsync(connection, transaction, command);
            Guid estabelecimentoId;
            if (command.EstabelecimentoId.HasValue)
            {
                estabelecimentoId = await ConfirmarEstabelecimentoAsync(connection, transaction, empresaId, command.EstabelecimentoId.Value);
            }
            else
            {
                var tipoEstabelecimentoId = await ResolverTipoEstabelecimentoIdAsync(connection, transaction, command.EstabelecimentoTipoSlug);
                estabelecimentoId = await CriarEstabelecimentoAsync(connection, transaction, command, empresaId, tipoEstabelecimentoId);
            }
            var responsavelUserId = await ResolverUsuarioResponsavelAsync(connection, transaction, command.ResponsavelUsuarioId);

            await VincularUsuarioEmpresaAsync(connection, transaction, responsavelUserId, empresaId, command.UsuarioId);
            await VincularUsuarioEstabelecimentoAsync(connection, transaction, responsavelUserId, estabelecimentoId, command.UsuarioId);

            var contratoId = await connection.ExecuteScalarAsync<Guid>(@"
INSERT INTO crm_contrato (
    id_oportunidade,
    empresa_id,
    estabelecimento_id,
    tipo,
    status,
    responsavel_usuario_id,
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
    @ResponsavelUsuarioId,
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
                    command.ResponsavelUsuarioId,
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

            // Passagem do acompanhamento: a cobranca so comeca depois do teste, entao o fechamento nao lanca nada.
            if (!command.SemLancamentos && command.MensalidadeSaas > 0)
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

            if (!command.SemLancamentos && command.MensalidadeMarketing > 0)
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

            if (!command.SemLancamentos && command.MarketingValorFixo > 0)
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

            if (!command.SemLancamentos && command.ImplantacaoValor > 0)
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

            if (command.RegistrarPropostaAceita && !string.IsNullOrWhiteSpace(command.PropostaJson))
            {
                await RegistrarPropostaAceitaAsync(connection, transaction, command);
            }

            if (command.VincularAcompanhamento)
            {
                // Fechamento pelo /crm legado: o acompanhamento da oportunidade (se ja existe) passa a ser o da loja.
                await APIBack.Acompanhamento.Fases.AcompanhamentoSql.VincularLojaAsync(
                    connection, transaction, command.OportunidadeId, estabelecimentoId, command.UsuarioId, "Fechamento pelo CRM");
            }

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
           opp.responsavel_usuario_id,
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
                   o.responsavel_usuario_id,
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
        responsavel_usuario_id,
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
           fonte.responsavel_usuario_id,
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
           fonte.responsavel_usuario_id,
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
        responsavel_usuario_id,
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
           fonte_resolvida.responsavel_usuario_id,
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

        public async Task<IReadOnlyCollection<CrmEmpresaExistente>> ListarEmpresasParaAssociacaoAsync()
        {
            const string sql = @"
SELECT id AS Id,
       COALESCE(nome_fantasia, '') AS NomeFantasia,
       cnpj AS Cnpj
  FROM empresas;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmEmpresaExistente>(sql);
            return rows.ToList();
        }

        public async Task<IReadOnlyCollection<CrmEstabelecimentoDaEmpresaRow>> ListarEstabelecimentosDasEmpresasAsync(IReadOnlyCollection<Guid> empresaIds)
        {
            if (empresaIds.Count == 0)
            {
                return Array.Empty<CrmEstabelecimentoDaEmpresaRow>();
            }

            const string sql = @"
SELECT id,
       id_empresa AS empresa_id,
       COALESCE(nome_fantasia, '') AS nome,
       COALESCE(ativo, TRUE) AS ativo,
       situacao_comercial AS situacao
  FROM estabelecimentos
 WHERE id_empresa = ANY(@EmpresaIds)
 ORDER BY nome_fantasia, id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmEstabelecimentoDaEmpresaRow>(sql, new { EmpresaIds = empresaIds.ToArray() });
            return rows.ToList();
        }

        public async Task<IReadOnlyCollection<CrmResponsavelRow>> ListarEquipeZippyAsync()
        {
            const string sql = @"
SELECT id,
       COALESCE(nome, email, '') AS nome
  FROM usuario
 WHERE COALESCE(is_super_admin, FALSE) = TRUE
   AND deleted_at IS NULL
 ORDER BY nome, id;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            var rows = await connection.QueryAsync<CrmResponsavelRow>(sql);
            return rows.ToList();
        }

        // ---- Leads (ILeadStore)

        public async Task<APIBack.Acompanhamento.Relacionamento.UsuarioEquipeDto?> MembroDaEquipeAsync(int usuarioId, System.Threading.CancellationToken ct)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            return await connection.QueryFirstOrDefaultAsync<APIBack.Acompanhamento.Relacionamento.UsuarioEquipeDto>(new CommandDefinition(@"
SELECT id AS Id, COALESCE(nome, email, '') AS Nome FROM usuario
 WHERE id = @Id AND COALESCE(is_super_admin, FALSE) = TRUE AND deleted_at IS NULL;", new { Id = usuarioId }, cancellationToken: ct));
        }

        // Normalizacao de nome no SQL: a mesma da migration (nome_normalizado) e de RelacionamentoRegras.NormalizarNome.
        private const string NomeNormalizadoSql = "btrim(regexp_replace(translate(lower({0}), 'áàâãäéèêëíìîïóòôõöúùûüçñ', 'aaaaaeeeeiiiiooooouuuucn'), '[^a-z0-9]+', ' ', 'g'))";

        public async Task<IReadOnlyList<APIBack.Acompanhamento.Relacionamento.LeadCandidatoBruto>> CandidatosAsync(string nomeNormalizado, string? e164, System.Threading.CancellationToken ct)
        {
            // Largo de proposito: a decisao fina de "parecido" e feita em codigo (LeadCriador.Classificar).
            var digitos = new string((e164 ?? string.Empty).Where(char.IsDigit).ToArray());
            var nomeLead = "COALESCE(o.nome_normalizado, " + string.Format(NomeNormalizadoSql, "o.nome_empresa") + ")";
            var sql = @"
SELECT o.id AS Id, 'lead' AS Tipo, o.nome_empresa AS Nome, COALESCE(o.whatsapp_e164, o.telefone) AS Telefone,
       o.responsavel_usuario_id AS ResponsavelUsuarioId, u.nome AS ResponsavelNome, o.status::text || '/' || o.coluna::text AS Situacao
  FROM crm_oportunidade o LEFT JOIN usuario u ON u.id = o.responsavel_usuario_id
 WHERE o.deleted_at IS NULL
   AND ((@Digitos <> '' AND length(@Digitos) >= 10
         AND right(regexp_replace(COALESCE(o.whatsapp_e164, o.telefone, ''), '[^0-9]', '', 'g'), 10) = right(@Digitos, 10))
        OR (@Nome <> '' AND (" + nomeLead + @" = @Nome
            OR " + nomeLead + @" LIKE '%' || @Nome || '%'
            OR @Nome LIKE '%' || NULLIF(" + nomeLead + @", '') || '%'
            OR split_part(" + nomeLead + @", ' ', 1) = split_part(@Nome, ' ', 1))))
 LIMIT 30;";
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            var candidatos = (await connection.QueryAsync<APIBack.Acompanhamento.Relacionamento.LeadCandidatoBruto>(
                new CommandDefinition(sql, new { Nome = nomeNormalizado, Digitos = digitos }, cancellationToken: ct))).ToList();
            if (nomeNormalizado.Length > 0)
            {
                var nomeLoja = string.Format(NomeNormalizadoSql, "e.nome_fantasia");
                var lojas = await connection.QueryAsync<APIBack.Acompanhamento.Relacionamento.LeadCandidatoBruto>(new CommandDefinition(@"
SELECT e.id AS Id, 'loja' AS Tipo, e.nome_fantasia AS Nome, NULL AS Telefone, NULL::int AS ResponsavelUsuarioId, NULL AS ResponsavelNome,
       COALESCE(e.situacao_comercial, 'ativo') AS Situacao
  FROM estabelecimentos e
 WHERE " + nomeLoja + @" = @Nome
    OR " + nomeLoja + @" LIKE '%' || @Nome || '%'
    OR @Nome LIKE '%' || NULLIF(" + nomeLoja + @", '') || '%'
    OR split_part(" + nomeLoja + @", ' ', 1) = split_part(@Nome, ' ', 1)
 LIMIT 30;", new { Nome = nomeNormalizado }, cancellationToken: ct));
                candidatos.AddRange(lojas);
            }
            return candidatos;
        }

        public async Task<APIBack.Acompanhamento.Relacionamento.LeadGravado?> LeadPorChaveAsync(string chave, System.Threading.CancellationToken ct)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            return await LerLeadPorChaveAsync(connection, chave, ct);
        }

        private static async Task<APIBack.Acompanhamento.Relacionamento.LeadGravado?> LerLeadPorChaveAsync(Npgsql.NpgsqlConnection connection, string chave, System.Threading.CancellationToken ct)
        {
            var lead = await connection.QueryFirstOrDefaultAsync<(Guid Id, string? Hash)?>(new CommandDefinition(
                "SELECT id, lead_hash FROM crm_oportunidade WHERE lead_chave = @Chave;", new { Chave = chave }, cancellationToken: ct));
            if (lead is not { } l) return null;
            var acompanhamento = await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                "SELECT id FROM acompanhamento WHERE oportunidade_id = @O;", new { O = l.Id }, cancellationToken: ct));
            var passo = acompanhamento.HasValue
                ? await connection.QueryFirstOrDefaultAsync<Guid?>(new CommandDefinition(
                    "SELECT id FROM acompanhamento_proximo_passo WHERE acompanhamento_id = @A ORDER BY criado_em LIMIT 1;", new { A = acompanhamento }, cancellationToken: ct))
                : null;
            return new APIBack.Acompanhamento.Relacionamento.LeadGravado
            {
                OportunidadeId = l.Id, AcompanhamentoId = acompanhamento ?? Guid.Empty, ProximoPassoId = passo ?? Guid.Empty, Hash = l.Hash, Repetida = true
            };
        }

        public async Task<APIBack.Acompanhamento.Relacionamento.LeadGravado> CriarLeadAsync(APIBack.Acompanhamento.Relacionamento.LeadNovo novo, System.Threading.CancellationToken ct)
        {
            await using var connection = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await connection.BeginTransactionAsync(ct);
            try
            {
                var oportunidade = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
INSERT INTO crm_oportunidade (nome_empresa, nome_contato, telefone, email, cnpj, tipo, responsavel_usuario_id, coluna, status, proxima_acao, estimativa_json,
    origem, decisor, whatsapp_e164, nome_normalizado, lead_chave, lead_hash)
VALUES (@Nome, @Decisor, @Whatsapp, @Email, @Cnpj, 'saas'::crm_tipo_servico, @Responsavel, 'conversa'::crm_kanban_col, 'ativo'::crm_oportunidade_status,
    @ProximaAcao, '{}'::jsonb, @Origem, @Decisor, @Whatsapp, @NomeNormalizado, @Chave, @Hash)
RETURNING id;", new
                {
                    novo.Nome, novo.Decisor, novo.Whatsapp, novo.Email, novo.Cnpj, Responsavel = novo.ResponsavelUsuarioId,
                    ProximaAcao = $"{novo.PassoTipo} em {novo.PassoData:dd/MM}", novo.Origem, novo.NomeNormalizado, novo.Chave, novo.Hash
                }, tx, cancellationToken: ct));

                var detalhe = $"origem: {novo.Origem}; decisor: {novo.Decisor}" + (novo.DecisaoDuplicidade == null ? "" : $"; duplicidade decidida: {novo.DecisaoDuplicidade}");
                await connection.ExecuteAsync(new CommandDefinition(
                    "INSERT INTO crm_oportunidade_historico (id_oportunidade, id_usuario, acao, detalhe) VALUES (@O, @U, 'Lead criado', @D);",
                    new { O = oportunidade, U = novo.UsuarioId, D = detalhe }, tx, cancellationToken: ct));

                var documento = novo.Cnpj is { Length: 14 } ? novo.Cnpj : null;
                var acompanhamento = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
INSERT INTO acompanhamento (oportunidade_id, fase, documento, quem_decide) VALUES (@O, 'conversa', @Doc, @Decisor)
ON CONFLICT (oportunidade_id) WHERE oportunidade_id IS NOT NULL DO UPDATE SET atualizado_em = acompanhamento.atualizado_em
RETURNING id;", new { O = oportunidade, Doc = documento, novo.Decisor }, tx, cancellationToken: ct));

                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_relacionamento (acompanhamento_id, responsavel_usuario_id, responsavel_desde) VALUES (@A, @R, NOW())
ON CONFLICT (acompanhamento_id) DO UPDATE SET responsavel_usuario_id = EXCLUDED.responsavel_usuario_id, responsavel_desde = NOW();",
                    new { A = acompanhamento, R = novo.ResponsavelUsuarioId }, tx, cancellationToken: ct));

                var passo = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(@"
INSERT INTO acompanhamento_proximo_passo (acompanhamento_id, tipo, data, nota, criado_por) VALUES (@A, @Tipo, @Data, @Nota, @U) RETURNING id;",
                    new { A = acompanhamento, Tipo = novo.PassoTipo, Data = novo.PassoData.ToDateTime(TimeOnly.MinValue), Nota = novo.PassoNota, U = novo.UsuarioId }, tx, cancellationToken: ct));

                var evento = System.Text.Json.JsonSerializer.Serialize(new
                {
                    oportunidadeId = oportunidade, novo.Origem, novo.Decisor, responsavel = novo.ResponsavelUsuarioId,
                    proximoPasso = new { tipo = novo.PassoTipo, data = novo.PassoData }
                });
                await connection.ExecuteAsync(new CommandDefinition(@"
INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, usuario_id) VALUES (@A, 'lead_criado', @D::jsonb, @U);",
                    new { A = acompanhamento, U = novo.UsuarioId, D = evento }, tx, cancellationToken: ct));

                await tx.CommitAsync(ct);
                return new APIBack.Acompanhamento.Relacionamento.LeadGravado { OportunidadeId = oportunidade, AcompanhamentoId = acompanhamento, ProximoPassoId = passo, Hash = novo.Hash };
            }
            catch (Npgsql.PostgresException ex) when (ex.SqlState == Npgsql.PostgresErrorCodes.UniqueViolation && ex.ConstraintName == "ux_crm_oportunidade_lead_chave" && novo.Chave != null)
            {
                // Duas chamadas com a mesma chave ao mesmo tempo: a segunda devolve o lead da primeira.
                await tx.RollbackAsync(ct);
                await using var leitura = await _dataSource.OpenConnectionAsync(ct);
                return (await LerLeadPorChaveAsync(leitura, novo.Chave, ct))!;
            }
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
       c.responsavel_usuario_id,
       ru.nome AS responsavel_nome,
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
  LEFT JOIN usuario ru ON ru.id = c.responsavel_usuario_id
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
       c.responsavel_usuario_id,
       ru.nome AS responsavel_nome,
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
  LEFT JOIN usuario ru ON ru.id = c.responsavel_usuario_id
 WHERE c.id = @ContratoId
 LIMIT 1;";

            await using var connection = await _dataSource.OpenConnectionAsync();
            return await connection.QueryFirstOrDefaultAsync<CrmContratoRow>(sql, new { ContratoId = contratoId });
        }

        public async Task AtualizarContratoAsync(Guid contratoId, string? status, int? responsavelUsuarioId, int? diaVencimento, DateTime? dataInicioCobranca, decimal? mensalidadeSaas, decimal? mensalidadeMarketing, decimal? marketingValorFixo, string? observacoes)
        {
            const string sql = @"
UPDATE crm_contrato
   SET status = COALESCE(@Status::crm_contrato_status, status),
       responsavel_usuario_id = COALESCE(@ResponsavelUsuarioId, responsavel_usuario_id),
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
                ResponsavelUsuarioId = responsavelUsuarioId,
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
            // Both branches filter by competencia (or fallback to data_vencimento) within the reference month.
            const string selectCols = @"
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
 WHERE DATE_TRUNC('month', COALESCE(l.competencia::timestamp, l.data_vencimento::timestamp))
       = DATE_TRUNC('month', @ReferenciaMes::timestamp)";

            string sql = selectCols + "\n ORDER BY l.data_vencimento ASC, l.created_at DESC;";

            await using var conn = await _dataSource.OpenConnectionAsync();
            var rows = await conn.QueryAsync<CrmLancamentoRow>(sql, new { ReferenciaMes = referenciaMes.Date });
            return rows.ToList();
        }

        public async Task<IReadOnlyCollection<string>> ListarMesesComLancamentosAsync(int ultimos)
        {
            const string sql = @"
SELECT DISTINCT TO_CHAR(DATE_TRUNC('month', COALESCE(competencia::timestamp, data_vencimento::timestamp)), 'YYYY-MM') AS mes
  FROM crm_lancamento
 ORDER BY mes DESC
 LIMIT @Ultimos;";

            await using var conn = await _dataSource.OpenConnectionAsync();
            var rows = await conn.QueryAsync<string>(sql, new { Ultimos = ultimos });
            return rows.ToList();
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
                         AND valor_pago > 0
                         AND DATE_TRUNC('month', COALESCE(competencia::timestamp, data_vencimento::timestamp))
                             = DATE_TRUNC('month', @ReferenciaMes::timestamp)
                        THEN valor_pago
                        ELSE 0
                    END), 0) AS registrado_manual_mes,
       COALESCE(SUM(CASE
                        WHEN status IN ('pendente', 'parcial')
                         AND DATE_TRUNC('month', COALESCE(competencia::timestamp, data_vencimento::timestamp))
                             = DATE_TRUNC('month', @ReferenciaMes::timestamp)
                        THEN valor_total - valor_pago
                        ELSE 0
                    END), 0) AS pendente_manual_mes,
       -- Dinheiro de verdade: o que a assinatura cobrou e o gateway confirmou.
       (SELECT COALESCE(SUM(COALESCE(cc.valor_pago, cc.valor)), 0)
          FROM comercial_cobranca cc
         WHERE cc.status = 'paga'
           AND DATE_TRUNC('month', cc.pago_em AT TIME ZONE 'America/Sao_Paulo') = DATE_TRUNC('month', @ReferenciaMes::timestamp)) AS recebido_mes,
       (SELECT COALESCE(SUM(cc.valor), 0)
          FROM comercial_cobranca cc
         WHERE cc.status IN ('aberta', 'processando')
           AND DATE_TRUNC('month', cc.vencimento AT TIME ZONE 'America/Sao_Paulo') = DATE_TRUNC('month', @ReferenciaMes::timestamp)) AS pendente_mes
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
   AND DATE_TRUNC('month', COALESCE(l.competencia::timestamp, l.data_vencimento::timestamp))
       = DATE_TRUNC('month', @ReferenciaMes::timestamp)
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

        public async Task RegistrarPropostaLegadaAsync(Guid oportunidadeId, int usuarioId, string propostaJson, decimal? valorMensal, decimal? valorImplantacao, DateTimeOffset? enviadaEm)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('acompanhamento_proposta_versao') IS NOT NULL;", transaction: transaction))
            {
                return;
            }

            // Mesma ordem das passagens: oportunidade -> acompanhamento.
            await connection.ExecuteAsync("SELECT 1 FROM crm_oportunidade WHERE id = @OportunidadeId FOR UPDATE;", new { OportunidadeId = oportunidadeId }, transaction);
            var acompanhamento = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM acompanhamento WHERE oportunidade_id = @OportunidadeId FOR UPDATE;", new { OportunidadeId = oportunidadeId }, transaction);

            var mesma = await connection.ExecuteScalarAsync<bool>(@"
SELECT EXISTS (SELECT 1 FROM acompanhamento_proposta_versao
                WHERE oportunidade_id = @OportunidadeId AND status IN ('enviada', 'em_negociacao') AND dados = CAST(@Dados AS jsonb));",
                new { OportunidadeId = oportunidadeId, Dados = propostaJson }, transaction);
            if (mesma)
            {
                return;
            }

            var anterior = await connection.ExecuteScalarAsync<int?>(@"
UPDATE acompanhamento_proposta_versao SET status = 'substituida', edicao = edicao + 1, atualizada_em = NOW()
 WHERE oportunidade_id = @OportunidadeId AND status IN ('enviada', 'em_negociacao') RETURNING versao;",
                new { OportunidadeId = oportunidadeId }, transaction);
            await connection.ExecuteAsync(@"
UPDATE acompanhamento_proposta_versao SET status = 'guardada', edicao = edicao + 1, atualizada_em = NOW()
 WHERE oportunidade_id = @OportunidadeId AND status = 'rascunho';", new { OportunidadeId = oportunidadeId }, transaction);

            var quando = enviadaEm?.UtcDateTime;
            var validade = enviadaEm.HasValue ? APIBack.Acompanhamento.Fases.PropostaRegras.Validade(enviadaEm.Value).ToDateTime(TimeOnly.MinValue) : (DateTime?)null;
            var versao = await connection.ExecuteScalarAsync<int>(@"
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, valor_mensal, valor_implantacao, dados, criada_por, apresentada_por,
                                            enviada_em, validade_ate, canal, origem, revisa_versao)
SELECT @OportunidadeId, COALESCE(MAX(versao), 0) + 1, 'enviada', @ValorMensal, @ValorImplantacao, CAST(@Dados AS jsonb), @UsuarioId, @UsuarioId,
       @EnviadaEm, @ValidadeAte, 'nao_informado', 'crm_legado', @Anterior
  FROM acompanhamento_proposta_versao WHERE oportunidade_id = @OportunidadeId
RETURNING versao;",
                new
                {
                    OportunidadeId = oportunidadeId, ValorMensal = valorMensal, ValorImplantacao = valorImplantacao, Dados = propostaJson, UsuarioId = usuarioId,
                    EnviadaEm = quando, ValidadeAte = validade, Anterior = anterior
                }, transaction);

            if (acompanhamento.HasValue)
            {
                await connection.ExecuteAsync(@"
INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, usuario_id) VALUES (@Acompanhamento, 'proposta_enviada', CAST(@Detalhe AS jsonb), @UsuarioId);",
                    new
                    {
                        Acompanhamento = acompanhamento, UsuarioId = usuarioId,
                        Detalhe = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.SerializarDetalhe(new
                        {
                            versao, canal = "nao_informado", origem = "crm_legado", valorMensal, enviadaEm = quando, substituiu = anterior
                        })
                    }, transaction);
            }

            await transaction.CommitAsync();
        }

        public async Task EspelharResponsavelLegadoAsync(Guid oportunidadeId, int? novo, int? anterior, int usuarioId)
        {
            await using var connection = await _dataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            if (!await connection.ExecuteScalarAsync<bool>("SELECT to_regclass('acompanhamento_relacionamento') IS NOT NULL;", transaction: transaction))
            {
                return;
            }

            await connection.ExecuteAsync("SELECT 1 FROM crm_oportunidade WHERE id = @OportunidadeId FOR UPDATE;", new { OportunidadeId = oportunidadeId }, transaction);
            var acompanhamento = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM acompanhamento WHERE oportunidade_id = @OportunidadeId FOR UPDATE;", new { OportunidadeId = oportunidadeId }, transaction);
            if (!acompanhamento.HasValue)
            {
                return;
            }

            var mudou = await connection.ExecuteAsync(@"
UPDATE acompanhamento_relacionamento
   SET responsavel_usuario_id = @Novo, responsavel_desde = NOW(), versao = versao + 1, atualizado_em = NOW()
 WHERE acompanhamento_id = @Acompanhamento AND responsavel_usuario_id IS DISTINCT FROM @Novo;",
                new { Acompanhamento = acompanhamento, Novo = novo }, transaction);
            if (mudou > 0)
            {
                await connection.ExecuteAsync(@"
INSERT INTO acompanhamento_relacionamento_evento (acompanhamento_id, tipo, detalhe, usuario_id) VALUES (@Acompanhamento, 'passou', CAST(@Detalhe AS jsonb), @UsuarioId);",
                    new
                    {
                        Acompanhamento = acompanhamento, UsuarioId = usuarioId,
                        Detalhe = APIBack.Acompanhamento.Relacionamento.RelacionamentoRegras.SerializarDetalhe(new { de = anterior, para = novo, motivo = "Alterado pelo CRM antigo." })
                    }, transaction);
            }

            await transaction.CommitAsync();
        }

        /// <summary>Guarda a proposta do fechamento como versao aceita (proxima versao da oportunidade). Sem a tabela, nao faz nada.</summary>
        private static async Task RegistrarPropostaAceitaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CrmCloseOpportunityCommand command)
        {
            var existe = await connection.ExecuteScalarAsync<bool>(
                "SELECT to_regclass('acompanhamento_proposta_versao') IS NOT NULL;", transaction: transaction);
            if (!existe)
            {
                return;
            }

            await connection.ExecuteAsync(@"
UPDATE acompanhamento_proposta_versao SET status = 'guardada', edicao = edicao + 1, atualizada_em = NOW()
 WHERE oportunidade_id = @OportunidadeId AND status IN ('rascunho', 'enviada', 'em_negociacao');
INSERT INTO acompanhamento_proposta_versao (oportunidade_id, versao, status, valor_mensal, valor_implantacao, dados, criada_por, enviada_em, canal, origem)
SELECT @OportunidadeId, COALESCE(MAX(versao), 0) + 1, 'aceita', @ValorMensal, @ValorImplantacao, CAST(@Dados AS jsonb), @UsuarioId, NOW(), 'nao_informado', 'crm_legado'
  FROM acompanhamento_proposta_versao WHERE oportunidade_id = @OportunidadeId;",
                new
                {
                    command.OportunidadeId,
                    ValorMensal = command.MensalidadeSaas + command.MensalidadeMarketing,
                    ValorImplantacao = command.ImplantacaoValor,
                    Dados = command.PropostaJson,
                    command.UsuarioId
                },
                transaction);
        }

        private static async Task<Guid> ConfirmarEmpresaAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid empresaId)
        {
            var existe = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id FROM empresas WHERE id = @EmpresaId FOR UPDATE;", new { EmpresaId = empresaId }, transaction);
            return existe ?? throw new KeyNotFoundException("Empresa confirmada nao encontrada.");
        }

        private static async Task<Guid> ConfirmarEstabelecimentoAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid empresaId, Guid estabelecimentoId)
        {
            var donoId = await connection.ExecuteScalarAsync<Guid?>(
                "SELECT id_empresa FROM estabelecimentos WHERE id = @EstabelecimentoId FOR UPDATE;", new { EstabelecimentoId = estabelecimentoId }, transaction);
            if (!donoId.HasValue)
            {
                throw new KeyNotFoundException("Loja confirmada nao encontrada.");
            }

            if (donoId.Value != empresaId)
            {
                throw new InvalidOperationException("A loja confirmada nao pertence a empresa confirmada.");
            }

            return estabelecimentoId;
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
    status,
    situacao_comercial)
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
    'ativo',
    -- Loja do fechamento nasce em implantacao (configura, nao opera) e so opera depois do teste de 15 dias.
    'implantacao')
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

        private static async Task<int> ResolverUsuarioResponsavelAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, int responsavelUsuarioId)
        {
            var id = await connection.ExecuteScalarAsync<int?>(@"
SELECT id
  FROM usuario
 WHERE id = @Id
   AND COALESCE(is_super_admin, FALSE) = TRUE
   AND deleted_at IS NULL;",
                new { Id = responsavelUsuarioId },
                transaction);

            return id ?? throw new InvalidOperationException("O responsavel precisa ser alguem da equipe Zippy.");
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
