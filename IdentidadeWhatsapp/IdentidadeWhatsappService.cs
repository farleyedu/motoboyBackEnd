using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using APIBack.Automation.Helpers;
using APIBack.Repository.Interface;
using APIBack.Security;
using APIBack.Service;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.IdentidadeWhatsapp
{
    /// <summary>
    /// Vinculo do WhatsApp de quem e da equipe com o usuario interno. Cadastrar o telefone nao concede nada: so o vinculo verificado
    /// (codigo enviado ao numero e digitado pela propria pessoa no painel) libera o assistente interno, e sempre com as permissoes reais
    /// dela, nunca como super admin nem pela loja de destino do numero.
    /// </summary>
    public interface IIdentidadeWhatsappService
    {
        Task<VinculoWhatsappView> ObterAsync(int usuarioId);
        /// <summary>Gera um codigo novo, grava so o HMAC e envia ao telefone atual do usuario.</summary>
        Task<VinculoWhatsappView> IniciarVerificacaoAsync(int usuarioId, int atorUsuarioId);
        /// <summary>So o proprio usuario confirma: e ele quem prova que recebeu o codigo no proprio numero.</summary>
        Task<VinculoWhatsappView> ConfirmarAsync(int usuarioId, int atorUsuarioId, string? codigo);
        /// <summary>Idempotente. Vale na hora: toda autorizacao e recalculada mensagem a mensagem.</summary>
        Task<VinculoWhatsappView> RevogarAsync(int usuarioId, int? atorUsuarioId, string motivo);
        /// <summary>Estado efetivo para a lista de usuarios da gestao.</summary>
        Task<IReadOnlyDictionary<int, string>> EstadosAsync(IReadOnlyCollection<UsuarioIdentidade> usuarios);
        /// <summary>Quem mandou a mensagem e para onde ela vai. <paramref name="simulacaoExterna"/> = canal simulador escolhido pelo funcionario.</summary>
        Task<RemetenteResolvido> ResolverRemetenteAsync(string? telefoneRemetente, bool simulacaoExterna);
        /// <summary>Autorizacao do remetente para comando interno, ou nulo. Nunca concede super admin.</summary>
        Task<AutorizacaoWhatsapp?> AutorizarAsync(string? telefoneRemetente);
    }

    /// <summary>Confere se o ator pode mexer no WhatsApp do usuario alvo (o proprio, ou gestor com o usuario no escopo). Implementado por GestaoService.</summary>
    public interface IIdentidadeWhatsappEscopo
    {
        Task GarantirAcessoAsync(int atorUsuarioId, Guid? empresaId, Guid? estabelecimentoId, string? companyRole, string? establishmentRole,
            bool isSuperAdmin, int alvoUsuarioId);
    }

    public sealed class IdentidadeWhatsappService : IIdentidadeWhatsappService
    {
        private const string CodigoInvalidoMensagem = "Codigo invalido ou expirado. Peca um novo codigo.";

        private readonly IIdentidadeWhatsappRepository _repo;
        private readonly IIdentidadeWhatsappEnviador _enviador;
        private readonly IGestaoRepository _gestao;
        private readonly IConfiguration _configuration;
        private readonly ILogger<IdentidadeWhatsappService> _logger;

        public IdentidadeWhatsappService(IIdentidadeWhatsappRepository repo, IIdentidadeWhatsappEnviador enviador, IGestaoRepository gestao,
            IConfiguration configuration, ILogger<IdentidadeWhatsappService> logger)
        {
            _repo = repo;
            _enviador = enviador;
            _gestao = gestao;
            _configuration = configuration;
            _logger = logger;
        }

        /// <summary>Relogio trocavel nos testes.</summary>
        internal Func<DateTime> Agora { get; set; } = () => DateTime.UtcNow;

        internal static IdentidadeWhatsappException TelefoneIndisponivel() =>
            new(409, "telefone_indisponivel", "Este telefone nao pode ser vinculado agora. Confira o numero cadastrado no usuario.");

        public async Task<VinculoWhatsappView> ObterAsync(int usuarioId)
        {
            var usuario = await UsuarioAsync(usuarioId);
            var vinculo = await _repo.ObterVinculoAsync(usuarioId);
            return IdentidadeWhatsappRegras.Ver(usuarioId, vinculo, usuario, Agora());
        }

        public async Task<VinculoWhatsappView> IniciarVerificacaoAsync(int usuarioId, int atorUsuarioId)
        {
            var chave = Chave();
            var agora = Agora();
            var usuario = await UsuarioAsync(usuarioId);
            if (!usuario.Ativo)
                throw new IdentidadeWhatsappException(409, "usuario_inativo", "Usuario inativo nao pode vincular WhatsApp.");
            if (string.IsNullOrEmpty(usuario.TelefoneE164))
                throw new IdentidadeWhatsappException(422, "sem_telefone", "Cadastre o telefone do usuario antes de verificar o WhatsApp.");
            var telefone = usuario.TelefoneE164;

            var vinculo = await _repo.ObterVinculoAsync(usuarioId);
            await PersistirRevogacaoImplicitaAsync(vinculo, usuario, atorUsuarioId);
            if (IdentidadeWhatsappRegras.EstadoEfetivo(vinculo, usuario, agora) == EstadoVinculoWhatsapp.Verificado)
                throw new IdentidadeWhatsappException(409, "ja_verificado", "Este WhatsApp ja esta verificado. Revogue antes de verificar de novo.");

            var bloqueio = IdentidadeWhatsappRegras.BloqueioEnvio(vinculo, agora);
            if (bloqueio.HasValue)
            {
                await AuditarAsync(usuarioId, vinculo?.Id, EventoAuditoriaWhatsapp.EnvioNegado, atorUsuarioId, telefone, bloqueio.Value.Codigo);
                throw new IdentidadeWhatsappException(429, bloqueio.Value.Codigo, "Aguarde para pedir outro codigo.", bloqueio.Value.TenteEm);
            }

            // Telefone de duas pessoas ativas da equipe: ninguem prova posse por ele ate o cadastro ser corrigido.
            var donos = await _repo.ListarUsuariosPorTelefoneAsync(telefone);
            if (donos.Any(d => d.Id != usuarioId))
            {
                await AuditarAsync(usuarioId, vinculo?.Id, EventoAuditoriaWhatsapp.EnvioNegado, atorUsuarioId, telefone, "telefone_compartilhado");
                throw TelefoneIndisponivel();
            }
            await LiberarTelefoneReaproveitadoAsync(telefone, usuarioId, atorUsuarioId);

            vinculo ??= new VinculoWhatsapp { Id = Guid.NewGuid(), UsuarioId = usuarioId };
            var versaoLida = vinculo.Versao;
            var codigo = IdentidadeWhatsappRegras.GerarCodigo();
            vinculo.TelefoneE164 = telefone;
            vinculo.Estado = EstadoVinculoWhatsapp.Pendente;
            vinculo.CodigoHash = IdentidadeWhatsappRegras.HashCodigo(chave, vinculo.Id, telefone, codigo);
            vinculo.CodigoExpiraEm = agora + IdentidadeWhatsappRegras.ValidadeCodigo;
            vinculo.Tentativas = 0;
            vinculo.BloqueadoAte = null;
            vinculo.VerificadoEm = null;
            vinculo.RevogadoEm = null;
            vinculo.Motivo = null;
            IdentidadeWhatsappRegras.ContarEnvio(vinculo, agora);
            if (!await _repo.SalvarAsync(vinculo, versaoLida)) throw Conflito();

            try
            {
                await _enviador.EnviarCodigoAsync(telefone, codigo);
            }
            catch (IdentidadeWhatsappException ex)
            {
                // O codigo nao chegou: nao fica pendente valendo. O envio continua contado no limite.
                vinculo.Estado = EstadoVinculoWhatsapp.NaoVerificado;
                vinculo.CodigoHash = null;
                vinculo.CodigoExpiraEm = null;
                await _repo.SalvarAsync(vinculo, vinculo.Versao);
                await AuditarAsync(usuarioId, vinculo.Id, EventoAuditoriaWhatsapp.EnvioFalhou, atorUsuarioId, telefone, ex.Codigo);
                throw;
            }

            await AuditarAsync(usuarioId, vinculo.Id, EventoAuditoriaWhatsapp.CodigoEnviado, atorUsuarioId, telefone, null);
            return IdentidadeWhatsappRegras.Ver(usuarioId, vinculo, usuario, agora);
        }

        public async Task<VinculoWhatsappView> ConfirmarAsync(int usuarioId, int atorUsuarioId, string? codigo)
        {
            if (atorUsuarioId != usuarioId)
                throw new IdentidadeWhatsappException(403, "somente_o_proprio", "Somente o proprio usuario confirma o codigo recebido no WhatsApp dele.");

            var chave = Chave();
            var agora = Agora();
            var usuario = await UsuarioAsync(usuarioId);
            var vinculo = await _repo.ObterVinculoAsync(usuarioId);
            var resultado = IdentidadeWhatsappRegras.AvaliarConfirmacao(vinculo, usuario, codigo, chave, agora);
            var telefone = vinculo?.TelefoneE164;

            switch (resultado)
            {
                case ResultadoConfirmacao.Bloqueado:
                    throw new IdentidadeWhatsappException(429, "bloqueado", "Muitas tentativas. Aguarde para pedir outro codigo.", vinculo!.BloqueadoAte);

                case ResultadoConfirmacao.SemCodigo:
                    await AuditarAsync(usuarioId, vinculo?.Id, EventoAuditoriaWhatsapp.ConfirmacaoFalhou, atorUsuarioId, telefone, "sem_codigo_valido");
                    throw new IdentidadeWhatsappException(422, "codigo_invalido", CodigoInvalidoMensagem);

                case ResultadoConfirmacao.Invalido:
                {
                    var lida = vinculo!.Versao;
                    vinculo.Tentativas++;
                    var esgotou = vinculo.Tentativas >= IdentidadeWhatsappRegras.MaxTentativas;
                    if (esgotou)
                    {
                        vinculo.Estado = EstadoVinculoWhatsapp.Suspenso;
                        vinculo.BloqueadoAte = agora + IdentidadeWhatsappRegras.Bloqueio;
                        vinculo.CodigoHash = null;
                        vinculo.CodigoExpiraEm = null;
                    }
                    // Sem gravar a tentativa, nao responde se o codigo estava certo ou errado.
                    if (!await _repo.SalvarAsync(vinculo, lida)) throw Conflito();
                    await AuditarAsync(usuarioId, vinculo.Id, esgotou ? EventoAuditoriaWhatsapp.Suspenso : EventoAuditoriaWhatsapp.ConfirmacaoFalhou,
                        atorUsuarioId, telefone, $"tentativa={vinculo.Tentativas}");
                    if (esgotou)
                        throw new IdentidadeWhatsappException(429, "bloqueado", "Muitas tentativas. Aguarde para pedir outro codigo.", vinculo.BloqueadoAte);
                    throw new IdentidadeWhatsappException(422, "codigo_invalido", CodigoInvalidoMensagem);
                }

                default:
                {
                    var lida = vinculo!.Versao;
                    vinculo.Estado = EstadoVinculoWhatsapp.Verificado;
                    vinculo.VerificadoEm = agora;
                    vinculo.CodigoHash = null;
                    vinculo.CodigoExpiraEm = null;
                    vinculo.Tentativas = 0;
                    if (!await _repo.SalvarAsync(vinculo, lida)) throw Conflito();
                    await AuditarAsync(usuarioId, vinculo.Id, EventoAuditoriaWhatsapp.Confirmado, atorUsuarioId, telefone, null);
                    return IdentidadeWhatsappRegras.Ver(usuarioId, vinculo, usuario, agora);
                }
            }
        }

        public async Task<VinculoWhatsappView> RevogarAsync(int usuarioId, int? atorUsuarioId, string motivo)
        {
            var usuario = await UsuarioAsync(usuarioId);
            for (var tentativa = 0; tentativa < 3; tentativa++)
            {
                var vinculo = await _repo.ObterVinculoAsync(usuarioId);
                if (vinculo == null || vinculo.Estado is EstadoVinculoWhatsapp.Revogado or EstadoVinculoWhatsapp.NaoVerificado)
                    return IdentidadeWhatsappRegras.Ver(usuarioId, vinculo, usuario, Agora());
                if (await GravarRevogacaoAsync(vinculo, atorUsuarioId, motivo))
                    return IdentidadeWhatsappRegras.Ver(usuarioId, vinculo, usuario, Agora());
            }
            throw Conflito();
        }

        public async Task<IReadOnlyDictionary<int, string>> EstadosAsync(IReadOnlyCollection<UsuarioIdentidade> usuarios)
        {
            var vinculos = (await _repo.ListarVinculosAsync(usuarios.Select(u => u.Id).ToArray())).ToDictionary(v => v.UsuarioId);
            var agora = Agora();
            return usuarios.ToDictionary(u => u.Id, u => IdentidadeWhatsappRegras.EstadoEfetivo(vinculos.GetValueOrDefault(u.Id), u, agora));
        }

        public async Task<RemetenteResolvido> ResolverRemetenteAsync(string? telefoneRemetente, bool simulacaoExterna)
        {
            if (!TelefoneHelper.TryNormalizarE164(telefoneRemetente, out var telefone) || telefone == null)
                return IdentidadeWhatsappRegras.Resolver(null, false, false, simulacaoExterna);

            var autorizacao = await AutorizarAsync(telefone);
            var equipe = await _repo.ListarUsuariosPorTelefoneAsync(telefone);
            var contato = await _repo.ExisteContatoCrmAsync(telefone);
            return IdentidadeWhatsappRegras.Resolver(
                autorizacao?.UsuarioId,
                equipe.Any(u => u.Id != autorizacao?.UsuarioId),
                contato,
                simulacaoExterna);
        }

        public async Task<AutorizacaoWhatsapp?> AutorizarAsync(string? telefoneRemetente)
        {
            if (!TelefoneHelper.TryNormalizarE164(telefoneRemetente, out var telefone) || telefone == null) return null;
            var vinculo = await _repo.ObterVinculoAtivoPorTelefoneAsync(telefone);
            if (vinculo == null) return null;
            var usuario = await _repo.ObterUsuarioAsync(vinculo.UsuarioId);
            if (!IdentidadeWhatsappRegras.Autoriza(vinculo, usuario, telefone, Agora())) return null;

            return new AutorizacaoWhatsapp(vinculo.UsuarioId, vinculo.Id, vinculo.Versao, await EscoposAsync(vinculo.UsuarioId));
        }

        /// <summary>Vinculos ativos reais do usuario com as permissoes efetivas (padrao do cargo + customizacoes). Super admin fica de fora.</summary>
        private async Task<IReadOnlyList<EscopoAutorizado>> EscoposAsync(int usuarioId)
        {
            var empresas = (await _gestao.ObterUsuarioEmpresasAsync(usuarioId)).Where(r => Ativo(r.Ativo, r.Status)).ToList();
            var lojas = (await _gestao.ObterUsuarioEstabelecimentosAsync(usuarioId)).Where(r => Ativo(r.Ativo, r.Status)).ToList();
            var cargos = empresas.Select(r => RoleCatalog.Normalize(r.TipoAcesso))
                .Concat(lojas.Select(r => RoleCatalog.Normalize(r.TipoAcesso)))
                .Where(c => !string.IsNullOrWhiteSpace(c) && !SuperAdmin(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var padrao = cargos.Length == 0
                ? new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.OrdinalIgnoreCase)
                : await _gestao.ListarPermissoesPadraoPorTipoAsync(cargos);

            var escopos = new List<EscopoAutorizado>();
            foreach (var r in empresas.Where(r => !SuperAdmin(r.TipoAcesso)))
                escopos.Add(new EscopoAutorizado(r.EmpresaId, null, RoleCatalog.Normalize(r.TipoAcesso), Permissoes(padrao, r.TipoAcesso, null)));
            foreach (var r in lojas.Where(r => !SuperAdmin(r.TipoAcesso)))
                escopos.Add(new EscopoAutorizado(r.EmpresaId, r.EstabelecimentoId, RoleCatalog.Normalize(r.TipoAcesso), Permissoes(padrao, r.TipoAcesso, r.PermissoesCustomizadas)));
            return escopos;
        }

        private static IReadOnlyDictionary<string, List<string>> Permissoes(Dictionary<string, Dictionary<string, List<string>>> padrao, string? cargo, string? custom) =>
            (IReadOnlyDictionary<string, List<string>>?)GestaoService.BuildEffectivePermissions(padrao, cargo, custom)
            ?? new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        private static bool Ativo(bool? ativo, string? status) =>
            ativo != false && string.Equals((status ?? "ativo").Trim(), "ativo", StringComparison.OrdinalIgnoreCase);

        private static bool SuperAdmin(string? cargo) =>
            string.Equals(RoleCatalog.Normalize(cargo), "super_admin", StringComparison.OrdinalIgnoreCase);

        /// <summary>Outro usuario ainda segura este telefone com vinculo cujo dono trocou de numero ou saiu: libera (revoga) o antigo.</summary>
        private async Task LiberarTelefoneReaproveitadoAsync(string telefone, int usuarioId, int atorUsuarioId)
        {
            var antigo = await _repo.ObterVinculoAtivoPorTelefoneAsync(telefone);
            if (antigo == null || antigo.UsuarioId == usuarioId) return;
            var dono = await _repo.ObterUsuarioAsync(antigo.UsuarioId);
            var motivo = dono == null ? MotivoRevogacaoWhatsapp.TelefoneReaproveitado : IdentidadeWhatsappRegras.MotivoRevogacaoImplicita(antigo, dono);
            if (motivo == null) throw TelefoneIndisponivel();
            if (!await GravarRevogacaoAsync(antigo, atorUsuarioId, motivo)) throw Conflito();
        }

        /// <summary>Grava a revogacao que o estado efetivo ja aplicava (telefone trocado ou conta inativa), para a auditoria e o indice unico.</summary>
        private async Task PersistirRevogacaoImplicitaAsync(VinculoWhatsapp? vinculo, UsuarioIdentidade usuario, int? atorUsuarioId)
        {
            if (vinculo == null) return;
            var motivo = IdentidadeWhatsappRegras.MotivoRevogacaoImplicita(vinculo, usuario);
            if (motivo != null && !await GravarRevogacaoAsync(vinculo, atorUsuarioId, motivo)) throw Conflito();
        }

        private async Task<bool> GravarRevogacaoAsync(VinculoWhatsapp vinculo, int? atorUsuarioId, string motivo)
        {
            var lida = vinculo.Versao;
            vinculo.Estado = EstadoVinculoWhatsapp.Revogado;
            vinculo.RevogadoEm = Agora();
            vinculo.Motivo = motivo;
            vinculo.CodigoHash = null;
            vinculo.CodigoExpiraEm = null;
            if (!await _repo.SalvarAsync(vinculo, lida)) return false;
            await AuditarAsync(vinculo.UsuarioId, vinculo.Id, EventoAuditoriaWhatsapp.Revogado, atorUsuarioId, vinculo.TelefoneE164, motivo);
            return true;
        }

        private async Task<UsuarioIdentidade> UsuarioAsync(int usuarioId) =>
            await _repo.ObterUsuarioAsync(usuarioId) ?? throw new KeyNotFoundException("Usuario nao encontrado.");

        private byte[] Chave()
        {
            var segredo = _configuration["IdentidadeWhatsapp:Segredo"];
            if (string.IsNullOrWhiteSpace(segredo)) segredo = _configuration["Jwt:SecretKey"];
            if (string.IsNullOrWhiteSpace(segredo) || segredo.Length < 32)
                throw new IdentidadeWhatsappException(503, "segredo_ausente", "Verificacao de WhatsApp indisponivel: segredo do servidor nao configurado.");
            return IdentidadeWhatsappRegras.DerivarChave(segredo);
        }

        private static IdentidadeWhatsappException Conflito() =>
            new(409, "conflito", "Outra operacao alterou este vinculo ao mesmo tempo. Tente de novo.");

        private async Task AuditarAsync(int usuarioId, Guid? vinculoId, string evento, int? atorUsuarioId, string? telefone, string? detalhe)
        {
            try
            {
                await _repo.AuditarAsync(new AuditoriaWhatsapp(usuarioId, vinculoId, evento, atorUsuarioId, IdentidadeWhatsappRegras.FinalTelefone(telefone), detalhe));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[identidade.wa] ev=auditoria_falhou usuario={Usuario} evento={Evento}", usuarioId, evento);
            }
        }
    }
}
