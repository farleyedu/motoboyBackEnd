using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Automation.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace APIBack.Atendimento.Templates
{
    public interface ITemplatesWhatsappService
    {
        /// <summary>Modelos da loja na conta (WABA) do numero, so do banco (sem chamar a Meta). Quem le a loja.</summary>
        Task<TemplatesListaView> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId);
        /// <summary>Detalhe com componentes enviados, componentes da Meta e historico. Quem le a loja.</summary>
        Task<TemplateView> ObterAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId);
        /// <summary>Confere sem gravar nada se a conexao permite gerenciar modelos (token, WABA, permissao). Quem le a loja.</summary>
        Task<DiagnosticoTemplatesView> DiagnosticarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId);
        /// <summary>So valida e monta os componentes como iriam para a Meta (sem chamar a Meta).</summary>
        ValidacaoTemplateView Validar(CriarTemplateRequest? request);
        /// <summary>Valida, grava e envia para analise da Meta. Sem resposta da Meta o modelo fica "verificando" (Confirmado = false).</summary>
        Task<CriacaoTemplateView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, CriarTemplateRequest? request);
        /// <summary>Atualiza os modelos DA LOJA com o que a Meta tem. Modelos desconhecidos da WABA nunca sao alterados nem adotados.</summary>
        Task<SincronizacaoView> SincronizarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId);
        Task<TemplateView> SincronizarUmAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId);
        /// <summary>Exclui na Meta SO este modelo (hsm_id), com o nome digitado como confirmacao. O historico local fica.</summary>
        Task<TemplateView> ExcluirAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId, string? confirmarNome);
    }

    /// <summary>
    /// Modelos oficiais de mensagem (templates) da Meta por loja. Regras de seguranca:
    /// <list type="bullet">
    /// <item>WABA e token saem do canal no backend (nunca do navegador). Numero proprio: a WABA da conexao. Numero da Zippy/central: uma
    /// das WABAs autorizadas no servidor (WhatsApp:WabaIds). Em ambos, so se opera depois de confirmar na Meta que o numero e da WABA.</item>
    /// <item>A WABA pode ser de varias lojas: o que isola e o vinculo local (whatsapp_template.id_estabelecimento). Um modelo que nao
    /// esta no Zippy nao e de ninguem: nao e alterado, excluido nem adotado; e o nome dele fica bloqueado para criacao.</item>
    /// <item>A Meta decide a aprovacao; aqui so se grava o status que ela devolve.</item>
    /// </list>
    /// </summary>
    public sealed class TemplatesWhatsappService : ITemplatesWhatsappService
    {
        /// <summary>Depois disto sem resposta, um envio "enviando"/"verificando" que a Meta nao tem e dado como nao criado.</summary>
        internal static readonly TimeSpan EnvioExpira = TimeSpan.FromMinutes(2);
        internal static readonly TimeSpan IntervaloSincronizacao = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan CacheWaba = TimeSpan.FromMinutes(15);
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        private readonly ICanalRepository _canais;
        private readonly ITemplateRepository _templates;
        private readonly IMetaTemplatesApi _meta;
        private readonly ITokenProtector _token;
        private readonly IWhatsAppTokenProvider _tokenGlobal;
        private readonly IConfiguration _configuration;
        private readonly IMemoryCache _cache;
        private readonly ILogger<TemplatesWhatsappService> _logger;

        /// <summary>Relogio (trocado nos testes).</summary>
        internal Func<DateTime> Agora { get; set; } = () => DateTime.UtcNow;

        public TemplatesWhatsappService(
            ICanalRepository canais, ITemplateRepository templates, IMetaTemplatesApi meta, ITokenProtector token,
            IWhatsAppTokenProvider tokenGlobal, IConfiguration configuration, IMemoryCache cache, ILogger<TemplatesWhatsappService> logger)
        {
            _canais = canais;
            _templates = templates;
            _meta = meta;
            _token = token;
            _tokenGlobal = tokenGlobal;
            _configuration = configuration;
            _cache = cache;
            _logger = logger;
        }

        private sealed record Contexto(CanalWhatsapp Canal, string Waba, string Token);

        // ---- leitura ---------------------------------------------------------------------------------------

        public async Task<TemplatesListaView> ListarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            return await MontarListaAsync(ator, canal, WabaConhecida(canal));
        }

        public async Task<TemplateView> ObterAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            await CanalDaLojaAsync(estabelecimentoId, canalId);
            var t = await TemplateDaLojaAsync(estabelecimentoId, templateId);
            return await VerDetalheAsync(t);
        }

        public async Task<DiagnosticoTemplatesView> DiagnosticarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId)
        {
            ServicosDaLojaService.ExigirLeitura(ator, estabelecimentoId);
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            try
            {
                var ctx = await ResolverAsync(canal);
                // Consulta minima de modelos: exige whatsapp_business_management.
                var teste = await _meta.ListarAsync(ctx.Waba, ctx.Token, "zippy_diagnostico");
                if (!teste.Ok) throw TemplateErros.DaMeta(teste.Falha!);
                return new DiagnosticoTemplatesView(true, canal.Origem, null, "Conexao pronta para gerenciar modelos.", null, null);
            }
            catch (WhatsappException ex)
            {
                return new DiagnosticoTemplatesView(false, canal.Origem, ex.Erro.Codigo, ex.Erro.Mensagem, ex.Erro.ComoResolver, ex.Erro.Link);
            }
        }

        public ValidacaoTemplateView Validar(CriarTemplateRequest? request)
        {
            try
            {
                var novo = TemplateRegras.Validar(request);
                return new ValidacaoTemplateView(true, new Dictionary<string, List<string>>(), TemplateRegras.ComponentesMeta(novo.Conteudo));
            }
            catch (TemplateIncompativelException ex)
            {
                return new ValidacaoTemplateView(false, ex.Errors, null);
            }
        }

        // ---- criacao ---------------------------------------------------------------------------------------

        public async Task<CriacaoTemplateView> CriarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, CriarTemplateRequest? request)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            ExigirGerenciar(ator, estabelecimentoId, canal, "criar");
            var novo = TemplateRegras.Validar(request);
            var ctx = await ResolverAsync(canal);

            var daWaba = await _templates.ListarDaWabaAsync(ctx.Waba);
            // Nome de outra loja (em qualquer idioma) fica com ela: nao se mistura dono na mesma WABA.
            if (daWaba.Any(t => t.Nome == novo.Nome && t.IdEstabelecimento != estabelecimentoId && t.Situacao != SituacaoTemplate.Falha))
            {
                _logger.LogWarning("[wa.tpl] ev=negado motivo=nome_de_outra_loja loja={Loja} canal={Canal} nome={Nome}", estabelecimentoId, canalId, novo.Nome);
                throw TemplateErros.Duplicado();
            }

            var anterior = daWaba.FirstOrDefault(t => t.Nome == novo.Nome && t.Idioma == novo.Idioma && t.IdEstabelecimento == estabelecimentoId
                && t.Situacao is SituacaoTemplate.Enviando or SituacaoTemplate.Verificando or SituacaoTemplate.Enviado);
            if (anterior != null) await ResolverAnteriorAsync(ctx, anterior, ator);

            // Na Meta: um modelo de mesmo nome que o Zippy nao conhece e de outra pessoa (ou de outra loja na WABA compartilhada).
            var remotos = await _meta.ListarAsync(ctx.Waba, ctx.Token, novo.Nome);
            if (!remotos.Ok) throw TemplateErros.DaMeta(remotos.Falha!);
            var conhecidos = daWaba.Where(t => t.MetaTemplateId != null).Select(t => t.MetaTemplateId!).ToHashSet();
            var estranho = remotos.Valor!.Itens.FirstOrDefault(r => r.Nome == novo.Nome && !conhecidos.Contains(r.Id));
            if (estranho != null)
            {
                _logger.LogWarning("[wa.tpl] ev=negado motivo=nome_existe_na_meta loja={Loja} canal={Canal} nome={Nome}", estabelecimentoId, canalId, novo.Nome);
                throw estranho.Status is StatusMeta.Excluido or StatusMeta.ExclusaoPendente ? TemplateErros.NomeEmExclusao() : TemplateErros.Duplicado();
            }

            var agora = Agora();
            var template = new TemplateWhatsapp
            {
                Id = Guid.NewGuid(),
                IdEstabelecimento = estabelecimentoId,
                IdCanal = canal.Id,
                WabaId = ctx.Waba,
                Nome = novo.Nome,
                Idioma = novo.Idioma,
                Categoria = novo.Categoria,
                ConteudoJson = JsonSerializer.Serialize(novo.Conteudo, Json),
                Situacao = SituacaoTemplate.Enviando,
                CriadoPor = ator.UsuarioId,
                EnviadoEm = agora,
                CriadoEm = agora
            };

            try
            {
                await _templates.InserirAsync(template);
            }
            catch (TemplateConflitoException)
            {
                // Dois cliques (ou duas abas) ao mesmo tempo: o indice unico deixa passar so um envio.
                throw TemplateErros.EmEnvio();
            }

            await Evento(template, "criado", ator.UsuarioId, new { template.Nome, template.Idioma, template.Categoria, canal = canal.Id });

            var resposta = await _meta.CriarAsync(ctx.Waba, ctx.Token, TemplateRegras.PayloadMeta(novo));
            if (resposta.Ok)
            {
                template.MetaTemplateId = resposta.Valor!.Id;
                template.Situacao = SituacaoTemplate.Enviado;
                template.StatusMeta = (resposta.Valor.Status ?? StatusMeta.Pendente).ToUpperInvariant();
                template.CategoriaMeta = resposta.Valor.Categoria?.ToUpperInvariant() ?? novo.Categoria;
                template.StatusAlteradoEm = agora;
                template.SincronizadoEm = agora;
                await _templates.AtualizarAsync(template);
                await Evento(template, "enviado", ator.UsuarioId, new { metaId = template.MetaTemplateId, status = template.StatusMeta, categoria = template.CategoriaMeta });
                _logger.LogInformation("[wa.tpl] ev=criado loja={Loja} canal={Canal} template={Template} meta={Meta} status={Status} usuario={Usuario}",
                    estabelecimentoId, canalId, template.Id, template.MetaTemplateId, template.StatusMeta, ator.UsuarioId);
                return new CriacaoTemplateView(true, await VerDetalheAsync(template));
            }

            var falha = resposta.Falha!;
            if (falha.SemResposta || falha.Http >= 500)
            {
                // A Meta pode ter criado: nunca reenviar sem conferir. Fica "verificando" e ja tenta conferir uma vez.
                template.Situacao = SituacaoTemplate.Verificando;
                template.UltimoErroCodigo = TemplateCodigos.EnvioIncerto;
                template.UltimoErro = "A Meta nao confirmou o recebimento. Use Sincronizar para conferir antes de enviar de novo.";
                await _templates.AtualizarAsync(template);
                await Evento(template, "envio_incerto", ator.UsuarioId, new { http = falha.Http });
                _logger.LogWarning("[wa.tpl] ev=envio_incerto loja={Loja} canal={Canal} template={Template} http={Http}", estabelecimentoId, canalId, template.Id, falha.Http);

                await ReconciliarAsync(ctx, template, ator.UsuarioId);
                return new CriacaoTemplateView(template.Situacao == SituacaoTemplate.Enviado, await VerDetalheAsync(template));
            }

            var erro = TemplateErros.DaMeta(falha);
            template.Situacao = SituacaoTemplate.Falha;
            template.UltimoErro = erro.Erro.Mensagem;
            template.UltimoErroCodigo = erro.Erro.Codigo;
            await _templates.AtualizarAsync(template);
            await Evento(template, "recusado_meta", ator.UsuarioId, new { codigo = erro.Erro.Codigo, http = falha.Http, metaCodigo = falha.Codigo, metaSubcodigo = falha.Subcodigo });
            _logger.LogWarning("[wa.tpl] ev=recusado loja={Loja} canal={Canal} template={Template} codigo={Codigo}", estabelecimentoId, canalId, template.Id, erro.Erro.Codigo);
            throw erro;
        }

        /// <summary>Ja existe um envio da loja com este nome e idioma: confere antes de deixar enviar outro.</summary>
        private async Task ResolverAnteriorAsync(Contexto ctx, TemplateWhatsapp anterior, AtendimentoAtor ator)
        {
            if (anterior.Situacao == SituacaoTemplate.Enviado) throw TemplateErros.Duplicado();
            if (anterior.Situacao == SituacaoTemplate.Enviando && !Expirado(anterior)) throw TemplateErros.EmEnvio();

            await ReconciliarAsync(ctx, anterior, ator.UsuarioId);
            if (anterior.Situacao == SituacaoTemplate.Enviado)
            {
                throw new WhatsappException(new WhatsappErro(TemplateCodigos.Duplicado,
                    "Este modelo ja tinha sido criado na Meta e foi recuperado.",
                    "Ele aparece na lista com o status real. Nao e preciso enviar de novo.", "nome"), 409);
            }
            if (anterior.Situacao != SituacaoTemplate.Falha) throw TemplateErros.EmEnvio();
        }

        // ---- sincronizacao ---------------------------------------------------------------------------------

        public async Task<SincronizacaoView> SincronizarAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            ExigirGerenciar(ator, estabelecimentoId, canal, "sincronizar");

            // A lista inteira da WABA custa varias chamadas na Meta: uma sincronizacao por numero a cada poucos segundos.
            var chave = $"wa.tpl.sync:{canal.Id}";
            if (_cache.TryGetValue(chave, out _)) throw TemplateErros.Limite();
            _cache.Set(chave, true, IntervaloSincronizacao);

            var ctx = await ResolverAsync(canal);
            var remotos = await _meta.ListarAsync(ctx.Waba, ctx.Token);
            if (!remotos.Ok)
            {
                var erro = TemplateErros.DaMeta(remotos.Falha!);
                await _canais.RegistrarAuditoriaAsync(canal.Id, "templates_sincronizar_falhou", estabelecimentoId, estabelecimentoId, ator.UsuarioId,
                    JsonSerializer.Serialize(new { codigo = erro.Erro.Codigo }));
                throw erro;
            }

            var lista = remotos.Valor!;
            var agora = Agora();
            var daWaba = await _templates.ListarDaWabaAsync(ctx.Waba);
            var donos = daWaba.Where(t => t.MetaTemplateId != null).Select(t => t.MetaTemplateId!).ToHashSet();
            var porId = lista.Itens.GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
            int atualizados = 0, alterados = 0, naoEncontrados = 0;

            foreach (var t in daWaba.Where(t => t.IdEstabelecimento == estabelecimentoId))
            {
                if (t.Situacao == SituacaoTemplate.Enviado && t.MetaTemplateId != null)
                {
                    if (porId.TryGetValue(t.MetaTemplateId, out var remoto))
                    {
                        atualizados++;
                        if (await AplicarAsync(t, remoto, agora, ator.UsuarioId)) alterados++;
                    }
                    else if (lista.Completa && t.StatusMeta != StatusMeta.Excluido)
                    {
                        naoEncontrados++;
                        await MarcarAusenteAsync(t, agora, ator.UsuarioId);
                    }
                }
                else if (t.Situacao == SituacaoTemplate.Verificando || (t.Situacao == SituacaoTemplate.Enviando && Expirado(t)))
                {
                    var achado = lista.Itens.FirstOrDefault(r => r.Nome == t.Nome && r.Idioma == t.Idioma && !donos.Contains(r.Id));
                    if (achado != null)
                    {
                        await AdotarAsync(t, achado, agora, ator.UsuarioId);
                        donos.Add(achado.Id);
                        atualizados++;
                        alterados++;
                    }
                    else if (lista.Completa && Expirado(t))
                    {
                        await MarcarNaoCriadoAsync(t, ator.UsuarioId);
                        alterados++;
                    }
                }
            }

            var ignorados = lista.Itens.Count(r => !donos.Contains(r.Id));
            await _canais.RegistrarAuditoriaAsync(canal.Id, "templates_sincronizados", estabelecimentoId, estabelecimentoId, ator.UsuarioId,
                JsonSerializer.Serialize(new { atualizados, alterados, naoEncontrados, completa = lista.Completa }));
            _logger.LogInformation(
                "[wa.tpl] ev=sincronizado loja={Loja} canal={Canal} atualizados={Atualizados} alterados={Alterados} nao_encontrados={NaoEncontrados} ignorados={Ignorados} completa={Completa}",
                estabelecimentoId, canalId, atualizados, alterados, naoEncontrados, ignorados, lista.Completa);

            return new SincronizacaoView(atualizados, alterados, naoEncontrados, ator.Gestao ? ignorados : null, lista.Completa, agora,
                await MontarListaAsync(ator, canal, ctx.Waba));
        }

        public async Task<TemplateView> SincronizarUmAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            ExigirGerenciar(ator, estabelecimentoId, canal, "sincronizar_um");
            var t = await TemplateDaLojaAsync(estabelecimentoId, templateId);
            var ctx = await ResolverAsync(canal);
            ExigirMesmaWaba(t, ctx);

            if (t.Situacao == SituacaoTemplate.Enviado && t.MetaTemplateId != null)
            {
                var r = await _meta.ObterAsync(t.MetaTemplateId, ctx.Token);
                if (r.Ok && r.Valor!.Nome == t.Nome)
                {
                    await AplicarAsync(t, r.Valor, Agora(), ator.UsuarioId);
                }
                else if (r.Ok || r.Falha is { SemResposta: false, Codigo: 100 } or { SemResposta: false, Http: 404 })
                {
                    // Sumiu da Meta (ou o ID agora aponta para outro modelo): fica indisponivel, nunca "aprovado".
                    await MarcarAusenteAsync(t, Agora(), ator.UsuarioId);
                }
                else
                {
                    throw TemplateErros.DaMeta(r.Falha!);
                }
            }
            else if (t.Situacao is SituacaoTemplate.Verificando or SituacaoTemplate.Enviando)
            {
                await ReconciliarAsync(ctx, t, ator.UsuarioId);
            }

            return await VerDetalheAsync(t);
        }

        // ---- exclusao --------------------------------------------------------------------------------------

        public async Task<TemplateView> ExcluirAsync(AtendimentoAtor ator, Guid estabelecimentoId, Guid canalId, Guid templateId, string? confirmarNome)
        {
            var canal = await CanalDaLojaAsync(estabelecimentoId, canalId);
            ExigirGerenciar(ator, estabelecimentoId, canal, "excluir");
            var t = await TemplateDaLojaAsync(estabelecimentoId, templateId);

            if (!string.Equals((confirmarNome ?? string.Empty).Trim(), t.Nome, StringComparison.Ordinal))
            {
                throw new TemplateRequisicaoInvalidaException(new Dictionary<string, List<string>>
                {
                    ["confirmarNome"] = new() { "Digite o nome tecnico do modelo para confirmar a exclusao." }
                });
            }
            if (t.Situacao != SituacaoTemplate.Enviado || t.MetaTemplateId == null)
                throw TemplateErros.NaoExcluivel("Este modelo nao esta confirmado na Meta.");

            var ctx = await ResolverAsync(canal);
            ExigirMesmaWaba(t, ctx);

            var r = await _meta.ExcluirAsync(ctx.Waba, ctx.Token, t.Nome, t.MetaTemplateId);
            if (!r.Ok && !(r.Falha is { SemResposta: false, Codigo: 100, Subcodigo: 33 }))
            {
                var erro = TemplateErros.DaMeta(r.Falha!);
                await Evento(t, "exclusao_falhou", ator.UsuarioId, new { codigo = erro.Erro.Codigo });
                throw erro;
            }

            t.Situacao = SituacaoTemplate.Excluido;
            t.StatusMeta = StatusMeta.Excluido;
            t.ExcluidoEm = Agora();
            t.StatusAlteradoEm = t.ExcluidoEm;
            await _templates.AtualizarAsync(t);
            await Evento(t, "excluido", ator.UsuarioId, new { metaId = t.MetaTemplateId });
            _logger.LogInformation("[wa.tpl] ev=excluido loja={Loja} canal={Canal} template={Template} meta={Meta} usuario={Usuario}",
                estabelecimentoId, canalId, t.Id, t.MetaTemplateId, ator.UsuarioId);
            return await VerDetalheAsync(t);
        }

        // ---- reconciliacao ---------------------------------------------------------------------------------

        /// <summary>
        /// Envio sem resposta: procura na Meta pelo nome + idioma. Achou (e ninguem do Zippy e dono) = foi criado: adota o ID. Nao achou
        /// com a lista completa e o envio ja expirou = nao foi criado (falha; pode enviar de novo). Meta fora: continua "verificando".
        /// </summary>
        private async Task ReconciliarAsync(Contexto ctx, TemplateWhatsapp t, int? usuarioId)
        {
            var r = await _meta.ListarAsync(ctx.Waba, ctx.Token, t.Nome);
            if (!r.Ok)
            {
                _logger.LogWarning("[wa.tpl] ev=reconciliar_sem_resposta template={Template} codigo={Codigo}", t.Id, TemplateErros.CodigoDe(r.Falha!));
                return;
            }

            var donos = (await _templates.ListarDaWabaAsync(ctx.Waba)).Where(x => x.MetaTemplateId != null && x.Id != t.Id).Select(x => x.MetaTemplateId!).ToHashSet();
            var achado = r.Valor!.Itens.FirstOrDefault(x => x.Nome == t.Nome && x.Idioma == t.Idioma && !donos.Contains(x.Id));
            if (achado != null)
            {
                await AdotarAsync(t, achado, Agora(), usuarioId);
            }
            else if (r.Valor.Completa && Expirado(t))
            {
                await MarcarNaoCriadoAsync(t, usuarioId);
            }
            else
            {
                t.SincronizadoEm = Agora();
                await _templates.AtualizarAsync(t);
            }
        }

        private async Task AdotarAsync(TemplateWhatsapp t, MetaTemplate remoto, DateTime agora, int? usuarioId)
        {
            t.MetaTemplateId = remoto.Id;
            t.Situacao = SituacaoTemplate.Enviado;
            t.UltimoErro = null;
            t.UltimoErroCodigo = null;
            await AplicarAsync(t, remoto, agora, usuarioId, forcarGravacao: true);
            await Evento(t, "confirmado_na_meta", usuarioId, new { metaId = remoto.Id, status = t.StatusMeta });
        }

        private async Task MarcarNaoCriadoAsync(TemplateWhatsapp t, int? usuarioId)
        {
            t.Situacao = SituacaoTemplate.Falha;
            t.UltimoErroCodigo = TemplateCodigos.EnvioIncerto;
            t.UltimoErro = "A Meta nao registrou este modelo. Voce pode envia-lo de novo.";
            t.SincronizadoEm = Agora();
            await _templates.AtualizarAsync(t);
            await Evento(t, "nao_criado_na_meta", usuarioId, null);
        }

        private async Task MarcarAusenteAsync(TemplateWhatsapp t, DateTime agora, int? usuarioId)
        {
            var anterior = t.StatusMeta;
            t.StatusMeta = StatusMeta.Excluido;
            t.StatusAlteradoEm = agora;
            t.SincronizadoEm = agora;
            await _templates.AtualizarAsync(t);
            await Evento(t, "nao_encontrado_na_meta", usuarioId, new { de = anterior });
        }

        /// <summary>Copia da Meta status, categoria, motivo de rejeicao, qualidade e componentes. Devolve se o status mudou.</summary>
        private async Task<bool> AplicarAsync(TemplateWhatsapp t, MetaTemplate remoto, DateTime agora, int? usuarioId, bool forcarGravacao = false)
        {
            var status = remoto.Status?.ToUpperInvariant();
            var motivo = string.IsNullOrWhiteSpace(remoto.MotivoRejeicao) || remoto.MotivoRejeicao.Equals("NONE", StringComparison.OrdinalIgnoreCase)
                ? null : remoto.MotivoRejeicao.ToUpperInvariant();
            var mudou = status != t.StatusMeta;
            var anterior = t.StatusMeta;

            t.StatusMeta = status;
            t.MotivoRejeicao = motivo;
            t.CategoriaMeta = remoto.Categoria?.ToUpperInvariant() ?? t.CategoriaMeta;
            t.Qualidade = remoto.Qualidade;
            t.ComponentesMetaJson = remoto.Componentes?.GetRawText() ?? t.ComponentesMetaJson;
            t.SincronizadoEm = agora;
            if (mudou) t.StatusAlteradoEm = agora;

            await _templates.AtualizarAsync(t);
            if (mudou && !forcarGravacao) await Evento(t, "status", usuarioId, new { de = anterior, para = status, motivo });
            return mudou;
        }

        private bool Expirado(TemplateWhatsapp t) => Agora() - (t.EnviadoEm ?? t.CriadoEm) > EnvioExpira;

        // ---- WABA, token e permissoes -----------------------------------------------------------------------

        /// <summary>Token e WABA do numero, ja confirmados na Meta (o numero esta na WABA). Nada disso vem do navegador.</summary>
        private async Task<Contexto> ResolverAsync(CanalWhatsapp canal)
        {
            if (canal.Status == StatusCanal.Inativo) throw TemplateErros.NumeroInativo();

            var (token, origemToken) = CanalToken.Resolver(canal, _token, _tokenGlobal);
            if (string.IsNullOrWhiteSpace(token)) throw TemplateErros.SemToken(origemToken == CanalToken.Global);

            var candidatas = Candidatas(canal);
            var chave = $"wa.tpl.waba:{canal.Id}:{canal.PhoneNumberId}:{canal.WabaId}:{origemToken}";
            if (_cache.TryGetValue(chave, out string? confirmada) && confirmada != null && candidatas.Contains(confirmada))
                return new Contexto(canal, confirmada, token!);

            foreach (var waba in candidatas)
            {
                var numeros = await _meta.ListarNumerosAsync(waba, token!);
                if (!numeros.Ok)
                {
                    // WABA errada ou sem acesso: tenta a proxima autorizada; outros erros (token, Meta fora) param aqui.
                    if (numeros.Falha is { SemResposta: false, Codigo: 100 }) continue;
                    throw TemplateErros.DaMeta(numeros.Falha!);
                }

                if (numeros.Valor!.Contains(canal.PhoneNumberId))
                {
                    _cache.Set(chave, waba, CacheWaba);
                    _cache.Set(ChaveWabaConhecida(canal), waba, CacheWaba);
                    return new Contexto(canal, waba, token!);
                }
            }

            _logger.LogWarning("[wa.tpl] ev=waba_nao_confirmada canal={Canal} loja={Loja} origem={Origem} candidatas={Candidatas}",
                canal.Id, canal.IdEstabelecimento, canal.Origem, candidatas.Count);
            throw TemplateErros.WabaIncorreta();
        }

        /// <summary>
        /// WABAs em que o numero pode estar. Proprio: so a da conexao. Zippy/central: as autorizadas no servidor; se o canal tem uma
        /// WABA gravada, ela precisa estar entre as autorizadas (ou o canal tem token proprio, e ai a WABA e da conta dele).
        /// </summary>
        private IReadOnlyList<string> Candidatas(CanalWhatsapp canal)
        {
            var local = canal.WabaId?.Trim();
            if (canal.Origem == OrigemCanal.Proprio)
            {
                if (string.IsNullOrEmpty(local)) throw new WhatsappException(WhatsappErros.WabaFormato());
                return new[] { local };
            }

            var autorizadas = WabasAutorizadas();
            if (!string.IsNullOrEmpty(local))
            {
                if (canal.TemToken || autorizadas.Contains(local)) return new[] { local };
                throw TemplateErros.WabaIncorreta();
            }

            if (autorizadas.Count == 0) throw TemplateErros.WabaNaoConfigurada();
            return autorizadas;
        }

        private IReadOnlyList<string> WabasAutorizadas() =>
            new[] { _configuration["WhatsApp:WabaIds"], _configuration["WhatsApp:WabaId"] }
                .SelectMany(v => (v ?? string.Empty).Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                .Select(v => v.Trim())
                .Where(v => v.Length > 0 && v.All(char.IsAsciiDigit) && !v.StartsWith("__", StringComparison.Ordinal))
                .Distinct()
                .ToList();

        /// <summary>WABA ja sabida sem chamar a Meta: a da conexao propria ou a ultima confirmada para este numero.</summary>
        private string? WabaConhecida(CanalWhatsapp canal)
        {
            if (canal.Origem == OrigemCanal.Proprio && !string.IsNullOrWhiteSpace(canal.WabaId)) return canal.WabaId.Trim();
            return _cache.TryGetValue(ChaveWabaConhecida(canal), out string? waba) ? waba : null;
        }

        private static string ChaveWabaConhecida(CanalWhatsapp canal) => $"wa.tpl.waba.conhecida:{canal.Id}:{canal.PhoneNumberId}";

        private async Task<CanalWhatsapp> CanalDaLojaAsync(Guid estabelecimentoId, Guid canalId)
        {
            var canal = await _canais.ObterAsync(canalId);
            // Numero de outra loja responde como inexistente (nao confirma que existe).
            if (canal == null || canal.IdEstabelecimento != estabelecimentoId) throw new KeyNotFoundException("Numero de WhatsApp nao encontrado.");
            return canal;
        }

        private async Task<TemplateWhatsapp> TemplateDaLojaAsync(Guid estabelecimentoId, Guid templateId)
        {
            var t = await _templates.ObterAsync(templateId);
            if (t == null || t.IdEstabelecimento != estabelecimentoId) throw new KeyNotFoundException("Modelo nao encontrado.");
            return t;
        }

        private static void ExigirMesmaWaba(TemplateWhatsapp t, Contexto ctx)
        {
            if (t.WabaId != ctx.Waba) throw new KeyNotFoundException("Modelo nao encontrado neste numero.");
        }

        /// <summary>
        /// Criar, sincronizar e excluir: a Gestao ou quem configura o WhatsApp da propria loja. No numero central de testes (WABA de
        /// todas as lojas de teste, sem como isolar o efeito), so a Gestao.
        /// </summary>
        private void ExigirGerenciar(AtendimentoAtor ator, Guid estabelecimentoId, CanalWhatsapp canal, string acao)
        {
            if (PodeGerenciar(ator, estabelecimentoId, canal)) return;

            _logger.LogWarning("[wa.tpl] ev=negado acao={Acao} loja={Loja} canal={Canal} usuario={Usuario} central={Central}",
                acao, estabelecimentoId, canal.Id, ator.UsuarioId, EhCentral(canal));
            throw new UnauthorizedAccessException(EhCentral(canal)
                ? "Somente a Gestao gerencia modelos do numero central de testes."
                : "Voce nao tem permissao para gerenciar os modelos de WhatsApp desta loja.");
        }

        private static bool PodeGerenciar(AtendimentoAtor ator, Guid estabelecimentoId, CanalWhatsapp canal)
        {
            if (ator.Gestao) return true;
            if (EhCentral(canal)) return false;
            return ator.EstabelecimentoId == estabelecimentoId && ator.TemPermissao("WhatsApp", "configurar");
        }

        private static bool EhCentral(CanalWhatsapp canal) => canal.Central || canal.Origem == OrigemCanal.Central;

        // ---- visoes ----------------------------------------------------------------------------------------

        private async Task<TemplatesListaView> MontarListaAsync(AtendimentoAtor ator, CanalWhatsapp canal, string? waba)
        {
            var itens = (await _templates.ListarAsync(canal.IdEstabelecimento, canal.Id, waba)).Select(t => Ver(t)).ToList();
            var ativos = itens.Where(i => i.Status != StatusTemplate.Excluido).ToList();
            var resumo = new ResumoTemplatesView(
                ativos.Count,
                ativos.Count(i => i.Status == StatusTemplate.Aprovado),
                ativos.Count(i => i.Status is StatusTemplate.EmAnalise or StatusTemplate.Enviando or StatusTemplate.Verificando),
                ativos.Count(i => i.Status == StatusTemplate.Rejeitado),
                ativos.Count(i => i.Status is not (StatusTemplate.Aprovado or StatusTemplate.EmAnalise or StatusTemplate.Enviando
                    or StatusTemplate.Verificando or StatusTemplate.Rejeitado)));
            return new TemplatesListaView(canal.Id, canal.Origem, PodeGerenciar(ator, canal.IdEstabelecimento, canal), itens, resumo,
                itens.Max(i => i.SincronizadoEm));
        }

        private async Task<TemplateView> VerDetalheAsync(TemplateWhatsapp t)
        {
            var eventos = (await _templates.ListarEventosAsync(t.Id)).Select(e => new TemplateEventoView(e.Acao, e.UsuarioId, e.CriadoEm, Elemento(e.DetalheJson))).ToList();
            return Ver(t, eventos, Elemento(t.ComponentesMetaJson));
        }

        internal static TemplateView Ver(TemplateWhatsapp t, IReadOnlyList<TemplateEventoView>? historico = null, JsonElement? componentesMeta = null)
        {
            var conteudo = JsonSerializer.Deserialize<TemplateConteudo>(t.ConteudoJson, Json) ?? new TemplateConteudo(null, null, null, null, null, null);
            var status = StatusTemplate.De(t.Situacao, t.StatusMeta);
            return new TemplateView(
                t.Id, t.IdEstabelecimento, t.IdCanal, t.Nome, t.Idioma, t.Categoria, t.CategoriaMeta, conteudo, status, t.Situacao, t.StatusMeta,
                DisponivelParaEnvio: t.Situacao == SituacaoTemplate.Enviado && t.StatusMeta == StatusMeta.Aprovado,
                t.MotivoRejeicao, Orientacao(status, t.MotivoRejeicao), t.MetaTemplateId, t.Qualidade, t.UltimoErro, t.UltimoErroCodigo,
                t.CriadoPor, t.CriadoEm, t.EnviadoEm, t.StatusAlteradoEm, t.SincronizadoEm, t.ExcluidoEm, historico, componentesMeta);
        }

        private static string? Orientacao(string status, string? motivo) => status switch
        {
            StatusTemplate.Rejeitado => TemplateRegras.OrientacaoRejeicao(motivo)
                ?? "A Meta recusou o modelo. Revise o texto e os exemplos e crie uma nova versao com outro nome.",
            StatusTemplate.Pausado => "A Meta pausou o modelo por baixa qualidade (bloqueios ou denuncias). Revise o conteudo; ele volta sozinho ou pode ser recriado.",
            StatusTemplate.Desativado => "A Meta desativou o modelo. Crie uma nova versao com conteudo revisado.",
            StatusTemplate.Indisponivel => "O modelo nao esta disponivel na Meta. Crie uma nova versao se precisar dele.",
            StatusTemplate.Verificando => "A Meta nao confirmou o envio. Use Sincronizar para conferir; nao envie de novo antes disso.",
            StatusTemplate.Falha => "O modelo nao foi criado na Meta. Corrija o que foi indicado e envie de novo.",
            _ => null
        };

        private static JsonElement? Elemento(string? json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            try
            {
                using var doc = JsonDocument.Parse(json);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private Task Evento(TemplateWhatsapp t, string acao, int? usuarioId, object? detalhe) =>
            _templates.RegistrarEventoAsync(t.Id, acao, usuarioId, detalhe == null ? null : JsonSerializer.Serialize(detalhe, Json));
    }
}
