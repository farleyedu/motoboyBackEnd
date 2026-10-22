using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <param name="Token">Token da Meta do numero. Vazio = nao mexe no token atual.</param>
    /// <param name="EstabelecimentoId">Loja que recebe as mensagens deste numero agora.</param>
    public sealed record CanalCentralRequest(string? PhoneNumberId, string? Numero, string? Token, Guid EstabelecimentoId);

    public interface ICanalCentralService
    {
        /// <summary>Numero central de testes, ou nulo se ainda nao foi cadastrado. So a Gestao.</summary>
        Task<CanalView?> ObterAsync(AtendimentoAtor ator);
        /// <summary>Cadastra ou altera o numero central e o vincula a uma loja. So a Gestao.</summary>
        Task<CanalView> SalvarAsync(AtendimentoAtor ator, CanalCentralRequest request);
    }

    /// <summary>
    /// Um numero de WhatsApp de testes compartilhado por todas as lojas de teste. O numero e o ID da Meta sao unicos no banco, entao
    /// ele existe uma vez so (um canal marcado como central) e quem recebe as mensagens e a loja a que ele esta vinculado agora.
    /// Trocar o vinculo e o mesmo "mover numero" das lojas comuns.
    /// </summary>
    public sealed class CanalCentralService : ICanalCentralService
    {
        private const string ModuloWhatsapp = "WHATSAPP";

        private readonly ICanalRepository _canais;
        private readonly ICatalogoRepository _catalogo;
        private readonly ITokenProtector _token;
        private readonly ILogger<CanalCentralService> _logger;

        public CanalCentralService(ICanalRepository canais, ICatalogoRepository catalogo, ITokenProtector token, ILogger<CanalCentralService> logger)
        {
            _canais = canais;
            _catalogo = catalogo;
            _token = token;
            _logger = logger;
        }

        public async Task<CanalView?> ObterAsync(AtendimentoAtor ator)
        {
            ExigirGestao(ator);
            var canal = await _canais.ObterCentralAsync();
            return canal == null ? null : Ver(canal);
        }

        public async Task<CanalView> SalvarAsync(AtendimentoAtor ator, CanalCentralRequest request)
        {
            ExigirGestao(ator);
            if (!await _catalogo.LojaExisteAsync(request.EstabelecimentoId))
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["estabelecimentoId"] = new List<string> { "Escolha o estabelecimento que recebe as mensagens do numero." }
                });
            }

            // Trocar quem atende nao exige redigitar o numero: sem numero nem ID, vale o central que ja existe.
            var informouIdentidade = !string.IsNullOrWhiteSpace(request.PhoneNumberId) || !string.IsNullOrWhiteSpace(request.Numero);
            var erros = informouIdentidade
                ? AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, null)
                : new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            var novoToken = CifrarToken(request.Token, erros);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var phoneNumberId = request.PhoneNumberId?.Trim() ?? string.Empty;
            var numero = informouIdentidade ? AtendimentoRegras.NormalizarNumero(request.Numero)! : string.Empty;

            try
            {
                CanalWhatsapp? canal;
                if (informouIdentidade)
                {
                    // O numero ja pode estar cadastrado (em qualquer loja): adota, em vez de tentar criar outro e bater na unicidade.
                    var porId = await _canais.ObterPorPhoneNumberIdAsync(phoneNumberId);
                    var porNumero = await _canais.ObterPorNumeroAsync(numero.TrimStart('+'));
                    if (porId != null && porNumero != null && porId.Id != porNumero.Id)
                    {
                        throw new CanalConflitoException("numero",
                            "Este numero e este Phone Number ID ja estao cadastrados em canais diferentes. Confira os dois valores.");
                    }

                    canal = porId ?? porNumero ?? await _canais.ObterCentralAsync();

                    if (canal != null && canal.Origem == OrigemCanal.Proprio)
                    {
                        throw new CanalConflitoException("phoneNumberId", "Este numero e o numero proprio de um cliente e nao pode ser o central de testes.");
                    }
                }
                else
                {
                    canal = await _canais.ObterCentralAsync()
                        ?? throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["phoneNumberId"] = new List<string> { "Ainda nao ha numero compartilhado: informe o numero e o Phone Number ID uma vez." }
                        });
                }

                var criado = false;

                if (canal == null)
                {
                    canal = new CanalWhatsapp
                    {
                        Id = Guid.NewGuid(), IdEstabelecimento = request.EstabelecimentoId, PhoneNumberId = phoneNumberId, NumeroE164 = numero,
                        Nome = "Numero central de testes", TokenCifrado = novoToken, Central = true, Origem = OrigemCanal.Central, Status = StatusCanal.Configurando,
                        ModoAtendimento = ModoAtendimento.Hibrido
                    };
                    await _canais.CriarAsync(canal, Array.Empty<string>());
                    criado = true;
                }
                else
                {
                    var identidadeMudou = informouIdentidade && (phoneNumberId != canal.PhoneNumberId || numero != canal.NumeroE164);
                    if (informouIdentidade)
                    {
                        canal.PhoneNumberId = phoneNumberId;
                        canal.NumeroE164 = numero;
                    }

                    if (novoToken != null) canal.TokenCifrado = novoToken;
                    if (identidadeMudou || novoToken != null)
                    {
                        canal.Status = StatusCanal.Configurando;
                        canal.UltimoErro = null;
                        await _canais.AtualizarDadosAsync(canal);
                    }
                }

                var origem = canal.IdEstabelecimento;
                if (!criado && origem != request.EstabelecimentoId)
                {
                    await _canais.MoverAsync(canal.Id, request.EstabelecimentoId);
                }

                if (criado || origem != request.EstabelecimentoId)
                {
                    // Mover zera os servicos do numero: a loja nova passa a atender com tudo o que ela tem ligado.
                    var ativos = (await _catalogo.ListarServicosDaLojaAsync(request.EstabelecimentoId)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                    await _canais.DefinirServicosAsync(canal.Id, ativos);
                    await _catalogo.GarantirModulosAsync(request.EstabelecimentoId, new[] { ModuloWhatsapp });
                }

                await _canais.DefinirCentralAsync(canal.Id);
                await _canais.RegistrarAuditoriaAsync(canal.Id, criado ? "central_criado" : "central_atualizado", criado ? null : origem,
                    request.EstabelecimentoId, ator.UsuarioId, JsonSerializer.Serialize(new { numero, tokenNovo = novoToken != null }));
                _logger.LogInformation(
                    "[canal] ev=central_salvo canal={Canal} de_loja={Origem} para_loja={Destino} usuario={Usuario} criado={Criado}",
                    canal.Id, origem, request.EstabelecimentoId, ator.UsuarioId, criado);

                return Ver((await _canais.ObterAsync(canal.Id))!);
            }
            catch (CanalConflitoException ex)
            {
                _logger.LogWarning("[canal] ev=conflito acao=salvar_central campo={Campo}", ex.Campo);
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [ex.Campo] = new List<string> { ex.Message }
                });
            }
        }

        private void ExigirGestao(AtendimentoAtor ator)
        {
            if (ator.Gestao) return;

            _logger.LogWarning("[canal] ev=negado acao=numero_central motivo=so_gestao usuario={Usuario}", ator.UsuarioId);
            throw new UnauthorizedAccessException("Somente a Gestao configura o numero central de testes.");
        }

        private string? CifrarToken(string? token, Dictionary<string, List<string>> erros)
        {
            if (string.IsNullOrWhiteSpace(token)) return null;

            if (!_token.Configurado)
            {
                if (!erros.TryGetValue("token", out var lista)) erros["token"] = lista = new List<string>();
                lista.Add("Para guardar o token falta configurar a chave de cifragem (Atendimento__ChaveToken) no ambiente.");
                return null;
            }

            return _token.Proteger(token.Trim());
        }

        private static CanalView Ver(CanalWhatsapp c) => new(
            c.Id, c.IdEstabelecimento, c.PhoneNumberId, c.NumeroE164, c.Nome, c.Status, c.ModoAtendimento, c.Servicos, c.TemToken,
            c.VerificadoEm, c.UltimoRecebimentoEm, c.UltimoEnvioOkEm, c.UltimoErro, c.UltimoErroEm, c.Central,
            c.Origem, c.WabaId, c.AppId, new CanalWebhookView(WebhookEstado.NaoAplicavel, null, null, null, null));
    }
}
