using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using APIBack.DTOs.CRM;
using APIBack.Model;
using APIBack.Model.CRM;
using APIBack.Repository.Interface;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Service
{
    public class CrmService : ICrmService
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };
        private static readonly CultureInfo PtBrCulture = new("pt-BR");

        private readonly ICrmRepository _crmRepository;
        private readonly IReservaRepository _reservaRepository;
        private readonly ILogger<CrmService> _logger;
        private readonly Guid? _centralEstabelecimentoId;

        public CrmService(
            ICrmRepository crmRepository,
            IReservaRepository reservaRepository,
            IConfiguration configuration,
            ILogger<CrmService> logger)
        {
            _crmRepository = crmRepository;
            _reservaRepository = reservaRepository;
            _logger = logger;
            _centralEstabelecimentoId = Guid.TryParse(configuration["WhatsApp:CentralEstabelecimentoId"], out var parsed)
                ? parsed
                : null;
        }

        public async Task<IReadOnlyCollection<CrmOportunidadeResumoDto>> ListarOportunidadesAsync(string? status, string? tipo, string? responsavel, string? busca)
        {
            var rows = await _crmRepository.ListarOportunidadesAsync(
                NormalizeNullable(status) ?? "ativo",
                NormalizeNullable(tipo),
                NormalizeNullable(responsavel),
                NormalizeNullable(busca));

            var result = new List<CrmOportunidadeResumoDto>(rows.Count);
            foreach (var row in rows)
            {
                result.Add(await MapOportunidadeResumoAsync(row));
            }

            return result;
        }

        public async Task<CrmOportunidadeDetalheDto?> ObterOportunidadeAsync(Guid oportunidadeId)
        {
            var row = await _crmRepository.ObterOportunidadeAsync(oportunidadeId);
            if (row == null)
            {
                return null;
            }

            var dto = await MapOportunidadeDetalheAsync(row);
            dto.Historico = (await _crmRepository.ListarHistoricoOportunidadeAsync(row.Id))
                .Select(MapHistorico)
                .ToList();
            return dto;
        }

        public async Task<CrmOportunidadeResumoDto> CriarOportunidadeAsync(int usuarioId, CriarCrmOportunidadeRequest request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ValidateRequired(request.NomeEmpresa, "nomeEmpresa", "Informe o nome da empresa.", errors);
            ValidateRequired(request.NomeContato, "nomeContato", "Informe o nome do contato.", errors);
            ValidateRequired(request.Telefone, "telefone", "Informe o telefone.", errors);
            ValidateAllowed(NormalizeToken(request.Tipo), "tipo", new[] { "saas", "marketing", "ambos" }, "Tipo invalido.", errors);
            ValidateAllowed(NormalizeToken(request.Responsavel), "responsavel", new[] { "farley", "socio" }, "Responsavel invalido.", errors);
            ThrowIfValidationErrors(errors);

            var oportunidade = new CrmOportunidadeRow
            {
                NomeEmpresa = request.NomeEmpresa.Trim(),
                NomeContato = request.NomeContato.Trim(),
                Telefone = NormalizeNullable(request.Telefone) ?? string.Empty,
                Email = NormalizeNullable(request.Email),
                Cnpj = NormalizeDigits(request.Cnpj),
                Tipo = NormalizeToken(request.Tipo),
                Responsavel = NormalizeToken(request.Responsavel),
                Coluna = "conversa",
                Status = "ativo",
                Substatus = NormalizeNullable(request.Substatus),
                EstimativaJson = SerializeOrEmpty(request.Estimativa)
            };

            var id = await _crmRepository.CriarOportunidadeAsync(oportunidade);
            await _crmRepository.AdicionarHistoricoOportunidadeAsync(
                id,
                usuarioId,
                "Lead criado",
                null);

            return (await ObterOportunidadeResumoAsync(id))!;
        }

        public async Task<CrmOportunidadeResumoDto> AtualizarOportunidadeAsync(int usuarioId, Guid oportunidadeId, AtualizarCrmOportunidadeRequest request)
        {
            var row = await _crmRepository.ObterOportunidadeAsync(oportunidadeId)
                ?? throw new KeyNotFoundException("Oportunidade nao encontrada.");

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (request.Responsavel != null)
            {
                ValidateAllowed(NormalizeToken(request.Responsavel), "responsavel", new[] { "farley", "socio" }, "Responsavel invalido.", errors);
            }

            if (request.Status != null)
            {
                ValidateAllowed(NormalizeToken(request.Status), "status", new[] { "ativo", "perdido" }, "Status invalido.", errors);
            }

            if (request.Proposta != null)
            {
                ValidateProposta(row.Tipo, request.Proposta, errors);
            }

            ThrowIfValidationErrors(errors);

            row.NomeEmpresa = NormalizeNullable(request.NomeEmpresa) ?? row.NomeEmpresa;
            row.NomeContato = NormalizeNullable(request.NomeContato) ?? row.NomeContato;
            row.Telefone = NormalizeNullable(request.Telefone) ?? row.Telefone;
            row.Email = NormalizeNullable(request.Email) ?? row.Email;
            row.Cnpj = NormalizeDigits(request.Cnpj) ?? row.Cnpj;
            row.Responsavel = request.Responsavel != null ? NormalizeToken(request.Responsavel) : row.Responsavel;
            row.Substatus = request.Substatus != null ? NormalizeNullable(request.Substatus) : row.Substatus;
            row.ProximaAcao = request.ProximaAcao != null ? NormalizeNullable(request.ProximaAcao) : row.ProximaAcao;
            row.Status = request.Status != null ? NormalizeToken(request.Status) : row.Status;
            row.ClosedAt = row.Status switch
            {
                "perdido" => row.ClosedAt ?? DateTimeOffset.UtcNow,
                "ativo" => null,
                _ => row.ClosedAt
            };

            var historico = "Lead atualizado";
            string? detalhe = null;
            if (request.Proposta != null)
            {
                row.PropostaJson = SerializeProposta(request.Proposta);
                row.PropostaEnviadaEm = ToDateTimeOffset(request.Proposta.EnviadaEm) ?? DateTimeOffset.UtcNow;
                historico = "Proposta registrada";
                detalhe = BuildPropostaHistorico(request.Proposta);
            }

            await _crmRepository.AtualizarOportunidadeAsync(row);
            await _crmRepository.AdicionarHistoricoOportunidadeAsync(oportunidadeId, usuarioId, historico, detalhe);
            return (await ObterOportunidadeResumoAsync(oportunidadeId))!;
        }

        public async Task<CrmOportunidadeResumoDto> MoverOportunidadeAsync(int usuarioId, Guid oportunidadeId, MoverCrmOportunidadeRequest request)
        {
            var row = await _crmRepository.ObterOportunidadeAsync(oportunidadeId)
                ?? throw new KeyNotFoundException("Oportunidade nao encontrada.");

            var colunaDestino = NormalizeToken(request.ColunaDestino);
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ValidateAllowed(colunaDestino, "colunaDestino", new[] { "conversa", "reuniao", "proposta", "quase", "aguardando" }, "Coluna invalida.", errors);
            ThrowIfValidationErrors(errors);

            if (row.Status != "ativo")
            {
                throw ValidationException("Dados invalidos.", ("status", "Somente oportunidades ativas podem ser movimentadas."));
            }

            if (colunaDestino == row.Coluna)
            {
                return (await ObterOportunidadeResumoAsync(oportunidadeId))!;
            }

            switch (colunaDestino)
            {
                case "reuniao":
                    return await MoverParaReuniaoAsync(usuarioId, row, request.Reuniao);
                case "proposta":
                    return await MoverParaPropostaAsync(usuarioId, row, request.Proposta);
                case "quase":
                    return await MoverParaQuaseAsync(usuarioId, row, request.QuaseFechado);
                case "aguardando":
                    await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                        row.Id,
                        colunaDestino,
                        status: row.Status,
                        substatus: row.Substatus,
                        propostaJson: row.PropostaJson,
                        propostaEnviadaEm: row.PropostaEnviadaEm,
                        reuniaoReservaId: row.ReuniaoReservaId,
                        reuniaoLocal: row.ReuniaoLocal,
                        respostaReservaId: row.RespostaReservaId,
                        pagamentoReservaId: row.PagamentoReservaId,
                        dtRespostaPrevista: row.DtRespostaPrevista,
                        dtPagamentoPrevista: row.DtPagamentoPrevista,
                        closedAt: row.ClosedAt);
                    await _crmRepository.AdicionarHistoricoOportunidadeAsync(row.Id, usuarioId, "Movido para Aguardando", "Lead movido para aguardando retorno.");
                    return (await ObterOportunidadeResumoAsync(row.Id))!;
                default:
                    await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                        row.Id,
                        "conversa",
                        status: row.Status,
                        substatus: row.Substatus,
                        propostaJson: row.PropostaJson,
                        propostaEnviadaEm: row.PropostaEnviadaEm,
                        reuniaoReservaId: row.ReuniaoReservaId,
                        reuniaoLocal: row.ReuniaoLocal,
                        respostaReservaId: row.RespostaReservaId,
                        pagamentoReservaId: row.PagamentoReservaId,
                        dtRespostaPrevista: row.DtRespostaPrevista,
                        dtPagamentoPrevista: row.DtPagamentoPrevista,
                        closedAt: row.ClosedAt);
                    await _crmRepository.AdicionarHistoricoOportunidadeAsync(row.Id, usuarioId, "Movido para Conversa", "Lead movido para conversa inicial.");
                    return (await ObterOportunidadeResumoAsync(row.Id))!;
            }
        }

        public async Task<FecharCrmOportunidadeResponse> FecharOportunidadeAsync(int usuarioId, Guid oportunidadeId, FecharCrmOportunidadeRequest request)
        {
            var row = await _crmRepository.ObterOportunidadeAsync(oportunidadeId)
                ?? throw new KeyNotFoundException("Oportunidade nao encontrada.");

            if (string.IsNullOrWhiteSpace(row.PropostaJson))
            {
                throw ValidationException("Dados invalidos.", ("proposta", "A oportunidade precisa ter proposta registrada para fechar negocio."));
            }

            if (row.Status != "ativo")
            {
                throw ValidationException("Dados invalidos.", ("status", "Somente oportunidades ativas podem ser fechadas."));
            }

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ValidateAllowed(NormalizeToken(request.StatusInicial), "statusInicial", new[] { "aguardando_inicio", "implantacao", "ativo" }, "Status inicial invalido.", errors);
            if (request.DiaVencimento < 1 || request.DiaVencimento > 31)
            {
                AddError(errors, "diaVencimento", "Informe um dia de vencimento entre 1 e 31.");
            }
            if (row.Coluna != "quase" && row.Coluna != "aguardando")
            {
                AddError(errors, "coluna", "A oportunidade so pode ser fechada nas colunas quase fechado ou aguardando retorno.");
            }

            ThrowIfValidationErrors(errors);

            var proposta = DeserializeProposta(row.PropostaJson, row.PropostaEnviadaEm) ?? new CrmPropostaDto();
            var nomeEmpresa = NormalizeNullable(request.NomeEmpresaOverride) ?? row.NomeEmpresa;
            var nomeContato = NormalizeNullable(request.NomeContatoOverride) ?? row.NomeContato;
            var telefone = NormalizeNullable(request.TelefoneOverride) ?? row.Telefone;
            var email = request.EmailOverride != null
                ? NormalizeNullable(request.EmailOverride)
                : row.Email;
            var cnpj = request.CnpjOverride != null
                ? NormalizeDigits(request.CnpjOverride)
                : row.Cnpj;
            var command = new CrmCloseOpportunityCommand
            {
                OportunidadeId = row.Id,
                UsuarioId = usuarioId,
                Responsavel = row.Responsavel,
                NomeEmpresa = nomeEmpresa,
                NomeContato = nomeContato,
                Telefone = telefone,
                Email = email,
                Cnpj = cnpj,
                Tipo = row.Tipo,
                StatusInicial = NormalizeToken(request.StatusInicial),
                DiaVencimento = request.DiaVencimento,
                DataInicioCobranca = request.DataInicioCobranca?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today,
                ImplantacaoJaPaga = request.ImplantacaoJaPaga,
                Observacoes = NormalizeNullable(request.Observacoes),
                EstabelecimentoTipoSlug = NormalizeNullable(request.EstabelecimentoTipoSlug),
                MensalidadeSaas = proposta.SaaSMensalidade ?? 0m,
                MensalidadeMarketing = proposta.MarketingMensalidade ?? 0m,
                MarketingValorFixo = proposta.MarketingValorFixo ?? 0m,
                ImplantacaoValor = proposta.Implantacao ?? 0m,
                PropostaJson = row.PropostaJson
            };

            var result = await _crmRepository.FecharOportunidadeAsync(command);
            _logger.LogInformation(
                "CRM fechou oportunidade {OportunidadeId} em contrato {ContratoId} para empresa {EmpresaId}",
                oportunidadeId,
                result.ContratoId,
                result.EmpresaId);

            return new FecharCrmOportunidadeResponse
            {
                ContratoId = result.ContratoId
            };
        }

        public async Task<IReadOnlyCollection<CrmContratoResumoDto>> ListarContratosAsync(string? status)
        {
            await SincronizarContratosLegadosAsync();
            var rows = await _crmRepository.ListarContratosAsync(NormalizeNullable(status));
            return rows.Select(MapContratoResumo).ToList();
        }

        public async Task<CrmContratoDetalheDto?> ObterContratoAsync(Guid contratoId)
        {
            var row = await _crmRepository.ObterContratoAsync(contratoId);
            return row == null ? null : await MapContratoDetalheAsync(row);
        }

        public async Task<CrmContratoResumoDto> AtualizarContratoAsync(int usuarioId, Guid contratoId, AtualizarCrmContratoRequest request)
        {
            var contrato = await _crmRepository.ObterContratoAsync(contratoId)
                ?? throw new KeyNotFoundException("Contrato nao encontrado.");

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (request.Status != null)
            {
                ValidateAllowed(
                    NormalizeToken(request.Status),
                    "status",
                    new[] { "aguardando_inicio", "implantacao", "ativo", "inadimplente", "pausado", "cancelado" },
                    "Status invalido.",
                    errors);
            }

            if (request.DiaVencimento.HasValue && (request.DiaVencimento.Value < 1 || request.DiaVencimento.Value > 31))
            {
                AddError(errors, "diaVencimento", "Informe um dia de vencimento entre 1 e 31.");
            }

            ThrowIfValidationErrors(errors);

            await _crmRepository.AtualizarContratoAsync(
                contratoId,
                request.Status != null ? NormalizeToken(request.Status) : null,
                request.DiaVencimento,
                request.DataInicioCobranca?.ToDateTime(TimeOnly.MinValue),
                request.Observacoes);

            await _crmRepository.AdicionarHistoricoContratoAsync(
                contratoId,
                usuarioId,
                "Contrato atualizado",
                $"Contrato atualizado. Status={NormalizeNullable(request.Status) ?? contrato.Status}; vencimento={request.DiaVencimento?.ToString() ?? contrato.DiaVencimento.ToString()}.");

            return MapContratoResumo((await _crmRepository.ObterContratoAsync(contratoId))!);
        }

        public async Task<CrmLancamentoDto> RegistrarPagamentoAsync(int usuarioId, RegistrarCrmLancamentoRequest request)
        {
            if (request.LancamentoId.HasValue)
            {
                var lancamento = await _crmRepository.ObterLancamentoAsync(request.LancamentoId.Value)
                    ?? throw new KeyNotFoundException("Lancamento nao encontrado.");

                var valorTotal = request.ValorTotal > 0 ? request.ValorTotal : lancamento.ValorTotal;
                var valorPago = request.ValorPago;
                if (valorPago < 0)
                {
                    throw ValidationException("Dados invalidos.", ("valorPago", "Valor pago nao pode ser negativo."));
                }

                var status = ResolverStatusLancamento(valorPago, valorTotal);
                DateTime? dataPagamento = valorPago > 0 ? request.DataPagamento?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today : null;

                await _crmRepository.AtualizarLancamentoAsync(
                    lancamento.Id,
                    valorPago,
                    dataPagamento,
                    status,
                    request.DataVencimento?.ToDateTime(TimeOnly.MinValue),
                    request.ValorTotal > 0 ? request.ValorTotal : null);

                if (lancamento.TipoLancamento == "implantacao_saas")
                {
                    var paga = status == "pago";
                    await _crmRepository.AtualizarImplantacaoPagamentoAsync(lancamento.ContratoId, valorPago, paga);
                    await _crmRepository.AtualizarContratoImplantacaoPagaAsync(lancamento.ContratoId, paga);
                }

                await _crmRepository.AdicionarHistoricoContratoAsync(
                    lancamento.ContratoId,
                    usuarioId,
                    "Lancamento atualizado",
                    $"Lancamento {lancamento.Descricao} atualizado para status {status}.");

                return MapLancamento((await _crmRepository.ObterLancamentoAsync(lancamento.Id))!);
            }

            if (!request.ContratoId.HasValue)
            {
                throw ValidationException("Dados invalidos.", ("contratoId", "Informe o contrato para criar o lancamento."));
            }

            var contrato = await _crmRepository.ObterContratoAsync(request.ContratoId.Value)
                ?? throw new KeyNotFoundException("Contrato nao encontrado.");

            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            ValidateRequired(request.TipoLancamento, "tipoLancamento", "Informe o tipo de lancamento.", errors);
            ValidateRequired(request.Descricao, "descricao", "Informe a descricao do lancamento.", errors);
            ValidateRequired(request.Competencia, "competencia", "Informe a competencia.", errors);
            if (!request.DataVencimento.HasValue)
            {
                AddError(errors, "dataVencimento", "Informe a data de vencimento.");
            }
            if (request.ValorTotal <= 0)
            {
                AddError(errors, "valorTotal", "Informe um valor total maior que zero.");
            }
            var tipoLancamento = NormalizeLancamentoTipo(request.TipoLancamento);
            ValidateAllowed(
                tipoLancamento,
                "tipoLancamento",
                new[] { "implantacao_saas", "mensalidade_saas", "mensalidade_marketing", "marketing_valor_fixo" },
                "Tipo de lancamento invalido.",
                errors);
            var competencia = ParseCompetencia(request.Competencia, "competencia", errors);
            ThrowIfValidationErrors(errors);

            var statusNovo = ResolverStatusLancamento(request.ValorPago, request.ValorTotal);
            DateTime? dataPagamentoNovo = request.ValorPago > 0 ? request.DataPagamento?.ToDateTime(TimeOnly.MinValue) ?? DateTime.Today : null;

            var id = await _crmRepository.CriarLancamentoAsync(new CrmLancamentoRow
            {
                ContratoId = contrato.Id,
                EmpresaId = contrato.EmpresaId,
                TipoLancamento = tipoLancamento,
                Descricao = request.Descricao!.Trim(),
                Competencia = competencia,
                DataVencimento = request.DataVencimento!.Value.ToDateTime(TimeOnly.MinValue),
                DataPagamento = dataPagamentoNovo,
                ValorTotal = request.ValorTotal,
                ValorPago = request.ValorPago,
                Status = statusNovo
            });

            if (tipoLancamento == "implantacao_saas")
            {
                var paga = statusNovo == "pago";
                await _crmRepository.AtualizarImplantacaoPagamentoAsync(contrato.Id, request.ValorPago, paga);
                await _crmRepository.AtualizarContratoImplantacaoPagaAsync(contrato.Id, paga);
            }

            await _crmRepository.AdicionarHistoricoContratoAsync(
                contrato.Id,
                usuarioId,
                "Lancamento criado",
                $"Lancamento {request.Descricao} criado com status {statusNovo}.");

            return MapLancamento((await _crmRepository.ObterLancamentoAsync(id))!);
        }

        public async Task<CrmFinanceiroResumoDto> ObterResumoFinanceiroAsync(DateTime? referenciaMes)
        {
            await SincronizarContratosLegadosAsync();
            var referencia = PrimeiroDiaMes(referenciaMes ?? DateTime.Today);
            var resumo = await _crmRepository.ObterResumoFinanceiroAsync(referencia);
            var divisao = await _crmRepository.ObterDivisaoFinanceiraAsync(referencia);

            return new CrmFinanceiroResumoDto
            {
                RecebidoMes = resumo.RecebidoMes,
                PendenteMes = resumo.PendenteMes,
                FarleyMes = divisao.Sum(item => item.FarleyValor),
                SocioMes = divisao.Sum(item => item.SocioValor)
            };
        }

        public async Task<IReadOnlyCollection<CrmLancamentoDto>> ListarLancamentosEmAbertoAsync()
        {
            await SincronizarContratosLegadosAsync();
            var rows = await _crmRepository.ListarLancamentosEmAbertoAsync();
            return rows.Select(MapLancamento).ToList();
        }

        public async Task<CrmDivisaoFinanceiraDto> ObterDivisaoFinanceiraAsync(DateTime? referenciaMes)
        {
            await SincronizarContratosLegadosAsync();
            var referencia = PrimeiroDiaMes(referenciaMes ?? DateTime.Today);
            var rows = await _crmRepository.ObterDivisaoFinanceiraAsync(referencia);

            return new CrmDivisaoFinanceiraDto
            {
                ReferenciaMes = referencia.ToString("yyyy-MM"),
                TotalPago = rows.Sum(item => item.TotalPago),
                FarleyTotal = rows.Sum(item => item.FarleyValor),
                SocioTotal = rows.Sum(item => item.SocioValor),
                Detalhes = rows.Select(item => new CrmDivisaoDetalheDto
                {
                    TipoLancamento = NormalizeLancamentoTipo(item.TipoLancamento),
                    TotalPago = item.TotalPago,
                    FarleyValor = item.FarleyValor,
                    SocioValor = item.SocioValor
                }).ToList()
            };
        }

        private async Task SincronizarContratosLegadosAsync()
        {
            var inseridos = await _crmRepository.SincronizarContratosLegadosAsync();
            if (inseridos > 0)
            {
                _logger.LogInformation("CRM legado equalizado: {Quantidade} contratos antigos criados em crm_contrato.", inseridos);
            }
        }

        private async Task<CrmOportunidadeResumoDto> MoverParaReuniaoAsync(int usuarioId, CrmOportunidadeRow row, CrmAgendamentoReuniaoRequest? request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (request?.Data == null)
            {
                AddError(errors, "reuniao.data", "Informe a data da reuniao.");
            }
            if (string.IsNullOrWhiteSpace(request?.HoraInicio) || !TimeSpan.TryParse(request.HoraInicio, out var horaInicio))
            {
                AddError(errors, "reuniao.horaInicio", "Informe o horario de inicio.");
                horaInicio = TimeSpan.Zero;
            }
            if (string.IsNullOrWhiteSpace(request?.HoraFim) || !TimeSpan.TryParse(request.HoraFim, out var horaFim))
            {
                AddError(errors, "reuniao.horaFim", "Informe o horario de fim.");
                horaFim = TimeSpan.Zero;
            }
            if (string.IsNullOrWhiteSpace(request?.Local))
            {
                AddError(errors, "reuniao.local", "Informe o local da reuniao.");
            }
            ThrowIfValidationErrors(errors);

            var reuniaoRequest = request!;
            var local = reuniaoRequest.Local!.Trim();
            var observacoes = NormalizeNullable(reuniaoRequest.Observacoes);
            var resumoReuniao = $"Reuniao agendada para {reuniaoRequest.Data:dd/MM/yyyy}";

            var reservaId = await CriarOuAtualizarReservaAsync(
                row,
                row.ReuniaoReservaId,
                reuniaoRequest.Data!.Value.ToDateTime(TimeOnly.MinValue),
                horaInicio,
                horaFim,
                observacoes,
                ReservaStatus.Confirmado);

            await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                row.Id,
                "reuniao",
                status: row.Status,
                substatus: observacoes ?? resumoReuniao,
                propostaJson: row.PropostaJson,
                propostaEnviadaEm: row.PropostaEnviadaEm,
                reuniaoReservaId: reservaId,
                reuniaoLocal: local,
                respostaReservaId: row.RespostaReservaId,
                pagamentoReservaId: row.PagamentoReservaId,
                dtRespostaPrevista: row.DtRespostaPrevista,
                dtPagamentoPrevista: row.DtPagamentoPrevista,
                closedAt: row.ClosedAt);

            await _crmRepository.AdicionarHistoricoOportunidadeAsync(
                row.Id,
                usuarioId,
                "Movido para Reuniao",
                $"Reuniao agendada para {reuniaoRequest.Data:dd/MM/yyyy} {reuniaoRequest.HoraInicio}-{reuniaoRequest.HoraFim} em {local}.");

            return (await ObterOportunidadeResumoAsync(row.Id))!;
        }

        private async Task<CrmOportunidadeResumoDto> MoverParaPropostaAsync(int usuarioId, CrmOportunidadeRow row, CrmPropostaDto? request)
        {
            if (request == null && string.IsNullOrWhiteSpace(row.PropostaJson))
            {
                throw ValidationException("Dados invalidos.", ("proposta", "Informe os valores da proposta antes de mover a oportunidade."));
            }

            if (request != null)
            {
                var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                ValidateProposta(row.Tipo, request, errors);
                ThrowIfValidationErrors(errors);

                await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                    row.Id,
                    "proposta",
                    status: row.Status,
                    substatus: row.Substatus,
                    propostaJson: SerializeProposta(request),
                    propostaEnviadaEm: ToDateTimeOffset(request.EnviadaEm) ?? DateTimeOffset.UtcNow,
                    reuniaoReservaId: row.ReuniaoReservaId,
                    reuniaoLocal: row.ReuniaoLocal,
                    respostaReservaId: row.RespostaReservaId,
                    pagamentoReservaId: row.PagamentoReservaId,
                    dtRespostaPrevista: row.DtRespostaPrevista,
                    dtPagamentoPrevista: row.DtPagamentoPrevista,
                    closedAt: row.ClosedAt);

                await _crmRepository.AdicionarHistoricoOportunidadeAsync(row.Id, usuarioId, "Movido para Proposta", BuildPropostaHistorico(request));
            }
            else
            {
                await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                    row.Id,
                    "proposta",
                    status: row.Status,
                    substatus: row.Substatus,
                    propostaJson: row.PropostaJson,
                    propostaEnviadaEm: row.PropostaEnviadaEm,
                    reuniaoReservaId: row.ReuniaoReservaId,
                    reuniaoLocal: row.ReuniaoLocal,
                    respostaReservaId: row.RespostaReservaId,
                    pagamentoReservaId: row.PagamentoReservaId,
                    dtRespostaPrevista: row.DtRespostaPrevista,
                    dtPagamentoPrevista: row.DtPagamentoPrevista,
                    closedAt: row.ClosedAt);

                await _crmRepository.AdicionarHistoricoOportunidadeAsync(row.Id, usuarioId, "Movido para Proposta", "Lead movido para proposta.");
            }

            return (await ObterOportunidadeResumoAsync(row.Id))!;
        }

        private async Task<CrmOportunidadeResumoDto> MoverParaQuaseAsync(int usuarioId, CrmOportunidadeRow row, CrmDatasQuaseFechadoRequest? request)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            if (request?.DtRespostaPrevista == null)
            {
                AddError(errors, "quaseFechado.dtRespostaPrevista", "Informe a data prevista de resposta.");
            }
            if (request?.DtPagamentoPrevista == null)
            {
                AddError(errors, "quaseFechado.dtPagamentoPrevista", "Informe a data prevista de pagamento.");
            }
            ThrowIfValidationErrors(errors);

            var quaseFechadoRequest = request!;
            var observacoes = NormalizeNullable(quaseFechadoRequest.Observacoes);
            var respostaId = await CriarOuAtualizarReservaAsync(
                row,
                row.RespostaReservaId,
                quaseFechadoRequest.DtRespostaPrevista!.Value.ToDateTime(TimeOnly.MinValue),
                new TimeSpan(9, 0, 0),
                new TimeSpan(9, 30, 0),
                $"CRM oportunidade {row.Id} | tipo=resposta | data_prevista={quaseFechadoRequest.DtRespostaPrevista:yyyy-MM-dd}",
                ReservaStatus.Pendente);

            var pagamentoId = await CriarOuAtualizarReservaAsync(
                row,
                row.PagamentoReservaId,
                quaseFechadoRequest.DtPagamentoPrevista!.Value.ToDateTime(TimeOnly.MinValue),
                new TimeSpan(10, 0, 0),
                new TimeSpan(10, 30, 0),
                $"CRM oportunidade {row.Id} | tipo=pagamento | data_prevista={quaseFechadoRequest.DtPagamentoPrevista:yyyy-MM-dd}",
                ReservaStatus.Pendente);

            await _crmRepository.AtualizarMovimentacaoOportunidadeAsync(
                row.Id,
                "quase",
                status: row.Status,
                substatus: observacoes ?? row.Substatus,
                propostaJson: row.PropostaJson,
                propostaEnviadaEm: row.PropostaEnviadaEm,
                reuniaoReservaId: row.ReuniaoReservaId,
                reuniaoLocal: row.ReuniaoLocal,
                respostaReservaId: respostaId,
                pagamentoReservaId: pagamentoId,
                dtRespostaPrevista: quaseFechadoRequest.DtRespostaPrevista.Value.ToDateTime(TimeOnly.MinValue),
                dtPagamentoPrevista: quaseFechadoRequest.DtPagamentoPrevista.Value.ToDateTime(TimeOnly.MinValue),
                closedAt: row.ClosedAt);

            await _crmRepository.AdicionarHistoricoOportunidadeAsync(
                row.Id,
                usuarioId,
                "Movido para Quase fechado",
                $"Datas previstas registradas: resposta {quaseFechadoRequest.DtRespostaPrevista:dd/MM/yyyy}; pagamento {quaseFechadoRequest.DtPagamentoPrevista:dd/MM/yyyy}.");

            return (await ObterOportunidadeResumoAsync(row.Id))!;
        }

        private async Task<long> CriarOuAtualizarReservaAsync(
            CrmOportunidadeRow row,
            long? reservaId,
            DateTime data,
            TimeSpan horaInicio,
            TimeSpan? horaFim,
            string? observacoes,
            ReservaStatus status)
        {
            var observacoesNormalizadas = NormalizeNullable(observacoes);
            if (!reservaId.HasValue)
            {
                return await CriarReservaAsync(row, data, horaInicio, horaFim, observacoesNormalizadas, status);
            }

            var reserva = await _reservaRepository.BuscarPorIdAsync(reservaId.Value);
            if (reserva == null || reserva.Status == ReservaStatus.Cancelado)
            {
                return await CriarReservaAsync(row, data, horaInicio, horaFim, observacoesNormalizadas, status);
            }

            reserva.NomeCliente = string.IsNullOrWhiteSpace(row.NomeContato) ? row.NomeEmpresa : row.NomeContato;
            reserva.QtdPessoas = 1;
            reserva.DataReserva = data.Date;
            reserva.HoraInicio = horaInicio;
            reserva.HoraFim = horaFim;
            reserva.Observacoes = observacoesNormalizadas;
            await _reservaRepository.AtualizarAsync(reserva);
            return reserva.Id ?? reservaId.Value;
        }

        private async Task<long> CriarReservaAsync(CrmOportunidadeRow row, DateTime data, TimeSpan horaInicio, TimeSpan? horaFim, string? observacoes, ReservaStatus status)
        {
            if (!_centralEstabelecimentoId.HasValue)
            {
                throw new InvalidOperationException("CentralEstabelecimentoId nao configurado para o CRM.");
            }

            return await _reservaRepository.AdicionarAsync(new Reserva
            {
                IdEstabelecimento = _centralEstabelecimentoId.Value,
                NomeCliente = string.IsNullOrWhiteSpace(row.NomeContato) ? row.NomeEmpresa : row.NomeContato,
                QtdPessoas = 1,
                DataReserva = data.Date,
                HoraInicio = horaInicio,
                HoraFim = horaFim,
                Status = status,
                Observacoes = observacoes
            });
        }

        private async Task<CrmOportunidadeResumoDto?> ObterOportunidadeResumoAsync(Guid oportunidadeId)
        {
            var row = await _crmRepository.ObterOportunidadeAsync(oportunidadeId);
            return row == null ? null : await MapOportunidadeResumoAsync(row);
        }

        private async Task<CrmOportunidadeResumoDto> MapOportunidadeResumoAsync(CrmOportunidadeRow row)
        {
            var reuniao = await MapReuniaoAsync(row);

            return new CrmOportunidadeResumoDto
            {
                Id = row.Id,
                NomeEmpresa = row.NomeEmpresa,
                NomeContato = row.NomeContato,
                Telefone = row.Telefone,
                Email = row.Email,
                Cnpj = row.Cnpj,
                Tipo = row.Tipo,
                Responsavel = row.Responsavel,
                Coluna = row.Coluna,
                Status = row.Status,
                Substatus = row.Substatus,
                ProximaAcao = row.ProximaAcao,
                Estimativa = DeserializeValores(row.EstimativaJson),
                Proposta = DeserializeProposta(row.PropostaJson, row.PropostaEnviadaEm),
                ReuniaoReservaId = row.ReuniaoReservaId,
                RespostaReservaId = row.RespostaReservaId,
                PagamentoReservaId = row.PagamentoReservaId,
                DtRespostaPrevista = row.DtRespostaPrevista.HasValue
                    ? DateOnly.FromDateTime(row.DtRespostaPrevista.Value)
                    : null,
                DtPagamentoPrevista = row.DtPagamentoPrevista.HasValue
                    ? DateOnly.FromDateTime(row.DtPagamentoPrevista.Value)
                    : null,
                Reuniao = reuniao,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt,
                ClosedAt = row.ClosedAt
            };
        }

        private async Task<CrmOportunidadeDetalheDto> MapOportunidadeDetalheAsync(CrmOportunidadeRow row)
        {
            var resumo = await MapOportunidadeResumoAsync(row);
            return new CrmOportunidadeDetalheDto
            {
                Id = resumo.Id,
                NomeEmpresa = resumo.NomeEmpresa,
                NomeContato = resumo.NomeContato,
                Telefone = resumo.Telefone,
                Email = resumo.Email,
                Cnpj = resumo.Cnpj,
                Tipo = resumo.Tipo,
                Responsavel = resumo.Responsavel,
                Coluna = resumo.Coluna,
                Status = resumo.Status,
                Substatus = resumo.Substatus,
                ProximaAcao = resumo.ProximaAcao,
                Estimativa = resumo.Estimativa,
                Proposta = resumo.Proposta,
                ReuniaoReservaId = resumo.ReuniaoReservaId,
                RespostaReservaId = resumo.RespostaReservaId,
                PagamentoReservaId = resumo.PagamentoReservaId,
                DtRespostaPrevista = resumo.DtRespostaPrevista,
                DtPagamentoPrevista = resumo.DtPagamentoPrevista,
                Reuniao = resumo.Reuniao,
                CreatedAt = resumo.CreatedAt,
                UpdatedAt = resumo.UpdatedAt,
                ClosedAt = resumo.ClosedAt,
                ContratoId = await _crmRepository.ObterContratoIdPorOportunidadeAsync(row.Id)
            };
        }

        private async Task<CrmReservaVinculadaDto?> MapReuniaoAsync(CrmOportunidadeRow row)
        {
            if (!row.ReuniaoReservaId.HasValue)
            {
                return null;
            }

            var reserva = await _crmRepository.ObterReservaAsync(row.ReuniaoReservaId.Value);
            if (reserva == null)
            {
                return null;
            }

            return new CrmReservaVinculadaDto
            {
                Id = reserva.Id,
                DataReserva = DateOnly.FromDateTime(reserva.DataReserva),
                HoraInicio = FormatTime(reserva.HoraInicio),
                HoraFim = FormatTime(reserva.HoraFim),
                Local = row.ReuniaoLocal,
                Observacoes = reserva.Observacoes
            };
        }

        private async Task<CrmContratoDetalheDto> MapContratoDetalheAsync(CrmContratoRow row)
        {
            var implantation = await _crmRepository.ObterImplantacaoAsync(row.Id);
            var lancamentos = await _crmRepository.ListarLancamentosContratoAsync(row.Id);
            var historico = await _crmRepository.ListarHistoricoContratoAsync(row.Id);

            var dto = MapContratoResumo(row);
            return new CrmContratoDetalheDto
            {
                Id = dto.Id,
                EmpresaId = dto.EmpresaId,
                NomeEmpresa = dto.NomeEmpresa,
                EstabelecimentoId = dto.EstabelecimentoId,
                NomeEstabelecimento = dto.NomeEstabelecimento,
                OportunidadeId = dto.OportunidadeId,
                Tipo = dto.Tipo,
                Status = dto.Status,
                Responsavel = dto.Responsavel,
                MensalidadeSaas = dto.MensalidadeSaas,
                MensalidadeMarketing = dto.MensalidadeMarketing,
                MarketingValorFixo = dto.MarketingValorFixo,
                ImplantacaoValor = dto.ImplantacaoValor,
                ImplantacaoPaga = dto.ImplantacaoPaga,
                DiaVencimento = dto.DiaVencimento,
                DataInicioCobranca = dto.DataInicioCobranca,
                Observacoes = dto.Observacoes,
                CreatedAt = dto.CreatedAt,
                UpdatedAt = dto.UpdatedAt,
                Implantacao = implantation == null ? null : new CrmImplantacaoDto
                {
                    Id = implantation.Id,
                    ContratoId = implantation.ContratoId,
                    Status = implantation.Status,
                    ValorTotal = implantation.ValorTotal,
                    ValorPago = implantation.ValorPago,
                    Paga = implantation.Paga,
                    Observacoes = implantation.Observacoes,
                    CreatedAt = implantation.CreatedAt,
                    UpdatedAt = implantation.UpdatedAt
                },
                Lancamentos = lancamentos.Select(MapLancamento).ToList(),
                Historico = historico.Select(MapHistorico).ToList()
            };
        }

        private static CrmContratoResumoDto MapContratoResumo(CrmContratoRow row)
        {
            return new CrmContratoResumoDto
            {
                Id = row.Id,
                EmpresaId = row.EmpresaId,
                NomeEmpresa = row.NomeEmpresa,
                EstabelecimentoId = row.EstabelecimentoId,
                NomeEstabelecimento = row.NomeEstabelecimento,
                OportunidadeId = row.OportunidadeId,
                Tipo = row.Tipo,
                Status = row.Status,
                Responsavel = row.Responsavel,
                MensalidadeSaas = row.MensalidadeSaas,
                MensalidadeMarketing = row.MensalidadeMarketing,
                MarketingValorFixo = row.MarketingValorFixo,
                ImplantacaoValor = row.ImplantacaoValor,
                ImplantacaoPaga = row.ImplantacaoPaga,
                DiaVencimento = row.DiaVencimento,
                DataInicioCobranca = DateOnly.FromDateTime(row.DataInicioCobranca),
                Observacoes = row.Observacoes,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            };
        }

        private static CrmLancamentoDto MapLancamento(CrmLancamentoRow row)
        {
            return new CrmLancamentoDto
            {
                Id = row.Id,
                ContratoId = row.ContratoId,
                EmpresaId = row.EmpresaId,
                NomeEmpresa = row.NomeEmpresa,
                TipoContrato = row.TipoContrato,
                TipoLancamento = NormalizeLancamentoTipo(row.TipoLancamento),
                Descricao = row.Descricao,
                Competencia = FormatCompetencia(row.Competencia),
                DataVencimento = DateOnly.FromDateTime(row.DataVencimento),
                DataPagamento = row.DataPagamento.HasValue ? DateOnly.FromDateTime(row.DataPagamento.Value) : null,
                ValorTotal = row.ValorTotal,
                ValorPago = row.ValorPago,
                Status = row.Status,
                CreatedAt = row.CreatedAt,
                UpdatedAt = row.UpdatedAt
            };
        }

        private static CrmHistoricoItemDto MapHistorico(CrmHistoricoRow row)
        {
            return new CrmHistoricoItemDto
            {
                Id = row.Id,
                UsuarioId = row.UsuarioId,
                Acao = HumanizeHistoricoAcao(row.Acao),
                Detalhe = row.Detalhe,
                CreatedAt = row.CreatedAt
            };
        }

        private static CrmValoresDto DeserializeValores(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CrmValoresDto();
            }

            return JsonSerializer.Deserialize<CrmValoresDto>(json, JsonOptions) ?? new CrmValoresDto();
        }

        private static CrmPropostaDto? DeserializeProposta(string? json, DateTimeOffset? enviadaEm)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            var proposta = JsonSerializer.Deserialize<CrmPropostaDto>(json, JsonOptions) ?? new CrmPropostaDto();
            proposta.EnviadaEm ??= enviadaEm.HasValue ? DateOnly.FromDateTime(enviadaEm.Value.DateTime) : null;
            return proposta;
        }

        private static string SerializeOrEmpty(CrmValoresDto? valores)
        {
            return JsonSerializer.Serialize(valores ?? new CrmValoresDto(), JsonOptions);
        }

        private static string SerializeProposta(CrmPropostaDto proposta)
        {
            return JsonSerializer.Serialize(proposta, JsonOptions);
        }

        private static string BuildPropostaHistorico(CrmPropostaDto proposta)
        {
            var partes = new List<string>();
            if (proposta.SaaSMensalidade.HasValue && proposta.SaaSMensalidade.Value > 0)
            {
                partes.Add($"SaaS {FormatCurrency(proposta.SaaSMensalidade.Value)}");
            }
            if (proposta.Implantacao.HasValue && proposta.Implantacao.Value > 0)
            {
                partes.Add($"implantacao {FormatCurrency(proposta.Implantacao.Value)}");
            }
            if (proposta.MarketingMensalidade.HasValue && proposta.MarketingMensalidade.Value > 0)
            {
                partes.Add($"marketing mensal {FormatCurrency(proposta.MarketingMensalidade.Value)}");
            }
            if (proposta.MarketingValorFixo.HasValue && proposta.MarketingValorFixo.Value > 0)
            {
                partes.Add($"marketing fixo {FormatCurrency(proposta.MarketingValorFixo.Value)}");
            }

            return partes.Count == 0 ? "Proposta registrada." : $"Proposta registrada: {string.Join(", ", partes)}.";
        }

        private static void ValidateProposta(string tipo, CrmPropostaDto proposta, Dictionary<string, List<string>> errors)
        {
            var normalizedTipo = NormalizeToken(tipo);
            switch (normalizedTipo)
            {
                case "saas":
                    if ((proposta.SaaSMensalidade ?? 0m) <= 0m)
                    {
                        AddError(errors, "proposta.saaSMensalidade", "Informe a mensalidade SaaS.");
                    }
                    if ((proposta.Implantacao ?? 0m) < 0m)
                    {
                        AddError(errors, "proposta.implantacao", "Implantacao nao pode ser negativa.");
                    }
                    break;
                case "marketing":
                    if ((proposta.MarketingMensalidade ?? 0m) <= 0m && (proposta.MarketingValorFixo ?? 0m) <= 0m)
                    {
                        AddError(errors, "proposta", "Informe a mensalidade ou o valor fixo de marketing.");
                    }
                    break;
                case "ambos":
                    if ((proposta.SaaSMensalidade ?? 0m) <= 0m)
                    {
                        AddError(errors, "proposta.saaSMensalidade", "Informe a mensalidade SaaS.");
                    }
                    if ((proposta.MarketingMensalidade ?? 0m) <= 0m && (proposta.MarketingValorFixo ?? 0m) <= 0m)
                    {
                        AddError(errors, "proposta.marketingMensalidade", "Informe a mensalidade ou o valor fixo de marketing.");
                    }
                    break;
            }
        }

        private static DateTime? ParseCompetencia(string? competencia, string field, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(competencia))
            {
                return null;
            }

            var trimmed = competencia.Trim();
            var parts = trimmed.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && int.TryParse(parts[0], out var year)
                && int.TryParse(parts[1], out var month)
                && year >= 1
                && month >= 1
                && month <= 12)
            {
                return new DateTime(year, month, 1);
            }

            AddError(errors, field, "Informe a competencia no formato yyyy-MM.");
            return null;
        }

        private static string? FormatCompetencia(DateTime? competencia)
        {
            return competencia?.ToString("yyyy-MM");
        }

        private static string NormalizeLancamentoTipo(string? value)
        {
            var normalized = NormalizeToken(value);
            return normalized switch
            {
                // Old DB enum values (backward compat)
                "implantacao" => "implantacao_saas",
                "mensalidade" => "mensalidade_saas",
                "fixo" => "marketing_valor_fixo",
                "marketing_fixo" => "marketing_valor_fixo",
                _ => normalized
            };
        }

        private static string HumanizeHistoricoAcao(string? value)
        {
            return NormalizeToken(value) switch
            {
                "lead_criado" => "Lead criado",
                "lead atualizado" => "Lead atualizado",
                "oportunidade_atualizada" => "Lead atualizado",
                "proposta_registrada" => "Proposta registrada",
                "movido_coluna" => "Movimentacao registrada",
                "reuniao_agendada" => "Movido para Reuniao",
                "quase_fechado" => "Movido para Quase fechado",
                "negocio_fechado" => "Negocio fechado",
                "contrato_criado" => "Contrato criado",
                "contrato_atualizado" => "Contrato atualizado",
                "lancamento_criado" => "Lancamento criado",
                "lancamento_atualizado" => "Lancamento atualizado",
                _ => value ?? string.Empty
            };
        }

        private static string FormatCurrency(decimal value)
        {
            return value.ToString("C", PtBrCulture);
        }

        private static string FormatTime(TimeSpan value)
        {
            return value.ToString(@"hh\:mm");
        }

        private static string? FormatTime(TimeSpan? value)
        {
            return value.HasValue ? FormatTime(value.Value) : null;
        }

        private static DateTimeOffset? ToDateTimeOffset(DateOnly? value)
        {
            return value.HasValue
                ? new DateTimeOffset(value.Value.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)
                : null;
        }

        private static string ResolverStatusLancamento(decimal valorPago, decimal valorTotal)
        {
            if (valorPago <= 0)
            {
                return "pendente";
            }

            return valorPago >= valorTotal ? "pago" : "parcial";
        }

        private static DateTime PrimeiroDiaMes(DateTime data)
        {
            return new DateTime(data.Year, data.Month, 1);
        }

        private static RequestValidationException ValidationException(string message, params (string Field, string Message)[] items)
        {
            var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                AddError(errors, item.Field, item.Message);
            }

            return new RequestValidationException(message, errors);
        }

        private static void ValidateRequired(string? value, string field, string message, Dictionary<string, List<string>> errors)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                AddError(errors, field, message);
            }
        }

        private static void ValidateAllowed(string value, string field, IEnumerable<string> allowed, string message, Dictionary<string, List<string>> errors)
        {
            if (!allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                AddError(errors, field, message);
            }
        }

        private static void ThrowIfValidationErrors(Dictionary<string, List<string>> errors)
        {
            if (errors.Count > 0)
            {
                throw new RequestValidationException("Dados invalidos.", errors);
            }
        }

        private static void AddError(Dictionary<string, List<string>> errors, string field, string message)
        {
            if (!errors.TryGetValue(field, out var messages))
            {
                messages = new List<string>();
                errors[field] = messages;
            }

            if (!messages.Contains(message, StringComparer.OrdinalIgnoreCase))
            {
                messages.Add(message);
            }
        }

        private static string NormalizeToken(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().ToLowerInvariant();
        }

        private static string? NormalizeNullable(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
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
    }
}
