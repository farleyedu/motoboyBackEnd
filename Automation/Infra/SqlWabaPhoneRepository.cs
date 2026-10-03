// ================= ZIPPYGO AUTOMATION SECTION (BEGIN) =================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Models;
using APIBack.Service;
using Microsoft.Extensions.Logging;

namespace APIBack.Automation.Infra
{
    /// <summary>
    /// Ponte entre o contrato antigo (IWabaPhoneRepository, que varios servicos ainda usam) e o modelo novo: tudo agora
    /// le e grava em canal_whatsapp (um numero pertence a uma loja; a loja pode ter varios). A tabela waba_phone nao e
    /// mais lida nem escrita; ela some na etapa de limpeza do plano de atendimento.
    /// </summary>
    public class SqlWabaPhoneRepository : IWabaPhoneRepository
    {
        private readonly ICanalRepository _canais;
        private readonly ICatalogoRepository _catalogo;
        private readonly ITokenProtector _token;
        private readonly ILogger<SqlWabaPhoneRepository>? _logger;

        public SqlWabaPhoneRepository(
            ICanalRepository canais, ICatalogoRepository catalogo, ITokenProtector token, ILogger<SqlWabaPhoneRepository>? logger = null)
        {
            _canais = canais;
            _catalogo = catalogo;
            _token = token;
            _logger = logger;
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorPhoneNumberIdAsync(string phoneNumberId)
        {
            if (string.IsNullOrWhiteSpace(phoneNumberId)) return null;

            var canal = await _canais.ObterAtivoPorPhoneNumberIdAsync(Digitos(phoneNumberId));
            return canal?.IdEstabelecimento;
        }

        public async Task<Guid?> ObterIdEstabelecimentoPorDisplayPhoneAsync(string displayPhoneNumber)
        {
            var digitos = Digitos(displayPhoneNumber);
            if (digitos.Length == 0) return null;

            var canal = await _canais.ObterPorNumeroAsync(digitos);
            return canal is { Status: not StatusCanal.Inativo } ? canal.IdEstabelecimento : null;
        }

        public async Task<bool> ExisteAtivoAsync(string phoneNumberId) =>
            !string.IsNullOrWhiteSpace(phoneNumberId) && await _canais.ObterAtivoPorPhoneNumberIdAsync(Digitos(phoneNumberId)) != null;

        public async Task<string?> ObterPhoneNumberIdPorEstabelecimentoAsync(Guid idEstabelecimento) =>
            idEstabelecimento == Guid.Empty ? null : (await _canais.ObterPrimeiroUsavelAsync(idEstabelecimento))?.PhoneNumberId;

        public async Task<string?> ObterDisplayPhonePorEstabelecimentoAsync(Guid idEstabelecimento) =>
            idEstabelecimento == Guid.Empty ? null : (await _canais.ObterPrimeiroUsavelAsync(idEstabelecimento))?.NumeroE164;

        public async Task<string?> ObterDisplayPhoneParaServicoAsync(Guid idEstabelecimento, string servicoCodigo) =>
            idEstabelecimento == Guid.Empty || string.IsNullOrWhiteSpace(servicoCodigo)
                ? null
                : (await _canais.ObterParaServicoAsync(idEstabelecimento, servicoCodigo))?.NumeroE164;

        public async Task<string?> ObterPhoneNumberIdPorDisplayPhoneAsync(string displayPhoneNumber)
        {
            var digitos = Digitos(displayPhoneNumber);
            return digitos.Length == 0 ? null : (await _canais.ObterPorNumeroAsync(digitos))?.PhoneNumberId;
        }

        /// <summary>Token deste numero (por ID da Meta ou por telefone), decifrado. Null = sem token proprio.</summary>
        public async Task<string?> ObterAccessTokenPorPhoneNumberIdAsync(string phoneNumberId)
        {
            var digitos = Digitos(phoneNumberId);
            if (digitos.Length == 0) return null;

            var canal = await _canais.ObterAtivoPorPhoneNumberIdAsync(digitos) ?? await _canais.ObterPorNumeroAsync(digitos);
            return canal == null ? null : _token.Revelar(canal.TokenCifrado);
        }

        /// <summary>
        /// Cadastro vindo da tela antiga de estabelecimento (so a Gestao chega aqui): grava no primeiro numero da loja,
        /// ou cria um. Exige o ID da Meta e o telefone para criar; para alterar basta o que mudou.
        /// </summary>
        /// <exception cref="RequestValidationException">O numero ou o ID ja pertence a outra loja.</exception>
        public async Task<bool> InserirOuAtualizarAsync(WabaPhone wabaPhone)
        {
            if (wabaPhone == null || wabaPhone.IdEstabelecimento == Guid.Empty) return false;

            var phoneNumberId = Digitos(wabaPhone.PhoneNumberId);
            var numero = AtendimentoRegras.NormalizarNumero(wabaPhone.DisplayPhoneNumber);
            var existente = (await _canais.ListarPorLojaAsync(wabaPhone.IdEstabelecimento)).FirstOrDefault();

            try
            {
                if (existente == null)
                {
                    if (!AtendimentoRegras.PhoneNumberIdValido(phoneNumberId) || numero == null)
                    {
                        _logger?.LogWarning(
                            "[canal] ev=cadastro_recusado motivo=faltam_dados loja={Loja} tem_id={TemId} tem_numero={TemNumero}",
                            wabaPhone.IdEstabelecimento, AtendimentoRegras.PhoneNumberIdValido(phoneNumberId), numero != null);
                        return false;
                    }

                    var ativos = (await _catalogo.ListarServicosDaLojaAsync(wabaPhone.IdEstabelecimento))
                        .Where(s => s.Ativo).Select(s => s.Codigo).ToList();
                    var canal = new CanalWhatsapp
                    {
                        Id = Guid.NewGuid(), IdEstabelecimento = wabaPhone.IdEstabelecimento, PhoneNumberId = phoneNumberId,
                        NumeroE164 = numero, Nome = wabaPhone.Descricao, ModoAtendimento = ModoAtendimento.Hibrido,
                        TokenCifrado = ProtegerSeHouver(wabaPhone.AccessToken), Servicos = ativos
                    };
                    await _canais.CriarAsync(canal, ativos);
                    await _catalogo.GarantirModulosAsync(wabaPhone.IdEstabelecimento, new[] { "WHATSAPP" });
                    _logger?.LogInformation("[canal] ev=criado origem=tela_estabelecimento canal={Canal} loja={Loja}", canal.Id, canal.IdEstabelecimento);
                    return true;
                }

                var mudouIdentidade = false;
                if (AtendimentoRegras.PhoneNumberIdValido(phoneNumberId) && phoneNumberId != existente.PhoneNumberId)
                {
                    existente.PhoneNumberId = phoneNumberId;
                    mudouIdentidade = true;
                }

                if (numero != null && numero != existente.NumeroE164)
                {
                    existente.NumeroE164 = numero;
                    mudouIdentidade = true;
                }

                var novoToken = ProtegerSeHouver(wabaPhone.AccessToken);
                if (novoToken != null) existente.TokenCifrado = novoToken;

                if (mudouIdentidade || novoToken != null)
                {
                    existente.Status = StatusCanal.Configurando;
                    existente.UltimoErro = null;
                    await _canais.AtualizarDadosAsync(existente);
                    _logger?.LogInformation(
                        "[canal] ev=atualizado origem=tela_estabelecimento canal={Canal} loja={Loja} identidade_mudou={Mudou}",
                        existente.Id, existente.IdEstabelecimento, mudouIdentidade);
                }

                return true;
            }
            catch (CanalConflitoException ex)
            {
                var campo = ex.Campo == "numero" ? "wabaDisplayPhone" : "wabaPhoneNumberId";
                throw new RequestValidationException("Dados invalidos.", new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
                {
                    [campo] = new List<string> { ex.Message }
                });
            }
        }

        private string? ProtegerSeHouver(string? token) =>
            string.IsNullOrWhiteSpace(token) || !_token.Configurado ? null : _token.Proteger(token.Trim());

        private static string Digitos(string? valor) => new((valor ?? string.Empty).Where(char.IsDigit).ToArray());
    }
}
// ================= ZIPPYGO AUTOMATION SECTION (END) ===================
