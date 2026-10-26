using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Payments.MercadoPago;
using Dapper;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.Payments.Chat
{
    /// <summary>
    /// Cobranca Pix do atendente no chat (CONTRATOS.md §8.5). So Pix, pelo mesmo IPixChargeService do cardapio, na conta MP da loja.
    /// Vinculada: o valor e o saldo do pedido calculado aqui, nunca o digitado. Avulsa: nunca toca em pedido. Acima do limite da loja a
    /// cobranca espera aprovacao do gerente; reembolso e troca de vinculo sao do gerente (ou pedidos por /solicitar-aprovacao).
    /// Toda mudanca passa por CobrancaChatSql.MudarStatusAsync (historico + outbox cobrancaAtualizada publicada depois do commit).
    /// </summary>
    public sealed class CobrancaChatService
    {
        private readonly NpgsqlDataSource _db;
        private readonly IPixChargeService _pix;
        private readonly IPagamentoEstornoService _estornos;
        private readonly IPagamentoSincronizador _sync;
        private readonly IMercadoPagoCredencialResolver _credenciais;
        private readonly IPagamentoSegredoProtector _segredos;
        private readonly ICobrancaChatMensageiro _mensageiro;
        private readonly IConfiguration _config;
        private readonly ILogger<CobrancaChatService> _logger;

        public CobrancaChatService(NpgsqlDataSource db, IPixChargeService pix, IPagamentoEstornoService estornos, IPagamentoSincronizador sync,
            IMercadoPagoCredencialResolver credenciais, IPagamentoSegredoProtector segredos, ICobrancaChatMensageiro mensageiro, IConfiguration config,
            ILogger<CobrancaChatService> logger)
        {
            _db = db;
            _pix = pix;
            _estornos = estornos;
            _sync = sync;
            _credenciais = credenciais;
            _segredos = segredos;
            _mensageiro = mensageiro;
            _config = config;
            _logger = logger;
        }

        private static string ContextoLink(Guid cobranca) => $"cobranca_chat.link:{cobranca:N}";
        private static string AtorNucleo(AtorCobranca? a) => a == null ? "sistema" : "usuario";

        // ---- config ----------------------------------------------------------------------------------------------------------

        public async Task<CobrancaChatConfigDto> ConfigAsync(Guid loja, AtorCobranca ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ConfigAsync(cx, loja, ct);
            c.PodeAprovar = ator.PodeAprovar;
            c.PodeReembolsar = ator.PodeReembolsar;
            c.PixDisponivel = await CobrancaChatRepositorio.PixDisponivelAsync(cx, loja, _credenciais.AmbienteAtivo, ct);
            return c;
        }

        public async Task<CobrancaChatConfigDto> SalvarConfigAsync(Guid loja, AtorCobranca ator, CobrancaChatConfigRequest? r, CancellationToken ct)
        {
            if (r == null) throw PagamentoException.Invalida("Corpo obrigatorio.");
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ConfigAsync(cx, loja, ct);
            if (r.Habilitada.HasValue) c.Habilitada = r.Habilitada.Value;
            if (r.PermitirAvulsa.HasValue) c.PermitirAvulsa = r.PermitirAvulsa.Value;
            if (r.SemLimite == true) c.LimiteSemAprovacao = null;
            else if (r.LimiteSemAprovacao.HasValue)
            {
                if (r.LimiteSemAprovacao.Value < 0 || r.LimiteSemAprovacao.Value > CobrancaChatRegras.ValorMaximo || decimal.Round(r.LimiteSemAprovacao.Value, 2) != r.LimiteSemAprovacao.Value)
                    throw PagamentoException.Regra(PagamentoCodigos.ValorInvalido, "Limite invalido.");
                c.LimiteSemAprovacao = r.LimiteSemAprovacao.Value;
            }
            if (r.TemplateNome != null) c.TemplateNome = string.IsNullOrWhiteSpace(r.TemplateNome) ? null : r.TemplateNome.Trim();
            if (!string.IsNullOrWhiteSpace(r.TemplateIdioma)) c.TemplateIdioma = r.TemplateIdioma.Trim();
            if (r.ConfirmarPagamentoCliente.HasValue) c.ConfirmarPagamentoCliente = r.ConfirmarPagamentoCliente.Value;
            await CobrancaChatRepositorio.SalvarConfigAsync(cx, loja, c, ator.UsuarioId, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, null, loja, "cobranca_chat_config", loja.ToString(), "alterada", "usuario", ator.UsuarioId,
                new { c.Habilitada, c.PermitirAvulsa, c.LimiteSemAprovacao, c.TemplateNome, c.ConfirmarPagamentoCliente }, ct);
            return await ConfigAsync(loja, ator, ct);
        }

        // ---- leitura ---------------------------------------------------------------------------------------------------------

        public async Task<CobrancaChatListaDto> ListarAsync(Guid loja, Guid conversaId, string? status, AtorCobranca ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, null, conversaId, loja, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            var ids = await CobrancaChatRepositorio.ConversasDoClienteAsync(cx, null, conversa, ct);
            var linhas = await CobrancaChatRepositorio.ListarAsync(cx, loja, ids, string.IsNullOrWhiteSpace(status) ? null : status.Trim(), ct);
            var itens = new List<CobrancaChatDto>();
            foreach (var l in linhas) itens.Add(await DtoAsync(cx, l, ator, null, comPix: false, ct));
            return new CobrancaChatListaDto { Itens = itens };
        }

        public async Task<CobrancaChatDto> ObterAsync(Guid loja, Guid id, AtorCobranca ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            return await DtoAsync(cx, c, ator, null, comPix: true, ct);
        }

        public async Task<CobrancaHistoricoDto> HistoricoAsync(Guid loja, Guid id, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            _ = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            return new CobrancaHistoricoDto { Itens = await CobrancaChatRepositorio.HistoricoAsync(cx, id, ct) };
        }

        // ---- criar -----------------------------------------------------------------------------------------------------------

        public async Task<CobrancaChatDto> CriarAsync(Guid loja, Guid conversaId, CriarCobrancaChatRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            if (r == null) throw PagamentoException.Invalida("Corpo obrigatorio.");
            var chave = CobrancaChatRegras.ExigirChave(r.IdempotencyKey);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var config = await CobrancaChatRepositorio.ConfigAsync(cx, loja, ct);
            if (!config.Habilitada) throw PagamentoException.Regra(CobrancaChatCodigos.Desativada, "A cobranca pelo chat esta desligada nesta loja.");
            var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, null, conversaId, loja, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            var conversasDoCliente = await CobrancaChatRepositorio.ConversasDoClienteAsync(cx, null, conversa, ct);

            var tipo = CobrancaChatRegras.Tipo(r.Tipo, r.PedidoId, r.PrePedidoId);
            // Clique duplo / repeticao: mesma chave devolve a mesma cobranca (e retoma o Pix se ele ficou sem resposta).
            var existente = await CobrancaChatRepositorio.PorChaveAsync(cx, null, loja, chave, ct);
            if (existente != null) return await RepetidaAsync(cx, existente, conversasDoCliente, tipo, r.PedidoId, r.PrePedidoId, r.Valor, ator, ct);

            decimal valor;
            string descricao;
            if (tipo == TiposCobranca.Vinculada)
            {
                var alvo = await AlvoValidoAsync(cx, null, loja, conversa, conversasDoCliente, r.PedidoId, r.PrePedidoId, ct);
                valor = CobrancaChatRegras.ValorVinculada(alvo, r.Valor);
                descricao = CobrancaChatRegras.ExigirDescricao(r.Descricao, $"Pedido {alvo.Codigo}");
            }
            else
            {
                if (!config.PermitirAvulsa) throw PagamentoException.Regra(CobrancaChatCodigos.AvulsaDesativada, "Esta loja so cobra pedidos pelo chat.");
                CobrancaChatRegras.ExigirValor(r.Valor);
                valor = r.Valor!.Value;
                descricao = CobrancaChatRegras.ExigirDescricao(r.Descricao);
            }

            var validade = await ValidadeAsync(cx, loja, r.ValidadeMinutos, ct);
            await ExigirPixDisponivelAsync(cx, loja, ct);
            var exige = CobrancaChatRegras.ExigeAprovacao(valor, config.LimiteSemAprovacao, ator.PodeAprovar);
            var nova = new CobrancaChatLinha
            {
                Id = Guid.NewGuid(), IdEstabelecimento = loja, ConversaId = conversa.Id, AutorUsuarioId = ator.UsuarioId, Descricao = descricao, Valor = valor,
                Tipo = tipo, PedidoId = r.PedidoId, PrePedidoId = r.PrePedidoId, ClienteId = conversa.IdCliente, ValidadeMinutos = validade,
                Status = exige ? StatusCobranca.AguardandoAprovacao : StatusCobranca.Criando, IdempotencyKey = chave
            };
            return await GravarEEmitirAsync(cx, nova, conversa, ator, ct)
                   ?? await RepetidaAsync(cx, (await CobrancaChatRepositorio.PorChaveAsync(cx, null, loja, chave, ct))!, conversasDoCliente, tipo, r.PedidoId,
                       r.PrePedidoId, r.Valor, ator, ct);
        }

        private async Task<CobrancaChatDto> RepetidaAsync(NpgsqlConnection cx, CobrancaChatLinha e, Guid[] conversasDoCliente, string tipo, int? pedidoId, Guid? prePedidoId,
            decimal? valor, AtorCobranca ator, CancellationToken ct)
        {
            if (!conversasDoCliente.Contains(e.ConversaId) || e.Tipo != tipo || e.PedidoId != pedidoId || e.PrePedidoId != prePedidoId ||
                (tipo == TiposCobranca.Avulsa && valor != e.Valor))
                throw new PagamentoException(PagamentoCodigos.IdempotenciaConflito, 409, "Esta chave de idempotencia ja foi usada com outros dados.");
            string? token = null;
            if (e.Status == StatusCobranca.Criando)
            {
                token = await GerarPixAsync(e, ator, ct);
                e = (await CobrancaChatRepositorio.ObterAsync(cx, null, e.Id, e.IdEstabelecimento, false, ct))!;
            }
            return await DtoAsync(cx, e, ator, token ?? AbrirToken(e), comPix: true, ct);
        }

        /// <summary>Grava a cobranca (e o pedido de aprovacao, se acima do limite) e gera o Pix. Null = a chave ja existia (corrida).</summary>
        private async Task<CobrancaChatDto?> GravarEEmitirAsync(NpgsqlConnection cx, CobrancaChatLinha nova, ConversaCobranca conversa, AtorCobranca ator, CancellationToken ct)
        {
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                bool gravou;
                try
                {
                    gravou = await CobrancaChatRepositorio.InserirAsync(cx, tx, nova, conversa, ator.Nome, ct);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation && ex.ConstraintName?.StartsWith("ux_cobranca_chat_aberta") == true)
                {
                    await tx.RollbackAsync(ct);
                    // Clique duplo com a mesma chave pode bater primeiro neste indice: e a mesma cobranca, nao outra aberta.
                    if (await CobrancaChatRepositorio.PorChaveAsync(cx, null, nova.IdEstabelecimento, nova.IdempotencyKey, ct) != null) return null;
                    throw await CobrancaAbertaAsync(cx, nova.IdEstabelecimento, nova.PedidoId, nova.PrePedidoId, ct);
                }
                if (!gravou)
                {
                    await tx.RollbackAsync(ct);
                    return null;
                }
                if (nova.Status == StatusCobranca.AguardandoAprovacao)
                    await CobrancaChatRepositorio.InserirAprovacaoAsync(cx, tx, nova.IdEstabelecimento, nova.Id, AcoesAprovacao.Emitir, nova.Valor, nova.PedidoId,
                        nova.PrePedidoId, null, ator.UsuarioId, $"emitir:{nova.Id:N}", ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, nova.IdEstabelecimento, "cobranca_chat", nova.Id.ToString(), "criada", "usuario", ator.UsuarioId,
                    new { nova.Valor, nova.Tipo, nova.PedidoId, nova.PrePedidoId, conversaId = nova.ConversaId, nova.ReemitidaDe, nova.Status }, ct);
                await tx.CommitAsync(ct);
            }
            _logger.LogInformation("[cobranca.chat] ev=criada cobranca={Id} loja={Loja} usuario={Usuario} pedido={Pedido} status={Status}",
                nova.Id, nova.IdEstabelecimento, ator.UsuarioId, nova.PedidoId, nova.Status);

            string? token = null;
            if (nova.Status == StatusCobranca.Criando) token = await GerarPixAsync(nova, ator, ct);
            var linha = (await CobrancaChatRepositorio.ObterAsync(cx, null, nova.Id, nova.IdEstabelecimento, false, ct))!;
            return await DtoAsync(cx, linha, ator, token, comPix: true, ct);
        }

        /// <summary>
        /// Gera (ou retoma) o Pix da cobranca 'criando' com a chave chat:{id}: repetir nunca cria um segundo Pix no MP. MP sem resposta deixa
        /// a cobranca em 'criando' (o CobrancaChatManutencao tenta de novo); recusa definitiva leva a 'recusado'. Devolve o token do link.
        /// </summary>
        public async Task<string?> GerarPixAsync(CobrancaChatLinha c, AtorCobranca? ator, CancellationToken ct)
        {
            PixChargeResult r;
            try
            {
                r = await _pix.CriarAsync(new PixChargeRequest
                {
                    IdEstabelecimento = c.IdEstabelecimento, Origem = OrigensPagamento.Chat, Valor = c.Valor, Descricao = c.Descricao,
                    IdempotencyKey = $"chat:{c.Id:N}", PedidoId = c.PedidoId, PrePedidoId = c.PrePedidoId, ConversaId = c.ConversaId, ClienteId = c.ClienteId,
                    CriadoPor = c.AutorUsuarioId, ValidadeMinutos = c.ValidadeMinutos, GerarLinkPublico = true
                }, ct);
            }
            catch (PagamentoException ex) when (ex.Http is 409 or 422 || ex.Codigo == PagamentoCodigos.MercadoPagoErro)
            {
                await RecusarAsync(c, ex.Codigo, ator, ct);
                throw;
            }
            catch (PagamentoException ex)
            {
                await RegistrarErroAsync(c.Id, ex.Codigo, ct);
                throw new PagamentoException(ex.Codigo, ex.Http, ex.Message, new Dictionary<string, object?> { ["cobrancaId"] = c.Id }, ex);
            }

            var token = r.TokenPublico;
            if (token == null && StatusIntent.Aberto(r.Status))
            {
                await using var cx0 = await _db.OpenConnectionAsync(ct);
                var cifrado = await cx0.ExecuteScalarAsync<string?>(new CommandDefinition(
                    "SELECT link_token_cifrado FROM cobranca_chat WHERE id = @Id", new { c.Id }, cancellationToken: ct));
                token = cifrado == null ? await _pix.GerarLinkPublicoAsync(r.IntentId, c.IdEstabelecimento, ct) : _segredos.Revelar(cifrado, ContextoLink(c.Id));
            }
            await VincularAsync(c.Id, c.IdEstabelecimento, r.IntentId, token, ator, ct);
            return token;
        }

        /// <summary>Liga a cobranca a intencao e alinha o status (o webhook pode ter chegado antes). Intencao travada antes da cobranca: mesma ordem do sincronizador.</summary>
        private async Task VincularAsync(Guid id, Guid loja, Guid intentId, string? token, AtorCobranca? ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var intent = (await PagamentoIntentSql.ObterAsync(cx, tx, intentId, true, ct))!;
            var c = (await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct))!;
            if (c.IdIntent != null && c.IdIntent != intentId) throw PagamentoException.Estado("Cobranca ja ligada a outro pagamento.", c.Status);
            var novaLigacao = c.IdIntent == null;
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat SET id_intent = @Intent, expira_em = COALESCE(@Expira, expira_em), ultimo_erro = NULL,
       link_token_cifrado = COALESCE(@Token, link_token_cifrado), updated_at = NOW()
 WHERE id = @Id", new
            {
                Id = id, Intent = intentId, Expira = intent.ExpiraEm,
                Token = token == null ? null : _segredos.Proteger(token, ContextoLink(id))
            }, tx, cancellationToken: ct));
            c.IdIntent = intentId;
            var novo = PagamentoStatusRules.StatusDaCobranca(c.Status, intent.Status);
            if (novo != null)
                await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, c, novo, AtorNucleo(ator), ator?.UsuarioId, new { intentId, intentStatus = intent.Status }, ct);
            if (novaLigacao && StatusIntent.FamiliaPaga(intent.Status))
                await CobrancaChatEfeitos.AposMudancaAsync(cx, tx, c, intent, StatusIntent.AguardandoPagamento, ct);
            await tx.CommitAsync(ct);
        }

        private async Task RecusarAsync(CobrancaChatLinha c, string codigo, AtorCobranca? ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using var tx = await cx.BeginTransactionAsync(ct);
            var atual = await CobrancaChatRepositorio.ObterAsync(cx, tx, c.Id, c.IdEstabelecimento, true, ct);
            if (atual is { Status: StatusCobranca.Criando, IdIntent: null })
            {
                await cx.ExecuteAsync(new CommandDefinition("UPDATE cobranca_chat SET ultimo_erro = @E, motivo = COALESCE(motivo, @E) WHERE id = @Id",
                    new { c.Id, E = codigo }, tx, cancellationToken: ct));
                await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, atual, StatusCobranca.Recusado, AtorNucleo(ator), ator?.UsuarioId, new { codigo }, ct);
            }
            await tx.CommitAsync(ct);
        }

        private async Task RegistrarErroAsync(Guid id, string codigo, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            await cx.ExecuteAsync(new CommandDefinition("UPDATE cobranca_chat SET ultimo_erro = @E, updated_at = NOW() WHERE id = @Id", new { Id = id, E = codigo }, cancellationToken: ct));
        }

        // ---- enviar ----------------------------------------------------------------------------------------------------------

        public async Task<CobrancaChatDto> EnviarAsync(Guid loja, Guid id, EnviarCobrancaChatRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            var formato = CobrancaChatRegras.Formato(r?.Formato);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            ExigirAberta(c);

            // Dois cliques (ou dois atendentes) ao mesmo tempo: so um envio sai.
            var trava = $"cobranca_chat_envio:{id:N}";
            if (!await cx.ExecuteScalarAsync<bool>(new CommandDefinition("SELECT pg_try_advisory_lock(hashtext(@K))", new { K = trava }, cancellationToken: ct)))
                throw new PagamentoException(CobrancaChatCodigos.EnvioEmAndamento, 409, "Esta cobranca ja esta sendo enviada.");
            try
            {
                c = (await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct))!;
                ExigirAberta(c);
                if (c.Status == StatusCobranca.Enviado && c.EnviadoEm > DateTime.UtcNow - CobrancaChatRegras.JanelaReenvioDuplicado)
                    return await DtoAsync(cx, c, ator, AbrirToken(c), comPix: true, ct);
                if (c.ExpiraEm.HasValue && c.ExpiraEm.Value <= DateTime.UtcNow.AddMinutes(1))
                    throw PagamentoException.Estado("O Pix desta cobranca venceu. Reemita a cobranca.", c.Status);
                if (!await CobrancaChatRepositorio.ContaDaIntentVivaAsync(cx, c.IdIntent!.Value, ct))
                    throw PagamentoException.Regra(PagamentoCodigos.ContaLojaNaoConectada, "A conta Mercado Pago da loja foi desconectada. Cancele e conecte de novo.");

                var pix = await _pix.ObterAsync(c.IdIntent.Value, loja, ct);
                if (StatusIntent.FamiliaPaga(pix.Status)) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga.");
                if (!StatusIntent.Aberto(pix.Status)) throw PagamentoException.Estado("Esta cobranca nao esta mais aberta.", c.Status);

                var token = AbrirToken(c) ?? await NovoTokenAsync(cx, c, ct);
                var link = Link(token);
                var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, null, c.ConversaId, loja, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
                var criadaPor = string.IsNullOrWhiteSpace(ator.Nome) ? "atendente" : ator.Nome!;
                Guid mensagemId;
                string via;
                if (CobrancaChatRegras.JanelaAberta(conversa.Janela24hFim, conversa.Estado, DateTime.UtcNow))
                {
                    var textos = CobrancaChatRegras.Textos(formato, c.Descricao, c.Valor, c.ExpiraEm, link, pix.CopiaECola);
                    mensagemId = await _mensageiro.EnviarTextoAsync(conversa.Id, loja, textos[0], criadaPor, ct);
                    foreach (var t in textos.Skip(1)) await _mensageiro.EnviarTextoAsync(conversa.Id, loja, t, criadaPor, ct);
                    via = "texto";
                }
                else
                {
                    var config = await CobrancaChatRepositorio.ConfigAsync(cx, loja, ct);
                    var modelo = string.IsNullOrWhiteSpace(config.TemplateNome) ? null
                        : await _mensageiro.TemplateAprovadoAsync(loja, conversa.IdCanal, config.TemplateNome!, config.TemplateIdioma, ct);
                    var parametros = modelo == null ? null : CobrancaChatRegras.ParametrosModelo(modelo.VariaveisCorpo, c.Descricao, c.Valor, link);
                    if (modelo == null || parametros == null)
                        throw PagamentoException.Regra(CobrancaChatCodigos.JanelaFechada,
                            "A janela de 24 horas do WhatsApp esta fechada e a loja nao tem modelo de cobranca aprovado. Peca para o cliente mandar uma mensagem.");
                    var textoNoChat = $"[Modelo {modelo.Nome}] {c.Descricao} - {CobrancaChatRegras.Moeda(c.Valor)} - {link}";
                    mensagemId = await _mensageiro.EnviarTemplateAsync(conversa.Id, loja, modelo, parametros, modelo.BotaoUrlVariavel ? token : null, textoNoChat, criadaPor, ct);
                    via = "modelo";
                }

                await using (var tx = await cx.BeginTransactionAsync(ct))
                {
                    var atual = (await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct))!;
                    await cx.ExecuteAsync(new CommandDefinition("UPDATE cobranca_chat SET enviado_em = NOW(), id_mensagem = @M WHERE id = @Id",
                        new { Id = id, M = mensagemId }, tx, cancellationToken: ct));
                    var detalhe = new { formato, via, mensagemId };
                    if (atual.Status == StatusCobranca.Aguardando)
                        await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, atual, StatusCobranca.Enviado, "usuario", ator.UsuarioId, detalhe, ct);
                    else if (StatusCobranca.Aberta(atual.Status))
                        await CobrancaChatRepositorio.MarcarAsync(cx, tx, atual, "usuario", ator.UsuarioId, new { reenvio = true, formato, via, mensagemId }, ct);
                    await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "cobranca_chat", id.ToString(), "enviada", "usuario", ator.UsuarioId,
                        new { formato, via, mensagemId, c.PedidoId, conversaId = c.ConversaId }, ct);
                    await tx.CommitAsync(ct);
                }
                c = (await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct))!;
                return await DtoAsync(cx, c, ator, token, comPix: true, ct);
            }
            finally
            {
                await cx.ExecuteAsync(new CommandDefinition("SELECT pg_advisory_unlock(hashtext(@K))", new { K = trava }, cancellationToken: CancellationToken.None));
            }
        }

        private static void ExigirAberta(CobrancaChatLinha c)
        {
            if (c.Status is StatusCobranca.Aguardando or StatusCobranca.Enviado && c.IdIntent.HasValue) return;
            if (c.Status is StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado or StatusCobranca.Reembolsado)
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga.");
            throw PagamentoException.Estado("So cobranca com Pix gerado e em aberto pode ser enviada.", c.Status);
        }

        private async Task<string> NovoTokenAsync(NpgsqlConnection cx, CobrancaChatLinha c, CancellationToken ct)
        {
            var token = await _pix.GerarLinkPublicoAsync(c.IdIntent!.Value, c.IdEstabelecimento, ct);
            await cx.ExecuteAsync(new CommandDefinition("UPDATE cobranca_chat SET link_token_cifrado = @T WHERE id = @Id",
                new { c.Id, T = _segredos.Proteger(token, ContextoLink(c.Id)) }, cancellationToken: ct));
            return token;
        }

        private string? AbrirToken(CobrancaChatLinha c) =>
            string.IsNullOrEmpty(c.LinkTokenCifrado) ? null : _segredos.Revelar(c.LinkTokenCifrado, ContextoLink(c.Id));

        public string Link(string token)
        {
            var baseUrl = (_config["Payments:MercadoPago:LinkPixBaseUrl"] ?? _config["Payments:MercadoPago:PainelUrl"] ?? _config["Rastreio:PublicBaseUrl"])?.Trim().TrimEnd('/');
            return string.IsNullOrEmpty(baseUrl) ? $"/pix/{token}" : $"{baseUrl}/pix/{token}";
        }

        // ---- cancelar e reemitir ---------------------------------------------------------------------------------------------

        public async Task<CobrancaChatDto> CancelarAsync(Guid loja, Guid id, CancelarCobrancaChatRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            await CancelarInternoAsync(loja, id, CobrancaChatRegras.Motivo(r?.Motivo), ator, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = (await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct))!;
            return await DtoAsync(cx, c, ator, null, comPix: false, ct);
        }

        /// <summary>
        /// Cancela de verdade no MP (IPixChargeService.CancelarAsync). Sem token da loja (conta desconectada) cancela so do nosso lado: se o
        /// cliente pagar mesmo assim, o pagamento tardio volta a cobranca para 'pago'. Ja paga = 409 PAG_JA_PAGO. <paramref name="ator"/> null = sistema.
        /// </summary>
        public async Task CancelarInternoAsync(Guid loja, Guid id, string? motivo, AtorCobranca? ator, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            switch (c.Status)
            {
                case StatusCobranca.Cancelado:
                    return;
                case StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado or StatusCobranca.Reembolsado:
                    throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga; use o reembolso.");
                case StatusCobranca.Expirado or StatusCobranca.Recusado:
                    throw PagamentoException.Estado("Esta cobranca ja esta encerrada.", c.Status);
            }

            if (c.IdIntent == null)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                var atual = (await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct))!;
                if (atual.IdIntent == null && atual.Status is StatusCobranca.AguardandoAprovacao or StatusCobranca.Criando)
                {
                    await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, atual, StatusCobranca.Cancelado, AtorNucleo(ator), ator?.UsuarioId, new { motivo }, ct);
                    await cx.ExecuteAsync(new CommandDefinition(
                        "UPDATE cobranca_chat_aprovacao SET status = 'cancelada', updated_at = NOW() WHERE id_cobranca = @Id AND status = 'pendente'", new { Id = id }, tx, cancellationToken: ct));
                    await FinalizarCancelamentoAsync(cx, tx, atual, motivo, ator, ct);
                    await tx.CommitAsync(ct);
                    return;
                }
                await tx.RollbackAsync(ct);
                c = atual;
                if (c.IdIntent == null) throw PagamentoException.Estado("Esta cobranca nao pode ser cancelada agora.", c.Status);
            }

            try
            {
                await _pix.CancelarAsync(c.IdIntent!.Value, loja, AtorNucleo(ator), ator?.UsuarioId, ct);
            }
            catch (PagamentoException ex) when (ex.Codigo is PagamentoCodigos.ContaLojaNaoConectada or PagamentoCodigos.ContaLojaReautorizar or PagamentoCodigos.NaoConfigurado)
            {
                _logger.LogWarning("[cobranca.chat] ev=cancelada_sem_conta cobranca={Id} codigo={Codigo}", id, ex.Codigo);
                await using var tx = await cx.BeginTransactionAsync(ct);
                var intent = (await PagamentoIntentSql.ObterAsync(cx, tx, c.IdIntent!.Value, true, ct))!;
                if (StatusIntent.Aberto(intent.Status) || intent.Status == StatusIntent.Recusado)
                    await _sync.MarcarIntentAsync(cx, tx, intent, StatusIntent.Cancelado, AtorNucleo(ator), ator?.UsuarioId, ct);
                await tx.CommitAsync(ct);
            }

            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var atual = (await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct))!;
                if (atual.Status == StatusCobranca.Cancelado) await FinalizarCancelamentoAsync(cx, tx, atual, motivo, ator, ct);
                await tx.CommitAsync(ct);
                if (atual.Status is StatusCobranca.Pago) throw new PagamentoException(PagamentoCodigos.JaPago, 409, "O cliente pagou antes do cancelamento.");
            }
        }

        private static async Task FinalizarCancelamentoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, string? motivo, AtorCobranca? ator, CancellationToken ct)
        {
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat SET motivo = COALESCE(@M, motivo), cancelado_por = COALESCE(cancelado_por, @U), updated_at = NOW() WHERE id = @Id",
                new { c.Id, M = motivo, U = ator?.UsuarioId }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "cobranca_chat", c.Id.ToString(), "cancelada", AtorNucleo(ator), ator?.UsuarioId,
                new { motivo, c.PedidoId, conversaId = c.ConversaId }, ct);
        }

        /// <summary>
        /// Nova tentativa controlada: cancela a anterior se ainda aberta (no MP), cria outra cobranca com nova chave e reemitida_de. Pago no
        /// meio do caminho = 409 PAG_JA_PAGO (nunca dois Pix pagaveis). Vinculada recalcula o saldo.
        /// </summary>
        public async Task<CobrancaChatDto> ReemitirAsync(Guid loja, Guid id, ReemitirCobrancaChatRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            var chave = CobrancaChatRegras.ExigirChave(r?.IdempotencyKey);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var existente = await CobrancaChatRepositorio.PorChaveAsync(cx, null, loja, chave, ct);
            if (existente != null)
            {
                if (existente.ReemitidaDe != id) throw new PagamentoException(PagamentoCodigos.IdempotenciaConflito, 409, "Esta chave de idempotencia ja foi usada.");
                if (existente.Status == StatusCobranca.Criando) await GerarPixAsync(existente, ator, ct);
                existente = (await CobrancaChatRepositorio.ObterAsync(cx, null, existente.Id, loja, false, ct))!;
                return await DtoAsync(cx, existente, ator, AbrirToken(existente), comPix: true, ct);
            }

            var orig = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            if (orig.ReemitidaPor.HasValue)
                throw new PagamentoException(PagamentoCodigos.CobrancaAberta, 409, "Esta cobranca ja foi reemitida; use a mais recente.",
                    new Dictionary<string, object?> { ["cobrancaId"] = orig.ReemitidaPor });
            if (orig.Status is StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado or StatusCobranca.Reembolsado)
                throw new PagamentoException(PagamentoCodigos.JaPago, 409, "Esta cobranca ja foi paga.");
            if (orig.Status is StatusCobranca.AguardandoAprovacao or StatusCobranca.Criando)
                throw PagamentoException.Estado("Esta cobranca ainda esta sendo emitida.", orig.Status);
            if (orig.Status is StatusCobranca.Aguardando or StatusCobranca.Enviado)
                await CancelarInternoAsync(loja, id, "reemitida", ator, ct);

            var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, null, orig.ConversaId, loja, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            var config = await CobrancaChatRepositorio.ConfigAsync(cx, loja, ct);
            if (!config.Habilitada) throw PagamentoException.Regra(CobrancaChatCodigos.Desativada, "A cobranca pelo chat esta desligada nesta loja.");
            var valor = orig.Valor;
            if (orig.Tipo == TiposCobranca.Vinculada)
            {
                var conversas = await CobrancaChatRepositorio.ConversasDoClienteAsync(cx, null, conversa, ct);
                var alvo = await AlvoValidoAsync(cx, null, loja, conversa, conversas, orig.PedidoId, orig.PrePedidoId, ct);
                valor = CobrancaChatRegras.ValorVinculada(alvo, null);
            }
            var validade = await ValidadeAsync(cx, loja, r?.ValidadeMinutos ?? orig.ValidadeMinutos, ct);
            await ExigirPixDisponivelAsync(cx, loja, ct);
            // Ja passou pela alcada (tinha Pix) e o valor nao subiu: nao pede aprovacao de novo.
            var jaAprovada = orig.IdIntent.HasValue && valor <= orig.Valor;
            var exige = !jaAprovada && CobrancaChatRegras.ExigeAprovacao(valor, config.LimiteSemAprovacao, ator.PodeAprovar);
            var nova = new CobrancaChatLinha
            {
                Id = Guid.NewGuid(), IdEstabelecimento = loja, ConversaId = conversa.Id, AutorUsuarioId = ator.UsuarioId, Descricao = orig.Descricao, Valor = valor,
                Tipo = orig.Tipo, PedidoId = orig.PedidoId, PrePedidoId = orig.PrePedidoId, ClienteId = orig.ClienteId ?? conversa.IdCliente, ValidadeMinutos = validade,
                Status = exige ? StatusCobranca.AguardandoAprovacao : StatusCobranca.Criando, IdempotencyKey = chave, ReemitidaDe = orig.Id
            };
            var dto = await GravarEEmitirAsync(cx, nova, conversa, ator, ct);
            if (dto != null) return dto;
            var repetida = (await CobrancaChatRepositorio.PorChaveAsync(cx, null, loja, chave, ct))!;
            return await DtoAsync(cx, repetida, ator, AbrirToken(repetida), comPix: true, ct);
        }

        // ---- reembolso e aprovacoes ------------------------------------------------------------------------------------------

        public async Task<CobrancaChatDto> ReembolsarAsync(Guid loja, Guid id, ReembolsarCobrancaChatRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            if (!ator.PodeReembolsar)
                throw new PagamentoException(CobrancaChatCodigos.ExigeAprovacao, 403, "Reembolso e do gerente: use /solicitar-aprovacao.");
            var chave = CobrancaChatRegras.ExigirChave(r?.IdempotencyKey);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct) ?? throw NaoEncontrada();
            await ReembolsarInternoAsync(c, r?.Valor, CobrancaChatRegras.Motivo(r?.Motivo), ator.UsuarioId, $"chat:{id:N}:{chave}", ct);
            c = (await CobrancaChatRepositorio.ObterAsync(cx, null, id, loja, false, ct))!;
            return await DtoAsync(cx, c, ator, null, comPix: false, ct);
        }

        private async Task ReembolsarInternoAsync(CobrancaChatLinha c, decimal? valor, string? motivo, int usuario, string chave, CancellationToken ct)
        {
            if (c.IdIntent == null || c.Status is not (StatusCobranca.Pago or StatusCobranca.ReembolsoSolicitado))
                throw PagamentoException.Estado("So cobranca paga pode ser reembolsada.", c.Status);
            await _estornos.EstornarAsync(new EstornoRequest
            {
                IntentId = c.IdIntent.Value, IdEstabelecimento = c.IdEstabelecimento, Valor = valor, Motivo = motivo, UsuarioId = usuario, IdempotencyKey = chave
            }, ct);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await PagamentoAuditoria.RegistrarAsync(cx, null, c.IdEstabelecimento, "cobranca_chat", c.Id.ToString(), "reembolso", "usuario", usuario,
                new { valor, motivo, c.PedidoId }, ct);
        }

        public async Task<CobrancaAprovacaoDto> SolicitarAprovacaoAsync(Guid loja, Guid id, SolicitarAprovacaoRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            if (r == null || !AcoesAprovacao.Valida(r.Acao?.Trim())) throw PagamentoException.Invalida("acao deve ser emitir, reembolsar ou trocar_vinculo.", "acao");
            var acao = r.Acao!.Trim();
            var chave = CobrancaChatRegras.ExigirChave(r.IdempotencyKey);
            var motivo = CobrancaChatRegras.Motivo(r.Motivo);
            await using var cx = await _db.OpenConnectionAsync(ct);
            var repetida = await CobrancaChatRepositorio.AprovacaoPorChaveAsync(cx, null, loja, chave, ct);
            if (repetida != null)
            {
                if (repetida.IdCobranca != id || repetida.Acao != acao) throw new PagamentoException(PagamentoCodigos.IdempotenciaConflito, 409, "Chave ja usada.");
                return Aprovacao(repetida);
            }

            await using var tx = await cx.BeginTransactionAsync(ct);
            var c = await CobrancaChatRepositorio.ObterAsync(cx, tx, id, loja, true, ct) ?? throw NaoEncontrada();
            decimal? valor = null;
            int? pedidoId = null;
            Guid? prePedidoId = null;
            switch (acao)
            {
                case AcoesAprovacao.Emitir:
                    if (c.Status != StatusCobranca.AguardandoAprovacao) throw PagamentoException.Estado("Esta cobranca nao espera aprovacao.", c.Status);
                    valor = c.Valor;
                    break;
                case AcoesAprovacao.Reembolsar:
                    if (c.Status != StatusCobranca.Pago) throw PagamentoException.Estado("So cobranca paga pode ser reembolsada.", c.Status);
                    var restante = c.Valor - c.ValorEstornado;
                    if (r.Valor.HasValue) CobrancaChatRegras.ExigirValor(r.Valor);
                    if (r.Valor > restante) throw PagamentoException.Regra(PagamentoCodigos.EstornoAcimaDoPago, "O reembolso passa do valor pago.");
                    valor = r.Valor ?? restante;
                    break;
                default:
                    if (c.Status != StatusCobranca.Pago) throw PagamentoException.Estado("So cobranca paga troca de pedido.", c.Status);
                    var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, tx, c.ConversaId, loja, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
                    var conversas = await CobrancaChatRepositorio.ConversasDoClienteAsync(cx, tx, conversa, ct);
                    CobrancaChatRegras.Tipo(null, r.PedidoId, r.PrePedidoId);
                    if (r.PedidoId == null && r.PrePedidoId == null) throw PagamentoException.Invalida("Informe o pedido de destino.", "pedidoId");
                    if (r.PedidoId == c.PedidoId && r.PrePedidoId == c.PrePedidoId) throw PagamentoException.Invalida("A cobranca ja esta neste pedido.", "pedidoId");
                    await AlvoValidoAsync(cx, tx, loja, conversa, conversas, r.PedidoId, r.PrePedidoId, ct);
                    (pedidoId, prePedidoId) = (r.PedidoId, r.PrePedidoId);
                    break;
            }
            var aprovacaoId = await CobrancaChatRepositorio.InserirAprovacaoAsync(cx, tx, loja, id, acao, valor, pedidoId, prePedidoId, motivo, ator.UsuarioId, chave, ct);
            await CobrancaChatRepositorio.MarcarAsync(cx, tx, c, "usuario", ator.UsuarioId, new { aprovacaoSolicitada = acao, aprovacaoId, valor, pedidoId, prePedidoId }, ct);
            await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "cobranca_chat", id.ToString(), "aprovacao_solicitada", "usuario", ator.UsuarioId,
                new { acao, aprovacaoId, valor, pedidoId, prePedidoId, motivo }, ct);
            await tx.CommitAsync(ct);
            return Aprovacao((await CobrancaChatRepositorio.AprovacaoAsync(cx, null, aprovacaoId, loja, false, ct))!);
        }

        public async Task<CobrancaAprovacaoListaDto> AprovacoesAsync(Guid loja, string? status, CancellationToken ct)
        {
            await using var cx = await _db.OpenConnectionAsync(ct);
            var lista = await CobrancaChatRepositorio.AprovacoesAsync(cx, loja, string.IsNullOrWhiteSpace(status) ? null : status.Trim(), ct);
            return new CobrancaAprovacaoListaDto { Itens = lista.Select(Aprovacao).ToList() };
        }

        public async Task<DecisaoAprovacaoDto> AprovarAsync(Guid loja, Guid aprovacaoId, DecidirAprovacaoRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            var motivo = CobrancaChatRegras.Motivo(r?.Motivo);
            await using var cx = await _db.OpenConnectionAsync(ct);
            CobrancaAprovacaoLinha a;
            CobrancaChatLinha c;
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                a = await CobrancaChatRepositorio.AprovacaoAsync(cx, tx, aprovacaoId, loja, true, ct) ?? throw PagamentoException.NaoEncontrado("Aprovacao nao encontrada.");
                if (a.Status == StatusAprovacao.Aprovada)
                {
                    await tx.RollbackAsync(ct);
                    return await DecisaoAsync(cx, loja, aprovacaoId, ator, ct);
                }
                if (a.Status != StatusAprovacao.Pendente) throw PagamentoException.Estado("Esta aprovacao ja foi decidida.", a.Status);
                if (a.SolicitadoPor == ator.UsuarioId) throw new PagamentoException(CobrancaChatCodigos.AprovacaoPropria, 403, "Outra pessoa precisa aprovar o seu pedido.");
                c = (await CobrancaChatRepositorio.ObterAsync(cx, tx, a.IdCobranca, loja, true, ct))!;

                switch (a.Acao)
                {
                    case AcoesAprovacao.Emitir:
                        if (c.Status != StatusCobranca.AguardandoAprovacao) throw PagamentoException.Estado("A cobranca nao espera mais aprovacao.", c.Status);
                        await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, c, StatusCobranca.Criando, "usuario", ator.UsuarioId, new { aprovacaoId, aprovada = true }, ct);
                        break;
                    case AcoesAprovacao.Reembolsar:
                        if (c.Status != StatusCobranca.Pago) throw PagamentoException.Estado("So cobranca paga pode ser reembolsada.", c.Status);
                        break;
                    default:
                        await TrocarVinculoAsync(cx, tx, c, a, ator, ct);
                        break;
                }
                await CobrancaChatRepositorio.DecidirAprovacaoAsync(cx, tx, aprovacaoId, StatusAprovacao.Aprovada, ator.UsuarioId, motivo, null, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "cobranca_chat", c.Id.ToString(), "aprovacao_aprovada", "usuario", ator.UsuarioId,
                    new { aprovacaoId, a.Acao, a.Valor, a.PedidoId, a.PrePedidoId, solicitante = a.SolicitadoPor }, ct);
                await tx.CommitAsync(ct);
            }

            // Efeitos com chamada externa so depois do commit da decisao.
            try
            {
                if (a.Acao == AcoesAprovacao.Emitir) await GerarPixAsync(c, ator, ct);
                else if (a.Acao == AcoesAprovacao.Reembolsar)
                {
                    var atual = (await CobrancaChatRepositorio.ObterAsync(cx, null, c.Id, loja, false, ct))!;
                    await ReembolsarInternoAsync(atual, a.Valor, a.Motivo, ator.UsuarioId, $"aprovacao:{aprovacaoId:N}", ct);
                }
            }
            catch (PagamentoException ex) when (a.Acao == AcoesAprovacao.Reembolsar)
            {
                await using var tx = await cx.BeginTransactionAsync(ct);
                await CobrancaChatRepositorio.DecidirAprovacaoAsync(cx, tx, aprovacaoId, StatusAprovacao.Falhou, null, null, new { erro = ex.Codigo }, ct);
                await tx.CommitAsync(ct);
                throw;
            }
            return await DecisaoAsync(cx, loja, aprovacaoId, ator, ct);
        }

        public async Task<DecisaoAprovacaoDto> RejeitarAsync(Guid loja, Guid aprovacaoId, DecidirAprovacaoRequest? r, AtorCobranca ator, CancellationToken ct)
        {
            var motivo = CobrancaChatRegras.Motivo(r?.Motivo);
            await using var cx = await _db.OpenConnectionAsync(ct);
            await using (var tx = await cx.BeginTransactionAsync(ct))
            {
                var a = await CobrancaChatRepositorio.AprovacaoAsync(cx, tx, aprovacaoId, loja, true, ct) ?? throw PagamentoException.NaoEncontrado("Aprovacao nao encontrada.");
                if (a.Status == StatusAprovacao.Rejeitada)
                {
                    await tx.RollbackAsync(ct);
                    return await DecisaoAsync(cx, loja, aprovacaoId, ator, ct);
                }
                if (a.Status != StatusAprovacao.Pendente) throw PagamentoException.Estado("Esta aprovacao ja foi decidida.", a.Status);
                var c = (await CobrancaChatRepositorio.ObterAsync(cx, tx, a.IdCobranca, loja, true, ct))!;
                await CobrancaChatRepositorio.DecidirAprovacaoAsync(cx, tx, aprovacaoId, StatusAprovacao.Rejeitada, ator.UsuarioId, motivo, null, ct);
                if (a.Acao == AcoesAprovacao.Emitir && c.Status == StatusCobranca.AguardandoAprovacao)
                {
                    await CobrancaChatRepositorio.MudarStatusAsync(cx, tx, c, StatusCobranca.Cancelado, "usuario", ator.UsuarioId, new { aprovacaoId, rejeitada = true, motivo }, ct);
                    await FinalizarCancelamentoAsync(cx, tx, c, motivo ?? "aprovacao_rejeitada", ator, ct);
                }
                else await CobrancaChatRepositorio.MarcarAsync(cx, tx, c, "usuario", ator.UsuarioId, new { aprovacaoId, a.Acao, rejeitada = true, motivo }, ct);
                await PagamentoAuditoria.RegistrarAsync(cx, tx, loja, "cobranca_chat", c.Id.ToString(), "aprovacao_rejeitada", "usuario", ator.UsuarioId,
                    new { aprovacaoId, a.Acao, motivo, solicitante = a.SolicitadoPor }, ct);
                await tx.CommitAsync(ct);
            }
            return await DecisaoAsync(cx, loja, aprovacaoId, ator, ct);
        }

        /// <summary>Move o dinheiro de uma cobranca paga para outro pedido do mesmo cliente (so com aprovacao). Recalcula os dois pedidos.</summary>
        private async Task TrocarVinculoAsync(NpgsqlConnection cx, NpgsqlTransaction tx, CobrancaChatLinha c, CobrancaAprovacaoLinha a, AtorCobranca ator, CancellationToken ct)
        {
            if (c.Status != StatusCobranca.Pago || c.IdIntent == null) throw PagamentoException.Estado("So cobranca paga troca de pedido.", c.Status);
            var conversa = await CobrancaChatRepositorio.ConversaAsync(cx, tx, c.ConversaId, c.IdEstabelecimento, ct) ?? throw PagamentoException.NaoEncontrado("Conversa nao encontrada.");
            var conversas = await CobrancaChatRepositorio.ConversasDoClienteAsync(cx, tx, conversa, ct);
            await AlvoValidoAsync(cx, tx, c.IdEstabelecimento, conversa, conversas, a.PedidoId, a.PrePedidoId, ct);
            var intent = (await PagamentoIntentSql.ObterAsync(cx, tx, c.IdIntent.Value, true, ct))!;
            var (pedidoAntes, preAntes) = (c.PedidoId, c.PrePedidoId);
            await cx.ExecuteAsync(new CommandDefinition(@"
UPDATE cobranca_chat SET tipo = 'vinculada', pedido_id = @P, pre_pedido_id = @Pre, updated_at = NOW() WHERE id = @Id;
UPDATE pagamento_intent SET pedido_id = @P, pre_pedido_id = @Pre, updated_at = NOW() WHERE id = @Intent;",
                new { c.Id, P = a.PedidoId, Pre = a.PrePedidoId, Intent = intent.Id }, tx, cancellationToken: ct));
            await PagamentoAuditoria.RegistrarAsync(cx, tx, c.IdEstabelecimento, "pagamento_intent", intent.Id.ToString(), "vinculo_trocado", "usuario", ator.UsuarioId,
                new { de = new { pedidoId = pedidoAntes, prePedidoId = preAntes }, para = new { a.PedidoId, a.PrePedidoId }, cobrancaId = c.Id }, ct);
            c.Tipo = TiposCobranca.Vinculada;
            if (pedidoAntes.HasValue) await CobrancaChatEfeitos.RecalcularPedidoAsync(cx, tx, c, pedidoAntes.Value, pagouAgora: false, ct);
            (c.PedidoId, c.PrePedidoId) = (a.PedidoId, a.PrePedidoId);
            if (a.PedidoId.HasValue) await CobrancaChatEfeitos.RecalcularPedidoAsync(cx, tx, c, a.PedidoId.Value, pagouAgora: true, ct);
            await CobrancaChatRepositorio.MarcarAsync(cx, tx, c, "usuario", ator.UsuarioId,
                new { trocaVinculo = new { de = new { pedidoId = pedidoAntes, prePedidoId = preAntes }, para = new { a.PedidoId, a.PrePedidoId } }, aprovacaoId = a.Id }, ct);
        }

        private async Task<DecisaoAprovacaoDto> DecisaoAsync(NpgsqlConnection cx, Guid loja, Guid aprovacaoId, AtorCobranca ator, CancellationToken ct)
        {
            var a = (await CobrancaChatRepositorio.AprovacaoAsync(cx, null, aprovacaoId, loja, false, ct))!;
            var c = (await CobrancaChatRepositorio.ObterAsync(cx, null, a.IdCobranca, loja, false, ct))!;
            return new DecisaoAprovacaoDto { Aprovacao = Aprovacao(a), Cobranca = await DtoAsync(cx, c, ator, null, comPix: false, ct) };
        }

        // ---- apoio -----------------------------------------------------------------------------------------------------------

        /// <summary>Pedido/pre-pedido da mesma loja, do cliente da conversa e ainda pagavel. Outra loja = 404 (nunca revela que existe).</summary>
        private static async Task<AlvoCobranca> AlvoValidoAsync(NpgsqlConnection cx, NpgsqlTransaction? tx, Guid loja, ConversaCobranca conversa, Guid[] conversasDoCliente,
            int? pedidoId, Guid? prePedidoId, CancellationToken ct)
        {
            var alvo = pedidoId.HasValue
                ? await CobrancaChatRepositorio.PedidoAsync(cx, tx, pedidoId.Value, false, ct)
                : await CobrancaChatRepositorio.PrePedidoAsync(cx, tx, prePedidoId!.Value, ct);
            if (alvo == null || alvo.IdEstabelecimento != loja) throw PagamentoException.NaoEncontrado("Pedido nao encontrado.");
            if (pedidoId.HasValue && prePedidoId.HasValue)
            {
                var pre = await CobrancaChatRepositorio.PrePedidoAsync(cx, tx, prePedidoId.Value, ct);
                if (pre == null || pre.IdEstabelecimento != loja || pre.PedidoId != pedidoId) throw PagamentoException.NaoEncontrado("Pedido nao encontrado.");
            }

            var doCliente = (alvo.ConversaId.HasValue && conversasDoCliente.Contains(alvo.ConversaId.Value)) ||
                            (alvo.ClienteId.HasValue && conversa.IdCliente.HasValue && alvo.ClienteId == conversa.IdCliente);
            if (!doCliente)
                throw PagamentoException.Regra(CobrancaChatCodigos.PedidoDeOutroCliente, "Este pedido nao e do cliente desta conversa.");

            var naoPagavel = pedidoId.HasValue
                ? alvo.Status == "4"
                : alvo.Status is "recusado" or "expirado" or "cancelado";
            if (naoPagavel) throw PagamentoException.Regra(PagamentoCodigos.PedidoNaoPagavel, "Este pedido nao pode mais ser cobrado.");
            return alvo;
        }

        private static async Task<int> ValidadeAsync(NpgsqlConnection cx, Guid loja, int? informada, CancellationToken ct)
        {
            var validade = informada ?? (await PreferenciasPagamento.LerAsync(cx, loja, ct)).PixValidadeMinutos;
            if (validade is < 5 or > 1440) throw PagamentoException.Invalida("Validade do Pix entre 5 e 1440 minutos.", "validadeMinutos");
            return validade;
        }

        private async Task ExigirPixDisponivelAsync(NpgsqlConnection cx, Guid loja, CancellationToken ct)
        {
            if (!(await PreferenciasPagamento.LerAsync(cx, loja, ct)).PixAtivo)
                throw PagamentoException.Regra(PagamentoCodigos.PixDesativado, "A loja nao aceita Pix online.");
            if (!await CobrancaChatRepositorio.PixDisponivelAsync(cx, loja, _credenciais.AmbienteAtivo, ct))
                throw PagamentoException.Regra(PagamentoCodigos.ContaLojaNaoConectada, "Conecte a conta Mercado Pago da loja para cobrar por Pix.");
        }

        private static async Task<PagamentoException> CobrancaAbertaAsync(NpgsqlConnection cx, Guid loja, int? pedidoId, Guid? prePedidoId, CancellationToken ct)
        {
            var aberta = await CobrancaChatRepositorio.AbertaDoAlvoAsync(cx, null, loja, pedidoId, prePedidoId, ct);
            return new PagamentoException(PagamentoCodigos.CobrancaAberta, 409, "Ja existe uma cobranca aberta para este pedido.",
                new Dictionary<string, object?> { ["cobrancaId"] = aberta });
        }

        private static PagamentoException NaoEncontrada() => PagamentoException.NaoEncontrado("Cobranca nao encontrada.");

        private async Task<CobrancaChatDto> DtoAsync(NpgsqlConnection cx, CobrancaChatLinha c, AtorCobranca ator, string? token, bool comPix, CancellationToken ct)
        {
            var d = new CobrancaChatDto
            {
                Id = c.Id, ConversaId = c.ConversaId, IntentId = c.IdIntent, Status = c.Status, Versao = c.Versao, Descricao = c.Descricao, Valor = c.Valor,
                Moeda = CobrancaChatRepositorio.Moeda(c.Moeda), Tipo = c.Tipo, PedidoId = c.PedidoId, PrePedidoId = c.PrePedidoId, ValidadeMinutos = c.ValidadeMinutos,
                ExpiraEm = Utc(c.ExpiraEm), EnviadoEm = Utc(c.EnviadoEm), IdMensagem = c.IdMensagem, PagoEm = Utc(c.PagoEm), CanceladoEm = Utc(c.CanceladoEm),
                CanceladoPor = c.CanceladoPor.HasValue ? new UsuarioResumoDto { Id = c.CanceladoPor.Value, Nome = c.CanceladoPorNome } : null,
                Motivo = c.Motivo, ReemitidaDe = c.ReemitidaDe, ReemitidaPor = c.ReemitidaPor,
                Autor = new UsuarioResumoDto { Id = c.AutorUsuarioId, Nome = c.AutorNome }, ValorEstornado = c.ValorEstornado,
                CriadoEm = Utc(c.CreatedAt)!.Value, AtualizadoEm = Utc(c.UpdatedAt)!.Value
            };
            if (c.Tipo == TiposCobranca.Vinculada)
            {
                var alvo = c.PedidoId.HasValue
                    ? await CobrancaChatRepositorio.PedidoAsync(cx, null, c.PedidoId.Value, false, ct)
                    : await CobrancaChatRepositorio.PrePedidoAsync(cx, null, c.PrePedidoId!.Value, ct);
                if (alvo != null && alvo.IdEstabelecimento == c.IdEstabelecimento)
                    d.SaldoPedido = CobrancaChatRegras.StatusPago(alvo.StatusPagamento) ? 0m : CobrancaChatRegras.Saldo(alvo.Total, alvo.Recebido);
            }
            if (c.Status is StatusCobranca.AguardandoAprovacao or StatusCobranca.Pago)
            {
                var pendente = await CobrancaChatRepositorio.AprovacaoPendenteAsync(cx, null, c.Id, ct);
                if (pendente != null) d.AprovacaoPendente = Aprovacao(pendente);
            }
            if (c.Status is StatusCobranca.Aguardando or StatusCobranca.Enviado && c.IdIntent.HasValue)
            {
                d.Pix = new CobrancaPixDto { LinkPublico = token == null ? null : Link(token) };
                if (comPix)
                {
                    var pix = await _pix.ObterAsync(c.IdIntent.Value, c.IdEstabelecimento, ct);
                    d.Pix.CopiaECola = pix.CopiaECola;
                    d.Pix.QrCodeBase64 = pix.QrCodeBase64;
                }
            }
            CobrancaChatRegras.PreencherAcoes(d, ator.PodeAprovar, ator.PodeReembolsar, c.ReemitidaPor.HasValue);
            return d;
        }

        private static CobrancaAprovacaoDto Aprovacao(CobrancaAprovacaoLinha a) => new()
        {
            Id = a.Id, CobrancaId = a.IdCobranca, ConversaId = a.ConversaId, Acao = a.Acao, Status = a.Status, Valor = a.Valor, ValorCobranca = a.ValorCobranca,
            DescricaoCobranca = a.DescricaoCobranca, PedidoId = a.PedidoId, PrePedidoId = a.PrePedidoId, Motivo = a.Motivo,
            SolicitadoPor = new UsuarioResumoDto { Id = a.SolicitadoPor, Nome = a.SolicitadoPorNome },
            DecididoPor = a.DecididoPor.HasValue ? new UsuarioResumoDto { Id = a.DecididoPor.Value, Nome = a.DecididoPorNome } : null,
            DecididoEm = Utc(a.DecididoEm), MotivoDecisao = a.MotivoDecisao, CriadoEm = Utc(a.CreatedAt)!.Value
        };

        private static DateTime? Utc(DateTime? d) => d.HasValue ? DateTime.SpecifyKind(d.Value, DateTimeKind.Utc) : null;
    }
}
