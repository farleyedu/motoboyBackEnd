using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using APIBack.Atendimento;
using APIBack.Atendimento.ConexaoMeta;
using APIBack.Atendimento.Templates;
using APIBack.Automation.Dtos;
using APIBack.Automation.Interfaces;
using APIBack.Automation.Services;
using APIBack.Service;
using APIBack.Service.Interface;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace APIBack.Tests.Unit
{
    /// <summary>Dados ficticios do Embedded Signup (nenhum ID real de producao).</summary>
    internal static class Es
    {
        public const string AppId = "900000000000001";
        public const string ConfigId = "900000000000002";
        public const string AppSecret = "segredo-do-app-zippygo-0123456789";
        public const string Waba = "700000000000001";
        public const string OutraWaba = "700000000000002";
        public const string Business = "600000000000001";
        public const string Phone = "800000000000001";
        public const string Phone2 = "800000000000002";
        public const string Code = "CODIGO-DO-EMBEDDED-SIGNUP-abc";
        public const string Token = "EAAG-token-de-integracao-do-cliente";
        public static readonly Guid Loja = Guid.NewGuid();
        public static readonly Guid OutraLoja = Guid.NewGuid();

        public static AtendimentoAtor Dono(Guid? loja = null, int usuario = 2) =>
            new(usuario, false, loja ?? Loja, (m, a) => m == "WhatsApp" && a == "configurar");

        public static IConfiguration Config(string? wabasZippy = null) =>
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Atendimento:ChaveToken"] = "chave-de-teste", ["WhatsApp:WabaIds"] = wabasZippy
            }).Build();

        /// <summary>O app ZippyGo como a Gestao cadastra na tela (o App Secret vai cifrado).</summary>
        public static AppMetaConfig App(ITokenProtector p, bool aprovado = true) => new()
        {
            AppId = AppId, ConfigurationId = ConfigId, AppSecretCifrado = p.Proteger(AppSecret), AppReviewAprovado = aprovado, BusinessIdIntegrador = "600000000000999"
        };

        public static NumeroMeta Numero(string id = Phone, string telefone = "+55 34 99821-0001", string? estado = "PENDING",
            string? verificacao = "VERIFIED", bool? noApp = null) => new(id, telefone, "Pizzaria Teste", estado, verificacao, "NOT_APPLICABLE", noApp);

        public static TokenInfo Info(string? app = AppId, string[]? escopos = null, string[]? wabas = null, bool valido = true) =>
            new(valido, app, null, escopos ?? new[] { "whatsapp_business_management", "whatsapp_business_messaging" }, wabas ?? new[] { Waba },
                wabas ?? new[] { Waba });
    }

    /// <summary>Canais em memoria (unicidade de Phone Number ID e telefone como no banco).</summary>
    internal sealed class CanaisEmMemoria : ICanalRepository
    {
        public readonly Dictionary<Guid, CanalWhatsapp> Itens = new();
        public readonly List<(Guid? Canal, string Acao, string? Detalhe)> Auditoria = new();
        public Func<Guid?, AutorizacaoMeta?>? Autorizacao { get; set; }

        private CanalWhatsapp? Copia(CanalWhatsapp? c)
        {
            if (c == null) return null;
            var json = JsonSerializer.Serialize(c);
            var copia = JsonSerializer.Deserialize<CanalWhatsapp>(json)!;
            var a = Autorizacao?.Invoke(c.IdAutorizacaoMeta);
            copia.TokenAutorizacaoCifrado = a?.Estado == EstadoAutorizacao.Ativa ? a.TokenCifrado : null;
            copia.AutorizacaoEstado = a?.Estado;
            return copia;
        }

        public Task<IReadOnlyList<CanalWhatsapp>> ListarPorLojaAsync(Guid loja) =>
            Task.FromResult<IReadOnlyList<CanalWhatsapp>>(Itens.Values.Where(c => c.IdEstabelecimento == loja).Select(c => Copia(c)!).ToList());
        public Task<CanalWhatsapp?> ObterAsync(Guid id) => Task.FromResult(Copia(Itens.GetValueOrDefault(id)));
        public Task<CanalWhatsapp?> ObterAtivoPorPhoneNumberIdAsync(string p) =>
            Task.FromResult(Copia(Itens.Values.FirstOrDefault(c => c.PhoneNumberId == p && c.Status != StatusCanal.Inativo)));
        public Task<CanalWhatsapp?> ObterPorNumeroAsync(string d) => Task.FromResult(Copia(Itens.Values.FirstOrDefault(c => c.NumeroE164 == "+" + d)));
        public Task<CanalWhatsapp?> ObterPorPhoneNumberIdAsync(string p) => Task.FromResult(Copia(Itens.Values.FirstOrDefault(c => c.PhoneNumberId == p)));
        public Task<CanalWhatsapp?> ObterPorWebhookChaveAsync(string chave) => Task.FromResult<CanalWhatsapp?>(null);
        public Task GravarProprioAsync(CanalWhatsapp canal) { Itens[canal.Id] = Copia(canal)!; return Task.CompletedTask; }
        public Task DefinirWebhookAsync(Guid id, string estado, string? erro, DateTime? em) { Itens[id].WebhookEstado = estado; return Task.CompletedTask; }
        public Task<CanalWhatsapp?> ObterPrimeiroUsavelAsync(Guid loja) => Task.FromResult<CanalWhatsapp?>(null);
        public Task<CanalWhatsapp?> ObterParaServicoAsync(Guid loja, string s) => Task.FromResult<CanalWhatsapp?>(null);
        public Task<CanalWhatsapp?> ObterCentralAsync() => Task.FromResult(Copia(Itens.Values.FirstOrDefault(c => c.Central)));
        public Task DefinirCentralAsync(Guid id) => Task.CompletedTask;

        public Task CriarAsync(CanalWhatsapp canal, IReadOnlyCollection<string> servicos)
        {
            Unico(canal);
            Itens[canal.Id] = Copia(canal)!;
            return Task.CompletedTask;
        }

        public Task AtualizarDadosAsync(CanalWhatsapp canal) { Unico(canal); Itens[canal.Id] = Copia(canal)!; return Task.CompletedTask; }

        public Task GravarAutorizadoAsync(CanalWhatsapp canal)
        {
            Unico(canal);
            var c = Copia(canal)!;
            c.Origem = OrigemCanal.Autorizado;
            c.TokenCifrado = null;
            c.AppSecretCifrado = null;
            c.WebhookChave = null;
            c.DesconectadoEm = null;
            Itens[canal.Id] = c;
            return Task.CompletedTask;
        }

        private void Unico(CanalWhatsapp canal)
        {
            if (Itens.Values.Any(o => o.Id != canal.Id && o.PhoneNumberId == canal.PhoneNumberId))
                throw new CanalConflitoException("phoneNumberId", "duplicado");
            if (Itens.Values.Any(o => o.Id != canal.Id && o.NumeroE164 == canal.NumeroE164))
                throw new CanalConflitoException("numero", "duplicado");
        }

        public Task AtualizarModoAsync(Guid id, string modo) => Task.CompletedTask;
        public Task AtualizarConfigAsync(Guid id, string json) => Task.CompletedTask;
        public Task DefinirServicosAsync(Guid id, IReadOnlyCollection<string> s) => Task.CompletedTask;
        public Task MoverAsync(Guid id, Guid destino) => Task.CompletedTask;
        public Task RemoverAsync(Guid id) { Itens.Remove(id); return Task.CompletedTask; }

        public Task RegistrarAuditoriaAsync(Guid? canalId, string acao, Guid? origem, Guid? destino, int? usuario, string? detalhe)
        {
            Auditoria.Add((canalId, acao, detalhe));
            return Task.CompletedTask;
        }

        public Task MarcarRecebimentoAsync(Guid id) { Itens[id].UltimoRecebimentoEm = DateTime.UtcNow; return Task.CompletedTask; }
        public Task MarcarEnvioOkAsync(Guid id) => Task.CompletedTask;
        public Task MarcarErroAsync(Guid id, string erro, bool desativar)
        {
            Itens[id].UltimoErro = erro;
            if (desativar) Itens[id].Status = StatusCanal.Erro;
            return Task.CompletedTask;
        }

        public Task MarcarVerificadoAsync(Guid id, bool ok, string? erro)
        {
            var c = Itens[id];
            c.VerificadoEm = ok ? DateTime.UtcNow : c.VerificadoEm;
            if (!ok) c.Status = StatusCanal.Erro;
            return Task.CompletedTask;
        }

        public Task DesativarAsync(Guid id)
        {
            Itens[id].Status = StatusCanal.Inativo;
            Itens[id].DesconectadoEm = DateTime.UtcNow;
            Itens[id].TokenCifrado = null;
            return Task.CompletedTask;
        }

        public Task<int> ContarCanaisDaWabaAsync(string waba, Guid exceto) =>
            Task.FromResult(Itens.Values.Count(c => c.WabaId == waba && c.Id != exceto && c.Status != StatusCanal.Inativo));
    }

    internal sealed class ConexoesEmMemoria : IConexaoMetaRepository
    {
        public readonly Dictionary<Guid, TentativaConexao> Tentativas = new();
        public readonly Dictionary<Guid, AutorizacaoMeta> Autorizacoes = new();
        public CanaisEmMemoria? Canais { get; set; }
        public bool RecusarReserva { get; set; }
        public AppMetaConfig? App { get; set; }

        private static T Copia<T>(T v) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v))!;

        public Task<bool> CriarTentativaAsync(TentativaConexao t)
        {
            if (Tentativas.Values.Any(o => o.IdEstabelecimento == t.IdEstabelecimento && o.Estado is "trocando" or "ativando")) return Task.FromResult(false);
            foreach (var o in Tentativas.Values.Where(o => o.IdEstabelecimento == t.IdEstabelecimento && o.Estado is "iniciada" or "autorizada" or "erro"))
                o.Estado = EstadoTentativa.Substituida;
            Tentativas[t.Id] = Copia(t);
            return Task.FromResult(true);
        }

        public Task<TentativaConexao?> ObterTentativaAsync(Guid id) =>
            Task.FromResult(Tentativas.TryGetValue(id, out var t) ? Copia(t) : null);
        public Task<TentativaConexao?> UltimaTentativaAsync(Guid loja) =>
            Task.FromResult(Tentativas.Values.Where(t => t.IdEstabelecimento == loja).Select(Copia).LastOrDefault());

        public Task<bool> ReservarParaTrocaAsync(Guid id)
        {
            if (RecusarReserva || !Tentativas.TryGetValue(id, out var t) || t.Estado != EstadoTentativa.Iniciada) return Task.FromResult(false);
            t.Estado = EstadoTentativa.Trocando;
            return Task.FromResult(true);
        }

        public Task<bool> ReservarParaAtivacaoAsync(Guid id)
        {
            if (!Tentativas.TryGetValue(id, out var t) || t.IdAutorizacao == null || t.Estado is not ("autorizada" or "erro")) return Task.FromResult(false);
            t.Estado = EstadoTentativa.Ativando;
            return Task.FromResult(true);
        }

        public Task AtualizarTentativaAsync(TentativaConexao t) { Tentativas[t.Id] = Copia(t); return Task.CompletedTask; }

        public Task<Guid> GravarAutorizacaoAsync(AutorizacaoMeta a)
        {
            var existente = Autorizacoes.Values.FirstOrDefault(o => o.IdEstabelecimento == a.IdEstabelecimento && o.AppId == a.AppId && o.WabaId == a.WabaId);
            var id = existente?.Id ?? a.Id;
            var nova = Copia(a);
            nova.Id = id;
            nova.Estado = EstadoAutorizacao.Ativa;
            Autorizacoes[id] = nova;
            return Task.FromResult(id);
        }

        public Task<AutorizacaoMeta?> ObterAutorizacaoAsync(Guid id) => Task.FromResult(Autorizacoes.TryGetValue(id, out var a) ? Copia(a) : null);
        public Task MarcarAutorizacaoAsync(Guid id, string estado, string? erro) { Autorizacoes[id].Estado = estado; return Task.CompletedTask; }
        public Task MarcarAutorizacaoValidadaAsync(Guid id, DateTime? expira, IReadOnlyCollection<string> p) => Task.CompletedTask;
        public Task<int> ContarOutrasAutorizacoesAtivasAsync(string waba, Guid exceto) =>
            Task.FromResult(Autorizacoes.Values.Count(a => a.WabaId == waba && a.Id != exceto && a.Estado == EstadoAutorizacao.Ativa));
        public Task<int> ContarCanaisDaAutorizacaoAsync(Guid id, Guid exceto) =>
            Task.FromResult(Canais?.Itens.Values.Count(c => c.IdAutorizacaoMeta == id && c.Id != exceto && c.Status != StatusCanal.Inativo) ?? 0);

        public Task<AppMetaConfig?> ObterAppAsync() => Task.FromResult(App == null ? null : Copia(App));
        public Task SalvarAppAsync(AppMetaConfig app) { App = Copia(app); return Task.CompletedTask; }

        public Task<int> RevogarPorWabaAsync(string waba, string? app, string motivo)
        {
            var ids = Autorizacoes.Values.Where(a => a.WabaId == waba).Select(a => a.Id).ToList();
            foreach (var id in ids) Autorizacoes[id].Estado = EstadoAutorizacao.Revogada;
            return Task.FromResult(ids.Count);
        }
    }

    /// <summary>Guarda tudo o que foi logado, para provar que token, codigo e App Secret nunca aparecem.</summary>
    internal sealed class LogCaptura<T> : ILogger<T>
    {
        public readonly ConcurrentQueue<string> Linhas = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f) => Linhas.Enqueue(f(s, ex) + ex);
    }

    public class ConexaoMetaRegrasTests
    {
        [Fact]
        public void State_so_confere_com_o_valor_original_e_nunca_vazio()
        {
            var state = ConexaoMetaRegras.GerarState();
            var hash = ConexaoMetaRegras.HashState(state);

            Assert.NotEqual(state, hash);
            Assert.True(ConexaoMetaRegras.StateConfere(hash, state));
            Assert.False(ConexaoMetaRegras.StateConfere(hash, state + "x"));
            Assert.False(ConexaoMetaRegras.StateConfere(hash, null));
            Assert.False(ConexaoMetaRegras.StateConfere(hash, ""));
        }

        [Fact]
        public void Waba_vem_do_token_e_o_navegador_so_escolhe_entre_as_autorizadas()
        {
            Assert.Equal((Es.Waba, false), Pick(ConexaoMetaRegras.EscolherWaba(new[] { Es.Waba }, null)));
            Assert.Equal((Es.Waba, false), Pick(ConexaoMetaRegras.EscolherWaba(new[] { Es.Waba, Es.OutraWaba }, Es.Waba)));
            Assert.Equal(ConexaoMetaCodigos.WabaNaoAutorizada, ConexaoMetaRegras.EscolherWaba(new[] { Es.Waba }, Es.OutraWaba).Erro!.Codigo);
            Assert.Equal(ConexaoMetaCodigos.WabaNaoAutorizada, ConexaoMetaRegras.EscolherWaba(new[] { Es.Waba, Es.OutraWaba }, null).Erro!.Codigo);
            // Sem granular_scopes: candidato do navegador, mas o servico precisa confirmar na Meta.
            Assert.Equal((Es.Waba, true), Pick(ConexaoMetaRegras.EscolherWaba(Array.Empty<string>(), Es.Waba)));
            Assert.Equal(ConexaoMetaCodigos.AutorizacaoIncompleta, ConexaoMetaRegras.EscolherWaba(Array.Empty<string>(), null).Erro!.Codigo);
            Assert.NotNull(ConexaoMetaRegras.EscolherWaba(Array.Empty<string>(), "123; DROP").Erro);

            static (string?, bool) Pick((string? W, bool C, WhatsappErro? E) r) => (r.W, r.C);
        }

        [Fact]
        public void Numero_de_outra_loja_aparece_mas_nunca_e_selecionavel_nem_escolhido_sozinho()
        {
            var outra = new CanalWhatsapp { IdEstabelecimento = Es.OutraLoja };
            var desta = new CanalWhatsapp { IdEstabelecimento = Es.Loja };

            var a = ConexaoMetaRegras.Classificar(Es.Numero(Es.Phone), outra, Es.Loja);
            var b = ConexaoMetaRegras.Classificar(Es.Numero(Es.Phone2, "+55 34 99821-0002"), desta, Es.Loja);
            var c = ConexaoMetaRegras.Classificar(Es.Numero("800000000000003", "+55 34 99821-0003"), null, Es.Loja);

            Assert.Equal(SituacaoNumero.OutraLoja, a.Situacao);
            Assert.False(a.Selecionavel);
            Assert.True(b.Selecionavel);
            Assert.True(c.Selecionavel);
            // Dois selecionaveis: pergunta. Indicado pelo navegador mas de outra loja: nao escolhe.
            Assert.Null(ConexaoMetaRegras.EscolherNumeroAutomatico(new[] { a, b, c }, null, null));
            Assert.Null(ConexaoMetaRegras.EscolherNumeroAutomatico(new[] { a }, Es.Phone, null));
            Assert.Equal(Es.Phone2, ConexaoMetaRegras.EscolherNumeroAutomatico(new[] { a, b, c }, Es.Phone2, null));
            Assert.Equal(Es.Phone2, ConexaoMetaRegras.EscolherNumeroAutomatico(new[] { a, b }, null, null));
            Assert.Null(ConexaoMetaRegras.EscolherNumeroAutomatico(new[] { a, b, c }, Es.Phone2, Es.Phone));
        }

        [Theory]
        [InlineData("CONNECTED", "VERIFIED", false, null, DecisaoRegistro.JaRegistrado)]
        [InlineData("PENDING", "VERIFIED", false, null, DecisaoRegistro.Registrar)]
        [InlineData("PENDING", "NOT_VERIFIED", false, null, DecisaoRegistro.NaoVerificado)]
        [InlineData("PENDING", "VERIFIED", true, null, DecisaoRegistro.Dispensado)]
        [InlineData("CONNECTED", "VERIFIED", false, true, DecisaoRegistro.Dispensado)]
        [InlineData("FLAGGED", "VERIFIED", false, null, DecisaoRegistro.RegistradoComRestricao)]
        [InlineData("BANNED", "VERIFIED", false, null, DecisaoRegistro.Bloqueado)]
        public void Registro_so_quando_necessario_e_nunca_na_coexistencia(string estado, string verificacao, bool coexistencia, bool? noApp, DecisaoRegistro esperado)
        {
            Assert.Equal(esperado, ConexaoMetaRegras.DecidirRegistro(Es.Numero(estado: estado, verificacao: verificacao, noApp: noApp), coexistencia));
        }

        [Fact]
        public void Registro_respeita_o_limite_de_tentativas_na_janela()
        {
            var agora = DateTime.UtcNow;
            Assert.True(ConexaoMetaRegras.PodeTentarRegistro(0, null, agora));
            Assert.True(ConexaoMetaRegras.PodeTentarRegistro(2, agora.AddHours(-1), agora));
            Assert.False(ConexaoMetaRegras.PodeTentarRegistro(3, agora.AddHours(-1), agora));
            Assert.True(ConexaoMetaRegras.PodeTentarRegistro(3, agora.AddHours(-73), agora));
            Assert.Equal(1, ConexaoMetaRegras.ContarTentativa(3, agora.AddHours(-73), agora));
        }

        [Fact]
        public void Resultado_parcial_nunca_vira_conectado()
        {
            var ok = ConexaoMetaRegras.Ok(EtapaConexao.Autorizacao, "ok");
            var pendente = new EtapaConexaoView(EtapaConexao.Recebimento, EstadoEtapa.Pendente, "aguardando");
            var erro = new EtapaConexaoView(EtapaConexao.Registro, EstadoEtapa.Erro, "falhou");

            Assert.Equal(EstadoConexao.Conectado, ConexaoMetaRegras.EstadoGeral(new[] { ok }));
            Assert.Equal(EstadoConexao.Configurando, ConexaoMetaRegras.EstadoGeral(new[] { ok, pendente }));
            Assert.Equal(EstadoConexao.Erro, ConexaoMetaRegras.EstadoGeral(new[] { ok, pendente, erro }));
            Assert.Equal(EstadoConexao.Revogado, ConexaoMetaRegras.EstadoGeral(new[] { ok }, revogado: true));
            Assert.Equal(EstadoConexao.AguardandoAutorizacao, ConexaoMetaRegras.EstadoGeral(Array.Empty<EtapaConexaoView>()));
        }

        [Fact]
        public void Debug_token_le_permissoes_e_wabas_autorizadas_dos_granular_scopes()
        {
            using var doc = JsonDocument.Parse(@"{""app_id"":""900000000000001"",""is_valid"":true,""expires_at"":0,
                ""scopes"":[""whatsapp_business_management"",""whatsapp_business_messaging""],
                ""granular_scopes"":[{""scope"":""whatsapp_business_management"",""target_ids"":[""700000000000001""]},
                                     {""scope"":""whatsapp_business_messaging"",""target_ids"":[""700000000000001""]}]}");

            var info = MetaOnboardingApi.LerTokenInfo(doc.RootElement);

            Assert.True(info.Valido);
            Assert.Null(info.ExpiraEm);
            Assert.Equal(new[] { Es.Waba }, info.WabasGerenciaveis);
            Assert.Empty(ConexaoMetaRegras.PermissoesFaltando(info.Permissoes));
            Assert.Equal(new[] { "whatsapp_business_messaging" }, ConexaoMetaRegras.PermissoesFaltando(new[] { "whatsapp_business_management" }));
        }

        [Fact]
        public void Erros_da_meta_viram_etapas_com_responsavel_e_acao()
        {
            var pin = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Registro, new MetaFalha(false, 400, 133005, null, "PIN mismatch", null));
            var token = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Inscricao, new MetaFalha(false, 401, 190, null, "expired", null));
            var rede = ConexaoMetaRegras.EtapaDaFalha(EtapaConexao.Numero, new MetaFalha(true, null, null, null, null, null));

            Assert.Equal(("informar_pin", ResponsavelPendencia.Loja, EstadoEtapa.Pendente), (pin.Acao, pin.Responsavel, pin.Estado));
            Assert.Equal(("reconectar", ConexaoMetaCodigos.TokenRevogado), (token.Acao, token.CodigoErro));
            Assert.Equal((ResponsavelPendencia.Meta, EstadoEtapa.Atencao), (rede.Responsavel, rede.Estado));
        }

        [Fact]
        public void Sem_cadastro_na_tela_fica_indisponivel_e_lista_as_pendencias_sem_inventar_valores()
        {
            var cfg = ConfigEmbeddedSignup.De(null, Es.Config(), WaTeste.Protetor());

            Assert.False(cfg.Configurado);
            Assert.Null(cfg.AppId);
            Assert.False(cfg.PermissoesAprovadas);
            Assert.Contains(cfg.Pendencias(), p => p.Contains("Configuration ID"));
            Assert.Contains(cfg.Pendencias(), p => p.Contains("aprovou"));
        }

        [Fact]
        public void App_secret_vazio_na_tela_usa_o_do_webhook_global()
        {
            var p = WaTeste.Protetor();
            var servidor = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Automation:Meta:AppSecret"] = "segredo-do-webhook-global-123" }).Build();

            var semNaTela = ConfigEmbeddedSignup.De(new AppMetaConfig { AppId = Es.AppId, ConfigurationId = Es.ConfigId }, servidor, p);
            var naTela = ConfigEmbeddedSignup.De(Es.App(p), servidor, p);

            Assert.Equal(("segredo-do-webhook-global-123", "servidor"), (semNaTela.AppSecret, semNaTela.AppSecretOrigem));
            Assert.True(semNaTela.Configurado);
            Assert.Equal((Es.AppSecret, "tela"), (naTela.AppSecret, naTela.AppSecretOrigem));
        }
    }

    public class ConexaoMetaServiceTests
    {
        private sealed class Fixture
        {
            public CanaisEmMemoria Canais { get; } = new();
            public ConexoesEmMemoria Repo { get; } = new();
            public Mock<IMetaOnboardingApi> Meta { get; } = new();
            public Mock<ICatalogoRepository> Catalogo { get; } = new();
            public Mock<ICanaisWhatsappService> CanaisService { get; } = new();
            public ITokenProtector Protetor { get; } = WaTeste.Protetor();
            public LogCaptura<ConexaoMetaService> Log { get; } = new();
            public IConfiguration Config { get; set; } = Es.Config();
            public List<string> Registros { get; } = new();

            public Fixture()
            {
                Repo.Canais = Canais;
                Repo.App = Es.App(Protetor);
                Canais.Autorizacao = id => id.HasValue && Repo.Autorizacoes.TryGetValue(id.Value, out var a) ? a : null;
                Catalogo.Setup(c => c.LojaExisteAsync(It.IsAny<Guid>())).ReturnsAsync(true);
                Catalogo.Setup(c => c.ListarServicosDaLojaAsync(It.IsAny<Guid>())).ReturnsAsync(new[] { new ServicoDaLoja { Codigo = "delivery", Ativo = true } });

                Meta.Setup(m => m.TrocarCodigoAsync(Es.AppId, Es.AppSecret, Es.Code)).ReturnsAsync(MetaResultado<TokenTrocado>.Sucesso(new TokenTrocado(Es.Token, null)));
                Meta.Setup(m => m.DepurarTokenAsync(Es.AppId, Es.AppSecret, Es.Token)).ReturnsAsync(MetaResultado<TokenInfo>.Sucesso(Es.Info()));
                Meta.Setup(m => m.ObterWabaAsync(It.IsAny<string>(), Es.Token)).ReturnsAsync((string w, string _) => MetaResultado<WabaInfo>.Sucesso(new WabaInfo(w, "Conta", Es.Business)));
                Numeros(Es.Numero());
                Meta.Setup(m => m.AppsInscritosAsync(Es.Waba, Es.Token)).ReturnsAsync(MetaResultado<IReadOnlyList<string>>.Sucesso(Array.Empty<string>()));
                Meta.Setup(m => m.InscreverAppAsync(Es.Waba, Es.Token)).ReturnsAsync(MetaResultado<bool>.Sucesso(true));
                Meta.Setup(m => m.RegistrarNumeroAsync(It.IsAny<string>(), Es.Token, It.IsAny<string>()))
                    .Callback<string, string, string>((_, _, pin) => Registros.Add(pin)).ReturnsAsync(MetaResultado<bool>.Sucesso(true));
                Meta.Setup(m => m.SincronizarAppBusinessAsync(It.IsAny<string>(), Es.Token, It.IsAny<string>())).ReturnsAsync(MetaResultado<bool>.Sucesso(true));
                Meta.Setup(m => m.DesinscreverAppAsync(It.IsAny<string>(), Es.Token)).ReturnsAsync(MetaResultado<bool>.Sucesso(true));
                Meta.Setup(m => m.ObterAppAsync(It.IsAny<string>(), It.IsAny<string>()))
                    .ReturnsAsync((string app, string segredo) => app == Es.AppId && segredo == Es.AppSecret
                        ? MetaResultado<string>.Sucesso("ZippyGo") : MetaResultado<string>.Erro(new MetaFalha(false, 400, 190, null, "Invalid OAuth access token", null)));
                Meta.Setup(m => m.AssinaturasDoAppAsync(Es.AppId, Es.AppSecret)).ReturnsAsync(MetaResultado<IReadOnlyList<AssinaturaApp>>.Sucesso(new[]
                {
                    new AssinaturaApp("whatsapp_business_account", true, new[] { "messages", "account_update", "smb_message_echoes", "history", "smb_app_state_sync" })
                }));
            }

            public void Numeros(params NumeroMeta[] numeros)
            {
                Meta.Setup(m => m.ListarNumerosAsync(Es.Waba, Es.Token)).ReturnsAsync(MetaResultado<IReadOnlyList<NumeroMeta>>.Sucesso(numeros));
                foreach (var n in numeros) Meta.Setup(m => m.ObterNumeroAsync(n.Id, Es.Token)).ReturnsAsync(MetaResultado<NumeroMeta>.Sucesso(n));
            }

            public ConexaoMetaService Build()
            {
                var webhook = new CanalWebhookService(Canais, Protetor, new Mock<IWebhookRegistrador>().Object, Config, NullLogger<CanalWebhookService>.Instance);
                return new ConexaoMetaService(Canais, Catalogo.Object, Repo, Meta.Object, Protetor, CanaisService.Object, webhook, Config, Log);
            }

            public async Task<(ConexaoMetaService S, TentativaIniciadaView T)> IniciarAsync(Guid? canalId = null, bool coexistencia = false)
            {
                var s = Build();
                return (s, await s.IniciarAsync(Es.Dono(), Es.Loja, new IniciarConexaoMetaRequest(canalId, coexistencia)));
            }

            public static ConcluirConexaoMetaRequest Fim(TentativaIniciadaView t, string evento = EventoFluxo.Finish, string? waba = Es.Waba,
                string? phone = Es.Phone) => new(t.TentativaId, t.State, evento, Es.Code, waba, phone, Es.Business);
        }

        [Fact]
        public async Task Configuracao_publica_nunca_expoe_o_app_secret()
        {
            var f = new Fixture();
            var view = await f.Build().ConfiguracaoAsync(Es.Dono(), Es.Loja);

            Assert.True(view.Disponivel);
            Assert.Equal("producao", view.Modo);
            Assert.Equal(Es.AppId, view.AppId);
            Assert.DoesNotContain(Es.AppSecret, JsonSerializer.Serialize(view));
        }

        [Fact]
        public async Task App_review_pendente_fica_em_modo_teste_e_sem_config_fica_indisponivel()
        {
            var teste = new Fixture();
            teste.Repo.App = Es.App(teste.Protetor, aprovado: false);
            var semConfig = new Fixture();
            semConfig.Repo.App = null;

            var a = await teste.Build().ConfiguracaoAsync(Es.Dono(), Es.Loja);
            var b = await semConfig.Build().ConfiguracaoAsync(Es.Dono(), Es.Loja);

            Assert.Equal("teste", a.Modo);
            Assert.NotEmpty(a.Pendencias);
            Assert.False(b.Disponivel);
            Assert.Null(b.AppId);
            var erro = await Assert.ThrowsAsync<WhatsappException>(() =>
                semConfig.Build().IniciarAsync(Es.Dono(), Es.Loja, new IniciarConexaoMetaRequest()));
            Assert.Equal((ConexaoMetaCodigos.Indisponivel, 503), (erro.Erro.Codigo, erro.Http));
        }

        [Fact]
        public async Task Iniciar_exige_permissao_e_guarda_so_o_hash_do_state()
        {
            var f = new Fixture();
            var s = f.Build();

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                s.IniciarAsync(new AtendimentoAtor(9, false, Es.Loja, (_, _) => false), Es.Loja, new IniciarConexaoMetaRequest()));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
                s.IniciarAsync(Es.Dono(Es.OutraLoja), Es.Loja, new IniciarConexaoMetaRequest()));

            var t = await s.IniciarAsync(Es.Dono(), Es.Loja, new IniciarConexaoMetaRequest());
            var gravada = f.Repo.Tentativas[t.TentativaId];
            Assert.NotEqual(t.State, gravada.StateHash);
            Assert.DoesNotContain(t.State, JsonSerializer.Serialize(gravada));
            Assert.Equal(Es.ConfigId, t.ConfigurationId);
        }

        [Fact]
        public async Task Fluxo_completo_conecta_sem_os_cinco_dados_e_nao_se_diz_pronto_antes_da_primeira_mensagem()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));

            var canal = Assert.Single(f.Canais.Itens.Values);
            Assert.Equal(OrigemCanal.Autorizado, canal.Origem);
            Assert.Equal((Es.Waba, Es.AppId, Es.Phone), (canal.WabaId, canal.AppId, canal.PhoneNumberId));
            Assert.Equal(StatusCanal.Ativo, canal.Status);
            Assert.Equal(RegistroEstado.Registrado, canal.RegistroEstado);
            Assert.Null(canal.TokenCifrado);
            Assert.Single(f.Registros);
            f.Meta.Verify(m => m.InscreverAppAsync(Es.Waba, Es.Token), Times.Once);

            var autorizacao = Assert.Single(f.Repo.Autorizacoes.Values);
            Assert.NotEqual(Es.Token, autorizacao.TokenCifrado);
            Assert.Equal(Es.Token, f.Protetor.Revelar(autorizacao.TokenCifrado));
            Assert.True(autorizacao.BusinessIdConfirmado);

            Assert.Equal(EstadoTentativa.Concluida, estado.EstadoTentativa);
            Assert.Equal(EstadoConexao.Configurando, estado.Estado);
            Assert.Contains(estado.Etapas, e => e.Codigo == EtapaConexao.Recebimento && e.Estado == EstadoEtapa.Pendente);

            var json = JsonSerializer.Serialize(estado);
            Assert.DoesNotContain(Es.Token, json);
            Assert.DoesNotContain(Es.Code, json);
            Assert.DoesNotContain(Es.AppSecret, json);
            Assert.All(f.Log.Linhas, l => Assert.DoesNotContain(Es.Token, l));
            Assert.All(f.Log.Linhas, l => Assert.DoesNotContain(Es.Code, l));
            Assert.All(f.Log.Linhas, l => Assert.DoesNotContain(Es.AppSecret, l));
            Assert.All(f.Canais.Auditoria, a => Assert.DoesNotContain(Es.Token, a.Detalhe ?? ""));
        }

        [Fact]
        public async Task Concluir_recusa_state_errado_outro_usuario_e_outra_loja()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();

            var state = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t) with { State = "forjado" }));
            var usuario = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.ConcluirAsync(Es.Dono(usuario: 77), Es.Loja, Fixture.Fim(t)));
            var loja = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.ConcluirAsync(Es.Dono(Es.OutraLoja), Es.OutraLoja, Fixture.Fim(t)));

            Assert.Equal(403, state.Http);
            Assert.Equal(403, usuario.Http);
            Assert.Equal(404, loja.Http);
            f.Meta.Verify(m => m.TrocarCodigoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Concluir_duas_vezes_nao_troca_o_codigo_de_novo()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));

            var replay = await Assert.ThrowsAsync<WhatsappException>(() => s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t)));

            Assert.Equal((ConexaoMetaCodigos.TentativaEmUso, 409), (replay.Erro.Codigo, replay.Http));
            f.Meta.Verify(m => m.TrocarCodigoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Concorrencia_na_troca_responde_em_uso()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            f.Repo.RecusarReserva = true;

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t)));

            Assert.Equal(ConexaoMetaCodigos.TentativaEmUso, erro.Erro.Codigo);
        }

        [Fact]
        public async Task Tentativa_expirada_e_recusada_e_marcada()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            f.Repo.Tentativas[t.TentativaId].ExpiraEm = DateTime.UtcNow.AddSeconds(-1);

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t)));

            Assert.Equal((ConexaoMetaCodigos.TentativaExpirada, 410), (erro.Erro.Codigo, erro.Http));
            Assert.Equal(EstadoTentativa.Expirada, f.Repo.Tentativas[t.TentativaId].Estado);
        }

        [Fact]
        public async Task Cancelar_na_meta_encerra_sem_chamar_a_meta_e_permite_nova_tentativa()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja,
                new ConcluirConexaoMetaRequest(t.TentativaId, t.State, EventoFluxo.Cancel, EtapaCancelada: "PHONE_NUMBER_SETUP"));

            Assert.Equal(EstadoTentativa.Cancelada, estado.EstadoTentativa);
            Assert.Equal(ConexaoMetaCodigos.FluxoCancelado, estado.ErroCodigo);
            Assert.Contains("conectar", estado.Acoes);
            Assert.Empty(f.Canais.Itens);
            f.Meta.Verify(m => m.TrocarCodigoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Codigo_recusado_pela_meta_nao_grava_autorizacao_nem_canal()
        {
            var f = new Fixture();
            f.Meta.Setup(m => m.TrocarCodigoAsync(Es.AppId, Es.AppSecret, Es.Code))
                .ReturnsAsync(MetaResultado<TokenTrocado>.Erro(new MetaFalha(false, 400, 100, null, "code expired", null)));
            var (s, t) = await f.IniciarAsync();

            var erro = await Assert.ThrowsAsync<WhatsappException>(() => s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t)));

            Assert.Equal(ConexaoMetaCodigos.CodigoInvalido, erro.Erro.Codigo);
            Assert.Empty(f.Repo.Autorizacoes);
            Assert.Empty(f.Canais.Itens);
            Assert.Equal(EstadoTentativa.Erro, f.Repo.Tentativas[t.TentativaId].Estado);
        }

        [Fact]
        public async Task Token_de_outro_app_permissao_faltando_ou_waba_nao_autorizada_sao_recusados()
        {
            async Task<string> Falha(Action<Fixture> preparar, Func<TentativaIniciadaView, ConcluirConexaoMetaRequest>? pedido = null)
            {
                var f = new Fixture();
                preparar(f);
                var (s, t) = await f.IniciarAsync();
                var erro = await Assert.ThrowsAsync<WhatsappException>(() => s.ConcluirAsync(Es.Dono(), Es.Loja, (pedido ?? (x => Fixture.Fim(x)))(t)));
                Assert.Empty(f.Canais.Itens);
                f.Meta.Verify(m => m.RegistrarNumeroAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never);
                return erro.Erro.Codigo;
            }

            Assert.Equal(ConexaoMetaCodigos.AppDiferente, await Falha(f =>
                f.Meta.Setup(m => m.DepurarTokenAsync(Es.AppId, Es.AppSecret, Es.Token)).ReturnsAsync(MetaResultado<TokenInfo>.Sucesso(Es.Info(app: "111")))));
            Assert.Equal(ConexaoMetaCodigos.AutorizacaoIncompleta, await Falha(f =>
                f.Meta.Setup(m => m.DepurarTokenAsync(Es.AppId, Es.AppSecret, Es.Token))
                    .ReturnsAsync(MetaResultado<TokenInfo>.Sucesso(Es.Info(escopos: new[] { "whatsapp_business_management" })))));
            // O navegador diz uma WABA que o token nao autorizou: nunca se confia so no ID recebido.
            Assert.Equal(ConexaoMetaCodigos.WabaNaoAutorizada, await Falha(_ => { }, t => Fixture.Fim(t, waba: Es.OutraWaba)));
        }

        [Fact]
        public async Task Varios_numeros_pedem_escolha_e_a_ativacao_usa_a_autorizacao_ja_gravada()
        {
            var f = new Fixture();
            f.Numeros(Es.Numero(Es.Phone), Es.Numero(Es.Phone2, "+55 34 99821-0002", "CONNECTED"));
            var (s, t) = await f.IniciarAsync();

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t, phone: null));

            Assert.Equal(EstadoTentativa.Autorizada, estado.EstadoTentativa);
            Assert.Contains("escolher_numero", estado.Acoes);
            Assert.Equal(2, estado.Numeros.Count);
            Assert.Empty(f.Canais.Itens);

            var ativado = await s.AtivarAsync(Es.Dono(), Es.Loja, new AtivarNumeroMetaRequest(t.TentativaId, Es.Phone2));

            Assert.Equal(Es.Phone2, Assert.Single(f.Canais.Itens.Values).PhoneNumberId);
            Assert.Empty(f.Registros); // ja estava CONNECTED: nao registra de novo
            Assert.Equal(EstadoTentativa.Concluida, ativado.EstadoTentativa);
            f.Meta.Verify(m => m.TrocarCodigoAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Waba_compartilhada_nao_permite_apropriar_numero_de_outra_loja()
        {
            var f = new Fixture();
            f.Numeros(Es.Numero(Es.Phone), Es.Numero(Es.Phone2, "+55 34 99821-0002"));
            var deOutra = new CanalWhatsapp
            {
                Id = Guid.NewGuid(), IdEstabelecimento = Es.OutraLoja, PhoneNumberId = Es.Phone, NumeroE164 = "+5534998210001",
                Origem = OrigemCanal.Zippy, Status = StatusCanal.Ativo, WabaId = Es.Waba
            };
            f.Canais.Itens[deOutra.Id] = deOutra;
            var (s, t) = await f.IniciarAsync();

            // O navegador aponta o numero da outra loja: nao e escolhido sozinho; o unico livre e.
            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t, phone: Es.Phone));

            Assert.False(estado.Numeros.Single(n => n.PhoneNumberId == Es.Phone).Selecionavel);
            Assert.Equal(Es.OutraLoja, f.Canais.Itens[deOutra.Id].IdEstabelecimento);
            Assert.Equal(OrigemCanal.Zippy, f.Canais.Itens[deOutra.Id].Origem);
            Assert.Equal(Es.Phone2, f.Canais.Itens.Values.Single(c => c.IdEstabelecimento == Es.Loja).PhoneNumberId);
        }

        [Fact]
        public async Task Ativar_numero_de_outra_loja_e_recusado_mesmo_pedindo_direto()
        {
            var f = new Fixture();
            f.Numeros(Es.Numero(Es.Phone), Es.Numero(Es.Phone2, "+55 34 99821-0002"));
            f.Canais.Itens[Guid.NewGuid()] = new CanalWhatsapp
            {
                IdEstabelecimento = Es.OutraLoja, PhoneNumberId = Es.Phone, NumeroE164 = "+5534998210001", Status = StatusCanal.Ativo
            };
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t, phone: null));
            // Libera a tentativa para a escolha manual (o concluir ativou o unico livre).
            f.Repo.Tentativas[t.TentativaId].Estado = EstadoTentativa.Autorizada;

            var erro = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.AtivarAsync(Es.Dono(), Es.Loja, new AtivarNumeroMetaRequest(t.TentativaId, Es.Phone)));

            Assert.Equal((WhatsappCodigos.NumeroOutraLoja, 409), (erro.Erro.Codigo, erro.Http));
        }

        [Fact]
        public async Task Coexistencia_nao_registra_o_numero_e_pede_a_sincronizacao_uma_vez()
        {
            var f = new Fixture();
            f.Numeros(Es.Numero(estado: "CONNECTED", noApp: true));
            var (s, t) = await f.IniciarAsync(coexistencia: true);
            Assert.Equal("whatsapp_business_app_onboarding", t.Extras["featureType"]);

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t, EventoFluxo.FinishCoexistencia));

            var canal = Assert.Single(f.Canais.Itens.Values);
            Assert.True(canal.Coexistencia);
            Assert.Equal(RegistroEstado.Dispensado, canal.RegistroEstado);
            Assert.Empty(f.Registros);
            f.Meta.Verify(m => m.SincronizarAppBusinessAsync(Es.Phone, Es.Token, "smb_app_state_sync"), Times.Once);
            f.Meta.Verify(m => m.SincronizarAppBusinessAsync(Es.Phone, Es.Token, "history"), Times.Once);
            Assert.Contains(estado.Etapas, e => e.Codigo == EtapaConexao.Coexistencia && e.Estado == EstadoEtapa.Ok);
        }

        [Fact]
        public async Task So_a_gestao_cadastra_o_app_e_o_app_secret_nunca_volta()
        {
            var f = new Fixture();
            f.Repo.App = null;
            var s = f.Build();
            var pedido = new SalvarAppMetaRequest(Es.AppId, Es.ConfigId, Es.AppSecret, true, null);

            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.SalvarAppAsync(Es.Dono(), pedido));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.ObterAppAsync(Es.Dono()));

            var salvo = await s.SalvarAppAsync(WaTeste.Gestao, pedido);

            Assert.True(salvo.Disponivel);
            Assert.True(salvo.TemAppSecret);
            Assert.Equal("tela", salvo.AppSecretOrigem);
            Assert.DoesNotContain(Es.AppSecret, JsonSerializer.Serialize(salvo));
            Assert.NotEqual(Es.AppSecret, f.Repo.App!.AppSecretCifrado);
            // Agora a loja ja consegue conectar.
            Assert.True((await s.ConfiguracaoAsync(Es.Dono(), Es.Loja)).Disponivel);
        }

        [Fact]
        public async Task App_secret_errado_nao_e_gravado_e_vazio_mantem_o_atual()
        {
            var f = new Fixture();
            var s = f.Build();
            var antes = f.Repo.App!.AppSecretCifrado;

            var erro = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.SalvarAppAsync(WaTeste.Gestao, new SalvarAppMetaRequest(Es.AppId, Es.ConfigId, "chave-errada-0123456789abcdef", true, null)));
            Assert.Equal(("appSecret", WhatsappCodigos.AppSecretInvalido), (erro.Erro.Campo, erro.Erro.Codigo));
            Assert.Equal(antes, f.Repo.App!.AppSecretCifrado);

            await s.SalvarAppAsync(WaTeste.Gestao, new SalvarAppMetaRequest(Es.AppId, "900000000000099", "", false, "600000000000001"));
            Assert.Equal(antes, f.Repo.App!.AppSecretCifrado);
            Assert.Equal(("900000000000099", false), (f.Repo.App.ConfigurationId, f.Repo.App.AppReviewAprovado));

            var campo = await Assert.ThrowsAsync<WhatsappException>(() =>
                s.SalvarAppAsync(WaTeste.Gestao, new SalvarAppMetaRequest("abc", Es.ConfigId, null, true, null)));
            Assert.Equal("appId", campo.Erro.Campo);
        }

        [Fact]
        public async Task Pin_exigido_fica_pendente_e_a_nova_tentativa_usa_o_pin_informado()
        {
            var f = new Fixture();
            f.Meta.SetupSequence(m => m.RegistrarNumeroAsync(Es.Phone, Es.Token, It.IsAny<string>()))
                .ReturnsAsync(MetaResultado<bool>.Erro(new MetaFalha(false, 400, 133005, null, "PIN mismatch", null)))
                .ReturnsAsync(MetaResultado<bool>.Sucesso(true));
            var (s, t) = await f.IniciarAsync();

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));

            Assert.Equal(EstadoTentativa.Autorizada, estado.EstadoTentativa);
            Assert.Contains("informar_pin", estado.Acoes);
            Assert.NotEqual(StatusCanal.Ativo, Assert.Single(f.Canais.Itens.Values).Status);

            var depois = await s.AtivarAsync(Es.Dono(), Es.Loja, new AtivarNumeroMetaRequest(t.TentativaId, Es.Phone, "123456"));

            f.Meta.Verify(m => m.RegistrarNumeroAsync(Es.Phone, Es.Token, "123456"), Times.Once);
            Assert.Equal(EstadoTentativa.Concluida, depois.EstadoTentativa);
            Assert.Equal(StatusCanal.Ativo, Assert.Single(f.Canais.Itens.Values).Status);
        }

        [Fact]
        public async Task Reconectar_mantem_o_mesmo_canal_e_a_mesma_loja()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));
            var canalId = Assert.Single(f.Canais.Itens.Keys);
            f.Repo.Autorizacoes.Values.Single().Estado = EstadoAutorizacao.Revogada;

            var r = await s.ReconectarAsync(Es.Dono(), Es.Loja, canalId);
            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja,
                new ConcluirConexaoMetaRequest(r.TentativaId, r.State, EventoFluxo.Finish, Es.Code, Es.Waba, null));

            Assert.Equal("reconectar", r.Modo);
            Assert.Equal(canalId, Assert.Single(f.Canais.Itens.Keys));
            Assert.Equal(EstadoAutorizacao.Ativa, f.Repo.Autorizacoes.Values.Single().Estado);
            Assert.Equal(canalId, estado.Canal!.Id);
        }

        [Fact]
        public async Task Conexao_manual_valida_nao_e_sobrescrita_sem_reconectar()
        {
            var f = new Fixture();
            f.Canais.Itens[Guid.NewGuid()] = new CanalWhatsapp
            {
                IdEstabelecimento = Es.Loja, PhoneNumberId = Es.Phone, NumeroE164 = "+5534998210001", Origem = OrigemCanal.Proprio, Status = StatusCanal.Ativo
            };
            var (s, t) = await f.IniciarAsync();

            var estado = await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));

            Assert.Equal(ConexaoMetaCodigos.ConexaoExistente, estado.ErroCodigo);
            Assert.Equal(OrigemCanal.Proprio, Assert.Single(f.Canais.Itens.Values).Origem);
        }

        [Fact]
        public async Task Desconectar_preserva_o_canal_e_nao_mexe_na_waba_compartilhada()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));
            var canalId = Assert.Single(f.Canais.Itens.Keys);
            // Outro numero (de outra loja) usa a mesma WABA.
            f.Canais.Itens[Guid.NewGuid()] = new CanalWhatsapp
            {
                IdEstabelecimento = Es.OutraLoja, PhoneNumberId = Es.Phone2, NumeroE164 = "+5534998210002", WabaId = Es.Waba, Status = StatusCanal.Ativo
            };

            var r = await s.DesconectarAsync(Es.Dono(), Es.Loja, canalId);

            Assert.True(r.HistoricoPreservado);
            Assert.False(r.InscricaoRemovida);
            Assert.Equal("waba_em_uso_por_outro_numero", r.MotivoInscricaoMantida);
            Assert.Equal(StatusCanal.Inativo, f.Canais.Itens[canalId].Status);
            f.Meta.Verify(m => m.DesinscreverAppAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Desconectar_tira_a_inscricao_so_quando_ninguem_mais_usa_a_waba_e_nunca_da_zippy()
        {
            var livre = new Fixture();
            var (s, t) = await livre.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));
            var r = await s.DesconectarAsync(Es.Dono(), Es.Loja, livre.Canais.Itens.Keys.Single());
            Assert.True(r.InscricaoRemovida);
            Assert.Equal(EstadoAutorizacao.Revogada, livre.Repo.Autorizacoes.Values.Single().Estado);

            var daZippy = new Fixture { Config = Es.Config(Es.Waba) };
            var (s2, t2) = await daZippy.IniciarAsync();
            await s2.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t2));
            var r2 = await s2.DesconectarAsync(Es.Dono(), Es.Loja, daZippy.Canais.Itens.Keys.Single());
            Assert.False(r2.InscricaoRemovida);
            Assert.Equal("waba_da_zippy", r2.MotivoInscricaoMantida);
            daZippy.Meta.Verify(m => m.DesinscreverAppAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task Revalidar_com_token_revogado_marca_a_autorizacao_e_pede_reconectar()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));
            f.Meta.Setup(m => m.DepurarTokenAsync(Es.AppId, Es.AppSecret, Es.Token)).ReturnsAsync(MetaResultado<TokenInfo>.Sucesso(Es.Info(valido: false)));

            var d = await s.RevalidarAsync(Es.Dono(), Es.Loja, f.Canais.Itens.Keys.Single(), new RevalidarCanalRequest());

            Assert.Equal(EstadoConexao.Revogado, d.Estado);
            Assert.Contains("reconectar", d.Acoes);
            Assert.Equal(EstadoAutorizacao.Revogada, f.Repo.Autorizacoes.Values.Single().Estado);
        }

        [Fact]
        public async Task Revalidar_sem_reparar_so_le_e_com_reparar_reinscreve()
        {
            var f = new Fixture();
            var (s, t) = await f.IniciarAsync();
            await s.ConcluirAsync(Es.Dono(), Es.Loja, Fixture.Fim(t));
            var canalId = f.Canais.Itens.Keys.Single();
            f.Meta.Invocations.Clear();

            var leitura = await s.RevalidarAsync(Es.Dono(), Es.Loja, canalId, new RevalidarCanalRequest());
            f.Meta.Verify(m => m.InscreverAppAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            Assert.Contains(leitura.Etapas, e => e.Codigo == EtapaConexao.Inscricao && e.Acao == "reparar");

            await s.RevalidarAsync(Es.Dono(), Es.Loja, canalId, new RevalidarCanalRequest(Reparar: true));
            f.Meta.Verify(m => m.InscreverAppAsync(Es.Waba, Es.Token), Times.Once);
        }

        [Fact]
        public async Task Canal_de_outra_loja_responde_como_inexistente()
        {
            var f = new Fixture();
            var canal = new CanalWhatsapp { Id = Guid.NewGuid(), IdEstabelecimento = Es.OutraLoja, PhoneNumberId = Es.Phone, Origem = OrigemCanal.Autorizado };
            f.Canais.Itens[canal.Id] = canal;

            await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Build().RevalidarAsync(Es.Dono(), Es.Loja, canal.Id, new RevalidarCanalRequest()));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Build().DesconectarAsync(Es.Dono(), Es.Loja, canal.Id));
            await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Build().ReconectarAsync(Es.Dono(), Es.Loja, canal.Id));
        }
    }

    public class ConexaoMetaWebhookTests
    {
        private static (WaWebhookIngestor Ingestor, List<WaEvento> Gravados) Ingestor()
        {
            var gravados = new List<WaEvento>();
            var repo = new Mock<IWaEventoRepository>();
            repo.Setup(r => r.RegistrarAsync(It.IsAny<WaEvento>())).ReturnsAsync((WaEvento e) =>
            {
                if (gravados.Any(g => g.Tipo == e.Tipo && g.Chave == e.Chave)) return false;
                gravados.Add(e);
                return true;
            });
            return (new WaWebhookIngestor(repo.Object, NullLogger<WaWebhookIngestor>.Instance), gravados);
        }

        private const string PartnerRemoved = @"{""entry"":[{""id"":""700000000000001"",""changes"":[{""field"":""account_update"",
            ""value"":{""event"":""PARTNER_REMOVED"",""waba_info"":{""waba_id"":""700000000000001"",""owner_business_id"":""600000000000001""}}}]}]}";

        private const string Eco = @"{""entry"":[{""id"":""700000000000001"",""changes"":[{""field"":""smb_message_echoes"",
            ""value"":{""metadata"":{""phone_number_id"":""800000000000001""},""message_echoes"":[{""id"":""wamid.ECO"",""from"":""5534998210001"",""to"":""5534999990000""}]}}]}]}";

        [Fact]
        public async Task Account_update_so_entra_pelo_webhook_global_e_e_idempotente()
        {
            var (ingestor, gravados) = Ingestor();
            using var doc = JsonDocument.Parse(PartnerRemoved);

            var global = await ingestor.IngerirAsync(doc.RootElement, canal: null);
            var repetido = await ingestor.IngerirAsync(doc.RootElement, canal: null);
            var doCliente = await ingestor.IngerirAsync(doc.RootElement, new CanalWhatsapp { PhoneNumberId = Es.Phone });

            Assert.Equal(1, global.Novos);
            Assert.Equal(1, repetido.Repetidos);
            Assert.Equal(1, doCliente.Ignorados);
            Assert.Equal(TipoEvento.Conta, Assert.Single(gravados).Tipo);
        }

        [Fact]
        public async Task Partner_removed_revoga_a_autorizacao_da_waba()
        {
            var conexoes = new Mock<IConexaoMetaRepository>();
            conexoes.Setup(c => c.RevogarPorWabaAsync(Es.Waba, null, It.IsAny<string>())).ReturnsAsync(1);
            var processador = new WaEventoProcessor(new Mock<ICanalRepository>().Object, new Mock<IMessageService>().Object,
                new Mock<IPipelineDeMensagem>().Object, NullLogger<WaEventoProcessor>.Instance, conexoes.Object);
            var (ingestor, gravados) = Ingestor();
            using var doc = JsonDocument.Parse(PartnerRemoved);
            await ingestor.IngerirAsync(doc.RootElement, canal: null);

            var r = await processador.ProcessarAsync(gravados.Single());

            Assert.Equal("processado", r.Resultado);
            conexoes.Verify(c => c.RevogarPorWabaAsync(Es.Waba, null, It.IsAny<string>()), Times.Once);
        }

        [Fact]
        public async Task Eco_do_app_business_e_gravado_mas_nunca_dispara_o_robo()
        {
            var (ingestor, gravados) = Ingestor();
            using var doc = JsonDocument.Parse(Eco);
            await ingestor.IngerirAsync(doc.RootElement, canal: null);
            var canais = new Mock<ICanalRepository>();
            canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync(Es.Phone)).ReturnsAsync(new CanalWhatsapp { Id = Guid.NewGuid(), PhoneNumberId = Es.Phone });
            var pipeline = new Mock<IPipelineDeMensagem>();
            var processador = new WaEventoProcessor(canais.Object, new Mock<IMessageService>().Object, pipeline.Object, NullLogger<WaEventoProcessor>.Instance);

            var r = await processador.ProcessarAsync(Assert.Single(gravados));

            Assert.Equal(TipoEvento.Eco, gravados.Single().Tipo);
            Assert.Equal("ignorado", r.Resultado);
            pipeline.Verify(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>()), Times.Never);
            canais.Verify(c => c.MarcarRecebimentoAsync(It.IsAny<Guid>()), Times.Never);
        }

        [Fact]
        public async Task Mensagem_de_outra_waba_nao_chega_ao_numero_autorizado()
        {
            var canal = new CanalWhatsapp
            {
                Id = Guid.NewGuid(), IdEstabelecimento = Es.Loja, PhoneNumberId = Es.Phone, Origem = OrigemCanal.Autorizado, WabaId = Es.Waba
            };
            var canais = new Mock<ICanalRepository>();
            canais.Setup(c => c.ObterAtivoPorPhoneNumberIdAsync(Es.Phone)).ReturnsAsync(canal);
            var pipeline = new Mock<IPipelineDeMensagem>();
            var processador = new WaEventoProcessor(canais.Object, new Mock<IMessageService>().Object, pipeline.Object, NullLogger<WaEventoProcessor>.Instance);
            var evento = new WaEvento
            {
                Id = Guid.NewGuid(), Tipo = TipoEvento.Mensagem, Chave = "wamid.X", PhoneNumberId = Es.Phone,
                PayloadJson = @"{""metadata"":{""phone_number_id"":""800000000000001""},""waba_id"":""700000000000002"",""message"":{""id"":""wamid.X"",""from"":""5534999990000"",""type"":""text"",""text"":{""body"":""oi""}}}"
            };

            var r = await processador.ProcessarAsync(evento);

            Assert.Equal("ignorado", r.Resultado);
            pipeline.Verify(p => p.ExecutarAsync(It.IsAny<ConversationProcessingInput>(), It.IsAny<CanalWhatsapp>()), Times.Never);
        }

        [Fact]
        public void Autorizado_usa_so_o_token_da_autorizacao_e_nunca_o_global()
        {
            var p = WaTeste.Protetor();
            var global = new Mock<IWhatsAppTokenProvider>();
            global.Setup(g => g.GetAccessToken()).Returns("EAAG-token-global-da-zippy");

            var comToken = CanalToken.Resolver(new CanalWhatsapp { Origem = OrigemCanal.Autorizado, TokenAutorizacaoCifrado = p.Proteger(Es.Token) }, p, global.Object);
            var revogado = CanalToken.Resolver(new CanalWhatsapp { Origem = OrigemCanal.Autorizado, TokenAutorizacaoCifrado = null }, p, global.Object);

            Assert.Equal((Es.Token, CanalToken.Autorizacao), comToken);
            Assert.Null(revogado.Token);
            global.Verify(g => g.GetAccessToken(), Times.Never);
        }

        [Fact]
        public void Migracao_e_aditiva_e_nao_reatribui_nem_apaga_canais()
        {
            var sql = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Migrations", "Delivery", "20261030_01_whatsapp_conexao_meta.sql"));

            foreach (var proibido in new[] { "DROP TABLE", "DROP COLUMN", "DELETE FROM", "TRUNCATE", "UPDATE canal_whatsapp" })
            {
                Assert.DoesNotContain(proibido, sql, StringComparison.OrdinalIgnoreCase);
            }

            Assert.Contains("'zippy', 'proprio', 'central', 'autorizado'", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS whatsapp_autorizacao_meta", sql, StringComparison.Ordinal);
            Assert.Contains("CREATE TABLE IF NOT EXISTS whatsapp_conexao_tentativa", sql, StringComparison.Ordinal);
            Assert.Contains("'20261030_01_whatsapp_conexao_meta'", sql, StringComparison.Ordinal);
        }
    }
}
