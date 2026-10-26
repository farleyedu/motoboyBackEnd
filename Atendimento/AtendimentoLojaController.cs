using APIBack.Atendimento.ConexaoMeta;
using APIBack.Atendimento.Motor;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using APIBack.Controllers;
using APIBack.DTOs.Common;
using APIBack.Extensions;
using APIBack.Service;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    public sealed class ServicosRequest
    {
        public List<string>? Servicos { get; set; }
    }

    public sealed class SimulacaoRequest
    {
        public string Texto { get; set; } = string.Empty;
        public string? Modo { get; set; }
        public List<string>? Servicos { get; set; }
        public bool PrimeiraMensagem { get; set; } = true;
        /// <summary>Estado devolvido na resposta anterior, para o teste seguir a conversa.</summary>
        public EstadoFluxo? Estado { get; set; }
        public DateTime? QuandoUtc { get; set; }
        public double? Latitude { get; set; }
        public double? Longitude { get; set; }
        public decimal Subtotal { get; set; }
        public string? TipoMensagem { get; set; }
        public string? Saudacao { get; set; }
        public string? MensagemForaHorario { get; set; }
        public Dictionary<string, string>? Mensagens { get; set; }
    }

    /// <summary>Acao do motor em forma serializavel (o tipo abstrato AcaoMotor sairia vazio no JSON).</summary>
    public sealed class AcaoSimulada
    {
        /// <summary>responder, botoes ou chamar_atendente.</summary>
        public string Tipo { get; set; } = string.Empty;
        public string? Mensagem { get; set; }
        public List<string>? Botoes { get; set; }
        public IReadOnlyList<Botao>? Opcoes { get; set; }
        public string? Motivo { get; set; }

        public static AcaoSimulada De(AcaoMotor acao) => acao switch
        {
            AcaoResponder r => new AcaoSimulada { Tipo = "responder", Mensagem = r.Mensagem },
            AcaoBotoes b => new AcaoSimulada { Tipo = "botoes", Mensagem = b.Mensagem, Botoes = b.Opcoes.Select(o => o.Titulo).ToList(), Opcoes = b.Opcoes },
            AcaoChamarAtendente c => new AcaoSimulada { Tipo = "chamar_atendente", Motivo = c.Motivo },
            _ => new AcaoSimulada { Tipo = acao.GetType().Name }
        };
    }

    public sealed class SimulacaoResposta
    {
        public List<AcaoSimulada> Acoes { get; set; } = new();
        public EstadoFluxo Estado { get; set; } = new();
        public string Fluxo { get; set; } = string.Empty;
        public string Passo { get; set; } = string.Empty;
        public string Regra { get; set; } = string.Empty;
    }

    public sealed class ModoRequest
    {
        public string? Modo { get; set; }
    }

    public sealed class MoverCanalRequest
    {
        public Guid Destino { get; set; }
    }

    /// <summary>
    /// Servicos e numeros de WhatsApp de uma loja. Quem pode o que e decidido nos servicos (nunca aqui): a Gestao liga
    /// servicos e cadastra numeros; o dono da loja le tudo da propria loja e escolhe o modo de atendimento do numero.
    /// </summary>
    [ApiController]
    [Route("api/v2/atendimento/estabelecimentos/{estabelecimentoId:guid}")]
    public sealed class AtendimentoLojaController : ApiControllerBase
    {
        private readonly IServicosDaLojaService _servicos;
        private readonly ICanaisWhatsappService _canais;
        private readonly IExecutorDeAtendimento _executor;
        private readonly IConexaoMetaService _conexaoMeta;
        private readonly ILogger<AtendimentoLojaController> _logger;

        public AtendimentoLojaController(
            IServicosDaLojaService servicos, ICanaisWhatsappService canais, IExecutorDeAtendimento executor, IConexaoMetaService conexaoMeta,
            ILogger<AtendimentoLojaController> logger)
        {
            _servicos = servicos;
            _canais = canais;
            _executor = executor;
            _conexaoMeta = conexaoMeta;
            _logger = logger;
        }

        // ---- Conectar com a Meta (Embedded Signup v4, app central ZippyGo) --------------------------------------------

        /// <summary>Se o fluxo esta disponivel, em que modo (producao/teste) e o App ID/Configuration ID publicos. Nunca segredos.</summary>
        [HttpGet("conexao-meta/configuracao")]
        public Task<IActionResult> ConfiguracaoConexaoMeta(Guid estabelecimentoId) =>
            Executar(ator => _conexaoMeta.ConfiguracaoAsync(ator, estabelecimentoId));

        /// <summary>Cria a tentativa (state + prazo) antes de abrir o FB.login.</summary>
        [HttpPost("conexao-meta/iniciar")]
        public Task<IActionResult> IniciarConexaoMeta(Guid estabelecimentoId, [FromBody] IniciarConexaoMetaRequest? request) =>
            Executar(ator => _conexaoMeta.IniciarAsync(ator, estabelecimentoId, request ?? new IniciarConexaoMetaRequest()),
                StatusCodes.Status201Created);

        /// <summary>Fim do Embedded Signup (FINISH*, CANCEL ou ERROR). O codigo vale 30 s: o painel envia assim que recebe.</summary>
        [HttpPost("conexao-meta/concluir")]
        public Task<IActionResult> ConcluirConexaoMeta(Guid estabelecimentoId, [FromBody] ConcluirConexaoMetaRequest request) =>
            Executar(ator => _conexaoMeta.ConcluirAsync(ator, estabelecimentoId, request));

        /// <summary>Escolhe o numero (quando a conta tem mais de um) ou repete a etapa que falhou, sem autorizar de novo.</summary>
        [HttpPost("conexao-meta/ativar")]
        public Task<IActionResult> AtivarNumeroMeta(Guid estabelecimentoId, [FromBody] AtivarNumeroMetaRequest request) =>
            Executar(ator => _conexaoMeta.AtivarAsync(ator, estabelecimentoId, request));

        [HttpGet("conexao-meta/estado")]
        public Task<IActionResult> EstadoConexaoMeta(Guid estabelecimentoId, [FromQuery] Guid? tentativaId) =>
            Executar(ator => _conexaoMeta.EstadoAsync(ator, estabelecimentoId, tentativaId));

        /// <summary>Diagnostico real (token, permissoes, WABA, numero, inscricao, webhook, recebimento). reparar=true refaz o que for seguro.</summary>
        [HttpPost("canais/{canalId:guid}/revalidar")]
        public Task<IActionResult> RevalidarCanal(Guid estabelecimentoId, Guid canalId, [FromBody] RevalidarCanalRequest? request) =>
            Executar(ator => _conexaoMeta.RevalidarAsync(ator, estabelecimentoId, canalId, request ?? new RevalidarCanalRequest()));

        /// <summary>Nova autorizacao na Meta para o mesmo canal (mantem conversas e configuracoes). Responde como o iniciar.</summary>
        [HttpPost("canais/{canalId:guid}/reconectar")]
        public Task<IActionResult> ReconectarCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _conexaoMeta.ReconectarAsync(ator, estabelecimentoId, canalId), StatusCodes.Status201Created);

        [HttpGet("servicos")]
        public Task<IActionResult> ObterServicos(Guid estabelecimentoId) =>
            Executar(ator => _servicos.ObterAsync(ator, estabelecimentoId));

        [HttpPut("servicos")]
        public Task<IActionResult> DefinirServicos(Guid estabelecimentoId, [FromBody] ServicosRequest request) =>
            Executar(ator => _servicos.DefinirAsync(ator, estabelecimentoId, request.Servicos));

        [HttpGet("canais")]
        public Task<IActionResult> ListarCanais(Guid estabelecimentoId) =>
            Executar(ator => _canais.ListarAsync(ator, estabelecimentoId));

        [HttpPost("canais")]
        public Task<IActionResult> CriarCanal(Guid estabelecimentoId, [FromBody] CanalRequest request) =>
            Executar(ator => _canais.CriarAsync(ator, estabelecimentoId, request), StatusCodes.Status201Created);

        /// <summary>A propria loja conecta o numero: cola o Phone Number ID e o token da Meta (o telefone vem da Meta).</summary>
        [HttpPost("canais/conectar")]
        public Task<IActionResult> ConectarCanal(Guid estabelecimentoId, [FromBody] ConectarCanalRequest request) =>
            Executar(ator => _canais.ConectarAsync(ator, estabelecimentoId, request), StatusCodes.Status201Created);

        /// <summary>
        /// Numero PROPRIO: o cliente usa o Meta dele e cola 5 dados (Phone Number ID, WABA, token, App ID, App Secret). O sistema registra o
        /// webhook no app dele; o canal so vira "ativo" quando chegar uma mensagem de verdade.
        /// </summary>
        [HttpPost("canais/proprio")]
        public Task<IActionResult> ConectarProprio(Guid estabelecimentoId, [FromBody] ConectarProprioRequest request) =>
            Executar(ator => _canais.ConectarProprioAsync(ator, estabelecimentoId, request), StatusCodes.Status201Created);

        /// <summary>Tenta de novo registrar o webhook do numero proprio na Meta.</summary>
        [HttpPost("canais/{canalId:guid}/webhook/registrar")]
        public Task<IActionResult> RegistrarWebhook(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _canais.RegistrarWebhookAsync(ator, canalId));

        /// <summary>Estado do webhook + URL + verify token para exibir. O app secret e o token nunca saem.</summary>
        [HttpGet("canais/{canalId:guid}/webhook")]
        public Task<IActionResult> ObterWebhook(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _canais.ObterWebhookAsync(ator, canalId));

        /// <summary>
        /// Desconecta do Zippy sem apagar o canal (conversas e configuracoes ficam) e sem excluir nada na Meta. Na conexao pela Meta, tira
        /// a inscricao do app da WABA so quando nenhuma outra loja ou numero depende dela.
        /// </summary>
        [HttpDelete("canais/{canalId:guid}/conexao")]
        public Task<IActionResult> DesconectarCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _conexaoMeta.DesconectarAsync(ator, estabelecimentoId, canalId));

        [HttpPut("canais/{canalId:guid}")]
        public Task<IActionResult> AtualizarCanal(Guid estabelecimentoId, Guid canalId, [FromBody] CanalRequest request) =>
            Executar(ator => _canais.AtualizarAsync(ator, canalId, request));

        [HttpPut("canais/{canalId:guid}/atendimento")]
        public Task<IActionResult> DefinirModo(Guid estabelecimentoId, Guid canalId, [FromBody] ModoRequest request) =>
            Executar(ator => _canais.DefinirModoAsync(ator, canalId, request.Modo));

        [HttpPut("canais/{canalId:guid}/servicos")]
        public Task<IActionResult> DefinirServicosDoCanal(Guid estabelecimentoId, Guid canalId, [FromBody] ServicosRequest request) =>
            Executar(ator => _canais.DefinirServicosAsync(ator, canalId, request.Servicos));

        [HttpPost("canais/{canalId:guid}/mover")]
        public Task<IActionResult> MoverCanal(Guid estabelecimentoId, Guid canalId, [FromBody] MoverCanalRequest request) =>
            Executar(ator => _canais.MoverAsync(ator, canalId, request.Destino));

        [HttpPost("canais/{canalId:guid}/verificar")]
        public Task<IActionResult> VerificarCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(ator => _canais.VerificarAsync(ator, canalId));

        [HttpPost("simulador")]
        public Task<IActionResult> Simular(Guid estabelecimentoId, [FromBody] SimulacaoRequest request) =>
            Executar(async ator =>
            {
                ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
                var servicos = request.Servicos ?? (await _servicos.ObterAsync(ator, estabelecimentoId)).Servicos.Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                var modo = ModoAtendimento.Valido(request.Modo) ? request.Modo! : ModoAtendimento.Hibrido;
                var texto = (request.Texto ?? string.Empty).Trim();
                if (texto.Length is 0 or > 500) throw new RequestValidationException("Mensagem invalida.", new Dictionary<string, List<string>> { ["texto"] = new() { "Escreva a mensagem do cliente (ate 500 caracteres)." } });
                string? Txt(string chave) => request.Mensagens != null && request.Mensagens.TryGetValue(chave, out var valor) ? valor : null;
                var textos = request.Mensagens == null ? null : new TextosMotor(
                    Txt(MensagensDoAtendimento.Menu), Txt(MensagensDoAtendimento.Cardapio), Txt(MensagensDoAtendimento.Atendente),
                    Txt(MensagensDoAtendimento.Agendamento), Txt(MensagensDoAtendimento.SemServico), Txt(MensagensDoAtendimento.CardapioFechado));
                var cenario = new CenarioSimulacaoMotor(
                    request.QuandoUtc, request.Latitude, request.Longitude, request.Subtotal,
                    request.TipoMensagem, request.Saudacao, request.MensagemForaHorario, textos);
                var r = await _executor.SimularAsync(estabelecimentoId, modo, servicos, texto, request.Estado, request.PrimeiraMensagem, cenario);
                return new SimulacaoResposta { Acoes = r.Acoes.Select(AcaoSimulada.De).ToList(), Estado = r.Estado, Fluxo = r.Fluxo, Passo = r.Passo, Regra = r.Regra };
            });

        [HttpDelete("canais/{canalId:guid}")]
        public Task<IActionResult> RemoverCanal(Guid estabelecimentoId, Guid canalId) =>
            Executar(async ator =>
            {
                await _canais.RemoverAsync(ator, canalId);
                return new object();
            });

        private async Task<IActionResult> Executar<T>(Func<AtendimentoAtor, Task<T>> operacao, int sucesso = StatusCodes.Status200OK)
        {
            var usuarioId = HttpContext.GetUserId();
            if (!usuarioId.HasValue)
            {
                return Unauthorized(ApiResponse<object>.Fail("Usuario nao autenticado."));
            }

            var ator = new AtendimentoAtor(
                usuarioId.Value, HttpContext.IsSuperAdmin(), HttpContext.GetEstabelecimentoId(), (modulo, acao) => HttpContext.TemPermissao(modulo, acao));

            try
            {
                return StatusCode(sucesso, ApiResponse<T>.Ok(await operacao(ator)));
            }
            catch (WhatsappException ex)
            {
                // Falha do WhatsApp: codigo estavel + como resolver + qual campo corrigir (nunca token ou app secret).
                _logger.LogWarning("[atend.api] ev=erro_whatsapp rota={Rota} codigo={Codigo} campo={Campo}", Request.Path.Value, ex.Erro.Codigo, ex.Erro.Campo);
                return StatusCode(ex.Http, ApiResponse<object>.Fail(ex.Erro.Mensagem, ex.Erro.Codigo, ex.Erro.Detalhes));
            }
            catch (RequestValidationException ex)
            {
                return ValidationErrorResponse(ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                return StatusCode(StatusCodes.Status403Forbidden, ApiResponse<object>.Fail(ex.Message));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFoundErrorResponse(ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[atend.api] ev=erro rota={Rota} usuario={Usuario}", Request.Path.Value, usuarioId.Value);
                return StatusCode(StatusCodes.Status500InternalServerError, ApiResponse<object>.Fail("Nao foi possivel concluir a operacao."));
            }
        }
    }
}
