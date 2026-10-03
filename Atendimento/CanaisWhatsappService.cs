using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento
{
    /// <param name="Token">Token da Meta deste numero. Vazio = nao mexe no token atual.</param>
    /// <param name="Servicos">Servicos que o numero atende. Nulo = todos os que a loja tem ligados.</param>
    public sealed record CanalRequest(
        string? PhoneNumberId, string? Numero, string? Nome, string? Token, string? ModoAtendimento, IReadOnlyList<string>? Servicos);

    public sealed record CanalView(
        Guid Id, Guid EstabelecimentoId, string PhoneNumberId, string NumeroE164, string? Nome, string Status, string ModoAtendimento,
        IReadOnlyList<string> Servicos, bool TemToken, DateTime? VerificadoEm, DateTime? UltimoRecebimentoEm,
        DateTime? UltimoEnvioOkEm, string? UltimoErro, DateTime? UltimoErroEm);

    public interface ICanaisWhatsappService
    {
        Task<IReadOnlyList<CanalView>> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId);
        Task<CanalView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, CanalRequest request);
        Task<CanalView> AtualizarAsync(AtendimentoAtor ator, Guid canalId, CanalRequest request);
        /// <summary>O dono do restaurante escolhe quem atende (bot, humano ou hibrido) no proprio numero.</summary>
        Task<CanalView> DefinirModoAsync(AtendimentoAtor ator, Guid canalId, string? modo);
        Task<CanalView> DefinirServicosAsync(AtendimentoAtor ator, Guid canalId, IEnumerable<string>? servicos);
        /// <summary>Passa o numero para outra loja (um numero pertence a uma loja so). Zera os servicos do numero.</summary>
        Task<CanalView> MoverAsync(AtendimentoAtor ator, Guid canalId, Guid destinoEstabelecimentoId);
        Task RemoverAsync(AtendimentoAtor ator, Guid canalId);
        /// <summary>Confere na Meta se o ID existe e e do telefone cadastrado. So a Gestao.</summary>
        Task<VerificacaoCanal> VerificarAsync(AtendimentoAtor ator, Guid canalId);
    }

    public sealed class CanaisWhatsappService : ICanaisWhatsappService
    {
        private const string ModuloWhatsapp = "WHATSAPP";

        private readonly ICanalRepository _canais;
        private readonly ICatalogoRepository _catalogo;
        private readonly ITokenProtector _token;
        private readonly ICanalVerificador _verificador;
        private readonly ILogger<CanaisWhatsappService> _logger;

        public CanaisWhatsappService(
            ICanalRepository canais, ICatalogoRepository catalogo, ITokenProtector token, ICanalVerificador verificador,
            ILogger<CanaisWhatsappService> logger)
        {
            _canais = canais;
            _catalogo = catalogo;
            _token = token;
            _verificador = verificador;
            _logger = logger;
        }

        public async Task<IReadOnlyList<CanalView>> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            return (await _canais.ListarPorLojaAsync(estabelecimentoId)).Select(Ver).ToList();
        }

        public async Task<CanalView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, CanalRequest request)
        {
            ExigirGestao(ator, "criar_canal", estabelecimentoId);
            if (!await _catalogo.LojaExisteAsync(estabelecimentoId)) throw new KeyNotFoundException("Estabelecimento nao encontrado.");

            var erros = AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, request.ModoAtendimento);
            var tokenCifrado = CifrarToken(request.Token, erros);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var ativos = (await _catalogo.ListarServicosDaLojaAsync(estabelecimentoId)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
            var servicos = request.Servicos == null
                ? ativos
                : AtendimentoRegras.ValidarServicosDoCanal(request.Servicos, ativos).ToList();

            var canal = new CanalWhatsapp
            {
                Id = Guid.NewGuid(),
                IdEstabelecimento = estabelecimentoId,
                PhoneNumberId = request.PhoneNumberId!.Trim(),
                NumeroE164 = AtendimentoRegras.NormalizarNumero(request.Numero)!,
                Nome = LimparTexto(request.Nome),
                TokenCifrado = tokenCifrado,
                Status = StatusCanal.Configurando,
                ModoAtendimento = request.ModoAtendimento ?? ModoAtendimento.Hibrido,
                Servicos = servicos
            };

            await ExecutarAsync("criar_canal", canal.Id, () => _canais.CriarAsync(canal, servicos));
            await _catalogo.GarantirModulosAsync(estabelecimentoId, new[] { ModuloWhatsapp });
            await _canais.RegistrarAuditoriaAsync(canal.Id, "criado", null, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero = canal.NumeroE164, servicos }));

            _logger.LogInformation(
                "[canal] ev=criado canal={Canal} loja={Loja} usuario={Usuario} numero={Numero} servicos={Servicos} modo={Modo} token={Token}",
                canal.Id, estabelecimentoId, ator.UsuarioId, canal.NumeroE164, string.Join(",", servicos), canal.ModoAtendimento, canal.TemToken);

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        public async Task<CanalView> AtualizarAsync(AtendimentoAtor ator, Guid canalId, CanalRequest request)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "atualizar_canal", canal.IdEstabelecimento);

            var erros = AtendimentoRegras.ValidarCanal(request.PhoneNumberId, request.Numero, request.ModoAtendimento);
            var novoToken = CifrarToken(request.Token, erros);
            if (erros.Count > 0) throw new RequestValidationException("Dados invalidos.", erros);

            var phoneNumberId = request.PhoneNumberId!.Trim();
            var numero = AtendimentoRegras.NormalizarNumero(request.Numero)!;
            var identidadeMudou = phoneNumberId != canal.PhoneNumberId || numero != canal.NumeroE164;

            canal.PhoneNumberId = phoneNumberId;
            canal.NumeroE164 = numero;
            canal.Nome = LimparTexto(request.Nome);
            if (novoToken != null) canal.TokenCifrado = novoToken;
            if (identidadeMudou || novoToken != null)
            {
                // Dado novo precisa ser verificado de novo na Meta antes de valer como "ativo".
                canal.Status = StatusCanal.Configurando;
                canal.UltimoErro = null;
            }

            await ExecutarAsync("atualizar_canal", canal.Id, () => _canais.AtualizarDadosAsync(canal));
            if (request.ModoAtendimento != null) await _canais.AtualizarModoAsync(canal.Id, request.ModoAtendimento);
            if (request.Servicos != null)
            {
                var ativos = (await _catalogo.ListarServicosDaLojaAsync(canal.IdEstabelecimento)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                await _canais.DefinirServicosAsync(canal.Id, AtendimentoRegras.ValidarServicosDoCanal(request.Servicos, ativos).ToList());
            }

            await _canais.RegistrarAuditoriaAsync(canal.Id, "atualizado", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { identidadeMudou, tokenNovo = novoToken != null }));
            _logger.LogInformation(
                "[canal] ev=atualizado canal={Canal} loja={Loja} usuario={Usuario} identidade_mudou={Mudou} token_novo={TokenNovo}",
                canal.Id, canal.IdEstabelecimento, ator.UsuarioId, identidadeMudou, novoToken != null);

            return Ver((await _canais.ObterAsync(canal.Id))!);
        }

        public async Task<CanalView> DefinirModoAsync(AtendimentoAtor ator, Guid canalId, string? modo)
        {
            var canal = await ObterOuFalharAsync(canalId);
            var donoComPermissao = ator.EstabelecimentoId == canal.IdEstabelecimento && ator.TemPermissao("WhatsApp", "configurar");
            if (!ator.Gestao && !donoComPermissao)
            {
                _logger.LogWarning("[canal] ev=negado acao=definir_modo canal={Canal} usuario={Usuario}", canalId, ator.UsuarioId);
                throw new UnauthorizedAccessException("Voce nao tem permissao para configurar o atendimento deste numero.");
            }

            if (!ModoAtendimento.Valido(modo))
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["modoAtendimento"] = new List<string> { "Modo invalido. Use bot, humano ou hibrido." }
                });
            }

            await _canais.AtualizarModoAsync(canalId, modo!);
            await _canais.RegistrarAuditoriaAsync(canalId, "modo_alterado", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { de = canal.ModoAtendimento, para = modo }));
            _logger.LogInformation(
                "[canal] ev=modo_alterado canal={Canal} loja={Loja} usuario={Usuario} de={De} para={Para}",
                canalId, canal.IdEstabelecimento, ator.UsuarioId, canal.ModoAtendimento, modo);

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task<CanalView> DefinirServicosAsync(AtendimentoAtor ator, Guid canalId, IEnumerable<string>? servicos)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "definir_servicos_canal", canal.IdEstabelecimento);

            var ativos = (await _catalogo.ListarServicosDaLojaAsync(canal.IdEstabelecimento)).Where(s => s.Ativo).Select(s => s.Codigo).ToList();
            var escolhidos = AtendimentoRegras.ValidarServicosDoCanal(servicos, ativos);
            await _canais.DefinirServicosAsync(canalId, escolhidos.ToList());
            await _canais.RegistrarAuditoriaAsync(canalId, "servicos_alterados", canal.IdEstabelecimento, canal.IdEstabelecimento, ator.UsuarioId,
                JsonSerializer.Serialize(new { de = canal.Servicos, para = escolhidos }));
            _logger.LogInformation(
                "[canal] ev=servicos_alterados canal={Canal} loja={Loja} usuario={Usuario} servicos={Servicos}",
                canalId, canal.IdEstabelecimento, ator.UsuarioId, string.Join(",", escolhidos));

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task<CanalView> MoverAsync(AtendimentoAtor ator, Guid canalId, Guid destinoEstabelecimentoId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "mover_canal", canal.IdEstabelecimento);
            if (!await _catalogo.LojaExisteAsync(destinoEstabelecimentoId)) throw new KeyNotFoundException("Estabelecimento de destino nao encontrado.");

            if (canal.IdEstabelecimento == destinoEstabelecimentoId)
            {
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["destino"] = new List<string> { "O numero ja pertence a este estabelecimento." }
                });
            }

            await _canais.MoverAsync(canalId, destinoEstabelecimentoId);
            await _catalogo.GarantirModulosAsync(destinoEstabelecimentoId, new[] { ModuloWhatsapp });
            await _canais.RegistrarAuditoriaAsync(canalId, "movido", canal.IdEstabelecimento, destinoEstabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { servicosRemovidos = canal.Servicos }));
            _logger.LogInformation(
                "[canal] ev=movido canal={Canal} de_loja={Origem} para_loja={Destino} usuario={Usuario} servicos_removidos={Servicos}",
                canalId, canal.IdEstabelecimento, destinoEstabelecimentoId, ator.UsuarioId, string.Join(",", canal.Servicos));

            return Ver((await _canais.ObterAsync(canalId))!);
        }

        public async Task RemoverAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "remover_canal", canal.IdEstabelecimento);

            await _canais.RegistrarAuditoriaAsync(canalId, "removido", canal.IdEstabelecimento, null, ator.UsuarioId,
                JsonSerializer.Serialize(new { numero = canal.NumeroE164, phoneNumberId = canal.PhoneNumberId }));
            await _canais.RemoverAsync(canalId);
            _logger.LogInformation("[canal] ev=removido canal={Canal} loja={Loja} usuario={Usuario}", canalId, canal.IdEstabelecimento, ator.UsuarioId);
        }

        public async Task<VerificacaoCanal> VerificarAsync(AtendimentoAtor ator, Guid canalId)
        {
            var canal = await ObterOuFalharAsync(canalId);
            ExigirGestao(ator, "verificar_canal", canal.IdEstabelecimento);
            return await _verificador.VerificarAsync(canal);
        }

        // ---- internos --------------------------------------------------------------------------------------

        private async Task<CanalWhatsapp> ObterOuFalharAsync(Guid canalId) =>
            await _canais.ObterAsync(canalId) ?? throw new KeyNotFoundException("Numero de WhatsApp nao encontrado.");

        private void ExigirGestao(AtendimentoAtor ator, string acao, Guid estabelecimentoId)
        {
            if (ator.Gestao) return;

            _logger.LogWarning("[canal] ev=negado acao={Acao} motivo=so_gestao loja={Loja} usuario={Usuario}", acao, estabelecimentoId, ator.UsuarioId);
            throw new UnauthorizedAccessException("Somente a Gestao cadastra e altera numeros de WhatsApp.");
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

        private async Task ExecutarAsync(string acao, Guid canalId, Func<Task> operacao)
        {
            try
            {
                await operacao();
            }
            catch (CanalConflitoException ex)
            {
                _logger.LogWarning("[canal] ev=conflito acao={Acao} canal={Canal} campo={Campo}", acao, canalId, ex.Campo);
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [ex.Campo] = new List<string> { ex.Message }
                });
            }
        }

        private static string? LimparTexto(string? texto)
        {
            var limpo = texto?.Trim();
            if (string.IsNullOrEmpty(limpo)) return null;
            return limpo.Length > 120 ? limpo[..120] : limpo;
        }

        private static CanalView Ver(CanalWhatsapp c) => new(
            c.Id, c.IdEstabelecimento, c.PhoneNumberId, c.NumeroE164, c.Nome, c.Status, c.ModoAtendimento, c.Servicos, c.TemToken,
            c.VerificadoEm, c.UltimoRecebimentoEm, c.UltimoEnvioOkEm, c.UltimoErro, c.UltimoErroEm);
    }
}
