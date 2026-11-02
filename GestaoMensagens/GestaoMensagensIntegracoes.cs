using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using APIBack.Acompanhamento.Fases;
using APIBack.Acompanhamento.Relacionamento;
using APIBack.Automation.Services;
using APIBack.Service;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace APIBack.GestaoMensagens
{
    /// <summary>Envio pelo WhatsAppSender existente (mesmo token, mesmo canal, mesmas regras da Meta). Nenhum cliente HTTP novo.</summary>
    public sealed class GestaoEnviadorMeta : IGestaoEnviadorMeta
    {
        private readonly WhatsAppSender _sender;

        public GestaoEnviadorMeta(WhatsAppSender sender) => _sender = sender;

        // O id de conversa do sender serve ao filtro de cliente de teste e ao log; aqui nao ha conversa operacional, entao e um id novo.
        public Task<string?> EnviarTextoAsync(CanalGestao canal, string destinoE164, string texto, CancellationToken ct) =>
            _sender.SendTextAsync(Guid.NewGuid(), canal.PhoneNumberId, destinoE164, texto, canal.NumeroE164);

        public Task<string?> EnviarModeloAsync(CanalGestao canal, string destinoE164, ModeloAprovado modelo, IReadOnlyList<string> corpo, string? sufixoBotaoUrl, CancellationToken ct) =>
            _sender.SendTemplateAsync(Guid.NewGuid(), canal.PhoneNumberId, destinoE164, modelo.Nome, modelo.Idioma, corpo, sufixoBotaoUrl);
    }

    /// <summary>Painel da loja = chamado da Zippy: responde o chamado informado/aberto da loja ou abre um novo. Nao cria outro canal de painel.</summary>
    public sealed class GestaoPainelChamados : IGestaoPainel
    {
        private readonly IChamadosService _chamados;

        public GestaoPainelChamados(IChamadosService chamados) => _chamados = chamados;

        public async Task<long> EnviarAsync(Guid lojaId, int autorUsuarioId, string texto, long? chamadoId, CancellationToken ct)
        {
            try
            {
                if (chamadoId.HasValue)
                {
                    var (chamado, _) = await _chamados.ObterAsync(chamadoId.Value, lojaId, ct);
                    if (chamado.Status == "resolvido")
                        throw new GestaoMensagensException(409, CodigosGestaoMensagem.EstadoInvalido, "Esse chamado ja foi resolvido.", "Envie sem chamadoId para abrir um novo assunto.");
                    await _chamados.ResponderAsync(chamado.Id, lojaId, autorUsuarioId, true, texto, null, ct);
                    return chamado.Id;
                }

                var aberto = (await _chamados.ListarAsync(lojaId, ct)).Where(c => c.Status != "resolvido").OrderByDescending(c => c.CriadoEm).FirstOrDefault();
                if (aberto != null)
                {
                    await _chamados.ResponderAsync(aberto.Id, lojaId, autorUsuarioId, true, texto, null, ct);
                    return aberto.Id;
                }

                var novo = await _chamados.AbrirAsync(lojaId, autorUsuarioId, new AbrirChamadoRequest("duvida", texto.Length >= 5 ? texto : texto.PadRight(5, '.')), null, ct);
                return novo.Id;
            }
            catch (ChamadoException ex)
            {
                throw new GestaoMensagensException(ex.Status == 404 ? 404 : 422, "CHAMADO_INVALIDO", ex.Message, "Confira o chamado ou envie sem chamadoId.");
            }
        }
    }

    /// <summary>Marco do relacionamento (acompanhamento_contato_marco), montado como no AcompanhamentoRelacionamentoController.</summary>
    public sealed class GestaoMarcosRelacionamento : IGestaoMarcos
    {
        private readonly IRelacionamentoService _relacionamento;

        public GestaoMarcosRelacionamento(NpgsqlDataSource db, IAcompanhamentoStore acompanhamento) =>
            _relacionamento = new RelacionamentoService(new RelacionamentoPostgresStore(db, acompanhamento));

        public Task RegistrarAsync(Guid referencia, string direcao, DateTimeOffset em, string referenciaMensagem, CancellationToken ct) =>
            _relacionamento.RegistrarMarcoContatoAsync(referencia, direcao, em, referenciaMensagem, ct);
    }

    /// <summary>Ponte do webhook: respostas recebidas e recibos de status. Chamada pelo WaEventoProcessor; nunca derruba o processamento.</summary>
    public interface IGestaoMensagensRetorno
    {
        /// <summary>Resposta real recebida no numero. Registra no historico do mesmo numero e, so entao, o marco "recebida". false = numero fora da Gestao.</summary>
        Task<bool> RegistrarRecebidaAsync(Guid canalId, string remetente, string wamid, DateTimeOffset em, string? texto, CancellationToken ct = default);
        /// <summary>Recibo (sent/delivered/read/failed) de mensagem enviada pela Gestao. false = a mensagem nao e da Gestao.</summary>
        Task<bool> AplicarReciboAsync(string wamid, string status, string? codigo, string? detalhe, CancellationToken ct = default);
    }

    public sealed class GestaoMensagensRetorno : IGestaoMensagensRetorno
    {
        private readonly IGestaoMensagensStore _store;
        private readonly IGestaoMarcos _marcos;
        private readonly ILogger<GestaoMensagensRetorno> _logger;

        public GestaoMensagensRetorno(IGestaoMensagensStore store, IGestaoMarcos marcos, ILogger<GestaoMensagensRetorno> logger)
        {
            _store = store;
            _marcos = marcos;
            _logger = logger;
        }

        public async Task<bool> RegistrarRecebidaAsync(Guid canalId, string remetente, string wamid, DateTimeOffset em, string? texto, CancellationToken ct = default)
        {
            var chave = GestaoMensagensRegras.ChaveTelefone(remetente);
            if (chave == null || string.IsNullOrWhiteSpace(wamid) || !await _store.CanalDaGestaoAsync(canalId, ct)) return false;

            // O retorno entra no historico de quem recebeu a ultima mensagem DESTE numero para ESTE telefone (nunca de outro numero).
            var ultima = await _store.UltimaSaidaAsync(canalId, chave, ct);
            var entrada = new MensagemRegistro
            {
                Id = Guid.NewGuid(),
                Direcao = DirecaoMensagem.Entrada,
                Via = ViaMensagem.Numero,
                CanalId = canalId,
                AlvoTipo = ultima?.AlvoTipo,
                EstabelecimentoId = ultima?.EstabelecimentoId,
                OportunidadeId = ultima?.OportunidadeId,
                ContatoRef = ultima?.ContatoRef,
                TelefoneE164 = "+" + remetente.TrimStart('+'),
                TelefoneChave = chave,
                Texto = texto,
                Estado = EstadoMensagem.Recebida,
                ReferenciaExterna = wamid,
                CriadoEm = em
            };
            if (!await _store.InserirAsync(entrada, ct)) return true; // reentrega do mesmo webhook: ja registrada, nada a repetir

            if (ultima != null)
            {
                var referencia = ultima.EstabelecimentoId ?? ultima.OportunidadeId!.Value;
                try { await _marcos.RegistrarAsync(referencia, "recebida", em, wamid, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[gestao.msg] ev=marco_recebida_falhou wa={Wa} canal={Canal}", wamid, canalId);
                }
            }
            return true;
        }

        public async Task<bool> AplicarReciboAsync(string wamid, string status, string? codigo, string? detalhe, CancellationToken ct = default)
        {
            var m = await _store.PorReferenciaSaidaAsync(wamid, ct);
            if (m == null) return false;
            var novo = GestaoMensagensRegras.AplicarRecibo(m.Estado, status);
            if (novo == null) return true;
            var anterior = m.Estado;
            m.Estado = novo;
            if (novo == EstadoMensagem.Falhou)
            {
                m.ErroCodigo = string.IsNullOrWhiteSpace(codigo) ? "META_FALHOU" : codigo;
                m.ErroDetalhe = detalhe is { Length: > 300 } ? detalhe[..300] : detalhe;
            }
            if (!await _store.AtualizarAsync(m, anterior, ct)) _logger.LogInformation("[gestao.msg] ev=recibo_concorrente wa={Wa} novo={Novo}", wamid, novo);
            return true;
        }
    }
}
